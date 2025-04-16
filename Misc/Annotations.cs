// Annotations.cs
using System;
using System.Collections.Generic;
using System.Linq;

namespace CTSegmenter
{
    /// <summary>
    /// Manages user annotations (points/boxes) for the SAM-based segmentation.
    /// </summary>
    internal class Annotations
    {
    }

    // If AnnotationPoint is already a separate class, we add the X2/Y2 properties for boxes
    public class AnnotationPoint
    {
        public int ID { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public int Z { get; set; }
        public string Type { get; set; } = "Point"; // "Point" or "Box"
        public string Label { get; set; } // The associated Material Name

        // Box properties (only if Type=="Box")
        public float X2 { get; set; } = 0;
        public float Y2 { get; set; } = 0;
    }

    /// <summary>
    /// Manages a list of AnnotationPoints for all slices and directions.
    /// </summary>
    public class AnnotationManager
    {
        public List<AnnotationPoint> Points { get; set; } = new List<AnnotationPoint>();

        public AnnotationPoint AddPoint(float x, float y, int z, string label)
        {
            int nextID = Points.Count + 1;
            var point = new AnnotationPoint
            {
                ID = nextID,
                X = x,
                Y = y,
                Z = z,
                Type = "Point",
                Label = label
            };
            Points.Add(point);
            return point;
        }

        // Add box creation method
        public AnnotationPoint AddBox(float x1, float y1, float x2, float y2, int z, string label)
        {
            int nextID = Points.Count + 1;
            var point = new AnnotationPoint
            {
                ID = nextID,
                X = x1,
                Y = y1,
                X2 = x2,
                Y2 = y2,
                Z = z,
                Type = "Box",
                Label = label
            };
            Points.Add(point);
            return point;
        }

        public void RemovePoint(int id)
        {
            Points.RemoveAll(p => p.ID == id);
            UpdatePointIDs();
        }

        private void UpdatePointIDs()
        {
            int counter = 1;
            foreach (var p in Points)
                p.ID = counter++;
        }
        public IEnumerable<AnnotationPoint> GetAllPoints()
        {
            return Points.ToList();
        }
        /// <summary>
        /// Returns all annotation points for a given slice Z.
        /// </summary>
        public IEnumerable<AnnotationPoint> GetPointsForSlice(int z)
        {
            return Points.FindAll(p => p.Z == z);
        }

        public void Clear()
        {
            Points.Clear();
        }
    }
}
