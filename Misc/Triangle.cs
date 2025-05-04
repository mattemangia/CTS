using System;
using System.Numerics;

namespace CTS
{
    /// <summary>
    /// Represents a 3D triangle with physical properties for simulation
    /// </summary>
    public struct Triangle
    {
        /// <summary>
        /// First vertex of the triangle
        /// </summary>
        public Vector3 V1 { get; set; }

        /// <summary>
        /// Second vertex of the triangle
        /// </summary>
        public Vector3 V2 { get; set; }

        /// <summary>
        /// Third vertex of the triangle
        /// </summary>
        public Vector3 V3 { get; set; }

        /// <summary>
        /// Normal vector of the triangle
        /// </summary>
        public Vector3 Normal { get; private set; }

        /// <summary>
        /// Center point of the triangle
        /// </summary>
        public Vector3 Center { get; private set; }

        /// <summary>
        /// Area of the triangle
        /// </summary>
        public float Area { get; private set; }

        /// <summary>
        /// Von Mises stress at this triangle (used for visualization)
        /// </summary>
        public float VonMisesStress { get; set; }

        /// <summary>
        /// Principal stress 1 (used for Mohr-Coulomb)
        /// </summary>
        public float Stress1 { get; set; }

        /// <summary>
        /// Principal stress 2 (used for Mohr-Coulomb)
        /// </summary>
        public float Stress2 { get; set; }

        /// <summary>
        /// Principal stress 3 (used for Mohr-Coulomb)
        /// </summary>
        public float Stress3 { get; set; }

        /// <summary>
        /// Whether this triangle is predicted to fracture
        /// </summary>
        public bool IsFractured { get; set; }

        /// <summary>
        /// Fracture probability (0-1)
        /// </summary>
        public float FractureProbability { get; set; }

        /// <summary>
        /// Displacement vector
        /// </summary>
        public Vector3 Displacement { get; set; }

        /// <summary>
        /// Constructor for a triangle with three vertices
        /// </summary>
        public Triangle(Vector3 v1, Vector3 v2, Vector3 v3)
        {
            V1 = v1;
            V2 = v2;
            V3 = v3;

            // Calculate normal
            Vector3 edge1 = V2 - V1;
            Vector3 edge2 = V3 - V1;
            Normal = Vector3.Normalize(Vector3.Cross(edge1, edge2));

            // Calculate center
            Center = (V1 + V2 + V3) / 3f;

            // Calculate area
            Area = 0.5f * Vector3.Cross(edge1, edge2).Length();

            // Initialize simulation properties
            VonMisesStress = 0;
            Stress1 = 0;
            Stress2 = 0;
            Stress3 = 0;
            IsFractured = false;
            FractureProbability = 0;
            Displacement = Vector3.Zero;
        }

        /// <summary>
        /// Calculate barycentric coordinates for a point projected onto this triangle
        /// </summary>
        public Vector3 CalculateBarycentricCoordinates(Vector3 point)
        {
            Vector3 e0 = V2 - V1;
            Vector3 e1 = V3 - V1;
            Vector3 e2 = point - V1;

            float d00 = Vector3.Dot(e0, e0);
            float d01 = Vector3.Dot(e0, e1);
            float d11 = Vector3.Dot(e1, e1);
            float d20 = Vector3.Dot(e2, e0);
            float d21 = Vector3.Dot(e2, e1);

            float denom = d00 * d11 - d01 * d01;
            float v = (d11 * d20 - d01 * d21) / denom;
            float w = (d00 * d21 - d01 * d20) / denom;
            float u = 1.0f - v - w;

            return new Vector3(u, v, w);
        }

        /// <summary>
        /// Check if a point projected onto the triangle's plane is inside the triangle
        /// </summary>
        public bool ContainsPoint(Vector3 point)
        {
            Vector3 barycentric = CalculateBarycentricCoordinates(point);
            return barycentric.X >= 0 && barycentric.Y >= 0 && barycentric.Z >= 0 &&
                   barycentric.X + barycentric.Y + barycentric.Z <= 1.0001f; // Small epsilon for floating point errors
        }

        /// <summary>
        /// Get the color for visualization based on stress or other properties
        /// </summary>
        public System.Drawing.Color GetColorForProperty(RenderMode mode)
        {
            switch (mode)
            {
                case RenderMode.Stress:
                    return GetHeatMapColor(VonMisesStress, 0, 100); // Assuming 0-100 MPa range

                case RenderMode.Strain:
                    // Using displacement magnitude as a proxy for strain
                    return GetHeatMapColor(Displacement.Length(), 0, 0.1f); // 0-0.1 range

                case RenderMode.FailureProbability:
                    return GetHeatMapColor(FractureProbability, 0, 1); // 0-1 range

                case RenderMode.Displacement:
                    return GetHeatMapColor(Displacement.Length(), 0, 0.1f); // 0-0.1 range

                default:
                    return IsFractured ? System.Drawing.Color.Red : System.Drawing.Color.LightBlue;
            }
        }

        /// <summary>
        /// Get a color from a heatmap gradient based on a value
        /// </summary>
        private System.Drawing.Color GetHeatMapColor(float value, float min, float max)
        {
            // Normalize value to 0-1 range
            float normalized = Math.Max(0, Math.Min(1, (value - min) / (max - min)));

            // Create a heatmap gradient: blue -> cyan -> green -> yellow -> red
            if (normalized < 0.25f)
            {
                // Blue to cyan
                float t = normalized / 0.25f;
                return System.Drawing.Color.FromArgb(
                    0,
                    (int)(255 * t),
                    255
                );
            }
            else if (normalized < 0.5f)
            {
                // Cyan to green
                float t = (normalized - 0.25f) / 0.25f;
                return System.Drawing.Color.FromArgb(
                    0,
                    255,
                    (int)(255 * (1 - t))
                );
            }
            else if (normalized < 0.75f)
            {
                // Green to yellow
                float t = (normalized - 0.5f) / 0.25f;
                return System.Drawing.Color.FromArgb(
                    (int)(255 * t),
                    255,
                    0
                );
            }
            else
            {
                // Yellow to red
                float t = (normalized - 0.75f) / 0.25f;
                return System.Drawing.Color.FromArgb(
                    255,
                    (int)(255 * (1 - t)),
                    0
                );
            }
        }
    }
}