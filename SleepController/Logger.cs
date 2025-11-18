using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;

namespace SleepController
{
    public static class Logger
    {
        private static readonly BlockingCollection<string> _queue = new BlockingCollection<string>(new ConcurrentQueue<string>());
        private static readonly StringBuilder _rolling = new StringBuilder();
        private static readonly int _maxRollingChars = 16_000; // about 1000 lines
        private static readonly string _logFilePath;
        private static readonly Thread _worker;
        public static bool Verbose { get; set; }

        static Logger()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SleepController");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            _logFilePath = Path.Combine(dir, "sleepcontroller.log");

            _worker = new Thread(ProcessQueue) { IsBackground = true };
            _worker.Start();
        }

        public static void Log(string message, bool forceVerbose = false)
        {
            if (!Verbose && forceVerbose) return;
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
            _queue.Add(line);
            lock (_rolling)
            {
                _rolling.AppendLine(line);
                if (_rolling.Length > _maxRollingChars)
                {
                    _rolling.Remove(0, _rolling.Length - _maxRollingChars);
                }
            }
        }

        private static void ProcessQueue()
        {
            using var fs = new FileStream(_logFilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var sw = new StreamWriter(fs, Encoding.UTF8) { AutoFlush = true };
            foreach (var item in _queue.GetConsumingEnumerable())
            {
                try
                {
                    sw.WriteLine(item);
                }
                catch { }
            }
        }

        public static string GetRollingLog()
        {
            lock (_rolling)
            {
                return _rolling.ToString();
            }
        }
    }
}
