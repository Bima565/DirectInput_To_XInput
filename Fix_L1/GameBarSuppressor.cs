using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace Fix_L1
{
    public static class GameBarSuppressor
    {
        public static void Initialize()
        {
            try
            {
                // 1. Nonaktifkan pembukaan Xbox Game Bar lewat tombol Guide / Nexus pada controller
                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\GameBar"))
                {
                    key?.SetValue("UseNexusForGameBarEnabled", 0, RegistryValueKind.DWord);
                }

                using (var key = Registry.CurrentUser.CreateSubKey(@"System\GameConfigStore"))
                {
                    key?.SetValue("GameDVR_Enabled", 0, RegistryValueKind.DWord);
                }

                // 2. Daftarkan dummy protocol handler untuk 'ms-gamebar'
                // Ini mencegah Windows memunculkan popup "Get an app to open this 'ms-gamebar' link"
                // saat Game Bar sudah di-uninstall oleh pengguna.
                using (var protoKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\ms-gamebar"))
                {
                    protoKey?.SetValue("", "URL:ms-gamebar", RegistryValueKind.String);
                    protoKey?.SetValue("URL Protocol", "", RegistryValueKind.String);
                }

                using (var cmdKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\ms-gamebar\shell\open\command"))
                {
                    cmdKey?.SetValue("", "rundll32.exe", RegistryValueKind.String);
                }

                // 3. Tutup window prompt jika sempat terbuka
                CloseAnyGameBarPopup();
            }
            catch { }
        }

        public static void CloseAnyGameBarPopup()
        {
            try
            {
                foreach (var p in Process.GetProcessesByName("OpenWith"))
                {
                    try { p.Kill(); } catch { }
                }
            }
            catch { }
        }
    }
}
