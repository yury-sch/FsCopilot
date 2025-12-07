namespace FsCopilot.Simulation;

using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Connection;
using Network;

public class MasterSwitch : IDisposable
{
    private static readonly string PeerId = Guid.NewGuid().ToString();
    
    private readonly SimClient _sim;
    // private readonly Peer2Peer _peer2Peer;
    private readonly BehaviorSubject<bool> _master = new(true);
    private readonly CompositeDisposable _d = new();

    public bool IsMaster => _master.Value;
    public IObservable<bool> Master => _master;

    public MasterSwitch(SimClient sim, IPeer2Peer p2p)
    {
        _sim = sim;

        p2p.RegisterPacket<SetMaster, SetMaster.Codec>();

        _d.Add(p2p.Stream<SetMaster>()
            .Subscribe(setMaster =>
            {
                var newMaster = setMaster.Peer == PeerId;
                if (newMaster != IsMaster) _master.OnNext(newMaster);
            }));

        _d.Add(_master.Subscribe(_ => UpdateFreeze()));
        
        _d.Add(_master.DistinctUntilChanged()
            .Where(isMaster => isMaster)
            .Subscribe(_ => p2p.SendAll(new SetMaster(PeerId))));

        // _d.Add(simConnect.Connected
        //     .Where(connected => connected)
        //     .Subscribe(_ => UpdateFreeze()));

        // _d.Add(Observable.Interval(TimeSpan.FromMilliseconds(500))
        //     .Subscribe(_ => UpdateFreeze()));
        _d.Add(_sim.Stream<Physics>()
            .Window(TimeSpan.FromSeconds(1))
            .Where(_ => !IsMaster)
            .Subscribe(_ => UpdateFreeze()));
        
        _d.Add(_sim.Config.Where(c => c.Undefined).Subscribe(_ => _sim.Set(new SimConfig(false, _master.Value))));
        _d.Add(_master.Subscribe(val => _sim.Set(new SimConfig(false, val))));
        _d.Add(_sim.Config.Where(c => !c.Undefined).Subscribe(c =>
        {
            if (c.Control) TakeControl();
        }));
    }

    private void UpdateFreeze()
    {
        _sim.Set("K:FREEZE_LATITUDE_LONGITUDE_SET", !IsMaster);
        _sim.Set("K:FREEZE_ALTITUDE_SET", !IsMaster);
        _sim.Set("K:FREEZE_ATTITUDE_SET", !IsMaster);
    }

    public void TakeControl() => _master.OnNext(true);

    //todo Temp solution. We should use ClientConnected event from Peer2Peer class 
    public void Join() => _master.OnNext(false);

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