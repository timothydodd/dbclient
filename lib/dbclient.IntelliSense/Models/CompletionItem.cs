namespace dbclient.IntelliSense.Models;

public class CompletionItem
{
    public string Text { get; set; } = string.Empty;
    public CompletionType Type { get; set; }
    public int Priority { get; set; }
    public string? Description { get; set; }
    public object? Tag { get; set; }

    public CompletionItem() { }

    public CompletionItem(string text, CompletionType type, int priority = 3, string? description = null)
    {
        Text = text;
        Type = type;
        Priority = priority;
        Description = description;
    }

    public override string ToString() => $"{Text} ({Type}, Priority: {Priority})";
}

public class DbColumn
{
    public string Name { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsNullable { get; set; }
    public bool IsPrimaryKey { get; set; }

    public DbColumn() { }

    public DbColumn(string name, string dataType = "")
    {
        Name = name;
        DataType = dataType;
    }
}

public class DbTable
{
    public string Name { get; set; } = string.Empty;
    public string? Schema { get; set; }
    public string? Description { get; set; }

    public DbTable() { }

    public DbTable(string name, string? schema = null)
    {
        Name = name;
        Schema = schema;
    }
}
