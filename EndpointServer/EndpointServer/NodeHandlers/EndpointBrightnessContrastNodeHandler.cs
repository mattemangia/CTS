using ILGPU;
using ILGPU.Runtime;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;

namespace ParallelComputingEndpoint
{
    public class EndpointBrightnessContrastNodeHandler : BaseNodeHandler, IProgressTrackable
    {
        private Action<int> _progressCallback;

        public void SetProgressCallback(Action<int> progressCallback)
        {
            _progressCallback = progressCallback;
        }

        public override async Task<Dictionary<string, string>> ProcessAsync(
            Dictionary<string, string> inputData,
            Dictionary<string, byte[]> binaryData,
            EndpointComputeService computeService)
        {
            // Extract parameters from input data
            int brightness = 0;
            int contrast = 100;
            byte blackPoint = 0;
            byte whitePoint = 255;
            int startZ = 0; // Starting Z slice (for distributed processing)

            // Parse parameters from input data
            if (inputData.TryGetValue("Brightness", out string brightnessStr))
                int.TryParse(brightnessStr, out brightness);

            if (inputData.TryGetValue("Contrast", out string contrastStr))
                int.TryParse(contrastStr, out contrast);

            if (inputData.TryGetValue("BlackPoint", out string blackPointStr))
                byte.TryParse(blackPointStr, out blackPoint);

            if (inputData.TryGetValue("WhitePoint", out string whitePointStr))
                byte.TryParse(whitePointStr, out whitePoint);

            // Check for distributed processing offset
            if (inputData.TryGetValue("StartZ", out string startZStr))
                int.TryParse(startZStr, out startZ);

            // Get volume data
            byte[] volumeData = null;
            int width = 0, height = 0, depth = 0, chunkDim = 64;

            if (inputData.TryGetValue("Width", out string widthStr))
                int.TryParse(widthStr, out width);

            if (inputData.TryGetValue("Height", out string heightStr))
                int.TryParse(heightStr, out height);

            if (inputData.TryGetValue("Depth", out string depthStr))
                int.TryParse(depthStr, out depth);

            if (inputData.TryGetValue("ChunkDim", out string chunkDimStr))
                int.TryParse(chunkDimStr, out chunkDim);

            // Get binary data reference
            string volumeKey = null;
            foreach (var entry in inputData)
            {
                if (entry.Value.StartsWith("binary_ref:") && entry.Key.Contains("Volume"))
                {
                    volumeKey = entry.Value.Substring("binary_ref:".Length);
                    break;
                }
            }

            if (volumeKey != null && binaryData.TryGetValue(volumeKey, out volumeData))
            {
                try
                {
                    _progressCallback?.Invoke(10); // Segnala inizio elaborazione

                    // Process volume data
                    var result = await Task.Run(() => ProcessVolumeData(
                        volumeData, width, height, depth, chunkDim,
                        brightness, contrast, blackPoint, whitePoint,
                        computeService.GpuAvailable, startZ));

                    _progressCallback?.Invoke(90); // Segnala elaborazione quasi completa

                    // Return processed data
                    var outputData = new Dictionary<string, string>();
                    outputData["Status"] = "Success";

                    // Add binary reference to output
                    string outputKey = "processed_volume";
                    binaryData[outputKey] = result;
                    outputData["ProcessedVolume"] = $"binary_ref:{outputKey}";

                    // Add metadata - preserve original dimensions and parameters
                    foreach (var key in inputData.Keys)
                    {
                        // Skip binary references from input
                        if (inputData[key].StartsWith("binary_ref:"))
                            continue;

                        // Include all other metadata
                        outputData[key] = inputData[key];
                    }

                    // Make sure dimensions are set correctly
                    outputData["Width"] = width.ToString();
                    outputData["Height"] = height.ToString();
                    outputData["Depth"] = depth.ToString();
                    outputData["ChunkDim"] = chunkDim.ToString();

                    // If we are processing a slice, include the StartZ offset
                    if (startZ > 0)
                        outputData["StartZ"] = startZ.ToString();

                    _progressCallback?.Invoke(100); // Segnala elaborazione completa

                    return outputData;
                }
                catch (Exception ex)
                {
                    var outputData = new Dictionary<string, string>();
                    outputData["Status"] = "Error";
                    outputData["Message"] = $"Error processing volume data: {ex.Message}";
                    return outputData;
                }
            }
            else
            {
                var outputData = new Dictionary<string, string>();
                outputData["Status"] = "Error";
                outputData["Message"] = "Missing volume data";
                return outputData;
            }
        }

