using System.Collections.Concurrent;

namespace DbExplorer.Tests;

/// <summary>
/// Runs a test body the way the app runs its view models: on one thread, with a SynchronizationContext that brings
/// every await continuation back to that thread. Without it, a fire-and-forget load that finishes on the thread pool
/// changes the view model's collections while the test thread is still inside the method that started it.
/// </summary>
internal static class UiThread
{
    public static Task RunAsync(Func<Task> body)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var context = new PumpContext();
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                var task = body();
                task.ContinueWith(_ => context.Stop(), TaskScheduler.Default);
                context.Pump();
                task.GetAwaiter().GetResult();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }) { IsBackground = true, Name = "test UI thread" };
        thread.Start();
        return tcs.Task;
    }

    private sealed class PumpContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback, object?)> _queue = [];

        public override void Post(SendOrPostCallback d, object? state)
        {
            // A load the test did not wait for may finish after the body: it runs on the pool then, as nothing watches it.
            try
            {
                if (!_queue.IsAddingCompleted) { _queue.Add((d, state)); return; }
            }
            catch (InvalidOperationException)
            {
                // Completed between the check and the add.
            }
            ThreadPool.QueueUserWorkItem(_ => d(state));
        }

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException("Send is not used by the view models.");

        public void Pump()
        {
            foreach (var (callback, state) in _queue.GetConsumingEnumerable()) callback(state);
        }

        public void Stop() => _queue.CompleteAdding();
    }
}
