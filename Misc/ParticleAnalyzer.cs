//Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CTS
{
    /// <summary>
    /// Provides functionality to analyze and modify particles within a label volume.
    /// This class is optimized for performance and low memory usage on large datasets.
    /// </summary>
    public class ParticleAnalyzer
    {
        // Private struct for tracking a 3D point, used in BFS
        private readonly struct Point3D
        {
            public readonly int X, Y, Z;
            public Point3D(int x, int y, int z) { X = x; Y = y; Z = z; }
        }

        // Private struct to store information about each discovered particle
        private class ParticleInfo
        {
            public long VoxelCount { get; set; }
            public Point3D StartVoxel { get; set; }
        }

        /// <summary>
        /// Asynchronously removes disconnected particles of a given material that are smaller than a specified voxel count.
        /// </summary>
        /// <param name="volumeLabels">The label volume data to modify.</param>
        /// <param name="materials">The list of materials to find a temporary ID from.</param>
        /// <param name="materialID">The ID of the material to process.</param>
        /// <param name="minVoxelCount">The minimum number of voxels a particle must have to not be removed.</param>
        /// <param name="statusProgress">A progress reporter for status messages.</param>
        /// <param name="percentageProgress">A progress reporter for overall percentage completion.</param>
        public async Task RemoveSmallIslandsAsync(
            ILabelVolumeData volumeLabels,
            List<Material> materials,
            byte materialID,
            long minVoxelCount,
            IProgress<string> statusProgress,
            IProgress<int> percentageProgress)
        {
            await Task.Run(() =>
            {
                int width = volumeLabels.Width;
                int height = volumeLabels.Height;
                int depth = volumeLabels.Depth;

                // 1. Find a temporary material ID for marking processed voxels
                byte tempID = FindTemporaryID(materials);
                statusProgress.Report($"Using temporary ID: {tempID}");

                // 2. Pass 1: Find all particles, mark them with tempID, and record their size.
                statusProgress.Report("Pass 1/2: Identifying particles...");
                var particles = new List<ParticleInfo>();

                for (int z = 0; z < depth; z++)
                {
                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            if (volumeLabels[x, y, z] == materialID)
                            {
                                // Found the start of a new, unvisited particle
                                var startVoxel = new Point3D(x, y, z);
                                long count = BfsMarkAndCount(volumeLabels, startVoxel, materialID, tempID);
                                particles.Add(new ParticleInfo { VoxelCount = count, StartVoxel = startVoxel });
                            }
                        }
                    }
                    int progress = (int)(((z + 1.0) / depth) * 50.0);
                    percentageProgress.Report(progress);
                }
                statusProgress.Report($"Identified {particles.Count} particles.");

                // 3. Pass 2: Re-label all particles based on their size.
                statusProgress.Report("Pass 2/2: Removing small particles...");
                int particlesProcessed = 0;
                foreach (var particle in particles)
                {
                    // If the particle is smaller than the threshold, relabel to Exterior (0).
                    // Otherwise, relabel it back to its original material ID.
                    byte finalID = (particle.VoxelCount < minVoxelCount) ? (byte)0 : materialID;
                    BfsRelabel(volumeLabels, particle.StartVoxel, tempID, finalID);

                    particlesProcessed++;
                    int progress = 50 + (int)(((double)particlesProcessed / particles.Count) * 50.0);
                    percentageProgress.Report(progress);
                }

                statusProgress.Report("Operation complete.");
                percentageProgress.Report(100);
            });
        }

        /// <summary>
        /// Finds an unused material ID to serve as a temporary marker during processing.
        /// </summary>
        private byte FindTemporaryID(List<Material> materials)
        {
            var usedIds = new HashSet<byte>(materials.Select(m => m.ID));
            for (byte i = 255; i > 0; i--)
            {
                if (!usedIds.Contains(i))
                {
                    return i;
                }
            }
            throw new InvalidOperationException("No available temporary material ID between 1 and 255. Please consolidate materials.");
        }

        /// <summary>
        /// Performs a Breadth-First Search (BFS) to find all connected voxels of a particle,
        /// marking them with a temporary ID and counting them.
        /// </summary>
        private long BfsMarkAndCount(ILabelVolumeData volume, Point3D start, byte targetID, byte tempID)
        {
            long count = 0;
            var queue = new Queue<Point3D>();
            int width = volume.Width, height = volume.Height, depth = volume.Depth;

            queue.Enqueue(start);
            volume[start.X, start.Y, start.Z] = tempID;
            count++;

            while (queue.Count > 0)
            {
                var p = queue.Dequeue();

                // Check 6 neighbors (Up, Down, Left, Right, Forward, Back)
                int[] dx = { 0, 0, 0, 0, 1, -1 };
                int[] dy = { 0, 0, 1, -1, 0, 0 };
                int[] dz = { 1, -1, 0, 0, 0, 0 };

                for (int i = 0; i < 6; i++)
                {
                    int nx = p.X + dx[i];
                    int ny = p.Y + dy[i];
                    int nz = p.Z + dz[i];

                    // Check bounds
                    if (nx >= 0 && nx < width && ny >= 0 && ny < height && nz >= 0 && nz < depth)
                    {
                        if (volume[nx, ny, nz] == targetID)
                        {
                            volume[nx, ny, nz] = tempID;
                            count++;
                            queue.Enqueue(new Point3D(nx, ny, nz));
                        }
                    }
                }
            }
            return count;
        }

        /// <summary>
        /// Performs a BFS to re-label a particle that was marked with a temporary ID.
        /// </summary>
        private void BfsRelabel(ILabelVolumeData volume, Point3D start, byte tempID, byte finalID)
        {
            var queue = new Queue<Point3D>();
            int width = volume.Width, height = volume.Height, depth = volume.Depth;

            queue.Enqueue(start);
            volume[start.X, start.Y, start.Z] = finalID;

            while (queue.Count > 0)
            {
                var p = queue.Dequeue();

                int[] dx = { 0, 0, 0, 0, 1, -1 };
                int[] dy = { 0, 0, 1, -1, 0, 0 };
                int[] dz = { 1, -1, 0, 0, 0, 0 };

                for (int i = 0; i < 6; i++)
                {
                    int nx = p.X + dx[i];
                    int ny = p.Y + dy[i];
                    int nz = p.Z + dz[i];

                    if (nx >= 0 && nx < width && ny >= 0 && ny < height && nz >= 0 && nz < depth)
                    {
                        if (volume[nx, ny, nz] == tempID)
                        {
                            volume[nx, ny, nz] = finalID;
                            queue.Enqueue(new Point3D(nx, ny, nz));
                        }
                    }
                }
            }
        }
    }
}
