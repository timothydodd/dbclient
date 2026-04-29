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
│   ├── Services/              # SchemaService, ThemeColors, ResultsClipboard, etc.
│   ├── Models/                # AppState, ConnectionConfig, QueryHistory
│   ├── Themes/                # Dark.axaml (default), Dracula.axaml, Light.axaml
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
- **ConnectionPanel.axaml** — Schema tree (databases as top-level nodes, active one expanded) + connections overlay. Tree binding managed in code-behind (`tree.ItemsSource = connTab.ConnectionTree`) to avoid compiled binding chain issues. Double-click table inserts `SELECT * FROM` with dialect-appropriate quoting. Ctrl+C copies selected node name. Always-visible search bar above the tree filters tables/columns; Ctrl+F focuses it. Connection failure shows an error overlay with a Retry button (toggled via `HasConnectionError`).
- **HistoryPanel.axaml** — Per-connection query history list. Subscribes to `ThemeColors.ThemeChanged` to refresh dynamically-built rows.
- **ConnectionDialog.axaml** — Modal for new/edit connection
- **SqlCompletionData.cs** — Bridges CompletionItem to ICompletionData with colored type indicators

#### Schema Tree Filter
- **Always-visible search bar** at the top of `ConnectionPanel`, with a flyout (funnel icon) to scope between `Tables` and `Columns`. Each `ConnectionTabViewModel` has its own `TreeFilter`, `FilterTables`, `FilterColumns`.
- **Search syntax**: spaces between tokens = AND within an alternative; `|` separates alternatives = OR. Example: `customer order|client name` → `(customer AND order) OR (client AND name)`.
- **Debounce**: 250ms `DispatcherTimer` in `ConnectionPanel.axaml.cs` resets on every TextChanged before pushing the filter into the VM.
- **Filtered tree**: `ConnectionTabViewModel` keeps two collections — `_unfilteredTree` (unbound source) and `ConnectionTree` (bound to TreeView). `ApplyTreeFilter()` rebuilds `ConnectionTree` from `_unfilteredTree` via `FilterNode` recursion. Containers (Database/Folder/Schema) always expand; tables/views expand only when a child column matched (i.e., column scope is on).
- **Esc** in the filter box clears the filter and returns focus to the tree.

#### ViewModels
- **MainWindowViewModel** — Holds `ConnectionTabs` collection, `SavedConnections`, state persistence. Auto-selects first query tab when switching connection tabs.
- **ConnectionTabViewModel** — Per-connection: IDbConnectionProvider, IntelliSenseProvider, QueryTabs, schema tree, database list, color. `ConnectAsync` uses `force: true` on `SwitchDatabaseAsync` to ensure schema loads even when ActiveDatabase matches saved state. Sets `HasConnectionError` + `ConnectionError` in the catch block; the panel watches these to show the retry overlay. Holds `_unfilteredTree` plus `TreeFilter` / `FilterTables` / `FilterColumns` for the tree filter.
- **SessionTabViewModel** — Per-query-tab: QueryText, ResultData, cursor. `SetQueryText()` fires `QueryTextSet` event so EditorView syncs the AvaloniaEdit control.

#### State & Services
- **AppState.cs** — JSON to `~/.dbclient/state.json`. Nested: `AppState → ConnectionTabState[] → TabState[]`. Auto-saves on query execute (in `finally` block) and app shutdown.
- **SchemaService.cs** — Bridges Data→IntelliSense models. Returns **dialect-specific keywords**: SQL Server (`GETDATE()`, `ISNULL`, `TOP`, `@@ROWCOUNT`), MySQL (`NOW()`, `IFNULL`, `GROUP_CONCAT`, `JSON_EXTRACT`), SQLite (`datetime('now')`, `strftime`, `PRAGMA`).
- **ThemeColors.cs** — Resource-lookup helpers (`Get(key, fallback)` returns a brush; `GetColor` returns the hex string). Exposes a static `ThemeChanged` event that `App.SetTheme` fires after swapping styles. Panels that build content programmatically (`ConnectionPanel`, `HistoryPanel`, `ResultsPanel`) subscribe and refresh their dynamic UI on theme swap.

#### Theme Swap
- `App.SetTheme(name)` removes the previous `Styles` entry and adds a new one (`DarkTheme` / `LightTheme` / `DraculaTheme`), then calls `ThemeColors.NotifyThemeChanged()`.
- Anything bound via `{DynamicResource ...}` updates automatically.
- Anything resolved imperatively via `ThemeColors.Get(...)` does **not** auto-refresh — its consumer must subscribe to `ThemeColors.ThemeChanged`. `ConnectionTreeNode.NameBrush` is one such case: the panel walks every connection tab's tree and calls `node.RefreshThemeBrushes()` on theme change so each `Foreground="{Binding NameBrush}"` re-evaluates.
- Tree node colors are intentionally unified per theme: a single `TreeNodeColor` brush for tables/views/columns/schemas/folders/procs (high-contrast normal text), with `DatabaseNodeColor` reserved for distinct accent on database nodes.

