using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mousharing.Windows.Models;

public sealed class Settings
{
    public string DeviceId { get; set; } = Guid.NewGuid().ToString();
    public string DeviceName { get; set; } = Environment.MachineName;
    public bool IsMaster { get; set; }
    public string Side { get; set; } = "right";
    public bool ClipboardEnabled { get; set; } = true;
    public bool EdgeSwitchEnabled { get; set; } = true;
    public string ManualAddress { get; set; } = "";
    public string? TrustedPeerId { get; set; }
    public string? MasterId { get; set; }
    public string? MasterName { get; set; }
    public string? ClientSecretName { get; set; }
    public string[] Addresses { get; set; } = [];

    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mousharing");
    public static string? LoadFailure { get; private set; }
    public static Settings Load()
    {
        string path = Path.Combine(DirectoryPath, "settings.json");
        if (!File.Exists(path)) return new();
        try
        {
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? throw new JsonException();
            if (!Guid.TryParse(settings.DeviceId, out _) || string.IsNullOrWhiteSpace(settings.DeviceName) || settings.DeviceName.Length > 128 || settings.Side is not ("left" or "right" or "top" or "bottom")) throw new JsonException();
            settings.Addresses ??= [];
            if (settings.ClientSecretName is not null && (settings.ClientSecretName.Length != 39 || !settings.ClientSecretName.StartsWith("client-", StringComparison.Ordinal) || settings.ClientSecretName[7..].Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))) throw new JsonException();
            if (settings.MasterId is not null && !Guid.TryParse(settings.MasterId,out _) || settings.TrustedPeerId is not null && !Guid.TryParse(settings.TrustedPeerId,out _)) throw new JsonException();
            return settings;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            LoadFailure = "Konfigurasi tersimpan tidak bisa dibaca. File dipertahankan di " + path + ". Pulihkan backup sebelum memakai aplikasi; konfigurasi tidak akan ditimpa.";
            return new();
        }
    }
    public Settings Copy() => JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(this)) ?? throw new InvalidOperationException("Konfigurasi tidak bisa disalin.");
    public void ApplyPairingFrom(Settings candidate)
    {
        MasterId = candidate.MasterId; MasterName = candidate.MasterName; ClientSecretName = candidate.ClientSecretName;
        TrustedPeerId = candidate.TrustedPeerId; Addresses = candidate.Addresses.ToArray(); ManualAddress = candidate.ManualAddress;
    }
    public void Save()
    {
        if (LoadFailure is not null) throw new InvalidOperationException(LoadFailure);
        Directory.CreateDirectory(DirectoryPath);
        string target = Path.Combine(DirectoryPath, "settings.json");
        File.WriteAllText(target + ".tmp", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(target + ".tmp", target, true);
    }
}

public static class SecretStore
{
    private static string FilePath(string name) => Path.Combine(Settings.DirectoryPath, name + ".protected");
    public static byte[]? Read(string name)
    {
        string path = FilePath(name);
        if (!File.Exists(path)) return null;
        try { return ProtectedData.Unprotect(File.ReadAllBytes(path), Encoding.UTF8.GetBytes("mousharing-native-v1"), DataProtectionScope.CurrentUser); }
        catch (Exception e) when (e is IOException or CryptographicException or UnauthorizedAccessException) { throw new InvalidOperationException("Kunci pairing tersimpan tidak bisa dibuka oleh akun Windows ini. File tetap dipertahankan; pulihkan backup atau hapus pairing secara sengaja.", e); }
    }
    public static void Write(string name, byte[] value)
    {
        Directory.CreateDirectory(Settings.DirectoryPath);
        if (Settings.LoadFailure is not null) throw new InvalidOperationException(Settings.LoadFailure);
        string target = FilePath(name), candidate = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(candidate, ProtectedData.Protect(value, Encoding.UTF8.GetBytes("mousharing-native-v1"), DataProtectionScope.CurrentUser));
            File.Move(candidate, target, true);
        }
        finally { if (File.Exists(candidate)) File.Delete(candidate); }
    }
    public static void Forget(string name) { if (File.Exists(FilePath(name))) File.Delete(FilePath(name)); }
}
