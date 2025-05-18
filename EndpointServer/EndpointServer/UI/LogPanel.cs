using System;
using System.Collections.Generic;
using Terminal.Gui;

namespace ParallelComputingEndpoint
{
    public class LogPanel : FrameView
    {
        private readonly ListView _listView;
        private readonly List<string> _logMessages = new List<string>();
        private readonly object _logLock = new object();

        public LogPanel() : base("Logs")
        {
            _listView = new ListView()
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill(),
                AllowsMarking = false,
                AllowsMultipleSelection = false
            };

            _listView.SetSource(_logMessages);
            Add(_listView);
        }

        public void AddLog(string message)
        {
            lock (_logLock)
            {
                // Add timestamp to message
                string timestampedMessage = $"[{DateTime.Now.ToString("HH:mm:ss")}] {message}";

                // Add to log list
                _logMessages.Add(timestampedMessage);

                // Keep a reasonable maximum number of log messages
                if (_logMessages.Count > 500)
                {
                    _logMessages.RemoveAt(0);
                }

                // Update listview
                _listView.SetSource(_logMessages);

                // Scroll to the bottom
                _listView.SelectedItem = _logMessages.Count - 1;

                // Ensure UI updates
                _listView.SetNeedsDisplay();
            }
        }

        public void Clear()
        {
            lock (_logLock)
            {
                _logMessages.Clear();
                _listView.SetSource(_logMessages);
                _listView.SetNeedsDisplay();
            }
        }
    }
}