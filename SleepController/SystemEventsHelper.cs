using System;
using Microsoft.Win32;

namespace SleepController
{
    public static class SystemEventsHelper
    {
        public static event EventHandler? SystemResume;

        static SystemEventsHelper()
        {
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
        }

        private static void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume)
            {
                SystemResume?.Invoke(null, EventArgs.Empty);
            }
        }

        public static void RaiseResumeEvent()
        {
            SystemResume?.Invoke(null, EventArgs.Empty);
        }
    }
}
