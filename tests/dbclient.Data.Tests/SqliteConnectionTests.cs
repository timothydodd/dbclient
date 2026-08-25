using dbclient.Data.Connections;

namespace dbclient.Data.Tests;

public sealed class SqliteConnectionTests : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbclient-sqlite-" + Guid.NewGuid().ToString("N"));
    private readonly string _file;
    private readonly SqliteDbConnection _con;

    public SqliteConnectionTests()
    {
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "test.db");
        _con = new SqliteDbConnection { FileName = _file };
    }

    public async ValueTask DisposeAsync()
    {
        await _con.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private async Task SeedAsync()
    {
        var r = await _con.ExecuteQueryAsync("", """
            CREATE TABLE people (
                id INTEGER PRIMARY KEY,
                name TEXT,
                score REAL,
                data BLOB,
                n INTEGER
            );
            """);
        Assert.False(r.IsError, r.ErrorMessage);
        r = await _con.ExecuteQueryAsync("", """
            INSERT INTO people (id, name, score, data, n) VALUES
              (1, 'O''Brien', 1.5, X'0102', 10),
              (2, NULL, NULL, NULL, NULL),
              (3, 'plain', 2.0, X'', 30),
              (4, 'four', 4.25, X'FF', 40),
              (5, 'five', 5.0, X'00', 50);
            """);
        Assert.False(r.IsError, r.ErrorMessage);
    }

    [Fact]
    public async Task Select_returns_columns_and_rows_with_nulls()
    {
        await SeedAsync();
        var r = await _con.ExecuteQueryAsync("", "SELECT id, name, score, data, n FROM people ORDER BY id");
        Assert.False(r.IsError, r.ErrorMessage);
        var rs = Assert.Single(r.Data!);
        Assert.Equal(new[] { "id", "name", "score", "data", "n" }, rs.ColumnNames);
        Assert.Equal(5, rs.Rows.Count);
        Assert.False(rs.Truncated);

        Assert.Equal(new string?[] { "1", "O'Brien", "1.5", "0x0102", "10" }, rs.Rows[0]);
        Assert.Equal(new string?[] { "2", null, null, null, null }, rs.Rows[1]);
        Assert.Equal("0x", rs.Rows[2][3]);
        Assert.Equal("0xFF", rs.Rows[3][3]);
        Assert.Equal(5, r.AffectedRows);
    }

    [Fact]
    public async Task Zero_row_select_keeps_columns_and_is_not_affected_rows()
    {
        await SeedAsync();
        var r = await _con.ExecuteQueryAsync("", "SELECT id, name FROM people WHERE id > 100");
        Assert.False(r.IsError, r.ErrorMessage);
        var rs = Assert.Single(r.Data!);
        Assert.Equal(new[] { "id", "name" }, rs.ColumnNames);
        Assert.Empty(rs.Rows);
        Assert.Equal(0, r.AffectedRows);
    }

    [Fact]
    public async Task Multi_statement_batch_yields_multiple_result_sets()
    {
        var r = await _con.ExecuteQueryAsync("", "SELECT 1 AS a; SELECT 2 AS b;");
        Assert.False(r.IsError, r.ErrorMessage);
        Assert.Equal(2, r.Data!.Count);
        Assert.Equal("a", r.Data[0].ColumnNames[0]);
        Assert.Equal("1", r.Data[0].Rows[0][0]);
        Assert.Equal("b", r.Data[1].ColumnNames[0]);
        Assert.Equal("2", r.Data[1].Rows[0][0]);
    }

    [Fact]
    public async Task Insert_reports_affected_rows_and_no_result_sets()
    {
        await _con.ExecuteQueryAsync("", "CREATE TABLE t (x INTEGER)");
        var r = await _con.ExecuteQueryAsync("", "INSERT INTO t (x) VALUES (1), (2), (3)");
        Assert.False(r.IsError, r.ErrorMessage);
        Assert.Empty(r.Data!);
        Assert.Equal(3, r.AffectedRows);
    }

    [Fact]
    public async Task MaxRows_truncates()
    {
        await SeedAsync();
        _con.MaxRows = 2;
        var r = await _con.ExecuteQueryAsync("", "SELECT id FROM people ORDER BY id");
        Assert.False(r.IsError, r.ErrorMessage);
        var rs = Assert.Single(r.Data!);
        Assert.Equal(2, rs.Rows.Count);
        Assert.True(rs.Truncated);
        Assert.Equal(new[] { "1", "2" }, rs.Rows.Select(x => x[0]));
    }

    [Fact]
    public async Task MaxRows_not_hit_is_not_truncated()
    {
        await SeedAsync();
        _con.MaxRows = 5;
        var r = await _con.ExecuteQueryAsync("", "SELECT id FROM people");
        Assert.False(Assert.Single(r.Data!).Truncated);
    }

    [Fact]
    public async Task Syntax_error_populates_error()
    {
        var r = await _con.ExecuteQueryAsync("", "SELEC nonsense FROM nowhere");
        Assert.True(r.IsError);
        Assert.False(string.IsNullOrEmpty(r.ErrorMessage));
        Assert.NotNull(r.ErrorCode);
        Assert.Null(r.Data?.FirstOrDefault());
    }

    [Fact]
    public async Task Precancelled_token_returns_error_result()
    {
        // ExecuteQueryAsync catches every exception, so cancellation surfaces as an error result rather than a throw.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var r = await _con.ExecuteQueryAsync("", "SELECT 1", cts.Token);
        Assert.True(r.IsError);
        Assert.Contains("cancel", r.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Load_schema_marks_primary_key()
    {
        await SeedAsync();
        await _con.ExecuteQueryAsync("", "CREATE VIEW v_people AS SELECT id, name FROM people");
        var db = await _con.LoadDatabaseSchemaAsync("test.db");
        Assert.True(db.Loaded);

        var table = Assert.Single(db.Tables, t => t.Name == "people");
        Assert.Equal(new[] { "id", "name", "score", "data", "n" }, table.Columns.Select(c => c.Name));
        var id = table.Columns.Single(c => c.Name == "id");
        Assert.True(id.IsPrimaryKey);
        Assert.Equal("INTEGER", id.DataType);
        Assert.All(table.Columns.Where(c => c.Name != "id"), c => Assert.False(c.IsPrimaryKey));

        var view = Assert.Single(db.Views);
        Assert.Equal("v_people", view.Name);
        Assert.Equal(2, view.Columns.Count);
    }

    [Fact]
    public async Task LoadDatabases_returns_file_name()
    {
        var master = await _con.LoadDatabasesAsync();
        Assert.Equal("test.db", Assert.Single(master.Databases).Name);
        Assert.Equal("test.db", _con.Name);
    }
}
