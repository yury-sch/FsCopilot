namespace FsCopilot.Simulation;

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

    public MasterSwitch(SimClient sim, INetwork net)
    {
        _sim = sim;

        net.RegisterPacket<SetMaster, SetMaster.Codec>();

        _d.Add(net.Stream<SetMaster>()
            .Subscribe(setMaster =>
            {
                var newMaster = setMaster.Peer == PeerId;
                if (newMaster != IsMaster) _master.OnNext(newMaster);
            }));

        _d.Add(_master.Subscribe(_ => UpdateFreeze()));
        
        _d.Add(_master.DistinctUntilChanged()
            .Where(isMaster => isMaster)
            .Subscribe(_ => net.SendAll(new SetMaster(PeerId))));
        
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
            public void Encode(SetMaster packet, BinaryWriter bw) => bw.Write(packet.Peer);

            public SetMaster Decode(BinaryReader br) => new(br.ReadString());
        }
    }
}