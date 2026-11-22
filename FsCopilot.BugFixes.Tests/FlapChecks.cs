using FsCopilot.Simulation;
static class FlapChecks {
    public static void Run(Action<bool,string> check){
        check(Definitions.TryLoadTree("original-baron.yaml",out var original),"Original Baron loads for flap regression.");
        check(Definitions.TryLoadTree("bksq-aircraft-baronpropress.yaml",out var candidate),"Patched Baron loads for flap regression.");
        var before=AltimeterChecks.Links(original).Single(d=>d.Get=="A:FLAPS HANDLE INDEX");
        var after=AltimeterChecks.Links(candidate).Single(d=>d.Get=="A:FLAPS HANDLE INDEX");
        check(before.Set(2d,0d)==before.Set(1d,0d),"Original flap setter loses the requested detent distance.");
        check(before.Set(0d,0d).Contains("FLAPS_DECR",StringComparison.Ordinal),"Original equal flap detent incorrectly sends DECR.");
        var absolute=new[]{"0 (>K:FLAPS_UP)","0 (>K:FLAPS_1)","0 (>K:FLAPS_DOWN)"};
        foreach(var current in new[]{0d,1d,2d})foreach(var target in new[]{0d,1d,2d}){
            check(after.Set(target,current)==(target==current?"":absolute[(int)target]),"Each requested Baron detent is absolute from any starting position.");
            check(after.Set(target,target)=="","Flap target replay emits no additional movement.");
        }
        foreach(var invalid in new[]{-1d,3d,.5,double.NaN,double.PositiveInfinity})
            check(after.Set(invalid,0d)=="","Invalid flap detent is ignored.");
        check(after.Get==before.Get&&after.Units==before.Units&&after.Shared==before.Shared,"Flap observation and vanilla sharing direction remain unchanged.");
    }
}
