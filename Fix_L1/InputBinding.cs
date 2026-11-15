using System;
using System.Text.Json.Serialization;
using SDL2;

namespace Fix_L1
{
    public enum InputType
    {
        None = 0,
        Button,      // Tombol digital fisik (index 0..n)
        HatUp,       // D-Pad arah Atas dari Hat (POV)
        HatDown,     // D-Pad arah Bawah dari Hat
        HatLeft,     // D-Pad arah Kiri dari Hat
        HatRight,    // D-Pad arah Kanan dari Hat
        AxisPos,     // Sumbu analog bernilai positif (> threshold)
        AxisNeg      // Sumbu analog bernilai negatif (< -threshold)
    }

    public class InputBinding
    {
        public InputType Type { get; set; } = InputType.None;
        public int Index { get; set; } = -1;

        public InputBinding() { }

        public InputBinding(InputType type, int index)
        {
            Type = type;
            Index = index;
        }

        public bool IsPressed(IntPtr joystick)
        {
            if (Index < 0 || Type == InputType.None || joystick == IntPtr.Zero) return false;

            switch (Type)
            {
                case InputType.Button:
                    return SDL.SDL_JoystickGetButton(joystick, Index) != 0;

                case InputType.HatUp:
                    return (SDL.SDL_JoystickGetHat(joystick, Index) & SDL.SDL_HAT_UP) != 0;

                case InputType.HatDown:
                    return (SDL.SDL_JoystickGetHat(joystick, Index) & SDL.SDL_HAT_DOWN) != 0;

                case InputType.HatLeft:
                    return (SDL.SDL_JoystickGetHat(joystick, Index) & SDL.SDL_HAT_LEFT) != 0;

                case InputType.HatRight:
                    return (SDL.SDL_JoystickGetHat(joystick, Index) & SDL.SDL_HAT_RIGHT) != 0;

                case InputType.AxisPos:
                    return SDL.SDL_JoystickGetAxis(joystick, Index) > 18000;

                case InputType.AxisNeg:
                    return SDL.SDL_JoystickGetAxis(joystick, Index) < -18000;

                default:
                    return false;
            }
        }

        public override string ToString()
        {
            return Type switch
            {
                InputType.Button => $"Tombol[{Index + 1}] (Index {Index})",
                InputType.HatUp => $"D-Pad Atas (Hat {Index})",
                InputType.HatDown => $"D-Pad Bawah (Hat {Index})",
                InputType.HatLeft => $"D-Pad Kiri (Hat {Index})",
                InputType.HatRight => $"D-Pad Kanan (Hat {Index})",
                InputType.AxisPos => $"Axis {Index} (+)",
                InputType.AxisNeg => $"Axis {Index} (-)",
                _ => "(tidak dipetakan)"
            };
        }
    }

    public class TriggerBinding
    {
        public bool IsAxis { get; set; } = false;
        public int AxisIndex { get; set; } = -1;
        public short RestingValue { get; set; } = 0;
        public InputBinding DigitalBinding { get; set; } = new(InputType.None, -1);

        public TriggerBinding() { }

        public TriggerBinding(int buttonIndex)
        {
            IsAxis = false;
            DigitalBinding = new InputBinding(InputType.Button, buttonIndex);
        }

        public TriggerBinding(int axisIndex, short restingValue)
        {
            IsAxis = true;
            AxisIndex = axisIndex;
            RestingValue = restingValue;
        }

        public byte GetValue(IntPtr joystick, int deadzonePercent = 0)
        {
            if (joystick == IntPtr.Zero) return 0;

            byte rawVal = 0;
            if (IsAxis && AxisIndex >= 0)
            {
                short raw = SDL.SDL_JoystickGetAxis(joystick, AxisIndex);

                // Jika istirahat di -32768 (standar trigger analog DirectInput/XInput)
                if (RestingValue < -15000)
                {
                    int scaled = (int)((long)(raw - RestingValue) * 255 / (32767 - RestingValue));
                    rawVal = (byte)Math.Clamp(scaled, 0, 255);
                }
                // Jika istirahat di +32767 (analog terbalik)
                else if (RestingValue > 15000)
                {
                    int scaled = (int)((long)(RestingValue - raw) * 255 / (RestingValue - (-32768)));
                    rawVal = (byte)Math.Clamp(scaled, 0, 255);
                }
                // Jika istirahat di tengah ~0 (hanya bergerak positif saat ditarik)
                else
                {
                    int scaled = (int)((long)Math.Max(0, (int)raw) * 255 / 32767);
                    rawVal = (byte)Math.Clamp(scaled, 0, 255);
                }
            }
            else
            {
                rawVal = DigitalBinding.IsPressed(joystick) ? (byte)255 : (byte)0;
            }

            // Terapkan filter Deadzone Trigger (0 - 90%)
            // Rentang 0 s.d. deadzonePercent dianggap 0 (diam), di atasnya di-rescale mulus ke 255
            if (deadzonePercent > 0 && deadzonePercent < 100)
            {
                int threshold = (255 * deadzonePercent) / 100;
                if (rawVal <= threshold)
                {
                    return 0;
                }

                int rescaled = (rawVal - threshold) * 255 / (255 - threshold);
                return (byte)Math.Clamp(rescaled, 0, 255);
            }

            return rawVal;
        }

        public override string ToString()
        {
            if (IsAxis && AxisIndex >= 0)
            {
                return $"Sumbu Analog (Axis {AxisIndex})";
            }
            return DigitalBinding.ToString();
        }
    }
}
