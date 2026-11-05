using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Mousharing.Windows.Models;

namespace Mousharing.Windows.Services;

public sealed record DiscoveredMaster(string Id, string Name, string Address, DateTime LastSeen)
{
    public override string ToString() => $"{Name} · {Address}";
}

public sealed class DiscoveryService : IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private UdpClient? socket;
    private Settings? advertising;
    public event Action<DiscoveredMaster>? Found;
    public event Action<string>? Status;
    public void Advertise(Settings? settings) => advertising = settings;
    public void Start()
    {
        try
        {
            socket = new UdpClient(AddressFamily.InterNetwork);
            socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, 47833));
            socket.EnableBroadcast = true;
            _ = ReceiveLoop(); _ = BroadcastLoop();
        }
        catch (SocketException) { Status?.Invoke("Pencarian otomatis tidak tersedia. Gunakan alamat perangkat secara manual."); }
    }
    private JsonObject Announcement() => new() { ["service"] = "mousharing-native", ["v"] = 1, ["id"] = advertising!.DeviceId, ["name"] = advertising.DeviceName, ["port"] = 47832 };
    private async Task BroadcastLoop()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                JsonObject data = advertising is null ? new() { ["service"] = "mousharing-native", ["v"] = 1, ["type"] = "query" } : Announcement();
                byte[] bytes = Encoding.UTF8.GetBytes(data.ToJsonString());
                await socket!.SendAsync(bytes, new IPEndPoint(IPAddress.Broadcast, 47833), stop.Token);
                await Task.Delay(2000, stop.Token);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    private async Task ReceiveLoop()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var packet = await socket!.ReceiveAsync(stop.Token);
                if (packet.Buffer.Length > 4096) continue;
                try
                {
                    var json = JsonNode.Parse(packet.Buffer) as JsonObject;
                    if (json?["service"]?.GetValue<string>() != "mousharing-native" || json["v"]?.GetValue<int>() != 1) continue;
                    if (json["type"]?.GetValue<string>() == "query")
                    {
                        if (advertising is not null) await socket.SendAsync(Encoding.UTF8.GetBytes(Announcement().ToJsonString()), packet.RemoteEndPoint, stop.Token);
                        continue;
                    }
                    string id = json["id"]!.GetValue<string>(), name = json["name"]!.GetValue<string>();
                    if (!Guid.TryParse(id, out _) || name.Length > 128 || json["port"]?.GetValue<int>() != 47832 || id == advertising?.DeviceId) continue;
                    Found?.Invoke(new(id, name, packet.RemoteEndPoint.Address.ToString(), DateTime.UtcNow));
                }
                catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or NullReferenceException) { }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    public void Dispose() { stop.Cancel(); socket?.Dispose(); stop.Dispose(); }
}
