using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Linq;
using ParallelComputingServer.Config;
using ParallelComputingServer.Models;

namespace ParallelComputingServer.Services
{
    public partial class NetworkService
    {
        private readonly ServerConfig _config;
        private readonly ComputeService _computeService;
        private readonly EndpointService _endpointService;
        private List<ClientInfo> _connectedClients = new();
        private NodeProcessingService _nodeProcessingService;
        // Event handlers for UI updates
        public event EventHandler<DateTime> BeaconSent;
        public event EventHandler<DateTime> KeepAliveReceived;
        public event EventHandler ClientsUpdated;

        public NetworkService(ServerConfig config, ComputeService computeService, EndpointService endpointService)
        {
            _config = config;
            _computeService = computeService;
            _endpointService = endpointService;

            // Initialize the node processing service
            _nodeProcessingService = new NodeProcessingService(computeService);
        }

        public List<ClientInfo> GetConnectedClients() => _connectedClients;

        public async Task StartBeaconServiceAsync(CancellationToken cancellationToken)
        {
            using var udpClient = new UdpClient();
            udpClient.EnableBroadcast = true;

            var endpoint = new IPEndPoint(IPAddress.Broadcast, _config.BeaconPort);
            var hostname = Dns.GetHostName();

            ////Console.WriteLine($"Starting beacon service on port {_config.BeaconPort}...");

            try
            {
                // Use Task.Delay instead of creating a new CancellationTokenSource for each iteration
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        // Create beacon message with server information
                        var beaconMessage = new BeaconMessage
                        {
                            ServerName = hostname,
                            ServerIP = GetLocalIPAddress(),
                            ServerPort = _config.ServerPort,
                            ClientsConnected = _connectedClients.Count,
                            EndpointsConnected = _endpointService.GetConnectedEndpoints().Count,
                            GpuEnabled = _computeService.GpuAvailable,
                            Timestamp = DateTime.Now
                        };

                        // Serialize and send
                        string message = JsonSerializer.Serialize(beaconMessage);
                        byte[] bytes = Encoding.UTF8.GetBytes(message);
                        await udpClient.SendAsync(bytes, bytes.Length, endpoint);

                        // Notify UI of beacon activity
                        BeaconSent?.Invoke(this, DateTime.Now);

                        // Use Task.Delay with the cancellation token
                        await Task.Delay(_config.BeaconIntervalMs, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break; // Exit when cancellation requested
                    }
                    catch (Exception ex)
                    {
                        ////Console.WriteLine($"Beacon service error: {ex.Message}");
                        // Add a small delay on error to prevent CPU spinning
                        await Task.Delay(1000, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation token is triggered
            }
            catch (Exception ex)
            {
                ////Console.WriteLine($"Beacon service error: {ex.Message}");
            }

            //Console.WriteLine("Beacon service stopped.");
        }
        private void InitializeNodeProcessingService()
        {
            _nodeProcessingService = new NodeProcessingService(_computeService);
        }
        public async Task StartServerAsync(CancellationToken cancellationToken)
        {
            var listener = new TcpListener(IPAddress.Any, _config.ServerPort);
            listener.Start();

            //Console.WriteLine($"Server listening on port {_config.ServerPort}...");

            try
            {
                // Use TaskCompletionSource pattern for more efficient waiting
                using var registration = cancellationToken.Register(() =>
                {
                    try { listener.Stop(); } catch { }
                });

                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        // This will naturally wait without spinning
                        var client = await listener.AcceptTcpClientAsync();

                        // Handle each client in a separate task
                        _ = HandleClientAsync(client, cancellationToken);
                    }
                    catch (SocketException ex) when (cancellationToken.IsCancellationRequested)
                    {
                        // Expected when the listener is stopped due to cancellation
                        break;
                    }
                    catch (Exception ex)
                    {
                        ////Console.WriteLine($"Server error accepting client: {ex.Message}");
                        // Add a small delay on error to prevent CPU spinning
                        await Task.Delay(1000, cancellationToken);
                    }
                }
            }
            finally
            {
                try { listener.Stop(); } catch { }
                //Console.WriteLine("Server stopped.");
            }
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

                ////Console.WriteLine($"Client connected: {clientInfo.ClientIP}:{clientInfo.ClientPort}");
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
                        var response = await ProcessClientMessageAsync(message, clientInfo, cancellationToken);

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
                ////Console.WriteLine($"Client disconnected: {clientInfo.ClientIP}:{clientInfo.ClientPort}");
            }
            catch (Exception ex)
            {
                //Console.WriteLine($"Error handling client: {ex.Message}");
            }
            finally
            {
                client.Dispose();
            }
        }

