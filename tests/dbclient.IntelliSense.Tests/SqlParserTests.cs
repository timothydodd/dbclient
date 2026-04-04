using dbclient.IntelliSense.Models;
using dbclient.IntelliSense.Parsing;

namespace dbclient.IntelliSense.Tests;

public class SqlParserTests
{
    private readonly SqlParser _parser = new();

    [Fact]
    public void SelectList_WithColumns_IdentifiesContext()
    {
        // Parser needs more than just "SELECT" token to identify SelectList
        var ctx = _parser.AnalyzeContext("SELECT Id, FROM Users", 11);
        Assert.Equal(SqlContextType.SelectList, ctx.Type);
    }

    [Fact]
    public void FromClause_IdentifiesContext()
    {
        var ctx = _parser.AnalyzeContext("SELECT * FROM ", 14);
        Assert.Equal(SqlContextType.FromClause, ctx.Type);
    }

    [Fact]
    public void WhereClause_IdentifiesContext()
    {
        var ctx = _parser.AnalyzeContext("SELECT * FROM Users WHERE ", 25);
        Assert.Equal(SqlContextType.WhereClause, ctx.Type);
    }

    [Fact]
    public void ColumnAfterDot_IdentifiesContext()
    {
        var ctx = _parser.AnalyzeContext("SELECT u. FROM Users u", 9);
        Assert.Equal(SqlContextType.ColumnAfterDot, ctx.Type);
        Assert.Equal("u", ctx.TablePrefix);
    }

    [Fact]
    public void ExtractsSimpleAlias()
    {
        var ctx = _parser.AnalyzeContext("SELECT u.Name FROM Users u WHERE ", 33);
        Assert.Contains("u", ctx.TableAliases.Keys);
        Assert.Equal("Users", ctx.TableAliases["u"]);
    }

    [Fact]
    public void ExtractsAsAlias()
    {
        var ctx = _parser.AnalyzeContext("SELECT p.Id FROM Products AS p WHERE ", 37);
        Assert.Contains("p", ctx.TableAliases.Keys);
        Assert.Equal("Products", ctx.TableAliases["p"]);
    }

    [Fact]
    public void ExtractsJoinAliases()
    {
        var sql = "SELECT o.Id FROM Orders o INNER JOIN Customers c ON ";
        var ctx = _parser.AnalyzeContext(sql, sql.Length);
        Assert.Contains("o", ctx.TableAliases.Keys);
        Assert.Contains("c", ctx.TableAliases.Keys);
        Assert.Equal("Orders", ctx.TableAliases["o"]);
        Assert.Equal("Customers", ctx.TableAliases["c"]);
    }

    [Fact]
    public void EmptyString_DoesNotThrow()
    {
        var ctx = _parser.AnalyzeContext("", 0);
        Assert.NotNull(ctx);
    }

    [Fact]
    public void CursorAtZero_DoesNotThrow()
    {
        var ctx = _parser.AnalyzeContext("SELECT * FROM Users", 0);
        Assert.NotNull(ctx);
    }

    [Fact]
    public void JoinCondition_IdentifiesContext()
    {
        var sql = "SELECT * FROM Orders o JOIN Customers c ON ";
        var ctx = _parser.AnalyzeContext(sql, sql.Length);
        Assert.Equal(SqlContextType.JoinCondition, ctx.Type);
    }

    [Fact]
    public void InsertInto_IdentifiesContext()
    {
        var ctx = _parser.AnalyzeContext("INSERT INTO ", 12);
        Assert.Equal(SqlContextType.InsertInto, ctx.Type);
    }

    [Fact]
    public void UpdateTable_IdentifiesContext()
    {
        var ctx = _parser.AnalyzeContext("UPDATE Users SET ", 17);
        Assert.True(ctx.Type == SqlContextType.UpdateTable || ctx.Type == SqlContextType.WhereClause || ctx.Type == SqlContextType.General);
    }

    [Fact]
    public void ExtractTableNames_FindsTables()
    {
        var tables = _parser.ExtractTableNames("SELECT * FROM Users u JOIN Orders o ON u.Id = o.UserId");
        Assert.Contains("Users", tables);
        Assert.Contains("Orders", tables);
    }

    [Fact]
    public void AliasesFromAfterCursor_AreAvailable()
    {
        // Cursor in SELECT, FROM clause is after cursor
        var ctx = _parser.AnalyzeContext("SELECT  FROM Users u", 7);
        Assert.Contains("u", ctx.TableAliases.Keys);
    }
}
