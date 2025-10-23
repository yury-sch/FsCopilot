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
}