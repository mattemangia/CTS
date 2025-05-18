using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ParallelComputingServer.Config;
using ParallelComputingServer.Models;

namespace ParallelComputingServer.Services
{
    public class NetworkService
    {
        private readonly ServerConfig _config;
        private readonly ComputeService _computeService;
        private List<ClientInfo> _connectedClients = new();

        // Event handlers for UI updates
        public event EventHandler<DateTime> BeaconSent;
        public event EventHandler<DateTime> KeepAliveReceived;
        public event EventHandler ClientsUpdated;

        public NetworkService(ServerConfig config, ComputeService computeService)
        {
            _config = config;
            _computeService = computeService;
        }

        public List<ClientInfo> GetConnectedClients() => _connectedClients;

        public async Task StartBeaconServiceAsync(CancellationToken cancellationToken)
        {
            using var udpClient = new UdpClient();
            udpClient.EnableBroadcast = true;

            var endpoint = new IPEndPoint(IPAddress.Broadcast, _config.BeaconPort);
            var hostname = Dns.GetHostName();

            Console.WriteLine($"Starting beacon service on port {_config.BeaconPort}...");

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    // Create beacon message with server information
                    var beaconMessage = new BeaconMessage
                    {
                        ServerName = hostname,
                        ServerIP = GetLocalIPAddress(),
                        ServerPort = _config.ServerPort,
                        ClientsConnected = _connectedClients.Count,
                        EndpointsConnected = 0, // This will be updated by EndpointService
                        GpuEnabled = _computeService.GpuAvailable,
                        Timestamp = DateTime.Now
                    };

                    // Serialize and send
                    string message = JsonSerializer.Serialize(beaconMessage);
                    byte[] bytes = Encoding.UTF8.GetBytes(message);
                    await udpClient.SendAsync(bytes, bytes.Length, endpoint);

                    // Notify UI of beacon activity
                    BeaconSent?.Invoke(this, DateTime.Now);

                    await Task.Delay(_config.BeaconIntervalMs, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation token is triggered
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Beacon service error: {ex.Message}");
            }

            Console.WriteLine("Beacon service stopped.");
        }

