using P2PDiscovery;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHostedService<StunBeta>();
builder.Services.AddHostedService<Stun>();
builder.Services.AddHostedService<Relay>();
var app = builder.Build();
app.MapGet("/", () => "I'm fine");
app.Run();