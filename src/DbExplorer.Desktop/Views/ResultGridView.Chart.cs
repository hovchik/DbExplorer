using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DbExplorer.Application.Query;

namespace DbExplorer.Desktop.Views;

// The chart view: the rows in view (after the search box and column filters) drawn as a chart that takes the result
// grid's place while the Chart toggle is on. Each result set remembers its own chart.
public partial class ResultGridView
{
    private static readonly ChartKind[] ChartKinds = [ChartKind.Bar, ChartKind.Line, ChartKind.Area, ChartKind.Pie, ChartKind.Scatter];

    private readonly Flyout _chartValuesFlyout = new() { Placement = PlacementMode.BottomEdgeAlignedLeft };
    private ChartData? _chart;
    private int _chartVersion;

    /// <summary>The chart now drawn, for tests and the UI renders.</summary>
    public ChartData? CurrentChart => _chart;

    private void InitChart()
    {
        ChartValuesButton.Flyout = _chartValuesFlyout;
        _chartValuesFlyout.Opening += (_, _) => _chartValuesFlyout.Content = BuildChartValuesPanel();
        ChartKindBox.ItemsSource = ChartKinds.Select(ChartSpec.Describe).ToList();
        ChartAggregateBox.ItemsSource = PivotAggregates.Select(PivotSpec.Describe).ToList();

        ChartToggle.IsCheckedChanged += (_, _) =>
        {
            if (_rebuilding) return;
            ShowChart(ChartToggle.IsChecked == true);
        };
        ChartKindBox.SelectionChanged += (_, _) =>
        {
            if (_rebuilding || _state.Chart is not { } spec || ChartKindBox.SelectedIndex < 0) return;
            SetChart(spec with { Kind = ChartKinds[ChartKindBox.SelectedIndex] });
        };
        ChartXBox.SelectionChanged += (_, _) =>
        {
            if (_rebuilding || _state.Chart is not { } spec || ChartXBox.SelectedIndex < 0) return;
            SetChart(spec with { XField = ChartXBox.SelectedIndex == 0 ? null : ChartXBox.SelectedIndex - 1 });
        };
        ChartAggregateBox.SelectionChanged += (_, _) =>
        {
            if (_rebuilding || _state.Chart is not { } spec || ChartAggregateBox.SelectedIndex < 0) return;
            SetChart(spec with { Aggregate = PivotAggregates[ChartAggregateBox.SelectedIndex] });
        };
        ChartSplitBox.SelectionChanged += (_, _) =>
        {
            if (_rebuilding || _state.Chart is not { } spec || ChartSplitBox.SelectedIndex < 0) return;
            SetChart(spec with { SeriesField = ChartSplitBox.SelectedIndex == 0 ? null : ChartSplitBox.SelectedIndex - 1 });
        };
    }

    /// <summary>Turns the chart on or off for the current result set (on turns the pivot off).</summary>
    public void ShowChart(bool on)
    {
        _state.ChartOn = on && ResultSet is not null;
        if (_state.ChartOn)
        {
            _state.Chart ??= DefaultChart();
            if (_state.PivotOn) { _state.PivotOn = false; SyncPivotControls(); UpdatePivot(); }
        }
        SyncChartControls();
        UpdateChart();
    }

    /// <summary>Changes the current result set's chart (used by the toolbar and the UI renders).</summary>
    public void SetChart(ChartSpec spec)
    {
        // Pick a sensible summary when the values change between "none" (count rows) and some columns.
        if (_state.Chart is { } old && old.YFields.Count == 0 && spec.YFields.Count > 0 && spec.Aggregate == PivotAggregate.Count)
            spec = spec with { Aggregate = PivotAggregate.Sum };
        else if (spec.YFields.Count == 0) spec = spec with { Aggregate = PivotAggregate.Count };
        _state.Chart = spec;
        SyncChartControls();
        UpdateChart();
    }

    private ChartSpec DefaultChart()
    {
        var rs = ResultSet!;
        var names = _columns.Select(c => c.Name).ToList();
        var numeric = _columns.Select(c => c.IsNumeric).ToList();
        var dates = _columns.Select(c => ResultChart.IsDateColumn(rs.Rows.Take(NumericSampleRows)
            .Select(r => c.Index < r.Values.Count ? r.Values[c.Index] : null))).ToList();
        return ResultChart.DefaultSpec(names, numeric, dates);
    }

