using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using Mousharing.Windows.Models;

namespace Mousharing.Windows.Services;

public sealed class SessionController : IDisposable
{
    private readonly DispatcherQueue dispatcher;
    private readonly nint owner;
    private CancellationTokenSource? stop;
    private TcpListener? listener;
    private SecureChannel? channel;
    private NativeInput input = new();
    private ClipboardService? clipboard;
    private DispatcherQueueTimer? clipboardTimer;
    private OutboundQueue? outbound;
    private long lastReceived;
    private bool paused;
    private readonly object lifecycle = new();
    public Settings Settings { get; }
    public bool Active => stop is not null;
    public bool Connected => channel is not null;
    public bool Paused => paused;
    public string Status { get; private set; } = "Siap disambungkan";
    public string Control { get; private set; } = "Mengontrol Windows ini";
    public event Action? Changed;
    public Action<Settings?>? Advertise;
    public SessionController(Settings settings, DispatcherQueue dispatcher, nint owner)
    {
        Settings = settings; this.dispatcher = dispatcher; this.owner = owner;
    }
    private void UI(Action action) { if (dispatcher.HasThreadAccess) action(); else dispatcher.TryEnqueue(() => action()); }
    private void Report(string text) => UI(() => { Status = text; Changed?.Invoke(); });
    public string MakeInvitation(bool rotate = false)
    {
        byte[]? key = rotate ? null : SecretStore.Read("master");
        key ??= RandomNumberGenerator.GetBytes(32);
        SecretStore.Write("master", key);
        if (rotate) { Settings.TrustedPeerId = null; Settings.Save(); }
        return new Invitation(1, Settings.DeviceId, Settings.DeviceName, 47832, Convert.ToBase64String(key), Invitation.LocalAddresses()).Encode();
    }
    public void ImportInvitation(string text)
    {
        if (Active) throw new InvalidOperationException("Putuskan sesi sebelum mengganti pasangan.");
        Invitation invitation = Invitation.Parse(text);
        if (invitation.Id == Settings.DeviceId) throw new InvalidOperationException("Undangan ini milik Windows ini. Gunakan undangan perangkat master yang lain.");
        SecretStore.Write("client", invitation.KeyBytes);
        Settings.MasterId = invitation.Id; Settings.MasterName = invitation.Name; Settings.Addresses = invitation.Addresses;
        Settings.Save();
    }
    public void Start()
    {
        if (Active) return;
        stop = new(); paused = false;
        Settings.Save();
        input = new NativeInput();
        input.Outbound = Enqueue;
        input.ControlChanged = text => UI(() => { Control = text; Changed?.Invoke(); });
        input.Failed = text => UI(() => { Stop(); Report(text); });
        if (Settings.IsMaster)
        {
            byte[] key = SecretStore.Read("master") ?? RandomNumberGenerator.GetBytes(32);
            SecretStore.Write("master", key);
            try
            {
                listener = new TcpListener(IPAddress.Any, 47832); listener.Start();
                input.StartMaster(Settings.Side, Settings.EdgeSwitchEnabled);
                Advertise?.Invoke(Settings);
                Report("Menunggu perangkat client · bagikan undangan pairing");
                _ = AcceptLoop(key, stop.Token);
            }
            catch (Exception e) { Stop(); Report("Tidak bisa membuka koneksi: " + e.Message); }
        }
        else
        {
            byte[]? key = SecretStore.Read("client");
            if (key is null || Settings.MasterId is null) { Stop(); Report("Tempel undangan dari perangkat master terlebih dahulu."); return; }
            Report("Menghubungkan ke " + Settings.MasterName + "…");
            _ = ConnectLoop(key, stop.Token);
        }
        Changed?.Invoke();
    }
    private async Task AcceptLoop(byte[] key, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient? tcp = null;
            try
            {
                tcp = await listener!.AcceptTcpClientAsync(token); tcp.NoDelay = true;
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token); handshake.CancelAfter(TimeSpan.FromSeconds(15));
                byte[] challenge = RandomNumberGenerator.GetBytes(32);
                var stream = tcp.GetStream(); var reader = new BoundedJsonReader(stream);
                string encodedChallenge = Convert.ToBase64String(challenge);
                await SecureChannel.WritePlainAsync(stream, new() { ["type"] = "challenge", ["v"] = 1, ["challenge"] = encodedChallenge, ["id"] = Settings.DeviceId, ["name"] = Settings.DeviceName }, handshake.Token);
                JsonObject hello = SecureChannel.Open(key, await reader.ReadAsync(handshake.Token), 0, "mousharing-native-v1:hello");
                string peerId = hello["id"]!.GetValue<string>(), peerName = hello["name"]!.GetValue<string>();
                byte[] nonce = SecureChannel.DecodeBase64(hello["clientNonce"]!.GetValue<string>());
                if (hello["type"]?.GetValue<string>() != "hello" || hello["v"]?.GetValue<int>() != 1 || hello["challenge"]?.GetValue<string>() != encodedChallenge || nonce.Length != 32 || !Guid.TryParse(peerId, out _) || peerId == Settings.DeviceId || peerName.Length > 128 || Settings.TrustedPeerId is not null && Settings.TrustedPeerId != peerId)
                    throw new CryptographicException("Pasangan tidak cocok. Buat undangan baru jika Anda mengganti perangkat.");
                await SecureChannel.WritePlainAsync(stream, SecureChannel.Seal(key, new() { ["type"] = "welcome", ["v"] = 1, ["challenge"] = encodedChallenge, ["clientNonce"] = Convert.ToBase64String(nonce), ["id"] = Settings.DeviceId, ["name"] = Settings.DeviceName }, 0, "mousharing-native-v1:welcome"), handshake.Token);
                UI(() => { Settings.TrustedPeerId = peerId; Settings.Save(); });
                var keys = SecureChannel.Keys(key, challenge, nonce);
                await RunSession(new(tcp, reader, keys.s2c, keys.c2s, "s2c", "c2s"), peerName, token);
                tcp = null;
            }
            catch (Exception e) when (e is SocketException or IOException or CryptographicException or InvalidOperationException or System.Text.Json.JsonException or FormatException or OperationCanceledException)
            { if (!token.IsCancellationRequested) Report("Pairing belum berhasil: " + SafeError(e)); }
            finally { tcp?.Dispose(); }
        }
    }
    private async Task ConnectLoop(byte[] key, CancellationToken token)
    {
        var addresses = (!string.IsNullOrWhiteSpace(Settings.ManualAddress) ? new[] { Settings.ManualAddress.Trim() } : Settings.Addresses).Distinct().ToArray();
        if (addresses.Length == 0) { UI(Stop); Report("Master belum ditemukan. Pilih perangkat dari daftar atau isi alamat LAN pada pengaturan."); return; }
        Exception? last = null;
        foreach (string address in addresses)
        {
            TcpClient? tcp = null;
            try
            {
                tcp = new(AddressFamily.InterNetwork) { NoDelay = true };
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token); handshake.CancelAfter(TimeSpan.FromSeconds(15));
                await tcp.ConnectAsync(address, 47832, handshake.Token);
                var stream = tcp.GetStream(); var reader = new BoundedJsonReader(stream);
                JsonObject challenge = await reader.ReadAsync(handshake.Token);
                string encodedChallenge = challenge["challenge"]!.GetValue<string>();
                byte[] rawChallenge = SecureChannel.DecodeBase64(encodedChallenge), nonce = RandomNumberGenerator.GetBytes(32);
                if (challenge["type"]?.GetValue<string>() != "challenge" || challenge["v"]?.GetValue<int>() != 1 || challenge["id"]?.GetValue<string>() != Settings.MasterId || rawChallenge.Length != 32)
                    throw new CryptographicException("Identitas master berbeda dari undangan.");
                await SecureChannel.WritePlainAsync(stream, SecureChannel.Seal(key, new() { ["type"] = "hello", ["v"] = 1, ["challenge"] = encodedChallenge, ["clientNonce"] = Convert.ToBase64String(nonce), ["id"] = Settings.DeviceId, ["name"] = Settings.DeviceName }, 0, "mousharing-native-v1:hello"), handshake.Token);
                JsonObject welcome = SecureChannel.Open(key, await reader.ReadAsync(handshake.Token), 0, "mousharing-native-v1:welcome");
                if (welcome["type"]?.GetValue<string>() != "welcome" || welcome["v"]?.GetValue<int>() != 1 || welcome["challenge"]?.GetValue<string>() != encodedChallenge || welcome["clientNonce"]?.GetValue<string>() != Convert.ToBase64String(nonce) || welcome["id"]?.GetValue<string>() != Settings.MasterId)
                    throw new CryptographicException("Undangan atau identitas master tidak cocok.");
                var keys = SecureChannel.Keys(key, rawChallenge, nonce);
                await RunSession(new(tcp, reader, keys.c2s, keys.s2c, "c2s", "s2c"), Settings.MasterName ?? "Master", token);
                tcp = null;
                if (!token.IsCancellationRequested) { UI(Stop); Report("Koneksi terputus. Kontrol Windows sudah lokal. Pilih Hubungkan untuk mencoba kembali."); }
                return;
            }
            catch (Exception e) when (e is SocketException or IOException or CryptographicException or InvalidOperationException or System.Text.Json.JsonException or FormatException or OperationCanceledException) { last = e; }
            finally { tcp?.Dispose(); }
        }
        if (!token.IsCancellationRequested) { UI(Stop); Report("Belum terhubung. Pastikan master aktif dan jaringan Private mengizinkan Mousharing. " + (last is null ? "" : SafeError(last))); }
    }
    private async Task RunSession(SecureChannel current, string peer, CancellationToken token)
    {
        using (current)
        using (var session = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            channel = current; outbound = new(); lastReceived = Environment.TickCount64;
            UI(() =>
            {
                clipboard = new(owner, Settings.DeviceId) { Outbound = Enqueue };
                clipboard.Reset();
                clipboardTimer = dispatcher.CreateTimer(); clipboardTimer.Interval = TimeSpan.FromMilliseconds(250);
                clipboardTimer.Tick += (_, _) => { if (Settings.ClipboardEnabled && !paused && Connected) clipboard?.Tick(); };
                clipboardTimer.Start(); input.SetConnected(!paused);
                Status = "Terhubung dengan " + peer; Changed?.Invoke();
            });
            Task send = WriterLoop(current, session.Token), read = ReaderLoop(current, session.Token), monitor = Watchdog(session.Token);
            try { await Task.WhenAny(send, read, monitor); }
            finally
            {
                session.Cancel(); current.Dispose(); channel = null; outbound = null;
                UI(() => { clipboardTimer?.Stop(); clipboardTimer = null; clipboard = null; input.SetConnected(false); input.ReleaseInjected(); Control = "Mengontrol Windows ini"; Changed?.Invoke(); });
                try { await Task.WhenAll(send, read, monitor); } catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException or InvalidDataException or CryptographicException or InvalidOperationException or FormatException or System.Text.Json.JsonException) { }
            }
        }
    }
    private async Task WriterLoop(SecureChannel current, CancellationToken token)
    {
        long pingAt = 0;
        while (!token.IsCancellationRequested)
        {
            if (Environment.TickCount64 >= pingAt) { await current.SendAsync(new() { ["type"] = "ping" }, token); pingAt = Environment.TickCount64 + 1000; }
            JsonObject? message = await outbound!.TakeAsync(token);
            if (message is not null) await current.SendAsync(message, token);
        }
    }
    private async Task ReaderLoop(SecureChannel current, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            JsonObject message = await current.ReadAsync(token); lastReceived = Environment.TickCount64;
            string type = message["type"]?.GetValue<string>() ?? throw new InvalidDataException("Pesan tidak valid.");
            if (type == "ping") { Enqueue(new() { ["type"] = "pong" }); continue; }
            if (type == "pong") continue;
            if (type == "clipboard") { UI(() => { if (!paused && Settings.ClipboardEnabled) { try { clipboard?.Receive(message); } catch (Exception e) { Stop(); Report(SafeError(e)); } } }); continue; }
            if (Settings.IsMaster)
            {
                if (type != "leave") throw new InvalidDataException("Client mengirim arah input yang tidak diizinkan.");
                double ratio = message["ratio"]!.GetValue<double>();
                if (!double.IsFinite(ratio) || ratio < 0 || ratio > 1) throw new InvalidDataException("Posisi kembali tidak valid.");
                input.ReturnLocal(ratio, message["switchId"]!.GetValue<int>());
            }
            else
            {
                if (type is not ("enter" or "move" or "button" or "key" or "scroll" or "reset")) throw new InvalidDataException("Pesan sesi tidak dikenal.");
                UI(() => { if (!paused || type == "reset") { try { input.Receive(message); } catch (Exception e) { Stop(); Report(SafeError(e)); } } });
            }
        }
    }
    private async Task Watchdog(CancellationToken token)
    {
        while (!token.IsCancellationRequested) { await Task.Delay(250, token); if (Environment.TickCount64 - Interlocked.Read(ref lastReceived) > 6000) throw new IOException("Perangkat pasangan tidak merespons."); }
    }
    public void Enqueue(JsonObject message)
    {
        if (channel is null || outbound is null || paused && message["type"]?.GetValue<string>() is not ("ping" or "pong" or "reset")) return;
        if (!outbound.Put(message)) UI(() => { Stop(); Report("Antrean input terlalu penuh. Kontrol sudah kembali lokal; hubungkan kembali."); });
    }
    public void TogglePause()
    {
        if (!Connected) return;
        paused = !paused; input.SetConnected(!paused);
        if (paused) { input.ReturnLocal(); input.ReleaseInjected(); Enqueue(new() { ["type"] = "reset" }); }
        clipboard?.Reset();
        Status = paused ? "Berbagi dijeda · mouse, keyboard, clipboard lokal" : "Berbagi dilanjutkan";
        Changed?.Invoke();
    }
    public void Stop()
    {
        lock (lifecycle)
        {
            stop?.Cancel(); stop?.Dispose(); stop = null; listener?.Stop(); listener = null;
            channel?.Dispose(); channel = null; outbound = null;
            clipboardTimer?.Stop(); clipboardTimer = null; clipboard = null;
            input.Dispose(); paused = false; Advertise?.Invoke(null);
            Status = "Tidak terhubung"; Control = "Mengontrol Windows ini"; Changed?.Invoke();
        }
    }
    public void ForgetPairing()
    {
        Stop(); SecretStore.Forget("client"); SecretStore.Forget("master");
        Settings.TrustedPeerId = null; Settings.MasterId = null; Settings.MasterName = null; Settings.Addresses = []; Settings.ManualAddress = ""; Settings.Save();
        Report("Pairing dihapus. Buat atau tempel undangan baru untuk menyambungkan.");
    }
    private static string SafeError(Exception e) => e is CryptographicException ? "Undangan tidak cocok atau sesi gagal diverifikasi. Buat undangan baru." : e is OperationCanceledException ? "Koneksi melewati batas waktu." : e is InvalidDataException ? e.Message : "Koneksi belum berhasil. Coba lagi setelah memastikan pasangan aktif.";
    public void Dispose() => Stop();
}

internal sealed class OutboundQueue
{
    private readonly LinkedList<JsonObject> list = new();
    private readonly SemaphoreSlim ready = new(0);
    public bool Put(JsonObject value)
    {
        lock (list)
        {
            if (value["type"]?.GetValue<string>() == "move" && list.Last?.Value is JsonObject tail && tail["type"]?.GetValue<string>() == "move" && tail["edgeSwitch"]?.GetValue<bool>() == value["edgeSwitch"]?.GetValue<bool>())
            { tail["dx"] = (int)Math.Clamp((long)tail["dx"]!.GetValue<int>() + value["dx"]!.GetValue<int>(), -2000, 2000); tail["dy"] = (int)Math.Clamp((long)tail["dy"]!.GetValue<int>() + value["dy"]!.GetValue<int>(), -2000, 2000); return true; }
            if (list.Count >= 1024) return false;
            list.AddLast(value); ready.Release(); return true;
        }
    }
    public async Task<JsonObject?> TakeAsync(CancellationToken token)
    {
        if (!await ready.WaitAsync(100, token)) return null;
        lock (list) { JsonObject message = list.First!.Value; list.RemoveFirst(); return message; }
    }
}
