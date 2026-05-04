namespace Fedi;

public class PromiseQueue
{
    private Task _last = Task.CompletedTask;
    private readonly object _lock = new();

    public int Count { get; private set; }

    public Task Add(Func<Task> operation, string? title = null)
    {
        lock (_lock)
        {
            Count++;
            var tcs = new TaskCompletionSource();
            _last = _last.ContinueWith(async _ =>
            {
                try
                {
                    await operation();
                    tcs.SetResult();
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
                finally
                {
                    lock (_lock) { Count--; }
                }
            }, TaskScheduler.Default).Unwrap();
            return tcs.Task;
        }
    }
}
