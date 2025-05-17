using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Krypton.Toolkit;
using System.Diagnostics;
using System.Text.Json;
using Timer = System.Threading.Timer;

namespace CTS
{
    /// <summary>
    /// Class representing a remote compute endpoint in the cluster
    /// </summary>
    public class ComputeEndpoint
    {
        public string Name { get; set; }
        public string IP { get; set; }
        public int Port { get; set; }
        public bool IsConnected { get; private set; }
        public bool IsBusy { get; private set; }
        public DateTime LastSeen { get; set; }
        public bool HasGPU { get; set; }
        public string AcceleratorType { get; set; }
        public int ConnectedNodes { get; set; }
        public TcpClient Connection { get; set; }
        public NetworkStream Stream { get; private set; }

        public event EventHandler<bool> ConnectionStatusChanged;

        public ComputeEndpoint(string name, string ip, int port)
        {
            Name = name;
            IP = ip;
            Port = port;
            LastSeen = DateTime.Now;
        }

        public async Task ConnectAsync()
        {
            try
            {
                Connection = new TcpClient();
                await Connection.ConnectAsync(IP, Port);
                Stream = Connection.GetStream();
                IsConnected = true;
                ConnectionStatusChanged?.Invoke(this, true);
            }
            catch (Exception ex)
            {
                Logger.Log($"[ComputeEndpoint] Failed to connect to {Name}: {ex.Message}");
                IsConnected = false;
                ConnectionStatusChanged?.Invoke(this, false);
                throw;
            }
        }

        public void Disconnect()
        {
            if (IsConnected)
            {
                try
                {
                    Stream?.Close();
                    Connection?.Close();
                }
                catch (Exception ex)
                {
                    Logger.Log($"[ComputeEndpoint] Error disconnecting from {Name}: {ex.Message}");
                }
                finally
                {
                    Stream = null;
                    Connection = null;
                    IsConnected = false;
                    ConnectionStatusChanged?.Invoke(this, false);
                }
            }
        }

        public async Task<string> SendCommandAsync(string command)
        {
            if (!IsConnected)
                throw new InvalidOperationException("Endpoint is not connected");

            try
            {
                byte[] commandBytes = Encoding.UTF8.GetBytes(command);
                await Stream.WriteAsync(commandBytes, 0, commandBytes.Length);

                // Read response with timeout
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                {
                    // Simple response reading
                    byte[] responseBuffer = new byte[8192];
                    int bytesRead = await Stream.ReadAsync(responseBuffer, 0, responseBuffer.Length, cts.Token);
                    return Encoding.UTF8.GetString(responseBuffer, 0, bytesRead);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[ComputeEndpoint] Error sending command to {Name}: {ex.Message}");
                throw;
            }
        }

        public void UpdateFromBeacon(BeaconMessage beacon)
        {
            Name = beacon.ServerName;
            ConnectedNodes = beacon.NodesConnected;
            HasGPU = beacon.GpuEnabled;
            LastSeen = DateTime.Now;
        }

        public void SetBusy(bool busy)
        {
            IsBusy = busy;
        }
    }

    /// <summary>
    /// Manager class for handling compute endpoints in the cluster
    /// </summary>
    public class ComputeEndpointManager
    {
        private List<ComputeEndpoint> endpoints = new List<ComputeEndpoint>();
        private UdpClient beaconListener;
        private CancellationTokenSource cts = new CancellationTokenSource();
        private int beaconPort = 7001; // Same as in Program.cs
        private Timer keepAliveTimer;
        private const int KeepAliveInterval = 5000; // 5 seconds

        public event EventHandler<ComputeEndpoint> EndpointDiscovered;
        public event EventHandler<ComputeEndpoint> EndpointStatusChanged;
        public event EventHandler<ComputeEndpoint> EndpointRemoved;

        public List<ComputeEndpoint> Endpoints => endpoints.ToList(); // Return a copy

        public ComputeEndpointManager()
        {
            keepAliveTimer = new Timer(KeepAliveTimerCallback, null, KeepAliveInterval, KeepAliveInterval);
        }

