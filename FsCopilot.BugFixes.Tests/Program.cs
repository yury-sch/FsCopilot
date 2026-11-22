var checks=0;
var failures=new List<string>();
void Check(bool condition,string description){++checks;if(!condition)failures.Add(description);}
try {
    NoOpChecks.Run(Check);
    SetterDispatchChecks.Run(Check);
    AltimeterChecks.Run(Check);
    foreach(var failure in failures)Console.Error.WriteLine("FAIL: "+failure);
    if(failures.Count>0)throw new InvalidOperationException($"{failures.Count}/{checks} regression checks failed.");
    Console.WriteLine($"PASS {checks} vanilla regression checks. Production loader/Coordinator code; simulated I/O, no MSFS or network access.");
} catch(Exception error){Console.Error.WriteLine(error);Environment.ExitCode=1;}
