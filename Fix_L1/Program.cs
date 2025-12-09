
using System;
using System.Threading;
using SharpDX.DirectInput;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets.Xbox360;

class Program
{
    // Definisikan range standar untuk sumbu analog DirectInput.
    // Ini memberikan pemetaan yang konsisten dan menghilangkan kalibrasi dinamis yang tidak bisa diandalkan.
    const int AXIS_MIN = 0;
    const int AXIS_MAX = 65535;

    static void Main()
    {
        var di = new DirectInput();
        var joystickGuid = Guid.Empty;

        // Cari Game Controller pertama yang terpasang
        foreach (var d in di.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly))
        {
            joystickGuid = d.InstanceGuid;
            Console.WriteLine($"Controller ditemukan: {d.InstanceName}");
            break;
        }

        if (joystickGuid == Guid.Empty)
        {
            Console.WriteLine("Controller tidak ditemukan. Pastikan controller sudah terpasang.");
            Console.ReadKey();
            return;
        }

        var stick = new Joystick(di, joystickGuid);
        stick.Properties.BufferSize = 128;
        stick.Acquire();

        var client = new ViGEmClient();
        var xbox = client.CreateXbox360Controller();
        xbox.Connect();
        Console.WriteLine("Virtual Xbox 360 Controller terhubung.");

        // Filter untuk tombol L1 (debounce/delay rilis)
        bool filteredL1 = false;
        bool prevRawL1 = false;
        DateTime offStart = DateTime.Now;
        const int OFF_THRESH = 20; // Waktu dalam milidetik sebelum L1 dianggap "off"

        Console.WriteLine("Pemetaan dimulai. Tekan Ctrl+C untuk keluar.");

