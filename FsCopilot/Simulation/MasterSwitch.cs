namespace FsCopilot.Simulation;

using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Connection;
using Network;

public class MasterSwitch : IDisposable
{
    private readonly SimConnectClient _simConnect;
    private readonly Peer2Peer _peer2Peer;
    private readonly BehaviorSubject<bool> _master = new(false);
    private readonly CompositeDisposable _d = new();

    public bool IsMaster => _master.Value;
    public IObservable<bool> Master => _master;

    public MasterSwitch(SimConnectClient simConnect, Peer2Peer peer2Peer)
    {
        _simConnect = simConnect;
        _peer2Peer = peer2Peer;

        _peer2Peer.RegisterPacket<SetMaster, SetMaster.Codec>();

        _d.Add(peer2Peer.Subscribe<SetMaster>(setMaster =>
        {
            var newMaster = setMaster.Peer == _peer2Peer.PeerId;
            if (newMaster != IsMaster) _master.OnNext(newMaster);
        }));

        _d.Add(_master.Subscribe(_ => UpdateFreeze()));

        // _d.Add(simConnect.Connected
        //     .Where(connected => connected)
        //     .Subscribe(_ => UpdateFreeze()));

        // _d.Add(Observable.Interval(TimeSpan.FromMilliseconds(500))
        //     .Subscribe(_ => UpdateFreeze()));
        _d.Add(_simConnect.Stream<Physics>()
            .Window(TimeSpan.FromSeconds(1))
            .Where(_ => !IsMaster)
            .Subscribe(_ => UpdateFreeze()));

        // simConnect.AddDataDefinition<Probe>();
        // _d.Add(simConnect.Stream<Probe>()
        //         .Select(p => (Ready: IsReady(p), p.Title, p.Category))
        //         .Buffer(3, 1)
        //         .Where(buf => buf.Count == 3)
        //         .Select(buf => (
        //             Ready: buf[0].Ready && buf[1].Ready && buf[2].Ready,
        //             Title: buf[2].Title,
        //             Category: buf[2].Category))
        //         .DistinctUntilChanged()
        //         .Delay(TimeSpan.FromSeconds(10))
        //     .Subscribe(p =>
        //     {
        //         Debug.WriteLine("___________________________________");
        //         Debug.WriteLine(p.Title);
        //         // Debug.WriteLine(p.AtcModel);
        //         Debug.WriteLine(p.Category);
        //         Debug.WriteLine("___________________________________");
        //         UpdateFreeze();
        //     }));

        // Observable.Merge(
        //         simConnect.Event("SimStart").Select(_ =>
        //         {
        //             Debug.WriteLine("SimStart");
        //             return true;
        //         }),
        //         simConnect.Event("SimStop").Select(_ =>
        //         {
        //             Debug.WriteLine("SimStop");
        //             return false;
        //         }),
        //         simConnect.Event("AircraftLoaded").Select(_ =>
        //         {
        //             Debug.WriteLine("AircraftLoaded");
        //             return false;
        //         }))
        //     .DistinctUntilChanged()
        //     .StartWith(false)
        //     .Where(connected => connected)
        //     .Subscribe(_ => UpdateFreeze());

        // _d.Add(simConnect.ClientConnected
        //     .Subscribe(_ => _master.OnNext(false)));
    }

    private void UpdateFreeze()
    {
        _simConnect.TransmitClientEvent("FREEZE_LATITUDE_LONGITUDE_SET", !IsMaster, 0);
        _simConnect.TransmitClientEvent("FREEZE_ALTITUDE_SET", !IsMaster, 0);
        _simConnect.TransmitClientEvent("FREEZE_ATTITUDE_SET", !IsMaster, 0);
    }

    public void TakeControl()
    {
        _master.OnNext(true);
        _peer2Peer.SendAll(new SetMaster(_peer2Peer.PeerId));
    }

    //todo Temp solution. We should use ClientConnected event from Peer2Peer class 
    public Task Join()
    {
        _master.OnNext(false);
        return Task.CompletedTask;
    }

    public void Dispose() => _d.Dispose();

    private record SetMaster(string Peer)
    {
        public class Codec : IPacketCodec<SetMaster>
        {
            public void Encode(SetMaster packet, BinaryWriter bw)
            {
                bw.Write(packet.Peer);
            }

            public SetMaster Decode(BinaryReader br)
            {
                var peer = br.ReadString();
                return new(peer);
            }
        }
    }
}