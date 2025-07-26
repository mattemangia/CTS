// Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using MapFlags = Vortice.Direct3D11.MapFlags;

namespace CTS.D3D11
{
    public class ChunkStreamingManager : IDisposable
    {
        private class GpuCacheSlot
        {
            public int ChunkIndex { get; set; } = -1;
            public long LastAccessTime { get; set; }
            public bool IsLoading { get; set; } = false;
        }

        private readonly ID3D11Device device;
        private readonly ID3D11DeviceContext context;
        private readonly IGrayscaleVolumeData volumeData;
        private readonly ILabelVolumeData labelData;

        private readonly ID3D11Texture2D grayscaleCache;
        private readonly ID3D11Texture2D labelCache;

        private readonly GpuCacheSlot[] cacheSlots;
        private readonly Dictionary<int, int> chunkToGpuSlotMap;
        private readonly ChunkInfoGPU[] chunkInfoGpuData;

        private Task streamingTask;
        private Task uploadTask;
        private readonly CancellationTokenSource cancellationTokenSource;
        private Camera lastCameraState;
        private bool isDirty = true;
        private readonly object _lock = new object();
        private readonly int totalChunks;

        private ID3D11Texture2D stagingGrayscaleTexture;
        private ID3D11Texture2D stagingLabelTexture;

        // Queue for asynchronous chunk uploads
        private readonly Queue<(int chunkIndex, int slotIndex)> uploadQueue = new Queue<(int, int)>();
        private readonly object uploadQueueLock = new object();
        private readonly ManualResetEventSlim uploadSignal = new ManualResetEventSlim(false);
        private volatile bool isDisposed = false;

        public ChunkStreamingManager(ID3D11Device device, ID3D11DeviceContext context, IGrayscaleVolumeData volumeData, ILabelVolumeData labelData, ID3D11Texture2D grayscaleCache, ID3D11Texture2D labelCache)
        {
            this.device = device ?? throw new ArgumentNullException(nameof(device));
            this.context = context ?? throw new ArgumentNullException(nameof(context));
            this.volumeData = volumeData ?? throw new ArgumentNullException(nameof(volumeData));
            this.labelData = labelData ?? throw new ArgumentNullException(nameof(labelData));
            this.grayscaleCache = grayscaleCache ?? throw new ArgumentNullException(nameof(grayscaleCache));
            this.labelCache = labelCache ?? throw new ArgumentNullException(nameof(labelCache));

            totalChunks = volumeData.ChunkCountX * volumeData.ChunkCountY * volumeData.ChunkCountZ;

            // Calculate cache size from texture array size
            int arraySize = grayscaleCache.Description.ArraySize;
            int chunkDim = volumeData.ChunkDim;
            int numCacheSlots = Math.Max(1, arraySize / chunkDim); // Ensure at least 1 slot

            cacheSlots = new GpuCacheSlot[numCacheSlots];
            for (int i = 0; i < cacheSlots.Length; i++)
            {
                cacheSlots[i] = new GpuCacheSlot();
            }

            chunkToGpuSlotMap = new Dictionary<int, int>();
            chunkInfoGpuData = new ChunkInfoGPU[totalChunks];
            for (int i = 0; i < chunkInfoGpuData.Length; i++)
                chunkInfoGpuData[i].GpuSlotIndex = -1;

            // Create staging textures for efficient GPU upload
            var stagingDesc = new Texture2DDescription
            {
                Width = volumeData.ChunkDim,
                Height = volumeData.ChunkDim,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Write
            };
            stagingGrayscaleTexture = device.CreateTexture2D(stagingDesc);

            stagingDesc.Format = Format.R8_UInt;
            stagingLabelTexture = device.CreateTexture2D(stagingDesc);

            Logger.Log($"[ChunkStreamingManager] Initialized with {numCacheSlots} cache slots for {totalChunks} total chunks");

            cancellationTokenSource = new CancellationTokenSource();

            // Start background tasks
            streamingTask = Task.Run(() => StreamingLoop(cancellationTokenSource.Token), cancellationTokenSource.Token);
            uploadTask = Task.Run(() => ProcessUploadQueue(cancellationTokenSource.Token), cancellationTokenSource.Token);
        }

        public void Update(Camera camera)
        {
            if (isDisposed || camera == null) return;

            lock (_lock)
            {
                lastCameraState = camera;
                isDirty = true;
            }
        }

        public bool IsDirty() => isDirty;
        public ChunkInfoGPU[] GetGpuChunkInfo() => chunkInfoGpuData;

        private async Task StreamingLoop(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && !isDisposed)
            {
                try
                {
                    Camera currentCamera = null;
                    lock (_lock)
                    {
                        if (isDirty && lastCameraState != null)
                        {
                            currentCamera = lastCameraState;
                            isDirty = false;
                        }
                    }

                    if (currentCamera != null)
                    {
                        var requiredChunks = DetermineRequiredChunks(currentCamera);
                        ProcessRequiredChunks(requiredChunks);
                    }

                    await Task.Delay(50, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Log($"[ChunkStreamingManager] StreamingLoop error: {ex.Message}");
                }
            }

            Logger.Log("[ChunkStreamingManager] StreamingLoop exited");
        }

