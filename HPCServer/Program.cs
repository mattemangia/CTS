using ILGPU.Runtime.CPU;
using ILGPU.Runtime;
using ILGPU;
using System.Data;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net;
using Terminal.Gui;
using System.Text.Json;

namespace ParallelComputingNodeServer
{
    class Program
    {
        private static readonly CancellationTokenSource _cancellationTokenSource = new();
        private static readonly ManualResetEvent _exitEvent = new(false);
        private static ServerConfig _config = new();
        private static List<NodeInfo> _connectedNodes = new();
        private static Context _ilgpuContext;
        private static Accelerator _accelerator;
        private static View beaconIndicator;
        private static View keepAliveIndicator;
        private static bool beaconActive = false;
        private static bool keepAliveActive = false;
        private static DateTime lastBeaconTime = DateTime.MinValue;
        private static DateTime lastKeepAliveTime = DateTime.MinValue;
        private static ColorScheme beaconColorScheme;
        private static ColorScheme keepAliveColorScheme;


        static async Task Main(string[] args)
        {
            Console.WriteLine("Initializing Parallel Computing Node Server...");

            // Initialize ILGPU
            InitializeILGPU();

            // Start the beacon and server in background tasks
            Task beaconTask = StartBeaconServiceAsync(_cancellationTokenSource.Token);
            Task serverTask = StartServerAsync(_cancellationTokenSource.Token);

            // Initialize and run the TUI
            Application.Init();
            BuildUI();
            Application.Run();

            // When TUI exits, stop all services
            _cancellationTokenSource.Cancel();
            await Task.WhenAll(beaconTask, serverTask);

            // Clean up ILGPU resources
            _accelerator?.Dispose();
            _ilgpuContext?.Dispose();

            Console.WriteLine("Server shutdown complete.");
        }

        #region ILGPU Integration

        private static void InitializeILGPU()
        {
            try
            {
                Console.WriteLine("Initializing ILGPU...");
                _ilgpuContext = Context.Create(builder => builder.Default().EnableAlgorithms());

                // Try to get the best available accelerator (Cuda > CPU)
                Device preferredDevice = _ilgpuContext.GetPreferredDevice(preferCPU: false);

                try
                {
                    _accelerator = preferredDevice.CreateAccelerator(_ilgpuContext);
                    Console.WriteLine($"Using accelerator: {_accelerator.Name} ({_accelerator.AcceleratorType})");
                }
                catch (Exception)
                {
                    // If preferred device fails, fall back to CPU
                    var cpuDevice = _ilgpuContext.GetPreferredDevice(preferCPU: true);
                    _accelerator = cpuDevice.CreateAccelerator(_ilgpuContext);
                    Console.WriteLine($"Using CPU accelerator: {_accelerator.Name}");
                }

                // Run a simple test computation
                TestGPUComputation();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ILGPU initialization failed: {ex.Message}");
                Console.WriteLine("The server will continue without GPU acceleration.");
            }
        }

        private static void TestGPUComputation()
        {
            if (_accelerator == null) return;

            try
            {
                // Create a simple kernel that adds two arrays
                var kernel = _accelerator.LoadAutoGroupedStreamKernel<
                    Index1D,
                    ArrayView<float>,
                    ArrayView<float>,
                    ArrayView<float>>(
                    (index, a, b, c) => c[index] = a[index] + b[index]);

                // Prepare some test data
                const int length = 1000;
                using var aBuffer = _accelerator.Allocate1D<float>(length);
                using var bBuffer = _accelerator.Allocate1D<float>(length);
                using var cBuffer = _accelerator.Allocate1D<float>(length);

                // Initialize data
                var aData = new float[length];
                var bData = new float[length];
                for (int i = 0; i < length; i++)
                {
                    aData[i] = i;
                    bData[i] = i * 2;
                }

                // Copy data to GPU
                aBuffer.CopyFromCPU(aData);
                bBuffer.CopyFromCPU(bData);

                // Execute kernel
                kernel(length, aBuffer.View, bBuffer.View, cBuffer.View);

                // Copy results back and verify
                var results = new float[length];
                cBuffer.CopyToCPU(results);

                Console.WriteLine("GPU computation test successful!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"GPU computation test failed: {ex.Message}");
            }
        }

        #endregion

        #region Network Services

        private static async Task StartBeaconServiceAsync(CancellationToken cancellationToken)
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
                        NodesConnected = _connectedNodes.Count,
                        GpuEnabled = _accelerator != null && !(_accelerator is CPUAccelerator),
                        Timestamp = DateTime.Now
                    };