        private byte[] ProcessVolumeData(
            byte[] inputData,
            int width, int height, int depth, int chunkDim,
            int brightness, int contrast, byte blackPoint, byte whitePoint,
            bool useGpu, int startZ = 0)
        {
            // Prepare output buffer - same size as input
            byte[] outputData = new byte[inputData.Length];

            _progressCallback?.Invoke(20); // Inizio elaborazione volume

            // Choose processing method based on data size and GPU availability
            if (useGpu && inputData.Length > 1000000)  // Only use GPU for larger volumes
            {
                // Use GPU processing for large volumes
                ProcessVolumeGpu(inputData, outputData, width, height, depth, brightness, contrast, blackPoint, whitePoint, startZ);
            }
            else
            {
                // Use CPU processing
                ProcessVolumeCpu(inputData, outputData, width, height, depth, brightness, contrast, blackPoint, whitePoint, startZ);
            }

            _progressCallback?.Invoke(80); // Fine elaborazione volume

            return outputData;
        }

        private void ProcessVolumeCpu(
            byte[] inputData, byte[] outputData,
            int width, int height, int depth,
            int brightness, int contrast, byte blackPoint, byte whitePoint,
            int startZ = 0)
        {
            int totalVoxels = inputData.Length;
            int reportInterval = Math.Max(1, totalVoxels / 100); // Report every 1%
            int lastReportedPercent = 0;

            // Basic implementation - process each voxel
            Parallel.For(0, inputData.Length, i =>
            {
                byte value = inputData[i];
                int adjustedValue = ApplyAdjustment(value, blackPoint, whitePoint, brightness, contrast);
                outputData[i] = (byte)Math.Max(0, Math.Min(255, adjustedValue));

                // Report progress periodically - use lock to avoid thread conflicts
                if (_progressCallback != null && i % reportInterval == 0)
                {
                    int percent = (int)((i / (double)totalVoxels) * 60) + 20; // Scala da 20% a 80%
                    if (percent > lastReportedPercent)
                    {
                        lock (this)
                        {
                            if (percent > lastReportedPercent)
                            {
                                _progressCallback(percent);
                                lastReportedPercent = percent;
                            }
                        }
                    }
                }
            });
        }

        private void ProcessVolumeGpu(
    byte[] inputData, byte[] outputData,
    int width, int height, int depth,
    int brightness, int contrast, byte blackPoint, byte whitePoint,
    int startZ = 0)
        {
            try
            {
                int totalVoxels = inputData.Length;
                int reportInterval = Math.Max(1, totalVoxels / 100); // Report every 1%
                using var ctx = Context.Create(builder => builder.Default().EnableAlgorithms());
                using var accelerator = ctx.GetPreferredDevice(preferCPU: false).CreateAccelerator(ctx);

                // Report we're starting GPU processing
                _progressCallback?.Invoke(50);

                // Create kernel
                var kernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<byte>, ArrayView<byte>, byte, byte, int, int>(
                    (index, input, output, bPoint, wPoint, bright, cont) =>
                    {
                        byte value = input[index];

                        // Map the value from [blackPoint, whitePoint] to [0, 255]
                        float normalized = 0;
                        if (wPoint > bPoint)
                        {
                            normalized = (value - bPoint) / (float)(wPoint - bPoint);
                        }
                        normalized = Math.Max(0, Math.Min(1, normalized));

                        // Apply contrast (percentage)
                        float contrasted = (normalized - 0.5f) * (cont / 100.0f) + 0.5f;
                        contrasted = Math.Max(0, Math.Min(1, contrasted));

                        // Apply brightness (offset)
                        int result = (int)(contrasted * 255) + bright;
                        output[index] = (byte)Math.Max(0, Math.Min(255, result));
                    });

                // Allocate device memory for input and output
                using var inputBuffer = accelerator.Allocate1D<byte>(totalVoxels);
                using var outputBuffer = accelerator.Allocate1D<byte>(totalVoxels);

                // Copy input data to GPU
                inputBuffer.CopyFromCPU(inputData);

                // Execute kernel
                kernel(totalVoxels, inputBuffer.View, outputBuffer.View, blackPoint, whitePoint, brightness, contrast);

                // Wait for completion
                accelerator.Synchronize();

                // Copy result back to CPU
                outputBuffer.CopyToCPU(outputData);

                _progressCallback?.Invoke(80); // Signal GPU processing completed
            }
            catch (Exception ex)
            {
                Console.WriteLine($"GPU processing failed, falling back to CPU: {ex.Message}");
                // Fall back to CPU implementation on error
                ProcessVolumeCpu(inputData, outputData, width, height, depth, brightness, contrast, blackPoint, whitePoint, startZ);
            }
        }

        // Algorithm from BrightnessContrastNode
        private int ApplyAdjustment(byte value, byte bPoint, byte wPoint, int bright, int cont)
        {
            // Map the value from [blackPoint, whitePoint] to [0, 255]
            double normalized = 0;
            if (wPoint > bPoint)
            {
                normalized = (value - bPoint) / (double)(wPoint - bPoint);
            }
            normalized = Math.Max(0, Math.Min(1, normalized));

            // Apply contrast (percentage)
            double contrasted = (normalized - 0.5) * (cont / 100.0) + 0.5;
            contrasted = Math.Max(0, Math.Min(1, contrasted));

            // Apply brightness (offset)
            int result = (int)(contrasted * 255) + bright;
            return result;
        }
    }
}