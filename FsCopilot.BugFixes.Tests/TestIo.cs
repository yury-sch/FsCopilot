// Compile production code with simulated I/O.
using System.Collections.Concurrent;
using System.Reflection;
namespace Avalonia.Platform { } // Production's unused namespace import.
namespace FsCopilot.Connection {
    public record Interact(string Instrument,string Event,string Id,string? Value);
    public sealed class SimClient {
        readonly Dictionary<string,Subject<object>> _values=new();
        public readonly List<string> Writes=[];
        public IObservable<string> Aircraft=>Observable.Never<string>();
        public IObservable<Interact> Interactions=>Observable.Never<Interact>();
        public void Register<T>()where T:unmanaged{}
        public IObservable<T> Stream<T>()where T:unmanaged=>Observable.Never<T>();
        public IObservable<object> Stream(string name,string units){if(!_values.TryGetValue(name,out var value))_values[name]=value=new();return value;}
        public void Observe(string name,object value)=>_values[name].OnNext(value);
        public void Set<T>(T value)where T:unmanaged{}
        public void Set(Interact value){}
        public void Set(string name,string? units,object[] values){if(values.Length>0)Writes.Add(name);}
        public void Execute(string expression){if(!string.IsNullOrWhiteSpace(expression))Writes.Add(expression);}
    }
}
namespace FsCopilot.Network {
    public interface INetwork {
        void RegisterPacket<T,TCodec>();
        IObservable<T> Stream<T>();
        void SendAll<T>(T value,bool unreliable=false);
    }
    public sealed class TestNetwork:INetwork {
        readonly Dictionary<Type,object> _streams=new();
        public readonly ConcurrentQueue<object> Sent=new();
        public void RegisterPacket<T,TCodec>(){}
        public IObservable<T> Stream<T>(){if(!_streams.TryGetValue(typeof(T),out var value))_streams[typeof(T)]=value=new Subject<T>();return (Subject<T>)value;}
        public void SendAll<T>(T value,bool unreliable=false)=>Sent.Enqueue(value!);
        public void Receive(string name,object value){
            var item=_streams.Single(pair=>pair.Key.Name=="Update");
            var packet=Activator.CreateInstance(item.Key,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,[name,value],null)!;
            item.Value.GetType().GetMethod("OnNext")!.Invoke(item.Value,[packet]);
        }
    }
}
namespace FsCopilot.Simulation {
    public sealed class MasterSwitch {public bool IsMaster {get;set;}=true;}
    public static class Skip {public static int Calls;public static void Next(string name)=>++Calls;public static bool Should(string name)=>false;}
    public struct Physics {public ulong SessionId;public uint TimeMs;public sealed class Codec;}
    public struct Surfaces {public ulong SessionId;public uint TimeMs;public sealed class Codec;}
}
