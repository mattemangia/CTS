using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ParallelComputingEndpoint
{
    public class EndpointNetworkService
    {
        private readonly EndpointConfig _config;
        private readonly EndpointComputeService _computeService;
        private TcpClient _serverConnection;
        private bool _isConnected = false;
        private string _currentTaskId;

        // Events
        public event EventHandler<List<ServerInfo>> ServersDiscovered;
        public event EventHandler<bool> ConnectionStatusChanged;
        public event EventHandler<string> MessageReceived;

        public EndpointNetworkService(EndpointConfig config, EndpointComputeService computeService)
        {
            _config = config;
            _computeService = computeService;

            // Subscribe to compute service events
            _computeService.CpuLoadUpdated += OnCpuLoadUpdated;
        }

        public bool IsConnected => _isConnected;

        public async Task<List<ServerInfo>> ScanForServersAsync(int timeoutMs = 3000)
        {
            var servers = new List<ServerInfo>();
            var uniqueServers = new HashSet<string>(); // To prevent duplicates

            using var udpClient = new UdpClient();
            udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, _config.BeaconPort));

            // Set receive timeout
            var cancellationTokenSource = new CancellationTokenSource(timeoutMs);
            var token = cancellationTokenSource.Token;

            try
            {
                Console.WriteLine($"Scanning for servers on port {_config.BeaconPort}...");

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var result = await udpClient.ReceiveAsync(token);
                        var message = Encoding.UTF8.GetString(result.Buffer);

                        try
                        {
                            // Parse beacon message
                            var beaconMessage = JsonSerializer.Deserialize<ServerInfo>(message);

                            // Add to list if not already present
                            string serverKey = $"{beaconMessage.ServerIP}:{beaconMessage.ServerPort}";
                            if (!uniqueServers.Contains(serverKey))
                            {
                                uniqueServers.Add(serverKey);
                                servers.Add(beaconMessage);

                                Console.WriteLine($"Server found: {beaconMessage.ServerName} ({beaconMessage.ServerIP})");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error parsing beacon message: {ex.Message}");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Timeout reached
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error scanning for servers: {ex.Message}");
            }

            // Notify listeners
            ServersDiscovered?.Invoke(this, servers);

            return servers;
        }

        public async Task<bool> ConnectToServerAsync(string serverIp, int serverPort)
        {
            try
            {
                // Disconnect first if already connected
                if (_isConnected)
                {
                    await DisconnectAsync();
                }

                // Use the correct endpoint port (7002) from the server config
                // If the port passed is the general server port (7000), adjust to endpoint port
                if (serverPort == 7000)
                {
                    serverPort = 7002; // Use the endpoint port instead of the client port
                    Console.WriteLine($"Adjusting to use endpoint port: {serverPort}");
                }

                _serverConnection = new TcpClient();
                await _serverConnection.ConnectAsync(serverIp, serverPort);

                // Send registration message
                var registrationMessage = new
                {
                    Command = "REGISTER",
                    Name = _config.EndpointName,
                    HardwareInfo = _computeService.HardwareInfo,
                    GpuEnabled = _computeService.GpuAvailable
                };

                var message = JsonSerializer.Serialize(registrationMessage);
                var bytes = Encoding.UTF8.GetBytes(message);

                var stream = _serverConnection.GetStream();
                await stream.WriteAsync(bytes, 0, bytes.Length);

                // Read registration response
                var buffer = new byte[4096];
                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                var response = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                // Parse response
                var responseObj = JsonSerializer.Deserialize<JsonElement>(response);
                if (responseObj.TryGetProperty("Status", out JsonElement statusElement) &&
                    statusElement.GetString() == "OK")
                {
                    _isConnected = true;
                    ConnectionStatusChanged?.Invoke(this, true);

                    // Update configuration
                    _config.ServerIP = serverIp;
                    _config.ServerPort = serverPort;

                    // Start listener for server messages
                    _ = ListenForServerMessagesAsync();

                    // Start sending status updates
                    _ = SendStatusUpdatesAsync();

                    Console.WriteLine($"Connected to server at {serverIp}:{serverPort}");
                    MessageReceived?.Invoke(this, $"Connected to server at {serverIp}:{serverPort}");

                    return true;
                }
                else
                {
                    _serverConnection.Dispose();
                    _serverConnection = null;

                    Console.WriteLine("Registration failed.");
                    MessageReceived?.Invoke(this, "Registration failed: " + response);

                    return false;
                }
            }
            catch (Exception ex)
            {
                _serverConnection?.Dispose();
                _serverConnection = null;

                Console.WriteLine($"Connection error: {ex.Message}");
                MessageReceived?.Invoke(this, $"Connection error: {ex.Message}");

                return false;
            }
        }

        public async Task DisconnectAsync()
        {
            if (_serverConnection != null)
            {
                try
                {
                    if (_serverConnection.Connected)
                    {
                        // Send disconnect message
                        var disconnectMessage = new
                        {
                            Command = "DISCONNECT"
                        };

                        var message = JsonSerializer.Serialize(disconnectMessage);
                        var bytes = Encoding.UTF8.GetBytes(message);

                        var stream = _serverConnection.GetStream();
                        await stream.WriteAsync(bytes, 0, bytes.Length);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error sending disconnect message: {ex.Message}");
                }
                finally
                {
                    _serverConnection.Dispose();
                    _serverConnection = null;
                    _isConnected = false;
                    ConnectionStatusChanged?.Invoke(this, false);
                    MessageReceived?.Invoke(this, "Disconnected from server.");
                }
            }
        }

        private async Task ListenForServerMessagesAsync()
        {
            try
            {
                var stream = _serverConnection.GetStream();
                var buffer = new byte[8192];

                while (_isConnected && _serverConnection.Connected)
                {
                    int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);

                    if (bytesRead == 0)
                    {
                        // Connection closed
                        break;
                    }

                    var message = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                    // Process message
                    await ProcessServerMessageAsync(message);
                }

                // Connection lost
                _isConnected = false;
                ConnectionStatusChanged?.Invoke(this, false);
                MessageReceived?.Invoke(this, "Connection to server lost.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error listening for server messages: {ex.Message}");
                _isConnected = false;
                ConnectionStatusChanged?.Invoke(this, false);
                MessageReceived?.Invoke(this, $"Connection error: {ex.Message}");
            }
        }

        private async Task ProcessServerMessageAsync(string message)
        {
            try
            {
                var msgObj = JsonSerializer.Deserialize<JsonElement>(message);

                if (msgObj.TryGetProperty("Command", out JsonElement commandElement))
                {
                    string command = commandElement.GetString();

                    switch (command)
                    {
                        case "PING":
                            await SendPongAsync();
                            break;

                        case "RESTART":
                            // Send acknowledgment first
                            await SendAcknowledgmentAsync("Restart command received");
                            MessageReceived?.Invoke(this, "Restart command received from server. Restarting...");

                            // Schedule restart after a short delay to allow response to be sent
                            Task.Run(async () => {
                                await Task.Delay(500);
                                RestartEndpoint();
                            });
                            break;

                        case "SHUTDOWN":
                            // Send acknowledgment first
                            await SendAcknowledgmentAsync("Shutdown command received");
                            MessageReceived?.Invoke(this, "Shutdown command received from server. Shutting down...");

                            // Schedule shutdown after a short delay to allow response to be sent
                            Task.Run(async () => {
                                await Task.Delay(500);
                                ShutdownEndpoint();
                            });
                            break;

                        case "DIAGNOSTICS":
                            // Run diagnostics and send results back
                            MessageReceived?.Invoke(this, "Running diagnostics requested by server...");
                            string diagnosticsResult = _computeService.RunBenchmark();
                            await SendDiagnosticsResultAsync(diagnosticsResult);
                            break;

                        case "EXECUTE_TASK":
                            
                            break;

                        default:
                            MessageReceived?.Invoke(this, $"Received unknown command: {command}");
                            await SendErrorResponseAsync($"Unknown command: {command}");
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing server message: {ex.Message}");
                MessageReceived?.Invoke(this, $"Error processing message: {ex.Message}");
                await SendErrorResponseAsync($"Error processing message: {ex.Message}");
            }
        }
        private async Task SendAcknowledgmentAsync(string message)
        {
            try
            {
                if (_isConnected && _serverConnection.Connected)
                {
                    var ackMessage = new
                    {
                        Status = "OK",
                        Message = message
                    };

                    var json = JsonSerializer.Serialize(ackMessage);
                    var bytes = Encoding.UTF8.GetBytes(json);

                    var stream = _serverConnection.GetStream();
                    await stream.WriteAsync(bytes, 0, bytes.Length);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending acknowledgment: {ex.Message}");
            }
        }
        private async Task SendDiagnosticsResultAsync(string diagnosticsResult)
        {
            try
            {
                if (_isConnected && _serverConnection.Connected)
                {
                    var resultMessage = new
                    {
                        Status = "OK",
                        Message = "Diagnostics completed",
                        DiagnosticsResult = diagnosticsResult
                    };

                    var json = JsonSerializer.Serialize(resultMessage);
                    var bytes = Encoding.UTF8.GetBytes(json);

                    var stream = _serverConnection.GetStream();
                    await stream.WriteAsync(bytes, 0, bytes.Length);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending diagnostics result: {ex.Message}");
            }
        }

        private async Task SendErrorResponseAsync(string errorMessage)
        {
            try
            {
                if (_isConnected && _serverConnection.Connected)
                {
                    var errorResponse = new
                    {
                        Status = "Error",
                        Message = errorMessage
                    };

                    var json = JsonSerializer.Serialize(errorResponse);
                    var bytes = Encoding.UTF8.GetBytes(json);

                    var stream = _serverConnection.GetStream();
                    await stream.WriteAsync(bytes, 0, bytes.Length);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending error response: {ex.Message}");
            }
        }
        private void RestartEndpoint()
        {
            Console.WriteLine("Endpoint restart initiated...");

            try
            {
                // Create a restart script based on the platform
                if (OperatingSystem.IsWindows())
                {
                    // Windows batch script
                    string scriptPath = Path.Combine(Path.GetTempPath(), "restart_endpoint.bat");
                    string batch = $@"@echo off
timeout /t 2 /nobreak > nul
start """" ""{System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName}""
exit";
                    File.WriteAllText(scriptPath, batch);

                    var startInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c {scriptPath}",
                        CreateNoWindow = true,
                        UseShellExecute = true
                    };
                    System.Diagnostics.Process.Start(startInfo);
                }
                else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                {
                    // Linux/macOS shell script
                    string scriptPath = Path.Combine(Path.GetTempPath(), "restart_endpoint.sh");
                    string shell = $@"#!/bin/bash
sleep 2
nohup ""{System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName}"" > /dev/null 2>&1 &
exit";
                    File.WriteAllText(scriptPath, shell);

                    // Make script executable
                    var chmodInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "chmod",
                        Arguments = $"+x {scriptPath}",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    System.Diagnostics.Process.Start(chmodInfo)?.WaitForExit();

                    var startInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "/bin/bash",
                        Arguments = scriptPath,
                        CreateNoWindow = true,
                        UseShellExecute = true
                    };
                    System.Diagnostics.Process.Start(startInfo);
                }
                else
                {
                    Console.WriteLine("Restart not supported on this platform.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error creating restart script: {ex.Message}");
            }

            // Exit the application
            Environment.Exit(42); // Special code for restart
        }

        private void ShutdownEndpoint()
        {
            Console.WriteLine("Endpoint shutdown initiated...");
            Environment.Exit(0);
        }
        private async Task SendPongAsync()
        {
            try
            {
                if (_isConnected && _serverConnection.Connected)
                {
                    var pongMessage = new
                    {
                        Command = "PONG"
                    };

                    var message = JsonSerializer.Serialize(pongMessage);
                    var bytes = Encoding.UTF8.GetBytes(message);

                    var stream = _serverConnection.GetStream();
                    await stream.WriteAsync(bytes, 0, bytes.Length);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending pong: {ex.Message}");
            }
        }

        private async Task SendStatusUpdatesAsync()
        {
            try
            {
                while (_isConnected && _serverConnection.Connected)
                {
                    var statusMessage = new
                    {
                        Command = "STATUS_UPDATE",
                        CpuLoad = _computeService.GetCpuLoad(),
                        Status = string.IsNullOrEmpty(_currentTaskId) ? "Available" : "Processing",
                        CurrentTask = _currentTaskId ?? "None"
                    };

                    var message = JsonSerializer.Serialize(statusMessage);
                    var bytes = Encoding.UTF8.GetBytes(message);

                    var stream = _serverConnection.GetStream();
                    await stream.WriteAsync(bytes, 0, bytes.Length);

                    // Send status updates every 5 seconds
                    await Task.Delay(5000);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending status update: {ex.Message}");
                _isConnected = false;
                ConnectionStatusChanged?.Invoke(this, false);
            }
        }

        private async Task SendTaskCompletionAsync(string taskId)
        {
            try
            {
                if (_isConnected && _serverConnection.Connected)
                {
                    var completionMessage = new
                    {
                        Command = "TASK_COMPLETED",
                        TaskId = taskId,
                        Result = "Task completed successfully"
                    };

                    var message = JsonSerializer.Serialize(completionMessage);
                    var bytes = Encoding.UTF8.GetBytes(message);

                    var stream = _serverConnection.GetStream();
                    await stream.WriteAsync(bytes, 0, bytes.Length);

                    _currentTaskId = null;
                    MessageReceived?.Invoke(this, $"Task {taskId} completed and result sent to server.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending task completion: {ex.Message}");
                MessageReceived?.Invoke(this, $"Error sending task completion: {ex.Message}");
            }
        }

        private void OnCpuLoadUpdated(object sender, double cpuLoad)
        {
            // If we want to immediately send a status update when CPU load changes significantly,
            // we could implement that here. For now, we're just sending updates periodically.
        }
    }

    public class ServerInfo
    {
        public string ServerName { get; set; }
        public string ServerIP { get; set; }
        public int ServerPort { get; set; }
        public int ClientsConnected { get; set; }
        public int EndpointsConnected { get; set; }
        public bool GpuEnabled { get; set; }
        public DateTime Timestamp { get; set; }
    }
}