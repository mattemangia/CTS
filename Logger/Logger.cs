using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace CTSegmenter
{
    public static class Logger
    {
        private static readonly string LogFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log.txt");
        private static readonly object LockObj = new object();
        private static Thread logWindowThread;
        public static bool ShuttingDown { get; set; } = false;

        // Expose the LogWindow instance so that the main form can interact with it.
        public static LogWindow LogWindowInstance { get; private set; }

        static Logger()
        {
            StartLogWindow();
        }

        private static void StartLogWindow()
        {
            logWindowThread = new Thread(() =>
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                LogWindowInstance = new LogWindow();
                Application.Run(LogWindowInstance);
            })
            {
                IsBackground = true
            };
            logWindowThread.Start();
        }

        // A helper method to restart the log window if it's been disposed.
        public static void RestartLogWindow()
        {
            if (LogWindowInstance == null || LogWindowInstance.IsDisposed)
            {
                StartLogWindow();
            }
        }

        public static void Log(string message, int severity)
        {
            // Map severity to log levels
            LogLevel level = LogLevel.Information;

            switch (severity)
            {
                case 0: level = LogLevel.Information; break;
                case 1: level = LogLevel.Warning; break;
                case 2: level = LogLevel.Error; break;
                default: level = LogLevel.Debug; break;
            }

            Log(message, level);
        }

        public static void Log(string message, LogLevel level)
        {
            try
            {
                string logMessage = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} - {message}{Environment.NewLine}";

                // File logging
                lock (LockObj)
                {
                    File.AppendAllText(LogFilePath, logMessage);
                }

                // UI logging, if available
                if (LogWindowInstance != null && LogWindowInstance.IsHandleCreated)
                {
                    LogWindowInstance.BeginInvoke(new Action(() =>
                    {
                        LogWindowInstance.AppendLog(logMessage);
                    }));
                }
            }
            catch (Exception)
            {
                // Swallow logging errors.
            }
        }

        public static void Log(string message)
        {
            try
            {
                string logMessage = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} - {message}{Environment.NewLine}";

                // File logging
                lock (LockObj)
                {
                    File.AppendAllText(LogFilePath, logMessage);
                }

                // UI logging, if available
                if (LogWindowInstance != null && LogWindowInstance.IsHandleCreated)
                {
                    LogWindowInstance.BeginInvoke(new Action(() =>
                    {
                        LogWindowInstance.AppendLog(logMessage);
                    }));
                }
            }
            catch (Exception)
            {
                // Swallow logging errors.
            }
        }
    }
}