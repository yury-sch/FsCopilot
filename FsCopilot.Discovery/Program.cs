using Microsoft.Extensions.FileProviders;
using P2PDiscovery;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHostedService<UdpHost>();
var app = builder.Build();
app.Urls.Add("http://*:80");
app.MapGet("/", () => "I'm fine");
app.UseFileServer(new FileServerOptions
{
    RequestPath = "/.well-known/acme-challenge",
    FileProvider = new PhysicalFileProvider(Path.Combine(Directory.GetCurrentDirectory(), ".well-known", "acme-challenge")),
    StaticFileOptions = { ServeUnknownFileTypes = true }
});
app.Run();