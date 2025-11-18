using System;
using System.Linq;
using System.Windows;

namespace SleepController
{
    public partial class App : System.Windows.Application
    {
        public static bool HideOnStart { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            var args = Environment.GetCommandLineArgs();
            // If /h passed, hide main window. If /v passed, enable verbose logging.
            HideOnStart = args.Any(a => string.Equals(a, "/h", StringComparison.OrdinalIgnoreCase));
            var verbose = args.Any(a => string.Equals(a, "/v", StringComparison.OrdinalIgnoreCase));
            Logger.Verbose = verbose;

            // Record verbose setting in persisted settings
            try
            {
                var s = Settings.Load();
                s.VerboseLog = verbose;
                s.Save();
            }
            catch { }

            var window = new MainWindow();
            if (HideOnStart)
            {
                window.Hide();
            }
            else
            {
                window.Show();
            }
        }
    }
}
