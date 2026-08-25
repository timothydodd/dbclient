using System.Data;
using Microsoft.Data.Sqlite;
using dbclient.Data.Models;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace dbclient.Data.Connections;

public class SqliteDbConnection : ConnectionBase
{
    public string FileName { get; set; } = "";

    public override string Name => Path.GetFileName(FileName);
    public override string ConnectionType => "SQLite";

    private string BuildConnectionString() => new SqliteConnectionStringBuilder
    {
        DataSource = FileName,
        Mode = SqliteOpenMode.ReadWriteCreate,
        DefaultTimeout = CommandTimeout
    }.ConnectionString;

    public override async Task<IDbConnection> GetConnectionAsync(string database, CancellationToken ct = default)
    {
        var connection = new MsSqliteConnection(BuildConnectionString());
        try
        {
            await connection.OpenAsync(ct);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
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

        using var con = (MsSqliteConnection)await GetConnectionAsync(databaseName, ct);

        // Get all tables and views from sqlite_master
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT name, type FROM sqlite_master WHERE type IN ('table','view') ORDER BY type DESC, name ASC;";
        cmd.CommandTimeout = CommandTimeout;
        using var masterReader = await cmd.ExecuteReaderAsync(ct);

        var entries = new List<(string Name, string Type)>();
        while (await masterReader.ReadAsync(ct))
        {
            var name = masterReader["name"]?.ToString() ?? "";
            var type = masterReader["type"]?.ToString() ?? "";
            if (name.StartsWith("sqlite_")) continue; // Skip internal tables
            entries.Add((name, type));
        }
        await masterReader.DisposeAsync();

        foreach (var (name, type) in entries)
        {
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

    private async Task LoadColumnsAsync(MsSqliteConnection con, string tableName, List<DbColumn> columns, CancellationToken ct)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({SqlIdentifier.Quote(SqlDialect.Sqlite, tableName)})";
        cmd.CommandTimeout = CommandTimeout;
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
            using var con = (MsSqliteConnection)await GetConnectionAsync(database, ct);

            using var cmd = con.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = CommandTimeout;

            // Microsoft.Data.Sqlite has no meaningful Command.Cancel; cancellation is honoured
            // between rows via the token passed to ReadAsync/NextResultAsync.
            result.Data = new List<ResultSet>();
            using var reader = await cmd.ExecuteReaderAsync(ct);
            do
            {
                if (reader.FieldCount == 0) continue;

                var rs = ReadColumns(reader);
                await ReadRowsAsync(reader, rs, MaxRows, ct);
                result.Data.Add(rs);
            } while (await reader.NextResultAsync(ct));

            result.AffectedRows = result.Data.Count > 0
                ? result.Data.Sum(d => d.Rows.Count)
                : Math.Max(reader.RecordsAffected, 0);
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
