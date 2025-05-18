using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CTS.NodeEditor
{
    /// <summary>
    /// Client for transferring datasets to and from the parallel computing server
    /// </summary>
    public class DatasetTransferClient
    {
        public readonly string _serverIp;
        public readonly int _serverPort;
        public readonly int _chunkSize;
        public readonly int _bufferSize = 8192; // 8KB buffer for network operations

        // Events for monitoring progress
        public event EventHandler<TransferProgressEventArgs> TransferProgressChanged;
        public event EventHandler<TransferStatusEventArgs> TransferStatusChanged;

        public DatasetTransferClient(string serverIp, int serverPort, int chunkSize = 256)
        {
            _serverIp = serverIp;
            _serverPort = serverPort;
            _chunkSize = chunkSize;
        }

        /// <summary>
        /// Initializes a new dataset transfer with the server
        /// </summary>
        public async Task<string> InitializeTransferAsync(DatasetMetadata metadata)
        {
            try
            {
                // Create command to initialize transfer
                var command = new Dictionary<string, object>
                {
                    { "Type", "DATASET_TRANSFER" },
                    { "DatasetCommand", "INITIALIZE_TRANSFER" },
                    { "Metadata", metadata }
                };

                var response = await SendCommandAsync(command);

                if (response.TryGetProperty("Status", out var statusElement) &&
                    statusElement.GetString() == "OK" &&
                    response.TryGetProperty("TransferId", out var transferIdElement))
                {
                    string transferId = transferIdElement.GetString();
                    OnTransferStatusChanged(new TransferStatusEventArgs(transferId, "Initialized", 0));
                    Logger.Log($"[DatasetTransferClient] Transfer initialized: {transferId}");
                    return transferId;
                }
                else
                {
                    string message = "Unknown error";
                    if (response.TryGetProperty("Message", out var messageElement))
                    {
                        message = messageElement.GetString();
                    }
                    Logger.Log($"[DatasetTransferClient] Failed to initialize transfer: {message}");
                    throw new Exception($"Failed to initialize transfer: {message}");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[DatasetTransferClient] Error initializing transfer: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Uploads a volume dataset to the server
        /// </summary>
        public async Task<bool> UploadVolumeAsync(
            string transferId,
            IGrayscaleVolumeData volume,
            CancellationToken cancellationToken = default)
        {
            try
            {
                int width = volume.Width;
                int height = volume.Height;
                int depth = volume.Depth;

                // Calculate chunks
                int chunkDim = _chunkSize;
                int chunkCountX = (width + chunkDim - 1) / chunkDim;
                int chunkCountY = (height + chunkDim - 1) / chunkDim;
                int chunkCountZ = (depth + chunkDim - 1) / chunkDim;
                int totalChunks = chunkCountX * chunkCountY * chunkCountZ;

                Logger.Log($"[DatasetTransferClient] Uploading volume: {width}x{height}x{depth}, " +
                          $"chunks: {chunkCountX}x{chunkCountY}x{chunkCountZ} (total: {totalChunks})");

                int uploadedChunks = 0;
                int chunkIndex = 0;

                // Upload chunks
                for (int cz = 0; cz < chunkCountZ; cz++)
                {
                    for (int cy = 0; cy < chunkCountY; cy++)
                    {
                        for (int cx = 0; cx < chunkCountX; cx++)
                        {
                            // Check cancellation
                            if (cancellationToken.IsCancellationRequested)
                            {
                                Logger.Log($"[DatasetTransferClient] Upload cancelled");
                                OnTransferStatusChanged(new TransferStatusEventArgs(transferId, "Cancelled", uploadedChunks * 100f / totalChunks));
                                return false;
                            }

                            // Extract chunk data
                            byte[] chunkData = ExtractChunkData(volume, cx, cy, cz, chunkDim, width, height, depth);

                            // Compress chunk data
                            byte[] compressedData = await CompressDataAsync(chunkData);

                            // Upload chunk
                            bool success = await UploadChunkAsync(transferId, chunkIndex, compressedData);
                            if (!success)
                            {
                                Logger.Log($"[DatasetTransferClient] Failed to upload chunk {chunkIndex}");
                                OnTransferStatusChanged(new TransferStatusEventArgs(transferId, "Failed", uploadedChunks * 100f / totalChunks));
                                return false;
                            }

                            // Update progress
                            uploadedChunks++;
                            float progress = uploadedChunks * 100f / totalChunks;
                            OnTransferProgressChanged(new TransferProgressEventArgs(transferId, "Uploading", progress, uploadedChunks, totalChunks));

                            chunkIndex++;
                        }
                    }
                }

                // Complete transfer
                bool completed = await CompleteTransferAsync(transferId);
                if (completed)
                {
                    OnTransferStatusChanged(new TransferStatusEventArgs(transferId, "Completed", 100));
                    Logger.Log($"[DatasetTransferClient] Upload completed for transfer {transferId}");
                    return true;
                }
                else
                {
                    OnTransferStatusChanged(new TransferStatusEventArgs(transferId, "Failed", uploadedChunks * 100f / totalChunks));
                    Logger.Log($"[DatasetTransferClient] Failed to complete transfer {transferId}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[DatasetTransferClient] Error uploading volume: {ex.Message}");
                OnTransferStatusChanged(new TransferStatusEventArgs(transferId, "Error", 0));
                throw;
            }
        }

        /// <summary>
        /// Uploads label data to the server
        /// </summary>
        public async Task<bool> UploadLabelsAsync(
            string transferId,
            ILabelVolumeData labels,
            CancellationToken cancellationToken = default)
        {
            try
            {
                int width = labels.Width;
                int height = labels.Height;
                int depth = labels.Depth;

                // Calculate chunks
                int chunkDim = _chunkSize;
                int chunkCountX = (width + chunkDim - 1) / chunkDim;
                int chunkCountY = (height + chunkDim - 1) / chunkDim;
                int chunkCountZ = (depth + chunkDim - 1) / chunkDim;
                int totalChunks = chunkCountX * chunkCountY * chunkCountZ;

                Logger.Log($"[DatasetTransferClient] Uploading labels: {width}x{height}x{depth}, " +
                          $"chunks: {chunkCountX}x{chunkCountY}x{chunkCountZ} (total: {totalChunks})");

                int uploadedChunks = 0;
                int chunkIndex = 0;

                // Upload chunks
                for (int cz = 0; cz < chunkCountZ; cz++)
                {
                    for (int cy = 0; cy < chunkCountY; cy++)
                    {
                        for (int cx = 0; cx < chunkCountX; cx++)
                        {
                            // Check cancellation
                            if (cancellationToken.IsCancellationRequested)
                            {
                                Logger.Log($"[DatasetTransferClient] Upload cancelled");
                                OnTransferStatusChanged(new TransferStatusEventArgs(transferId, "Cancelled", uploadedChunks * 100f / totalChunks));
                                return false;
                            }

                            // Extract chunk data
                            byte[] chunkData = ExtractLabelChunkData(labels, cx, cy, cz, chunkDim, width, height, depth);

                            // Compress chunk data
                            byte[] compressedData = await CompressDataAsync(chunkData);

                            // Upload chunk
                            bool success = await UploadLabelChunkAsync(transferId, chunkIndex, compressedData);
                            if (!success)
                            {
                                Logger.Log($"[DatasetTransferClient] Failed to upload label chunk {chunkIndex}");
                                OnTransferStatusChanged(new TransferStatusEventArgs(transferId, "Failed", uploadedChunks * 100f / totalChunks));
                                return false;
                            }

                            // Update progress
                            uploadedChunks++;
                            float progress = uploadedChunks * 100f / totalChunks;
                            OnTransferProgressChanged(new TransferProgressEventArgs(transferId, "Uploading Labels", progress, uploadedChunks, totalChunks));

                            chunkIndex++;
                        }
                    }
                }

                Logger.Log($"[DatasetTransferClient] Label upload completed for transfer {transferId}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[DatasetTransferClient] Error uploading labels: {ex.Message}");
                OnTransferStatusChanged(new TransferStatusEventArgs(transferId, "Error", 0));
                throw;
            }
        }

        /// <summary>
        /// Requests the server to process the dataset with a specific node type
        /// </summary>
        public async Task<bool> ProcessDatasetAsync(string datasetId, string nodeType, Dictionary<string, object> parameters = null)
        {
            try
            {
                // Create command to process dataset
                var command = new Dictionary<string, object>
                {
                    { "Type", "DATASET_TRANSFER" },
                    { "DatasetCommand", "PROCESS_DATASET" },
                    { "DatasetId", datasetId },
                    { "NodeType", nodeType }
                };

                if (parameters != null)
                {
                    command["Parameters"] = parameters;
                }

                var response = await SendCommandAsync(command);

                if (response.TryGetProperty("Status", out var statusElement) &&
                    statusElement.GetString() == "OK")
                {
                    Logger.Log($"[DatasetTransferClient] Processing request sent for dataset {datasetId}");
                    OnTransferStatusChanged(new TransferStatusEventArgs(datasetId, "Processing", 0));
                    return true;
                }
                else
                {
                    string message = "Unknown error";
                    if (response.TryGetProperty("Message", out var messageElement))
                    {
                        message = messageElement.GetString();
                    }
                    Logger.Log($"[DatasetTransferClient] Failed to process dataset: {message}");
                    throw new Exception($"Failed to process dataset: {message}");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[DatasetTransferClient] Error processing dataset: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Downloads processed volume data from the server
        /// </summary>
        public async Task<ChunkedVolume> DownloadProcessedVolumeAsync(
            string datasetId,
            int width, int height, int depth,
            CancellationToken cancellationToken = default)
        {
            try
            {
                // Calculate chunks
                int chunkDim = _chunkSize;
                int chunkCountX = (width + chunkDim - 1) / chunkDim;
                int chunkCountY = (height + chunkDim - 1) / chunkDim;
                int chunkCountZ = (depth + chunkDim - 1) / chunkDim;
                int totalChunks = chunkCountX * chunkCountY * chunkCountZ;

                Logger.Log($"[DatasetTransferClient] Downloading processed volume: {width}x{height}x{depth}, " +
                          $"chunks: {chunkCountX}x{chunkCountY}x{chunkCountZ} (total: {totalChunks})");

                // Create new volume
                ChunkedVolume volume = new ChunkedVolume(width, height, depth, chunkDim);

                int downloadedChunks = 0;

                // Download chunks
                for (int chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
                {
                    // Check cancellation
                    if (cancellationToken.IsCancellationRequested)
                    {
                        Logger.Log($"[DatasetTransferClient] Download cancelled");
                        volume.Dispose();
                        return null;
                    }

                    // Get chunk from server
                    byte[] compressedData = await GetVolumeChunkAsync(datasetId, chunkIndex);
                    if (compressedData == null)
                    {
                        Logger.Log($"[DatasetTransferClient] Failed to download chunk {chunkIndex}");
                        volume.Dispose();
                        return null;
                    }

                    // Decompress chunk data
                    byte[] chunkData = await DecompressDataAsync(compressedData);

                    // Get chunk coordinates
                    int cx = chunkIndex % chunkCountX;
                    int cy = (chunkIndex / chunkCountX) % chunkCountY;
                    int cz = chunkIndex / (chunkCountX * chunkCountY);

                    // Fill volume with chunk data
                    FillVolumeChunk(volume, cx, cy, cz, chunkDim, chunkData);

                    // Update progress
                    downloadedChunks++;
                    float progress = downloadedChunks * 100f / totalChunks;
                    OnTransferProgressChanged(new TransferProgressEventArgs(datasetId, "Downloading", progress, downloadedChunks, totalChunks));
                }

                Logger.Log($"[DatasetTransferClient] Download completed for dataset {datasetId}");
                OnTransferStatusChanged(new TransferStatusEventArgs(datasetId, "Downloaded", 100));
                return volume;
            }
            catch (Exception ex)
            {
                Logger.Log($"[DatasetTransferClient] Error downloading processed volume: {ex.Message}");
                OnTransferStatusChanged(new TransferStatusEventArgs(datasetId, "Error", 0));
                throw;
            }
        }

        /// <summary>
        /// Downloads processed label data from the server
        /// </summary>
        public async Task<ChunkedLabelVolume> DownloadProcessedLabelsAsync(
            string datasetId,
            int width, int height, int depth,
            CancellationToken cancellationToken = default)
        {
            try
            {
                // Calculate chunks
                int chunkDim = _chunkSize;
                int chunkCountX = (width + chunkDim - 1) / chunkDim;
                int chunkCountY = (height + chunkDim - 1) / chunkDim;
                int chunkCountZ = (depth + chunkDim - 1) / chunkDim;
                int totalChunks = chunkCountX * chunkCountY * chunkCountZ;

                Logger.Log($"[DatasetTransferClient] Downloading processed labels: {width}x{height}x{depth}, " +
                          $"chunks: {chunkCountX}x{chunkCountY}x{chunkCountZ} (total: {totalChunks})");

                // Create new label volume
                ChunkedLabelVolume labels = new ChunkedLabelVolume(width, height, depth, chunkDim, false);

                int downloadedChunks = 0;

                // Download chunks
                for (int chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
                {
                    // Check cancellation
                    if (cancellationToken.IsCancellationRequested)
                    {
                        Logger.Log($"[DatasetTransferClient] Download cancelled");
                        labels.Dispose();
                        return null;
                    }

                    // Get chunk from server
                    byte[] compressedData = await GetLabelsChunkAsync(datasetId, chunkIndex);
                    if (compressedData == null)
                    {
                        Logger.Log($"[DatasetTransferClient] Failed to download chunk {chunkIndex}");
                        labels.Dispose();
                        return null;
                    }

                    // Decompress chunk data
                    byte[] chunkData = await DecompressDataAsync(compressedData);

                    // Extract chunk coordinates from index
                    int cx = chunkIndex % chunkCountX;
                    int cy = (chunkIndex / chunkCountX) % chunkCountY;
                    int cz = chunkIndex / (chunkCountX * chunkCountY);

                    // Fill labels with chunk data (implementation depends on ChunkedLabelVolume structure)
                    FillLabelsChunk(labels, cx, cy, cz, chunkDim, chunkData);

                    // Update progress
                    downloadedChunks++;
                    float progress = downloadedChunks * 100f / totalChunks;
                    OnTransferProgressChanged(new TransferProgressEventArgs(datasetId, "Downloading Labels", progress, downloadedChunks, totalChunks));
                }

                Logger.Log($"[DatasetTransferClient] Labels download completed for dataset {datasetId}");
                return labels;
            }
            catch (Exception ex)
            {
                Logger.Log($"[DatasetTransferClient] Error downloading processed labels: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Gets the status of a transfer from the server
        /// </summary>
        public async Task<TransferStatusInfo> GetTransferStatusAsync(string transferId)
        {
            try
            {
                var command = new Dictionary<string, object>
                {
                    { "Type", "DATASET_TRANSFER" },
                    { "DatasetCommand", "GET_TRANSFER_STATUS" }
                };

                var response = await SendCommandAsync(command);

                if (response.TryGetProperty("Status", out var statusElement) &&
                    statusElement.GetString() == "OK" &&
                    response.TryGetProperty("Transfers", out var transfersElement))
                {
                    // Get all transfers
                    var transfers = transfersElement.EnumerateObject();

                    // Find the requested transfer
                    foreach (var transfer in transfers)
                    {
                        if (transfer.Name == transferId)
                        {
                            var transferInfo = JsonSerializer.Deserialize<TransferStatusInfo>(transfer.Value.GetRawText());
                            return transferInfo;
                        }
                    }

                    // If we get here, the transfer was not found
                    Logger.Log($"[DatasetTransferClient] Transfer {transferId} not found");
                    return null;
                }
                else
                {
                    string message = "Unknown error";
                    if (response.TryGetProperty("Message", out var messageElement))
                    {
                        message = messageElement.GetString();
                    }
                    Logger.Log($"[DatasetTransferClient] Failed to get transfer status: {message}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[DatasetTransferClient] Error getting transfer status: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Cleans up a completed transfer on the server
        /// </summary>
        public async Task<bool> CleanupTransferAsync(string transferId)
        {
            try
            {
                var command = new Dictionary<string, object>
                {
                    { "Type", "DATASET_TRANSFER" },
                    { "DatasetCommand", "CLEANUP_TRANSFER" },
                    { "TransferId", transferId }
                };

                var response = await SendCommandAsync(command);

                if (response.TryGetProperty("Status", out var statusElement) &&
                    statusElement.GetString() == "OK")
                {
                    Logger.Log($"[DatasetTransferClient] Transfer {transferId} cleaned up successfully");
                    return true;
                }
                else
                {
                    string message = "Unknown error";
                    if (response.TryGetProperty("Message", out var messageElement))
                    {
                        message = messageElement.GetString();
                    }
                    Logger.Log($"[DatasetTransferClient] Failed to cleanup transfer: {message}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[DatasetTransferClient] Error cleaning up transfer: {ex.Message}");
                return false;
            }
        }

        #region public Helper Methods

        /// <summary>
        /// Extracts chunk data from a volume
        /// </summary>
        public byte[] ExtractChunkData(IGrayscaleVolumeData volume, int cx, int cy, int cz, int chunkDim, int width, int height, int depth)
        {
            int chunkSize = chunkDim * chunkDim * chunkDim;
            byte[] chunkData = new byte[chunkSize];

            // Calculate start coordinates
            int startX = cx * chunkDim;
            int startY = cy * chunkDim;
            int startZ = cz * chunkDim;

            // Iterate through chunk positions
            int index = 0;
            for (int z = 0; z < chunkDim; z++)
            {
                int globalZ = startZ + z;
                if (globalZ >= depth) break;

                for (int y = 0; y < chunkDim; y++)
                {
                    int globalY = startY + y;
                    if (globalY >= height) break;

                    for (int x = 0; x < chunkDim; x++)
                    {
                        int globalX = startX + x;
                        if (globalX >= width) break;

                        // Get voxel value from volume
                        chunkData[index++] = volume[globalX, globalY, globalZ];
                    }

                    // Pad remaining X positions if needed
                    while (index % chunkDim != 0 && index < chunkSize)
                    {
                        chunkData[index++] = 0;
                    }
                }

                // Pad remaining Y positions if needed
                while (index % (chunkDim * chunkDim) != 0 && index < chunkSize)
                {
                    chunkData[index++] = 0;
                }
            }

            // Pad remaining Z positions if needed
            while (index < chunkSize)
            {
                chunkData[index++] = 0;
            }

            return chunkData;
        }

        /// <summary>
        /// Extracts chunk data from a label volume
        /// </summary>
        public byte[] ExtractLabelChunkData(ILabelVolumeData labels, int cx, int cy, int cz, int chunkDim, int width, int height, int depth)
        {
            int chunkSize = chunkDim * chunkDim * chunkDim;
            byte[] chunkData = new byte[chunkSize];

            // Calculate start coordinates
            int startX = cx * chunkDim;
            int startY = cy * chunkDim;
            int startZ = cz * chunkDim;

            // Iterate through chunk positions
            int index = 0;
            for (int z = 0; z < chunkDim; z++)
            {
                int globalZ = startZ + z;
                if (globalZ >= depth) break;

                for (int y = 0; y < chunkDim; y++)
                {
                    int globalY = startY + y;
                    if (globalY >= height) break;

                    for (int x = 0; x < chunkDim; x++)
                    {
                        int globalX = startX + x;
                        if (globalX >= width) break;

                        // Get voxel value from labels
                        chunkData[index++] = labels[globalX, globalY, globalZ];
                    }

                    // Pad remaining X positions if needed
                    while (index % chunkDim != 0 && index < chunkSize)
                    {
                        chunkData[index++] = 0;
                    }
                }

                // Pad remaining Y positions if needed
                while (index % (chunkDim * chunkDim) != 0 && index < chunkSize)
                {
                    chunkData[index++] = 0;
                }
            }

            // Pad remaining Z positions if needed
            while (index < chunkSize)
            {
                chunkData[index++] = 0;
            }

            return chunkData;
        }

        /// <summary>
        /// Fills a volume chunk with data
        /// </summary>
        public void FillVolumeChunk(ChunkedVolume volume, int cx, int cy, int cz, int chunkDim, byte[] chunkData)
        {
            int chunkIndex = volume.GetChunkIndex(cx, cy, cz);
            volume.Chunks[chunkIndex] = chunkData;
        }

        /// <summary>
        /// Fills a labels chunk with data
        /// </summary>
        public void FillLabelsChunk(ChunkedLabelVolume labels, int cx, int cy, int cz, int chunkDim, byte[] chunkData)
        {
            // This implementation depends on the structure of your ChunkedLabelVolume class
            // For this example, we'll assume the class has a method to set chunk data directly
            int chunkIndex = labels.GetChunkIndex(cx, cy, cz);

            // Access chunk data directly if possible
            var chunks = typeof(ChunkedLabelVolume).GetField("_chunks", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (chunks != null)
            {
                var chunksArray = chunks.GetValue(labels) as byte[][];
                if (chunksArray != null)
                {
                    chunksArray[chunkIndex] = chunkData;
                    return;
                }
            }

            // Fallback: set data voxel by voxel
            int startX = cx * chunkDim;
            int startY = cy * chunkDim;
            int startZ = cz * chunkDim;

            int index = 0;
            for (int z = 0; z < chunkDim; z++)
            {
                int globalZ = startZ + z;
                if (globalZ >= labels.Depth) break;

                for (int y = 0; y < chunkDim; y++)
                {
                    int globalY = startY + y;
                    if (globalY >= labels.Height) break;

                    for (int x = 0; x < chunkDim; x++)
                    {
                        int globalX = startX + x;
                        if (globalX >= labels.Width) break;

                        labels[globalX, globalY, globalZ] = chunkData[index++];
                    }

                    // Skip padding in X
                    index += chunkDim - Math.Min(chunkDim, labels.Width - startX);
                }

                // Skip padding in Y
                index += chunkDim * (chunkDim - Math.Min(chunkDim, labels.Height - startY));
            }
        }

        /// <summary>
        /// Compresses data using GZip
        /// </summary>
        public async Task<byte[]> CompressDataAsync(byte[] data)
        {
            using (var outputStream = new MemoryStream())
            {
                using (var compressionStream = new GZipStream(outputStream, CompressionMode.Compress))
                {
                    await compressionStream.WriteAsync(data, 0, data.Length);
                }

                return outputStream.ToArray();
            }
        }

        /// <summary>
        /// Decompresses data using GZip
        /// </summary>
        public async Task<byte[]> DecompressDataAsync(byte[] compressedData)
        {
            using (var inputStream = new MemoryStream(compressedData))
            using (var decompressionStream = new GZipStream(inputStream, CompressionMode.Decompress))
            using (var outputStream = new MemoryStream())
            {
                await decompressionStream.CopyToAsync(outputStream);
                return outputStream.ToArray();
            }
        }

        /// <summary>
        /// Uploads a volume chunk to the server
        /// </summary>
        public async Task<bool> UploadChunkAsync(string transferId, int chunkIndex, byte[] compressedData)
        {
            var command = new Dictionary<string, object>
            {
                { "Type", "DATASET_TRANSFER" },
                { "DatasetCommand", "UPLOAD_VOLUME_CHUNK" },
                { "TransferId", transferId },
                { "ChunkIndex", chunkIndex },
                { "ChunkData", Convert.ToBase64String(compressedData) }
            };

            var response = await SendCommandAsync(command);

            return response.TryGetProperty("Status", out var statusElement) &&
                   statusElement.GetString() == "OK";
        }

        /// <summary>
        /// Uploads a label chunk to the server
        /// </summary>
        public async Task<bool> UploadLabelChunkAsync(string transferId, int chunkIndex, byte[] compressedData)
        {
            var command = new Dictionary<string, object>
            {
                { "Type", "DATASET_TRANSFER" },
                { "DatasetCommand", "UPLOAD_LABELS_CHUNK" },
                { "TransferId", transferId },
                { "ChunkIndex", chunkIndex },
                { "ChunkData", Convert.ToBase64String(compressedData) }
            };

            var response = await SendCommandAsync(command);

            return response.TryGetProperty("Status", out var statusElement) &&
                   statusElement.GetString() == "OK";
        }

        /// <summary>
        /// Completes a transfer on the server
        /// </summary>
        public async Task<bool> CompleteTransferAsync(string transferId)
        {
            var command = new Dictionary<string, object>
            {
                { "Type", "DATASET_TRANSFER" },
                { "DatasetCommand", "COMPLETE_TRANSFER" },
                { "TransferId", transferId }
            };

            var response = await SendCommandAsync(command);

            return response.TryGetProperty("Status", out var statusElement) &&
                   statusElement.GetString() == "OK";
        }

        /// <summary>
        /// Gets a volume chunk from the server
        /// </summary>
        public async Task<byte[]> GetVolumeChunkAsync(string datasetId, int chunkIndex)
        {
            var command = new Dictionary<string, object>
            {
                { "Type", "DATASET_TRANSFER" },
                { "DatasetCommand", "GET_VOLUME_CHUNK" },
                { "DatasetId", datasetId },
                { "ChunkIndex", chunkIndex }
            };

            var response = await SendCommandAsync(command);

            if (response.TryGetProperty("Status", out var statusElement) &&
                statusElement.GetString() == "OK" &&
                response.TryGetProperty("ChunkData", out var chunkDataElement))
            {
                string base64Data = chunkDataElement.GetString();
                return Convert.FromBase64String(base64Data);
            }
            else
            {
                string message = "Unknown error";
                if (response.TryGetProperty("Message", out var messageElement))
                {
                    message = messageElement.GetString();
                }
                Logger.Log($"[DatasetTransferClient] Failed to get volume chunk: {message}");
                return null;
            }
        }

        /// <summary>
        /// Gets a labels chunk from the server
        /// </summary>
        public async Task<byte[]> GetLabelsChunkAsync(string datasetId, int chunkIndex)
        {
            var command = new Dictionary<string, object>
            {
                { "Type", "DATASET_TRANSFER" },
                { "DatasetCommand", "GET_LABELS_CHUNK" },
                { "DatasetId", datasetId },
                { "ChunkIndex", chunkIndex }
            };

            var response = await SendCommandAsync(command);

            if (response.TryGetProperty("Status", out var statusElement) &&
                statusElement.GetString() == "OK" &&
                response.TryGetProperty("ChunkData", out var chunkDataElement))
            {
                string base64Data = chunkDataElement.GetString();
                return Convert.FromBase64String(base64Data);
            }
            else
            {
                string message = "Unknown error";
                if (response.TryGetProperty("Message", out var messageElement))
                {
                    message = messageElement.GetString();
                }
                Logger.Log($"[DatasetTransferClient] Failed to get labels chunk: {message}");
                return null;
            }
        }

        /// <summary>
        /// Sends a command to the server and gets the response
        /// </summary>
        public async Task<JsonElement> SendCommandAsync(Dictionary<string, object> command)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    // Connect to server with timeout
                    var connectTask = client.ConnectAsync(_serverIp, _serverPort);
                    if (await Task.WhenAny(connectTask, Task.Delay(10000)) != connectTask)
                    {
                        throw new TimeoutException("Connection to server timed out");
                    }

                    using (var stream = client.GetStream())
                    {
                        // Serialize command
                        string json = JsonSerializer.Serialize(command);
                        byte[] commandBytes = Encoding.UTF8.GetBytes(json);

                        // Send command
                        await stream.WriteAsync(commandBytes, 0, commandBytes.Length);

                        // Wait for response with timeout
                        var buffer = new byte[_bufferSize];
                        using (var ms = new MemoryStream())
                        {
                            int bytesRead;
                            var readTask = stream.ReadAsync(buffer, 0, buffer.Length);
                            if (await Task.WhenAny(readTask, Task.Delay(30000)) != readTask)
                            {
                                throw new TimeoutException("Reading response from server timed out");
                            }

                            bytesRead = await readTask;

                            // Check if we got a response
                            if (bytesRead == 0)
                            {
                                throw new Exception("Server closed connection without sending data");
                            }

                            // Write received data to memory stream
                            ms.Write(buffer, 0, bytesRead);

                            // For larger responses, we need to continue reading until we get all data
                            while (client.Available > 0)
                            {
                                readTask = stream.ReadAsync(buffer, 0, buffer.Length);
                                if (await Task.WhenAny(readTask, Task.Delay(5000)) != readTask)
                                {
                                    break; // Timeout on subsequent reads, use what we have
                                }

                                bytesRead = await readTask;
                                if (bytesRead == 0) break;

                                ms.Write(buffer, 0, bytesRead);
                            }

                            // Reset stream position and parse response
                            ms.Position = 0;

                            using (var sr = new StreamReader(ms, Encoding.UTF8))
                            {
                                string responseJson = await sr.ReadToEndAsync();
                                return JsonSerializer.Deserialize<JsonElement>(responseJson);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[DatasetTransferClient] Error sending command: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Raises the TransferProgressChanged event
        /// </summary>
        public void OnTransferProgressChanged(TransferProgressEventArgs args)
        {
            TransferProgressChanged?.Invoke(this, args);
        }

        /// <summary>
        /// Raises the TransferStatusChanged event
        /// </summary>
        public void OnTransferStatusChanged(TransferStatusEventArgs args)
        {
            TransferStatusChanged?.Invoke(this, args);
        }

        #endregion
    }

    /// <summary>
    /// Event arguments for transfer progress
    /// </summary>
    public class TransferProgressEventArgs : EventArgs
    {
        public string TransferId { get; }
        public string Status { get; }
        public float ProgressPercentage { get; }
        public int CompletedChunks { get; }
        public int TotalChunks { get; }

        public TransferProgressEventArgs(string transferId, string status, float progressPercentage, int completedChunks, int totalChunks)
        {
            TransferId = transferId;
            Status = status;
            ProgressPercentage = progressPercentage;
            CompletedChunks = completedChunks;
            TotalChunks = totalChunks;
        }
    }

    /// <summary>
    /// Event arguments for transfer status changes
    /// </summary>
    public class TransferStatusEventArgs : EventArgs
    {
        public string TransferId { get; }
        public string Status { get; }
        public float ProgressPercentage { get; }

        public TransferStatusEventArgs(string transferId, string status, float progressPercentage)
        {
            TransferId = transferId;
            Status = status;
            ProgressPercentage = progressPercentage;
        }
    }

    /// <summary>
    /// Dataset metadata class for communication with the server
    /// </summary>
    public class DatasetMetadata
    {
        public string Name { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int Depth { get; set; }
        public int ChunkDim { get; set; }
        public int VolumeChunks { get; set; }
        public int BitDepth { get; set; } = 8;
        public double PixelSize { get; set; } = 1e-6; // Default to 1 micron
        public Dictionary<string, string> Properties { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// Transfer status information from the server
    /// </summary>
    public class TransferStatusInfo
    {
        public string TransferId { get; set; }
        public string DatasetId { get; set; }
        public string Status { get; set; }
        public float ProgressPercentage { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime LastUpdateTime { get; set; }
        public int TotalChunks { get; set; }
        public int ReceivedChunks { get; set; }
    }
}