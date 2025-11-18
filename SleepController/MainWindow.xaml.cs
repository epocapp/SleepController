using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Forms;
using System.Runtime;

namespace SleepController
{
    public partial class MainWindow : Window
    {
        private  IdleMonitor _idle;
        private readonly PowerManager _power;
        private readonly NotifyIcon _tray;
        private readonly Settings _settings;
        private readonly DispatcherTimer _uiTimer;
        private readonly DispatcherTimer _overridesTimer;
        private int? _osTimeoutSec;

        public MainWindow()
        {
            InitializeComponent();

            _settings = Settings.Load();
            PreCmdText.Text = _settings.PreSleepCommand ?? string.Empty;
            PreArgsText.Text = _settings.PreSleepArgs ?? string.Empty;
            PostCmdText.Text = _settings.PostWakeCommand ?? string.Empty;
            PostArgsText.Text = _settings.PostWakeArgs ?? string.Empty;
            IdleMinutesText.Text = _settings.IdleMinutes.ToString();
            PreventRdpChk.IsChecked = _settings.PreventSleepDuringRdp;
            UseWindowsTimeoutChk.IsChecked = _settings.UseWindowsSleepTimeout;
            Logger.Verbose = _settings.VerboseLog;
            Logger.Log("Application starting", forceVerbose: false);

            //_idle.IdleReached -= OnIdleReached;
            _idle = new IdleMonitor(_settings.IdleMinutes);
            _idle.RespectRdpSessions = _settings.PreventSleepDuringRdp;
            _idle.IgnoredBlockers = _settings.IgnoredBlockers;
            _idle.IdleReached += OnIdleReached;
            SystemEventsHelper.SystemResume += OnSystemResume;

            _power = new PowerManager();

            // Save current power plan GUID if missing
            try
            {
               // if (string.IsNullOrWhiteSpace(_settings.OriginalPowerPlanGuid))
               // {
                    var cur = _power.GetCurrentPowerPlanGuid();
                    if (!string.IsNullOrWhiteSpace(cur))
                    {
                        _settings.OriginalPowerPlanGuid = cur;
                    }
                //}
                //if (!_settings.OriginalDcSleepAfter.HasValue || !_settings.OriginalAcSleepAfter.HasValue)
                //{
                    var curval = _power.GetSleepAfterValue();
                    _settings.OriginalDcSleepAfter = curval.Dc;
                    _settings.OriginalAcSleepAfter = curval.Ac;
                    _settings.Save();
        //        }
            }
            catch { }

            // Attempt to disable automatic sleep (best-effort)
            try
            {
                _power.ChangeSystemAutoSleep(0,0);
                Logger.Log("Requested system auto-sleep disable via powercfg", forceVerbose: true);
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to disable auto-sleep: {ex.Message}", forceVerbose: true);
            }

            // Create tray icon (only one) and menu
            _tray = new NotifyIcon();
            _tray.Icon = BuildTrayIcon();
            _tray.Text = "Sleep Controller";
            _tray.Visible = true;
            var menu = new ContextMenuStrip();
            menu.Items.Add("Show", null, (s, e) => Dispatcher.Invoke(ShowWindow));
            menu.Items.Add("Reload", null, ReloadMenu_Click);
            menu.Items.Add("Exit", null, (s, e) => Dispatcher.Invoke(ExitApp));
            _tray.ContextMenuStrip = menu;

            // Start monitoring using configured idle minutes
            var idleMins = Math.Max(1, _settings.IdleMinutes);
            _idle.Start(TimeSpan.FromMinutes(idleMins));
            Logger.Log($"Idle monitor started with threshold {idleMins} minutes", forceVerbose: true);

            // If using Windows sleep timeout, load and apply asynchronously
            _ = InitOsTimeoutIfEnabled();

            // UI timer to update idle progress bar
            _uiTimer = new DispatcherTimer();
            _uiTimer.Interval = TimeSpan.FromMilliseconds(500);
            _uiTimer.Tick += UiTimer_Tick;
            _uiTimer.Start();

            // Periodically refresh Windows overrides display (non-blocking)
            _overridesTimer = new DispatcherTimer();
            _overridesTimer.Interval = TimeSpan.FromSeconds(30);
            _overridesTimer.Tick += OverridesTimer_Tick;
            _overridesTimer.Start();
            // Kick an initial async load
            _ = UpdateOverridesStatusAsync();

            // If App requested to show, it's already shown by App.OnStartup. If hiding requested, ensure hidden state
            if (App.HideOnStart)
            {
                this.Hide();
                this.ShowInTaskbar = false;
            }
        }

