using System;
using System.Threading;
using Nefarius.ViGEm.Client.Targets;

namespace Fix_L1
{
    public class ControllerRuntime : IDisposable
    {
        public ControllerProfile Profile { get; set; } = null!;
        public IntPtr JoystickHandle { get; set; }
        public int SdlIndex { get; set; }
        public IXbox360Controller Xbox { get; set; } = null!;
        public CancellationTokenSource Cts { get; set; } = null!;
        public Thread? Thread { get; set; }
        public VibrationController? Vibration { get; set; }

        // State untuk Filter Debounce L1 ("Fix_L1")
        public bool FilteredL1;
        public bool PrevRawL1;
        public DateTime OffStart = DateTime.Now;

        public void Dispose()
        {
            try { Cts?.Cancel(); } catch { }
            try { Thread?.Join(500); } catch { }
            try { Vibration?.Dispose(); } catch { }
            try { Xbox?.Disconnect(); } catch { }
        }
    }
}
