using System.Runtime.InteropServices;
using static Mousharing.Windows.Interop.NativeMethods;

namespace Mousharing.Windows.Interop;

public sealed class TrayIcon : IDisposable
{
    private readonly WindowProc procedure;
    private nint window;
    private NotifyData data;
    public Action? Open;
    public Action? Pause;
    public Action? Exit;
    public Func<bool>? IsPaused;
    public TrayIcon()
    {
        procedure = WindowMessage;
        string name = "MousharingTray" + Environment.ProcessId;
        var cls = new WindowClass { proc = procedure, instance = GetModuleHandle(null), name = name };
        RegisterClass(ref cls);
        window = CreateWindowEx(0, name, "Mousharing", 0, 0, 0, 0, 0, 0, 0, cls.instance, 0);
        data = new() { size = (uint)Marshal.SizeOf<NotifyData>(), window = window, id = 1, flags = 1 | 2 | 4, callback = 0x8002, icon = LoadIcon(0, (nint)32512), tip = "Mousharing · Tidak terhubung", info = "", infoTitle = "" };
        Shell_NotifyIcon(0, ref data);
    }
    public void Update(string text)
    {
        data.tip = ("Mousharing · " + text)[..Math.Min(127, ("Mousharing · " + text).Length)];
        Shell_NotifyIcon(1, ref data);
    }
    private nint WindowMessage(nint h, uint message, nint w, nint l)
    {
        if (message == 0x8002)
        {
            int action = (int)l & 0xFFFF;
            if (action is 0x202 or 0x203) Open?.Invoke();
            if (action == 0x205)
            {
                nint menu = CreatePopupMenu();
                AppendMenu(menu, 0, 1, "Buka Mousharing"); AppendMenu(menu, 0, 2, IsPaused?.Invoke() == true ? "Lanjutkan berbagi" : "Jeda berbagi"); AppendMenu(menu, 0x800, 0, ""); AppendMenu(menu, 0, 3, "Keluar");
                GetCursorPos(out var point); SetForegroundWindow(window);
                int choice = TrackPopupMenu(menu, 0x0100 | 0x0002, point.X, point.Y, 0, window, 0); DestroyMenu(menu);
                if (choice == 1) Open?.Invoke(); else if (choice == 2) Pause?.Invoke(); else if (choice == 3) Exit?.Invoke();
            }
            return 0;
        }
        return DefWindowProc(h, message, w, l);
    }
    public void Dispose() { if (window == 0) return; Shell_NotifyIcon(2, ref data); DestroyWindow(window); window = 0; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NotifyData
    {
        public uint size; public nint window; public uint id, flags, callback; public nint icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string tip;
        public uint state, stateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string info;
        public uint timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string infoTitle;
        public uint infoFlags; public Guid guid; public nint balloon;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NotifyData data);
    [DllImport("user32.dll")] private static extern nint LoadIcon(nint module, nint resource);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string text);
    [DllImport("user32.dll")] private static extern int TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
}
