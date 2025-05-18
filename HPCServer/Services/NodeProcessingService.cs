using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ParallelComputingServer.Data;
using ParallelComputingServer.Models;
using ParallelComputingServer.Services.NodeHandlers;
using System.Net.Sockets;

namespace ParallelComputingServer.Services
{
    public class NodeProcessingService
    {
        private readonly ComputeService _computeService;
        private readonly EndpointService _endpointService;
        private Dictionary<string, INodeHandler> _nodeHandlers = new Dictionary<string, INodeHandler>();

        public NodeProcessingService(ComputeService computeService, EndpointService endpointService = null)
        {
            _computeService = computeService;
            _endpointService = endpointService;
            RegisterNodeHandlers();
        }

        private void RegisterNodeHandlers()
        {
            // Register the available node handlers

            // Tools nodes
            _nodeHandlers.Add("BrightnessContrastNode", new BrightnessContrastNodeHandler());
            // Uncomment these as you implement them
            //_nodeHandlers.Add("ResampleVolumeNode", new ResampleVolumeNodeHandler());
            //_nodeHandlers.Add("ThresholdNode", new ThresholdNodeHandler());
            //_nodeHandlers.Add("ManualThresholdingNode", new ManualThresholdingNodeHandler());
            //_nodeHandlers.Add("BinarizeNode", new BinarizeNodeHandler());
            //_nodeHandlers.Add("RemoveSmallIslandsNode", new RemoveSmallIslandsNodeHandler());

            // Simulation nodes
            //_nodeHandlers.Add("PoreNetworkNode", new PoreNetworkNodeHandler());
            //_nodeHandlers.Add("AcousticSimulationNode", new AcousticSimulationNodeHandler());
            //_nodeHandlers.Add("TriaxialSimulationNode", new TriaxialSimulationNodeHandler());
            //_nodeHandlers.Add("NMRSimulationNode", new NMRSimulationNodeHandler());

            // Filter nodes
            //_nodeHandlers.Add("FilterNode", new FilterNodeHandler());

            Console.WriteLine($"Registered {_nodeHandlers.Count} node handlers");
            foreach (var handler in _nodeHandlers.Keys)
            {
                Console.WriteLine($"  - {handler}");
            }
        }

        public string GetAvailableNodeTypes()
        {
            var nodeTypes = new List<string>(_nodeHandlers.Keys);

            return JsonSerializer.Serialize(new
            {
                Status = "OK",
                AvailableNodes = nodeTypes
            });
        }

