using System.Data;
using Dapper;
using MySqlConnector;
using dbclient.Data.Models;

namespace dbclient.Data.Connections;

public class MySqlDbConnection : ConnectionBase
{
    public override string Name => $"MySQL:{Address}";
    public override string ConnectionType => "MySQL";

    private string? _lastConnectionString;

    public override async Task<IDbConnection> GetConnectionAsync(string database, CancellationToken ct = default)
    {
        await EnsureSshTunnelAsync(ct);
        var cs = BuildConnectionString(database);
        _lastConnectionString = cs;
        var con = new MySqlConnection(cs);
        try
        {
            await con.OpenAsync(ct);
        }
        catch
        {
            await con.DisposeAsync();
            throw;
        }
        return con;
    }

    protected override IDisposable? SubscribeInfoMessages(IDbConnection con, List<string> sink)
    {
        if (con is not MySqlConnection my) return null;
        void Handler(object sender, MySqlInfoMessageEventArgs e)
        {
            foreach (var err in e.Errors)
                sink.Add(string.IsNullOrEmpty(err.Level) ? err.Message : $"{err.Level} {err.ErrorCode}: {err.Message}");
        }
        my.InfoMessage += Handler;
        return new Unsubscriber(() => my.InfoMessage -= Handler);
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    public override async Task<DbMaster> LoadDatabasesAsync(CancellationToken ct = default)
    {
        var master = new DbMaster();
        var excludeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "mysql", "sys", "performance_schema", "information_schema" };

        using var con = await GetConnectionAsync("information_schema", ct);
        var databases = await con.QueryAsync<string>(Command("SHOW DATABASES;", ct: ct));
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
        var columnsQuery = @"
            SELECT CASE WHEN b.TABLE_NAME IS NOT NULL THEN 'view' ELSE 'table' END OBJECT_TYPE, a.*
            FROM INFORMATION_SCHEMA.COLUMNS a
            LEFT OUTER JOIN INFORMATION_SCHEMA.VIEWS b
                ON a.TABLE_CATALOG = b.TABLE_CATALOG AND a.TABLE_SCHEMA = b.TABLE_SCHEMA AND a.TABLE_NAME = b.TABLE_NAME
            WHERE a.TABLE_SCHEMA = @Schema
            ORDER BY a.TABLE_NAME, a.ORDINAL_POSITION";

        using (var con = await GetConnectionAsync("information_schema", ct))
        {
            var rows = await con.QueryAsync(Command(columnsQuery, new { Schema = databaseName }, ct));
            string? currentTable = null;
            List<DbColumn>? currentColumns = null;

            foreach (var row in rows)
            {
                string tableName = row.TABLE_NAME;
                string columnName = row.COLUMN_NAME;
                string dataType = row.DATA_TYPE;
                string objectType = row.OBJECT_TYPE;
                string isNullable = row.IS_NULLABLE;
                // CHARACTER_MAXIMUM_LENGTH is bigint unsigned in information_schema and comes
                // back as ulong; a dynamic ulong->long? assignment throws (no implicit
                // conversion), so read it as object and convert safely.
                object? maxLengthObj = row.CHARACTER_MAXIMUM_LENGTH;
                string? maxLength = maxLengthObj?.ToString();

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
                    DataType = !string.IsNullOrEmpty(maxLength) ? $"{dataType}({maxLength})" : dataType,
                    IsNullable = isNullable == "YES"
                });
            }
        }

        // Load primary keys
        using (var con = (MySqlConnection)await GetConnectionAsync(databaseName, ct))
        {
            foreach (var table in database.Tables)
            {
                ct.ThrowIfCancellationRequested();
                var pkSql = $"SHOW KEYS FROM {SqlIdentifier.Quote(SqlDialect.MySql, table.Name)} WHERE Key_name = 'PRIMARY';";
                try
                {
                    await ReadDataAsync(con, pkSql, reader =>
                    {
                        var columnName = reader["Column_Name"]?.ToString();
                        var col = table.Columns.FirstOrDefault(c =>
                            c.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase));
                        if (col != null) col.IsPrimaryKey = true;
                    }, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
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
        var b = new MySqlConnectionStringBuilder
        {
            Server = UseSSH ? "127.0.0.1" : Address,
            Database = database,
            UserID = User,
            Password = Password,
            ConnectionTimeout = (uint)ConnectionTimeout,
            DefaultCommandTimeout = (uint)CommandTimeout,
            GuidFormat = MySqlGuidFormat.LittleEndianBinary16, // equivalent of MySql.Data "old guids=true"
            ConvertZeroDateTime = true,
            CharacterSet = "utf8mb4",
            AllowUserVariables = true,
            UseXaTransactions = false
        };

        if (UseSSH)
            b.Port = (uint)EstablishedSshPort;
        else if (uint.TryParse(Port, out var port) && port > 0)
            b.Port = port;

        return b.ConnectionString;
    }

    public override ValueTask DisposeAsync()
    {
        if (_lastConnectionString != null)
        {
            try { using var c = new MySqlConnection(_lastConnectionString); MySqlConnection.ClearPool(c); } catch { /* best effort */ }
        }
        return base.DisposeAsync();
    }
}
