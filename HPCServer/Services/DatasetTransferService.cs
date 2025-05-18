using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ParallelComputingServer.Data;
using ParallelComputingServer.Models;
using ParallelComputingServer.UI;

namespace ParallelComputingServer.Services
{
    public class DatasetTransferService:IDisposable
    {
        private readonly string _datasetStoragePath;
        private readonly ComputeService _computeService;
        private Dictionary<string, DatasetTransferState> _activeTransfers = new Dictionary<string, DatasetTransferState>();
        private Dictionary<string, DatasetMetadata> _datasets = new Dictionary<string, DatasetMetadata>();

        // Class to track transfer state
        private class DatasetTransferState
        {
            public string TransferId { get; set; }
            public string DatasetId { get; set; }
            public DateTime StartTime { get; set; }
            public DateTime LastUpdateTime { get; set; }
            public int TotalChunks { get; set; }
            public int ReceivedChunks { get; set; }
            public DatasetMetadata Metadata { get; set; }
            public bool IsComplete => ReceivedChunks >= TotalChunks;
            public TransferStatus Status { get; set; }
            public string FilePath { get; set; }
            public string LabelsFilePath { get; set; }
            public ServerChunkedVolume Volume { get; set; }
            public ServerChunkedLabelVolume Labels { get; set; }
            public object ProcessingResult { get; set; }

            public float ProgressPercentage => TotalChunks > 0 ? (float)ReceivedChunks / TotalChunks * 100 : 0;
        }

        public enum TransferStatus
        {
            Initializing,
            Receiving,
            Processing,
            Sending,
            Completed,
            Failed
        }

        public DatasetTransferService(string datasetStoragePath, ComputeService computeService)
        {
            _datasetStoragePath = datasetStoragePath;
            _computeService = computeService;

            // Create the storage directory if it doesn't exist
            if (!Directory.Exists(_datasetStoragePath))
            {
                Directory.CreateDirectory(_datasetStoragePath);
            }

            Console.WriteLine($"Dataset transfer service initialized with storage path: {_datasetStoragePath}");
        }

        /// <summary>
        /// Initializes a new dataset transfer
        /// </summary>
        public async Task<string> InitializeTransferAsync(DatasetMetadata metadata)
        {
            string transferId = Guid.NewGuid().ToString();
            string datasetId = Guid.NewGuid().ToString();

            // Create directory for this dataset
            string datasetDir = Path.Combine(_datasetStoragePath, datasetId);
            Directory.CreateDirectory(datasetDir);

            // Setup file paths
            string volumeFilePath = Path.Combine(datasetDir, "volume.bin");
            string labelsFilePath = Path.Combine(datasetDir, "labels.bin");

            // Create transfer state
            var transferState = new DatasetTransferState
            {
                TransferId = transferId,
                DatasetId = datasetId,
                StartTime = DateTime.Now,
                LastUpdateTime = DateTime.Now,
                TotalChunks = metadata.VolumeChunks,
                ReceivedChunks = 0,
                Metadata = metadata,
                Status = TransferStatus.Initializing,
                FilePath = volumeFilePath,
                LabelsFilePath = labelsFilePath
            };

            // Save metadata
            string metadataPath = Path.Combine(datasetDir, "metadata.json");
            string json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(metadataPath, json);

            // Initialize volume and labels
            try
            {
                // Create volume file
                transferState.Volume = new ServerChunkedVolume(
                    metadata.Width,
                    metadata.Height,
                    metadata.Depth,
                    metadata.ChunkDim,
                    volumeFilePath);

                // Create labels file
                transferState.Labels = new ServerChunkedLabelVolume(
                    metadata.Width,
                    metadata.Height,
                    metadata.Depth,
                    metadata.ChunkDim,
                    labelsFilePath);

                // Store the transfer state
                lock (_activeTransfers)
                {
                    _activeTransfers[transferId] = transferState;
                }

                // Store dataset reference
                lock (_datasets)
                {
                    _datasets[datasetId] = metadata;
                }

                // Update status
                transferState.Status = TransferStatus.Receiving;
                TuiLogger.Log($"Dataset transfer initialized: {transferId} for dataset {datasetId}");

                return transferId;
            }
            catch (Exception ex)
            {
                TuiLogger.Log($"Error initializing dataset transfer: {ex.Message}");

                // Clean up
                try
                {
                    transferState.Volume?.Dispose();
                    transferState.Labels?.Dispose();

                    if (Directory.Exists(datasetDir))
                    {
                        Directory.Delete(datasetDir, true);
                    }
                }
                catch
                {
                    // Ignore cleanup errors
                }

                throw;
            }
        }

