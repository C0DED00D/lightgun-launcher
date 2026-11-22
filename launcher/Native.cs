using System.Runtime.InteropServices;
using System.Text;

namespace LightgunLauncher;

static class Native
{
    public const int WM_INPUT = 0x00FF;
    public const int WM_INPUT_DEVICE_CHANGE = 0x00FE;
    public const uint RIDEV_DEVNOTIFY = 0x00002000;
    public const int WH_KEYBOARD_LL = 13;
    public const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    public const uint RIM_TYPEMOUSE = 0, RIM_TYPEKEYBOARD = 1;
    public const uint RIDI_DEVICENAME = 0x20000007;
    public const uint RID_INPUT = 0x10000003;
    public const uint RIDEV_INPUTSINK = 0x00000100;
    public const ushort MOUSE_MOVE_ABSOLUTE = 0x01, MOUSE_VIRTUAL_DESKTOP = 0x02;
    public const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    public const long WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000, WS_BORDER = 0x00800000,
        WS_DLGFRAME = 0x00400000, WS_SYSMENU = 0x00080000, WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000;
    public const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80,
        WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x8;
    public static readonly IntPtr HWND_TOPMOST = new(-1), HWND_NOTOPMOST = new(-2);
    public const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOZORDER = 4,SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20, SWP_SHOWWINDOW = 0x40;

    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICELIST { public IntPtr hDevice; public uint dwType; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTHEADER { public uint dwType, dwSize; public IntPtr hDevice, wParam; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWMOUSE
    {
        public ushort usFlags;
        public ushort pad;
        public ushort usButtonFlags, usButtonData;
        public uint ulRawButtons;
        public int lLastX, lLastY;
        public uint ulExtraInformation;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[]? list, ref uint count, uint size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint command, StringBuilder? data, ref uint size);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
    [DllImport("user32.dll")]
    public static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data, ref uint size, uint headerSize);

    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern IntPtr CreateCursor(IntPtr hInst, int xHot, int yHot, int w, int h, byte[] andMask, byte[] xorMask);
    [DllImport("user32.dll")] public static extern bool SetSystemCursor(IntPtr hcur, uint id);
    [DllImport("user32.dll")] public static extern bool SystemParametersInfo(uint action, uint param, IntPtr pv, uint winIni);

    // Standard pointer shapes (arrow, I-beam, busy, cross, resize, hand, ...).
    static readonly uint[] CursorIds = { 32512, 32513, 32514, 32515, 32516, 32642, 32643, 32644, 32645, 32646, 32648, 32649, 32650, 32651 };

    /// <summary>Make the mouse pointer invisible everywhere (the guns move it while aiming).
    /// Only lasts until RestoreSystemCursors or the next sign-in; nothing is saved.</summary>
    public static void HideSystemCursors()
    {
        var and = Enumerable.Repeat((byte)0xFF, 32 * 32 / 8).ToArray(); // transparent
        var xor = new byte[32 * 32 / 8];
        foreach (var id in CursorIds)
            SetSystemCursor(CreateCursor(IntPtr.Zero, 0, 0, 32, 32, and, xor), id); // takes ownership of the cursor
    }

    /// <summary>Reload the user's normal pointers (SPI_SETCURSORS reads them back without changing anything).</summary>
    public static void RestoreSystemCursors() => SystemParametersInfo(0x57, 0, IntPtr.Zero, 0);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern long GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern long SetWindowLongPtr(IntPtr h, int i, long v);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>Bring a window to the front even when Windows' focus rules would refuse
    /// (a short Alt tap counts as user input, which lifts the restriction).</summary>
    public static void ForceForeground(IntPtr h)
    {
        if (GetForegroundWindow() == h) return;
        keybd_event(0x12, 0, 0, UIntPtr.Zero);
        SetForegroundWindow(h);
        keybd_event(0x12, 0, 2, UIntPtr.Zero);
    }
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    public const int SW_RESTORE = 9;
    [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr GetMenu(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetMenu(IntPtr h, IntPtr menu);
    [DllImport("user32.dll")] public static extern bool MapWindowPoints(IntPtr from, IntPtr to, ref RECT r, int n);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);

    /// <summary>
    /// Ask a program to close normally by sending WM_CLOSE to its top-level windows, hidden ones included.
    /// Tray programs like Sinden have no visible main window, so Process.CloseMainWindow does nothing for them.
    /// </summary>
    public static int PostCloseToProcessWindows(int pid, string? title = null)
    {
        int sent = 0;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint p);
            if (p != pid) return true;
            if (title != null)
            {
                var sb = new StringBuilder(256);
                GetWindowText(h, sb, sb.Capacity);
                if (sb.ToString() != title) return true;
            }
            if (PostMessage(h, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero)) sent++;
            return true;
        }, IntPtr.Zero);
        return sent;
    }
}

/// <summary>One raw-input device (mouse or keyboard) as Windows lists it.</summary>
record RawDevice(IntPtr Handle, uint Type, string Path);

static class RawDevices
{
    public static List<RawDevice> List()
    {
        uint n = 0, sz = (uint)Marshal.SizeOf<Native.RAWINPUTDEVICELIST>();
        Native.GetRawInputDeviceList(null, ref n, sz);
        var arr = new Native.RAWINPUTDEVICELIST[n];
        Native.GetRawInputDeviceList(arr, ref n, sz);
        var list = new List<RawDevice>();
        foreach (var d in arr.Take((int)n))
        {
            uint len = 0;
            Native.GetRawInputDeviceInfo(d.hDevice, Native.RIDI_DEVICENAME, null, ref len);
            var sb = new StringBuilder((int)len + 2);
            Native.GetRawInputDeviceInfo(d.hDevice, Native.RIDI_DEVICENAME, sb, ref len);
            list.Add(new RawDevice(d.hDevice, d.dwType, sb.ToString()));
        }
        return list;
    }
}

/// <summary>Where each Sinden gun shows up right now (paths and mouse numbers change when guns are re-plugged).</summary>
class GunDevices
{
    public RawDevice? P1Mouse, P2Mouse, P1Keyboard, P2Keyboard;
    /// <summary>
    /// 1-based mouse numbers as Supermodel counts them ({P1MOUSE}/{P2MOUSE}, only used by Supermodel): it walks
    /// Windows' raw device list backwards and skips remote-desktop mice.
    /// </summary>
    public int P1MouseIndex, P2MouseIndex;

    public static GunDevices Detect(string p1Id, string p2Id)
    {
        var all = RawDevices.List();
        var mice = all.Where(d => d.Type == Native.RIM_TYPEMOUSE).ToList();
        var kbds = all.Where(d => d.Type == Native.RIM_TYPEKEYBOARD).ToList();
        var g = new GunDevices
        {
            P1Mouse = mice.FirstOrDefault(m => Has(m, p1Id)),
            P2Mouse = mice.FirstOrDefault(m => Has(m, p2Id)),
            P1Keyboard = kbds.FirstOrDefault(m => Has(m, p1Id)),
            P2Keyboard = kbds.FirstOrDefault(m => Has(m, p2Id)),
        };
        var supermodelMice = mice.Where(m => !m.Path.Contains("Root#RDP_", StringComparison.OrdinalIgnoreCase)).Reverse().ToList();
        g.P1MouseIndex = g.P1Mouse == null ? 1 : supermodelMice.IndexOf(g.P1Mouse) + 1;
        g.P2MouseIndex = g.P2Mouse == null ? 2 : supermodelMice.IndexOf(g.P2Mouse) + 1;
        return g;
    }

    static bool Has(RawDevice d, string id) => d.Path.Contains(id, StringComparison.OrdinalIgnoreCase);
}
