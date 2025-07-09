//Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Windows.Forms;

namespace CTS.Modules.Simulation.NMR
{
    /// <summary>
    /// Provides plotting functionality for NMR simulation results
    /// </summary>
    public class NMRResultPlotter : IDisposable
    {
        private readonly Color[] _componentColors;
        private readonly Font _labelFont;
        private readonly Font _titleFont;
        private readonly Font _legendFont;
        private readonly Font _tickFont;
        private bool _disposed = false;

        public NMRResultPlotter()
        {
            // Initialize color palette for components (brighter colors for dark mode)
            _componentColors = new Color[]
            {
                Color.FromArgb(0, 180, 255),   // Bright blue
                Color.FromArgb(255, 100, 100), // Bright red
                Color.FromArgb(50, 255, 100),  // Bright green
                Color.FromArgb(255, 180, 0),   // Bright orange
                Color.FromArgb(180, 130, 255), // Bright purple
                Color.FromArgb(0, 255, 255),   // Bright cyan
                Color.FromArgb(255, 130, 255), // Bright magenta
                Color.FromArgb(255, 200, 0),   // Bright gold
                Color.FromArgb(150, 220, 255), // Light blue
                Color.FromArgb(255, 160, 120), // Light coral
                Color.FromArgb(140, 255, 140), // Light green
                Color.FromArgb(255, 220, 130), // Light orange
                Color.FromArgb(200, 170, 255), // Light purple
                Color.FromArgb(130, 255, 220), // Light teal
                Color.FromArgb(255, 200, 200)  // Light pink
            };

            _labelFont = new Font("Segoe UI", 9);
            _titleFont = new Font("Segoe UI", 11, FontStyle.Bold);
            _legendFont = new Font("Segoe UI", 8);
            _tickFont = new Font("Segoe UI", 8);
        }

        public Bitmap PlotDecayCurve(NMRSimulationResult result, Size imageSize, bool logScale = true, bool showComponents = true)
        {
            try
            {
                if (result == null || result.TimePoints == null || result.Magnetization == null || result.TimePoints.Length == 0)
                    return CreateErrorBitmap(imageSize, "No valid data to plot");

                // Use exact panel size, ensure minimum dimensions for stability
                int width = Math.Max(imageSize.Width, 300);
                int height = Math.Max(imageSize.Height, 200);

                var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);

                using (var g = Graphics.FromImage(bitmap))
                {
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                    g.Clear(Color.Black);

                    // Calculate proper margins to ensure tick labels are visible
                    int leftMargin = 80;  // Increased to accommodate Y-axis tick labels
                    int topMargin = 50;
                    int rightMargin = 40;
                    int bottomMargin = 90; // Increased to make room for X-axis labels + stats bar

                    var margins = new Padding(leftMargin, topMargin, rightMargin, bottomMargin);

                    var plotArea = new Rectangle(
                        margins.Left,
                        margins.Top,
                        bitmap.Width - margins.Left - margins.Right,
                        bitmap.Height - margins.Top - margins.Bottom
                    );

                    // Draw title
                    string title = "NMR Decay Curve";
                    if (result.UsedGPU)
                        title += " (GPU Accelerated)";

                    DrawTitle(g, title, new Rectangle(0, 0, bitmap.Width, margins.Top));

                    // Find data ranges for axes
                    double minTime = result.TimePoints.Min();
                    double maxTime = result.TimePoints.Max();
                    double maxMag = result.Magnetization.Max();
                    double minMag = logScale ? Math.Max(result.Magnetization.Min(), 0.001) : 0;

                    // Draw axes with labels and ticks
                    DrawAxesWithLabels(g, plotArea, logScale, minTime, maxTime, minMag, maxMag);

                    // Plot data
                    PlotDecayData(g, plotArea, result, logScale, showComponents, minTime, maxTime, minMag, maxMag);

                    // Draw legend
                    if (showComponents && result.FittedComponents != null && result.FittedComponents.Count > 0)
                    {
                        DrawDecayLegend(g, result, new Rectangle(plotArea.Right - 150, plotArea.Top + 10, 140, 180));
                    }

                    // Draw statistics - MOVED to avoid overlap with X-axis label
                    int statsBarY = bitmap.Height - 35; // Position it at the very bottom
                    DrawStatistics(g, result, new Rectangle(10, statsBarY, bitmap.Width - 20, 30));
                }

                return bitmap;
            }
            catch (OutOfMemoryException)
            {
                return CreateErrorBitmap(imageSize, "Error: Out of memory when creating plot");
            }
            catch (Exception ex)
            {
                return CreateErrorBitmap(imageSize, $"Error: {ex.Message}");
            }
        }

