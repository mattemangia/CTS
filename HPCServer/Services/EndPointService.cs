using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ParallelComputingServer.Config;
using ParallelComputingServer.Models;

namespace ParallelComputingServer.Services
{
    public class EndpointService
    {
        private readonly ServerConfig _config;
        private List<EndpointInfo> _connectedEndpoints = new();

        // Event handlers for UI updates
        public event EventHandler EndpointsUpdated;

        public EndpointService(ServerConfig config)
        {
            _config = config;
        }

        public List<EndpointInfo> GetConnectedEndpoints() => _connectedEndpoints;

        public async Task StartEndpointServiceAsync(CancellationToken cancellationToken)
        {
            var listener = new TcpListener(IPAddress.Any, _config.EndpointPort);
            listener.Start();

            //Console.WriteLine($"Endpoint service listening on port {_config.EndpointPort}...");

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

                    // Handle each endpoint in a separate task
                    _ = HandleEndpointAsync(client, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation token is triggered
            }
            catch (Exception ex)
            {
                //Console.WriteLine($"Endpoint service error: {ex.Message}");
            }
            finally
            {
                listener.Stop();
            }

            //Console.WriteLine("Endpoint service stopped.");
        }


        private async Task HandleEndpointAsync(TcpClient client, CancellationToken cancellationToken)
        {
            try
            {
                // Get endpoint connection info
                var endpoint = (IPEndPoint)client.Client.RemoteEndPoint;

                //Console.WriteLine($"Endpoint connected from: {endpoint.Address}:{endpoint.Port}, waiting for registration...");

                // Wait for initial registration message
                using var stream = client.GetStream();
                var buffer = new byte[4096];

                // Read registration message
                var bytesRead = await stream.ReadAsync(buffer, cancellationToken);
                if (bytesRead == 0) return; // Client disconnected immediately

                var message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                EndpointInfo endpointInfo = null;

                try
                {
                    // Log the incoming message for debugging
                    //Console.WriteLine($"Received endpoint registration: {message}");

                    // Try to parse registration message
                    var registrationObj = JsonSerializer.Deserialize<JsonElement>(message);

                    if (registrationObj.TryGetProperty("Command", out JsonElement commandElement) &&
                        commandElement.GetString() == "REGISTER")
                    {
                        // Extract endpoint information
                        endpointInfo = new EndpointInfo
                        {
                            EndpointIP = endpoint.Address.ToString(),
                            ConnectedAt = DateTime.Now
                        };

                        if (registrationObj.TryGetProperty("Name", out JsonElement nameElement))
                        {
                            endpointInfo.Name = nameElement.GetString();
                        }
                        else
                        {
                            endpointInfo.Name = $"Endpoint-{endpoint.Address}-{Guid.NewGuid().ToString().Substring(0, 8)}";
                        }

                        if (registrationObj.TryGetProperty("HardwareInfo", out JsonElement hwElement))
                        {
                            endpointInfo.HardwareInfo = hwElement.GetString();
                        }

                        if (registrationObj.TryGetProperty("GpuEnabled", out JsonElement gpuElement))
                        {
                            endpointInfo.GpuEnabled = gpuElement.GetBoolean();
                        }

                        // Check if the endpoint has specified a listening port
                        if (registrationObj.TryGetProperty("ListeningPort", out JsonElement listeningPortElement))
                        {
                            // Use the listening port provided by the endpoint
                            endpointInfo.EndpointPort = listeningPortElement.GetInt32();
                            //Console.WriteLine($"Registered endpoint {endpointInfo.Name} listening port: {endpointInfo.EndpointPort}");
                        }
                        else
                        {
                            // Fallback to the source port if no listening port is provided (for backward compatibility)
                            endpointInfo.EndpointPort = endpoint.Port;
                            //Console.WriteLine($"Warning: Endpoint {endpointInfo.Name} did not provide a listening port, using source port: {endpointInfo.EndpointPort}");
                        }

                        // Send registration confirmation
                        var response = new
                        {
                            Status = "OK",
                            Message = "Registration successful",
                            EndpointId = Guid.NewGuid().ToString()
                        };

                        var responseJson = JsonSerializer.Serialize(response);
                        var responseBytes = Encoding.UTF8.GetBytes(responseJson);
                        await stream.WriteAsync(responseBytes, cancellationToken);

                        // Add to connected endpoints
                        _connectedEndpoints.Add(endpointInfo);
                        EndpointsUpdated?.Invoke(this, EventArgs.Empty);

                        //Console.WriteLine($"Endpoint registered: {endpointInfo.Name} ({endpointInfo.EndpointIP})");
                    }
                    else
                    {
                        // Invalid registration
                        //Console.WriteLine($"Invalid registration message received: {message}");
                        var errorResponse = "{\"Status\":\"Error\",\"Message\":\"Invalid registration message. Expected REGISTER command.\"}";
                        var errorBytes = Encoding.UTF8.GetBytes(errorResponse);
                        await stream.WriteAsync(errorBytes, cancellationToken);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    //Console.WriteLine($"Error processing endpoint registration: {ex.Message}");
                    var errorResponse = $"{{\"Status\":\"Error\",\"Message\":\"{ex.Message}\"}}";
                    var errorBytes = Encoding.UTF8.GetBytes(errorResponse);
                    await stream.WriteAsync(errorBytes, cancellationToken);
                    return;
                }

                // Handle ongoing communication with the endpoint
                while (!cancellationToken.IsCancellationRequested && client.Connected)
                {
                    try
                    {
                        bytesRead = await stream.ReadAsync(buffer, cancellationToken);
                        if (bytesRead == 0) break; // Endpoint disconnected

                        message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                        //Console.WriteLine($"Received message from endpoint: {message}");

                        // Check if this is a status update
                        if (message.Contains("\"Command\":\"STATUS_UPDATE\""))
                        {
                            UpdateEndpointStatus(message, endpointInfo);
                            // Send acknowledgment
                            var ackResponse = "{\"Status\":\"OK\",\"Message\":\"Status updated\"}";
                            var ackBytes = Encoding.UTF8.GetBytes(ackResponse);
                            await stream.WriteAsync(ackBytes, cancellationToken);
                            continue;
                        }

                        if (message.Contains("\"Command\":\"PING\"") || message.Contains("\"Command\":\"PONG\""))
                        {
                            // Simple ping/pong
                            var pingResponse = "{\"Status\":\"OK\",\"Message\":\"Pong\"}";
                            var pingBytes = Encoding.UTF8.GetBytes(pingResponse);
                            await stream.WriteAsync(pingBytes, cancellationToken);
                            continue;
                        }

                        // Process other endpoint messages
                        var response = ProcessEndpointMessage(message, endpointInfo);

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

                // Cleanup when endpoint disconnects
                if (endpointInfo != null)
                {
                    _connectedEndpoints.Remove(endpointInfo);
                    EndpointsUpdated?.Invoke(this, EventArgs.Empty);
                    //Console.WriteLine($"Endpoint disconnected: {endpointInfo.Name} ({endpointInfo.EndpointIP})");
                }
            }
            catch (Exception ex)
            {
                //Console.WriteLine($"Error handling endpoint: {ex.Message}");
            }
            finally
            {
                client.Dispose();
            }
        }
        public async Task<string> SendCommandToEndpoint(EndpointInfo endpoint, string commandType)
        {
            try
            {
                // First make sure the endpoint exists in our list
                var targetEndpoint = _connectedEndpoints.FirstOrDefault(e => e.Name == endpoint.Name);
                if (targetEndpoint == null)
                {
                    return "{\"Status\":\"Error\",\"Message\":\"Endpoint not found\"}";
                }

                //Console.WriteLine($"Connecting to endpoint at {endpoint.EndpointIP}:{endpoint.EndpointPort}");
                using var client = new TcpClient();

                // Connect with timeout
                var connectTask = client.ConnectAsync(endpoint.EndpointIP, endpoint.EndpointPort);
                var timeoutTask = Task.Delay(5000);

                await Task.WhenAny(connectTask, timeoutTask);

                if (timeoutTask.IsCompleted)
                {
                    //Console.WriteLine($"Connection to endpoint {endpoint.Name} timed out");
                    return "{\"Status\":\"Error\",\"Message\":\"Connection to endpoint timed out\"}";
                }

                if (!client.Connected)
                {
                    //Console.WriteLine($"Failed to connect to endpoint {endpoint.Name}");
                    return "{\"Status\":\"Error\",\"Message\":\"Failed to connect to endpoint\"}";
                }

                // Connected successfully
                //Console.WriteLine($"Connected to endpoint {endpoint.Name}, sending {commandType} command");
                using NetworkStream stream = client.GetStream();
                var command = new { Command = commandType };
                string commandJson = JsonSerializer.Serialize(command);
                byte[] commandBytes = Encoding.UTF8.GetBytes(commandJson);
                await stream.WriteAsync(commandBytes, 0, commandBytes.Length);

                // Read response with timeout
                //Console.WriteLine($"Waiting for response from endpoint {endpoint.Name}");
                var buffer = new byte[8192];
                var readTask = stream.ReadAsync(buffer, 0, buffer.Length);
                timeoutTask = Task.Delay(5000);

                await Task.WhenAny(readTask, timeoutTask);

                if (timeoutTask.IsCompleted)
                {
                    //Console.WriteLine($"Reading response from endpoint {endpoint.Name} timed out");
                    // For actions like RESTART/SHUTDOWN, timeout might be expected
                    return "{\"Status\":\"OK\",\"Message\":\"Command sent to endpoint (no response received)\"}";
                }

                // Got response
                int bytesRead = await readTask;
                string response = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                //Console.WriteLine($"Received {bytesRead} bytes response from endpoint {endpoint.Name}");

                return response;
            }
            catch (Exception ex)
            {
                //Console.WriteLine($"Error sending {commandType} command to endpoint {endpoint.Name}: {ex.Message}");
                return $"{{\"Status\":\"Error\",\"Message\":\"Error sending {commandType} command to endpoint: {ex.Message}\"}}";
            }
        }

        public async Task<string> RunEndpointDiagnostics(EndpointInfo endpoint)
        {
            return await SendCommandToEndpoint(endpoint, "DIAGNOSTICS");
        }
        private void UpdateEndpointStatus(string message, EndpointInfo endpointInfo)
        {
            try
            {
                var statusObj = JsonSerializer.Deserialize<JsonElement>(message);

                if (statusObj.TryGetProperty("CpuLoad", out JsonElement cpuLoadElement))
                {
                    endpointInfo.CpuLoadPercent = cpuLoadElement.GetDouble();
                }

                if (statusObj.TryGetProperty("Status", out JsonElement statusElement))
                {
                    endpointInfo.Status = statusElement.GetString();
                }

                if (statusObj.TryGetProperty("CurrentTask", out JsonElement taskElement))
                {
                    endpointInfo.CurrentTask = taskElement.GetString();
                }

                // Notify UI of status update
                EndpointsUpdated?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                //Console.WriteLine($"Error updating endpoint status: {ex.Message}");
            }
        }

        private string ProcessEndpointMessage(string message, EndpointInfo endpointInfo)
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

                        case "REQUEST_TASK":
                            // In a real implementation, we would assign a computation task
                            return "{\"Status\":\"OK\",\"Message\":\"No tasks available at this time\"}";

                        case "TASK_COMPLETED":
                            // Process completed task results
                            if (commandObj.TryGetProperty("TaskId", out JsonElement taskIdElement))
                            {
                                string taskId = taskIdElement.GetString();
                                //Console.WriteLine($"Task {taskId} completed by endpoint {endpointInfo.Name}");
                                return "{\"Status\":\"OK\",\"Message\":\"Task result received\"}";
                            }
                            return "{\"Status\":\"Error\",\"Message\":\"Missing task ID\"}";

                        default:
                            return "{\"Status\":\"Error\",\"Message\":\"Unknown command\"}";
                    }
                }

                return "{\"Status\":\"Error\",\"Message\":\"Invalid command format\"}";
            }
            catch (Exception ex)
            {
                //Console.WriteLine($"Error processing endpoint message: {ex.Message}");
                return $"{{\"Status\":\"Error\",\"Message\":\"{ex.Message}\"}}";
            }
        }
    }
}