        private async Task<string> ProcessClientMessageAsync(string message, ClientInfo clientInfo, CancellationToken cancellationToken)
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
                            // Run diagnostic tests on the server
                            return _computeService.RunDiagnostics();

                        case "RESTART":
                            // Schedule a restart
                            Task.Run(async () =>
                            {
                                await Task.Delay(500);
                                RestartServer();
                            });
                            return "{\"Status\":\"OK\",\"Message\":\"Server restart initiated\"}";

                        case "SHUTDOWN":
                            // Schedule a shutdown
                            Task.Run(async () =>
                            {
                                await Task.Delay(500);
                                ShutdownServer();
                            });
                            return "{\"Status\":\"OK\",\"Message\":\"Server shutdown initiated\"}";

                        case "LIST_ENDPOINTS":
                            // Return the list of connected endpoints
                            var endpoints = _endpointService.GetConnectedEndpoints();
                            return JsonSerializer.Serialize(new
                            {
                                Status = "OK",
                                Endpoints = endpoints
                            });

                        case "GET_ENDPOINTS":
                            // Alternative method to get endpoints, just returns the count
                            var endpointCount = _endpointService.GetConnectedEndpoints().Count;
                            return JsonSerializer.Serialize(new
                            {
                                Status = "OK",
                                EndpointCount = endpointCount
                            });

                        case "ENDPOINT_DIAGNOSTICS":
                            return await RunEndpointDiagnosticsCommand(commandObj, cancellationToken);

                        case "RESTART_ENDPOINT":
                            return await HandleEndpointActionCommand(commandObj, "RESTART", cancellationToken);

                        case "SHUTDOWN_ENDPOINT":
                            return await HandleEndpointActionCommand(commandObj, "SHUTDOWN", cancellationToken);

                        case "FORWARD_TO_ENDPOINT":
                            return await ForwardCommandToEndpoint(commandObj, cancellationToken);
                        case "GET_AVAILABLE_NODES":
                            // Return the list of node types this server can process
                            if (_nodeProcessingService == null)
                            {
                                InitializeNodeProcessingService();
                            }
                            return _nodeProcessingService.GetAvailableNodeTypes();

