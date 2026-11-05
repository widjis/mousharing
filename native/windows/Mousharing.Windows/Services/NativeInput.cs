using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Mousharing.Windows.Interop;
using static Mousharing.Windows.Interop.NativeMethods;

namespace Mousharing.Windows.Services;

public sealed class NativeInput : IDisposable
{
    private const nuint InjectionTag = 0x4D534852;
    private readonly ConcurrentQueue<Action> actions = new();
    private Thread? thread;
    private uint threadId;
    private nint mouseHook, keyboardHook, overlay;
    private HookProc? mouseProc, keyProc;
    private WindowProc? windowProc;
    private volatile bool connected, remote, shutdown;
    private readonly HashSet<uint> reservedKeys = [];
    private readonly HashSet<uint> physicalKeys = [];
    private readonly HashSet<uint> physicalButtons = [];
    private readonly HashSet<string> heldKeys = [];
    private readonly HashSet<string> heldButtons = [];
    private string side = "right";
    private bool edgeSwitch = true;
    private int switchId;
    private long rearmAfter;
    private bool receiving;
    private int receivingSwitch;
    private string receivingSide = "right";
    private double pressure;
    private POINT previous;
    public Action<JsonObject>? Outbound;
    public Action<string>? ControlChanged;
    public Action<string>? Failed;
    public bool Remote => remote;
    public void StartMaster(string screenSide, bool edges)
    {
        side = screenSide; edgeSwitch = edges; shutdown = false;
        if (thread is not null) return;
        thread = new Thread(HookLoop) { IsBackground = true, Name = "Mousharing input" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }
    public void SetConnected(bool value) { connected = value; if (!value) ReturnLocal(); }
    private void Post(Action action)
    {
        actions.Enqueue(action);
        if (threadId != 0) PostThreadMessage(threadId, WM_APP, 0, 0);
    }
    private void HookLoop()
    {
        threadId = GetCurrentThreadId();
        if (shutdown) { threadId = 0; return; }
        windowProc = WindowHandler;
        string clsName = "MousharingCapture" + threadId;
        var cls = new WindowClass { proc = windowProc, instance = GetModuleHandle(null), name = clsName };
        try
        {
            if (RegisterClass(ref cls) == 0) throw new InvalidOperationException("Tidak bisa menyiapkan penangkap input.");
            overlay = CreateWindowEx(0x08080088, clsName, "Mousharing capture", 0x80000000, 0, 0, GetSystemMetrics(0), GetSystemMetrics(1), 0, 0, cls.instance, 0);
            if (overlay == 0 || !SetLayeredWindowAttributes(overlay, 0, 1, 2)) throw new InvalidOperationException("Tidak bisa menyiapkan kursor remote.");
            var raw = new RawDevice { page = 1, usage = 2, flags = 0x100, target = overlay };
            if (!RegisterRawInputDevices([raw], 1, (uint)Marshal.SizeOf<RawDevice>())) throw new InvalidOperationException("Raw mouse tidak tersedia.");
            mouseProc = MouseHandler; keyProc = KeyboardHandler;
            mouseHook = SetWindowsHookEx(WH_MOUSE_LL, mouseProc, cls.instance, 0);
            keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, keyProc, cls.instance, 0);
            if (mouseHook == 0 || keyboardHook == 0) throw new InvalidOperationException("Windows menolak penangkapan input. Tutup aplikasi lain yang mengunci input dan coba kembali.");
            GetCursorPos(out previous);
            while (!shutdown && GetMessage(out var msg, 0, 0, 0) > 0)
            {
                if (msg.message == WM_APP) while (actions.TryDequeue(out var action)) action();
                TranslateMessage(ref msg); DispatchMessage(ref msg);
            }
        }
        catch (Exception e) { Failed?.Invoke(e.Message); }
        finally
        {
            remote = false;
            physicalKeys.Clear(); physicalButtons.Clear(); reservedKeys.Clear();
            if (overlay != 0) { ShowWindow(overlay, 0); DestroyWindow(overlay); overlay = 0; }
            if (mouseHook != 0) UnhookWindowsHookEx(mouseHook);
            if (keyboardHook != 0) UnhookWindowsHookEx(keyboardHook);
            threadId = 0;
        }
    }
    private nint WindowHandler(nint hwnd, uint message, nint w, nint l)
    {
        if (message == WM_SETCURSOR && remote) { SetCursor(0); return 1; }
        if (message == WM_INPUT && remote)
        {
            uint size = 0, header = (uint)Marshal.SizeOf<RawHeader>();
            GetRawInputData(l, 0x10000003, 0, ref size, header);
            if (size >= header + 24 && size <= 1024)
            {
                nint data = Marshal.AllocHGlobal((int)size);
                try
                {
                    if (GetRawInputData(l, 0x10000003, data, ref size, header) == size)
                    {
                        var h = Marshal.PtrToStructure<RawHeader>(data);
                        var mouse = Marshal.PtrToStructure<RawMouse>(data + (int)header);
                        if (h.type == 0 && (mouse.flags & 1) == 0 && (mouse.x != 0 || mouse.y != 0)) Outbound?.Invoke(new() { ["type"] = "move", ["dx"] = mouse.x, ["dy"] = mouse.y, ["edgeSwitch"] = edgeSwitch });
                    }
                }
                finally { Marshal.FreeHGlobal(data); }
            }
        }
        return DefWindowProc(hwnd, message, w, l);
    }
    private nint MouseHandler(int code, nint message, nint data)
    {
        if (code < 0) return CallNextHookEx(mouseHook, code, message, data);
        var mouse = Marshal.PtrToStructure<MouseHook>(data);
        if ((mouse.flags & 1) != 0 || mouse.extra == InjectionTag) return CallNextHookEx(mouseHook, code, message, data);
        int kind = (int)message;
        string? button = kind is 0x201 or 0x202 ? "left" : kind is 0x204 or 0x205 ? "right" : kind is 0x207 or 0x208 ? "middle" : null;
        bool down = kind is 0x201 or 0x204 or 0x207;
        if (button is not null) { if (down) physicalButtons.Add((uint)(kind)); else physicalButtons.Remove((uint)(kind - 1)); }
        if (!connected) return CallNextHookEx(mouseHook, code, message, data);
        if (!remote)
        {
            int dx = mouse.point.X - previous.X, dy = mouse.point.Y - previous.Y; previous = mouse.point;
            if (kind == 0x200 && edgeSwitch && Environment.TickCount64 >= rearmAfter && physicalKeys.Count == 0 && physicalButtons.Count == 0 && Crossed(side, mouse.point.X, mouse.point.Y, dx, dy)) EnterRemote();
            return remote ? 1 : CallNextHookEx(mouseHook, code, message, data);
        }
        SetCursor(0);
        if (button is not null) Outbound?.Invoke(new() { ["type"] = "button", ["name"] = button, ["down"] = down });
        else if (kind is 0x20A or 0x20E) Outbound?.Invoke(new() { ["type"] = "scroll", ["dx"] = kind == 0x20E ? (short)(mouse.mouseData >> 16) / 120 : 0, ["dy"] = kind == 0x20A ? (short)(mouse.mouseData >> 16) / 120 : 0 });
        return 1;
    }
    private nint KeyboardHandler(int code, nint message, nint data)
    {
        if (code < 0) return CallNextHookEx(keyboardHook, code, message, data);
        var key = Marshal.PtrToStructure<KeyHook>(data);
        if ((key.flags & 0x10) != 0 || key.extra == InjectionTag) return CallNextHookEx(keyboardHook, code, message, data);
        bool down = (int)message is 0x100 or 0x104;
        if (key.vkCode is 0x77 or 0x1B && connected)
        {
            bool firstPress = down ? reservedKeys.Add(key.vkCode) : false;
            if (!down) reservedKeys.Remove(key.vkCode);
            if (firstPress) { if (key.vkCode == 0x1B || remote) ReturnLocal(); else if (physicalKeys.Count == 0 && physicalButtons.Count == 0) EnterRemote(); }
            return 1;
        }
        if (down) physicalKeys.Add(key.vkCode); else physicalKeys.Remove(key.vkCode);
        if (!remote || !connected) return CallNextHookEx(keyboardHook, code, message, data);
        string? name = KeyName(key.vkCode);
        if (name is not null) Outbound?.Invoke(new() { ["type"] = "key", ["name"] = name, ["down"] = down });
        return 1;
    }
    private void EnterRemote()
    {
        if (!connected || overlay == 0 || remote) return;
        remote = true; switchId++;
        ShowWindow(overlay, 4); SetCursor(0);
        GetCursorPos(out var cursor);
        Outbound?.Invoke(new() { ["type"] = "enter", ["side"] = side, ["ratio"] = Ratio(side, cursor.X, cursor.Y), ["switchId"] = switchId });
        ControlChanged?.Invoke("Mengontrol perangkat pasangan");
    }
    public void ReturnLocal(double? ratio = null, int? expectedSwitch = null)
    {
        Post(() =>
        {
            if (expectedSwitch.HasValue && expectedSwitch != switchId) return;
            bool wasRemote = remote; remote = false;
            if (overlay != 0) ShowWindow(overlay, 0);
            if (wasRemote)
            {
                var point = Entry(side, ratio ?? .5);
                Warp(point.X, point.Y); previous = point;
                rearmAfter = Environment.TickCount64 + 300;
                Outbound?.Invoke(new() { ["type"] = "reset" });
                ControlChanged?.Invoke("Mengontrol Windows ini");
            }
        });
    }
    public void Receive(JsonObject frame)
    {
        string type = frame["type"]?.GetValue<string>() ?? "";
        if (type == "reset") { receiving = false; ReleaseInjected(); return; }
        if (type == "enter")
        {
            ReleaseInjected(); receivingSide = frame["side"]!.GetValue<string>(); ValidateSide(receivingSide);
            receivingSwitch = frame["switchId"]!.GetValue<int>(); pressure = 0;
            double ratio = frame["ratio"]!.GetValue<double>();
            if (!double.IsFinite(ratio) || ratio < 0 || ratio > 1) throw new InvalidDataException("Posisi layar tidak valid.");
            var point = Entry(Opposite(receivingSide), ratio); Warp(point.X, point.Y); receiving = true; return;
        }
        if (!receiving) return;
        if (type == "move")
        {
            GetCursorPos(out var old);
            int dx = BoundedDelta(frame, "dx", 2000), dy = BoundedDelta(frame, "dy", 2000);
            int x = Math.Clamp(old.X + dx, 0, GetSystemMetrics(0) - 1), y = Math.Clamp(old.Y + dy, 0, GetSystemMetrics(1) - 1);
            Warp(x, y);
            string edge = Opposite(receivingSide);
            if (frame["edgeSwitch"]?.GetValue<bool>() == true && heldKeys.Count == 0 && heldButtons.Count == 0 && Crossed(edge, x, y, dx, dy)) pressure += edge is "left" or "right" ? Math.Abs(dx) : Math.Abs(dy); else pressure = 0;
            if (pressure >= 24) { receiving = false; ReleaseInjected(); Outbound?.Invoke(new() { ["type"] = "leave", ["ratio"] = Ratio(edge, x, y), ["switchId"] = receivingSwitch }); }
        }
        else if (type == "key")
        {
            string name = frame["name"]!.GetValue<string>(); bool down = frame["down"]!.GetValue<bool>();
            ushort? vk = VirtualKey(name); if (vk is null) throw new InvalidDataException("Tombol tidak didukung.");
            InjectKey(vk.Value, down); if (down) heldKeys.Add(name); else heldKeys.Remove(name);
        }
        else if (type == "button")
        {
            string name = frame["name"]!.GetValue<string>(); bool down = frame["down"]!.GetValue<bool>();
            InjectButton(name, down); if (down) heldButtons.Add(name); else heldButtons.Remove(name);
        }
        else if (type == "scroll")
        {
            int dx = BoundedDelta(frame, "dx", 100), dy = BoundedDelta(frame, "dy", 100);
            if (dy != 0) InjectMouse(0, 0, unchecked((uint)(dy * 120)), 0x0800);
            if (dx != 0) InjectMouse(0, 0, unchecked((uint)(dx * 120)), 0x1000);
        }
    }
    public void ReleaseInjected()
    {
        foreach (string key in heldKeys.ToArray()) { var vk = VirtualKey(key); if (vk is not null) InjectKey(vk.Value, false); }
        foreach (string button in heldButtons.ToArray()) InjectButton(button, false);
        heldKeys.Clear(); heldButtons.Clear(); receiving = false;
    }
    private static int BoundedDelta(JsonObject frame, string field, int bound)
    {
        double value = frame[field]?.GetValue<double>() ?? throw new InvalidDataException("Gerakan tidak valid.");
        if (!double.IsFinite(value)) throw new InvalidDataException("Gerakan tidak valid.");
        return (int)Math.Round(Math.Clamp(value, -bound, bound));
    }
    public static string Opposite(string side) => side switch { "left" => "right", "right" => "left", "top" => "bottom", "bottom" => "top", _ => throw new InvalidDataException("Susunan layar tidak valid.") };
    private static void ValidateSide(string value) { _ = Opposite(value); }
    private static bool Crossed(string edge, int x, int y, int dx, int dy) => edge switch { "left" => dx < 0 && x <= 1, "right" => dx > 0 && x >= GetSystemMetrics(0) - 2, "top" => dy < 0 && y <= 1, "bottom" => dy > 0 && y >= GetSystemMetrics(1) - 2, _ => false };
    private static double Ratio(string edge, int x, int y) => Math.Clamp(edge is "left" or "right" ? y / (double)Math.Max(1, GetSystemMetrics(1) - 1) : x / (double)Math.Max(1, GetSystemMetrics(0) - 1), 0, 1);
    private static POINT Entry(string edge, double ratio) => new() { X = edge == "left" ? 12 : edge == "right" ? GetSystemMetrics(0) - 13 : (int)(ratio * (GetSystemMetrics(0) - 1)), Y = edge == "top" ? 12 : edge == "bottom" ? GetSystemMetrics(1) - 13 : (int)(ratio * (GetSystemMetrics(1) - 1)) };
    private static void Warp(int x, int y) => InjectMouse((int)(x * 65535.0 / Math.Max(1, GetSystemMetrics(0) - 1)), (int)(y * 65535.0 / Math.Max(1, GetSystemMetrics(1) - 1)), 0, 0x8001);
    private static void InjectMouse(int x, int y, uint data, uint flags) => SendChecked(new() { type = 0, data = new() { mouse = new() { dx = x, dy = y, mouseData = data, flags = flags, extra = InjectionTag } } });
    private static void InjectKey(ushort vk, bool down) => SendChecked(new() { type = 1, data = new() { keyboard = new() { vk = vk, flags = (down ? 0u : 2u) | (vk is 0xA3 or 0xA5 or >= 0x21 and <= 0x28 or 0x2D or 0x2E or 0x5B or 0x5C ? 1u : 0u), extra = InjectionTag } } });
    private static void InjectButton(string name, bool down) => InjectMouse(0, 0, 0, name switch { "left" => down ? 2u : 4u, "right" => down ? 8u : 16u, "middle" => down ? 32u : 64u, _ => throw new InvalidDataException("Tombol mouse tidak valid.") });
    private static void SendChecked(INPUT input) { if (SendInput(1, [input], Marshal.SizeOf<INPUT>()) != 1) throw new InvalidOperationException("Windows memblokir input. Coba aplikasi biasa dengan tingkat izin yang sama."); }
    private static readonly Dictionary<string, ushort> Special = new() { ["ctrl"] = 0xA2, ["ctrl_r"] = 0xA3, ["shift"] = 0xA0, ["shift_r"] = 0xA1, ["alt"] = 0xA4, ["alt_r"] = 0xA5, ["cmd"] = 0x5B, ["cmd_r"] = 0x5C, ["enter"] = 13, ["tab"] = 9, ["space"] = 32, ["backspace"] = 8, ["esc"] = 27, ["delete"] = 46, ["home"] = 36, ["end"] = 35, ["page_up"] = 33, ["page_down"] = 34, ["left"] = 37, ["right"] = 39, ["up"] = 38, ["down"] = 40, ["caps_lock"] = 20, [";"] = 0xBA, ["="] = 0xBB, [","] = 0xBC, ["-"] = 0xBD, ["."] = 0xBE, ["/"] = 0xBF, ["`"] = 0xC0, ["["] = 0xDB, ["\\"] = 0xDC, ["]"] = 0xDD, ["'"] = 0xDE };
    private static ushort? VirtualKey(string name) { if (Special.TryGetValue(name, out ushort value)) return value; if (name.Length == 1 && char.IsAsciiLetterOrDigit(name[0])) return char.ToUpperInvariant(name[0]); if (name.StartsWith('f') && int.TryParse(name[1..], out int f) && f is >= 1 and <= 12) return (ushort)(0x6F + f); return null; }
    private static string? KeyName(uint vk) { if (vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A) return char.ToLowerInvariant((char)vk).ToString(); if (vk is >= 0x70 and <= 0x7B) return "f" + (vk - 0x6F); return Special.FirstOrDefault(x => x.Value == vk).Key; }
    public void Dispose()
    {
        connected = false; shutdown = true; ReturnLocal();
        if (threadId != 0) PostThreadMessage(threadId, WM_QUIT, 0, 0);
        if (thread is not null && Thread.CurrentThread != thread) thread.Join(1000);
        thread = null;
        ReleaseInjected();
    }
}
