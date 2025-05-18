using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ParallelComputingServer.Data;
using ParallelComputingServer.Services;
using ParallelComputingServer.UI;

namespace ParallelComputingServer.Services
{
    /// <summary>
    /// Command handlers for dataset transfer operations to be integrated into NetworkService
    /// </summary>
    public class DatasetTransferCommands
    {
        private readonly DatasetTransferService _transferService;

        public DatasetTransferCommands(DatasetTransferService transferService)
        {
            _transferService = transferService;
        }

        /// <summary>
        /// Process a command related to dataset transfers
        /// </summary>
        public async Task<string> ProcessDatasetCommandAsync(JsonElement commandObj)
        {
            try
            {
                if (!commandObj.TryGetProperty("DatasetCommand", out JsonElement commandTypeElement))
                {
                    return JsonSerializer.Serialize(new
                    {
                        Status = "Error",
                        Message = "Missing DatasetCommand property"
                    });
                }

                string commandType = commandTypeElement.GetString();

                switch (commandType)
                {
                    case "INITIALIZE_TRANSFER":
                        return await HandleInitializeTransferCommand(commandObj);

                    case "UPLOAD_VOLUME_CHUNK":
                        return await HandleUploadVolumeChunkCommand(commandObj);

                    case "UPLOAD_LABELS_CHUNK":
                        return await HandleUploadLabelsChunkCommand(commandObj);

                    case "COMPLETE_TRANSFER":
                        return await HandleCompleteTransferCommand(commandObj);

                    case "PROCESS_DATASET":
                        return await HandleProcessDatasetCommand(commandObj);

                    case "GET_VOLUME_CHUNK":
                        return await HandleGetVolumeChunkCommand(commandObj);

                    case "GET_LABELS_CHUNK":
                        return await HandleGetLabelsChunkCommand(commandObj);

                    case "GET_TRANSFER_STATUS":
                        return await HandleGetTransferStatusCommand(commandObj);

                    case "CLEANUP_TRANSFER":
                        return await HandleCleanupTransferCommand(commandObj);

                    default:
                        return JsonSerializer.Serialize(new
                        {
                            Status = "Error",
                            Message = $"Unknown dataset command: {commandType}"
                        });
                }
            }
            catch (Exception ex)
            {
                TuiLogger.Log($"Error processing dataset command: {ex.Message}");
                return JsonSerializer.Serialize(new
                {
                    Status = "Error",
                    Message = $"Error processing dataset command: {ex.Message}"
                });
            }
        }

        /// <summary>
        /// Handles the command to initialize a dataset transfer
        /// </summary>
        private async Task<string> HandleInitializeTransferCommand(JsonElement commandObj)
        {
            try
            {
                // Parse the metadata
                if (!commandObj.TryGetProperty("Metadata", out JsonElement metadataElement))
                {
                    return JsonSerializer.Serialize(new
                    {
                        Status = "Error",
                        Message = "Missing Metadata property"
                    });
                }

                // Deserialize the metadata
                var metadata = JsonSerializer.Deserialize<DatasetMetadata>(metadataElement.GetRawText());

                // Validate metadata
                if (metadata == null || metadata.Width <= 0 || metadata.Height <= 0 || metadata.Depth <= 0 || metadata.ChunkDim <= 0)
                {
                    return JsonSerializer.Serialize(new
                    {
                        Status = "Error",
                        Message = "Invalid metadata"
                    });
                }

                // Initialize the transfer
                string transferId = await _transferService.InitializeTransferAsync(metadata);

                // Return the transfer ID
                return JsonSerializer.Serialize(new
                {
                    Status = "OK",
                    TransferId = transferId,
                    Message = "Transfer initialized successfully"
                });
            }
            catch (Exception ex)
            {
                TuiLogger.Log($"Error initializing transfer: {ex.Message}");
                return JsonSerializer.Serialize(new
                {
                    Status = "Error",
                    Message = $"Error initializing transfer: {ex.Message}"
                });
            }
        }

        /// <summary>
        /// Handles the command to upload a volume chunk
        /// </summary>
        private async Task<string> HandleUploadVolumeChunkCommand(JsonElement commandObj)
        {
            try
            {
                // Get the required parameters
                if (!commandObj.TryGetProperty("TransferId", out JsonElement transferIdElement) ||
                    !commandObj.TryGetProperty("ChunkIndex", out JsonElement chunkIndexElement) ||
                    !commandObj.TryGetProperty("ChunkData", out JsonElement chunkDataElement))
                {
                    return JsonSerializer.Serialize(new
                    {
                        Status = "Error",
                        Message = "Missing required parameters"
                    });
                }

                string transferId = transferIdElement.GetString();
                int chunkIndex = chunkIndexElement.GetInt32();
                string base64Data = chunkDataElement.GetString();

                // Convert base64 to bytes
                byte[] compressedData = Convert.FromBase64String(base64Data);

                // Process the chunk
                bool success = await _transferService.ProcessVolumeChunkAsync(transferId, chunkIndex, compressedData);

                if (success)
                {
                    return JsonSerializer.Serialize(new
                    {
                        Status = "OK",
                        Message = $"Chunk {chunkIndex} processed successfully"
                    });
                }
                else
                {
                    return JsonSerializer.Serialize(new
                    {
                        Status = "Error",
                        Message = $"Failed to process chunk {chunkIndex}"
                    });
                }
            }
            catch (Exception ex)
            {
                TuiLogger.Log($"Error processing volume chunk: {ex.Message}");
                return JsonSerializer.Serialize(new
                {
                    Status = "Error",
                    Message = $"Error processing volume chunk: {ex.Message}"
                });
            }
        }

