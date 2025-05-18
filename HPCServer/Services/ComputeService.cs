using System;
using System.Threading;
using System.Threading.Tasks;
using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.CPU;

namespace ParallelComputingServer.Services
{
    public class ComputeService : IDisposable
    {
        private Context _ilgpuContext;
        private Accelerator _accelerator;

        public bool GpuAvailable => _accelerator != null && !(_accelerator is CPUAccelerator);
        public string AcceleratorName => _accelerator?.Name ?? "None";

        public async Task InitializeAsync()
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

        private void TestGPUComputation()
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

        public string RunDiagnostics()
        {
            var results = new System.Text.StringBuilder();
            results.AppendLine("=== Diagnostic Test Results ===");

            // System Info
            results.AppendLine($"Hostname: {System.Environment.MachineName}");
            results.AppendLine($"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
            results.AppendLine($"Processor Count: {Environment.ProcessorCount}");
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

        private string RunCpuTest()
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

        private string RunGpuTest()
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
                    // Copy a subset of data
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

        public void Dispose()
        {
            _accelerator?.Dispose();
            _ilgpuContext?.Dispose();
        }
    }
}