        private void ShowWindow()
        {
            this.Show();
            this.WindowState = WindowState.Normal;
            this.ShowInTaskbar = true;
            this.Activate();
        }

        private void ExitApp()
        {
            // Restore original power plan if saved
            try
            {
                //if (!string.IsNullOrWhiteSpace(_settings.OriginalPowerPlanGuid))
                //{
                //    _power.SetPowerPlanGuid(_settings.OriginalPowerPlanGuid);
                //}
                if (_settings.OriginalAcSleepAfter.HasValue)
                {
                    _power.ChangeSystemAutoSleep(acval:_settings.OriginalAcSleepAfter);
                }
                if (_settings.OriginalDcSleepAfter.HasValue)
                {
                    _power.ChangeSystemAutoSleep(dcval: _settings.OriginalDcSleepAfter);
                }
            }
            catch { }

            Logger.Log("Exiting application", forceVerbose: true);
            _tray.Visible = false;
            _idle.Dispose();
            System.Windows.Application.Current.Shutdown();
        }

        private async void OnIdleReached(object? sender, EventArgs e)
        {
            // Unsubscribe temporarily to avoid multiple triggers
            _idle.IdleReached -= OnIdleReached;
            
            // run pre-sleep command if specified
            string? pre = _settings.PreSleepCommand;
            if (!string.IsNullOrWhiteSpace(pre) && File.Exists(pre))
            {
                Logger.Log($"Running pre-sleep command: {pre} {_settings.PreSleepArgs}", forceVerbose: true);
                var res = await CommandRunner.RunAsync(pre, _settings.PreSleepArgs);
                Logger.Log($"Pre-sleep finished. Success={res.Success}, Out={res.StdOut}, Err={res.StdErr}", forceVerbose: true);
            }
            _idle.Reset();
            // attempt to put system to sleep
            Logger.Log("Requesting system suspend", forceVerbose: true);
            _power.Suspend();
        }

        private async void OnSystemResume(object? sender, EventArgs e)
        {
            // run post-wake command
            var post = _settings.PostWakeCommand;
            if (!string.IsNullOrWhiteSpace(post) && File.Exists(post))
            {
                Logger.Log($"Running post-wake command: {post} {_settings.PostWakeArgs}", forceVerbose: true);
                var res = await CommandRunner.RunAsync(post, _settings.PostWakeArgs,waitForExit: false);
                Logger.Log($"Post-wake finished. Success={res.Success}, Out={res.StdOut}, Err={res.StdErr}", forceVerbose: true);
            }

            // re-arm idle monitoring
            _idle.Dispose();
            _idle = new IdleMonitor(_settings.IdleMinutes);
            _idle.RespectRdpSessions = _settings.PreventSleepDuringRdp;
            _idle.IgnoredBlockers = _settings.IgnoredBlockers;
            _idle.Reset();
            _idle.IdleReached += OnIdleReached;
            Logger.Log("System resume handled, idle monitoring re-armed", forceVerbose: true);
        }

        private void PreBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog();
            if (dlg.ShowDialog() == true)
            {
                PreCmdText.Text = dlg.FileName;
            }
        }

