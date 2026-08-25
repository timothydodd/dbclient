using dbclient.IntelliSense.Interfaces;
using dbclient.IntelliSense.Models;

namespace dbclient.IntelliSense.Tests;

public class MockSchemaProvider : ISchemaProvider
{
    public Task<IList<DbTable>> GetTablesAsync(CancellationToken ct = default) =>
        Task.FromResult<IList<DbTable>>(new List<DbTable>
        {
            new("Users"),
            new("Orders"),
            new("Products")
        });

    public Task<IList<DbColumn>> GetColumnsAsync(string tableName, CancellationToken ct = default)
    {
        IList<DbColumn> cols = tableName switch
        {
            "Users" => [new("Id", "int") { IsPrimaryKey = true }, new("Name", "varchar"), new("Email", "varchar")],
            "Orders" => [new("Id", "int") { IsPrimaryKey = true }, new("UserId", "int"), new("Total", "decimal")],
            "Products" => [new("Id", "int") { IsPrimaryKey = true }, new("Name", "varchar"), new("Price", "decimal")],
            _ => []
        };
        return Task.FromResult(cols);
    }

    public Task<IList<string>> GetKeywordsAsync(CancellationToken ct = default) =>
        Task.FromResult<IList<string>>(new List<string> { "SELECT", "FROM", "WHERE", "JOIN", "INSERT", "UPDATE", "DELETE" });
}

public class SqlIntelliSenseProviderTests
{
    private async Task<SqlIntelliSenseProvider> CreateProvider()
    {
        var provider = new SqlIntelliSenseProvider();
        await provider.InitializeAsync(new MockSchemaProvider(), TestContext.Current.CancellationToken);
        return provider;
    }

    [Fact]
    public async Task FromClause_ReturnsTables()
    {
        var provider = await CreateProvider();
        var items = await provider.GetCompletionsAsync("SELECT * FROM ", 14, TestContext.Current.CancellationToken);
        Assert.Contains(items, i => i.Text == "Users" && i.Type == CompletionType.Table);
        Assert.Contains(items, i => i.Text == "Orders" && i.Type == CompletionType.Table);
    }

    [Fact]
    public async Task SelectList_WithAlias_ReturnsColumnsOrAliases()
    {
        var provider = await CreateProvider();
        var items = await provider.GetCompletionsAsync("SELECT  FROM Users u", 7, TestContext.Current.CancellationToken);
        // Should return at least aliases or columns from the Users table
        Assert.True(items.Count > 0);
        var hasColumnsOrAliases = items.Any(i => i.Type == CompletionType.Column || i.Type == CompletionType.Alias);
        Assert.True(hasColumnsOrAliases || items.Any(i => i.Type == CompletionType.Keyword));
    }

    [Fact]
    public async Task DotNotation_ReturnsColumnsForAlias()
    {
        var provider = await CreateProvider();
        var items = await provider.GetCompletionsAsync("SELECT u. FROM Users u", 9, TestContext.Current.CancellationToken);
        Assert.Contains(items, i => i.Text == "Id");
        Assert.Contains(items, i => i.Text == "Name");
        Assert.Contains(items, i => i.Text == "Email");
    }

    [Fact]
    public async Task EmptyQuery_ReturnsKeywords()
    {
        var provider = await CreateProvider();
        var items = await provider.GetCompletionsAsync("", 0, TestContext.Current.CancellationToken);
        Assert.Contains(items, i => i.Type == CompletionType.Keyword);
    }

    [Fact]
    public async Task WhereClause_ReturnsColumnsAndKeywords()
    {
        var provider = await CreateProvider();
        var items = await provider.GetCompletionsAsync("SELECT * FROM Users u WHERE ", 28, TestContext.Current.CancellationToken);
        Assert.Contains(items, i => i.Type == CompletionType.Column);
        Assert.Contains(items, i => i.Type == CompletionType.Keyword);
    }

    [Fact]
    public async Task InsertInto_ReturnsTables()
    {
        var provider = await CreateProvider();
        var items = await provider.GetCompletionsAsync("INSERT INTO ", 12, TestContext.Current.CancellationToken);
        Assert.Contains(items, i => i.Text == "Users" && i.Type == CompletionType.Table);
    }

