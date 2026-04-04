# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build and Development Commands

```bash
# Build the entire solution
dotnet build

# Run the application
dotnet run --project src/dbclient/dbclient.csproj

# Build individual projects
dotnet build lib/dbclient.IntelliSense/
dotnet build lib/dbclient.Data/
dotnet build src/dbclient/

# Clean and rebuild (useful if WSL file locks cause MSB3021 errors)
dotnet clean && dotnet build

# Restore packages
dotnet restore
```

Solution file: `dbclient.slnx`

## Architecture Overview

Modern SQL database client built with **Avalonia UI 11.3** and **.NET 10.0**, ported from the CSVQuery WPF project. Color scheme based on CSVQuery's ExpressionDark theme.

### Project Structure

```
dbclient/
├── src/dbclient/              # Avalonia UI application
│   ├── Views/                 # AXAML views + code-behind
│   ├── ViewModels/            # MVVM view models (INotifyPropertyChanged)
│   ├── Services/              # SchemaService, MockSchemaProvider
│   ├── Models/                # AppState, ConnectionConfig
│   ├── Themes/                # Dark.axaml (default), Dracula.axaml (alternate)
│   └── Assets/                # sql.xshd (syntax highlighting, embedded resource)
├── lib/dbclient.IntelliSense/ # SQL IntelliSense engine (zero dependencies)
│   ├── Parsing/SqlParser.cs   # Regex tokenizer, context detection, alias tracking
│   ├── SqlIntelliSenseProvider.cs  # Completion generation from parsed context
│   ├── Interfaces/            # IIntelliSenseProvider, ISchemaProvider, ISqlParser
│   └── Models/                # CompletionItem, SqlContext, DbTable, DbColumn
└── lib/dbclient.Data/         # Database abstraction layer
    ├── Connections/            # SqlServer, MySQL, SQLite implementations + SSH
    └── Models/                # DbMaster, DbDatabase, DbTable, QueryResult
```

### Key Components

#### Two-Level Tab System
- **Connection tabs** (top strip) — one per database connection, color-coded by name hash (deterministic HSL). Switching tabs swaps the entire context.
- **Query sub-tabs** (below) — isolated per connection. Each has its own editor text, results, IntelliSense.
- Both tab strips are inside the right column only; the left panel is independent.

#### IntelliSense (lib/dbclient.IntelliSense/)
- **SqlParser.cs** — Parses SQL at cursor position, returns SqlContext with context type (SelectList, FromClause, WhereClause, ColumnAfterDot, JoinCondition, etc.). Extracts aliases from the **full query text** (including after cursor) so SELECT columns get completions from tables in a FROM clause that's written after the cursor.
- **SqlIntelliSenseProvider.cs** — Generates filtered CompletionItem list based on context. Only shows columns when tables exist in the FROM clause. Deduplicates and ranks by priority.
- **ISchemaProvider** — Implemented by SchemaService (real DB) with dialect-specific keywords.
- Zero dependencies — defines its own DbTable/DbColumn models.

#### Database Connections (lib/dbclient.Data/)
- **ConnectionBase.cs** — Abstract base with Dapper query execution, SSH tunnel management
- **SqlServerConnection.cs** — INFORMATION_SCHEMA queries, SqlClient
- **MySqlDbConnection.cs** — INFORMATION_SCHEMA + SHOW KEYS
- **SqliteConnection.cs** — sqlite_master + PRAGMA table_info
- **SshTunnel.cs** — SSH.NET port forwarding with dynamic local port

#### UI (src/dbclient/)
- **MainWindow.axaml** — Custom title bar (36x28 window buttons), connection tab strip, query tab strip, connection panel (left), editor+results split (center), status bar
- **EditorView.axaml.cs** — AvaloniaEdit + CompletionWindow. Reads `IntelliSenseProvider` live from ViewModel on each completion trigger (not cached). Dot typing closes existing completion window and opens fresh column completions. SQL highlighting loaded from embedded `sql.xshd` resource.
- **ResultsPanel.axaml.cs** — Converts DataTable to `List<string?[]>` with manual `DataGridTextColumn` using integer indexer bindings. Subscribes to `ResultData` property changes.
- **ConnectionPanel.axaml** — Schema tree (databases as top-level nodes, active one expanded) + connections overlay. Tree binding managed in code-behind (`tree.ItemsSource = connTab.ConnectionTree`) to avoid compiled binding chain issues. Double-click table inserts `SELECT * FROM` with dialect-appropriate quoting. Ctrl+C copies selected node name.
- **ConnectionDialog.axaml** — Modal for new/edit connection
- **SqlCompletionData.cs** — Bridges CompletionItem to ICompletionData with colored type indicators

