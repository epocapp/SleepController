using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace SleepController
{
    public static class CommandRunner
    {
        public static async Task<CommandResult> RunAsync(string file, string? args = null, int timeoutMs = 30000, bool waitForExit = true)
        {
            var psi = new ProcessStartInfo(file)
            {
                Arguments = args ?? string.Empty,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(file) ?? Environment.CurrentDirectory

            };

            // If caller doesn't want to wait for exit (detached GUI app), start with UseShellExecute=true so the app runs normally with its own UI/tray.
            if (!waitForExit)
            {
                try
                {
                    var detached = new ProcessStartInfo(file)
                    {
                        Arguments = args ?? string.Empty,
                        UseShellExecute = true,
                        CreateNoWindow = false,
                        LoadUserProfile=true,
                        WorkingDirectory = Path.GetDirectoryName(file) ?? Environment.CurrentDirectory
                    };
                    Process.Start(detached);
                    return new CommandResult(true, "Started detached (no output captured)", string.Empty);
                }
                catch (Exception ex)
                {
                    return new CommandResult(false, string.Empty, "Failed to start detached: " + ex.Message);
                }
            }

            try
            {
                using var p = new Process();
                p.StartInfo = psi;
                p.Start();

                var tOut = p.StandardOutput.ReadToEndAsync();
                var tErr = p.StandardError.ReadToEndAsync();

                var exited = p.WaitForExit(timeoutMs);
                if (!exited)
                {
                    try { p.Kill(); } catch { }
                    return new CommandResult(false, "Timed out", string.Empty);
                }

                var stdout = await tOut;
                var stderr = await tErr;
                return new CommandResult(true, stdout, stderr);
            }
            catch (System.ComponentModel.Win32Exception wex) when (wex.NativeErrorCode == 740)
            {
                // ERROR_ELEVATION_REQUIRED (740) - attempt to start elevated.
                try
                {
                    var psi2 = new ProcessStartInfo(file)
                    {
                        Arguments = args ?? string.Empty,
                        UseShellExecute = true,
                        Verb = "runas",
                        WorkingDirectory = Path.GetDirectoryName(file) ?? Environment.CurrentDirectory
                    };
                    Process.Start(psi2);
                    // Can't capture output when starting elevated via ShellExecute.
                    return new CommandResult(true, "Started elevated (no output captured)", string.Empty);
                }
                catch (Exception ex2)
                {
                    return new CommandResult(false, string.Empty, "Failed to start elevated: " + ex2.Message);
                }
            }
            catch (Exception ex)
            {
                return new CommandResult(false, string.Empty, ex.Message);
            }
        }
        //public static async Task<CommandResult> RunAsync(string file, string? args = null, int timeoutMs = 30000)
        //{
        //    var psi = new ProcessStartInfo(file)
        //    {
        //        Arguments = args ?? string.Empty,
        //        CreateNoWindow = true,
        //        UseShellExecute = false,
        //        RedirectStandardOutput = true,
        //        RedirectStandardError = true
        //    };

        //    try
        //    {
        //        using var p = new Process();
        //        p.StartInfo = psi;
        //        p.Start();

        //        var tOut = p.StandardOutput.ReadToEndAsync();
        //        var tErr = p.StandardError.ReadToEndAsync();

        //        var exited = p.WaitForExit(timeoutMs);
        //        if (!exited)
        //        {
        //            try { p.Kill(); } catch { }
        //            return new CommandResult(false, "Timed out", string.Empty);
        //        }

        //        var stdout = await tOut;
        //        var stderr = await tErr;
        //        return new CommandResult(true, stdout, stderr);
        //    }
        //    catch (Exception ex)
        //    {
        //        return new CommandResult(false, string.Empty, ex.Message);
        //    }
        //}
    }

    public record CommandResult(bool Success, string StdOut, string StdErr);
}
