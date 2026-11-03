using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Connections;
using DbExplorer.Application.Export;
using DbExplorer.Application.Sessions;
using DbExplorer.Application.TestData;
using DbExplorer.Core.Models;
using DbExplorer.Desktop.Services;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>A generator choice in a column's drop-down.</summary>
public sealed record TestKindOption(TestValueKind Kind, string Label)
{
    public override string ToString() => Label;

    public static string LabelOf(TestValueKind kind) => kind switch
    {
        TestValueKind.ServerDefault => "Server default",
        TestValueKind.Null => "NULL",
        TestValueKind.Fixed => "Fixed value",
        TestValueKind.Sequence => "Sequence",
        TestValueKind.Reference => "Parent row (FK)",
        TestValueKind.OneOf => "One of a list",
        TestValueKind.Integer => "Whole number",
        TestValueKind.Decimal => "Decimal number",
        TestValueKind.Price => "Amount / price",
        TestValueKind.Quantity => "Quantity",
        TestValueKind.Age => "Age",
        TestValueKind.Year => "Year",
        TestValueKind.Boolean => "True / false",
        TestValueKind.Date => "Recent date",
        TestValueKind.BirthDate => "Birth date",
        TestValueKind.DateTime => "Recent date/time",
        TestValueKind.Time => "Time of day",
        TestValueKind.Uuid => "UUID",
        TestValueKind.Word => "Word",
        TestValueKind.ProductName => "Product / title",
        TestValueKind.Sentence => "Sentence",
        TestValueKind.Paragraph => "Paragraph",
        TestValueKind.Code => "Code",
        TestValueKind.FirstName => "First name",
        TestValueKind.LastName => "Last name",
        TestValueKind.FullName => "Full name",
        TestValueKind.Username => "User name",
        TestValueKind.Email => "Email",
        TestValueKind.Phone => "Phone",
        TestValueKind.Company => "Company",
        TestValueKind.Street => "Street address",
        TestValueKind.City => "City",
        TestValueKind.Region => "State / region",
        TestValueKind.Country => "Country",
        TestValueKind.PostalCode => "Postal code",
        TestValueKind.Url => "URL",
        TestValueKind.Color => "Colour",
        TestValueKind.Json => "JSON object",
        TestValueKind.Binary => "Random bytes",
        _ => kind.ToString()
    };
}

/// <summary>One column of the generator window: what it is, how it's filled, and a few sample values.</summary>
public sealed partial class TestColumnRow : ObservableObject
{
    private readonly Action<TestColumnRow> _changed;

    public TestColumnRow(TestColumnPlan plan, Action<TestColumnRow> changed)
    {
        _changed = changed;
        Plan = plan;
        Options = plan.Options.Select(k => new TestKindOption(k, TestKindOption.LabelOf(k))).ToList();
        _selectedOption = Options.FirstOrDefault(o => o.Kind == plan.Kind) ?? Options[0];
        _fixedValue = plan.FixedValue;
    }

    public TestColumnPlan Plan { get; private set; }
    public string Name => Plan.Column.Name;
    public string Type => Plan.Column.DataType.Length > 0 ? Plan.Column.DataType : Plan.Column.BaseType;
    public string Flags => string.Join(" ", new[]
    {
        Plan.Column.IsPrimaryKey ? "PK" : null,
        Plan.ReferenceIndex is not null ? "FK" : null,
        Plan.Unique && !Plan.Column.IsPrimaryKey ? "unique" : null,
        Plan.Column.IsNullable ? null : "not null"
    }.Where(f => f is not null));

    public IReadOnlyList<TestKindOption> Options { get; }
    public string Note => Plan.Note;

    [ObservableProperty] private TestKindOption _selectedOption;
    [ObservableProperty] private string _fixedValue;
    [ObservableProperty] private string _sample = "";

    public bool IsFixed => SelectedOption.Kind == TestValueKind.Fixed;

    /// <summary>The plan with the user's choice applied.</summary>
    public TestColumnPlan Current => Plan with { Kind = SelectedOption.Kind, FixedValue = FixedValue };

    /// <summary>Takes a re-read plan (after an insert) while keeping the user's choice.</summary>
    public void Refresh(TestColumnPlan plan) => Plan = plan;

    partial void OnSelectedOptionChanged(TestKindOption value)
    {
        OnPropertyChanged(nameof(IsFixed));
        _changed(this);
    }

    partial void OnFixedValueChanged(string value) => _changed(this);
}

