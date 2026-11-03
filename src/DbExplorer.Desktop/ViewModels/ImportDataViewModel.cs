using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Connections;
using DbExplorer.Application.Design;
using DbExplorer.Application.Export;
using DbExplorer.Application.Import;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Models;
using DbExplorer.Desktop.Services;
using CopyDialect = DbExplorer.Application.Copy.SqlDialect;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>One file column in the import window: where it goes (an existing table's column, or a new table's column
/// with its type) and a few of its values.</summary>
public partial class ImportMappingRow : ObservableObject
{
    public const string Skip = "(don't import)";

    public required int Source { get; init; }
    public required string SourceName { get; init; }
    public string Sample { get; init; } = "";

    /// <summary>Existing table: the column it goes into, or <see cref="Skip"/>.</summary>
    [ObservableProperty] private string _target = Skip;

    /// <summary>New table: whether the column is created and filled.</summary>
    [ObservableProperty] private bool _include = true;

    /// <summary>New table: the column's name and type (guessed from the values, editable).</summary>
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string _newType = "";

    /// <summary>Values in the checked rows that do not fit, as "12 bad values".</summary>
    [ObservableProperty] private string _problems = "";

    public IReadOnlyList<string> Targets { get; init; } = [Skip];
    public IReadOnlyList<string> Types { get; init; } = [];
}

/// <summary>A table the file can go into, as the table list shows it.</summary>
public sealed record ImportTableChoice(DbObject Table, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The Import data window: pick a CSV, TSV or Excel file, check how it is read, map its columns onto an existing table
/// (or a new table with guessed types), see the converted rows, check every row in the background, and import in one
/// transaction (PostgreSQL's COPY, else INSERT batches) with progress and Cancel. Read-only connections can only hand
/// the script to a Query tab; production connections ask for PRODUCTION to be typed.
/// </summary>
public partial class ImportDataViewModel : ViewModelBase
{
    public const int PreviewRows = 200;
    private const int TimeoutSeconds = 600;

    private readonly DatabaseSession _session;
    private readonly SessionService _sessions;
    private readonly IDialogService _dialogs;
    private ImportFile? _file;
    private ImportPreview? _filePreview;
    private bool _loading;
    private CancellationTokenSource? _checkCts;
    private CancellationTokenSource? _importCts;
    private ImportCheck? _check;

    public ImportDataViewModel(DatabaseSession session, SessionService sessions, IDialogService dialogs, DbObject? table)
    {
        _session = session;
        _sessions = sessions;
        _dialogs = dialogs;
        ProviderKey = session.Provider.ProviderKey;
        var multipleDatabases = session.Snapshot.Databases.Count > 1;
        Tables = session.Snapshot.Objects
            .Where(o => o.Type == DbObjectType.Table)
            .OrderBy(o => o.Database, StringComparer.OrdinalIgnoreCase).ThenBy(o => o.FullName, StringComparer.OrdinalIgnoreCase)
            .Select(o => new ImportTableChoice(o, multipleDatabases && o.Database.Length > 0 ? $"{o.Database} · {o.FullName}" : o.FullName))
            .ToList();
        Databases = session.Snapshot.Databases.Count > 0 ? session.Snapshot.Databases : [session.Profile.Database];
        NewDatabase = table?.Database is { Length: > 0 } db ? db
            : Databases.FirstOrDefault(d => Same(d, session.Profile.Database)) ?? Databases.FirstOrDefault() ?? "";
        NewSchema = ProviderKey == CopyDialect.MySqlKey ? "" : TableScriptBuilder.DefaultSchema(ProviderKey, NewDatabase);
        _selectedTable = table is null ? null : Tables.FirstOrDefault(t => SameTable(t.Table, table));
        _isNewTable = _selectedTable is null;
        Types = ColumnTypes.For(ProviderKey);
    }

    public string ProviderKey { get; }
    public bool IsMySql => ProviderKey == CopyDialect.MySqlKey;
    public string Title => SelectedTable is { } t && !IsNewTable ? $"Import data into {t.Table.FullName}" : "Import data";

    /// <summary>Opens the window's Query tab for the import script: (sql, database, title).</summary>
    public Action<string, string?, string>? OpenSql { get; init; }

    // ----- File -----

    [ObservableProperty] private string _fileName = "";
    [ObservableProperty] private string _fileInfo = "Choose a CSV, TSV or Excel (.xlsx) file.";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsText))] private bool _isExcel;
    [ObservableProperty] private bool _hasFile;
    public bool IsText => !IsExcel;

    public IReadOnlyList<string> Delimiters { get; } = ["Comma ( , )", "Semicolon ( ; )", "Tab", "Pipe ( | )"];
    [ObservableProperty] private string _delimiter = "Comma ( , )";
    public IReadOnlyList<string> Encodings { get; } = ["UTF-8", "UTF-16", "Latin-1 (Western European)"];
    [ObservableProperty] private string _encoding = "UTF-8";
    [ObservableProperty] private IReadOnlyList<string> _sheets = [];
    [ObservableProperty] private string? _sheet;
    [ObservableProperty] private bool _hasHeader = true;
    [ObservableProperty] private decimal _skipRows;

    // ----- Values -----

    [ObservableProperty] private bool _emptyIsNull = true;
    [ObservableProperty] private string _nullText = "";
    public IReadOnlyList<string> DecimalSeparators { get; } = ["Point (1.5)", "Comma (1,5)"];
    [ObservableProperty] private string _decimalSeparator = "Point (1.5)";
    [ObservableProperty] private string _dateFormat = "";
    [ObservableProperty] private bool _trimText;

    // ----- Target -----

    public IReadOnlyList<ImportTableChoice> Tables { get; }
    public IReadOnlyList<string> Databases { get; }
    public IReadOnlyList<string> Types { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExistingTable), nameof(Title))]
    private bool _isNewTable;

    public bool IsExistingTable
    {
        get => !IsNewTable;
        set => IsNewTable = !value;
    }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Title))] private ImportTableChoice? _selectedTable;
    [ObservableProperty] private string _newDatabase = "";
    [ObservableProperty] private string _newSchema = "";
    [ObservableProperty] private string _newTableName = "";

    [ObservableProperty] private bool _deleteExisting;
    [ObservableProperty] private bool _skipInvalidRows;

    public ObservableCollection<ImportMappingRow> Mappings { get; } = [];

    // ----- Preview, check, import -----

    [ObservableProperty] private ResultSetView? _preview;
    [ObservableProperty] private string _checkSummary = "";
    [ObservableProperty] private IReadOnlyList<string> _problems = [];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasProblems))] private int _problemCount;
    public bool HasProblems => ProblemCount > 0;
    [ObservableProperty] private string _status = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand), nameof(OpenScriptCommand), nameof(CancelImportCommand), nameof(BrowseCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isImporting;

    public bool IsIdle => !IsImporting;

    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _progressKnown;

    // ----- Opening a file -----

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task BrowseAsync()
    {
        var path = await _dialogs.PickFilePathAsync("Import data from a file",
            [("CSV, TSV and Excel files", ["*.csv", "*.tsv", "*.txt", "*.xlsx", "*.xlsm"]), ("All files", ["*"])]);
        if (path is not null) await OpenAsync(path);
    }

    public async Task OpenAsync(string path)
    {
        Status = "";
        try
        {
            var (file, format) = await Task.Run(() => ImportFile.Open(path));
            _file = file;
            _loading = true;
            try
            {
                IsExcel = file.Kind == ImportFileKind.Excel;
                Sheets = file.Sheets;
                Sheet = format.Sheet;
                Delimiter = DelimiterName(format.Delimiter);
                Encoding = EncodingName(format.Encoding);
                HasHeader = format.HasHeader;
                SkipRows = 0;
                FileName = file.Name;
                HasFile = true;
                if (IsNewTable && NewTableName.Length == 0) NewTableName = TableNameFrom(file.Name);
            }
            finally
            {
                _loading = false;
            }
            await ReloadAsync(guessSeparator: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or InvalidDataException)
        {
            Status = $"{Path.GetFileName(path)} cannot be read: {ex.Message}";
        }
    }

    private ImportFileFormat Format => new()
    {
        Kind = _file?.Kind ?? ImportFileKind.Delimited,
        Delimiter = Delimiter switch { "Semicolon ( ; )" => ';', "Tab" => '\t', "Pipe ( | )" => '|', _ => ',' },
        Encoding = Encoding switch { "UTF-16" => "utf-16", "Latin-1 (Western European)" => "iso-8859-1", _ => "utf-8" },
        HasHeader = HasHeader,
        Sheet = Sheet,
        SkipRows = (int)Math.Max(0, SkipRows)
    };

    private ImportValueOptions ValueOptions => new()
    {
        EmptyIsNull = EmptyIsNull,
        NullText = NullText.Trim(),
        DecimalSeparator = DecimalSeparator.StartsWith("Comma", StringComparison.Ordinal) ? ',' : '.',
        DateFormat = DateFormat.Trim(),
        TrimText = TrimText
    };

    private static string DelimiterName(char c) => c switch { ';' => "Semicolon ( ; )", '\t' => "Tab", '|' => "Pipe ( | )", _ => "Comma ( , )" };

    private static string EncodingName(string? webName) => webName switch
    {
        "utf-16" or "utf-16BE" or "unicodeFFFE" => "UTF-16",
        "iso-8859-1" => "Latin-1 (Western European)",
        _ => "UTF-8"
    };

    /// <summary>"Sales 2024 (final).csv" → sales_2024_final, or Sales2024Final where the database names tables that way.</summary>
    internal static string TableNameFrom(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var chars = stem.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_').ToArray();
        var name = string.Join('_', new string(chars).Split('_', StringSplitOptions.RemoveEmptyEntries));
        if (name.Length == 0) name = "imported";
        if (char.IsDigit(name[0])) name = "t_" + name;
        return name.Length > 60 ? name[..60] : name;
    }

    partial void OnDelimiterChanged(string value) => _ = ReloadIfReadyAsync();
    partial void OnEncodingChanged(string value) => _ = ReloadIfReadyAsync();
    partial void OnSheetChanged(string? value) => _ = ReloadIfReadyAsync();
    partial void OnHasHeaderChanged(bool value) => _ = ReloadIfReadyAsync();
    partial void OnSkipRowsChanged(decimal value) => _ = ReloadIfReadyAsync();
    partial void OnEmptyIsNullChanged(bool value) => OptionsChanged();
    partial void OnNullTextChanged(string value) => OptionsChanged();
    partial void OnDecimalSeparatorChanged(string value) => OptionsChanged(retype: true);
    partial void OnDateFormatChanged(string value) => OptionsChanged(retype: true);
    partial void OnTrimTextChanged(bool value) => OptionsChanged();
    partial void OnSkipInvalidRowsChanged(bool value) => UpdateCheckSummary();

    partial void OnIsNewTableChanged(bool value)
    {
        if (value && NewTableName.Length == 0 && _file is not null) NewTableName = TableNameFrom(_file.Name);
        RebuildMappings();
    }

    partial void OnSelectedTableChanged(ImportTableChoice? value)
    {
        if (value is not null && IsNewTable) IsNewTable = false;
        else RebuildMappings();
    }

    private Task ReloadIfReadyAsync() => _loading || _file is null ? Task.CompletedTask : ReloadAsync(guessSeparator: false);

    private async Task ReloadAsync(bool guessSeparator)
    {
        if (_file is not { } file) return;
        var format = Format;
        try
        {
            _filePreview = await Task.Run(() => file.Preview(format, PreviewRows));
        }
        catch (Exception ex) when (ex is IOException or FormatException or ArgumentException or InvalidDataException)
        {
            Status = "The file cannot be read this way: " + ex.Message;
            return;
        }
        if (guessSeparator)
        {
            _loading = true;
            DecimalSeparator = ImportValues.GuessDecimalSeparator(_filePreview.Rows.SelectMany(r => r)) == ',' ? DecimalSeparators[1] : DecimalSeparators[0];
            _loading = false;
        }
        FileInfo = $"{(file.Kind == ImportFileKind.Excel ? "Excel workbook" : "Text file")} · {_filePreview.ColumnCount} column(s)" +
                   (_filePreview.ReachedEnd ? $" · {_filePreview.Rows.Count:N0} row(s)" : $" · the preview shows the first {PreviewRows} rows");
        RebuildMappings();
    }

    private void OptionsChanged(bool retype = false)
    {
        if (_loading || _filePreview is null) return;
        if (retype && IsNewTable) RebuildMappings();
        else MappingChanged();
    }

    // ----- Mapping -----

    private IReadOnlyList<DbColumn> TargetColumns =>
        SelectedTable is { } t
            ? _session.Snapshot.ColumnsOf(t.Table.Database, t.Table.Schema, t.Table.Name).OrderBy(c => c.Ordinal).ToList()
            : [];

    private void RebuildMappings()
    {
        foreach (var row in Mappings) row.PropertyChanged -= OnMappingRowChanged;
        Mappings.Clear();
        if (_filePreview is not { } preview) return;

        var options = ValueOptions;
        var columns = TargetColumns;
        var auto = IsNewTable ? [] : TableImport.MapByName(preview.Headers, columns, byPosition: !HasHeader);
        var targets = new[] { ImportMappingRow.Skip }.Concat(columns.Where(c => !c.IsComputed).Select(c => c.Name)).ToList();
        for (var i = 0; i < preview.ColumnCount; i++)
        {
            var values = preview.Rows.Select(r => i < r.Length ? r[i] : null).ToList();
            var guess = IsNewTable ? ImportValues.GuessColumn(preview.Headers[i], values, ProviderKey, options) : null;
            var row = new ImportMappingRow
            {
                Source = i,
                SourceName = preview.Headers[i],
                Sample = string.Join(" · ", values.Where(v => !string.IsNullOrWhiteSpace(v)).Take(3).Select(v => v!.Length > 24 ? v[..24] + "…" : v)),
                Targets = targets,
                Types = Types,
                Target = auto.FirstOrDefault(m => m.Source == i)?.Column ?? ImportMappingRow.Skip,
                NewName = preview.Headers[i],
                NewType = guess?.FullType ?? ""
            };
            row.PropertyChanged += OnMappingRowChanged;
            Mappings.Add(row);
        }
        MappingChanged();
    }

    private void OnMappingRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ImportMappingRow.Problems)) return;
        MappingChanged();
    }

    /// <summary>The mapping as the importer takes it, or why it cannot be used yet.</summary>
    private (IReadOnlyList<ImportColumn> Columns, string? Problem) BuildColumns()
    {
        if (_filePreview is null) return ([], "Choose a file first.");
        if (IsNewTable)
        {
            var included = Mappings.Where(m => m.Include).ToList();
            if (included.Count == 0) return ([], "Tick at least one column to import.");
            if (included.FirstOrDefault(m => m.NewName.Trim().Length == 0) is { } unnamed) return ([], $"Column {unnamed.Source + 1} of the file needs a name.");
            if (included.FirstOrDefault(m => m.NewType.Trim().Length == 0) is { } untyped) return ([], $"{untyped.NewName} needs a type.");
            var duplicate = included.GroupBy(m => m.NewName.Trim(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (duplicate is not null) return ([], $"Two columns are named {duplicate.Key}.");
            return (included.Select(m => new ImportColumn(m.Source, m.NewName.Trim(), m.NewType.Trim())).ToList(), null);
        }

        if (SelectedTable is null) return ([], "Choose the table to import into.");
        var byName = TargetColumns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var mapped = Mappings.Where(m => m.Target != ImportMappingRow.Skip && byName.ContainsKey(m.Target)).ToList();
        if (mapped.Count == 0) return ([], "Pick a table column for at least one file column.");
        var twice = mapped.GroupBy(m => m.Target, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (twice is not null) return ([], $"{twice.Key} is picked for more than one file column.");
        return (mapped.Select(m =>
        {
            var c = byName[m.Target];
            return new ImportColumn(m.Source, c.Name, c.DataType.Length > 0 ? c.DataType : c.BaseType, c.IsIdentity);
        }).ToList(), null);
    }

    private void MappingChanged()
    {
        UpdatePreview();
        StartCheck();
        ImportCommand.NotifyCanExecuteChanged();
        OpenScriptCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The first rows as they will be stored: one column per mapped table column, values that do not fit shown
    /// as "⚠ text".</summary>
    private void UpdatePreview()
    {
        var (columns, _) = BuildColumns();
        if (_filePreview is null || columns.Count == 0)
        {
            Preview = null;
            return;
        }
        var options = ValueOptions;
        var rows = _filePreview.Rows.Select(r => new ResultRow(columns.Select(c =>
        {
            var text = c.Source < r.Length ? r[c.Source] : null;
            var parsed = ImportValues.Parse(text, c.Family, options, c.MaxLength);
            return parsed.IsValid ? parsed.Value : "⚠ " + text;
        }).ToArray())).ToList();
        Preview = new ResultSetView("Preview", columns.Select(c => c.Column).ToList(), rows)
        {
            TotalRowCount = rows.Count,
            Dialect = ResultExporter.DialectFor(ProviderKey),
            Quote = _session.Provider.QuoteIdentifier
        };
    }

    /// <summary>Reads every row of the file against the mapping in the background; a newer change cancels it.</summary>
    private void StartCheck()
    {
        _checkCts?.Cancel();
        _check = null;
        foreach (var row in Mappings) row.Problems = "";
        var (columns, problem) = BuildColumns();
        if (_file is not { } file || columns.Count == 0)
        {
            CheckSummary = problem ?? "";
            Problems = [];
            ProblemCount = 0;
            return;
        }

        var cts = _checkCts = new CancellationTokenSource();
        var format = Format;
        var options = ValueOptions;
        var first = FirstRowNumber(format);
        CheckSummary = "Checking every row…";
        var progress = new Progress<long>(n => { if (!cts.IsCancellationRequested) CheckSummary = $"Checking every row… {n:N0} so far"; });
        _ = Task.Run(() => TableImport.Check(file.ReadDataRows(format), columns, options, first, progress, cts.Token), cts.Token)
            .ContinueWith(t =>
            {
                if (cts.IsCancellationRequested) return;
                if (t.IsFaulted)
                {
                    CheckSummary = "The file could not be read to the end: " + t.Exception!.GetBaseException().Message;
                    return;
                }
                _check = t.Result;
                var perColumn = t.Result.Errors.GroupBy(e => e.Column, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
                foreach (var row in Mappings)
                {
                    var name = IsNewTable ? row.NewName.Trim() : row.Target;
                    row.Problems = perColumn.TryGetValue(name, out var n) && (IsNewTable ? row.Include : row.Target != ImportMappingRow.Skip)
                        ? $"⚠ {n}{(t.Result.Errors.Count >= TableImport.MaxListedErrors ? "+" : "")} bad"
                        : "";
                }
                Problems = t.Result.Errors.Select(e => e.Text).ToList();
                ProblemCount = (int)Math.Min(int.MaxValue, t.Result.InvalidRows);
                UpdateCheckSummary();
                ImportCommand.NotifyCanExecuteChanged();
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void UpdateCheckSummary()
    {
        if (_check is not { } check) return;
        CheckSummary = check.InvalidRows == 0
            ? $"All {check.Rows:N0} row(s) fit their columns."
            : SkipInvalidRows
                ? $"{check.Rows - check.InvalidRows:N0} of {check.Rows:N0} rows will be imported; {check.InvalidRows:N0} with a value that does not fit will be left out."
                : $"{check.InvalidRows:N0} of {check.Rows:N0} rows have a value that does not fit its column. Fix the file or the settings, or tick \"Leave out rows that don't fit\".";
        ImportCommand.NotifyCanExecuteChanged();
    }

    private static long FirstRowNumber(ImportFileFormat format) => 1 + format.SkipRows + (format.HasHeader ? 1 : 0);

    // ----- Import -----

    private bool CanImport => IsIdle && _file is not null && _check is { } c && c.Rows > 0 && (c.InvalidRows == 0 || SkipInvalidRows);

    private (ImportPlan? Plan, string? Problem) BuildPlan()
    {
        var (columns, problem) = BuildColumns();
        if (problem is not null) return (null, problem);
        var values = ValueOptions;
        if (!IsNewTable)
            return (new ImportPlan { Table = SelectedTable!.Table, Columns = columns, DeleteExisting = DeleteExisting, SkipInvalidRows = SkipInvalidRows, Values = values }, null);

        var name = NewTableName.Trim();
        if (name.Length == 0) return (null, "Name the new table.");
        var database = NewDatabase;
        var schema = IsMySql ? database : NewSchema.Trim().Length > 0 ? NewSchema.Trim() : TableScriptBuilder.DefaultSchema(ProviderKey, database);
        if (Tables.Any(t => Same(t.Table.Name, name) && Same(t.Table.Schema, schema) && (t.Table.Database.Length == 0 || Same(t.Table.Database, database))))
            return (null, $"{schema}.{name} already exists. Pick it under \"Existing table\" to add rows to it.");
        var design = new TableDesign
        {
            Database = database,
            Schema = IsMySql ? "" : schema,
            Name = name,
            Columns = columns.Select(c => new ColumnDesign { Name = c.Column, Type = c.DataType }).ToList()
        };
        var context = DesignContext.From(DatabaseSnapshot(database), ProviderKey);
        var script = TableCreator.Script(design, context);
        var table = new DbObject { Database = database, Schema = schema, Name = name, Type = DbObjectType.Table };
        return (new ImportPlan { Table = table, Columns = columns, Create = design, CreateScript = script, SkipInvalidRows = SkipInvalidRows, Values = values }, null);
    }

    private Application.Metadata.MetadataSnapshot DatabaseSnapshot(string database) =>
        database.Length > 0 && _session.Snapshot.ContainsDatabase(database) ? _session.Snapshot.ForDatabase(database) : _session.Snapshot;

    /// <summary>The database the import's session opens: the table's own, or the connection's.</summary>
    private static string? DatabaseOf(DbObject table) => string.IsNullOrEmpty(table.Database) ? null : table.Database;

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportAsync()
    {
        var (plan, problem) = BuildPlan();
        if (plan is null || _file is not { } file || _check is not { } check)
        {
            Status = problem ?? "";
            return;
        }
        if (_session.Profile.ReadOnly)
        {
            Status = ReadOnlyGuard.Refusal(_session.Profile, "INSERT") + " Open as script still hands the statements over.";
            return;
        }

        var format = Format;
        var first = FirstRowNumber(format);
        var dialect = ResultExporter.DialectFor(ProviderKey);
        var rows = check.Rows - (SkipInvalidRows ? check.InvalidRows : 0);
        var where = $"{plan.Table.FullName}{(plan.Table.Database.Length > 0 ? $" in {plan.Table.Database}" : "")}";
        var how = dialect == SqlDialect.Postgres ? "with COPY" : $"as INSERT statements of {plan.BatchSize} rows";
        var steps = new List<string>();
        if (plan.CreateScript is not null) steps.Add("creates the table");
        if (plan.DeleteExisting) steps.Add($"deletes the {(SelectedTable?.Table.RowCount is { } n ? $"{n:N0} (estimated) " : "")}rows it has now");
        steps.Add($"loads {rows:N0} row(s) {how}");
        var transaction = IsMySql && plan.CreateScript is not null
            ? "It runs in one transaction; MySQL commits CREATE TABLE by itself, so if the import fails the new table is dropped again."
            : "It all runs in one transaction: if anything fails, nothing is kept.";
        var message = $"Import {file.Name} into {where}? This {Join(steps)}. {transaction}";
        var preview = await Task.Run(() => TableImport.Script(plan, file.ReadDataRows(format), first, dialect, _session.Provider.QuoteIdentifier, maxRows: 3));
        var production = _session.Profile.IsProduction;
        if (!await _dialogs.ConfirmAsync(message, "Import", production ? "PRODUCTION" : null,
                production ? $"PRODUCTION · {_session.Profile.DisplayName}" : null,
                "-- The first rows, as they will be written:\n" + preview)) return;

        IsImporting = true;
        ProgressKnown = rows > 0;
        Progress = 0;
        Status = $"Importing into {plan.Table.FullName}…";
        var cts = _importCts = new CancellationTokenSource();
        var progress = new Progress<long>(n =>
        {
            Progress = rows > 0 ? Math.Min(100, n * 100.0 / rows) : 0;
            Status = $"Importing into {plan.Table.FullName}… {n:N0} of {rows:N0} rows";
        });
        try
        {
            var result = await Task.Run(() => TableImport.RunAsync(_session.Provider, plan, file.ReadDataRows(format), first, TimeoutSeconds, progress, cts.Token));
            Progress = 100;
            var skipped = result.Skipped > 0 ? $"; {result.Skipped:N0} row(s) that did not fit were left out" : "";
            Status = $"Imported {result.Imported:N0} row(s) into {where} in {result.Elapsed.TotalSeconds:N1} s{skipped}.";
            if (plan.CreateScript is not null || plan.DeleteExisting || result.Imported > 0) await RefreshCatalogAsync(plan);
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled. Nothing was imported.";
        }
        catch (ImportFailedException ex)
        {
            Status = ex.Message;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("import data", ex);
            Status = "Nothing was imported: " + ex.Message;
        }
        finally
        {
            IsImporting = false;
            _importCts = null;
        }
    }

    /// <summary>Row counts and a new table show up everywhere once the catalog is read again.</summary>
    private async Task RefreshCatalogAsync(ImportPlan plan)
    {
        try
        {
            await _sessions.RefreshDatabaseSnapshotAsync(_session, DatabaseOf(plan.Table));
        }
        catch (Exception ex)
        {
            ErrorLog.Write("import refresh", ex);
        }
    }

    private bool CanCancelImport => IsImporting;

    [RelayCommand(CanExecute = nameof(CanCancelImport))]
    private void CancelImport() => _importCts?.Cancel();

    private bool CanOpenScript => IsIdle && _file is not null;

    /// <summary>The import as statements in a Query tab (the first 5,000 rows), to read, change or run by hand.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenScript))]
    private async Task OpenScriptAsync()
    {
        var (plan, problem) = BuildPlan();
        if (plan is null || _file is not { } file)
        {
            Status = problem ?? "";
            return;
        }
        var format = Format;
        var dialect = ResultExporter.DialectFor(ProviderKey);
        var script = await Task.Run(() => TableImport.Script(plan, file.ReadDataRows(format), FirstRowNumber(format), dialect, _session.Provider.QuoteIdentifier));
        OpenSql?.Invoke(script, DatabaseOf(plan.Table), "Import " + plan.Table.Name);
        Status = "The script is open in a Query tab. Nothing runs until you run it there.";
    }

    /// <summary>Stops the background check and any import (the window is closing).</summary>
    public void Cancel()
    {
        _checkCts?.Cancel();
        _importCts?.Cancel();
    }

    private static string Join(IReadOnlyList<string> parts) =>
        parts.Count <= 1 ? string.Concat(parts) : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool SameTable(DbObject a, DbObject b) =>
        Same(a.Schema, b.Schema) && Same(a.Name, b.Name) && (a.Database.Length == 0 || b.Database.Length == 0 || Same(a.Database, b.Database));
}
