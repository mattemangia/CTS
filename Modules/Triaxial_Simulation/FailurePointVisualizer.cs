using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;

namespace CTS
{
    public class FailurePointVisualizer
    {
        public enum ColorMapMode { Damage, Stress, Strain }
        private enum SliceDirection { X, Y, Z }

        public struct Point3D
        {
            public int X, Y, Z;
            public Point3D(int x, int y, int z) { X = x; Y = y; Z = z; }
        }

        private readonly int _width, _height, _depth;
        private readonly byte _materialId;
        private ILabelVolumeData _labelData;
        private double[,,] _damageData;
        private float[,,] _stressData;
        private float[,,] _strainData;

        private bool _failureDetected;
        private Point3D _failurePoint;

        private float _rotationX, _rotationY, _zoom;
        private PointF _pan;

        public FailurePointVisualizer(int width, int height, int depth, byte materialId)
        {
            _width = width; _height = height; _depth = depth;
            _materialId = materialId;

            _failureDetected = false;
            _failurePoint = new Point3D(-1, -1, -1);

            _rotationX = _rotationY = 30f;
            _zoom = 1f;
            _pan = new PointF(0, 0);
        }

        // 2-arg overload so you can call SetData(labels, damageData)
        public void SetData(ILabelVolumeData labels, double[,,] damage)
        {
            _labelData = labels;
            _damageData = damage;
            _stressData = null;
            _strainData = null;
        }

        // 3-arg overload used when you have damage+strain
        public void SetData(ILabelVolumeData labels, double[,,] damage, float[,,] strain)
        {
            _labelData = labels;
            _damageData = damage;
            _stressData = null;
            _strainData = strain;
        }

        // 4-arg overload used when you have damage+stress+strain
        public void SetData(ILabelVolumeData labels, double[,,] damage, float[,,] stress, float[,,] strain)
        {
            _labelData = labels;
            _damageData = damage;
            _stressData = stress;
            _strainData = strain;
        }

        public void SetFailurePoint(bool detected, Point3D pt)
        {
            _failureDetected = detected;
            _failurePoint = detected ? pt : new Point3D(-1, -1, -1);
        }

        public void SetViewParameters(float rotationX, float rotationY, float zoom, PointF pan)
        {
            _rotationX = rotationX;
            _rotationY = rotationY;
            _zoom = zoom;
            _pan = pan;
        }

        public Bitmap CreateVisualization(int totalWidth, int totalHeight, ColorMapMode mode)
        {
            var bmp = new Bitmap(totalWidth, totalHeight);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                g.Clear(Color.FromArgb(20, 20, 20));

                int quadW = totalWidth / 2;
                int quadH = totalHeight / 2;

                DrawSliceView(g, new Rectangle(0, 0, quadW, quadH), SliceDirection.Z, mode, "Top (XY)");
                DrawSliceView(g, new Rectangle(quadW, 0, quadW, quadH), SliceDirection.Y, mode, "Front (XZ)");
                DrawSliceView(g, new Rectangle(0, quadH, quadW, quadH), SliceDirection.X, mode, "Side (YZ)");
                DrawIso3DView(g, new Rectangle(quadW, quadH, quadW, quadH));

                int cbW = Math.Min(200, totalWidth / 5);
                int cbH = quadH * 2 - 40;
                var cbRect = new Rectangle(totalWidth - cbW - 20, 20, cbW, cbH);
                DrawColorbar(g, cbRect, mode);
            }
            return bmp;
        }