        public async Task<string> ProcessNodeAsync(string nodeType, string compressedData)
        {
            try
            {
                Console.WriteLine($"Processing node: {nodeType}");

                if (!_nodeHandlers.TryGetValue(nodeType, out var handler))
                {
                    return JsonSerializer.Serialize(new
                    {
                        Status = "Error",
                        Message = $"Node type {nodeType} is not supported"
                    });
                }

                // Decompress and deserialize the input data
                byte[] compressedBytes = Convert.FromBase64String(compressedData);
                Dictionary<string, string> inputData = new Dictionary<string, string>();
                Dictionary<string, byte[]> binaryData = new Dictionary<string, byte[]>();

                using (MemoryStream compressedStream = new MemoryStream(compressedBytes))
                using (GZipStream decompressStream = new GZipStream(compressedStream, CompressionMode.Decompress))
                {
                    // Read metadata length
                    byte[] lengthBuffer = new byte[4];
                    await decompressStream.ReadAsync(lengthBuffer, 0, 4);
                    int metadataLength = BitConverter.ToInt32(lengthBuffer, 0);

                    // Read metadata
                    byte[] metadataBuffer = new byte[metadataLength];
                    await decompressStream.ReadAsync(metadataBuffer, 0, metadataLength);
                    string jsonMetadata = Encoding.UTF8.GetString(metadataBuffer);
                    inputData = JsonSerializer.Deserialize<Dictionary<string, string>>(jsonMetadata);

                    // Read binary keys length
                    await decompressStream.ReadAsync(lengthBuffer, 0, 4);
                    int binaryKeysLength = BitConverter.ToInt32(lengthBuffer, 0);

                    // Read binary keys
                    byte[] binaryKeysBuffer = new byte[binaryKeysLength];
                    await decompressStream.ReadAsync(binaryKeysBuffer, 0, binaryKeysLength);
                    string jsonBinaryKeys = Encoding.UTF8.GetString(binaryKeysBuffer);
                    List<string> binaryKeys = JsonSerializer.Deserialize<List<string>>(jsonBinaryKeys);

                    // Read binary data
                    foreach (var key in binaryKeys)
                    {
                        // Read key length
                        await decompressStream.ReadAsync(lengthBuffer, 0, 4);
                        int keyLength = BitConverter.ToInt32(lengthBuffer, 0);

                        // Read key
                        byte[] keyBuffer = new byte[keyLength];
                        await decompressStream.ReadAsync(keyBuffer, 0, keyLength);
                        string keyName = Encoding.UTF8.GetString(keyBuffer);

                        // Read value length
                        await decompressStream.ReadAsync(lengthBuffer, 0, 4);
                        int valueLength = BitConverter.ToInt32(lengthBuffer, 0);

                        // Read value
                        byte[] valueBuffer = new byte[valueLength];
                        await decompressStream.ReadAsync(valueBuffer, 0, valueLength);
                        binaryData[keyName] = valueBuffer;
                    }
                }

                // Check if we should distribute processing to endpoints
                bool shouldDistribute = ShouldDistributeProcessing(nodeType);

                Dictionary<string, string> outputData;
                if (shouldDistribute)
                {
                    // Process node with distributed endpoints
                    Console.WriteLine($"Distributing {nodeType} processing across endpoints");
                    outputData = await ProcessDistributedAsync(nodeType, inputData, binaryData);
                }
                else
                {
                    // Process node locally on the server
                    Console.WriteLine($"Processing {nodeType} locally on server");
                    outputData = await handler.ProcessAsync(inputData, binaryData, _computeService);
                }

                // Serialize and compress the output data including binary data
                string base64Data;

                using (MemoryStream memoryStream = new MemoryStream())
                {
                    using (GZipStream compressionStream = new GZipStream(memoryStream, CompressionMode.Compress))
                    {
                        // Get binary references from output data
                        Dictionary<string, byte[]> outputBinaryData = new Dictionary<string, byte[]>();
                        foreach (var entry in outputData)
                        {
                            if (entry.Value.StartsWith("binary_ref:"))
                            {
                                string binaryKey = entry.Value.Substring("binary_ref:".Length);
                                if (binaryData.TryGetValue(binaryKey, out byte[] data))
                                {
                                    outputBinaryData[binaryKey] = data;
                                }
                            }
                        }

                        // Prepare output metadata (without binary data)
                        string jsonOutput = JsonSerializer.Serialize(outputData);
                        byte[] jsonBytes = Encoding.UTF8.GetBytes(jsonOutput);

                        // Write metadata length and metadata
                        compressionStream.Write(BitConverter.GetBytes(jsonBytes.Length), 0, 4);
                        compressionStream.Write(jsonBytes, 0, jsonBytes.Length);

                        // Write binary keys length and keys
                        List<string> outputBinaryKeys = new List<string>(outputBinaryData.Keys);
                        string jsonBinaryKeys = JsonSerializer.Serialize(outputBinaryKeys);
                        byte[] binaryKeysBytes = Encoding.UTF8.GetBytes(jsonBinaryKeys);
                        compressionStream.Write(BitConverter.GetBytes(binaryKeysBytes.Length), 0, 4);
                        compressionStream.Write(binaryKeysBytes, 0, binaryKeysBytes.Length);

                        // Write binary data
                        foreach (var entry in outputBinaryData)
                        {
                            // Write key length and key
                            byte[] keyBytes = Encoding.UTF8.GetBytes(entry.Key);
                            compressionStream.Write(BitConverter.GetBytes(keyBytes.Length), 0, 4);
                            compressionStream.Write(keyBytes, 0, keyBytes.Length);

                            // Write value length and value
                            compressionStream.Write(BitConverter.GetBytes(entry.Value.Length), 0, 4);
                            compressionStream.Write(entry.Value, 0, entry.Value.Length);
                        }
                    }

                    base64Data = Convert.ToBase64String(memoryStream.ToArray());
                }

                return JsonSerializer.Serialize(new
                {
                    Status = "OK",
                    OutputData = base64Data
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing node: {ex.Message}");
                return JsonSerializer.Serialize(new
                {
                    Status = "Error",
                    Message = $"Error processing node: {ex.Message}"
                });
            }
        }

        private bool ShouldDistributeProcessing(string nodeType)
        {
            // Check if endpoint service is available
            if (_endpointService == null)
                return false;

            // Check if we have any connected endpoints
            var endpoints = _endpointService.GetConnectedEndpoints();
            if (endpoints == null || !endpoints.Any())
                return false;

            // Check if we have endpoints that are connected and not busy
            bool hasAvailableEndpoints = endpoints.Any(e => !string.IsNullOrEmpty(e.Status) &&
                                                           e.Status.ToLower() != "busy" &&
                                                           e.Status.ToLower() != "offline");

            // Check if the node type supports distributed processing
            bool supportsDistribution = nodeType switch
            {
                "BrightnessContrastNode" => true, // Our newly implemented node supports distribution
                // Add more node types as they're supported
                _ => false
            };

            return hasAvailableEndpoints && supportsDistribution;
        }

        private async Task<Dictionary<string, string>> ProcessDistributedAsync(
     string nodeType,
     Dictionary<string, string> inputData,
     Dictionary<string, byte[]> binaryData)
        {
            // Get connected and available endpoints
            var endpoints = _endpointService.GetConnectedEndpoints()
                .Where(e => !string.IsNullOrEmpty(e.Status) &&
                           e.Status.ToLower() != "busy" &&
                           e.Status.ToLower() != "offline")
                .ToList();

            if (!endpoints.Any())
            {
                // No available endpoints, fall back to local processing
                Console.WriteLine("No available endpoints for distributed processing, falling back to local");
                return await _nodeHandlers[nodeType].ProcessAsync(inputData, binaryData, _computeService);
            }

            Console.WriteLine($"Found {endpoints.Count} available endpoints for distributed processing");

            // Get volume dimensions
            if (!inputData.TryGetValue("Width", out var widthStr) ||
                !inputData.TryGetValue("Height", out var heightStr) ||
                !inputData.TryGetValue("Depth", out var depthStr))
            {
                throw new ArgumentException("Missing volume dimensions in input data");
            }

            int width = int.Parse(widthStr);
            int height = int.Parse(heightStr);
            int depth = int.Parse(depthStr);

            Console.WriteLine($"Processing volume: {width}x{height}x{depth}");

            // Get volume data
            byte[] volumeData = null;
            foreach (var entry in inputData)
            {
                if (entry.Value.StartsWith("binary_ref:") && entry.Key.Contains("Volume"))
                {
                    string key = entry.Value.Substring("binary_ref:".Length);
                    if (binaryData.TryGetValue(key, out byte[] data))
                    {
                        volumeData = data;
                        break;
                    }
                }
            }

            if (volumeData == null)
            {
                throw new ArgumentException("Missing volume data");
            }

            // Calculate load distribution based on endpoint capabilities
            var endpointCapabilities = new Dictionary<EndpointInfo, double>();
            foreach (var endpoint in endpoints)
            {
                // Simple heuristic: GPU endpoints get more work
                double capabilityFactor = endpoint.GpuEnabled ? 2.0 : 1.0;

                // If we have CPU load info, use it (lower load = higher capability)
                if (endpoint.CpuLoadPercent > 0 && endpoint.CpuLoadPercent <= 100)
                {
                    capabilityFactor *= (100.0 - endpoint.CpuLoadPercent) / 100.0;
                }

                endpointCapabilities[endpoint] = Math.Max(0.1, capabilityFactor); // Ensure minimum capability
                Console.WriteLine($"Endpoint {endpoint.Name}: Capability factor {capabilityFactor:F2}");
            }

            // Calculate total capability and proportions
            double totalCapability = endpointCapabilities.Values.Sum();
            var endpointSlices = new Dictionary<EndpointInfo, (int start, int count)>();

            int assignedSlices = 0;
            foreach (var endpoint in endpoints)
            {
                // Proportion based on capability
                double proportion = endpointCapabilities[endpoint] / totalCapability;
                // Calculate slice count, ensure minimum of 1 slice
                int sliceCount = Math.Max(1, (int)Math.Ceiling(depth * proportion));

                // Don't assign more slices than remaining
                if (assignedSlices + sliceCount > depth)
                    sliceCount = depth - assignedSlices;

                if (sliceCount <= 0)
                    continue;

                endpointSlices[endpoint] = (assignedSlices, sliceCount);
                assignedSlices += sliceCount;

                Console.WriteLine($"Endpoint {endpoint.Name}: Assigned slices {endpointSlices[endpoint].start} to " +
                                 $"{endpointSlices[endpoint].start + endpointSlices[endpoint].count - 1} ({sliceCount} slices, {proportion:P2})");

                if (assignedSlices >= depth)
                    break;
            }

            // Create tasks for each endpoint
            var tasks = new List<Task<Tuple<Dictionary<string, string>, Dictionary<string, byte[]>>>>();
            var endpointResults = new Dictionary<int, Dictionary<string, string>>();
            var endpointData = new Dictionary<int, Dictionary<string, byte[]>>();

            int endpointIndex = 0;
            foreach (var kvp in endpointSlices)
            {
                var endpoint = kvp.Key;
                int startZ = kvp.Value.start;
                int sliceCount = kvp.Value.count;

                // Calculate slice size
                int sliceSize = width * height;
                int subsetSize = sliceCount * sliceSize;

                // Create a subset of the volume data for this endpoint
                byte[] subsetData = new byte[subsetSize];
                Buffer.BlockCopy(volumeData, startZ * sliceSize, subsetData, 0, subsetSize);

                Console.WriteLine($"Created {subsetSize} byte subset for endpoint {endpoint.Name}");

                // Create task for this endpoint
                int currentIndex = endpointIndex++;
                var task = Task.Run(async () =>
                {
                    try
                    {
                        // Create input data for this endpoint
                        var endpointInputData = new Dictionary<string, string>(inputData);
                        var endpointBinary = new Dictionary<string, byte[]>(binaryData);

                        // Override dimensions for this subset
                        endpointInputData["Depth"] = sliceCount.ToString();
                        endpointInputData["StartZ"] = startZ.ToString(); // Add processing offset info

                        Console.WriteLine($"Processing slice subset on endpoint {endpoint.Name}: " +
                                         $"StartZ={startZ}, Depth={sliceCount}");

                        // Replace volume data with the subset
                        string volumeKey = null;
                        foreach (var entry in endpointInputData)
                        {
                            if (entry.Value.StartsWith("binary_ref:") && entry.Key.Contains("Volume"))
                            {
                                volumeKey = entry.Value.Substring("binary_ref:".Length);
                                break;
                            }
                        }

                        if (volumeKey != null)
                        {
                            endpointBinary[volumeKey] = subsetData;
                        }

                        // Send the task to the endpoint
                        Console.WriteLine($"Sending task to endpoint {endpoint.Name}");
                        var result = await ProcessOnEndpointAsync(nodeType, endpoint, endpointInputData, endpointBinary);
                        Console.WriteLine($"Received result from endpoint {endpoint.Name}");

                        // Store the result for this endpoint
                        lock (endpointResults)
                        {
                            endpointResults[currentIndex] = result.Item1;
                            endpointData[currentIndex] = result.Item2;
                        }

                        return result;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error processing on endpoint {endpoint.Name}: {ex.Message}");
                        throw;
                    }
                });

                tasks.Add(task);
            }

            // Wait for all tasks to complete (with error handling)
            Console.WriteLine($"Waiting for {tasks.Count} endpoint tasks to complete...");
            try
            {
                // Try to wait for all tasks
                await Task.WhenAll(tasks);
                Console.WriteLine("All endpoint tasks completed successfully");
            }
            catch (Exception ex)
            {
                // One or more tasks failed
                Console.WriteLine($"Some endpoint tasks failed: {ex.Message}");

                // Check if all tasks failed or just some
                bool allFailed = true;
                foreach (var task in tasks)
                {
                    if (task.IsCompletedSuccessfully)
                    {
                        allFailed = false;
                        break;
                    }
                }

                if (allFailed)
                {
                    // If all tasks failed, fall back to local processing
                    Console.WriteLine("All endpoint tasks failed, falling back to local processing");
                    return await _nodeHandlers[nodeType].ProcessAsync(inputData, binaryData, _computeService);
                }

                // Otherwise continue with the successful results we have
                Console.WriteLine("Continuing with partial results from successful endpoints");
            }

            // Merge results from all endpoints
            Console.WriteLine("Merging results from all endpoints");
            return await MergeResultsAsync(
                nodeType, inputData, endpoints.Count,
                endpointResults, endpointData,
                width, height, depth);
        }

        private async Task<Tuple<Dictionary<string, string>, Dictionary<string, byte[]>>> ProcessOnEndpointAsync(
     string nodeType,
     EndpointInfo endpoint,
     Dictionary<string, string> inputData,
     Dictionary<string, byte[]> binaryData)
        {
            try
            {
                // Create command to send to endpoint
                var compressedData = await CompressDataAsync(inputData, binaryData);

                var command = new
                {
                    Command = "EXECUTE_NODE",
                    NodeType = nodeType,
                    InputData = compressedData
                };

                // Convert command to JSON
                string commandJson = JsonSerializer.Serialize(command);

                // Connect directly to endpoint
                using var client = new TcpClient();
                await client.ConnectAsync(endpoint.EndpointIP, endpoint.EndpointPort);

                using NetworkStream stream = client.GetStream();
                byte[] commandBytes = Encoding.UTF8.GetBytes(commandJson);
                await stream.WriteAsync(commandBytes, 0, commandBytes.Length);

                // Read response with timeout
                var buffer = new byte[32768]; // Larger buffer for binary data
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5)); // 5 minute timeout
                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token);
                string response = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                // Parse the response
                var responseJson = JsonSerializer.Deserialize<JsonElement>(response);

                if (responseJson.TryGetProperty("Status", out var status) && status.GetString() == "OK" &&
                    responseJson.TryGetProperty("OutputData", out var outputDataElement))
                {
                    // Parse and decompress the output data
                    string base64Data = outputDataElement.GetString();
                    return await DecompressDataAsync(base64Data);
                }

                throw new Exception($"Failed to process on endpoint {endpoint.Name}: {response}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing on endpoint {endpoint.Name}: {ex.Message}");
                throw;
            }
        }
        private async Task<Dictionary<string, string>> MergeResultsAsync(
            string nodeType,
            Dictionary<string, string> originalInputData,
            int endpointCount,
            Dictionary<int, Dictionary<string, string>> endpointResults,
            Dictionary<int, Dictionary<string, byte[]>> endpointData,
            int width, int height, int depth)
        {
            Console.WriteLine("Merging results from all endpoints");

            // For BrightnessContrastNode, we need to merge the processed volume data
            if (nodeType == "BrightnessContrastNode")
            {
                return await MergeBrightnessContrastResultsAsync(
                    originalInputData, endpointCount,
                    endpointResults, endpointData,
                    width, height, depth);
            }

            // For other node types, implement their merge logic
            throw new NotImplementedException($"Distributed processing for {nodeType} is not fully implemented yet");
        }

