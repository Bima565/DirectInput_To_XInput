using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using SDL2;

namespace Fix_L1
{
    public class ControllerProfile
    {
        public string DeviceName { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string ProductGuid { get; set; } = "";

        // Virtual PID untuk membedakan nama masing-masing stik di Windows joy.cpl
        public ushort VirtualPid { get; set; } = 0x028E;

        // Nama kustom khusus di Windows joy.cpl (opsional, jika kosong gunakan DisplayName)
        public string? WindowsCustomName { get; set; }

        public string GetEffectiveWindowsName()
        {
            if (!string.IsNullOrWhiteSpace(WindowsCustomName))
                return WindowsCustomName.Trim();
            if (!string.IsNullOrWhiteSpace(DisplayName))
                return DisplayName.Trim();
            return JoystickNameManager.DEFAULT_XBOX_NAME;
        }

        // Mapping seluruh tombol digital & D-Pad
        public Dictionary<string, InputBinding> Bindings { get; set; } = new();

        // Trigger L2 & R2 (Bisa Tombol Digital atau Sumbu Analog)
        public TriggerBinding LeftTrigger { get; set; } = new(6);
        public TriggerBinding RightTrigger { get; set; } = new(7);

        // Analog Stik Kiri
        public int LeftStickXAxis { get; set; } = 0;
        public int LeftStickYAxis { get; set; } = 1;
        public bool InvertLeftStickX { get; set; } = false;
        public bool InvertLeftStickY { get; set; } = true;

        // Analog Stik Kanan
        public int RightStickXAxis { get; set; } = 2;
        public int RightStickYAxis { get; set; } = 3;
        public bool InvertRightStickX { get; set; } = false;
        public bool InvertRightStickY { get; set; } = true;

        // Deadzone untuk meredam stick drift stik murah / abal-abal
        public int Deadzone { get; set; } = 11000;

        // Deadzone Trigger (dalam persen: 0 - 90%)
        // Khusus Right Trigger (RT): default 35% untuk meredam ghost input stik rusak yang suka naik 10-20% saat idle
        public int RightTriggerDeadzone { get; set; } = 35;
        public int LeftTriggerDeadzone { get; set; } = 0;

        // Fitur Utama Fix_L1: Debounce Filter untuk tombol L1 yang rewel / kendor / chattering
        public bool L1DebounceEnabled { get; set; } = true;
        public int L1DebounceMs { get; set; } = 20;

        // Fitur Getaran / Vibration
        public bool VibrationEnabled { get; set; } = true;
        public int VibrationStrength { get; set; } = 100; // 0 - 100%

        // Kompatibilitas mundur dengan controllers_config.json versi lama
        [JsonPropertyName("ButtonMap")]
        public Dictionary<string, int>? LegacyButtonMap { get; set; }

        public void EnsureDefaults()
        {
            // Migrasi dari format lama jika ada
            if (LegacyButtonMap != null && LegacyButtonMap.Count > 0 && Bindings.Count == 0)
            {
                foreach (var kv in LegacyButtonMap)
                {
                    if (kv.Key == "LeftTrigger")
                    {
                        if (kv.Value >= 0) LeftTrigger = new TriggerBinding(kv.Value);
                    }
                    else if (kv.Key == "RightTrigger")
                    {
                        if (kv.Value >= 0) RightTrigger = new TriggerBinding(kv.Value);
                    }
                    else
                    {
                        if (kv.Value >= 0)
                            Bindings[kv.Key] = new InputBinding(InputType.Button, kv.Value);
                    }
                }
            }

            if (RightTriggerDeadzone <= 0)
            {
                RightTriggerDeadzone = 35; // Default 35% anti-ghosting RT rusak
            }

            if (VirtualPid == 0)
            {
                VirtualPid = 0x028E;
            }

            // Pastikan D-Pad terdefinisi (default Hat 0) jika belum diset
            if (!Bindings.ContainsKey("DpadUp"))
                Bindings["DpadUp"] = new InputBinding(InputType.HatUp, 0);
            if (!Bindings.ContainsKey("DpadDown"))
                Bindings["DpadDown"] = new InputBinding(InputType.HatDown, 0);
            if (!Bindings.ContainsKey("DpadLeft"))
                Bindings["DpadLeft"] = new InputBinding(InputType.HatLeft, 0);
            if (!Bindings.ContainsKey("DpadRight"))
                Bindings["DpadRight"] = new InputBinding(InputType.HatRight, 0);

            // Pastikan tombol dasar ada
            string[] standardButtons = new[]
            {
                "A", "B", "X", "Y",
                "LeftShoulder", "RightShoulder",
                "Back", "Start", "Guide",
                "LeftThumb", "RightThumb"
            };

            foreach (var btn in standardButtons)
            {
                if (!Bindings.ContainsKey(btn))
                {
                    Bindings[btn] = new InputBinding(InputType.None, -1);
                }
            }
        }

        public bool GetDigitalState(IntPtr joystick, string buttonName)
        {
            if (Bindings.TryGetValue(buttonName, out var binding))
            {
                return binding.IsPressed(joystick);
            }
            return false;
        }
    }
}
