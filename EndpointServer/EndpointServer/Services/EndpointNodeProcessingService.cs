using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ParallelComputingEndpoint
{
    public class EndpointNodeProcessingService
    {
        private readonly EndpointComputeService _computeService;
        private Dictionary<string, INodeHandler> _nodeHandlers = new Dictionary<string, INodeHandler>();
        private readonly LogPanel _logPanel;

        public EndpointNodeProcessingService(EndpointComputeService computeService, LogPanel logPanel = null)
        {
            _computeService = computeService;
            _logPanel = logPanel;
            RegisterNodeHandlers();
        }

        private void RegisterNodeHandlers()
        {
            // Register the available node handlers
            _nodeHandlers.Add("BrightnessContrastNode", new BrightnessContrastNodeHandler());
            // _nodeHandlers.Add("ResampleVolumeNode", new ResampleVolumeNodeHandler());
            // _nodeHandlers.Add("ThresholdNode", new ThresholdNodeHandler());
            // _nodeHandlers.Add("ManualThresholdingNode", new ManualThresholdingNodeHandler());
            // _nodeHandlers.Add("BinarizeNode", new BinarizeNodeHandler());
            // _nodeHandlers.Add("RemoveSmallIslandsNode", new RemoveSmallIslandsNodeHandler());
            // _nodeHandlers.Add("PoreNetworkNode", new PoreNetworkNodeHandler());
            // _nodeHandlers.Add("AcousticSimulationNode", new AcousticSimulationNodeHandler());
            // _nodeHandlers.Add("TriaxialSimulationNode", new TriaxialSimulationNodeHandler());
            // _nodeHandlers.Add("NMRSimulationNode", new NMRSimulationNodeHandler());
            // _nodeHandlers.Add("FilterNode", new FilterNodeHandler());
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
            var progressTracker = new NodeProcessingProgressTracker(_logPanel);
            progressTracker.Show(nodeType);

            try
            {
                Console.WriteLine($"Processing node: {nodeType}");
                progressTracker.SetStage(ProcessingStage.ReceivingData);

                if (!_nodeHandlers.TryGetValue(nodeType, out var handler))
                {
                    progressTracker.SetStage(ProcessingStage.Failed, $"Node type {nodeType} is not supported");
                    progressTracker.Close();
                    return JsonSerializer.Serialize(new
                    {
                        Status = "Error",
                        Message = $"Node type {nodeType} is not supported"
                    });
                }

                // Decompress and deserialize the input data
                progressTracker.SetDetails("Decompressione dati in corso...");
                progressTracker.UpdateProgress(10);

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
                    progressTracker.UpdateProgress(20);
                    byte[] metadataBuffer = new byte[metadataLength];
                    decompressStream.Read(metadataBuffer, 0, metadataLength);
                    string jsonMetadata = Encoding.UTF8.GetString(metadataBuffer);
                    inputData = JsonSerializer.Deserialize<Dictionary<string, string>>(jsonMetadata);

                    // Read binary keys length
                    progressTracker.UpdateProgress(30);
                    decompressStream.Read(lengthBuffer, 0, 4);
                    int binaryKeysLength = BitConverter.ToInt32(lengthBuffer, 0);

                    // Read binary keys
                    byte[] binaryKeysBuffer = new byte[binaryKeysLength];
                    decompressStream.Read(binaryKeysBuffer, 0, binaryKeysLength);
                    string jsonBinaryKeys = Encoding.UTF8.GetString(binaryKeysBuffer);
                    List<string> binaryKeys = JsonSerializer.Deserialize<List<string>>(jsonBinaryKeys);

                    // Read binary data
                    progressTracker.UpdateProgress(40);
                    foreach (var key in binaryKeys)
                    {
                        if (progressTracker.IsCancelled)
                        {
                            progressTracker.Close();
                            return JsonSerializer.Serialize(new
                            {
                                Status = "Cancelled",
                                Message = "Operation cancelled by user"
                            });
                        }

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

                        progressTracker.SetDetails($"Lettura blocco dati: {keyName}");
                    }
                }

                // Process the node with the handler
                progressTracker.SetStage(ProcessingStage.ProcessingData);
                progressTracker.UpdateProgress(50);

                // Extract volume dimensions for progress reporting
                int width = 0, height = 0, depth = 0;
                if (inputData.TryGetValue("Width", out string widthStr)) int.TryParse(widthStr, out width);
                if (inputData.TryGetValue("Height", out string heightStr)) int.TryParse(heightStr, out height);
                if (inputData.TryGetValue("Depth", out string depthStr)) int.TryParse(depthStr, out depth);

                int totalVoxels = width * height * depth;
                progressTracker.SetDetails($"Elaborazione volume {width}x{height}x{depth}");

                // Aggiungi il tracking del progresso al handler (se supportato)
                if (handler is IProgressTrackable trackableHandler)
                {
                    trackableHandler.SetProgressCallback((percent) => {
                        // Scala la percentuale per coprire solo una parte dell'elaborazione complessiva (50%-90%)
                        int scaledPercent = 50 + (int)(percent * 0.4);
                        progressTracker.UpdateProgress(scaledPercent);
                    });
                }

                var outputData = await handler.ProcessAsync(inputData, binaryData, _computeService);

                // Serialize and compress the output data
                progressTracker.SetStage(ProcessingStage.SendingResults);
                progressTracker.UpdateProgress(90);

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

                progressTracker.SetStage(ProcessingStage.Completed);
                progressTracker.UpdateProgress(100);
                progressTracker.Close();

                return JsonSerializer.Serialize(new
                {
                    Status = "OK",
                    OutputData = base64Data
                });
            }
            catch (Exception ex)
            {
                progressTracker.SetStage(ProcessingStage.Failed, $"Error: {ex.Message}");
                progressTracker.Close();

                Console.WriteLine($"Error processing node: {ex.Message}");
                return JsonSerializer.Serialize(new
                {
                    Status = "Error",
                    Message = $"Error processing node: {ex.Message}"
                });
            }
        }
    }
}