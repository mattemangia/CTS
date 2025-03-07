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
    public class AnnotationPoint
    {
        public int ID { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public int Z { get; set; }
        public string Type { get; set; } // e.g., "Point", "Rectangle"
        public string Label { get; set; } // Material Name
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
