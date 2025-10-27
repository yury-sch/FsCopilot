namespace P2PDiscovery;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

public class UdpHost : BackgroundService
{
    private static readonly byte[] HostProtocolVersion = Guid.Parse("8f0fecf9-07a7-4f1e-90d2-b7ccde5099a8").ToByteArray();
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        const int udpPort = 3478;
        var peers = new ConcurrentDictionary<string, Peer>();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, udpPort));
        Console.WriteLine($"UDP echo/relay listening on port {udpPort}");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var peer in peers.Values.ToArray())
                {
                    if (peer.Timestamp < DateTime.UtcNow - TimeSpan.FromMinutes(5))
                    {
                        peers.TryRemove(peer.PeerId, out _);
                    }
                }

                var result = await udp.ReceiveAsync(stoppingToken);
                var remote = result.RemoteEndPoint;
                var buffer = result.Buffer;

                using var msr = new MemoryStream(buffer);
                using var br = new BinaryReader(msr, Encoding.UTF8, true);

                var requestType = (SystemPacketTypes)br.ReadByte();
                var peerId = br.ReadString();
                var version = br.ReadBytes(16);
                if (!version.SequenceEqual(HostProtocolVersion)) continue;
                
                if (requestType == SystemPacketTypes.DISCOVER) // HELLO
                {
                    var localCount = br.ReadInt32();
                    var localAddresses = new List<IPEndPoint>(localCount);
                    for (var i = 0; i < localCount; i++)
                    {
                        var localAddrStr = br.ReadString();
                        if (IPEndPoint.TryParse(localAddrStr, out var localAdd)) localAddresses.Add(localAdd);
                    }
                    var schema = br.ReadString();

                    // var a = IPEndPoint.TryParse(localAddr, out var local) ? local : new IPEndPoint();

                    peers[peerId] = new(peerId, localAddresses.ToArray(), remote, schema, DateTime.UtcNow);

                    using var msw = new MemoryStream();
                    await using var bw = new BinaryWriter(msw, Encoding.UTF8, true);
                    bw.Write((byte)requestType); // HELLO RESP
                    bw.Write(peerId);
                    bw.Write(remote.ToString());
                    bw.Flush();
                    var reply = msw.ToArray();
                    await udp.SendAsync(reply, reply.Length, remote);
                }
                else if (requestType == SystemPacketTypes.CONNECT) // CONNECT
                {
                    var sourceId = br.ReadString();
                    var targetId = br.ReadString();

                    var window = DateTime.UtcNow - TimeSpan.FromSeconds(30);
                    if (sourceId != targetId
                        && peers.TryGetValue(sourceId, out var sourceInfo)
                        && peers.TryGetValue(targetId, out var targetInfo)
                        && sourceInfo.Timestamp >= window
                        && targetInfo.Timestamp >= window)
                    {
                        if (sourceInfo.Schema.Equals(targetInfo.Schema))
                        {
                            var targetData = PreparePeerConnection(targetInfo);
                            await udp.SendAsync(targetData, targetData.Length, sourceInfo.UdpSeen);

                            var notify = PreparePeerConnection(sourceInfo);
                            await udp.SendAsync(notify, notify.Length, targetInfo.UdpSeen);    
                        }
                        else
                        {
                            using var msw = new MemoryStream();
                            await using var bw = new BinaryWriter(msw, Encoding.UTF8, true);
                            bw.Write((byte)SystemPacketTypes.MISMATCH);
                            var reply = msw.ToArray();
                            bw.Write(peerId);
                            await udp.SendAsync(reply, reply.Length, remote);
                        }   
                    }
                }
                // else if (requestType == byte.MaxValue) // RELAYTO
                // {
                //     var fromPeerId = br.ReadString();
                //     var toPeerId = br.ReadString();
                //     var msgLen = br.ReadInt32();
                //     var msgData = br.ReadBytes(msgLen);

                //     if (peers.TryGetValue(toPeerId, out var targetInfo))
                //     {
                //         await udp.SendAsync(msgData, msgData.Length, targetInfo.UdpSeen);
                //     }
                // }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }
    }

    private static byte[] PreparePeerConnection(Peer peer)
    {
        using var msw = new MemoryStream();
        using var bw = new BinaryWriter(msw, Encoding.UTF8, true);
        bw.Write((byte)SystemPacketTypes.CONNECT); // CONNECT RESP
        bw.Write(peer.PeerId);
        bw.Write(peer.UdpSeen.ToString());
        bw.Write(peer.Local.Length);
        foreach (var local in peer.Local)
            bw.Write(local.ToString());

        bw.Flush();
        return msw.ToArray();
    }

    private record Peer(string PeerId, IPEndPoint[] Local, IPEndPoint UdpSeen, string Schema, DateTime Timestamp);

    private enum SystemPacketTypes : byte
    {
        DISCOVER = 0,
        CONNECT = 1,
        MISMATCH = 2,
    }
}
