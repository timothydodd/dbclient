using System.Data;
using Microsoft.Data.SqlClient;
using dbclient.Data.Models;

namespace dbclient.Data.Connections;

public class SqlServerConnection : ConnectionBase
{
    public override string Name => $"SQL:{Address}";
    public override string ConnectionType => "SQL Server";

    public bool Encrypted { get; set; }
    public SqlServerAuthMode AuthMode { get; set; } = SqlServerAuthMode.SqlLogin;

    public override async Task<IDbConnection> GetConnectionAsync(string database, CancellationToken ct = default)
    {
        EnsureSshTunnel();
        var con = new SqlConnection(BuildConnectionString(database));
        await con.OpenAsync(ct);
        return con;
    }

    public override async Task<DbMaster> LoadDatabasesAsync(CancellationToken ct = default)
    {
        var master = new DbMaster();
        var query = "SELECT name FROM master.sys.databases WHERE name NOT IN ('master','model','msdb','tempdb');";

        using var con = (SqlConnection)await GetConnectionAsync("master", ct);
        await ReadDataAsync(con, query, reader =>
        {
            master.Databases.Add(new DbDatabase { Name = (string)reader["name"] });
        });

        return master;
    }

    public override async Task<DbDatabase> LoadDatabaseSchemaAsync(string databaseName, CancellationToken ct = default)
    {
        var database = new DbDatabase { Name = databaseName };

        // Load tables, views, and columns
        var columnsQuery = @"
            SELECT CASE WHEN b.TABLE_NAME IS NOT NULL THEN 'view' ELSE 'table' END OBJECT_TYPE, a.*
            FROM INFORMATION_SCHEMA.COLUMNS a
            LEFT OUTER JOIN INFORMATION_SCHEMA.VIEWS b
                ON a.TABLE_CATALOG = b.TABLE_CATALOG AND a.TABLE_SCHEMA = b.TABLE_SCHEMA AND a.TABLE_NAME = b.TABLE_NAME
            ORDER BY a.TABLE_NAME, a.ORDINAL_POSITION";

        using (var con = (SqlConnection)await GetConnectionAsync(databaseName, ct))
        {
            string? currentTable = null;
            List<DbColumn>? currentColumns = null;
            bool isView = false;

            await ReadDataAsync(con, columnsQuery, reader =>
            {
                var tableName = (string)reader["TABLE_NAME"];
                var columnName = (string)reader["COLUMN_NAME"];
                var dataType = (string)reader["DATA_TYPE"];
                var schema = (string)reader["TABLE_SCHEMA"];
                var objectType = (string)reader["OBJECT_TYPE"];
                var isNullable = (string)reader["IS_NULLABLE"] == "YES";
                var maxLength = reader["CHARACTER_MAXIMUM_LENGTH"]?.ToString();

                if (schema == "sys") return;

                if (currentTable != tableName)
                {
                    currentTable = tableName;
                    isView = objectType == "view";

                    if (isView)
                    {
                        var view = new DbView { Name = tableName, Schema = schema };
                        database.Views.Add(view);
                        currentColumns = view.Columns;
                    }
                    else
                    {
                        var table = new DbTable { Name = tableName, Schema = schema };
                        database.Tables.Add(table);
                        currentColumns = table.Columns;
                    }
                }

                var col = new DbColumn
                {
                    Name = columnName,
                    DataType = !string.IsNullOrEmpty(maxLength) && maxLength != "-1"
                        ? $"{dataType}({maxLength})"
                        : dataType,
                    IsNullable = isNullable
                };

                currentColumns?.Add(col);
            });
        }

        // Load primary keys
        var pkQuery = @"
            SELECT Col.Table_Name, Col.Column_Name
            FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS Tab, INFORMATION_SCHEMA.CONSTRAINT_COLUMN_USAGE Col
            WHERE Col.Constraint_Name = Tab.Constraint_Name AND Col.Table_Name = Tab.Table_Name
                AND (Constraint_Type = 'PRIMARY KEY' OR Constraint_Type = 'UNIQUE')";

        using (var con = (SqlConnection)await GetConnectionAsync(databaseName, ct))
        {
            await ReadDataAsync(con, pkQuery, reader =>
            {
                var tableName = (string)reader["Table_Name"];
                var columnName = (string)reader["Column_Name"];

                var table = database.Tables.FirstOrDefault(t =>
                    t.Name.Equals(tableName, StringComparison.OrdinalIgnoreCase));
                var col = table?.Columns.FirstOrDefault(c =>
                    c.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase));
                if (col != null) col.IsPrimaryKey = true;
            });
        }

        // Load stored procedures
        var spQuery = @"
            SELECT SPECIFIC_SCHEMA, SPECIFIC_NAME
            FROM information_schema.routines
            WHERE routine_type = 'PROCEDURE' AND LEFT(Routine_Name, 3) NOT IN ('sp_', 'xp_', 'ms_')
            ORDER BY SPECIFIC_NAME";

        using (var con = (SqlConnection)await GetConnectionAsync(databaseName, ct))
        {
            await ReadDataAsync(con, spQuery, reader =>
            {
                database.StoredProcedures.Add(new DbProc
                {
                    Name = (string)reader["SPECIFIC_NAME"],
                    Schema = (string)reader["SPECIFIC_SCHEMA"]
                });
            });
        }

        database.Loaded = true;
        return database;
    }

    private string BuildConnectionString(string database)
    {
        var portPart = "";
        if (UseSSH)
            portPart = $",{EstablishedSshPort}";
        else if (!string.IsNullOrWhiteSpace(Port))
            portPart = $",{Port}";

        var baseStr = $"Server={Address}{portPart};Database={database};" +
                      $"Connection Timeout={ConnectionTimeout};TrustServerCertificate=True;";

        if (AuthMode == SqlServerAuthMode.AzureDefault)
        {
            // DefaultAzureCredential chain: env vars, managed identity, VS, VS Code, az CLI, etc.
            // Encryption is required for Azure SQL.
            return baseStr + "Authentication=Active Directory Default;Encrypt=True;";
        }

        return baseStr +
               $"User ID={User};Password={Password};" +
               (Encrypted ? "Encrypt=True;" : "");
    }
}

public enum SqlServerAuthMode
{
    SqlLogin,
    AzureDefault
}
