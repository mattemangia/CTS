// Custom panel for 3D orthogonal view
using System;
using System.Drawing;
using System.Windows.Forms;
using CTSegmenter;

// Custom panel for 3D orthogonal view
public class OrthogonalViewPanel : Panel
{
    private int currentX, currentY, currentZ;
    private int width, height, depth;
    private readonly Color xyPlaneColor = Color.FromArgb(128, Color.Yellow);
    private readonly Color xzPlaneColor = Color.FromArgb(128, Color.Red);
    private readonly Color yzPlaneColor = Color.FromArgb(128, Color.Green);

    public OrthogonalViewPanel(MainForm parent)
    {
        this.BackColor = Color.Black;
        this.DoubleBuffered = true;
        this.SetStyle(ControlStyles.OptimizedDoubleBuffer |
                       ControlStyles.AllPaintingInWmPaint |
                       ControlStyles.UserPaint, true);
    }

    public void UpdateDimensions(int width, int height, int depth)
    {
        this.width = width;
        this.height = height;
        this.depth = depth;
        this.Invalidate();
    }

    public void UpdatePosition(int x, int y, int z)
    {
        this.currentX = x;
        this.currentY = y;
        this.currentZ = z;
        this.Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (width <= 0 || height <= 0 || depth <= 0)
            return;

        Graphics g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        // Calculate the scaling factor to fit the volume in the view
        float maxDim = Math.Max(Math.Max(width, height), depth);
        float scaleFactor = Math.Min(ClientSize.Width, ClientSize.Height) * 0.7f / maxDim;

        // Center point for the drawing
        float centerX = ClientSize.Width / 2f;
        float centerY = ClientSize.Height / 2f;

        // Isometric projection factors
        float isoX = 0.7f;
        float isoY = 0.4f;
        float isoZ = 0.7f;

        // Draw volume box
        DrawWireframeCube(g, centerX, centerY, width, height, depth, scaleFactor, isoX, isoY, isoZ);

        // Draw orthogonal planes at current positions
        DrawOrthogonalPlanes(g, centerX, centerY, scaleFactor, isoX, isoY, isoZ);

        // Draw text info in bottom corner
        using (Font font = new Font("Arial", 8))
        using (SolidBrush brush = new SolidBrush(Color.White))
        {
            g.DrawString($"Slice XY: {currentZ + 1}/{depth}", font, brush, 5, ClientSize.Height - 45);
            g.DrawString($"Row XZ: {currentY + 1}/{height}", font, brush, 5, ClientSize.Height - 30);
            g.DrawString($"Column YZ: {currentX + 1}/{width}", font, brush, 5, ClientSize.Height - 15);
        }
    }

    private void DrawWireframeCube(Graphics g, float centerX, float centerY,
                                  int width, int height, int depth,
                                  float scale, float isoX, float isoY, float isoZ)
    {
        // Calculate the corners of the cube in 3D space
        PointF[] corners = new PointF[8];

        // Transform 3D coordinates to 2D screen coordinates
        corners[0] = TransformPoint(0, 0, 0, centerX, centerY, scale, isoX, isoY, isoZ);
        corners[1] = TransformPoint(width, 0, 0, centerX, centerY, scale, isoX, isoY, isoZ);
        corners[2] = TransformPoint(width, height, 0, centerX, centerY, scale, isoX, isoY, isoZ);
        corners[3] = TransformPoint(0, height, 0, centerX, centerY, scale, isoX, isoY, isoZ);
        corners[4] = TransformPoint(0, 0, depth, centerX, centerY, scale, isoX, isoY, isoZ);
        corners[5] = TransformPoint(width, 0, depth, centerX, centerY, scale, isoX, isoY, isoZ);
        corners[6] = TransformPoint(width, height, depth, centerX, centerY, scale, isoX, isoY, isoZ);
        corners[7] = TransformPoint(0, height, depth, centerX, centerY, scale, isoX, isoY, isoZ);

        using (Pen pen = new Pen(Color.Gray, 1))
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

            // Connecting edges
            g.DrawLine(pen, corners[0], corners[4]);
            g.DrawLine(pen, corners[1], corners[5]);
            g.DrawLine(pen, corners[2], corners[6]);
            g.DrawLine(pen, corners[3], corners[7]);
        }
    }

    private void DrawOrthogonalPlanes(Graphics g, float centerX, float centerY,
                                     float scale, float isoX, float isoY, float isoZ)
    {
        // XY plane (current Z position)
        DrawPlane(g, xyPlaneColor, scale, isoX, isoY, isoZ, centerX, centerY, new[]
        {
            new Point3D(0, 0, currentZ),
            new Point3D(width, 0, currentZ),
            new Point3D(width, height, currentZ),
            new Point3D(0, height, currentZ)
        });

        // XZ plane (current Y position)
        DrawPlane(g, xzPlaneColor, scale, isoX, isoY, isoZ, centerX, centerY, new[]
        {
            new Point3D(0, currentY, 0),
            new Point3D(width, currentY, 0),
            new Point3D(width, currentY, depth),
            new Point3D(0, currentY, depth)
        });

        // YZ plane (current X position)
        DrawPlane(g, yzPlaneColor, scale, isoX, isoY, isoZ, centerX, centerY, new[]
        {
            new Point3D(currentX, 0, 0),
            new Point3D(currentX, height, 0),
            new Point3D(currentX, height, depth),
            new Point3D(currentX, 0, depth)
        });
    }

    private void DrawPlane(Graphics g, Color color, float scale,
                          float isoX, float isoY, float isoZ,
                          float centerX, float centerY, Point3D[] points)
    {
        PointF[] screenPoints = new PointF[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            screenPoints[i] = TransformPoint(
                points[i].X, points[i].Y, points[i].Z,
                centerX, centerY, scale, isoX, isoY, isoZ);
        }

        using (SolidBrush brush = new SolidBrush(color))
        {
            g.FillPolygon(brush, screenPoints);
        }

        using (Pen pen = new Pen(Color.FromArgb(200, Color.White), 1))
        {
            g.DrawPolygon(pen, screenPoints);
        }
    }

    private PointF TransformPoint(float x, float y, float z,
                                 float centerX, float centerY,
                                 float scale, float isoX, float isoY, float isoZ)
    {
        // Apply isometric projection
        float screenX = centerX + scale * (x * isoX - z * isoZ);
        float screenY = centerY + scale * (x * isoY + y * -1 + z * isoY);

        return new PointF(screenX, screenY);
    }

    private struct Point3D
    {
        public float X, Y, Z;

        public Point3D(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }
    }
}