using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mousharing.Windows.Models;
using Mousharing.Windows.Services;

return await ProtocolChecks.RunAsync(args);

internal static class ProtocolChecks
{
    private const string Prefix = "mousharing-native-v1:";
    private const string MasterId = "11111111-1111-4111-8111-111111111111";
    private const string ClientId = "22222222-2222-4222-8222-222222222222";
    private const string ClientText = "CROSS-RUNTIME CLIENT 👋\nline 2";
    private const string ServerText = "CROSS-RUNTIME SERVER ✅\nline 2";
    private static JsonObject fixture = null!;
    private static byte[] secret = [];
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length < 2) throw new ArgumentException("Usage: selftest FIXTURES | server FIXTURES PORT_FILE | client FIXTURES PORT");
            fixture = JsonNode.Parse(await File.ReadAllTextAsync(args[1]))!.AsObject();
            secret = Convert.FromHexString(Text(fixture, "keyHex"));
            if (args[0] == "selftest") await SelfTestAsync();
            else if (args[0] == "server" && args.Length == 3) await ServeAsync(args[2]);
            else if (args[0] == "client" && args.Length == 3) await ConnectAsync(int.Parse(args[2]));
            else throw new ArgumentException("Unknown protocol-check mode");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"FAIL {error.GetType().Name}: {error.Message}");
            return 1;
        }
    }

    private static string Text(JsonObject value, string field) => value[field]!.GetValue<string>();
    private static byte[] Bytes(JsonObject value, string field) => Convert.FromBase64String(Text(value, field));
    private static void Require(bool condition, string label) { if (!condition) throw new InvalidDataException(label); }
    private static JsonObject Clone(JsonObject value) => value.DeepClone().AsObject();
    private static void Reject(Action action, string label)
    {
        try { action(); }
        catch (Exception ex) when (ex is CryptographicException or InvalidDataException or InvalidOperationException or FormatException or JsonException or ArgumentException or OverflowException) { return; }
        throw new InvalidDataException("Accepted invalid " + label);
    }
    private static async Task RejectAsync(Func<Task> action, string label)
    {
        try { await action(); }
        catch (Exception ex) when (ex is CryptographicException or InvalidDataException or EndOfStreamException or InvalidOperationException or FormatException or JsonException or ArgumentException or OverflowException) { return; }
        throw new InvalidDataException("Accepted invalid " + label);
    }

    private static async Task SelfTestAsync()
    {
        var invitation = Invitation.Parse(Text(fixture, "invite"));
        Require(invitation.Id == MasterId && invitation.Port == 47832 && invitation.KeyBytes.SequenceEqual(secret), "Invitation vector mismatch");
        Require(Invitation.Parse(invitation.Encode()).Id == MasterId, "Invitation roundtrip mismatch");
        var (c2s, s2c) = SecureChannel.Keys(secret, Bytes(fixture, "challengeBase64"), Bytes(fixture, "clientNonceBase64"));
        Require(Convert.ToHexString(c2s).Equals(Text(fixture, "c2sKeyHex"), StringComparison.OrdinalIgnoreCase), "c2s HKDF vector mismatch");
        Require(Convert.ToHexString(s2c).Equals(Text(fixture, "s2cKeyHex"), StringComparison.OrdinalIgnoreCase), "s2c HKDF vector mismatch");
        Require(!c2s.SequenceEqual(s2c), "Directional keys were not separated");
        var records = fixture["records"]!.AsObject();
        foreach (var item in records)
        {
            var record = item.Value!.AsObject();
            var envelope = record["envelope"]!.AsObject();
            long seq = envelope["seq"]!.GetValue<long>();
            byte[] key = item.Key is "hello" or "welcome" ? secret : item.Key.StartsWith("c2s", StringComparison.Ordinal) ? c2s : s2c;
            string aad = Text(record, "aad");
            byte[] plaintext = Bytes(record, "plaintextBase64");
            byte[] opened = SecureChannel.OpenBytes(key, envelope, seq, aad);
            Require(opened.SequenceEqual(plaintext), "Decryption vector mismatch: " + item.Key);
            JsonObject sealedRecord = SecureChannel.SealBytes(key, plaintext, seq, aad, Bytes(envelope, "nonce"));
            foreach (string field in new[] { "nonce", "ciphertext", "tag" }) Require(Text(sealedRecord, field) == Text(envelope, field), "Encryption vector mismatch: " + item.Key + "/" + field);
            Require(SecureChannel.Open(key, envelope, seq, aad)["type"] is not null, "Production JSON decoder failed");
            Reject(() => SecureChannel.OpenBytes(key, envelope, seq, aad + "-wrong"), "AAD");
            byte[] wrongKey = key.ToArray(); wrongKey[0] ^= 1;
            Reject(() => SecureChannel.OpenBytes(wrongKey, envelope, seq, aad), "key");
            var tampered = Clone(envelope); byte[] tag = Bytes(tampered, "tag"); tag[0] ^= 1; tampered["tag"] = Convert.ToBase64String(tag);
            Reject(() => SecureChannel.OpenBytes(key, tampered, seq, aad), "tag");
        }
        var sample = records["c2sClipboard"]!["envelope"]!.AsObject();
        foreach (JsonNode invalid in new JsonNode[] { JsonValue.Create(true)!, JsonValue.Create(1.5)!, JsonValue.Create(-1)! })
        {
            var changed = Clone(sample); changed["seq"] = invalid.DeepClone();
            Reject(() => SecureChannel.Open(c2s, changed, 1, Prefix + "c2s:1"), "sequence type");
        }
        Reject(() => SecureChannel.Open(secret, new JsonObject { ["seq"] = 0 }, 0, Prefix + "hello"), "missing encrypted envelope fields");
        var badNonce = Clone(sample); badNonce["nonce"] = Convert.ToBase64String(new byte[11]);
        Reject(() => SecureChannel.Open(c2s, badNonce, 1, Prefix + "c2s:1"), "nonce length");
        var whitespace = Clone(sample); whitespace["nonce"] = " " + Text(whitespace, "nonce");
        Reject(() => SecureChannel.Open(c2s, whitespace, 1, Prefix + "c2s:1"), "base64 whitespace");
        Reject(() => Invitation.Parse("MS1.not an invitation"), "invitation");
        using var truncated = new MemoryStream(Utf8.GetBytes("{}"));
        await RejectAsync(async () => { await new BoundedJsonReader(truncated).ReadAsync(CancellationToken.None); }, "truncated frame");
        using var oversized = new MemoryStream(Utf8.GetBytes("{\"data\":\"" + new string('x', 3 * 1024 * 1024) + "\"}\n"));
        await RejectAsync(async () => { await new BoundedJsonReader(oversized).ReadAsync(CancellationToken.None); }, "oversized frame");
        await ChannelSequenceChecksAsync(c2s, s2c);
        Console.WriteLine("PASS actual C# invitation, HKDF, AES-GCM vectors, malformed records, sequence and framing checks");
    }

    private static async Task ChannelSequenceChecksAsync(byte[] c2s, byte[] s2c)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var sender = new TcpClient();
        Task connect = sender.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var receiver = await listener.AcceptTcpClientAsync(); await connect;
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var channel = new SecureChannel(receiver, new BoundedJsonReader(receiver.GetStream()), s2c, c2s, "s2c", "c2s");
        JsonObject first = SecureChannel.Seal(c2s, new() { ["type"] = "ping" }, 1, Prefix + "c2s:1");
        await SecureChannel.WritePlainAsync(sender.GetStream(), first, cancel.Token);
        Require(Text(await channel.ReadAsync(cancel.Token), "type") == "ping", "Valid channel record failed");
        await SecureChannel.WritePlainAsync(sender.GetStream(), first, cancel.Token);
        await RejectAsync(async () => { await channel.ReadAsync(cancel.Token); }, "replayed channel record");
    }

    private static async Task ServeAsync(string portFile)
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        await File.WriteAllTextAsync(portFile, ((IPEndPoint)listener.LocalEndpoint).Port.ToString());
        using var client = await listener.AcceptTcpClientAsync(cancel.Token); client.NoDelay = true;
        var stream = client.GetStream(); var reader = new BoundedJsonReader(stream);
        byte[] challenge = RandomNumberGenerator.GetBytes(32);
        string encodedChallenge = Convert.ToBase64String(challenge);
        await SecureChannel.WritePlainAsync(stream, new() { ["type"] = "challenge", ["v"] = 1, ["challenge"] = encodedChallenge, ["id"] = MasterId, ["name"] = "C# test master" }, cancel.Token);
        JsonObject hello = SecureChannel.Open(secret, await reader.ReadAsync(cancel.Token), 0, Prefix + "hello");
        Require(Text(hello, "type") == "hello" && hello["v"]!.GetValue<int>() == 1 && Text(hello, "challenge") == encodedChallenge && Text(hello, "id") == ClientId, "Invalid client hello");
        Require(hello["clipboard"]?.GetValue<bool>() == true, "Clipboard capability missing");
        byte[] clientNonce = Bytes(hello, "clientNonce"); Require(clientNonce.Length == 32, "Invalid client nonce");
        var welcome = new JsonObject { ["type"] = "welcome", ["v"] = 1, ["challenge"] = encodedChallenge, ["clientNonce"] = Convert.ToBase64String(clientNonce), ["id"] = MasterId, ["name"] = "C# test master", ["clipboard"] = true };
        await SecureChannel.WritePlainAsync(stream, SecureChannel.Seal(secret, welcome, 0, Prefix + "welcome"), cancel.Token);
        var (c2s, s2c) = SecureChannel.Keys(secret, challenge, clientNonce);
        using var channel = new SecureChannel(client, reader, s2c, c2s, "s2c", "c2s");
        JsonObject clipboard = await channel.ReadAsync(cancel.Token);
        Require(Text(clipboard, "type") == "clipboard" && Text(clipboard, "text") == ClientText, "Client clipboard mismatch");
        await channel.SendAsync(new() { ["type"] = "clipboard", ["text"] = ServerText, ["clock"] = 2, ["origin"] = MasterId }, cancel.Token);
        Require(Text(await channel.ReadAsync(cancel.Token), "type") == "ping", "Client ping missing");
        await channel.SendAsync(new() { ["type"] = "pong" }, cancel.Token);
        Console.WriteLine("PASS C# master/Python client loopback");
    }

    private static async Task ConnectAsync(int port)
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var client = new TcpClient { NoDelay = true }; await client.ConnectAsync(IPAddress.Loopback, port, cancel.Token);
        var stream = client.GetStream(); var reader = new BoundedJsonReader(stream);
        JsonObject plain = await reader.ReadAsync(cancel.Token);
        Require(Text(plain, "type") == "challenge" && plain["v"]!.GetValue<int>() == 1 && Text(plain, "id") == MasterId, "Untrusted master challenge");
        byte[] challenge = Bytes(plain, "challenge"), clientNonce = RandomNumberGenerator.GetBytes(32); Require(challenge.Length == 32, "Invalid challenge");
        var hello = new JsonObject { ["type"] = "hello", ["v"] = 1, ["challenge"] = Convert.ToBase64String(challenge), ["clientNonce"] = Convert.ToBase64String(clientNonce), ["id"] = ClientId, ["name"] = "C# test client", ["clipboard"] = true };
        await SecureChannel.WritePlainAsync(stream, SecureChannel.Seal(secret, hello, 0, Prefix + "hello"), cancel.Token);
        JsonObject welcome = SecureChannel.Open(secret, await reader.ReadAsync(cancel.Token), 0, Prefix + "welcome");
        Require(Text(welcome, "type") == "welcome" && welcome["v"]!.GetValue<int>() == 1 && Text(welcome, "id") == MasterId && Bytes(welcome, "challenge").SequenceEqual(challenge) && Bytes(welcome, "clientNonce").SequenceEqual(clientNonce), "Welcome identity/nonce mismatch");
        Require(welcome["clipboard"]?.GetValue<bool>() == true, "Clipboard capability not negotiated");
        var (c2s, s2c) = SecureChannel.Keys(secret, challenge, clientNonce);
        using var channel = new SecureChannel(client, reader, c2s, s2c, "c2s", "s2c");
        await channel.SendAsync(new() { ["type"] = "clipboard", ["text"] = ClientText, ["clock"] = 1, ["origin"] = ClientId }, cancel.Token);
        JsonObject clipboard = await channel.ReadAsync(cancel.Token);
        Require(Text(clipboard, "type") == "clipboard" && Text(clipboard, "text") == ServerText, "Server clipboard mismatch");
        await channel.SendAsync(new() { ["type"] = "ping" }, cancel.Token);
        Require(Text(await channel.ReadAsync(cancel.Token), "type") == "pong", "Server pong missing");
        Console.WriteLine("PASS Python master/C# client loopback");
    }
}
