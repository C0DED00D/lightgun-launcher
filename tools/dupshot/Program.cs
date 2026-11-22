// dupshot: capture the primary monitor with DXGI desktop duplication (sees OpenGL/flip-model windows that GDI
// screen grabs show as black) and save it as a 32-bit BMP.   dupshot <out.bmp>
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

var outPath = args.Length > 0 ? args[0] : "dupshot.bmp";
D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, null!, out ID3D11Device? dev).CheckError();
using var device = dev!;
using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
using var adapter = dxgiDevice.GetAdapter();
adapter.EnumOutputs(0, out IDXGIOutput? o).CheckError();
using var output1 = o!.QueryInterface<IDXGIOutput1>();
using var dup = output1.DuplicateOutput(device);

// "dupshot fps <seconds>": count the frames presented to the screen (any game, any API) and report the rate,
// the slowest 1 % of frame gaps and the longest gap. Prints one line: "fps 59.9 p99gap 17.2ms maxgap 18.0ms frames 1797".
if (outPath.Equals("fps", StringComparison.OrdinalIgnoreCase))
{
    double secs = args.Length > 1 ? double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 10;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var gaps = new List<double>();
    long frames = 0; double last = -1;
    while (sw.Elapsed.TotalSeconds < secs)
    {
        var r = dup.AcquireNextFrame(250, out var fi, out var fr);
        if (!r.Success) continue;
        if (fi.LastPresentTime != 0 && fi.AccumulatedFrames > 0)
        {
            double now = sw.Elapsed.TotalMilliseconds;
            if (last >= 0) { var g = (now - last) / fi.AccumulatedFrames; for (int k = 0; k < fi.AccumulatedFrames; k++) gaps.Add(g); }
            last = now;
            frames += fi.AccumulatedFrames;
        }
        fr?.Dispose(); dup.ReleaseFrame();
    }
    gaps.Sort();
    double p99 = gaps.Count > 0 ? gaps[Math.Min(gaps.Count - 1, (int)(gaps.Count * 0.99))] : 0;
    double max = gaps.Count > 0 ? gaps[^1] : 0;
    Console.WriteLine(FormattableString.Invariant($"fps {frames / sw.Elapsed.TotalSeconds:0.0} p99gap {p99:0.0}ms maxgap {max:0.0}ms frames {frames}"));
    return 0;
}

