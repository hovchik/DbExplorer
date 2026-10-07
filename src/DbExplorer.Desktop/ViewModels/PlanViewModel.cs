using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Query.Plans;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>One line of the details panel.</summary>
public sealed record PlanFact(string Name, string Value);

/// <summary>The Plan tab of a query: the drawn operator tree of one statement, its warnings and the selected operator's details.</summary>
public sealed partial class PlanViewModel : ObservableObject
{
    public PlanViewModel(ExecutionPlan plan)
    {
        Plan = plan;
        Layout = PlanLayout.Arrange(plan);
        // Start on the operator doing the most work, so the details panel has something worth reading.
        _selectedNode = plan.Nodes.Where(n => n.IsHotspot).MaxBy(n => n.Share) ?? plan.Root;
    }

    public ExecutionPlan Plan { get; }

    public PlanLayout Layout { get; }

    public string Summary => Plan.Summary;

    public string? Statement => Plan.Statement is { } s ? string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) : null;

    public IReadOnlyList<PlanWarning> Warnings => Plan.Warnings;

    public bool HasWarnings => Warnings.Count > 0;

    public string WarningsHeader => $"{Warnings.Count} thing{(Warnings.Count == 1 ? "" : "s")} worth a look";

    /// <summary>".sqlplan" opens in SQL Server Management Studio and Azure Data Studio; PostgreSQL and MariaDB plans save as JSON, MySQL's tree as text.</summary>
    public string RawExtension => Plan.Provider switch
    {
        "SqlServer" => "sqlplan",
        "MySql" when !Plan.Raw.TrimStart().StartsWith('{') => "txt",
        _ => "json"
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedTitle), nameof(SelectedObject), nameof(SelectedFacts), nameof(SelectedWarnings),
        nameof(SelectedProperties), nameof(HasSelection), nameof(SelectedHasWarnings))]
    private PlanNode? _selectedNode;

    [ObservableProperty] private double _zoom = 1.0;

    public bool HasSelection => SelectedNode is not null;

    public string SelectedTitle => SelectedNode?.Operator ?? "Click an operator for its details";

    public string? SelectedObject => SelectedNode?.Object;

    public IReadOnlyList<PlanWarning> SelectedWarnings => SelectedNode?.Warnings ?? [];

    public bool SelectedHasWarnings => SelectedWarnings.Count > 0;

    /// <summary>The numbers that matter, worded for people: rows, executions, cost share, time.</summary>
    public IReadOnlyList<PlanFact> SelectedFacts
    {
        get
        {
            if (SelectedNode is not { } n) return [];
            var facts = new List<PlanFact>();
            facts.Add(new("Estimated rows", PlanFormat.Count(n.EstimatedRows) +
                                            (n.EstimatedExecutions > 1 ? $" per execution × {PlanFormat.Count(n.EstimatedExecutions)}" : "")));
            if (n.ActualRows is { } actual)
                facts.Add(new("Actual rows", PlanFormat.Count(actual) + (n.ActualExecutions is { } e && e > 1 ? $" in {PlanFormat.Count(e)} executions" : "")));
            facts.Add(new(Plan.IsActual && Plan.Provider != "SqlServer" ? "Share of the time" : "Share of the cost", PlanFormat.Percent(n.Share)));
            if (n.SubtreeCost is { } cost) facts.Add(new("Cost with its inputs", PlanFormat.Number(cost)));
            if (n.ActualTimeMs is { } ms) facts.Add(new("Time with its inputs", PlanFormat.Ms(ms)));
            if (n.Detail is { } detail) facts.Add(new("Condition", detail));
            return facts;
        }
    }

    /// <summary>Everything the engine reported about the operator.</summary>
    public IReadOnlyList<PlanFact> SelectedProperties =>
        SelectedNode?.Properties.Select(p => new PlanFact(p.Key, p.Value)).ToList() ?? [];

    [RelayCommand] private void ZoomIn() => Zoom = Math.Min(2.5, Math.Round(Zoom + 0.1, 1));
    [RelayCommand] private void ZoomOut() => Zoom = Math.Max(0.3, Math.Round(Zoom - 0.1, 1));
    [RelayCommand] private void ResetZoom() => Zoom = 1.0;

    /// <summary>Selects the operator a warning is about.</summary>
    [RelayCommand]
    private void ShowWarning(PlanWarning? warning)
    {
        if (warning?.Node is { } node) SelectedNode = node;
    }

    /// <summary>The zoom at which the whole plan fits <paramref name="width"/> × <paramref name="height"/>.</summary>
    public void Fit(double width, double height)
    {
        if (width <= 0 || height <= 0) return;
        var zoom = Math.Min(width / Layout.Width, height / Layout.Height);
        Zoom = Math.Clamp(Math.Floor(zoom * 10) / 10, 0.3, 1.0);
    }

    public string ZoomText => Zoom.ToString("P0", CultureInfo.InvariantCulture);

    partial void OnZoomChanged(double value) => OnPropertyChanged(nameof(ZoomText));
}