        private async Task<Dictionary<string, string>> MergeBrightnessContrastResultsAsync(
    Dictionary<string, string> originalInputData,
    int endpointCount,
    Dictionary<int, Dictionary<string, string>> endpointResults,
    Dictionary<int, Dictionary<string, byte[]>> endpointData,
    int width, int height, int depth)
        {
            Console.WriteLine("Merging brightness contrast results from all endpoints");

            // Create output dictionary based on the original input
            var outputData = new Dictionary<string, string>(originalInputData);

            // Create a combined volume
            byte[] mergedVolume = new byte[width * height * depth];

            // Determine slices per endpoint
            int slicesPerEndpoint = (depth + endpointCount - 1) / endpointCount;

            // Merge all the partial results into the complete volume
            int totalProcessedSlices = 0;
            for (int i = 0; i < endpointCount; i++)
            {
                if (!endpointResults.TryGetValue(i, out var result) ||
                    !endpointData.TryGetValue(i, out var binaryData))
                {
                    Console.WriteLine($"Warning: Missing results from endpoint {i}");
                    continue; // Skip missing results
                }

                // Calculate z-range for this endpoint
                int startZ = i * slicesPerEndpoint;
                int endZ = Math.Min((i + 1) * slicesPerEndpoint - 1, depth - 1);

                if (startZ >= depth)
                {
                    Console.WriteLine($"Skipping endpoint {i} - start slice {startZ} is beyond volume depth {depth}");
                    continue;
                }

                int sliceCount = endZ - startZ + 1;
                int sliceSize = width * height;
                totalProcessedSlices += sliceCount;

                Console.WriteLine($"Merging results from endpoint {i}: slices {startZ}-{endZ} ({sliceCount} slices)");

                // Get the processed data from this endpoint
                byte[] processedData = null;
                string volumeKey = null;

                foreach (var entry in result)
                {
                    if (entry.Value.StartsWith("binary_ref:") &&
                        (entry.Key == "ProcessedVolume" || entry.Key.Contains("Volume")))
                    {
                        volumeKey = entry.Value.Substring("binary_ref:".Length);
                        if (binaryData.TryGetValue(volumeKey, out processedData))
                            break;
                    }
                }

                if (processedData == null)
                {
                    Console.WriteLine($"Warning: No processed data found from endpoint {i}");
                    continue;
                }

                if (processedData.Length != sliceCount * sliceSize)
                {
                    Console.WriteLine($"Warning: Data size mismatch from endpoint {i}. " +
                                     $"Expected {sliceCount * sliceSize} bytes, received {processedData.Length} bytes");

                    // Try to handle partial data if possible
                    int copyLength = Math.Min(processedData.Length, sliceCount * sliceSize);
                    if (copyLength > 0 && startZ * sliceSize + copyLength <= mergedVolume.Length)
                    {
                        Buffer.BlockCopy(processedData, 0, mergedVolume, startZ * sliceSize, copyLength);
                        Console.WriteLine($"Copied {copyLength} bytes of partial data from endpoint {i}");
                    }

                    continue;
                }

                // Copy this endpoint's data to the correct position in the merged volume
                Buffer.BlockCopy(processedData, 0, mergedVolume, startZ * sliceSize, processedData.Length);
                Console.WriteLine($"Successfully merged {processedData.Length} bytes from endpoint {i}");
            }

            // Verify we have all the data
            Console.WriteLine($"Volume merge complete. Total processed slices: {totalProcessedSlices}, Expected: {depth}");
            if (totalProcessedSlices != depth)
            {
                Console.WriteLine("WARNING: Not all slices were processed. Result may be incomplete.");
            }

            // Create output data structure
            outputData["Width"] = width.ToString();
            outputData["Height"] = height.ToString();
            outputData["Depth"] = depth.ToString();

            // Add processed volume to binary data
            string outputKey = "processed_volume";
            outputData["ProcessedVolume"] = $"binary_ref:{outputKey}";

            // Add the merged volume to binary data that will be returned
            Dictionary<string, byte[]> outputBinaryData;
            if (endpointData.TryGetValue(0, out var firstEndpointData))
            {
                outputBinaryData = firstEndpointData;
            }
            else
            {
                outputBinaryData = new Dictionary<string, byte[]>();
                endpointData[0] = outputBinaryData;
            }

            // Add the merged volume to the binary data
            outputBinaryData[outputKey] = mergedVolume;

            return outputData;
        }

