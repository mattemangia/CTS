using System;
using System.Collections.Generic;
using Terminal.Gui;

namespace ParallelComputingServer.UI
{
    public class LogPanel : FrameView
    {
        private readonly List<string> _logEntries = new List<string>();
        private ListView _listView;
        private readonly int _maxEntries = 1000;
        private bool _autoScroll = true;

        public LogPanel() : base("Server Logs")
        {
            // Initialize the listview with the log entries
            _listView = new ListView(_logEntries)
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill(),
                AllowsMarking = false,
                CanFocus = true
            };

            Add(_listView);

            // Add a key handler to toggle auto-scroll and clear logs
            _listView.KeyPress += (e) => {
                if (e.KeyEvent.Key == Key.A)
                {
                    _autoScroll = !_autoScroll;
                    AddLog($"Auto-scroll {(_autoScroll ? "enabled" : "disabled")}");
                    e.Handled = true;
                }
                else if (e.KeyEvent.Key == Key.C)
                {
                    ClearLogs();
                    e.Handled = true;
                }
            };
        }

        public void AddLog(string message)
        {
            if (message == null) return;

            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            string logEntry = $"[{timestamp}] {message}";

            Application.MainLoop.Invoke(() => {
                _logEntries.Add(logEntry);

                // Trim log if it gets too large
                while (_logEntries.Count > _maxEntries)
                {
                    _logEntries.RemoveAt(0);
                }

                _listView.SetNeedsDisplay();

                // Auto-scroll to bottom
                if (_autoScroll && _listView != null)
                {
                    _listView.SelectedItem = Math.Max(0, _logEntries.Count - 1);
                }
            });
        }

        public void ClearLogs()
        {
            Application.MainLoop.Invoke(() => {
                _logEntries.Clear();
                _listView.SetNeedsDisplay();
            });
        }
    }
    public class TuiLogger
    {
        private static LogPanel _logPanel;
        private static TextWriter _originalConsoleOut;

        public static void Initialize(LogPanel logPanel)
        {
            _logPanel = logPanel;
            _originalConsoleOut = Console.Out;

            // Redirect console output
            Console.SetOut(new LogTextWriter(_originalConsoleOut, _logPanel));
        }

        public static void Log(string message)
        {
            Console.WriteLine(message);
        }

        // Custom TextWriter that writes to both console and log panel
        private class LogTextWriter : TextWriter
        {
            private readonly TextWriter _originalWriter;
            private readonly LogPanel _logPanel;

            public LogTextWriter(TextWriter originalWriter, LogPanel logPanel)
            {
                _originalWriter = originalWriter;
                _logPanel = logPanel;
            }

            public override void WriteLine(string value)
            {
                if (_originalWriter != null)
                {
                    _originalWriter.WriteLine(value);
                }

                try
                {
                    if (_logPanel != null && value != null)
                    {
                        _logPanel.AddLog(value);
                    }
                }
                catch (Exception)
                {
                    // Ignore exceptions from the log panel
                }
            }

            public override void Write(string value)
            {
                if (_originalWriter != null)
                {
                    _originalWriter.Write(value);
                }
                // Don't add to log panel until we get a full line
            }

            public override System.Text.Encoding Encoding => _originalWriter?.Encoding ?? System.Text.Encoding.UTF8;
        }
    }
}