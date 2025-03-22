using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CTSegmenter
{
    internal class Annotations
    {
    }

    // If AnnotationPoint is already a separate class, add the X2/Y2 properties
    public class AnnotationPoint
    {
        public int ID { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public int Z { get; set; }
        public string Type { get; set; } = "Point"; // "Point" or "Box" 
        public string Label { get; set; } // Material Name

        // Add these properties for box support
        public float X2 { get; set; } = 0;
        public float Y2 { get; set; } = 0;
    }

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