                    // Serialize and send
                    string message = System.Text.Json.JsonSerializer.Serialize(beaconMessage);
                    byte[] bytes = Encoding.UTF8.GetBytes(message);
                    await udpClient.SendAsync(bytes, bytes.Length, endpoint);

                    // Update the beacon time
                    lastBeaconTime = DateTime.Now;

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
        private static async Task StartServerAsync(CancellationToken cancellationToken)
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
        private static async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
            try
            {
                // Get client info
                var endpoint = (IPEndPoint)client.Client.RemoteEndPoint;
                var nodeInfo = new NodeInfo
                {
                    NodeIP = endpoint.Address.ToString(),
                    NodePort = endpoint.Port,
                    ConnectedAt = DateTime.Now
                };

                Console.WriteLine($"Client connected: {nodeInfo.NodeIP}:{nodeInfo.NodePort}");
                _connectedNodes.Add(nodeInfo);

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
                            lastKeepAliveTime = DateTime.Now;

                            // Send a simple response - use a different variable name here
                            var pingResponse = "{\"Status\":\"OK\",\"Message\":\"Pong\"}";
                            var pingResponseBytes = Encoding.UTF8.GetBytes(pingResponse);
                            await stream.WriteAsync(pingResponseBytes, cancellationToken);
                            continue;
                        }

