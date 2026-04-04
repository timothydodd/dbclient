using System.Data;
using dbclient.Data.Models;

namespace dbclient.Data.Connections;

public interface IDbConnectionProvider : IAsyncDisposable
{
    string Name { get; }
    string ConnectionType { get; }
    Task<DbMaster> LoadDatabasesAsync(CancellationToken ct = default);
    Task<DbDatabase> LoadDatabaseSchemaAsync(string databaseName, CancellationToken ct = default);
    Task<QueryResult> ExecuteQueryAsync(string database, string sql, CancellationToken ct = default);
    Task<IDbConnection> GetConnectionAsync(string database, CancellationToken ct = default);
}
