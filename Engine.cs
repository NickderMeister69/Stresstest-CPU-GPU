using System.Diagnostics;
using System.Globalization;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Stresstest;

/// <summary>CPU-Last: pro gewähltem Thread eine Endlos-Rechenschleife.</summary>
public sealed class CpuLoad
{
    private volatile bool _run;
    private readonly List<Thread> _threads = new();
    private long _ops;
    public bool Running => _run;
    public int ThreadCount { get; private set; }
    public long Ops => Interlocked.Read(ref _ops);

    public void Start(int threads)
    {
        Stop();
        _run = true;
        ThreadCount = threads;
        for (int t = 0; t < threads; t++)
        {
            int seed = t;
            var th = new Thread(() => Burn(seed)) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "CPU-Last " + t };
            _threads.Add(th);
            th.Start();
        }
    }

    private void Burn(int seed)
    {
        double x = 1.0001 + seed * 0.01, y = 0.9999, z = 1.5;
        while (_run)
        {
            for (int i = 0; i < 2_000_000; i++)
            {
                x = Math.Sqrt(x * y + 1.25);
                y = Math.Sin(x) + 2.0;
                z = (z * x + y) % 97.0 + 1.0;
            }
            Interlocked.Add(ref _ops, 2_000_000);
        }
        if (z < 0) Console.WriteLine(z); // verhindert Wegoptimieren
    }

    public void Stop()
    {
        _run = false;
        foreach (var t in _threads) t.Join(2000);
        _threads.Clear();
    }
}

/// <summary>GPU-Last: Direct3D-11-Compute-Shader mit selbstregelnder Last.</summary>
public sealed class GpuLoad
{
    private const int WG = 256, GROUPS = 8192, N = WG * GROUPS; // ~2 Mio Threads
    private const int PER_BATCH = 4;
    private const double TARGET_MS = 30; // kurze Dispatches -> kein Treiber-Timeout (TDR)
    private const double FLOPS_PER_ITER = 4 * 4 * 3; // mad (2) + frac (1) auf 4x float4

    private volatile bool _run;
    private Thread _thread;
    private double _flop;
    private readonly object _lock = new();
    public bool Running => _run;
    public string AdapterName { get; private set; } = "–";
    public string Error { get; private set; }
    public double TotalFlop { get { lock (_lock) return _flop; } }

    private const string HLSL = @"
cbuffer P : register(b0) { uint iters; uint3 pad; };
RWStructuredBuffer<float4> buf : register(u0);
[numthreads(256,1,1)]
void main(uint3 id : SV_DispatchThreadID)
{
    float f = id.x * 0.000001;
    float4 a = float4(f, f+0.1, f+0.2, f+0.3);
    float4 b = float4(0.5, 0.25, 0.75, 0.125);
    float4 c = float4(0.3, 0.6, 0.9, 0.1);
    float4 d = float4(0.7, 0.2, 0.4, 0.8);
    [loop] for (uint i = 0; i < iters; i++)
    {
        a = frac(mad(a, b, c));
        b = frac(mad(b, c, d));
        c = frac(mad(c, d, a));
        d = frac(mad(d, a, b));
    }
    buf[id.x] = a + b + c + d;
}";

