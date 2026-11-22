// aimrig: make a Sinden gun report a chosen screen position, for aim testing without a person.
// The Sinden PC software normally sends the gun its aim over the gun's USB serial port as a 7-byte message
//   AA 28 Xhi Xlo Yhi Ylo BB   (X, Y = fraction of the screen * 32767)
// and the gun then reports that position to Windows as its own (absolute) mouse. aimrig sends ONLY that position
// message (allow-listed below). It never sends button, recoil, calibration or firmware messages.
// The Sinden software must be closed while aimrig holds the port.
//
//   aimrig point <gun 1|2> <x 0..1> <y 0..1> [holdMs]   send the position repeatedly for holdMs (default 1500)
//   aimrig watch [ms]                                   print the guns' raw mouse reports (absolute positions)
//   aimrig probe <gun>                                  send centre once while watching that gun's device
//   aimrig peek <process> <offset hex> <u8|i16|i32|f32> [count]   read values from a process (main module + offset)
// Options (anywhere): --p1 <id> --p2 <id>  text in each gun's USB device id (default VID_16C0&PID_0F02 / VID_16C0&PID_0F01,
// Sinden's defaults for the first two guns). The COM port is the gun's serial interface (<id>&MI_00), looked up each run.
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

static class Rig
{
    public static string P1 = "VID_16C0&PID_0F02", P2 = "VID_16C0&PID_0F01";

    static int Main(string[] args)
    {
        var list = args.ToList();
        for (int i = 0; i < list.Count - 1; i++)
        {
            if (list[i] == "--p1") { P1 = list[i + 1]; list.RemoveRange(i, 2); i--; }
            else if (list[i] == "--p2") { P2 = list[i + 1]; list.RemoveRange(i, 2); i--; }
        }
        var a = list.ToArray();
        if (a.Length == 0) { Console.WriteLine("aimrig point|watch|probe|peek ...  [--p1 <id>] [--p2 <id>]"); return 1; }
        switch (a[0])
        {
            case "point":
            {
                int gun = int.Parse(a[1]); double x = double.Parse(a[2]), y = double.Parse(a[3]);
                int hold = a.Length > 4 ? int.Parse(a[4]) : 1500;
                using var port = SerialPort.Open(ComFor(gun));
                var end = Environment.TickCount64 + hold;
                do { port.Write(Position(x, y)); Thread.Sleep(16); } while (Environment.TickCount64 < end);
                Console.WriteLine($"gun {gun}: sent {x:0.000},{y:0.000} for {hold} ms");
                return 0;
            }
            case "peek":
            {
                // aimrig peek <process> <offsetFromMainModule hex> <i16|i32|u8|f32> [count]
                var p = System.Diagnostics.Process.GetProcessesByName(a[1]).FirstOrDefault() ?? throw new Exception("no process " + a[1]);
                var addr = p.MainModule!.BaseAddress + Convert.ToInt32(a[2], 16);
                int n = a.Length > 4 ? int.Parse(a[4]) : 1, w = a[3] switch { "u8" => 1, "i16" => 2, _ => 4 };
                var buf = new byte[w * n];
                var h = Mem.OpenProcess(0x0010, false, p.Id);
                Mem.ReadProcessMemory(h, addr, buf, buf.Length, out _);
                var vals = Enumerable.Range(0, n).Select(i => a[3] switch
                {
                    "u8" => buf[i].ToString(), "i16" => BitConverter.ToInt16(buf, i * 2).ToString(),
                    "f32" => BitConverter.ToSingle(buf, i * 4).ToString("0.###"), _ => BitConverter.ToInt32(buf, i * 4).ToString()
                });
                Console.WriteLine(string.Join(" ", vals));
                return 0;
            }
            case "watch":
                RawWatch.Run(a.Length > 1 ? int.Parse(a[1]) : 3000, null);
                return 0;
            case "probe":
            {
                int gun = int.Parse(a[1]);
                var com = ComFor(gun);
                Console.WriteLine($"gun {gun} on {com}");
                var t = new Thread(() =>
                {
                    Thread.Sleep(500);
                    using var port = SerialPort.Open(com);
                    foreach (var (x, y) in new[] { (0.5, 0.5), (0.25, 0.25), (0.75, 0.75) })
                    {
                        var end = Environment.TickCount64 + 700;
                        do { port.Write(Position(x, y)); Thread.Sleep(16); } while (Environment.TickCount64 < end);
                        Console.WriteLine($"  sent {x},{y}");
                    }
                });
                t.Start();
                RawWatch.Run(3500, gun == 1 ? P1 : P2);
                t.Join();
                return 0;
            }
        }
        return 1;
    }