        /// <summary>
        /// Processes a chunk of data for the volume
        /// </summary>
        public async Task<bool> ProcessVolumeChunkAsync(string transferId, int chunkIndex, byte[] compressedData)
        {
            if (!_activeTransfers.TryGetValue(transferId, out var transferState))
            {
                TuiLogger.Log($"Transfer not found: {transferId}");
                return false;
            }

            try
            {
                // Decompress the chunk data
                byte[] chunkData;
                using (var compressedStream = new MemoryStream(compressedData))
                using (var decompressionStream = new GZipStream(compressedStream, CompressionMode.Decompress))
                using (var decompressedStream = new MemoryStream())
                {
                    await decompressionStream.CopyToAsync(decompressedStream);
                    chunkData = decompressedStream.ToArray();
                }

                // Verify chunk size
                int expectedSize = transferState.Metadata.ChunkDim * transferState.Metadata.ChunkDim * transferState.Metadata.ChunkDim;
                if (chunkData.Length != expectedSize)
                {
                    TuiLogger.Log($"Chunk size mismatch. Expected {expectedSize}, got {chunkData.Length}");
                    return false;
                }

                // Set the chunk data
                transferState.Volume.SetChunkData(chunkIndex, chunkData);

                // Update counters
                lock (_activeTransfers)
                {
                    transferState.ReceivedChunks++;
                    transferState.LastUpdateTime = DateTime.Now;

                    // Log progress periodically
                    if (transferState.ReceivedChunks % 10 == 0 || transferState.ReceivedChunks == transferState.TotalChunks)
                    {
                        TuiLogger.Log($"Transfer {transferId}: {transferState.ReceivedChunks}/{transferState.TotalChunks} chunks " +
                                      $"({transferState.ProgressPercentage:F1}%)");
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                TuiLogger.Log($"Error processing volume chunk {chunkIndex}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Processes a chunk of data for the labels
        /// </summary>
        public async Task<bool> ProcessLabelsChunkAsync(string transferId, int chunkIndex, byte[] compressedData)
        {
            if (!_activeTransfers.TryGetValue(transferId, out var transferState))
            {
                TuiLogger.Log($"Transfer not found: {transferId}");
                return false;
            }

            try
            {
                // Decompress the chunk data
                byte[] chunkData;
                using (var compressedStream = new MemoryStream(compressedData))
                using (var decompressionStream = new GZipStream(compressedStream, CompressionMode.Decompress))
                using (var decompressedStream = new MemoryStream())
                {
                    await decompressionStream.CopyToAsync(decompressedStream);
                    chunkData = decompressedStream.ToArray();
                }

                // Verify chunk size
                int expectedSize = transferState.Metadata.ChunkDim * transferState.Metadata.ChunkDim * transferState.Metadata.ChunkDim;
                if (chunkData.Length != expectedSize)
                {
                    TuiLogger.Log($"Label chunk size mismatch. Expected {expectedSize}, got {chunkData.Length}");
                    return false;
                }

                // Set the chunk data
                transferState.Labels.SetChunkData(chunkIndex, chunkData);

                return true;
            }
            catch (Exception ex)
            {
                TuiLogger.Log($"Error processing label chunk {chunkIndex}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Finalizes the dataset transfer
        /// </summary>
        public async Task<bool> CompleteTransferAsync(string transferId)
        {
            if (!_activeTransfers.TryGetValue(transferId, out var transferState))
            {
                TuiLogger.Log($"Transfer not found: {transferId}");
                return false;
            }

            try
            {
                // Ensure all data is flushed to disk
                transferState.Volume.Flush();
                transferState.Labels.Flush();

                // Update status
                transferState.Status = TransferStatus.Completed;
                TuiLogger.Log($"Transfer {transferId} completed successfully!");

                // Process the dataset (in a real implementation, this would typically be queued for processing)
                // For now, we'll just mark it as completed
                return true;
            }
            catch (Exception ex)
            {
                TuiLogger.Log($"Error completing transfer: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Processes a dataset (to be implemented based on node type)
        /// </summary>
        public async Task<object> ProcessDatasetAsync(string datasetId, string nodeType, Dictionary<string, object> parameters)
        {
            if (!_datasets.TryGetValue(datasetId, out var metadata))
            {
                TuiLogger.Log($"Dataset not found: {datasetId}");
                return null;
            }

            // Find the transfer state for this dataset
            var transferState = _activeTransfers.Values.FirstOrDefault(t => t.DatasetId == datasetId);
            if (transferState == null)
            {
                TuiLogger.Log($"No active transfer found for dataset: {datasetId}");
                return null;
            }

            try
            {
                // Update status
                transferState.Status = TransferStatus.Processing;
                TuiLogger.Log($"Processing dataset {datasetId} with node type {nodeType}");

                // TODO: Implement actual processing based on node type
                // This would involve running the appropriate algorithm on the volume/labels

                // For now, we'll simulate processing with a delay
                await Task.Delay(2000);

                // Store the result (for now, just a dummy message)
                transferState.ProcessingResult = new
                {
                    Success = true,
                    Message = $"Processed dataset with {nodeType}",
                    ProcessedAt = DateTime.Now
                };

                // Update status
                transferState.Status = TransferStatus.Completed;
                TuiLogger.Log($"Dataset {datasetId} processed successfully");

                return transferState.ProcessingResult;
            }
            catch (Exception ex)
            {
                TuiLogger.Log($"Error processing dataset: {ex.Message}");
                transferState.Status = TransferStatus.Failed;
                return null;
            }
        }

        /// <summary>
        /// Gets a chunk of processed volume data
        /// </summary>
        public async Task<byte[]> GetProcessedVolumeChunkAsync(string datasetId, int chunkIndex)
        {
            var transferState = _activeTransfers.Values.FirstOrDefault(t => t.DatasetId == datasetId);
            if (transferState == null || transferState.Volume == null)
            {
                TuiLogger.Log($"Dataset or volume not found: {datasetId}");
                return null;
            }

            try
            {
                // Get the chunk data
                byte[] chunkData = transferState.Volume.GetChunkBytes(chunkIndex);

                // Compress the data
                using (var compressedStream = new MemoryStream())
                {
                    using (var compressionStream = new GZipStream(compressedStream, CompressionMode.Compress))
                    {
                        await compressionStream.WriteAsync(chunkData, 0, chunkData.Length);
                    }

                    return compressedStream.ToArray();
                }
            }
            catch (Exception ex)
            {
                TuiLogger.Log($"Error getting processed volume chunk: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Gets a chunk of processed label data
        /// </summary>
        public async Task<byte[]> GetProcessedLabelsChunkAsync(string datasetId, int chunkIndex)
        {
            var transferState = _activeTransfers.Values.FirstOrDefault(t => t.DatasetId == datasetId);
            if (transferState == null || transferState.Labels == null)
            {
                TuiLogger.Log($"Dataset or labels not found: {datasetId}");
                return null;
            }

            try
            {
                // Get the chunk data
                byte[] chunkData = transferState.Labels.GetChunkBytes(chunkIndex);

                // Compress the data
                using (var compressedStream = new MemoryStream())
                {
                    using (var compressionStream = new GZipStream(compressedStream, CompressionMode.Compress))
                    {
                        await compressionStream.WriteAsync(chunkData, 0, chunkData.Length);
                    }

                    return compressedStream.ToArray();
                }
            }
            catch (Exception ex)
            {
                TuiLogger.Log($"Error getting processed labels chunk: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Returns information about all active transfers
        /// </summary>
        public Dictionary<string, TransferStatusInfo> GetTransfersStatus()
        {
            var result = new Dictionary<string, TransferStatusInfo>();

            lock (_activeTransfers)
            {
                foreach (var transfer in _activeTransfers)
                {
                    result[transfer.Key] = new TransferStatusInfo
                    {
                        TransferId = transfer.Value.TransferId,
                        DatasetId = transfer.Value.DatasetId,
                        Status = transfer.Value.Status.ToString(),
                        ProgressPercentage = transfer.Value.ProgressPercentage,
                        StartTime = transfer.Value.StartTime,
                        LastUpdateTime = transfer.Value.LastUpdateTime,
                        TotalChunks = transfer.Value.TotalChunks,
                        ReceivedChunks = transfer.Value.ReceivedChunks
                    };
                }
            }

            return result;
        }

        /// <summary>
        /// Cleans up a completed transfer
        /// </summary>
        public void CleanupTransfer(string transferId)
        {
            if (!_activeTransfers.TryGetValue(transferId, out var transferState))
            {
                TuiLogger.Log($"Transfer not found for cleanup: {transferId}");
                return;
            }

            try
            {
                // Dispose resources
                transferState.Volume?.Dispose();
                transferState.Labels?.Dispose();

                // Remove from active transfers
                lock (_activeTransfers)
                {
                    _activeTransfers.Remove(transferId);
                }

                TuiLogger.Log($"Transfer {transferId} cleaned up successfully");
            }
            catch (Exception ex)
            {
                TuiLogger.Log($"Error cleaning up transfer: {ex.Message}");
            }
        }
        // Fix for CS0103: '_storagePath' does not exist in the current context  
        // Replace '_storagePath' with '_datasetStoragePath' in the Dispose method.  

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_datasetStoragePath))
                {
                    foreach (var file in Directory.EnumerateFiles(_datasetStoragePath, "*", SearchOption.AllDirectories))
                        File.Delete(file);
                    foreach (var dir in Directory.EnumerateDirectories(_datasetStoragePath))
                        Directory.Delete(dir, true);
                }
            }
            catch (Exception ex)
            {
                // swallow or log—up to you  
                Console.Error.WriteLine($"Dataset cleanup failed: {ex}");
            }
        }
    }

    /// <summary>
    /// Status information about a transfer, for reporting to clients
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

    /// <summary>
    /// Dataset metadata class
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
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public Dictionary<string, string> Properties { get; set; } = new Dictionary<string, string>();
    }
}