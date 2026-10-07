namespace DbExplorer.Core.Debugging;

/// <summary>The few moves a debugger engine offers natively; <see cref="DebugStepper"/> builds step over/out on them.</summary>
public interface IDebugPrimitives
{
    Task<DebugPosition> StepIntoAsync(CancellationToken ct);
    Task<DebugPosition> StepOverAsync(CancellationToken ct);
    Task<DebugPosition> ContinueAsync(CancellationToken ct);
}

/// <summary>
/// Step over and step out with the behaviour people expect from an IDE. PostgreSQL's debugger has no step out, and its
/// step over inside a called routine runs to the end of the whole call when that routine returns (it does not stop
/// back in the caller). Its step into does stop in the caller, so both moves are made of single steps into, stopping
/// at the first line back at (over) or above (out) the starting depth, or at a breakpoint on the way.
/// </summary>
public static class DebugStepper
{
    /// <summary>Single steps taken at most before giving up and continuing (a long loop in a called routine).</summary>
    public const int MaxSteps = 20_000;

    public static async Task<DebugPosition> StepOverAsync(
        IDebugPrimitives engine, DebugPosition from, Func<DebugFrame, bool> isBreakpoint, CancellationToken ct = default)
    {
        if (from.IsFinished) return from;
        // In the outermost routine the native step over is right: nothing to return into.
        if (from.Depth <= 1) return await engine.StepOverAsync(ct);
        return await StepUntilAsync(engine, p => p.Depth <= from.Depth, isBreakpoint, ct);
    }

    public static async Task<DebugPosition> StepOutAsync(
        IDebugPrimitives engine, DebugPosition from, Func<DebugFrame, bool> isBreakpoint, CancellationToken ct = default)
    {
        if (from.IsFinished) return from;
        if (from.Depth <= 1) return await engine.ContinueAsync(ct);
        return await StepUntilAsync(engine, p => p.Depth < from.Depth, isBreakpoint, ct);
    }

    private static async Task<DebugPosition> StepUntilAsync(
        IDebugPrimitives engine, Func<DebugPosition, bool> arrived, Func<DebugFrame, bool> isBreakpoint, CancellationToken ct)
    {
        for (var i = 0; i < MaxSteps; i++)
        {
            ct.ThrowIfCancellationRequested();
            var position = await engine.StepIntoAsync(ct);
            if (position.IsFinished || arrived(position) || isBreakpoint(position.Top!)) return position;
        }
        return await engine.ContinueAsync(ct);
    }
}
