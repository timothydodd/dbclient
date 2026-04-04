namespace dbclient.Data.Models;

public class DbMaster
{
    public List<DbDatabase> Databases { get; set; } = new();
}

public class DbDatabase
{
    public string Name { get; set; } = string.Empty;
    public bool Loaded { get; set; }
    public HashSet<string> Schemas { get; set; } = new();
    public List<DbTable> Tables { get; set; } = new();
    public List<DbView> Views { get; set; } = new();
    public List<DbProc> StoredProcedures { get; set; } = new();
}

public class DbTable
{
    public string Name { get; set; } = string.Empty;
    public string Schema { get; set; } = string.Empty;
    public List<DbColumn> Columns { get; set; } = new();
}

public class DbView
{
    public string Name { get; set; } = string.Empty;
    public string Schema { get; set; } = string.Empty;
    public List<DbColumn> Columns { get; set; } = new();
}

public class DbProc
{
    public string Name { get; set; } = string.Empty;
    public string Schema { get; set; } = string.Empty;
}

public class DbColumn
{
    public string Name { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public int DataLength { get; set; }
    public bool IsPrimaryKey { get; set; }
    public bool IsNullable { get; set; }
}
