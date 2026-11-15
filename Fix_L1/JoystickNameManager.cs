using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Win32;

namespace Fix_L1
{
    public static class JoystickNameManager
    {
        public const string DEFAULT_XBOX_NAME = "Controller (XBOX 360 For Windows)";
        public const ushort DEFAULT_VID = 0x045E;
        public const ushort BASE_PID = 0x028E;

        private const string OEM_REG_KEY = @"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM";

        // OEMData standar Xbox 360 Controller untuk kapabilitas DirectInput lengkap
        private static readonly byte[] DEFAULT_OEMDATA = new byte[] { 0x23, 0x00, 0x80, 0x11, 0x0A, 0x00, 0x00, 0x00 };

        public static string GetOemSubKeyPath(ushort vid, ushort pid)
        {
            return $@"{OEM_REG_KEY}\VID_{vid:X4}&PID_{pid:X4}";
        }

        /// <summary>
        /// Membaca nama tampilan controller yang terdaftar di Windows DirectInput / joy.cpl
        /// </summary>
        public static string GetWindowsControllerName(ushort vid = DEFAULT_VID, ushort pid = BASE_PID)
        {
            string subKey = GetOemSubKeyPath(vid, pid);

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(subKey, false);
                var val = key?.GetValue("OEMName") as string;
                if (!string.IsNullOrWhiteSpace(val)) return val;
            }
            catch { }

            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(subKey, false);
                var val = key?.GetValue("OEMName") as string;
                if (!string.IsNullOrWhiteSpace(val)) return val;
            }
            catch { }

            return DEFAULT_XBOX_NAME;
        }

        /// <summary>
        /// Mengatur nama tampilan controller di Windows DirectInput / joy.cpl
        /// </summary>
        public static bool SetWindowsControllerName(ushort vid, ushort pid, string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                name = DEFAULT_XBOX_NAME;

            name = name.Trim();
            string subKey = GetOemSubKeyPath(vid, pid);
            bool success = false;

            // 1. Tulis ke HKEY_CURRENT_USER (Tidak butuh Administrator dan diprioritaskan oleh DirectInput)
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(subKey, true);
                if (key != null)
                {
                    key.SetValue("OEMName", name, RegistryValueKind.String);
                    key.SetValue("OEMData", DEFAULT_OEMDATA, RegistryValueKind.Binary);
                    success = true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Gagal menulis nama joystick ke HKCU: {ex.Message}");
            }

            // 2. Tulis ke HKEY_LOCAL_MACHINE jika berjalan sebagai Administrator
            try
            {
                using var key = Registry.LocalMachine.CreateSubKey(subKey, true);
                if (key != null)
                {
                    key.SetValue("OEMName", name, RegistryValueKind.String);
                    key.SetValue("OEMData", DEFAULT_OEMDATA, RegistryValueKind.Binary);
                    success = true;
                }
            }
            catch { }

            return success;
        }

        /// <summary>
        /// Mengembalikan nama controller di Windows ke default "Controller (XBOX 360 For Windows)"
        /// </summary>
        public static bool ResetWindowsControllerName(ushort vid = DEFAULT_VID, ushort pid = BASE_PID)
        {
            return SetWindowsControllerName(vid, pid, DEFAULT_XBOX_NAME);
        }

        /// <summary>
        /// Membersihkan registry OEM key untuk PID tertentu jika tidak lagi digunakan
        /// </summary>
        public static void CleanupOemKey(ushort vid, ushort pid)
        {
            if (pid == BASE_PID)
            {
                ResetWindowsControllerName(vid, pid);
                return;
            }

            string subKey = GetOemSubKeyPath(vid, pid);
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(subKey, false);
            }
            catch { }

            try
            {
                Registry.LocalMachine.DeleteSubKeyTree(subKey, false);
            }
            catch { }
        }

        /// <summary>
        /// Membuka dialog Game Controllers bawaan Windows (joy.cpl)
        /// </summary>
        public static void OpenJoyCpl()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "control.exe",
                    Arguments = "joy.cpl",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Gagal membuka Game Controllers (joy.cpl): {ex.Message}");
            }
        }

        /// <summary>
        /// Mencari Virtual PID berikutnya yang belum digunakan oleh profil manapun
        /// </summary>
        public static ushort GetNextAvailablePid(IEnumerable<ControllerProfile> existingProfiles)
        {
            var usedPids = existingProfiles
                .Where(p => p.VirtualPid > 0)
                .Select(p => p.VirtualPid)
                .ToHashSet();

            ushort candidate = BASE_PID; // Mulai dari 0x028E
            while (usedPids.Contains(candidate))
            {
                candidate++;
            }
            return candidate;
        }

        /// <summary>
        /// Memastikan seluruh profil controller memiliki Virtual PID unik dan namanya sinkron di Windows Registry
        /// </summary>
        public static void SyncAllProfiles(List<ControllerProfile> profiles)
        {
            var usedPids = new HashSet<ushort>();
            bool anyChanged = false;

            for (int i = 0; i < profiles.Count; i++)
            {
                var p = profiles[i];
                if (p.VirtualPid == 0 || usedPids.Contains(p.VirtualPid))
                {
                    ushort candidate = (ushort)(BASE_PID + i);
                    while (usedPids.Contains(candidate))
                    {
                        candidate++;
                    }
                    p.VirtualPid = candidate;
                    anyChanged = true;
                }

                usedPids.Add(p.VirtualPid);

                // Sinkronkan nama ke Windows Registry (DirectInput / joy.cpl)
                string effectiveName = p.GetEffectiveWindowsName();
                SetWindowsControllerName(DEFAULT_VID, p.VirtualPid, effectiveName);
            }

            if (anyChanged)
            {
                Debug.WriteLine("[JoystickNameManager] PIDs dialokasikan ulang untuk mencegah tabrakan nama.");
            }
        }
    }
}
