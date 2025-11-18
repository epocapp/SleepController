using System.Windows;
using System.Windows.Threading;

namespace SleepController
{
    public partial class LogWindow : Window
    {
        private readonly DispatcherTimer _uiTimer;
        public LogWindow()
        {
            InitializeComponent();
            Refresh();
            // UI timer to update idle progress bar
            _uiTimer = new DispatcherTimer();
            _uiTimer.Interval = TimeSpan.FromMilliseconds(500);
            _uiTimer.Tick += UiTimer_Tick;
            _uiTimer.Start();
        }
        private void UiTimer_Tick(object? sender, EventArgs e)
        { 
            Refresh();
            LogText.ScrollToEnd();
        }
        public void Refresh()
        {
            LogText.Text = Logger.GetRollingLog();
        }

        private void RefreshBtn_Click(object sender, RoutedEventArgs e)
        {
            Refresh();
        }

        private void ClearBtn_Click(object sender, RoutedEventArgs e)
        {
            // Clearing in-memory rolling buffer isn't implemented - to keep things simple, overwrite file
            // but keep rolling buffer
            try
            {
                var path = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "SleepController", "sleepcontroller.log");
                if (System.IO.File.Exists(path)) System.IO.File.WriteAllText(path, string.Empty);
            }
            catch { }
            Refresh();
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}
