using dbclient.Data;

namespace dbclient.Data.Tests;

public class SqlIdentifierTests
{
    [Theory]
    [InlineData("col", "[col]")]
    [InlineData("a]b", "[a]]b]")]
    [InlineData("a[b", "[a[b]")]
    public void SqlServer_uses_brackets_and_doubles_closing(string name, string expected)
        => Assert.Equal(expected, SqlIdentifier.Quote(SqlDialect.SqlServer, name));

    [Theory]
    [InlineData("col", "`col`")]
    [InlineData("a`b", "`a``b`")]
    public void MySql_uses_backticks_and_doubles_backtick(string name, string expected)
        => Assert.Equal(expected, SqlIdentifier.Quote(SqlDialect.MySql, name));

    [Theory]
    [InlineData("col", "\"col\"")]
    [InlineData("a\"b", "\"a\"\"b\"")]
    public void Sqlite_uses_double_quotes_and_doubles_quote(string name, string expected)
        => Assert.Equal(expected, SqlIdentifier.Quote(SqlDialect.Sqlite, name));

    [Fact]
    public void Null_name_throws()
        => Assert.Throws<ArgumentNullException>(() => SqlIdentifier.Quote(SqlDialect.Sqlite, null!));

    [Fact]
    public void Unknown_dialect_throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => SqlIdentifier.Quote((SqlDialect)99, "x"));
}