        public async Task StartServerAsync(CancellationToken cancellationToken)
        {
            var listener = new TcpListener(IPAddress.Any, _config.ServerPort);
            listener.Start();

            Console.WriteLine($"Server listening on port {_config.ServerPort}...");

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    // Use WaitAsync with the cancellation token
                    TcpClient client;
                    try
                    {
                        client = await listener.AcceptTcpClientAsync().WaitAsync(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break; // Exit the loop when cancellation is requested
                    }

                    // Handle each client in a separate task
                    _ = HandleClientAsync(client, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation token is triggered
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Server error: {ex.Message}");
            }
            finally
            {
                listener.Stop();
            }

            Console.WriteLine("Server stopped.");
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
            try
            {
                // Get client info
                var endpoint = (IPEndPoint)client.Client.RemoteEndPoint;
                var clientInfo = new ClientInfo
                {
                    ClientIP = endpoint.Address.ToString(),
                    ClientPort = endpoint.Port,
                    ConnectedAt = DateTime.Now
                };

                Console.WriteLine($"Client connected: {clientInfo.ClientIP}:{clientInfo.ClientPort}");
                _connectedClients.Add(clientInfo);
                ClientsUpdated?.Invoke(this, EventArgs.Empty);

                // Handle client communication
                using var stream = client.GetStream();
                var buffer = new byte[4096];

                while (!cancellationToken.IsCancellationRequested && client.Connected)
                {
                    try
                    {
                        var bytesRead = await stream.ReadAsync(buffer, cancellationToken);
                        if (bytesRead == 0) break; // Client disconnected

                        var message = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                        // Check if this is a ping/keep-alive message
                        if (message.Contains("\"Command\":\"PING\""))
                        {
                            KeepAliveReceived?.Invoke(this, DateTime.Now);

                            // Send a simple response - use a different variable name here
                            var pingResponse = "{\"Status\":\"OK\",\"Message\":\"Pong\"}";
                            var pingResponseBytes = Encoding.UTF8.GetBytes(pingResponse);
                            await stream.WriteAsync(pingResponseBytes, cancellationToken);
                            continue;
                        }

                        // Process the message
                        var response = ProcessClientMessage(message, clientInfo);

                        // Send response
                        var responseBytes = Encoding.UTF8.GetBytes(response);
                        await stream.WriteAsync(responseBytes, cancellationToken);
                    }
                    catch (IOException)
                    {
                        // Connection was closed
                        break;
                    }
                }

                // Cleanup when client disconnects
                _connectedClients.Remove(clientInfo);
                ClientsUpdated?.Invoke(this, EventArgs.Empty);
                Console.WriteLine($"Client disconnected: {clientInfo.ClientIP}:{clientInfo.ClientPort}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling client: {ex.Message}");
            }
            finally
            {
                client.Dispose();
            }
        }

        private string ProcessClientMessage(string message, ClientInfo clientInfo)
        {
            try
            {
                // Parse the JSON message
                var commandObj = JsonSerializer.Deserialize<JsonElement>(message);

                if (commandObj.TryGetProperty("Command", out JsonElement commandElement))
                {
                    string command = commandElement.GetString();

                    switch (command)
                    {
                        case "PING":
                            return "{\"Status\":\"OK\",\"Message\":\"Pong\"}";

                        case "DIAGNOSTICS":
                            // Run diagnostic tests
                            return _computeService.RunDiagnostics();

                        case "RESTART":
                            // Schedule a restart
                            Task.Run(async () =>
                            {
                                // Wait a moment before restarting to allow response to be sent
                                await Task.Delay(500);
                                RestartServer();
                            });
                            return "{\"Status\":\"OK\",\"Message\":\"Server restart initiated\"}";

                        case "SHUTDOWN":
                            // Schedule a shutdown
                            Task.Run(async () =>
                            {
                                // Wait a moment before shutting down to allow response to be sent
                                await Task.Delay(500);
                                ShutdownServer();
                            });
                            return "{\"Status\":\"OK\",\"Message\":\"Server shutdown initiated\"}";

                        default:
                            return "{\"Status\":\"Error\",\"Message\":\"Unknown command\"}";
                    }
                }

                return "{\"Status\":\"Error\",\"Message\":\"Invalid command format\"}";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing message: {ex.Message}");
                return $"{{\"Status\":\"Error\",\"Message\":\"{ex.Message}\"}}";
            }
        }

        public static string GetLocalIPAddress()
        {
            // Get the first non-loopback IPv4 address
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var ip in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ip.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(ip.Address))
                    {
                        return ip.Address.ToString();
                    }
                }
            }
            return "127.0.0.1"; // Fallback to localhost
        }

        private void RestartServer()
        {
            Console.WriteLine("Server restart initiated...");

            // Create a restart script based on the platform
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    // Windows batch script
                    string scriptPath = Path.Combine(Path.GetTempPath(), "restart_server.bat");
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
                else if (OperatingSystem.IsLinux())
                {
                    // Linux shell script
                    string scriptPath = Path.Combine(Path.GetTempPath(), "restart_server.sh");
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
                else if (OperatingSystem.IsMacOS())
                {
                    // macOS shell script (similar to Linux)
                    string scriptPath = Path.Combine(Path.GetTempPath(), "restart_server.sh");
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
                    Console.WriteLine("Restart not supported on this platform. Please restart manually.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error creating restart script: {ex.Message}");
            }

            // Exit the application
            Environment.Exit(42); // Special code that a wrapper script could use to restart
        }

        private void ShutdownServer()
        {
            Console.WriteLine("Server shutdown initiated...");
            Environment.Exit(0);
        }
    }
}