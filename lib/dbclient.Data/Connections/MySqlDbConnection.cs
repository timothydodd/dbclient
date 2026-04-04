using System.Data;
using Dapper;
using MySql.Data.MySqlClient;
using dbclient.Data.Models;

namespace dbclient.Data.Connections;

public class MySqlDbConnection : ConnectionBase
{
    public override string Name => $"MySQL:{Address}";
    public override string ConnectionType => "MySQL";

    public override async Task<IDbConnection> GetConnectionAsync(string database, CancellationToken ct = default)
    {
        EnsureSshTunnel();
        var con = new MySqlConnection(BuildConnectionString(database));
        await con.OpenAsync(ct);
        return con;
    }

    public override async Task<DbMaster> LoadDatabasesAsync(CancellationToken ct = default)
    {
        var master = new DbMaster();
        var excludeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "mysql", "sys", "performance_schema", "information_schema" };

        using var con = await GetConnectionAsync("information_schema", ct);
        var databases = await con.QueryAsync<string>("SHOW DATABASES;");
        foreach (var db in databases)
        {
            if (!excludeSet.Contains(db))
                master.Databases.Add(new DbDatabase { Name = db });
        }

        return master;
    }

    public override async Task<DbDatabase> LoadDatabaseSchemaAsync(string databaseName, CancellationToken ct = default)
    {
        var database = new DbDatabase { Name = databaseName };

        // Load tables, views, and columns via INFORMATION_SCHEMA
        var columnsQuery = $@"
            SELECT CASE WHEN b.TABLE_NAME IS NOT NULL THEN 'view' ELSE 'table' END OBJECT_TYPE, a.*
            FROM INFORMATION_SCHEMA.COLUMNS a
            LEFT OUTER JOIN INFORMATION_SCHEMA.VIEWS b
                ON a.TABLE_CATALOG = b.TABLE_CATALOG AND a.TABLE_SCHEMA = b.TABLE_SCHEMA AND a.TABLE_NAME = b.TABLE_NAME
            WHERE a.TABLE_SCHEMA = @Schema
            ORDER BY a.TABLE_NAME, a.ORDINAL_POSITION";

        using (var con = await GetConnectionAsync("information_schema", ct))
        {
            var rows = await con.QueryAsync(columnsQuery, new { Schema = databaseName });
            string? currentTable = null;
            List<DbColumn>? currentColumns = null;

            foreach (var row in rows)
            {
                string tableName = row.TABLE_NAME;
                string columnName = row.COLUMN_NAME;
                string dataType = row.DATA_TYPE;
                string objectType = row.OBJECT_TYPE;
                string isNullable = row.IS_NULLABLE;
                long? maxLength = row.CHARACTER_MAXIMUM_LENGTH;

                if (currentTable != tableName)
                {
                    currentTable = tableName;
                    if (objectType == "view")
                    {
                        var view = new DbView { Name = tableName };
                        database.Views.Add(view);
                        currentColumns = view.Columns;
                    }
                    else
                    {
                        var table = new DbTable { Name = tableName };
                        database.Tables.Add(table);
                        currentColumns = table.Columns;
                    }
                }

                currentColumns?.Add(new DbColumn
                {
                    Name = columnName,
                    DataType = maxLength.HasValue ? $"{dataType}({maxLength})" : dataType,
                    IsNullable = isNullable == "YES"
                });
            }
        }

        // Load primary keys
        using (var con = (MySqlConnection)await GetConnectionAsync(databaseName, ct))
        {
            foreach (var table in database.Tables)
            {
                var pkSql = $"SHOW KEYS FROM `{table.Name}` WHERE Key_name = 'PRIMARY';";
                try
                {
                    await ReadDataAsync(con, pkSql, reader =>
                    {
                        var columnName = reader["Column_Name"]?.ToString();
                        var col = table.Columns.FirstOrDefault(c =>
                            c.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase));
                        if (col != null) col.IsPrimaryKey = true;
                    });
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Could not load PKs for {table.Name}: {ex.Message}");
                    System.Diagnostics.Trace.TraceWarning($"Primary key loading failed for {table.Name}: {ex.Message}");
                }
            }
        }

        database.Loaded = true;
        return database;
    }

    private string BuildConnectionString(string database)
    {
        var portPart = "";
        if (UseSSH)
            portPart = $"Port={EstablishedSshPort};";
        else if (!string.IsNullOrWhiteSpace(Port))
            portPart = $"Port={Port};";

        return $"Server={Address};{portPart}Database={database};Uid={User};Pwd={Password};" +
               $"Connection Timeout={ConnectionTimeout};old guids=true;Convert Zero Datetime=True;CharSet=utf8;";
    }
}
