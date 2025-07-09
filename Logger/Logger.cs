//Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CTS
{
    public static class Logger
    {
        public static readonly string LogFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CTS", "log.txt");

        private static readonly object LockObj = new object();
        private static Thread logWindowThread;
        private static readonly ConcurrentQueue<LogEntry> _logQueue = new ConcurrentQueue<LogEntry>();
        private static readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private static readonly Task _logProcessorTask;

        public static bool ShuttingDown { get; set; } = false;

        // Expose the LogWindow instance so that the main form can interact with it.
        public static LogWindow LogWindowInstance { get; private set; }

        static Logger()
        {
            // Create log directory if it doesn't exist
            string logDirectory = Path.GetDirectoryName(LogFilePath);
            if (!Directory.Exists(logDirectory))
            {
                Directory.CreateDirectory(logDirectory);
            }

            StartLogWindow();
            _logProcessorTask = Task.Run(ProcessLogQueueAsync);
        }

        // Log system information
        public static void LogSystemInfo()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("===== SYSTEM INFORMATION =====");

                // OS Information
                sb.AppendLine($"OS: {GetOSInfo()}");
                sb.AppendLine($"CLR Version: {Environment.Version}");
                sb.AppendLine($"64-bit OS: {Environment.Is64BitOperatingSystem}");
                sb.AppendLine($"64-bit Process: {Environment.Is64BitProcess}");
                sb.AppendLine($"Processor Count: {Environment.ProcessorCount}");

                // Memory Information
                sb.AppendLine($"System Memory: {GetTotalPhysicalMemory()} MB");

                // CPU Information
                string cpuInfo = GetCpuInfo();
                if (!string.IsNullOrEmpty(cpuInfo))
                {
                    sb.AppendLine($"CPU: {cpuInfo}");
                }

                // Disk Information
                string systemDrive = Path.GetPathRoot(Environment.SystemDirectory);
                DriveInfo drive = new DriveInfo(systemDrive);
                sb.AppendLine($"System Drive: {systemDrive}");
                sb.AppendLine($"System Drive Free Space: {drive.AvailableFreeSpace / (1024 * 1024 * 1024)} GB / {drive.TotalSize / (1024 * 1024 * 1024)} GB");

                // Current Directory
                sb.AppendLine($"Application Directory: {AppDomain.CurrentDomain.BaseDirectory}");

                // Process Information
                using (Process currentProcess = Process.GetCurrentProcess())
                {
                    sb.AppendLine($"Process Memory Usage: {currentProcess.PrivateMemorySize64 / (1024 * 1024)} MB");
                    sb.AppendLine($"Process Started: {currentProcess.StartTime}");
                }

                sb.AppendLine("=============================");

                // Log the system information
                Log(sb.ToString(), LogLevel.Information);
            }
            catch (Exception ex)
            {
                Log($"Failed to gather system information: {ex.Message}", LogLevel.Error);
            }
        }

        private static string GetOSInfo()
        {
            try
            {
                OperatingSystem os = Environment.OSVersion;
                string osName = "";

                // Get OS name based on platform and version
                if (os.Platform == PlatformID.Win32NT)
                {
                    if (os.Version.Major == 10 && os.Version.Build >= 22000)
                        osName = "Windows 11";
                    else if (os.Version.Major == 10)
                        osName = "Windows 10";
                    else if (os.Version.Major == 6 && os.Version.Minor == 3)
                        osName = "Windows 8.1";
                    else if (os.Version.Major == 6 && os.Version.Minor == 2)
                        osName = "Windows 8";
                    else if (os.Version.Major == 6 && os.Version.Minor == 1)
                        osName = "Windows 7";
                    else if (os.Version.Major == 6 && os.Version.Minor == 0)
                        osName = "Windows Vista";
                    else
                        osName = "Windows";
                }
                else
                {
                    osName = os.Platform.ToString();
                }

                return $"{osName} {os.Version} {(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")}";
            }
            catch
            {
                return "Unknown Operating System";
            }
        }

        private static string GetCpuInfo()
        {
            try
            {
                // Try to get CPU info using WMI
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT Name, MaxClockSpeed FROM Win32_Processor"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        string name = obj["Name"] as string;
                        uint clockSpeed = 0;
                        if (obj["MaxClockSpeed"] != null)
                        {
                            clockSpeed = (uint)obj["MaxClockSpeed"];
                        }

                        return $"{name?.Trim()} @ {clockSpeed / 1000.0:F2} GHz";
                    }
                }

                return "Unknown CPU";
            }
            catch
            {
                return null; // Return null to indicate failure
            }
        }

        private static ulong GetTotalPhysicalMemory()
        {
            try
            {
                // Try to get RAM info using WMI
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        if (obj["TotalPhysicalMemory"] != null)
                        {
                            ulong totalMemory = Convert.ToUInt64(obj["TotalPhysicalMemory"]);
                            return totalMemory / (1024 * 1024); // Convert to MB
                        }
                    }
                }

                return 0;
            }
            catch
            {
                return 0;
            }
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
                string formattedMessage = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} - {message}{Environment.NewLine}";

                // Queue the log entry for async processing
                _logQueue.Enqueue(new LogEntry
                {
                    Message = formattedMessage,
                    Level = level
                });
            }
            catch (Exception)
            {
                // Swallow logging errors.
            }
        }

        public static void Log(string message)
        {
            Log(message, LogLevel.Information);
        }

        // Asynchronously process the log queue
        private static async Task ProcessLogQueueAsync()
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    // Process all current items in the queue
                    while (_logQueue.TryDequeue(out LogEntry entry))
                    {
                        // File logging
                        await WriteToFileAsync(entry.Message);

                        // UI logging, if available
                        UpdateLogWindow(entry.Message);
                    }

                    // Wait a short time before checking the queue again
                    await Task.Delay(10, _cancellationTokenSource.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    // Swallow exceptions in the background task
                }
            }

            // Process any remaining items when shutting down
            while (_logQueue.TryDequeue(out LogEntry entry))
            {
                try
                {
                    // Use synchronous file write during shutdown to ensure all logs are saved
                    File.AppendAllText(LogFilePath, entry.Message);
                }
                catch
                {
                    // Swallow exceptions during shutdown
                }
            }
        }

        private static async Task WriteToFileAsync(string message)
        {
            try
            {
                // Implement async file writing manually
                using (var fileStream = new FileStream(LogFilePath, FileMode.Append, FileAccess.Write, FileShare.Read))
                using (var streamWriter = new StreamWriter(fileStream))
                {
                    await streamWriter.WriteAsync(message).ConfigureAwait(false);
                    await streamWriter.FlushAsync().ConfigureAwait(false);
                }
            }
            catch
            {
                // Swallow file writing errors
            }
        }

        private static void UpdateLogWindow(string message)
        {
            try
            {
                if (LogWindowInstance != null && LogWindowInstance.IsHandleCreated)
                {
                    LogWindowInstance.BeginInvoke(new Action(() =>
                    {
                        LogWindowInstance.AppendLog(message);
                    }));
                }
            }
            catch
            {
                // Swallow UI updating errors
            }
        }

        // Call this method when the application is shutting down
        public static void Shutdown()
        {
            ShuttingDown = true;
            _cancellationTokenSource.Cancel();

            try
            {
                // Wait for the log processor to finish (with a timeout)
                _logProcessorTask.Wait(1000);
            }
            catch
            {
                // Ignore exceptions during shutdown
            }
        }

        // Class to hold log entry information
        private class LogEntry
        {
            public string Message { get; set; }
            public LogLevel Level { get; set; }
        }
    }
}