// ps2mem: read/scan/write the emulated PS2's main RAM (32 MB) inside a running PCSX2.
// PCSX2 exports "EEmem", a pointer to that RAM, for debugging tools.
//   ps2mem scan <u8|u16|u32> <value>     new scan, keeps matches in ps2mem.cand
//   ps2mem next <value>                   keep candidates that now hold <value>
//   ps2mem list                           show candidates (first 50)
//   ps2mem read <addr> [count]            dump bytes (addr is a PS2 address, hex)
//   ps2mem write <u8|u16|u32> <addr> <value>
using System.Diagnostics;
using System.Runtime.InteropServices;

static class P
{
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint a, bool i, int pid);
    [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, nint size, out nint read);
    [DllImport("kernel32.dll")] static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, nint size, out nint written);

    const int RamSize = 32 * 1024 * 1024;
    static string CandFile => Path.Combine(AppContext.BaseDirectory, "ps2mem.cand");

    static int Main(string[] a)
    {
        var p = Process.GetProcessesByName("pcsx2-qt").FirstOrDefault();
        if (p == null) { Console.WriteLine("PCSX2 not running"); return 1; }
        var h = OpenProcess(0x0010 | 0x0020 | 0x0008 | 0x0400, false, p.Id);
        var mod = p.MainModule!;
        long rva = ExportRva(mod.FileName, "EEmem");
        var ptr = new byte[8];
        ReadProcessMemory(h, mod.BaseAddress + (nint)rva, ptr, 8, out _);
        var ram = (IntPtr)BitConverter.ToInt64(ptr);
        if (a.Length == 0) { Console.WriteLine($"EEmem = 0x{(long)ram:X}"); return 0; }

        byte[] Mem() { var b = new byte[RamSize]; ReadProcessMemory(h, ram, b, RamSize, out _); return b; }
        static long Get(byte[] m, int addr, int size) => size switch { 1 => m[addr], 2 => BitConverter.ToUInt16(m, addr), _ => BitConverter.ToUInt32(m, addr) };
        int Size(string t) => t == "u8" ? 1 : t == "u16" ? 2 : 4;
        long Num(string s) => s.StartsWith("0x") ? Convert.ToInt64(s[2..], 16) : long.Parse(s);

        switch (a[0])
        {
            case "scan":
            {
                int size = Size(a[1]); long v = Num(a[2]); var m = Mem();
                var list = new List<int>();
                for (int i = 0; i + size <= RamSize; i += size) if (Get(m, i, size) == v) list.Add(i);
                File.WriteAllLines(CandFile, new[] { size.ToString() }.Concat(list.Select(x => x.ToString("X"))));
                Console.WriteLine($"{list.Count} matches"); break;
            }
            case "next":
            {
                var lines = File.ReadAllLines(CandFile); int size = int.Parse(lines[0]); long v = Num(a[1]); var m = Mem();
                var list = lines.Skip(1).Select(x => Convert.ToInt32(x, 16)).Where(x => Get(m, x, size) == v).ToList();
                File.WriteAllLines(CandFile, new[] { size.ToString() }.Concat(list.Select(x => x.ToString("X"))));
                Console.WriteLine($"{list.Count} matches");
                foreach (var x in list.Take(30)) Console.WriteLine($"  {x:X8}");
                break;
            }
            case "list":
            {
                var lines = File.ReadAllLines(CandFile); int size = int.Parse(lines[0]); var m = Mem();
                foreach (var x in lines.Skip(1).Take(50)) { int ad = Convert.ToInt32(x, 16); Console.WriteLine($"{ad:X8} = {Get(m, ad, size)}"); }
                break;
            }
            case "read":
            {
                int ad = Convert.ToInt32(a[1], 16) & 0x1FFFFFF; int n = a.Length > 2 ? int.Parse(a[2]) : 64; var m = Mem();
                for (int i = 0; i < n; i += 16)
                    Console.WriteLine($"{ad + i:X8}: " + string.Join(" ", Enumerable.Range(0, Math.Min(16, n - i)).Select(k => m[ad + i + k].ToString("X2"))));
                break;
            }
            case "dump":
                File.WriteAllBytes(Path.Combine(AppContext.BaseDirectory, a[1] + ".bin"), Mem());
                Console.WriteLine("saved"); break;
            case "delta":
            {
                // ps2mem delta <dumpname> <+1|-1> : 32-bit words (small values) that changed by exactly that amount since the dump
                var old = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, a[1] + ".bin")); var m = Mem(); int d = int.Parse(a[2]);
                var list = new List<int>();
                for (int i = 0; i < RamSize; i += 4)
                {
                    uint o = BitConverter.ToUInt32(old, i), n = BitConverter.ToUInt32(m, i);
                    if (o < 20 && n < 20 && (int)n - (int)o == d && (a.Length < 4 || o == uint.Parse(a[3]))) list.Add(i);
                }
                File.WriteAllLines(CandFile, new[] { "4" }.Concat(list.Select(x => x.ToString("X"))));
                Console.WriteLine($"{list.Count} matches");
                foreach (var x in list.Take(40)) Console.WriteLine($"  {x:X8}: {BitConverter.ToUInt32(old, x)} -> {BitConverter.ToUInt32(m, x)}");
                break;
            }
            case "write":
            {
                int size = Size(a[1]); int ad = Convert.ToInt32(a[2], 16) & 0x1FFFFFF; long v = Num(a[3]);
                var b = BitConverter.GetBytes(v).Take(size).ToArray();
                WriteProcessMemory(h, ram + ad, b, size, out _);
                Console.WriteLine("ok"); break;
            }
        }
        return 0;
    }

    /// <summary>Find an exported symbol's RVA by reading the PE export table from disk.</summary>
    static long ExportRva(string file, string name)
    {
        var b = File.ReadAllBytes(file);
        int pe = BitConverter.ToInt32(b, 0x3C);
        int opt = pe + 24;
        int nsec = BitConverter.ToUInt16(b, pe + 6), optSize = BitConverter.ToUInt16(b, pe + 20);
        int expRva = BitConverter.ToInt32(b, opt + 112); // PE32+ data directory 0
        int secs = opt + optSize;
        int Off(int rva)
        {
            for (int i = 0; i < nsec; i++)
            {
                int s = secs + i * 40; int va = BitConverter.ToInt32(b, s + 12), sz = BitConverter.ToInt32(b, s + 8), raw = BitConverter.ToInt32(b, s + 20);
                if (rva >= va && rva < va + sz) return rva - va + raw;
            }
            throw new Exception("rva");
        }
        int e = Off(expRva);
        int nNames = BitConverter.ToInt32(b, e + 24);
        int funcs = Off(BitConverter.ToInt32(b, e + 28)), names = Off(BitConverter.ToInt32(b, e + 32)), ords = Off(BitConverter.ToInt32(b, e + 36));
        for (int i = 0; i < nNames; i++)
        {
            int no = Off(BitConverter.ToInt32(b, names + i * 4)); int end = Array.IndexOf(b, (byte)0, no);
            if (System.Text.Encoding.ASCII.GetString(b, no, end - no) == name)
                return BitConverter.ToInt32(b, funcs + BitConverter.ToUInt16(b, ords + i * 2) * 4);
        }
        throw new Exception("export not found: " + name);
    }
}
