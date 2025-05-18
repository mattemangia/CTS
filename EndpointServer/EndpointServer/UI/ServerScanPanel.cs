using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Terminal.Gui;

namespace ParallelComputingEndpoint
{
    public class ServerScanPanel : Dialog
    {
        private readonly NetworkService _networkService;
        private readonly LogPanel _logPanel;
        private readonly ListView _serversListView;
        private readonly Label _statusLabel;
        private readonly Button _scanButton;
        private readonly Button _connectButton;
        private readonly Button _disconnectButton;
        private readonly Button _closeButton;

        private List<ServerInfo> _discoveredServers = new List<ServerInfo>();
        private bool _isScanning = false;

        public ServerScanPanel(NetworkService networkService, LogPanel logPanel)
            : base("Connect to Server", 70, 30) // Made even taller
        {
            _networkService = networkService;
            _logPanel = logPanel;

            // Create list view for discovered servers
            var listLabel = new Label("Available Servers:")
            {
                X = 1,
                Y = 1
            };

            _serversListView = new ListView()
            {
                X = 1,
                Y = 3,
                Width = Dim.Fill() - 2,
                Height = 15 // Fixed height instead of relative
            };

            // Status label for connection or scanning status
            _statusLabel = new Label("")
            {
                X = 1,
                Y = 19, // Fixed position right below the ListView
                Width = Dim.Fill() - 2
            };

            // Create a ButtonBar which handles layout automatically
            var buttonBar = new View()
            {
                X = 0,
                Y = 22, // Absolute position
                Width = Dim.Fill(),
                Height = 1
            };

            // Create the buttons with fixed positions
            _scanButton = new Button(3, 0, "Scan");
            buttonBar.Add(_scanButton);

            _connectButton = new Button(15, 0, "Connect") { Enabled = false };
            buttonBar.Add(_connectButton);

            _disconnectButton = new Button(30, 0, "Disconnect") { Enabled = _networkService.IsConnected };
            buttonBar.Add(_disconnectButton);

            _closeButton = new Button(50, 0, "Close");
            buttonBar.Add(_closeButton);

            // Add event handlers
            _scanButton.Clicked += OnScanClicked;
            _connectButton.Clicked += OnConnectClicked;
            _disconnectButton.Clicked += OnDisconnectClicked;
            _closeButton.Clicked += () => Application.RequestStop();

            // Add all views to the dialog
            Add(listLabel, _serversListView, _statusLabel, buttonBar);

            // Update UI based on connection status
            UpdateConnectionUI();

            // Auto-scan when opened
            Task.Run(async () => await ScanForServersAsync());
        }

        private void UpdateConnectionUI()
        {
            bool isConnected = _networkService.IsConnected;

            if (isConnected)
            {
                _statusLabel.Text = "Connected to server.";
                _connectButton.Enabled = false;
                _disconnectButton.Enabled = true;
            }
            else
            {
                bool hasSelection = _serversListView.SelectedItem >= 0 &&
                                   _serversListView.SelectedItem < _discoveredServers.Count;

                _connectButton.Enabled = hasSelection && !_isScanning;
                _disconnectButton.Enabled = false;

                if (_isScanning)
                {
                    _statusLabel.Text = "Scanning for servers...";
                }
                else if (_discoveredServers.Count == 0)
                {
                    _statusLabel.Text = "No servers found. Click Scan to search again.";
                }
                else
                {
                    _statusLabel.Text = "Select a server and click Connect.";
                }
            }
        }

        private void OnScanClicked()
        {
            if (!_isScanning)
            {
                Task.Run(async () => await ScanForServersAsync());
            }
        }