/// <summary>
/// The Generate test data window: plans the table's columns from the catalog, previews generated rows as the INSERT
/// script, and inserts them in one transaction after a confirmation (typing PRODUCTION on a production connection;
/// refused on read-only connections). Opening the script in a Query tab runs nothing.
/// </summary>
public sealed partial class TestDataViewModel : ViewModelBase
{
    private const int PreviewRows = 20;
    private const int MaxScriptRows = 5_000;

    private readonly DatabaseSession _session;
    private readonly IDialogService _dialogs;
    private readonly Action<string, string?, string> _openSql;
    private TestDataPlan? _plan;
    private CancellationTokenSource? _insertCts;
    private bool _loading;

    public TestDataViewModel(DatabaseSession session, DbObject table, IDialogService dialogs, Action<string, string?, string> openSql)
    {
        _session = session;
        _dialogs = dialogs;
        _openSql = openSql;
        Table = table;
        _seed = Random.Shared.Next(1, 1_000_000);
        var where = string.IsNullOrEmpty(table.Database) ? "" : $" in {table.Database}";
        Title = $"Generate test data · {table.FullName}";
        Subtitle = $"{table.FullName}{where} on {session.Profile.DisplayName}" +
                   (table.RowCount is { } n ? $" · about {n:N0} row(s) now" : "");
    }