        private async Task<string> CompressDataAsync(Dictionary<string, string> inputData, Dictionary<string, byte[]> binaryData)
        {
            using (MemoryStream memoryStream = new MemoryStream())
            {
                using (GZipStream compressionStream = new GZipStream(memoryStream, CompressionMode.Compress))
                {
                    // Write metadata
                    string jsonMetadata = JsonSerializer.Serialize(inputData);
                    byte[] metadataBytes = Encoding.UTF8.GetBytes(jsonMetadata);
                    await compressionStream.WriteAsync(BitConverter.GetBytes(metadataBytes.Length), 0, 4);
                    await compressionStream.WriteAsync(metadataBytes, 0, metadataBytes.Length);

                    // Write binary keys
                    List<string> binaryKeys = new List<string>(binaryData.Keys);
                    string jsonBinaryKeys = JsonSerializer.Serialize(binaryKeys);
                    byte[] binaryKeysBytes = Encoding.UTF8.GetBytes(jsonBinaryKeys);
                    await compressionStream.WriteAsync(BitConverter.GetBytes(binaryKeysBytes.Length), 0, 4);
                    await compressionStream.WriteAsync(binaryKeysBytes, 0, binaryKeysBytes.Length);

                    // Write binary data
                    foreach (var entry in binaryData)
                    {
                        // Write key
                        byte[] keyBytes = Encoding.UTF8.GetBytes(entry.Key);
                        await compressionStream.WriteAsync(BitConverter.GetBytes(keyBytes.Length), 0, 4);
                        await compressionStream.WriteAsync(keyBytes, 0, keyBytes.Length);

                        // Write value
                        await compressionStream.WriteAsync(BitConverter.GetBytes(entry.Value.Length), 0, 4);
                        await compressionStream.WriteAsync(entry.Value, 0, entry.Value.Length);
                    }
                }

                return Convert.ToBase64String(memoryStream.ToArray());
            }
        }

