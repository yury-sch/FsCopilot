using P2PDiscovery;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHostedService<LegacyHost>();
builder.Services.AddHostedService<LiteHost>();
var app = builder.Build();
app.MapGet("/", () => "I'm fine");
app.Run();