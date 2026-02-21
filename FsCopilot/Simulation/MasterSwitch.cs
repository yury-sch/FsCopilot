namespace FsCopilot.Simulation;

using Connection;
using Network;

public class MasterSwitch : IDisposable
{
    private static readonly string PeerId = Guid.NewGuid().ToString();

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
        
        _d.Add(_master
            .Subscribe(isMaster => sim.SetControl(isMaster ? BehaviorControl.Master : BehaviorControl.Slave)));
        
        _d.Add(_master.DistinctUntilChanged()
            .Where(isMaster => isMaster)
            .Subscribe(_ => net.SendAll(new SetMaster(PeerId))));
        
        _d.Add(sim.Stream("A:WATER RUDDER HANDLE POSITION", "Bool")
            .Select(Convert.ToDouble)
            .Sample(TimeSpan.FromSeconds(1))
            .Where(v => v < 0)
            .Subscribe(_ =>
            {
                Skip.Next("FSC_TAKE_CONTROL");
                sim.Set("A:WATER RUDDER HANDLE POSITION", 0);
            }));
        
        _d.Add(sim.Stream("A:WATER RUDDER HANDLE POSITION", "Bool")
            .Skip(1)
            .Select(Convert.ToDouble)
            .Where(v => v >= 0 && !Skip.Should("FSC_TAKE_CONTROL"))
            .DistinctUntilChanged()
            .Do(_ => Log.Information("Water rudder toggle detected"))
            .Subscribe(_ => TakeControl()));
        
        // _d.Add(sim.Config.Where(c => c.Undefined).Subscribe(_ => sim.Set(new SimConfig(false, _master.Value))));
        // _d.Add(_master.Subscribe(val => sim.Set(new SimConfig(false, val))));
        // _d.Add(sim.Config.Where(c => !c.Undefined).Subscribe(c =>
        // {
        //     if (c.Control) TakeControl();
        // }));
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