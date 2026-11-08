using System.Drawing.Drawing2D;

namespace Stresstest;

public sealed class MainForm : Form
{
    static readonly Color Bg = Color.FromArgb(15, 17, 21), Card = Color.FromArgb(24, 27, 34), Line = Color.FromArgb(38, 42, 51),
        Txt = Color.FromArgb(231, 233, 238), Mut = Color.FromArgb(138, 144, 160),
        CpuC = Color.FromArgb(79, 157, 255), GpuC = Color.FromArgb(62, 207, 142), Hot = Color.FromArgb(255, 93, 93), Warn = Color.FromArgb(255, 182, 72);

    readonly CpuLoad _cpu = new();
    readonly GpuLoad _gpu = new();
    readonly Sensors _sens = new();
    readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };

    Button bCpu, bGpu, bBoth, bStop;
    ComboBox cbThreads, cbDur;
    CheckBox chkSafe;
    Label lTimer, lCpuBig, lCpuInfo, lGpuBig, lGpuInfo, lStatus;
    Bar barCpu, barGpu;

    DateTime _start; int _limitSec; long _lastOps; double _lastFlop; DateTime _lastT;
    float _maxGpuTemp = -1;

    public MainForm()
    {
        Text = "Stresstest – CPU & GPU";
        BackColor = Bg; ForeColor = Txt;
        Font = new Font("Segoe UI", 10f);
        ClientSize = new Size(760, 520);
        MinimumSize = new Size(700, 540);
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;

        var title = new Label { Text = "CPU & GPU Stresstest", Font = new Font("Segoe UI Semibold", 17f), AutoSize = true, Location = new Point(20, 14) };
        var sub = new Label { Text = "Lastet Prozessor und Grafikkarte auf 100 % aus. Temperaturen im Blick behalten.", ForeColor = Mut, AutoSize = true, Location = new Point(22, 50) };
        Controls.AddRange(new Control[] { title, sub });

        // --- Steuerung ---
        var ctrl = MakeCard(new Rectangle(20, 82, 720, 110));
        bCpu = MakeBtn("Nur CPU", CpuC, Color.White, 16);
        bGpu = MakeBtn("Nur GPU", GpuC, Color.FromArgb(6, 41, 26), 136);
        bBoth = MakeBtn("CPU + GPU", Color.FromArgb(70, 120, 230), Color.White, 256);
        bStop = MakeBtn("Stop", Hot, Color.White, 376); bStop.Enabled = false;
        ctrl.Controls.AddRange(new Control[] { bCpu, bGpu, bBoth, bStop });

        int threads = Environment.ProcessorCount;
        ctrl.Controls.Add(new Label { Text = "CPU-Threads", ForeColor = Mut, AutoSize = true, Location = new Point(16, 66) });
        cbThreads = MakeCombo(new Point(110, 62), 200,
            $"Alle Threads ({threads})", $"Halbe Last ({Math.Max(1, threads / 2)})", "1 Thread (Single-Core)");
        ctrl.Controls.Add(new Label { Text = "Dauer", ForeColor = Mut, AutoSize = true, Location = new Point(326, 66) });
        cbDur = MakeCombo(new Point(376, 62), 120, "1 Min", "5 Min", "10 Min", "15 Min", "30 Min", "Unbegrenzt");
        cbDur.SelectedIndex = 2;
        chkSafe = new CheckBox { Text = "Auto-Stop bei GPU ≥ 88 °C", Checked = true, AutoSize = true, ForeColor = Txt, Location = new Point(512, 64) };
        ctrl.Controls.AddRange(new Control[] { cbThreads, cbDur, chkSafe });
        lTimer = new Label { Text = "00:00", Font = new Font("Segoe UI Semibold", 16f), AutoSize = false, TextAlign = ContentAlignment.MiddleRight, Size = new Size(200, 40), Location = new Point(504, 14) };
        ctrl.Controls.Add(lTimer);

        // --- CPU-Karte ---
        var cpuCard = MakeCard(new Rectangle(20, 206, 352, 190));
        cpuCard.Controls.Add(new Label { Text = "CPU", ForeColor = CpuC, Font = new Font("Segoe UI Semibold", 11f), AutoSize = true, Location = new Point(16, 12) });
        lCpuBig = new Label { Text = "–", Font = new Font("Segoe UI Semibold", 24f), AutoSize = true, Location = new Point(12, 38) };
        barCpu = new Bar { Location = new Point(16, 92), Size = new Size(320, 12), Fill = CpuC };
        lCpuInfo = new Label { Text = $"{threads} logische Threads erkannt", ForeColor = Mut, AutoSize = false, Size = new Size(320, 70), Location = new Point(16, 112) };
        cpuCard.Controls.AddRange(new Control[] { lCpuBig, barCpu, lCpuInfo });

        // --- GPU-Karte ---
        var gpuCard = MakeCard(new Rectangle(388, 206, 352, 190));
        gpuCard.Controls.Add(new Label { Text = "GPU", ForeColor = GpuC, Font = new Font("Segoe UI Semibold", 11f), AutoSize = true, Location = new Point(16, 12) });
        lGpuBig = new Label { Text = "–", Font = new Font("Segoe UI Semibold", 24f), AutoSize = true, Location = new Point(12, 38) };
        barGpu = new Bar { Location = new Point(16, 92), Size = new Size(320, 12), Fill = GpuC };
        lGpuInfo = new Label { Text = "Direct3D 11 Compute", ForeColor = Mut, AutoSize = false, Size = new Size(320, 70), Location = new Point(16, 112) };
        gpuCard.Controls.AddRange(new Control[] { lGpuBig, barGpu, lGpuInfo });

        lStatus = new Label
        {
            Text = "Bereit. Bei CPU > 95 °C oder GPU > 90 °C abbrechen – CPU-Temperatur im PC Monitor / HWiNFO prüfen.",
            ForeColor = Warn, AutoSize = false, Size = new Size(720, 60), Location = new Point(22, 410)
        };
        Controls.Add(lStatus);

        bCpu.Click += (_, _) => StartTest(true, false);
        bGpu.Click += (_, _) => StartTest(false, true);
        bBoth.Click += (_, _) => StartTest(true, true);
        bStop.Click += (_, _) => StopTest("Gestoppt.");
        _tick.Tick += (_, _) => UpdateUi();
        _tick.Start();
        FormClosing += (_, _) => { _cpu.Stop(); _gpu.Stop(); _sens.Dispose(); };
    }

    // ---------- Ablauf ----------
    bool Active => _cpu.Running || _gpu.Running;

    void StartTest(bool cpu, bool gpu)
    {
        int n = Environment.ProcessorCount;
        int threads = cbThreads.SelectedIndex switch { 1 => Math.Max(1, n / 2), 2 => 1, _ => n };
        _limitSec = new[] { 60, 300, 600, 900, 1800, 0 }[cbDur.SelectedIndex];
        if (cpu) _cpu.Start(threads);
        if (gpu) _gpu.Start();
        _start = _lastT = DateTime.Now; _lastOps = _cpu.Ops; _lastFlop = _gpu.TotalFlop; _maxGpuTemp = -1;
        SetRunning(true);
        lStatus.ForeColor = Warn;
        lStatus.Text = "Test läuft … Temperaturen beobachten.";
    }

    void StopTest(string msg)
    {
        _cpu.Stop(); _gpu.Stop();
        SetRunning(false);
        lStatus.Text = msg + (_maxGpuTemp > 0 ? $"  Höchste GPU-Temperatur: {_maxGpuTemp:0} °C" : "");
    }

    void SetRunning(bool r)
    {
        bCpu.Enabled = bGpu.Enabled = bBoth.Enabled = cbThreads.Enabled = cbDur.Enabled = !r;
        bStop.Enabled = r;
    }

    void UpdateUi()
    {
        var now = DateTime.Now;
        double dt = Math.Max(0.001, (now - _lastT).TotalSeconds);

        // CPU
        float cu = _sens.CpuUtil;
        if (_cpu.Running)
        {
            long ops = _cpu.Ops;
            lCpuInfo.Text = $"Last auf {_cpu.ThreadCount} von {Environment.ProcessorCount} Threads\nRechenleistung: {(ops - _lastOps) / dt / 1e6:0} Mio Ops/s";
            _lastOps = ops;
        }
        lCpuBig.Text = cu >= 0 ? $"{cu:0} % Auslastung" : "–";
        barCpu.Value = cu >= 0 ? cu : 0;

        // GPU
        if (_gpu.Error != null) { StopTest("GPU-Fehler: " + _gpu.Error); lStatus.ForeColor = Hot; }
        string perf = "";
        if (_gpu.Running)
        {
            double f = _gpu.TotalFlop;
            double g = (f - _lastFlop) / dt / 1e9;
            perf = g > 1000 ? $"{g / 1000:0.00} TFLOPS (ca.)" : $"{g:0} GFLOPS (ca.)";
            _lastFlop = f;
        }
        if (_sens.HasNvidia && _sens.GpuUtil >= 0)
        {
            lGpuBig.Text = $"{_sens.GpuUtil:0} % Auslastung";
            barGpu.Value = _sens.GpuUtil;
            if (_sens.GpuTemp > _maxGpuTemp) _maxGpuTemp = _sens.GpuTemp;
            lGpuInfo.Text = $"{_gpu.AdapterName}\nTemp: {_sens.GpuTemp:0} °C  ·  Takt: {_sens.GpuClock:0} MHz  ·  {_sens.GpuPower:0} W" +
                            (perf != "" ? $"\nLeistung: {perf}" : "");
            lGpuInfo.ForeColor = _sens.GpuTemp >= 85 ? Hot : Mut;
        }
        else
        {
            lGpuBig.Text = _gpu.Running ? "läuft" : "–";
            barGpu.Value = _gpu.Running ? 100 : 0;
            lGpuInfo.Text = $"{_gpu.AdapterName}\nKeine NVIDIA-Sensoren gefunden (nvidia-smi)" + (perf != "" ? $"\nLeistung: {perf}" : "");
        }
        _lastT = now;

        // Zeit & Sicherheit
        if (Active)
        {
            int el = (int)(now - _start).TotalSeconds;
            lTimer.Text = Fmt(el) + (_limitSec > 0 ? " / " + Fmt(_limitSec) : "");
            if (_limitSec > 0 && el >= _limitSec) StopTest("Fertig – Testzeit abgelaufen.");
            else if (chkSafe.Checked && _gpu.Running && _sens.GpuTemp >= 88)
            { StopTest($"Sicherheitsstopp: GPU bei {_sens.GpuTemp:0} °C."); lStatus.ForeColor = Hot; }
        }
    }

    static string Fmt(int s) => $"{s / 60:00}:{s % 60:00}";

    // ---------- UI-Helfer ----------
    Panel MakeCard(Rectangle r)
    {
        var p = new RoundPanel { Bounds = r, BackColor = Card, Border = Line };
        Controls.Add(p);
        return p;
    }

    static Button MakeBtn(string text, Color bg, Color fg, int x)
    {
        var b = new Button
        {
            Text = text, BackColor = bg, ForeColor = fg, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI Semibold", 10.5f),
            Size = new Size(110, 40), Location = new Point(x, 12), Cursor = Cursors.Hand
        };
        b.FlatAppearance.BorderSize = 0;
        b.EnabledChanged += (_, _) => b.BackColor = b.Enabled ? bg : Color.FromArgb(50, 54, 63);
        return b;
    }

    static ComboBox MakeCombo(Point loc, int w, params string[] items)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = loc, Width = w, BackColor = Bg, ForeColor = Txt, FlatStyle = FlatStyle.Flat };
        c.Items.AddRange(items);
        c.SelectedIndex = 0;
        return c;
    }
}

sealed class RoundPanel : Panel
{
    public Color Border { get; set; }
    public RoundPanel() { DoubleBuffered = true; }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Border);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}

sealed class Bar : Control
{
    float _v;
    public Color Fill { get; set; }
    public float Value { get => _v; set { _v = Math.Clamp(value, 0, 100); Invalidate(); } }
    public Bar() { DoubleBuffered = true; SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Color.Black);
        using (var bg = new SolidBrush(Color.FromArgb(15, 17, 21))) g.FillRectangle(bg, ClientRectangle);
        int w = (int)(Width * _v / 100f);
        if (w > 0) using (var b = new SolidBrush(Fill)) g.FillRectangle(b, 0, 0, w, Height);
    }
}