        public async Task StartDiscoveryAsync()
        {
            try
            {
                beaconListener = new UdpClient();
                try
                {
                    beaconListener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    beaconListener.Client.Bind(new IPEndPoint(IPAddress.Any, beaconPort));
                }
                catch (Exception ex)
                {
                    Logger.Log($"[ComputeEndpointManager] Failed to bind to port {beaconPort}: {ex.Message}");
                    Logger.Log("[ComputeEndpointManager] Trying to bind to any available port");
                    beaconListener.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
                }

                beaconListener.EnableBroadcast = true;

                Logger.Log("[ComputeEndpointManager] Started beacon discovery service");

                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        // Use a cancellation token source with timeout
                        using (var receiveTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token)) 
                            receiveTimeoutCts.CancelAfter(TimeSpan.FromSeconds(10)); // 10-second timeout
                        var result = await beaconListener.ReceiveAsync();
                        var message = Encoding.UTF8.GetString(result.Buffer);

                        try
                        {
                            var beaconMessage = JsonSerializer.Deserialize<BeaconMessage>(message);

                            // Process beacon message
                            ProcessBeaconMessage(beaconMessage, result.RemoteEndPoint);
                        }
                        catch (JsonException)
                        {
                            // Invalid JSON format - ignore
                            Logger.Log($"[ComputeEndpointManager] Received invalid beacon format");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[ComputeEndpointManager] Error receiving beacon: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[ComputeEndpointManager] Discovery error: {ex.Message}");
            }
            finally
            {
                beaconListener?.Close();
                Logger.Log("[ComputeEndpointManager] Beacon discovery service stopped");
            }
        }

        public void StopDiscovery()
        {
            Logger.Log("[ComputeEndpointManager] Stopping discovery service");
            cts.Cancel();
            beaconListener?.Close();
        }

