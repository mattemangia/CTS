using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.IO;

namespace ParallelComputingEndpoint
{
    public class EndpointNetworkService
    {
        private readonly EndpointConfig _config;
        private readonly EndpointComputeService _computeService;
        private TcpClient _serverConnection;
        private TcpListener _tcpListener;
        private int _listeningPort = 7010;
        private bool _isConnected = false;
        private string _currentTaskId;
        private CancellationTokenSource _listenerCts;

        public event EventHandler<List<ServerInfo>> ServersDiscovered;
        public event EventHandler<bool> ConnectionStatusChanged;
        public event EventHandler<string> MessageReceived;

        public EndpointNetworkService(EndpointConfig config, EndpointComputeService computeService)
        {
            _config = config;
            _computeService = computeService;
            _computeService.CpuLoadUpdated += OnCpuLoadUpdated;
        }

        public bool IsConnected => _isConnected;

        public async Task StartListenerAsync()
        {
            if (_tcpListener != null) return;

            try
            {
                _listenerCts = new CancellationTokenSource();
                _tcpListener = new TcpListener(IPAddress.Any, _listeningPort);
                _tcpListener.Start();

                Console.WriteLine($"Endpoint now listening for direct connections on port {_listeningPort}");

                _ = AcceptConnectionsAsync(_listenerCts.Token);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error starting TCP listener: {ex.Message}");
                _tcpListener = null;

                _listeningPort++;
                Console.WriteLine($"Trying alternative port: {_listeningPort}");
                await StartListenerAsync();
            }
        }

        private async Task AcceptConnectionsAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var client = await _tcpListener.AcceptTcpClientAsync().WaitAsync(cancellationToken);
                    _ = HandleIncomingConnectionAsync(client, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error accepting connections: {ex.Message}");
            }
        }

