using System;
using System.Drawing;

namespace CTSegmenter
{
    /// <summary>
    /// Extension methods for ChunkedVolume that match the signatures 
    /// used in MainForm (ReadRow, ReadLineXZ, ReadLineYZ).
    /// </summary>
    public static class VolumeExtensions
    {
        /// <summary>
        /// Reads a single row (y=rowIndex) from the XY plane at Z=sliceIndex 
        /// into 'buffer' of length = volume width.
        /// </summary>
        public static void ReadRow(this ChunkedVolume vol, int sliceIndex, int rowIndex, byte[] buffer)
        {
            // We'll do naive indexing from the volume indexer.
            // data = volume[x, y, z]
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (buffer.Length != vol.Width)
                throw new ArgumentException("buffer.Length must match volume width.", nameof(buffer));

            for (int x = 0; x < vol.Width; x++)
            {
                buffer[x] = vol[x, rowIndex, sliceIndex];
            }
        }

        /// <summary>
        /// Reads a horizontal line in the XZ plane for Y=yFixed at Z=z. 
        /// Fills 'buffer[x]' for x in [0..Width-1].
        /// </summary>
        public static void ReadLineXZ(this ChunkedVolume vol, int yFixed, int z, byte[] buffer)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (buffer.Length != vol.Width)
                throw new ArgumentException("buffer.Length must match volume width.", nameof(buffer));

            for (int x = 0; x < vol.Width; x++)
            {
                buffer[x] = vol[x, yFixed, z];
            }
        }

        /// <summary>
        /// Reads a horizontal line in the YZ plane for X=xFixed at Y=y. 
        /// Fills 'buffer[z]' for z in [0..Depth-1].
        /// </summary>
        public static void ReadLineYZ(this ChunkedVolume vol, int xFixed, int y, byte[] buffer)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (buffer.Length != vol.Depth)
                throw new ArgumentException("buffer.Length must match volume depth.", nameof(buffer));

            for (int z = 0; z < vol.Depth; z++)
            {
                buffer[z] = vol[xFixed, y, z];
            }
        }
    }

    /// <summary>
    /// Extension methods for ChunkedLabelVolume to match the calls in MainForm 
    /// (ReadSliceZ, ReadLineXZ, ReadLineYZ).
    /// </summary>
    public static class LabelVolumeExtensions
    {
        /// <summary>
        /// Reads the entire XY plane at Z=sliceIndex into dataOut of length (width*height).
        /// dataOut is row-major: row=Y, col=X => index = y*width + x.
        /// </summary>
        public static void ReadSliceZ(this ChunkedLabelVolume vol, int sliceIndex, byte[] dataOut)
        {
            if (dataOut == null) throw new ArgumentNullException(nameof(dataOut));
            if (dataOut.Length != vol.Width * vol.Height)
                throw new ArgumentException("dataOut.Length must be width * height.", nameof(dataOut));

            int w = vol.Width;
            int h = vol.Height;
            int d = vol.Depth;
            if (sliceIndex < 0 || sliceIndex >= d)
                throw new ArgumentOutOfRangeException(nameof(sliceIndex));

            // For each (x,y) in [0..w-1, 0..h-1], read labelVolume[x,y,z]
            int idx = 0;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    dataOut[idx++] = vol[x, y, sliceIndex];
                }
            }
        }

        /// <summary>
        /// Reads a horizontal line in the XZ plane for Y=yFixed at Z=z,
        /// filling buffer[x].
        /// </summary>
        public static void ReadLineXZ(this ChunkedLabelVolume vol, int yFixed, int z, byte[] buffer)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (buffer.Length != vol.Width)
                throw new ArgumentException("buffer.Length must match volume width.", nameof(buffer));

            int w = vol.Width;
            for (int x = 0; x < w; x++)
            {
                buffer[x] = vol[x, yFixed, z];
            }
        }

        /// <summary>
        /// Reads a horizontal line in the YZ plane for X=xFixed, Y=y in [0..Height-1], 
        /// filling buffer[z] for z in [0..Depth-1].
        /// </summary>
        public static void ReadLineYZ(this ChunkedLabelVolume vol, int xFixed, int y, byte[] buffer)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (buffer.Length != vol.Depth)
                throw new ArgumentException("buffer.Length must match volume depth.", nameof(buffer));

            int d = vol.Depth;
            for (int z = 0; z < d; z++)
            {
                buffer[z] = vol[xFixed, y, z];
            }
        }
    }

    /// <summary>
    /// A small static class that provides BlendColors and GetComplementaryColor, 
    /// since MainForm references them but didn't define them.
    /// </summary>
    public static class ColorHelpers
    {
        /// <summary>
        /// Blends two colors with the given alpha for the second color.
        /// alpha=0 => 100% baseColor, alpha=1 => 100% overlay
        /// </summary>
        public static Color BlendColors(Color baseColor, Color overlay, float alpha)
        {
            int r = (int)(baseColor.R * (1 - alpha) + overlay.R * alpha);
            int g = (int)(baseColor.G * (1 - alpha) + overlay.G * alpha);
            int b = (int)(baseColor.B * (1 - alpha) + overlay.B * alpha);
            return Color.FromArgb(r, g, b);
        }

        /// <summary>
        /// Returns the complementary color to the input (255 - R,G,B).
        /// </summary>
        public static Color GetComplementaryColor(Color color)
        {
            return Color.FromArgb(255 - color.R, 255 - color.G, 255 - color.B);
        }
    }
}
