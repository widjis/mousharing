using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Mousharing.Windows.Services;

public sealed class BoundedJsonReader(Stream stream)
{
    private readonly byte[] buffer = new byte[8192];
    private int start, count;
    public async Task<JsonObject> ReadAsync(CancellationToken token)
    {
        using var data = new MemoryStream();
        while (true)
        {
            if (start == count) { count = await stream.ReadAsync(buffer.AsMemory(), token); start = 0; if (count == 0) throw new EndOfStreamException("Perangkat pasangan menutup koneksi."); }
            int end = Array.IndexOf(buffer, (byte)'\n', start, count - start);
            int length = (end < 0 ? count : end) - start;
            if (data.Length + length + 1 > 3 * 1024 * 1024) throw new InvalidDataException("Pesan melampaui batas ukuran.");
            data.Write(buffer, start, length);
            start += length + (end < 0 ? 0 : 1);
            if (end >= 0) return JsonNode.Parse(new UTF8Encoding(false, true).GetString(data.ToArray())) as JsonObject ?? throw new InvalidDataException("Pesan tidak valid.");
        }
    }
}

public sealed class SecureChannel(TcpClient client, BoundedJsonReader reader, byte[] sendKey, byte[] receiveKey, string sendDirection, string receiveDirection) : IDisposable
{
    private readonly SemaphoreSlim writer = new(1, 1);
    private long sent, received;
    private readonly NetworkStream stream = client.GetStream();
    public async Task SendAsync(JsonObject message, CancellationToken token)
    {
        await writer.WaitAsync(token);
        try
        {
            long seq = checked(++sent);
            await WritePlainAsync(stream, Seal(sendKey, message, seq, $"mousharing-native-v1:{sendDirection}:{seq.ToString(CultureInfo.InvariantCulture)}"), token);
        }
        finally { writer.Release(); }
    }
    public async Task<JsonObject> ReadAsync(CancellationToken token)
    {
        JsonObject frame = await reader.ReadAsync(token);
        long seq = frame["seq"]?.GetValue<long>() ?? throw new InvalidDataException("Nomor urut tidak ada.");
        if (seq != received + 1) throw new CryptographicException("Nomor urut sesi tidak valid.");
        JsonObject result = Open(receiveKey, frame, seq, $"mousharing-native-v1:{receiveDirection}:{seq.ToString(CultureInfo.InvariantCulture)}");
        received = seq;
        return result;
    }
    public static JsonObject Seal(byte[] key, JsonObject payload, long sequence, string aad) => SealBytes(key, Encoding.UTF8.GetBytes(payload.ToJsonString()), sequence, aad, RandomNumberGenerator.GetBytes(12));
    public static JsonObject SealBytes(byte[] key, byte[] data, long sequence, string aad, byte[] nonce)
    {
        if (nonce.Length != 12) throw new CryptographicException("Nonce tidak valid.");
        byte[] ciphertext = new byte[data.Length], tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, data, ciphertext, tag, Encoding.UTF8.GetBytes(aad));
        return new() { ["seq"] = sequence, ["nonce"] = Convert.ToBase64String(nonce), ["ciphertext"] = Convert.ToBase64String(ciphertext), ["tag"] = Convert.ToBase64String(tag) };
    }
    public static JsonObject Open(byte[] key, JsonObject frame, long sequence, string aad) => JsonNode.Parse(new UTF8Encoding(false, true).GetString(OpenBytes(key, frame, sequence, aad))) as JsonObject ?? throw new InvalidDataException("Isi pesan tidak valid.");
    public static byte[] OpenBytes(byte[] key, JsonObject frame, long sequence, string aad)
    {
        if (frame["seq"]?.GetValue<long>() != sequence) throw new CryptographicException("Nomor urut tidak cocok.");
        byte[] nonce = DecodeBase64(frame["nonce"]!.GetValue<string>()), ciphertext = DecodeBase64(frame["ciphertext"]!.GetValue<string>()), tag = DecodeBase64(frame["tag"]!.GetValue<string>());
        if (nonce.Length != 12 || tag.Length != 16) throw new CryptographicException("Format enkripsi tidak valid.");
        byte[] data = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, ciphertext, tag, data, Encoding.UTF8.GetBytes(aad));
        return data;
    }
    public static byte[] DecodeBase64(string value)
    {
        byte[] result = Convert.FromBase64String(value);
        if (Convert.ToBase64String(result) != value) throw new FormatException("Base64 tidak kanonis.");
        return result;
    }
    public static async Task WritePlainAsync(Stream stream, JsonObject frame, CancellationToken token)
    {
        byte[] data = Encoding.UTF8.GetBytes(frame.ToJsonString() + "\n");
        if (data.Length > 3 * 1024 * 1024) throw new InvalidDataException("Pesan melampaui batas ukuran.");
        await stream.WriteAsync(data, token);
    }
    public static (byte[] c2s, byte[] s2c) Keys(byte[] invitationKey, byte[] challenge, byte[] clientNonce)
    {
        if (challenge.Length != 32 || clientNonce.Length != 32) throw new CryptographicException("Challenge tidak valid.");
        byte[] salt = SHA256.HashData(challenge.Concat(clientNonce).ToArray());
        return (HKDF.DeriveKey(HashAlgorithmName.SHA256, invitationKey, 32, salt, Encoding.UTF8.GetBytes("mousharing-native-v1:c2s")),
            HKDF.DeriveKey(HashAlgorithmName.SHA256, invitationKey, 32, salt, Encoding.UTF8.GetBytes("mousharing-native-v1:s2c")));
    }
    public void Dispose() { client.Dispose(); }
}
