using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CTSegmenter
{
    public class Rotation3DControl : UserControl
    {
        // Rotation values in degrees
        private double _rotX = 0;

        private double _rotY = 0;
        private double _rotZ = 0;

        // Dragging state
        private bool _isDragging = false;

        private Point _lastMousePos;
        private RotationAxis _activeAxis = RotationAxis.None;

        // Appearance
        private readonly Color _xAxisColor = Color.Red;

        private readonly Color _yAxisColor = Color.Green;
        private readonly Color _zAxisColor = Color.Blue;
        private readonly int _axisLength = 60;
        private readonly int _handleRadius = 8;

        // Event to notify when rotation changes
        public event EventHandler<RotationChangedEventArgs> RotationChanged;

        public enum RotationAxis
        { None, X, Y, Z }

        public Rotation3DControl()
        {
            this.DoubleBuffered = true;
            this.Size = new Size(150, 150);
            this.BackColor = Color.FromArgb(64, 64, 64);

            this.MouseDown += Rotation3DControl_MouseDown;
            this.MouseMove += Rotation3DControl_MouseMove;
            this.MouseUp += Rotation3DControl_MouseUp;
        }

        public double RotationX
        {
            get => _rotX;
            set
            {
                if (_rotX != value)
                {
                    _rotX = value;
                    Invalidate();
                }
            }
        }

        public double RotationY
        {
            get => _rotY;
            set
            {
                if (_rotY != value)
                {
                    _rotY = value;
                    Invalidate();
                }
            }
        }

        public double RotationZ
        {
            get => _rotZ;
            set
            {
                if (_rotZ != value)
                {
                    _rotZ = value;
                    Invalidate();
                }
            }
        }

        private void Rotation3DControl_MouseDown(object sender, MouseEventArgs e)
        {
            Point center = new Point(Width / 2, Height / 2);

            // Check if clicking on X axis handle
            PointF xHandle = GetTransformedPoint(_axisLength, 0, 0);
            if (Distance(new PointF(e.X, e.Y), new PointF(xHandle.X + center.X, xHandle.Y + center.Y)) <= _handleRadius)
            {
                _activeAxis = RotationAxis.X;
                _isDragging = true;
                _lastMousePos = e.Location;
                return;
            }

            // Check if clicking on Y axis handle
            PointF yHandle = GetTransformedPoint(0, _axisLength, 0);
            if (Distance(new PointF(e.X, e.Y), new PointF(yHandle.X + center.X, yHandle.Y + center.Y)) <= _handleRadius)
            {
                _activeAxis = RotationAxis.Y;
                _isDragging = true;
                _lastMousePos = e.Location;
                return;
            }

            // Check if clicking on Z axis handle
            PointF zHandle = GetTransformedPoint(0, 0, _axisLength);
            if (Distance(new PointF(e.X, e.Y), new PointF(zHandle.X + center.X, zHandle.Y + center.Y)) <= _handleRadius)
            {
                _activeAxis = RotationAxis.Z;
                _isDragging = true;
                _lastMousePos = e.Location;
                return;
            }

            // If clicking anywhere else on the control, allow rotating the entire view
            _activeAxis = RotationAxis.None;
            _isDragging = true;
            _lastMousePos = e.Location;
        }

        private void Rotation3DControl_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDragging) return;

            int dx = e.X - _lastMousePos.X;
            int dy = e.Y - _lastMousePos.Y;

            switch (_activeAxis)
            {
                case RotationAxis.X:
                    _rotX = (_rotX + dy) % 360;
                    break;

                case RotationAxis.Y:
                    _rotY = (_rotY + dx) % 360;
                    break;

                case RotationAxis.Z:
                    _rotZ = (_rotZ + Math.Sqrt(dx * dx + dy * dy) * Math.Sign(dx)) % 360;
                    break;

                case RotationAxis.None:
                    // Rotate around X and Y based on mouse movement
                    _rotY = (_rotY + dx * 0.5) % 360;
                    _rotX = (_rotX + dy * 0.5) % 360;
                    break;
            }

            // Normalize rotations to -180 to 180 range
            _rotX = NormalizeAngle(_rotX);
            _rotY = NormalizeAngle(_rotY);
            _rotZ = NormalizeAngle(_rotZ);

            _lastMousePos = e.Location;

            // Notify about rotation changes
            RotationChanged?.Invoke(this, new RotationChangedEventArgs(_rotX, _rotY, _rotZ));

            Invalidate();
        }

        private void Rotation3DControl_MouseUp(object sender, MouseEventArgs e)
        {
            _isDragging = false;
            _activeAxis = RotationAxis.None;
        }

        private double NormalizeAngle(double angle)
        {
            while (angle > 180) angle -= 360;
            while (angle < -180) angle += 360;
            return angle;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

            // Calculate center of control
            Point center = new Point(Width / 2, Height / 2);

            // Draw a sphere representing the object
            using (Brush sphereBrush = new SolidBrush(Color.FromArgb(100, 180, 180, 180)))
            {
                int sphereRadius = Math.Min(Width, Height) / 3;
                g.FillEllipse(sphereBrush, center.X - sphereRadius, center.Y - sphereRadius,
                              sphereRadius * 2, sphereRadius * 2);
            }

            // Draw axes
            DrawAxis(g, center, _axisLength, 0, 0, _xAxisColor, "X");
            DrawAxis(g, center, 0, _axisLength, 0, _yAxisColor, "Y");
            DrawAxis(g, center, 0, 0, _axisLength, _zAxisColor, "Z");

            // Draw rotation values
            using (Font font = new Font("Arial", 8))
            using (Brush textBrush = new SolidBrush(Color.White))
            {
                string rotInfo = $"X: {_rotX:0.0}° Y: {_rotY:0.0}° Z: {_rotZ:0.0}°";
                g.DrawString(rotInfo, font, textBrush, 5, Height - 20);
            }
        }

        private void DrawAxis(Graphics g, Point center, float x, float y, float z, Color color, string label)
        {
            // Transform 3D point based on current rotation
            PointF transformedPoint = GetTransformedPoint(x, y, z);

            // Scale for drawing
            float drawX = transformedPoint.X + center.X;
            float drawY = transformedPoint.Y + center.Y;

            // Draw axis line
            using (Pen axisPen = new Pen(color, 2))
            {
                g.DrawLine(axisPen, center.X, center.Y, drawX, drawY);
            }

            // Draw handle at end of axis
            using (Brush handleBrush = new SolidBrush(color))
            {
                g.FillEllipse(handleBrush, drawX - _handleRadius, drawY - _handleRadius,
                             _handleRadius * 2, _handleRadius * 2);
            }

            // Draw axis label
            using (Font labelFont = new Font("Arial", 9, FontStyle.Bold))
            using (Brush labelBrush = new SolidBrush(color))
            {
                g.DrawString(label, labelFont, labelBrush, drawX + 5, drawY - 8);
            }
        }

        // Transform a 3D point based on the current rotation values
        private PointF GetTransformedPoint(float x, float y, float z)
        {
            // Convert degrees to radians
            double radX = _rotX * Math.PI / 180.0;
            double radY = _rotY * Math.PI / 180.0;
            double radZ = _rotZ * Math.PI / 180.0;

            // Apply rotations in order: Z, Y, X
            // Rotate around Z
            double tempX = x * Math.Cos(radZ) - y * Math.Sin(radZ);
            double tempY = x * Math.Sin(radZ) + y * Math.Cos(radZ);
            x = (float)tempX;
            y = (float)tempY;

            // Rotate around Y
            tempX = x * Math.Cos(radY) + z * Math.Sin(radY);
            double tempZ = -x * Math.Sin(radY) + z * Math.Cos(radY);
            x = (float)tempX;
            z = (float)tempZ;

            // Rotate around X
            tempY = y * Math.Cos(radX) - z * Math.Sin(radX);
            tempZ = y * Math.Sin(radX) + z * Math.Cos(radX);
            y = (float)tempY;
            z = (float)tempZ;

            // Apply simple perspective projection
            float scale = 400.0f / (400.0f - z);
            x *= scale;
            y *= scale;

            return new PointF(x, y);
        }

        // Calculate distance between two points
        private float Distance(PointF p1, PointF p2)
        {
            float dx = p2.X - p1.X;
            float dy = p2.Y - p1.Y;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }
    }

    public class RotationChangedEventArgs : EventArgs
    {
        public double RotationX { get; }
        public double RotationY { get; }
        public double RotationZ { get; }

        public RotationChangedEventArgs(double x, double y, double z)
        {
            RotationX = x;
            RotationY = y;
            RotationZ = z;
        }
    }
}