        private void ProcessBeaconMessage(BeaconMessage message, IPEndPoint endpoint)
        {
            try
            {
                // Use server IP from the beacon if available, otherwise use the sender's IP
                string serverIP = !string.IsNullOrEmpty(message.ServerIP) ? message.ServerIP : endpoint.Address.ToString();

                // Check if we already know this endpoint
                var existingEndpoint = endpoints.FirstOrDefault(e =>
                    e.IP == serverIP && e.Port == message.ServerPort);

                if (existingEndpoint != null)
                {
                    // Update existing endpoint
                    existingEndpoint.UpdateFromBeacon(message);
                    EndpointStatusChanged?.Invoke(this, existingEndpoint);
                }
                else
                {
                    // Create new endpoint
                    var newEndpoint = new ComputeEndpoint(message.ServerName, serverIP, message.ServerPort);
                    newEndpoint.UpdateFromBeacon(message);
                    endpoints.Add(newEndpoint);

                    Logger.Log($"[ComputeEndpointManager] Discovered new compute endpoint: {newEndpoint.Name} at {newEndpoint.IP}:{newEndpoint.Port}");
                    EndpointDiscovered?.Invoke(this, newEndpoint);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[ComputeEndpointManager] Error processing beacon message: {ex.Message}");
            }
        }

        private void KeepAliveTimerCallback(object state)
        {
            // Check for endpoints that haven't been seen in a while
            var now = DateTime.Now;
            var timeoutThreshold = TimeSpan.FromSeconds(15);

            for (int i = endpoints.Count - 1; i >= 0; i--)
            {
                var endpoint = endpoints[i];
                if (now - endpoint.LastSeen > timeoutThreshold)
                {
                    // Endpoint timed out
                    Logger.Log($"[ComputeEndpointManager] Endpoint timed out: {endpoint.Name} ({endpoint.IP}:{endpoint.Port})");
                    endpoint.Disconnect();
                    endpoints.RemoveAt(i);
                    EndpointRemoved?.Invoke(this, endpoint);
                }
            }

            // Send keep-alive to connected endpoints
            foreach (var endpoint in endpoints.Where(e => e.IsConnected))
            {
                try
                {
                    // This should really be done in a separate task to avoid blocking the timer
                    Task.Run(async () =>
                    {
                        try
                        {
                            // Send a simple ping command
                            var command = new { Command = "PING" };
                            await endpoint.SendCommandAsync(JsonSerializer.Serialize(command));
                        }
                        catch
                        {
                            endpoint.Disconnect();
                            EndpointStatusChanged?.Invoke(this, endpoint);
                        }
                    });
                }
                catch
                {
                    endpoint.Disconnect();
                    EndpointStatusChanged?.Invoke(this, endpoint);
                }
            }
        }

        public async Task ConnectToEndpointAsync(ComputeEndpoint endpoint)
        {
            await endpoint.ConnectAsync();
            EndpointStatusChanged?.Invoke(this, endpoint);
        }

        public void DisconnectEndpoint(ComputeEndpoint endpoint)
        {
            endpoint.Disconnect();
            EndpointStatusChanged?.Invoke(this, endpoint);
        }

        public async Task RestartEndpointAsync(ComputeEndpoint endpoint)
        {
            if (!endpoint.IsConnected)
                await endpoint.ConnectAsync();

            // Send restart command
            var command = new { Command = "RESTART" };
            await endpoint.SendCommandAsync(JsonSerializer.Serialize(command));

            // The endpoint will disconnect and reconnect, but we'll monitor it via beacon
            endpoint.Disconnect();
            EndpointStatusChanged?.Invoke(this, endpoint);
        }

        public async Task ShutdownEndpointAsync(ComputeEndpoint endpoint)
        {
            if (!endpoint.IsConnected)
                await endpoint.ConnectAsync();

            // Send shutdown command
            var command = new { Command = "SHUTDOWN" };
            await endpoint.SendCommandAsync(JsonSerializer.Serialize(command));

            // The endpoint will shutdown and stop sending beacons
            endpoint.Disconnect();
            EndpointStatusChanged?.Invoke(this, endpoint);
        }

        public async Task<string> RunDiagnosticsAsync(ComputeEndpoint endpoint)
        {
            if (!endpoint.IsConnected)
                await endpoint.ConnectAsync();

            endpoint.SetBusy(true);
            EndpointStatusChanged?.Invoke(this, endpoint);

            try
            {
                // Send diagnostics command
                var command = new { Command = "DIAGNOSTICS" };
                var result = await endpoint.SendCommandAsync(JsonSerializer.Serialize(command));
                return result;
            }
            finally
            {
                endpoint.SetBusy(false);
                EndpointStatusChanged?.Invoke(this, endpoint);
            }
        }

        public void AddEndpointManually(string name, string ip, int port)
        {
            // Check if endpoint already exists
            var existingEndpoint = endpoints.FirstOrDefault(e => e.IP == ip && e.Port == port);
            if (existingEndpoint != null)
            {
                Logger.Log($"[ComputeEndpointManager] Endpoint at {ip}:{port} already exists");
                return;
            }

            var newEndpoint = new ComputeEndpoint(name, ip, port);
            endpoints.Add(newEndpoint);
            Logger.Log($"[ComputeEndpointManager] Manually added endpoint: {name} at {ip}:{port}");
            EndpointDiscovered?.Invoke(this, newEndpoint);
        }

        public void SaveEndpoints(string filePath)
        {
            var endpointsData = endpoints.Select(e => new
            {
                e.Name,
                e.IP,
                e.Port,
                e.HasGPU,
                e.AcceleratorType
            }).ToList();

            string json = JsonSerializer.Serialize(endpointsData, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(filePath, json);
            Logger.Log($"[ComputeEndpointManager] Saved {endpoints.Count} endpoints to {filePath}");
        }

        public void LoadEndpoints(string filePath)
        {
            if (!File.Exists(filePath))
            {
                Logger.Log($"[ComputeEndpointManager] Endpoint file not found: {filePath}");
                return;
            }

            try
            {
                string json = File.ReadAllText(filePath);
                var endpointsData = JsonSerializer.Deserialize<List<EndpointData>>(json);

                foreach (var data in endpointsData)
                {
                    // Skip if endpoint already exists
                    if (endpoints.Any(e => e.IP == data.IP && e.Port == data.Port))
                        continue;

                    var newEndpoint = new ComputeEndpoint(data.Name, data.IP, data.Port);
                    newEndpoint.HasGPU = data.HasGPU;
                    newEndpoint.AcceleratorType = data.AcceleratorType;

                    endpoints.Add(newEndpoint);
                    EndpointDiscovered?.Invoke(this, newEndpoint);
                }

                Logger.Log($"[ComputeEndpointManager] Loaded {endpointsData.Count} endpoints from {filePath}");
            }
            catch (Exception ex)
            {
                Logger.Log($"[ComputeEndpointManager] Error loading endpoints: {ex.Message}");
                throw;
            }
        }

        private class EndpointData
        {
            public string Name { get; set; }
            public string IP { get; set; }
            public int Port { get; set; }
            public bool HasGPU { get; set; }
            public string AcceleratorType { get; set; }
        }
    }

    /// <summary>
    /// Form for managing the compute cluster
    /// </summary>
    public class ComputeClusterForm : KryptonForm
    {
        private ComputeEndpointManager manager = new ComputeEndpointManager();
        private List<ComputeEndpoint> selectedEndpoints = new List<ComputeEndpoint>();
        private Dictionary<ComputeEndpoint, EndpointControl> endpointControls = new Dictionary<ComputeEndpoint, EndpointControl>();
        private MainForm mainForm;

        // UI controls
        private FlowLayoutPanel topologyPanel;
        private KryptonRichTextBox outputTextBox;
        private KryptonButton btnConnect;
        private KryptonButton btnDisconnect;
        private KryptonButton btnRestart;
        private KryptonButton btnShutdown;
        private KryptonButton btnRunDiagnostics;
        private KryptonButton btnAddManually;
        private KryptonButton btnSave;
        private KryptonButton btnLoad;
        private System.Windows.Forms.Timer refreshTimer;

        public ComputeClusterForm(MainForm mainForm)
        {
            this.mainForm = mainForm;
            this.Icon = Properties.Resources.favicon;
            InitializeComponent();

            // Start discovery
            Task.Run(async () => await manager.StartDiscoveryAsync());

            // Set up event handlers
            manager.EndpointDiscovered += Manager_EndpointDiscovered;
            manager.EndpointStatusChanged += Manager_EndpointStatusChanged;
            manager.EndpointRemoved += Manager_EndpointRemoved;

            // Set up refresh timer to update the UI
            refreshTimer = new System.Windows.Forms.Timer();
            refreshTimer.Interval = 1000; // 1 second
            refreshTimer.Tick += RefreshTimer_Tick;
            refreshTimer.Start();

            LogMessage("Compute Cluster Manager started");
            LogMessage("Scanning for available compute endpoints...");
        }

        private void InitializeComponent()
        {
            // Set form properties
            this.Text = "Compute Cluster Manager";
            this.Size = new Size(840, 600);
            this.MinimumSize = new Size(800, 600);
            this.StartPosition = FormStartPosition.CenterParent;
            this.BackColor = Color.FromArgb(30, 30, 30);
            this.ForeColor = Color.White;
            this.PaletteMode = PaletteMode.Office2010Black;

            // Create layout
            TableLayoutPanel mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                BackColor = Color.FromArgb(30, 30, 30)
            };

            mainLayout.RowStyles.Clear();
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 70));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 30));

