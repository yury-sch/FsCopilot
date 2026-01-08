using P2PDiscovery;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;
using Serilog.Templates;
using Serilog.Templates.Themes;

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Services.AddSerilog((_, serilog) => serilog
        .Enrich.FromLogContext()
        .WriteTo.Console(new ExpressionTemplate("[{@t:HH:mm:ss} {@l:u3}] {Substring(SourceContext, LastIndexOf(SourceContext, '.') + 1),-15} {@m}\n{@x}", theme: TemplateTheme.Code)));
    builder.Services.AddHostedService<StunBeta>();
    builder.Services.AddHostedService<Stun>();
    builder.Services.AddHostedService<Relay>();
    var app = builder.Build();
    app.MapGet("/", () => "I'm fine");
    await app.RunAsync();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
