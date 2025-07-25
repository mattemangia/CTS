// Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
using System;
using Vector3 = System.Numerics.Vector3;

namespace CTS.D3D11
{
    public class Camera
    {
        public Vector3 Position { get; private set; }
        public Vector3 Target { get; private set; }
        public Vector3 Up { get; private set; }
        public float AspectRatio { get; set; }
        public float Fov { get; set; } = (float)Math.PI / 4.0f;
        public float NearPlane { get; set; } = 0.1f;
        public float FarPlane { get; set; } = 5000.0f;

        private float radius;
        private float theta; // Azimuth
        private float phi;   // Polar

        public Camera(Vector3 position, Vector3 target, Vector3 up, float aspectRatio)
        {
            Position = position;
            Target = target;
            Up = up;
            AspectRatio = aspectRatio;
            UpdateSphericalCoords();
        }

        public Matrix4x4 ViewMatrix => Matrix4x4.CreateLookAt(Position, Target, Up);
        public Matrix4x4 ProjectionMatrix => Matrix4x4.CreatePerspectiveFieldOfView(Fov, AspectRatio, NearPlane, FarPlane);

        // Custom Math.Clamp implementation for .NET 4.8.1
        private static float Clamp(float value, float min, float max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private void UpdateSphericalCoords()
        {
            Vector3 direction = Position - Target;
            radius = direction.Length();
            phi = (float)Math.Acos(direction.Y / radius);
            theta = (float)Math.Atan2(direction.Z, direction.X);
        }

        private void UpdateCartesianCoords()
        {
            float x = radius * (float)Math.Sin(phi) * (float)Math.Cos(theta);
            float y = radius * (float)Math.Cos(phi);
            float z = radius * (float)Math.Sin(phi) * (float)Math.Sin(theta);
            Position = Target + new Vector3(x, y, z);
        }

        public void Orbit(float deltaTheta, float deltaPhi)
        {
            theta += deltaTheta;
            phi -= deltaPhi;
            phi = Clamp(phi, 0.1f, (float)Math.PI - 0.1f); // Prevent gimbal lock
            UpdateCartesianCoords();
        }

        public void Pan(float dx, float dy)
        {
            Vector3 right = Vector3.Normalize(Vector3.Cross(Up, Position - Target));
            Vector3 localUp = Vector3.Normalize(Vector3.Cross(Position - Target, right));

            Vector3 panVector = (right * -dx + localUp * dy) * 0.001f * radius;
            Position += panVector;
            Target += panVector;
        }

        public void Zoom(float delta)
        {
            radius += delta * radius;
            radius = Math.Max(0.1f, radius);
            UpdateCartesianCoords();
        }
    }
}