    /// <summary>The only message aimrig ever sends.</summary>
    static byte[] Position(double fx, double fy)
    {
        int x = (int)Math.Round(Math.Clamp(fx, 0, 1) * 32767), y = (int)Math.Round(Math.Clamp(fy, 0, 1) * 32767);
        return new byte[] { 0xAA, 0x28, (byte)(x / 256), (byte)(x % 256), (byte)(y / 256), (byte)(y % 256), 0xBB };
    }

    /// <summary>COM port name of the gun's serial interface (MI_00) from the registry.</summary>
    static string ComFor(int gun)
    {
        string id = gun == 1 ? P1 : P2;
        using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB")!;
        foreach (var dev in usb.GetSubKeyNames().Where(n => n.Contains(id + "&MI_00", StringComparison.OrdinalIgnoreCase)))
            using (var d = usb.OpenSubKey(dev)!)
                foreach (var inst in d.GetSubKeyNames())
                    using (var p = d.OpenSubKey(inst + @"\Device Parameters"))
                        if (p?.GetValue("PortName") is string name) return name;
        throw new Exception("COM port not found for gun " + gun);
    }
}

/// <summary>Minimal Win32 serial port (no NuGet package needed). 115200 8N1, like the Sinden software.</summary>
sealed class SerialPort : IDisposable
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetCommState(IntPtr h, ref DCB dcb);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetCommState(IntPtr h, ref DCB dcb);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteFile(IntPtr h, byte[] buf, int n, out int written, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool EscapeCommFunction(IntPtr h, int fn);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    [StructLayout(LayoutKind.Sequential)]
    struct DCB
    {
        public int DCBlength; public int BaudRate; public uint Flags; public ushort wReserved; public ushort XonLim; public ushort XoffLim;
        public byte ByteSize; public byte Parity; public byte StopBits; public byte XonChar; public byte XoffChar; public byte ErrorChar;
        public byte EofChar; public byte EvtChar; public ushort wReserved1;
    }

    IntPtr _h;
    public static SerialPort Open(string com)
    {
        var h = CreateFile(@"\\.\" + com, 0xC0000000, 0, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error(), $"Can't open {com} (is the Sinden software still running?)");
        var dcb = new DCB { DCBlength = Marshal.SizeOf<DCB>() };
        GetCommState(h, ref dcb);
        dcb.BaudRate = 115200; dcb.ByteSize = 8; dcb.Parity = 0; dcb.StopBits = 0;
        dcb.Flags = 1 | (1u << 4); // fBinary, fDtrControl = enable
        SetCommState(h, ref dcb);
        EscapeCommFunction(h, 5); // SETDTR, as .NET SerialPort with DtrEnable does
        return new SerialPort { _h = h };
    }
    public void Write(byte[] b) { if (!WriteFile(_h, b, b.Length, out _, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    public void Dispose() { if (_h != IntPtr.Zero) { CloseHandle(_h); _h = IntPtr.Zero; } }
}

/// <summary>Prints raw mouse input from the two guns (device path contains the --p1 / --p2 id).</summary>
static class RawWatch
{
    public static void Run(int ms, string? onlyPid)
    {
        var f = new Form { ShowInTaskbar = false, Opacity = 0, Width = 1, Height = 1 };
        f.Load += (_, _) =>
        {
            var dev = new[] { new Native.RAWINPUTDEVICE { usUsagePage = 1, usUsage = 2, dwFlags = 0x100 /*INPUTSINK*/, hwndTarget = f.Handle } };
            Native.RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf<Native.RAWINPUTDEVICE>());
            var t = new System.Windows.Forms.Timer { Interval = ms };
            t.Tick += (_, _) => f.Close();
            t.Start();
        };
        var names = new Dictionary<IntPtr, string>();
        var last = new Dictionary<IntPtr, (int, int)>();
        var mf = new MsgFilter(m =>
        {
            if (m.Msg != 0x00FF) return;
            uint size = 0, hdr = (uint)Marshal.SizeOf<Native.RAWINPUTHEADER>();
            Native.GetRawInputData(m.LParam, 0x10000003, IntPtr.Zero, ref size, hdr);
            var buf = Marshal.AllocHGlobal((int)size);
            try
            {
                Native.GetRawInputData(m.LParam, 0x10000003, buf, ref size, hdr);
                var h = Marshal.PtrToStructure<Native.RAWINPUTHEADER>(buf);
                if (h.dwType != 0) return;
                if (!names.TryGetValue(h.hDevice, out var name)) names[h.hDevice] = name = Native.DeviceName(h.hDevice);
                bool isP1 = name.Contains(Rig.P1, StringComparison.OrdinalIgnoreCase);
                if (!isP1 && !name.Contains(Rig.P2, StringComparison.OrdinalIgnoreCase)) return;
                if (onlyPid != null && !name.Contains(onlyPid, StringComparison.OrdinalIgnoreCase)) return;
                var mo = Marshal.PtrToStructure<Native.RAWMOUSE>(buf + (int)hdr);
                bool abs = (mo.usFlags & 1) != 0;
                var pos = (mo.lLastX, mo.lLastY);
                if (last.TryGetValue(h.hDevice, out var lp) && lp == pos && mo.usButtonFlags == 0) return;
                last[h.hDevice] = pos;
                string gun = isP1 ? "P1" : "P2";
                Console.WriteLine($"  {gun} {(abs ? "abs" : "rel")} x={mo.lLastX / 65535.0:0.000} y={mo.lLastY / 65535.0:0.000} buttons=0x{mo.usButtonFlags:X}");
            }
            finally { Marshal.FreeHGlobal(buf); }
        });
        Application.AddMessageFilter(mf);
        Application.Run(f);
        Application.RemoveMessageFilter(mf);
    }

    sealed class MsgFilter(Action<Message> on) : IMessageFilter
    {
        public bool PreFilterMessage(ref Message m) { on(m); return false; }
    }
}

static class Mem
{
    [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] public static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, nint size, out nint read);
}

static class Native
{
    [StructLayout(LayoutKind.Sequential)] public struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }
    [StructLayout(LayoutKind.Sequential)] public struct RAWINPUTHEADER { public uint dwType, dwSize; public IntPtr hDevice, wParam; }
    [StructLayout(LayoutKind.Sequential)]
    public struct RAWMOUSE { public ushort usFlags; public ushort pad; public ushort usButtonFlags, usButtonData; public uint ulRawButtons; public int lLastX, lLastY; public uint ulExtraInformation; }
    [DllImport("user32.dll")] public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, uint n, uint size);
    [DllImport("user32.dll")] public static extern uint GetRawInputData(IntPtr h, uint cmd, IntPtr data, ref uint size, uint hdr);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint GetRawInputDeviceInfo(IntPtr h, uint cmd, System.Text.StringBuilder? data, ref uint size);
    public static string DeviceName(IntPtr h)
    {
        uint len = 0; GetRawInputDeviceInfo(h, 0x20000007, null, ref len);
        var sb = new System.Text.StringBuilder((int)len + 2); GetRawInputDeviceInfo(h, 0x20000007, sb, ref len);
        return sb.ToString();
    }
}
