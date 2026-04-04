using System.Data;
using System.Data.SQLite;
using dbclient.Data.Models;

namespace dbclient.Data.Connections;

public class SqliteDbConnection : ConnectionBase
{
    public string FileName { get; set; } = "";

    public override string Name => Path.GetFileName(FileName);
    public override string ConnectionType => "SQLite";

    public override async Task<IDbConnection> GetConnectionAsync(string database, CancellationToken ct = default)
    {
        var connection = new SQLiteConnection($"Data Source={FileName};Version=3;");
        await connection.OpenAsync(ct);
        return connection;
    }

    public override Task<DbMaster> LoadDatabasesAsync(CancellationToken ct = default)
    {
        var master = new DbMaster();
        master.Databases.Add(new DbDatabase { Name = Path.GetFileName(FileName) });
        return Task.FromResult(master);
    }

    public override async Task<DbDatabase> LoadDatabaseSchemaAsync(string databaseName, CancellationToken ct = default)
    {
        var database = new DbDatabase { Name = databaseName };

        using var con = (SQLiteConnection)await GetConnectionAsync(databaseName, ct);

        // Get all tables and views from sqlite_master
        using var cmd = new SQLiteCommand(
            "SELECT name, type FROM sqlite_master WHERE type IN ('table','view') ORDER BY type DESC, name ASC;",
            con);
        using var masterReader = await cmd.ExecuteReaderAsync(ct);

        while (await masterReader.ReadAsync(ct))
        {
            var name = masterReader["name"]?.ToString() ?? "";
            var type = masterReader["type"]?.ToString() ?? "";

            if (name.StartsWith("sqlite_")) continue; // Skip internal tables

            if (type == "view")
            {
                var view = new DbView { Name = name };
                await LoadColumnsAsync(con, name, view.Columns, ct);
                database.Views.Add(view);
            }
            else
            {
                var table = new DbTable { Name = name };
                await LoadColumnsAsync(con, name, table.Columns, ct);
                database.Tables.Add(table);
            }
        }

        database.Loaded = true;
        return database;
    }

    private static async Task LoadColumnsAsync(SQLiteConnection con, string tableName, List<DbColumn> columns, CancellationToken ct)
    {
        var safeTableName = tableName.Replace("\"", "\"\"");
        using var cmd = new SQLiteCommand($"PRAGMA table_info(\"{safeTableName}\")", con);
        using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var col = new DbColumn
            {
                Name = reader["name"]?.ToString() ?? "",
                DataType = reader["type"]?.ToString() ?? "",
                IsPrimaryKey = reader["pk"]?.ToString() == "1",
                IsNullable = reader["notnull"]?.ToString() != "1"
            };
            columns.Add(col);
        }
    }

    public override async Task<QueryResult> ExecuteQueryAsync(string database, string sql, CancellationToken ct = default)
    {
        var result = new QueryResult();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            using var con = new SQLiteConnection($"Data Source={FileName};Version=3;");
            await con.OpenAsync(ct);

            using var cmd = new SQLiteCommand(sql, con);
            cmd.CommandTimeout = CommandTimeout;

            if (ct != default)
                ct.Register(() => cmd.Cancel());

            using var reader = await cmd.ExecuteReaderAsync(ct);

            var rs = new Data.Models.ResultSet();

            if (reader.FieldCount > 0)
            {
                rs.ColumnNames = new string[reader.FieldCount];
                rs.ColumnTypes = new string?[reader.FieldCount];

                for (int i = 0; i < reader.FieldCount; i++)
                {
                    rs.ColumnNames[i] = reader.GetName(i);
                    rs.ColumnTypes[i] = reader.GetDataTypeName(i);
                }

                while (await reader.ReadAsync(ct))
                {
                    if (MaxRows > 0 && rs.Rows.Count >= MaxRows) break;

                    var values = new string?[reader.FieldCount];
                    for (int i = 0; i < reader.FieldCount; i++)
                        values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i)?.ToString();
                    rs.Rows.Add(values);
                }
            }

            result.AffectedRows = rs.Rows.Count;
            result.Data = [rs];
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            PopulateErrorDetails(result, ex);
        }

        sw.Stop();
        result.ExecutionTime = sw.Elapsed;
        return result;
    }
}
