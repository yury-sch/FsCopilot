namespace FsCopilot.Network;

internal interface IPacketSubject
{
    void Publish(object packet);
}

internal sealed class PacketSubject<T> : IPacketSubject
{
    private readonly List<IObserver<T>> _observers = [];

    public IDisposable Subscribe(Action<T> action)
    {
        var obs = new ActionObserver<T>(action);
        lock (_observers) _observers.Add(obs);
        return new Unsubscriber<T>(_observers, obs);
    }

    public void Publish(object packet)
    {
        var value = (T)packet;
        IObserver<T>[] snapshot;
        lock (_observers) snapshot = _observers.ToArray();
        foreach (var o in snapshot)
            o.OnNext(value);
    }

    private sealed class ActionObserver<TV>(Action<TV> onNext) : IObserver<TV>
    {
        public void OnNext(TV value) => onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    private sealed class Unsubscriber<TV>(List<IObserver<TV>> list, IObserver<TV> obs) : IDisposable
    {
        private IObserver<TV>? _obs = obs;

        public void Dispose()
        {
            var o = _obs;
            if (o is null) return;
            _obs = null;
            lock (list) list.Remove(o);
        }
    }
}