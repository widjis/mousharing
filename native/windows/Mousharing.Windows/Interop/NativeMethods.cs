using System.Runtime.InteropServices;

namespace Mousharing.Windows.Interop;

internal static class NativeMethods
{
    public const uint WM_INPUT = 0x00FF, WM_QUIT = 0x0012, WM_APP = 0x8001, WM_SETCURSOR = 0x0020;
    public const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    public delegate nint HookProc(int code, nint message, nint data);
    public delegate nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct MSG { public nint hwnd; public uint message; public nuint wParam; public nint lParam; public uint time; public POINT pt; public uint lPrivate; }
    [StructLayout(LayoutKind.Sequential)] public struct MouseHook { public POINT point; public uint mouseData, flags, time; public nuint extra; }
    [StructLayout(LayoutKind.Sequential)] public struct KeyHook { public uint vkCode, scanCode, flags, time; public nuint extra; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] public struct WindowClass { public uint style; public WindowProc proc; public int clsExtra, wndExtra; public nint instance, icon, cursor, background; public string? menu; public string name; }
    [StructLayout(LayoutKind.Sequential)] public struct RawDevice { public ushort page, usage; public uint flags; public nint target; }
    [StructLayout(LayoutKind.Sequential)] public struct RawHeader { public uint type, size; public nint device; public nuint wParam; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] public struct RawMouse { [FieldOffset(0)] public ushort flags; [FieldOffset(4)] public uint buttons; [FieldOffset(8)] public uint rawButtons; [FieldOffset(12)] public int x; [FieldOffset(16)] public int y; [FieldOffset(20)] public uint extra; }
    [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public InputUnion data; }
    [StructLayout(LayoutKind.Explicit)] public struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mouse; [FieldOffset(0)] public KEYBDINPUT keyboard; }
    [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy; public uint mouseData, flags, time; public nuint extra; }
    [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public nuint extra; }
    [DllImport("user32.dll", SetLastError = true)] public static extern nint SetWindowsHookEx(int id, HookProc proc, nint module, uint thread);
    [DllImport("user32.dll")] public static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] public static extern int GetMessage(out MSG msg, nint window, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] public static extern nint DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] public static extern bool PostThreadMessage(uint thread, uint message, nuint wParam, nint lParam);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern ushort RegisterClass(ref WindowClass cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern nint CreateWindowEx(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] public static extern nint DefWindowProc(nint h, uint m, nint w, nint l);
    [DllImport("user32.dll")] public static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(nint hwnd, uint color, byte alpha, uint flags);
    [DllImport("user32.dll")] public static extern nint SetCursor(nint cursor);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterRawInputDevices(RawDevice[] devices, uint count, uint size);
    [DllImport("user32.dll")] public static extern uint GetRawInputData(nint input, uint command, nint data, ref uint size, uint headerSize);
    [DllImport("user32.dll")] public static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
}
