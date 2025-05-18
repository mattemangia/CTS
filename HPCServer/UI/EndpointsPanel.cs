using System;
using System.Collections.Generic;
using Terminal.Gui;
using ParallelComputingServer.Services;
using ParallelComputingServer.Models;
using System.Threading.Tasks;
using System.Text.Json;

namespace ParallelComputingServer.UI
{
    public class EndpointsPanel : Dialog
    {
        private readonly EndpointService _endpointService;
        private readonly NetworkService _networkService; // Add reference to NetworkService
        private readonly ListView _listView;
        private readonly Label _detailsLabel;
        private readonly Label _statusLabel;
        private List<EndpointInfo> _endpoints;
        private bool _operationInProgress = false;

        public EndpointsPanel(EndpointService endpointService)
            : base("Connected Endpoints", 80, 30)
        {
            _endpointService = endpointService;

            // Create heading with fixed position
            Add(new Label(1, 1, "List of compute endpoints connected to the server:"));

            // Create list view for endpoints
            _listView = new ListView()
            {
                X = 1,
                Y = 3,
                Width = Dim.Fill() - 2,
                Height = 12 // Fixed height for list view
            };

            // Create details frame
            var detailsFrame = new FrameView("Endpoint Details")
            {
                X = 1,
                Y = 16, // Fixed position
                Width = Dim.Fill() - 2,
                Height = 8 // Fixed height for details
            };

            _detailsLabel = new Label("")
            {
                X = 1,
                Y = 0,
                Width = Dim.Fill() - 2,
                Height = 7 // Fixed height
            };

            detailsFrame.Add(_detailsLabel);

            // Add status label for operation feedback
            _statusLabel = new Label("")
            {
                X = 1,
                Y = 24, // Just above the buttons
                Width = Dim.Fill() - 2,
                Height = 1
            };
            Add(_statusLabel);

            // Set selected item handler
            _listView.SelectedItemChanged += OnEndpointSelected;

            // Add key handler for Enter key
            _listView.KeyPress += (args) => {
                if (args.KeyEvent.Key == Key.Enter)
                {
                    ShowEndpointMenu();
                    args.Handled = true;
                }
            };

            // Create a button bar with fixed positions
            var buttonBar = new View()
            {
                X = 0,
                Y = 25, // Fixed position for buttons
                Width = Dim.Fill(),
                Height = 1
            };

            var manageButton = new Button(10, 0, "Manage");
            manageButton.Clicked += () => ShowEndpointMenu();
            buttonBar.Add(manageButton);

            var refreshButton = new Button(30, 0, "Refresh");
            refreshButton.Clicked += () => UpdateEndpointList();
            buttonBar.Add(refreshButton);

            var closeButton = new Button(55, 0, "Close");
            closeButton.Clicked += () => Application.RequestStop();
            buttonBar.Add(closeButton);

            Add(_listView, detailsFrame, buttonBar);

            // Update the list
            UpdateEndpointList();

            // Register for updates
            _endpointService.EndpointsUpdated += (sender, args) => UpdateEndpointList();
        }

        private void ShowEndpointMenu()
        {
            if (_endpoints.Count == 0 || _listView.SelectedItem <= 0 || _listView.SelectedItem > _endpoints.Count)
            {
                return; // No valid selection
            }

            var endpoint = _endpoints[_listView.SelectedItem - 1];

            // Create a dialog for the endpoint menu
            var menuDialog = new Dialog($"Manage Endpoint: {endpoint.Name}", 60, 12);

            var restartButton = new Button(1, 1, "Restart Endpoint");
            restartButton.Clicked += () => {
                Application.RequestStop(); // Close menu
                RestartEndpoint(endpoint);
            };
            menuDialog.Add(restartButton);

            var shutdownButton = new Button(1, 3, "Shutdown Endpoint");
            shutdownButton.Clicked += () => {
                Application.RequestStop(); // Close menu
                ShutdownEndpoint(endpoint);
            };
            menuDialog.Add(shutdownButton);

            var diagnosticsButton = new Button(1, 5, "Run Diagnostics");
            diagnosticsButton.Clicked += () => {
                Application.RequestStop(); // Close menu
                RunEndpointDiagnostics(endpoint);
            };
            menuDialog.Add(diagnosticsButton);

            var cancelButton = new Button(1, 7, "Cancel");
            cancelButton.Clicked += () => Application.RequestStop();
            menuDialog.Add(cancelButton);

            Application.Run(menuDialog);
        }

