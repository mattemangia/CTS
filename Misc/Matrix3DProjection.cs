//Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
using System;
using System.Drawing;

namespace CTS
{
    /// <summary>
    /// Helper class for 3D to 2D projections.
    /// </summary>
    public class Matrix3DProjection
    {
        private float _rotationX, _rotationY;
        private float _zoom;
        private PointF _pan;
        private int _width, _height, _depth;

        public Matrix3DProjection(int width, int height, int depth,
                                 float rotationX, float rotationY,
                                 float zoom, PointF pan)
        {
            _width = width;
            _height = height;
            _depth = depth;
            _rotationX = rotationX;
            _rotationY = rotationY;
            _zoom = zoom;
            _pan = pan;
        }

        public PointF Project(float x, float y, float z, int screenWidth, int screenHeight)
        {
            // Normalize to center
            float nx = x - _width / 2.0f;
            float ny = y - _height / 2.0f;
            float nz = z - _depth / 2.0f;

            // Apply rotation around X-axis
            float rotX = _rotationX * (float)Math.PI / 180.0f;
            float cosX = (float)Math.Cos(rotX);
            float sinX = (float)Math.Sin(rotX);

            float y1 = ny * cosX - nz * sinX;
            float z1 = ny * sinX + nz * cosX;

            // Apply rotation around Y-axis
            float rotY = _rotationY * (float)Math.PI / 180.0f;
            float cosY = (float)Math.Cos(rotY);
            float sinY = (float)Math.Sin(rotY);

            float x2 = nx * cosY + z1 * sinY;
            float z2 = -nx * sinY + z1 * cosY;

            // Apply zoom and pan
            float screenX = x2 * _zoom + _pan.X + screenWidth / 2.0f;
            float screenY = y1 * _zoom + _pan.Y + screenHeight / 2.0f;

            return new PointF(screenX, screenY);
        }
    }
}