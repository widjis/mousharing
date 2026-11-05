using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mousharing.Windows.Models;

public sealed record Invitation(
    [property: JsonPropertyName("v")] int Version,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("addresses")] string[] Addresses)
{
    public string Encode() => "MS1." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(this)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public byte[] KeyBytes { get { byte[] bytes = Convert.FromBase64String(Key); if (Convert.ToBase64String(bytes) != Key) throw new FormatException("Kunci undangan tidak valid."); return bytes; } }
    public static Invitation Parse(string value)
    {
        value = value.Trim();
        if (!value.StartsWith("MS1.", StringComparison.Ordinal) || value.Length > 8192) throw new FormatException("Tempel undangan Mousharing lengkap yang dimulai MS1.");
        string encoded = value[4..].Replace('-', '+').Replace('_', '/');
        encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
        var invitation = JsonSerializer.Deserialize<Invitation>(Convert.FromBase64String(encoded)) ?? throw new FormatException("Undangan tidak valid.");
        if (invitation.Version != 1 || !Guid.TryParse(invitation.Id, out _) || invitation.Port != 47832 || invitation.KeyBytes.Length != 32 || string.IsNullOrWhiteSpace(invitation.Name) || invitation.Name.Length > 128)
            throw new FormatException("Undangan tidak valid atau versinya tidak didukung.");
        return invitation with { Id = invitation.Id.ToLowerInvariant(), Addresses = (invitation.Addresses ?? []).Where(x => IPAddress.TryParse(x, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork).Take(16).ToArray() };
    }
    public static string[] LocalAddresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(x => x.OperationalStatus == OperationalStatus.Up && x.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(x => x.GetIPProperties().UnicastAddresses)
        .Where(x => x.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(x.Address))
        .Select(x => x.Address.ToString()).Distinct().Take(16).ToArray();
}