                        // Process the message (placeholder for actual logic)
                        var response = ProcessClientMessage(message, nodeInfo);

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
                _connectedNodes.Remove(nodeInfo);
                Console.WriteLine($"Client disconnected: {nodeInfo.NodeIP}:{nodeInfo.NodePort}");
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
        private static string ProcessClientMessage(string message, NodeInfo nodeInfo)
        {
            try
            {
                // Parse the JSON message
                var commandObj = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(message);

                if (commandObj.TryGetProperty("Command", out JsonElement commandElement))
                {
                    string command = commandElement.GetString();

                    switch (command)
                    {
                        case "PING":
                            return "{\"Status\":\"OK\",\"Message\":\"Pong\"}";

                        case "DIAGNOSTICS":
                            // Run diagnostic tests
                            return RunDiagnostics();

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

        private static string GetLocalIPAddress()
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

        #endregion

        #region TUI Implementation

        private static void BuildUI()
        {
            var top = Application.Top;

            // Create a window that takes up the full screen
            var win = new Window("Parallel Computing Node Server")
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill()
            };
            top.Add(win);

            // Create status bar with About option
            var statusBar = new StatusBar(new StatusItem[] {
        new StatusItem(Key.F1, "~F1~ Help", ShowHelp),
        new StatusItem(Key.F2, "~F2~ Settings", ShowSettings),
        new StatusItem(Key.F3, "~F3~ Nodes", ShowNodes),
        new StatusItem(Key.F4, "~F4~ About", ShowAbout),
        new StatusItem(Key.F10, "~F10~ Quit", () => Application.RequestStop())
    });
            top.Add(statusBar);

            // Add a padding view for the main content area
            var mainView = new FrameView("Status")
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill() - 1 // Leave space for status bar
            };
            win.Add(mainView);

            // Create a table view for displaying status info
            var tableView = new TableView()
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill(),
                FullRowSelect = true
            };

            // Create a data table for our information
            var table = new DataTable();
            table.Columns.Add("Property", typeof(string));
            table.Columns.Add("Value", typeof(string));

            table.Rows.Add("Server Status", "Running");
            table.Rows.Add("Server IP", GetLocalIPAddress());
            table.Rows.Add("Server Port", _config.ServerPort.ToString());
            table.Rows.Add("Beacon Port", _config.BeaconPort.ToString());
            table.Rows.Add("Beacon Interval", $"{_config.BeaconIntervalMs} ms");
            table.Rows.Add("Connected Nodes", "0");
            table.Rows.Add("GPU Accelerator", _accelerator?.Name ?? "None");

            // Set the table to the view
            tableView.Table = table;

            mainView.Add(tableView);

            // Add indicators to the status bar area
            // First create color schemes
            beaconColorScheme = new ColorScheme();
            beaconColorScheme.Normal = Terminal.Gui.Attribute.Make(Color.Red, Color.Black);

            keepAliveColorScheme = new ColorScheme();
            keepAliveColorScheme.Normal = Terminal.Gui.Attribute.Make(Color.Green, Color.Black);

            // Label for indicator descriptions
            var indicatorLabel = new Label("B:Beacon K:KeepAlive")
            {
                X = Pos.AnchorEnd(22),
                Y = Pos.AnchorEnd(1),
                ColorScheme = Colors.TopLevel
            };
            top.Add(indicatorLabel);

            // Create indicators
            beaconIndicator = new Label("●")
            {
                X = Pos.AnchorEnd(9),
                Y = Pos.AnchorEnd(1),
                ColorScheme = beaconColorScheme,
                Width = 1
            };
            top.Add(beaconIndicator);

            keepAliveIndicator = new Label("●")
            {
                X = Pos.AnchorEnd(3),
                Y = Pos.AnchorEnd(1),
                ColorScheme = keepAliveColorScheme,
                Width = 1
            };
            top.Add(keepAliveIndicator);

            // Create a timer to update the UI periodically
            Application.MainLoop.AddTimeout(TimeSpan.FromSeconds(1), (_) =>
            {
                // Update connected nodes count
                if (table.Rows.Count > 5)
                {
                    table.Rows[5]["Value"] = _connectedNodes.Count.ToString();
                    tableView.SetNeedsDisplay();
                }

                // Update the indicators
                UpdateActivityIndicators();

                return true; // Return true to keep the timer running
            });
        }
        private static void UpdateActivityIndicators()
        {
            // Check if beacon was recently active
            if (DateTime.Now - lastBeaconTime < TimeSpan.FromSeconds(1))
            {
                beaconIndicator.Visible = true;
            }
            else
            {
                beaconIndicator.Visible = !beaconIndicator.Visible; // Blink when inactive
            }

            // Check if keep-alive was recently active
            if (DateTime.Now - lastKeepAliveTime < TimeSpan.FromSeconds(1))
            {
                keepAliveIndicator.Visible = true;
            }
            else
            {
                keepAliveIndicator.Visible = !keepAliveIndicator.Visible; // Blink when inactive
            }

            // Request redraw
            beaconIndicator.SetNeedsDisplay();
            keepAliveIndicator.SetNeedsDisplay();
        }
        private static void UpdateIndicators()
        {
            // Update beacon indicator
            if (DateTime.Now - lastBeaconTime < TimeSpan.FromSeconds(1))
            {
                beaconActive = !beaconActive;
                beaconIndicator.ColorScheme = new ColorScheme()
                {
                    Normal = beaconActive ?
                        Terminal.Gui.Attribute.Make(Color.Red, Color.Black) :
                        Terminal.Gui.Attribute.Make(Color.Black, Color.Black)
                };
            }
            else
            {
                beaconIndicator.ColorScheme = new ColorScheme()
                {
                    Normal = Terminal.Gui.Attribute.Make(Color.Black, Color.Black)
                };
            }

            // Update keep-alive indicator
            if (DateTime.Now - lastKeepAliveTime < TimeSpan.FromSeconds(1))
            {
                keepAliveActive = !keepAliveActive;
                keepAliveIndicator.ColorScheme = new ColorScheme()
                {
                    Normal = keepAliveActive ?
                        Terminal.Gui.Attribute.Make(Color.Green, Color.Black) :
                        Terminal.Gui.Attribute.Make(Color.Black, Color.Black)
                };
            }
            else
            {
                keepAliveIndicator.ColorScheme = new ColorScheme()
                {
                    Normal = Terminal.Gui.Attribute.Make(Color.Black, Color.Black)
                };
            }

            beaconIndicator.SetNeedsDisplay();
            keepAliveIndicator.SetNeedsDisplay();
        }
        private static void ShowAbout()
        {
            var dialog = new Dialog("About", 60, 14);

            // Get version info from assembly
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            string versionStr = version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "1.0.0";

            var aboutText = new Label()
            {
                X = 1,
                Y = 1,
                Width = Dim.Fill() - 2,
                Height = Dim.Fill() - 4,
                Text = "CTS - Parallel Computing Node Server\n" +
                      $"Version {versionStr}\n\n" +
                      "University of Fribourg\n" +
                      "Geosciences Department\n\n" +
                      "Contact: matteo.mangiagalli@unifr.ch\n" +
                      "© 2025 All Rights Reserved"
            };

            var okButton = new Button("OK")
            {
                X = Pos.Center(),
                Y = Pos.Bottom(dialog) - 3,
            };
            okButton.Clicked += () => Application.RequestStop();

            dialog.Add(aboutText, okButton);
            Application.Run(dialog);
        }
        private static void ShowHelp()
        {
            var dialog = new Dialog("Help", 60, 20);

            var helpText = new Label()
            {
                X = 1,
                Y = 1,
                Width = Dim.Fill() - 2,
                Height = Dim.Fill() - 4,
                Text = "Parallel Computing Node Server\n\n" +
                      "F1: Show this help screen\n" +
                      "F2: Configure server settings\n" +
                      "F3: View connected nodes\n" +
                      "F10: Exit the application\n\n" +
                      "This server manages a network of compute nodes that can\n" +
                      "perform parallel computations using ILGPU.\n\n" +
                      "The server broadcasts a beacon to help nodes discover it\n" +
                      "and coordinates computation tasks across all nodes."
            };

            var okButton = new Button("OK")
            {
                X = Pos.Center(),
                Y = Pos.Bottom(dialog) - 3,
            };
            okButton.Clicked += () => Application.RequestStop();

            dialog.Add(helpText, okButton);
            Application.Run(dialog);
        }

        private static void ShowSettings()
        {
            var dialog = new Dialog("Settings", 60, 15);

            var serverPortLabel = new Label("Server Port:")
            {
                X = 1,
                Y = 1
            };

            var serverPortField = new TextField(_config.ServerPort.ToString())
            {
                X = 15,
                Y = 1,
                Width = 10
            };

            var beaconPortLabel = new Label("Beacon Port:")
            {
                X = 1,
                Y = 3
            };

            var beaconPortField = new TextField(_config.BeaconPort.ToString())
            {
                X = 15,
                Y = 3,
                Width = 10
            };

            var beaconIntervalLabel = new Label("Beacon Interval (ms):")
            {
                X = 1,
                Y = 5
            };

            var beaconIntervalField = new TextField(_config.BeaconIntervalMs.ToString())
            {
                X = 23,
                Y = 5,
                Width = 10
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
                    var newConfig = new ServerConfig
                    {
                        ServerPort = int.Parse(serverPortField.Text.ToString()),
                        BeaconPort = int.Parse(beaconPortField.Text.ToString()),
                        BeaconIntervalMs = int.Parse(beaconIntervalField.Text.ToString())
                    };

                    // Validate settings
                    if (newConfig.ServerPort < 1024 || newConfig.ServerPort > 65535 ||
                        newConfig.BeaconPort < 1024 || newConfig.BeaconPort > 65535 ||
                        newConfig.BeaconIntervalMs < 100)
                    {
                        MessageBox.ErrorQuery("Invalid Settings",
                            "Port numbers must be between 1024 and 65535.\n" +
                            "Beacon interval must be at least 100ms.", "OK");
                        return;
                    }

                    // Apply new settings
                    _config = newConfig;

                    MessageBox.Query("Settings Saved",
                        "Server must be restarted for changes to take effect.", "OK");

                    Application.RequestStop();
                }
                catch (Exception)
                {
                    MessageBox.ErrorQuery("Invalid Input", "Please enter valid numbers.", "OK");
                }
            };

            cancelButton.Clicked += () => Application.RequestStop();

            dialog.Add(
                serverPortLabel, serverPortField,
                beaconPortLabel, beaconPortField,
                beaconIntervalLabel, beaconIntervalField,
                saveButton, cancelButton
            );

            Application.Run(dialog);
        }

