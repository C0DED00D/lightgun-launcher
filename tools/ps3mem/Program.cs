// ps3mem: scan/read/write the emulated PS3's memory inside a running RPCS3 (values are big-endian).
// RPCS3 maps guest memory at host 0x300000000 (vm::g_base_addr) and a write-anywhere view at 0x400000000
// (vm::g_sudo_addr); both are printed at the top of RPCS3.log.
//   ps3mem scan <u8|u16|u32> <value>   new scan of all mapped guest memory, keeps matches in ps3mem.cand
//   ps3mem next <value>                 keep candidates that now hold <value>
//   ps3mem list                         show candidates (first 50)
//   ps3mem read <guestaddr hex> [count]
//   ps3mem write <u8|u16|u32> <guestaddr hex> <value>
using System.Diagnostics;
using System.Runtime.InteropServices;

static class P
{
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint a, bool i, int pid);
    [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, nint size, out nint read);
    [DllImport("kernel32.dll")] static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, nint size, out nint written);
    [DllImport("kernel32.dll")] static extern nint VirtualQueryEx(IntPtr h, IntPtr addr, out MBI info, nint len);

    [StructLayout(LayoutKind.Sequential)]
    struct MBI { public IntPtr BaseAddress, AllocationBase; public uint AllocationProtect; public ushort PartitionId; public nint RegionSize; public uint State, Protect, Type; }

    const long Base = 0x300000000, Sudo = 0x400000000, Span = 0x100000000;
    static string CandFile => Path.Combine(AppContext.BaseDirectory, "ps3mem.cand");

    static int Main(string[] a)
    {
        var p = Process.GetProcessesByName("rpcs3").FirstOrDefault();
        if (p == null) { Console.WriteLine("RPCS3 not running"); return 1; }
        var h = OpenProcess(0x0010 | 0x0020 | 0x0008 | 0x0400, false, p.Id);
        int Size(string t) => t == "u8" ? 1 : t == "u16" ? 2 : 4;
        long Num(string s) => s.StartsWith("0x") ? Convert.ToInt64(s[2..], 16) : long.Parse(s);
        static long Get(byte[] b, int i, int size) => size switch { 1 => b[i], 2 => (b[i] << 8) | b[i + 1], _ => ((long)b[i] << 24) | ((long)b[i + 1] << 16) | ((long)b[i + 2] << 8) | b[i + 3] };
        byte[] Read(long guest, int n) { var b = new byte[n]; ReadProcessMemory(h, (IntPtr)(Base + guest), b, n, out _); return b; }

        // Committed, readable regions inside the guest window.
        IEnumerable<(long start, long size)> Regions()
        {
            long addr = Base;
            while (addr < Base + Span && VirtualQueryEx(h, (IntPtr)addr, out var m, Marshal.SizeOf<MBI>()) != 0)
            {
                long rs = (long)m.BaseAddress, rsz = m.RegionSize;
                bool readable = m.State == 0x1000 && (m.Protect & 0x66) != 0 && (m.Protect & 0x100) == 0; // committed, R/RW/XR/XRW, not guard
                if (readable) yield return (rs - Base, rsz);
                addr = rs + rsz;
            }
        }

        switch (a[0])
        {
            case "find":
            {
                // ps3mem find <hex bytes> [maxaddr hex]: every guest address where this byte string occurs
                var pat = Convert.FromHexString(a[1]); long max = a.Length > 2 ? Convert.ToInt64(a[2], 16) : Span; int hits = 0;
                foreach (var (s, z) in Regions())
                {
                    if (s >= max) break;
                    var b = Read(s, (int)z);
                    for (int i = 0; i + pat.Length <= b.Length; i++)
                        if (b.AsSpan(i, pat.Length).SequenceEqual(pat)) { Console.WriteLine($"  {s + i:X8}"); if (++hits >= 40) return 0; }
                }
                Console.WriteLine($"{hits} hits"); break;
            }
            case "insn":
            {
                // ps3mem insn <low16 hex> [from hex] [to hex]: 32-bit words in code whose low 16 bits match (D-form displacement)
                int low = Convert.ToInt32(a[1], 16); long from = a.Length > 2 ? Convert.ToInt64(a[2], 16) : 0x10000, to = a.Length > 3 ? Convert.ToInt64(a[3], 16) : 0x640000;
                var b = Read(from, (int)(to - from)); int hits = 0;
                for (int i = 0; i + 4 <= b.Length; i += 4)
                {
                    uint w = (uint)Get(b, i, 4); uint op = w >> 26;
                    if ((w & 0xFFFF) == low && ((op >= 32 && op <= 47) || op == 14 || op == 24 || op == 58 || op == 62)) { Console.WriteLine($"  {from + i:X8}: {w:X8} op{op} rT{(w >> 21) & 31} rA{(w >> 16) & 31}"); if (++hits > 60) break; }
                }
                break;
            }
            case "regions":
                foreach (var (s, z) in Regions()) Console.WriteLine($"{s:X8} +{z:X}");
                break;
            case "scan":
            {
                int size = Size(a[1]); long v = Num(a[2]); var list = new List<long>();
                foreach (var (s, z) in Regions())
                    for (long off = 0; off < z; off += 0x1000000)
                    {
                        int n = (int)Math.Min(0x1000000, z - off); var b = Read(s + off, n);
                        for (int i = 0; i + size <= n; i += size) if (Get(b, i, size) == v) list.Add(s + off + i);
                    }
                File.WriteAllLines(CandFile, new[] { size.ToString() }.Concat(list.Select(x => x.ToString("X"))));
                Console.WriteLine($"{list.Count} matches"); break;
            }
            case "next":
            {
                var lines = File.ReadAllLines(CandFile); int size = int.Parse(lines[0]); long v = Num(a[1]);
                var list = lines.Skip(1).Select(x => Convert.ToInt64(x, 16)).Where(x => Get(Read(x, size), 0, size) == v).ToList();
                File.WriteAllLines(CandFile, new[] { size.ToString() }.Concat(list.Select(x => x.ToString("X"))));
                Console.WriteLine($"{list.Count} matches");
                foreach (var x in list.Take(30)) Console.WriteLine($"  {x:X8}");
                break;
            }
            case "list":
            {
                var lines = File.ReadAllLines(CandFile); int size = int.Parse(lines[0]);
                foreach (var x in lines.Skip(1).Take(50)) { long ad = Convert.ToInt64(x, 16); Console.WriteLine($"{ad:X8} = {Get(Read(ad, size), 0, size)}"); }
                break;
            }
            case "read":
            {
                long ad = Convert.ToInt64(a[1], 16); int n = a.Length > 2 ? int.Parse(a[2]) : 64; var b = Read(ad, n);
                for (int i = 0; i < n; i += 16) Console.WriteLine($"{ad + i:X8}: " + string.Join(" ", b.Skip(i).Take(16).Select(x => x.ToString("X2"))));
                break;
            }
            case "write":
            {
                int size = Size(a[1]); long ad = Convert.ToInt64(a[2], 16); long v = Num(a[3]);
                var b = BitConverter.GetBytes(v).Take(size).Reverse().ToArray();
                WriteProcessMemory(h, (IntPtr)(Sudo + ad), b, size, out _);
                Console.WriteLine("ok"); break;
            }
        }
        return 0;
    }
}
