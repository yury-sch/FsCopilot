namespace FsCopilot.Connection;

public static class WaitHandleExtensions
{
    public static Task<bool> WaitOneAsync(this WaitHandle handle, TimeSpan timeout, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (ct.CanBeCanceled)
        {
            var reg = ct.Register(() => tcs.TrySetCanceled(ct));
            tcs.Task.ContinueWith(_ => reg.Dispose(), TaskScheduler.Default);
        }

        // Non-blocking waiting by the pool core
        var rwh = ThreadPool.RegisterWaitForSingleObject(handle,
            (state, timedOut) =>
            {
                var src = (TaskCompletionSource<bool>)state!;
                src.TrySetResult(!timedOut);
            },
            tcs,
            timeout,
            /* executeOnlyOnce: */
            true);

        var t = tcs.Task;
        t.ContinueWith(_ => rwh.Unregister(null));
        return t;
    }
}