        private void RestartEndpoint(EndpointInfo endpoint)
        {
            if (_operationInProgress)
            {
                MessageBox.ErrorQuery("Operation In Progress", "Please wait for the current operation to complete.", "OK");
                return;
            }

            // Confirm restart
            var result = MessageBox.Query(
                "Restart Endpoint",
                $"Are you sure you want to restart endpoint '{endpoint.Name}'?",
                "Yes", "No");

            if (result == 0) // Yes
            {
                _operationInProgress = true;
                _statusLabel.Text = $"Restarting endpoint {endpoint.Name}...";
                _statusLabel.SetNeedsDisplay();

                Task.Run(async () =>
                {
                    try
                    {
                        // Connect directly to the endpoint using IP and port
                        using var client = new System.Net.Sockets.TcpClient();

                        try
                        {
                            await client.ConnectAsync(endpoint.EndpointIP, endpoint.EndpointPort);
                        }
                        catch
                        {
                            Application.MainLoop.Invoke(() =>
                            {
                                _operationInProgress = false;
                                _statusLabel.Text = $"Failed to connect to endpoint {endpoint.Name}";
                                _statusLabel.SetNeedsDisplay();
                            });
                            return;
                        }

                        // Create and send restart command
                        var command = new { Command = "RESTART" };
                        var commandJson = System.Text.Json.JsonSerializer.Serialize(command);
                        var commandBytes = System.Text.Encoding.UTF8.GetBytes(commandJson);

                        var stream = client.GetStream();
                        await stream.WriteAsync(commandBytes, 0, commandBytes.Length);

                        // Read response
                        var buffer = new byte[4096];
                        var responseTask = stream.ReadAsync(buffer, 0, buffer.Length);
                        var timeoutTask = Task.Delay(5000);

                        await Task.WhenAny(responseTask, timeoutTask);

                        Application.MainLoop.Invoke(() =>
                        {
                            _operationInProgress = false;
                            if (responseTask.IsCompleted)
                            {
                                int bytesRead = responseTask.Result;
                                string response = System.Text.Encoding.UTF8.GetString(buffer, 0, bytesRead);
                                _statusLabel.Text = $"Restart command sent to {endpoint.Name}: {response}";
                            }
                            else
                            {
                                _statusLabel.Text = $"Restart command sent to {endpoint.Name} (no response received)";
                            }
                            _statusLabel.SetNeedsDisplay();
                        });
                    }
                    catch (Exception ex)
                    {
                        Application.MainLoop.Invoke(() =>
                        {
                            _operationInProgress = false;
                            _statusLabel.Text = $"Error restarting endpoint: {ex.Message}";
                            _statusLabel.SetNeedsDisplay();
                        });
                    }
                });
            }
        }

        private void ShutdownEndpoint(EndpointInfo endpoint)
        {
            if (_operationInProgress)
            {
                MessageBox.ErrorQuery("Operation In Progress", "Please wait for the current operation to complete.", "OK");
                return;
            }

            // Confirm shutdown
            var result = MessageBox.Query(
                "Shutdown Endpoint",
                $"Are you sure you want to shutdown endpoint '{endpoint.Name}'?",
                "Yes", "No");

            if (result == 0) // Yes
            {
                _operationInProgress = true;
                _statusLabel.Text = $"Shutting down endpoint {endpoint.Name}...";
                _statusLabel.SetNeedsDisplay();

                Task.Run(async () =>
                {
                    try
                    {
                        // Connect directly to the endpoint using IP and port
                        using var client = new System.Net.Sockets.TcpClient();

                        try
                        {
                            await client.ConnectAsync(endpoint.EndpointIP, endpoint.EndpointPort);
                        }
                        catch
                        {
                            Application.MainLoop.Invoke(() =>
                            {
                                _operationInProgress = false;
                                _statusLabel.Text = $"Failed to connect to endpoint {endpoint.Name}";
                                _statusLabel.SetNeedsDisplay();
                            });
                            return;
                        }

                        // Create and send shutdown command
                        var command = new { Command = "SHUTDOWN" };
                        var commandJson = System.Text.Json.JsonSerializer.Serialize(command);
                        var commandBytes = System.Text.Encoding.UTF8.GetBytes(commandJson);

                        var stream = client.GetStream();
                        await stream.WriteAsync(commandBytes, 0, commandBytes.Length);

                        // Read response
                        var buffer = new byte[4096];
                        var responseTask = stream.ReadAsync(buffer, 0, buffer.Length);
                        var timeoutTask = Task.Delay(5000);

                        await Task.WhenAny(responseTask, timeoutTask);

                        Application.MainLoop.Invoke(() =>
                        {
                            _operationInProgress = false;
                            if (responseTask.IsCompleted)
                            {
                                int bytesRead = responseTask.Result;
                                string response = System.Text.Encoding.UTF8.GetString(buffer, 0, bytesRead);
                                _statusLabel.Text = $"Shutdown command sent to {endpoint.Name}: {response}";
                            }
                            else
                            {
                                _statusLabel.Text = $"Shutdown command sent to {endpoint.Name} (no response received)";
                            }
                            _statusLabel.SetNeedsDisplay();
                        });
                    }
                    catch (Exception ex)
                    {
                        Application.MainLoop.Invoke(() =>
                        {
                            _operationInProgress = false;
                            _statusLabel.Text = $"Error shutting down endpoint: {ex.Message}";
                            _statusLabel.SetNeedsDisplay();
                        });
                    }
                });
            }
        }

