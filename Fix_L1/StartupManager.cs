using System;
using System.IO;
using Microsoft.Win32;

namespace Fix_L1
{
    public static class StartupManager
    {
        private const string RUN_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string APP_NAME = "Fix_L1_GamepadConverter";

        public static bool IsStartupEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RUN_KEY, false);
                if (key == null) return false;
                var val = key.GetValue(APP_NAME);
                return val != null;
            }
            catch
            {
                return false;
            }
        }

        public static bool SetStartup(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RUN_KEY, true);
                if (key == null) return false;

                if (enable)
                {
                    // Prioritaskan file exe langsung dengan flag --startup
                    string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                    string exePath = Path.Combine(exeDir, "Fix_L1.exe");
                    string cmdPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "Run_Fix_L1.cmd");
                    cmdPath = Path.GetFullPath(cmdPath);

                    string targetPath = File.Exists(exePath) ? exePath : (File.Exists(cmdPath) ? cmdPath : exePath);
                    string command = $"\"{targetPath}\" --startup";

                    key.SetValue(APP_NAME, command, RegistryValueKind.String);
                    return true;
                }
                else
                {
                    key.DeleteValue(APP_NAME, false);
                    return true;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Gagal mengatur startup: {ex.Message}");
                return false;
            }
        }
    }
}
