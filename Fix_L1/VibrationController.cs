using System;
using System.Threading;
using SDL2;

namespace Fix_L1
{
    public class VibrationController : IDisposable
    {
        private readonly IntPtr joystick;
        private IntPtr haptic = IntPtr.Zero;
        private bool hapticInitialized = false;
        private bool hapticRumbleSupported = false;
        private bool preferHaptic = false;
        private readonly object vibLock = new();

        public bool IsSupported { get; private set; } = false;

        public VibrationController(IntPtr joystickHandle)
        {
            this.joystick = joystickHandle;
            InitVibration();
        }

        private void InitVibration()
        {
            if (joystick == IntPtr.Zero) return;

            try
            {
                // Cek apakah controller mendukung Haptic SDL2
                if (SDL.SDL_JoystickIsHaptic(joystick) > 0)
                {
                    haptic = SDL.SDL_HapticOpenFromJoystick(joystick);
                    if (haptic != IntPtr.Zero)
                    {
                        if (SDL.SDL_HapticRumbleSupported(haptic) > 0)
                        {
                            if (SDL.SDL_HapticRumbleInit(haptic) == 0)
                            {
                                hapticRumbleSupported = true;
                                hapticInitialized = true;
                                IsSupported = true;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Inisialisasi Haptic gagal: {ex.Message}");
            }

            // Test apakah SDL_JoystickRumble bisa dipanggil tanpa error
            try
            {
                int test = SDL.SDL_JoystickRumble(joystick, 0, 0, 0);
                if (test == 0)
                {
                    IsSupported = true;
                }
                else if (hapticInitialized)
                {
                    preferHaptic = true;
                }
            }
            catch
            {
                if (hapticInitialized) preferHaptic = true;
            }
        }

        public bool SetVibration(byte largeMotor, byte smallMotor, uint durationMs = 1000)
        {
            lock (vibLock)
            {
                if (joystick == IntPtr.Zero) return false;

                if (largeMotor == 0 && smallMotor == 0)
                {
                    Stop();
                    return true;
                }

                // largeMotor: Motor berat / frekuensi rendah (biasanya sisi kiri stik)
                // smallMotor: Motor ringan / frekuensi tinggi (biasanya sisi kanan stik)
                ushort lowFreq = (ushort)(largeMotor * 257);
                ushort highFreq = (ushort)(smallMotor * 257);

                if (!preferHaptic)
                {
                    int res = SDL.SDL_JoystickRumble(joystick, lowFreq, highFreq, durationMs);
                    if (res == 0)
                    {
                        IsSupported = true;
                        return true;
                    }
                    preferHaptic = true; // Alihkan ke haptic jika rumble direct gagal
                }

                if (hapticInitialized && hapticRumbleSupported && haptic != IntPtr.Zero)
                {
                    float strength = Math.Max(largeMotor, smallMotor) / 255.0f;
                    int hRes = SDL.SDL_HapticRumblePlay(haptic, strength, durationMs);
                    if (hRes == 0)
                    {
                        IsSupported = true;
                        return true;
                    }
                }

                return false;
            }
        }

        public void Stop()
        {
            lock (vibLock)
            {
                if (joystick != IntPtr.Zero)
                {
                    try { SDL.SDL_JoystickRumble(joystick, 0, 0, 0); } catch { }
                }

                if (hapticInitialized && haptic != IntPtr.Zero)
                {
                    try { SDL.SDL_HapticRumbleStop(haptic); } catch { }
                }
            }
        }

        public void TestVibratePattern(int patternType)
        {
            switch (patternType)
            {
                case 1:
                    // Motor Kiri (Heavy / Bass)
                    SetVibration(255, 0, 1500);
                    Thread.Sleep(1500);
                    Stop();
                    break;

                case 2:
                    // Motor Kanan (Light / Treble)
                    SetVibration(0, 255, 1500);
                    Thread.Sleep(1500);
                    Stop();
                    break;

                case 3:
                    // Kedua Motor (Maksimal)
                    SetVibration(255, 255, 2000);
                    Thread.Sleep(2000);
                    Stop();
                    break;

                case 4:
                    // Denyut (Pulse 3x)
                    for (int i = 0; i < 3; i++)
                    {
                        SetVibration(220, 220, 250);
                        Thread.Sleep(250);
                        Stop();
                        Thread.Sleep(150);
                    }
                    break;
            }
        }

        public void Dispose()
        {
            Stop();
            lock (vibLock)
            {
                if (haptic != IntPtr.Zero)
                {
                    try { SDL.SDL_HapticClose(haptic); } catch { }
                    haptic = IntPtr.Zero;
                }
            }
        }
    }
}