    /// <summary>Puts the toggle, the pickers and which view is showing in line with the current result set's chart.</summary>
    private void SyncChartControls()
    {
        var wasRebuilding = _rebuilding;
        _rebuilding = true;
        try
        {
            var on = ResultSet is not null && _state.ChartOn;
            ChartToggle.IsChecked = on;
            ChartToggle.IsEnabled = ResultSet is not null && _columns.Count > 0;
            ChartBar.IsVisible = on;
            ChartPanel.IsVisible = on;
            BodyGrid.IsVisible = !on && !(ResultSet is not null && _state.PivotOn);
            if (!on) return;

            var spec = _state.Chart ??= DefaultChart();
            var names = _columns.Select(c => c.Name).ToList();
            ChartKindBox.SelectedIndex = Array.IndexOf(ChartKinds, spec.Kind);
            ChartXBox.ItemsSource = new[] { spec.Kind == ChartKind.Scatter ? "(row number)" : "(none)" }.Concat(names).ToList();
            ChartXBox.SelectedIndex = spec.XField is { } xf && xf < names.Count ? xf + 1 : 0;

            var scatter = spec.Kind == ChartKind.Scatter;
            ChartAggregateBox.IsVisible = !scatter;
            ChartAggregateBox.SelectedIndex = Array.IndexOf(PivotAggregates, spec.YFields.Count == 0 ? PivotAggregate.Count : spec.Aggregate);
            ChartAggregateBox.IsEnabled = spec.YFields.Count > 0;

            var values = spec.YFields.Where(f => f < names.Count).Select(f => names[f]).ToList();
            var pie = spec.Kind == ChartKind.Pie;
            if (pie && values.Count > 1) values = [values[0]];
            ChartValuesButton.Content = (values.Count == 0 ? "(row count)" : string.Join(", ", values)) + " ▾";

            var canSplit = !pie && !scatter;
            ChartSplitLabel.IsVisible = canSplit;
            ChartSplitBox.IsVisible = canSplit;
            ChartSplitBox.ItemsSource = new[] { "(none)" }.Concat(names).ToList();
            ChartSplitBox.SelectedIndex = spec.SeriesField is { } sf && sf < names.Count ? sf + 1 : 0;
            ChartSplitBox.IsEnabled = spec.YFields.Count <= 1;
            ToolTip.SetTip(ChartSplitBox, spec.YFields.Count > 1
                ? "Split by works with one value column; untick the others to use it"
                : "Draw one series per value of this column (up to 8)");
        }
        finally
        {
            _rebuilding = wasRebuilding;
        }
    }

