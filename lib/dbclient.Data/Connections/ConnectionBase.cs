using System.Data;
using System.Data.Common;
using Dapper;
using dbclient.Data.Models;

// Max rows per result set to prevent unbounded memory growth
// Set to 0 for unlimited

namespace dbclient.Data.Connections;

public abstract class ConnectionBase : IDbConnectionProvider
{
    public abstract string Name { get; }
    public abstract string ConnectionType { get; }

    public string Address { get; set; } = "";
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public string Port { get; set; } = "";
    public int ConnectionTimeout { get; set; } = 15;
    public int CommandTimeout { get; set; } = 30;
    public int MaxRows { get; set; } = 100_000;

    // SSH tunnel
    public bool UseSSH { get; set; }
    public string SshHost { get; set; } = "";
    public string SshUser { get; set; } = "";
    public string SshPassword { get; set; } = "";
    public int SshRemotePort { get; set; }
    public string SshKeyFile { get; set; } = "";
    protected int EstablishedSshPort { get; set; }
    private SshTunnel? _sshTunnel;

    public abstract Task<IDbConnection> GetConnectionAsync(string database, CancellationToken ct = default);
    public abstract Task<DbMaster> LoadDatabasesAsync(CancellationToken ct = default);
    public abstract Task<DbDatabase> LoadDatabaseSchemaAsync(string databaseName, CancellationToken ct = default);

    public virtual async Task<QueryResult> ExecuteQueryAsync(string database, string sql, CancellationToken ct = default)
    {
        var result = new QueryResult();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            using var con = await GetConnectionAsync(database, ct);

            // Try to detect if it's a SELECT-like query
            var trimmed = sql.TrimStart();
            var isSelect = trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) ||
                           trimmed.StartsWith("WITH", StringComparison.OrdinalIgnoreCase) ||
                           trimmed.StartsWith("SHOW", StringComparison.OrdinalIgnoreCase) ||
                           trimmed.StartsWith("DESCRIBE", StringComparison.OrdinalIgnoreCase) ||
                           trimmed.StartsWith("EXPLAIN", StringComparison.OrdinalIgnoreCase) ||
                           trimmed.StartsWith("PRAGMA", StringComparison.OrdinalIgnoreCase);

            if (isSelect)
            {
                result.Data = new List<ResultSet>();
                using var cmd = ((DbConnection)con).CreateCommand();
                cmd.CommandText = sql;
                cmd.CommandTimeout = CommandTimeout;
                using var reader = await cmd.ExecuteReaderAsync(ct);
                do
                {
                    if (reader.FieldCount == 0) continue;

                    var rs = new ResultSet
                    {
                        ColumnNames = new string[reader.FieldCount],
                        ColumnTypes = new string?[reader.FieldCount]
                    };

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

                    result.Data.Add(rs);
                } while (await reader.NextResultAsync(ct));
                result.AffectedRows = result.Data.Sum(d => d.Rows.Count);
            }
            else
            {
                result.AffectedRows = await con.ExecuteAsync(new CommandDefinition(sql, commandTimeout: CommandTimeout, cancellationToken: ct));
            }
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

    protected void EnsureSshTunnel()
    {
        if (UseSSH && _sshTunnel == null)
        {
            if (!int.TryParse(Port, out var sshPort) || sshPort <= 0 || sshPort > 65535)
                throw new ArgumentException($"Invalid SSH port: '{Port}'. Port must be a number between 1 and 65535.");

            _sshTunnel = new SshTunnel(SshHost, sshPort, SshUser, SshPassword, SshKeyFile, (uint)SshRemotePort);
            EstablishedSshPort = _sshTunnel.LocalPort;
        }
    }

    protected static async Task ReadDataAsync(DbConnection con, string sql, Action<IDataReader> callback)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            callback(reader);
    }

    protected static void PopulateErrorDetails(QueryResult result, Exception ex)
    {
        if (ex is Microsoft.Data.SqlClient.SqlException sqlEx)
        {
            result.ErrorCode = sqlEx.Number;
            result.ErrorLine = sqlEx.LineNumber;
            result.SqlState = sqlEx.State.ToString();
        }
        else if (ex is MySql.Data.MySqlClient.MySqlException myEx)
        {
            result.ErrorCode = (int)myEx.Number;
            result.SqlState = myEx.SqlState;
        }
        else if (ex is System.Data.SQLite.SQLiteException liteEx)
        {
            result.ErrorCode = (int)liteEx.ResultCode;
        }
    }

    public virtual ValueTask DisposeAsync()
    {
        _sshTunnel?.Dispose();
        _sshTunnel = null;
        return ValueTask.CompletedTask;
    }
}