        private async Task ProcessUploadQueue(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && !isDisposed)
            {
                try
                {
                    (int chunkIndex, int slotIndex) uploadItem = (-1, -1);

                    lock (uploadQueueLock)
                    {
                        if (uploadQueue.Count > 0)
                        {
                            uploadItem = uploadQueue.Dequeue();
                        }
                        else
                        {
                            uploadSignal.Reset();
                        }
                    }

                    if (uploadItem.chunkIndex >= 0)
                    {
                        if (!isDisposed)
                        {
                            UploadChunkToGpu(uploadItem.chunkIndex, uploadItem.slotIndex);

                            lock (_lock)
                            {
                                if (!isDisposed && uploadItem.slotIndex < cacheSlots.Length)
                                {
                                    cacheSlots[uploadItem.slotIndex].IsLoading = false;
                                }
                            }
                        }
                    }
                    else
                    {
                        // Wait for signal or timeout
                        uploadSignal.Wait(100, cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Log($"[ChunkStreamingManager] ProcessUploadQueue error: {ex.Message}");
                }
            }

            Logger.Log("[ChunkStreamingManager] ProcessUploadQueue exited");
        }

        private HashSet<int> DetermineRequiredChunks(Camera camera)
        {
            var required = new HashSet<int>();
            var camPos = camera.Position;
            var chunkDistances = new List<(int index, float distance)>();

            int countX = volumeData.ChunkCountX;
            int countY = volumeData.ChunkCountY;
            int countZ = volumeData.ChunkCountZ;
            int chunkDim = volumeData.ChunkDim;

            // Calculate distances to all chunks
            for (int cz = 0; cz < countZ; cz++)
            {
                for (int cy = 0; cy < countY; cy++)
                {
                    for (int cx = 0; cx < countX; cx++)
                    {
                        var chunkCenter = new Vector3(
                            (cx + 0.5f) * chunkDim,
                            (cy + 0.5f) * chunkDim,
                            (cz + 0.5f) * chunkDim);

                        float distance = Vector3.DistanceSquared(camPos, chunkCenter);
                        int chunkIndex = cz * (countX * countY) + cy * countX + cx;
                        chunkDistances.Add((chunkIndex, distance));
                    }
                }
            }

            // Sort by distance and take the closest chunks that fit in cache
            var orderedChunks = chunkDistances.OrderBy(c => c.distance).ToList();
            int maxChunks = cacheSlots.Length;

            foreach (var (chunkIndex, _) in orderedChunks.Take(maxChunks))
            {
                required.Add(chunkIndex);
            }

            return required;
        }

        private void ProcessRequiredChunks(HashSet<int> requiredChunks)
        {
            if (isDisposed || requiredChunks == null) return;

            // First pass: mark chunks that are already loaded
            foreach (var chunkIndex in requiredChunks)
            {
                if (chunkToGpuSlotMap.ContainsKey(chunkIndex))
                {
                    int slotIndex = chunkToGpuSlotMap[chunkIndex];
                    if (slotIndex >= 0 && slotIndex < cacheSlots.Length)
                    {
                        cacheSlots[slotIndex].LastAccessTime = DateTime.Now.Ticks;
                    }
                }
            }

            // Second pass: load missing chunks
            foreach (var chunkIndex in requiredChunks)
            {
                if (!chunkToGpuSlotMap.ContainsKey(chunkIndex))
                {
                    int slotIndex = FindGpuSlot(requiredChunks);
                    if (slotIndex >= 0)
                    {
                        lock (_lock)
                        {
                            if (isDisposed) return;

                            // Check if slot is already being loaded
                            if (cacheSlots[slotIndex].IsLoading)
                                continue;

                            // Clean up old chunk in this slot
                            if (cacheSlots[slotIndex].ChunkIndex != -1)
                            {
                                int oldChunkIndex = cacheSlots[slotIndex].ChunkIndex;
                                chunkToGpuSlotMap.Remove(oldChunkIndex);
                                if (oldChunkIndex >= 0 && oldChunkIndex < chunkInfoGpuData.Length)
                                {
                                    chunkInfoGpuData[oldChunkIndex].GpuSlotIndex = -1;
                                }
                            }

                            // Mark as loading and assign chunk
                            cacheSlots[slotIndex].IsLoading = true;
                            cacheSlots[slotIndex].ChunkIndex = chunkIndex;
                            cacheSlots[slotIndex].LastAccessTime = DateTime.Now.Ticks;
                            chunkToGpuSlotMap[chunkIndex] = slotIndex;
                            if (chunkIndex >= 0 && chunkIndex < chunkInfoGpuData.Length)
                            {
                                chunkInfoGpuData[chunkIndex].GpuSlotIndex = slotIndex;
                            }
                        }

                        // Queue for async upload
                        lock (uploadQueueLock)
                        {
                            uploadQueue.Enqueue((chunkIndex, slotIndex));
                            uploadSignal.Set();
                        }
                    }
                }
            }
        }

        private int FindGpuSlot(HashSet<int> requiredChunks)
        {
            lock (_lock)
            {
                if (isDisposed) return -1;

                // First, try to find an empty slot
                for (int i = 0; i < cacheSlots.Length; i++)
                {
                    if (cacheSlots[i].ChunkIndex == -1 && !cacheSlots[i].IsLoading)
                        return i;
                }

                // Find the least recently used slot that's not currently loading
                // and not in the required set
                long minTime = long.MaxValue;
                int lruIndex = -1;

                for (int i = 0; i < cacheSlots.Length; i++)
                {
                    if (!cacheSlots[i].IsLoading &&
                        cacheSlots[i].ChunkIndex != -1 &&
                        !requiredChunks.Contains(cacheSlots[i].ChunkIndex) &&
                        cacheSlots[i].LastAccessTime < minTime)
                    {
                        minTime = cacheSlots[i].LastAccessTime;
                        lruIndex = i;
                    }
                }

                return lruIndex;
            }
        }

        private void UploadChunkToGpu(int chunkIndex, int slotIndex)
        {
            if (isDisposed) return;

            try
            {
                var grayData = volumeData.GetChunkBytes(chunkIndex);
                var labelDataBytes = labelData.GetChunkBytes(chunkIndex);

                if (grayData == null || labelDataBytes == null)
                {
                    Logger.Log($"[ChunkStreamingManager] Null data for chunk {chunkIndex}");
                    return;
                }

                int chunkDim = volumeData.ChunkDim;
                int sliceByteSize = chunkDim * chunkDim;
                int startSliceIndexInAtlas = slotIndex * chunkDim;

                // Upload each slice
                lock (context)
                {
                    if (isDisposed) return;

                    for (int i = 0; i < chunkDim; i++)
                    {
                        if (isDisposed) break;

                        // Upload grayscale slice
                        var mappedGray = context.Map(stagingGrayscaleTexture, 0, MapMode.Write, MapFlags.None);
                        if (mappedGray.DataPointer != IntPtr.Zero)
                        {
                            try
                            {
                                // Calculate proper stride and copy
                                int srcOffset = i * sliceByteSize;
                                if (srcOffset + sliceByteSize <= grayData.Length)
                                {
                                    Marshal.Copy(grayData, srcOffset, mappedGray.DataPointer, sliceByteSize);
                                }
                            }
                            finally
                            {
                                context.Unmap(stagingGrayscaleTexture, 0);
                            }

                            int destGraySliceSubresource = startSliceIndexInAtlas + i;

                            // Use explicit source box
                            var srcBox = new Box(0, 0, 0, chunkDim, chunkDim, 1);
                            context.CopySubresourceRegion(
                                grayscaleCache,
                                destGraySliceSubresource,
                                0, 0, 0,
                                stagingGrayscaleTexture,
                                0,
                                srcBox);
                        }

                        // Upload label slice
                        var mappedLabel = context.Map(stagingLabelTexture, 0, MapMode.Write, MapFlags.None);
                        if (mappedLabel.DataPointer != IntPtr.Zero)
                        {
                            try
                            {
                                int srcOffset = i * sliceByteSize;
                                if (srcOffset + sliceByteSize <= labelDataBytes.Length)
                                {
                                    Marshal.Copy(labelDataBytes, srcOffset, mappedLabel.DataPointer, sliceByteSize);
                                }
                            }
                            finally
                            {
                                context.Unmap(stagingLabelTexture, 0);
                            }

                            int destLabelSliceSubresource = startSliceIndexInAtlas + i;

                            // Use explicit source box
                            var srcBox = new Box(0, 0, 0, chunkDim, chunkDim, 1);
                            context.CopySubresourceRegion(
                                labelCache,
                                destLabelSliceSubresource,
                                0, 0, 0,
                                stagingLabelTexture,
                                0,
                                srcBox);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[ChunkStreamingManager] Error uploading chunk {chunkIndex}: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (isDisposed) return;

            Logger.Log("[ChunkStreamingManager] Dispose called");

            isDisposed = true;

            // Cancel background tasks
            cancellationTokenSource?.Cancel();
            uploadSignal?.Set(); // Wake up the upload thread

            try
            {
                // Wait for tasks to complete
                var tasks = new List<Task>();
                if (streamingTask != null) tasks.Add(streamingTask);
                if (uploadTask != null) tasks.Add(uploadTask);

                if (tasks.Count > 0)
                {
                    if (!Task.WaitAll(tasks.ToArray(), 2000))
                    {
                        Logger.Log("[ChunkStreamingManager] Tasks did not complete in time");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[ChunkStreamingManager] Error waiting for tasks: {ex.Message}");
            }

            // Clean up resources
            cancellationTokenSource?.Dispose();
            uploadSignal?.Dispose();

            // Clear staging textures - check if not null before disposing
            try
            {
                stagingGrayscaleTexture?.Dispose();
                stagingLabelTexture?.Dispose();
            }
            catch (Exception ex)
            {
                Logger.Log($"[ChunkStreamingManager] Error disposing staging textures: {ex.Message}");
            }

            Logger.Log("[ChunkStreamingManager] Disposed");
        }
    }
}