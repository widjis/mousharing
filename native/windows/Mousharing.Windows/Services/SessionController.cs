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
    private volatile SecureChannel? channel;
    private NativeInput input = new();
    private ClipboardService? clipboard;
    private System.Threading.Timer? clipboardTimer;
    private volatile OutboundQueue? outbound;
    private long lastReceived;
    private volatile bool paused;
    private int generation, operation;
    private bool negotiatedClipboard;
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
    private void Report(string text, int? expectedOperation = null) => UI(() => { if (expectedOperation.HasValue && expectedOperation != operation) return; Status = text; Changed?.Invoke(); });
    public string MakeInvitation()
    {
        byte[]? key = SecretStore.Read("master");
        if (key is null) { key = RandomNumberGenerator.GetBytes(32); SecretStore.Write("master", key); }
        if (key.Length != 32) throw new InvalidOperationException("Kunci master tersimpan tidak valid. Pulihkan backup atau hapus pairing secara sengaja.");
        return new Invitation(1, Settings.DeviceId, Settings.DeviceName, 47832, Convert.ToBase64String(key), Invitation.LocalAddresses()).Encode();
    }
    public void ImportInvitation(string text)
    {
        if (Active) throw new InvalidOperationException("Putuskan sesi sebelum mengganti pasangan.");
        Invitation invitation = Invitation.Parse(text);
        if (invitation.Id == Settings.DeviceId) throw new InvalidOperationException("Undangan ini milik Windows ini. Gunakan undangan perangkat master yang lain.");
        // A unique candidate key never replaces the active credential before config commit.
        string candidateName = "client-" + Guid.NewGuid().ToString("N");
        SecretStore.Write(candidateName, invitation.KeyBytes);
        Settings candidate = Settings.Copy();
        candidate.MasterId = invitation.Id; candidate.MasterName = invitation.Name; candidate.Addresses = invitation.Addresses; candidate.ClientSecretName = candidateName;
        candidate.Save();
        Settings.ApplyPairingFrom(candidate);
    }
    public void Start()
    {
        if (Active) return;
        Settings.Save();
        byte[]? storedKey = SecretStore.Read(Settings.IsMaster ? "master" : Settings.ClientSecretName ?? "client");
        if (storedKey is not null && storedKey.Length != 32) throw new InvalidOperationException("Kunci pairing tersimpan tidak valid. File dipertahankan; pulihkan backup atau hapus pairing secara sengaja.");
        if (!Settings.IsMaster && (storedKey is null || Settings.MasterId is null)) { Report("Tempel undangan dari perangkat master terlebih dahulu."); return; }
        if (Settings.IsMaster && storedKey is null) { storedKey = RandomNumberGenerator.GetBytes(32); SecretStore.Write("master", storedKey); }
        stop = new(); paused = false;
        int currentOperation = Interlocked.Increment(ref operation);
        input = new NativeInput();
        input.Outbound = message => Enqueue(message, expectedOperation: currentOperation);
        input.ControlChanged = text => UI(() => { if (currentOperation != operation) return; Control = text; Changed?.Invoke(); });
        input.Failed = text => UI(() => { if (currentOperation != operation) return; Stop(); Report(text); });
        if (Settings.IsMaster)
        {
            byte[] key = storedKey!;
            try
            {
                listener = new TcpListener(IPAddress.Any, 47832); listener.Start();
                input.StartMaster(Settings.Side, Settings.EdgeSwitchEnabled);
                Advertise?.Invoke(Settings);
                Report("Menunggu perangkat client · bagikan undangan pairing");
                var token = stop.Token;
                _ = Task.Run(() => AcceptLoop(key, token));
            }
            catch (Exception e) { Stop(); Report("Tidak bisa membuka koneksi: " + e.Message); }
        }
        else
        {
            byte[] key = storedKey!;
            Report("Menghubungkan ke " + Settings.MasterName + "…");
            var token = stop.Token;
            _ = Task.Run(() => ConnectLoop(key, token));
        }
        Changed?.Invoke();
    }
    private async Task AcceptLoop(byte[] key, CancellationToken token)
    {
        int acceptingOperation = operation;
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
                string peerId = SecureChannel.Text(hello,"id"), peerName = SecureChannel.Text(hello,"name");
                byte[] nonce = SecureChannel.DecodeBase64(SecureChannel.Text(hello,"clientNonce"));
                if (hello["type"]?.GetValue<string>() != "hello" || hello["v"]?.GetValue<int>() != 1 || hello["challenge"]?.GetValue<string>() != encodedChallenge || nonce.Length != 32 || !Guid.TryParse(peerId, out _) || peerId == Settings.DeviceId || string.IsNullOrWhiteSpace(peerName) || peerName.Length > 128 || Settings.TrustedPeerId is not null && Settings.TrustedPeerId != peerId)
                    throw new CryptographicException("Pasangan tidak cocok. Buat undangan baru jika Anda mengganti perangkat.");
                bool clipboardAllowed = Settings.ClipboardEnabled && ClipboardCapability(hello);
                await RememberPeerAsync(peerId, acceptingOperation, handshake.Token);
                await SecureChannel.WritePlainAsync(stream, SecureChannel.Seal(key, new() { ["type"] = "welcome", ["v"] = 1, ["challenge"] = encodedChallenge, ["clientNonce"] = Convert.ToBase64String(nonce), ["id"] = Settings.DeviceId, ["name"] = Settings.DeviceName, ["clipboard"] = clipboardAllowed }, 0, "mousharing-native-v1:welcome"), handshake.Token);
                var keys = SecureChannel.Keys(key, challenge, nonce);
                await RunSession(new(tcp, reader, keys.s2c, keys.c2s, "s2c", "c2s"), peerName, clipboardAllowed, token);
                if (!token.IsCancellationRequested) Report("Menunggu perangkat client · kontrol Windows sudah lokal", acceptingOperation);
                tcp = null;
            }
            catch (Exception e) when (e is SocketException or IOException or CryptographicException or InvalidOperationException or System.Text.Json.JsonException or FormatException or OperationCanceledException or NullReferenceException or ArgumentException)
            { if (!token.IsCancellationRequested) Report("Pairing belum berhasil: " + SafeError(e), acceptingOperation); }
            finally { tcp?.Dispose(); }
        }
    }
    private async Task RememberPeerAsync(string peerId, int expectedOperation, CancellationToken token)
    {
        // Persistence belongs to the session, not the visible window's dispatcher.
        lock (lifecycle)
        {
            token.ThrowIfCancellationRequested();
            if (expectedOperation != operation) throw new OperationCanceledException(token);
            string? previous = Settings.TrustedPeerId;
            try { Settings.TrustedPeerId = peerId; Settings.Save(); }
            catch (Exception e) { Settings.TrustedPeerId = previous; throw new InvalidOperationException("Pairing belum bisa disimpan. Periksa akses folder konfigurasi sebelum mencoba kembali.", e); }
        }
        await Task.CompletedTask;
    }
    private async Task ConnectLoop(byte[] key, CancellationToken token)
    {
        int connectingOperation = operation;
        int attempt = 0;
        int[] delays = [1, 2, 4, 8, 15];
        while (!token.IsCancellationRequested)
        {
            var addresses = (!string.IsNullOrWhiteSpace(Settings.ManualAddress) ? new[] { Settings.ManualAddress.Trim() } : Settings.Addresses).Distinct().ToArray();
            if (addresses.Length == 0) { UI(() => { if (connectingOperation != operation || token.IsCancellationRequested) return; Stop(); Report("Master belum ditemukan. Pilih perangkat dari daftar atau isi alamat LAN pada pengaturan."); }); return; }
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
                    string encodedChallenge = SecureChannel.Text(challenge,"challenge");
                    byte[] rawChallenge = SecureChannel.DecodeBase64(encodedChallenge), nonce = RandomNumberGenerator.GetBytes(32);
                    if (challenge["type"]?.GetValue<string>() != "challenge" || challenge["v"]?.GetValue<int>() != 1 || challenge["id"]?.GetValue<string>() != Settings.MasterId || rawChallenge.Length != 32)
                        throw new CryptographicException("Identitas master berbeda dari undangan.");
                    await SecureChannel.WritePlainAsync(stream, SecureChannel.Seal(key, new() { ["type"] = "hello", ["v"] = 1, ["challenge"] = encodedChallenge, ["clientNonce"] = Convert.ToBase64String(nonce), ["id"] = Settings.DeviceId, ["name"] = Settings.DeviceName, ["clipboard"] = Settings.ClipboardEnabled }, 0, "mousharing-native-v1:hello"), handshake.Token);
                    JsonObject welcome = SecureChannel.Open(key, await reader.ReadAsync(handshake.Token), 0, "mousharing-native-v1:welcome");
                    if (welcome["type"]?.GetValue<string>() != "welcome" || welcome["v"]?.GetValue<int>() != 1 || welcome["challenge"]?.GetValue<string>() != encodedChallenge || welcome["clientNonce"]?.GetValue<string>() != Convert.ToBase64String(nonce) || welcome["id"]?.GetValue<string>() != Settings.MasterId)
                        throw new CryptographicException("Undangan atau identitas master tidak cocok.");
                    var keys = SecureChannel.Keys(key, rawChallenge, nonce);
                    attempt = 0;
                    await RunSession(new(tcp, reader, keys.c2s, keys.s2c, "c2s", "s2c"), Settings.MasterName ?? "Master", Settings.ClipboardEnabled && ClipboardCapability(welcome), token);
                    tcp = null;
                    if (token.IsCancellationRequested) return;
                }
                catch (Exception e) when (e is SocketException or IOException or CryptographicException or InvalidOperationException or System.Text.Json.JsonException or FormatException or OperationCanceledException or NullReferenceException or ArgumentException) { last = e; }
                finally { tcp?.Dispose(); }
            }
            if (token.IsCancellationRequested) return;
            int delay = delays[Math.Min(attempt++, delays.Length - 1)];
            Report($"Menyambungkan kembali dalam {delay} detik · kontrol Windows sudah lokal. " + (last is CryptographicException ? SafeError(last) : "Pastikan master aktif dan firewall jaringan Private mengizinkan Mousharing."), connectingOperation);
            try { await Task.Delay(TimeSpan.FromSeconds(delay), token); } catch (OperationCanceledException) { return; }
        }
    }
    private async Task RunSession(SecureChannel current, string peer, bool clipboardAllowed, CancellationToken token)
    {
        using (current)
        using (var session = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            int currentGeneration;
            OutboundQueue sessionQueue;
            NativeInput sessionInput;
            lock (lifecycle)
            {
                token.ThrowIfCancellationRequested();
                currentGeneration = Interlocked.Increment(ref generation);
                channel = current; outbound = sessionQueue = new(); sessionInput = input; negotiatedClipboard = clipboardAllowed; lastReceived = Environment.TickCount64;
                clipboard = new(owner, Settings.DeviceId) { Outbound = message => Enqueue(message, expectedGeneration: currentGeneration) };
                clipboard.Reset();
                if (negotiatedClipboard) clipboard.Tick();
                clipboardTimer = new System.Threading.Timer(_ =>
                {
                    lock (lifecycle)
                    {
                        if (currentGeneration != generation || token.IsCancellationRequested || paused || !Connected) return;
                        try { if (negotiatedClipboard) clipboard?.Tick(); }
                        catch (Exception e) { Stop(); Report(SafeError(e)); }
                    }
                }, null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
                input.SetConnected(!paused);
            }
            Report("Terhubung dengan " + peer);
            Task send = WriterLoop(current, sessionQueue, session.Token), read = ReaderLoop(current, sessionInput, currentGeneration, session.Token), monitor = Watchdog(session.Token);
            try { await Task.WhenAny(send, read, monitor); }
            finally
            {
                session.Cancel(); current.Dispose();
                lock (lifecycle)
                {
                    if (currentGeneration == generation)
                    {
                        channel = null; outbound = null;
                        clipboardTimer?.Dispose(); clipboardTimer = null; clipboard = null;
                        input.SetConnected(false); input.ReleaseInjected();
                        UI(() => { if (currentGeneration != generation) return; Control = "Mengontrol Windows ini"; Changed?.Invoke(); });
                    }
                }
                try { await Task.WhenAll(send, read, monitor); } catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException or InvalidDataException or CryptographicException or InvalidOperationException or FormatException or System.Text.Json.JsonException) { }
            }
        }
    }
    private async Task WriterLoop(SecureChannel current, OutboundQueue queue, CancellationToken token)
    {
        long pingAt = 0;
        while (!token.IsCancellationRequested)
        {
            if (Environment.TickCount64 >= pingAt) { await current.SendAsync(new() { ["type"] = "ping" }, token); pingAt = Environment.TickCount64 + 1000; }
            JsonObject? message = await queue.TakeAsync(token);
            if (message is not null) await current.SendAsync(message, token);
        }
    }
    private async Task ReaderLoop(SecureChannel current, NativeInput sessionInput, int currentGeneration, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            JsonObject message = await current.ReadAsync(token);
            if (currentGeneration != generation || !ReferenceEquals(channel,current) || token.IsCancellationRequested) return;
            lastReceived = Environment.TickCount64;
            string type = message["type"]?.GetValue<string>() ?? throw new InvalidDataException("Pesan tidak valid.");
            if (type == "ping") { Enqueue(new() { ["type"] = "pong" }, expectedGeneration: currentGeneration); continue; }
            if (type == "pong") continue;
            if (type == "clipboard")
            {
                lock (lifecycle)
                {
                    if (currentGeneration != generation || !ReferenceEquals(channel,current) || token.IsCancellationRequested) return;
                    if (!paused && negotiatedClipboard) clipboard?.Receive(message);
                }
                continue;
            }
            if (Settings.IsMaster)
            {
                if (type != "leave") throw new InvalidDataException("Client mengirim arah input yang tidak diizinkan.");
                double ratio = message["ratio"]!.GetValue<double>();
                if (!double.IsFinite(ratio) || ratio < 0 || ratio > 1) throw new InvalidDataException("Posisi kembali tidak valid.");
                sessionInput.ReturnLocal(ratio, message["switchId"]!.GetValue<int>());
            }
            else
            {
                if (type is not ("enter" or "move" or "button" or "key" or "scroll" or "reset")) throw new InvalidDataException("Pesan sesi tidak dikenal.");
                // Never queue input behind WinUI rendering/visibility. Serialize with
                // Stop and clipboard cleanup so stale sessions cannot inject after stop.
                lock (lifecycle)
                {
                    if (currentGeneration != generation || !ReferenceEquals(channel,current) || token.IsCancellationRequested) return;
                    if (!paused || type == "reset") sessionInput.Receive(message);
                }
            }
        }
    }
    private async Task Watchdog(CancellationToken token)
    {
        while (!token.IsCancellationRequested) { await Task.Delay(250, token); if (Environment.TickCount64 - Interlocked.Read(ref lastReceived) > 6000) throw new IOException("Perangkat pasangan tidak merespons."); }
    }
    public void Enqueue(JsonObject message, int? expectedOperation = null, int? expectedGeneration = null)
    {
        var queue = outbound;
        var currentChannel = channel;
        if (expectedOperation.HasValue && expectedOperation != operation || expectedGeneration.HasValue && expectedGeneration != generation) return;
        if (currentChannel is null || queue is null || paused && message["type"]?.GetValue<string>() is not ("ping" or "pong" or "reset")) return;
        if (!queue.Put(message)) UI(() => { if (!ReferenceEquals(currentChannel, channel)) return; Stop(); Report("Antrean input terlalu penuh. Kontrol sudah kembali lokal; hubungkan kembali."); });
    }
    public void TogglePause()
    {
        if (paused) { Start(); return; }
        if (!Active) return;
        Stop(); paused = true;
        Status = "Berbagi dijeda · mouse, keyboard, clipboard lokal";
        Changed?.Invoke();
    }
    public void Stop()
    {
        lock (lifecycle)
        {
            Interlocked.Increment(ref generation); Interlocked.Increment(ref operation);
            stop?.Cancel(); stop?.Dispose(); stop = null; listener?.Stop(); listener = null;
            channel?.Dispose(); channel = null; outbound = null;
            clipboardTimer?.Dispose(); clipboardTimer = null; clipboard = null;
            input.Dispose(); paused = false; Advertise?.Invoke(null);
            Status = "Tidak terhubung"; Control = "Mengontrol Windows ini"; UI(() => Changed?.Invoke());
        }
    }
    public void ForgetPairing()
    {
        if (Models.Settings.LoadFailure is not null) throw new InvalidOperationException(Models.Settings.LoadFailure);
        Stop();
        if (Settings.ClientSecretName is not null) SecretStore.Forget(Settings.ClientSecretName);
        SecretStore.Forget("client"); SecretStore.Forget("master");
        Settings candidate = Settings.Copy();
        candidate.TrustedPeerId = null; candidate.MasterId = null; candidate.MasterName = null; candidate.ClientSecretName = null; candidate.Addresses = []; candidate.ManualAddress = "";
        candidate.Save();
        Settings.ApplyPairingFrom(candidate);
        Report("Pairing dihapus. Buat atau tempel undangan baru untuk menyambungkan.");
    }
    private static bool ClipboardCapability(JsonObject hello)
    {
        if (hello["clipboard"] is null) return false;
        if (hello["clipboard"] is JsonValue value && value.TryGetValue<bool>(out bool enabled)) return enabled;
        throw new InvalidDataException("Kapabilitas clipboard tidak valid.");
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