### Technology Stack
- **.NET 10.0** cross-platform
- **Avalonia UI 11.3.11** — Cross-platform UI framework
- **AvaloniaEdit 11.4.0** — Text editor with CompletionWindow, syntax highlighting
- **Avalonia.Controls.DataGrid 11.3.11** — Results grid
- **Dapper** — Query execution
- **Microsoft.Data.SqlClient, MySql.Data, System.Data.SQLite.Core** — DB drivers
- **SSH.NET** — SSH tunnel support
- **System.Text.Json** — State serialization

### Icons — Lucide
- All UI icons (except the app icon) use **Lucide** SVG paths from https://github.com/lucide-icons/lucide/tree/main/icons
- Icons are **stroke-based**: use `Stroke`, `StrokeThickness="1.5"`, `StrokeLineCap="Round"`, `StrokeJoin="Round"` — never `Fill` for icons. (Avalonia uses `StrokeJoin`, not `StrokeLineJoin`.)
- Lucide SVGs use a 24×24 viewBox. Multi-element SVGs (multiple `<path>`, `<circle>`, `<rect>`, `<ellipse>`) must be combined into a single `Data` string:
  - Concatenate multiple `<path d="..."/>` with spaces: `"M3 12h18 M12 3v18"`
  - Convert `<circle cx="12" cy="12" r="10"/>` → `M2 12a10 10 0 1 0 20 0a10 10 0 1 0-20 0`
  - Convert `<ellipse cx="12" cy="5" rx="9" ry="3"/>` → `M3 5a9 3 0 1 0 18 0a9 3 0 1 0-18 0`
  - Convert `<rect x="3" y="3" width="18" height="18" rx="2"/>` → `M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z`
- When adding new icons, find the matching Lucide icon at the URL above, fetch its SVG, and convert using the rules above.

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
- TreeView chevron color: the Fluent theme binds the chevron `Path.Fill` to `DynamicResource TreeViewItemForeground` — styling the Path element directly has no effect. Override `TreeViewItemForeground`, `TreeViewItemForegroundPointerOver`, `TreeViewItemForegroundSelected`, etc. in `<Styles.Resources>` instead.
- Menu/ContextMenu colors: the Fluent theme uses `MenuFlyoutPresenterBackground`, `MenuFlyoutPresenterBorderBrush`, `MenuFlyoutItemBackground`, `MenuFlyoutItemBackgroundPointerOver`, `MenuFlyoutItemForeground`, `MenuFlyoutSubItemChevron`, etc. Override these in `<Styles.Resources>` — direct style selectors on `MenuItem` or `ContextMenu` may not take effect.
- TreeView selection: style `Border#PART_LayoutRoot` for selection highlight, set `ContentPresenter#PART_HeaderPresenter` to Transparent to remove default gray box.
- ScrollBar auto-hide: set `AllowAutoHide="False"` both globally and inside DataGrid's own `<DataGrid.Styles>`.
- `AvaloniaResource` in csproj: don't add explicit `<AvaloniaResource Include="Assets\**"/>` — it overrides the SDK default that auto-includes `*.axaml` files, causing "No precompiled XAML found" errors.

#### sql.xshd Syntax Highlighting
- Must be an `<EmbeddedResource>` in csproj, NOT an AvaloniaResource.
- Loaded via `Assembly.GetManifestResourceStream("dbclient.Assets.sql.xshd")`.

#### TreeView IsExpanded
- `TreeViewItem.IsExpanded` is **not** auto-bound to a same-named property on the data context. To drive expansion from the model (e.g., `ConnectionTreeNode.IsExpanded`), declare an in-tree `Style` inside the `TreeView`:
  ```xml
  <TreeView.Styles>
      <Style Selector="TreeViewItem" x:DataType="vm:ConnectionTreeNode">
          <Setter Property="IsExpanded" Value="{Binding IsExpanded, Mode=TwoWay}"/>
      </Style>
  </TreeView.Styles>
  ```

#### Flattening TextBox focus visuals
- Setting `BorderThickness="0"` on a `TextBox` is not enough — the Fluent theme's template still draws focus/hover state on the inner `Border#PART_BorderElement`. To make a chrome-less search-style box, add a class (e.g., `Classes="searchbox"`) and override the template parts:
  ```xml
  <Style Selector="TextBox.searchbox /template/ Border#PART_BorderElement">
      <Setter Property="BorderThickness" Value="0"/>
      <Setter Property="Background" Value="Transparent"/>
  </Style>
  <Style Selector="TextBox.searchbox:focus /template/ Border#PART_BorderElement">
      <Setter Property="BorderBrush" Value="Transparent"/>
      <Setter Property="Background" Value="Transparent"/>
      <Setter Property="BorderThickness" Value="0"/>
  </Style>
  ```
  Repeat for `:pointerover` / `:focus-within`. Done this way for the schema-tree search box so the focus highlight lives on the outer wrapper Border instead.

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