        private async Task HandleIncomingConnectionAsync(TcpClient client, CancellationToken cancellationToken)
        {
            try
            {
                var endpoint = (IPEndPoint)client.Client.RemoteEndPoint;
                Console.WriteLine($"Incoming connection from {endpoint.Address}:{endpoint.Port}");

                using var stream = client.GetStream();
                var buffer = new byte[8192];

                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                if (bytesRead == 0) return;

                var message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                Console.WriteLine($"Received message: {message}");

                string response = "";

                try
                {
                    var msgObj = JsonSerializer.Deserialize<JsonElement>(message);

                    if (msgObj.TryGetProperty("Command", out JsonElement commandElement))
                    {
                        string command = commandElement.GetString();

                        switch (command)
                        {
                            case "DIAGNOSTICS":
                                MessageReceived?.Invoke(this, "Running diagnostics requested by direct connection...");
                                string diagnosticsResult = _computeService.RunBenchmark();
                                response = JsonSerializer.Serialize(new
                                {
                                    Status = "OK",
                                    Message = "Diagnostics completed",
                                    DiagnosticsResult = diagnosticsResult
                                });
                                break;

                            case "PING":
                                response = JsonSerializer.Serialize(new
                                {
                                    Status = "OK",
                                    Message = "Pong"
                                });
                                break;

                            case "RESTART":
                                response = JsonSerializer.Serialize(new
                                {
                                    Status = "OK",
                                    Message = "Restart command received"
                                });

                                Task.Run(async () => {
                                    await Task.Delay(500);
                                    RestartEndpoint();
                                });
                                break;

                            case "SHUTDOWN":
                                response = JsonSerializer.Serialize(new
                                {
                                    Status = "OK",
                                    Message = "Shutdown command received"
                                });

                                Task.Run(async () => {
                                    await Task.Delay(500);
                                    ShutdownEndpoint();
                                });
                                break;

                            default:
                                response = JsonSerializer.Serialize(new
                                {
                                    Status = "Error",
                                    Message = $"Unknown command: {command}"
                                });
                                break;
                        }
                    }
                    else
                    {
                        response = JsonSerializer.Serialize(new
                        {
                            Status = "Error",
                            Message = "Invalid message format"
                        });
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error processing message: {ex.Message}");
                    response = JsonSerializer.Serialize(new
                    {
                        Status = "Error",
                        Message = $"Error processing message: {ex.Message}"
                    });
                }

                var responseBytes = Encoding.UTF8.GetBytes(response);
                await stream.WriteAsync(responseBytes, 0, responseBytes.Length, cancellationToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling incoming connection: {ex.Message}");
            }
            finally
            {
                client.Dispose();
            }
        }

        public async Task<List<ServerInfo>> ScanForServersAsync(int timeoutMs = 3000)
        {
            var servers = new List<ServerInfo>();
            var uniqueServers = new HashSet<string>();

            using var udpClient = new UdpClient();
            udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            int beaconPort = 7001;
            if (_config != null && _config.BeaconPort > 0)
            {
                beaconPort = _config.BeaconPort;
            }
            udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, beaconPort));

            var cancellationTokenSource = new CancellationTokenSource(timeoutMs);
            var token = cancellationTokenSource.Token;

            try
            {
                Console.WriteLine($"Scanning for servers on port {beaconPort}...");

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        UdpReceiveResult result;
                        try
                        {
                            result = await udpClient.ReceiveAsync().WaitAsync(token);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }

                        var message = Encoding.UTF8.GetString(result.Buffer);

                        try
                        {
                            var beaconMessage = JsonSerializer.Deserialize<ServerInfo>(message);

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
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error receiving UDP data: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error scanning for servers: {ex.Message}");
            }

            ServersDiscovered?.Invoke(this, servers);
            return servers;
        }

        public async Task<bool> ConnectToServerAsync(string serverIp, int serverPort)
        {
            try
            {
                if (_tcpListener == null)
                {
                    await StartListenerAsync();
                }

                if (_isConnected)
                {
                    await DisconnectAsync();
                }

                if (serverPort == 7000)
                {
                    serverPort = 7002;
                    Console.WriteLine($"Adjusting to use endpoint port: {serverPort}");
                }

                _serverConnection = new TcpClient();
                await _serverConnection.ConnectAsync(serverIp, serverPort);

                var registrationMessage = new
                {
                    Command = "REGISTER",
                    Name = _config.EndpointName,
                    HardwareInfo = _computeService.HardwareInfo,
                    GpuEnabled = _computeService.GpuAvailable,
                    ListeningPort = _listeningPort
                };

                var message = JsonSerializer.Serialize(registrationMessage);
                var bytes = Encoding.UTF8.GetBytes(message);

                var stream = _serverConnection.GetStream();
                await stream.WriteAsync(bytes, 0, bytes.Length);

                var buffer = new byte[4096];
                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                var response = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                var responseObj = JsonSerializer.Deserialize<JsonElement>(response);
                if (responseObj.TryGetProperty("Status", out JsonElement statusElement) &&
                    statusElement.GetString() == "OK")
                {
                    _isConnected = true;
                    ConnectionStatusChanged?.Invoke(this, true);

                    _config.ServerIP = serverIp;
                    _config.ServerPort = serverPort;

                    _ = ListenForServerMessagesAsync();
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

        public void Shutdown()
        {
            try
            {
                _listenerCts?.Cancel();
                _tcpListener?.Stop();
                _tcpListener = null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error stopping TCP listener: {ex.Message}");
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
                        break;
                    }

                    var message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    await ProcessServerMessageAsync(message);
                }

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
                            await SendAcknowledgmentAsync("Restart command received");
                            MessageReceived?.Invoke(this, "Restart command received from server. Restarting...");

                            Task.Run(async () => {
                                await Task.Delay(500);
                                RestartEndpoint();
                            });
                            break;

                        case "SHUTDOWN":
                            await SendAcknowledgmentAsync("Shutdown command received");
                            MessageReceived?.Invoke(this, "Shutdown command received from server. Shutting down...");

                            Task.Run(async () => {
                                await Task.Delay(500);
                                ShutdownEndpoint();
                            });
                            break;

                        case "DIAGNOSTICS":
                            MessageReceived?.Invoke(this, "Running diagnostics requested by server...");
                            string diagnosticsResult = _computeService.RunBenchmark();
                            await SendDiagnosticsResultAsync(diagnosticsResult);
                            break;

                        case "EXECUTE_TASK":
                            // Execute task when implemented
                            MessageReceived?.Invoke(this, "Execute task command received");
                            await SendAcknowledgmentAsync("Task received, processing not yet implemented");
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
                if (OperatingSystem.IsWindows())
                {
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
                    string scriptPath = Path.Combine(Path.GetTempPath(), "restart_endpoint.sh");
                    string shell = $@"#!/bin/bash
sleep 2
nohup ""{System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName}"" > /dev/null 2>&1 &
exit";
                    File.WriteAllText(scriptPath, shell);

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

            Environment.Exit(42);
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
                        CurrentTask = _currentTaskId ?? "None",
                        ListeningPort = _listeningPort
                    };

                    var message = JsonSerializer.Serialize(statusMessage);
                    var bytes = Encoding.UTF8.GetBytes(message);

                    var stream = _serverConnection.GetStream();
                    await stream.WriteAsync(bytes, 0, bytes.Length);

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
            // This is intentionally empty as we're using periodic status updates
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