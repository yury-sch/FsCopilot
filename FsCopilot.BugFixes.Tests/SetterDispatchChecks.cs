using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
static class SetterDispatchChecks {
    public static void Run(Action<bool,string> check){
        // Compile the production setter against recording I/O.
        var original=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Source","SimClient.cs"));
        var type=CSharpSyntaxTree.ParseText(original).GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().Single(t=>t.Identifier.Text=="SimClient");
        var method=type.Members.OfType<MethodDeclarationSyntax>().Single(m=>m.Identifier.Text=="Set"&&m.ParameterList.Parameters.Count==3);
        var source="""
        #nullable enable
        using System;
        using System.Linq;
        using System.Globalization;
        using System.Collections.Generic;
        public sealed class SetProbe {
            public List<string> Calls=new();
            private void SetLVar(string name,string? units,object value)=>Calls.Add("L:"+name);
            private void SetSimVar(string name,string? units,object value)=>Calls.Add("A:"+name);
            private void TransmitKEvent(string name,object[] values)=>Calls.Add("K:"+name+":"+string.Join(",",values));
            private void Execute(string expression)=>Calls.Add("calculator:"+expression);
        """+method.ToFullString()+"\n}";
        var references=((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Distinct().Select(path=>MetadataReference.CreateFromFile(path));
        var compilation=CSharpCompilation.Create("ProductionSetterProbe",[CSharpSyntaxTree.ParseText(source)],references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var bytes=new MemoryStream();var result=compilation.Emit(bytes);
        if(!result.Success)throw new InvalidOperationException(string.Join("\n",result.Diagnostics));
        var probeType=Assembly.Load(bytes.ToArray()).GetType("SetProbe")!;var probe=Activator.CreateInstance(probeType)!;
        var calls=(List<string>)probeType.GetField("Calls")!.GetValue(probe)!;var set=probeType.GetMethod("Set")!;
        foreach(var name in new[]{"L:TEST","A:TEST","K:TEST_SET","B:TEST_SET","H:TEST","Z:TEST"}){
            calls.Clear();set.Invoke(probe,[name,"Number",new object[]{5}]);
            check(calls.Count==1,"Production SimClient.Set dispatches "+name+" once, not a typed write plus fallback.");
            check(calls.Count>0&&(name[0] is 'L' or 'A' or 'K'?calls[0].StartsWith(name[..2],StringComparison.Ordinal):calls[0].StartsWith("calculator:",StringComparison.Ordinal)),"Original setter route is retained for "+name);
            calls.Clear();set.Invoke(probe,[name,"Number",Array.Empty<object>()]);check(calls.Count==0,"Empty argument list is ignored for "+name);
        }
        calls.Clear();set.Invoke(probe,["K:2:KOHLSMAN_SET",null,new object[]{2,16212}]);
        check(calls.Count==1&&calls[0]=="K:2:KOHLSMAN_SET:2,16212","Indexed key arguments retain their order with one dispatch.");
    }
}
