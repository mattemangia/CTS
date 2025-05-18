using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Terminal.Gui;

namespace ParallelComputingEndpoint
{
    class Program
    {
        private static readonly CancellationTokenSource _cancellationTokenSource = new();
        private static EndpointConfig _config;
        private static EndpointNetworkService _networkService;
        private static EndpointComputeService _computeService;
        private static UIManager _uiManager;
        private static EndpointNodeProcessingService _nodeProcessingService;
        private static LogPanel _logPanel;

        static async Task Main(string[] args)
        {
            try
            {
                Console.WriteLine("Initializing Parallel Computing Endpoint...");

                // Load or create configuration
                _config = LoadConfig();

                // Initialize compute service first
                _computeService = new EndpointComputeService();

                // Initialize hardware
                await _computeService.InitializeAsync();

                // Initialize services
                _networkService = new EndpointNetworkService(_config, _computeService);
                _uiManager = new UIManager(_config, _networkService, _computeService);

                // Get LogPanel reference after UI init
                Application.Init();
                _uiManager.BuildUI(); // Assicurati che BuildUI sia public
                _logPanel = _uiManager.LogPanel;

                // Create node processing service with LogPanel
                _nodeProcessingService = new EndpointNodeProcessingService(_computeService, _logPanel);

                // Start UI
                Application.Run();

                // When UI exits, stop all services
                _cancellationTokenSource.Cancel();

                // Clean up resources
                _computeService.Dispose();
                _networkService.Shutdown();

                // Save config when exiting
                SaveConfig(_config);

                Console.WriteLine("Endpoint shutdown complete.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Fatal error: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                Console.ReadKey();
            }
        }

        private static EndpointConfig LoadConfig()
        {
            string configPath = "endpoint_config.json";

            if (File.Exists(configPath))
            {
                try
                {
                    string json = File.ReadAllText(configPath);
                    var config = JsonSerializer.Deserialize<EndpointConfig>(json);
                    Console.WriteLine("Configuration loaded successfully.");
                    return config;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error loading configuration: {ex.Message}");
                    Console.WriteLine("Using default configuration instead.");
                }
            }
            else
            {
                Console.WriteLine("Configuration file not found. Creating default configuration.");
            }

            // Create default config
            var defaultConfig = new EndpointConfig
            {
                ServerIP = "localhost",
                ServerPort = 7002,
                BeaconPort = 7001,
                EndpointName = Environment.MachineName
            };

            // Save the default config
            SaveConfig(defaultConfig);

            return defaultConfig;
        }

        private static void SaveConfig(EndpointConfig config)
        {
            string configPath = "endpoint_config.json";

            try
            {
                string json = JsonSerializer.Serialize(config, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                File.WriteAllText(configPath, json);
                Console.WriteLine("Configuration saved successfully.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving configuration: {ex.Message}");
            }
        }
    }

    public class EndpointConfig
    {
        public string ServerIP { get; set; } = "localhost";
        public int ServerPort { get; set; } = 7002;
        public int BeaconPort { get; set; } = 7001;
        public string EndpointName { get; set; } = Environment.MachineName;
        public bool AutoConnect { get; set; } = false;

        // Metodo statico per la compatibilità con altri file
        public static EndpointConfig Load()
        {
            return new EndpointConfig();
        }
    }
}