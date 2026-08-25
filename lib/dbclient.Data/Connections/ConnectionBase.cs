using System.Data;
using System.Data.Common;
using Dapper;
using dbclient.Data.Models;

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

    /// <summary>Max rows per result set to prevent unbounded memory growth. 0 = unlimited.</summary>
    public int MaxRows { get; set; } = 100_000;

    // SSH tunnel
    public bool UseSSH { get; set; }
    public string SshHost { get; set; } = "";
    public string SshUser { get; set; } = "";
    public string SshPassword { get; set; } = "";
    public int SshRemotePort { get; set; }
    public string SshKeyFile { get; set; } = "";
    public string SshKeyPassphrase { get; set; } = "";
    protected int EstablishedSshPort { get; set; }
    private SshTunnel? _sshTunnel;
    private readonly SemaphoreSlim _sshLock = new(1, 1);

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
            using var messages = SubscribeInfoMessages(con, result.Messages);

            // Always use the reader path. It returns any result sets a batch
            // produces (SELECT, EXEC of a proc, OUTPUT clauses, etc.) and falls
            // back to the affected-row count when no rows are returned. Sniffing
            // the leading keyword is unreliable — batches that start with a
            // comment, DECLARE, SET, or EXEC can still return result sets.
            result.Data = new List<ResultSet>();
            using var cmd = ((DbConnection)con).CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = CommandTimeout;
            using var reader = await cmd.ExecuteReaderAsync(ct);
            do
            {
                // A schema with zero rows is still a result set (headers must be shown).
                if (reader.FieldCount == 0) continue;

                var rs = ReadColumns(reader);
                await ReadRowsAsync(reader, rs, MaxRows, ct);
                result.Data.Add(rs);
            } while (await reader.NextResultAsync(ct));

            // RecordsAffected is only valid after the reader is consumed/closed.
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

    /// <summary>Providers override to route server info messages (PRINT, warnings) into <paramref name="sink"/>.</summary>
    protected virtual IDisposable? SubscribeInfoMessages(IDbConnection con, List<string> sink) => null;

    protected static ResultSet ReadColumns(DbDataReader reader)
    {
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
        return rs;
    }

    /// <summary>Reads rows into <paramref name="rs"/> honouring the row cap; sets <see cref="ResultSet.Truncated"/> when the cap is hit.</summary>
    protected static async Task ReadRowsAsync(DbDataReader reader, ResultSet rs, int maxRows, CancellationToken ct)
    {
        var fieldCount = reader.FieldCount;
        var buffer = new object[fieldCount];
        while (await reader.ReadAsync(ct))
        {
            if (maxRows > 0 && rs.Rows.Count >= maxRows)
            {
                rs.Truncated = true;
                break;
            }

            reader.GetValues(buffer);
            var values = new string?[fieldCount];
            for (int i = 0; i < fieldCount; i++)
                values[i] = CellFormatter.Format(buffer[i]);
            rs.Rows.Add(values);
        }
    }

    /// <summary>
    /// Ensures the SSH tunnel is up when <see cref="UseSSH"/> is set. Reconnects if a previous tunnel dropped.
    /// </summary>
    protected async Task EnsureSshTunnelAsync(CancellationToken ct = default)
    {
        if (!UseSSH) return;

        await _sshLock.WaitAsync(ct);
        try
        {
            if (_sshTunnel != null && _sshTunnel.IsConnected) return;

            _sshTunnel?.Dispose();
            _sshTunnel = null;

            if (!int.TryParse(Port, out var sshPort) || sshPort <= 0 || sshPort > 65535)
                throw new ArgumentException($"Invalid SSH port: '{Port}'. Port must be a number between 1 and 65535.");

            var tunnel = new SshTunnel(SshHost, sshPort, SshUser, SshPassword, SshKeyFile, (uint)SshRemotePort, SshKeyPassphrase);
            try
            {
                await tunnel.ConnectAsync(ct);
            }
            catch
            {
                tunnel.Dispose();
                throw;
            }

            _sshTunnel = tunnel;
            EstablishedSshPort = tunnel.LocalPort;
        }
        finally
        {
            _sshLock.Release();
        }
    }

    protected async Task ReadDataAsync(DbConnection con, string sql, Action<IDataReader> callback, CancellationToken ct = default)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = CommandTimeout;
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            callback(reader);
    }

    protected CommandDefinition Command(string sql, object? parameters = null, CancellationToken ct = default) =>
        new(sql, parameters, commandTimeout: CommandTimeout, cancellationToken: ct);

    protected static void PopulateErrorDetails(QueryResult result, Exception ex)
    {
        if (ex is Microsoft.Data.SqlClient.SqlException sqlEx)
        {
            result.ErrorCode = sqlEx.Number;
            result.ErrorLine = sqlEx.LineNumber;
            result.SqlState = sqlEx.State.ToString();
        }
        else if (ex is MySqlConnector.MySqlException myEx)
        {
            result.ErrorCode = myEx.Number;
            result.SqlState = myEx.SqlState;
        }
        else if (ex is Microsoft.Data.Sqlite.SqliteException liteEx)
        {
            result.ErrorCode = liteEx.SqliteErrorCode;
        }
    }

    public virtual ValueTask DisposeAsync()
    {
        _sshTunnel?.Dispose();
        _sshTunnel = null;
        return ValueTask.CompletedTask;
    }
}