        while (true)
        {
            var s = stick.GetCurrentState();

            // === PEMETAAN TOMBOL ===

            // Tombol Muka (Face Buttons)
            xbox.SetButtonState(Xbox360Button.A, GetButton(s.Buttons, 1)); // Sesuai permintaan: Tombol 1 -> A
            xbox.SetButtonState(Xbox360Button.B, GetButton(s.Buttons, 2)); // Sesuai permintaan: Tombol 2 -> B
            xbox.SetButtonState(Xbox360Button.X, GetButton(s.Buttons, 0)); // Sesuai permintaan: Tombol 3 -> X
            xbox.SetButtonState(Xbox360Button.Y, GetButton(s.Buttons, 3)); // Sesuai permintaan: Tombol 4 -> Y

            // Tombol Bahu (Shoulder Buttons) - L1/R1
            bool rawL1 = GetButton(s.Buttons, 4);
            bool rawR1 = GetButton(s.Buttons, 5);

            // Terapkan filter L1 (jangan dihapus)
            if (rawL1)
            {
                filteredL1 = true;
                prevRawL1 = true;
            }
            else
            {
                if (prevRawL1)
                {
                    offStart = DateTime.Now;
                    prevRawL1 = false;
                }
                if ((DateTime.Now - offStart).TotalMilliseconds > OFF_THRESH)
                {
                    filteredL1 = false;
                }
            }
            xbox.SetButtonState(Xbox360Button.LeftShoulder, filteredL1);
            xbox.SetButtonState(Xbox360Button.RightShoulder, rawR1);

            // Tombol Tambahan (Start, Back, Guide)
            xbox.SetButtonState(Xbox360Button.Back, GetButton(s.Buttons, 8));  // Select
            xbox.SetButtonState(Xbox360Button.Start, GetButton(s.Buttons, 9));  // Start
            xbox.SetButtonState(Xbox360Button.Guide, GetButton(s.Buttons, 12)); // Tombol Home/PS

            // Tombol Stik Analog (L3/R3)
            xbox.SetButtonState(Xbox360Button.LeftThumb, GetButton(s.Buttons, 10));
            xbox.SetButtonState(Xbox360Button.RightThumb, GetButton(s.Buttons, 11));

            // D-Pad (POV Hat) dengan dukungan diagonal
            int pov = s.PointOfViewControllers.Length > 0 ? s.PointOfViewControllers[0] : -1;
            xbox.SetButtonState(Xbox360Button.Up, pov >= 0 && (pov <= 4500 || pov >= 31500));
            xbox.SetButtonState(Xbox360Button.Right, pov >= 4500 && pov <= 13500);
            xbox.SetButtonState(Xbox360Button.Down, pov >= 13500 && pov <= 22500);
            xbox.SetButtonState(Xbox360Button.Left, pov >= 22500 && pov <= 31500);

            // === PEMETAAN SUMBU ANALOG (AXES) ===

            // Triggers (L2/R2) - Dipetakan dari RotationX dan RotationY untuk kompatibilitas umum
            // Ini membebaskan Z/RotationZ untuk stik kanan.
            byte leftTrigger = NormalizeTrigger(s.RotationX, AXIS_MIN, AXIS_MAX);
            byte rightTrigger = NormalizeTrigger(s.RotationY, AXIS_MIN, AXIS_MAX);
            xbox.SetSliderValue(Xbox360Slider.LeftTrigger, leftTrigger);
            xbox.SetSliderValue(Xbox360Slider.RightTrigger, rightTrigger);

            // Stik Analog Kiri (Left Stick)
            short lx = NormalizeAxis(s.X, AXIS_MIN, AXIS_MAX);
            short ly = InvertAxis(NormalizeAxis(s.Y, AXIS_MIN, AXIS_MAX)); // Sumbu Y dibalik dengan aman
            xbox.SetAxisValue(Xbox360Axis.LeftThumbX, lx);
            xbox.SetAxisValue(Xbox360Axis.LeftThumbY, ly);

            // Stik Analog Kanan (Right Stick) - Dipetakan dari Z dan RotationZ
            short rx = NormalizeAxis(s.Z, AXIS_MIN, AXIS_MAX);
            short ry = InvertAxis(NormalizeAxis(s.RotationZ, AXIS_MIN, AXIS_MAX)); // Sumbu Y dibalik dengan aman
            xbox.SetAxisValue(Xbox360Axis.RightThumbX, rx);
            xbox.SetAxisValue(Xbox360Axis.RightThumbY, ry);

            // Kirim laporan status ke virtual controller
            xbox.SubmitReport();
            
            // Kurangi penggunaan CPU, 4ms (250Hz) lebih dari cukup untuk polling
            Thread.Sleep(4);
        }
    }

    /// <summary>
    /// Membalik nilai sumbu dengan aman, menghindari overflow pada short.MinValue.
    /// </summary>
    static short InvertAxis(short value)
    {
        // Saat menegasi -32768 (short.MinValue), hasilnya adalah 32768, yang di luar jangkauan short.
        // Ini menyebabkan overflow dan kembali menjadi -32768.
        // Kita tangani kasus ini secara eksplisit untuk menghasilkan nilai maksimum (32767).
        if (value == short.MinValue)
        {
            return short.MaxValue;
        }
        return (short)-value;
    }

    /// <summary>
    /// Menormalkan nilai sumbu dari range DirectInput (misal: 0-65535) ke range XInput (-32768 hingga 32767).
    /// </summary>
    static short NormalizeAxis(int value, int min, int max)
    {
        if (max <= min) return 0;
        
        // Konversi nilai ke rentang 0.0 - 1.0
        double normalized = (double)(value - min) / (max - min);
        
        // Skalakan ke rentang short XInput dan pusatkan di 0
        long scaled = (long)(normalized * 65535.0) - 32768;

        // Pastikan nilai berada dalam rentang short
        return (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, scaled));
    }

    /// <summary>
    /// Menormalkan nilai trigger dari range DirectInput (misal: 0-65535) ke range XInput (0-255).
    /// </summary>
    static byte NormalizeTrigger(int value, int min, int max)
    {
        if (max <= min) return 0;

        // Konversi nilai ke rentang 0.0 - 1.0
        double normalized = (double)(value - min) / (max - min);

        // Skalakan ke rentang byte XInput
        double scaled = normalized * 255.0;

        // Pastikan nilai berada dalam rentang byte
        return (byte)Math.Max(byte.MinValue, Math.Min(byte.MaxValue, scaled));
    }

    /// <summary>
    /// Helper untuk mendapatkan status tombol dengan aman dari array.
    /// </summary>
    static bool GetButton(bool[] buttons, int index)
    {
        return buttons != null && buttons.Length > index && buttons[index];
    }
}
