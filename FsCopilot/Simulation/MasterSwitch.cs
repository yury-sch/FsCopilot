namespace FsCopilot.Simulation;

using Connection;
using Network;

public class MasterSwitch : IDisposable
{
    private static readonly string PeerId = Guid.NewGuid().ToString();
    
    // private readonly Peer2Peer _peer2Peer;
    private readonly BehaviorSubject<bool> _master = new(true);
    private readonly CompositeDisposable _d = new();

    public bool IsMaster => _master.Value;
    public IObservable<bool> Master => _master;

    public MasterSwitch(SimClient sim, INetwork net)
    {
        net.RegisterPacket<SetMaster, SetMaster.Codec>();

        _d.Add(net.Stream<SetMaster>()
            .Subscribe(setMaster =>
            {
                var newMaster = setMaster.Peer == PeerId;
                if (newMaster != IsMaster) _master.OnNext(newMaster);
            }));
        
        _d.Add(Observable
            .Interval(TimeSpan.FromMilliseconds(500))
            .WithLatestFrom(_master, (_, isMaster) => !isMaster)
            .Subscribe(freeze => sim.Set("L:FSC_FREEZE", freeze)));
        
        _d.Add(_master.DistinctUntilChanged()
            .Where(isMaster => isMaster)
            .Subscribe(_ => net.SendAll(new SetMaster(PeerId))));
        
        _d.Add(sim.Config.Where(c => c.Undefined).Subscribe(_ => sim.Set(new SimConfig(false, _master.Value))));
        _d.Add(_master.Subscribe(val => sim.Set(new SimConfig(false, val))));
        _d.Add(sim.Config.Where(c => !c.Undefined).Subscribe(c =>
        {
            if (c.Control) TakeControl();
        }));
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