    private Control BuildChartValuesPanel()
    {
        var list = new StackPanel { Spacing = 2, MinWidth = 200 };
        var spec = _state.Chart;
        var selected = spec?.YFields ?? [];
        var single = spec?.Kind == ChartKind.Pie;
        var numbers = _columns.Where(c => c.IsNumeric).ToList();
        foreach (var c in numbers)
        {
            Control item;
            if (single)
            {
                var radio = new RadioButton { Content = c.Name, GroupName = "chartValue", IsChecked = selected.FirstOrDefault(-1) == c.Index };
                radio.IsCheckedChanged += (_, _) =>
                {
                    if (radio.IsChecked == true && _state.Chart is { } s) SetChart(s with { YFields = [c.Index] });
                };
                item = radio;
            }
            else
            {
                var box = new CheckBox { Content = c.Name, IsChecked = selected.Contains(c.Index) };
                box.IsCheckedChanged += (_, _) =>
                {
                    if (_state.Chart is not { } s) return;
                    var fields = s.YFields.Where(f => f != c.Index).ToList();
                    if (box.IsChecked == true) fields.Add(c.Index);
                    fields.Sort();
                    SetChart(s with { YFields = fields, SeriesField = fields.Count > 1 ? null : s.SeriesField });
                };
                item = box;
            }
            list.Children.Add(item);
        }
        var countRows = new Button { Content = "Count rows instead", Classes = { "compact" }, Margin = new Avalonia.Thickness(0, 4, 0, 0) };
        countRows.Click += (_, _) =>
        {
            if (_state.Chart is { } s) SetChart(s with { YFields = [] });
            _chartValuesFlyout.Hide();
        };
        var hint = numbers.Count == 0
            ? "No column holds numbers, so the chart counts rows."
            : single ? "The value each slice sizes by" : $"Columns drawn as series (up to {ResultChart.MaxSeries})";
        return new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = hint, Classes = { "hint" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 260 },
                new ScrollViewer { MaxHeight = 380, Content = list },
                countRows
            }
        };
    }

    /// <summary>Re-charts the rows in view. Large views are summarised off the UI thread; only the latest request is shown.</summary>
    private async void UpdateChart()
    {
        if (ResultSet is not { } rs || !_state.ChartOn || _state.Chart is not { } spec)
        {
            _chart = null;
            ChartView.Data = null;
            ChartView.Caption = null;
            return;
        }
        var version = ++_chartVersion;
        var rows = _viewRows.Where(r => !r.IsDeleted).ToList();
        var names = _columns.Select(c => c.Name).ToList();
        ChartData chart;
        try
        {
            if (rows.Count < BackgroundViewRows)
                chart = ResultChart.Build(rows, r => r.Values, names, spec);
            else
            {
                ChartInfoText.Text = $"Charting {rows.Count:N0} row(s)…";
                chart = await Task.Run(() => ResultChart.Build(rows, r => r.Values, names, spec));
                if (version != _chartVersion || !ReferenceEquals(rs, ResultSet)) return;
            }
        }
        catch (Exception ex)
        {
            if (version == _chartVersion) ChartInfoText.Text = "Could not chart the rows: " + ex.Message;
            return;
        }
        _chart = chart;
        ChartView.Data = chart;

        var info = chart.Kind == ChartKind.Pie
            ? $"{chart.PointCount:N0} slice(s) from {chart.SourceRows:N0} row(s) in view"
            : $"{chart.PointCount:N0} point(s) from {chart.SourceRows:N0} row(s) in view";
        if (chart.SkippedRows > 0) info += $" · {chart.SkippedRows:N0} row(s) without a {(chart.XAxis == ChartAxisKind.Time ? "date" : "number")} skipped";
        if (chart.Note is { } note) info += " · " + note;
        ChartInfoText.Text = "";
        ChartView.Caption = info;
    }

    private void OnCloseChart(object? sender, RoutedEventArgs e) => ChartToggle.IsChecked = false;

    /// <summary>The charted values as tab-separated text: the X labels down the side, one column per series.</summary>
    public static string ChartDataText(ChartData chart)
    {
        var text = new StringBuilder();
        text.AppendLine(string.Join('\t', new[] { chart.XTitle }.Concat(chart.Series.Select(s => s.Name)).Select(Clean)));
        for (var i = 0; i < chart.PointCount; i++)
            text.AppendLine(string.Join('\t', new[] { Clean(chart.Labels[i]) }
                .Concat(chart.Series.Select(s => s.Values[i] is { } v ? v.ToString("R", CultureInfo.InvariantCulture) : ""))));
        return text.ToString();

        static string Clean(string value) => value.Replace('\t', ' ').ReplaceLineEndings(" ");
    }

    private void OnCopyChartData(object? sender, RoutedEventArgs e)
    {
        if (_chart is { } chart) _ = SetClipboardAsync(ChartDataText(chart));
    }

    private async void OnSaveChartImage(object? sender, RoutedEventArgs e)
    {
        if (_chart is null || ResultSet is not { } rs || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save chart",
            SuggestedFileName = SuggestedName(rs) + "-chart.png",
            DefaultExtension = "png",
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType("PNG image") { Patterns = ["*.png"] }]
        });
        if (file is null) return;
        try
        {
            await using var stream = await file.OpenWriteAsync();
            ChartView.SavePng(stream);
            ChartInfoText.Text = "Saved " + file.Name;
        }
        catch (Exception ex)
        {
            ChartInfoText.Text = "Could not save the chart: " + ex.Message;
        }
    }
}