// "dupshot motion <seconds> <outDir> <x0> <y0> <x1> <y1>": every presented frame of the region (screen fractions),
// for judging how the picture MOVES (60 presented fps can still look laggy when many frames repeat). Prints one line per
// frame: present time, gap since the previous frame, and how much it changed (mean |diff| 0..255, 0 = repeat of the
// previous picture). Saves the first 48 frames as quarter-size BMPs in outDir (m000.bmp ...).
if (outPath.Equals("motion", StringComparison.OrdinalIgnoreCase))
{
    var ci = System.Globalization.CultureInfo.InvariantCulture;
    double secs = double.Parse(args[1], ci); string dir = args[2];
    double fx0 = double.Parse(args[3], ci), fy0 = double.Parse(args[4], ci), fx1 = double.Parse(args[5], ci), fy1 = double.Parse(args[6], ci);
    bool save = dir != "-";   // "-" = numbers only, no frame files (launcher session checks)
    if (save) Directory.CreateDirectory(dir);
    var od = output1.Description.DesktopCoordinates;
    int sw0 = od.Right - od.Left, sh0 = od.Bottom - od.Top;
    int rx = (int)(fx0 * sw0), ry = (int)(fy0 * sh0), rw = (int)((fx1 - fx0) * sw0) & ~3, rh = (int)((fy1 - fy0) * sh0) & ~3;
    var rdesc = new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)rw, (uint)rh, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read);
    using var region = device.CreateTexture2D(rdesc);
    var ctx = device.ImmediateContext;
    int qw = rw / 4, qh = rh / 4;
    byte[]? prev = null; long prevQpc = 0; int n = 0, repeats = 0;
    double qpcMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var lines = new List<string>();
    while (sw.Elapsed.TotalSeconds < secs)
    {
        var r = dup.AcquireNextFrame(100, out var fi, out var fr);
        if (!r.Success) continue;
        try
        {
            if (fi.LastPresentTime == 0) continue;
            using (var t = fr!.QueryInterface<ID3D11Texture2D>())
                ctx.CopySubresourceRegion(region, 0, 0, 0, 0, t, 0, new Vortice.Mathematics.Box(rx, ry, 0, rx + rw, ry + rh, 1));
            var m = ctx.Map(region, 0, MapMode.Read);
            var q = new byte[qw * qh * 4];
            var row = new byte[rw * 4];
            for (int y = 0; y < qh; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(m.DataPointer + (y * 4) * (nint)m.RowPitch, row, 0, row.Length);
                for (int x = 0; x < qw; x++) Buffer.BlockCopy(row, x * 16, q, (y * qw + x) * 4, 4);
            }
            ctx.Unmap(region, 0);
            double diff = 0;
            if (prev != null) { long s = 0; for (int i = 0; i < q.Length; i += 4) s += Math.Abs(q[i] - prev[i]) + Math.Abs(q[i + 1] - prev[i + 1]) + Math.Abs(q[i + 2] - prev[i + 2]); diff = s / (q.Length / 4.0) / 3.0; }
            double gap = prevQpc != 0 ? (fi.LastPresentTime - prevQpc) * qpcMs : 0;
            if (prev != null && diff < 0.5) repeats++;
            lines.Add(FormattableString.Invariant($"{n,4} gap {gap,6:0.0}ms change {diff,6:0.00}{(fi.AccumulatedFrames > 1 ? $" (+{fi.AccumulatedFrames - 1} skipped)" : "")}"));
            if (save && n < 48)
            {
                using var fs = File.Create(Path.Combine(dir, $"m{n:000}.bmp"));
                using var bw = new BinaryWriter(fs);
                int size = qw * qh * 4;
                bw.Write((ushort)0x4D42); bw.Write(54 + size); bw.Write(0); bw.Write(54);
                bw.Write(40); bw.Write(qw); bw.Write(-qh); bw.Write((ushort)1); bw.Write((ushort)32); bw.Write(0); bw.Write(size); bw.Write(2835); bw.Write(2835); bw.Write(0); bw.Write(0);
                bw.Write(q);
            }
            prev = q; prevQpc = fi.LastPresentTime; n++;
        }
        finally { fr?.Dispose(); dup.ReleaseFrame(); }
    }
    if (save) foreach (var l in lines) Console.WriteLine(l);
    // Longest run of repeated pictures = the worst visible freeze.
    int run = 0, worst = 0;
    foreach (var l in lines) { if (l.Contains("change   0.0") || l.Contains("change   0.1") || l.Contains("change   0.2")) { run++; worst = Math.Max(worst, run); } else run = 0; }
    double secsDone = sw.Elapsed.TotalSeconds;
    Console.WriteLine(FormattableString.Invariant($"motion frames {n} repeats {repeats} ({(n > 1 ? 100.0 * repeats / (n - 1) : 0):0}%) new pictures/s {(n - repeats) / secsDone:0.0} longest freeze {worst} frames"));
    return 0;
}

