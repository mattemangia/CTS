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
            var endpointService = new Services.EndpointService(config);
            var networkService = new Services.NetworkService(config, computeService, endpointService);
            var uiManager = new UI.UIManager(config, networkService, computeService, endpointService);
            string datasetStoragePath = Path.Combine(AppContext.BaseDirectory, "datasets");
            var datasetTransferService = new Services.DatasetTransferService(datasetStoragePath, computeService);
            uiManager.AddDatasetTransferService(datasetTransferService);
            // Initialize ILGPU
            try
            {
                // normal startup + run
                await computeService.InitializeAsync();

                using var cts = new CancellationTokenSource();
                var beaconTask = networkService.StartBeaconServiceAsync(cts.Token);
                var serverTask = networkService.StartServerAsync(cts.Token);
                var endpointTask = endpointService.StartEndpointServiceAsync(cts.Token);

                uiManager.Run();          // ← blocks until user quits
                cts.Cancel();             // ask background services to stop
                await Task.WhenAll(beaconTask, serverTask, endpointTask);
            }
            finally
            {
                // guaranteed to run—even on Ctrl-C or unhandled exception
                EmptyDirectory(datasetStoragePath);
                computeService.Dispose();
                datasetTransferService.Dispose();   // optional: if you add IDisposable (see below)
            }

            Console.WriteLine("Server shutdown complete.");
        }
        static void EmptyDirectory(string path)
        {
            if (!Directory.Exists(path)) return;

            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                File.Delete(file);

            foreach (var dir in Directory.EnumerateDirectories(path))
                Directory.Delete(dir, recursive: true);
        }
    }
}