        /// <summary>
        /// Handles the command to upload a labels chunk
        /// </summary>
        private async Task<string> HandleUploadLabelsChunkCommand(JsonElement commandObj)
        {
            try
            {
                // Get the required parameters
                if (!commandObj.TryGetProperty("TransferId", out JsonElement transferIdElement) ||
                    !commandObj.TryGetProperty("ChunkIndex", out JsonElement chunkIndexElement) ||
                    !commandObj.TryGetProperty("ChunkData", out JsonElement chunkDataElement))
                {
                    return JsonSerializer.Serialize(new
                    {
                        Status = "Error",
                        Message = "Missing required parameters"
                    });
                }

                string transferId = transferIdElement.GetString();
                int chunkIndex = chunkIndexElement.GetInt32();
                string base64Data = chunkDataElement.GetString();

                // Convert base64 to bytes
                byte[] compressedData = Convert.FromBase64String(base64Data);

                // Process the chunk
                bool success = await _transferService.ProcessLabelsChunkAsync(transferId, chunkIndex, compressedData);

                if (success)
                {
                    return JsonSerializer.Serialize(new
                    {
                        Status = "OK",
                        Message = $"Labels chunk {chunkIndex} processed successfully"
                    });
                }
                else
                {
                    return JsonSerializer.Serialize(new
                    {
                        Status = "Error",
                        Message = $"Failed to process labels chunk {chunkIndex}"
                    });
                }
            }
            catch (Exception ex)
            {
                TuiLogger.Log($"Error processing labels chunk: {ex.Message}");
                return JsonSerializer.Serialize(new
                {
                    Status = "Error",
                    Message = $"Error processing labels chunk: {ex.Message}"
                });
            }
        }

        /// <summary>
        /// Handles the command to complete a transfer
        /// </summary>
        private async Task<string> HandleCompleteTransferCommand(JsonElement commandObj)
        {
            try
            {
                // Get the transfer ID
                if (!commandObj.TryGetProperty("TransferId", out JsonElement transferIdElement))
                {
                    return JsonSerializer.Serialize(new
                    {
                        Status = "Error",
                        Message = "Missing TransferId parameter"
                    });
                }

                string transferId = transferIdElement.GetString();

                // Complete the transfer
                bool success = await _transferService.CompleteTransferAsync(transferId);

                if (success)
                {
                    return JsonSerializer.Serialize(new
                    {
                        Status = "OK",
                        Message = "Transfer completed successfully"
                    });
                }
                else
                {
                    return JsonSerializer.Serialize(new
                    {
                        Status = "Error",
                        Message = "Failed to complete transfer"
                    });
                }
            }
            catch (Exception ex)
            {
                TuiLogger.Log($"Error completing transfer: {ex.Message}");
                return JsonSerializer.Serialize(new
                {
                    Status = "Error",
                    Message = $"Error completing transfer: {ex.Message}"
                });
            }
        }

        /// <summary>
        /// Handles the command to process a dataset
        /// </summary>
        private async Task<string> HandleProcessDatasetCommand(JsonElement commandObj)
        {
            try
            {
                // Get the required parameters
                if (!commandObj.TryGetProperty("DatasetId", out JsonElement datasetIdElement) ||
                    !commandObj.TryGetProperty("NodeType", out JsonElement nodeTypeElement))
                {
                    return JsonSerializer.Serialize(new
                    {
                        Status = "Error",
                        Message = "Missing required parameters"
                    });
                }

                string datasetId = datasetIdElement.GetString();
                string nodeType = nodeTypeElement.GetString();

                // Extract optional parameters
                Dictionary<string, object> parameters = new Dictionary<string, object>();
                if (commandObj.TryGetProperty("Parameters", out JsonElement parametersElement))
                {
                    foreach (var param in parametersElement.EnumerateObject())
                    {
                        parameters[param.Name] = param.Value.GetRawText();
                    }
                }

                // Process the dataset
                var result = await _transferService.ProcessDatasetAsync(datasetId, nodeType, parameters);

                if (result != null)
                {
                    return JsonSerializer.Serialize(new
                    {
                        Status = "OK",
                        Result = result,
                        Message = "Dataset processed successfully"
                    });
                }
                else
                {
                    return JsonSerializer.Serialize(new
                    {
                        Status = "Error",
                        Message = "Failed to process dataset"
                    });
                }
            }
            catch (Exception ex)
            {
                TuiLogger.Log($"Error processing dataset: {ex.Message}");
                return JsonSerializer.Serialize(new
                {
                    Status = "Error",
                    Message = $"Error processing dataset: {ex.Message}"
                });
            }
        }