// "dupshot latency <vkHex> <x0> <y0> <x1> <y1> [count] [reloadVkHex]": input-to-screen delay inside the PC.
// Presses the key (e.g. a gun's test trigger), then times until the region (screen fractions, e.g. the ammo counter)
// visibly changes in a presented frame. After every 5 shots it holds reloadVk (cover/reload) for 1.5 s.
// Prints each delay and "latency median N ms min N max N".
if (outPath.Equals("latency", StringComparison.OrdinalIgnoreCase))
{
    var ci = System.Globalization.CultureInfo.InvariantCulture;
    byte vk = Convert.ToByte(args[1], 16);
    double fx0 = double.Parse(args[2], ci), fy0 = double.Parse(args[3], ci), fx1 = double.Parse(args[4], ci), fy1 = double.Parse(args[5], ci);
    int count = args.Length > 6 ? int.Parse(args[6]) : 15;
    byte reloadVk = args.Length > 7 ? Convert.ToByte(args[7], 16) : (byte)0;
    var odesc = output1.Description.DesktopCoordinates;
    int sw = odesc.Right - odesc.Left, sh = odesc.Bottom - odesc.Top;
    int rx = (int)(fx0 * sw), ry = (int)(fy0 * sh), rw = Math.Max(4, (int)((fx1 - fx0) * sw)), rh = Math.Max(4, (int)((fy1 - fy0) * sh));
    var rdesc = new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)rw, (uint)rh, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read);
    using var region = device.CreateTexture2D(rdesc);
    var ctx = device.ImmediateContext;
    byte[]? Grab(int timeoutMs, out long presentQpc)
    {
        presentQpc = 0;
        var r = dup.AcquireNextFrame((uint)timeoutMs, out var fi, out var fr);
        if (!r.Success) return null;
        try
        {
            if (fi.LastPresentTime == 0) return null;
            presentQpc = fi.LastPresentTime;
            using var t = fr!.QueryInterface<ID3D11Texture2D>();
            ctx.CopySubresourceRegion(region, 0, 0, 0, 0, t, 0, new Vortice.Mathematics.Box(rx, ry, 0, rx + rw, ry + rh, 1));
            var m = ctx.Map(region, 0, MapMode.Read);
            var buf = new byte[rw * rh * 4];
            for (int y = 0; y < rh; y++) System.Runtime.InteropServices.Marshal.Copy(m.DataPointer + y * (nint)m.RowPitch, buf, y * rw * 4, rw * 4);
            ctx.Unmap(region, 0);
            return buf;
        }
        finally { fr?.Dispose(); dup.ReleaseFrame(); }
    }
    static double Changed(byte[] a, byte[] b)
    {
        int n = 0;
        for (int i = 0; i < a.Length; i += 4)
            if (Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]) > 90) n++;
        return n / (a.Length / 4.0);
    }
    var lat = new List<double>();
    double qpcMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    for (int shot = 0; shot < count; shot++)
    {
        if (reloadVk != 0 && shot > 0 && shot % 5 == 0)
        {
            Native.keybd_event(reloadVk, 0, 0, 0); Thread.Sleep(1500); Native.keybd_event(reloadVk, 0, 2, 0); Thread.Sleep(800);
        }
        // Settle: newest frame as the baseline.
        byte[]? baseFrame = null; var settle = System.Diagnostics.Stopwatch.StartNew();
        while (settle.ElapsedMilliseconds < 400) { var g = Grab(50, out _); if (g != null) baseFrame = g; }
        if (baseFrame == null) { Console.WriteLine("no frames"); continue; }
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Native.keybd_event(vk, (byte)Native.MapVirtualKey(vk, 0), 0, 0);
        bool released = false; double found = -1;
        while ((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * qpcMs < 500)
        {
            if (!released && (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * qpcMs > 120) { Native.keybd_event(vk, (byte)Native.MapVirtualKey(vk, 0), 2, 0); released = true; }
            var g = Grab(20, out long pq);
            if (g != null && Changed(baseFrame, g) > 0.03) { found = (pq - t0) * qpcMs; break; }
        }
        if (!released) Native.keybd_event(vk, (byte)Native.MapVirtualKey(vk, 0), 2, 0);
        Console.WriteLine(found >= 0 ? FormattableString.Invariant($"shot {shot}: {found:0.0} ms") : $"shot {shot}: no change");
        if (found >= 0) lat.Add(found);
        Thread.Sleep(300);
    }
    lat.Sort();
    if (lat.Count == 0) { Console.WriteLine("latency none"); return 2; }
    Console.WriteLine(FormattableString.Invariant($"latency median {lat[lat.Count / 2]:0} ms min {lat[0]:0} max {lat[^1]:0} n {lat.Count}/{count}"));
    return 0;
}

IDXGIResource? res = null;
// The first frames can be empty; wait for one that has been presented.
for (int i = 0; i < 40; i++)
{
    var r = dup.AcquireNextFrame(500, out var info, out res);
    if (r.Success && info.LastPresentTime != 0) break;
    if (r.Success) { res?.Dispose(); res = null; dup.ReleaseFrame(); }
}
if (res == null) { Console.WriteLine("no frame"); return 1; }

using var tex = res.QueryInterface<ID3D11Texture2D>();
var desc = tex.Description;
var sdesc = desc with { Usage = ResourceUsage.Staging, BindFlags = BindFlags.None, CPUAccessFlags = CpuAccessFlags.Read, MiscFlags = ResourceOptionFlags.None, MipLevels = 1, ArraySize = 1 };
using var staging = device.CreateTexture2D(sdesc);
device.ImmediateContext.CopyResource(staging, tex);
var map = device.ImmediateContext.Map(staging, 0, MapMode.Read);
int w = (int)desc.Width, h = (int)desc.Height;
using (var fs = File.Create(outPath))
using (var bw = new BinaryWriter(fs))
{
    int size = w * h * 4;
    bw.Write((ushort)0x4D42); bw.Write(54 + size); bw.Write(0); bw.Write(54);
    bw.Write(40); bw.Write(w); bw.Write(-h); bw.Write((ushort)1); bw.Write((ushort)32); bw.Write(0); bw.Write(size); bw.Write(2835); bw.Write(2835); bw.Write(0); bw.Write(0);
    var row = new byte[w * 4];
    for (int y = 0; y < h; y++)
    {
        System.Runtime.InteropServices.Marshal.Copy(map.DataPointer + y * (nint)map.RowPitch, row, 0, row.Length);
        bw.Write(row);
    }
}
device.ImmediateContext.Unmap(staging, 0);
res.Dispose(); dup.ReleaseFrame();
Console.WriteLine($"{outPath} {w}x{h}");
return 0;

static class Native
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, nuint extra);
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint type);
}