        private async Task<Tuple<Dictionary<string, string>, Dictionary<string, byte[]>>> DecompressDataAsync(string base64Data)
        {
            Dictionary<string, string> metadata = new Dictionary<string, string>();
            Dictionary<string, byte[]> binaryData = new Dictionary<string, byte[]>();

            byte[] compressedBytes = Convert.FromBase64String(base64Data);

            using (MemoryStream compressedStream = new MemoryStream(compressedBytes))
            using (GZipStream decompressStream = new GZipStream(compressedStream, CompressionMode.Decompress))
            {
                // Read metadata length
                byte[] lengthBuffer = new byte[4];
                await decompressStream.ReadAsync(lengthBuffer, 0, 4);
                int metadataLength = BitConverter.ToInt32(lengthBuffer, 0);

                // Read metadata
                byte[] metadataBuffer = new byte[metadataLength];
                await decompressStream.ReadAsync(metadataBuffer, 0, metadataLength);
                string jsonMetadata = Encoding.UTF8.GetString(metadataBuffer);
                metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(jsonMetadata);

                // Read binary keys length
                await decompressStream.ReadAsync(lengthBuffer, 0, 4);
                int binaryKeysLength = BitConverter.ToInt32(lengthBuffer, 0);

                // Read binary keys
                byte[] binaryKeysBuffer = new byte[binaryKeysLength];
                await decompressStream.ReadAsync(binaryKeysBuffer, 0, binaryKeysLength);
                string jsonBinaryKeys = Encoding.UTF8.GetString(binaryKeysBuffer);
                List<string> binaryKeys = JsonSerializer.Deserialize<List<string>>(jsonBinaryKeys);

                // Read binary data
                foreach (var key in binaryKeys)
                {
                    // Read key length
                    await decompressStream.ReadAsync(lengthBuffer, 0, 4);
                    int keyLength = BitConverter.ToInt32(lengthBuffer, 0);

                    // Read key
                    byte[] keyBuffer = new byte[keyLength];
                    await decompressStream.ReadAsync(keyBuffer, 0, keyLength);
                    string keyName = Encoding.UTF8.GetString(keyBuffer);

                    // Read value length
                    await decompressStream.ReadAsync(lengthBuffer, 0, 4);
                    int valueLength = BitConverter.ToInt32(lengthBuffer, 0);

                    // Read value
                    byte[] valueBuffer = new byte[valueLength];
                    await decompressStream.ReadAsync(valueBuffer, 0, valueLength);
                    binaryData[keyName] = valueBuffer;
                }
            }

            return new Tuple<Dictionary<string, string>, Dictionary<string, byte[]>>(metadata, binaryData);
        }
    }

    // Interface for node handlers
    public interface INodeHandler
    {
        Task<Dictionary<string, string>> ProcessAsync(
            Dictionary<string, string> inputData,
            Dictionary<string, byte[]> binaryData,
            ComputeService computeService);
    }

    // Base class for node handlers
    public abstract class BaseNodeHandler : INodeHandler
    {
        public abstract Task<Dictionary<string, string>> ProcessAsync(
            Dictionary<string, string> inputData,
            Dictionary<string, byte[]> binaryData,
            ComputeService computeService);

        // Helper method to log node processing
        protected void LogProcessing(string nodeType)
        {
            Console.WriteLine($"Processing {nodeType}...");
        }
    }
}