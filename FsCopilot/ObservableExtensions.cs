namespace FsCopilot;

using System.Diagnostics;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;

public static class ObservableExtensions
{
    public static IObservable<(T Prev, T Curr)> WithPreviousFirstPair<T>(this IObservable<T> source) =>
        Observable.Create<(T Prev, T Curr)>(observer =>
        {
            var hasPrev = false;
            T prev = default!; // safe: not used until hasPrev == true

            return source.Subscribe(
                curr =>
                {
                    if (!hasPrev)
                    {
                        prev = curr;
                        hasPrev = true;
                        observer.OnNext((curr, curr)); // (first, first)
                    }
                    else
                    {
                        observer.OnNext((prev, curr));
                        prev = curr;
                    }
                },
                observer.OnError,
                observer.OnCompleted
            );
        });
    
    public static IDisposable Record<T>(this IObservable<T> source, IList<Recorded<T>> buffer)
        where T : notnull
    {
        var start = Stopwatch.GetTimestamp();
        var freq = (double)Stopwatch.Frequency;

        return source.Subscribe(x =>
        {
            var now = Stopwatch.GetTimestamp();
            var seconds = (now - start) / freq;
            buffer.Add(new(TimeSpan.FromSeconds(seconds), x));
        });
    }
    
    public static IObservable<T> Playback<T>(this IReadOnlyList<Recorded<T>> buffer)
    {
        return Observable.Create<T>(observer =>
        {
            if (buffer.Count == 0)
            {
                observer.OnCompleted();
                return Disposable.Empty;
            }

            var cts = new CancellationTokenSource();

            var thread = new Thread(() =>
            {
                try
                {
                    var start = Stopwatch.GetTimestamp();
                    var freq = (double)Stopwatch.Frequency;

                    var i = 0;

                    while (!cts.IsCancellationRequested)
                    {
                        var now = Stopwatch.GetTimestamp();
                        var elapsed = TimeSpan.FromSeconds((now - start) / freq);

                        // Emit everything that is due.
                        while (i < buffer.Count && buffer[i].Offset <= elapsed)
                        {
                            observer.OnNext(buffer[i].Value);
                            i++;
                        }

                        if (i >= buffer.Count)
                        {
                            observer.OnCompleted();
                            return;
                        }

                        // Time until next sample
                        var nextDue = buffer[i].Offset;
                        var remain = (nextDue - elapsed).Milliseconds;

                        switch (remain)
                        {
                            // Adaptive waiting: sleep when far, spin when near.
                            case > 12:
                                Thread.Sleep(1); // coarse wait
                                break;
                            case > 4:
                                Thread.Sleep(0); // yield
                                break;
                            default:
                                Thread.SpinWait(200); // fine-grained
                                break;
                        }
                    }
                }
                catch (Exception e)
                {
                    observer.OnError(e);
                }
            })
            {
                IsBackground = true,
                Name = "PlaybackThreadLoop"
            };

            thread.Start();

            return Disposable.Create(() =>
            {
                cts.Cancel();
                try { thread.Join(200); } catch { /* ignore */ }
                cts.Dispose();
            });
        });
    }
}

public readonly record struct Recorded<T>(TimeSpan Offset, T Value);
