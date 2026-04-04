using dbclient.IntelliSense.Interfaces;
using dbclient.IntelliSense.Models;

namespace dbclient.Services;

public class MockSchemaProvider : ISchemaProvider
{
    private static readonly IList<DbTable> Tables =
    [
        new("patient", "dbo"),
        new("appointment", "dbo"),
        new("tenant", "dbo"),
        new("doctor", "dbo"),
        new("medication", "dbo"),
    ];

    private static readonly Dictionary<string, IList<DbColumn>> ColumnsByTable = new(StringComparer.OrdinalIgnoreCase)
    {
        ["patient"] =
        [
            new("id", "int") { IsPrimaryKey = true },
            new("name", "varchar(100)"),
            new("email", "varchar(255)"),
            new("date_of_birth", "date") { IsNullable = true },
            new("phone", "varchar(20)") { IsNullable = true },
            new("address", "varchar(500)") { IsNullable = true },
            new("tenant_id", "int"),
            new("created_at", "datetime"),
            new("updated_at", "datetime") { IsNullable = true },
        ],
        ["appointment"] =
        [
            new("id", "int") { IsPrimaryKey = true },
            new("patient_id", "int"),
            new("doctor_id", "int"),
            new("appointment_date", "datetime"),
            new("status", "varchar(50)"),
            new("notes", "text") { IsNullable = true },
            new("tenant_id", "int"),
            new("created_at", "datetime"),
        ],
        ["tenant"] =
        [
            new("id", "int") { IsPrimaryKey = true },
            new("name", "varchar(100)"),
            new("domain", "varchar(255)"),
            new("plan", "varchar(50)"),
            new("is_active", "bit"),
            new("created_at", "datetime"),
        ],
        ["doctor"] =
        [
            new("id", "int") { IsPrimaryKey = true },
            new("name", "varchar(100)"),
            new("specialty", "varchar(100)"),
            new("email", "varchar(255)"),
            new("tenant_id", "int"),
        ],
        ["medication"] =
        [
            new("id", "int") { IsPrimaryKey = true },
            new("name", "varchar(200)"),
            new("dosage", "varchar(100)"),
            new("patient_id", "int"),
            new("prescribed_by", "int"),
            new("start_date", "date"),
            new("end_date", "date") { IsNullable = true },
        ],
    };

    private static readonly IList<string> Keywords =
    [
        "SELECT", "FROM", "WHERE", "JOIN", "INNER JOIN", "LEFT JOIN", "RIGHT JOIN",
        "CROSS JOIN", "FULL JOIN", "ON", "AND", "OR", "NOT", "IN", "BETWEEN",
        "LIKE", "IS", "NULL", "IS NOT NULL", "EXISTS", "CASE", "WHEN", "THEN",
        "ELSE", "END", "AS", "DISTINCT", "TOP", "ORDER BY", "GROUP BY", "HAVING",
        "INSERT INTO", "VALUES", "UPDATE", "SET", "DELETE", "CREATE", "ALTER",
        "DROP", "TABLE", "INDEX", "VIEW", "PROCEDURE", "FUNCTION", "TRIGGER",
        "BEGIN", "COMMIT", "ROLLBACK", "UNION", "UNION ALL", "EXCEPT", "INTERSECT",
        "COUNT", "SUM", "AVG", "MIN", "MAX", "COALESCE", "ISNULL", "CAST", "CONVERT",
        "GETDATE", "DATEADD", "DATEDIFF", "YEAR", "MONTH", "DAY",
        "ASC", "DESC", "LIMIT", "OFFSET", "FETCH", "NEXT", "ROWS", "ONLY",
    ];

    public Task<IList<DbTable>> GetTablesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Tables);

    public Task<IList<DbColumn>> GetColumnsAsync(string tableName, CancellationToken cancellationToken = default)
    {
        if (ColumnsByTable.TryGetValue(tableName, out var columns))
            return Task.FromResult(columns);
        return Task.FromResult<IList<DbColumn>>([]);
    }

    public Task<IList<string>> GetKeywordsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Keywords);
}
