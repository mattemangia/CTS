using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Terminal.Gui;

namespace ParallelComputingEndpoint
{
    public class UIManager
    {
        private readonly EndpointConfig _config;
        private readonly EndpointNetworkService _networkService;
        private readonly EndpointComputeService _computeService;

        private StatusPanel _statusPanel;
        private LogPanel _logPanel;
        private Label _connectionLabel;
        private ColorScheme _connectedColorScheme;
        private ColorScheme _disconnectedColorScheme;

        public UIManager(
            EndpointConfig config,
            EndpointNetworkService networkService,
            EndpointComputeService computeService)
        {
            _config = config;
            _networkService = networkService;
            _computeService = computeService;

            // Subscribe to network service events
            _networkService.ConnectionStatusChanged += OnConnectionStatusChanged;
            _networkService.MessageReceived += OnMessageReceived;
        }

        public void Run()
        {
            Application.Init();
            BuildUI();

            // Auto-connect if enabled
            if (_config.AutoConnect)
            {
                Task.Run(async () =>
                {
                    await Task.Delay(1000); // Give UI time to initialize
                    await _networkService.ConnectToServerAsync(_config.ServerIP, _config.ServerPort);
                });
            }

            Application.Run();
        }

        private void BuildUI()
        {
            var top = Application.Top;

            // Create a window that takes up the full screen
            var win = new Window("CTS - Parallel Computing Endpoint")
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill()
            };
            top.Add(win);

            // Create status bar with menu options
            var statusBar = new StatusBar(new StatusItem[] {
                new StatusItem(Key.F1, "~F1~ Help", ShowHelp),
                new StatusItem(Key.F2, "~F2~ Settings", ShowSettings),
                new StatusItem(Key.F3, "~F3~ Connect", ShowServerScan),
                new StatusItem(Key.F4, "~F4~ Benchmark", RunBenchmark),
                new StatusItem(Key.F5, "~F5~ About", ShowAbout),
                new StatusItem(Key.F10, "~F10~ Quit", () => Application.RequestStop())
            });
            top.Add(statusBar);

            // Create color schemes for connection status
            _connectedColorScheme = new ColorScheme();
            _connectedColorScheme.Normal = Terminal.Gui.Attribute.Make(Color.Green, Color.Black);

            _disconnectedColorScheme = new ColorScheme();
            _disconnectedColorScheme.Normal = Terminal.Gui.Attribute.Make(Color.Red, Color.Black);

            // Create a layout with top status panel and bottom log panel
            var mainLayout = new FrameView("Endpoint Status")
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill() - 1 // Leave space for status bar
            };
            win.Add(mainLayout);

            // Create connection status indicator
            _connectionLabel = new Label("■ Disconnected")
            {
                X = Pos.Right(mainLayout) - 18,
                Y = 0,
                ColorScheme = _disconnectedColorScheme
            };
            mainLayout.Add(_connectionLabel);

            // Create status panel (top half)
            _statusPanel = new StatusPanel(_config, _networkService, _computeService)
            {
                X = 0,
                Y = 1,
                Width = Dim.Fill(),
                Height = Dim.Percent(50)
            };
            mainLayout.Add(_statusPanel);

            // Create log panel (bottom half)
            _logPanel = new LogPanel()
            {
                X = 0,
                Y = Pos.Bottom(_statusPanel),
                Width = Dim.Fill(),
                Height = Dim.Fill()
            };
            mainLayout.Add(_logPanel);

            // Initialize log with a welcome message
            _logPanel.AddLog("Endpoint initialized. Use F3 to scan for and connect to a server.");

