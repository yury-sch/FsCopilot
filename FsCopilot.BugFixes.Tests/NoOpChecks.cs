using FsCopilot.Simulation;
using FsCopilot.Connection;
using FsCopilot.Network;
static class NoOpChecks {
    public static void Run(Action<bool,string> check){
        foreach(var shared in new[]{false,true})foreach(var empty in new[]{"","   "}){
            var expression=$"value === current ? '{empty}' : `${{value}} (>L:TEST_NOOP, Number)`";
            var definition=new Definition(shared,"L:TEST_NOOP, Number",expression,null);
            definition.ParseSet(10d,10d,out _,out var values);
            check(values.Length==0,$"Empty/blank setter has zero arguments, shared={shared}, blank={empty.Length}.");
            var root=Path.Combine(AppContext.BaseDirectory,"Definitions");Directory.CreateDirectory(root);
            var yaml=(shared?"shared":"master")+":\n  - get: L:TEST_NOOP, Number\n    set: \""+expression+"\"\n";
            File.WriteAllText(Path.Combine(root,"regression-noop.yaml"),yaml);
            var definitions=Definitions.Load("regression-noop");check(definitions.Count==1,"Real vanilla loader reads the test definition.");
            var sim=new SimClient();var network=new TestNetwork();var master=new MasterSwitch();
            using var coordinator=new Coordinator(sim,network,master);coordinator.Load(definitions);
            sim.Observe("L:TEST_NOOP",10d);
            check(SpinWait.SpinUntil(()=>network.Sent.Count>0,2000),"Production Coordinator observes the initial simulator value.");
            master.IsMaster=false;Skip.Calls=0;network.Receive("L:TEST_NOOP",10d);
            check(sim.Writes.Count==0,$"No-op emits no simulator write, shared={shared}, blank={empty.Length}.");
            check(Skip.Calls==0,$"No-op consumes no echo-suppression count, shared={shared}, blank={empty.Length}.");
            network.Receive("L:TEST_NOOP",11d);
            check(sim.Writes.Count==1,"A changed value still executes exactly one setter.");
            check(Skip.Calls==(shared?1:0),"A changed shared value retains its normal suppression count.");
        }
    }
}
