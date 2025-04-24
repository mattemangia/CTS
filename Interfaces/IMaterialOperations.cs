namespace CTSegmenter
{
    /// <summary>
    /// Interface for material and voxel operations on volume data.
    /// Provides methods for adding, removing, and modifying material assignments in 3D volumes.
    /// </summary>
    public interface IMaterialOperations
    {
        // Material management
        byte GetNextMaterialID();

        void RemoveMaterial(byte materialID);

        // Voxel operations
        void AddVoxelsByThreshold(IGrayscaleVolumeData volumeData, byte materialID, byte minVal, byte maxVal);

        void AddVoxelsByThresholdForSlice(IGrayscaleVolumeData volumeData, byte materialID, byte minVal, byte maxVal, int slice);

        void RemoveVoxelsByThreshold(IGrayscaleVolumeData volumeData, byte materialID, byte minVal, byte maxVal);

        void RemoveVoxelsByThresholdForSlice(IGrayscaleVolumeData volumeData, byte materialID, byte minVal, byte maxVal, int slice);

        void ApplySelection(byte[,] selection, int slice);

        void SubtractSelection(byte[,] selection, int slice);

        void ApplyOrthogonalSelection(byte[,] selection, int fixedIndex, OrthogonalView view);

        void SubtractOrthogonalSelection(byte[,] selection, int fixedIndex, OrthogonalView view);
    }

    /// <summary>
    /// Enum representing the different orthogonal views in 3D volume data
    /// </summary>
    public enum OrthogonalView
    {
        XY, // Standard slice view (Z axis fixed)
        XZ, // X-Z view (Y axis fixed)
        YZ  // Y-Z view (X axis fixed)
    }
}