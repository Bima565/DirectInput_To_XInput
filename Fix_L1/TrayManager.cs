using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Fix_L1
{
    public static class TrayManager
    {
        private static NotifyIcon? notifyIcon;
        private static ContextMenuStrip? contextMenu;
        private static ToolStripMenuItem? statusMenuItem;
        private static ToolStripMenuItem? startupMenuItem;
        private static Thread? uiThread;
        private static volatile bool isInitialized = false;

        public static event Action? OnExitRequested;

        public static void Initialize()
        {
            if (isInitialized) return;

            uiThread = new Thread(RunTrayMessageLoop)
            {
                IsBackground = true,
                Name = "TrayMessageLoop"
            };
            uiThread.SetApartmentState(ApartmentState.STA);
            uiThread.Start();

            // Tunggu sebentar sampai inisialisasi selesai
            int wait = 0;
            while (!isInitialized && wait < 20)
            {
                Thread.Sleep(50);
                wait++;
            }
        }

        private static void RunTrayMessageLoop()
        {
            try
            {
                contextMenu = new ContextMenuStrip();
                contextMenu.Font = new Font("Segoe UI", 9f);

                // 1. Header Aplikasi
                var headerItem = new ToolStripMenuItem("🎮 Fix_L1 - Gamepad Converter")
                {
                    Enabled = false,
                    Font = new Font("Segoe UI", 9f, FontStyle.Bold)
                };
                contextMenu.Items.Add(headerItem);

                // 2. Info Status Controller
                statusMenuItem = new ToolStripMenuItem("○ Stik yang terhubung: 0");
                statusMenuItem.Enabled = false;
                contextMenu.Items.Add(statusMenuItem);

                contextMenu.Items.Add(new ToolStripSeparator());

                // 3. Buka / Sembunyikan Konsol
                var openItem = new ToolStripMenuItem("Buka Menu CMD", null, (s, e) =>
                {
                    ConsoleWindowManager.ShowConsole();
                });
                contextMenu.Items.Add(openItem);

                var hideItem = new ToolStripMenuItem("Tutup Menu CMD (Jalan di Tray)", null, (s, e) =>
                {
                    ConsoleWindowManager.HideConsole();
                });
                contextMenu.Items.Add(hideItem);

                // Shortcut cepat untuk membuka joy.cpl
                var joyCplItem = new ToolStripMenuItem("🎮 Buka Game Controllers (joy.cpl)", null, (s, e) =>
                {
                    JoystickNameManager.OpenJoyCpl();
                });
                contextMenu.Items.Add(joyCplItem);

                contextMenu.Items.Add(new ToolStripSeparator());

                // 4. Toggle Run On Startup
                startupMenuItem = new ToolStripMenuItem("Jalankan saat Startup Windows")
                {
                    Checked = StartupManager.IsStartupEnabled(),
                    CheckOnClick = true
                };
                startupMenuItem.Click += (s, e) =>
                {
                    bool enable = startupMenuItem.Checked;
                    StartupManager.SetStartup(enable);
                    ShowBalloon("Pengaturan Startup", enable ? "Fix_L1 akan berjalan otomatis saat Windows menyala." : "Jalan otomatis saat startup dinonaktifkan.", 1500);
                };
                contextMenu.Items.Add(startupMenuItem);

                contextMenu.Items.Add(new ToolStripSeparator());

                // 5. Tombol Keluar (Exit)
                var exitItem = new ToolStripMenuItem("Exit", null, (s, e) =>
                {
                    Shutdown();
                    OnExitRequested?.Invoke();
                })
                {
                    ForeColor = Color.DarkRed
                };
                contextMenu.Items.Add(exitItem);

                // Buat NotifyIcon
                notifyIcon = new NotifyIcon
                {
                    ContextMenuStrip = contextMenu,
                    Visible = true,
                    Text = "Fix_L1 - Gamepad Converter"
                };

                // Load Icon
                ApplyTrayIcon();

                // Klik ganda atau klik kiri pada tray icon untuk toggle jendela console
                notifyIcon.MouseClick += (s, e) =>
                {
                    if (e.Button == MouseButtons.Left)
                    {
                        ConsoleWindowManager.ToggleVisibility();
                    }
                };

                isInitialized = true;
                Application.Run();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error pada Tray: {ex.Message}");
            }
        }

        private static void ApplyTrayIcon()
        {
            if (notifyIcon == null) return;
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string icoPath = Path.Combine(baseDir, "gamepads.ico");
                if (!File.Exists(icoPath))
                {
                    icoPath = Path.Combine(baseDir, "..", "..", "..", "..", "gamepads.ico");
                    icoPath = Path.GetFullPath(icoPath);
                }

                if (File.Exists(icoPath))
                {
                    notifyIcon.Icon = new Icon(icoPath);
                }
                else
                {
                    notifyIcon.Icon = SystemIcons.Application;
                }
            }
            catch
            {
                notifyIcon.Icon = SystemIcons.Application;
            }
        }

        public static void UpdateStatus(int activeCount, string controllerNames)
        {
            if (notifyIcon == null || !isInitialized) return;

            try
            {
                if (contextMenu?.InvokeRequired == true)
                {
                    contextMenu.BeginInvoke(new Action(() => UpdateStatus(activeCount, controllerNames)));
                    return;
                }

                string statusText = activeCount > 0
                    ? $"● Stik yang terhubung: {controllerNames}"
                    : "○ Stik yang terhubung: 0";

                if (statusMenuItem != null)
                {
                    statusMenuItem.Text = statusText;
                    statusMenuItem.ForeColor = activeCount > 0 ? Color.DarkGreen : Color.Gray;
                }

                string tip = activeCount > 0
                    ? $"Fix_L1: {activeCount} Stik Aktif ({controllerNames})"
                    : "Fix_L1: Tidak ada stik terhubung";

                // Tooltip max 63 karakter pada WinForms
                if (tip.Length > 63) tip = tip.Substring(0, 60) + "...";
                notifyIcon.Text = tip;

                if (startupMenuItem != null)
                {
                    startupMenuItem.Checked = StartupManager.IsStartupEnabled();
                }
            }
            catch { }
        }

        public static void ShowBalloon(string title, string text, int timeoutMs = 2000)
        {
            if (notifyIcon == null || !isInitialized) return;

            try
            {
                notifyIcon.ShowBalloonTip(timeoutMs, title, text, ToolTipIcon.Info);
            }
            catch { }
        }

        public static void Shutdown()
        {
            try
            {
                if (notifyIcon != null)
                {
                    notifyIcon.Visible = false;
                    notifyIcon.Dispose();
                    notifyIcon = null;
                }
                Application.Exit();
            }
            catch { }
        }
    }
}
