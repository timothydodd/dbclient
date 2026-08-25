using dbclient.Models;
using dbclient.Services;
using dbclient.Views;

namespace dbclient.Data.Tests;

public class UpdateSqlGeneratorTests
{
    private static readonly string[] Cols = ["id", "name", "qty"];
    private static readonly string?[] Types = ["int", "nvarchar", "decimal"];

    private static List<ResultRow> Rows(params string?[][] rows) => rows.Select(r => new ResultRow(r)).ToList();

    private static UpdateScript Gen(
        ConnectionType type,
        List<ResultRow> original,
        List<ResultRow> current,
        IEnumerable<int> dirty,
        string query = "SELECT id, name, qty FROM people",
        HashSet<string>? pk = null,
        string? schema = null)
        => UpdateSqlGenerator.Generate("people", schema, type,
            pk ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id" },
            Cols, Types, original, current, dirty, query);

    [Fact]
    public void No_primary_key_is_error()
    {
        var orig = Rows(["1", "a", "1"]);
        var cur = Rows(["1", "b", "1"]);
        var s = Gen(ConnectionType.SqlServer, orig, cur, [0], pk: new HashSet<string>());
        Assert.True(s.IsError);
        Assert.Contains("primary key", s.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("", s.Sql);
    }

    [Fact]
    public void Pk_missing_from_select_is_error()
    {
        var orig = Rows(["1", "a", "1"]);
        var cur = Rows(["1", "b", "1"]);
        var s = Gen(ConnectionType.SqlServer, orig, cur, [0], pk: new HashSet<string> { "other_id" });
        Assert.True(s.IsError);
        Assert.Contains("other_id", s.Error);
    }

    [Theory]
    [InlineData("SELECT p.id, p.name, p.qty FROM people p JOIN other o ON o.id = p.id", "JOIN")]
    [InlineData("SELECT id, name AS n, qty FROM people", "alias")]
    [InlineData("SELECT id, name n, qty FROM people", "alias")]
    [InlineData("SELECT id, name, qty FROM people WHERE id IN (SELECT id FROM x)", "subquery")]
    [InlineData("SELECT DISTINCT id, name, qty FROM people", "aggregates")]
    [InlineData("SELECT id, UPPER(name), qty FROM people", "function")]
    [InlineData("SELECT id, name, qty * 2 FROM people", "computed")]
    [InlineData("UPDATE people SET name = 'x'", "not a SELECT")]
    [InlineData("", "No query")]
    public void Unsafe_queries_are_rejected(string query, string reason)
    {
        var orig = Rows(["1", "a", "1"]);
        var cur = Rows(["1", "b", "1"]);
        var s = Gen(ConnectionType.SqlServer, orig, cur, [0], query);
        Assert.True(s.IsError);
        Assert.Contains(reason, s.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Comments_are_ignored_by_validation()
    {
        var orig = Rows(["1", "a", "1"]);
        var cur = Rows(["1", "b", "1"]);
        var s = Gen(ConnectionType.SqlServer, orig, cur, [0], "-- JOIN in a comment\nSELECT id, name, qty /* AS x */ FROM people");
        Assert.False(s.IsError, s.Error);
    }

    [Fact]
    public void SqlServer_happy_path()
    {
        var orig = Rows(["1", "a", "1"], ["2", "b", "2"], ["3", "c", "3"]);
        var cur = Rows(["1", "O'Brien", "1"], ["2", "b", "2.5"], ["3", null, "3"]);
        var s = Gen(ConnectionType.SqlServer, orig, cur, [2, 0, 1]);
        Assert.False(s.IsError, s.Error);
        Assert.Equal(3, s.ExpectedStatements);

        var sql = s.Sql;
        Assert.StartsWith("SET XACT_ABORT ON;", sql);
        Assert.Contains("BEGIN TRAN;", sql);
        Assert.EndsWith("COMMIT;", sql);
        Assert.Equal(3, sql.Split("IF @@ROWCOUNT <> 1 THROW 50000, 'Expected 1 row', 1;").Length - 1);

        Assert.Contains("UPDATE [people] SET [name] = 'O''Brien' WHERE [id] = 1;", sql);
        Assert.Contains("UPDATE [people] SET [qty] = 2.5 WHERE [id] = 2;", sql);
        Assert.Contains("UPDATE [people] SET [name] = NULL WHERE [id] = 3;", sql);

        // WHERE only on the PK, never on the other columns; statements ordered by row index.
        Assert.DoesNotContain("[name] = 'a'", sql);
        Assert.True(sql.IndexOf("WHERE [id] = 1") < sql.IndexOf("WHERE [id] = 2"));
        Assert.True(sql.IndexOf("WHERE [id] = 2") < sql.IndexOf("WHERE [id] = 3"));
    }

    [Fact]
    public void Schema_is_quoted_separately()
    {
        var orig = Rows(["1", "a", "1"]);
        var cur = Rows(["1", "b", "1"]);
        var s = Gen(ConnectionType.SqlServer, orig, cur, [0], schema: "dbo");
        Assert.Contains("UPDATE [dbo].[people] SET", s.Sql);
    }

    [Fact]
    public void MySql_wrapper_and_backticks()
    {
        var orig = Rows(["1", "a", "1"]);
        var cur = Rows(["1", "b", "1"]);
        var s = Gen(ConnectionType.MySql, orig, cur, [0]);
        Assert.False(s.IsError, s.Error);
        Assert.Equal("START TRANSACTION;\nUPDATE `people` SET `name` = 'b' WHERE `id` = 1;\nCOMMIT;",
            s.Sql.Replace("\r\n", "\n"));
        Assert.DoesNotContain("XACT_ABORT", s.Sql);
    }

    [Fact]
    public void Sqlite_wrapper_and_double_quotes()
    {
        var orig = Rows(["1", "a", "1"]);
        var cur = Rows(["1", "b", "1"]);
        var s = Gen(ConnectionType.Sqlite, orig, cur, [0]);
        Assert.False(s.IsError, s.Error);
        Assert.Equal("BEGIN;\nUPDATE \"people\" SET \"name\" = 'b' WHERE \"id\" = 1;\nCOMMIT;",
            s.Sql.Replace("\r\n", "\n"));
    }

    [Fact]
    public void Null_pk_uses_IS_NULL()
    {
        var orig = Rows([null, "a", "1"]);
        var cur = Rows([null, "b", "1"]);
        var s = Gen(ConnectionType.SqlServer, orig, cur, [0]);
        Assert.Contains("WHERE [id] IS NULL;", s.Sql);
    }

    [Fact]
    public void Typed_NULL_literal_in_pk_uses_IS_NULL()
    {
        var orig = Rows(["null", "a", "1"]);
        var cur = Rows(["null", "b", "1"]);
        var s = Gen(ConnectionType.SqlServer, orig, cur, [0]);
        Assert.Contains("WHERE [id] IS NULL;", s.Sql);
    }

    [Fact]
    public void Unchanged_dirty_rows_are_skipped_and_none_is_error()
    {
        var orig = Rows(["1", "a", "1"], ["2", "b", "2"]);
        var cur = Rows(["1", "a", "1"], ["2", "b", "2"]);
        var s = Gen(ConnectionType.SqlServer, orig, cur, [0, 1]);
        Assert.True(s.IsError);
        Assert.Equal("No changes to apply.", s.Error);
    }

    [Fact]
    public void Out_of_range_dirty_indices_are_ignored()
    {
        var orig = Rows(["1", "a", "1"]);
        var cur = Rows(["1", "b", "1"]);
        var s = Gen(ConnectionType.SqlServer, orig, cur, [0, 7]);
        Assert.False(s.IsError, s.Error);
        Assert.Equal(1, s.ExpectedStatements);
    }

    [Fact]
    public void Composite_pk_all_columns_in_where()
    {
        var orig = Rows(["1", "a", "1"]);
        var cur = Rows(["1", "a", "9"]);
        var s = Gen(ConnectionType.SqlServer, orig, cur, [0],
            pk: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ID", "NAME" });
        Assert.False(s.IsError, s.Error);
        Assert.Contains("SET [qty] = 9 WHERE [id] = 1 AND [name] = 'a';", s.Sql);
    }

    [Theory]
    [InlineData(null, "int", "NULL")]
    [InlineData("null", "int", "NULL")]
    [InlineData("42", "int", "42")]
    [InlineData(" 42 ", "bigint", "42")]
    [InlineData("1e3", "float", "1e3")]
    [InlineData("true", "bit", "1")]
    [InlineData("FALSE", "bit", "0")]
    [InlineData("abc", "int", "'abc'")]
    [InlineData("42", "varchar", "'42'")]
    [InlineData("it's", null, "'it''s'")]
    public void Literal_formats(string? value, string? type, string expected)
        => Assert.Equal(expected, UpdateSqlGenerator.Literal(value, type));

    [Theory]
    [InlineData("SELECT * FROM dbo.People p", "dbo", "People")]
    [InlineData("select * from [dbo].[People]", "dbo", "People")]
    [InlineData("SELECT * FROM `t`", null, "t")]
    [InlineData("SELECT 1", null, null)]
    public void ParseTableRef(string query, string? schema, string? table)
    {
        var r = UpdateSqlGenerator.ParseTableRef(query);
        if (table == null) { Assert.Null(r); return; }
        Assert.Equal((schema, table), r!.Value);
    }
}
