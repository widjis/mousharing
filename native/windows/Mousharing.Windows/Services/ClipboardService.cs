using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Mousharing.Windows.Services;

public sealed class ClipboardService(nint owner, string origin)
{
    public const int Limit = 256 * 1024;
    private uint revision;
    private string? digest;
    private bool baseline;
    private long clock;
    private (long Clock, string Origin) stamp;
    private JsonObject? pending;
    public Action<JsonObject>? Outbound;
    public void Reset() { baseline = false; pending = null; digest = null; clock = 0; stamp = (0, ""); }
    public void Tick()
    {
        if (pending is not null && !Apply(pending)) return;
        uint next = GetClipboardSequenceNumber();
        if (baseline && revision == next) return;
        if (!OpenClipboard(owner)) return;
        string? text = null;
        try
        {
            next = GetClipboardSequenceNumber();
            if (IsClipboardFormatAvailable(13))
            {
                nint handle = GetClipboardData(13);
                nuint bytes = handle == 0 ? 0 : GlobalSize(handle);
                if (bytes > 0 && bytes <= Limit * 2 + 2)
                {
                    nint memory = GlobalLock(handle);
                    if (memory != 0) { try { text = Marshal.PtrToStringUni(memory, (int)bytes / 2)?.Split('\0')[0].Replace("\r\n", "\n"); } finally { GlobalUnlock(handle); } }
                }
            }
        }
        finally { CloseClipboard(); }
        revision = next;
        string? hash = text is null || Encoding.UTF8.GetByteCount(text) > Limit ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        if (!baseline) { baseline = true; digest = hash; return; }
        if (hash is null) { digest = null; return; }
        if (hash == digest) return;
        digest = hash;
        stamp = (++clock, origin);
        Outbound?.Invoke(new() { ["type"] = "clipboard", ["text"] = text, ["clock"] = clock, ["origin"] = origin });
    }
    public void Receive(JsonObject frame)
    {
        string text = frame["text"]?.GetValue<string>() ?? throw new InvalidDataException("Clipboard teks tidak valid.");
        long value = frame["clock"]?.GetValue<long>() ?? 0;
        string sender = frame["origin"]?.GetValue<string>() ?? "";
        if (value < 1 || !Guid.TryParse(sender, out _) || text.Contains('\0') || Encoding.UTF8.GetByteCount(text) > Limit) throw new InvalidDataException("Clipboard tidak valid atau terlalu besar.");
        clock = Math.Max(clock, value);
        if (value < stamp.Clock || value == stamp.Clock && string.CompareOrdinal(sender, stamp.Origin) <= 0) return;
        stamp = (value, sender);
        pending = (JsonObject)frame.DeepClone();
        Apply(pending);
    }
    private bool Apply(JsonObject frame)
    {
        string text = frame["text"]!.GetValue<string>().Replace("\r\n", "\n");
        if (!OpenClipboard(owner)) return false;
        nint memory = 0;
        try
        {
            byte[] data = Encoding.Unicode.GetBytes(text + '\0');
            memory = GlobalAlloc(2, (nuint)data.Length);
            if (memory == 0) return false;
            nint pointer = GlobalLock(memory);
            if (pointer == 0) return false;
            try { Marshal.Copy(data, 0, pointer, data.Length); } finally { GlobalUnlock(memory); }
            if (!EmptyClipboard() || SetClipboardData(13, memory) == 0) return false;
            memory = 0;
            revision = GetClipboardSequenceNumber();
            digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            baseline = true;
            pending = null;
            return true;
        }
        finally { if (memory != 0) GlobalFree(memory); CloseClipboard(); }
    }
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] private static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern nint SetClipboardData(uint format, nint memory);
    [DllImport("kernel32.dll")] private static extern nuint GlobalSize(nint memory);
    [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint flags, nuint size);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint memory);
}
