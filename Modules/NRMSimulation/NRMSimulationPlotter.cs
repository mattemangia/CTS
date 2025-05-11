using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace CTS.Modules.Simulation.NMR
{
    /// <summary>
    /// Provides plotting functionality for NMR simulation results
    /// </summary>
    public class NMRResultPlotter
    {
        private readonly Color[] _componentColors;
        private readonly Font _labelFont;
        private readonly Font _titleFont;
        private readonly Font _legendFont;

        public NMRResultPlotter()
        {
            // Initialize color palette for components
            _componentColors = new Color[]
            {
                Color.Blue, Color.Red, Color.Green, Color.Orange, Color.Purple,
                Color.Cyan, Color.Magenta, Color.Brown, Color.Navy, Color.DarkGreen,
                Color.DarkRed, Color.DarkOrange, Color.Indigo, Color.Teal, Color.Maroon
            };

            _labelFont = new Font("Arial", 9);
            _titleFont = new Font("Arial", 11, FontStyle.Bold);
            _legendFont = new Font("Arial", 8);
        }

        public Bitmap PlotDecayCurve(NMRSimulationResult result, Size imageSize, bool logScale = true, bool showComponents = true)
        {
            var bitmap = new Bitmap(imageSize.Width, imageSize.Height);

            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.White);

                // Set up margins and plot area
                var margins = new Padding(60, 40, 30, 50);
                var plotArea = new Rectangle(margins.Left, margins.Top,
                    imageSize.Width - margins.Left - margins.Right,
                    imageSize.Height - margins.Top - margins.Bottom);

                // Draw title
                string title = "NMR Decay Curve";
                if (result.UsedGPU)
                    title += " (GPU Accelerated)";

                DrawTitle(g, title, new Rectangle(0, 0, imageSize.Width, margins.Top));

                // Draw axes
                DrawAxes(g, plotArea, logScale);

                // Plot data
                PlotDecayData(g, plotArea, result, logScale, showComponents);

                // Draw legend
                if (showComponents)
                {
                    DrawDecayLegend(g, result, new Rectangle(plotArea.Right - 120, plotArea.Top + 10, 100, 150));
                }

                // Draw statistics
                DrawStatistics(g, result, new Rectangle(10, imageSize.Height - 40, imageSize.Width - 20, 30));
            }

            return bitmap;
        }

        public Bitmap PlotT2Distribution(NMRSimulationResult result, Size imageSize, bool logScale = true)
        {
            var bitmap = new Bitmap(imageSize.Width, imageSize.Height);

            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.White);

                // Set up margins and plot area
                var margins = new Padding(60, 40, 30, 50);
                var plotArea = new Rectangle(margins.Left, margins.Top,
                    imageSize.Width - margins.Left - margins.Right,
                    imageSize.Height - margins.Top - margins.Bottom);

                // Draw title
                DrawTitle(g, "T2 Relaxation Distribution", new Rectangle(0, 0, imageSize.Width, margins.Top));

                // Draw axes
                DrawT2DistributionAxes(g, plotArea, logScale);

                // Plot distribution
                PlotT2DistributionData(g, plotArea, result, logScale);

                // Draw fitting parameters
                DrawFittingParameters(g, result, new Rectangle(plotArea.Right - 200, plotArea.Top + 10, 180, 100));

                // Draw statistics
                DrawT2Statistics(g, result, new Rectangle(10, imageSize.Height - 40, imageSize.Width - 20, 30));
            }

            return bitmap;
        }

        public Bitmap PlotComponentsOverview(NMRSimulationResult result, Size imageSize)
        {
            var bitmap = new Bitmap(imageSize.Width, imageSize.Height);

            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.White);

                // Create subplots
                int plotHeight = (imageSize.Height - 100) / 2;
                var topPlot = new Rectangle(20, 40, imageSize.Width - 40, plotHeight);
                var bottomPlot = new Rectangle(20, 60 + plotHeight, imageSize.Width - 40, plotHeight);

                // Draw title
                DrawTitle(g, "NMR Components Overview", new Rectangle(0, 0, imageSize.Width, 40));

                // Plot decay curve
                DrawSubplotDecay(g, topPlot, result);

                // Plot T2 distribution
                DrawSubplotT2Distribution(g, bottomPlot, result);

                // Draw comprehensive statistics
                DrawComprehensiveStatistics(g, result, new Rectangle(20, imageSize.Height - 80, imageSize.Width - 40, 60));
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

            g.DrawString(title, _titleFont, Brushes.Black, area, format);
        }

        private void DrawAxes(Graphics g, Rectangle plotArea, bool logScale)
        {
            using (var pen = new Pen(Color.Black, 1))
            {
                // Draw X and Y axes
                g.DrawLine(pen, plotArea.Left, plotArea.Bottom, plotArea.Right, plotArea.Bottom);
                g.DrawLine(pen, plotArea.Left, plotArea.Bottom, plotArea.Left, plotArea.Top);

                // Draw labels
                g.DrawString("Time (ms)", _labelFont, Brushes.Black, plotArea.Left + plotArea.Width / 2 - 30, plotArea.Bottom + 20);

                var format = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    FormatFlags = StringFormatFlags.DirectionVertical
                };
                g.DrawString(logScale ? "Magnetization (log)" : "Magnetization", _labelFont, Brushes.Black, plotArea.Left - 40, plotArea.Top + plotArea.Height / 2, format);
            }
        }

        private void PlotDecayData(Graphics g, Rectangle plotArea, NMRSimulationResult result, bool logScale, bool showComponents)
        {
            if (result.TimePoints == null || result.Magnetization == null)
                return;

            // Find data ranges
            double minTime = result.TimePoints.Min();
            double maxTime = result.TimePoints.Max();
            double maxMag = result.Magnetization.Max();
            double minMag = logScale ? 0.001 : 0;

            // Plot total magnetization
            var points = new List<PointF>();
            for (int i = 0; i < result.TimePoints.Length; i++)
            {
                double x = logScale ? Math.Log10(result.TimePoints[i]) : result.TimePoints[i];
                double y = logScale ? Math.Log10(Math.Max(result.Magnetization[i], minMag)) : result.Magnetization[i];

                float plotX = (float)(plotArea.Left + (x - (logScale ? Math.Log10(minTime) : minTime)) /
                              ((logScale ? Math.Log10(maxTime) : maxTime) - (logScale ? Math.Log10(minTime) : minTime)) * plotArea.Width);
                float plotY = (float)(plotArea.Bottom - (y - (logScale ? Math.Log10(minMag) : minMag)) /
                              ((logScale ? Math.Log10(maxMag) : maxMag) - (logScale ? Math.Log10(minMag) : minMag)) * plotArea.Height);

                points.Add(new PointF(plotX, plotY));
            }

            using (var pen = new Pen(Color.Black, 2))
            {
                if (points.Count > 1)
                    g.DrawLines(pen, points.ToArray());
            }

            // Plot individual components if requested
            if (showComponents && result.FittedComponents != null)
            {
                for (int comp = 0; comp < Math.Min(result.FittedComponents.Count, _componentColors.Length); comp++)
                {
                    var component = result.FittedComponents[comp];
                    var compPoints = new List<PointF>();

                    for (int i = 0; i < result.TimePoints.Length; i++)
                    {
                        double t = result.TimePoints[i];
                        double mag = component.Amplitude * Math.Exp(-t / component.RelaxationTime);

                        double x = logScale ? Math.Log10(t) : t;
                        double y = logScale ? Math.Log10(Math.Max(mag, minMag)) : mag;

                        float plotX = (float)(plotArea.Left + (x - (logScale ? Math.Log10(minTime) : minTime)) /
                                      ((logScale ? Math.Log10(maxTime) : maxTime) - (logScale ? Math.Log10(minTime) : minTime)) * plotArea.Width);
                        float plotY = (float)(plotArea.Bottom - (y - (logScale ? Math.Log10(minMag) : minMag)) /
                                      ((logScale ? Math.Log10(maxMag) : maxMag) - (logScale ? Math.Log10(minMag) : minMag)) * plotArea.Height);

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
        }

        private void DrawDecayLegend(Graphics g, NMRSimulationResult result, Rectangle area)
        {
            using (var brush = new SolidBrush(Color.FromArgb(200, Color.White)))
            {
                g.FillRectangle(brush, area);
            }

            using (var pen = new Pen(Color.Black))
            {
                g.DrawRectangle(pen, area);
            }

            int y = area.Top + 5;

            // Total curve
            using (var pen = new Pen(Color.Black, 2))
            {
                g.DrawLine(pen, area.Left + 5, y + 8, area.Left + 25, y + 8);
            }
            g.DrawString("Total", _legendFont, Brushes.Black, area.Left + 30, y);
            y += 15;

            // Component curves
            if (result.FittedComponents != null)
            {
                for (int i = 0; i < Math.Min(5, result.FittedComponents.Count); i++)
                {
                    using (var pen = new Pen(_componentColors[i % _componentColors.Length], 1))
                    {
                        pen.DashStyle = DashStyle.Dash;
                        g.DrawLine(pen, area.Left + 5, y + 8, area.Left + 25, y + 8);
                    }
                    g.DrawString($"T2={result.FittedComponents[i].RelaxationTime:F1}ms", _legendFont, Brushes.Black, area.Left + 30, y);
                    y += 15;
                }

                if (result.FittedComponents.Count > 5)
                {
                    g.DrawString($"... +{result.FittedComponents.Count - 5} more", _legendFont, Brushes.Black, area.Left + 5, y);
                }
            }
        }

        private void DrawT2DistributionAxes(Graphics g, Rectangle plotArea, bool logScale)
        {
            using (var pen = new Pen(Color.Black, 1))
            {
                // Draw X and Y axes
                g.DrawLine(pen, plotArea.Left, plotArea.Bottom, plotArea.Right, plotArea.Bottom);
                g.DrawLine(pen, plotArea.Left, plotArea.Bottom, plotArea.Left, plotArea.Top);

                // Draw labels
                g.DrawString(logScale ? "T2 (ms, log scale)" : "T2 (ms)", _labelFont, Brushes.Black, plotArea.Left + plotArea.Width / 2 - 40, plotArea.Bottom + 20);

                var format = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    FormatFlags = StringFormatFlags.DirectionVertical
                };
                g.DrawString("Amplitude", _labelFont, Brushes.Black, plotArea.Left - 40, plotArea.Top + plotArea.Height / 2, format);
            }
        }

        private void PlotT2DistributionData(Graphics g, Rectangle plotArea, NMRSimulationResult result, bool logScale)
        {
            if (result.T2Values == null || result.T2Distribution == null)
                return;

            // Find data ranges
            double minT2 = result.T2Values.Min();
            double maxT2 = result.T2Values.Max();
            double maxAmplitude = result.T2Distribution.Max();

            // Create bar chart for distribution
            int numBars = result.T2Values.Length;
            float barWidth = (float)plotArea.Width / numBars;

            for (int i = 0; i < numBars; i++)
            {
                double t2 = result.T2Values[i];
                double amplitude = result.T2Distribution[i];

                // Calculate position
                double x = logScale ? Math.Log10(t2) : t2;
                double xPos = (x - (logScale ? Math.Log10(minT2) : minT2)) /
                             ((logScale ? Math.Log10(maxT2) : maxT2) - (logScale ? Math.Log10(minT2) : minT2));

                double yPos = amplitude / maxAmplitude;

                // Draw bar
                var barRect = new RectangleF(
                    (float)(plotArea.Left + xPos * plotArea.Width - barWidth / 2),
                    (float)(plotArea.Bottom - yPos * plotArea.Height),
                    barWidth * 0.8f,
                    (float)(yPos * plotArea.Height)
                );

                using (var brush = new SolidBrush(Color.FromArgb(150, Color.Blue)))
                {
                    g.FillRectangle(brush, barRect);
                }

                using (var pen = new Pen(Color.Blue))
                {
                    g.DrawRectangle(pen, barRect.X, barRect.Y, barRect.Width, barRect.Height);
                }
            }
        }

        private void DrawStatistics(Graphics g, NMRSimulationResult result, Rectangle area)
        {
            string stats = $"Simulation Time: {result.SimulationTime:F1}ms | " +
                          $"Threads: {result.ThreadsUsed} | " +
                          $"GPU: {(result.UsedGPU ? "Yes" : "No")} | " +
                          $"Average T2: {result.AverageT2:F1}ms | " +
                          $"Total Porosity: {result.TotalPorosity:P2}";

            g.DrawString(stats, _labelFont, Brushes.Black, area, new StringFormat { Alignment = StringAlignment.Center });
        }

        private void DrawT2Statistics(Graphics g, NMRSimulationResult result, Rectangle area)
        {
            string stats = $"Average T2: {result.AverageT2:F1}ms | " +
                          $"Average Tortuosity: {result.AverageTortuosity:F2} | " +
                          $"Total Porosity: {result.TotalPorosity:P2} | " +
                          $"Components: {result.FittedComponents?.Count ?? 0}";

            g.DrawString(stats, _labelFont, Brushes.Black, area, new StringFormat { Alignment = StringAlignment.Center });
        }

        private void DrawFittingParameters(Graphics g, NMRSimulationResult result, Rectangle area)
        {
            using (var brush = new SolidBrush(Color.FromArgb(240, Color.White)))
            {
                g.FillRectangle(brush, area);
            }

            using (var pen = new Pen(Color.Gray))
            {
                g.DrawRectangle(pen, area);
            }

            int y = area.Top + 5;
            g.DrawString("Fitting Parameters:", _labelFont, Brushes.Black, area.Left + 5, y);
            y += 15;

            // Show top components
            if (result.FittedComponents != null && result.FittedComponents.Count > 0)
            {
                var topComponents = result.FittedComponents
                    .OrderByDescending(c => c.Amplitude)
                    .Take(5)
                    .ToList();

                foreach (var component in topComponents)
                {
                    string text = $"T2={component.RelaxationTime:F1}ms, A={component.Amplitude:F3}";
                    g.DrawString(text, _labelFont, Brushes.Black, area.Left + 5, y);
                    y += 12;
                }
            }
        }

        private void DrawSubplotDecay(Graphics g, Rectangle area, NMRSimulationResult result)
        {
            // Similar to PlotDecayData but in a smaller area
            DrawAxes(g, area, true);
            PlotDecayData(g, area, result, true, false);

            g.DrawString("Decay Curve", _labelFont, Brushes.Black, area.Left + 5, area.Top + 5);
        }

        private void DrawSubplotT2Distribution(Graphics g, Rectangle area, NMRSimulationResult result)
        {
            // Similar to PlotT2Distribution but in a smaller area
            DrawT2DistributionAxes(g, area, true);
            PlotT2DistributionData(g, area, result, true);

            g.DrawString("T2 Distribution", _labelFont, Brushes.Black, area.Left + 5, area.Top + 5);
        }

        private void DrawComprehensiveStatistics(Graphics g, NMRSimulationResult result, Rectangle area)
        {
            using (var brush = new SolidBrush(Color.FromArgb(250, Color.LightGray)))
            {
                g.FillRectangle(brush, area);
            }

            using (var pen = new Pen(Color.Black))
            {
                g.DrawRectangle(pen, area);
            }

            int y = area.Top + 5;
            int x = area.Left + 10;
            int colWidth = area.Width / 3;

            // Column 1
            g.DrawString("Simulation Info:", _labelFont, Brushes.Black, x, y);
            y += 15;
            g.DrawString($"Time: {result.SimulationTime:F1}ms", _labelFont, Brushes.Black, x, y);
            y += 12;
            g.DrawString($"GPU: {(result.UsedGPU ? "Yes" : "No")}", _labelFont, Brushes.Black, x, y);

            // Column 2
            y = area.Top + 5;
            x += colWidth;
            g.DrawString("Relaxation Info:", _labelFont, Brushes.Black, x, y);
            y += 15;
            g.DrawString($"Avg T2: {result.AverageT2:F1}ms", _labelFont, Brushes.Black, x, y);
            y += 12;
            g.DrawString($"Components: {result.FittedComponents?.Count ?? 0}", _labelFont, Brushes.Black, x, y);

            // Column 3
            y = area.Top + 5;
            x += colWidth;
            g.DrawString("Pore Info:", _labelFont, Brushes.Black, x, y);
            y += 15;
            g.DrawString($"Porosity: {result.TotalPorosity:P2}", _labelFont, Brushes.Black, x, y);
            y += 12;
            g.DrawString($"Avg Tortuosity: {result.AverageTortuosity:F2}", _labelFont, Brushes.Black, x, y);
        }

        public void SavePlots(NMRSimulationResult result, string basePath)
        {
            // Save decay curve
            var decayCurve = PlotDecayCurve(result, new Size(800, 600));
            decayCurve.Save($"{basePath}_decay.png", System.Drawing.Imaging.ImageFormat.Png);
            decayCurve.Dispose();

            // Save T2 distribution
            var t2Distribution = PlotT2Distribution(result, new Size(800, 600));
            t2Distribution.Save($"{basePath}_t2distribution.png", System.Drawing.Imaging.ImageFormat.Png);
            t2Distribution.Dispose();

            // Save overview
            var overview = PlotComponentsOverview(result, new Size(1200, 800));
            overview.Save($"{basePath}_overview.png", System.Drawing.Imaging.ImageFormat.Png);
            overview.Dispose();
        }

        public void Dispose()
        {
            _labelFont?.Dispose();
            _titleFont?.Dispose();
            _legendFont?.Dispose();
        }
    }
}