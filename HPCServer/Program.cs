using System;
using System.Threading;
using System.Threading.Tasks;

namespace ParallelComputingServer
{
    class Program
    {
        private static readonly CancellationTokenSource _cancellationTokenSource = new();
        private static readonly ManualResetEvent _exitEvent = new(false);

        static async Task Main(string[] args)
        {
            Console.WriteLine("Initializing Parallel Computing Server...");

            // Initialize services
            var config = new Config.ServerConfig();
            var computeService = new Services.ComputeService();
            var networkService = new Services.NetworkService(config, computeService);
            var endpointService = new Services.EndpointService(config);
            var uiManager = new UI.UIManager(config, networkService, computeService, endpointService);

            // Initialize ILGPU
            await computeService.InitializeAsync();

            // Start network services
            Task beaconTask = networkService.StartBeaconServiceAsync(_cancellationTokenSource.Token);
            Task serverTask = networkService.StartServerAsync(_cancellationTokenSource.Token);
            Task endpointTask = endpointService.StartEndpointServiceAsync(_cancellationTokenSource.Token);

            // Initialize and run the TUI
            uiManager.Run();

            // When TUI exits, stop all services
            _cancellationTokenSource.Cancel();
            await Task.WhenAll(beaconTask, serverTask, endpointTask);

            // Clean up resources
            computeService.Dispose();

            Console.WriteLine("Server shutdown complete.");
        }
    }
}