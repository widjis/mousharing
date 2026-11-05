// Only OS/UI boundaries are replaced: the actual SessionController and encrypted
// TCP implementation run with a UI dispatcher that never drains its queue.
using System.Text.Json.Nodes;
namespace Microsoft.UI.Dispatching
{
    public sealed class DispatcherQueue
    {
        public bool HasThreadAccess => false;
        public bool TryEnqueue(Action action) => true;
    }
}
namespace Mousharing.Windows.Models
{
    public sealed class Settings
    {
        public string DeviceId = Guid.NewGuid().ToString(), DeviceName = "Test", Side = "right", ManualAddress = "";
        public string? MasterId, MasterName, ClientSecretName, TrustedPeerId;
        public string[] Addresses = [];
        public bool IsMaster, ClipboardEnabled = true, EdgeSwitchEnabled = true;
        public static string? LoadFailure => null;
        public void Save() { }
        public Settings Copy() => (Settings)MemberwiseClone();
        public void ApplyPairingFrom(Settings other) { }
    }
    public static class SecretStore
    {
        public static byte[]? Read(string name) => null;
        public static void Write(string name, byte[] key) { }
        public static void Forget(string name) { }
    }
}
namespace Mousharing.Windows.Services
{
    public sealed class NativeInput : IDisposable
    {
        public Action<JsonObject>? Outbound;
        public Action<string>? ControlChanged, Failed;
        public static int Received, Released;
        public void StartMaster(string side, bool edges) { }
        public void SetConnected(bool value) { }
        public void ReturnLocal(double ratio, int id) { }
        public void Receive(JsonObject frame) => Interlocked.Increment(ref Received);
        public void ReleaseInjected() => Interlocked.Increment(ref Released);
        public void Dispose() => ReleaseInjected();
    }
    public sealed class ClipboardService(nint owner, string origin)
    {
        public Action<JsonObject>? Outbound;
        public static int Ticks, Received;
        public void Reset() { }
        public void Tick() => Interlocked.Increment(ref Ticks);
        public void Receive(JsonObject frame) => Interlocked.Increment(ref Received);
    }
}
