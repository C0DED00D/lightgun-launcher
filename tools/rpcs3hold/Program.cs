// rpcs3hold: keeps values in an RPCS3 game's memory topped up while it runs (used for infinite credits).
//   rpcs3hold <guard addr>=<guard value> <addr>=<value> [<addr>=<value> ...]
// All numbers are hex; addresses are PS3 (guest) addresses, values are big-endian 32-bit.
// It only writes while the guard word matches (for example the loaded program's ELF entry point), so it never
// touches another program on the same disc. Waits up to 3 minutes for RPCS3, exits when RPCS3 exits.
using System.Diagnostics;
using System.Runtime.InteropServices;

static class Hold
{
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint a, bool i, int pid);
    [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, nint size, out nint read);
    [DllImport("kernel32.dll")] static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, nint size, out nint written);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    const long Base = 0x300000000, Sudo = 0x400000000; // vm::g_base_addr / vm::g_sudo_addr (see RPCS3.log)

    static (long addr, uint value) Pair(string s)
    {
        var p = s.Split('=');
        return (Convert.ToInt64(p[0], 16), Convert.ToUInt32(p[1], 16));
    }

    static int Main(string[] a)
    {
        if (a.Length < 2) { Console.WriteLine("rpcs3hold <guard>=<value> <addr>=<value> ..."); return 1; }
        var guard = Pair(a[0]);
        var targets = a.Skip(1).Select(Pair).ToArray();

        Process? p = null;
        var giveUp = DateTime.UtcNow.AddMinutes(3);
        while (p == null && DateTime.UtcNow < giveUp)
        {
            p = Process.GetProcessesByName("rpcs3").FirstOrDefault();
            if (p == null) Thread.Sleep(1000);
        }
        if (p == null) return 2;

        var h = OpenProcess(0x0010 | 0x0020 | 0x0008 | 0x0400 | 0x00100000, false, p.Id);
        var buf = new byte[4];
        uint Read(long guest) => ReadProcessMemory(h, (IntPtr)(Base + guest), buf, 4, out var n) && n == 4
            ? (uint)(buf[0] << 24 | buf[1] << 16 | buf[2] << 8 | buf[3]) : 0xFFFFFFFF;
        try
        {
            while (!p.HasExited)
            {
                if (Read(guard.addr) == guard.value)
                    foreach (var (addr, value) in targets)
                        if (Read(addr) != value)
                        {
                            var b = new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };
                            WriteProcessMemory(h, (IntPtr)(Sudo + addr), b, 4, out _);
                        }
                Thread.Sleep(500);
            }
        }
        finally { CloseHandle(h); }
        return 0;
    }
}
