using FsCopilot.Simulation;
static class AltimeterChecks {
    public static IEnumerable<Definition> Links(DefinitionNode node)=>node.Include.SelectMany(Links).Concat(node.Master).Concat(node.Shared);
    public static void Run(Action<bool,string> check){
        check(Definitions.TryLoadTree("original-baron.yaml",out var original),"Original downloaded Baron profile loads.");
        check(Definitions.TryLoadTree("bksq-aircraft-baronpropress.yaml",out var candidate),"Candidate Baron profile and include tree load.");
        check(original.Include.Length==5&&candidate.Include.Length==5,"All five vanilla modules remain included; no EFB module is added.");
        var before=Links(original).ToArray();var after=Links(candidate).ToArray();
        foreach(var old in before.Where(d=>d.Get.StartsWith("L:INSTRUMENT_Altimeter_",StringComparison.Ordinal))){
            check(old.Set(1013.25,1013.25).Contains("KOHLSMAN_DEC",StringComparison.Ordinal),"Original equal-pressure setter demonstrably decrements.");
            check(old.Set(1030d,990d).Contains("KOHLSMAN_INC",StringComparison.Ordinal),"Original large pressure change sends only a relative step.");
        }
        var altimeters=after.Where(d=>d.Get.StartsWith("A:KOHLSMAN SETTING MB EX1:",StringComparison.Ordinal)).ToArray();
        check(altimeters.Length==2,"Candidate has two indexed absolute altimeter mappings.");
        if(altimeters.Length!=2)return;
        check(!after.Any(d=>d.Get=="A:KOHLSMAN SETTING MB:0"||d.Get.StartsWith("L:INSTRUMENT_Altimeter_",StringComparison.Ordinal)),"Baron tree contains no old relative or conflicting GTN barometer writer.");
        foreach(var index in new[]{1,2}){
            var def=altimeters.Single(d=>d.Get.EndsWith(":"+index,StringComparison.Ordinal));
            check(def.Shared&&def.Units=="Millibars","Each indexed instrument remains normally shared in explicit pressure units.");
            foreach(var current in new[]{500d,1013.25,1200d})foreach(var target in new[]{500d,950d,1000d,1013.25,1025.0625,1100d,1200d}){
                var result=def.ParseSet(target,current,out _,out var values);var encoded=Math.Floor(target*16+.5);
                if(encoded==Math.Floor(current*16+.5))check(values.Length==0,"Repeated equal pressure emits no command.");
                else{
                    check(result=="K:KOHLSMAN_SET"&&values.Length==2,"Vanilla parser recognizes one absolute two-argument event.");
                    check(Convert.ToDouble(values[0])==encoded&&Convert.ToDouble(values[1])==index,"RPN is reversed into SDK pressure-first, instrument-index-second argument order.");
                }
                def.ParseSet(target,encoded/16,out _,out var replay);
                check(replay.Length==0,"Replay after quantized readback emits no chasing command.");
            }
            foreach(var invalid in new[]{double.NaN,double.PositiveInfinity,double.NegativeInfinity,0,499.9,1200.1,1300}){
                def.ParseSet(invalid,1013.25,out _,out var values);check(values.Length==0,"Invalid/out-of-range pressures never fall through into a variable write.");
            }
            check(def.Set(1013.26,1013.25)=="","Sub-quantum pressure differences are stable.");
        }
        bool Barometer(Definition d)=>d.Get.StartsWith("L:INSTRUMENT_Altimeter_",StringComparison.Ordinal)||d.Get.StartsWith("A:KOHLSMAN SETTING MB",StringComparison.Ordinal);
        var untouchedBefore=before.Where(d=>!Barometer(d)).ToArray();var untouchedAfter=after.Where(d=>!Barometer(d)).ToArray();
        check(untouchedBefore.Length==untouchedAfter.Length,"Non-barometer mapping count is retained.");
        for(var i=0;i<Math.Min(untouchedBefore.Length,untouchedAfter.Length);++i){
            var a=untouchedBefore[i];var b=untouchedAfter[i];
            check(a.Get==b.Get&&a.Units==b.Units&&a.Shared==b.Shared&&a.Skip==b.Skip,"Other mappings retain identity, units, direction, order and suppression rule.");
            check(a.Set(1d,0d)==b.Set(1d,0d)&&a.Set(0d,1d)==b.Set(0d,1d),"Other aircraft/GTN mappings retain both setter directions.");
        }
    }
}