        public Bitmap PlotT2Distribution(NMRSimulationResult result, Size imageSize, bool logScale = true)
        {
            try
            {
                if (result == null || result.T2Values == null || result.T2Distribution == null || result.T2Values.Length == 0)
                    return CreateErrorBitmap(imageSize, "No valid T2 data to plot");

                // Use exact panel size, ensure minimum dimensions for stability
                int width = Math.Max(imageSize.Width, 300);
                int height = Math.Max(imageSize.Height, 200);

                var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);

                using (var g = Graphics.FromImage(bitmap))
                {
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                    g.Clear(Color.Black);

                    // Calculate proper margins to ensure tick labels are visible
                    int leftMargin = 80;  // Increased to accommodate Y-axis tick labels
                    int topMargin = 50;
                    int rightMargin = 40;
                    int bottomMargin = 90; // Increased to make room for X-axis labels + stats bar

                    var margins = new Padding(leftMargin, topMargin, rightMargin, bottomMargin);

                    var plotArea = new Rectangle(
                        margins.Left,
                        margins.Top,
                        bitmap.Width - margins.Left - margins.Right,
                        bitmap.Height - margins.Top - margins.Bottom
                    );

                    // Draw title
                    DrawTitle(g, "T2 Relaxation Distribution", new Rectangle(0, 0, bitmap.Width, margins.Top));

                    // Find data ranges for axes
                    double minT2 = result.T2Values.Min();
                    double maxT2 = result.T2Values.Max();
                    double maxAmplitude = result.T2Distribution.Max();
                    if (maxAmplitude <= 0) maxAmplitude = 1; // Prevent division by zero

                    // Draw axes with labels and ticks
                    DrawT2DistributionAxesWithLabels(g, plotArea, logScale, minT2, maxT2, maxAmplitude);

                    // Plot distribution
                    PlotT2DistributionData(g, plotArea, result, logScale, minT2, maxT2, maxAmplitude);

                    // Draw fitting parameters
                    DrawFittingParameters(g, result, new Rectangle(plotArea.Right - 200, plotArea.Top + 10, 180, 100));

                    // Draw statistics - MOVED to avoid overlap with X-axis label
                    int statsBarY = bitmap.Height - 35; // Position it at the very bottom
                    DrawT2Statistics(g, result, new Rectangle(10, statsBarY, bitmap.Width - 20, 30));
                }

                return bitmap;
            }
            catch (OutOfMemoryException)
            {
                return CreateErrorBitmap(new Size(400, 300), "Error: Out of memory when creating T2 plot");
            }
            catch (Exception ex)
            {
                return CreateErrorBitmap(imageSize, $"Error: {ex.Message}");
            }
        }

        public Bitmap PlotComponentsOverview(NMRSimulationResult result, Size imageSize)
        {
            try
            {
                if (result == null)
                    return CreateErrorBitmap(imageSize, "No simulation results available");

                // Use exact panel size, ensure minimum dimensions for stability
                int width = Math.Max(imageSize.Width, 600);
                int height = Math.Max(imageSize.Height, 450);

                var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);

                using (var g = Graphics.FromImage(bitmap))
                {
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                    g.Clear(Color.Black);

                    // Create subplots with proper spacing
                    int topMargin = 40;                      // Top margin for title
                    int infoBarHeight = 60;                  // Height of the info bar
                    int plotSpacing = 30;                    // Space between plots
                    int bottomMargin = 10;                   // Margin at bottom
                    int sideMargin = 20;                     // Side margins

                    // Calculate plot heights to ensure they fit properly
                    int plotHeight = (bitmap.Height - topMargin - infoBarHeight - plotSpacing - bottomMargin) / 2;

                    var topPlot = new Rectangle(
                        sideMargin,
                        topMargin,
                        bitmap.Width - (2 * sideMargin),
                        plotHeight
                    );

                    var bottomPlot = new Rectangle(
                        sideMargin,
                        topMargin + plotHeight + plotSpacing,
                        bitmap.Width - (2 * sideMargin),
                        plotHeight
                    );

                    var infoBar = new Rectangle(
                        sideMargin,
                        bitmap.Height - infoBarHeight - bottomMargin,
                        bitmap.Width - (2 * sideMargin),
                        infoBarHeight
                    );

                    // Draw title
                    DrawTitle(g, "NMR Components Overview", new Rectangle(0, 0, bitmap.Width, topMargin));

                    // Plot decay curve with tick labels
                    DrawSubplotDecay(g, topPlot, result);

                    // Plot T2 distribution with tick labels
                    DrawSubplotT2Distribution(g, bottomPlot, result);

                    // Draw comprehensive statistics
                    DrawComprehensiveStatistics(g, result, infoBar);
                }

                return bitmap;
            }
            catch (OutOfMemoryException)
            {
                return CreateErrorBitmap(new Size(400, 300), "Error: Out of memory when creating overview plot");
            }
            catch (Exception ex)
            {
                return CreateErrorBitmap(imageSize, $"Error: {ex.Message}");
            }
        }

        private Bitmap CreateErrorBitmap(Size size, string errorMessage)
        {
            // Create a bitmap with the exact size of the container
            int width = Math.Max(size.Width, 300);
            int height = Math.Max(size.Height, 200);

            var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);

            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Black);
                using (var brush = new SolidBrush(Color.Red))
                using (var font = new Font("Segoe UI", 10, FontStyle.Bold))
                {
                    var format = new StringFormat
                    {
                        Alignment = StringAlignment.Center,
                        LineAlignment = StringAlignment.Center
                    };
                    g.DrawString(errorMessage, font, brush, new Rectangle(0, 0, bitmap.Width, bitmap.Height), format);
                }
            }
            return bitmap;
        }

        private void DrawTitle(Graphics g, string title, Rectangle area)
        {
            var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };

            using (var brush = new SolidBrush(Color.White))
            {
                g.DrawString(title, _titleFont, brush, area, format);
            }
        }

        private void DrawAxesWithLabels(Graphics g, Rectangle plotArea, bool logScale, double minTime, double maxTime, double minMag, double maxMag)
        {
            // Draw grid first
            using (var gridPen = new Pen(Color.FromArgb(40, Color.Gray), 1))
            {
                gridPen.DashStyle = DashStyle.Dot;
                // X grid lines
                for (int i = 1; i <= 4; i++)
                {
                    int x = plotArea.Left + (plotArea.Width * i) / 5;
                    g.DrawLine(gridPen, x, plotArea.Top, x, plotArea.Bottom);
                }
                // Y grid lines
                for (int i = 1; i <= 4; i++)
                {
                    int y = plotArea.Bottom - (plotArea.Height * i) / 5;
                    g.DrawLine(gridPen, plotArea.Left, y, plotArea.Right, y);
                }
            }

            // Draw axes
            using (var pen = new Pen(Color.LightGray, 1))
            {
                // Draw X and Y axes
                g.DrawLine(pen, plotArea.Left, plotArea.Bottom, plotArea.Right, plotArea.Bottom);
                g.DrawLine(pen, plotArea.Left, plotArea.Bottom, plotArea.Left, plotArea.Top);
            }

            // Setup tick label formats
            var centerFormat = new StringFormat { Alignment = StringAlignment.Center };
            var rightFormat = new StringFormat { Alignment = StringAlignment.Far };

            using (var brush = new SolidBrush(Color.White))
            {
                // Draw X-axis tick marks and labels
                for (int i = 0; i <= 5; i++)
                {
                    int x = plotArea.Left + (plotArea.Width * i) / 5;
                    g.DrawLine(new Pen(Color.LightGray), x, plotArea.Bottom, x, plotArea.Bottom + 5);

                    // Calculate the actual value at this tick position
                    double timeValue;
                    if (logScale)
                    {
                        double logMin = Math.Log10(minTime);
                        double logMax = Math.Log10(maxTime);
                        double logVal = logMin + (logMax - logMin) * i / 5.0;
                        timeValue = Math.Pow(10, logVal);
                    }
                    else
                    {
                        timeValue = minTime + (maxTime - minTime) * i / 5.0;
                    }

                    // Format the tick label based on magnitude
                    string tickLabel;
                    if (timeValue < 0.1)
                        tickLabel = $"{timeValue:F3}";
                    else if (timeValue < 1)
                        tickLabel = $"{timeValue:F2}";
                    else if (timeValue < 10)
                        tickLabel = $"{timeValue:F1}";
                    else
                        tickLabel = $"{timeValue:F0}";

                    g.DrawString(tickLabel, _tickFont, brush, x, plotArea.Bottom + 8, centerFormat);
                }

                // Draw Y-axis tick marks and labels
                for (int i = 0; i <= 5; i++)
                {
                    int y = plotArea.Bottom - (plotArea.Height * i) / 5;
                    g.DrawLine(new Pen(Color.LightGray), plotArea.Left - 5, y, plotArea.Left, y);

                    // Calculate the actual value at this tick position
                    double magValue;
                    if (logScale)
                    {
                        double logMin = Math.Log10(minMag);
                        double logMax = Math.Log10(maxMag);
                        double logVal = logMin + (logMax - logMin) * i / 5.0;
                        magValue = Math.Pow(10, logVal);
                    }
                    else
                    {
                        magValue = minMag + (maxMag - minMag) * i / 5.0;
                    }

                    // Format the tick label based on magnitude
                    string tickLabel;
                    if (magValue < 0.001)
                        tickLabel = $"{magValue:E1}";
                    else if (magValue < 0.01)
                        tickLabel = $"{magValue:F4}";
                    else if (magValue < 0.1)
                        tickLabel = $"{magValue:F3}";
                    else if (magValue < 1)
                        tickLabel = $"{magValue:F2}";
                    else
                        tickLabel = $"{magValue:F1}";

                    g.DrawString(tickLabel, _tickFont, brush, plotArea.Left - 8, y, rightFormat);
                }

                // Draw X-axis label - placed well below tick labels
                g.DrawString("Time (ms)", _labelFont, brush,
                    plotArea.Left + plotArea.Width / 2 - 30,
                    plotArea.Bottom + 45);

                // Draw Y-axis label with vertical text
                using (var rotatedFont = new Font(_labelFont.FontFamily, _labelFont.Size, _labelFont.Style))
                {
                    // Save current state
                    var state = g.Save();

                    // Translate and rotate
                    g.TranslateTransform(plotArea.Left - 55, plotArea.Top + plotArea.Height / 2);
                    g.RotateTransform(-90);

                    // Draw rotated text
                    string yLabel = logScale ? "Magnetization (log)" : "Magnetization";
                    g.DrawString(yLabel, rotatedFont, brush, 0, 0, new StringFormat { Alignment = StringAlignment.Center });

                    // Restore state
                    g.Restore(state);
                }
            }
        }

        private void PlotDecayData(Graphics g, Rectangle plotArea, NMRSimulationResult result, bool logScale, bool showComponents, double minTime, double maxTime, double minMag, double maxMag)
        {
            if (result.TimePoints == null || result.Magnetization == null || result.TimePoints.Length == 0)
                return;

            // Plot components first (under the main curve) - with optimization for large datasets
            if (showComponents && result.FittedComponents != null && result.FittedComponents.Count > 0)
            {
                // Determine if we need to sample points for performance
                int skipFactor = result.TimePoints.Length > 1000 ? result.TimePoints.Length / 1000 : 1;

                for (int comp = 0; comp < Math.Min(result.FittedComponents.Count, _componentColors.Length); comp++)
                {
                    var component = result.FittedComponents[comp];
                    if (component.RelaxationTime <= 0 || component.Amplitude <= 0)
                        continue;

                    var compPoints = new List<PointF>();

                    for (int i = 0; i < result.TimePoints.Length; i += skipFactor)
                    {
                        double t = result.TimePoints[i];
                        double mag = component.Amplitude * Math.Exp(-t / component.RelaxationTime);

                        double x = logScale ? Math.Log10(Math.Max(t, 0.001)) : t;
                        double y = logScale ? Math.Log10(Math.Max(mag, minMag)) : mag;

                        float plotX = (float)(plotArea.Left + (x - (logScale ? Math.Log10(minTime) : minTime)) /
                                      ((logScale ? Math.Log10(maxTime) : maxTime) - (logScale ? Math.Log10(minTime) : minTime)) * plotArea.Width);
                        float plotY = (float)(plotArea.Bottom - (y - (logScale ? Math.Log10(minMag) : minMag)) /
                                      ((logScale ? Math.Log10(maxMag) : maxMag) - (logScale ? Math.Log10(minMag) : minMag)) * plotArea.Height);

                        // Ensure point is within plot area bounds
                        plotX = Math.Max(plotArea.Left, Math.Min(plotX, plotArea.Right));
                        plotY = Math.Max(plotArea.Top, Math.Min(plotY, plotArea.Bottom));

                        compPoints.Add(new PointF(plotX, plotY));
                    }

                    using (var pen = new Pen(_componentColors[comp % _componentColors.Length], 1))
                    {
                        pen.DashStyle = DashStyle.Dash;
                        if (compPoints.Count > 1)
                            g.DrawLines(pen, compPoints.ToArray());
                    }
                }
            }

            // Plot total magnetization with adaptive sampling for large datasets
            int mainSkipFactor = result.TimePoints.Length > 2000 ? result.TimePoints.Length / 2000 : 1;
            var points = new List<PointF>();

            for (int i = 0; i < result.TimePoints.Length; i += mainSkipFactor)
            {
                double x = logScale ? Math.Log10(Math.Max(result.TimePoints[i], 0.001)) : result.TimePoints[i];
                double y = logScale ? Math.Log10(Math.Max(result.Magnetization[i], minMag)) : result.Magnetization[i];

                float plotX = (float)(plotArea.Left + (x - (logScale ? Math.Log10(minTime) : minTime)) /
                          ((logScale ? Math.Log10(maxTime) : maxTime) - (logScale ? Math.Log10(minTime) : minTime)) * plotArea.Width);
                float plotY = (float)(plotArea.Bottom - (y - (logScale ? Math.Log10(minMag) : minMag)) /
                          ((logScale ? Math.Log10(maxMag) : maxMag) - (logScale ? Math.Log10(minMag) : minMag)) * plotArea.Height);

                // Ensure point is within plot area bounds
                plotX = Math.Max(plotArea.Left, Math.Min(plotX, plotArea.Right));
                plotY = Math.Max(plotArea.Top, Math.Min(plotY, plotArea.Bottom));

                points.Add(new PointF(plotX, plotY));
            }

            using (var pen = new Pen(Color.White, 2))
            {
                if (points.Count > 1)
                    g.DrawLines(pen, points.ToArray());
            }
        }

        private void DrawDecayLegend(Graphics g, NMRSimulationResult result, Rectangle area)
        {
            using (var brush = new SolidBrush(Color.FromArgb(40, Color.Gray)))
            {
                g.FillRectangle(brush, area);
            }

            using (var pen = new Pen(Color.DarkGray))
            {
                g.DrawRectangle(pen, area);
            }

            int y = area.Top + 5;

            // Total curve
            using (var pen = new Pen(Color.White, 2))
            {
                g.DrawLine(pen, area.Left + 5, y + 8, area.Left + 25, y + 8);
            }
            using (var brush = new SolidBrush(Color.White))
            {
                g.DrawString("Total", _legendFont, brush, area.Left + 30, y);
            }
            y += 15;

            // Component curves - limit to what fits in the area
            if (result.FittedComponents != null && result.FittedComponents.Count > 0)
            {
                int maxComponentsInLegend = Math.Min((area.Height - 30) / 15, 10);
                int compToShow = Math.Min(maxComponentsInLegend, result.FittedComponents.Count);

                for (int i = 0; i < compToShow; i++)
                {
                    using (var pen = new Pen(_componentColors[i % _componentColors.Length], 1))
                    {
                        pen.DashStyle = DashStyle.Dash;
                        g.DrawLine(pen, area.Left + 5, y + 8, area.Left + 25, y + 8);
                    }
                    using (var brush = new SolidBrush(Color.White))
                    {
                        g.DrawString($"T2={result.FittedComponents[i].RelaxationTime:F1}ms", _legendFont, brush, area.Left + 30, y);
                    }
                    y += 15;
                }

                if (result.FittedComponents.Count > compToShow)
                {
                    using (var brush = new SolidBrush(Color.White))
                    {
                        g.DrawString($"+{result.FittedComponents.Count - compToShow} more", _legendFont, brush, area.Left + 30, y);
                    }
                }
            }
        }

        private void DrawT2DistributionAxesWithLabels(Graphics g, Rectangle plotArea, bool logScale, double minT2, double maxT2, double maxAmplitude)
        {
            // Draw grid first
            using (var gridPen = new Pen(Color.FromArgb(40, Color.Gray), 1))
            {
                gridPen.DashStyle = DashStyle.Dot;
                // X grid lines
                for (int i = 1; i <= 4; i++)
                {
                    int x = plotArea.Left + (plotArea.Width * i) / 5;
                    g.DrawLine(gridPen, x, plotArea.Top, x, plotArea.Bottom);
                }
                // Y grid lines
                for (int i = 1; i <= 4; i++)
                {
                    int y = plotArea.Bottom - (plotArea.Height * i) / 5;
                    g.DrawLine(gridPen, plotArea.Left, y, plotArea.Right, y);
                }
            }

            // Draw axes
            using (var pen = new Pen(Color.LightGray, 1))
            {
                // Draw X and Y axes
                g.DrawLine(pen, plotArea.Left, plotArea.Bottom, plotArea.Right, plotArea.Bottom);
                g.DrawLine(pen, plotArea.Left, plotArea.Bottom, plotArea.Left, plotArea.Top);
            }

            // Setup tick label formats
            var centerFormat = new StringFormat { Alignment = StringAlignment.Center };
            var rightFormat = new StringFormat { Alignment = StringAlignment.Far };

            using (var brush = new SolidBrush(Color.White))
            {
                // Draw X-axis tick marks and labels
                for (int i = 0; i <= 5; i++)
                {
                    int x = plotArea.Left + (plotArea.Width * i) / 5;
                    g.DrawLine(new Pen(Color.LightGray), x, plotArea.Bottom, x, plotArea.Bottom + 5);

                    // Calculate the actual T2 value at this tick position
                    double t2Value;
                    if (logScale)
                    {
                        double logMin = Math.Log10(minT2);
                        double logMax = Math.Log10(maxT2);
                        double logVal = logMin + (logMax - logMin) * i / 5.0;
                        t2Value = Math.Pow(10, logVal);
                    }
                    else
                    {
                        t2Value = minT2 + (maxT2 - minT2) * i / 5.0;
                    }

                    // Format the tick label based on magnitude
                    string tickLabel;
                    if (t2Value < 0.1)
                        tickLabel = $"{t2Value:F3}";
                    else if (t2Value < 1)
                        tickLabel = $"{t2Value:F2}";
                    else if (t2Value < 10)
                        tickLabel = $"{t2Value:F1}";
                    else
                        tickLabel = $"{t2Value:F0}";

                    g.DrawString(tickLabel, _tickFont, brush, x, plotArea.Bottom + 8, centerFormat);
                }

                // Draw Y-axis tick marks and labels
                for (int i = 0; i <= 5; i++)
                {
                    int y = plotArea.Bottom - (plotArea.Height * i) / 5;
                    g.DrawLine(new Pen(Color.LightGray), plotArea.Left - 5, y, plotArea.Left, y);

                    // Calculate the actual amplitude value at this tick position
                    double ampValue = maxAmplitude * i / 5.0;

                    // Format the tick label based on magnitude
                    string tickLabel;
                    if (ampValue < 0.001)
                        tickLabel = $"{ampValue:E1}";
                    else if (ampValue < 0.01)
                        tickLabel = $"{ampValue:F4}";
                    else if (ampValue < 0.1)
                        tickLabel = $"{ampValue:F3}";
                    else if (ampValue < 1)
                        tickLabel = $"{ampValue:F2}";
                    else
                        tickLabel = $"{ampValue:F1}";

                    g.DrawString(tickLabel, _tickFont, brush, plotArea.Left - 8, y, rightFormat);
                }

                // Draw X-axis label - placed well below tick labels
                g.DrawString(logScale ? "T2 (ms, log scale)" : "T2 (ms)", _labelFont, brush,
                    plotArea.Left + plotArea.Width / 2 - 40,
                    plotArea.Bottom + 45);

                // Draw Y-axis label with vertical text
                using (var rotatedFont = new Font(_labelFont.FontFamily, _labelFont.Size, _labelFont.Style))
                {
                    // Save current state
                    var state = g.Save();

                    // Translate and rotate
                    g.TranslateTransform(plotArea.Left - 55, plotArea.Top + plotArea.Height / 2);
                    g.RotateTransform(-90);

                    // Draw rotated text
                    g.DrawString("Amplitude", rotatedFont, brush, 0, 0, new StringFormat { Alignment = StringAlignment.Center });

                    // Restore state
                    g.Restore(state);
                }
            }
        }

        private void PlotT2DistributionData(Graphics g, Rectangle plotArea, NMRSimulationResult result, bool logScale, double minT2, double maxT2, double maxAmplitude)
        {
            if (result.T2Values == null || result.T2Distribution == null || result.T2Values.Length == 0)
                return;

            // Determine appropriate skip factor based on the number of bars and available space
            int numBars = result.T2Values.Length;
            int skipFactor = Math.Max(1, numBars / Math.Max(plotArea.Width / 5, 20));

            // Calculate bar width based on available space and number of bars we'll draw
            float barWidth = plotArea.Width / Math.Max((float)Math.Ceiling((double)numBars / skipFactor), 20);

            // Draw bars with adaptive sampling
            for (int i = 0; i < numBars; i += skipFactor)
            {
                double t2 = result.T2Values[i];
                double amplitude = result.T2Distribution[i];

                // Calculate position
                double x = logScale ? Math.Log10(Math.Max(t2, 0.001)) : t2;
                double normalizedX = (x - (logScale ? Math.Log10(minT2) : minT2)) /
                                  ((logScale ? Math.Log10(maxT2) : maxT2) - (logScale ? Math.Log10(minT2) : minT2));

                double normalizedY = amplitude / maxAmplitude;

                float barX = (float)(plotArea.Left + normalizedX * plotArea.Width - barWidth / 2);
                float barHeight = (float)(normalizedY * plotArea.Height);

                // Create rectangle for the bar
                var barRect = new RectangleF(
                    barX,
                    (float)(plotArea.Bottom - barHeight),
                    barWidth * 0.8f,
                    barHeight
                );

                // Use solid color for bars with higher contrast
                using (var brush = new SolidBrush(Color.DodgerBlue))
                {
                    g.FillRectangle(brush, barRect);
                }

                // Only draw outlines for larger bars to improve performance
                if (barHeight > 2)
                {
                    using (var pen = new Pen(Color.LightBlue))
                    {
                        g.DrawRectangle(pen, barRect.X, barRect.Y, barRect.Width, barRect.Height);
                    }
                }
            }
        }

        private void DrawStatistics(Graphics g, NMRSimulationResult result, Rectangle area)
        {
            // Create translucent dark background for better readability
            using (var brush = new SolidBrush(Color.FromArgb(80, Color.DimGray)))
            {
                g.FillRectangle(brush, area);
            }

            string stats = $"Simulation Time: {result.SimulationTime:F1}ms | " +
                          $"Threads: {result.ThreadsUsed} | " +
                          $"GPU: {(result.UsedGPU ? "Yes" : "No")} | " +
                          $"Average T2: {result.AverageT2:F1}ms | " +
                          $"Total Porosity: {result.TotalPorosity:P2}";

            // Choose font size based on area width to ensure text fits
            float fontSize = area.Width > 600 ? 9.0f : 8.0f;
            using (var font = new Font("Segoe UI", fontSize))
            using (var brush = new SolidBrush(Color.White))
            {
                g.DrawString(stats, font, brush, area,
                    new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
            }
        }

        private void DrawT2Statistics(Graphics g, NMRSimulationResult result, Rectangle area)
        {
            // Create translucent dark background for better readability
            using (var brush = new SolidBrush(Color.FromArgb(80, Color.DimGray)))
            {
                g.FillRectangle(brush, area);
            }

            string stats = $"Average T2: {result.AverageT2:F1}ms | " +
                          $"Average Tortuosity: {result.AverageTortuosity:F2} | " +
                          $"Total Porosity: {result.TotalPorosity:P2} | " +
                          $"Components: {result.FittedComponents?.Count ?? 0}";

            // Choose font size based on area width to ensure text fits
            float fontSize = area.Width > 600 ? 9.0f : 8.0f;
            using (var font = new Font("Segoe UI", fontSize))
            using (var brush = new SolidBrush(Color.White))
            {
                g.DrawString(stats, font, brush, area,
                    new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
            }
        }

        private void DrawFittingParameters(Graphics g, NMRSimulationResult result, Rectangle area)
        {
            using (var brush = new SolidBrush(Color.FromArgb(40, Color.Gray)))
            {
                g.FillRectangle(brush, area);
            }

            using (var pen = new Pen(Color.DarkGray))
            {
                g.DrawRectangle(pen, area);
            }

            int y = area.Top + 5;
            using (var brush = new SolidBrush(Color.White))
            {
                g.DrawString("Fitting Parameters:", _labelFont, brush, area.Left + 5, y);
                y += 15;

                // Show top components - limit to what fits in the box
                if (result.FittedComponents != null && result.FittedComponents.Count > 0)
                {
                    int maxComponentsToShow = Math.Min((area.Height - 20) / 12, 5);

                    var topComponents = result.FittedComponents
                        .OrderByDescending(c => c.Amplitude)
                        .Take(maxComponentsToShow)
                        .ToList();

                    foreach (var component in topComponents)
                    {
                        string text = $"T2={component.RelaxationTime:F1}ms, A={component.Amplitude:F3}";
                        g.DrawString(text, _labelFont, brush, area.Left + 5, y);
                        y += 12;
                    }
                }
            }
        }

        private void DrawSubplotDecay(Graphics g, Rectangle area, NMRSimulationResult result)
        {
            if (result.TimePoints == null || result.Magnetization == null)
                return;

            // Plot background
            g.FillRectangle(new SolidBrush(Color.FromArgb(15, Color.White)), area);
            using (var pen = new Pen(Color.FromArgb(60, Color.White)))
            {
                g.DrawRectangle(pen, area);
            }

            // Title
            using (var brush = new SolidBrush(Color.White))
            {
                g.DrawString("Decay Curve", _labelFont, brush, area.Left + 5, area.Top + 5);
            }

            // Create smaller plot area inside, leaving margin for axes and labels
            var plotArea = new Rectangle(
                area.Left + 60,      // left margin for Y axis and tick labels
                area.Top + 25,       // top margin for title
                area.Width - 70,     // right margin
                area.Height - 45     // bottom margin for X axis and labels
            );

            // Find data ranges for plotting and tick labels
            double minTime = result.TimePoints.Min();
            double maxTime = result.TimePoints.Max();
            double maxMag = result.Magnetization.Max();
            double minMag = 0.001; // for log scale
            bool logScale = true;  // always use log scale for overview

            // Draw axes
            using (var pen = new Pen(Color.LightGray, 1))
            {
                g.DrawLine(pen, plotArea.Left, plotArea.Bottom, plotArea.Right, plotArea.Bottom); // X axis
                g.DrawLine(pen, plotArea.Left, plotArea.Top, plotArea.Left, plotArea.Bottom); // Y axis

                // Draw X-axis tick marks and labels
                var centerFormat = new StringFormat { Alignment = StringAlignment.Center };
                var rightFormat = new StringFormat { Alignment = StringAlignment.Far };

                // Draw X-axis tick marks and labels
                for (int i = 0; i <= 5; i++)
                {
                    int x = plotArea.Left + (plotArea.Width * i) / 5;
                    g.DrawLine(pen, x, plotArea.Bottom, x, plotArea.Bottom + 5);

                    // Calculate and format tick label for time
                    double timeValue;
                    if (logScale)
                    {
                        double logMin = Math.Log10(minTime);
                        double logMax = Math.Log10(maxTime);
                        double logVal = logMin + (logMax - logMin) * i / 5.0;
                        timeValue = Math.Pow(10, logVal);
                    }
                    else
                    {
                        timeValue = minTime + (maxTime - minTime) * i / 5.0;
                    }

                    string timeLabel = timeValue < 10 ? $"{timeValue:F1}" : $"{timeValue:F0}";
                    g.DrawString(timeLabel, _tickFont, new SolidBrush(Color.White), x, plotArea.Bottom + 8, centerFormat);
                }

                // Draw Y-axis tick marks and labels
                for (int i = 0; i <= 5; i++)
                {
                    int y = plotArea.Bottom - (plotArea.Height * i) / 5;
                    g.DrawLine(pen, plotArea.Left - 5, y, plotArea.Left, y);

                    // Calculate and format tick label for magnetization
                    double magValue;
                    if (logScale)
                    {
                        double logMin = Math.Log10(minMag);
                        double logMax = Math.Log10(maxMag);
                        double logVal = logMin + (logMax - logMin) * i / 5.0;
                        magValue = Math.Pow(10, logVal);
                    }
                    else
                    {
                        magValue = minMag + (maxMag - minMag) * i / 5.0;
                    }

                    string magLabel;
                    if (magValue < 0.01)
                        magLabel = $"{magValue:E1}";
                    else if (magValue < 0.1)
                        magLabel = $"{magValue:F3}";
                    else
                        magLabel = $"{magValue:F2}";

                    g.DrawString(magLabel, _tickFont, new SolidBrush(Color.White), plotArea.Left - 8, y, rightFormat);
                }
            }

            // Draw axes labels
            using (var brush = new SolidBrush(Color.White))
            {
                g.DrawString("Time (ms)", _labelFont, brush,
                    plotArea.Left + plotArea.Width / 2 - 30,
                    plotArea.Bottom + 15);
            }

            // Plot data (optimized for overview display)
            if (result.TimePoints != null && result.Magnetization != null && result.TimePoints.Length > 0)
            {
                // Adaptive sampling to improve performance
                int skipFactor = Math.Max(1, result.TimePoints.Length / 200);

                var points = new List<PointF>();
                for (int i = 0; i < result.TimePoints.Length; i += skipFactor)
                {
                    double x = logScale ? Math.Log10(Math.Max(result.TimePoints[i], 0.001)) : result.TimePoints[i];
                    double y = logScale ? Math.Log10(Math.Max(result.Magnetization[i], minMag)) : result.Magnetization[i];

                    float plotX = (float)(plotArea.Left + (x - (logScale ? Math.Log10(minTime) : minTime)) /
                                ((logScale ? Math.Log10(maxTime) : maxTime) - (logScale ? Math.Log10(minTime) : minTime)) * plotArea.Width);
                    float plotY = (float)(plotArea.Bottom - (y - (logScale ? Math.Log10(minMag) : minMag)) /
                                ((logScale ? Math.Log10(maxMag) : maxMag) - (logScale ? Math.Log10(minMag) : minMag)) * plotArea.Height);

                    // Ensure point is within bounds
                    plotX = Math.Max(plotArea.Left, Math.Min(plotX, plotArea.Right));
                    plotY = Math.Max(plotArea.Top, Math.Min(plotY, plotArea.Bottom));

                    points.Add(new PointF(plotX, plotY));
                }

                using (var pen = new Pen(Color.White, 2))
                {
                    if (points.Count > 1)
                        g.DrawLines(pen, points.ToArray());
                }
            }
        }

        private void DrawSubplotT2Distribution(Graphics g, Rectangle area, NMRSimulationResult result)
        {
            if (result.T2Values == null || result.T2Distribution == null)
                return;

            // Plot background
            g.FillRectangle(new SolidBrush(Color.FromArgb(15, Color.White)), area);
            using (var pen = new Pen(Color.FromArgb(60, Color.White)))
            {
                g.DrawRectangle(pen, area);
            }

            // Title
            using (var brush = new SolidBrush(Color.White))
            {
                g.DrawString("T2 Distribution", _labelFont, brush, area.Left + 5, area.Top + 5);
            }

            // Create smaller plot area inside, leaving margin for axes and labels
            var plotArea = new Rectangle(
                area.Left + 60,      // left margin for Y axis and tick labels
                area.Top + 25,       // top margin for title
                area.Width - 70,     // right margin
                area.Height - 45     // bottom margin for X axis and tick labels
            );

            // Find data ranges
            double minT2 = result.T2Values.Min();
            double maxT2 = result.T2Values.Max();
            double maxAmplitude = result.T2Distribution.Max();
            if (maxAmplitude <= 0) maxAmplitude = 1; // Prevent division by zero
            bool logScale = true; // always use log scale for overview

            // Draw axes
            using (var pen = new Pen(Color.LightGray, 1))
            {
                g.DrawLine(pen, plotArea.Left, plotArea.Bottom, plotArea.Right, plotArea.Bottom); // X axis
                g.DrawLine(pen, plotArea.Left, plotArea.Top, plotArea.Left, plotArea.Bottom); // Y axis

                // Setup formats for tick labels
                var centerFormat = new StringFormat { Alignment = StringAlignment.Center };
                var rightFormat = new StringFormat { Alignment = StringAlignment.Far };

                // Draw X-axis tick marks and labels
                for (int i = 0; i <= 5; i++)
                {
                    int x = plotArea.Left + (plotArea.Width * i) / 5;
                    g.DrawLine(pen, x, plotArea.Bottom, x, plotArea.Bottom + 5);

                    // Calculate and format tick label for T2
                    double t2Value;
                    if (logScale)
                    {
                        double logMin = Math.Log10(minT2);
                        double logMax = Math.Log10(maxT2);
                        double logVal = logMin + (logMax - logMin) * i / 5.0;
                        t2Value = Math.Pow(10, logVal);
                    }
                    else
                    {
                        t2Value = minT2 + (maxT2 - minT2) * i / 5.0;
                    }

                    string t2Label = t2Value < 10 ? $"{t2Value:F1}" : $"{t2Value:F0}";
                    g.DrawString(t2Label, _tickFont, new SolidBrush(Color.White), x, plotArea.Bottom + 8, centerFormat);
                }

                // Draw Y-axis tick marks and labels
                for (int i = 0; i <= 5; i++)
                {
                    int y = plotArea.Bottom - (plotArea.Height * i) / 5;
                    g.DrawLine(pen, plotArea.Left - 5, y, plotArea.Left, y);

                    // Calculate and format tick label for amplitude
                    double ampValue = maxAmplitude * i / 5.0;

                    string ampLabel;
                    if (ampValue < 0.001)
                        ampLabel = $"{ampValue:E1}";
                    else if (ampValue < 0.01)
                        ampLabel = $"{ampValue:F4}";
                    else if (ampValue < 0.1)
                        ampLabel = $"{ampValue:F3}";
                    else
                        ampLabel = $"{ampValue:F2}";

                    g.DrawString(ampLabel, _tickFont, new SolidBrush(Color.White), plotArea.Left - 8, y, rightFormat);
                }
            }

            // Draw axes labels
            using (var brush = new SolidBrush(Color.White))
            {
                g.DrawString("T2 (ms, log scale)", _labelFont, brush,
                    plotArea.Left + plotArea.Width / 2 - 40,
                    plotArea.Bottom + 15);
            }

            // Plot T2 distribution with optimized sampling
            if (result.T2Values != null && result.T2Distribution != null && result.T2Values.Length > 0)
            {
                // Determine appropriate sampling based on available width
                int numBars = result.T2Values.Length;
                int skipFactor = Math.Max(1, numBars / Math.Max(plotArea.Width / 8, 10));

                // Calculate bar width to ensure they're visible and properly spaced
                float barWidth = (float)plotArea.Width / Math.Max((float)Math.Ceiling((double)numBars / skipFactor), 20);

                for (int i = 0; i < numBars; i += skipFactor)
                {
                    double t2 = result.T2Values[i];
                    double amplitude = result.T2Distribution[i];

                    // Calculate position
                    double x = logScale ? Math.Log10(Math.Max(t2, 0.001)) : t2;
                    double xPos = (x - (logScale ? Math.Log10(minT2) : minT2)) /
                                 ((logScale ? Math.Log10(maxT2) : maxT2) - (logScale ? Math.Log10(minT2) : minT2));

                    double yPos = amplitude / maxAmplitude;

                    // Draw the bar - ensure it's wide enough to be visible
                    float barX = (float)(plotArea.Left + xPos * plotArea.Width - barWidth / 2);
                    float barHeight = (float)(yPos * plotArea.Height);

                    var barRect = new RectangleF(
                        barX,
                        plotArea.Bottom - barHeight,
                        barWidth * 0.8f,
                        barHeight
                    );

                    // Use solid color for better visibility
                    using (var brush = new SolidBrush(Color.DodgerBlue))
                    {
                        g.FillRectangle(brush, barRect);
                    }

                    // Only draw outlines for larger bars
                    if (barHeight > 3 && barRect.Width > 2)
                    {
                        using (var pen = new Pen(Color.LightBlue))
                        {
                            g.DrawRectangle(pen, barRect.X, barRect.Y, barRect.Width, barRect.Height);
                        }
                    }
                }
            }
        }

        private void DrawComprehensiveStatistics(Graphics g, NMRSimulationResult result, Rectangle area)
        {
            // Create a semi-transparent background 
            using (var brush = new SolidBrush(Color.FromArgb(50, Color.DarkGray)))
            {
                g.FillRectangle(brush, area);
            }

            using (var pen = new Pen(Color.FromArgb(100, Color.White)))
            {
                g.DrawRectangle(pen, area);
            }

            // Reorganized layout with better spacing for taller info bar
            // Use a 3x2 grid layout to fit all information clearly
            int colWidth = area.Width / 3;

            using (var brush = new SolidBrush(Color.White))
            {
                // Column headers
                g.DrawString("Simulation Info:", _labelFont, brush, area.Left + 10, area.Top + 5);
                g.DrawString("Relaxation Info:", _labelFont, brush, area.Left + 10 + colWidth, area.Top + 5);
                g.DrawString("Pore Info:", _labelFont, brush, area.Left + 10 + 2 * colWidth, area.Top + 5);

                // Column 1 - Simulation Info details
                g.DrawString($"Time: {result.SimulationTime:F1}ms", _labelFont, brush,
                             area.Left + 15, area.Top + 25);
                g.DrawString($"GPU: {(result.UsedGPU ? "Yes" : "No")}", _labelFont, brush,
                             area.Left + 15, area.Top + 40);

                // Column 2 - Relaxation Info details
                g.DrawString($"Avg T2: {result.AverageT2:F1}ms", _labelFont, brush,
                             area.Left + 15 + colWidth, area.Top + 25);
                g.DrawString($"Components: {result.FittedComponents?.Count ?? 0}", _labelFont, brush,
                             area.Left + 15 + colWidth, area.Top + 40);

                // Column 3 - Pore Info details
                g.DrawString($"Porosity: {result.TotalPorosity:P2}", _labelFont, brush,
                             area.Left + 15 + 2 * colWidth, area.Top + 25);
                g.DrawString($"Avg Tortuosity: {result.AverageTortuosity:F2}", _labelFont, brush,
                             area.Left + 15 + 2 * colWidth, area.Top + 40);
            }
        }

        public void SavePlots(NMRSimulationResult result, string basePath)
        {
            try
            {
                // Use consistent sizes for saved images
                var decayCurve = PlotDecayCurve(result, new Size(800, 600));
                decayCurve.Save($"{basePath}_decay.png", ImageFormat.Png);
                decayCurve.Dispose();

                var t2Distribution = PlotT2Distribution(result, new Size(800, 600));
                t2Distribution.Save($"{basePath}_t2distribution.png", ImageFormat.Png);
                t2Distribution.Dispose();

                var overview = PlotComponentsOverview(result, new Size(1000, 700));
                overview.Save($"{basePath}_overview.png", ImageFormat.Png);
                overview.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving plots: {ex.Message}");
            }
        }

        public void ExportOverview(NMRSimulationResult result, string filePath)
        {
            if (result == null) return;

            try
            {
                ImageFormat format = ImageFormat.Png;
                if (filePath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                    filePath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                    format = ImageFormat.Jpeg;

                // Use a consistent high-quality size for export
                var overview = PlotComponentsOverview(result, new Size(1200, 800));

                // Use format with appropriate compression
                if (format == ImageFormat.Jpeg)
                {
                    // For JPEG, we can set the quality
                    var jpegEncoder = GetEncoder(ImageFormat.Jpeg);
                    var encoderParams = new EncoderParameters(1);
                    encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, 90L);
                    overview.Save(filePath, jpegEncoder, encoderParams);
                }
                else
                {
                    overview.Save(filePath, format);
                }

                overview.Dispose();
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to export overview: {ex.Message}", ex);
            }
        }

        private ImageCodecInfo GetEncoder(ImageFormat format)
        {
            ImageCodecInfo[] codecs = ImageCodecInfo.GetImageDecoders();
            foreach (ImageCodecInfo codec in codecs)
            {
                if (codec.FormatID == format.Guid)
                {
                    return codec;
                }
            }
            return null;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _labelFont?.Dispose();
                    _titleFont?.Dispose();
                    _legendFont?.Dispose();
                    _tickFont?.Dispose();
                }
                _disposed = true;
            }
        }
    }
}