        private async Task ScanForServersAsync()
        {
            if (_isScanning) return;

            try
            {
                _isScanning = true;
                _scanButton.Enabled = false;
                _connectButton.Enabled = false;

                Application.MainLoop.Invoke(() =>
                {
                    _statusLabel.Text = "Scanning for servers...";
                    _statusLabel.SetNeedsDisplay();
                });

                _logPanel.AddLog("Scanning for servers...");

                // Clear existing servers
                _discoveredServers.Clear();
                UpdateServerList();

                // Start scan
                _discoveredServers = await _networkService.ScanForServersAsync();

                // Update UI with results
                Application.MainLoop.Invoke(() =>
                {
                    UpdateServerList();

                    if (_discoveredServers.Count > 0)
                    {
                        _statusLabel.Text = $"Found {_discoveredServers.Count} server(s). Select one and click Connect.";
                        _serversListView.SelectedItem = 0;
                        _connectButton.Enabled = true;
                    }
                    else
                    {
                        _statusLabel.Text = "No servers found. Click Scan to search again.";
                    }

                    _statusLabel.SetNeedsDisplay();
                });

                _logPanel.AddLog($"Found {_discoveredServers.Count} server(s).");
            }
            catch (Exception ex)
            {
                Application.MainLoop.Invoke(() =>
                {
                    _statusLabel.Text = $"Error scanning: {ex.Message}";
                    _statusLabel.SetNeedsDisplay();
                });

                _logPanel.AddLog($"Error scanning for servers: {ex.Message}");
            }
            finally
            {
                _isScanning = false;

                Application.MainLoop.Invoke(() =>
                {
                    _scanButton.Enabled = true;
                    _scanButton.SetNeedsDisplay();
                });
            }
        }

        private void UpdateServerList()
        {
            var serverList = new List<string>();

            if (_discoveredServers.Count == 0)
            {
                serverList.Add("No servers found");
            }
            else
            {
                serverList.Add($"{"Server Name",-20} {"IP Address",-15} {"Port",-8} {"Endpoints",-10} {"GPU",-5}");

                foreach (var server in _discoveredServers)
                {
                    serverList.Add($"{server.ServerName,-20} {server.ServerIP,-15} {server.ServerPort,-8} {server.EndpointsConnected,-10} {(server.GpuEnabled ? "Yes" : "No"),-5}");
                }
            }

            _serversListView.SetSource(serverList);
            _serversListView.SetNeedsDisplay();
        }

        private async void OnConnectClicked()
        {
            int selectedIndex = _serversListView.SelectedItem - 1; // -1 for header row

            if (selectedIndex >= 0 && selectedIndex < _discoveredServers.Count)
            {
                var selectedServer = _discoveredServers[selectedIndex];

                _statusLabel.Text = $"Connecting to {selectedServer.ServerName}...";
                _statusLabel.SetNeedsDisplay();

                _connectButton.Enabled = false;
                _scanButton.Enabled = false;

                _logPanel.AddLog($"Connecting to {selectedServer.ServerName} ({selectedServer.ServerIP}:{selectedServer.ServerPort})...");

                bool connected = await _networkService.ConnectToServerAsync(
                    selectedServer.ServerIP,
                    selectedServer.ServerPort);

                if (connected)
                {
                    _statusLabel.Text = $"Connected to {selectedServer.ServerName}.";
                    _disconnectButton.Enabled = true;

                    // Automatically close the dialog after successful connection
                    Application.RequestStop();
                }
                else
                {
                    _statusLabel.Text = "Connection failed. Check logs for details.";
                    _connectButton.Enabled = true;
                    _scanButton.Enabled = true;
                }

                _statusLabel.SetNeedsDisplay();
            }
        }

        private async void OnDisconnectClicked()
        {
            _statusLabel.Text = "Disconnecting...";
            _statusLabel.SetNeedsDisplay();

            _disconnectButton.Enabled = false;

            _logPanel.AddLog("Disconnecting from server...");

            await _networkService.DisconnectAsync();

            _statusLabel.Text = "Disconnected.";
            _scanButton.Enabled = true;

            UpdateConnectionUI();
            _statusLabel.SetNeedsDisplay();
        }
    }
}