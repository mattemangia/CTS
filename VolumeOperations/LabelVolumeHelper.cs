//Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
using CTS;
using System;

/// <summary>
/// Helper class for working with labeled volumes that could be either int[,,] or ChunkedLabelVolume
/// </summary>
public static class LabelVolumeHelper
{
    /// <summary>
    /// Gets the width of a label volume
    /// </summary>
    public static int GetWidth(object labelVolume)
    {
        if (labelVolume is int[,,] array)
            return array.GetLength(0);
        else if (labelVolume is ChunkedLabelVolume chunked)
            return chunked.Width;
        throw new ArgumentException("Unsupported label volume type");
    }

    /// <summary>
    /// Gets the height of a label volume
    /// </summary>
    public static int GetHeight(object labelVolume)
    {
        if (labelVolume is int[,,] array)
            return array.GetLength(1);
        else if (labelVolume is ChunkedLabelVolume chunked)
            return chunked.Height;
        throw new ArgumentException("Unsupported label volume type");
    }

    /// <summary>
    /// Gets the depth of a label volume
    /// </summary>
    public static int GetDepth(object labelVolume)
    {
        if (labelVolume is int[,,] array)
            return array.GetLength(2);
        else if (labelVolume is ChunkedLabelVolume chunked)
            return chunked.Depth;
        throw new ArgumentException("Unsupported label volume type");
    }

    /// <summary>
    /// Gets the label at the specified position
    /// </summary>
    public static int GetLabel(object labelVolume, int x, int y, int z)
    {
        if (labelVolume is int[,,] array)
            return array[x, y, z];
        else if (labelVolume is ChunkedLabelVolume chunked)
            return chunked[x, y, z]; // Assuming this returns an int or can be implicitly converted to int
        throw new ArgumentException("Unsupported label volume type");
    }
}