            // If autoconnect is enabled, show a message
            if (_config.AutoConnect)
            {
                _logPanel.AddLog($"Auto-connect enabled. Connecting to {_config.ServerIP}:{_config.ServerPort}...");
            }
        }

        private void OnConnectionStatusChanged(object sender, bool isConnected)
        {
            Application.MainLoop.Invoke(() =>
            {
                if (isConnected)
                {
                    _connectionLabel.Text = "■ Connected";
                    _connectionLabel.ColorScheme = _connectedColorScheme;
                }
                else
                {
                    _connectionLabel.Text = "■ Disconnected";
                    _connectionLabel.ColorScheme = _disconnectedColorScheme;
                }
                _connectionLabel.SetNeedsDisplay();
                _statusPanel.UpdateDisplay();
            });
        }

        private void OnMessageReceived(object sender, string message)
        {
            Application.MainLoop.Invoke(() =>
            {
                _logPanel.AddLog(message);
            });
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
                Text = "Parallel Computing Endpoint\n\n" +
                      "F1: Show this help screen\n" +
                      "F2: Configure endpoint settings\n" +
                      "F3: Scan for and connect to servers\n" +
                      "F4: Run benchmark tests\n" +
                      "F5: Show about information\n" +
                      "F10: Exit the application\n\n" +
                      "This endpoint connects to a Parallel Computing Server\n" +
                      "and performs distributed computation tasks using ILGPU.\n\n" +
                      "The endpoint listens for incoming computation tasks and\n" +
                      "returns results to the server when complete."
            };
            dialog.Add(helpText);

            // Use fixed positioning for the button
            var okButton = new Button(25, 20, "OK");
            okButton.Clicked += () => Application.RequestStop();
            dialog.Add(okButton);

            Application.Run(dialog);
        }

        private void ShowSettings()
        {
            var dialog = new Dialog("Settings", 60, 15);

            var serverIpLabel = new Label("Server IP:")
            {
                X = 1,
                Y = 1
            };

            var serverIpField = new TextField(_config.ServerIP)
            {
                X = 15,
                Y = 1,
                Width = 30
            };

            var serverPortLabel = new Label("Server Port:")
            {
                X = 1,
                Y = 3
            };

            var serverPortField = new TextField(_config.ServerPort.ToString())
            {
                X = 15,
                Y = 3,
                Width = 10
            };

            var beaconPortLabel = new Label("Beacon Port:")
            {
                X = 1,
                Y = 5
            };

            var beaconPortField = new TextField(_config.BeaconPort.ToString())
            {
                X = 15,
                Y = 5,
                Width = 10
            };

            var endpointNameLabel = new Label("Endpoint Name:")
            {
                X = 1,
                Y = 7
            };

            var endpointNameField = new TextField(_config.EndpointName)
            {
                X = 15,
                Y = 7,
                Width = 30
            };

            var autoConnectLabel = new Label("Auto Connect:")
            {
                X = 1,
                Y = 9
            };

            var autoConnectCheck = new CheckBox()
            {
                X = 15,
                Y = 9,
                Checked = _config.AutoConnect
            };

            var saveButton = new Button("Save")
            {
                X = Pos.Center() - 10,
                Y = Pos.Bottom(dialog) - 3,
            };

            var cancelButton = new Button("Cancel")
            {
                X = Pos.Center() + 2,
                Y = Pos.Bottom(dialog) - 3,
            };

            saveButton.Clicked += () =>
            {
                try
                {
                    // Parse port numbers
                    int serverPort = int.Parse(serverPortField.Text.ToString());
                    int beaconPort = int.Parse(beaconPortField.Text.ToString());

                    // Validate settings
                    if (serverPort < 1024 || serverPort > 65535 ||
                        beaconPort < 1024 || beaconPort > 65535)
                    {
                        MessageBox.ErrorQuery("Invalid Settings",
                            "Port numbers must be between 1024 and 65535.", "OK");
                        return;
                    }

                    // Apply new settings
                    _config.ServerIP = serverIpField.Text.ToString();
                    _config.ServerPort = serverPort;
                    _config.BeaconPort = beaconPort;
                    _config.EndpointName = endpointNameField.Text.ToString();
                    _config.AutoConnect = autoConnectCheck.Checked;

                    Application.RequestStop();

                    // Add log message
                    _logPanel.AddLog("Settings updated.");

                    // Update status panel
                    _statusPanel.UpdateDisplay();
                }
                catch (Exception)
                {
                    MessageBox.ErrorQuery("Invalid Input", "Please enter valid numbers.", "OK");
                }
            };

            cancelButton.Clicked += () => Application.RequestStop();

            dialog.Add(
                serverIpLabel, serverIpField,
                serverPortLabel, serverPortField,
                beaconPortLabel, beaconPortField,
                endpointNameLabel, endpointNameField,
                autoConnectLabel, autoConnectCheck,
                saveButton, cancelButton
            );

            Application.Run(dialog);
        }

        private void ShowServerScan()
        {
            var scanPanel = new ServerScanPanel(_networkService, _logPanel);
            Application.Run(scanPanel);
        }

        private async void RunBenchmark()
        {
            var dialog = new Dialog("Running Benchmark", 60, 10);

            var progressText = new Label("Running benchmark tests. Please wait...")
            {
                X = Pos.Center(),
                Y = Pos.Center(),
                Width = Dim.Fill() - 4,
                TextAlignment = TextAlignment.Centered
            };

            dialog.Add(progressText);

            // Create a cancellation token source for the task
            var cts = new CancellationTokenSource();

            // Start the benchmark in a background task
            var benchmarkTask = Task.Run(() => _computeService.RunBenchmark(), cts.Token);

            // Add a timer to close the dialog when the benchmark finishes
            Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(100), (_) => {
                if (benchmarkTask.IsCompleted)
                {
                    Application.RequestStop();
                    return false; // Stop the timer
                }
                return true; // Continue checking
            });

            // Show the dialog modally - this will block until RequestStop is called
            Application.Run(dialog);

            // Get the results from the completed task
            string results = await benchmarkTask;

            // Show results dialog
            var resultsDialog = new Dialog("Benchmark Results", 70, 20);

            var resultsText = new TextView()
            {
                X = 1,
                Y = 1,
                Width = Dim.Fill() - 2,
                Height = Dim.Fill() - 4,
                ReadOnly = true,
                Text = results
            };

            var closeButton = new Button("Close")
            {
                X = Pos.Center(),
                Y = Pos.Bottom(resultsDialog) - 3,
            };
            closeButton.Clicked += () => Application.RequestStop();

            resultsDialog.Add(resultsText, closeButton);
            Application.Run(resultsDialog);

            // Add log entry
            _logPanel.AddLog("Benchmark completed.");
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
                Text = "CTS - Parallel Computing Endpoint\n" +
                      $"Version {versionStr}\n\n" +
                      "University of Fribourg\n" +
                      "Geosciences Department\n\n" +
                      "Contact: matteo.mangiagalli@unifr.ch\n" +
                      "© 2025 All Rights Reserved"
            };
            dialog.Add(aboutText);

            // Use fixed positioning for the button
            var okButton = new Button(25, 16, "OK");
            okButton.Clicked += () => Application.RequestStop();
            dialog.Add(okButton);

            Application.Run(dialog);
        }
    }
}