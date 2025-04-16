using System;
using System.Drawing;
using System.Windows.Media;
using SharpDX;

namespace CTSegmenter.SharpDXIntegration
{
    public class MeasurementLine
    {
        public Vector3 Start { get; set; }
        public Vector3 End { get; set; }
        public float Distance { get; set; }
        public float RealDistance { get; set; }
        public string Unit { get; set; } = "mm";
        public string Label { get; set; } = "M1";
        public bool Visible { get; set; } = true;
        public System.Drawing.Color Color { get; set; } = System.Drawing.Color.Yellow;

        // For slice-specific measurements
        public bool IsOnSlice { get; set; } = false;
        public int SliceType { get; set; } = 0; // 1=X, 2=Y, 3=Z
        public int SlicePosition { get; set; } = 0;

        public override string ToString()
        {
            return $"{Label}: {RealDistance:F2} {Unit} ({Distance:F1} voxels)";
        }
    }
}