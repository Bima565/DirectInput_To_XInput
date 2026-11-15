using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using SDL2;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace Fix_L1
{
    class Program
    {
        const int LEARN_TIMEOUT_SECONDS = 10;
        const string CONFIG_FILE = "controllers_config.json";

        static ViGEmClient? vigemClient;
        static readonly List<ControllerRuntime> activeControllers = new();
        static List<ControllerProfile> savedProfiles = new();
        static readonly object consoleLock = new();
        static readonly object listLock = new();

        static bool running = true;
        static volatile bool isWatcherPaused = false;
        static Thread? watcherThread;

        static void Main(string[] args)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("=================================================================");
            Console.WriteLine("=== DirectInput to XInput Converter (SDL2 & ViGEmBus Edition) ===");
            Console.WriteLine("===                        By Bima PD                         ===");
            Console.WriteLine("=================================================================");
            Console.ResetColor();

            bool startHidden = args.Contains("--startup");
            ConsoleWindowManager.Initialize(startHidden);
            GameBarSuppressor.Initialize();

            TrayManager.OnExitRequested += () =>
            {
                running = false;
                StopAllControllers();
                try { SDL.SDL_Quit(); } catch { }
                Environment.Exit(0);
            };

            TrayManager.Initialize();

            // Pastikan event joystick tetap berjalan lancar saat console di background / fokus di game
            SDL.SDL_SetHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");
            SDL.SDL_SetHint("SDL_JOYSTICK_RAWINPUT", "0"); // Gunakan XInput / DirectInput standar
            SDL.SDL_SetHint("SDL_JOYSTICK_THREAD", "1");
            SDL.SDL_SetHint("SDL_HAPTIC_GAIN_MAX", "100");

            // Aktifkan HIDAPI untuk mendeteksi DualSense dan stik PlayStation lainnya
            SDL.SDL_SetHint("SDL_JOYSTICK_HIDAPI", "1");
            SDL.SDL_SetHint("SDL_JOYSTICK_HIDAPI_PS4", "1");
            SDL.SDL_SetHint("SDL_JOYSTICK_HIDAPI_PS5", "1");
            SDL.SDL_SetHint("SDL_JOYSTICK_HIDAPI_STEAM", "1");

            // Inisialisasi Subsistem Joystick, GameController dan Haptic (Getaran)
            uint initFlags = SDL.SDL_INIT_JOYSTICK | SDL.SDL_INIT_GAMECONTROLLER | SDL.SDL_INIT_HAPTIC;
            if (SDL.SDL_Init(initFlags) < 0)
            {
                Console.WriteLine($"Gagal menginisialisasi SDL2: {SDL.SDL_GetError()}");
                PauseAndClear();
                return;
            }

            try
            {
                vigemClient = new ViGEmClient();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Gagal membuat ViGEm client: {ex.Message}");
                Console.WriteLine("Pastikan driver ViGEmBus sudah terpasang di Windows.");
                SDL.SDL_Quit();
                PauseAndClear();
                return;
            }

            LoadProfiles();
            AutoStartKnownControllers();

            watcherThread = new Thread(DeviceWatcherLoop)
            {
                IsBackground = true,
                Name = "DeviceWatcher"
            };
            watcherThread.Start();

            while (running)
            {
                PrintMenu();
                string? input = ReadCleanLine();
                if (input == null) break;
                if (string.IsNullOrWhiteSpace(input)) continue;

                switch (input)
                {
                    case "1":
                        AddNewController();
                        PauseAndClear();
                        break;
                    case "2":
                        ListControllers();
                        PauseAndClear();
                        break;
                    case "3":
                        EditController();
                        PauseAndClear();
                        break;
                    case "4":
                        TestControllerMenu();
                        PauseAndClear();
                        break;
                    case "5":
                        DeleteController();
                        PauseAndClear();
                        break;
                    case "6":
                        ManageWindowsJoystickNames();
                        PauseAndClear();
                        break;
                    case "7":
                        ToggleStartup();
                        PauseAndClear();
                        break;
                    case "8":
                        ConsoleWindowManager.HideConsole();
                        TrayManager.ShowBalloon("Fix_L1", "Aplikasi berjalan di latar belakang (System Tray).", 1500);
                        break;
                    case "9":
                        running = false;
                        break;
                    default:
                        Console.WriteLine("Pilihan tidak valid, coba lagi.\n");
                        PauseAndClear();
                        break;
                }
            }

            running = false;
            watcherThread?.Join(500);
            StopAllControllers();
            ConsoleWindowManager.Shutdown();
            SDL.SDL_Quit();
            Console.WriteLine("Semua virtual controller dihentikan. Sampai jumpa!");
        }

        static void DeviceWatcherLoop()
        {
            while (running)
            {
                try
                {
                    if (isWatcherPaused)
                    {
                        Thread.Sleep(500);
                        continue;
                    }

                    SDL.SDL_PumpEvents();
                    SDL.SDL_JoystickUpdate();
                    GameBarSuppressor.CloseAnyGameBarPopup();

                    // 1. Cek disconnect
                    List<ControllerRuntime> toRemove = new List<ControllerRuntime>();
                    lock (listLock)
                    {
                        foreach (var rt in activeControllers)
                        {
                            if (SDL.SDL_JoystickGetAttached(rt.JoystickHandle) == SDL.SDL_bool.SDL_FALSE)
                            {
                                toRemove.Add(rt);
                            }
                        }
                    }

                    foreach (var rt in toRemove)
                    {
                        try { rt.Cts.Cancel(); } catch { }
                        try { rt.Thread?.Join(500); } catch { }
                        try { rt.Dispose(); } catch { }
                        try { SDL.SDL_JoystickClose(rt.JoystickHandle); } catch { }
                        lock (listLock) { activeControllers.Remove(rt); }
                    }

                    // 2. Cek connect (Auto start)
                    int numJoysticks = SDL.SDL_NumJoysticks();
                    lock (listLock)
                    {
                        var activeInstanceIds = activeControllers.Select(c => SDL.SDL_JoystickInstanceID(c.JoystickHandle)).ToHashSet();
                        var activeProfiles = activeControllers.Select(c => c.Profile).ToHashSet();

                        for (int i = 0; i < numJoysticks; i++)
                        {
                            int instanceId = SDL.SDL_JoystickGetDeviceInstanceID(i);
                            if (activeInstanceIds.Contains(instanceId)) continue;

                            string instanceName = SDL.SDL_JoystickNameForIndex(i) ?? $"Generic Controller {i}";
                            string productGuid = GetJoystickGuidString(i);

                            var matchingProfile = savedProfiles.FirstOrDefault(p => p.ProductGuid == productGuid && p.DeviceName == instanceName);
                            if (matchingProfile != null)
                            {
                                // JANGAN start jika profil ini sudah aktif di controller lain (mencegah loopback / duplikasi virtual Xbox)
                                if (activeProfiles.Contains(matchingProfile)) continue;

                                StartController(matchingProfile, i);
                                activeInstanceIds.Add(instanceId);
                                activeProfiles.Add(matchingProfile);
                            }
                        }
                    }

                    // Update Status di Tray
                    lock (listLock)
                    {
                        string names = string.Join(", ", activeControllers.Select(x => x.Profile.DisplayName));
                        TrayManager.UpdateStatus(activeControllers.Count, names);
                    }
                }
                catch { }

                Thread.Sleep(2000); // Polling per 2 detik (sangat ringan)
            }
        }

        static void PrintMenu()
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("==================================================");
            Console.WriteLine("                    MENU UTAMA                    ");
            Console.WriteLine("==================================================");
            Console.ResetColor();
            
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($"Controller virtual aktif saat ini: {activeControllers.Count}");
            Console.WriteLine();
            
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("1. Tambah controller baru (Wizard Mapping Lengkap)");
            Console.WriteLine("2. List controller & detail mapping");
            Console.WriteLine("3. Edit controller (Remap Tombol, Invert, Stick, Trigger, Getaran, L1)");
            Console.WriteLine("4. Test Controller & Getaran (Live Input & Vibration Test)");
            Console.WriteLine("5. Hapus controller");
            Console.WriteLine("6. Kelola Nama Game Controllers di Windows (joy.cpl)");

            bool isStartup = StartupManager.IsStartupEnabled();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"7. Run on Startup Windows: {(isStartup ? "[ON]" : "[OFF]")}");
            Console.WriteLine("8. Sembunyikan ke System Tray (Tetap Berjalan)");

            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("9. Keluar (Exit) Aplikasi");
            Console.ResetColor();

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("                              By Bima PD");
            Console.ResetColor();

            Console.Write("Pilih menu [1-9]: ");
        }

        static void ToggleStartup()
        {
            bool current = StartupManager.IsStartupEnabled();
            bool target = !current;
            if (StartupManager.SetStartup(target))
            {
                Console.WriteLine($"\n[SUCCESS] Run on Startup sekarang {(target ? "ON" : "OFF")}.");
                TrayManager.UpdateStatus(activeControllers.Count, string.Join(", ", activeControllers.Select(x => x.Profile.DisplayName))); // update tray menu
            }
            else
            {
                Console.WriteLine("\n[ERROR] Gagal mengubah pengaturan startup. Pastikan jalankan sebagai Administrator jika diperlukan.");
            }
        }

        static void PauseAndClear()
        {
            Console.WriteLine();
            Console.Write("Tekan ENTER untuk kembali ke menu...");
            try { ReadCleanLine(); } catch { }
            if (!Console.IsOutputRedirected)
            {
                try { Console.Clear(); } catch { }
            }
        }

        static string? ReadCleanLine()
        {
            string? line = Console.ReadLine();
            if (line == null) return null;
            return line.Trim('﻿', ' ', '\t', '\r', '\n');
        }

        // ================= PERSISTENSI PROFIL =================

        static string GetConfigFilePath()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string pathInBase = Path.Combine(baseDir, CONFIG_FILE);
            if (File.Exists(pathInBase)) return pathInBase;

            string pathInCwd = Path.Combine(Directory.GetCurrentDirectory(), CONFIG_FILE);
            if (File.Exists(pathInCwd)) return pathInCwd;

            return pathInBase;
        }

        static void LoadProfiles()
        {
            string configPath = GetConfigFilePath();
            if (!File.Exists(configPath))
            {
                savedProfiles = new List<ControllerProfile>();
                return;
            }

            try
            {
                string json = File.ReadAllText(configPath);
                var loaded = JsonSerializer.Deserialize<List<ControllerProfile>>(json) ?? new List<ControllerProfile>();
                var dedup = new Dictionary<string, ControllerProfile>();
                foreach (var p in loaded)
                {
                    p.EnsureDefaults();
                    string key = p.ProductGuid + "_" + p.DeviceName;
                    dedup[key] = p; // Timpa profil duplikat jika ada, simpan yang terbaru
                }
                savedProfiles = dedup.Values.ToList();
                JoystickNameManager.SyncAllProfiles(savedProfiles);
                SaveProfiles();
                Console.WriteLine($"Berhasil memuat {savedProfiles.Count} profil controller dari {Path.GetFileName(configPath)}.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Gagal membaca file konfigurasi: {ex.Message}");
                savedProfiles = new List<ControllerProfile>();
            }
        }

        static void SaveProfiles()
        {
            try
            {
                string configPath = GetConfigFilePath();
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(savedProfiles, options);
                File.WriteAllText(configPath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Gagal menyimpan konfigurasi: {ex.Message}");
            }
        }

        // ================= AUTO START =================

        static void AutoStartKnownControllers()
        {
            SDL.SDL_PumpEvents();
            SDL.SDL_JoystickUpdate();
            int numJoysticks = SDL.SDL_NumJoysticks();

            var activeInstanceIds = activeControllers.Select(c => SDL.SDL_JoystickInstanceID(c.JoystickHandle)).ToHashSet();

            for (int i = 0; i < numJoysticks; i++)
            {
                int instanceId = SDL.SDL_JoystickGetDeviceInstanceID(i);
                if (activeInstanceIds.Contains(instanceId)) continue;

                string instanceName = SDL.SDL_JoystickNameForIndex(i) ?? $"Generic Controller {i}";
                string productGuid = GetJoystickGuidString(i);

                var matchingProfile = savedProfiles.FirstOrDefault(p => p.ProductGuid == productGuid && p.DeviceName == instanceName);
                if (matchingProfile != null)
                {
                    StartController(matchingProfile, i);
                    activeInstanceIds.Add(instanceId);
                }
            }

            if (activeControllers.Count > 0)
                Console.WriteLine($"{activeControllers.Count} controller yang sudah dikenal langsung diaktifkan otomatis.");
        }

        // ================= 1. TAMBAH CONTROLLER (FULL WIZARD) =================

        static void AddNewController()
        {
            isWatcherPaused = true;
            try
            {
                SDL.SDL_PumpEvents();
                SDL.SDL_JoystickUpdate();
                int numJoysticks = SDL.SDL_NumJoysticks();

                var busyIds = activeControllers.Select(c => SDL.SDL_JoystickInstanceID(c.JoystickHandle)).ToHashSet();
                var candidates = new List<int>();

                for (int i = 0; i < numJoysticks; i++)
                {
                    if (!busyIds.Contains(SDL.SDL_JoystickGetDeviceInstanceID(i))) candidates.Add(i);
                }

                if (candidates.Count == 0)
                {
                    Console.WriteLine("Tidak ada controller baru yang belum terdaftar.");
                    if (activeControllers.Count > 0)
                    {
                        Console.WriteLine($"Saat ini {activeControllers.Count} controller Anda sudah aktif berjalan.");
                        Console.WriteLine("Untuk mapping ulang / mengedit controller yang sudah ada, silakan gunakan Menu 3 (Edit controller).\n");
                    }
                    else
                    {
                        Console.WriteLine("Pastikan stik USB sudah tercolok ke komputer dan tidak tersembunyi.\n");
                    }
                    return;
                }

                Console.WriteLine("Daftar controller baru yang siap ditambahkan:");
                for (int i = 0; i < candidates.Count; i++)
                    Console.WriteLine($"{i + 1}. {SDL.SDL_JoystickNameForIndex(candidates[i])}");

                Console.WriteLine("0. Batal");
                Console.Write("Pilih nomor: ");

                if (!int.TryParse(ReadCleanLine(), out int idx) || idx < 1 || idx > candidates.Count)
                {
                    Console.WriteLine("Dibatalkan atau pilihan tidak valid.\n");
                    return;
                }
                int chosenIndex = candidates[idx - 1];

                IntPtr joystick = SDL.SDL_JoystickOpen(chosenIndex);
                if (joystick == IntPtr.Zero)
                {
                    Console.WriteLine($"Gagal mengakses controller: {SDL.SDL_GetError()}\n");
                    return;
                }

                string deviceName = SDL.SDL_JoystickName(joystick) ?? $"Generic Controller {chosenIndex}";
                string productGuid = GetJoystickGuidString(chosenIndex);

                var profile = new ControllerProfile
                {
                    DeviceName = deviceName,
                    DisplayName = deviceName,
                    ProductGuid = productGuid,
                    VirtualPid = JoystickNameManager.GetNextAvailablePid(savedProfiles)
                };
                profile.EnsureDefaults();

                Console.WriteLine();
                Console.WriteLine("==============================================================");
                Console.WriteLine($" WIZARD PEMETAAN LENGKAP XBOX 360: {deviceName}");
                Console.WriteLine("==============================================================");
                Console.WriteLine("Petunjuk:");
                Console.WriteLine($"- Tekan tombol fisik di stik kamu dalam waktu {LEARN_TIMEOUT_SECONDS} detik.");
                Console.WriteLine("- Untuk melewati suatu tombol, tekan ENTER pada keyboard.");
                Console.WriteLine("- Untuk mengosongkan/melepas tombol, tekan C atau DELETE.");
                Console.WriteLine("==============================================================\n");

                RunFullMappingWizard(joystick, profile);

                savedProfiles.RemoveAll(p => p.ProductGuid == profile.ProductGuid && p.DeviceName == profile.DeviceName);
                savedProfiles.Add(profile);
                JoystickNameManager.SetWindowsControllerName(JoystickNameManager.DEFAULT_VID, profile.VirtualPid, profile.GetEffectiveWindowsName());
                SaveProfiles();

                // Uji getaran singkat jika didukung
                using (var vib = new VibrationController(joystick))
                {
                    if (vib.IsSupported)
                    {
                        Console.WriteLine("\nMenguji motor getaran (vibration)...");
                        vib.TestVibratePattern(4); // 3x denyut getaran
                        Console.WriteLine("[Getaran] Motor getar berhasil diuji dan aktif.");
                    }
                }

                SDL.SDL_JoystickClose(joystick);

                StartController(profile, chosenIndex);
                Console.WriteLine($"\nSelamat! Controller '{profile.DisplayName}' berhasil disimpan dan langsung aktif sebagai Virtual Xbox 360 Controller!\n");
            }
            finally
            {
                isWatcherPaused = false;
            }
        }

        static void RunFullMappingWizard(IntPtr joystick, ControllerProfile profile)
        {
            Console.WriteLine("==============================================================");
            Console.WriteLine(" PEMETAAN INPUT XBOX CONTROLLER (SATU PER SATU)");
            Console.WriteLine(" Tekan ENTER pada keyboard jika ingin melewati suatu input.");
            Console.WriteLine("==============================================================\n");

            Console.WriteLine("--- [1/21] Tombol A ---");
            MapSingleDigitalPrompt(joystick, profile, "A", "Tombol [A]");

            Console.WriteLine("\n--- [2/21] Tombol B ---");
            MapSingleDigitalPrompt(joystick, profile, "B", "Tombol [B]");

            Console.WriteLine("\n--- [3/21] Tombol X ---");
            MapSingleDigitalPrompt(joystick, profile, "X", "Tombol [X]");

            Console.WriteLine("\n--- [4/21] Tombol Y ---");
            MapSingleDigitalPrompt(joystick, profile, "Y", "Tombol [Y]");

            Console.WriteLine("\n--- [5/21] Tombol LB (Left Bumper) ---");
            MapSingleDigitalPrompt(joystick, profile, "LeftShoulder", "Tombol [LB] (Bumper Kiri - Fitur Fix_L1)");

            Console.WriteLine("\n--- [6/21] Tombol RB (Right Bumper) ---");
            MapSingleDigitalPrompt(joystick, profile, "RightShoulder", "Tombol [RB] (Bumper Kanan)");

            Console.WriteLine("\n--- [7/21] Trigger LT (Left Trigger) ---");
            MapTriggerPrompt(joystick, profile, isLeft: true);

            Console.WriteLine("\n--- [8/21] Trigger RT (Right Trigger) ---");
            MapTriggerPrompt(joystick, profile, isLeft: false);

            Console.WriteLine("\n--- [9/21] Tombol Back ---");
            MapSingleDigitalPrompt(joystick, profile, "Back", "Tombol [Back]");

            Console.WriteLine("\n--- [10/21] Tombol Start ---");
            MapSingleDigitalPrompt(joystick, profile, "Start", "Tombol [Start]");

            Console.WriteLine("\n--- [11/21] Tombol Guide (Xbox) ---");
            MapSingleDigitalPrompt(joystick, profile, "Guide", "Tombol [Guide]");

            Console.WriteLine("\n--- [12/21] Tombol LS Click (Left Stick Click) ---");
            MapSingleDigitalPrompt(joystick, profile, "LeftThumb", "Tombol [LS Click]");

            Console.WriteLine("\n--- [13/21] Tombol RS Click (Right Stick Click) ---");
            MapSingleDigitalPrompt(joystick, profile, "RightThumb", "Tombol [RS Click]");

            Console.WriteLine("\n--- [14/21] D-Pad Up ---");
            MapSingleDigitalPrompt(joystick, profile, "DpadUp", "D-Pad [Up]");

            Console.WriteLine("\n--- [15/21] D-Pad Down ---");
            MapSingleDigitalPrompt(joystick, profile, "DpadDown", "D-Pad [Down]");

            Console.WriteLine("\n--- [16/21] D-Pad Left ---");
            MapSingleDigitalPrompt(joystick, profile, "DpadLeft", "D-Pad [Left]");

            Console.WriteLine("\n--- [17/21] D-Pad Right ---");
            MapSingleDigitalPrompt(joystick, profile, "DpadRight", "D-Pad [Right]");

            Console.WriteLine("\n--- [18/21] Left Stick (LS): Left / Right ---");
            MapSingleStickAxisPrompt(joystick, profile, isRight: false, isVertical: false);

            Console.WriteLine("\n--- [19/21] Left Stick (LS): Up / Down ---");
            MapSingleStickAxisPrompt(joystick, profile, isRight: false, isVertical: true);

            Console.WriteLine("\n--- [20/21] Right Stick (RS): Left / Right ---");
            MapSingleStickAxisPrompt(joystick, profile, isRight: true, isVertical: false);

            Console.WriteLine("\n--- [21/21] Right Stick (RS): Up / Down ---");
            MapSingleStickAxisPrompt(joystick, profile, isRight: true, isVertical: true);
        }

        static void MapSingleDigitalPrompt(IntPtr joystick, ControllerProfile profile, string key, string label)
        {
            Console.Write($"Tekan {label} [ENTER: lewati | C/DEL: kosongkan] ... ");
            WaitForButtonRelease(joystick, TimeSpan.FromSeconds(2));
            var (input, clear) = WaitForAnyInputOrClear(joystick, TimeSpan.FromSeconds(LEARN_TIMEOUT_SECONDS));

            if (clear)
            {
                profile.Bindings[key] = new InputBinding(InputType.None, -1);
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"> Tombol '{label}' dilepas (tidak dipetakan/None).");
                Console.ResetColor();
            }
            else if (input != null && input.Type != InputType.None)
            {
                // Cek apakah tombol/input ini sudah dipakai oleh fungsi lain di profil
                var conflict = profile.Bindings.FirstOrDefault(b => b.Key != key && b.Value.Type == input.Type && b.Value.Index == input.Index);
                if (!string.IsNullOrEmpty(conflict.Key))
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"\n  [Info] Input '{input}' sebelumnya dipakai oleh '{conflict.Key}'.");
                    Console.WriteLine($"         Fungsi '{conflict.Key}' otomatis dilepas agar TIDAK GABUNG / bentrok!");
                    Console.ResetColor();
                    profile.Bindings[conflict.Key] = new InputBinding(InputType.None, -1);
                }

                profile.Bindings[key] = input;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"> Berhasil: {input}");
                Console.ResetColor();
            }
            else
            {
                Console.WriteLine($"> (Dilewati, mapping tidak diubah: {profile.Bindings.GetValueOrDefault(key)})");
            }
            WaitForButtonRelease(joystick, TimeSpan.FromSeconds(2));
        }

        static void MapTriggerPrompt(IntPtr joystick, ControllerProfile profile, bool isLeft)
        {
            string name = isLeft ? "LT (Left Trigger)" : "RT (Right Trigger)";
            Console.Write($"Tekan atau tarik {name} [ENTER untuk lewati] ... ");
            WaitForRelease(joystick, TimeSpan.FromSeconds(2));

            // Rekam baseline posisi axis
            int numAxes = SDL.SDL_JoystickNumAxes(joystick);
            short[] baseline = new short[numAxes];
            for (int i = 0; i < numAxes; i++) baseline[i] = SDL.SDL_JoystickGetAxis(joystick, i);

            // KECUALIKAN axis yang sudah dipakai oleh Left Trigger saat mapping Right Trigger!
            int excludeAxis = -1;
            if (!isLeft && profile.LeftTrigger.IsAxis && profile.LeftTrigger.AxisIndex >= 0)
            {
                excludeAxis = profile.LeftTrigger.AxisIndex;
            }

            var start = DateTime.Now;
            bool detected = false;

            while (DateTime.Now - start < TimeSpan.FromSeconds(LEARN_TIMEOUT_SECONDS))
            {
                if (Console.KeyAvailable)
                {
                    var k = Console.ReadKey(true);
                    if (k.Key == ConsoleKey.Enter || k.Key == ConsoleKey.Spacebar || k.Key == ConsoleKey.Escape)
                    {
                        break;
                    }
                }

                SDL.SDL_PumpEvents();
                SDL.SDL_JoystickUpdate();

                // 1. Cek tombol fisik (jika trigger berupa tombol digital)
                int numButtons = SDL.SDL_JoystickNumButtons(joystick);
                for (int b = 0; b < numButtons; b++)
                {
                    if (SDL.SDL_JoystickGetButton(joystick, b) != 0)
                    {
                        var tb = new TriggerBinding(b);
                        if (isLeft) profile.LeftTrigger = tb; else profile.RightTrigger = tb;
                        Console.WriteLine($"> Berhasil: Tombol[{b + 1}] (Digital)");
                        detected = true;
                        break;
                    }
                }
                if (detected) break;

                // 2. Cek pergerakan sumbu analog (jika trigger berupa tuas analog, abaikan excludeAxis)
                for (int a = 0; a < numAxes; a++)
                {
                    if (a == excludeAxis) continue;

                    short cur = SDL.SDL_JoystickGetAxis(joystick, a);
                    if (Math.Abs((int)cur - (int)baseline[a]) > 16000)
                    {
                        var tb = new TriggerBinding(a, baseline[a]);
                        if (isLeft) profile.LeftTrigger = tb; else profile.RightTrigger = tb;
                        Console.WriteLine($"> Berhasil: Axis {a} (Analog Trigger)");
                        detected = true;
                        break;
                    }
                }
                if (detected) break;

                Thread.Sleep(15);
            }

            if (!detected)
            {
                var cur = isLeft ? profile.LeftTrigger : profile.RightTrigger;
                Console.WriteLine($"> (Dilewati, tetap: {cur})");
            }

            WaitForRelease(joystick, TimeSpan.FromSeconds(2));
        }

        static void MapSingleStickAxisPrompt(IntPtr joystick, ControllerProfile profile, bool isRight, bool isVertical)
        {
            string stickName = isRight ? "Right Stick (RS)" : "Left Stick (LS)";
            string dirName = isVertical ? "ke ATAS atau BAWAH penuh (Up / Down)" : "ke KIRI atau KANAN penuh (Left / Right)";

            Console.Write($"Gerakkan {stickName} {dirName} [ENTER untuk lewati] ... ");
            WaitForRelease(joystick, TimeSpan.FromSeconds(2));

            int exclude = isVertical
                ? (isRight ? profile.RightStickXAxis : profile.LeftStickXAxis)
                : -1;

            int? axis = DetectMovedAxis(joystick, TimeSpan.FromSeconds(LEARN_TIMEOUT_SECONDS), exclude, out _);
            if (axis.HasValue)
            {
                if (isRight)
                {
                    if (isVertical)
                    {
                        profile.RightStickYAxis = axis.Value;
                        Console.WriteLine($"> Berhasil: Axis {axis.Value}");
                    }
                    else
                    {
                        profile.RightStickXAxis = axis.Value;
                        Console.WriteLine($"> Berhasil: Axis {axis.Value}");
                    }
                }
                else
                {
                    if (isVertical)
                    {
                        profile.LeftStickYAxis = axis.Value;
                        Console.WriteLine($"> Berhasil: Axis {axis.Value}");
                    }
                    else
                    {
                        profile.LeftStickXAxis = axis.Value;
                        Console.WriteLine($"> Berhasil: Axis {axis.Value}");
                    }
                }
            }
            else
            {
                int cur = isRight
                    ? (isVertical ? profile.RightStickYAxis : profile.RightStickXAxis)
                    : (isVertical ? profile.LeftStickYAxis : profile.LeftStickXAxis);
                Console.WriteLine($"> (Dilewati, tetap: Axis {cur})");
            }

            WaitForRelease(joystick, TimeSpan.FromSeconds(2));
        }

        static void MapStickPrompts(IntPtr joystick, ControllerProfile profile)
        {
            MapSingleStickAxisPrompt(joystick, profile, isRight: false, isVertical: false);
            MapSingleStickAxisPrompt(joystick, profile, isRight: false, isVertical: true);
            MapSingleStickAxisPrompt(joystick, profile, isRight: true, isVertical: false);
            MapSingleStickAxisPrompt(joystick, profile, isRight: true, isVertical: true);
        }

        static int? DetectMovedAxis(IntPtr joystick, TimeSpan timeout, int excludeAxis, out short finalValue)
        {
            finalValue = 0;
            int numAxes = SDL.SDL_JoystickNumAxes(joystick);
            short[] baseline = new short[numAxes];
            for (int i = 0; i < numAxes; i++) baseline[i] = SDL.SDL_JoystickGetAxis(joystick, i);

            var start = DateTime.Now;
            while (DateTime.Now - start < timeout)
            {
                if (Console.KeyAvailable)
                {
                    var k = Console.ReadKey(true);
                    if (k.Key == ConsoleKey.Enter || k.Key == ConsoleKey.Spacebar || k.Key == ConsoleKey.Escape)
                    {
                        break;
                    }
                }

                SDL.SDL_PumpEvents();
                SDL.SDL_JoystickUpdate();
                for (int a = 0; a < numAxes; a++)
                {
                    if (a == excludeAxis) continue;
                    short cur = SDL.SDL_JoystickGetAxis(joystick, a);
                    if (Math.Abs((int)cur - (int)baseline[a]) > 16000)
                    {
                        finalValue = cur;
                        return a;
                    }
                }
                Thread.Sleep(15);
            }
            return null;
        }

        static int? DetectMovedAxis(IntPtr joystick, TimeSpan timeout, int excludeAxis)
        {
            return DetectMovedAxis(joystick, timeout, excludeAxis, out _);
        }

        // ================= INPUT DETECTION HELPERS =================

        static void WaitForButtonRelease(IntPtr joystick, TimeSpan maxWait)
        {
            var start = DateTime.Now;
            int stableTicks = 0;

            while (DateTime.Now - start < maxWait)
            {
                SDL.SDL_PumpEvents();
                SDL.SDL_JoystickUpdate();

                bool anyPressed = false;
                int numButtons = SDL.SDL_JoystickNumButtons(joystick);
                for (int i = 0; i < numButtons; i++)
                {
                    if (SDL.SDL_JoystickGetButton(joystick, i) != 0) { anyPressed = true; break; }
                }

                if (!anyPressed)
                {
                    int numHats = SDL.SDL_JoystickNumHats(joystick);
                    for (int h = 0; h < numHats; h++)
                    {
                        if (SDL.SDL_JoystickGetHat(joystick, h) != SDL.SDL_HAT_CENTERED) { anyPressed = true; break; }
                    }
                }

                if (!anyPressed)
                {
                    stableTicks++;
                    if (stableTicks >= 5) // ~50ms stabil tanpa tombol ditekan
                    {
                        return;
                    }
                }
                else
                {
                    stableTicks = 0;
                }

                Thread.Sleep(10);
            }
        }

        static (InputBinding? Binding, bool Clear) WaitForAnyInputOrClear(IntPtr joystick, TimeSpan timeout)
        {
            var start = DateTime.Now;
            while (DateTime.Now - start < timeout)
            {
                if (Console.KeyAvailable)
                {
                    var k = Console.ReadKey(true);
                    if (k.Key == ConsoleKey.C || k.Key == ConsoleKey.Delete || k.Key == ConsoleKey.Backspace)
                    {
                        return (null, true);
                    }
                    if (k.Key == ConsoleKey.Enter || k.Key == ConsoleKey.Spacebar || k.Key == ConsoleKey.Escape)
                    {
                        return (null, false);
                    }
                }

                SDL.SDL_PumpEvents();
                SDL.SDL_JoystickUpdate();

                // 1. Cek tombol fisik
                int numButtons = SDL.SDL_JoystickNumButtons(joystick);
                for (int i = 0; i < numButtons; i++)
                {
                    if (SDL.SDL_JoystickGetButton(joystick, i) != 0)
                    {
                        return (new InputBinding(InputType.Button, i), false);
                    }
                }

                // 2. Cek D-Pad (Hat / POV)
                int numHats = SDL.SDL_JoystickNumHats(joystick);
                for (int h = 0; h < numHats; h++)
                {
                    byte hatVal = SDL.SDL_JoystickGetHat(joystick, h);
                    if ((hatVal & SDL.SDL_HAT_UP) != 0) return (new InputBinding(InputType.HatUp, h), false);
                    if ((hatVal & SDL.SDL_HAT_DOWN) != 0) return (new InputBinding(InputType.HatDown, h), false);
                    if ((hatVal & SDL.SDL_HAT_LEFT) != 0) return (new InputBinding(InputType.HatLeft, h), false);
                    if ((hatVal & SDL.SDL_HAT_RIGHT) != 0) return (new InputBinding(InputType.HatRight, h), false);
                }

                Thread.Sleep(15);
            }
            return (null, false);
        }

        static void WaitForRelease(IntPtr joystick, TimeSpan maxWait)
        {
            var start = DateTime.Now;

            int numAxes = SDL.SDL_JoystickNumAxes(joystick);
            short[] prevAxes = new short[numAxes];
            for (int a = 0; a < numAxes; a++) prevAxes[a] = SDL.SDL_JoystickGetAxis(joystick, a);

            int stableTicks = 0;

            while (DateTime.Now - start < maxWait)
            {
                SDL.SDL_PumpEvents();
                SDL.SDL_JoystickUpdate();

                // 1. Cek tombol fisik
                bool anyPressed = false;
                int numButtons = SDL.SDL_JoystickNumButtons(joystick);
                for (int i = 0; i < numButtons; i++)
                {
                    if (SDL.SDL_JoystickGetButton(joystick, i) != 0) { anyPressed = true; break; }
                }

                // 2. Cek D-Pad (Hat)
                if (!anyPressed)
                {
                    int numHats = SDL.SDL_JoystickNumHats(joystick);
                    for (int h = 0; h < numHats; h++)
                    {
                        if (SDL.SDL_JoystickGetHat(joystick, h) != SDL.SDL_HAT_CENTERED) { anyPressed = true; break; }
                    }
                }

                // 3. Cek apakah ada axis yang masih bergerak
                bool axesMoving = false;
                for (int a = 0; a < numAxes; a++)
                {
                    short cur = SDL.SDL_JoystickGetAxis(joystick, a);
                    if (Math.Abs((int)cur - (int)prevAxes[a]) > 600)
                    {
                        axesMoving = true;
                    }
                    prevAxes[a] = cur;
                }

                if (!anyPressed && !axesMoving)
                {
                    stableTicks++;
                    if (stableTicks >= 10) // Stabil selama ~150ms tanpa gerakan apa pun
                    {
                        return;
                    }
                }
                else
                {
                    stableTicks = 0;
                }

                Thread.Sleep(15);
            }
        }

        static InputBinding? WaitForAnyInput(IntPtr joystick, TimeSpan timeout)
        {
            var start = DateTime.Now;
            while (DateTime.Now - start < timeout)
            {
                if (Console.KeyAvailable)
                {
                    var k = Console.ReadKey(true);
                    if (k.Key == ConsoleKey.Enter || k.Key == ConsoleKey.Spacebar || k.Key == ConsoleKey.Escape)
                    {
                        return null; // Pengguna menekan ENTER/Spasi untuk lewati
                    }
                }

                SDL.SDL_PumpEvents();
                SDL.SDL_JoystickUpdate();

                // 1. Cek tombol fisik
                int numButtons = SDL.SDL_JoystickNumButtons(joystick);
                for (int i = 0; i < numButtons; i++)
                {
                    if (SDL.SDL_JoystickGetButton(joystick, i) != 0)
                    {
                        return new InputBinding(InputType.Button, i);
                    }
                }

                // 2. Cek D-Pad (Hat / POV)
                int numHats = SDL.SDL_JoystickNumHats(joystick);
                for (int h = 0; h < numHats; h++)
                {
                    byte hatVal = SDL.SDL_JoystickGetHat(joystick, h);
                    if ((hatVal & SDL.SDL_HAT_UP) != 0) return new InputBinding(InputType.HatUp, h);
                    if ((hatVal & SDL.SDL_HAT_DOWN) != 0) return new InputBinding(InputType.HatDown, h);
                    if ((hatVal & SDL.SDL_HAT_LEFT) != 0) return new InputBinding(InputType.HatLeft, h);
                    if ((hatVal & SDL.SDL_HAT_RIGHT) != 0) return new InputBinding(InputType.HatRight, h);
                }

                Thread.Sleep(15);
            }
            return null;
        }

        // ================= 2. READ: LIST CONTROLLER =================

        static void ListControllers()
        {
            if (savedProfiles.Count == 0)
            {
                Console.WriteLine("Belum ada controller yang tersimpan di sistem.\n");
                return;
            }

            Console.WriteLine();
            Console.WriteLine("==================================================");
            Console.WriteLine("             DAFTAR CONTROLLER TERSIMPAN          ");
            Console.WriteLine("==================================================");

            for (int i = 0; i < savedProfiles.Count; i++)
            {
                var p = savedProfiles[i];
                bool active = activeControllers.Any(c => c.Profile == p);
                Console.WriteLine($"{i + 1}. {p.DisplayName} [{(active ? "AKTIF" : "Tidak Terpasang")}]");
                Console.WriteLine($"   GUID         : {p.ProductGuid}");
                Console.WriteLine($"   Device       : {p.DeviceName}");
                Console.WriteLine($"   Nama Windows : {JoystickNameManager.GetWindowsControllerName(JoystickNameManager.DEFAULT_VID, p.VirtualPid)} (joy.cpl / PID 0x{p.VirtualPid:X4})");
                Console.WriteLine($"   Fix_L1       : {(p.L1DebounceEnabled ? $"AKTIF ({p.L1DebounceMs}ms debounce)" : "Nonaktif")}");
                Console.WriteLine($"   Getaran      : {(p.VibrationEnabled ? $"AKTIF ({p.VibrationStrength}%)" : "Nonaktif")}");
                Console.WriteLine($"   Deadzone     : Stick={p.Deadzone} | RT Trigger={p.RightTriggerDeadzone}% (Anti-Ghosting 0-35%) | LT Trigger={p.LeftTriggerDeadzone}%");
                Console.WriteLine($"   Left Stick (LS) : Left/Right=Axis {p.LeftStickXAxis}{(p.InvertLeftStickX ? " [Invert]" : "")}, Up/Down=Axis {p.LeftStickYAxis}{(p.InvertLeftStickY ? " [Invert]" : "")}");
                Console.WriteLine($"   Right Stick (RS): Left/Right=Axis {p.RightStickXAxis}{(p.InvertRightStickX ? " [Invert]" : "")}, Up/Down=Axis {p.RightStickYAxis}{(p.InvertRightStickY ? " [Invert]" : "")}");
                Console.WriteLine($"   Triggers        : LT={p.LeftTrigger} | RT={p.RightTrigger}");
                Console.WriteLine("   Tombol Digital & D-Pad:");
                foreach (var kv in p.Bindings)
                {
                    Console.WriteLine($"     - {kv.Key,-14} : {kv.Value}");
                }
                Console.WriteLine();
            }
        }

        // ================= 3. UPDATE: EDIT CONTROLLER =================

        static void EditController()
        {
            if (savedProfiles.Count == 0)
            {
                Console.WriteLine("Belum ada controller yang tersimpan.\n");
                return;
            }

            var profile = PickProfile("Pilih controller yang ingin diedit:");
            if (profile == null) return;

            Console.WriteLine();
            Console.WriteLine($"=== Edit Controller: {profile.DisplayName} ===");
            Console.WriteLine("1. Ubah nama tampilan stik (Display Name & Nama di Windows joy.cpl)");
            Console.WriteLine("2. Mapping ulang SEMUA input (Full Wizard Lengkap)");
            Console.WriteLine("3. Mapping ulang SATU input tertentu");
            Console.WriteLine("4. Konfigurasi Invert Analog Stick (Balik Arah Sumbu X / Y)");
            Console.WriteLine("5. Konfigurasi Analog Sticks & Deadzone");
            Console.WriteLine("6. Konfigurasi Fitur Anti-Obrak Fix_L1 (Debounce Filter)");
            Console.WriteLine("7. Konfigurasi & Test Getaran (Vibration)");
            Console.WriteLine("8. Buka Game Controllers (joy.cpl) untuk cek hasil");
            Console.WriteLine("9. Kembali");
            Console.Write("Pilih menu [1-9]: ");

            string? choice = ReadCleanLine();
            switch (choice)
            {
                case "1":
                    EditControllerName(profile);
                    break;

                case "2":
                    RemapProfileFull(profile);
                    break;

                case "3":
                    RemapSingleInput(profile);
                    break;

                case "4":
                    ConfigureInvertMenu(profile);
                    break;

                case "5":
                    ConfigureSticksAndDeadzone(profile);
                    break;

                case "6":
                    ConfigureL1Debounce(profile);
                    break;

                case "7":
                    ConfigureVibrationMenu(profile);
                    break;

                case "8":
                    JoystickNameManager.OpenJoyCpl();
                    break;

                default:
                    Console.WriteLine("Dibatalkan.\n");
                    break;
            }
        }

        static void EditControllerName(ControllerProfile profile)
        {
            Console.WriteLine();
            Console.WriteLine("==============================================================");
            Console.WriteLine($"  UBAH NAMA TAMPILAN CONTROLLER: {profile.DisplayName}");
            Console.WriteLine("==============================================================");
            string currentWinName = JoystickNameManager.GetWindowsControllerName(JoystickNameManager.DEFAULT_VID, profile.VirtualPid);
            Console.WriteLine($"Nama di Aplikasi (Fix_L1) : {profile.DisplayName}");
            Console.WriteLine($"Nama di Windows (joy.cpl)  : {currentWinName}");
            Console.WriteLine($"Virtual Device ID          : VID 045E & PID {profile.VirtualPid:X4}");
            Console.WriteLine();
            Console.WriteLine("1. Ubah nama stik (Otomatis update di Aplikasi & Windows joy.cpl)");
            Console.WriteLine("2. Ubah nama khusus di Windows joy.cpl saja (nama di Fix_L1 tetap)");
            Console.WriteLine("3. Kembalikan nama Windows ke default (\"Controller (XBOX 360 For Windows)\")");
            Console.WriteLine("4. Buka Game Controllers (joy.cpl) sekarang");
            Console.WriteLine("5. Kembali");
            Console.Write("Pilih [1-5]: ");

            string? choice = ReadCleanLine();
            switch (choice)
            {
                case "1":
                    Console.Write("\nMasukkan nama tampilan baru: ");
                    string? newName = ReadCleanLine();
                    if (!string.IsNullOrWhiteSpace(newName))
                    {
                        profile.DisplayName = newName.Trim();
                        profile.WindowsCustomName = null;
                        JoystickNameManager.SetWindowsControllerName(JoystickNameManager.DEFAULT_VID, profile.VirtualPid, newName.Trim());
                        SaveProfiles();

                        bool wasActive = activeControllers.Any(c => c.Profile == profile);
                        if (wasActive)
                        {
                            Console.WriteLine("Menerapkan nama baru ke controller virtual yang sedang aktif...");
                            RestartController(profile);
                        }

                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"\n[BERHASIL] Nama controller diubah menjadi '{profile.DisplayName}'.");
                        Console.WriteLine("Nama di Windows Game Controllers (joy.cpl) langsung diperbarui!");
                        Console.ResetColor();

                        Console.Write("\nIngin buka Game Controllers (joy.cpl) untuk melihat perubahan? (y/n): ");
                        if (ReadCleanLine()?.ToLower() == "y")
                        {
                            JoystickNameManager.OpenJoyCpl();
                        }
                    }
                    break;

                case "2":
                    Console.Write("\nMasukkan nama khusus untuk Windows joy.cpl: ");
                    string? customWinName = ReadCleanLine();
                    if (!string.IsNullOrWhiteSpace(customWinName))
                    {
                        profile.WindowsCustomName = customWinName.Trim();
                        JoystickNameManager.SetWindowsControllerName(JoystickNameManager.DEFAULT_VID, profile.VirtualPid, customWinName.Trim());
                        SaveProfiles();

                        bool wasActive = activeControllers.Any(c => c.Profile == profile);
                        if (wasActive)
                        {
                            Console.WriteLine("Menerapkan nama baru ke controller virtual yang sedang aktif...");
                            RestartController(profile);
                        }

                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"\n[BERHASIL] Nama di Windows joy.cpl diubah menjadi '{profile.WindowsCustomName}'.");
                        Console.ResetColor();

                        Console.Write("\nIngin buka Game Controllers (joy.cpl) sekarang? (y/n): ");
                        if (ReadCleanLine()?.ToLower() == "y")
                        {
                            JoystickNameManager.OpenJoyCpl();
                        }
                    }
                    break;

                case "3":
                    profile.WindowsCustomName = JoystickNameManager.DEFAULT_XBOX_NAME;
                    JoystickNameManager.ResetWindowsControllerName(JoystickNameManager.DEFAULT_VID, profile.VirtualPid);
                    SaveProfiles();

                    bool wasActiveReset = activeControllers.Any(c => c.Profile == profile);
                    if (wasActiveReset)
                    {
                        RestartController(profile);
                    }

                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"\n[INFO] Nama Windows di-reset ke '{JoystickNameManager.DEFAULT_XBOX_NAME}'.");
                    Console.ResetColor();
                    break;

                case "4":
                    JoystickNameManager.OpenJoyCpl();
                    break;
            }
        }

        static void ManageWindowsJoystickNames()
        {
            if (savedProfiles.Count == 0)
            {
                Console.WriteLine("Belum ada controller tersimpan.\n");
                return;
            }

            Console.WriteLine();
            Console.WriteLine("==============================================================");
            Console.WriteLine("        PENGATURAN NAMA GAME CONTROLLERS DI WINDOWS           ");
            Console.WriteLine("==============================================================");
            Console.WriteLine("Fitur ini mengatur bagaimana stik kamu terdaftar di Windows");
            Console.WriteLine("Game Controllers (joy.cpl) dan di game.");
            Console.WriteLine();
            Console.WriteLine("Daftar controller tersimpan & nama tampilannya di Windows:");
            for (int i = 0; i < savedProfiles.Count; i++)
            {
                var p = savedProfiles[i];
                string winName = JoystickNameManager.GetWindowsControllerName(JoystickNameManager.DEFAULT_VID, p.VirtualPid);
                bool active = activeControllers.Any(c => c.Profile == p);
                Console.WriteLine($"{i + 1}. {p.DisplayName} [{(active ? "AKTIF" : "Tidak Terpasang")}]");
                Console.WriteLine($"   -> Nama di Windows (joy.cpl): \"{winName}\" (PID: 0x{p.VirtualPid:X4})");
            }
            Console.WriteLine();
            Console.WriteLine("Pilihan:");
            Console.WriteLine("1. Ubah nama tampilan stik di Windows joy.cpl");
            Console.WriteLine("2. Kembalikan semua nama Windows ke default (\"Controller (XBOX 360 For Windows)\")");
            Console.WriteLine("3. Buka Game Controllers (joy.cpl) sekarang");
            Console.WriteLine("4. Kembali");
            Console.Write("Pilih [1-4]: ");

            string? choice = ReadCleanLine();
            switch (choice)
            {
                case "1":
                    var profile = PickProfile("Pilih controller yang ingin diubah namanya:");
                    if (profile != null)
                    {
                        EditControllerName(profile);
                    }
                    break;

                case "2":
                    Console.Write("Yakin ingin mengembalikan semua nama controller di Windows ke default? (y/n): ");
                    if (ReadCleanLine()?.ToLower() == "y")
                    {
                        foreach (var p in savedProfiles)
                        {
                            p.WindowsCustomName = null;
                            JoystickNameManager.ResetWindowsControllerName(JoystickNameManager.DEFAULT_VID, p.VirtualPid);
                        }
                        SaveProfiles();

                        // Restart controller yang sedang aktif
                        foreach (var active in activeControllers.ToList())
                        {
                            RestartController(active.Profile);
                        }

                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine("\n[BERHASIL] Semua nama controller di Windows telah dikembalikan ke default.");
                        Console.ResetColor();

                        Console.Write("\nIngin buka Game Controllers (joy.cpl) untuk melihat perubahan? (y/n): ");
                        if (ReadCleanLine()?.ToLower() == "y")
                        {
                            JoystickNameManager.OpenJoyCpl();
                        }
                    }
                    break;

                case "3":
                    JoystickNameManager.OpenJoyCpl();
                    break;
            }
        }

        static void RemapProfileFull(ControllerProfile profile)
        {
            isWatcherPaused = true;
            try
            {
                int targetIndex = FindDeviceIndex(profile);
                if (targetIndex == -1)
                {
                    Console.WriteLine("Controller sedang tidak terpasang. Sambungkan stik USB terlebih dahulu.\n");
                    return;
                }

                bool wasActive = activeControllers.Any(c => c.Profile == profile);
                if (wasActive) StopControllerByProfile(profile);

                IntPtr joystick = SDL.SDL_JoystickOpen(targetIndex);
                if (joystick == IntPtr.Zero)
                {
                    Console.WriteLine($"Gagal membuka controller: {SDL.SDL_GetError()}\n");
                    if (wasActive) StartController(profile, targetIndex);
                    return;
                }

                RunFullMappingWizard(joystick, profile);
                SDL.SDL_JoystickClose(joystick);

                SaveProfiles();
                Console.WriteLine($"Mapping '{profile.DisplayName}' berhasil diperbarui.\n");

                if (wasActive) StartController(profile, targetIndex);
            }
            finally
            {
                isWatcherPaused = false;
            }
        }

        static void RemapSingleInput(ControllerProfile profile)
        {
            isWatcherPaused = true;
            try
            {
                int targetIndex = FindDeviceIndex(profile);
                if (targetIndex == -1)
                {
                    Console.WriteLine("Controller sedang tidak terpasang. Sambungkan stik USB terlebih dahulu.\n");
                    return;
                }

                var items = new (string Name, string Current, Action<IntPtr> Action)[]
                {
                    ("A", profile.Bindings.GetValueOrDefault("A")?.ToString() ?? "-", j => MapSingleDigitalPrompt(j, profile, "A", "Tombol [A]")),
                    ("B", profile.Bindings.GetValueOrDefault("B")?.ToString() ?? "-", j => MapSingleDigitalPrompt(j, profile, "B", "Tombol [B]")),
                    ("X", profile.Bindings.GetValueOrDefault("X")?.ToString() ?? "-", j => MapSingleDigitalPrompt(j, profile, "X", "Tombol [X]")),
                    ("Y", profile.Bindings.GetValueOrDefault("Y")?.ToString() ?? "-", j => MapSingleDigitalPrompt(j, profile, "Y", "Tombol [Y]")),
                    ("LB (Left Bumper)", profile.Bindings.GetValueOrDefault("LeftShoulder")?.ToString() ?? "-", j => MapSingleDigitalPrompt(j, profile, "LeftShoulder", "Tombol [LB]")),
                    ("RB (Right Bumper)", profile.Bindings.GetValueOrDefault("RightShoulder")?.ToString() ?? "-", j => MapSingleDigitalPrompt(j, profile, "RightShoulder", "Tombol [RB]")),
                    ("LT (Left Trigger)", profile.LeftTrigger.ToString(), j => MapTriggerPrompt(j, profile, isLeft: true)),
                    ("RT (Right Trigger)", profile.RightTrigger.ToString(), j => MapTriggerPrompt(j, profile, isLeft: false)),
                    ("Back", profile.Bindings.GetValueOrDefault("Back")?.ToString() ?? "-", j => MapSingleDigitalPrompt(j, profile, "Back", "Tombol [Back]")),
                    ("Start", profile.Bindings.GetValueOrDefault("Start")?.ToString() ?? "-", j => MapSingleDigitalPrompt(j, profile, "Start", "Tombol [Start]")),
                    ("Guide (Xbox Logo)", profile.Bindings.GetValueOrDefault("Guide")?.ToString() ?? "-", j => MapSingleDigitalPrompt(j, profile, "Guide", "Tombol [Guide]")),
                    ("LS Click (Left Stick Click)", profile.Bindings.GetValueOrDefault("LeftThumb")?.ToString() ?? "-", j => MapSingleDigitalPrompt(j, profile, "LeftThumb", "Tombol [LS Click]")),
                    ("RS Click (Right Stick Click)", profile.Bindings.GetValueOrDefault("RightThumb")?.ToString() ?? "-", j => MapSingleDigitalPrompt(j, profile, "RightThumb", "Tombol [RS Click]")),
                    ("D-Pad Up", profile.Bindings.GetValueOrDefault("DpadUp")?.ToString() ?? "-", j => MapSingleDigitalPrompt(j, profile, "DpadUp", "D-Pad [Up]")),
                    ("D-Pad Down", profile.Bindings.GetValueOrDefault("DpadDown")?.ToString() ?? "-", j => MapSingleDigitalPrompt(j, profile, "DpadDown", "D-Pad [Down]")),
                    ("D-Pad Left", profile.Bindings.GetValueOrDefault("DpadLeft")?.ToString() ?? "-", j => MapSingleDigitalPrompt(j, profile, "DpadLeft", "D-Pad [Left]")),
                    ("D-Pad Right", profile.Bindings.GetValueOrDefault("DpadRight")?.ToString() ?? "-", j => MapSingleDigitalPrompt(j, profile, "DpadRight", "D-Pad [Right]")),
                    ("Left Stick: Left / Right", $"Axis {profile.LeftStickXAxis} (Invert: {profile.InvertLeftStickX})", j => MapSingleStickAxisPrompt(j, profile, isRight: false, isVertical: false)),
                    ("Left Stick: Up / Down", $"Axis {profile.LeftStickYAxis} (Invert: {profile.InvertLeftStickY})", j => MapSingleStickAxisPrompt(j, profile, isRight: false, isVertical: true)),
                    ("Right Stick: Left / Right", $"Axis {profile.RightStickXAxis} (Invert: {profile.InvertRightStickX})", j => MapSingleStickAxisPrompt(j, profile, isRight: true, isVertical: false)),
                    ("Right Stick: Up / Down", $"Axis {profile.RightStickYAxis} (Invert: {profile.InvertRightStickY})", j => MapSingleStickAxisPrompt(j, profile, isRight: true, isVertical: true))
                };

                Console.WriteLine();
                Console.WriteLine("==============================================================");
                Console.WriteLine("            PILIH SATU INPUT UNTUK DIREMAP (1-21)             ");
                Console.WriteLine("==============================================================");
                for (int i = 0; i < items.Length; i++)
                {
                    Console.WriteLine($"{i + 1,2}. {items[i].Name,-35} -> {items[i].Current}");
                }
                Console.Write("\nPilih nomor input [1-21] (kosongkan untuk batal): ");
                string? input = ReadCleanLine();
                if (string.IsNullOrWhiteSpace(input) || !int.TryParse(input, out int idx) || idx < 1 || idx > items.Length)
                {
                    Console.WriteLine("Dibatalkan.\n");
                    return;
                }

                var chosen = items[idx - 1];

                bool wasActive = activeControllers.Any(c => c.Profile == profile);
                if (wasActive) StopControllerByProfile(profile);

                IntPtr joystick = SDL.SDL_JoystickOpen(targetIndex);
                if (joystick == IntPtr.Zero)
                {
                    Console.WriteLine($"Gagal membuka controller: {SDL.SDL_GetError()}\n");
                    if (wasActive) StartController(profile, targetIndex);
                    return;
                }

                Console.WriteLine();
                Console.WriteLine($"=== Mulai Remap: {chosen.Name} ===");
                chosen.Action(joystick);

                SDL.SDL_JoystickClose(joystick);
                SaveProfiles();

                if (wasActive) StartController(profile, targetIndex);
                Console.WriteLine("Input berhasil diperbarui dan disimpan.\n");
            }
            finally
            {
                isWatcherPaused = false;
            }
        }

        static void ConfigureInvertMenu(ControllerProfile profile)
        {
            while (true)
            {
                Console.WriteLine();
                Console.WriteLine("==============================================================");
                Console.WriteLine($"    KONFIGURASI INVERT ANALOG STICK: {profile.DisplayName}     ");
                Console.WriteLine("==============================================================");
                Console.WriteLine($"1. Invert Left Stick Up/Down    (Y) : {(profile.InvertLeftStickY ? "[AKTIF - Terbalik]" : "[NONAKTIF - Normal]")}");
                Console.WriteLine($"2. Invert Left Stick Left/Right (X) : {(profile.InvertLeftStickX ? "[AKTIF - Terbalik]" : "[NONAKTIF - Normal]")}");
                Console.WriteLine($"3. Invert Right Stick Up/Down   (Y) : {(profile.InvertRightStickY ? "[AKTIF - Terbalik]" : "[NONAKTIF - Normal]")}");
                Console.WriteLine($"4. Invert Right Stick Left/Right(X) : {(profile.InvertRightStickX ? "[AKTIF - Terbalik]" : "[NONAKTIF - Normal]")}");
                Console.WriteLine("5. Kembali");
                Console.Write("Pilih [1-5]: ");

                string? choice = ReadCleanLine();
                switch (choice)
                {
                    case "1":
                        profile.InvertLeftStickY = !profile.InvertLeftStickY;
                        SaveProfiles();
                        Console.WriteLine($"\n> Invert Left Stick Up/Down (Y) diubah menjadi: {(profile.InvertLeftStickY ? "AKTIF" : "NONAKTIF")}");
                        break;

                    case "2":
                        profile.InvertLeftStickX = !profile.InvertLeftStickX;
                        SaveProfiles();
                        Console.WriteLine($"\n> Invert Left Stick Left/Right (X) diubah menjadi: {(profile.InvertLeftStickX ? "AKTIF" : "NONAKTIF")}");
                        break;

                    case "3":
                        profile.InvertRightStickY = !profile.InvertRightStickY;
                        SaveProfiles();
                        Console.WriteLine($"\n> Invert Right Stick Up/Down (Y) diubah menjadi: {(profile.InvertRightStickY ? "AKTIF" : "NONAKTIF")}");
                        break;

                    case "4":
                        profile.InvertRightStickX = !profile.InvertRightStickX;
                        SaveProfiles();
                        Console.WriteLine($"\n> Invert Right Stick Left/Right (X) diubah menjadi: {(profile.InvertRightStickX ? "AKTIF" : "NONAKTIF")}");
                        break;

                    case "5":
                    case "":
                        return;

                    default:
                        Console.WriteLine("Pilihan tidak valid.");
                        break;
                }
            }
        }

        static void ConfigureSticksAndDeadzone(ControllerProfile profile)
        {
            Console.WriteLine();
            Console.WriteLine($"=== Konfigurasi Deadzone & Stik: {profile.DisplayName} ===");
            Console.WriteLine($"1. Deadzone Analog Sticks (Saat ini: {profile.Deadzone})");
            Console.WriteLine($"2. Deadzone Trigger RT / R2 (Saat ini: {profile.RightTriggerDeadzone}%) [Anti-Ghosting Sensor Rusak]");
            Console.WriteLine($"3. Deadzone Trigger LT / L2 (Saat ini: {profile.LeftTriggerDeadzone}%)");
            Console.WriteLine($"4. Konfigurasi Invert Analog Stick (Buka Menu Invert X & Y)");
            Console.WriteLine($"5. Manual set Axis Stik Kiri (X={profile.LeftStickXAxis}, Y={profile.LeftStickYAxis})");
            Console.WriteLine($"6. Manual set Axis Stik Kanan (X={profile.RightStickXAxis}, Y={profile.RightStickYAxis})");
            Console.WriteLine("7. Kembali");
            Console.Write("Pilih [1-7]: ");

            string? choice = ReadCleanLine();
            switch (choice)
            {
                case "1":
                    Console.Write("Masukkan nilai Deadzone Analog Stick baru (rekomendasi: 8000 - 15000): ");
                    if (int.TryParse(ReadCleanLine(), out int dz) && dz >= 0 && dz <= 30000)
                    {
                        profile.Deadzone = dz;
                        SaveProfiles();
                        Console.WriteLine("Deadzone Analog Stick berhasil diubah.");
                    }
                    break;

                case "2":
                    Console.Write("Masukkan Deadzone RT / R2 dalam persen (0 - 90%, rekomendasi: 35): ");
                    if (int.TryParse(ReadCleanLine(), out int rtdz) && rtdz >= 0 && rtdz <= 90)
                    {
                        profile.RightTriggerDeadzone = rtdz;
                        SaveProfiles();
                        Console.WriteLine($"Deadzone RT diubah menjadi {rtdz}% (Input 0 s.d. {rtdz}% akan dianggap 0).");
                    }
                    break;

                case "3":
                    Console.Write("Masukkan Deadzone LT / L2 dalam persen (0 - 90%): ");
                    if (int.TryParse(ReadCleanLine(), out int ltdz) && ltdz >= 0 && ltdz <= 90)
                    {
                        profile.LeftTriggerDeadzone = ltdz;
                        SaveProfiles();
                        Console.WriteLine($"Deadzone LT diubah menjadi {ltdz}%.");
                    }
                    break;

                case "4":
                    ConfigureInvertMenu(profile);
                    break;

                case "5":
                    Console.Write("Masukkan Axis X Stik Kiri (0..n): ");
                    if (int.TryParse(ReadCleanLine(), out int lx)) profile.LeftStickXAxis = lx;
                    Console.Write("Masukkan Axis Y Stik Kiri (0..n): ");
                    if (int.TryParse(ReadCleanLine(), out int ly)) profile.LeftStickYAxis = ly;
                    SaveProfiles();
                    Console.WriteLine("Axis Stik Kiri diperbarui.");
                    break;

                case "6":
                    Console.Write("Masukkan Axis X Stik Kanan (0..n): ");
                    if (int.TryParse(ReadCleanLine(), out int rx)) profile.RightStickXAxis = rx;
                    Console.Write("Masukkan Axis Y Stik Kanan (0..n): ");
                    if (int.TryParse(ReadCleanLine(), out int ry)) profile.RightStickYAxis = ry;
                    SaveProfiles();
                    Console.WriteLine("Axis Stik Kanan diperbarui.");
                    break;
            }
        }

        static void ConfigureL1Debounce(ControllerProfile profile)
        {
            Console.WriteLine();
            Console.WriteLine($"=== Fitur Anti-Obrak Fix_L1 (Debounce Filter): {profile.DisplayName} ===");
            Console.WriteLine("Fitur ini dirancang khusus untuk mengatasi tombol L1 yang switch-nya sudah kendor / chattering.");
            Console.WriteLine($"Status saat ini: {(profile.L1DebounceEnabled ? "AKTIF" : "NONAKTIF")} (Threshold: {profile.L1DebounceMs} ms)");
            Console.WriteLine("1. Toggle Aktif / Nonaktifkan Filter");
            Console.WriteLine("2. Ubah Waktu Debounce Threshold (ms)");
            Console.WriteLine("3. Kembali");
            Console.Write("Pilih: ");

            string? choice = ReadCleanLine();
            switch (choice)
            {
                case "1":
                    profile.L1DebounceEnabled = !profile.L1DebounceEnabled;
                    SaveProfiles();
                    Console.WriteLine($"Filter L1 sekarang: {(profile.L1DebounceEnabled ? "AKTIF" : "NONAKTIF")}");
                    break;

                case "2":
                    Console.Write("Masukkan waktu debounce dalam milidetik (rekomendasi: 15 - 50 ms): ");
                    if (int.TryParse(ReadCleanLine(), out int ms) && ms >= 5 && ms <= 200)
                    {
                        profile.L1DebounceMs = ms;
                        SaveProfiles();
                        Console.WriteLine($"Threshold debounce L1 diubah menjadi {ms} ms.");
                    }
                    break;
            }
        }

        static void ConfigureVibrationMenu(ControllerProfile profile)
        {
            Console.WriteLine();
            Console.WriteLine($"=== Konfigurasi & Test Getaran: {profile.DisplayName} ===");
            Console.WriteLine($"Status Getaran: {(profile.VibrationEnabled ? "AKTIF" : "NONAKTIF")}");
            Console.WriteLine($"Kekuatan Getar: {profile.VibrationStrength}%");
            Console.WriteLine("1. Toggle Aktif / Nonaktif Getaran");
            Console.WriteLine("2. Atur Kekuatan Getar (0 - 100%)");
            Console.WriteLine("3. Uji Getar: Motor Kiri (Heavy / Low Freq)");
            Console.WriteLine("4. Uji Getar: Motor Kanan (Light / High Freq)");
            Console.WriteLine("5. Uji Getar: Kedua Motor (Maksimal)");
            Console.WriteLine("6. Uji Getar: Denyut (Pulse)");
            Console.WriteLine("7. Kembali");
            Console.Write("Pilih [1-7]: ");

            string? choice = ReadCleanLine();
            switch (choice)
            {
                case "1":
                    profile.VibrationEnabled = !profile.VibrationEnabled;
                    SaveProfiles();
                    Console.WriteLine($"Getaran sekarang: {(profile.VibrationEnabled ? "AKTIF" : "NONAKTIF")}");
                    break;

                case "2":
                    Console.Write("Masukkan kekuatan getar (0 - 100): ");
                    if (int.TryParse(ReadCleanLine(), out int p) && p >= 0 && p <= 100)
                    {
                        profile.VibrationStrength = p;
                        SaveProfiles();
                        Console.WriteLine($"Kekuatan getar diatur ke {p}%.");
                    }
                    break;

                case "3":
                case "4":
                case "5":
                case "6":
                    int targetIndex = FindDeviceIndex(profile);
                    if (targetIndex == -1)
                    {
                        Console.WriteLine("Controller tidak terpasang untuk uji getaran.\n");
                        return;
                    }
                    IntPtr joy = SDL.SDL_JoystickOpen(targetIndex);
                    if (joy != IntPtr.Zero)
                    {
                        using var vib = new VibrationController(joy);
                        int pattern = int.Parse(choice) - 2;
                        Console.WriteLine("Menjalankan uji getaran...");
                        vib.TestVibratePattern(pattern);
                        SDL.SDL_JoystickClose(joy);
                        Console.WriteLine("Uji getaran selesai.");
                    }
                    break;
            }
        }

        // ================= 4. TEST CONTROLLER LIVE =================

        static void TestControllerMenu()
        {
            if (savedProfiles.Count == 0)
            {
                Console.WriteLine("Belum ada controller tersimpan untuk diuji.\n");
                return;
            }

            var profile = PickProfile("Pilih controller yang ingin diuji:");
            if (profile == null) return;

            int targetIndex = FindDeviceIndex(profile);
            if (targetIndex == -1)
            {
                Console.WriteLine("Controller sedang tidak terpasang.\n");
                return;
            }

            Console.WriteLine();
            Console.WriteLine("Pilih mode pengujian:");
            Console.WriteLine("1. Live Input Viewer (Pantau tombol, stick, trigger & D-pad real-time)");
            Console.WriteLine("2. Test Getaran (Vibration Test)");
            Console.WriteLine("3. Kembali");
            Console.Write("Pilih: ");

            string? mode = ReadCleanLine();
            if (mode == "1")
            {
                RunLiveInputViewer(profile, targetIndex);
            }
            else if (mode == "2")
            {
                ConfigureVibrationMenu(profile);
            }
        }

        static void RunLiveInputViewer(ControllerProfile profile, int sdlIndex)
        {
            isWatcherPaused = true;
            try
            {
                bool wasActive = activeControllers.Any(c => c.Profile == profile);
                if (wasActive) StopControllerByProfile(profile);

                IntPtr joystick = SDL.SDL_JoystickOpen(sdlIndex);
                if (joystick == IntPtr.Zero)
                {
                    Console.WriteLine($"Gagal membuka controller: {SDL.SDL_GetError()}");
                    if (wasActive) StartController(profile, sdlIndex);
                    return;
                }

                try { Console.Clear(); } catch { }
                Console.WriteLine("===============================================================");
                Console.WriteLine($"       LIVE INPUT VIEWER: {profile.DisplayName}");
                Console.WriteLine("===============================================================");
                Console.WriteLine("Tekan sembarang tombol di keyboard (atau Ctrl+C) untuk keluar.");
                Console.WriteLine("===============================================================\n");

                int topPos = Console.CursorTop;

                try
                {
                    while (!Console.KeyAvailable)
                    {
                        SDL.SDL_PumpEvents();
                        SDL.SDL_JoystickUpdate();

                        // Baca tombol aktif
                        var pressedButtons = new List<string>();
                        foreach (var kv in profile.Bindings)
                        {
                            if (kv.Value.IsPressed(joystick))
                            {
                                pressedButtons.Add(kv.Key);
                            }
                        }

                        // Baca Triggers
                        byte rawRt = profile.RightTrigger.GetValue(joystick, 0); // Raw tanpa filter
                        byte lt = profile.LeftTrigger.GetValue(joystick, profile.LeftTriggerDeadzone);
                        byte rt = profile.RightTrigger.GetValue(joystick, profile.RightTriggerDeadzone);

                        // Baca Analog Sticks
                        short rawLX = SDL.SDL_JoystickGetAxis(joystick, profile.LeftStickXAxis);
                        short rawLY = SDL.SDL_JoystickGetAxis(joystick, profile.LeftStickYAxis);
                        short rawRX = SDL.SDL_JoystickGetAxis(joystick, profile.RightStickXAxis);
                        short rawRY = SDL.SDL_JoystickGetAxis(joystick, profile.RightStickYAxis);

                        short lx = ApplyDeadzone(rawLX, profile.Deadzone);
                        short ly = ApplyDeadzone(rawLY, profile.Deadzone);
                        if (profile.InvertLeftStickX) lx = InvertAxis(lx);
                        if (profile.InvertLeftStickY) ly = InvertAxis(ly);

                        short rx = ApplyDeadzone(rawRX, profile.Deadzone);
                        short ry = ApplyDeadzone(rawRY, profile.Deadzone);
                        if (profile.InvertRightStickX) rx = InvertAxis(rx);
                        if (profile.InvertRightStickY) ry = InvertAxis(ry);

                        // D-Pad
                        bool up = profile.GetDigitalState(joystick, "DpadUp");
                        bool down = profile.GetDigitalState(joystick, "DpadDown");
                        bool left = profile.GetDigitalState(joystick, "DpadLeft");
                        bool right = profile.GetDigitalState(joystick, "DpadRight");
                        string dpadStr = $"{(up ? "▲" : "-")} {(down ? "▼" : "-")} {(left ? "◄" : "-")} {(right ? "►" : "-")}";

                        int ltPct = lt * 100 / 255;
                        int rtPct = rt * 100 / 255;
                        int rawRtPct = rawRt * 100 / 255;

                        try
                        {
                            Console.SetCursorPosition(0, topPos);
                        }
                        catch { }

                        Console.WriteLine($"[Tombol Ditekan]    : {(pressedButtons.Count > 0 ? string.Join(", ", pressedButtons) : "(tidak ada)")}".PadRight(70));
                        Console.WriteLine($"[D-Pad]             : {dpadStr}".PadRight(70));
                        Console.WriteLine($"[LT (Left Trigger)] : {lt,3}/255 ({ltPct,3}%)".PadRight(70));
                        Console.WriteLine($"[RT (Right Trigger)]: {rt,3}/255 ({rtPct,3}%) [Sensor Raw: {rawRtPct,3}%, Filter: {profile.RightTriggerDeadzone}%]".PadRight(70));
                        Console.WriteLine($"[Left Stick (LS)]   : Left/Right={lx,6} (Raw: {rawLX,6}) | Up/Down={ly,6} (Raw: {rawLY,6})".PadRight(70));
                        Console.WriteLine($"[Right Stick (RS)]  : Left/Right={rx,6} (Raw: {rawRX,6}) | Up/Down={ry,6} (Raw: {rawRY,6})".PadRight(70));
                        Console.WriteLine($"[LB Raw State]      : {(profile.GetDigitalState(joystick, "LeftShoulder") ? "DITEKAN" : "LEPAS")}".PadRight(70));

                        Thread.Sleep(30);
                    }
                    Console.ReadKey(true);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(ex.Message);
                }

                SDL.SDL_JoystickClose(joystick);
                if (wasActive) StartController(profile, sdlIndex);
                Console.WriteLine("\nLive Input Viewer ditutup.");
            }
            finally
            {
                isWatcherPaused = false;
            }
        }

        // ================= 5. DELETE CONTROLLER =================

        static void DeleteController()
        {
            if (savedProfiles.Count == 0)
            {
                Console.WriteLine("Belum ada controller yang tersimpan.\n");
                return;
            }

            var profile = PickProfile("Pilih controller yang ingin dihapus:");
            if (profile == null) return;

            Console.Write($"Yakin mau menghapus '{profile.DisplayName}'? (y/n): ");
            string? confirm = ReadCleanLine()?.ToLower();
            if (confirm != "y")
            {
                Console.WriteLine("Dibatalkan.\n");
                return;
            }

            StopControllerByProfile(profile);
            JoystickNameManager.CleanupOemKey(JoystickNameManager.DEFAULT_VID, profile.VirtualPid);
            savedProfiles.Remove(profile);
            SaveProfiles();
            Console.WriteLine($"Controller '{profile.DisplayName}' berhasil dihapus.\n");
        }

        // ================= RUNTIME LOOP CONTROLLER =================

        static void StartController(ControllerProfile profile, int sdlIndex)
        {
            lock (listLock)
            {
                if (activeControllers.Any(c => c.Profile == profile))
                {
                    return; // Sudah aktif, jangan buat duplikat!
                }
            }

            IntPtr joystick = SDL.SDL_JoystickOpen(sdlIndex);
            if (joystick == IntPtr.Zero)
            {
                Console.WriteLine($"Gagal mengaktifkan '{profile.DisplayName}': {SDL.SDL_GetError()}");
                return;
            }

            var vib = new VibrationController(joystick);

            // Pastikan nama Windows terdaftar di Registry sebelum connect
            JoystickNameManager.SetWindowsControllerName(
                JoystickNameManager.DEFAULT_VID,
                profile.VirtualPid,
                profile.GetEffectiveWindowsName());

            var runtime = new ControllerRuntime
            {
                Profile = profile,
                JoystickHandle = joystick,
                SdlIndex = sdlIndex,
                Xbox = vigemClient!.CreateXbox360Controller(JoystickNameManager.DEFAULT_VID, profile.VirtualPid),
                Cts = new CancellationTokenSource(),
                Vibration = vib
            };

            runtime.Xbox.FeedbackReceived += (s, e) => OnVibrationReceived(runtime, e);
            runtime.Xbox.Connect();

            runtime.Thread = new Thread(() => RunControllerLoop(runtime))
            {
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };

            lock (listLock) { activeControllers.Add(runtime); }
            runtime.Thread.Start();

            lock (consoleLock)
            {
                Console.WriteLine($"[AKTIF] {profile.DisplayName} -> Virtual Xbox 360 terhubung (Nama Windows: \"{profile.GetEffectiveWindowsName()}\").");
                Console.WriteLine($"        Fitur Getaran: {(vib.IsSupported && profile.VibrationEnabled ? "SIAP & AKTIF" : "Tidak didukung perangkat ini / Nonaktif")}");
                if (profile.L1DebounceEnabled)
                {
                    Console.WriteLine($"        Fitur Anti-Obrak Fix_L1: AKTIF ({profile.L1DebounceMs}ms debounce filter)");
                }
            }
        }

        static void RestartController(ControllerProfile profile)
        {
            int targetIndex = FindDeviceIndex(profile);
            StopControllerByProfile(profile);
            Thread.Sleep(300);
            if (targetIndex != -1)
            {
                StartController(profile, targetIndex);
            }
        }

        static void StopControllerByProfile(ControllerProfile profile)
        {
            List<ControllerRuntime> targets;
            lock (listLock)
            {
                targets = activeControllers.Where(c => c.Profile == profile).ToList();
            }
            if (targets.Count == 0) return;

            foreach (var rt in targets)
            {
                try { rt.Cts.Cancel(); } catch { }
                try { rt.Thread?.Join(500); } catch { }
                try { rt.Dispose(); } catch { }
                try { SDL.SDL_JoystickClose(rt.JoystickHandle); } catch { }
                lock (listLock) { activeControllers.Remove(rt); }
            }
        }

        static void StopAllControllers()
        {
            lock (listLock)
            {
                foreach (var rt in activeControllers)
                {
                    rt.Cts.Cancel();
                }
            }
            foreach (var rt in activeControllers.ToList())
            {
                rt.Thread?.Join(500);
                rt.Dispose();
                try { SDL.SDL_JoystickClose(rt.JoystickHandle); } catch { }
            }
            activeControllers.Clear();
        }

        static void RunControllerLoop(ControllerRuntime rt)
        {
            var profile = rt.Profile;
            IntPtr joystick = rt.JoystickHandle;

            try
            {
                while (!rt.Cts.IsCancellationRequested)
                {
                    SDL.SDL_PumpEvents();
                    SDL.SDL_JoystickUpdate();

                    // === 1. TOMBOL AKSI UTAMA ===
                    rt.Xbox.SetButtonState(Xbox360Button.A, profile.GetDigitalState(joystick, "A"));
                    rt.Xbox.SetButtonState(Xbox360Button.B, profile.GetDigitalState(joystick, "B"));
                    rt.Xbox.SetButtonState(Xbox360Button.X, profile.GetDigitalState(joystick, "X"));
                    rt.Xbox.SetButtonState(Xbox360Button.Y, profile.GetDigitalState(joystick, "Y"));

                    // === 2. BUMPER / SHOULDER DENGAN FITUR UTAMA FIX_L1 ===
                    bool rawL1 = profile.GetDigitalState(joystick, "LeftShoulder");
                    bool rawR1 = profile.GetDigitalState(joystick, "RightShoulder");

                    if (profile.L1DebounceEnabled)
                    {
                        if (rawL1)
                        {
                            rt.FilteredL1 = true;
                            rt.PrevRawL1 = true;
                        }
                        else
                        {
                            if (rt.PrevRawL1)
                            {
                                rt.OffStart = DateTime.Now;
                                rt.PrevRawL1 = false;
                            }
                            if ((DateTime.Now - rt.OffStart).TotalMilliseconds > profile.L1DebounceMs)
                            {
                                rt.FilteredL1 = false;
                            }
                        }
                        rt.Xbox.SetButtonState(Xbox360Button.LeftShoulder, rt.FilteredL1);
                    }
                    else
                    {
                        rt.Xbox.SetButtonState(Xbox360Button.LeftShoulder, rawL1);
                    }
                    rt.Xbox.SetButtonState(Xbox360Button.RightShoulder, rawR1);

                    // === 3. TOMBOL MENU & SPECIAL ===
                    rt.Xbox.SetButtonState(Xbox360Button.Back, profile.GetDigitalState(joystick, "Back"));
                    rt.Xbox.SetButtonState(Xbox360Button.Start, profile.GetDigitalState(joystick, "Start"));
                    rt.Xbox.SetButtonState(Xbox360Button.Guide, profile.GetDigitalState(joystick, "Guide"));

                    // === 4. THUMBSTICK CLICKS ===
                    rt.Xbox.SetButtonState(Xbox360Button.LeftThumb, profile.GetDigitalState(joystick, "LeftThumb"));
                    rt.Xbox.SetButtonState(Xbox360Button.RightThumb, profile.GetDigitalState(joystick, "RightThumb"));

                    // === 5. D-PAD ===
                    rt.Xbox.SetButtonState(Xbox360Button.Up, profile.GetDigitalState(joystick, "DpadUp"));
                    rt.Xbox.SetButtonState(Xbox360Button.Down, profile.GetDigitalState(joystick, "DpadDown"));
                    rt.Xbox.SetButtonState(Xbox360Button.Left, profile.GetDigitalState(joystick, "DpadLeft"));
                    rt.Xbox.SetButtonState(Xbox360Button.Right, profile.GetDigitalState(joystick, "DpadRight"));

                    // === 6. TRIGGERS (LT & RT) ===
                    byte lt = profile.LeftTrigger.GetValue(joystick, profile.LeftTriggerDeadzone);
                    byte rtVal = profile.RightTrigger.GetValue(joystick, profile.RightTriggerDeadzone);
                    rt.Xbox.SetSliderValue(Xbox360Slider.LeftTrigger, lt);
                    rt.Xbox.SetSliderValue(Xbox360Slider.RightTrigger, rtVal);

                    // === 7. STIK ANALOG KIRI (LEFT STICK) ===
                    short rawLX = SDL.SDL_JoystickGetAxis(joystick, profile.LeftStickXAxis);
                    short rawLY = SDL.SDL_JoystickGetAxis(joystick, profile.LeftStickYAxis);
                    short lx = ApplyDeadzone(rawLX, profile.Deadzone);
                    short ly = ApplyDeadzone(rawLY, profile.Deadzone);
                    if (profile.InvertLeftStickX) lx = InvertAxis(lx);
                    if (profile.InvertLeftStickY) ly = InvertAxis(ly);
                    rt.Xbox.SetAxisValue(Xbox360Axis.LeftThumbX, lx);
                    rt.Xbox.SetAxisValue(Xbox360Axis.LeftThumbY, ly);

                    // === 8. STIK ANALOG KANAN (RIGHT STICK) ===
                    short rawRX = SDL.SDL_JoystickGetAxis(joystick, profile.RightStickXAxis);
                    short rawRY = SDL.SDL_JoystickGetAxis(joystick, profile.RightStickYAxis);
                    short rx = ApplyDeadzone(rawRX, profile.Deadzone);
                    short ry = ApplyDeadzone(rawRY, profile.Deadzone);
                    if (profile.InvertRightStickX) rx = InvertAxis(rx);
                    if (profile.InvertRightStickY) ry = InvertAxis(ry);
                    rt.Xbox.SetAxisValue(Xbox360Axis.RightThumbX, rx);
                    rt.Xbox.SetAxisValue(Xbox360Axis.RightThumbY, ry);

                    // Kirim report ke virtual Xbox controller
                    rt.Xbox.SubmitReport();

                    Thread.Sleep(4); // 250 Hz polling rate
                }
            }
            catch (Exception ex)
            {
                lock (consoleLock)
                {
                    Console.WriteLine($"\n[TERPUTUS] {rt.Profile.DisplayName}: {ex.Message}");
                }
            }
            finally
            {
                lock (listLock) { activeControllers.Remove(rt); }
            }
        }

        // ================= GETARAN / VIBRATION EVENT =================

        static void OnVibrationReceived(ControllerRuntime rt, Xbox360FeedbackReceivedEventArgs e)
        {
            if (rt.Profile == null || !rt.Profile.VibrationEnabled || rt.Vibration == null) return;

            float strengthFactor = rt.Profile.VibrationStrength / 100.0f;
            byte large = (byte)Math.Clamp((int)(e.LargeMotor * strengthFactor), 0, 255);
            byte small = (byte)Math.Clamp((int)(e.SmallMotor * strengthFactor), 0, 255);

            rt.Vibration.SetVibration(large, small);
        }

        // ================= UTILITAS =================

        static short InvertAxis(short value)
        {
            if (value <= -32767) return 32767;
            return (short)-value;
        }

        static short ApplyDeadzone(short rawValue, int deadzone)
        {
            if (Math.Abs((int)rawValue) < deadzone)
            {
                return 0;
            }
            return rawValue;
        }

        static string GetJoystickGuidString(int sdlIndex)
        {
            var guid = SDL.SDL_JoystickGetDeviceGUID(sdlIndex);
            byte[] bytes = new byte[16];
            unsafe
            {
                byte* ptr = (byte*)&guid;
                for (int i = 0; i < 16; i++) bytes[i] = ptr[i];
            }
            return BitConverter.ToString(bytes).Replace("-", "").ToLower();
        }

        static int FindDeviceIndex(ControllerProfile profile)
        {
            SDL.SDL_PumpEvents();
            SDL.SDL_JoystickUpdate();
            int numJoysticks = SDL.SDL_NumJoysticks();
            for (int i = 0; i < numJoysticks; i++)
            {
                string instanceName = SDL.SDL_JoystickNameForIndex(i) ?? "";
                string productGuid = GetJoystickGuidString(i);
                if (productGuid == profile.ProductGuid && instanceName == profile.DeviceName)
                {
                    return i;
                }
            }

            // Fallback 1: Cocokkan ProductGuid
            for (int i = 0; i < numJoysticks; i++)
            {
                if (GetJoystickGuidString(i) == profile.ProductGuid) return i;
            }

            // Fallback 2: Cocokkan DeviceName
            for (int i = 0; i < numJoysticks; i++)
            {
                string instanceName = SDL.SDL_JoystickNameForIndex(i) ?? "";
                if (!string.IsNullOrEmpty(instanceName) && instanceName == profile.DeviceName) return i;
            }

            // Fallback 3: Jika hanya ada 1 joystick terpasang
            if (numJoysticks == 1) return 0;

            return -1;
        }

        static ControllerProfile? PickProfile(string prompt)
        {
            Console.WriteLine();
            Console.WriteLine(prompt);
            for (int i = 0; i < savedProfiles.Count; i++)
            {
                bool active = activeControllers.Any(c => c.Profile == savedProfiles[i]);
                Console.WriteLine($"{i + 1}. {savedProfiles[i].DisplayName} [{(active ? "AKTIF" : "Tidak Terpasang")}]");
            }
            Console.Write("Pilih nomor (kosongkan untuk batal): ");
            string? input = ReadCleanLine();
            if (string.IsNullOrWhiteSpace(input)) return null;

            if (!int.TryParse(input, out int idx) || idx < 1 || idx > savedProfiles.Count)
            {
                Console.WriteLine("Pilihan tidak valid.\n");
                return null;
            }

            return savedProfiles[idx - 1];
        }
    }
}
