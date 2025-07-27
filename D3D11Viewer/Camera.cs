// Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
using System;
using System.Numerics;

namespace CTS.D3D11
{
    public class Camera
    {
        private Vector3 position;
        private Vector3 target;
        private Vector3 up;
        private float aspectRatio;
        private float fov = (float)Math.PI / 4.0f;
        private float nearPlane = 0.1f;
        private float farPlane = 5000.0f;

        private float radius;
        private float theta; // Azimuth
        private float phi;   // Polar

        // Thread-safe properties
        public Vector3 Position
        {
            get { lock (this) { return position; } }
            private set { lock (this) { position = value; } }
        }

        public Vector3 Target
        {
            get { lock (this) { return target; } }
            private set { lock (this) { target = value; } }
        }

        public Vector3 Up
        {
            get { lock (this) { return up; } }
            private set { lock (this) { up = value; } }
        }

        public float AspectRatio
        {
            get { lock (this) { return aspectRatio; } }
            set { lock (this) { aspectRatio = value; } }
        }

        public float Fov
        {
            get { lock (this) { return fov; } }
            set { lock (this) { fov = Clamp(value, 0.1f, (float)Math.PI - 0.1f); } }
        }

        public float NearPlane
        {
            get { lock (this) { return nearPlane; } }
            set { lock (this) { nearPlane = Math.Max(0.01f, value); } }
        }

        public float FarPlane
        {
            get { lock (this) { return farPlane; } }
            set { lock (this) { farPlane = Math.Max(nearPlane + 1.0f, value); } }
        }

        public Camera(Vector3 position, Vector3 target, Vector3 up, float aspectRatio)
        {
            this.position = position;
            this.target = target;
            this.up = Vector3.Normalize(up);
            this.aspectRatio = aspectRatio;
            UpdateSphericalCoords();
        }

        // Copy constructor for thread safety
        public Camera(Camera other)
        {
            if (other == null)
                throw new ArgumentNullException(nameof(other));

            lock (other)
            {
                this.position = other.position;
                this.target = other.target;
                this.up = other.up;
                this.aspectRatio = other.aspectRatio;
                this.fov = other.fov;
                this.nearPlane = other.nearPlane;
                this.farPlane = other.farPlane;
                this.radius = other.radius;
                this.theta = other.theta;
                this.phi = other.phi;
            }
        }

        public Matrix4x4 ViewMatrix
        {
            get
            {
                lock (this)
                {
                    return Matrix4x4.CreateLookAt(position, target, up);
                }
            }
        }

        public Matrix4x4 ProjectionMatrix
        {
            get
            {
                lock (this)
                {
                    return Matrix4x4.CreatePerspectiveFieldOfView(fov, aspectRatio, nearPlane, farPlane);
                }
            }
        }

        // Custom Math.Clamp implementation for .NET 4.8.1
        private static float Clamp(float value, float min, float max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private void UpdateSphericalCoords()
        {
            Vector3 direction = position - target;
            radius = Math.Max(0.1f, direction.Length());

            if (radius > 0.001f)
            {
                phi = (float)Math.Acos(Clamp(direction.Y / radius, -1.0f, 1.0f));
                theta = (float)Math.Atan2(direction.Z, direction.X);
            }
            else
            {
                // Handle degenerate case
                phi = (float)Math.PI / 2.0f;
                theta = 0.0f;
            }
        }

        private void UpdateCartesianCoords()
        {
            float x = radius * (float)Math.Sin(phi) * (float)Math.Cos(theta);
            float y = radius * (float)Math.Cos(phi);
            float z = radius * (float)Math.Sin(phi) * (float)Math.Sin(theta);
            position = target + new Vector3(x, y, z);
        }

        public void Orbit(float deltaTheta, float deltaPhi)
        {
            lock (this)
            {
                theta += deltaTheta;
                phi -= deltaPhi;
                phi = Clamp(phi, 0.1f, (float)Math.PI - 0.1f); // Prevent gimbal lock
                UpdateCartesianCoords();
            }
        }

        public void Pan(float dx, float dy)
        {
            lock (this)
            {
                Vector3 direction = position - target;
                if (direction.LengthSquared() < 0.001f)
                {
                    // Handle degenerate case
                    direction = Vector3.UnitZ;
                }

                Vector3 right = Vector3.Normalize(Vector3.Cross(up, direction));

                // Handle case where up and direction are parallel
                if (right.LengthSquared() < 0.001f)
                {
                    right = Vector3.UnitX;
                }

                Vector3 localUp = Vector3.Normalize(Vector3.Cross(direction, right));

                float panScale = 0.001f * radius;
                Vector3 panVector = (right * -dx + localUp * dy) * panScale;

                position += panVector;
                target += panVector;
            }
        }

        public void Zoom(float delta)
        {
            lock (this)
            {
                radius += delta * radius;
                radius = Math.Max(0.1f, Math.Min(10000.0f, radius)); // Clamp to reasonable range
                UpdateCartesianCoords();
            }
        }

        // Safe method to get camera state
        public void GetCameraState(out Vector3 outPosition, out Vector3 outTarget, out Vector3 outUp, out float outAspectRatio)
        {
            lock (this)
            {
                outPosition = position;
                outTarget = target;
                outUp = up;
                outAspectRatio = aspectRatio;
            }
        }
    }
}