        /// <summary>
        /// Handles the command to get a processed volume chunk
        /// </summary>
        private async Task<string> HandleGetVolumeChunkCommand(JsonElement commandObj)
        {
            try
            {
                // Get the required parameters
                if (!commandObj.TryGetProperty("DatasetId", out JsonElement datasetIdElement) ||
                    !commandObj.TryGetProperty("ChunkIndex", out JsonElement chunkIndexElement))
                {
                    return JsonSerializer.Serialize(new
                    {
                        Status = "Error",
                        Message = "Missing required parameters"
                    });
                }

                string datasetId = datasetIdElement.GetString();
                int chunkIndex = chunkIndexElement.GetInt32();

                // Get the chunk data
                byte[] compressedData = await _transferService.GetProcessedVolumeChunkAsync(datasetId, chunkIndex);

                if (compressedData != null)
                {
                    return JsonSerializer.Serialize(new
                    {
                        Status = "OK",
                        ChunkData = Convert.ToBase64String(compressedData)
                    });
                }
                else
                {
                    return JsonSerializer.Serialize(new
                    {
                        Status = "Error",
                        Message = $"Failed to get volume chunk {chunkIndex}"
                    });
                }
            }
            catch (Exception ex)
            {
                TuiLogger.Log($"Error getting volume chunk: {ex.Message}");
                return JsonSerializer.Serialize(new
                {
                    Status = "Error",
                    Message = $"Error getting volume chunk: {ex.Message}"
                });
            }
        }

        /// <summary>
        /// Handles the command to get a processed labels chunk
        /// </summary>
        private async Task<string> HandleGetLabelsChunkCommand(JsonElement commandObj)
        {
            try
            {
                // Get the required parameters
                if (!commandObj.TryGetProperty("DatasetId", out JsonElement datasetIdElement) ||
                    !commandObj.TryGetProperty("ChunkIndex", out JsonElement chunkIndexElement))
                {
                    return JsonSerializer.Serialize(new
                    {
                        Status = "Error",
                        Message = "Missing required parameters"
                    });
                }

                string datasetId = datasetIdElement.GetString();
                int chunkIndex = chunkIndexElement.GetInt32();

                // Get the chunk data
                byte[] compressedData = await _transferService.GetProcessedLabelsChunkAsync(datasetId, chunkIndex);

                if (compressedData != null)
                {
                    return JsonSerializer.Serialize(new
                    {
                        Status = "OK",
                        ChunkData = Convert.ToBase64String(compressedData)
                    });
                }
                else
                {
                    return JsonSerializer.Serialize(new
                    {
                        Status = "Error",
                        Message = $"Failed to get labels chunk {chunkIndex}"
                    });
                }
            }
            catch (Exception ex)
            {
                TuiLogger.Log($"Error getting labels chunk: {ex.Message}");
                return JsonSerializer.Serialize(new
                {
                    Status = "Error",
                    Message = $"Error getting labels chunk: {ex.Message}"
                });
            }
        }

        /// <summary>
        /// Handles the command to get transfer status
        /// </summary>
        private async Task<string> HandleGetTransferStatusCommand(JsonElement commandObj)
        {
            try
            {
                // Get all transfer statuses
                var transfers = _transferService.GetTransfersStatus();

                return JsonSerializer.Serialize(new
                {
                    Status = "OK",
                    Transfers = transfers
                });
            }
            catch (Exception ex)
            {
                TuiLogger.Log($"Error getting transfer status: {ex.Message}");
                return JsonSerializer.Serialize(new
                {
                    Status = "Error",
                    Message = $"Error getting transfer status: {ex.Message}"
                });
            }
        }

        /// <summary>
        /// Handles the command to cleanup a transfer
        /// </summary>
        private async Task<string> HandleCleanupTransferCommand(JsonElement commandObj)
        {
            try
            {
                // Get the transfer ID
                if (!commandObj.TryGetProperty("TransferId", out JsonElement transferIdElement))
                {
                    return JsonSerializer.Serialize(new
                    {
                        Status = "Error",
                        Message = "Missing TransferId parameter"
                    });
                }

                string transferId = transferIdElement.GetString();

                // Cleanup the transfer
                _transferService.CleanupTransfer(transferId);

                return JsonSerializer.Serialize(new
                {
                    Status = "OK",
                    Message = "Transfer cleaned up successfully"
                });
            }
            catch (Exception ex)
            {
                TuiLogger.Log($"Error cleaning up transfer: {ex.Message}");
                return JsonSerializer.Serialize(new
                {
                    Status = "Error",
                    Message = $"Error cleaning up transfer: {ex.Message}"
                });
            }
        }
    }
}