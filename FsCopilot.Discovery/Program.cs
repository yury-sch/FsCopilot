using P2PDiscovery;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHostedService<UdpHost>();
var app = builder.Build();
app.MapGet("/", () => "I'm fine");
app.Run();