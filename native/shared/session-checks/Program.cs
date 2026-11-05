using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.UI.Dispatching;
using Mousharing.Windows.Models;
using Mousharing.Windows.Services;

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
using var sender = new TcpClient();
await sender.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, deadline.Token);
using var receiver = await listener.AcceptTcpClientAsync(deadline.Token);
listener.Stop();
byte[] a = RandomNumberGenerator.GetBytes(32), b = RandomNumberGenerator.GetBytes(32);
using var peer = new SecureChannel(sender, new(sender.GetStream()), a, b, "s2c", "c2s");
using var incoming = new SecureChannel(receiver, new(receiver.GetStream()), b, a, "c2s", "s2c");
using var controller = new SessionController(new Settings(), new DispatcherQueue(), 0);
var run = typeof(SessionController).GetMethod("RunSession", BindingFlags.Instance | BindingFlags.NonPublic)!;
Task session = Task.Run(() => (Task)run.Invoke(controller, [incoming, "Test peer", true, deadline.Token])!);
await Until(() => controller.Connected);
await peer.SendAsync(new() { ["type"] = "enter" }, deadline.Token);
await peer.SendAsync(new() { ["type"] = "move" }, deadline.Token);
await peer.SendAsync(new() { ["type"] = "clipboard" }, deadline.Token);
await Until(() => NativeInput.Received == 2 && ClipboardService.Received == 1 && ClipboardService.Ticks >= 2);
controller.Stop();
await session.WaitAsync(deadline.Token);
int count = NativeInput.Received, ticks = ClipboardService.Ticks;
await Task.Delay(350, deadline.Token);
if (controller.Connected || NativeInput.Released == 0 || NativeInput.Received != count || ClipboardService.Ticks != ticks)
    throw new Exception("Stop failed to release input or stop background processing");
Console.WriteLine("PASS real encrypted session delivers input and clipboard with UI queue stalled; Stop releases input and stops polling.");
async Task Until(Func<bool> condition)
{
    while (!condition()) await Task.Delay(10, deadline.Token);
}