    public void Start()
    {
        Stop();
        Error = null;
        _run = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "GPU-Last" };
        _thread.Start();
    }

    public void Stop()
    {
        _run = false;
        _thread?.Join(3000);
        _thread = null;
    }

    private static IDXGIAdapter1 PickAdapter(out string name)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        IDXGIAdapter1 best = null; ulong bestMem = 0; name = "–";
        for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1 a).Success; i++)
        {
            var d = a.Description1;
            bool software = (d.Flags & AdapterFlags.Software) != 0;
            if (!software && (ulong)d.DedicatedVideoMemory > bestMem)
            {
                best?.Dispose();
                best = a; bestMem = (ulong)d.DedicatedVideoMemory; name = d.Description;
            }
            else a.Dispose();
        }
        return best;
    }

    private void Loop()
    {
        ID3D11Device device = null; ID3D11DeviceContext ctx = null;
        try
        {
            using var adapter = PickAdapter(out string name);
            AdapterName = name;
            var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
            D3D11.D3D11CreateDevice(adapter, adapter == null ? DriverType.Hardware : DriverType.Unknown,
                DeviceCreationFlags.None, levels, out device, out ctx).CheckError();

            var bytecode = Compiler.Compile(HLSL, "main", "stress.hlsl", "cs_5_0", ShaderFlags.OptimizationLevel3);
            using var cs = device.CreateComputeShader(bytecode.Span);

            using var buf = device.CreateBuffer(new BufferDescription((uint)(N * 16), BindFlags.UnorderedAccess,
                ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, 16));
            using var uav = device.CreateUnorderedAccessView(buf,
                new UnorderedAccessViewDescription(buf, Format.Unknown, 0, (uint)N));
            using var cb = device.CreateBuffer(new BufferDescription(16, BindFlags.ConstantBuffer));

            var queries = new[] { device.CreateQuery(new QueryDescription(QueryType.Event)), device.CreateQuery(new QueryDescription(QueryType.Event)) };
            var queryIters = new uint[2];
            bool[] inFlight = new bool[2];

            ctx.CSSetShader(cs);
            ctx.CSSetConstantBuffer(0, cb);
            ctx.CSSetUnorderedAccessView(0, uav);

            uint iters = 32;
            var sw = Stopwatch.StartNew();
            double tPrev = 0;
            int slot = 0;

            while (_run)
            {
                var p = new uint[] { iters, 0, 0, 0 };
                ctx.UpdateSubresource(p, cb);
                for (int k = 0; k < PER_BATCH; k++) ctx.Dispatch((uint)GROUPS, 1, 1);
                ctx.End(queries[slot]);
                queryIters[slot] = iters;
                inFlight[slot] = true;
                ctx.Flush();

                // Auf den VORHERIGEN Batch warten -> GPU hat immer einen Batch in der Warteschlange
                int prev = slot ^ 1;
                if (inFlight[prev])
                {
                    while (!ctx.IsDataAvailable(queries[prev]) && _run) Thread.Sleep(0);
                    inFlight[prev] = false;
                    double now = sw.Elapsed.TotalMilliseconds;
                    double per = (now - tPrev) / PER_BATCH;
                    tPrev = now;
                    lock (_lock) _flop += (double)N * queryIters[prev] * PER_BATCH * FLOPS_PER_ITER;
                    if (per < TARGET_MS * 0.7) iters = Math.Min(iters * 3 / 2 + 1, 1u << 16);
                    else if (per > TARGET_MS * 1.4) iters = Math.Max(iters * 7 / 10, 8u);
                }
                else tPrev = sw.Elapsed.TotalMilliseconds;
                slot = prev;
            }
            foreach (var q in queries) q.Dispose();
            ctx.ClearState();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            _run = false;
        }
        finally
        {
            ctx?.Dispose();
            device?.Dispose();
        }
    }
}

/// <summary>Echte Sensorwerte: CPU-Auslastung (Windows) + NVIDIA-Werte über nvidia-smi.</summary>
public sealed class Sensors : IDisposable
{
    private PerformanceCounter _cpu;
    public float CpuUtil { get; private set; } = -1;
    public float GpuUtil { get; private set; } = -1;
    public float GpuTemp { get; private set; } = -1;
    public float GpuClock { get; private set; } = -1;
    public float GpuPower { get; private set; } = -1;
    public bool HasNvidia { get; private set; } = true;
    private volatile bool _run = true;
    private readonly Thread _thread;

    public Sensors()
    {
        try { _cpu = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total"); _cpu.NextValue(); }
        catch { try { _cpu = new PerformanceCounter("Processor", "% Processor Time", "_Total"); _cpu.NextValue(); } catch { _cpu = null; } }
        _thread = new Thread(Poll) { IsBackground = true, Name = "Sensoren" };
        _thread.Start();
    }

    private void Poll()
    {
        while (_run)
        {
            try { if (_cpu != null) CpuUtil = Math.Min(100f, _cpu.NextValue()); } catch { }
            if (HasNvidia) ReadNvidia();
            Thread.Sleep(1000);
        }
    }

    private void ReadNvidia()
    {
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi",
                "--query-gpu=utilization.gpu,temperature.gpu,clocks.gr,power.draw --format=csv,noheader,nounits")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            string line = p.StandardOutput.ReadLine();
            p.WaitForExit(3000);
            if (string.IsNullOrWhiteSpace(line)) return;
            var v = line.Split(',');
            float F(int i) => i < v.Length && float.TryParse(v[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : -1;
            GpuUtil = F(0); GpuTemp = F(1); GpuClock = F(2); GpuPower = F(3);
        }
        catch { HasNvidia = false; }
    }

    public void Dispose() { _run = false; _cpu?.Dispose(); }
}