        private static void ShowNodes()
        {
            var dialog = new Dialog("Connected Nodes", 70, 20);

            var listView = new ListView()
            {
                X = 1,
                Y = 1,
                Width = Dim.Fill() - 2,
                Height = Dim.Fill() - 4
            };

            var nodeList = new List<string>();
            if (_connectedNodes.Count == 0)
            {
                nodeList.Add("No nodes connected");
            }
            else
            {
                nodeList.Add($"{"IP Address",-15} {"Port",-8} {"Connected Since",-20} {"Status",-10}");
                foreach (var node in _connectedNodes)
                {
                    nodeList.Add($"{node.NodeIP,-15} {node.NodePort,-8} {node.ConnectedAt,-20} {"Active",-10}");
                }
            }

            listView.SetSource(nodeList);

            var closeButton = new Button("Close")
            {
                X = Pos.Center(),
                Y = Pos.Bottom(dialog) - 3,
            };
            closeButton.Clicked += () => Application.RequestStop();

            dialog.Add(listView, closeButton);
            Application.Run(dialog);
        }

        #endregion
        #region Commands
        private static string RunDiagnostics()
        {
            var results = new System.Text.StringBuilder();
            results.AppendLine("=== Diagnostic Test Results ===");

            // System Info
            results.AppendLine($"Hostname: {System.Environment.MachineName}");
            results.AppendLine($"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
            results.AppendLine($"Processor Count: {Environment.ProcessorCount}");
            results.AppendLine($"Connected Nodes: {_connectedNodes.Count}");
            results.AppendLine();

            // Memory Usage
            var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
            var memoryMB = currentProcess.WorkingSet64 / (1024 * 1024);
            results.AppendLine($"Memory Usage: {memoryMB} MB");
            results.AppendLine();

            // CPU Test
            results.AppendLine("Running CPU Performance Test...");
            var cpuResult = RunCpuTest();
            results.AppendLine($"CPU Test Result: {cpuResult}");
            results.AppendLine();

            // GPU Test if available
            if (_accelerator != null)
            {
                results.AppendLine($"Accelerator: {_accelerator.Name} ({_accelerator.AcceleratorType})");

                try
                {
                    results.AppendLine("Running GPU Performance Test...");
                    var gpuResult = RunGpuTest();
                    results.AppendLine($"GPU Test Result: {gpuResult}");
                }
                catch (Exception ex)
                {
                    results.AppendLine($"GPU Test Failed: {ex.Message}");
                }
            }
            else
            {
                results.AppendLine("No GPU Accelerator Available");
            }

            return results.ToString();
        }

        private static string RunCpuTest()
        {
            var stopwatch = new System.Diagnostics.Stopwatch();
            stopwatch.Start();

            // Simple CPU-intensive task: compute pi to many digits
            double pi = 0;
            double denominator = 1;
            int iterations = 100_000_000;

            for (int i = 0; i < iterations; i++)
            {
                if (i % 2 == 0)
                    pi += 4 / denominator;
                else
                    pi -= 4 / denominator;

                denominator += 2;
            }

            stopwatch.Stop();
            return $"Calculated Pi with {iterations} iterations in {stopwatch.ElapsedMilliseconds} ms, Result: {pi}";
        }

        private static string RunGpuTest()
        {
            if (_accelerator == null)
                return "No GPU accelerator available";

            try
            {
                var stopwatch = new System.Diagnostics.Stopwatch();
                stopwatch.Start();

                // Create a simple kernel that adds two arrays
                var kernel = _accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>>(
                    (index, a, b, c) => c[index] = a[index] + b[index]);

                // Prepare test data - use a smaller size for reliability
                const int length = 1_000_000;

                // Allocate memory
                var aBuffer = _accelerator.Allocate1D<float>(length);
                var bBuffer = _accelerator.Allocate1D<float>(length);
                var cBuffer = _accelerator.Allocate1D<float>(length);

                try
                {
                    // Initialize data
                    var aData = new float[length];
                    var bData = new float[length];
                    for (int i = 0; i < length; i++)
                    {
                        aData[i] = i;
                        bData[i] = i * 2;
                    }

                    // Copy data to GPU
                    aBuffer.CopyFromCPU(aData);
                    bBuffer.CopyFromCPU(bData);

                    // Execute kernel
                    kernel(length, aBuffer.View, bBuffer.View, cBuffer.View);
                    _accelerator.Synchronize(); // Ensure GPU execution completes

                    // Copy only first 10 elements for verification
                    var results = new float[10];
                    // The standard way to copy a subset of data in ILGPU
                    cBuffer.View.SubView(0, 10).CopyToCPU(results);

                    stopwatch.Stop();

                    return $"Processed {length} elements in {stopwatch.ElapsedMilliseconds} ms, First result: {results[0]}";
                }
                finally
                {
                    // Properly dispose of GPU buffers
                    aBuffer.Dispose();
                    bBuffer.Dispose();
                    cBuffer.Dispose();
                }
            }
            catch (Exception ex)
            {
                return $"GPU Test Failed: {ex.Message}";
            }
        }
        private static void RestartServer()
        {
            Console.WriteLine("Server restart initiated...");

            // Clean up resources
            _accelerator?.Dispose();
            _ilgpuContext?.Dispose();

            // Cancel any ongoing operations
            _cancellationTokenSource.Cancel();

            // Set exit code for restart
            Environment.ExitCode = 42; // Special code that a wrapper script could use to restart

            // Create a batch or shell script to restart the server
            string scriptExt = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) ? "bat" : "sh";
            string scriptPath = Path.Combine(Path.GetTempPath(), $"restart_server.{scriptExt}");

            try
            {
                if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                {
                    // Windows batch script
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
                else
                {
                    // Linux/macOS shell script
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
                    System.Diagnostics.Process.Start(chmodInfo).WaitForExit();

                    var startInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "/bin/bash",
                        Arguments = scriptPath,
                        CreateNoWindow = true,
                        UseShellExecute = true
                    };
                    System.Diagnostics.Process.Start(startInfo);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error creating restart script: {ex.Message}");
            }

            // Exit the application
            Application.RequestStop();
            Environment.Exit(Environment.ExitCode);
        }

        private static void ShutdownServer()
        {
            Console.WriteLine("Server shutdown initiated...");

            // Clean up resources
            _accelerator?.Dispose();
            _ilgpuContext?.Dispose();

            // Cancel any ongoing operations
            _cancellationTokenSource.Cancel();

            // Exit the application
            Application.RequestStop();
            Environment.Exit(0);
        }
        #endregion
    }

    #region Model Classes

    public class ServerConfig
    {
        public int ServerPort { get; set; } = 7000;
        public int BeaconPort { get; set; } = 7001;
        public int BeaconIntervalMs { get; set; } = 5000;
    }

    public class NodeInfo
    {
        public string NodeIP { get; set; }
        public int NodePort { get; set; }
        public DateTime ConnectedAt { get; set; }
        public string Status { get; set; } = "Active";
    }

    public class BeaconMessage
    {
        public string ServerName { get; set; }
        public string ServerIP { get; set; }
        public int ServerPort { get; set; }
        public int NodesConnected { get; set; }
        public bool GpuEnabled { get; set; }
        public DateTime Timestamp { get; set; }
    }

    #endregion
}