        private void DrawSliceView(Graphics g, Rectangle r, SliceDirection dir, ColorMapMode mode, string title)
        {
            // background, border, title
            using (var bg = new SolidBrush(Color.FromArgb(40, 40, 40))) g.FillRectangle(bg, r);
            using (var bd = new Pen(Color.FromArgb(100, 100, 100))) g.DrawRectangle(bd, r);
            using (var f = new Font("Segoe UI", 10, FontStyle.Bold))
            using (var sb = new SolidBrush(Color.White))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center })
                g.DrawString(title, f, sb, new RectangleF(r.X, r.Y + 5, r.Width, 20), sf);

            if (_labelData == null || _damageData == null)
            {
                using (var f2 = new Font("Segoe UI", 9))
                using (var sb2 = new SolidBrush(Color.White))
                using (var sf2 = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("No data", f2, sb2, r, sf2);
                return;
            }

            // choose slice index based on the failure point location if available
            int idx;
            if (dir == SliceDirection.Z)
                idx = _failureDetected && InRange(_failurePoint.Z, 0, _depth) ? _failurePoint.Z : _depth / 2;
            else if (dir == SliceDirection.Y)
                idx = _failureDetected && InRange(_failurePoint.Y, 0, _height) ? _failurePoint.Y : _height / 2;
            else
                idx = _failureDetected && InRange(_failurePoint.X, 0, _width) ? _failurePoint.X : _width / 2;

            int nx = dir == SliceDirection.X ? _height : _width;
            int ny = dir == SliceDirection.Z ? _height : _depth;
            float cw = r.Width / (float)nx;
            float ch = (r.Height - 25) / (float)ny;

            // pick data array based on selected mode
            Func<int, int, int, double> getValue = (x, y, z) => _damageData[x, y, z];
            if (mode == ColorMapMode.Stress && _stressData != null)
                getValue = (x, y, z) => _stressData[x, y, z];
            else if (mode == ColorMapMode.Strain && _strainData != null)
                getValue = (x, y, z) => _strainData[x, y, z];

            // find min/max over material voxels
            double minV = double.MaxValue, maxV = double.MinValue;
            for (int x = 0; x < _width; x++)
                for (int y = 0; y < _height; y++)
                    for (int z = 0; z < _depth; z++)
                        if (_labelData[x, y, z] == _materialId)
                        {
                            double v = getValue(x, y, z);
                            if (v < minV) minV = v;
                            if (v > maxV) maxV = v;
                        }
            if (minV > maxV) { minV = 0; maxV = 1; }

            // draw cells
            for (int i = 0; i < nx; i++)
                for (int j = 0; j < ny; j++)
                {
                    int x, y, z;
                    if (dir == SliceDirection.Z) { x = i; y = j; z = idx; }
                    else if (dir == SliceDirection.Y) { x = i; y = idx; z = j; }
                    else { x = idx; y = i; z = j; }

                    // Ensure coordinates are within bounds
                    if (x >= 0 && x < _width && y >= 0 && y < _height && z >= 0 && z < _depth)
                    {
                        double v = _labelData[x, y, z] == _materialId ? getValue(x, y, z) : minV;
                        double t = (v - minV) / (maxV - minV);
                        if (t < 0) t = 0; if (t > 1) t = 1;
                        Color col = Color.FromArgb((int)(t * 255), 0, (int)((1 - t) * 255));

                        float px = r.X + i * cw;
                        float py = r.Y + 25 + j * ch;
                        using (var b = new SolidBrush(col)) g.FillRectangle(b, px, py, cw + 1, ch + 1);
                    }
                }

            // failure dot - draw ONLY if failure is detected and the slice contains the failure point
            if (_failureDetected)
            {
                int fx, fy;
                bool isFailureSlice = false;

                if (dir == SliceDirection.Z && _failurePoint.Z == idx)
                {
                    fx = _failurePoint.X;
                    fy = _failurePoint.Y;
                    isFailureSlice = true;
                }
                else if (dir == SliceDirection.Y && _failurePoint.Y == idx)
                {
                    fx = _failurePoint.X;
                    fy = _failurePoint.Z;
                    isFailureSlice = true;
                }
                else if (dir == SliceDirection.X && _failurePoint.X == idx)
                {
                    fx = _failurePoint.Y;
                    fy = _failurePoint.Z;
                    isFailureSlice = true;
                }
                else
                {
                    fx = fy = 0;
                    isFailureSlice = false;
                }

                if (isFailureSlice && fx >= 0 && fx < nx && fy >= 0 && fy < ny)
                {
                    float px = r.X + fx * cw;
                    float py = r.Y + 25 + fy * ch;

                    // Draw a more visible failure marker
                    using (var br = new SolidBrush(Color.Red))
                    {
                        g.FillEllipse(br, px - 6, py - 6, 12, 12);
                    }
                    using (var pen = new Pen(Color.White, 1.5f))
                    {
                        g.DrawEllipse(pen, px - 6, py - 6, 12, 12);
                    }

                    // Add a "Failure" label
                    using (var f3 = new Font("Segoe UI", 8, FontStyle.Bold))
                    using (var sb3 = new SolidBrush(Color.White))
                    using (var bgBrush = new SolidBrush(Color.FromArgb(180, 0, 0, 0)))
                    {
                        string label = "Failure";
                        SizeF labelSize = g.MeasureString(label, f3);
                        g.FillRectangle(bgBrush, px + 8, py - labelSize.Height / 2, labelSize.Width, labelSize.Height);
                        g.DrawString(label, f3, sb3, px + 8, py - labelSize.Height / 2);
                    }
                }
            }
        }

        private void DrawIso3DView(Graphics g, Rectangle r)
        {
            using (var bg = new SolidBrush(Color.FromArgb(40, 40, 40))) g.FillRectangle(bg, r);
            using (var bd = new Pen(Color.FromArgb(100, 100, 100))) g.DrawRectangle(bd, r);
            using (var f = new Font("Segoe UI", 10, FontStyle.Bold))
            using (var sb = new SolidBrush(Color.White))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center })
                g.DrawString("3D Volume", f, sb, new RectangleF(r.X, r.Y + 5, r.Width, 20), sf);

            if (_labelData == null || _damageData == null)
            {
                using (var f2 = new Font("Segoe UI", 9))
                using (var sb2 = new SolidBrush(Color.White))
                using (var sf2 = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("No data", f2, sb2, r, sf2);
                return;
            }

            // Apply rotation based on view parameters
            float angleX = _rotationX * (float)Math.PI / 180.0f;
            float angleY = _rotationY * (float)Math.PI / 180.0f;

            // Calculate scaling factors for isometric projection
            float scaleX = r.Width / (float)(_width + _depth) * _zoom;
            float scaleY = (r.Height - 25) / (float)(_height + _depth) * _zoom;
            float scale = Math.Min(scaleX, scaleY);

            // Center point in the view
            float cx = r.X + r.Width / 2.0f + _pan.X;
            float cy = r.Y + r.Height / 2.0f + _pan.Y;

            // Create a matrix transformations for 3D rotations
            Matrix3D transform = new Matrix3D();
            transform.RotateX(angleX);
            transform.RotateY(angleY);

            // Project a 3D point to 2D screen coordinates
            Func<float, float, float, PointF> Project = (float x, float y, float z) =>
            {
                // Center coordinates to origin
                float fx = x - _width / 2.0f;
                float fy = y - _height / 2.0f;
                float fz = z - _depth / 2.0f;

                // Apply 3D transformation
                Vector3 v = transform.Transform(new Vector3(fx, fy, fz));

                // Project to 2D with scaling
                return new PointF(
                    cx + v.X * scale,
                    cy + v.Y * scale
                );
            };

            // Create a list of vertices for the volume boundary
            var frontFaceVertices = new List<PointF>();
            var backFaceVertices = new List<PointF>();
            var rightFaceVertices = new List<PointF>();
            var leftFaceVertices = new List<PointF>();
            var topFaceVertices = new List<PointF>();
            var bottomFaceVertices = new List<PointF>();

            // Find corners of the volume (0,0,0) to (width,height,depth)
            var corners = new List<PointF>();
            corners.Add(Project(0, 0, 0));
            corners.Add(Project(_width, 0, 0));
            corners.Add(Project(_width, _height, 0));
            corners.Add(Project(0, _height, 0));
            corners.Add(Project(0, 0, _depth));
            corners.Add(Project(_width, 0, _depth));
            corners.Add(Project(_width, _height, _depth));
            corners.Add(Project(0, _height, _depth));

            // Draw volume as wireframe
            using (var pen = new Pen(Color.LightBlue, 1.5f))
            {
                // Bottom face
                g.DrawLine(pen, corners[0], corners[1]);
                g.DrawLine(pen, corners[1], corners[2]);
                g.DrawLine(pen, corners[2], corners[3]);
                g.DrawLine(pen, corners[3], corners[0]);

                // Top face
                g.DrawLine(pen, corners[4], corners[5]);
                g.DrawLine(pen, corners[5], corners[6]);
                g.DrawLine(pen, corners[6], corners[7]);
                g.DrawLine(pen, corners[7], corners[4]);

                // Side edges
                g.DrawLine(pen, corners[0], corners[4]);
                g.DrawLine(pen, corners[1], corners[5]);
                g.DrawLine(pen, corners[2], corners[6]);
                g.DrawLine(pen, corners[3], corners[7]);
            }

            // Draw a point cloud of the material voxels with the selected color mapping
            if (_failureDetected)
            {
                // Draw slice planes through the failure point
                int fx = _failurePoint.X;
                int fy = _failurePoint.Y;
                int fz = _failurePoint.Z;

                // Only draw them if the failure point is in a valid range
                if (fx >= 0 && fx < _width && fy >= 0 && fy < _height && fz >= 0 && fz < _depth)
                {
                    // XY Plane (at failure Z)
                    using (var slicePen = new Pen(Color.FromArgb(100, 255, 255, 255), 1))
                    {
                        var p1 = Project(0, 0, fz);
                        var p2 = Project(_width, 0, fz);
                        var p3 = Project(_width, _height, fz);
                        var p4 = Project(0, _height, fz);

                        g.DrawLine(slicePen, p1, p2);
                        g.DrawLine(slicePen, p2, p3);
                        g.DrawLine(slicePen, p3, p4);
                        g.DrawLine(slicePen, p4, p1);
                    }

                    // XZ Plane (at failure Y)
                    using (var slicePen = new Pen(Color.FromArgb(100, 255, 255, 255), 1))
                    {
                        var p1 = Project(0, fy, 0);
                        var p2 = Project(_width, fy, 0);
                        var p3 = Project(_width, fy, _depth);
                        var p4 = Project(0, fy, _depth);

                        g.DrawLine(slicePen, p1, p2);
                        g.DrawLine(slicePen, p2, p3);
                        g.DrawLine(slicePen, p3, p4);
                        g.DrawLine(slicePen, p4, p1);
                    }

                    // YZ Plane (at failure X)
                    using (var slicePen = new Pen(Color.FromArgb(100, 255, 255, 255), 1))
                    {
                        var p1 = Project(fx, 0, 0);
                        var p2 = Project(fx, _height, 0);
                        var p3 = Project(fx, _height, _depth);
                        var p4 = Project(fx, 0, _depth);

                        g.DrawLine(slicePen, p1, p2);
                        g.DrawLine(slicePen, p2, p3);
                        g.DrawLine(slicePen, p3, p4);
                        g.DrawLine(slicePen, p4, p1);
                    }

                    // Draw failure point with a cross-hair marker for better visibility
                    var fp = Project(fx, fy, fz);
                    using (var br = new SolidBrush(Color.Red))
                    {
                        g.FillEllipse(br, fp.X - 5, fp.Y - 5, 10, 10);
                    }
                    using (var pen = new Pen(Color.White, 1.5f))
                    {
                        g.DrawEllipse(pen, fp.X - 5, fp.Y - 5, 10, 10);
                        g.DrawLine(pen, fp.X - 8, fp.Y, fp.X + 8, fp.Y);
                        g.DrawLine(pen, fp.X, fp.Y - 8, fp.X, fp.Y + 8);
                    }

                    // Add failure coordinates label
                    using (var f3 = new Font("Segoe UI", 8, FontStyle.Bold))
                    using (var sb3 = new SolidBrush(Color.White))
                    using (var bgBrush = new SolidBrush(Color.FromArgb(180, 0, 0, 0)))
                    {
                        string label = $"Failure @ ({fx},{fy},{fz})";
                        SizeF labelSize = g.MeasureString(label, f3);
                        g.FillRectangle(bgBrush, fp.X + 8, fp.Y - labelSize.Height / 2, labelSize.Width, labelSize.Height);
                        g.DrawString(label, f3, sb3, fp.X + 8, fp.Y - labelSize.Height / 2);
                    }
                }
            }
        }

        private void DrawColorbar(Graphics g, Rectangle r, ColorMapMode mode)
        {
            if (_labelData == null || _damageData == null)
            {
                using (var bg = new SolidBrush(Color.FromArgb(40, 40, 40))) g.FillRectangle(bg, r);
                using (var pen = new Pen(Color.White)) g.DrawRectangle(pen, r);
                using (var f = new Font("Segoe UI", 9))
                using (var sb = new SolidBrush(Color.White))
                {
                    g.DrawString("No data", f, sb, r.X + 5, r.Y + r.Height / 2 - 6);
                }
                return;
            }

            // Find the appropriate data range based on mode
            double minV = double.MaxValue, maxV = double.MinValue;

            // Use appropriate data array based on mode
            Func<int, int, int, double> getValue = (x, y, z) => _damageData[x, y, z];
            if (mode == ColorMapMode.Stress && _stressData != null)
                getValue = (x, y, z) => _stressData[x, y, z];
            else if (mode == ColorMapMode.Strain && _strainData != null)
                getValue = (x, y, z) => _strainData[x, y, z];

            // Find min/max over material voxels
            for (int x = 0; x < _width; x++)
                for (int y = 0; y < _height; y++)
                    for (int z = 0; z < _depth; z++)
                        if (_labelData[x, y, z] == _materialId)
                        {
                            double v = getValue(x, y, z);
                            if (v < minV) minV = v;
                            if (v > maxV) maxV = v;
                        }
            if (minV > maxV) { minV = 0; maxV = 1; }

            // Draw colorbar background
            using (var bg = new SolidBrush(Color.FromArgb(50, 50, 50))) g.FillRectangle(bg, r);
            using (var pen = new Pen(Color.White)) g.DrawRectangle(pen, r);

            // Draw color gradient
            for (int i = 0; i < r.Height; i++)
            {
                float t = 1f - i / (float)(r.Height - 1);
                Color c = Color.FromArgb((int)(t * 255), 0, (int)((1 - t) * 255));
                using (var p = new Pen(c)) g.DrawLine(p, r.X, r.Y + i, r.Right, r.Y + i);
            }

            // Draw title and value labels
            using (var f = new Font("Segoe UI", 8))
            using (var fb = new Font("Segoe UI", 8, FontStyle.Bold))
            using (var sb = new SolidBrush(Color.White))
            {
                // Add a more descriptive title based on the mode
                string title = mode.ToString();
                switch (mode)
                {
                    case ColorMapMode.Damage:
                        title = "Damage Index";
                        break;
                    case ColorMapMode.Stress:
                        title = "Stress (MPa)";
                        break;
                    case ColorMapMode.Strain:
                        title = "Strain";
                        break;
                }

                g.DrawString(title, fb, sb, r.X, r.Y - 16);

                // Format values appropriately based on their range
                string maxStr, minStr;
                if (maxV < 0.01 || maxV > 999)
                    maxStr = maxV.ToString("E2");
                else
                    maxStr = maxV.ToString("F2");

                if (minV < 0.01 || minV > 999)
                    minStr = minV.ToString("E2");
                else
                    minStr = minV.ToString("F2");

                g.DrawString(maxStr, f, sb, r.X + r.Width + 4, r.Y - 4);
                g.DrawString(minStr, f, sb, r.X + r.Width + 4, r.Bottom - 10);

                // Add tick marks and intermediate values
                int numTicks = 4;
                for (int i = 1; i < numTicks; i++)
                {
                    float y = r.Y + (i * r.Height / numTicks);
                    double val = maxV - (i * (maxV - minV) / numTicks);
                    string valStr;

                    if (val < 0.01 || val > 999)
                        valStr = val.ToString("E2");
                    else
                        valStr = val.ToString("F2");

                    g.DrawLine(Pens.White, r.X - 3, y, r.X, y);
                    g.DrawString(valStr, f, sb, r.X + r.Width + 4, y - 6);
                }
            }

            // If failure detected, mark the failure threshold on the colorbar if applicable
            if (_failureDetected && mode == ColorMapMode.Damage)
            {
                double failureThreshold = 0.9; // This can be adjusted to the actual threshold used
                if (failureThreshold >= minV && failureThreshold <= maxV)
                {
                    float t = (float)((failureThreshold - minV) / (maxV - minV));
                    float y = r.Y + r.Height - (t * r.Height);

                    using (var pen = new Pen(Color.Red, 2))
                    {
                        g.DrawLine(pen, r.X - 5, y, r.Right + 5, y);
                    }

                    using (var f = new Font("Segoe UI", 8, FontStyle.Bold))
                    using (var sb = new SolidBrush(Color.Red))
                    {
                        g.DrawString("Failure Threshold", f, sb, r.X + r.Width + 10, y - 6);
                    }
                }
            }
        }

        private bool InRange(int v, int lo, int hi) { return v >= lo && v < hi; }

        // Helper class for 3D transformations
        private class Matrix3D
        {
            private float[] m = new float[16]; // 4x4 matrix in column-major order

            public Matrix3D()
            {
                // Initialize to identity matrix
                m[0] = 1; m[4] = 0; m[8] = 0; m[12] = 0;
                m[1] = 0; m[5] = 1; m[9] = 0; m[13] = 0;
                m[2] = 0; m[6] = 0; m[10] = 1; m[14] = 0;
                m[3] = 0; m[7] = 0; m[11] = 0; m[15] = 1;
            }

            public void RotateX(float angle)
            {
                float c = (float)Math.Cos(angle);
                float s = (float)Math.Sin(angle);

                float m1 = m[1], m5 = m[5], m9 = m[9], m13 = m[13];
                float m2 = m[2], m6 = m[6], m10 = m[10], m14 = m[14];

                m[1] = m1 * c + m2 * s;
                m[5] = m5 * c + m6 * s;
                m[9] = m9 * c + m10 * s;
                m[13] = m13 * c + m14 * s;

                m[2] = m2 * c - m1 * s;
                m[6] = m6 * c - m5 * s;
                m[10] = m10 * c - m9 * s;
                m[14] = m14 * c - m13 * s;
            }

            public void RotateY(float angle)
            {
                float c = (float)Math.Cos(angle);
                float s = (float)Math.Sin(angle);

                float m0 = m[0], m4 = m[4], m8 = m[8], m12 = m[12];
                float m2 = m[2], m6 = m[6], m10 = m[10], m14 = m[14];

                m[0] = m0 * c - m2 * s;
                m[4] = m4 * c - m6 * s;
                m[8] = m8 * c - m10 * s;
                m[12] = m12 * c - m14 * s;

                m[2] = m0 * s + m2 * c;
                m[6] = m4 * s + m6 * c;
                m[10] = m8 * s + m10 * c;
                m[14] = m12 * s + m14 * c;
            }

            public Vector3 Transform(Vector3 v)
            {
                return new Vector3(
                    m[0] * v.X + m[4] * v.Y + m[8] * v.Z + m[12],
                    m[1] * v.X + m[5] * v.Y + m[9] * v.Z + m[13],
                    m[2] * v.X + m[6] * v.Y + m[10] * v.Z + m[14]
                );
            }
        }

        // Simple 3D vector class for transformations
        private struct Vector3
        {
            public float X, Y, Z;

            public Vector3(float x, float y, float z)
            {
                X = x;
                Y = y;
                Z = z;
            }
        }
    }
}