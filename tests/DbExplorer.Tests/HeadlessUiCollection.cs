namespace DbExplorer.Tests;

/// <summary>
/// Every test class with [AvaloniaFact] / [AvaloniaTheory] belongs here. The headless session resets the global
/// Dispatcher.UIThread around each test, so a plain test running in parallel that touches Dispatcher.UIThread (a view
/// model with a DispatcherTimer, say) can create it in between with a dispatcher that isn't the headless one; the
/// headless test then fails in Dispatcher.PushFrame with PlatformNotSupportedException. Whether that happens depends on
/// test order, so it shows up when classes are added. Non-parallel collections run on their own, after the rest.
/// </summary>
[CollectionDefinition(nameof(HeadlessUiCollection), DisableParallelization = true)]
public sealed class HeadlessUiCollection;
