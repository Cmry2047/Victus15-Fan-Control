using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Management;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HPVictusFanControl
{
    // =========================================================================
    // HP WMI: akses langsung ke BIOS (tanpa spawn PowerShell)
    // =========================================================================
    public static class HpWmi
    {
        private const string Ns = @"root\wmi";
        private const string Cls = "hpqBIntM";
        private const uint Cmd = 0x20008;
        private static readonly byte[] Sign = new byte[] { 0x53, 0x45, 0x43, 0x55 }; // "SECU"
        private static readonly object Gate = new object();
        public const int MaxRpm = 5800;

        public struct GpuTelemetry
        {
            public int TemperatureC;
            public float PowerWatts;
        }

        private static readonly object PowerGate = new object();
        private static PerformanceCounter cpuPackagePowerCounter;

        // Satu pintu untuk semua panggilan WMI. Return false + error bila gagal
        // (termasuk bila firmware menolak lewat rwReturnCode).
        private static bool Invoke(uint type, byte[] data, int outSize, out byte[] resp, out string error)
        {
            resp = null;
            error = null;
            lock (Gate)
            {
                try
                {
                    using (ManagementClass mc = new ManagementClass(Ns, Cls, null))
                    using (ManagementObjectCollection col = mc.GetInstances())
                    {
                        ManagementObject target = null;
                        foreach (ManagementObject o in col)
                        {
                            if (target == null) target = o; else o.Dispose();
                        }
                        if (target == null)
                        {
                            error = "Antarmuka WMI hpqBIntM tidak ditemukan (model/BIOS tidak didukung).";
                            return false;
                        }

                        using (target)
                        using (ManagementClass inCls = new ManagementClass(Ns, "hpqBDataIn", null))
                        using (ManagementObject inData = inCls.CreateInstance())
                        {
                            inData["Command"] = Cmd;
                            inData["CommandType"] = type;
                            inData["Sign"] = Sign;
                            inData["Size"] = (uint)data.Length;
                            inData["hpqBData"] = data;

                            string method = "hpqBIOSInt" + outSize;
                            ManagementBaseObject result = null;
                            Exception first = null;
                            try
                            {
                                using (ManagementBaseObject p = target.GetMethodParameters(method))
                                {
                                    p["InData"] = inData;
                                    result = target.InvokeMethod(method, p, null);
                                }
                            }
                            catch (Exception ex) { first = ex; }

                            if (first != null)
                            {
                                try
                                {
                                    result = target.InvokeMethod(method, new object[] { inData }) as ManagementBaseObject;
                                    first = null;
                                }
                                catch (Exception) { }
                            }
                            if (first != null)
                            {
                                error = first.Message;
                                return false;
                            }

                            ManagementBaseObject od = result == null ? null : result["OutData"] as ManagementBaseObject;
                            if (od == null) return true;

                            foreach (PropertyData pd in od.Properties)
                            {
                                if (pd.Name == "rwReturnCode" && pd.Value != null)
                                {
                                    uint code = Convert.ToUInt32(pd.Value);
                                    if (code != 0)
                                    {
                                        error = "Firmware menolak perintah (kode 0x" + code.ToString("X2") + ").";
                                        return false;
                                    }
                                }
                                else if ((pd.Name == "Data" || pd.Name == "hpqBData") && resp == null)
                                {
                                    resp = pd.Value as byte[];
                                }
                            }
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
            }
        }

        private static string Send(uint type, byte[] d)
        {
            byte[] r; string e;
            return Invoke(type, d, 4, out r, out e) ? null : e;
        }

        public static bool IsAvailable()
        {
            try
            {
                lock (Gate)
                using (ManagementClass mc = new ManagementClass(Ns, Cls, null))
                using (ManagementObjectCollection col = mc.GetInstances())
                {
                    foreach (ManagementObject o in col) { o.Dispose(); return true; }
                }
            }
            catch (Exception) { }
            return false;
        }

        // ---- Kontrol (return null = sukses, selain itu pesan error) ----

        public static string SetMaxFan(bool enable)
        {
            if (enable) Send(0x2E, new byte[] { 0, 0, 0, 0 }); // bersihkan override manual
            return Send(0x27, new byte[] { (byte)(enable ? 1 : 0), 0, 0, 0 });
        }

        public static int LevelFromPercent(int percent)
        {
            int clamped = Math.Max(15, Math.Min(95, percent));
            int raw = 20 + (int)Math.Round(clamped * 34.0 / 100.0);
            return Math.Max(20, Math.Min(54, raw));
        }

        public static int RpmFromPercent(int percent)
        {
            return percent >= 100 ? MaxRpm : LevelFromPercent(percent) * 100;
        }

        public static string SetPercent(int percent)
        {
            if (percent >= 100) return SetMaxFan(true);
            string err = Send(0x27, new byte[] { 0, 0, 0, 0 });
            if (err != null) return err;
            byte lvl = (byte)LevelFromPercent(percent);
            return Send(0x2E, new byte[] { lvl, lvl, 0, 0 });
        }

        public static string ResetToAuto()
        {
            string err = Send(0x27, new byte[] { 0, 0, 0, 0 });
            if (err != null) return err;
            Send(0x2E, new byte[] { 0, 0, 0, 0 });
            return Send(0x1A, new byte[] { 0xFF, 0, 0, 0 });
        }

        // ---- Pembacaan sensor ----

        public static int ReadCpuTemp()
        {
            byte[] b; string e;
            if (Invoke(0x23, new byte[] { 1, 0, 0, 0 }, 4, out b, out e) && b != null && b.Length > 0)
            {
                if (b[0] >= 20 && b[0] <= 110) return b[0];
            }
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"))
                using (ManagementObjectCollection res = s.Get())
                {
                    foreach (ManagementObject m in res)
                    {
                        using (m)
                        {
                            int t = (int)Math.Round(Convert.ToDouble(m["CurrentTemperature"]) / 10.0 - 273.15);
                            if (t >= 0 && t <= 110) return t;
                        }
                    }
                }
            }
            catch (Exception) { }
            return -1;
        }

        public static GpuTelemetry ReadGpuTelemetry()
        {
            GpuTelemetry telemetry = new GpuTelemetry { TemperatureC = -1, PowerWatts = -1f };
            try
            {
                string systemNvidiaSmi = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe");
                ProcessStartInfo si = new ProcessStartInfo
                {
                    FileName = System.IO.File.Exists(systemNvidiaSmi) ? systemNvidiaSmi : "nvidia-smi.exe",
                    Arguments = "--query-gpu=temperature.gpu,power.draw --format=csv,noheader,nounits",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (Process p = Process.Start(si))
                {
                    if (p == null) return telemetry;

                    StringBuilder output = new StringBuilder();
                    StringBuilder error = new StringBuilder();
                    p.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                    {
                        if (e.Data != null) output.AppendLine(e.Data);
                    };
                    p.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                    {
                        if (e.Data != null) error.AppendLine(e.Data);
                    };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();

                    if (!p.WaitForExit(2500))
                    {
                        p.Kill();
                        p.WaitForExit();
                        return telemetry;
                    }
                    p.WaitForExit();

                    if (p.ExitCode != 0) return telemetry;

                    string[] values = output.ToString().Trim().Split(',');
                    float temperature;
                    float power;
                    if (values.Length >= 2 &&
                        float.TryParse(values[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out temperature) &&
                        !float.IsNaN(temperature) && !float.IsInfinity(temperature) &&
                        temperature >= 0f && temperature <= 120f)
                    {
                        telemetry.TemperatureC = (int)Math.Round(temperature);
                    }
                    if (values.Length >= 2 &&
                        float.TryParse(values[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out power) &&
                        !float.IsNaN(power) && !float.IsInfinity(power) && power >= 0f)
                    {
                        telemetry.PowerWatts = power;
                    }
                }
            }
            catch (Exception) { }
            return telemetry;
        }

        public static float ReadCpuPowerWatts()
        {
            try
            {
                lock (PowerGate)
                {
                    if (cpuPackagePowerCounter == null)
                    {
                        cpuPackagePowerCounter = new PerformanceCounter(
                            "Energy Meter", "Power", "RAPL_Package0_PKG", true);
                        cpuPackagePowerCounter.NextValue();
                        System.Threading.Thread.Sleep(500);
                    }

                    float milliwatts = cpuPackagePowerCounter.NextValue();
                    if (!float.IsNaN(milliwatts) && !float.IsInfinity(milliwatts) && milliwatts > 0f)
                        return milliwatts / 1000f;
                }
            }
            catch (Exception)
            {
                lock (PowerGate)
                {
                    if (cpuPackagePowerCounter != null)
                    {
                        cpuPackagePowerCounter.Dispose();
                        cpuPackagePowerCounter = null;
                    }
                }
            }
            return -1f;
        }

        // source: 1 = tachometer asli, 2 = target level dari firmware, 0 = tidak ada
        public static int ReadFanRpm(int fan, out int source)
        {
            byte[] b; string e;
            if (Invoke(0x11, new byte[] { (byte)fan, 0, 0, 0 }, 4, out b, out e) && b != null && b.Length >= 4)
            {
                int rpm = (b[2] << 8) | b[3];
                if (rpm > 200 && rpm < 9000) { source = 1; return rpm; }
                int alt = (b[0] << 8) | b[1];
                if (alt > 200 && alt < 9000) { source = 1; return alt; }
            }
            // 0x2D: level kipas saat ini, satuan 100 RPM per kipas (byte 0 = CPU, byte 1 = GPU)
            if (Invoke(0x2D, new byte[] { 0, 0, 0, 0 }, 128, out b, out e) && b != null && b.Length > fan)
            {
                int lvl = b[fan];
                if (lvl >= 10 && lvl <= 70) { source = 2; return lvl * 100; }
            }
            source = 0;
            return -1;
        }
    }

    // =========================================================================
    // Helper UI: warna, animasi, gambar
    // =========================================================================
    internal static class Ui
    {
        public static readonly Color Bg = Color.FromArgb(13, 14, 18);
        public static readonly Color Card = Color.FromArgb(23, 25, 32);
        public static readonly Color CardHi = Color.FromArgb(33, 36, 46);
        public static readonly Color Line = Color.FromArgb(40, 44, 56);
        public static readonly Color Text = Color.FromArgb(234, 236, 242);
        public static readonly Color Muted = Color.FromArgb(130, 137, 154);
        public static readonly Color Cyan = Color.FromArgb(0, 229, 255);
        public static readonly Color Green = Color.FromArgb(0, 230, 118);
        public static readonly Color Amber = Color.FromArgb(255, 179, 0);
        public static readonly Color Red = Color.FromArgb(255, 82, 82);

        public static event Action Tick;
        public static bool Paused;
        private static Timer timer;
        private static readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();
        private static readonly StringFormat[] formats = new StringFormat[3];

        public static void Start()
        {
            if (timer != null) return;
            timer = new Timer();
            timer.Interval = 33; // ~30 fps, cukup halus dan hemat CPU
            timer.Tick += delegate { if (!Paused && Tick != null) Tick(); };
            timer.Start();
        }

        public static float Approach(float cur, float target, float k, float eps)
        {
            float d = target - cur;
            if (Math.Abs(d) < eps) return target;
            return cur + d * k;
        }

        public static float Approach(float cur, float target, float k)
        {
            return Approach(cur, target, k, 0.003f);
        }

        public static Color Mix(Color a, Color b, float t)
        {
            t = Math.Max(0f, Math.Min(1f, t));
            return Color.FromArgb(
                (int)Math.Round(a.A + (b.A - a.A) * t),
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        public static Color Chase(Color c, Color t, float k)
        {
            if (Math.Abs(c.R - t.R) + Math.Abs(c.G - t.G) + Math.Abs(c.B - t.B) < 12) return t;
            return Mix(c, t, k);
        }

        public static Color Alpha(Color c, int a)
        {
            return Color.FromArgb(Math.Max(0, Math.Min(255, a)), c);
        }

        public static Font F(float size, bool bold)
        {
            string k = size.ToString(CultureInfo.InvariantCulture) + (bold ? "b" : "r");
            Font f;
            if (!fonts.TryGetValue(k, out f))
            {
                f = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular);
                fonts[k] = f;
            }
            return f;
        }

        public static void Quality(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        }

        public static void Str(Graphics g, string s, Font f, Color c, RectangleF r, StringAlignment a)
        {
            StringFormat sf = formats[(int)a];
            if (sf == null)
            {
                sf = new StringFormat();
                sf.Alignment = a;
                sf.LineAlignment = StringAlignment.Center;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                sf.Trimming = StringTrimming.EllipsisCharacter;
                formats[(int)a] = sf;
            }
            using (SolidBrush b = new SolidBrush(c)) g.DrawString(s, f, b, r, sf);
        }

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            float d = Math.Min(rad * 2f, Math.Min(r.Width, r.Height));
            GraphicsPath p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Color c, RectangleF r, float rad)
        {
            using (GraphicsPath p = Round(r, rad))
            using (SolidBrush b = new SolidBrush(c)) g.FillPath(b, p);
        }

        public static void StrokeRound(Graphics g, Color c, float w, RectangleF r, float rad)
        {
            using (GraphicsPath p = Round(r, rad))
            using (Pen pen = new Pen(c, w)) g.DrawPath(pen, p);
        }

        // Kipas dengan 4 bilah; angle = sudut putar saat ini
        public static void DrawFan(Graphics g, float cx, float cy, float radius, float angle, Color c)
        {
            using (SolidBrush b = new SolidBrush(c))
            {
                for (int i = 0; i < 4; i++)
                {
                    GraphicsState st = g.Save();
                    g.TranslateTransform(cx, cy);
                    g.RotateTransform(angle + i * 90f);
                    g.TranslateTransform(radius * 0.55f, 0f);
                    g.RotateTransform(-28f);
                    g.FillEllipse(b, -radius * 0.42f, -radius * 0.24f, radius * 0.84f, radius * 0.48f);
                    g.Restore(st);
                }
            }
            using (SolidBrush hub = new SolidBrush(Card))
                g.FillEllipse(hub, cx - radius * 0.24f, cy - radius * 0.24f, radius * 0.48f, radius * 0.48f);
            using (SolidBrush dot = new SolidBrush(c))
                g.FillEllipse(dot, cx - radius * 0.1f, cy - radius * 0.1f, radius * 0.2f, radius * 0.2f);
        }

        // Ikon preset: 0 auto, 1 quiet, 2 balanced, 3 performance, 4 max
        public static void DrawIcon(Graphics g, int kind, RectangleF rc, Color c)
        {
            using (SolidBrush br = new SolidBrush(c))
            using (Pen pen = new Pen(c, 2.2f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                float x = rc.X, y = rc.Y, w = rc.Width, h = rc.Height;
                switch (kind)
                {
                    case 0:
                    {
                        g.DrawArc(pen, x + 2, y + 2, w - 4, h - 4, 50, 270);
                        float R = w / 2f - 2f, cx = x + w / 2f, cy = y + h / 2f;
                        double a = 320.0 * Math.PI / 180.0;
                        PointF end = new PointF(cx + (float)(R * Math.Cos(a)), cy + (float)(R * Math.Sin(a)));
                        PointF dir = new PointF((float)(-Math.Sin(a)), (float)Math.Cos(a));
                        PointF nrm = new PointF(-dir.Y, dir.X);
                        PointF[] tri = new PointF[] {
                            new PointF(end.X + dir.X * 6f, end.Y + dir.Y * 6f),
                            new PointF(end.X + nrm.X * 4.5f, end.Y + nrm.Y * 4.5f),
                            new PointF(end.X - nrm.X * 4.5f, end.Y - nrm.Y * 4.5f) };
                        g.FillPolygon(br, tri);
                        break;
                    }
                    case 1:
                    {
                        using (GraphicsPath a = new GraphicsPath())
                        using (GraphicsPath b = new GraphicsPath())
                        {
                            a.AddEllipse(x + 2, y + 2, w - 4, h - 4);
                            b.AddEllipse(x + 8, y - 1, w - 6, h - 4);
                            using (Region rg = new Region(a)) { rg.Exclude(b); g.FillRegion(br, rg); }
                        }
                        break;
                    }
                    case 2:
                    {
                        float[] hs = new float[] { 0.45f, 0.85f, 0.6f };
                        for (int i = 0; i < 3; i++)
                        {
                            float bh = h * hs[i];
                            g.FillRectangle(br, x + 3 + i * 7.5f, y + h - bh, 5f, bh);
                        }
                        break;
                    }
                    case 3:
                    {
                        float[,] p = { { 0.62f, 0f }, { 0.18f, 0.58f }, { 0.47f, 0.58f }, { 0.36f, 1f }, { 0.82f, 0.38f }, { 0.53f, 0.38f } };
                        PointF[] pts = new PointF[6];
                        for (int i = 0; i < 6; i++) pts[i] = new PointF(x + p[i, 0] * w, y + p[i, 1] * h);
                        g.FillPolygon(br, pts);
                        break;
                    }
                    default:
                    {
                        g.DrawLines(pen, new PointF[] { new PointF(x + w * 0.15f, y + h * 0.5f), new PointF(x + w * 0.5f, y + h * 0.15f), new PointF(x + w * 0.85f, y + h * 0.5f) });
                        g.DrawLines(pen, new PointF[] { new PointF(x + w * 0.15f, y + h * 0.88f), new PointF(x + w * 0.5f, y + h * 0.53f), new PointF(x + w * 0.85f, y + h * 0.88f) });
                        break;
                    }
                }
            }
        }
    }

    internal abstract class AnimControl : Control
    {
        private readonly Action handler;

        protected AnimControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Bg;
            handler = delegate { if (Visible && IsHandleCreated && Step()) Invalidate(); };
            Ui.Tick += handler;
        }

        // Kembalikan true bila perlu digambar ulang
        protected abstract bool Step();

        protected override void Dispose(bool disposing)
        {
            if (disposing) Ui.Tick -= handler;
            base.Dispose(disposing);
        }
    }

    // =========================================================================
    // Header
    // =========================================================================
    internal sealed class HeaderBar : AnimControl
    {
        private float phase;

        public HeaderBar() { Size = new Size(548, 56); }

        protected override bool Step() { phase += 0.12f; return true; }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Ui.Quality(g);
            g.Clear(Ui.Bg);

            RectangleF logo = new RectangleF(2, 6, 44, 44);
            using (GraphicsPath p = Ui.Round(logo, 12))
            using (LinearGradientBrush lg = new LinearGradientBrush(logo, Color.FromArgb(0, 229, 255), Color.FromArgb(0, 113, 197), 45f))
                g.FillPath(lg, p);
            Ui.DrawFan(g, logo.X + 22, logo.Y + 22, 15f, phase * 25f, Color.White);

            Ui.Str(g, "VICTUS FAN CONTROL", Ui.F(14f, true), Ui.Text, new RectangleF(58, 6, 340, 24), StringAlignment.Near);
            Ui.Str(g, "By Cmry2047  \u2022  Kontrol kipas & pemantauan suhu", Ui.F(8.5f, false), Ui.Muted, new RectangleF(58, 30, 340, 18), StringAlignment.Near);

        }
    }

    // =========================================================================
    // Kartu status dengan kipas berputar
    // =========================================================================
    internal sealed class StatusCard : AnimControl
    {
        private string title = "AUTO", desc = "Menunggu perintah", badge = "AUTO";
        private string cpuPowerText = "N/A", gpuPowerText = "N/A", totalPowerText = "N/A";
        private Color accent = Ui.Green, accentT = Ui.Green;
        private float fade = 1f, angle, spin = 4f, spinT = 4f;

        public StatusCard() { Size = new Size(548, 104); }

        public void SetMode(string t, string d, string b, Color c, float spinSpeed)
        {
            title = t; desc = d; badge = b; accentT = c; spinT = spinSpeed; fade = 0f;
            Invalidate();
        }

        public void SetPower(float cpuWatts, float gpuWatts)
        {
            cpuPowerText = FormatWatts(cpuWatts);
            gpuPowerText = FormatWatts(gpuWatts);
            totalPowerText = cpuWatts >= 0f && gpuWatts >= 0f
                ? FormatWatts(cpuWatts + gpuWatts)
                : "N/A";
            Invalidate();
        }

        private static string FormatWatts(float watts)
        {
            return watts >= 0f && !float.IsNaN(watts) && !float.IsInfinity(watts)
                ? watts.ToString("0.0", CultureInfo.InvariantCulture) + " W"
                : "N/A";
        }

        protected override bool Step()
        {
            accent = Ui.Chase(accent, accentT, 0.18f);
            fade = Ui.Approach(fade, 1f, 0.18f);
            spin = Ui.Approach(spin, spinT, 0.06f, 0.02f);
            angle = (angle + spin) % 360f;
            return true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Ui.Quality(g);
            g.Clear(Ui.Bg);
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Ui.FillRound(g, Ui.Card, r, 14);
            Ui.StrokeRound(g, Ui.Mix(Ui.Line, accent, 0.35f), 1f, r, 14);

            using (SolidBrush glow = new SolidBrush(Ui.Alpha(accent, 34)))
                g.FillEllipse(glow, 14, 11, 56, 56);
            Ui.DrawFan(g, 42, 39, 22f, angle, accent);

            Color tc = Ui.Mix(Ui.Card, accent, 0.35f + 0.65f * fade);
            Ui.Str(g, title, Ui.F(15f, true), tc, new RectangleF(84 + (1f - fade) * 10f, 12, 340, 28), StringAlignment.Near);
            Ui.Str(g, desc, Ui.F(8.5f, false), Ui.Muted, new RectangleF(84, 42, 360, 22), StringAlignment.Near);

            RectangleF b = new RectangleF(Width - 112, 20, 96, 38);
            Ui.FillRound(g, Ui.Alpha(accent, 32), b, 10);
            Ui.StrokeRound(g, Ui.Alpha(accent, 140), 1f, b, 10);
            Ui.Str(g, badge, Ui.F(12f, true), accent, b, StringAlignment.Center);

            using (Pen divider = new Pen(Ui.Line, 1f))
                g.DrawLine(divider, 14, 68, Width - 14, 68);
            string[] labels = { "CPU PACKAGE", "GPU NVIDIA", "TOTAL WATTS" };
            string[] values = { cpuPowerText, gpuPowerText, totalPowerText };
            Color[] colors = { Ui.Cyan, Color.FromArgb(118, 185, 0), Ui.Text };
            for (int i = 0; i < labels.Length; i++)
            {
                RectangleF column = new RectangleF(20 + i * 176, 70, 156, 13);
                Ui.Str(g, labels[i], Ui.F(7f, true), Ui.Muted, column, StringAlignment.Center);
                Ui.Str(g, values[i], Ui.F(9f, true), colors[i],
                       new RectangleF(column.X, 83, column.Width, 17), StringAlignment.Center);
            }
        }
    }

    // =========================================================================
    // Tachometer: busur RPM beranimasi + suhu
    // =========================================================================
    internal sealed class TachometerGauge : AnimControl
    {
        private const int MaxRpm = 6000;
        private readonly string title;
        private readonly Color chip;
        private float shown, target, temp, tempT, phase;
        private bool has, tempHas;

        public TachometerGauge(string title, Color chip)
        {
            this.title = title;
            this.chip = chip;
            Size = new Size(270, 170);
        }

        // rpm < 0 = tidak ada data.
        public void SetRpm(int rpm)
        {
            has = rpm >= 0;
            target = has ? Math.Min(MaxRpm, rpm) : 0;
        }

        public void SetTemp(int t)
        {
            tempHas = t > 0;
            if (tempHas) tempT = t;
        }

        protected override bool Step()
        {
            float s = Ui.Approach(shown, target, 0.14f, 0.6f);
            float t = Ui.Approach(temp, tempT, 0.2f, 0.2f);
            bool turbo = s > 5200f;
            if (turbo) phase += 0.25f;
            bool ch = s != shown || t != temp || turbo;
            shown = s; temp = t;
            return ch;
        }

        private static Color RpmColor(float rpm)
        {
            Color c1 = Color.FromArgb(0, 229, 255), c2 = Color.FromArgb(0, 230, 118),
                  c3 = Color.FromArgb(255, 179, 0), c4 = Color.FromArgb(255, 61, 0);
            if (rpm < 2000f) return c1;
            if (rpm < 3500f) return Ui.Mix(c1, c2, (rpm - 2000f) / 1500f);
            if (rpm < 4700f) return Ui.Mix(c2, c3, (rpm - 3500f) / 1200f);
            return Ui.Mix(c3, c4, (rpm - 4700f) / 1100f);
        }

        private static Color TempColor(float t)
        {
            if (t < 60f) return Ui.Green;
            if (t < 75f) return Ui.Amber;
            if (t < 85f) return Color.FromArgb(255, 145, 0);
            return Ui.Red;
        }

        private static string TempText(float t)
        {
            if (t < 60f) return "SEJUK";
            if (t < 75f) return "HANGAT";
            if (t < 85f) return "TINGGI";
            return "PANAS";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Ui.Quality(g);
            g.Clear(Ui.Bg);
            int w = Width, h = Height;
            RectangleF card = new RectangleF(0.5f, 0.5f, w - 1.5f, h - 1.5f);
            Ui.FillRound(g, Ui.Card, card, 14);
            Ui.StrokeRound(g, Ui.Line, 1f, card, 14);

            // Judul
            Ui.FillRound(g, chip, new RectangleF(14, 13, 4, 14), 2);
            Ui.Str(g, title, Ui.F(9.5f, true), Ui.Text, new RectangleF(24, 8, 150, 24), StringAlignment.Near);

            int d = 112, cx = w / 2, cy = 84;
            RectangleF arc = new RectangleF(cx - d / 2f, cy - d / 2f, d, d);
            float start = 135f, sweep = 270f;
            Color gc = RpmColor(shown);

            using (Pen bg = new Pen(Color.FromArgb(36, 40, 52), 9f))
            {
                bg.StartCap = LineCap.Round; bg.EndCap = LineCap.Round;
                g.DrawArc(bg, arc, start, sweep);
            }

            float active = Math.Max(3f, Math.Min(sweep, shown / MaxRpm * sweep));
            if (has || shown > 1f)
            {
                float pulse = shown > 5200f ? 0.5f + 0.5f * (float)Math.Sin(phase) : 0.4f;
                using (Pen glow = new Pen(Ui.Alpha(gc, (int)(28 + 40 * pulse)), 16f))
                {
                    glow.StartCap = LineCap.Round; glow.EndCap = LineCap.Round;
                    g.DrawArc(glow, arc, start, active);
                }
                using (Pen ap = new Pen(gc, 9f))
                {
                    ap.StartCap = LineCap.Round; ap.EndCap = LineCap.Round;
                    g.DrawArc(ap, arc, start, active);
                }
            }

            using (Pen tick = new Pen(Color.FromArgb(70, 76, 92), 1.5f))
            {
                for (int r = 0; r <= MaxRpm; r += 1000)
                {
                    double a = (start + (float)r / MaxRpm * sweep) * Math.PI / 180.0;
                    float ri = d / 2f + 10f, ro = d / 2f + 15f;
                    g.DrawLine(tick, cx + (float)(Math.Cos(a) * ri), cy + (float)(Math.Sin(a) * ri),
                                     cx + (float)(Math.Cos(a) * ro), cy + (float)(Math.Sin(a) * ro));
                }
            }

            string num = has ? ((int)Math.Round(shown)).ToString("N0", CultureInfo.GetCultureInfo("id-ID")) : "--";
            Ui.Str(g, num, Ui.F(17f, true), Ui.Text, new RectangleF(0, cy - 16, w, 28), StringAlignment.Center);
            Ui.Str(g, "RPM", Ui.F(7.5f, true), gc, new RectangleF(0, cy + 10, w, 16), StringAlignment.Center);

            // Pil suhu: isi bar mengikuti suhu
            RectangleF pill = new RectangleF(14, h - 38, w - 29, 26);
            Ui.FillRound(g, Color.FromArgb(18, 20, 26), pill, 9);
            if (tempHas)
            {
                Color tc = TempColor(temp);
                float frac = Math.Max(0f, Math.Min(1f, (temp - 30f) / 70f));
                using (GraphicsPath clip = Ui.Round(pill, 9))
                {
                    g.SetClip(clip);
                    using (SolidBrush fb = new SolidBrush(Ui.Alpha(tc, 46)))
                        g.FillRectangle(fb, pill.X, pill.Y, pill.Width * frac, pill.Height);
                    g.ResetClip();
                }
                Ui.Str(g, ((int)Math.Round(temp)) + "\u00B0C", Ui.F(10.5f, true), Ui.Text,
                       new RectangleF(pill.X + 12, pill.Y, 80, pill.Height), StringAlignment.Near);
                Ui.Str(g, TempText(temp), Ui.F(8f, true), tc,
                       new RectangleF(pill.X + 80, pill.Y, pill.Width - 92, pill.Height), StringAlignment.Far);
            }
            else
            {
                Ui.Str(g, "--\u00B0C", Ui.F(10.5f, true), Ui.Muted, new RectangleF(pill.X + 12, pill.Y, 80, pill.Height), StringAlignment.Near);
                Ui.Str(g, "SENSOR TIDAK TERSEDIA", Ui.F(7.5f, true), Ui.Muted, new RectangleF(pill.X + 60, pill.Y, pill.Width - 72, pill.Height), StringAlignment.Far);
            }
        }
    }

    // =========================================================================
    // Tombol preset beranimasi (hover, tekan, terpilih)
    // =========================================================================
    internal sealed class PresetButton : AnimControl
    {
        public string Title = "", Sub = "";
        public Color Accent = Ui.Cyan;
        public int Icon;
        private bool selected, hot, down;
        private float hov, dn, sel;

        public PresetButton()
        {
            Size = new Size(104, 86);
            Cursor = Cursors.Hand;
        }

        public bool Selected { get { return selected; } set { selected = value; } }

        protected override bool Step()
        {
            float h = Ui.Approach(hov, hot ? 1f : 0f, 0.3f);
            float d = Ui.Approach(dn, down ? 1f : 0f, 0.45f);
            float s = Ui.Approach(sel, selected ? 1f : 0f, 0.18f);
            bool ch = h != hov || d != dn || s != sel;
            hov = h; dn = d; sel = s;
            return ch;
        }

        protected override void OnMouseEnter(EventArgs e) { hot = true; base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hot = false; down = false; base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) down = true; base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Ui.Quality(g);
            g.Clear(Ui.Bg);

            float lift = hov * 3f, sh = dn * 2f;
            RectangleF r = new RectangleF(3 + sh, 4 - lift + sh, Width - 6 - 2 * sh, Height - 9 - sh);

            if (sel > 0.01f)
                Ui.StrokeRound(g, Ui.Alpha(Accent, (int)(50 * sel)), 4f, r, 12);

            Color fill = Ui.Mix(Ui.Card, Ui.CardHi, hov);
            fill = Ui.Mix(fill, Ui.Mix(Ui.Card, Accent, 0.22f), sel);
            Ui.FillRound(g, fill, r, 12);
            Ui.StrokeRound(g, Ui.Mix(Ui.Line, Accent, Math.Max(sel, hov * 0.5f)), 1.2f, r, 12);

            Color ic = Ui.Mix(Ui.Muted, Accent, Math.Max(sel, hov));
            float cx = r.X + r.Width / 2f;
            Ui.DrawIcon(g, Icon, new RectangleF(cx - 11, r.Y + 10, 22, 22), ic);
            Ui.Str(g, Title, Ui.F(9f, true), Ui.Mix(Ui.Text, Accent, sel * 0.6f), new RectangleF(r.X, r.Y + 36, r.Width, 18), StringAlignment.Center);
            Ui.Str(g, Sub, Ui.F(7.5f, false), Ui.Muted, new RectangleF(r.X, r.Y + 53, r.Width, 16), StringAlignment.Center);
        }
    }

    // =========================================================================
    // Slider kustom (menerapkan saat dilepas)
    // =========================================================================
    internal sealed class ModernSlider : AnimControl
    {
        public int Minimum = 20, Maximum = 100, Step_ = 5;
        public event EventHandler ValueChanged;
        public event EventHandler Committed;
        private int val = 50;
        private float pos = -1f, thumb;
        private bool drag, hot;
        private string valueText = "";

        public ModernSlider()
        {
            Size = new Size(548, 84);
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        public string ValueText { get { return valueText; } set { valueText = value; Invalidate(); } }

        public int Value
        {
            get { return val; }
            set
            {
                int v = Math.Max(Minimum, Math.Min(Maximum, value));
                v = Minimum + (int)Math.Round((v - Minimum) / (double)Step_) * Step_;
                v = Math.Min(Maximum, v);
                if (v == val) return;
                val = v;
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }

        private float Frac { get { return (val - Minimum) / (float)(Maximum - Minimum); } }
        private RectangleF Track { get { return new RectangleF(24, 54, Width - 48, 8); } }

        protected override bool Step()
        {
            float f = Frac;
            if (pos < 0f) pos = f;
            float np = Ui.Approach(pos, f, 0.35f, 0.001f);
            float nt = Ui.Approach(thumb, drag ? 1f : (hot ? 0.6f : 0f), 0.3f);
            bool ch = np != pos || nt != thumb;
            pos = np; thumb = nt;
            return ch;
        }

        private static Color BarColor(float f)
        {
            if (f < 0.7f) return Ui.Mix(Ui.Cyan, Ui.Amber, f / 0.7f);
            return Ui.Mix(Ui.Amber, Ui.Red, (f - 0.7f) / 0.3f);
        }

        private void SetFromX(int x)
        {
            float f = (x - Track.Left) / Track.Width;
            Value = Minimum + (int)Math.Round(f * (Maximum - Minimum));
        }

        protected override void OnMouseEnter(EventArgs e) { hot = true; base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { if (!drag) hot = false; base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { Focus(); drag = true; SetFromX(e.X); }
            base.OnMouseDown(e);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (drag) SetFromX(e.X);
            base.OnMouseMove(e);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (drag)
            {
                drag = false;
                hot = ClientRectangle.Contains(e.Location);
                if (Committed != null) Committed(this, EventArgs.Empty);
            }
            base.OnMouseUp(e);
        }
        protected override bool IsInputKey(Keys k)
        {
            return k == Keys.Left || k == Keys.Right || k == Keys.Up || k == Keys.Down || base.IsInputKey(k);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Down) { Value = val - Step_; e.Handled = true; }
            else if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Up) { Value = val + Step_; e.Handled = true; }
            base.OnKeyDown(e);
        }
        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right || e.KeyCode == Keys.Up || e.KeyCode == Keys.Down)
            {
                if (Committed != null) Committed(this, EventArgs.Empty);
            }
            base.OnKeyUp(e);
        }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Ui.Quality(g);
            g.Clear(Ui.Bg);
            RectangleF card = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Ui.FillRound(g, Ui.Card, card, 14);
            Ui.StrokeRound(g, Focused ? Ui.Alpha(Ui.Cyan, 140) : Ui.Line, 1f, card, 14);

            float p = pos < 0f ? Frac : pos;
            Color col = BarColor(p);
            Ui.Str(g, "KECEPATAN KUSTOM", Ui.F(8f, true), Ui.Muted, new RectangleF(18, 10, 260, 20), StringAlignment.Near);
            Ui.Str(g, valueText, Ui.F(10f, true), col, new RectangleF(Width - 260, 10, 242, 20), StringAlignment.Far);

            RectangleF t = Track;
            Ui.FillRound(g, Color.FromArgb(36, 40, 52), t, 4);
            float fw = Math.Max(2f, t.Width * p);
            RectangleF fr = new RectangleF(t.X, t.Y, fw, t.Height);
            using (GraphicsPath gp = Ui.Round(fr, 4))
            using (LinearGradientBrush lg = new LinearGradientBrush(new RectangleF(t.X, t.Y, Math.Max(fw, 2f), t.Height), Ui.Cyan, col, 0f))
                g.FillPath(lg, gp);

            float tx = t.X + t.Width * p, ty = t.Y + t.Height / 2f;
            float rad = 9f + thumb * 2f;
            using (SolidBrush glow = new SolidBrush(Ui.Alpha(col, (int)(70 * thumb))))
                g.FillEllipse(glow, tx - rad - 6, ty - rad - 6, (rad + 6) * 2, (rad + 6) * 2);
            using (SolidBrush wb = new SolidBrush(Color.White))
                g.FillEllipse(wb, tx - rad, ty - rad, rad * 2, rad * 2);
            using (Pen pn = new Pen(col, 3f))
                g.DrawEllipse(pn, tx - rad + 1.5f, ty - rad + 1.5f, rad * 2 - 3, rad * 2 - 3);

            Ui.Str(g, "Geser lalu lepas untuk menerapkan  \u2022  tombol panah juga bisa", Ui.F(7.5f, false), Ui.Muted,
                   new RectangleF(18, Height - 22, Width - 36, 16), StringAlignment.Center);
        }
    }

    // =========================================================================
    // Jendela utama
    // =========================================================================
    public class MainForm : Form
    {
        private static readonly CultureInfo Id = new CultureInfo("id-ID");
        private static readonly string[] PTitle = { "Auto", "Quiet", "Balanced", "Performance", "MAX" };
        private static readonly int[] PPct = { 0, 15, 55, 80, 100 };
        private static readonly string[] PName = { "AUTO", "QUIET", "BALANCED", "PERFORMANCE", "MAX FAN" };
        private static readonly string[] PDesc = {
            "Kecepatan kipas diatur otomatis oleh BIOS sesuai suhu",
            "Senyap untuk tugas ringan, target sekitar 2.500 RPM",
            "Seimbang untuk coding, multitasking, dan browsing",
            "Pendinginan tinggi untuk rendering atau kompilasi berat",
            "Kipas berputar pada kecepatan maksimum" };
        private static readonly Color[] PCol = {
            Ui.Green, Color.FromArgb(64, 170, 255), Ui.Cyan, Color.FromArgb(255, 167, 38), Ui.Red };

        private HeaderBar header;
        private StatusCard status;
        private TachometerGauge gaugeCpu, gaugeGpu;
        private PresetButton[] presets;
        private ModernSlider slider;
        private TextBox txtLog;
        private CheckBox chkRestore;
        private Timer pollTimer;
        private Task chain = Task.FromResult(0);
        private bool polling;
        private int tick;
        private bool isAuto = true;
        private int targetRpm;

        private static string N(int v) { return v.ToString("N0", Id); }
        private static float Spin(int pct) { return pct <= 0 ? 4f : 3f + pct * 0.16f; }

        public MainForm()
        {
            Ui.Start();
            InitializeComponent();
            try
            {
                string[] iconPaths =
                {
                    System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico"),
                    System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\src\app.ico")
                };

                foreach (string iconPath in iconPaths)
                {
                    if (!System.IO.File.Exists(iconPath)) continue;
                    Icon = new Icon(iconPath);
                    break;
                }
            }
            catch (Exception) { }

            Log("Aplikasi siap. Memeriksa koneksi WMI...");
            Task.Factory.StartNew(delegate
            {
                bool ok = HpWmi.IsAvailable();
                UiInvoke(delegate
                {
                    Log(ok ? "WMI hpqBIntM terhubung." : "WMI hpqBIntM tidak ditemukan. Kontrol kipas mungkin tidak didukung model ini.");
                });
            });

            pollTimer = new Timer();
            pollTimer.Interval = 1000;
            pollTimer.Tick += delegate { Poll(); };
            pollTimer.Start();
            Poll();
        }

        private void InitializeComponent()
        {
            Text = "HP Victus 15 Fan Control";
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(580, 690);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            BackColor = Ui.Bg;
            ForeColor = Ui.Text;
            Font = new Font("Segoe UI", 9F);
            DoubleBuffered = true;

            header = new HeaderBar { Location = new Point(16, 12) };
            status = new StatusCard { Location = new Point(16, 76) };
            status.SetMode("AUTO", PDesc[0], "AUTO", Ui.Green, Spin(0));

            gaugeCpu = new TachometerGauge("KIPAS CPU", Color.FromArgb(0, 113, 197)) { Location = new Point(16, 188) };
            gaugeGpu = new TachometerGauge("KIPAS GPU", Color.FromArgb(118, 185, 0)) { Location = new Point(294, 188) };

            Label lblPreset = SectionLabel("PRESET KECEPATAN", 18, 368);
            presets = new PresetButton[5];
            for (int i = 0; i < 5; i++)
            {
                int idx = i;
                int pct = PPct[i];
                string sub = i == 0 ? "Default BIOS" : (pct >= 100 ? "~" + N(HpWmi.MaxRpm) + " RPM" : "~" + N(HpWmi.RpmFromPercent(pct)) + " RPM");
                presets[i] = new PresetButton
                {
                    Title = PTitle[i], Sub = sub, Accent = PCol[i], Icon = i,
                    Location = new Point(16 + i * 111, 386)
                };
                presets[i].Click += delegate { OnPreset(idx); };
            }

            slider = new ModernSlider { Location = new Point(16, 484) };
            slider.ValueChanged += delegate { UpdateSliderText(); };
            slider.Committed += delegate { OnSliderCommit(); };
            UpdateSliderText();

            Label lblLog = SectionLabel("LOG FIRMWARE & STATUS", 18, 576);
            Panel logHost = new Panel { Location = new Point(16, 594), Size = new Size(548, 58), BackColor = Color.FromArgb(16, 17, 21), Padding = new Padding(10, 6, 6, 6) };
            txtLog = new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None,
                ScrollBars = ScrollBars.Vertical, BackColor = Color.FromArgb(16, 17, 21),
                ForeColor = Color.FromArgb(118, 255, 3), Font = new Font("Consolas", 8.5F)
            };
            logHost.Controls.Add(txtLog);

            chkRestore = new CheckBox
            {
                Text = "Kembalikan ke Auto saat aplikasi ditutup", Checked = true, AutoSize = true,
                ForeColor = Ui.Muted, Location = new Point(18, 660), Cursor = Cursors.Hand
            };

            Controls.Add(header);
            Controls.Add(status);
            Controls.Add(gaugeCpu);
            Controls.Add(gaugeGpu);
            Controls.Add(lblPreset);
            foreach (PresetButton p in presets) Controls.Add(p);
            Controls.Add(slider);
            Controls.Add(lblLog);
            Controls.Add(logHost);
            Controls.Add(chkRestore);

            SelectPreset(0);

            // Fade-in saat dibuka
            Opacity = 0;
            Action fade = null;
            fade = delegate
            {
                Opacity = Math.Min(1.0, Opacity + 0.12);
                if (Opacity >= 1.0) Ui.Tick -= fade;
            };
            Ui.Tick += fade;
        }

        private static Label SectionLabel(string text, int x, int y)
        {
            return new Label { Text = text, AutoSize = true, Location = new Point(x, y), ForeColor = Ui.Muted, Font = new Font("Segoe UI", 8F, FontStyle.Bold) };
        }

        // ---------- Aksi ----------

        private void RunFan(string message, Func<string> work, Action onSuccess)
        {
            Log(message);
            chain = chain.ContinueWith(delegate(Task t)
            {
                string err = work();
                UiInvoke(delegate
                {
                    if (err == null) onSuccess();
                    else Log("-> Gagal: " + err);
                });
            });
        }

        private void OnPreset(int i)
        {
            if (i == 0) ApplyAuto();
            else ApplyPercent(PPct[i], i);
        }

        private void ApplyAuto()
        {
            RunFan("Mengembalikan ke mode AUTO (default BIOS)...", delegate { return HpWmi.ResetToAuto(); }, delegate
            {
                isAuto = true;
                targetRpm = 0;
                SelectPreset(0);
                status.SetMode(PName[0], PDesc[0], "AUTO", PCol[0], Spin(0));
                Log("-> Mode Auto aktif.");
            });
        }

        private void ApplyPercent(int pct, int presetIdx)
        {
            RunFan("Menerapkan kecepatan " + pct + "%...", delegate { return HpWmi.SetPercent(pct); }, delegate
            {
                isAuto = false;
                targetRpm = HpWmi.RpmFromPercent(pct);
                SelectPreset(presetIdx);
                string name = presetIdx >= 0 ? PName[presetIdx] : "KUSTOM " + pct + "%";
                string desc = presetIdx >= 0 ? PDesc[presetIdx] : "Target manual " + pct + "% (sekitar " + N(targetRpm) + " RPM)";
                Color col = presetIdx >= 0 ? PCol[presetIdx] : Color.FromArgb(186, 104, 255);
                status.SetMode(name, desc, pct >= 100 ? "MAX" : pct + "%", col, Spin(pct));
                if (presetIdx >= 0) slider.Value = Math.Max(slider.Minimum, pct);
                Log("-> OK: " + pct + "% (~" + N(targetRpm) + " RPM).");
            });
        }

        private void OnSliderCommit()
        {
            int pct = slider.Value;
            ApplyPercent(pct, pct >= 100 ? 4 : -1);
        }

        private void SelectPreset(int idx)
        {
            for (int i = 0; i < presets.Length; i++) presets[i].Selected = (i == idx);
        }

        private void UpdateSliderText()
        {
            int pct = slider.Value;
            slider.ValueText = pct >= 100 ? "MAX  \u2022  ~" + N(HpWmi.MaxRpm) + " RPM" : pct + "%  \u2022  ~" + N(HpWmi.RpmFromPercent(pct)) + " RPM";
        }

        // ---------- Polling sensor (di thread latar, UI tidak membeku) ----------

        private void Poll()
        {
            if (polling) return;
            polling = true;
            bool doTemp = (tick++ % 4) == 0;
            Task.Factory.StartNew(delegate
            {
                int s0, s1;
                int r0 = HpWmi.ReadFanRpm(0, out s0);
                int r1 = HpWmi.ReadFanRpm(1, out s1);
                int ct = -2;
                HpWmi.GpuTelemetry gpu = new HpWmi.GpuTelemetry { TemperatureC = -1, PowerWatts = -1f };
                float cpuWatts = -1f;
                if (doTemp)
                {
                    ct = HpWmi.ReadCpuTemp();
                    gpu = HpWmi.ReadGpuTelemetry();
                    cpuWatts = HpWmi.ReadCpuPowerWatts();
                }
                UiInvoke(delegate
                {
                    ShowRpm(gaugeCpu, r0, s0, 0);
                    ShowRpm(gaugeGpu, r1, s1, 50);
                    if (doTemp)
                    {
                        gaugeCpu.SetTemp(ct);
                        gaugeGpu.SetTemp(gpu.TemperatureC);
                        status.SetPower(cpuWatts, gpu.PowerWatts);
                    }
                    polling = false;
                });
            });
        }

        // Tanpa jitter acak: bila sensor tidak ada, gunakan target dari mode aktif.
        private void ShowRpm(TachometerGauge g, int rpm, int src, int offset)
        {
            if (src > 0) g.SetRpm(rpm);
            else if (!isAuto && targetRpm > 0) g.SetRpm(targetRpm - offset);
            else g.SetRpm(-1);
        }

        // ---------- Util ----------

        private void UiInvoke(Action a)
        {
            try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); }
            catch (Exception) { }
        }

        private void Log(string msg)
        {
            if (txtLog.TextLength > 8000) txtLog.Text = txtLog.Text.Substring(4000);
            txtLog.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg + Environment.NewLine);
        }

        protected override void OnResize(EventArgs e)
        {
            Ui.Paused = WindowState == FormWindowState.Minimized;
            base.OnResize(e);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (chkRestore != null && chkRestore.Checked && !isAuto)
            {
                try { HpWmi.ResetToAuto(); } catch (Exception) { }
            }
            base.OnFormClosing(e);
        }

        [STAThread]
        public static void Main()
        {
            bool isAdmin;
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                isAdmin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);

            if (!isAdmin)
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        UseShellExecute = true,
                        WorkingDirectory = Environment.CurrentDirectory,
                        FileName = Application.ExecutablePath,
                        Verb = "runas"
                    });
                }
                catch (Exception)
                {
                    MessageBox.Show("Aplikasi ini membutuhkan izin Administrator untuk mengatur kipas BIOS.", "Izin Diperlukan", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
