using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ParallelComputingServer.Services
{
    public class NodeProcessingService
    {
        private readonly ComputeService _computeService;
        private Dictionary<string, INodeHandler> _nodeHandlers = new Dictionary<string, INodeHandler>();

        public NodeProcessingService(ComputeService computeService)
        {
            _computeService = computeService;
            RegisterNodeHandlers();
        }

        private void RegisterNodeHandlers()
        {
            // Register the available node handlers
            /*
            // Tools nodes
            _nodeHandlers.Add("BrightnessContrastNode", new BrightnessContrastNodeHandler());
            _nodeHandlers.Add("ResampleVolumeNode", new ResampleVolumeNodeHandler());
            _nodeHandlers.Add("ThresholdNode", new ThresholdNodeHandler());
            _nodeHandlers.Add("ManualThresholdingNode", new ManualThresholdingNodeHandler());
            _nodeHandlers.Add("BinarizeNode", new BinarizeNodeHandler());
            _nodeHandlers.Add("RemoveSmallIslandsNode", new RemoveSmallIslandsNodeHandler());

            // Simulation nodes
            _nodeHandlers.Add("PoreNetworkNode", new PoreNetworkNodeHandler());
            _nodeHandlers.Add("AcousticSimulationNode", new AcousticSimulationNodeHandler());
            _nodeHandlers.Add("TriaxialSimulationNode", new TriaxialSimulationNodeHandler());
            _nodeHandlers.Add("NMRSimulationNode", new NMRSimulationNodeHandler());

            // Filter nodes
            _nodeHandlers.Add("FilterNode", new FilterNodeHandler());*/
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
                    decompressStream.Read(lengthBuffer, 0, 4);
                    int metadataLength = BitConverter.ToInt32(lengthBuffer, 0);

                    // Read metadata
                    byte[] metadataBuffer = new byte[metadataLength];
                    decompressStream.Read(metadataBuffer, 0, metadataLength);
                    string jsonMetadata = Encoding.UTF8.GetString(metadataBuffer);
                    inputData = JsonSerializer.Deserialize<Dictionary<string, string>>(jsonMetadata);

                    // Read binary keys length
                    decompressStream.Read(lengthBuffer, 0, 4);
                    int binaryKeysLength = BitConverter.ToInt32(lengthBuffer, 0);

                    // Read binary keys
                    byte[] binaryKeysBuffer = new byte[binaryKeysLength];
                    decompressStream.Read(binaryKeysBuffer, 0, binaryKeysLength);
                    string jsonBinaryKeys = Encoding.UTF8.GetString(binaryKeysBuffer);
                    List<string> binaryKeys = JsonSerializer.Deserialize<List<string>>(jsonBinaryKeys);

                    // Read binary data
                    foreach (var key in binaryKeys)
                    {
                        // Read key length
                        decompressStream.Read(lengthBuffer, 0, 4);
                        int keyLength = BitConverter.ToInt32(lengthBuffer, 0);

                        // Read key
                        byte[] keyBuffer = new byte[keyLength];
                        decompressStream.Read(keyBuffer, 0, keyLength);
                        string keyName = Encoding.UTF8.GetString(keyBuffer);

                        // Read value length
                        decompressStream.Read(lengthBuffer, 0, 4);
                        int valueLength = BitConverter.ToInt32(lengthBuffer, 0);

                        // Read value
                        byte[] valueBuffer = new byte[valueLength];
                        decompressStream.Read(valueBuffer, 0, valueLength);
                        binaryData[keyName] = valueBuffer;
                    }
                }

                // Process the node with the handler
                var outputData = await handler.ProcessAsync(inputData, binaryData, _computeService);

                // Serialize and compress the output data
                string jsonOutput = JsonSerializer.Serialize(outputData);
                string base64Data;

                using (MemoryStream memoryStream = new MemoryStream())
                {
                    using (GZipStream compressionStream = new GZipStream(memoryStream, CompressionMode.Compress))
                    using (StreamWriter writer = new StreamWriter(compressionStream))
                    {
                        writer.Write(jsonOutput);
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