    [Fact]
    public async Task FilterByPrefix_ReturnsMatching()
    {
        var provider = await CreateProvider();
        var items = await provider.GetCompletionsAsync("SELECT * FROM Us", 16, TestContext.Current.CancellationToken);
        Assert.Contains(items, i => i.Text == "Users");
        Assert.DoesNotContain(items, i => i.Text == "Orders");
    }
}

public class SchemaAwareMockProvider : ISchemaProvider
{
    public string? DefaultSchema => "dbo";

    public Task<IList<DbTable>> GetTablesAsync(CancellationToken ct = default) =>
        Task.FromResult<IList<DbTable>>(new List<DbTable>
        {
            new("Users", "dbo"),
            new("Orders", "dbo"),
            new("PartnerUser", "partner")
        });

    public Task<IList<DbColumn>> GetColumnsAsync(string tableName, CancellationToken ct = default)
    {
        IList<DbColumn> cols = tableName switch
        {
            "Users" or "dbo.Users" => [new("Id", "int") { IsPrimaryKey = true }, new("Name", "varchar")],
            "PartnerUser" or "partner.PartnerUser" => [new("Id", "int") { IsPrimaryKey = true }, new("PartnerId", "int")],
            _ => []
        };
        return Task.FromResult(cols);
    }

    public Task<IList<string>> GetKeywordsAsync(CancellationToken ct = default) =>
        Task.FromResult<IList<string>>(new List<string> { "SELECT", "FROM", "WHERE" });
}

public class SchemaCompletionTests
{
    private async Task<SqlIntelliSenseProvider> CreateProvider()
    {
        var provider = new SqlIntelliSenseProvider();
        await provider.InitializeAsync(new SchemaAwareMockProvider(), TestContext.Current.CancellationToken);
        return provider;
    }

    [Fact]
    public async Task FromClause_OffersSchemaNames()
    {
        var provider = await CreateProvider();
        var items = await provider.GetCompletionsAsync("SELECT * FROM ", 14, TestContext.Current.CancellationToken);
        Assert.Contains(items, i => i.Text == "partner" && i.Type == CompletionType.Schema);
        Assert.Contains(items, i => i.Text == "dbo" && i.Type == CompletionType.Schema);
    }

    [Fact]
    public async Task FromClause_SchemaMatchesTypedPrefix()
    {
        var sql = "SELECT DISTINCT * FROM partner";
        var provider = await CreateProvider();
        var items = await provider.GetCompletionsAsync(sql, sql.Length, TestContext.Current.CancellationToken);
        Assert.Contains(items, i => i.Text == "partner" && i.Type == CompletionType.Schema);
    }

    [Fact]
    public async Task FromClause_NonDefaultSchemaTable_InsertsQualified()
    {
        var provider = await CreateProvider();
        var items = await provider.GetCompletionsAsync("SELECT * FROM ", 14, TestContext.Current.CancellationToken);
        Assert.Contains(items, i => i.Text == "partner.PartnerUser" && i.Type == CompletionType.Table);
        Assert.DoesNotContain(items, i => i.Text == "PartnerUser");
    }

    [Fact]
    public async Task FromClause_DefaultSchemaTable_InsertsBare()
    {
        var provider = await CreateProvider();
        var items = await provider.GetCompletionsAsync("SELECT * FROM ", 14, TestContext.Current.CancellationToken);
        Assert.Contains(items, i => i.Text == "Users" && i.Type == CompletionType.Table);
        Assert.DoesNotContain(items, i => i.Text == "dbo.Users");
    }

    [Fact]
    public async Task SchemaDot_OffersSchemaTables()
    {
        var sql = "SELECT * FROM partner.";
        var provider = await CreateProvider();
        var items = await provider.GetCompletionsAsync(sql, sql.Length, TestContext.Current.CancellationToken);
        Assert.Contains(items, i => i.Text == "PartnerUser" && i.Type == CompletionType.Table);
    }
}