        private void RunEndpointDiagnostics(EndpointInfo endpoint)
        {
            if (_operationInProgress)
            {
                MessageBox.ErrorQuery("Operation In Progress", "Please wait for the current operation to complete.", "OK");
                return;
            }

            _operationInProgress = true;
            _statusLabel.Text = $"Running diagnostics on endpoint {endpoint.Name}...";
            _statusLabel.SetNeedsDisplay();

            Task.Run(async () =>
            {
                try
                {
                    // Connect directly to the endpoint using IP and port
                    using var client = new System.Net.Sockets.TcpClient();

                    try
                    {
                        await client.ConnectAsync(endpoint.EndpointIP, endpoint.EndpointPort);
                    }
                    catch
                    {
                        Application.MainLoop.Invoke(() =>
                        {
                            _operationInProgress = false;
                            _statusLabel.Text = $"Failed to connect to endpoint {endpoint.Name}";
                            _statusLabel.SetNeedsDisplay();
                        });
                        return;
                    }

                    // Create and send diagnostics command
                    var command = new { Command = "DIAGNOSTICS" };
                    var commandJson = System.Text.Json.JsonSerializer.Serialize(command);
                    var commandBytes = System.Text.Encoding.UTF8.GetBytes(commandJson);

                    var stream = client.GetStream();
                    await stream.WriteAsync(commandBytes, 0, commandBytes.Length);

                    // Read response
                    var buffer = new byte[16384]; // Larger buffer for diagnostics results
                    var responseTask = stream.ReadAsync(buffer, 0, buffer.Length);
                    var timeoutTask = Task.Delay(15000); // 15 second timeout for diagnostics

                    await Task.WhenAny(responseTask, timeoutTask);

                    Application.MainLoop.Invoke(() =>
                    {
                        _operationInProgress = false;
                        if (responseTask.IsCompleted)
                        {
                            int bytesRead = responseTask.Result;
                            string response = System.Text.Encoding.UTF8.GetString(buffer, 0, bytesRead);

                            // Extract diagnostic results
                            string diagnosticsResult = "";
                            try
                            {
                                // Try to extract the DiagnosticsResult field
                                if (response.Contains("DiagnosticsResult"))
                                {
                                    var jsonResponse = JsonDocument.Parse(response).RootElement;
                                    if (jsonResponse.TryGetProperty("DiagnosticsResult", out var resultElement))
                                    {
                                        diagnosticsResult = resultElement.GetString();
                                        // Replace escaped newlines with actual newlines
                                        diagnosticsResult = diagnosticsResult.Replace("\\n", "\n").Replace("\\r", "");
                                    }
                                }
                                else
                                {
                                    diagnosticsResult = response; // Just use the raw response if no DiagnosticsResult field
                                }
                            }
                            catch
                            {
                                diagnosticsResult = "Error parsing diagnostics result: " + response.Substring(0, Math.Min(200, response.Length));
                            }

                            // Show diagnostics result in a dialog
                            ShowDiagnosticsResult(endpoint.Name, diagnosticsResult);

                            _statusLabel.Text = $"Diagnostics completed for {endpoint.Name}";
                        }
                        else
                        {
                            _statusLabel.Text = $"Diagnostics timed out for {endpoint.Name}";
                        }
                        _statusLabel.SetNeedsDisplay();
                    });
                }
                catch (Exception ex)
                {
                    Application.MainLoop.Invoke(() =>
                    {
                        _operationInProgress = false;
                        _statusLabel.Text = $"Error running diagnostics: {ex.Message}";
                        _statusLabel.SetNeedsDisplay();
                    });
                }
            });
        }