        private void PostBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog();
            if (dlg.ShowDialog() == true)
            {
                PostCmdText.Text = dlg.FileName;
            }
        }

        private async void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            _settings.PreSleepCommand = PreCmdText.Text.Trim();
            _settings.PreSleepArgs = PreArgsText.Text.Trim();
            _settings.PostWakeCommand = PostCmdText.Text.Trim();
            _settings.PostWakeArgs = PostArgsText.Text.Trim();
            if (int.TryParse(IdleMinutesText.Text.Trim(), out var im)) _settings.IdleMinutes = Math.Max(1, im);
            _settings.PreventSleepDuringRdp = PreventRdpChk.IsChecked == true;
            _settings.UseWindowsSleepTimeout = UseWindowsTimeoutChk.IsChecked == true;
            _settings.VerboseLog = Logger.Verbose;
            _settings.Save();
            Logger.Log("Settings saved", forceVerbose: true);

            // Apply RDP policy immediately
            _idle.RespectRdpSessions = _settings.PreventSleepDuringRdp;
            _idle.IgnoredBlockers = _settings.IgnoredBlockers;

            // Apply timeout policy
            if (_settings.UseWindowsSleepTimeout)
            {
                await RefreshOsTimeoutAsync();
                if (_osTimeoutSec.HasValue)
                {
                    _idle.Start(TimeSpan.FromSeconds(_osTimeoutSec.Value));
                    Logger.Log($"Idle monitor updated to Windows timeout {_osTimeoutSec.Value} seconds", forceVerbose: true);
                }
            }
            else
            {
                var mins = Math.Max(1, _settings.IdleMinutes);
                _idle.Start(TimeSpan.FromMinutes(mins));
                Logger.Log($"Idle monitor updated to {_settings.IdleMinutes} minutes", forceVerbose: true);
            }
            System.Windows.MessageBox.Show("Settings saved.", "Sleep Controller", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ShowLogBtn_Click(object sender, RoutedEventArgs e)
        {
            var w = new LogWindow();
            w.Owner = this;
            w.Show();
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
        {
            this.Hide();
            this.ShowInTaskbar = false;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // minimize to tray instead of exit
            e.Cancel = true;
            this.Hide();
            this.ShowInTaskbar = false;
        }

        private System.Drawing.Icon BuildTrayIcon()
        {
            // Draw a simple 16x16 icon programmatically
            var bmp = new System.Drawing.Bitmap(16, 16);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.Clear(System.Drawing.Color.Transparent);
                using var b = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0, 120, 215));
                g.FillEllipse(b, 1, 1, 14, 14);
                using var p = new System.Drawing.Pen(System.Drawing.Color.White, 2);
                g.DrawLine(p, 4, 8, 12, 8);
            }
            return System.Drawing.Icon.FromHandle(bmp.GetHicon());
        }

        private void UiTimer_Tick(object? sender, EventArgs e)
        {
            try
            {
                var maxSec = _settings.UseWindowsSleepTimeout && _osTimeoutSec.HasValue
                    ? Math.Max(1, _osTimeoutSec.Value)
                    : Math.Max(1, _settings.IdleMinutes * 60);
                // Update blocker status using cached value (non-blocking for UI)
                var (hasBlockers, summary) = _idle.GetBlockerStatus(refresh: true);
                IdleProgress.Maximum = maxSec;
                double progress = 0;
                if (!hasBlockers)
                {
                    var idleMs = IdleMonitor.GetIdleMilliseconds();
                    var idleSec = idleMs / 1000.0;
                    Logger.Log($"idle for {idleSec}/{maxSec}", forceVerbose: true);
                    progress = Math.Min(maxSec, idleSec);
                }
                else
                {
                    // When blocked, indicate no idle progress
                    progress = 0;
                }
                IdleProgress.Value = progress;
                if (hasBlockers)
                {
                    BlockerStatusText.Text = string.IsNullOrWhiteSpace(summary) ?
                        "Sleep blockers: active" : $"Sleep blockers: {summary}";
                    BlockerStatusText.Foreground = System.Windows.Media.Brushes.OrangeRed;
                    BlockerStatusText.ToolTip = summary;
                }
                else
                {
                    BlockerStatusText.Text = "Sleep blockers: none";
                    BlockerStatusText.Foreground = System.Windows.Media.Brushes.Green;
                    BlockerStatusText.ToolTip = null;
                }
            }
            catch { }
        }

        private async void OverridesTimer_Tick(object? sender, EventArgs e)
        {
            await UpdateOverridesStatusAsync();
            await UpdateBlockersListAsync();
            if (_settings.UseWindowsSleepTimeout)
            {
                await RefreshOsTimeoutAsync();
            }
        }

        private async Task UpdateOverridesStatusAsync()
        {
            try
            {
                var summary = await IdleMonitor.GetOverridesSummaryAsync();
                if (string.IsNullOrWhiteSpace(summary))
                {
                    OverridesStatusText.Text = "Windows overrides: none";
                    OverridesStatusText.Foreground = System.Windows.Media.Brushes.Gray;
                    OverridesStatusText.ToolTip = null;
                }
                else
                {
                    OverridesStatusText.Text = "Windows overrides: " + (summary.Length > 200 ? summary.Substring(0, 200) + "..." : summary);
                    OverridesStatusText.Foreground = System.Windows.Media.Brushes.SteelBlue;
                    OverridesStatusText.ToolTip = summary;
                }
            }
            catch
            {
                // ignore UI update errors
            }
        }

     

        private async Task UpdateBlockersListAsync()
        {
            try
            {
                var blockers = await IdleMonitor.GetCurrentBlockersAsync(includeRdp: _settings.PreventSleepDuringRdp);
                // Show only items that can be overridden (skip synthetic RDP entries)
                var view = blockers.Where(b => !string.Equals(b.Name, "RDP Session", StringComparison.OrdinalIgnoreCase))
                                   .Select(b =>
                                   {
                                       bool ignored = _settings.IgnoredBlockers.Any(r =>
                                           string.Equals(r.CallerType, b.CallerType, StringComparison.OrdinalIgnoreCase) &&
                                           string.Equals(r.Name, b.Name, StringComparison.OrdinalIgnoreCase) &&
                                           (r.Section == "*" || string.Equals(r.Section, b.Section, StringComparison.OrdinalIgnoreCase)));
                                       return new BlockerView
                                       {
                                           Section = b.Section,
                                           CallerType = b.CallerType,
                                           Name = b.Name,
                                           Line = b.Line,
                                           IsIgnored = ignored,
                                           State = ignored ? "Ignored" : "Active"
                                       };
                                   })
                                   .ToList();
                BlockersList.ItemsSource = view;
            }
            catch { }
        }

        private async void RefreshBlockersBtn_Click(object sender, RoutedEventArgs e)
        {
            await UpdateBlockersListAsync();
        }

        private async Task RefreshOsTimeoutAsync()
        {
            try
            {
                _osTimeoutSec = await IdleMonitor.GetWindowsSleepTimeoutSecondsAsync();
                if (_settings.UseWindowsSleepTimeout)
                {
                    IdleMinutesText.IsEnabled = false;
                    if (_osTimeoutSec.HasValue)
                    {
                        var mins = _osTimeoutSec.Value / 60.0;
                        OsTimeoutInfo.Text = $"{mins:0.#} min";
                    }
                    else
                    {
                        OsTimeoutInfo.Text = "Never";
                    }
                }
                else
                {
                    IdleMinutesText.IsEnabled = true;
                    OsTimeoutInfo.Text = string.Empty;
                }
            }
            catch
            {
                // ignore UI errors
            }
        }

        private async Task InitOsTimeoutIfEnabled()
        {
            if (_settings.UseWindowsSleepTimeout)
            {
                await RefreshOsTimeoutAsync();
                if (_osTimeoutSec.HasValue)
                {
                    _idle.Start(TimeSpan.FromSeconds(_osTimeoutSec.Value));
                    Logger.Log($"Idle monitor switched to Windows sleep timeout {_osTimeoutSec.Value} seconds", forceVerbose: true);
                }
            }
            else
            {
                IdleMinutesText.IsEnabled = true;
                OsTimeoutInfo.Text = string.Empty;
            }
        }

        private async void OverrideSelectedBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var items = BlockersList.SelectedItems.Cast<BlockerView>().ToList();
                foreach (var it in items)
                {
                    // Add to app-level ignore list
                    var rule = new IgnoredBlockerRule
                    {
                        Section = it.Section,
                        CallerType = it.CallerType,
                        Name = it.Name
                    };
                    if (!_settings.IgnoredBlockers.Any(r =>
                        string.Equals(r.CallerType, rule.CallerType, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(r.Name, rule.Name, StringComparison.OrdinalIgnoreCase) &&
                        (string.Equals(r.Section, rule.Section, StringComparison.OrdinalIgnoreCase) || r.Section == "*")))
                    {
                        _settings.IgnoredBlockers.Add(rule);
                    }
                }
                _settings.Save();
                _idle.IgnoredBlockers = _settings.IgnoredBlockers;
                await UpdateBlockersListAsync();
            }
            catch (Exception ex)
            {
                Logger.Log("Ignore failed: " + ex.Message, forceVerbose: true);
            }
        }

        private async void RemoveOverrideBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var items = BlockersList.SelectedItems.Cast<BlockerView>().ToList();
                foreach (var it in items)
                {
                    // Remove from app-level ignore list (match by caller+name; section match or wildcard)
                    _settings.IgnoredBlockers.RemoveAll(r =>
                        string.Equals(r.CallerType, it.CallerType, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(r.Name, it.Name, StringComparison.OrdinalIgnoreCase) &&
                        (r.Section == "*" || string.Equals(r.Section, it.Section, StringComparison.OrdinalIgnoreCase))
                    );
                }
                _settings.Save();
                _idle.IgnoredBlockers = _settings.IgnoredBlockers;
                await UpdateBlockersListAsync();
            }
            catch (Exception ex)
            {
                Logger.Log("Remove ignore failed: " + ex.Message, forceVerbose: true);
            }
        }

        private sealed class BlockerView
        {
            public string Section { get; set; } = string.Empty;
            public string CallerType { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public string Line { get; set; } = string.Empty;
            public bool IsIgnored { get; set; }
            public string State { get; set; } = string.Empty;
        }

        private async void ReloadMenu_Click(object? sender, EventArgs e)
        {
            try
            {
                // Run Pre-sleep command (if available)
                string? pre = _settings.PreSleepCommand;
                if (!string.IsNullOrWhiteSpace(pre) && File.Exists(pre))
                {
                    Logger.Log($"Reload: running pre-sleep command: {pre} {_settings.PreSleepArgs}", forceVerbose: true);
                    var resPre = await CommandRunner.RunAsync(pre, _settings.PreSleepArgs);
                    Logger.Log($"Reload: pre-sleep finished. Success={resPre.Success}, Out={resPre.StdOut}, Err={resPre.StdErr}", forceVerbose: true);
                }

                // Then run Post-wake command (if available)
                string? post = _settings.PostWakeCommand;
                if (!string.IsNullOrWhiteSpace(post) && File.Exists(post))
                {
                    Logger.Log($"Reload: running post-wake command: {post} {_settings.PostWakeArgs}", forceVerbose: true);
                    var resPost = await CommandRunner.RunAsync(post, _settings.PostWakeArgs, waitForExit: false);
                    Logger.Log($"Reload: post-wake invoked. Success={resPost.Success}, Out={resPost.StdOut}, Err={resPost.StdErr}", forceVerbose: true);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Reload: exception {ex.Message}", forceVerbose: true);
            }
        }
    }
}
