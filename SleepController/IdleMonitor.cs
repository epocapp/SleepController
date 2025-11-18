using System;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SleepController
{
    public class IdleMonitor : IDisposable
    {
        public event EventHandler? IdleReached;

        private System.Threading.Timer? _timer;
        private TimeSpan _idleThreshold;
        private bool _disposed;
        private bool _reached = false;
        private readonly TimeSpan _requestsCheckInterval = TimeSpan.FromSeconds(5);
        private record RequestsSnapshot(DateTime TimestampUtc, bool HasBlockers, string Summary);
        private RequestsSnapshot _requestsSnapshot = new RequestsSnapshot(DateTime.MinValue, false, string.Empty);
        private int _refreshing = 0; // 0 = no, 1 = in progress
        public bool RespectRdpSessions { get; set; } = true;
        public IReadOnlyList<IgnoredBlockerRule> IgnoredBlockers { get; set; } = Array.Empty<IgnoredBlockerRule>();
        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }
        public IdleMonitor(int minutes)
        {
            _idleThreshold = TimeSpan.FromSeconds(minutes);
            _reached = false;
        }
        public void Start(TimeSpan idleThreshold)
        {
            _idleThreshold = idleThreshold;
            _timer = new System.Threading.Timer(CheckIdle, null, 1000, 1000);
            SystemEventsHelper.SystemResume += OnSystemResume;
            // We will refresh power requests each idle-check loop (non-blocking)
        }
        public void Reset()
        {
            _reached = false;
        }
        
        /// <summary>
        /// Returns the current cached blocker status and summary. If refresh is true,
        /// forces a re-check (subject to immediate execution timing).
        /// </summary>
        public (bool hasBlockers, string summary) GetBlockerStatus(bool refresh = false)
        {
            if (refresh)
            {
                // Trigger background refresh; return last cached immediately
                _ = EnsureRefreshInBackgroundAsync();
            }
            var snap = System.Threading.Volatile.Read(ref _requestsSnapshot);
            return (snap.HasBlockers, snap.Summary);
        }
        /// <summary>
        /// Returns current idle time in milliseconds using GetLastInputInfo.
        /// </summary>
        public static uint GetIdleMilliseconds()
        {
            var li = new LASTINPUTINFO();
            li.cbSize = (uint)Marshal.SizeOf(li);
            if (!GetLastInputInfo(ref li)) return 0;
            return (uint)Environment.TickCount - li.dwTime;
        }

        private void OnSystemResume(object? sender, EventArgs e)
        {
            // When system resumes, we can restart the timer checks if needed.
            _timer?.Change(1000, 1000);
        }
     
        private void CheckIdle(object? state)
        {
            // Kick off a background refresh of power requests each loop (non-blocking)
            _ = EnsureRefreshInBackgroundAsync();
            var li = new LASTINPUTINFO();
            li.cbSize = (uint)Marshal.SizeOf(li);
            if (!GetLastInputInfo(ref li)) return;

            uint idleMs = (uint)Environment.TickCount - li.dwTime;
      
            if (idleMs >= _idleThreshold.TotalMilliseconds)
            {
                // Before signaling idle, confirm there are no active system sleep blockers
                // Use cached value (refresh already triggered above to avoid blocking).
                var snap = System.Threading.Volatile.Read(ref _requestsSnapshot);
                var hasBlockers = snap.HasBlockers;
                var summary = snap.Summary;
                if (hasBlockers)
                {
                    // We reached the idle threshold but Windows would not sleep due to blockers.
                    // Reset reached flag so we can trigger once blockers clear.
                    if (!string.IsNullOrWhiteSpace(summary))
                        Logger.Log($"Idle threshold reached but blocked: {summary}");
                    _reached = false;
                    return;
                }

                if (!_reached)
                {
                    _reached = true;
                    IdleReached?.Invoke(this, EventArgs.Empty);
                }
             
            }
        }

        /// <summary>
        /// Runs 'powercfg /requests' and caches the result to
        /// check for active Windows sleep blockers (SYSTEM/AWAYMODE/EXECUTION).
        /// Respects 'powercfg /requestsoverride' entries to align with Windows behavior.
        /// </summary>
        private async Task RefreshPowerRequestsAsync()
        {
            try
            {
                // Use local CommandRunner in this project to execute powercfg
                var result = await CommandRunner.RunAsync("powercfg", "/requests", timeoutMs: 5000, waitForExit: true);
                if (!result.Success)
                {
                    var summary = string.IsNullOrWhiteSpace(result.StdErr) ? "Unable to query power requests" : result.StdErr.Trim();
                    var snap = new RequestsSnapshot(DateTime.UtcNow, true, summary);
                    System.Threading.Interlocked.Exchange(ref _requestsSnapshot, snap);
                    return;
                }

                // Get current overrides and entries, filter overridden items out
                var overrides = await GetRequestOverridesAsync();
                var entries = ParseRequestsEntries(result.StdOut);
                var filtered = entries.Where(e => !IsOverridden(e, overrides)).ToList();
                // Apply app-level ignores
                if (IgnoredBlockers.Count > 0)
                {
                    filtered = filtered.Where(e => !IsIgnored(e, IgnoredBlockers)).ToList();
                }

                bool hasBlockers = filtered.Any();
                string reasons = BuildReasonsSummary(filtered);
                // Also consider active RDP sessions as a sleep blocker (if enabled)
                if (RespectRdpSessions && IsAnyRdpSessionActive())
                {
                    hasBlockers = true;
                    reasons = string.IsNullOrEmpty(reasons) ? "RDP: active session" : reasons + " | RDP: active session";
                }
                if (_requestsSnapshot.Summary!= reasons)
                {
                    Logger.Log(reasons, forceVerbose: true);
                }
              
                var okSnap = new RequestsSnapshot(DateTime.UtcNow, hasBlockers, reasons);
                System.Threading.Interlocked.Exchange(ref _requestsSnapshot, okSnap);
                return;
            }
            catch (Exception ex)
            {
                var errSnap = new RequestsSnapshot(DateTime.UtcNow, true, "Exception reading power requests: " + ex.Message);
                System.Threading.Interlocked.Exchange(ref _requestsSnapshot, errSnap);
                return;
            }
        }

        private Task EnsureRefreshInBackgroundAsync()
        {
            // Only one refresh at a time
            if (System.Threading.Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0)
                return Task.CompletedTask;
            return System.Threading.Tasks.Task.Run(async () =>
            {
                try { await RefreshPowerRequestsAsync(); }
                finally { System.Threading.Interlocked.Exchange(ref _refreshing, 0); }
            });
        }

        // --- RDP detection (WTS API) ---
        private const int WTS_CURRENT_SERVER_HANDLE_VALUE = 0;
        private static readonly IntPtr WTS_CURRENT_SERVER_HANDLE = new IntPtr(WTS_CURRENT_SERVER_HANDLE_VALUE);

        private enum WTS_CONNECTSTATE_CLASS
        {
            WTSActive = 0,
            WTSConnected = 1,
            WTSConnectQuery = 2,
            WTSShadow = 3,
            WTSDisconnected = 4,
            WTSIdle = 5,
            WTSListen = 6,
            WTSReset = 7,
            WTSDown = 8,
            WTSInit = 9
        }

        private enum WTS_INFO_CLASS
        {
            WTSInitialProgram = 0,
            WTSApplicationName = 1,
            WTSWorkingDirectory = 2,
            WTSOEMId = 3,
            WTSSessionId = 4,
            WTSUserName = 5,
            WTSWinStationName = 6,
            WTSDomainName = 7,
            WTSConnectState = 8,
            WTSClientBuildNumber = 9,
            WTSClientName = 10,
            WTSClientDirectory = 11,
            WTSClientProductId = 12,
            WTSClientHardwareId = 13,
            WTSClientAddress = 14,
            WTSClientDisplay = 15,
            WTSClientProtocolType = 16
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WTS_SESSION_INFO
        {
            public int SessionID;
            public IntPtr pWinStationName; // LPWSTR
            public WTS_CONNECTSTATE_CLASS State;
        }

        [DllImport("Wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSEnumerateSessions(
            IntPtr hServer,
            int Reserved,
            int Version,
            out IntPtr ppSessionInfo,
            out int pCount);

        [DllImport("Wtsapi32.dll")] 
        private static extern void WTSFreeMemory(IntPtr pMemory);

        [DllImport("Wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool WTSQuerySessionInformation(
            IntPtr hServer,
            int sessionId,
            WTS_INFO_CLASS wtsInfoClass,
            out IntPtr ppBuffer,
            out int pBytesReturned);

        private static bool IsAnyRdpSessionActive()
        {
            IntPtr ppSessionInfo = IntPtr.Zero;
            try
            {
                if (!WTSEnumerateSessions(WTS_CURRENT_SERVER_HANDLE, 0, 1, out ppSessionInfo, out int count) || count <= 0)
                    return false;

                int dataSize = Marshal.SizeOf(typeof(WTS_SESSION_INFO));
                for (int i = 0; i < count; i++)
                {
                    var ptr = new IntPtr(ppSessionInfo.ToInt64() + i * dataSize);
                    var si = Marshal.PtrToStructure<WTS_SESSION_INFO>(ptr);
                    if (si.State == WTS_CONNECTSTATE_CLASS.WTSActive)
                    {
                        if (TryGetClientProtocol(si.SessionID, out ushort proto) && proto == 2)
                        {
                            return true; // 2 == RDP
                        }
                    }
                }
                return false;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (ppSessionInfo != IntPtr.Zero) WTSFreeMemory(ppSessionInfo);
            }
        }

        private static bool TryGetClientProtocol(int sessionId, out ushort protocol)
        {
            protocol = 0;
            IntPtr buf = IntPtr.Zero;
            try
            {
                if (!WTSQuerySessionInformation(WTS_CURRENT_SERVER_HANDLE, sessionId, WTS_INFO_CLASS.WTSClientProtocolType, out buf, out int bytes) || buf == IntPtr.Zero || bytes < 2)
                    return false;
                protocol = (ushort)Marshal.ReadInt16(buf);
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (buf != IntPtr.Zero) WTSFreeMemory(buf);
            }
        }

        // --- powercfg parsing and overrides ---
        private sealed class RequestOverride
        {
            public string CallerType = string.Empty; // PROCESS | SERVICE | DRIVER
            public string Name = string.Empty;       // e.g., Legacy Kernel Caller
            public HashSet<string> RequestTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // SYSTEM | DISPLAY | AWAYMODE | EXECUTION
        }

        private static async Task<List<RequestOverride>> GetRequestOverridesAsync()
        {
            try
            {
                var res = await CommandRunner.RunAsync("powercfg", "/requestsoverride", timeoutMs: 4000, waitForExit: true);
                if (!res.Success) return new List<RequestOverride>();
                return ParseRequestOverrides(res.StdOut);
            }
            catch
            {
                return new List<RequestOverride>();
            }
        }

        private static List<RequestOverride> ParseRequestOverrides(string stdout)
        {
            var list = new List<RequestOverride>();
            if (string.IsNullOrWhiteSpace(stdout)) return list;
            // The output is grouped by types; lines typically look like:
            // DRIVER\n[DRIVER] Legacy Kernel Caller\nSYSTEM\n...
            // But formats vary; we'll parse leniently: capture blocks of the form
            // <CallerType>\n<Name>\n<RequestType(s)> ...

            string currentType = string.Empty;
            RequestOverride? current = null;
            foreach (var raw in stdout.Split(new[] {"\r\n", "\n"}, StringSplitOptions.None))
            {
                var line = raw.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                // Caller type headers
                if (line.Equals("PROCESS", StringComparison.OrdinalIgnoreCase) ||
                    line.Equals("SERVICE", StringComparison.OrdinalIgnoreCase) ||
                    line.Equals("DRIVER", StringComparison.OrdinalIgnoreCase))
                {
                    currentType = line.ToUpperInvariant();
                    current = null;
                    continue;
                }

                // If looks like a new entry name
                if (current == null)
                {
                    current = new RequestOverride { CallerType = currentType, Name = line };
                    list.Add(current);
                    continue;
                }

                // Otherwise treat as request types line(s)
                foreach (var token in line.Split(new[] { ' ', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var t = token.Trim().TrimEnd('.').ToUpperInvariant();
                    if (t is "SYSTEM" or "DISPLAY" or "AWAYMODE" or "EXECUTION")
                    {
                        current.RequestTypes.Add(t);
                    }
                }
            }

            // Normalize names by removing bracketed prefixes if present
            foreach (var o in list)
            {
                o.Name = NormalizeName(o.Name);
            }
            return list;
        }

        private sealed class RequestEntry
        {
            public string Section = string.Empty;    // SYSTEM | DISPLAY | AWAYMODE | EXECUTION | ...
            public string CallerType = string.Empty; // PROCESS | SERVICE | DRIVER | UNKNOWN
            public string Name = string.Empty;       // e.g., Legacy Kernel Caller or process name
            public string Line = string.Empty;       // original line content
        }

        private static List<RequestEntry> ParseRequestsEntries(string stdout)
        {
            var list = new List<RequestEntry>();
            if (string.IsNullOrWhiteSpace(stdout)) return list;
            string section = string.Empty;
            foreach (var raw in stdout.Split(new[] {"\r\n", "\n"}, StringSplitOptions.None))
            {
                var line = raw.Trim();
                if (string.IsNullOrEmpty(line)) continue;
                if (line.EndsWith(":"))
                {
                    section = line.TrimEnd(':').ToUpperInvariant();
                    continue;
                }
                if (line.Equals("None.", StringComparison.OrdinalIgnoreCase)) continue;
                if (section is "SYSTEM" or "AWAYMODE" or "EXECUTION" or "DISPLAY")
                {
                    var (ctype, name) = ExtractCaller(line);
                    list.Add(new RequestEntry
                    {
                        Section = section,
                        CallerType = ctype,
                        Name = name,
                        Line = line
                    });
                }
            }
            return list;
        }

        private static (string callerType, string name) ExtractCaller(string line)
        {
            // Examples:
            // [DRIVER] Legacy Kernel Caller
            // [PROCESS] someapp.exe
            // myservice (Service) ...
            if (line.StartsWith("[", StringComparison.Ordinal) && line.Contains("]"))
            {
                var end = line.IndexOf(']');
                var tag = line.Substring(1, end - 1).Trim().ToUpperInvariant();
                var rest = line.Substring(end + 1).Trim();
                return (tag, NormalizeName(rest));
            }

            // Service pattern (best-effort)
            var idx = line.IndexOf("(Service)", StringComparison.OrdinalIgnoreCase);
            if (idx > 0)
            {
                var name = line.Substring(0, idx).Trim();
                return ("SERVICE", NormalizeName(name));
            }

            // Fallback unknown
            return ("UNKNOWN", NormalizeName(line));
        }

        private static string NormalizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;
            var n = name.Trim();
            if (n.StartsWith("[", StringComparison.Ordinal) && n.Contains("]"))
            {
                var end = n.IndexOf(']');
                n = n.Substring(end + 1).Trim();
            }
            return n;
        }

        private static bool IsOverridden(RequestEntry e, List<RequestOverride> overrides)
        {
            if (overrides.Count == 0) return false;
            // Map section to request type
            var reqType = e.Section.ToUpperInvariant();
            foreach (var o in overrides)
            {
                if (!o.RequestTypes.Contains(reqType)) continue;
                if (!e.CallerType.Equals(o.CallerType, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(e.Name, o.Name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string BuildReasonsSummary(List<RequestEntry> entries)
        {
            if (entries.Count == 0) return string.Empty;
            var sb = new System.Text.StringBuilder();
            foreach (var e in entries)
            {
                if (sb.Length > 0) sb.Append(" | ");
                var piece = e.Section + ": " + e.Line;
                sb.Append(piece.Length > 200 ? piece.Substring(0, 200) + "..." : piece);
            }
            return sb.ToString();
        }

        private static bool IsIgnored(RequestEntry e, IReadOnlyList<IgnoredBlockerRule> rules)
        {
            foreach (var r in rules)
            {
                if (!string.Equals(e.CallerType, r.CallerType, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(e.Name, r.Name, StringComparison.OrdinalIgnoreCase)) continue;
                if (r.Section == "*" || string.Equals(e.Section, r.Section, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // Public helper to surface Windows overrides to the UI
        public static async Task<string> GetOverridesSummaryAsync()
        {
            try
            {
                var res = await CommandRunner.RunAsync("powercfg", "/requestsoverride", timeoutMs: 4000, waitForExit: true);
                if (!res.Success) return string.Empty;
                var list = ParseRequestOverrides(res.StdOut);
                if (list.Count == 0) return string.Empty;
                var sb = new System.Text.StringBuilder();
                foreach (var o in list)
                {
                    if (sb.Length > 0) sb.Append(" | ");
                    var types = o.RequestTypes.Count == 0 ? "" : (" [" + string.Join(',', o.RequestTypes.OrderBy(x => x)) + "]");
                    sb.Append(o.CallerType).Append(": ").Append(o.Name).Append(types);
                }
                return sb.ToString();
            }
            catch
            {
                return string.Empty;
            }
        }

        // Public helper to get the list of current blockers (after overrides), for UI actions
        public record PowerRequestBlocker(string Section, string CallerType, string Name, string Line);

        public static async Task<List<PowerRequestBlocker>> GetCurrentBlockersAsync(bool includeRdp = true)
        {
            var list = new List<PowerRequestBlocker>();
            try
            {
                var req = await CommandRunner.RunAsync("powercfg", "/requests", timeoutMs: 5000, waitForExit: true);
                if (!req.Success) return list;
                var overrides = await GetRequestOverridesAsync();
                var entries = ParseRequestsEntries(req.StdOut);
                var filtered = entries.Where(e => !IsOverridden(e, overrides)).ToList();
                foreach (var e in filtered)
                {
                    list.Add(new PowerRequestBlocker(e.Section, e.CallerType, e.Name, e.Line));
                }
                // Append RDP as synthetic blocker
                if (includeRdp && IsAnyRdpSessionActive())
                {
                    list.Add(new PowerRequestBlocker("SYSTEM", "PROCESS", "RDP Session", "RDP: active session"));
                }
            }
            catch { }
            return list;
        }

        // Get Windows "Sleep after" timeout (active scheme), returns seconds (AC/DC aware)
        public static async Task<int?> GetWindowsSleepTimeoutSecondsAsync()
        {
            try
            {
                var res = await CommandRunner.RunAsync("powercfg", "/q", timeoutMs: 8000, waitForExit: true);
                if (!res.Success || string.IsNullOrWhiteSpace(res.StdOut)) return null;

                // Identify AC/DC context
                bool onAc = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online;
                string targetLine = onAc ? "Current AC Power Setting Index" : "Current DC Power Setting Index";

                // Find Sleep subgroup and Sleep after setting blocks and extract the target line's hex value
                // Subgroup GUID: 238c9fa8-0aad-41ed-83f4-97be242c8f20 (Sleep)
                // Power Setting GUID: 29f6c1db-86da-48c5-9fdb-f2b67b1f44da (Sleep after)
                var lines = res.StdOut.Split(new[] {"\r\n", "\n"}, StringSplitOptions.None);
                bool inSleepSub = false;
                bool inSleepAfter = false;
                foreach (var raw in lines)
                {
                    var line = raw.Trim();
                    if (line.StartsWith("Subgroup GUID:", StringComparison.OrdinalIgnoreCase))
                    {
                        inSleepSub = line.IndexOf("238c9fa8-0aad-41ed-83f4-97be242c8f20", StringComparison.OrdinalIgnoreCase) >= 0;
                        inSleepAfter = false;
                        continue;
                    }
                    if (inSleepSub && line.StartsWith("Power Setting GUID:", StringComparison.OrdinalIgnoreCase))
                    {
                        inSleepAfter = line.IndexOf("29f6c1db-86da-48c5-9fdb-f2b67b1f44da", StringComparison.OrdinalIgnoreCase) >= 0;
                        continue;
                    }
                    if (inSleepSub && inSleepAfter && line.StartsWith(targetLine, StringComparison.OrdinalIgnoreCase))
                    {
                        // Example: Current AC Power Setting Index: 00000000
                        var idx = line.IndexOf(":", StringComparison.Ordinal);
                        if (idx > 0)
                        {
                            var hex = line.Substring(idx + 1).Trim();
                            if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int seconds))
                            {
                                // Some configs use 0 for Never
                                if (seconds <= 0) return null;
                                return seconds;
                            }
                            // Sometimes decimal appears; try decimal parse
                            if (int.TryParse(hex, out int secDec))
                            {
                                if (secDec <= 0) return null;
                                return secDec;
                            }
                        }
                    }
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _timer?.Dispose();
            SystemEventsHelper.SystemResume -= OnSystemResume;
            _disposed = true;
        }
    }
}
