using P2PDiscovery;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<RelayOptions>(builder.Configuration.GetSection("Relay"));
builder.Services.AddHostedService<StunBeta>();
builder.Services.AddHostedService<StunRc1>();
builder.Services.AddHostedService<RelayStunServer>();
var app = builder.Build();
app.MapGet("/", () => "I'm fine");
app.Run();