#### ViewModels
- **MainWindowViewModel** — Holds `ConnectionTabs` collection, `SavedConnections`, state persistence. Auto-selects first query tab when switching connection tabs.
- **ConnectionTabViewModel** — Per-connection: IDbConnectionProvider, IntelliSenseProvider, QueryTabs, schema tree, database list, color. `ConnectAsync` uses `force: true` on `SwitchDatabaseAsync` to ensure schema loads even when ActiveDatabase matches saved state.
- **SessionTabViewModel** — Per-query-tab: QueryText, ResultData, cursor. `SetQueryText()` fires `QueryTextSet` event so EditorView syncs the AvaloniaEdit control.

#### State & Services
- **AppState.cs** — JSON to `~/.dbclient/state.json`. Nested: `AppState → ConnectionTabState[] → TabState[]`. Auto-saves on query execute (in `finally` block) and app shutdown.
- **SchemaService.cs** — Bridges Data→IntelliSense models. Returns **dialect-specific keywords**: SQL Server (`GETDATE()`, `ISNULL`, `TOP`, `@@ROWCOUNT`), MySQL (`NOW()`, `IFNULL`, `GROUP_CONCAT`, `JSON_EXTRACT`), SQLite (`datetime('now')`, `strftime`, `PRAGMA`).

### Technology Stack
- **.NET 10.0** cross-platform
- **Avalonia UI 11.3.11** — Cross-platform UI framework
- **AvaloniaEdit 11.4.0** — Text editor with CompletionWindow, syntax highlighting
- **Avalonia.Controls.DataGrid 11.3.11** — Results grid
- **Dapper** — Query execution
- **Microsoft.Data.SqlClient, MySql.Data, System.Data.SQLite.Core** — DB drivers
- **SSH.NET** — SSH tunnel support
- **System.Text.Json** — State serialization

### Avalonia-Specific Gotchas

#### DataGrid
- Requires `<StyleInclude Source="avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml"/>` in App.axaml or it renders invisible.
- Data bound as `List<string?[]>` with manual `DataGridTextColumn` + integer indexer bindings `[0]`, `[1]` — the only approach that reliably shows cell text. ExpandoObject, DataView, Dictionary all fail.
- Column header template is overridden in theme to remove sort icon space.

#### Compiled Bindings
- Cannot resolve dynamic/dictionary properties.
- Chained property paths like `SelectedConnectionTab.ConnectionTree` don't reliably update when the intermediate property changes. Use code-behind to set `ItemsSource` directly instead.
- Inside `DataTemplate`, use `$parent[Window].((vm:Type)DataContext).Property` instead of `#ElementName` for cross-scope references.

#### Themes / Styling
- Menu separators in Fluent theme have their own background. Fix: set `MenuItem` and `MenuItem /template/ Border#PART_LayoutRoot` to `Transparent` so they inherit from the popup container. Style `MenuFlyoutPresenter` and `MenuItem /template/ Popup > Border` for popup background.
- TreeView chevron gap: override `ToggleButton#PART_ExpandCollapseChevron` width/padding in theme. Content margin can use negative values to pull text closer.
- TreeView selection: style `Border#PART_LayoutRoot` for selection highlight, set `ContentPresenter#PART_HeaderPresenter` to Transparent to remove default gray box.
- ScrollBar auto-hide: set `AllowAutoHide="False"` both globally and inside DataGrid's own `<DataGrid.Styles>`.
- `AvaloniaResource` in csproj: don't add explicit `<AvaloniaResource Include="Assets\**"/>` — it overrides the SDK default that auto-includes `*.axaml` files, causing "No precompiled XAML found" errors.

#### sql.xshd Syntax Highlighting
- Must be an `<EmbeddedResource>` in csproj, NOT an AvaloniaResource.
- Loaded via `Assembly.GetManifestResourceStream("dbclient.Assets.sql.xshd")`.

### Known Issues
- WSL builds on /mnt/f may get MSB3021 file lock errors if the app is running. Build from Windows terminal or close the app first.
- WSL may also produce stale `InitializeComponent` errors when obj directory is locked. These are not real compilation errors.

### Ported From
Originally ported from CSVQuery (WPF/.NET 9.0) at `/mnt/f/github/CSVQuery/`. Key files ported:
- IntelliSense: `CSVQuery/lib/CSVQuery.IntelliSense/` → `lib/dbclient.IntelliSense/`
- DB connections: `CSVQuery/CSVQuery/BL/DbSQLConnection.cs`, `DbMySQLConnection.cs`, `DbLiteConnection.cs`
- DB models: `CSVQuery/CSVQuery/BL/SQLMaster/DbMaster.cs`
- SSH tunnel: `CSVQuery/lib/dbcommon/net/SSHTunnel.cs`
- Color scheme: `CSVQuery/lib/dbcommon.wpf/Styles/ExpressionDark.xaml` + `CSVQuery/Styles/CustomDark.xaml`
- UI reference: NoteMode project at `/mnt/f/github/NoteMode/` (Avalonia, MVVM, custom title bar)
