using System;
using System.IO;
using System.Text.Json;
using System.Collections.Generic;

namespace SleepController
{
    public class IgnoredBlockerRule
    {
        public string Section { get; set; } = "*"; // SYSTEM/DISPLAY/AWAYMODE/EXECUTION or * for any
        public string CallerType { get; set; } = string.Empty; // PROCESS/SERVICE/DRIVER
        public string Name { get; set; } = string.Empty; // e.g., Legacy Kernel Caller or process name
    }

    public class Settings
    {
        public string? PreSleepCommand { get; set; }
        public string? PreSleepArgs { get; set; }
        public string? PostWakeCommand { get; set; }
        public string? PostWakeArgs { get; set; }
        public string? OriginalPowerPlanGuid { get; set; }
        public int? OriginalDcSleepAfter { get; set; } 
        public int? OriginalAcSleepAfter { get; set; } 
        public int IdleMinutes { get; set; } = 10;
        public bool VerboseLog { get; set; } = false;
        public bool PreventSleepDuringRdp { get; set; } = true;
        public bool UseWindowsSleepTimeout { get; set; } = false;
        public List<IgnoredBlockerRule> IgnoredBlockers { get; set; } = new();

        //private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SleepController", "settings.json");
        private static string FilePath => Path.Combine(Path.GetDirectoryName(Environment.ProcessPath), "settings.json");
        public void Save()
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir!);
            var s = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, s);
        }

        public static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var s = File.ReadAllText(FilePath);
                    return JsonSerializer.Deserialize<Settings>(s) ?? new Settings();
                }
            }
            catch { }
            return new Settings();
        }
    }
}
