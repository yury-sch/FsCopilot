using System.Net.WebSockets;
using System.Text;

namespace P2PDiscovery;

public static class StatsWebSocketEndpoint
{
   public static void MapStatsWebSocket(this WebApplication app)
    {
        app.Map("/ws/stats", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("WebSocket request expected.");
                return;
            }

            var stats = context.RequestServices.GetRequiredService<ServerStats>();
            using var socket = await context.WebSockets.AcceptWebSocketAsync();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            var ct = cts.Token;

            var receiveTask = Task.Run(async () =>
            {
                var buffer = new byte[1024];

                try
                {
                    while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
                    {
                        var result = await socket.ReceiveAsync(buffer, ct);
                        if (result.MessageType != WebSocketMessageType.Close) continue;

                        cts.Cancel();
                        break;
                    }
                }
                catch (OperationCanceledException) { }
                catch (WebSocketException) { }
            });

            string? lastJson = null;

            try
            {
                while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var json = stats.ToJson();
                    if (!string.Equals(json, lastJson, StringComparison.Ordinal))
                    {
                        var bytes = Encoding.UTF8.GetBytes(json);
                        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken: CancellationToken.None);
                        lastJson = json;
                    }

                    await Task.Delay(1000, CancellationToken.None);
                }
            }
            catch (WebSocketException) { }
            catch (OperationCanceledException) { }
            finally
            {
                cts.Cancel();

                try { await receiveTask; }
                catch { /* ignore */ }

                if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
                {
                    try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", CancellationToken.None); }
                    catch { /* ignore */ }
                }
            }
        });
    }
}
