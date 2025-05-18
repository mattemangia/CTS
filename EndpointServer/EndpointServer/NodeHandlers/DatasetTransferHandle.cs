using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace ParallelComputingEndpoint
{
    public class DatasetTransferHandler
    {
        private readonly string _datasetStoragePath;
        private Dictionary<string, DatasetInfo> _datasets = new Dictionary<string, DatasetInfo>();
        private Dictionary<string, TransferState> _activeTransfers = new Dictionary<string, TransferState>();

        public DatasetTransferHandler(string storagePath)
        {
            _datasetStoragePath = storagePath;

            // Create storage directory if it doesn't exist
            if (!Directory.Exists(_datasetStoragePath))
            {
                Directory.CreateDirectory(_datasetStoragePath);
            }
        }

        public async Task<string> InitializeReceiveAsync(string transferId, DatasetMetadata metadata)
        {
            string datasetId = Guid.NewGuid().ToString();
            string datasetDir = Path.Combine(_datasetStoragePath, datasetId);

            try
            {
                Directory.CreateDirectory(datasetDir);

                // Save metadata
                string metadataPath = Path.Combine(datasetDir, "metadata.json");
                string json = System.Text.Json.JsonSerializer.Serialize(metadata);
                await File.WriteAllTextAsync(metadataPath, json);

                // Setup volume file paths
                string volumeFilePath = Path.Combine(datasetDir, "volume.bin");
                string labelsFilePath = Path.Combine(datasetDir, "labels.bin");

                // Create transfer state
                var transferState = new TransferState
                {
                    TransferId = transferId,
                    DatasetId = datasetId,
                    StartTime = DateTime.Now,
                    LastUpdateTime = DateTime.Now,
                    TotalChunks = metadata.TotalChunks,
                    ReceivedChunks = 0,
                    Metadata = metadata,
                    Status = TransferStatus.Receiving,
                    VolumeFilePath = volumeFilePath,
                    LabelsFilePath = labelsFilePath
                };

                _activeTransfers[transferId] = transferState;
                _datasets[datasetId] = new DatasetInfo
                {
                    Metadata = metadata,
                    VolumeFilePath = volumeFilePath,
                    LabelsFilePath = labelsFilePath
                };

                return datasetId;
            }
            catch (Exception)
            {
                // Clean up on failure
                if (Directory.Exists(datasetDir))
                {
                    try
                    {
                        Directory.Delete(datasetDir, true);
                    }
                    catch { }
                }

                throw;
            }
        }

        public async Task<bool> ProcessVolumeChunkAsync(string transferId, int chunkIndex, byte[] chunkData)
        {
            if (!_activeTransfers.TryGetValue(transferId, out var transfer))
                return false;

            try
            {
                // Calculate position in file for this chunk
                long position = (long)chunkIndex * transfer.Metadata.ChunkSize;

                // Write chunk data
                using (var fileStream = new FileStream(
                    transfer.VolumeFilePath,
                    FileMode.OpenOrCreate,
                    FileAccess.Write,
                    FileShare.None))
                {
                    fileStream.Seek(position, SeekOrigin.Begin);
                    await fileStream.WriteAsync(chunkData, 0, chunkData.Length);
                }

                // Update transfer state
                transfer.ReceivedChunks++;
                transfer.LastUpdateTime = DateTime.Now;

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public async Task<bool> ProcessLabelsChunkAsync(string transferId, int chunkIndex, byte[] chunkData)
        {
            if (!_activeTransfers.TryGetValue(transferId, out var transfer))
                return false;

            try
            {
                // Calculate position in file for this chunk
                long position = (long)chunkIndex * transfer.Metadata.ChunkSize;

                // Write chunk data
                using (var fileStream = new FileStream(
                    transfer.LabelsFilePath,
                    FileMode.OpenOrCreate,
                    FileAccess.Write,
                    FileShare.None))
                {
                    fileStream.Seek(position, SeekOrigin.Begin);
                    await fileStream.WriteAsync(chunkData, 0, chunkData.Length);
                }

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public async Task<bool> CompleteTransferAsync(string transferId)
        {
            if (!_activeTransfers.TryGetValue(transferId, out var transfer))
                return false;

            try
            {
                transfer.Status = TransferStatus.Completed;
                transfer.CompletionTime = DateTime.Now;

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public async Task<byte[]> GetVolumeChunkAsync(string datasetId, int chunkIndex)
        {
            if (!_datasets.TryGetValue(datasetId, out var dataset))
                return null;

            try
            {
                // Calculate position and size
                long position = (long)chunkIndex * dataset.Metadata.ChunkSize;
                int chunkSize = dataset.Metadata.ChunkSize;

                byte[] buffer = new byte[chunkSize];

                using (var fileStream = new FileStream(
                    dataset.VolumeFilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read))
                {
                    fileStream.Seek(position, SeekOrigin.Begin);
                    await fileStream.ReadAsync(buffer, 0, chunkSize);
                }

                return buffer;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public async Task<byte[]> GetLabelsChunkAsync(string datasetId, int chunkIndex)
        {
            if (!_datasets.TryGetValue(datasetId, out var dataset))
                return null;

            try
            {
                // Calculate position and size
                long position = (long)chunkIndex * dataset.Metadata.ChunkSize;
                int chunkSize = dataset.Metadata.ChunkSize;

                byte[] buffer = new byte[chunkSize];

                using (var fileStream = new FileStream(
                    dataset.LabelsFilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read))
                {
                    fileStream.Seek(position, SeekOrigin.Begin);
                    await fileStream.ReadAsync(buffer, 0, chunkSize);
                }

                return buffer;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public Dictionary<string, TransferStatusInfo> GetActiveTransfers()
        {
            var result = new Dictionary<string, TransferStatusInfo>();

            foreach (var transfer in _activeTransfers.Values)
            {
                result[transfer.TransferId] = new TransferStatusInfo
                {
                    TransferId = transfer.TransferId,
                    DatasetId = transfer.DatasetId,
                    Status = transfer.Status.ToString(),
                    ProgressPercentage = transfer.TotalChunks > 0 ?
                        (float)transfer.ReceivedChunks / transfer.TotalChunks * 100 : 0,
                    StartTime = transfer.StartTime,
                    LastUpdateTime = transfer.LastUpdateTime,
                    TotalChunks = transfer.TotalChunks,
                    ReceivedChunks = transfer.ReceivedChunks
                };
            }

            return result;
        }

        public void CleanupTransfer(string transferId)
        {
            if (_activeTransfers.TryGetValue(transferId, out var transfer))
            {
                _activeTransfers.Remove(transferId);

                // Don't delete the dataset, just release the transfer
            }
        }

        public void Cleanup()
        {
            // Release all active transfers
            _activeTransfers.Clear();
        }
    }

    public class DatasetInfo
    {
        public DatasetMetadata Metadata { get; set; }
        public string VolumeFilePath { get; set; }
        public string LabelsFilePath { get; set; }
    }

    public class TransferState
    {
        public string TransferId { get; set; }
        public string DatasetId { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime LastUpdateTime { get; set; }
        public DateTime? CompletionTime { get; set; }
        public int TotalChunks { get; set; }
        public int ReceivedChunks { get; set; }
        public DatasetMetadata Metadata { get; set; }
        public TransferStatus Status { get; set; }
        public string VolumeFilePath { get; set; }
        public string LabelsFilePath { get; set; }
        public bool IsComplete => ReceivedChunks >= TotalChunks;
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

    public class DatasetMetadata
    {
        public string Name { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int Depth { get; set; }
        public int ChunkDim { get; set; }
        public int TotalChunks { get; set; }
        public int ChunkSize => ChunkDim * ChunkDim * ChunkDim;
        public int BitDepth { get; set; } = 8;
        public double PixelSize { get; set; } = 1e-6; // Default to 1 micron
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public Dictionary<string, string> Properties { get; set; } = new Dictionary<string, string>();
    }
}