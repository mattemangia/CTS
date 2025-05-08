using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CTS
{
    /// <summary>
    /// Class to generate custom icons for PoreNetworkModeling buttons
    /// </summary>
    public static class PoreNetworkButtonIcons
    {
        /// <summary>
        /// Creates a particle separation icon showing particles being divided
        /// </summary>
        /// <param name="size">Size of the icon</param>
        /// <param name="primaryColor">Main color for the icon</param>
        /// <returns>Bitmap containing the icon</returns>
        public static Bitmap CreateParticleSeparationIcon(Size size, Color primaryColor)
        {
            int width = size.Width;
            int height = size.Height;
            Bitmap bmp = new Bitmap(width, height);

            using (Graphics g = Graphics.FromImage(bmp))
            {
                // Set up high quality rendering
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                // Clear background (transparent)
                g.Clear(Color.Transparent);

                // Calculate sizes
                int padding = width / 10;
                int effectiveWidth = width - (2 * padding);
                int effectiveHeight = height - (2 * padding);

                // Draw multiple particles with separation lines
                using (Pen separationPen = new Pen(Color.FromArgb(180, Color.Black), 2))
                {
                    separationPen.DashStyle = DashStyle.Dash;

                    // Draw vertical separation line
                    g.DrawLine(separationPen,
                        new Point(width / 2, padding),
                        new Point(width / 2, height - padding));

                    // Draw horizontal separation line
                    g.DrawLine(separationPen,
                        new Point(padding, height / 2),
                        new Point(width - padding, height / 2));
                }

                // Draw particles in each quadrant with slightly different colors
                Color color1 = ChangeColorBrightness(primaryColor, 0.2f);
                Color color2 = ChangeColorBrightness(primaryColor, 0);
                Color color3 = ChangeColorBrightness(primaryColor, -0.1f);
                Color color4 = ChangeColorBrightness(primaryColor, -0.2f);

                // Top-left particle
                int particleSize = effectiveWidth / 3;
                int x1 = padding + effectiveWidth / 4 - particleSize / 2;
                int y1 = padding + effectiveHeight / 4 - particleSize / 2;
                using (SolidBrush brush = new SolidBrush(color1))
                {
                    g.FillEllipse(brush, x1, y1, particleSize, particleSize);
                }

                // Top-right particle
                int x2 = width - padding - effectiveWidth / 4 - particleSize / 2;
                int y2 = padding + effectiveHeight / 4 - particleSize / 2;
                using (SolidBrush brush = new SolidBrush(color2))
                {
                    g.FillEllipse(brush, x2, y2, particleSize, particleSize);
                }

                // Bottom-left particle
                int x3 = padding + effectiveWidth / 4 - particleSize / 2;
                int y3 = height - padding - effectiveHeight / 4 - particleSize / 2;
                using (SolidBrush brush = new SolidBrush(color3))
                {
                    g.FillEllipse(brush, x3, y3, particleSize, particleSize);
                }

                // Bottom-right particle
                int x4 = width - padding - effectiveWidth / 4 - particleSize / 2;
                int y4 = height - padding - effectiveHeight / 4 - particleSize / 2;
                using (SolidBrush brush = new SolidBrush(color4))
                {
                    g.FillEllipse(brush, x4, y4, particleSize, particleSize);
                }

                // Draw outlines
                using (Pen outlinePen = new Pen(Color.FromArgb(200, Color.Black), 1))
                {
                    g.DrawEllipse(outlinePen, x1, y1, particleSize, particleSize);
                    g.DrawEllipse(outlinePen, x2, y2, particleSize, particleSize);
                    g.DrawEllipse(outlinePen, x3, y3, particleSize, particleSize);
                    g.DrawEllipse(outlinePen, x4, y4, particleSize, particleSize);
                }
            }

            return bmp;
        }

        /// <summary>
        /// Creates a network generation icon showing pores connected by throats
        /// </summary>
        /// <param name="size">Size of the icon</param>
        /// <param name="primaryColor">Main color for the icon</param>
        /// <returns>Bitmap containing the icon</returns>
        public static Bitmap CreateNetworkGenerationIcon(Size size, Color primaryColor)
        {
            int width = size.Width;
            int height = size.Height;
            Bitmap bmp = new Bitmap(width, height);

            using (Graphics g = Graphics.FromImage(bmp))
            {
                // Set up high quality rendering
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                // Clear background (transparent)
                g.Clear(Color.Transparent);

                // Calculate sizes
                int padding = width / 10;
                int effectiveWidth = width - (2 * padding);
                int effectiveHeight = height - (2 * padding);

                // Define color schemes
                Color nodeColor = primaryColor;
                Color edgeColor = Color.FromArgb(180, 30, 30, 30);
                Color highlightColor = ChangeColorBrightness(primaryColor, 0.3f);

                // Define node positions (in a network pattern)
                Point[] nodes = new Point[]
                {
                    new Point(padding + effectiveWidth / 6, padding + effectiveHeight / 6),           // Top-left
                    new Point(padding + effectiveWidth / 2, padding + effectiveHeight / 5),           // Top-center
                    new Point(width - padding - effectiveWidth / 6, padding + effectiveHeight / 6),   // Top-right
                    new Point(padding + effectiveWidth / 7, padding + effectiveHeight / 2),           // Middle-left
                    new Point(padding + effectiveWidth / 2, padding + effectiveHeight / 2),           // Center
                    new Point(width - padding - effectiveWidth / 7, padding + effectiveHeight / 2),   // Middle-right
                    new Point(padding + effectiveWidth / 6, height - padding - effectiveHeight / 6),  // Bottom-left
                    new Point(padding + effectiveWidth / 2, height - padding - effectiveHeight / 5),  // Bottom-center
                    new Point(width - padding - effectiveWidth / 6, height - padding - effectiveHeight / 6) // Bottom-right
                };

                // Define node sizes (varied for visual interest)
                int[] nodeSizes = new int[]
                {
                    effectiveWidth / 8,
                    effectiveWidth / 7,
                    effectiveWidth / 8,
                    effectiveWidth / 7,
                    effectiveWidth / 6,
                    effectiveWidth / 7,
                    effectiveWidth / 8,
                    effectiveWidth / 7,
                    effectiveWidth / 8
                };

                // Define connections between nodes (index pairs)
                int[,] connections = new int[,]
                {
                    { 0, 1 }, { 1, 2 }, { 0, 3 }, { 1, 4 }, { 2, 5 },
                    { 3, 4 }, { 4, 5 }, { 3, 6 }, { 4, 7 }, { 5, 8 },
                    { 6, 7 }, { 7, 8 }, { 0, 4 }, { 2, 4 }, { 4, 6 }, { 4, 8 }
                };

                // Draw connections (edges) first so they appear behind nodes
                using (Pen connectionPen = new Pen(edgeColor, 2))
                {
                    for (int i = 0; i < connections.GetLength(0); i++)
                    {
                        int from = connections[i, 0];
                        int to = connections[i, 1];
                        g.DrawLine(connectionPen, nodes[from], nodes[to]);
                    }
                }

                // Draw nodes with varied sizes
                for (int i = 0; i < nodes.Length; i++)
                {
                    int nodeSize = nodeSizes[i];
                    int x = nodes[i].X - nodeSize / 2;
                    int y = nodes[i].Y - nodeSize / 2;

                    // Use different shades for visual depth
                    Color currentColor = (i == 4) ? highlightColor : nodeColor; // Center node is highlighted

                    // Draw node
                    using (SolidBrush brush = new SolidBrush(currentColor))
                    {
                        g.FillEllipse(brush, x, y, nodeSize, nodeSize);
                    }

                    // Draw outline
                    using (Pen outlinePen = new Pen(Color.FromArgb(180, Color.Black), 1))
                    {
                        g.DrawEllipse(outlinePen, x, y, nodeSize, nodeSize);
                    }
                }
            }

            return bmp;
        }

        /// <summary>
        /// Creates a permeability calculation icon showing flow through a porous medium
        /// </summary>
        /// <param name="size">Size of the icon</param>
        /// <param name="primaryColor">Main color for the icon</param>
        /// <returns>Bitmap containing the icon</returns>
        public static Bitmap CreatePermeabilityIcon(Size size, Color primaryColor)
        {
            int width = size.Width;
            int height = size.Height;
            Bitmap bmp = new Bitmap(width, height);

            using (Graphics g = Graphics.FromImage(bmp))
            {
                // Set up high quality rendering
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                // Clear background (transparent)
                g.Clear(Color.Transparent);

                // Calculate sizes
                int padding = width / 10;
                int effectiveWidth = width - (2 * padding);
                int effectiveHeight = height - (2 * padding);

                // Draw a container representing the porous medium
                Rectangle container = new Rectangle(
                    padding, padding,
                    effectiveWidth, effectiveHeight);

                // Create a gradient for the background to represent the pressure field
                using (LinearGradientBrush gradientBrush = new LinearGradientBrush(
                    container,
                    Color.FromArgb(40, Color.Blue),   // Low pressure (output)
                    Color.FromArgb(40, Color.Red),    // High pressure (input)
                    LinearGradientMode.Horizontal))
                {
                    g.FillRectangle(gradientBrush, container);
                }

                // Draw the container border
                using (Pen containerPen = new Pen(Color.FromArgb(180, Color.Black), 2))
                {
                    g.DrawRectangle(containerPen, container);
                }

                // Draw particles (pores) in the container
                Random random = new Random(42); // Fixed seed for reproducibility
                int particleCount = 12;

                for (int i = 0; i < particleCount; i++)
                {
                    // Randomize position but ensure particles are inside container
                    int particleSize = effectiveWidth / 10 + random.Next(effectiveWidth / 10);
                    int x = padding + random.Next(effectiveWidth - particleSize);
                    int y = padding + random.Next(effectiveHeight - particleSize);

                    // Vary color based on position (to match pressure gradient)
                    float position = (float)(x - padding) / effectiveWidth;
                    Color particleColor = GetGradientColor(Color.Blue, primaryColor, Color.Red, position);

                    // Draw particle
                    using (SolidBrush brush = new SolidBrush(particleColor))
                    {
                        g.FillEllipse(brush, x, y, particleSize, particleSize);
                    }

                    // Draw outline
                    using (Pen outlinePen = new Pen(Color.FromArgb(120, Color.Black), 1))
                    {
                        g.DrawEllipse(outlinePen, x, y, particleSize, particleSize);
                    }
                }

                // Draw flow arrows from left to right
                int arrowCount = 5;
                int arrowLength = effectiveWidth / 5;
                int arrowHeight = effectiveHeight / 12;

                using (Pen arrowPen = new Pen(Color.FromArgb(220, Color.White), 2))
                {
                    for (int i = 0; i < arrowCount; i++)
                    {
                        // Distribute arrows vertically
                        int y = padding + effectiveHeight / (arrowCount + 1) * (i + 1);

                        // Calculate arrow positions
                        int x1 = padding + effectiveWidth / 4;
                        int x2 = x1 + arrowLength;

                        // Draw arrow line
                        g.DrawLine(arrowPen, x1, y, x2, y);

                        // Draw arrowhead
                        Point[] arrowHead = new Point[]
                        {
                            new Point(x2, y),
                            new Point(x2 - arrowHeight, y - arrowHeight / 2),
                            new Point(x2 - arrowHeight, y + arrowHeight / 2)
                        };

                        g.FillPolygon(Brushes.White, arrowHead);
                    }
                }
            }

            return bmp;
        }

        /// <summary>
        /// Creates a tortuosity icon showing winding paths through a medium
        /// </summary>
        /// <param name="size">Size of the icon</param>
        /// <param name="primaryColor">Main color for the icon</param>
        /// <returns>Bitmap containing the icon</returns>
        public static Bitmap CreateTortuosityIcon(Size size, Color primaryColor)
        {
            int width = size.Width;
            int height = size.Height;
            Bitmap bmp = new Bitmap(width, height);

            using (Graphics g = Graphics.FromImage(bmp))
            {
                // Set up high quality rendering
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                // Clear background (transparent)
                g.Clear(Color.Transparent);

                // Calculate sizes
                int padding = width / 10;
                int effectiveWidth = width - (2 * padding);
                int effectiveHeight = height - (2 * padding);

                // Draw background container representing medium
                Rectangle container = new Rectangle(
                    padding, padding,
                    effectiveWidth, effectiveHeight);

                // Fill with a light color
                using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(30, primaryColor)))
                {
                    g.FillRectangle(bgBrush, container);
                }

                // Draw the container border
                using (Pen containerPen = new Pen(Color.FromArgb(100, Color.Black), 1))
                {
                    g.DrawRectangle(containerPen, container);
                }

                // Draw obstacles (particles) in the container
                Random random = new Random(123); // Fixed seed for reproducibility
                int particleCount = 8;

                List<Rectangle> particles = new List<Rectangle>();

                for (int i = 0; i < particleCount; i++)
                {
                    // Randomize position but ensure particles are inside container
                    int particleSize = effectiveWidth / 7 + random.Next(effectiveWidth / 14);
                    int x = padding + random.Next(effectiveWidth - particleSize);
                    int y = padding + random.Next(effectiveHeight - particleSize);

                    Rectangle particle = new Rectangle(x, y, particleSize, particleSize);
                    particles.Add(particle);

                    // Draw particle
                    using (SolidBrush brush = new SolidBrush(Color.FromArgb(180, primaryColor)))
                    {
                        g.FillEllipse(brush, particle);
                    }

                    // Draw outline
                    using (Pen outlinePen = new Pen(Color.FromArgb(120, Color.Black), 1))
                    {
                        g.DrawEllipse(outlinePen, particle);
                    }
                }

                // Draw winding flow path around obstacles
                // Create a path that winds around the obstacles from left to right
                using (GraphicsPath tortuousPath = new GraphicsPath())
                {
                    // Start point on the left
                    Point startPoint = new Point(padding, padding + effectiveHeight / 2);

                    // End point on the right
                    Point endPoint = new Point(padding + effectiveWidth, padding + effectiveHeight / 2);

                    // Control points for the path (create a winding route)
                    Point[] pathPoints = new Point[]
                    {
                        startPoint,
                        new Point(padding + effectiveWidth / 6, padding + effectiveHeight / 4),
                        new Point(padding + effectiveWidth / 3, padding + effectiveHeight * 3 / 4),
                        new Point(padding + effectiveWidth / 2, padding + effectiveHeight / 3),
                        new Point(padding + effectiveWidth * 2 / 3, padding + effectiveHeight * 2 / 3),
                        new Point(padding + effectiveWidth * 5 / 6, padding + effectiveHeight / 4),
                        endPoint
                    };

                    // Add to path
                    tortuousPath.AddLines(pathPoints);

                    // Draw the path with a gradient color to show flow direction
                    using (LinearGradientBrush pathBrush = new LinearGradientBrush(
                        container,
                        Color.Blue,   // Start color
                        Color.Red,    // End color
                        LinearGradientMode.Horizontal))
                    {
                        using (Pen pathPen = new Pen(pathBrush, 3))
                        {
                            pathPen.StartCap = LineCap.RoundAnchor;
                            pathPen.EndCap = LineCap.ArrowAnchor;
                            pathPen.DashStyle = DashStyle.Dash;

                            // Draw the tortuous path
                            g.DrawPath(pathPen, tortuousPath);
                        }
                    }

                    // Draw a straight line to compare with the tortuous path
                    using (Pen straightPen = new Pen(Color.FromArgb(100, Color.Black), 1))
                    {
                        straightPen.DashStyle = DashStyle.Dot;
                        g.DrawLine(straightPen, startPoint, endPoint);
                    }
                }

                // Add tau symbol (τ) to indicate tortuosity
                using (Font symbolFont = new Font("Arial", effectiveWidth / 6, FontStyle.Bold))
                {
                    using (SolidBrush textBrush = new SolidBrush(Color.FromArgb(180, Color.Black)))
                    {
                        // Position in bottom right
                        g.DrawString("τ", symbolFont, textBrush,
                            padding + effectiveWidth * 3 / 4,
                            padding + effectiveHeight * 3 / 4);
                    }
                }
            }

            return bmp;
        }

        /// <summary>
        /// Helper method to change the brightness of a color
        /// </summary>
        /// <param name="color">Base color</param>
        /// <param name="correctionFactor">Correction factor (-1 to 1)</param>
        /// <returns>Adjusted color</returns>
        private static Color ChangeColorBrightness(Color color, float correctionFactor)
        {
            float red = color.R;
            float green = color.G;
            float blue = color.B;

            if (correctionFactor < 0)
            {
                correctionFactor = 1 + correctionFactor;
                red *= correctionFactor;
                green *= correctionFactor;
                blue *= correctionFactor;
            }
            else
            {
                red = (255 - red) * correctionFactor + red;
                green = (255 - green) * correctionFactor + green;
                blue = (255 - blue) * correctionFactor + blue;
            }

            return Color.FromArgb(color.A, (int)red, (int)green, (int)blue);
        }

        /// <summary>
        /// Helper method to get a color along a three-color gradient
        /// </summary>
        /// <param name="startColor">Starting color</param>
        /// <param name="middleColor">Middle color</param>
        /// <param name="endColor">End color</param>
        /// <param name="position">Position (0.0 - 1.0)</param>
        /// <returns>Color at the specified position</returns>
        private static Color GetGradientColor(Color startColor, Color middleColor, Color endColor, float position)
        {
            if (position <= 0.5f)
            {
                // Interpolate between start and middle color
                float adjustedPosition = position * 2; // Scale 0-0.5 to 0-1
                return InterpolateColor(startColor, middleColor, adjustedPosition);
            }
            else
            {
                // Interpolate between middle and end color
                float adjustedPosition = (position - 0.5f) * 2; // Scale 0.5-1 to 0-1
                return InterpolateColor(middleColor, endColor, adjustedPosition);
            }
        }

        /// <summary>
        /// Helper method to interpolate between two colors
        /// </summary>
        /// <param name="color1">First color</param>
        /// <param name="color2">Second color</param>
        /// <param name="amount">Amount (0.0 - 1.0)</param>
        /// <returns>Interpolated color</returns>
        private static Color InterpolateColor(Color color1, Color color2, float amount)
        {
            int r = (int)(color1.R + amount * (color2.R - color1.R));
            int g = (int)(color1.G + amount * (color2.G - color1.G));
            int b = (int)(color1.B + amount * (color2.B - color1.B));
            int a = (int)(color1.A + amount * (color2.A - color1.A));

            r = Math.Max(0, Math.Min(255, r));
            g = Math.Max(0, Math.Min(255, g));
            b = Math.Max(0, Math.Min(255, b));
            a = Math.Max(0, Math.Min(255, a));

            return Color.FromArgb(a, r, g, b);
        }
    }
}