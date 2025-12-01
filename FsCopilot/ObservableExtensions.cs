using System.Reactive.Concurrency;
using System.Reactive.Disposables;

namespace FsCopilot;

using System.Reactive.Linq;

public static class ObservableExtensions
{
    /// <summary>
    /// Emits (prev, curr) pairs; the first item is (first, first).
    /// Works for any T (no reliance on null/default).
    /// </summary>
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

    public static IDisposable Record<T>(this IObservable<T> source, 
        IList<Recorded<T>> buffer, 
        IScheduler? scheduler = null) 
        where T: notnull
    {
        scheduler ??= Scheduler.Default;
        
        var start = scheduler.Now;

        return source.Subscribe(x =>
        {
            var now = scheduler.Now;
            var offset = now - start;
            buffer.Add(new Recorded<T>(offset, x));
        });
    }
    public static IObservable<T> Playback<T>(
        this IReadOnlyList<Recorded<T>> buffer,
        IScheduler? scheduler = null)
    {
        scheduler ??= Scheduler.Default;

        return Observable.Create<T>(observer =>
        {
            if (buffer.Count == 0)
            {
                observer.OnCompleted();
                return Disposable.Empty;
            }

            var serialDisp = new SerialDisposable();

            ScheduleNext(0);

            return serialDisp;

            void ScheduleNext(int index)
            {
                if (index >= buffer.Count)
                {
                    observer.OnCompleted();
                    return;
                }

                var item = buffer[index];

                var delay = index == 0
                    ? item.Offset
                    : item.Offset - buffer[index - 1].Offset;

                if (delay < TimeSpan.Zero)
                    delay = TimeSpan.Zero;

                serialDisp.Disposable = scheduler.Schedule(index, delay, (sch, i) =>
                {
                    observer.OnNext(buffer[i].Value);
                    ScheduleNext(i + 1);
                    return Disposable.Empty;
                });
            }
        });
    }
}

public readonly record struct Recorded<T>(TimeSpan Offset, T Value);