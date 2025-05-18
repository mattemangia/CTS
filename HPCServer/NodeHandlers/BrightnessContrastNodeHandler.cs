using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ParallelComputingServer.Data;
using ParallelComputingServer.Services;

namespace ParallelComputingServer.Services.NodeHandlers
{
    /// <summary>
    /// Server-side implementation of Brightness/Contrast node processing with distributed processing support
    /// </summary>
    public class BrightnessContrastNodeHandler : BaseNodeHandler
    {
        public override async Task<Dictionary<string, string>> ProcessAsync(
            Dictionary<string, string> inputData,
            Dictionary<string, byte[]> binaryData,
            ComputeService computeService)
        {
            LogProcessing("BrightnessContrastNode");

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

            Console.WriteLine($"Processing brightness/contrast adjustment: brightness={brightness}, contrast={contrast}, blackPoint={blackPoint}, whitePoint={whitePoint}, startZ={startZ}");

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
                // Create a temporary file for processing
                string tempFilePath = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    $"temp_volume_{Guid.NewGuid()}.bin");

                try
                {
                    // Process volume data
                    var result = await Task.Run(() => ProcessVolumeData(
                        volumeData, width, height, depth, chunkDim,
                        brightness, contrast, blackPoint, whitePoint,
                        tempFilePath, computeService.GpuAvailable, startZ));

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

                    return outputData;
                }
                finally
                {
                    // Clean up temporary file
                    try
                    {
                        if (System.IO.File.Exists(tempFilePath))
                            System.IO.File.Delete(tempFilePath);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error cleaning up temp file: {ex.Message}");
                    }
                }
            }
            else
            {
                throw new ArgumentException("Missing volume data");
            }
        }

        private byte[] ProcessVolumeData(
            byte[] inputData,
            int width, int height, int depth, int chunkDim,
            int brightness, int contrast, byte blackPoint, byte whitePoint,
            string tempFilePath, bool useGpu, int startZ = 0)
        {
            Console.WriteLine($"Processing volume data: {width}x{height}x{depth}, startZ={startZ}");

            // For sliced processing (distributed), we need to know if the input data
            // is a slice or the full volume
            bool isSlice = startZ > 0 || inputData.Length < width * height * depth;

            // The input data might be a slice of the full volume
            int volumeSliceSize = width * height;
            int expectedSize = width * height * depth;

            if (inputData.Length != expectedSize && !isSlice)
            {
                Console.WriteLine($"Warning: Input data size ({inputData.Length}) doesn't match expected size ({expectedSize})");
            }

            // Prepare output buffer - same size as input
            byte[] outputData = new byte[inputData.Length];

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

            return outputData;
        }

        private void ProcessVolumeCpu(
            byte[] inputData, byte[] outputData,
            int width, int height, int depth,
            int brightness, int contrast, byte blackPoint, byte whitePoint,
            int startZ = 0)
        {
            int sliceSize = width * height;

            // Process the entire volume voxel by voxel
            for (int i = 0; i < inputData.Length; i++)
            {
                byte value = inputData[i];
                int adjustedValue = ApplyAdjustment(value, blackPoint, whitePoint, brightness, contrast);
                outputData[i] = (byte)Math.Max(0, Math.Min(255, adjustedValue));
            }

            Console.WriteLine($"Processed {inputData.Length} voxels on CPU");
        }

        private void ProcessVolumeGpu(
            byte[] inputData, byte[] outputData,
            int width, int height, int depth,
            int brightness, int contrast, byte blackPoint, byte whitePoint,
            int startZ = 0)
        {
            // TODO: Implement GPU processing using ILGPU
            // For now, fall back to CPU implementation
            Console.WriteLine("GPU processing not implemented yet, falling back to CPU");
            ProcessVolumeCpu(inputData, outputData, width, height, depth, brightness, contrast, blackPoint, whitePoint, startZ);
        }

        private byte[] ProcessChunk(
            byte[] chunkData, int chunkDim,
            int brightness, int contrast, byte blackPoint, byte whitePoint,
            bool useGpu)
        {
            int chunkSize = chunkDim * chunkDim * chunkDim;
            byte[] result = new byte[chunkSize];

            if (useGpu && chunkSize > 1000000)  // Only use GPU for larger chunks
            {
                // TODO: Implement GPU processing using ILGPU
                // For now, fall back to CPU implementation
                return ProcessChunkCpu(chunkData, chunkDim, brightness, contrast, blackPoint, whitePoint);
            }
            else
            {
                return ProcessChunkCpu(chunkData, chunkDim, brightness, contrast, blackPoint, whitePoint);
            }
        }

        private byte[] ProcessChunkCpu(
            byte[] chunkData, int chunkDim,
            int brightness, int contrast, byte blackPoint, byte whitePoint)
        {
            int chunkSize = chunkDim * chunkDim * chunkDim;
            byte[] result = new byte[chunkSize];

            for (int i = 0; i < chunkSize; i++)
            {
                byte origValue = chunkData[i];
                int adjustedValue = ApplyAdjustment(origValue, blackPoint, whitePoint, brightness, contrast);
                result[i] = (byte)Math.Max(0, Math.Min(255, adjustedValue));
            }

            return result;
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