                        case "EXECUTE_NODE":
                            // Execute a node and return the results
                            if (commandObj.TryGetProperty("NodeType", out JsonElement nodeTypeElement) &&
                                commandObj.TryGetProperty("InputData", out JsonElement inputDataElement))
                            {
                                string nodeType = nodeTypeElement.GetString();
                                string inputData = inputDataElement.GetString();

                                // Initialize node processing service if not already done
                                if (_nodeProcessingService == null)
                                {
                                    InitializeNodeProcessingService();
                                }

                                return await _nodeProcessingService.ProcessNodeAsync(nodeType, inputData);
                            }
                            else
                            {
                                return JsonSerializer.Serialize(new
                                {
                                    Status = "Error",
                                    Message = "Missing required parameters: NodeType and/or InputData"
                                });
                            }
                        default:
                            return "{\"Status\":\"Error\",\"Message\":\"Unknown command\"}";
                    }
                }

                return "{\"Status\":\"Error\",\"Message\":\"Invalid command format\"}";
            }
            catch (Exception ex)
            {
                ////Console.WriteLine($"Error processing message: {ex.Message}");
                return $"{{\"Status\":\"Error\",\"Message\":\"{ex.Message}\"}}";
            }
        }
        private async Task<string> RunEndpointDiagnosticsCommand(JsonElement commandObj, CancellationToken cancellationToken)
        {
            try
            {
                // Get endpoint information from the command
                if (!commandObj.TryGetProperty("EndpointName", out JsonElement endpointNameElement))
                {
                    return JsonSerializer.Serialize(new
                    {
                        Status = "Error",
                        Message = "Endpoint name not specified"
                    });
                }

                string endpointName = endpointNameElement.GetString();
                string endpointIP = "";
                int endpointPort = 0;

                // Get endpoint IP if provided
                if (commandObj.TryGetProperty("EndpointIP", out JsonElement endpointIPElement))
                {
                    endpointIP = endpointIPElement.GetString();
                }

                // Get endpoint port if provided
                if (commandObj.TryGetProperty("EndpointPort", out JsonElement endpointPortElement))
                {
                    endpointPort = endpointPortElement.GetInt32();
                }

                // Find the endpoint
                var endpoint = _endpointService.GetConnectedEndpoints()
                    .FirstOrDefault(e => e.Name == endpointName ||
                                         (e.EndpointIP == endpointIP && !string.IsNullOrEmpty(endpointIP)));

                if (endpoint == null)
                {
                    // If not found in our list but IP/port provided, we can try direct connection
                    if (string.IsNullOrEmpty(endpointIP) || endpointPort == 0)
                    {
                        return JsonSerializer.Serialize(new
                        {
                            Status = "Error",
                            Message = $"Endpoint '{endpointName}' not found"
                        });
                    }

                    // Use provided IP/port for direct connection
                    ////Console.WriteLine($"Endpoint {endpointName} not in connected list, attempting direct connection to {endpointIP}:{endpointPort}");
                }
                else
                {
                    // Use endpoint from our list
                    endpointIP = endpoint.EndpointIP;
                    endpointPort = endpoint.EndpointPort;
                    ////Console.WriteLine($"Found endpoint {endpointName} at {endpointIP}:{endpointPort}");
                }

                // Create diagnostics command
                var diagCommand = new { Command = "DIAGNOSTICS" };
                string diagCommandJson = JsonSerializer.Serialize(diagCommand);

                // Connect to the endpoint and send command
                try
                {
                    ////Console.WriteLine($"Connecting to endpoint at {endpointIP}:{endpointPort}");
                    using var client = new TcpClient();

                    // Connect with timeout
                    var connectTask = client.ConnectAsync(endpointIP, endpointPort);
                    var timeoutTask = Task.Delay(5000, cancellationToken);

                    await Task.WhenAny(connectTask, timeoutTask);

                    if (timeoutTask.IsCompleted)
                    {
                        ////Console.WriteLine($"Connection to endpoint {endpointName} timed out");
                        return JsonSerializer.Serialize(new
                        {
                            Status = "Error",
                            Message = "Connection to endpoint timed out"
                        });
                    }

                    if (!client.Connected)
                    {
                        ////Console.WriteLine($"Failed to connect to endpoint {endpointName}");
                        return JsonSerializer.Serialize(new
                        {
                            Status = "Error",
                            Message = "Failed to connect to endpoint"
                        });
                    }

                    // Connected successfully
                   // //Console.WriteLine($"Connected to endpoint {endpointName}, sending diagnostics command");
                    using NetworkStream stream = client.GetStream();
                    byte[] commandBytes = Encoding.UTF8.GetBytes(diagCommandJson);
                    await stream.WriteAsync(commandBytes, 0, commandBytes.Length, cancellationToken);

                    // Read response with timeout
                    ////Console.WriteLine($"Waiting for diagnostics response from endpoint {endpointName}");
                    var buffer = new byte[32768]; // Larger buffer for diagnostics results
                    var readTask = stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                    timeoutTask = Task.Delay(15000, cancellationToken); // Longer timeout for diagnostics

                    await Task.WhenAny(readTask, timeoutTask);

                    if (timeoutTask.IsCompleted)
                    {
                        ////Console.WriteLine($"Reading diagnostics response from endpoint {endpointName} timed out");
                        return JsonSerializer.Serialize(new
                        {
                            Status = "Error",
                            Message = "Reading diagnostics response timed out"
                        });
                    }

                    // Got response
                    int bytesRead = await readTask;
                    string response = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    ////Console.WriteLine($"Received {bytesRead} bytes diagnostics response from endpoint {endpointName}");

                    // Return diagnostics result
                    return JsonSerializer.Serialize(new
                    {
                        Status = "OK",
                        DiagnosticsResult = response
                    });
                }
                catch (Exception ex)
                {
                    ////Console.WriteLine($"Error running diagnostics on endpoint {endpointName}: {ex.Message}");
                    return JsonSerializer.Serialize(new
                    {
                        Status = "Error",
                        Message = $"Error running diagnostics on endpoint: {ex.Message}"
                    });
                }
            }
            catch (Exception ex)
            {
                ////Console.WriteLine($"Error processing endpoint diagnostics command: {ex.Message}");
                return JsonSerializer.Serialize(new
                {
                    Status = "Error",
                    Message = $"Error processing endpoint diagnostics command: {ex.Message}"
                });
            }
        }
        private async Task<string> HandleEndpointActionCommand(JsonElement commandObj, string action, CancellationToken cancellationToken)
        {
            try
            {
                // Get endpoint information from the command
                if (!commandObj.TryGetProperty("EndpointName", out JsonElement endpointNameElement))
                {
                    return JsonSerializer.Serialize(new
                    {
                        Status = "Error",
                        Message = "Endpoint name not specified"
                    });
                }

                string endpointName = endpointNameElement.GetString();
                string endpointIP = "";
                int endpointPort = 0;

                // Get endpoint IP if provided
                if (commandObj.TryGetProperty("EndpointIP", out JsonElement endpointIPElement))
                {
                    endpointIP = endpointIPElement.GetString();
                }

                // Get endpoint port if provided
                if (commandObj.TryGetProperty("EndpointPort", out JsonElement endpointPortElement))
                {
                    endpointPort = endpointPortElement.GetInt32();
                }

                // Find the endpoint
                var endpoint = _endpointService.GetConnectedEndpoints()
                    .FirstOrDefault(e => e.Name == endpointName ||
                                         (e.EndpointIP == endpointIP && !string.IsNullOrEmpty(endpointIP)));

                if (endpoint == null)
                {
                    // If not found in our list but IP/port provided, we can try direct connection
                    if (string.IsNullOrEmpty(endpointIP) || endpointPort == 0)
                    {
                        return JsonSerializer.Serialize(new
                        {
                            Status = "Error",
                            Message = $"Endpoint '{endpointName}' not found"
                        });
                    }

                    // Use provided IP/port for direct connection
                   // //Console.WriteLine($"Endpoint {endpointName} not in connected list, attempting direct connection to {endpointIP}:{endpointPort}");
                }
                else
                {
                    // Use endpoint from our list
                    endpointIP = endpoint.EndpointIP;
                    endpointPort = endpoint.EndpointPort;
                   // //Console.WriteLine($"Found endpoint {endpointName} at {endpointIP}:{endpointPort}");
                }

                // Create action command
                var actionCommand = new { Command = action };
                string actionCommandJson = JsonSerializer.Serialize(actionCommand);

                // Connect to the endpoint and send command
                try
                {
                    ////Console.WriteLine($"Connecting to endpoint at {endpointIP}:{endpointPort}");
                    using var client = new TcpClient();

                    // Connect with timeout
                    var connectTask = client.ConnectAsync(endpointIP, endpointPort);
                    var timeoutTask = Task.Delay(5000, cancellationToken);

                    await Task.WhenAny(connectTask, timeoutTask);

                    if (timeoutTask.IsCompleted)
                    {
                        ////Console.WriteLine($"Connection to endpoint {endpointName} timed out");
                        return JsonSerializer.Serialize(new
                        {
                            Status = "Error",
                            Message = "Connection to endpoint timed out"
                        });
                    }

                    if (!client.Connected)
                    {
                        ////Console.WriteLine($"Failed to connect to endpoint {endpointName}");
                        return JsonSerializer.Serialize(new
                        {
                            Status = "Error",
                            Message = "Failed to connect to endpoint"
                        });
                    }

                    // Connected successfully
                    ////Console.WriteLine($"Connected to endpoint {endpointName}, sending {action} command");
                    using NetworkStream stream = client.GetStream();
                    byte[] commandBytes = Encoding.UTF8.GetBytes(actionCommandJson);
                    await stream.WriteAsync(commandBytes, 0, commandBytes.Length, cancellationToken);

                    // Read response with timeout
                    ////Console.WriteLine($"Waiting for response from endpoint {endpointName}");
                    var buffer = new byte[8192];
                    var readTask = stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                    timeoutTask = Task.Delay(5000, cancellationToken);

                    await Task.WhenAny(readTask, timeoutTask);

                    if (timeoutTask.IsCompleted)
                    {
                        ////Console.WriteLine($"Reading response from endpoint {endpointName} timed out");
                        // For actions like RESTART/SHUTDOWN, timeout might be expected
                        return JsonSerializer.Serialize(new
                        {
                            Status = "OK",
                            Message = $"{action} command sent to endpoint (no response received)"
                        });
                    }

                    // Got response
                    int bytesRead = await readTask;
                    string response = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    ////Console.WriteLine($"Received {bytesRead} bytes response from endpoint {endpointName}");

                    return response;
                }
                catch (Exception ex)
                {
                    ////Console.WriteLine($"Error sending {action} command to endpoint {endpointName}: {ex.Message}");
                    return JsonSerializer.Serialize(new
                    {
                        Status = "Error",
                        Message = $"Error sending {action} command to endpoint: {ex.Message}"
                    });
                }
            }
            catch (Exception ex)
            {
                ////Console.WriteLine($"Error processing endpoint {action} command: {ex.Message}");
                return JsonSerializer.Serialize(new
                {
                    Status = "Error",
                    Message = $"Error processing endpoint {action} command: {ex.Message}"
                });
            }
        }
        private async Task<string> ForwardCommandToEndpoint(JsonElement commandObj, CancellationToken cancellationToken)
        {
            try
            {
                // Extract required parameters
                if (!commandObj.TryGetProperty("EndpointName", out JsonElement endpointNameElement))
                {
                    return JsonSerializer.Serialize(new
                    {
                        Status = "Error",
                        Message = "Endpoint name not specified"
                    });
                }

                if (!commandObj.TryGetProperty("ForwardedCommand", out JsonElement forwardedCommandElement))
                {
                    return JsonSerializer.Serialize(new
                    {
                        Status = "Error",
                        Message = "Forwarded command not specified"
                    });
                }

                string endpointName = endpointNameElement.GetString();
                string forwardedCommand = forwardedCommandElement.GetString();
                string endpointIP = "";
                int endpointPort = 0;

                // Get endpoint IP/port if provided
                if (commandObj.TryGetProperty("EndpointIP", out JsonElement endpointIPElement))
                {
                    endpointIP = endpointIPElement.GetString();
                }

                if (commandObj.TryGetProperty("EndpointPort", out JsonElement endpointPortElement))
                {
                    endpointPort = endpointPortElement.GetInt32();
                }

                // Find the endpoint in our list
                var endpoint = _endpointService.GetConnectedEndpoints()
                    .FirstOrDefault(e => e.Name == endpointName ||
                                        (e.EndpointIP == endpointIP && !string.IsNullOrEmpty(endpointIP)));

                if (endpoint == null)
                {
                    // If endpoint not found in our list, but IP/port provided, try direct connection
                    if (string.IsNullOrEmpty(endpointIP) || endpointPort == 0)
                    {
                        return JsonSerializer.Serialize(new
                        {
                            Status = "Error",
                            Message = $"Endpoint '{endpointName}' not found"
                        });
                    }

                    // Use provided IP/port
                    ////Console.WriteLine($"Endpoint {endpointName} not in connected list, attempting direct connection to {endpointIP}:{endpointPort}");
                }
                else
                {
                    // Use endpoint from our list
                    endpointIP = endpoint.EndpointIP;
                    endpointPort = endpoint.EndpointPort;
                   // //Console.WriteLine($"Found endpoint {endpointName} at {endpointIP}:{endpointPort}");
                }

                // Connect to the endpoint and forward the command
                try
                {
                   // //Console.WriteLine($"Connecting to endpoint at {endpointIP}:{endpointPort}");
                    using var client = new TcpClient();

                    // Connect with timeout
                    var connectTask = client.ConnectAsync(endpointIP, endpointPort);
                    var timeoutTask = Task.Delay(5000, cancellationToken);

                    await Task.WhenAny(connectTask, timeoutTask);

                    if (timeoutTask.IsCompleted)
                    {
                        ////Console.WriteLine($"Connection to endpoint {endpointName} timed out");
                        return JsonSerializer.Serialize(new
                        {
                            Status = "Error",
                            Message = "Connection to endpoint timed out"
                        });
                    }

                    if (!client.Connected)
                    {
                        ////Console.WriteLine($"Failed to connect to endpoint {endpointName}");
                        return JsonSerializer.Serialize(new
                        {
                            Status = "Error",
                            Message = "Failed to connect to endpoint"
                        });
                    }

                    // Connected successfully, send the command
                    ////Console.WriteLine($"Connected to endpoint {endpointName}, forwarding command");
                    using NetworkStream stream = client.GetStream();
                    byte[] commandBytes = Encoding.UTF8.GetBytes(forwardedCommand);
                    await stream.WriteAsync(commandBytes, 0, commandBytes.Length, cancellationToken);

                    // Read response with timeout
                    ////Console.WriteLine($"Waiting for response from endpoint {endpointName}");
                    var buffer = new byte[16384]; // Larger buffer for potential large responses
                    var readTask = stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                    timeoutTask = Task.Delay(10000, cancellationToken); // 10 second timeout

                    await Task.WhenAny(readTask, timeoutTask);

                    if (timeoutTask.IsCompleted)
                    {
                        ////Console.WriteLine($"Reading response from endpoint {endpointName} timed out");
                        return JsonSerializer.Serialize(new
                        {
                            Status = "Error",
                            Message = "Reading response from endpoint timed out"
                        });
                    }

                    // Got response
                    int bytesRead = await readTask;
                    string response = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                   // //Console.WriteLine($"Received {bytesRead} bytes response from endpoint {endpointName}");

                    return response;
                }
                catch (Exception ex)
                {
                    ////Console.WriteLine($"Error forwarding command to endpoint {endpointName}: {ex.Message}");
                    return JsonSerializer.Serialize(new
                    {
                        Status = "Error",
                        Message = $"Error forwarding command to endpoint: {ex.Message}"
                    });
                }
            }
            catch (Exception ex)
            {
                ////Console.WriteLine($"Error in command forwarding: {ex.Message}");
                return JsonSerializer.Serialize(new
                {
                    Status = "Error",
                    Message = $"Error in command forwarding: {ex.Message}"
                });
            }
        }
        private async Task<string> HandleEndpointActionAsync(JsonElement commandObj, string action, CancellationToken cancellationToken)
        {
            try
            {
                // Get endpoint information from the command
                if (!commandObj.TryGetProperty("EndpointName", out JsonElement endpointNameElement))
                {
                    return JsonSerializer.Serialize(new
                    {
                        Status = "Error",
                        Message = "Endpoint name not specified"
                    });
                }

                string endpointName = endpointNameElement.GetString();
                var endpoint = _endpointService.GetConnectedEndpoints()
                    .FirstOrDefault(e => e.Name == endpointName);

                if (endpoint == null)
                {
                    // Also try to find by IP if provided
                    if (commandObj.TryGetProperty("EndpointIP", out JsonElement endpointIPElement))
                    {
                        string endpointIP = endpointIPElement.GetString();
                        endpoint = _endpointService.GetConnectedEndpoints()
                            .FirstOrDefault(e => e.EndpointIP == endpointIP);

                        if (endpoint == null)
                        {
                            return JsonSerializer.Serialize(new
                            {
                                Status = "Error",
                                Message = $"Endpoint not found with name '{endpointName}' or IP '{endpointIP}'"
                            });
                        }
                    }
                    else
                    {
                        return JsonSerializer.Serialize(new
                        {
                            Status = "Error",
                            Message = $"Endpoint '{endpointName}' not found"
                        });
                    }
                }

                // Create command to send to the endpoint
                var forwardCommand = new
                {
                    Command = action
                };

                // Connect to the endpoint and send the command
                try
                {
                    using var client = new TcpClient();
                    var timeoutTask = Task.Delay(5000, cancellationToken); // 5 second timeout
                    var connectTask = client.ConnectAsync(endpoint.EndpointIP, endpoint.EndpointPort);

                    await Task.WhenAny(connectTask, timeoutTask);

                    if (timeoutTask.IsCompleted || !client.Connected)
                    {
                        return JsonSerializer.Serialize(new
                        {
                            Status = "Error",
                            Message = $"Connection to endpoint timed out or failed"
                        });
                    }

                    // Send the command
                    using NetworkStream stream = client.GetStream();
                    string commandJson = JsonSerializer.Serialize(forwardCommand);
                    byte[] commandBytes = Encoding.UTF8.GetBytes(commandJson);
                    await stream.WriteAsync(commandBytes, 0, commandBytes.Length, cancellationToken);

                    // Read the response with timeout
                    var buffer = new byte[8192];
                    var readTask = stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                    timeoutTask = Task.Delay(10000, cancellationToken); // 10 second timeout for reading response

                    await Task.WhenAny(readTask, timeoutTask);

                    if (timeoutTask.IsCompleted)
                    {
                        return JsonSerializer.Serialize(new
                        {
                            Status = "Error",
                            Message = $"Reading response from endpoint timed out"
                        });
                    }

                    int bytesRead = await readTask;
                    string response = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                    return response;
                }
                catch (Exception ex)
                {
                    return JsonSerializer.Serialize(new
                    {
                        Status = "Error",
                        Message = $"Error communicating with endpoint: {ex.Message}"
                    });
                }
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new
                {
                    Status = "Error",
                    Message = $"Error handling endpoint action: {ex.Message}"
                });
            }
        }
        private async Task<string> ForwardCommandToEndpointAsync(JsonElement commandObj, CancellationToken cancellationToken)
        {
            try
            {
                // Extract required parameters
                if (!commandObj.TryGetProperty("EndpointName", out JsonElement endpointNameElement))
                {
                    return JsonSerializer.Serialize(new
                    {
                        Status = "Error",
                        Message = "Endpoint name not specified"
                    });
                }

                string endpointName = endpointNameElement.GetString();
                string endpointIP = "";
                int endpointPort = 0;

                // Get endpoint IP/port if provided
                if (commandObj.TryGetProperty("EndpointIP", out JsonElement endpointIPElement))
                {
                    endpointIP = endpointIPElement.GetString();
                }

                if (commandObj.TryGetProperty("EndpointPort", out JsonElement endpointPortElement))
                {
                    endpointPort = endpointPortElement.GetInt32();
                }

                // Find the endpoint in our connected endpoints list
                var endpoint = _endpointService.GetConnectedEndpoints()
                    .FirstOrDefault(e => e.Name == endpointName);

                if (endpoint == null && !string.IsNullOrEmpty(endpointIP))
                {
                    // Try finding by IP instead
                    endpoint = _endpointService.GetConnectedEndpoints()
                        .FirstOrDefault(e => e.EndpointIP == endpointIP);
                }

                // If we still can't find the endpoint in our list, but we have IP/port info,
                // we can try to connect directly to the endpoint
                if (endpoint == null)
                {
                    if (string.IsNullOrEmpty(endpointIP) || endpointPort == 0)
                    {
                        return JsonSerializer.Serialize(new
                        {
                            Status = "Error",
                            Message = $"Endpoint '{endpointName}' not found and IP/port not provided"
                        });
                    }

                    // Create a virtual endpoint since it's not in our list
                    //Console.WriteLine($"Endpoint {endpointName} not in server's list, attempting direct connection to {endpointIP}:{endpointPort}");
                }
                else
                {
                    // Use the endpoint from our list
                    endpointIP = endpoint.EndpointIP;
                    endpointPort = endpoint.EndpointPort;
                    //Console.WriteLine($"Found endpoint {endpointName} in server list at {endpointIP}:{endpointPort}");
                }

                if (!commandObj.TryGetProperty("ForwardedCommand", out JsonElement forwardedCommandElement))
                {
                    return JsonSerializer.Serialize(new
                    {
                        Status = "Error",
                        Message = "Forwarded command not specified"
                    });
                }

                string forwardedCommand = forwardedCommandElement.GetString();

                // Connect to the endpoint and forward the command
                try
                {
                    //Console.WriteLine($"Attempting to connect to endpoint at {endpointIP}:{endpointPort}");
                    using var client = new TcpClient();
                    var timeoutTask = Task.Delay(5000, cancellationToken); // 5 second timeout
                    var connectTask = client.ConnectAsync(endpointIP, endpointPort);

                    await Task.WhenAny(connectTask, timeoutTask);

                    if (timeoutTask.IsCompleted)
                    {
                        //Console.WriteLine($"Connection to endpoint {endpointName} timed out");
                        return JsonSerializer.Serialize(new
                        {
                            Status = "Error",
                            Message = $"Connection to endpoint timed out"
                        });
                    }

                    if (!client.Connected)
                    {
                        //Console.WriteLine($"Failed to connect to endpoint {endpointName}");
                        return JsonSerializer.Serialize(new
                        {
                            Status = "Error",
                            Message = $"Failed to connect to endpoint"
                        });
                    }

                    // Send the forwarded command
                    //Console.WriteLine($"Connected to endpoint {endpointName}, sending command");
                    using NetworkStream stream = client.GetStream();
                    byte[] commandBytes = Encoding.UTF8.GetBytes(forwardedCommand);
                    await stream.WriteAsync(commandBytes, 0, commandBytes.Length, cancellationToken);

                    // Read the response with timeout
                    //Console.WriteLine($"Waiting for response from endpoint {endpointName}");
                    var buffer = new byte[16384]; // Larger buffer for potentially large responses
                    var readTask = stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                    timeoutTask = Task.Delay(15000, cancellationToken); // 15 second timeout for reading response

                    await Task.WhenAny(readTask, timeoutTask);

                    if (timeoutTask.IsCompleted)
                    {
                        //Console.WriteLine($"Reading response from endpoint {endpointName} timed out");
                        return JsonSerializer.Serialize(new
                        {
                            Status = "Error",
                            Message = $"Reading response from endpoint timed out"
                        });
                    }

                    int bytesRead = await readTask;
                    string response = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    //Console.WriteLine($"Received {bytesRead} bytes response from endpoint {endpointName}");

                    return response;
                }
                catch (Exception ex)
                {
                    //Console.WriteLine($"Error forwarding command to endpoint {endpointName}: {ex.Message}");
                    return JsonSerializer.Serialize(new
                    {
                        Status = "Error",
                        Message = $"Error forwarding command to endpoint: {ex.Message}"
                    });
                }
            }
            catch (Exception ex)
            {
                //Console.WriteLine($"Error in command forwarding: {ex.Message}");
                return JsonSerializer.Serialize(new
                {
                    Status = "Error",
                    Message = $"Error in command forwarding: {ex.Message}"
                });
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
            //Console.WriteLine("Server restart initiated...");

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
                    //Console.WriteLine("Restart not supported on this platform. Please restart manually.");
                }
            }
            catch (Exception ex)
            {
                //Console.WriteLine($"Error creating restart script: {ex.Message}");
            }

            // Exit the application
            Environment.Exit(42); // Special code that a wrapper script could use to restart
        }

        private void ShutdownServer()
        {
            //Console.WriteLine("Server shutdown initiated...");
            Environment.Exit(0);
        }
    }
}