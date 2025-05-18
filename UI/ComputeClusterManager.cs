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
    /// Class representing a server node in the compute cluster
    /// </summary>
    public class ComputeServer
    {
        public string Name { get; set; }
        public string IP { get; set; }
        public int Port { get; set; }
        public int EndpointPort { get; set; } // Port for endpoints to connect to
        public bool IsConnected { get; private set; }
        public bool IsBusy { get; private set; }
        public DateTime LastSeen { get; set; }
        public bool HasGPU { get; set; }
        public string AcceleratorType { get; set; }
        public List<ComputeEndpoint> ConnectedEndpoints { get; private set; } = new List<ComputeEndpoint>();
        public TcpClient Connection { get; set; }
        public NetworkStream Stream { get; private set; }

        public event EventHandler<bool> ConnectionStatusChanged;
        public event EventHandler EndpointsUpdated;
        private List<ComputeServer> servers = new List<ComputeServer>();
        private UdpClient beaconListener;
        private CancellationTokenSource cts = new CancellationTokenSource();
        private int beaconPort = 7001;
        private Timer keepAliveTimer;
        private const int KeepAliveInterval = 5000; // 5 seconds
        private bool isRefreshing = false; // Flag to prevent UI refresh during operations
        private bool isReconnecting = false;
        private int reconnectAttempts = 0;
        private const int MaxReconnectAttempts = 3;
        public event EventHandler<ComputeServer> ServerDiscovered;
        public event EventHandler<ComputeServer> ServerStatusChanged;
        public event EventHandler<ComputeServer> ServerRemoved;
        public event EventHandler<ComputeEndpoint> EndpointStatusChanged;
        public List<ComputeServer> Servers => servers.ToList();
        public ComputeServer(string name, string ip, int port)
        {
            Name = name;
            IP = ip;
            Port = port;
            EndpointPort = 7002; // Default endpoint port
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

                // After connecting, query for connected endpoints
                await RefreshEndpointsAsync();
            }
            catch (Exception ex)
            {
                Logger.Log($"[ComputeServer] Failed to connect to {Name}: {ex.Message}");
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
                    Logger.Log($"[ComputeServer] Error disconnecting from {Name}: {ex.Message}");
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
                throw new InvalidOperationException("Server is not connected");

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
            catch (IOException ex)
            {
                Logger.Log($"[ComputeServer] IO Error communicating with {Name}: {ex.Message}");

                // Mark as disconnected but don't try to reconnect here
                Disconnect();

                throw;
            }
            catch (Exception ex)
            {
                Logger.Log($"[ComputeServer] Error sending command to {Name}: {ex.Message}");
                throw;
            }
        }

        public async Task RefreshEndpointsAsync()
        {
            if (!IsConnected)
                return;

            try
            {
                // Query server for connected endpoints
                var command = new { Command = "LIST_ENDPOINTS" };
                string response = await SendCommandAsync(JsonSerializer.Serialize(command));

                try
                {
                    // First log the actual response for debugging
                    Logger.Log($"[ComputeServer] Endpoint list response: {response}");

                    var responseObj = JsonSerializer.Deserialize<JsonElement>(response);

                    if (responseObj.TryGetProperty("Status", out JsonElement statusElement) &&
                        statusElement.GetString() == "OK")
                    {
                        if (responseObj.TryGetProperty("Endpoints", out JsonElement endpointsElement))
                        {
                            // Process endpoints with state preservation
                            UpdateEndpointsFromResponse(endpointsElement);
                        }
                    }
                    else
                    {
                        // If we don't get an OK status or no Endpoints field, try the fallback endpoint command
                        await RefreshEndpointsFallbackAsync();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[ComputeServer] Error parsing endpoints response: {ex.Message}");
                    await RefreshEndpointsFallbackAsync();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[ComputeServer] Error refreshing endpoints: {ex.Message}");
            }
        }
        private void UpdateEndpointsFromResponse(JsonElement endpointsElement)
        {
            var newEndpoints = new List<ComputeEndpoint>();

            foreach (JsonElement endpointElement in endpointsElement.EnumerateArray())
            {
                try
                {
                    string name = endpointElement.GetProperty("Name").GetString();
                    string ip = endpointElement.GetProperty("EndpointIP").GetString();
                    int port = endpointElement.GetProperty("EndpointPort").GetInt32();

                    // Look for existing endpoint with the same name or IP
                    var existingEndpoint = ConnectedEndpoints.FirstOrDefault(e =>
                        e.Name == name || (e.IP == ip && e.Port == port));

                    ComputeEndpoint endpoint;

                    if (existingEndpoint != null)
                    {
                        // Keep the existing endpoint to preserve its connection state
                        endpoint = existingEndpoint;

                        // Update properties that might have changed
                        endpoint.Name = name;
                        endpoint.IP = ip;
                        endpoint.Port = port;
                    }
                    else
                    {
                        // Create new endpoint
                        endpoint = new ComputeEndpoint(name, ip, port);
                        endpoint.ParentServer = this;
                    }

                    // Update non-connection state properties
                    if (endpointElement.TryGetProperty("ConnectedAt", out var connectedAtElem))
                        endpoint.ConnectedAt = connectedAtElem.GetDateTime();

                    if (endpointElement.TryGetProperty("GpuEnabled", out var gpuElem))
                        endpoint.HasGPU = gpuElem.GetBoolean();

                    if (endpointElement.TryGetProperty("Status", out var statusElem))
                        endpoint.Status = statusElem.GetString();

                    if (endpointElement.TryGetProperty("CpuLoadPercent", out var cpuLoadElem))
                        endpoint.CpuLoad = cpuLoadElem.GetDouble();

                    newEndpoints.Add(endpoint);
                }
                catch (Exception ex)
                {
                    Logger.Log($"[ComputeServer] Error parsing endpoint info: {ex.Message}");
                }
            }

            // Replace list but preserve existing endpoints
            var updatedList = newEndpoints.ToList();

            // Find endpoints that disappeared and preserve them if they're connected
            foreach (var existingEndpoint in ConnectedEndpoints)
            {
                if (!newEndpoints.Any(e => e.Name == existingEndpoint.Name) && existingEndpoint.IsConnected)
                {
                    // This endpoint is connected but no longer reported by server
                    // Keep it for now to maintain connection
                    Logger.Log($"[ComputeServer] Endpoint {existingEndpoint.Name} connected but not in server list");
                    updatedList.Add(existingEndpoint);
                }
            }

            ConnectedEndpoints = updatedList;
            EndpointsUpdated?.Invoke(this, EventArgs.Empty);
        }
        
        
        // Fallback method if LIST_ENDPOINTS is not implemented on the server
        private async Task RefreshEndpointsFallbackAsync()
        {
            try
            {
                // Try alternative command to get endpoint info
                var command = new { Command = "GET_ENDPOINTS" };
                string response = await SendCommandAsync(JsonSerializer.Serialize(command));

                try
                {
                    var responseObj = JsonSerializer.Deserialize<JsonElement>(response);

                    if (responseObj.TryGetProperty("EndpointCount", out JsonElement countElement))
                    {
                        int endpointCount = countElement.GetInt32();

                        // Create dummy endpoints if we only have the count
                        var endpoints = new List<ComputeEndpoint>();

                        for (int i = 0; i < endpointCount; i++)
                        {
                            var endpoint = new ComputeEndpoint(
                                $"Endpoint-{i + 1}",
                                "Unknown", // We don't have the IP
                                0          // We don't have the port
                            );

                            endpoint.Status = "Connected";
                            endpoint.ParentServer = this;
                            endpoints.Add(endpoint);
                        }

                        // Update the list
                        ConnectedEndpoints = endpoints;
                        EndpointsUpdated?.Invoke(this, EventArgs.Empty);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[ComputeServer] Error in fallback endpoint refresh: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[ComputeServer] Fallback endpoint refresh failed: {ex.Message}");
            }
        }

        public void UpdateFromBeacon(BeaconMessage beacon)
        {
            Name = beacon.ServerName;
            EndpointPort = 7002; // Assume default endpoint port (should be included in beacon)
            HasGPU = beacon.GpuEnabled;
            LastSeen = DateTime.Now;
        }

        public void SetBusy(bool busy)
        {
            IsBusy = busy;
        }

        public async Task<bool> LaunchSimulationAsync(SimulationParameters parameters)
        {
            if (!IsConnected)
                return false;

            try
            {
                // Send simulation parameters to the server
                var command = new
                {
                    Command = "START_SIMULATION",
                    Parameters = parameters
                };

                string response = await SendCommandAsync(JsonSerializer.Serialize(command));
                var responseObj = JsonSerializer.Deserialize<JsonElement>(response);

                return responseObj.TryGetProperty("Status", out JsonElement statusElement) &&
                       statusElement.GetString() == "OK";
            }
            catch (Exception ex)
            {
                Logger.Log($"[ComputeServer] Error launching simulation on {Name}: {ex.Message}");
                return false;
            }
        }

        public async Task<SimulationExecutionStatus> GetSimulationStatusAsync()
        {
            if (!IsConnected)
                return new SimulationExecutionStatus { Status = "Disconnected" };

            try
            {
                var command = new { Command = "GET_SIMULATION_STATUS" };
                string response = await SendCommandAsync(JsonSerializer.Serialize(command));
                return JsonSerializer.Deserialize<SimulationExecutionStatus>(response);
            }
            catch (Exception ex)
            {
                Logger.Log($"[ComputeServer] Error getting simulation status from {Name}: {ex.Message}");
                return new SimulationExecutionStatus { Status = "Error", Message = ex.Message };
            }
        }
    }

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
        public DateTime ConnectedAt { get; set; }
        public string Status { get; set; } = "Unknown";
        public bool HasGPU { get; set; }
        public string HardwareInfo { get; set; }
        public double CpuLoad { get; set; }
        public string CurrentTask { get; set; } = "None";
        public ComputeServer ParentServer { get; set; }

        // Direct connection properties (for backward compatibility)
        public TcpClient Connection { get; set; }
        public NetworkStream Stream { get; private set; }

        public event EventHandler<bool> ConnectionStatusChanged;

        public ComputeEndpoint(string name, string ip, int port)
        {
            Name = name;
            IP = ip;
            Port = port;
            ConnectedAt = DateTime.Now;
        }
        private bool isReconnecting = false;
        private int reconnectAttempts = 0;
        private const int MaxReconnectAttempts = 3;
        /// <summary>
        /// Connect directly to the endpoint (legacy mode)
        /// </summary>
        public async Task ConnectAsync()
        {
            try
            {
                // If there's a parent server and it's connected, use it instead of direct connection
                if (ParentServer != null && ParentServer.IsConnected)
                {
                    Logger.Log($"[ComputeEndpoint] Connecting to {Name} through parent server");
                    IsConnected = true;
                    ConnectionStatusChanged?.Invoke(this, true);
                    return;
                }

                // Otherwise try direct connection (legacy mode)
                Logger.Log($"[ComputeEndpoint] Connecting directly to {Name} at {IP}:{Port}");
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
        /// <summary>
        /// Disconnect from the endpoint
        /// </summary>
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

        /// <summary>
        /// Send a command to the endpoint directly (legacy mode)
        /// </summary>
        public async Task<string> SendCommandAsync(string command)
        {
            // If we have a parent server, ALWAYS use it instead of direct connection
            if (ParentServer != null && ParentServer.IsConnected)
            {
                Logger.Log($"[ComputeEndpoint] Sending command to {Name} via parent server");
                return await SendCommandViaServerAsync(command);
            }

            // Only use direct connection if no parent server is available
            if (!IsConnected)
                throw new InvalidOperationException("Endpoint is not connected");

            try
            {
                Logger.Log($"[ComputeEndpoint] Sending command directly to {Name}");
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
        private async Task AttemptReconnectAsync()
        {
            if (isReconnecting) return;

            try
            {
                isReconnecting = true;
                reconnectAttempts = 0;

                while (reconnectAttempts < MaxReconnectAttempts && !IsConnected)
                {
                    reconnectAttempts++;
                    Logger.Log($"[ComputeEndpoint] Attempting to reconnect to {Name} (attempt {reconnectAttempts}/{MaxReconnectAttempts})");

                    try
                    {
                        // Clean up previous connection resources
                        Stream?.Dispose();
                        Connection?.Dispose();

                        // Try reconnecting through parent server first
                        if (ParentServer != null && ParentServer.IsConnected)
                        {
                            IsConnected = true;
                            ConnectionStatusChanged?.Invoke(this, true);
                            Logger.Log($"[ComputeEndpoint] Successfully reconnected to {Name} via parent server");
                            return;
                        }

                        // Otherwise try direct connection
                        Connection = new TcpClient();
                        var connectTask = Connection.ConnectAsync(IP, Port);
                        var timeoutTask = Task.Delay(5000); // 5 second timeout

                        await Task.WhenAny(connectTask, timeoutTask);

                        if (connectTask.IsCompleted && Connection.Connected)
                        {
                            Stream = Connection.GetStream();
                            IsConnected = true;
                            ConnectionStatusChanged?.Invoke(this, true);
                            Logger.Log($"[ComputeEndpoint] Successfully reconnected to {Name}");
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[ComputeEndpoint] Reconnection attempt {reconnectAttempts} to {Name} failed: {ex.Message}");
                    }

                    // Wait before next attempt
                    await Task.Delay(2000 * reconnectAttempts); // Increasing backoff
                }

                if (!IsConnected)
                {
                    Logger.Log($"[ComputeEndpoint] Failed to reconnect to {Name} after {MaxReconnectAttempts} attempts");
                }
            }
            finally
            {
                isReconnecting = false;
            }
        }
        /// <summary>
        /// Send command to the endpoint through the parent server
        /// </summary>
        public async Task<string> SendCommandViaServerAsync(string command)
        {
            if (ParentServer == null)
                throw new InvalidOperationException("Parent server is not assigned");

            if (!ParentServer.IsConnected)
                throw new InvalidOperationException("Parent server is not connected");

            try
            {
                // Send command to server that will forward to endpoint
                var forwardCommand = new
                {
                    Command = "FORWARD_TO_ENDPOINT",
                    EndpointName = Name,
                    EndpointIP = IP,
                    EndpointPort = Port,
                    ForwardedCommand = command
                };

                // Send the forward command to the parent server
                return await ParentServer.SendCommandAsync(JsonSerializer.Serialize(forwardCommand));
            }
            catch (Exception ex)
            {
                Logger.Log($"[ComputeEndpoint] Error sending command to {Name} via server: {ex.Message}");
                throw;
            }
        }
        /// <summary>
        /// Set the busy state of the endpoint
        /// </summary>
        public void SetBusy(bool busy)
        {
            IsBusy = busy;
        }

        /// <summary>
        /// Run diagnostics on the endpoint
        /// </summary>
        public async Task<string> RunDiagnosticsAsync()
        {
            try
            {
                var command = new { Command = "DIAGNOSTICS" };
                string commandJson = JsonSerializer.Serialize(command);

                // If we have a parent server, ALWAYS use it rather than trying direct connection
                if (ParentServer != null && ParentServer.IsConnected)
                {
                    Logger.Log($"[ComputeEndpoint] Running diagnostics via parent server for {Name}");
                    return await SendCommandViaServerAsync(commandJson);
                }
                else
                {
                    // Only use direct connection if no parent server is available
                    if (!IsConnected)
                    {
                        return "Error running diagnostics: Endpoint is not connected";
                    }

                    Logger.Log($"[ComputeEndpoint] Running diagnostics via direct connection for {Name}");
                    return await SendCommandAsync(commandJson);
                }
            }
            catch (Exception ex)
            {
                return $"Error running diagnostics: {ex.Message}";
            }
        }
    }

    /// <summary>
    /// Manager class for handling servers and endpoints in the compute cluster
    /// </summary>
    public class ComputeClusterManager
    {
        private List<ComputeServer> servers = new List<ComputeServer>();
        private UdpClient beaconListener;
        private CancellationTokenSource cts = new CancellationTokenSource();
        private int beaconPort = 7001;
        private Timer keepAliveTimer;
        private const int KeepAliveInterval = 5000; // 5 seconds
        private bool isRefreshing = false; // 5 seconds
        private TimeSpan serverTimeoutThreshold = TimeSpan.FromSeconds(30);
        public event EventHandler<ComputeServer> ServerDiscovered;
        public event EventHandler<ComputeServer> ServerStatusChanged;
        public event EventHandler<ComputeServer> ServerRemoved;
        public event EventHandler<ComputeEndpoint> EndpointStatusChanged;

        public List<ComputeServer> Servers => servers.ToList(); // Return a copy
        public bool IsRefreshing => isRefreshing;
        public ComputeClusterManager()
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
                    Logger.Log($"[ComputeClusterManager] Failed to bind to port {beaconPort}: {ex.Message}");
                    Logger.Log("[ComputeClusterManager] Trying to bind to any available port");
                    beaconListener.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
                }

                beaconListener.EnableBroadcast = true;

                Logger.Log("[ComputeClusterManager] Started server discovery service");

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
                            Logger.Log($"[ComputeClusterManager] Received invalid beacon format");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[ComputeClusterManager] Error receiving beacon: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[ComputeClusterManager] Discovery error: {ex.Message}");
            }
            finally
            {
                beaconListener?.Close();
                Logger.Log("[ComputeClusterManager] Server discovery service stopped");
            }
        }
        public void StopDiscovery()
        {
            Logger.Log("[ComputeClusterManager] Stopping discovery service");
            cts.Cancel();
            beaconListener?.Close();
        }

        private void ProcessBeaconMessage(BeaconMessage message, IPEndPoint endpoint)
        {
            try
            {
                // Use server IP from the beacon if available, otherwise use the sender's IP
                string serverIP = !string.IsNullOrEmpty(message.ServerIP) ? message.ServerIP : endpoint.Address.ToString();

                // Check if we already know this server
                var existingServer = servers.FirstOrDefault(s =>
                    s.IP == serverIP && s.Port == message.ServerPort);

                if (existingServer != null)
                {
                    // Update existing server
                    existingServer.UpdateFromBeacon(message);
                    ServerStatusChanged?.Invoke(this, existingServer);
                }
                else
                {
                    // Create new server
                    var newServer = new ComputeServer(message.ServerName, serverIP, message.ServerPort);
                    newServer.UpdateFromBeacon(message);

                    // Subscribe to server events
                    newServer.EndpointsUpdated += (sender, e) => {
                        ServerStatusChanged?.Invoke(this, newServer);
                    };

                    servers.Add(newServer);

                    Logger.Log($"[ComputeClusterManager] Discovered new server: {newServer.Name} at {newServer.IP}:{newServer.Port}");
                    ServerDiscovered?.Invoke(this, newServer);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[ComputeClusterManager] Error processing beacon message: {ex.Message}");
            }
        }
        private void KeepAliveTimerCallback(object state)
        {
            // Skip refresh if already in progress
            if (isRefreshing) return;

            try
            {
                isRefreshing = true;

                // Check for servers that haven't been seen in a while
                var now = DateTime.Now;

                for (int i = servers.Count - 1; i >= 0; i--)
                {
                    var server = servers[i];
                    if (now - server.LastSeen > serverTimeoutThreshold)
                    {
                        // Server timed out
                        Logger.Log($"[ComputeClusterManager] Server timed out: {server.Name} ({server.IP}:{server.Port})");
                        server.Disconnect();
                        servers.RemoveAt(i);
                        ServerRemoved?.Invoke(this, server);
                    }
                }

                // Send keep-alive to connected servers and refresh endpoint information
                foreach (var server in servers.Where(s => s.IsConnected))
                {
                    try
                    {
                        // Use a separate task to avoid blocking the timer
                        Task.Run(async () =>
                        {
                            try
                            {
                                // Send a simple ping command
                                var command = new { Command = "PING" };
                                string response = await server.SendCommandAsync(JsonSerializer.Serialize(command));

                                // Check if response is valid
                                try
                                {
                                    var responseObj = JsonSerializer.Deserialize<JsonElement>(response);
                                    if (responseObj.TryGetProperty("Status", out JsonElement statusElement) &&
                                        statusElement.GetString() == "OK")
                                    {
                                        // Update last seen time
                                        server.LastSeen = DateTime.Now;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Logger.Log($"[ComputeClusterManager] Error parsing ping response from {server.Name}: {ex.Message}");
                                }

                                // Refresh endpoint list (less frequently)
                                if (DateTime.Now.Second % 10 == 0) // Every 10 seconds
                                {
                                    await server.RefreshEndpointsAsync();
                                }
                            }
                            catch (OperationCanceledException)
                            {
                                // Timeout occurred, but don't disconnect yet
                                Logger.Log($"[ComputeClusterManager] Keep-alive timeout for {server.Name}");
                            }
                            catch (Exception ex)
                            {
                                Logger.Log($"[ComputeClusterManager] Error in keep-alive for {server.Name}: {ex.Message}");

                                // Set server as disconnected but don't remove it yet
                                if (server.IsConnected)
                                {
                                    server.Disconnect();
                                    ServerStatusChanged?.Invoke(this, server);
                                }
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[ComputeClusterManager] Error scheduling keep-alive for {server.Name}: {ex.Message}");
                        server.Disconnect();
                        ServerStatusChanged?.Invoke(this, server);
                    }
                }
            }
            finally
            {
                isRefreshing = false;
            }
        }
        public async Task ConnectToServerAsync(ComputeServer server)
        {
            isRefreshing = true; // Prevent UI refresh during connection
            try
            {
                // Connect to the server first
                await server.ConnectAsync();
                ServerStatusChanged?.Invoke(this, server);

                // After connecting to server, wait for endpoints to be populated
                await server.RefreshEndpointsAsync();

                // Automatically connect to all endpoints of the server
                if (server.ConnectedEndpoints.Count > 0)
                {
                    Logger.Log($"[ComputeClusterManager] Connecting to {server.ConnectedEndpoints.Count} endpoints on {server.Name}...");

                    // Connect to endpoints one by one with short delays to avoid overwhelming the server
                    foreach (var endpoint in server.ConnectedEndpoints)
                    {
                        try
                        {
                            Logger.Log($"[ComputeClusterManager] Automatically connecting to endpoint {endpoint.Name}...");
                            endpoint.ParentServer = server; // Ensure parent server is set
                            await endpoint.ConnectAsync();
                            EndpointStatusChanged?.Invoke(this, endpoint);
                            await Task.Delay(300); // Small delay between endpoint connections
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"[ComputeClusterManager] Error connecting to endpoint {endpoint.Name}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[ComputeClusterManager] Error connecting to server {server.Name}: {ex.Message}");
            }
            finally
            {
                isRefreshing = false;
            }
        }
        public void DisconnectServer(ComputeServer server)
        {
            isRefreshing = true; // Prevent UI refresh during disconnection
            try
            {
                // Disconnect all endpoints first
                foreach (var endpoint in server.ConnectedEndpoints.ToList())
                {
                    try
                    {
                        endpoint.Disconnect();
                        EndpointStatusChanged?.Invoke(this, endpoint);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[ComputeClusterManager] Error disconnecting endpoint {endpoint.Name}: {ex.Message}");
                    }
                }

                // Then disconnect the server
                server.Disconnect();
                ServerStatusChanged?.Invoke(this, server);
            }
            finally
            {
                isRefreshing = false;
            }
        }
        public async Task ConnectToEndpointAsync(ComputeEndpoint endpoint)
        {
            isRefreshing = true; // Prevent UI refresh during connection
            try
            {
                // If parent server is not connected, connect to it first
                if (endpoint.ParentServer != null && !endpoint.ParentServer.IsConnected)
                {
                    await ConnectToServerAsync(endpoint.ParentServer);
                }

                // Now connect to the endpoint
                await endpoint.ConnectAsync();
                EndpointStatusChanged?.Invoke(this, endpoint);
            }
            catch (Exception ex)
            {
                Logger.Log($"[ComputeClusterManager] Error connecting to endpoint {endpoint.Name}: {ex.Message}");
            }
            finally
            {
                isRefreshing = false;
            }
        }
        public void DisconnectEndpoint(ComputeEndpoint endpoint)
        {
            isRefreshing = true; // Prevent UI refresh during disconnection
            try
            {
                endpoint.Disconnect();
                EndpointStatusChanged?.Invoke(this, endpoint);
            }
            finally
            {
                isRefreshing = false;
            }
        }
        public async Task RestartServerAsync(ComputeServer server)
        {
            if (!server.IsConnected)
                await server.ConnectAsync();

            // Send restart command
            var command = new { Command = "RESTART" };
            await server.SendCommandAsync(JsonSerializer.Serialize(command));

            // The server will disconnect and reconnect, but we'll monitor it via beacon
            server.Disconnect();
            ServerStatusChanged?.Invoke(this, server);
        }
        public async Task RestartEndpointAsync(ComputeEndpoint endpoint)
        {
            if (!endpoint.IsConnected)
                await endpoint.ConnectAsync();

            try
            {
                // Send restart command
                var command = new { Command = "RESTART" };
                await endpoint.SendCommandAsync(JsonSerializer.Serialize(command));

                // The endpoint will disconnect and reconnect
                endpoint.Disconnect();
                EndpointStatusChanged?.Invoke(this, endpoint);
            }
            catch (Exception ex)
            {
                Logger.Log($"[ComputeClusterManager] Error restarting endpoint {endpoint.Name}: {ex.Message}");
            }
        }

        public async Task ShutdownServerAsync(ComputeServer server)
        {
            if (!server.IsConnected)
                await server.ConnectAsync();

            // Send shutdown command
            var command = new { Command = "SHUTDOWN" };
            await server.SendCommandAsync(JsonSerializer.Serialize(command));

            // The server will shutdown and stop sending beacons
            server.Disconnect();
            ServerStatusChanged?.Invoke(this, server);
        }
        public async Task ShutdownEndpointAsync(ComputeEndpoint endpoint)
        {
            if (!endpoint.IsConnected)
                await endpoint.ConnectAsync();

            try
            {
                // Send shutdown command
                var command = new { Command = "SHUTDOWN" };
                await endpoint.SendCommandAsync(JsonSerializer.Serialize(command));

                // The endpoint will shut down
                endpoint.Disconnect();
                EndpointStatusChanged?.Invoke(this, endpoint);
            }
            catch (Exception ex)
            {
                Logger.Log($"[ComputeClusterManager] Error shutting down endpoint {endpoint.Name}: {ex.Message}");
            }
        }
        public async Task<string> RunServerDiagnosticsAsync(ComputeServer server)
        {
            if (!server.IsConnected)
                await server.ConnectAsync();

            server.SetBusy(true);
            ServerStatusChanged?.Invoke(this, server);

            try
            {
                // Send diagnostics command
                var command = new { Command = "DIAGNOSTICS" };
                var result = await server.SendCommandAsync(JsonSerializer.Serialize(command));
                return result;
            }
            finally
            {
                server.SetBusy(false);
                ServerStatusChanged?.Invoke(this, server);
            }
        }

        public async Task<string> RunEndpointDiagnosticsAsync(ComputeEndpoint endpoint)
        {
            try
            {
                // Set busy state
                endpoint.SetBusy(true);
                EndpointStatusChanged?.Invoke(this, endpoint);

                Logger.Log($"[ComputeClusterManager] Running diagnostics on endpoint {endpoint.Name}...");

                // Make sure we have a parent server connection
                if (endpoint.ParentServer == null || !endpoint.ParentServer.IsConnected)
                {
                    Logger.Log($"[ComputeClusterManager] Parent server is not connected for endpoint {endpoint.Name}");
                    return "Error running diagnostics: Parent server is not connected";
                }

                // Use the server to run diagnostics on the endpoint
                var diagnosticsCommand = new
                {
                    Command = "ENDPOINT_DIAGNOSTICS",
                    EndpointName = endpoint.Name,
                    EndpointIP = endpoint.IP,
                    EndpointPort = endpoint.Port
                };

                // Send command to the server and await response
                string responseJson = await endpoint.ParentServer.SendCommandAsync(
                    JsonSerializer.Serialize(diagnosticsCommand));

                // Parse the response
                try
                {
                    var responseObj = JsonSerializer.Deserialize<JsonElement>(responseJson);

                    // If we have a DiagnosticsResult field, return it
                    if (responseObj.TryGetProperty("DiagnosticsResult", out var resultElement))
                    {
                        string diagnosticsResult = resultElement.GetString();
                        return diagnosticsResult;
                    }

                    // Otherwise, return the full response
                    return responseJson;
                }
                catch
                {
                    // If parsing fails, just return the raw response
                    return responseJson;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[ComputeClusterManager] Error running diagnostics on endpoint {endpoint.Name}: {ex.Message}");
                return $"Error running diagnostics: {ex.Message}";
            }
            finally
            {
                endpoint.SetBusy(false);
                EndpointStatusChanged?.Invoke(this, endpoint);
            }
        }
        public async Task<bool> LaunchSimulationAsync(ComputeServer server, SimulationParameters parameters)
        {
            if (!server.IsConnected)
                await server.ConnectAsync();

            return await server.LaunchSimulationAsync(parameters);
        }

        public async Task<bool> LaunchSimulationOnAllServersAsync(SimulationParameters parameters)
        {
            bool allSucceeded = true;

            foreach (var server in servers.Where(s => s.IsConnected))
            {
                try
                {
                    bool success = await server.LaunchSimulationAsync(parameters);
                    if (!success)
                        allSucceeded = false;
                }
                catch
                {
                    allSucceeded = false;
                }
            }

            return allSucceeded;
        }

        public void AddServerManually(string name, string ip, int port)
        {
            // Check if server already exists
            var existingServer = servers.FirstOrDefault(s => s.IP == ip && s.Port == port);
            if (existingServer != null)
            {
                Logger.Log($"[ComputeClusterManager] Server at {ip}:{port} already exists");
                return;
            }

            var newServer = new ComputeServer(name, ip, port);

            // Subscribe to server events
            newServer.EndpointsUpdated += (sender, e) => {
                ServerStatusChanged?.Invoke(this, newServer);
            };

            servers.Add(newServer);
            Logger.Log($"[ComputeClusterManager] Manually added server: {name} at {ip}:{port}");
            ServerDiscovered?.Invoke(this, newServer);
        }

        public void SaveServers(string filePath)
        {
            var serversData = servers.Select(s => new
            {
                s.Name,
                s.IP,
                s.Port,
                s.EndpointPort,
                s.HasGPU,
                s.AcceleratorType
            }).ToList();

            string json = JsonSerializer.Serialize(serversData, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(filePath, json);
            Logger.Log($"[ComputeClusterManager] Saved {servers.Count} servers to {filePath}");
        }

        public void LoadServers(string filePath)
        {
            if (!File.Exists(filePath))
            {
                Logger.Log($"[ComputeClusterManager] Server file not found: {filePath}");
                return;
            }

            try
            {
                string json = File.ReadAllText(filePath);
                var serversData = JsonSerializer.Deserialize<List<ServerData>>(json);

                foreach (var data in serversData)
                {
                    // Skip if server already exists
                    if (servers.Any(s => s.IP == data.IP && s.Port == data.Port))
                        continue;

                    var newServer = new ComputeServer(data.Name, data.IP, data.Port);
                    newServer.EndpointPort = data.EndpointPort;
                    newServer.HasGPU = data.HasGPU;
                    newServer.AcceleratorType = data.AcceleratorType;

                    // Subscribe to server events
                    newServer.EndpointsUpdated += (sender, e) => {
                        ServerStatusChanged?.Invoke(this, newServer);
                    };

                    servers.Add(newServer);
                    ServerDiscovered?.Invoke(this, newServer);
                }

                Logger.Log($"[ComputeClusterManager] Loaded {serversData.Count} servers from {filePath}");
            }
            catch (Exception ex)
            {
                Logger.Log($"[ComputeClusterManager] Error loading servers: {ex.Message}");
                throw;
            }
        }

        private class ServerData
        {
            public string Name { get; set; }
            public string IP { get; set; }
            public int Port { get; set; }
            public int EndpointPort { get; set; }
            public bool HasGPU { get; set; }
            public string AcceleratorType { get; set; }
        }
    }

    /// <summary>
    /// Parameters for simulation execution
    /// </summary>
    public class SimulationParameters
    {
        public string SimulationName { get; set; }
        public string DataPath { get; set; }
        public int Iterations { get; set; }
        public bool UseGPU { get; set; }
        public Dictionary<string, object> AdditionalParams { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Status information for a running simulation
    /// </summary>
    public class SimulationExecutionStatus
    {
        public string Status { get; set; } // Running, Completed, Error, etc.
        public int CompletionPercentage { get; set; }
        public string Message { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public int ActiveEndpoints { get; set; }
        public Dictionary<string, object> Results { get; set; }
    }

    /// <summary>
    /// Form for managing the compute cluster
    /// </summary>
    public class ComputeClusterForm : KryptonForm
    {
        private ComputeClusterManager manager = new ComputeClusterManager();
        private List<ComputeServer> selectedServers = new List<ComputeServer>();
        private List<ComputeEndpoint> selectedEndpoints = new List<ComputeEndpoint>();
        private Dictionary<ComputeServer, ServerNodeControl> serverControls = new Dictionary<ComputeServer, ServerNodeControl>();
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
        private KryptonButton btnLaunchSimulation;
        private System.Windows.Forms.Timer refreshTimer;

        public ComputeClusterForm(MainForm mainForm)
        {
            this.mainForm = mainForm;
            this.Icon = Properties.Resources.favicon;
            InitializeComponent();

            // Start discovery
            Task.Run(async () => await manager.StartDiscoveryAsync());

            // Set up event handlers
            manager.ServerDiscovered += Manager_ServerDiscovered;
            manager.ServerStatusChanged += Manager_ServerStatusChanged;
            manager.ServerRemoved += Manager_ServerRemoved;
            manager.EndpointStatusChanged += Manager_EndpointStatusChanged;

            // Set up refresh timer to update the UI
            refreshTimer = new System.Windows.Forms.Timer();
            refreshTimer.Interval = 1000; // 1 second
            refreshTimer.Tick += RefreshTimer_Tick;
            refreshTimer.Start();

            LogMessage("Compute Cluster Manager started");
            LogMessage("Scanning for available compute servers...");
        }

        private void InitializeComponent()
        {
            // Set form properties
            this.Text = "Compute Cluster Manager";
            this.Size = new Size(1000, 700);
            this.MinimumSize = new Size(900, 650);
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
            btnAddManually = CreateButton("Add Server");
            btnSave = CreateButton("Save List");
            btnLoad = CreateButton("Load List");
            btnLaunchSimulation = CreateButton("Launch Simulation");
            btnLaunchSimulation.Width = 140;

            buttonPanel.Controls.AddRange(new Control[]
            {
                btnConnect, btnDisconnect, btnRestart, btnShutdown,
                btnRunDiagnostics, btnAddManually, btnSave, btnLoad, btnLaunchSimulation
            });

            // Middle panel for topology map
            KryptonGroupBox topologyGroup = new KryptonGroupBox();
            topologyGroup.Dock = DockStyle.Fill;
            topologyGroup.Text = "Compute Cluster Topology";
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

            // Use a panel that will draw lines between nodes
            var topologyHostPanel = new ConnectionsPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(20, 20, 20),
                AutoScroll = true
            };
            topologyHostPanel.Controls.Add(topologyPanel);

            topologyGroup.Panel.Controls.Add(topologyHostPanel);

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
            btnLaunchSimulation.Click += btnLaunchSimulation_Click;

            // Initially disable buttons until nodes are selected
            btnConnect.Enabled = false;
            btnDisconnect.Enabled = false;
            btnRestart.Enabled = false;
            btnShutdown.Enabled = false;
            btnRunDiagnostics.Enabled = false;
            btnLaunchSimulation.Enabled = false;
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
            // Skip refresh if connect/disconnect operations are in progress
            if (manager.IsRefreshing) return;

            // Keep track of the currently selected nodes
            var selectedServerIds = selectedServers.Select(s => s.IP + ":" + s.Port).ToList();
            var selectedEndpointIds = selectedEndpoints.Select(x => x.Name).ToList();

            // Update the node controls
            foreach (var kvp in serverControls)
            {
                kvp.Value.UpdateDisplay();
            }

            foreach (var kvp in endpointControls)
            {
                kvp.Value.UpdateDisplay();
            }

            // Update button states based on selection
            UpdateButtonStates();

            // Refresh the panel to update connection lines
            topologyPanel.Parent.Refresh();
        }
        private void UpdateButtonStates()
        {
            bool hasServerSelected = selectedServers.Count > 0;
            bool hasEndpointSelected = selectedEndpoints.Count > 0;
            bool hasConnectedServer = selectedServers.Any(s => s.IsConnected);
            bool hasConnectedEndpoint = selectedEndpoints.Any(e => e.IsConnected);

            btnConnect.Enabled = (hasServerSelected && selectedServers.Any(s => !s.IsConnected)) ||
                              (hasEndpointSelected && selectedEndpoints.Any(e => !e.IsConnected));
            btnDisconnect.Enabled = hasConnectedServer || hasConnectedEndpoint;
            btnRestart.Enabled = hasConnectedServer || hasConnectedEndpoint;
            btnShutdown.Enabled = hasConnectedServer || hasConnectedEndpoint;
            btnRunDiagnostics.Enabled = hasConnectedServer || hasConnectedEndpoint;
            btnLaunchSimulation.Enabled = hasConnectedServer;
        }

        private void Manager_ServerDiscovered(object sender, ComputeServer server)
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(() => Manager_ServerDiscovered(sender, server)));
                return;
            }

            // Create a visual control for the server
            var control = new ServerNodeControl(server);
            control.Selected += ServerNodeControl_Selected;
            control.DoubleClicked += ServerNodeControl_DoubleClicked;

            serverControls[server] = control;
            topologyPanel.Controls.Add(control);

            LogMessage($"Discovered server: {server.Name} ({server.IP}:{server.Port})");

            // Subscribe to the server's endpoints updated event
            server.EndpointsUpdated += (s, e) => UpdateServerEndpoints(server);
        }

        private void UpdateServerEndpoints(ComputeServer server)
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(() => UpdateServerEndpoints(server)));
                return;
            }

            // Update endpoint controls for this server
            foreach (var endpoint in server.ConnectedEndpoints)
            {
                if (!endpointControls.ContainsKey(endpoint))
                {
                    var control = new EndpointControl(endpoint);
                    control.Selected += EndpointControl_Selected;
                    endpointControls[endpoint] = control;
                    topologyPanel.Controls.Add(control);
                }
            }

            // Remove endpoints that are no longer connected
            var endpointsToRemove = endpointControls.Keys
                .Where(e => e.ParentServer == server && !server.ConnectedEndpoints.Contains(e))
                .ToList();

            foreach (var endpoint in endpointsToRemove)
            {
                var control = endpointControls[endpoint];
                topologyPanel.Controls.Remove(control);
                endpointControls.Remove(endpoint);
                selectedEndpoints.Remove(endpoint);
                control.Dispose();
            }

            // Refresh display
            serverControls[server].UpdateDisplay();
            topologyPanel.Parent.Refresh();
        }

        private void Manager_ServerStatusChanged(object sender, ComputeServer server)
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(() => Manager_ServerStatusChanged(sender, server)));
                return;
            }

            if (serverControls.TryGetValue(server, out var control))
            {
                control.UpdateDisplay();
            }
        }

        private void Manager_ServerRemoved(object sender, ComputeServer server)
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(() => Manager_ServerRemoved(sender, server)));
                return;
            }

            if (serverControls.TryGetValue(server, out var control))
            {
                topologyPanel.Controls.Remove(control);
                serverControls.Remove(server);
                selectedServers.Remove(server);
                control.Dispose();
            }

            // Also remove all endpoints connected to this server
            var endpointsToRemove = endpointControls.Keys
                .Where(e => e.ParentServer == server)
                .ToList();

            foreach (var endpoint in endpointsToRemove)
            {
                var epControl = endpointControls[endpoint];
                topologyPanel.Controls.Remove(epControl);
                endpointControls.Remove(endpoint);
                selectedEndpoints.Remove(endpoint);
                epControl.Dispose();
            }

            LogMessage($"Server removed: {server.Name} ({server.IP}:{server.Port})");
        }

        private void Manager_EndpointStatusChanged(object sender, ComputeEndpoint endpoint)
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(() => Manager_EndpointStatusChanged(sender, endpoint)));
                return;
            }

            if (endpointControls.TryGetValue(endpoint, out var control))
            {
                control.UpdateDisplay();
            }
        }

        private void ServerNodeControl_Selected(object sender, EventArgs args)
        {
            var control = sender as ServerNodeControl;
            if (control != null)
            {
                // Check if Ctrl is pressed for multi-select
                bool ctrlPressed = (Control.ModifierKeys & Keys.Control) == Keys.Control;

                if (!ctrlPressed)
                {
                    // Deselect all other nodes
                    foreach (var kvp in serverControls)
                    {
                        if (kvp.Value != control)
                        {
                            kvp.Value.IsSelected = false;
                        }
                    }
                    foreach (var kvp in endpointControls)
                    {
                        kvp.Value.IsSelected = false;
                    }
                    selectedServers.Clear();
                    selectedEndpoints.Clear();
                }

                // Toggle selection of clicked server
                if (control.IsSelected)
                {
                    selectedServers.Add(control.Server);
                }
                else
                {
                    selectedServers.Remove(control.Server);
                }

                UpdateButtonStates();
            }
        }

        private void ServerNodeControl_DoubleClicked(object sender, EventArgs args)
        {
            var control = sender as ServerNodeControl;
            if (control != null && !control.Server.IsConnected)
            {
                // Auto-connect on double-click
                Task.Run(async () =>
                {
                    try
                    {
                        LogMessage($"Connecting to {control.Server.Name}...");
                        await manager.ConnectToServerAsync(control.Server);
                        LogMessage($"Connected to {control.Server.Name} successfully");
                    }
                    catch (Exception ex)
                    {
                        LogMessage($"Failed to connect to {control.Server.Name}: {ex.Message}");
                    }
                });
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
                    // Deselect all other nodes
                    foreach (var kvp in serverControls)
                    {
                        kvp.Value.IsSelected = false;
                    }
                    foreach (var kvp in endpointControls)
                    {
                        if (kvp.Value != control)
                        {
                            kvp.Value.IsSelected = false;
                        }
                    }
                    selectedServers.Clear();
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

                UpdateButtonStates();
            }
        }
        private async void btnConnect_Click(object sender, EventArgs e)
        {
            // Disable button during connection to prevent multiple clicks
            btnConnect.Enabled = false;

            try
            {
                // Connect to selected servers
                foreach (var server in selectedServers.ToList())
                {
                    if (!server.IsConnected)
                    {
                        try
                        {
                            LogMessage($"Connecting to server {server.Name}...");
                            await manager.ConnectToServerAsync(server);
                            LogMessage($"Connected to server {server.Name} successfully");
                        }
                        catch (Exception ex)
                        {
                            LogMessage($"Failed to connect to server {server.Name}: {ex.Message}");
                        }
                    }
                }

                // Connect to selected endpoints
                foreach (var endpoint in selectedEndpoints.ToList())
                {
                    if (!endpoint.IsConnected)
                    {
                        try
                        {
                            LogMessage($"Connecting to endpoint {endpoint.Name}...");
                            await manager.ConnectToEndpointAsync(endpoint);
                            LogMessage($"Connected to endpoint {endpoint.Name} successfully");
                        }
                        catch (Exception ex)
                        {
                            LogMessage($"Failed to connect to endpoint {endpoint.Name}: {ex.Message}");
                        }
                    }
                }
            }
            finally
            {
                btnConnect.Enabled = true;
                UpdateButtonStates();
            }
        }
        private void btnDisconnect_Click(object sender, EventArgs e)
        {
            // Disconnect selected servers
            foreach (var server in selectedServers.ToList().Where(s => s.IsConnected))
            {
                try
                {
                    LogMessage($"Disconnecting from server {server.Name}...");
                    manager.DisconnectServer(server);
                    LogMessage($"Disconnected from server {server.Name}");
                }
                catch (Exception ex)
                {
                    LogMessage($"Error disconnecting from server {server.Name}: {ex.Message}");
                }
            }

            // Disconnect selected endpoints
            foreach (var endpoint in selectedEndpoints.ToList().Where(x => x.IsConnected))
            {
                try
                {
                    LogMessage($"Disconnecting from endpoint {endpoint.Name}...");
                    endpoint.Disconnect();
                    LogMessage($"Disconnected from endpoint {endpoint.Name}");
                }
                catch (Exception ex)
                {
                    LogMessage($"Error disconnecting from endpoint {endpoint.Name}: {ex.Message}");
                }
            }
        }

        private async void btnRestart_Click(object sender, EventArgs e)
        {
            if (selectedServers.Count > 0)
            {
                btnRestart.Enabled = false;

                foreach (var server in selectedServers.ToList().Where(s => s.IsConnected))
                {
                    try
                    {
                        LogMessage($"Restarting server {server.Name}...");
                        await manager.RestartServerAsync(server);
                        LogMessage($"Restart command sent to server {server.Name}");
                    }
                    catch (Exception ex)
                    {
                        LogMessage($"Failed to restart server {server.Name}: {ex.Message}");
                    }
                }

                btnRestart.Enabled = true;
            }
            else if (selectedEndpoints.Count > 0)
            {
                btnRestart.Enabled = false;

                foreach (var endpoint in selectedEndpoints.ToList())
                {
                    try
                    {
                        LogMessage($"Restarting endpoint {endpoint.Name}...");
                        var command = new { Command = "RESTART" };
                        await endpoint.SendCommandAsync(JsonSerializer.Serialize(command));
                        LogMessage($"Restart command sent to endpoint {endpoint.Name}");
                    }
                    catch (Exception ex)
                    {
                        LogMessage($"Failed to restart endpoint {endpoint.Name}: {ex.Message}");
                    }
                }

                btnRestart.Enabled = true;
            }
        }

        private async void btnShutdown_Click(object sender, EventArgs e)
        {
            string nodeType = selectedServers.Count > 0 ? "servers" : "endpoints";
            int nodeCount = selectedServers.Count > 0 ? selectedServers.Count : selectedEndpoints.Count;

            // Confirm shutdown
            DialogResult result = MessageBox.Show(
                $"Are you sure you want to shut down {nodeCount} {nodeType}?",
                "Confirm Shutdown",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (result != DialogResult.Yes)
                return;

            btnShutdown.Enabled = false;

            if (selectedServers.Count > 0)
            {
                foreach (var server in selectedServers.ToList().Where(s => s.IsConnected))
                {
                    try
                    {
                        LogMessage($"Shutting down server {server.Name}...");
                        await manager.ShutdownServerAsync(server);
                        LogMessage($"Shutdown command sent to server {server.Name}");
                    }
                    catch (Exception ex)
                    {
                        LogMessage($"Failed to shut down server {server.Name}: {ex.Message}");
                    }
                }
            }
            else if (selectedEndpoints.Count > 0)
            {
                foreach (var endpoint in selectedEndpoints.ToList())
                {
                    try
                    {
                        LogMessage($"Shutting down endpoint {endpoint.Name}...");
                        var command = new { Command = "SHUTDOWN" };
                        await endpoint.SendCommandAsync(JsonSerializer.Serialize(command));
                        LogMessage($"Shutdown command sent to endpoint {endpoint.Name}");
                    }
                    catch (Exception ex)
                    {
                        LogMessage($"Failed to shut down endpoint {endpoint.Name}: {ex.Message}");
                    }
                }
            }

            btnShutdown.Enabled = true;
        }

        private async void btnRunDiagnostics_Click(object sender, EventArgs e)
        {
            btnRunDiagnostics.Enabled = false;

            if (selectedServers.Count > 0)
            {
                foreach (var server in selectedServers.ToList().Where(s => s.IsConnected))
                {
                    try
                    {
                        LogMessage($"Running diagnostics on server {server.Name}...");
                        string diagnosticResult = await manager.RunServerDiagnosticsAsync(server);
                        LogMessage($"Diagnostics for server {server.Name}:");
                        LogMessage(diagnosticResult);
                    }
                    catch (Exception ex)
                    {
                        LogMessage($"Failed to run diagnostics on server {server.Name}: {ex.Message}");
                    }
                }
            }
            else if (selectedEndpoints.Count > 0)
            {
                foreach (var endpoint in selectedEndpoints.ToList())
                {
                    try
                    {
                        LogMessage($"Running diagnostics on endpoint {endpoint.Name}...");
                        // Use the manager's method to run diagnostics on endpoints
                        string diagnosticResult = await manager.RunEndpointDiagnosticsAsync(endpoint);
                        LogMessage($"Diagnostics for endpoint {endpoint.Name}:");
                        LogMessage(diagnosticResult);
                    }
                    catch (Exception ex)
                    {
                        LogMessage($"Failed to run diagnostics on endpoint {endpoint.Name}: {ex.Message}");
                    }
                }
            }

            btnRunDiagnostics.Enabled = true;
        }
        private void btnAddManually_Click(object sender, EventArgs e)
        {
            // Show dialog to add server manually
            using (var dialog = new AddServerDialog())
            {
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    string name = dialog.ServerName;
                    string ip = dialog.ServerIP;
                    int port = dialog.ServerPort;

                    manager.AddServerManually(name, ip, port);
                    LogMessage($"Manually added server: {name} ({ip}:{port})");
                }
            }
        }

        private void btnSave_Click(object sender, EventArgs e)
        {
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*";
                dialog.DefaultExt = "json";
                dialog.Title = "Save Compute Servers";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        manager.SaveServers(dialog.FileName);
                        LogMessage($"Servers saved to {dialog.FileName}");
                    }
                    catch (Exception ex)
                    {
                        LogMessage($"Error saving servers: {ex.Message}");
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
                dialog.Title = "Load Compute Servers";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        manager.LoadServers(dialog.FileName);
                        LogMessage($"Servers loaded from {dialog.FileName}");
                    }
                    catch (Exception ex)
                    {
                        LogMessage($"Error loading servers: {ex.Message}");
                    }
                }
            }
        }

        private async void btnLaunchSimulation_Click(object sender, EventArgs e)
        {
            if (selectedServers.Count == 0)
                return;

            using (var dialog = new SimulationParametersDialog())
            {
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    var parameters = dialog.Parameters;

                    btnLaunchSimulation.Enabled = false;
                    LogMessage($"Launching simulation '{parameters.SimulationName}' on selected servers...");

                    foreach (var server in selectedServers.Where(s => s.IsConnected))
                    {
                        try
                        {
                            bool success = await manager.LaunchSimulationAsync(server, parameters);
                            if (success)
                                LogMessage($"Simulation launched successfully on {server.Name}");
                            else
                                LogMessage($"Failed to launch simulation on {server.Name}");
                        }
                        catch (Exception ex)
                        {
                            LogMessage($"Error launching simulation on {server.Name}: {ex.Message}");
                        }
                    }

                    btnLaunchSimulation.Enabled = true;
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

            // Disconnect all servers
            foreach (var server in manager.Servers.Where(s => s.IsConnected))
            {
                try
                {
                    manager.DisconnectServer(server);
                }
                catch
                {
                    // Ignore errors on shutdown
                }
            }

            base.OnFormClosing(e);
        }
    }

    /// <summary>
    /// Panel that draws connection lines between server and endpoint controls
    /// </summary>
    public class ConnectionsPanel : Panel
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            // Get all server and endpoint controls
            var childControls = Controls.Count > 0 ? Controls[0].Controls : null;
            if (childControls == null) return;

            // Draw connections between servers and endpoints
            using (var pen = new Pen(Color.FromArgb(80, 80, 80), 1))
            {
                pen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dot;

                foreach (Control control in childControls)
                {
                    if (control is EndpointControl epControl && epControl.Endpoint.ParentServer != null)
                    {
                        foreach (Control serverControl in childControls)
                        {
                            if (serverControl is ServerNodeControl srvControl &&
                                srvControl.Server == epControl.Endpoint.ParentServer)
                            {
                                // Calculate positions for line drawing
                                Point serverCenter = new Point(
                                    serverControl.Left + serverControl.Width / 2,
                                    serverControl.Top + serverControl.Height / 2);

                                Point endpointCenter = new Point(
                                    control.Left + control.Width / 2,
                                    control.Top + control.Height / 2);

                                // Draw connection line
                                e.Graphics.DrawLine(pen, serverCenter, endpointCenter);

                                // Draw direction arrow
                                DrawArrow(e.Graphics, pen, serverCenter, endpointCenter);

                                break;
                            }
                        }
                    }
                }
            }
        }

        private void DrawArrow(Graphics g, Pen pen, Point start, Point end)
        {
            const float arrowSize = 8.0f;

            // Calculate arrow direction
            float dx = end.X - start.X;
            float dy = end.Y - start.Y;
            float length = (float)Math.Sqrt(dx * dx + dy * dy);

            if (length < 1) return; // Avoid division by zero

            dx /= length;
            dy /= length;

            // Calculate arrow points
            Point arrowPoint = new Point(
                (int)(end.X - dx * arrowSize * 2),
                (int)(end.Y - dy * arrowSize * 2));

            PointF pt1 = new PointF(
                arrowPoint.X + arrowSize * dy,
                arrowPoint.Y - arrowSize * dx);

            PointF pt2 = new PointF(
                arrowPoint.X - arrowSize * dy,
                arrowPoint.Y + arrowSize * dx);

            // Draw arrow
            using (var arrowPen = new Pen(Color.FromArgb(120, 120, 120), 1))
            {
                g.DrawLine(arrowPen, end, pt1);
                g.DrawLine(arrowPen, end, pt2);
            }
        }
    }

    /// <summary>
    /// Control for displaying a server node in the topology
    /// </summary>
    public class ServerNodeControl : Panel
    {
        public ComputeServer Server { get; }
        public bool IsSelected { get; set; }

        private Label lblName;
        private Label lblStatus;
        private Label lblEndpoints;
        private PictureBox statusIcon;

        public event EventHandler Selected;
        public event EventHandler DoubleClicked;

        public ServerNodeControl(ComputeServer server)
        {
            Server = server;

            // Set up visual appearance
            this.Size = new Size(180, 80);
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

            lblName = new Label
            {
                Text = server.Name,
                Location = new Point(25, 5),
                AutoSize = false,
                Size = new Size(150, 20),
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                ForeColor = Color.White
            };

            lblStatus = new Label
            {
                Text = "Disconnected",
                Location = new Point(5, 30),
                AutoSize = false,
                Size = new Size(170, 20),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.Gray
            };

            lblEndpoints = new Label
            {
                Text = "Endpoints: 0",
                Location = new Point(5, 55),
                AutoSize = false,
                Size = new Size(170, 20),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.Silver
            };

            this.Controls.AddRange(new Control[] { statusIcon, lblName, lblStatus, lblEndpoints });

            // Add click and double-click handlers
            this.Click += ServerNodeControl_Click;
            this.DoubleClick += ServerNodeControl_DoubleClick;

            // Forward events to child controls
            foreach (Control child in this.Controls)
            {
                child.Click += (s, e) => this.OnClick(e);
                child.DoubleClick += (s, e) => this.OnDoubleClick(e);
            }

            // Update display
            UpdateDisplay();
        }

        public void UpdateDisplay()
        {
            lblName.Text = Server.Name;
            lblEndpoints.Text = $"Endpoints: {Server.ConnectedEndpoints.Count}";

            if (Server.IsConnected)
            {
                lblStatus.Text = "Connected";
                lblStatus.ForeColor = Color.LightGreen;
                this.BackColor = IsSelected ? Color.FromArgb(10, 70, 10) : Color.FromArgb(0, 40, 0);
            }
            else if (DateTime.Now - Server.LastSeen < TimeSpan.FromSeconds(10))
            {
                lblStatus.Text = "Available";
                lblStatus.ForeColor = Color.Gray;
                this.BackColor = IsSelected ? Color.FromArgb(60, 60, 60) : Color.FromArgb(40, 40, 40);
            }
            else
            {
                lblStatus.Text = "Offline";
                lblStatus.ForeColor = Color.Gray;
                this.BackColor = IsSelected ? Color.FromArgb(70, 30, 30) : Color.FromArgb(50, 20, 20);
            }

            // Update status icon
            UpdateStatusIcon();
        }

        private void UpdateStatusIcon()
        {
            // Determine color based on status
            Color statusColor = Server.IsConnected ? Color.Green :
                (DateTime.Now - Server.LastSeen < TimeSpan.FromSeconds(10)) ? Color.Gray : Color.Red;

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

        private void ServerNodeControl_Click(object sender, EventArgs e)
        {
            IsSelected = !IsSelected;
            UpdateDisplay();
            Selected?.Invoke(this, EventArgs.Empty);
        }

        private void ServerNodeControl_DoubleClick(object sender, EventArgs e)
        {
            DoubleClicked?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Control for displaying an endpoint in the topology
    /// </summary>
    public class EndpointControl : Panel
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

            // Forward events to child controls
            foreach (Control child in this.Controls)
            {
                child.Click += (s, e) => this.OnClick(e);
            }

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
                lblStatus.Text = Endpoint.Status;
                lblStatus.ForeColor = Color.Gray;
                this.BackColor = IsSelected ? Color.FromArgb(60, 60, 60) : Color.FromArgb(40, 40, 40);
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
                statusColor = Color.Gray; // Available but not connected
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

    /// <summary>
    /// Dialog for manually adding a compute server
    /// </summary>
    public class AddServerDialog : KryptonForm
    {
        private KryptonTextBox txtName;
        private KryptonTextBox txtIP;
        private KryptonNumericUpDown numPort;

        public string ServerName => txtName.Text;
        public string ServerIP => txtIP.Text;
        public int ServerPort => (int)numPort.Value;

        public AddServerDialog()
        {
            this.Text = "Add Compute Server";
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
                Value = 7000  // Default port for client connection
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
                    MessageBox.Show("Please enter a name for the server.", "Validation Error",
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

    /// <summary>
    /// Dialog for setting simulation parameters
    /// </summary>
    public class SimulationParametersDialog : KryptonForm
    {
        private KryptonTextBox txtName;
        private KryptonTextBox txtDataPath;
        private KryptonNumericUpDown numIterations;
        private KryptonCheckBox chkUseGPU;
        private KryptonButton btnBrowse;

        public SimulationParameters Parameters { get; private set; }

        public SimulationParametersDialog()
        {
            this.Text = "Simulation Parameters";
            this.Size = new Size(450, 280);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.PaletteMode = PaletteMode.Office2010Black;

            // Create controls
            KryptonLabel lblName = new KryptonLabel { Text = "Simulation Name:", Location = new Point(20, 20), AutoSize = true };
            txtName = new KryptonTextBox { Location = new Point(150, 17), Width = 250 };

            KryptonLabel lblDataPath = new KryptonLabel { Text = "Data Path:", Location = new Point(20, 50), AutoSize = true };
            txtDataPath = new KryptonTextBox { Location = new Point(150, 47), Width = 200 };

            btnBrowse = new KryptonButton { Text = "...", Location = new Point(360, 47), Width = 40 };
            btnBrowse.Click += (s, e) => BrowseForDataFile();

            KryptonLabel lblIterations = new KryptonLabel { Text = "Iterations:", Location = new Point(20, 80), AutoSize = true };
            numIterations = new KryptonNumericUpDown
            {
                Location = new Point(150, 77),
                Width = 250,
                Minimum = 1,
                Maximum = 10000,
                Value = 100
            };

            chkUseGPU = new KryptonCheckBox
            {
                Text = "Use GPU acceleration if available",
                Location = new Point(150, 110),
                Checked = true
            };

            KryptonButton btnOK = new KryptonButton
            {
                Text = "Start Simulation",
                DialogResult = DialogResult.OK,
                Location = new Point(150, 170),
                Width = 120
            };

            KryptonButton btnCancel = new KryptonButton
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(280, 170),
                Width = 80
            };

            // Add controls to form
            this.Controls.AddRange(new Control[]
            {
                lblName, txtName,
                lblDataPath, txtDataPath, btnBrowse,
                lblIterations, numIterations,
                chkUseGPU,
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
                    MessageBox.Show("Please enter a name for the simulation.", "Validation Error",
                                  MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    this.DialogResult = DialogResult.None;
                    return;
                }

                // Create simulation parameters
                Parameters = new SimulationParameters
                {
                    SimulationName = txtName.Text,
                    DataPath = txtDataPath.Text,
                    Iterations = (int)numIterations.Value,
                    UseGPU = chkUseGPU.Checked
                };
            };
        }

        private void BrowseForDataFile()
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Filter = "Data Files|*.csv;*.dat;*.json;*.xml|All Files|*.*";
                dialog.Title = "Select Data File";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    txtDataPath.Text = dialog.FileName;
                }
            }
        }
    }
}