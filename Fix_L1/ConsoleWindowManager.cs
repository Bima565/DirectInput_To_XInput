using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Fix_L1
{
    public static class ConsoleWindowManager
    {
        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);

        [DllImport("user32.dll")]
        private static extern bool DeleteMenu(IntPtr hMenu, uint uPosition, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private const uint SC_CLOSE = 0xF060;
        private const uint MF_BYCOMMAND = 0x00000000;

        private const int SW_HIDE = 0;
        private const int SW_RESTORE = 9;
        private const int SW_SHOW = 5;

        private const uint WM_SETICON = 0x0080;
        private const int ICON_SMALL = 0;
        private const int ICON_BIG = 1;

        private static IntPtr consoleHwnd = IntPtr.Zero;
        private static Thread? minimizeWatcherThread;
        private static volatile bool isRunning = true;

        public static IntPtr Handle
        {
            get
            {
                if (consoleHwnd == IntPtr.Zero)
                {
                    consoleHwnd = GetConsoleWindow();
                }
                return consoleHwnd;
            }
        }

        public static void Initialize(bool startHidden = false)
        {
            IntPtr hWnd = Handle;
            if (hWnd == IntPtr.Zero) return;

            // 1. Set Icon Console ke gamepads.ico
            ApplyConsoleIcon(hWnd);

            // 2. Disable tombol Close [X] pada title bar agar tidak terbunuh sengaja
            DisableCloseButton(hWnd);

            // 3. Jika diminta mulai dalam mode tersembunyi (startup)
            if (startHidden)
            {
                HideConsole();
            }

            // 4. Jalankan watcher thread untuk mendeteksi saat tombol Minimize [-] diklik
            minimizeWatcherThread = new Thread(WatchMinimize)
            {
                IsBackground = true,
                Name = "ConsoleMinimizeWatcher"
            };
            minimizeWatcherThread.Start();
        }

        private static void ApplyConsoleIcon(IntPtr hWnd)
        {
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
                    using var ico = new Icon(icoPath);
                    SendMessage(hWnd, WM_SETICON, (IntPtr)ICON_SMALL, ico.Handle);
                    SendMessage(hWnd, WM_SETICON, (IntPtr)ICON_BIG, ico.Handle);
                }
            }
            catch { }
        }

        private static void DisableCloseButton(IntPtr hWnd)
        {
            try
            {
                IntPtr hMenu = GetSystemMenu(hWnd, false);
                if (hMenu != IntPtr.Zero)
                {
                    // Menghapus menu Close [X]
                    DeleteMenu(hMenu, SC_CLOSE, MF_BYCOMMAND);
                }
            }
            catch { }
        }

        private static void WatchMinimize()
        {
            IntPtr hWnd = Handle;
            if (hWnd == IntPtr.Zero) return;

            while (isRunning)
            {
                try
                {
                    // Jika user mengklik tombol Minimize [-] pada jendela console,
                    // IsIconic akan true -> langsung sembunyikan ke System Tray!
                    if (IsIconic(hWnd))
                    {
                        HideConsole();
                        TrayManager.ShowBalloon("Fix_L1 Diminimalkan", "Aplikasi berjalan di System Tray. Klik icon untuk membuka kembali.", 1500);
                    }
                }
                catch { }

                Thread.Sleep(300);
            }
        }

        public static void HideConsole()
        {
            IntPtr hWnd = Handle;
            if (hWnd != IntPtr.Zero)
            {
                ShowWindow(hWnd, SW_HIDE);
            }
        }

        public static void ShowConsole()
        {
            IntPtr hWnd = Handle;
            if (hWnd != IntPtr.Zero)
            {
                ShowWindow(hWnd, SW_SHOW);
                ShowWindow(hWnd, SW_RESTORE);
                SetForegroundWindow(hWnd);
            }
        }

        public static bool IsVisible()
        {
            IntPtr hWnd = Handle;
            if (hWnd == IntPtr.Zero) return false;
            return IsWindowVisible(hWnd);
        }

        public static void ToggleVisibility()
        {
            if (IsVisible())
            {
                HideConsole();
            }
            else
            {
                ShowConsole();
            }
        }

        public static void Shutdown()
        {
            isRunning = false;
        }
    }
}
