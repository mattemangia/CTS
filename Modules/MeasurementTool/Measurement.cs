//Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
using System;
using System.Drawing;

namespace CTS
{
    /// <summary>
    /// Represents a single measurement line in the volume
    /// </summary>
    public class Measurement
    {
        public int ID { get; set; }
        public string Name { get; set; }
        public ViewType ViewType { get; set; }
        public Point StartPoint { get; set; }
        public Point EndPoint { get; set; }
        public int SliceIndex { get; set; }
        public double Distance { get; set; } // Distance in meters
        public string DistanceDisplayText { get; set; }
        public Color LineColor { get; set; }
        public DateTime CreatedAt { get; set; }

        // Constructor
        public Measurement()
        {
            ID = 0;
            Name = "";
            ViewType = ViewType.XY;
            StartPoint = Point.Empty;
            EndPoint = Point.Empty;
            SliceIndex = 0;
            Distance = 0.0;
            DistanceDisplayText = "";
            LineColor = Color.Red;
            CreatedAt = DateTime.Now;
        }

        public Measurement(int id, string name, ViewType viewType, Point start, Point end, int sliceIndex, double distance, Color color)
        {
            ID = id;
            Name = name;
            ViewType = viewType;
            StartPoint = start;
            EndPoint = end;
            SliceIndex = sliceIndex;
            Distance = distance;
            LineColor = color;
            CreatedAt = DateTime.Now;

            // Create display text based on distance
            if (distance < 1e-3)
                DistanceDisplayText = $"{distance * 1e6:0.00} µm";
            else if (distance < 1.0)
                DistanceDisplayText = $"{distance * 1e3:0.00} mm";
            else
                DistanceDisplayText = $"{distance:0.000} m";
        }

        // Calculate the actual distance between two points in 3D space
        public static double CalculateDistance(Point start, Point end, ViewType viewType, double pixelSize)
        {
            switch (viewType)
            {
                case ViewType.XY:
                    // XY plane: distance = sqrt((x2-x1)² + (y2-y1)²) * pixelSize
                    double dx = end.X - start.X;
                    double dy = end.Y - start.Y;
                    return Math.Sqrt(dx * dx + dy * dy) * pixelSize;

                case ViewType.XZ:
                    // XZ plane: distance = sqrt((x2-x1)² + (z2-z1)²) * pixelSize
                    double dxz_x = end.X - start.X;
                    double dxz_z = end.Y - start.Y; // Y coordinate represents Z in XZ view
                    return Math.Sqrt(dxz_x * dxz_x + dxz_z * dxz_z) * pixelSize;

                case ViewType.YZ:
                    // YZ plane: distance = sqrt((y2-y1)² + (z2-z1)²) * pixelSize
                    double dyz_y = end.Y - start.Y;
                    double dyz_z = end.X - start.X; // X coordinate represents Z in YZ view
                    return Math.Sqrt(dyz_y * dyz_y + dyz_z * dyz_z) * pixelSize;

                default:
                    return 0.0;
            }
        }

        // Get the center point for label placement
        public Point GetLabelPoint()
        {
            return new Point(
                (StartPoint.X + EndPoint.X) / 2,
                (StartPoint.Y + EndPoint.Y) / 2
            );
        }

        // Check if a point is near this measurement line
        public bool IsNearLine(Point point, float tolerance = 5.0f)
        {
            // Calculate distance from point to line segment
            double lineLength = Math.Sqrt(Math.Pow(EndPoint.X - StartPoint.X, 2) + Math.Pow(EndPoint.Y - StartPoint.Y, 2));
            if (lineLength == 0) return false;

            double t = ((point.X - StartPoint.X) * (EndPoint.X - StartPoint.X) +
                        (point.Y - StartPoint.Y) * (EndPoint.Y - StartPoint.Y)) / (lineLength * lineLength);

            t = Math.Max(0, Math.Min(1, t));

            double projectionX = StartPoint.X + t * (EndPoint.X - StartPoint.X);
            double projectionY = StartPoint.Y + t * (EndPoint.Y - StartPoint.Y);

            double distance = Math.Sqrt(Math.Pow(point.X - projectionX, 2) + Math.Pow(point.Y - projectionY, 2));

            return distance <= tolerance;
        }
    }
}