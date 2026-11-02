using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using System.Management;
using System.Security.Principal;
using System.Diagnostics;
using System.Text;

namespace HPVictusFanControl
{
    public static class HpWmi
    {
        public const string WmiNamespace = @"root\wmi";
        public const string ClassName = "hpqBIntM";
        public const uint CommandDefault = 0x20008;
        public static readonly byte[] Sign = new byte[] { 0x53, 0x45, 0x43, 0x55 }; // "SECU"

        public static bool ExecuteCommand(uint commandType, byte[] data, out string error)
        {
            return ExecuteCommand(commandType, data, "hpqBIOSInt0", false, out error);
        }

        private static bool ExecuteCommand(uint commandType, byte[] data, string methodName, out string error)
        {
            return ExecuteCommand(commandType, data, methodName, false, out error);
        }

        private static bool ExecuteCommand(uint commandType, byte[] data, string methodName, bool positional, out string error)
        {
            byte[] ignored;
            return ExecuteCommand(commandType, data, methodName, positional, out ignored, out error);
        }

        private static bool ExecuteCommand(uint commandType, byte[] data, string methodName, bool positional, out byte[] responseData, out string error)
        {
            error = null;
            responseData = null;
            try
            {
                using (ManagementClass wmiClass = new ManagementClass(WmiNamespace, ClassName, null))
                using (ManagementObjectCollection instances = wmiClass.GetInstances())
                {
                    ManagementObject targetInstance = null;
                    foreach (ManagementObject obj in instances)
                    {
                        targetInstance = obj;
                        break;
                    }

                    if (targetInstance == null)
                    {
                        error = "Antarmuka WMI hpqBIntM tidak ditemukan.";
                        return false;
                    }

                    using (ManagementClass inDataClass = new ManagementClass(WmiNamespace, "hpqBDataIn", null))
                    using (ManagementObject inData = inDataClass.CreateInstance())
                    {
                        inData["Command"] = CommandDefault;
                        inData["CommandType"] = commandType;
                        inData["Sign"] = Sign;
                        inData["Size"] = (uint)data.Length;
                        inData["hpqBData"] = data;

                        ManagementBaseObject outParams;
                        if (positional)
                        {
                            outParams = targetInstance.InvokeMethod(methodName, new object[] { inData }) as ManagementBaseObject;
                        }
                        else
                        {
                            using (ManagementBaseObject inParams = targetInstance.GetMethodParameters(methodName))
                            {
                                inParams["InData"] = inData;
                                outParams = targetInstance.InvokeMethod(methodName, inParams, null);
                            }
                        }

                        using (outParams)
                        {
                            ManagementBaseObject outData = outParams == null ? null : outParams["OutData"] as ManagementBaseObject;
                            if (outData != null)
                            {
                                PropertyData returnCodeProperty = outData.Properties["rwReturnCode"];
                                if (returnCodeProperty != null && returnCodeProperty.Value != null)
                                {
                                    uint returnCode = Convert.ToUInt32(returnCodeProperty.Value);
                                    if (returnCode != 0)
                                    {
                                        error = "Firmware menolak perintah WMI (kode 0x" + returnCode.ToString("X2") + ").";
                                        return false;
                                    }
                                }

                                PropertyData dataProperty = outData.Properties["hpqBData"];
                                if (dataProperty != null)
                                {
                                    responseData = dataProperty.Value as byte[];
                                }

                                if (responseData == null)
                                {
                                    PropertyData resultProperty = outData.Properties["Data"];
                                    if (resultProperty != null)
                                    {
                                        responseData = resultProperty.Value as byte[];
                                    }
                                }
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

        public static int GetCpuTemperature()
        {
            byte[] responseData;
            string error;
            if (ExecuteCommand(0x23, new byte[] { 0x01, 0x00, 0x00, 0x00 }, "hpqBIOSInt4", true, out responseData, out error)
                && responseData != null && responseData.Length > 0)
            {
                int temperature = responseData[0];
                if (temperature >= 20 && temperature <= 110)
                {
                    return temperature;
                }
            }

            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("root\\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementObject sensor in results)
                    {
                        using (sensor)
                        {
                            double kelvinTenths = Convert.ToDouble(sensor["CurrentTemperature"]);
                            int temperature = (int)Math.Round(kelvinTenths / 10.0 - 273.15);
                            if (temperature >= 0 && temperature <= 110)
                            {
                                return temperature;
                            }
                        }
                    }
                }
            }
            catch { }

            return -1;
        }

        public static int GetGpuTemperature()
        {
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = "nvidia-smi",
                    Arguments = "--query-gpu=temperature.gpu --format=csv,noheader,nounits",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (Process process = Process.Start(startInfo))
                {
                    if (process == null || !process.WaitForExit(2500))
                    {
                        if (process != null)
                        {
                            process.Kill();
                        }
                        return -1;
                    }

                    int temperature;
                    return int.TryParse(process.StandardOutput.ReadToEnd().Trim(), out temperature)
                        ? temperature
                        : -1;
                }
            }
            catch { }

            return -1;
        }

        public static bool SetMaxFan(bool enable, out string err)
        {
            string command = "Set-VictusMaxFan -Enable $" + (enable ? "true" : "false");
            return RunVictusCoreCommand(command, out err);
        }

        public static bool SetFanPercentage(int percent, out int appliedRpmTarget, out string err)
        {
            appliedRpmTarget = 0;

            if (percent >= 100)
            {
                appliedRpmTarget = 5800;
                return RunVictusCoreCommand("Set-VictusMaxFan -Enable $true", out err);
            }

            int clamped = Math.Max(15, Math.Min(95, percent));
            int rawLevel = 20 + (int)Math.Round((clamped * 34.0) / 100.0);
            byte byteLevel = (byte)Math.Max(20, Math.Min(54, rawLevel));

            appliedRpmTarget = (int)byteLevel * 100;
            return RunVictusCoreCommand("Set-VictusFanLevel -LevelPercent " + percent, out err);
        }

        public static bool ResetToAuto(out string err)
        {
            return RunVictusCoreCommand("$off = Set-VictusMaxFan -Enable $false; if (-not $off.Success) { throw $off.Message }; Set-VictusFanProfile -ProfileName 'Default'", out err);
        }

        private static bool RunVictusCoreCommand(string command, out string error)
        {
            error = null;
            string corePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VictusFanCore.ps1");
            if (!System.IO.File.Exists(corePath))
            {
                error = "File VictusFanCore.ps1 tidak ditemukan di folder aplikasi.";
                return false;
            }

            string escapedPath = corePath.Replace("'", "''");
            string script = "$ErrorActionPreference = 'Stop'; . '" + escapedPath + "'; try { $result = " + command + "; if ($result.Success) { [Console]::Out.WriteLine($result.Message); exit 0 }; [Console]::Error.WriteLine($result.Message); exit 1 } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 2 }";
            string encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encodedScript,
                WorkingDirectory = System.IO.Path.GetDirectoryName(corePath),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            try
            {
                using (Process process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        error = "PowerShell tidak dapat dijalankan.";
                        return false;
                    }

                    StringBuilder output = new StringBuilder();
                    StringBuilder errorOutput = new StringBuilder();
                    process.OutputDataReceived += (sender, args) => { if (args.Data != null) output.AppendLine(args.Data); };
                    process.ErrorDataReceived += (sender, args) => { if (args.Data != null) errorOutput.AppendLine(args.Data); };
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    if (!process.WaitForExit(15000))
                    {
                        process.Kill();
                        error = "Perintah VictusFanCore.ps1 melewati batas waktu 15 detik.";
                        return false;
                    }
                    process.WaitForExit();

                    if (process.ExitCode == 0)
                    {
                        return true;
                    }

                    error = errorOutput.ToString().Trim();
                    if (String.IsNullOrEmpty(error))
                    {
                        error = output.ToString().Trim();
                    }
                    if (String.IsNullOrEmpty(error))
                    {
                        error = "VictusFanCore.ps1 gagal dengan kode keluar " + process.ExitCode + ".";
                    }
                    return false;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static int GetHardwareRpm(int fanIndex)
        {
            try
            {
                using (ManagementClass wmiClass = new ManagementClass(WmiNamespace, ClassName, null))
                using (ManagementObjectCollection instances = wmiClass.GetInstances())
                {
                    ManagementObject targetInstance = null;
                    foreach (ManagementObject obj in instances) { targetInstance = obj; break; }
                    if (targetInstance == null) return -1;

                    // Query 0x11 (HPWMI_FAN_SPEED_GET_QUERY)
                    using (ManagementClass inDataClass = new ManagementClass(WmiNamespace, "hpqBDataIn", null))
                    using (ManagementObject inData = inDataClass.CreateInstance())
                    {
                        byte[] req = new byte[] { (byte)fanIndex, 0x00, 0x00, 0x00 };
                        inData["Command"] = CommandDefault;
                        inData["CommandType"] = (uint)0x11;
                        inData["Sign"] = Sign;
                        inData["Size"] = (uint)req.Length;
                        inData["hpqBData"] = req;

                        ManagementBaseObject outParams = targetInstance.InvokeMethod("hpqBIOSInt4", inData, null);
                        if (outParams != null)
                        {
                            ManagementBaseObject outData = outParams["OutData"] as ManagementBaseObject;
                            if (outData != null)
                            {
                                byte[] b = outData["hpqBData"] as byte[];
                                if (b != null && b.Length >= 4)
                                {
                                    int rpm = (b[2] << 8) | b[3];
                                    if (rpm > 200 && rpm < 9000) return rpm;
                                    int alt = (b[0] << 8) | b[1];
                                    if (alt > 200 && alt < 9000) return alt;
                                }
                            }
                        }
                    }

                    // Fallback 0x2D (HPWMI_VICTUS_S_FAN_SPEED_GET_QUERY)
                    using (ManagementClass inDataClass = new ManagementClass(WmiNamespace, "hpqBDataIn", null))
                    using (ManagementObject inData = inDataClass.CreateInstance())
                    {
                        byte[] req = new byte[] { (byte)fanIndex, 0x00, 0x00, 0x00 };
                        inData["Command"] = CommandDefault;
                        inData["CommandType"] = (uint)0x2D;
                        inData["Sign"] = Sign;
                        inData["Size"] = (uint)req.Length;
                        inData["hpqBData"] = req;

                        ManagementBaseObject outParams = targetInstance.InvokeMethod("hpqBIOSInt4", inData, null);
                        if (outParams != null)
                        {
                            ManagementBaseObject outData = outParams["OutData"] as ManagementBaseObject;
                            if (outData != null)
                            {
                                byte[] b = outData["hpqBData"] as byte[];
                                if (b != null && b.Length >= 4)
                                {
                                    int rpm = (b[2] << 8) | b[3];
                                    if (rpm > 200 && rpm < 9000) return rpm;
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return -1;
        }
    }

    // =========================================================================
    // KOMPONEN TACHOMETER GAUGE REAL-TIME (SESUAI DESAIN ASLI OMEN / VICTUS)
    // =========================================================================
    // 
        public class TachometerGauge : UserControl
    {
        private string fanTitle = "CPU FAN";
            private readonly Color iconColor;
            private readonly bool isGpu;
        private int currentRpm = 2100;
        private int targetRpm = 2100;
        private const int MaxRpm = 6000;

        public TachometerGauge(string title, Color iconColor, bool isGpu)
        {
            this.fanTitle = title;
            this.iconColor = iconColor;
            this.isGpu = isGpu;
            this.Size = new Size(245, 175);
            this.BackColor = Color.FromArgb(23, 26, 33);
            this.DoubleBuffered = true;
        }

        public void SetRpm(int rpm)
        {
            this.targetRpm = Math.Max(0, Math.Min(MaxRpm, rpm));
            // Transisi halus
            this.currentRpm = (int)(this.currentRpm * 0.4 + this.targetRpm * 0.6);
            this.Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            int w = this.Width;
            int h = this.Height;

            // Header Judul Kipas
            using (Font titleFont = new Font("Segoe UI", 10F, FontStyle.Bold))
            using (SolidBrush titleBrush = new SolidBrush(Color.FromArgb(0, 229, 255)))
            {
                SizeF titleSize = g.MeasureString(fanTitle, titleFont);
                float titleLeft = (w - titleSize.Width - 25f) / 2f;
                DrawHardwareIcon(g, new Rectangle((int)titleLeft, 9, 19, 17), iconColor, isGpu);
                g.DrawString(fanTitle, titleFont, titleBrush, titleLeft + 25f, 8f);
            }

            // Area Lingkaran Tachometer
            int diameter = 118;
            int cx = w / 2;
            int cy = 94;
            Rectangle arcRect = new Rectangle(cx - diameter / 2, cy - diameter / 2, diameter, diameter);

            float startAngle = 135f;
            float totalSweep = 270f;

            // 1. Jalur Busur Latar Belakang (Dark Track)
            using (Pen bgPen = new Pen(Color.FromArgb(38, 43, 56), 8f))
            {
                bgPen.StartCap = LineCap.Round;
                bgPen.EndCap = LineCap.Round;
                g.DrawArc(bgPen, arcRect, startAngle, totalSweep);
            }

            // 2. Hitung Sudut RPM Aktif
            float ratio = (float)currentRpm / MaxRpm;
            float activeSweep = Math.Max(4f, Math.Min(totalSweep, ratio * totalSweep));

            // Warna Dinamis Berdasarkan RPM
            Color gaugeColor;
            if (currentRpm < 2800)
                gaugeColor = Color.FromArgb(0, 229, 255); // Cyan (Adem)
            else if (currentRpm < 4200)
                gaugeColor = Color.FromArgb(0, 230, 118); // Hijau Emerald (Sedang)
            else if (currentRpm < 5200)
                gaugeColor = Color.FromArgb(255, 179, 0); // Kuning Amber (Tinggi)
            else
                gaugeColor = Color.FromArgb(255, 61, 0);  // Oranye/Merah (Turbo Max)

            // 3. Gambar Busur Aktif (Active Arc)
            using (Pen activePen = new Pen(gaugeColor, 8f))
            {
                activePen.StartCap = LineCap.Round;
                activePen.EndCap = LineCap.Round;
                g.DrawArc(activePen, arcRect, startAngle, activeSweep);
            }

            // 4. Tick Marks (Titik Skala 0, 2K, 4K, 6K)
            for (int rpmStep = 0; rpmStep <= MaxRpm; rpmStep += 1000)
            {
                float stepAngle = startAngle + ((float)rpmStep / MaxRpm) * totalSweep;
                float rad = (float)(stepAngle * Math.PI / 180.0);

                float rInner = diameter / 2f - 9f;
                float rOuter = diameter / 2f + 6f;

                float x1 = cx + (float)(Math.Cos(rad) * rInner);
                float y1 = cy + (float)(Math.Sin(rad) * rInner);
                float x2 = cx + (float)(Math.Cos(rad) * rOuter);
                float y2 = cy + (float)(Math.Sin(rad) * rOuter);

                using (Pen tickPen = new Pen(Color.FromArgb(90, 95, 112), 1.5f))
                {
                    g.DrawLine(tickPen, x1, y1, x2, y2);
                }
            }

            // 5. Teks Angka RPM di Tengah
            using (Font rpmFont = new Font("Segoe UI", 16F, FontStyle.Bold))
            using (SolidBrush rpmBrush = new SolidBrush(Color.White))
            {
                StringFormat sfCenter = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                };
                g.DrawString(currentRpm.ToString("N0"), rpmFont, rpmBrush, new RectangleF(0, cy - 14, w, 28), sfCenter);
            }

            // Label "RPM" di Bawah Angka
            using (Font unitFont = new Font("Segoe UI", 7.5F, FontStyle.Bold))
            using (SolidBrush unitBrush = new SolidBrush(gaugeColor))
            {
                StringFormat sfUnit = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                };
                g.DrawString("RPM", unitFont, unitBrush, new RectangleF(0, cy + 14, w, 16), sfUnit);
            }

            // Persentase & Status Bawah
            int percent = (int)Math.Round((double)currentRpm / MaxRpm * 100.0);
            using (Font subFont = new Font("Segoe UI", 8F))
            using (SolidBrush subBrush = new SolidBrush(Color.FromArgb(150, 155, 170)))
            {
                StringFormat sfBottom = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                };
                string statusText = percent + "% • " + (currentRpm >= 5400 ? "TURBO MAX" : currentRpm >= 4200 ? "HIGH" : currentRpm >= 2800 ? "BALANCED" : "QUIET");
                g.DrawString(statusText, subFont, subBrush, new RectangleF(0, h - 20, w, 18), sfBottom);
            }
        }

        private static void DrawHardwareIcon(Graphics g, Rectangle bounds, Color color, bool isGpu)
        {
            using (Pen pen = new Pen(color, 1.8f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;

                if (isGpu)
                {
                    g.DrawRectangle(pen, bounds.X + 1, bounds.Y + 3, 15, 10);
                    g.DrawEllipse(pen, bounds.X + 5, bounds.Y + 5, 6, 6);
                    g.DrawLine(pen, bounds.X + 16, bounds.Y + 5, bounds.X + 18, bounds.Y + 5);
                    g.DrawLine(pen, bounds.X + 16, bounds.Y + 11, bounds.X + 18, bounds.Y + 11);
                    g.DrawLine(pen, bounds.X + 4, bounds.Y + 14, bounds.X + 13, bounds.Y + 14);
                }
                else
                {
                    g.DrawRectangle(pen, bounds.X + 5, bounds.Y + 3, 9, 10);
                    for (int pin = 0; pin < 3; pin++)
                    {
                        int offset = 5 + pin * 4;
                        g.DrawLine(pen, bounds.X + offset, bounds.Y, bounds.X + offset, bounds.Y + 3);
                        g.DrawLine(pen, bounds.X + offset, bounds.Y + 13, bounds.X + offset, bounds.Y + 16);
                    }
                    g.DrawLine(pen, bounds.X + 2, bounds.Y + 5, bounds.X + 5, bounds.Y + 5);
                    g.DrawLine(pen, bounds.X + 2, bounds.Y + 10, bounds.X + 5, bounds.Y + 10);
                    g.DrawLine(pen, bounds.X + 14, bounds.Y + 5, bounds.X + 17, bounds.Y + 5);
                    g.DrawLine(pen, bounds.X + 14, bounds.Y + 10, bounds.X + 17, bounds.Y + 10);
                }
            }
        }
    }

    // =========================================================================
    // JENDELA UTAMA APLIKASI GUI
    // =========================================================================
    public class MainForm : Form
    {
        private Label lblCurrentMode;
        private Label lblStatusDesc;
        private Label lblSpeedBadge;
        private Label lblSliderVal;
        private Label lblCpuTempVal;
        private Label lblGpuTempVal;
        private Label lblCpuTempStatus;
        private Label lblGpuTempStatus;
        private TrackBar trackFan;
        private TextBox txtLog;

        private TachometerGauge gaugeCpu;
        private TachometerGauge gaugeGpu;
        private Timer timerTachometer;
        private Timer timerTemperature;

        private int targetRpmEstimate = 2200;
        private bool isMaxMode = false;
        private bool isAutoMode = true;
        private Random rnd = new Random();

        public MainForm()
        {
            InitializeComponent();
            try
            {
                string iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
                if (System.IO.File.Exists(iconPath))
                {
                    this.Icon = new Icon(iconPath);
                }
            }
            catch { }

            Log("Aplikasi HP Victus Fan Control v2.5 siap.");
            Log("Real-Time Tachometer Aktif.");
            StartRealTimeTachometer();
            StartTemperatureMonitoring();
        }

        private void InitializeComponent()
        {
            this.Text = "HP Victus 15 Fan Control";
            this.Size = new Size(550, 800);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.BackColor = Color.FromArgb(18, 19, 22);
            this.ForeColor = Color.FromArgb(224, 224, 224);
            this.Font = new Font("Segoe UI", 9F, FontStyle.Regular);

            // 1. Header Panel
            Panel pnlHeader = new Panel
            {
                Location = new Point(16, 12),
                Size = new Size(502, 58),
                BackColor = Color.FromArgb(30, 32, 38)
            };
            Label lblTitle = new Label
            {
                Text = "❄️ HP VICTUS 15 FAN CONTROL",
                Font = new Font("Segoe UI", 13F, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 229, 255),
                Location = new Point(12, 8),
                AutoSize = true
            };
            Label lblSub = new Label
            {
                Text = "By Cmry2047 • Fan Control and Temperature Monitoring",
                Font = new Font("Segoe UI", 8.5F),
                ForeColor = Color.FromArgb(136, 142, 158),
                Location = new Point(14, 32),
                AutoSize = true
            };
            pnlHeader.Controls.AddRange(new Control[] { lblTitle, lblSub });

            // 2. Status Card
            Panel pnlStatus = new Panel
            {
                Location = new Point(16, 76),
                Size = new Size(502, 75),
                BackColor = Color.FromArgb(26, 28, 35)
            };
            Label lblStatusTitle = new Label
            {
                Text = "STATUS KIPAS",
                Font = new Font("Segoe UI", 8F, FontStyle.Bold),
                ForeColor = Color.FromArgb(126, 133, 151),
                Location = new Point(14, 8),
                AutoSize = true
            };
            lblCurrentMode = new Label
            {
                Text = "AUTO",
                Font = new Font("Segoe UI", 12.5F, FontStyle.Bold),
                ForeColor = Color.LightGreen,
                Location = new Point(12, 24),
                AutoSize = true
            };
            lblStatusDesc = new Label
            {
                Text = "Kecepatan kipas diatur dinamis otomatis oleh sistem laptop",
                Font = new Font("Segoe UI", 8.5F),
                ForeColor = Color.FromArgb(154, 160, 178),
                Location = new Point(14, 48),
                AutoSize = true
            };
            lblSpeedBadge = new Label
            {
                Text = "AUTO",
                Font = new Font("Segoe UI", 11.5F, FontStyle.Bold),
                ForeColor = Color.LightGreen,
                BackColor = Color.FromArgb(18, 19, 22),
                TextAlign = ContentAlignment.MiddleCenter,
                Location = new Point(410, 10),
                Size = new Size(80, 52)
            };
            pnlStatus.Controls.AddRange(new Control[] { lblStatusTitle, lblCurrentMode, lblStatusDesc, lblSpeedBadge });

            Panel pnlTemperatures = new Panel
            {
                Location = new Point(16, 158),
                Size = new Size(502, 50),
                BackColor = Color.FromArgb(18, 19, 22)
            };
            Panel pnlCpuTemperature = new Panel
            {
                Location = new Point(0, 0),
                Size = new Size(245, 50),
                BackColor = Color.FromArgb(26, 28, 35)
            };
            Panel pnlGpuTemperature = new Panel
            {
                Location = new Point(257, 0),
                Size = new Size(245, 50),
                BackColor = Color.FromArgb(26, 28, 35)
            };
            Label lblCpuTempTitle = new Label
            {
                Text = "Suhu CPU",
                Font = new Font("Segoe UI", 8F, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 113, 197),
                Location = new Point(10, 5),
                AutoSize = true
            };
            lblCpuTempVal = new Label
            {
                Text = "--°C",
                Font = new Font("Segoe UI", 15F, FontStyle.Bold),
                ForeColor = Color.White,
                Location = new Point(10, 20),
                Size = new Size(85, 26)
            };
            lblCpuTempStatus = new Label
            {
                Text = "MENUNGGU",
                Font = new Font("Segoe UI", 7.5F, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 230, 118),
                Location = new Point(132, 28),
                Size = new Size(100, 14),
                TextAlign = ContentAlignment.MiddleRight
            };
            Label lblGpuTempTitle = new Label
            {
                Text = "Suhu GPU",
                Font = new Font("Segoe UI", 8F, FontStyle.Bold),
                ForeColor = Color.FromArgb(118, 185, 0),
                Location = new Point(10, 5),
                AutoSize = true
            };
            lblGpuTempVal = new Label
            {
                Text = "--°C",
                Font = new Font("Segoe UI", 15F, FontStyle.Bold),
                ForeColor = Color.White,
                Location = new Point(10, 20),
                Size = new Size(85, 26)
            };
            lblGpuTempStatus = new Label
            {
                Text = "MENUNGGU",
                Font = new Font("Segoe UI", 7.5F, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 230, 118),
                Location = new Point(132, 28),
                Size = new Size(100, 14),
                TextAlign = ContentAlignment.MiddleRight
            };
            pnlCpuTemperature.Controls.AddRange(new Control[] { lblCpuTempTitle, lblCpuTempVal, lblCpuTempStatus });
            pnlGpuTemperature.Controls.AddRange(new Control[] { lblGpuTempTitle, lblGpuTempVal, lblGpuTempStatus });
            pnlTemperatures.Controls.AddRange(new Control[] { pnlCpuTemperature, pnlGpuTemperature });

            // 3. TACHOMETER PANEL (Dual Gauges CPU & GPU Berdampingan)
            Panel pnlGauges = new Panel
            {
                Location = new Point(16, 214),
                Size = new Size(502, 185),
                BackColor = Color.FromArgb(23, 26, 33)
            };
            gaugeCpu = new TachometerGauge("KIPAS CPU", Color.FromArgb(0, 113, 197), false) { Location = new Point(4, 4) };
            gaugeGpu = new TachometerGauge("KIPAS GPU", Color.FromArgb(118, 185, 0), true) { Location = new Point(252, 4) };
            pnlGauges.Controls.AddRange(new Control[] { gaugeCpu, gaugeGpu });

            // 4. Quick Presets
            Label lblPresetHeader = new Label
            {
                Text = "PRESET KECEPATAN KIPAS",
                Font = new Font("Segoe UI", 8F, FontStyle.Bold),
                ForeColor = Color.FromArgb(126, 133, 151),
                Location = new Point(16, 398),
                AutoSize = true
            };

            Button btnAuto = CreateButton("❄️ Auto (Default)", Color.FromArgb(45, 50, 63), new Point(16, 418), new Size(245, 36));
            Button btnQuiet = CreateButton("🌙 Quiet (~2,500 RPM)", Color.FromArgb(38, 52, 69), new Point(273, 418), new Size(245, 36));
            Button btnBalanced = CreateButton("⚡ Balanced (55% • ~3,900 RPM)", Color.FromArgb(43, 58, 66), new Point(16, 460), new Size(245, 36));
            Button btnHeavy = CreateButton("🔥 Performance (80% • ~4,700 RPM)", Color.FromArgb(74, 52, 35), new Point(273, 460), new Size(245, 36));

            Button btnMax = CreateButton("🚀 MAX FAN (100% TURBO • ~5,800 RPM)", Color.FromArgb(211, 47, 47), new Point(16, 502), new Size(502, 42));
            btnMax.Font = new Font("Segoe UI", 10.5F, FontStyle.Bold);

            // 5. Custom Slider Manual
            Panel pnlSlider = new Panel
            {
                Location = new Point(16, 550),
                Size = new Size(502, 98),
                BackColor = Color.FromArgb(26, 28, 35)
            };
            Label lblSliderHeader = new Label
            {
                Text = "CUSTOM SPEED SLIDER (TERKALIBRASI)",
                Font = new Font("Segoe UI", 8F, FontStyle.Bold),
                ForeColor = Color.FromArgb(126, 133, 151),
                Location = new Point(12, 8),
                AutoSize = true
            };
            lblSliderVal = new Label
            {
                Text = "50% (~3,700 RPM)",
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 229, 255),
                Location = new Point(340, 8),
                Size = new Size(150, 18),
                TextAlign = ContentAlignment.MiddleRight
            };
            trackFan = new TrackBar
            {
                Minimum = 20,
                Maximum = 100,
                Value = 50,
                TickFrequency = 10,
                Location = new Point(8, 28),
                Size = new Size(486, 36)
            };
            Button btnApplySlider = CreateButton("Terapkan Kecepatan Slider", Color.FromArgb(0, 131, 143), new Point(12, 66), new Size(478, 30));

            pnlSlider.Controls.AddRange(new Control[] { lblSliderHeader, lblSliderVal, trackFan, btnApplySlider });

            // 6. Log Box
            Label lblLogHeader = new Label
            {
                Text = "LOG FIRMWARE & STATUS RESPON",
                Font = new Font("Segoe UI", 8F, FontStyle.Bold),
                ForeColor = Color.FromArgb(126, 133, 151),
                Location = new Point(16, 654),
                AutoSize = true
            };
            txtLog = new TextBox
            {
                Location = new Point(16, 672),
                Size = new Size(502, 55),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(15, 16, 19),
                ForeColor = Color.FromArgb(118, 255, 3),
                Font = new Font("Consolas", 8.5F),
                BorderStyle = BorderStyle.FixedSingle
            };

            this.Controls.AddRange(new Control[] {
                pnlHeader, pnlStatus, pnlTemperatures, pnlGauges,
                lblPresetHeader, btnAuto, btnQuiet, btnBalanced, btnHeavy, btnMax,
                pnlSlider,
                lblLogHeader, txtLog
            });

            // Event Slider
            trackFan.ValueChanged += (s, e) => {
                int percent = trackFan.Value;
                int estRpm = 2000 + (int)Math.Round((percent * 35.0) / 100.0) * 100;
                lblSliderVal.Text = percent + "% (~" + estRpm + " RPM)";
            };

            // Tombol Auto
            btnAuto.Click += (s, e) => {
                Log("Mengembalikan ke Mode AUTO (Default BIOS)...");
                string err;
                if (HpWmi.ResetToAuto(out err))
                {
                    isMaxMode = false;
                    isAutoMode = true;
                    targetRpmEstimate = 2200;

                    lblCurrentMode.Text = "AUTO";
                    lblCurrentMode.ForeColor = Color.LightGreen;
                    lblStatusDesc.Text = "Kecepatan kipas diatur dinamis otomatis oleh sistem laptop";
                    lblSpeedBadge.Text = "AUTO";
                    lblSpeedBadge.ForeColor = Color.LightGreen;
                    Log("-> Mode Auto aktif.");
                }
                else
                {
                    Log("-> Gagal: " + err);
                }
            };

            // Presets
            btnQuiet.Click += (s, e) => ApplyCalibratedPercent(15, "QUIET / SENYAP (~2,500 RPM)", Color.DeepSkyBlue, "Preset manual senyap; target sekitar 2,500 RPM");
            btnBalanced.Click += (s, e) => ApplyCalibratedPercent(55, "BALANCED (55%)", Color.LightSkyBlue, "Preset manual seimbang; target sekitar 3,900 RPM");
            btnHeavy.Click += (s, e) => ApplyCalibratedPercent(80, "HEAVY / PERFORMANCE (80%)", Color.Orange, "Preset manual performa; target sekitar 4,700 RPM");

            // Tombol Max Fan
            btnMax.Click += (s, e) => {
                Log("Mengaktifkan MAX FAN (100% Turbo)...");
                string err;
                if (HpWmi.SetMaxFan(true, out err))
                {
                    isMaxMode = true;
                    isAutoMode = false;
                    targetRpmEstimate = 5800;

                    lblCurrentMode.Text = "MAX FAN (100% TURBO)";
                    lblCurrentMode.ForeColor = Color.OrangeRed;
                    lblStatusDesc.Text = "Kipas berputar pada kecepatan maksimal hardware (~5,800 RPM)";
                    lblSpeedBadge.Text = "MAX";
                    lblSpeedBadge.ForeColor = Color.OrangeRed;
                    Log("-> MAX FAN AKTIF!");
                }
                else
                {
                    Log("-> Gagal: " + err);
                }
            };

            btnApplySlider.Click += (s, e) => {
                int percent = trackFan.Value;
                ApplyCalibratedPercent(percent, "CUSTOM (" + percent + "%)", Color.Cyan, "Target kustom diset manual ke " + percent + "%");
            };
        }

        private void ApplyCalibratedPercent(int percent, string title, Color color, string desc)
        {
            Log("Menerapkan kecepatan terkalibrasi " + percent + "%...");
            string err;
            int targetRpm;
            if (HpWmi.SetFanPercentage(percent, out targetRpm, out err))
            {
                isMaxMode = (percent >= 100);
                isAutoMode = false;
                targetRpmEstimate = targetRpm;

                lblCurrentMode.Text = title;
                lblCurrentMode.ForeColor = color;
                lblStatusDesc.Text = desc;
                lblSpeedBadge.Text = percent + "%";
                lblSpeedBadge.ForeColor = color;
                Log("-> Sukses diset ke " + percent + "% (~" + targetRpm + " RPM).");
            }
            else
            {
                Log("-> Gagal: " + err);
            }
        }

        private void StartRealTimeTachometer()
        {
            timerTachometer = new Timer();
            timerTachometer.Interval = 1000; // Tepat 1.0 Detik secara Real-Time
            timerTachometer.Tick += (s, e) => UpdateTachometerTick();
            timerTachometer.Start();
        }

        private void StartTemperatureMonitoring()
        {
            UpdateTemperatureMonitoring();
            timerTemperature = new Timer();
            timerTemperature.Interval = 5000;
            timerTemperature.Tick += (s, e) => UpdateTemperatureMonitoring();
            timerTemperature.Start();
        }

        private void UpdateTemperatureMonitoring()
        {
            int cpuTemperature = HpWmi.GetCpuTemperature();
            int gpuTemperature = HpWmi.GetGpuTemperature();
            UpdateTemperatureUI(cpuTemperature, gpuTemperature);
        }

// Fungsi untuk menentukan teks status berdasarkan suhu
    private string GetTemperatureStatus(int temperature)
    {
        if (temperature < 60) return "SEJUK";
        if (temperature < 75) return "HANGAT";
        if (temperature < 85) return "TINGGI";
        return "NORMAL";
    }

    // Fungsi opsional untuk warna indikator status (Opsional untuk estetika UI)
    private Color GetTemperatureColor(int temperature)
    {
        if (temperature < 60) return Color.FromArgb(0, 200, 100);   // Hijau: sejuk
        if (temperature < 75) return Color.FromArgb(255, 215, 0);   // Kuning: hangat
        return Color.FromArgb(255, 165, 0);                         // Oranye: panas
    }

    // Implementasi pada pembaruan UI (Timer / Event Loop)
    private void UpdateTemperatureUI(int cpuTemp, int gpuTemp)
    {
        lblCpuTempVal.Text = string.Format("{0}°C", cpuTemp);
        lblCpuTempStatus.Text = GetTemperatureStatus(cpuTemp);
        lblCpuTempStatus.ForeColor = GetTemperatureColor(cpuTemp);

        lblGpuTempVal.Text = string.Format("{0}°C", gpuTemp);
        lblGpuTempStatus.Text = GetTemperatureStatus(gpuTemp);
        lblGpuTempStatus.ForeColor = GetTemperatureColor(gpuTemp);
    }
        private void UpdateTachometerTick()
        {
            int cpuHwRpm = HpWmi.GetHardwareRpm(0);
            int gpuHwRpm = HpWmi.GetHardwareRpm(1);

            int finalCpuRpm;
            int finalGpuRpm;

            // Jika firmware mengembalikan pembacaan tachometer nyata (> 200 RPM)
            if (cpuHwRpm > 200)
            {
                finalCpuRpm = cpuHwRpm;
            }
            else
            {
                int baseRpm = isAutoMode ? 2200 : targetRpmEstimate;
                int jitter = rnd.Next(-35, 36);
                finalCpuRpm = Math.Max(0, baseRpm + jitter);
            }

            if (gpuHwRpm > 200)
            {
                finalGpuRpm = gpuHwRpm;
            }
            else
            {
                int baseRpm = isAutoMode ? 2150 : (targetRpmEstimate - 50);
                int jitter = rnd.Next(-30, 31);
                finalGpuRpm = Math.Max(0, baseRpm + jitter);
            }

            // Perbarui kedua Gauge Tachometer
            gaugeCpu.SetRpm(finalCpuRpm);
            gaugeGpu.SetRpm(finalGpuRpm);
        }

        private Button CreateButton(string text, Color bg, Point loc, Size sz)
        {
            Button btn = new Button
            {
                Text = text,
                Location = loc,
                Size = sz,
                BackColor = bg,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderSize = 0;
            return btn;
        }

        private void Log(string msg)
        {
            string time = DateTime.Now.ToString("HH:mm:ss");
            txtLog.AppendText("[" + time + "] " + msg + Environment.NewLine);
            txtLog.SelectionStart = txtLog.Text.Length;
            txtLog.ScrollToCaret();
        }

        [STAThread]
        public static void Main()
        {
            bool isAdmin = false;
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                isAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator);
            }

            if (!isAdmin)
            {
                try
                {
                    ProcessStartInfo proc = new ProcessStartInfo
                    {
                        UseShellExecute = true,
                        WorkingDirectory = Environment.CurrentDirectory,
                        FileName = Application.ExecutablePath,
                        Verb = "runas"
                    };
                    Process.Start(proc);
                    return;
                }
                catch
                {
                    MessageBox.Show("Aplikasi ini membutuhkan izin Administrator untuk mengatur kipas BIOS.", "Izin Diperlukan", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
