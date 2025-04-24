using System;

namespace CTSegmenter
{
    /// <summary>
    /// Class to store statistics for a single material
    /// </summary>
    public class MaterialStatistics
    {
        /// <summary>
        /// The material this statistics object represents
        /// </summary>
        public Material Material { get; set; }

        /// <summary>
        /// The number of voxels with this material in the volume
        /// </summary>
        public long VoxelCount { get; set; }

        /// <summary>
        /// Volume in cubic micrometers
        /// </summary>
        public double VolumeUm3 { get; set; }

        /// <summary>
        /// Volume in cubic millimeters
        /// </summary>
        public double VolumeMm3 { get; set; }

        /// <summary>
        /// Volume in cubic centimeters
        /// </summary>
        public double VolumeCm3 { get; set; }

        /// <summary>
        /// Percentage of total volume (0-100)
        /// </summary>
        public double VolumePercentage { get; set; }

        /// <summary>
        /// Creates a new MaterialStatistics object for the specified material
        /// </summary>
        /// <param name="material">The material to calculate statistics for</param>
        public MaterialStatistics(Material material)
        {
            Material = material;
        }

        /// <summary>
        /// Calculates volume values based on voxel count and pixel size
        /// </summary>
        /// <param name="pixelSizeMeters">The pixel size in meters</param>
        public void CalculateVolumes(double pixelSizeMeters)
        {
            // Calculate volume in cubic micrometers (1 meter = 1,000,000 micrometers)
            double voxelVolumeUm3 = Math.Pow(pixelSizeMeters * 1e6, 3);
            VolumeUm3 = VoxelCount * voxelVolumeUm3;

            // Calculate volume in cubic millimeters (1 meter = 1,000 millimeters)
            VolumeMm3 = VolumeUm3 / 1e9;

            // Calculate volume in cubic centimeters (1 meter = 100 centimeters)
            VolumeCm3 = VolumeMm3 / 1000;
        }
    }
}