using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SleepController
{
    public class PowerManager
    {
        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

        private bool _suspended;

        public PowerManager()
        {
            SystemEventsHelper.SystemResume += OnSystemResume;
        }
        public (int? Dc,int? Ac) GetSleepAfterValue()
        {
            var settings = Settings.Load();
            try
            {
                var dc = 0;
                var ac = 0;
                var psi = new ProcessStartInfo("powercfg", $"/q {settings.OriginalPowerPlanGuid}")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                };
                using var p = Process.Start(psi);
                var output = p?.StandardOutput.ReadToEnd() ?? string.Empty;
                p?.WaitForExit(2000);

                // 1) Find the "Sleep after" block by GUID Alias: STANDBYIDLE
                var sleepAfterBlockRegex = new Regex(
                    @"GUID Alias:\s*STANDBYIDLE.*?(?=Power Setting GUID:|Subgroup GUID:|$)",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);

                var blockMatch = sleepAfterBlockRegex.Match(output);
                if (!blockMatch.Success)
                {
                    Console.WriteLine("Sleep after block not found.");
                    return (null,null);
                }

                string block = blockMatch.Value;

                // 2) Extract AC/DC indices
                var acRegex = new Regex(@"Current\s+AC\s+Power\s+Setting\s+Index:\s*0x([0-9a-fA-F]+)");
                var dcRegex = new Regex(@"Current\s+DC\s+Power\s+Setting\s+Index:\s*0x([0-9a-fA-F]+)");

                var acMatch = acRegex.Match(block);
                var dcMatch = dcRegex.Match(block);

                if (acMatch.Success)
                {
                    ac = int.Parse(acMatch.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)/60;
                }

                if (dcMatch.Success)
                {
                    dc = int.Parse(dcMatch.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)/60;
                }

                return (dc, ac);
            }
            catch {
                return (null,null);
            }
        }
        public string? GetCurrentPowerPlanGuid()
        {
            try
            {
                var psi = new ProcessStartInfo("powercfg", "/getactivescheme")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                };
                using var p = Process.Start(psi);
                var outp = p?.StandardOutput.ReadToEnd() ?? string.Empty;
                p?.WaitForExit(2000);
                // Output typically contains GUID in parentheses: Power Scheme GUID: xxxx-xxxx (PlanName)
                var idx = outp.IndexOf(':');
                if (idx >= 0)
                {
                    var part = outp.Substring(idx + 1).Trim();
                    var firstSpace = part.IndexOf(' ');
                    if (firstSpace > 0)
                    {
                        var guid = part.Substring(0, firstSpace).Trim();
                        return guid;
                    }
                }
            }
            catch { }
            return null;
        }

        public void SetPowerPlanGuid(string guid)
        {
            if (string.IsNullOrWhiteSpace(guid)) return;
            RunPowerCfg($"/setactive {guid}");
        }

        /// <summary>
        /// Attempts to disable Windows automatic sleep using powercfg. May require elevation.
        /// </summary>
        public void ChangeSystemAutoSleep(int? acval=null, int? dcval=null)
        {
            try
            {
                if(acval.HasValue)
                    RunPowerCfg($"/change standby-timeout-ac {acval}");
                if (dcval.HasValue)
                    RunPowerCfg($"/change standby-timeout-dc {dcval}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("DisableSystemAutoSleep failed: " + ex.Message);
            }
        }

        private void RunPowerCfg(string args)
        {
            try
            {
                var psi = new ProcessStartInfo("powercfg", args)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(5000);
            }
            catch { }
        }

        public void Suspend()
        {
            try
            {
                _suspended = true;
                // Try SetSuspendState. Note: may require privileges.
                SetSuspendState(false, true, true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Suspend failed: " + ex.Message);
            }
        }

        private void OnSystemResume(object? sender, EventArgs e)
        {
            if (!_suspended) return;
            _suspended = false;
            // run post wake command from settings
            var settings = Settings.Load();
            var post = settings.PostWakeCommand;
            if (!string.IsNullOrWhiteSpace(post))
            {
                try { Process.Start(new ProcessStartInfo(post) { UseShellExecute = true }); }
                catch { }
            }
        }
    }
}