    public DbObject Table { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public bool IsProduction => _session.Profile.IsProduction;
    public bool IsReadOnly => _session.Profile.ReadOnly;
    public string ProductionBanner => $"PRODUCTION · {_session.Profile.DisplayName} · inserting here writes to live data";

    public ObservableCollection<TestColumnRow> Columns { get; } = [];

    [ObservableProperty] private decimal _rowCount = 100;
    [ObservableProperty] private decimal _seed;
    [ObservableProperty] private decimal _nullPercent = 10;
    [ObservableProperty] private string _preview = "";
    [ObservableProperty] private string _warnings = "";
    [ObservableProperty] private string _status = "Reading the table…";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InsertCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenInQueryTabCommand))]
    [NotifyCanExecuteChangedFor(nameof(ShuffleCommand))]
    private bool _isBusy = true;

    [ObservableProperty] private bool _isInserting;

    public decimal MaxRows => TestDataGenerator.MaxRows;

    public string InsertText => $"Insert {Count:N0} row(s)";

    private int Count => (int)Math.Clamp(RowCount, 1, TestDataGenerator.MaxRows);

    partial void OnRowCountChanged(decimal value)
    {
        OnPropertyChanged(nameof(InsertText));
        UpdatePreview();
    }

    partial void OnSeedChanged(decimal value) => UpdatePreview();

    partial void OnNullPercentChanged(decimal value) => UpdatePreview();

    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var plan = await Task.Run(() => TestDataService.LoadAsync(_session.Provider, _session.Snapshot, Table, _session.Lifetime));
            Apply(plan);
            Status = IsReadOnly
                ? "Read-only connection: you can preview and open the script, but not insert."
                : "Pick how each column is filled, then insert. The rows go in one transaction: if one fails, none are kept.";
        }
        catch (Exception ex)
        {
            Status = "Could not read the table: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Shows a plan, keeping choices the user already made for columns that are still there.</summary>
    private void Apply(TestDataPlan plan)
    {
        _loading = true;
        try
        {
            var previous = Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
            Columns.Clear();
            foreach (var column in plan.Columns)
            {
                var row = new TestColumnRow(column, _ => UpdatePreview());
                if (previous.TryGetValue(column.Column.Name, out var old))
                {
                    var kept = row.Options.FirstOrDefault(o => o.Kind == old.SelectedOption.Kind);
                    if (kept is not null) row.SelectedOption = kept;
                    row.FixedValue = old.FixedValue;
                }
                Columns.Add(row);
            }
            _plan = plan;
        }
        finally
        {
            _loading = false;
        }
        UpdatePreview();
    }

    private TestDataPlan? CurrentPlan()
    {
        if (_plan is null) return null;
        var plan = _plan;
        foreach (var row in Columns) plan = plan.WithColumn(row.Current);
        return plan;
    }

    private TestDataRows Generate(TestDataPlan plan, int count) =>
        TestDataGenerator.Generate(plan, count, (int)Seed, (int)NullPercent);

    /// <summary>The first rows as the INSERT they become, and three sample values per column. The first rows of a
    /// full run are the same as these, since generation is sequential from the seed.</summary>
    private void UpdatePreview()
    {
        if (_loading || CurrentPlan() is not { } plan) return;
        var rows = Generate(plan, Math.Min(Count, PreviewRows));
        foreach (var row in Columns)
        {
            var index = rows.Columns.ToList().FindIndex(c => c.Name == row.Name);
            row.Sample = index < 0
                ? (row.SelectedOption.Kind == TestValueKind.ServerDefault ? "(left to the server)" : "")
                : string.Join("  ·  ", rows.Rows.Take(3).Select(r => Short(r[index] is null ? "NULL" : ResultExporter.FormatInvariant(r[index]))));
        }

        var statements = TestDataScript.Statements(plan.ProviderKey, Table, rows);
        var more = Count > rows.Rows.Count && rows.Shortfall == 0 ? $"\n-- … and {Count - rows.Rows.Count:N0} more row(s) like these." : "";
        Preview = statements.Count == 0 ? "-- No rows to insert." : string.Join("\n", statements) + more;
        Warnings = string.Join("\n", plan.Warnings.Concat(rows.Notes).Select(w => "• " + w));
    }

    private static string Short(string text) => text.Length > 40 ? text[..39] + "…" : text;

    private bool CanAct => !IsBusy && _plan is not null;

    /// <summary>A new seed: different rows with the same choices.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private void Shuffle() => Seed = Random.Shared.Next(1, 1_000_000);

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task OpenInQueryTabAsync()
    {
        if (CurrentPlan() is not { } plan) return;
        if (Count > MaxScriptRows)
        {
            Status = $"A script of {Count:N0} rows is too big for the editor; open at most {MaxScriptRows:N0}, or insert from here.";
            return;
        }
        IsBusy = true;
        try
        {
            var seed = (int)Seed;
            var count = Count;
            var script = await Task.Run(() => TestDataScript.Script(plan.ProviderKey, Table, Generate(plan, count), seed));
            _openSql(script, string.IsNullOrEmpty(Table.Database) ? null : Table.Database, "Test data " + Table.Name);
            Status = "Opened the script in a Query tab. Nothing has been inserted.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task InsertAsync()
    {
        if (CurrentPlan() is not { } plan) return;
        if (IsReadOnly)
        {
            Status = ReadOnlyGuard.Refusal(_session.Profile, "INSERT");
            return;
        }

        IsBusy = true;
        try
        {
            var count = Count;
            var rows = await Task.Run(() => Generate(plan, count));
            if (rows.Rows.Count == 0)
            {
                Status = rows.Notes.FirstOrDefault() ?? "No rows to insert.";
                return;
            }

            var where = string.IsNullOrEmpty(Table.Database) ? "the connection's database" : Table.Database;
            var shortfall = rows.Shortfall > 0 ? $"\n\nOnly {rows.Rows.Count:N0} of the {count:N0} rows asked for could be made: {rows.Notes.LastOrDefault()}" : "";
            var production = IsProduction;
            var message = $"Insert {rows.Rows.Count:N0} generated row(s) into {Table.FullName} in {where}?" +
                          (production ? " You are on a PRODUCTION connection." : "") +
                          "\n\nThey go in one transaction: if any row is refused, none are kept. Triggers on the table run for every row." +
                          shortfall;
            var first = TestDataScript.Statements(plan.ProviderKey, Table, rows with { Rows = rows.Rows.Take(5).ToList() }).FirstOrDefault();
            if (!await _dialogs.ConfirmAsync(message, "Insert rows", production ? "PRODUCTION" : null,
                    production ? $"PRODUCTION · {_session.Profile.DisplayName}" : null, first)) return;

            IsInserting = true;
            _insertCts = CancellationTokenSource.CreateLinkedTokenSource(_session.Lifetime);
            var progress = new Progress<int>(done => Status = $"Inserting… {done:N0} of {rows.Rows.Count:N0}");
            Status = "Inserting…";
            try
            {
                var inserted = await Task.Run(() => TestDataService.InsertAsync(_session.Provider, Table, rows, progress, _insertCts.Token));
                Status = $"Inserted {inserted:N0} row(s) into {Table.FullName}.";
            }
            catch (OperationCanceledException)
            {
                Status = "Cancelled. The transaction was rolled back, so no rows were kept.";
                return;
            }
            catch (Exception ex)
            {
                Status = $"The server refused the insert, so no rows were kept: {ex.Message}";
                return;
            }
            finally
            {
                IsInserting = false;
                _insertCts.Dispose();
                _insertCts = null;
            }

            // The table changed: the next run continues after the new keys and avoids the values just used.
            try
            {
                var fresh = await Task.Run(() => TestDataService.SampleAsync(_session.Provider, _plan!, _session.Lifetime));
                Apply(fresh);
                Seed = Random.Shared.Next(1, 1_000_000);
            }
            catch (Exception ex)
            {
                ErrorLog.Write("test data resample", ex);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CancelInsert() => _insertCts?.Cancel();

    /// <summary>Called when the window closes: an insert still running is cancelled and rolled back.</summary>
    public void Close() => _insertCts?.Cancel();
}