        private void ShowDiagnosticsResult(string endpointName, string diagnosticsResult)
        {
            var diagDialog = new Dialog($"Diagnostics Result: {endpointName}", 80, 20);

            var scrollView = new ScrollView()
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill() - 2,
                ContentSize = new Size(76, Math.Max(20, diagnosticsResult.Split('\n').Length + 1)),
                ShowVerticalScrollIndicator = true,
                ShowHorizontalScrollIndicator = true
            };

            var resultsText = new Label(diagnosticsResult)
            {
                X = 0,
                Y = 0,
                Width = 76,
                Height = Math.Max(20, diagnosticsResult.Split('\n').Length + 1)
            };

            scrollView.Add(resultsText);
            diagDialog.Add(scrollView);

            var closeButton = new Button("Close");
            closeButton.X = Pos.Center();
            closeButton.Y = Pos.Bottom(diagDialog) - 3;
            closeButton.Clicked += () => { Application.RequestStop(); };
            diagDialog.Add(closeButton);

            Application.Run(diagDialog);
        }

        private void UpdateEndpointList()
        {
            _endpoints = _endpointService.GetConnectedEndpoints();
            var endpointList = new List<string>();

            if (_endpoints.Count == 0)
            {
                endpointList.Add("No endpoints connected");
                _detailsLabel.Text = "";
            }
            else
            {
                endpointList.Add($"{"Name",-20} {"IP Address",-15} {"Status",-12} {"GPU",-5} {"CPU Load",-10}");
                foreach (var endpoint in _endpoints)
                {
                    endpointList.Add($"{endpoint.Name,-20} {endpoint.EndpointIP,-15} {endpoint.Status,-12} {(endpoint.GpuEnabled ? "Yes" : "No"),-5} {endpoint.CpuLoadPercent,10:F1}%");
                }

                // If there's a selection, refresh details
                if (_listView.SelectedItem > 0 && _listView.SelectedItem <= _endpoints.Count)
                {
                    ShowEndpointDetails(_endpoints[_listView.SelectedItem - 1]);
                }
                else if (_endpoints.Count > 0)
                {
                    // Select the first endpoint if none is selected
                    _listView.SelectedItem = 1;
                    ShowEndpointDetails(_endpoints[0]);
                }
            }

            _listView.SetSource(endpointList);
            _listView.SetNeedsDisplay();
        }

        private void OnEndpointSelected(ListViewItemEventArgs args)
        {
            if (_endpoints.Count == 0 || args.Item <= 0 || args.Item > _endpoints.Count)
            {
                _detailsLabel.Text = "";
                return;
            }

            ShowEndpointDetails(_endpoints[args.Item - 1]);
        }

        private void ShowEndpointDetails(EndpointInfo endpoint)
        {
            var details = new System.Text.StringBuilder();
            details.AppendLine($"Name: {endpoint.Name}");
            details.AppendLine($"IP Address: {endpoint.EndpointIP}");
            details.AppendLine($"Port: {endpoint.EndpointPort}");
            details.AppendLine($"Connected Since: {endpoint.ConnectedAt}");
            details.AppendLine($"Status: {endpoint.Status}");
            details.AppendLine($"GPU Enabled: {(endpoint.GpuEnabled ? "Yes" : "No")}");
            details.AppendLine($"CPU Load: {endpoint.CpuLoadPercent:F1}%");
            details.AppendLine($"Current Task: {endpoint.CurrentTask}");

            if (!string.IsNullOrEmpty(endpoint.HardwareInfo))
            {
                details.AppendLine($"Hardware Info: {endpoint.HardwareInfo}");
            }

            _detailsLabel.Text = details.ToString();
            _detailsLabel.SetNeedsDisplay();
        }
    }
}