using System;
using System.Threading;
using System.Threading.Tasks;
using Terminal.Gui;
using ParallelComputingServer.Config;
using ParallelComputingServer.Services;
using ParallelComputingServer.UI;
using ILGPU.Util;

namespace ParallelComputingServer.UI
{
    public partial class UIManager
    {
        private readonly ServerConfig _config;
        private readonly NetworkService _networkService;
        private readonly ComputeService _computeService;
        private readonly EndpointService _endpointService;

        private View _beaconIndicator;
        private View _keepAliveIndicator;
        private DateTime _lastBeaconTime = DateTime.MinValue;
        private DateTime _lastKeepAliveTime = DateTime.MinValue;
        private ColorScheme _beaconColorScheme;
        private ColorScheme _keepAliveColorScheme;
        private LogPanel _logPanel; // New log panel

        public UIManager(
            ServerConfig config,
            NetworkService networkService,
            ComputeService computeService,
            EndpointService endpointService)
        {
            _config = config;
            _networkService = networkService;
            _computeService = computeService;
            _endpointService = endpointService;

            // Subscribe to network service events
            _networkService.BeaconSent += (sender, time) => _lastBeaconTime = time;
            _networkService.KeepAliveReceived += (sender, time) => _lastKeepAliveTime = time;
        }

        public void Run()
        {
            Application.Init();
            BuildUI();
            Application.Run();
        }

        private void BuildUI()
        {
            var top = Application.Top;

            // Create a window that takes up the full screen
            var win = new Window("CTS - Parallel Computing Client Server (CTS/PCCS)")
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill()
            };
            top.Add(win);

            // Create status bar with menu options (add F6 for logs)
            var items = new List<StatusItem> {
    new StatusItem(Key.F1,  "~F1~ Help",     ShowHelp),
    new StatusItem(Key.F2,  "~F2~ Settings", ShowSettings),
    new StatusItem(Key.F3,  "~F3~ Clients",  ShowClients),
    new StatusItem(Key.F4,  "~F4~ Endpoints",ShowEndpoints),
    new StatusItem(Key.F5,  "~F5~ About",    ShowAbout),
    new StatusItem(Key.F6,  "~F6~ Logs",     ShowLogs)
};

            if (_datasetTransferService != null)                       // ← add F7 only if wired
                items.Add(new StatusItem(Key.F7, "~F7~ Transfers", ShowTransfers));

            items.Add(new StatusItem(Key.F10, "~F10~ Quit", () => Application.RequestStop()));

            var statusBar = new StatusBar(items.ToArray());
            top.Add(statusBar);

            // Add the main status panel
            var statusPanel = new StatusPanel(_config, _networkService, _computeService, _endpointService)
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill() - 1 // Leave space for status bar
            };
            win.Add(statusPanel);
            if (_datasetTransferService != null)          
                AddTransferStatusToUI(win);
            // Initialize the log panel (hidden initially)
            _logPanel = new LogPanel();
            TuiLogger.Initialize(_logPanel);

            // Log startup message
            TuiLogger.Log("Server UI initialized");

            // Add indicators to the status bar area
            // First create color schemes
            _beaconColorScheme = new ColorScheme();
            _beaconColorScheme.Normal = Terminal.Gui.Attribute.Make(Color.Red, Color.Black);

            _keepAliveColorScheme = new ColorScheme();
            _keepAliveColorScheme.Normal = Terminal.Gui.Attribute.Make(Color.Green, Color.Black);

            // Label for indicator descriptions
            var indicatorLabel = new Label("B:Beacon  K:KeepAlive  ")
            {
                X = Pos.AnchorEnd(22),
                Y = Pos.AnchorEnd(1),
                ColorScheme = Colors.TopLevel
            };
            top.Add(indicatorLabel);

            // Create indicators
            _beaconIndicator = new Label("●")
            {
                X = Pos.AnchorEnd(1),
                Y = Pos.AnchorEnd(1),
                ColorScheme = _beaconColorScheme,
                Width = 1
            };
            top.Add(_beaconIndicator);

            _keepAliveIndicator = new Label("●")
            {
                X = Pos.AnchorEnd(13),
                Y = Pos.AnchorEnd(1),
                ColorScheme = _keepAliveColorScheme,
                Width = 1
            };
            top.Add(_keepAliveIndicator);

