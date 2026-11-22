// mempatch: patch a few bytes of a running program's code, in memory only (the exe on disk is untouched).
// Usage: mempatch <process name> <offset>=<expected hex>:<new hex> [...]
//   offset is relative to the main module (hex). A patch is only written when the current bytes equal <expected>,
//   so a different emulator build is never touched. Waits up to 60 s for the process, exits after patching.
// Typical use: a games.json helper that redirects an emulator code path a gun hook doesn't cover
// (see docs\tools.md).
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

static class P
{
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out IntPtr read);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out IntPtr written);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool VirtualProtectEx(IntPtr h, IntPtr addr, UIntPtr size, uint prot, out uint old);
    [DllImport("kernel32.dll")] static extern bool FlushInstructionCache(IntPtr h, IntPtr addr, UIntPtr size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("psapi.dll", SetLastError = true)] static extern bool EnumProcessModulesEx(IntPtr h, IntPtr[] mods, int cb, out int needed, uint filter);

    static string Log = Path.Combine(AppContext.BaseDirectory, "mempatch.log");
    static void Say(string s) { Console.WriteLine(s); try { File.AppendAllText(Log, $"{DateTime.Now:HH:mm:ss} {s}\r\n"); } catch { } }

    static byte[] Hex(string s) => Enumerable.Range(0, s.Length / 2).Select(i => byte.Parse(s.Substring(i * 2, 2), NumberStyles.HexNumber)).ToArray();

    static int Main(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("usage: mempatch <process> <offset>=<expected>:<new> ..."); return 2; }
        var patches = args.Skip(1).Select(a =>
        {
            var (off, rest) = (a.Split('=')[0], a.Split('=')[1]);
            return (off: long.Parse(off, NumberStyles.HexNumber), expect: Hex(rest.Split(':')[0]), now: Hex(rest.Split(':')[1]));
        }).ToList();

        Process? p = null;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(60) && (p = Process.GetProcessesByName(args[0]).FirstOrDefault()) == null) Thread.Sleep(250);
        if (p == null) { Say($"{args[0]}: not running"); return 1; }
        Thread.Sleep(1500); // let it finish loading

        var h = OpenProcess(0x0008 | 0x0010 | 0x0020 | 0x0400, false, p.Id); // VM_OPERATION|VM_READ|VM_WRITE|QUERY_INFORMATION
        if (h == IntPtr.Zero) { Say($"open failed ({Marshal.GetLastWin32Error()})"); return 1; }
        try
        {
            var mods = new IntPtr[1];
            if (!EnumProcessModulesEx(h, mods, IntPtr.Size, out _, 0x03)) { Say($"module list failed ({Marshal.GetLastWin32Error()})"); return 1; }
            var baseAddr = mods[0];
            int ok = 0;
            foreach (var (off, expect, now) in patches)
            {
                var addr = (IntPtr)((long)baseAddr + off);
                var cur = new byte[expect.Length];
                ReadProcessMemory(h, addr, cur, cur.Length, out _);
                if (cur.SequenceEqual(now)) { Say($"{args[0]}+{off:X}: already patched"); ok++; continue; }
                if (!cur.SequenceEqual(expect)) { Say($"{args[0]}+{off:X}: found {Convert.ToHexString(cur)}, expected {Convert.ToHexString(expect)} - NOT patched (different build?)"); continue; }
                VirtualProtectEx(h, addr, (UIntPtr)now.Length, 0x40, out uint old); // PAGE_EXECUTE_READWRITE
                bool w = WriteProcessMemory(h, addr, now, now.Length, out _);
                VirtualProtectEx(h, addr, (UIntPtr)now.Length, old, out _);
                FlushInstructionCache(h, addr, (UIntPtr)now.Length);
                Say($"{args[0]}+{off:X}: {Convert.ToHexString(expect)} -> {Convert.ToHexString(now)} {(w ? "ok" : "FAILED " + Marshal.GetLastWin32Error())}");
                if (w) ok++;
            }
            return ok == patches.Count ? 0 : 1;
        }
        finally { CloseHandle(h); }
    }
}