            // Top panel for buttons
            FlowLayoutPanel buttonPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoScroll = true,
                BackColor = Color.FromArgb(30, 30, 30)
            };

            btnConnect = CreateButton("Connect");
            btnDisconnect = CreateButton("Disconnect");
            btnRestart = CreateButton("Restart");
            btnShutdown = CreateButton("Shutdown");
            btnRunDiagnostics = CreateButton("Diagnostics");
            btnAddManually = CreateButton("Add Manually");
            btnSave = CreateButton("Save List");
            btnLoad = CreateButton("Load List");

            buttonPanel.Controls.AddRange(new Control[]
            {
                btnConnect, btnDisconnect, btnRestart, btnShutdown,
                btnRunDiagnostics, btnAddManually, btnSave, btnLoad
            });

            // Middle panel for topology map
            KryptonGroupBox topologyGroup = new KryptonGroupBox();
            topologyGroup.Dock = DockStyle.Fill;
            topologyGroup.Text = "Compute Endpoint Topology";
            topologyGroup.PaletteMode = PaletteMode.Office2010Black;

            topologyPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                AutoScroll = true,
                BackColor = Color.FromArgb(20, 20, 20),
                Padding = new Padding(10)
            };

            topologyGroup.Panel.Controls.Add(topologyPanel);

            // Bottom panel for output
            KryptonGroupBox outputGroup = new KryptonGroupBox();
            outputGroup.Dock = DockStyle.Fill;
            outputGroup.Text = "Output";
            outputGroup.PaletteMode = PaletteMode.Office2010Black;

            outputTextBox = new KryptonRichTextBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(25, 25, 25),
                ForeColor = Color.LightGray,
                ReadOnly = true,
                Font = new Font("Consolas", 9),
                Margin = new Padding(5)
            };

            outputGroup.Panel.Controls.Add(outputTextBox);

            mainLayout.Controls.Add(buttonPanel, 0, 0);
            mainLayout.Controls.Add(topologyGroup, 0, 1);
            mainLayout.Controls.Add(outputGroup, 0, 2);

            this.Controls.Add(mainLayout);

            // Set up button event handlers
            btnConnect.Click += btnConnect_Click;
            btnDisconnect.Click += btnDisconnect_Click;
            btnRestart.Click += btnRestart_Click;
            btnShutdown.Click += btnShutdown_Click;
            btnRunDiagnostics.Click += btnRunDiagnostics_Click;
            btnAddManually.Click += btnAddManually_Click;
            btnSave.Click += btnSave_Click;
            btnLoad.Click += btnLoad_Click;

            // Initially disable buttons until endpoints are selected
            btnConnect.Enabled = false;
            btnDisconnect.Enabled = false;
            btnRestart.Enabled = false;
            btnShutdown.Enabled = false;
            btnRunDiagnostics.Enabled = false;
        }

        private KryptonButton CreateButton(string text)
        {
            var button = new KryptonButton
            {
                Text = text,
                PaletteMode = PaletteMode.Office2010Black,
                Width = 90,
                Height = 30,
                Margin = new Padding(5)
            };
            return button;
        }

        private void RefreshTimer_Tick(object sender, EventArgs e)
        {
            // Update the endpoint controls
            foreach (var kvp in endpointControls)
            {
                kvp.Value.UpdateDisplay();
            }

            // Update button states based on selection
            bool hasSelection = selectedEndpoints.Count > 0;
            bool hasConnected = selectedEndpoints.Any(endpoint => endpoint.IsConnected);

            btnConnect.Enabled = hasSelection && selectedEndpoints.Any(endpoint => !endpoint.IsConnected);
            btnDisconnect.Enabled = hasConnected;
            btnRestart.Enabled = hasConnected;
            btnShutdown.Enabled = hasConnected;
            btnRunDiagnostics.Enabled = hasConnected;
        }

        private void Manager_EndpointDiscovered(object sender, ComputeEndpoint e)
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(() => Manager_EndpointDiscovered(sender, e)));
                return;
            }

            // Create a visual control for the endpoint
            var control = new EndpointControl(e);
            control.Selected += EndpointControl_Selected;

            endpointControls[e] = control;
            topologyPanel.Controls.Add(control);

            LogMessage($"Discovered endpoint: {e.Name} ({e.IP}:{e.Port})");

            // If this is the first endpoint discovered, let's add it to the mainForm endpoints list
            if (mainForm.ComputeEndpoints == null)
            {
                mainForm.ComputeEndpoints = new List<ComputeEndpoint>();
            }

            if (!mainForm.ComputeEndpoints.Any(ep => ep.IP == e.IP && ep.Port == e.Port))
            {
                mainForm.ComputeEndpoints.Add(e);
            }
        }

        private void Manager_EndpointStatusChanged(object sender, ComputeEndpoint e)
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(() => Manager_EndpointStatusChanged(sender, e)));
                return;
            }

            if (endpointControls.TryGetValue(e, out var control))
            {
                control.UpdateDisplay();
            }
        }

        private void Manager_EndpointRemoved(object sender, ComputeEndpoint e)
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(() => Manager_EndpointRemoved(sender, e)));
                return;
            }

            if (endpointControls.TryGetValue(e, out var control))
            {
                topologyPanel.Controls.Remove(control);
                endpointControls.Remove(e);
                selectedEndpoints.Remove(e);
                control.Dispose();
            }

            LogMessage($"Endpoint removed: {e.Name} ({e.IP}:{e.Port})");

            // Remove from mainForm's list too
            if (mainForm.ComputeEndpoints != null)
            {
                var endpointToRemove = mainForm.ComputeEndpoints.FirstOrDefault(ep => ep.IP == e.IP && ep.Port == e.Port);
                if (endpointToRemove != null)
                {
                    mainForm.ComputeEndpoints.Remove(endpointToRemove);
                }
            }
        }

        private void EndpointControl_Selected(object sender, EventArgs args)
        {
            var control = sender as EndpointControl;
            if (control != null)
            {
                // Check if Ctrl is pressed for multi-select
                bool ctrlPressed = (Control.ModifierKeys & Keys.Control) == Keys.Control;

                if (!ctrlPressed)
                {
                    // Deselect all other endpoints
                    foreach (var kvp in endpointControls)
                    {
                        if (kvp.Value != control)
                        {
                            kvp.Value.IsSelected = false;
                        }
                    }
                    selectedEndpoints.Clear();
                }

                // Toggle selection of clicked endpoint
                if (control.IsSelected)
                {
                    selectedEndpoints.Add(control.Endpoint);
                }
                else
                {
                    selectedEndpoints.Remove(control.Endpoint);
                }
            }
        }

        private async void btnConnect_Click(object sender, EventArgs e)
        {
            if (selectedEndpoints.Count == 0)
                return;

            // Connect to selected endpoints
            btnConnect.Enabled = false;

            foreach (var endpoint in selectedEndpoints.ToList())
            {
                if (!endpoint.IsConnected)
                {
                    try
                    {
                        LogMessage($"Connecting to {endpoint.Name}...");
                        await manager.ConnectToEndpointAsync(endpoint);
                        LogMessage($"Connected to {endpoint.Name} successfully");
                    }
                    catch (Exception ex)
                    {
                        LogMessage($"Failed to connect to {endpoint.Name}: {ex.Message}");
                    }
                }
            }

            btnConnect.Enabled = true;
        }

        private void btnDisconnect_Click(object sender, EventArgs e)
        {
            if (selectedEndpoints.Count == 0)
                return;

            foreach (var endpoint in selectedEndpoints.ToList().Where(ep => ep.IsConnected))
            {
                try
                {
                    LogMessage($"Disconnecting from {endpoint.Name}...");
                    manager.DisconnectEndpoint(endpoint);
                    LogMessage($"Disconnected from {endpoint.Name}");
                }
                catch (Exception ex)
                {
                    LogMessage($"Error disconnecting from {endpoint.Name}: {ex.Message}");
                }
            }
        }

        private async void btnRestart_Click(object sender, EventArgs e)
        {
            if (selectedEndpoints.Count == 0)
                return;

            // Restart selected endpoints
            btnRestart.Enabled = false;

            foreach (var endpoint in selectedEndpoints.ToList().Where(ep => ep.IsConnected))
            {
                try
                {
                    LogMessage($"Restarting {endpoint.Name}...");
                    await manager.RestartEndpointAsync(endpoint);
                    LogMessage($"Restart command sent to {endpoint.Name}");
                }
                catch (Exception ex)
                {
                    LogMessage($"Failed to restart {endpoint.Name}: {ex.Message}");
                }
            }

            btnRestart.Enabled = true;
        }

        private async void btnShutdown_Click(object sender, EventArgs e)
        {
            if (selectedEndpoints.Count == 0)
                return;

            // Confirm shutdown
            DialogResult result = MessageBox.Show(
                $"Are you sure you want to shut down {selectedEndpoints.Count} compute endpoints?",
                "Confirm Shutdown",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (result != DialogResult.Yes)
                return;

            // Shutdown selected endpoints
            btnShutdown.Enabled = false;

            foreach (var endpoint in selectedEndpoints.ToList().Where(ep => ep.IsConnected))
            {
                try
                {
                    LogMessage($"Shutting down {endpoint.Name}...");
                    await manager.ShutdownEndpointAsync(endpoint);
                    LogMessage($"Shutdown command sent to {endpoint.Name}");
                }
                catch (Exception ex)
                {
                    LogMessage($"Failed to shut down {endpoint.Name}: {ex.Message}");
                }
            }

            btnShutdown.Enabled = true;
        }

        private async void btnRunDiagnostics_Click(object sender, EventArgs e)
        {
            if (selectedEndpoints.Count == 0)
                return;

            // Run diagnostics on selected endpoints
            btnRunDiagnostics.Enabled = false;

            foreach (var endpoint in selectedEndpoints.ToList().Where(ep => ep.IsConnected))
            {
                try
                {
                    LogMessage($"Running diagnostics on {endpoint.Name}...");
                    endpoint.SetBusy(true);

                    string diagnosticResult = await manager.RunDiagnosticsAsync(endpoint);

                    LogMessage($"Diagnostics for {endpoint.Name}:");
                    LogMessage(diagnosticResult);
                }
                catch (Exception ex)
                {
                    LogMessage($"Failed to run diagnostics on {endpoint.Name}: {ex.Message}");
                }
                finally
                {
                    endpoint.SetBusy(false);
                }
            }

            btnRunDiagnostics.Enabled = true;
        }

        private void btnAddManually_Click(object sender, EventArgs e)
        {
            // Show dialog to add endpoint manually
            using (var dialog = new AddEndpointDialog())
            {
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    string name = dialog.EndpointName;
                    string ip = dialog.EndpointIP;
                    int port = dialog.EndpointPort;

                    manager.AddEndpointManually(name, ip, port);
                    LogMessage($"Manually added endpoint: {name} ({ip}:{port})");
                }
            }
        }

        private void btnSave_Click(object sender, EventArgs e)
        {
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*";
                dialog.DefaultExt = "json";
                dialog.Title = "Save Compute Endpoints";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        manager.SaveEndpoints(dialog.FileName);
                        LogMessage($"Endpoints saved to {dialog.FileName}");
                    }
                    catch (Exception ex)
                    {
                        LogMessage($"Error saving endpoints: {ex.Message}");
                    }
                }
            }
        }

        private void btnLoad_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*";
                dialog.DefaultExt = "json";
                dialog.Title = "Load Compute Endpoints";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        manager.LoadEndpoints(dialog.FileName);
                        LogMessage($"Endpoints loaded from {dialog.FileName}");
                    }
                    catch (Exception ex)
                    {
                        LogMessage($"Error loading endpoints: {ex.Message}");
                    }
                }
            }
        }

        private void LogMessage(string message)
        {
            if (outputTextBox.InvokeRequired)
            {
                outputTextBox.BeginInvoke(new Action(() => LogMessage(message)));
                return;
            }

            outputTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            // Scroll to end
            outputTextBox.SelectionStart = outputTextBox.Text.Length;
            outputTextBox.ScrollToCaret();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Stop discovery and clean up
            refreshTimer.Stop();
            manager.StopDiscovery();

            // Disconnect all endpoints
            foreach (var endpoint in manager.Endpoints.Where(ep => ep.IsConnected))
            {
                try
                {
                    manager.DisconnectEndpoint(endpoint);
                }
                catch
                {
                    // Ignore errors on shutdown
                }
            }

            base.OnFormClosing(e);
        }

        // Custom control to display a compute endpoint
        private class EndpointControl : Panel
        {
            public ComputeEndpoint Endpoint { get; }
            public bool IsSelected { get; set; }

            private Label lblName;
            private Label lblStatus;
            private Label lblIP;
            private Label lblGPU;
            private PictureBox statusIcon;

            public event EventHandler Selected;

            public EndpointControl(ComputeEndpoint endpoint)
            {
                Endpoint = endpoint;

                // Set up visual appearance
                this.Size = new Size(150, 120);
                this.Margin = new Padding(10);
                this.BackColor = Color.FromArgb(40, 40, 40);
                this.BorderStyle = BorderStyle.FixedSingle;

                // Add controls
                statusIcon = new PictureBox
                {
                    Size = new Size(16, 16),
                    Location = new Point(5, 5),
                    BackColor = Color.Transparent
                };

                // Draw circle for status icon
                Bitmap iconBitmap = new Bitmap(16, 16);
                using (Graphics g = Graphics.FromImage(iconBitmap))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (SolidBrush brush = new SolidBrush(Color.Gray))
                    {
                        g.FillEllipse(brush, 0, 0, 16, 16);
                    }
                }
                statusIcon.Image = iconBitmap;

                lblName = new Label
                {
                    Text = endpoint.Name,
                    Location = new Point(25, 5),
                    AutoSize = false,
                    Size = new Size(120, 20),
                    TextAlign = ContentAlignment.MiddleLeft,
                    Font = new Font("Segoe UI", 9, FontStyle.Bold),
                    ForeColor = Color.White
                };

                lblStatus = new Label
                {
                    Text = "Disconnected",
                    Location = new Point(5, 30),
                    AutoSize = false,
                    Size = new Size(140, 20),
                    TextAlign = ContentAlignment.MiddleCenter,
                    ForeColor = Color.Gray
                };

                lblIP = new Label
                {
                    Text = $"{endpoint.IP}:{endpoint.Port}",
                    Location = new Point(5, 55),
                    AutoSize = false,
                    Size = new Size(140, 20),
                    TextAlign = ContentAlignment.MiddleCenter,
                    ForeColor = Color.Silver
                };

                lblGPU = new Label
                {
                    Text = endpoint.HasGPU ? "GPU: Yes" : "GPU: No",
                    Location = new Point(5, 80),
                    AutoSize = false,
                    Size = new Size(140, 20),
                    TextAlign = ContentAlignment.MiddleCenter,
                    ForeColor = endpoint.HasGPU ? Color.LightGreen : Color.Gray
                };

                this.Controls.AddRange(new Control[] { statusIcon, lblName, lblStatus, lblIP, lblGPU });

                // Add click handler
                this.Click += EndpointControl_Click;

                // Update display
                UpdateDisplay();
            }

            public void UpdateDisplay()
            {
                // Update all display elements based on endpoint state
                lblName.Text = Endpoint.Name;
                lblIP.Text = $"{Endpoint.IP}:{Endpoint.Port}";
                lblGPU.Text = Endpoint.HasGPU ? "GPU: Yes" : "GPU: No";
                lblGPU.ForeColor = Endpoint.HasGPU ? Color.LightGreen : Color.Gray;

                // Update the status icon
                UpdateStatusIcon();

                // Update status and background color
                if (!Endpoint.IsConnected)
                {
                    // Check if it's recently seen via beacon
                    if (DateTime.Now - Endpoint.LastSeen < TimeSpan.FromSeconds(10))
                    {
                        lblStatus.Text = "Available";
                        lblStatus.ForeColor = Color.Gray;
                        this.BackColor = IsSelected ? Color.FromArgb(60, 60, 60) : Color.FromArgb(40, 40, 40);
                    }
                    else
                    {
                        lblStatus.Text = "Offline";
                        lblStatus.ForeColor = Color.Gray;
                        this.BackColor = IsSelected ? Color.FromArgb(50, 50, 50) : Color.Black;
                    }
                }
                else if (Endpoint.IsBusy)
                {
                    lblStatus.Text = "Busy";
                    lblStatus.ForeColor = Color.Yellow;
                    this.BackColor = IsSelected ? Color.FromArgb(60, 60, 30) : Color.FromArgb(40, 40, 0);
                }
                else
                {
                    lblStatus.Text = "Connected";
                    lblStatus.ForeColor = Color.LightGreen;
                    this.BackColor = IsSelected ? Color.FromArgb(30, 60, 30) : Color.FromArgb(0, 40, 0);
                }
            }

            private void UpdateStatusIcon()
            {
                // Determine the color based on endpoint state
                Color statusColor;

                if (!Endpoint.IsConnected)
                {
                    // Check if it's recently seen via beacon
                    if (DateTime.Now - Endpoint.LastSeen < TimeSpan.FromSeconds(10))
                    {
                        statusColor = Color.Gray; // Available but not connected
                    }
                    else
                    {
                        statusColor = Color.Black; // Offline
                    }
                }
                else if (Endpoint.IsBusy)
                {
                    statusColor = Color.Yellow; // Busy
                }
                else
                {
                    statusColor = Color.Green; // Connected and ready
                }

                // Create a new status icon
                Bitmap iconBitmap = new Bitmap(16, 16);
                using (Graphics g = Graphics.FromImage(iconBitmap))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (SolidBrush brush = new SolidBrush(statusColor))
                    {
                        g.FillEllipse(brush, 0, 0, 16, 16);
                    }
                }

                // Replace the existing image
                var oldImage = statusIcon.Image;
                statusIcon.Image = iconBitmap;
                oldImage?.Dispose();
            }

            private void EndpointControl_Click(object sender, EventArgs eventArgs)
            {
                IsSelected = !IsSelected;
                UpdateDisplay();
                Selected?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>
    /// Dialog for manually adding a compute endpoint
    /// </summary>
    public class AddEndpointDialog : KryptonForm
    {
        private KryptonTextBox txtName;
        private KryptonTextBox txtIP;
        private KryptonNumericUpDown numPort;

        public string EndpointName => txtName.Text;
        public string EndpointIP => txtIP.Text;
        public int EndpointPort => (int)numPort.Value;

        public AddEndpointDialog()
        {
            this.Text = "Add Compute Endpoint";
            this.Size = new Size(350, 200);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.PaletteMode = PaletteMode.Office2010Black;

            // Create controls
            KryptonLabel lblName = new KryptonLabel { Text = "Name:", Location = new Point(20, 20), AutoSize = true };
            txtName = new KryptonTextBox { Location = new Point(120, 17), Width = 180 };

            KryptonLabel lblIP = new KryptonLabel { Text = "IP Address:", Location = new Point(20, 50), AutoSize = true };
            txtIP = new KryptonTextBox { Location = new Point(120, 47), Width = 180 };

            KryptonLabel lblPort = new KryptonLabel { Text = "Port:", Location = new Point(20, 80), AutoSize = true };
            numPort = new KryptonNumericUpDown
            {
                Location = new Point(120, 77),
                Width = 180,
                Minimum = 1,
                Maximum = 65535,
                Value = 7000  // Default port from Program.cs
            };

            KryptonButton btnOK = new KryptonButton
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Location = new Point(120, 120),
                Width = 80
            };

            KryptonButton btnCancel = new KryptonButton
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(220, 120),
                Width = 80
            };

            // Add controls to form
            this.Controls.AddRange(new Control[]
            {
                lblName, txtName,
                lblIP, txtIP,
                lblPort, numPort,
                btnOK, btnCancel
            });

            // Set accept and cancel buttons
            this.AcceptButton = btnOK;
            this.CancelButton = btnCancel;

            // Add validation handler
            btnOK.Click += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(txtName.Text))
                {
                    MessageBox.Show("Please enter a name for the endpoint.", "Validation Error",
                                  MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    this.DialogResult = DialogResult.None;
                    return;
                }

                if (string.IsNullOrWhiteSpace(txtIP.Text))
                {
                    MessageBox.Show("Please enter an IP address.", "Validation Error",
                                  MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    this.DialogResult = DialogResult.None;
                    return;
                }

                // Validate IP address format
                if (!IPAddress.TryParse(txtIP.Text, out _))
                {
                    MessageBox.Show("Please enter a valid IP address.", "Validation Error",
                                  MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    this.DialogResult = DialogResult.None;
                    return;
                }
            };
        }
    }
}