            // Create a timer to update the UI periodically
            Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(2000), (_) =>
            {
                
               
                UpdateActivityIndicators();
                
                return true;
            });
        }

        private void UpdateActivityIndicators()
        {
            // Check if beacon was recently active
            if (DateTime.Now - _lastBeaconTime < TimeSpan.FromSeconds(1))
            {
                _beaconIndicator.Visible = true;
            }
            else
            {
                _beaconIndicator.Visible = !_beaconIndicator.Visible; // Blink when inactive
            }

            // Check if keep-alive was recently active
            if (DateTime.Now - _lastKeepAliveTime < TimeSpan.FromSeconds(1))
            {
                _keepAliveIndicator.Visible = true;
            }
            else
            {
                _keepAliveIndicator.Visible = !_keepAliveIndicator.Visible; // Blink when inactive
            }

            // Request redraw
            _beaconIndicator.SetNeedsDisplay();
            _keepAliveIndicator.SetNeedsDisplay();
        }

        // New method to show the logs panel
        private void ShowLogs()
        {
            try
            {
                // Create a dialog with specific dimensions
                var dialog = new Dialog("Server Logs", 80, 25);

                // Create a help label for keyboard shortcuts
                var helpLabel = new Label("Press A to toggle auto-scroll, C to clear logs")
                {
                    X = 1,
                    Y = 0,
                    Width = Dim.Fill() - 2
                };
                dialog.Add(helpLabel);

                // Make sure _logPanel is initialized before using it
                if (_logPanel == null)
                {
                    _logPanel = new LogPanel();
                    TuiLogger.Initialize(_logPanel);
                    TuiLogger.Log("Log panel initialized");
                }

                // Create a container view to hold the log panel
                var container = new FrameView()
                {
                    X = 0,
                    Y = 1,
                    Width = Dim.Fill(),
                    Height = Dim.Fill() - 3,
                    CanFocus = true
                };
                dialog.Add(container);

                // Set the log panel dimensions
                _logPanel.X = 0;
                _logPanel.Y = 0;
                _logPanel.Width = Dim.Fill();
                _logPanel.Height = Dim.Fill();
                _logPanel.CanFocus = true;

                // Add the log panel to the container
                container.Add(_logPanel);

                // Create a close button
                var closeButton = new Button("Close")
                {
                    X = Pos.Center(),
                    Y = Pos.AnchorEnd(1)
                };
                closeButton.Clicked += () => Application.RequestStop();
                dialog.Add(closeButton);

                // Set the initial focus to the log panel
                container.SetFocus();

                // Show the dialog
                Application.Run(dialog);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error showing logs: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
        }
        private void ShowHelp()
        {
            var dialog = new Dialog("Help", 60, 25); // Increased height

            var helpText = new Label()
            {
                X = 1,
                Y = 1,
                Width = Dim.Fill() - 2,
                Height = 18, // Fixed height
                Text = "Parallel Computing Client Server (CTS/PCCS)\n\n" +
                      "F1: Show this help screen\n" +
                      "F2: Configure server settings\n" +
                      "F3: View connected clients\n" +
                      "F4: View connected computing endpoints\n" +
                      "F5: Show about information\n" +
                      "F6: View server logs\n" + // Added logs option
                      "F10: Exit the application\n\n" +
                      "This server manages a network of compute nodes that can\n" +
                      "perform parallel computations using ILGPU.\n\n" +
                      "The server broadcasts a beacon to help nodes discover it\n" +
                      "and coordinates computation tasks across them."
            };
            dialog.Add(helpText);

            // Fixed position button
            var okButton = new Button(25, 20, "OK");
            okButton.Clicked += () => Application.RequestStop();
            dialog.Add(okButton);

            Application.Run(dialog);
        }

        private void ShowSettings()
        {
            var dialog = new Dialog("Settings", 60, 25); // Increased height

            // Labels with fixed positions
            dialog.Add(new Label(1, 1, "Server Port:"));
            dialog.Add(new Label(1, 4, "Beacon Port:"));
            dialog.Add(new Label(1, 7, "Endpoint Port:"));
            dialog.Add(new Label(1, 10, "Beacon Interval (ms):"));

            // Text fields with fixed positions
            var serverPortField = new TextField(_config.ServerPort.ToString())
            {
                X = 15,
                Y = 1,
                Width = 10
            };
            dialog.Add(serverPortField);

            var beaconPortField = new TextField(_config.BeaconPort.ToString())
            {
                X = 15,
                Y = 4,
                Width = 10
            };
            dialog.Add(beaconPortField);

            var endpointPortField = new TextField(_config.EndpointPort.ToString())
            {
                X = 15,
                Y = 7,
                Width = 10
            };
            dialog.Add(endpointPortField);

            var beaconIntervalField = new TextField(_config.BeaconIntervalMs.ToString())
            {
                X = 23,
                Y = 10,
                Width = 10
            };
            dialog.Add(beaconIntervalField);

            // Button bar with fixed positions
            var buttonBar = new View()
            {
                X = 0,
                Y = 18, // Fixed position for buttons
                Width = Dim.Fill(),
                Height = 1
            };
            dialog.Add(buttonBar);

            var saveButton = new Button(15, 0, "Save");
            buttonBar.Add(saveButton);

            var cancelButton = new Button(35, 0, "Cancel");
            buttonBar.Add(cancelButton);

            saveButton.Clicked += () =>
            {
                try
                {
                    // Parse and validate port numbers
                    int serverPort = int.Parse(serverPortField.Text.ToString());
                    int beaconPort = int.Parse(beaconPortField.Text.ToString());
                    int endpointPort = int.Parse(endpointPortField.Text.ToString());
                    int beaconInterval = int.Parse(beaconIntervalField.Text.ToString());

                    // Validate settings
                    if (serverPort < 1024 || serverPort > 65535 ||
                        beaconPort < 1024 || beaconPort > 65535 ||
                        endpointPort < 1024 || endpointPort > 65535 ||
                        beaconInterval < 100)
                    {
                        MessageBox.ErrorQuery("Invalid Settings",
                            "Port numbers must be between 1024 and 65535.\n" +
                            "Beacon interval must be at least 100ms.", "OK");
                        return;
                    }

                    // Check for duplicate ports
                    if (serverPort == beaconPort || serverPort == endpointPort || beaconPort == endpointPort)
                    {
                        MessageBox.ErrorQuery("Invalid Settings",
                            "All port numbers must be different.", "OK");
                        return;
                    }

                    // Apply new settings
                    _config.ServerPort = serverPort;
                    _config.BeaconPort = beaconPort;
                    _config.EndpointPort = endpointPort;
                    _config.BeaconIntervalMs = beaconInterval;

                    MessageBox.Query("Settings Saved",
                        "Server must be restarted for changes to take effect.", "OK");

                    Application.RequestStop();
                }
                catch (Exception)
                {
                    MessageBox.ErrorQuery("Invalid Input", "Please enter valid numbers.", "OK");
                }
            };

            cancelButton.Clicked += () => Application.RequestStop();

            Application.Run(dialog);
        }

        private void ShowClients()
        {
            var clientsPanel = new ClientsPanel(_networkService);
            Application.Run(clientsPanel);
        }

        private void ShowEndpoints()
        {
            var endpointsPanel = new EndpointsPanel(_endpointService);
            Application.Run(endpointsPanel);
        }

        private void ShowAbout()
        {
            var dialog = new Dialog("About", 60, 20); // Increased height

            // Get version info from assembly
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            string versionStr = version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "1.0.0";

            var aboutText = new Label()
            {
                X = 1,
                Y = 1,
                Width = Dim.Fill() - 2,
                Height = 14, // Fixed height
                Text = "CTS - Parallel Computing Client Server (CTS/PCCS)\n" +
                      $"Version {versionStr}\n\n" +
                      "University of Fribourg\n" +
                      "Geosciences Department\n\n" +
                      "Contact: matteo.mangiagalli@unifr.ch\n" +
                      "© 2025 All Rights Reserved"
            };
            dialog.Add(aboutText);

            // Fixed position button
            var okButton = new Button(25, 16, "OK");
            okButton.Clicked += () => Application.RequestStop();
            dialog.Add(okButton);

            Application.Run(dialog);
        }
    }
}