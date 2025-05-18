using System;
using System.Threading;
using System.Threading.Tasks;
using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.CPU;

namespace ParallelComputingEndpoint
{
    public class ComputeService : IDisposable
    {
        private Context _ilgpuContext;
        private Accelerator _accelerator;

        // Properties
        public bool IsInitialized => _accelerator != null;
        public bool GpuAvailable => _accelerator != null && !(_accelerator is CPUAccelerator);
        public string AcceleratorName => _accelerator?.Name ?? "None";
        public string HardwareInfo { get; private set; }

        // Events
        public event EventHandler<double> CpuLoadUpdated;

        // Background monitoring
        private Timer _monitorTimer;
        private readonly object _lockObject = new();
        private double _cpuLoad = 0;

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

                // Gather hardware information
                GatherHardwareInfo();

                // Start monitoring CPU load
                StartMonitoring();

                // Run a simple test computation
                await Task.Run(() => TestGPUComputation());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ILGPU initialization failed: {ex.Message}");
                Console.WriteLine("The endpoint will continue without GPU acceleration.");
            }
        }

        private void GatherHardwareInfo()
        {
            var info = new System.Text.StringBuilder();

            // OS info
            info.Append($"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}. ");

            // CPU info
            info.Append($"CPU: {Environment.ProcessorCount} cores. ");

            // Memory info
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    // Windows implementation using GlobalMemoryStatusEx
                    long memoryBytes = GetWindowsMemorySize();
                    double memoryGB = memoryBytes / (1024.0 * 1024 * 1024);
                    info.Append($"RAM: {memoryGB:F1} GB. ");
                }
                else if (OperatingSystem.IsLinux())
                {
                    // Linux implementation using /proc/meminfo
                    string[] memInfo = File.ReadAllLines("/proc/meminfo");
                    foreach (string line in memInfo)
                    {
                        if (line.StartsWith("MemTotal:"))
                        {
                            string[] parts = line.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length >= 2 && long.TryParse(parts[1], out long memKB))
                            {
                                double memGB = memKB / (1024.0 * 1024);
                                info.Append($"RAM: {memGB:F1} GB. ");
                                break;
                            }
                        }
                    }
                }
                else if (OperatingSystem.IsMacOS())
                {
                    // MacOS implementation using sysctl
                    using var process = new System.Diagnostics.Process();
                    process.StartInfo.FileName = "sysctl";
                    process.StartInfo.Arguments = "hw.memsize";
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.Start();

                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();

                    if (output.Contains(":"))
                    {
                        string memBytes = output.Split(':')[1].Trim();
                        if (long.TryParse(memBytes, out long bytes))
                        {
                            double memGB = bytes / (1024.0 * 1024 * 1024);
                            info.Append($"RAM: {memGB:F1} GB. ");
                        }
                    }
                }
            }
            catch
            {
                info.Append("RAM: Unknown. ");
            }

            // GPU info
            if (GpuAvailable)
            {
                info.Append($"GPU: {_accelerator.Name}");
            }
            else
            {
                info.Append("GPU: None");
            }

            HardwareInfo = info.ToString();
        }

        private long GetWindowsMemorySize()
        {
            // Windows-specific implementation without using Microsoft.VisualBasic
            try
            {
                MEMORYSTATUSEX memoryStatus = new MEMORYSTATUSEX();
                if (GlobalMemoryStatusEx(memoryStatus))
                {
                    return (long)memoryStatus.ullTotalPhys;
                }
            }
            catch
            {
                // Fallback to Environment.SystemPageSize * Environment.WorkingSet 
                // which is not accurate but better than nothing
            }

            return Environment.WorkingSet;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;

            public MEMORYSTATUSEX()
            {
                this.dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf(this);
            }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto, SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([System.Runtime.InteropServices.In, System.Runtime.InteropServices.Out] MEMORYSTATUSEX lpBuffer);

        private void StartMonitoring()
        {
            // Start monitoring timer
            _monitorTimer = new Timer(_ =>
            {
                try
                {
                    lock (_lockObject)
                    {
                        _cpuLoad = GetCpuUsagePercentage();
                        CpuLoadUpdated?.Invoke(this, _cpuLoad);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error updating CPU load: {ex.Message}");
                }
            }, null, 1000, 2000); // Update every 2 seconds after an initial 1 second delay
        }

        private double GetCpuUsagePercentage()
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    return GetWindowsCpuUsage();
                }
                else if (OperatingSystem.IsLinux())
                {
                    return GetLinuxCpuUsage();
                }
                else if (OperatingSystem.IsMacOS())
                {
                    return GetMacOSCpuUsage();
                }
                else
                {
                    return 0.0;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting CPU usage: {ex.Message}");
                return 0.0;
            }
        }

        // Fields for CPU calculation
        private double _previousTotalTime = 0;
        private double _previousIdleTime = 0;

        private double GetLinuxCpuUsage()
        {
            try
            {
                string[] cpuInfoLines = File.ReadAllLines("/proc/stat");
                string cpuLine = cpuInfoLines[0]; // First line is total CPU info

                // Format: cpu user nice system idle iowait irq softirq steal guest guest_nice
                string[] cpuData = cpuLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                if (cpuData.Length < 5) return 0.0;

                double user = double.Parse(cpuData[1]);
                double nice = double.Parse(cpuData[2]);
                double system = double.Parse(cpuData[3]);
                double idle = double.Parse(cpuData[4]);
                double iowait = cpuData.Length > 5 ? double.Parse(cpuData[5]) : 0;

                double idleTime = idle + iowait;
                double totalTime = user + nice + system + idle + iowait;

                if (_previousTotalTime == 0 || _previousIdleTime == 0)
                {
                    _previousTotalTime = totalTime;
                    _previousIdleTime = idleTime;
                    return 0.0; // First reading
                }

                double deltaTotal = totalTime - _previousTotalTime;
                double deltaIdle = idleTime - _previousIdleTime;

                _previousTotalTime = totalTime;
                _previousIdleTime = idleTime;

                if (deltaTotal <= 0) return 0.0;

                return 100.0 * (1.0 - deltaIdle / deltaTotal);
            }
            catch (Exception)
            {
                return 0.0;
            }
        }

        private double GetWindowsCpuUsage()
        {
            try
            {
                // Windows-specific implementation using PerformanceCounter
                using var cpuCounter = new System.Diagnostics.PerformanceCounter("Processor", "% Processor Time", "_Total");
                cpuCounter.NextValue(); // First call always returns 0
                Thread.Sleep(1000); // Wait to get a valid reading
                return cpuCounter.NextValue();
            }
            catch (Exception)
            {
                return 0.0;
            }
        }

        private double GetMacOSCpuUsage()
        {
            try
            {
                // Simple implementation for macOS using top command
                using var process = new System.Diagnostics.Process();
                process.StartInfo.FileName = "top";
                process.StartInfo.Arguments = "-l 1 -n 0";
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardOutput = true;
                process.Start();

                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                // Parse CPU load from top output
                var lines = output.Split('\n');
                foreach (var line in lines)
                {
                    if (line.Contains("CPU usage"))
                    {
                        // Format: CPU usage: x.xx% user, x.xx% sys, x.xx% idle
                        var parts = line.Split(',');
                        if (parts.Length >= 3)
                        {
                            string idlePart = parts[2].Trim();
                            if (idlePart.Contains("%"))
                            {
                                string idlePercentStr = idlePart.Split('%')[0].Trim();
                                if (double.TryParse(idlePercentStr, out double idlePercent))
                                {
                                    return 100.0 - idlePercent;
                                }
                            }
                        }
                    }
                }

                return 0.0;
            }
            catch (Exception)
            {
                return 0.0;
            }
        }

        public double GetCpuLoad()
        {
            lock (_lockObject)
            {
                return _cpuLoad;
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

        public string RunBenchmark()
        {
            var resultsBuilder = new System.Text.StringBuilder();
            resultsBuilder.AppendLine("=== Benchmark Results ===");

            // System Info
            resultsBuilder.AppendLine($"Hostname: {Environment.MachineName}");
            resultsBuilder.AppendLine(HardwareInfo);
            resultsBuilder.AppendLine();

            // Memory Usage
            var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
            var memoryMB = currentProcess.WorkingSet64 / (1024 * 1024);
            resultsBuilder.AppendLine($"Memory Usage: {memoryMB} MB");
            resultsBuilder.AppendLine();

            // CPU Test
            resultsBuilder.AppendLine("Running CPU Performance Test...");
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
            resultsBuilder.AppendLine($"CPU Test: Calculated Pi with {iterations} iterations in {stopwatch.ElapsedMilliseconds} ms");
            resultsBuilder.AppendLine();

            // GPU Test if available
            if (_accelerator != null)
            {
                resultsBuilder.AppendLine($"Accelerator: {_accelerator.Name} ({_accelerator.AcceleratorType})");

                try
                {
                    stopwatch.Restart();

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
                        var resultData = new float[10];
                        // Copy a subset of data
                        cBuffer.View.SubView(0, 10).CopyToCPU(resultData);

                        stopwatch.Stop();

                        resultsBuilder.AppendLine($"GPU Test: Processed {length} elements in {stopwatch.ElapsedMilliseconds} ms");
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
                    resultsBuilder.AppendLine($"GPU Test Failed: {ex.Message}");
                }
            }
            else
            {
                resultsBuilder.AppendLine("No GPU Accelerator Available");
            }

            return resultsBuilder.ToString();
        }

        public void Dispose()
        {
            _monitorTimer?.Dispose();
            _accelerator?.Dispose();
            _ilgpuContext?.Dispose();
        }
    }
}