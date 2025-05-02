using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace CTSegmenter
{
    /// <summary>
    /// Extension class to handle Mohr-Coulomb visualization
    /// </summary>
    public class MohrCoulombVisualization
    {
        // Cached visualization parameters
        private double _confiningPressure = 0;
        private double _axialPressure = 0;
        private double _frictionAngle = 30;
        private double _cohesion = 5;

        /// <summary>
        /// Create a new Mohr circle visualization
        /// </summary>
        public Bitmap CreateMohrCircleVisualization(int width, int height,
                                                  double confiningPressure, double axialPressure,
                                                  double frictionAngle, double cohesion)
        {
            // Store parameters
            _confiningPressure = confiningPressure;
            _axialPressure = axialPressure;
            _frictionAngle = frictionAngle;
            _cohesion = cohesion;

            // Create bitmap
            Bitmap bmp = new Bitmap(Math.Max(1, width), Math.Max(1, height));

            using (Graphics g = Graphics.FromImage(bmp))
            {
                // Setup high quality rendering
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                // Fill background
                g.Clear(Color.FromArgb(40, 40, 40));

                // Draw the visualization
                DrawMohrCircles(g, width, height);
            }

            return bmp;
        }

        /// <summary>
        /// Draw the Mohr circles
        /// </summary>
        private void DrawMohrCircles(Graphics g, int width, int height)
        {
            // Leave margins for labels
            int margin = 50;
            int graphWidth = width - 2 * margin;
            int graphHeight = height - 2 * margin;

            // Calculate Mohr circle parameters
            double sig1 = Math.Max(_axialPressure, _confiningPressure);
            double sig3 = Math.Min(_axialPressure, _confiningPressure);
            double center = (sig1 + sig3) / 2;
            double radius = (sig1 - sig3) / 2;

            // Calculate failure envelope parameters
            double phi = _frictionAngle * Math.PI / 180.0;
            double sinPhi = Math.Sin(phi);
            double cosPhi = Math.Cos(phi);

            // Calculate maximum normal stress for plotting
            double maxSigma = sig1 * 1.2;

            // Calculate maximum shear stress based on envelope
            double maxTau = (_cohesion * cosPhi + maxSigma * sinPhi);

            // Create scaling factors 
            double scaleX = graphWidth / maxSigma;
            double scaleY = graphHeight / (2 * maxTau);

            // Origin in graph coordinates
            double originX = margin;
            double originY = height - margin;

            // Draw axes
            using (Pen axisPen = new Pen(Color.White, 1))
            {
                // X-axis (normal stress)
                g.DrawLine(axisPen,
                           (float)originX, (float)originY,
                           (float)(originX + graphWidth), (float)originY);

                // Y-axis (shear stress)
                g.DrawLine(axisPen,
                           (float)originX, (float)originY,
                           (float)originX, (float)(originY - graphHeight));

                // Add arrow to Y-axis
                const float arrowSize = 6;
                PointF[] arrowPoints = {
                    new PointF((float)originX, (float)(originY - graphHeight)),
                    new PointF((float)originX - arrowSize, (float)(originY - graphHeight + arrowSize)),
                    new PointF((float)originX + arrowSize, (float)(originY - graphHeight + arrowSize))
                };
                g.FillPolygon(Brushes.White, arrowPoints);

                // Add arrow to X-axis
                PointF[] arrowPointsX = {
                    new PointF((float)(originX + graphWidth), (float)originY),
                    new PointF((float)(originX + graphWidth - arrowSize), (float)(originY - arrowSize)),
                    new PointF((float)(originX + graphWidth - arrowSize), (float)(originY + arrowSize))
                };
                g.FillPolygon(Brushes.White, arrowPointsX);
            }

            // Draw axis labels
            using (Font labelFont = new Font("Segoe UI", 10))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            {
                // X-axis label
                g.DrawString("Normal Stress (σ) [MPa]", labelFont, textBrush,
                            (float)(originX + graphWidth / 2 - 60), (float)(originY + 10));

                // Y-axis label
                StringFormat sfVert = new StringFormat();
                sfVert.Alignment = StringAlignment.Center;
                Matrix rotationMatrix = new Matrix();
                rotationMatrix.RotateAt(-90, new PointF((float)(originX - 35), (float)(originY - graphHeight / 2)));
                g.Transform = rotationMatrix;
                g.DrawString("Shear Stress (τ) [MPa]", labelFont, textBrush,
                            (float)(originX - 35), (float)(originY - graphHeight / 2));
                g.ResetTransform();
            }

            // Draw scale marks on axes
            using (Pen tickPen = new Pen(Color.White, 1))
            using (Font tickFont = new Font("Segoe UI", 8))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            {
                // X-axis ticks
                for (int i = 0; i <= 10; i++)
                {
                    double tickValue = maxSigma * i / 10;
                    float tickX = (float)(originX + tickValue * scaleX);

                    // Draw tick
                    g.DrawLine(tickPen, tickX, (float)originY, tickX, (float)(originY + 5));

                    // Draw label
                    StringFormat sf = new StringFormat();
                    sf.Alignment = StringAlignment.Center;
                    g.DrawString(tickValue.ToString("F0"), tickFont, textBrush,
                                tickX, (float)(originY + 7), sf);
                }

                // Y-axis ticks (positive only)
                for (int i = 0; i <= 5; i++)
                {
                    double tickValue = maxTau * i / 5;
                    float tickY = (float)(originY - tickValue * scaleY);

                    // Draw tick
                    g.DrawLine(tickPen, (float)originX - 5, tickY, (float)originX, tickY);

                    // Draw label
                    g.DrawString(tickValue.ToString("F1"), tickFont, textBrush,
                                (float)(originX - 25), tickY - 6);
                }
            }

            // Draw Mohr Circle
            using (Pen circlePen = new Pen(Color.LightBlue, 2))
            {
                // Convert to screen coordinates
                float centerX = (float)(originX + center * scaleX);
                float centerY = (float)originY;
                float radiusX = (float)(radius * scaleX);
                float radiusY = (float)(radius * scaleY);

                // Draw the circle
                g.DrawEllipse(circlePen,
                             centerX - radiusX, centerY - radiusY,
                             2 * radiusX, 2 * radiusY);

                // Mark the center
                using (SolidBrush pointBrush = new SolidBrush(Color.Yellow))
                {
                    g.FillEllipse(pointBrush, centerX - 3, centerY - 3, 6, 6);
                }

                // Mark the principal stress points
                g.FillEllipse(Brushes.Red,
                             (float)(originX + sig1 * scaleX) - 4, (float)originY - 4,
                             8, 8);
                g.FillEllipse(Brushes.Red,
                             (float)(originX + sig3 * scaleX) - 4, (float)originY - 4,
                             8, 8);

                // Add principal stress labels
                using (Font stressFont = new Font("Segoe UI", 8, FontStyle.Bold))
                using (SolidBrush textBrush = new SolidBrush(Color.White))
                {
                    g.DrawString("σ₁", stressFont, textBrush,
                                (float)(originX + sig1 * scaleX) - 4, (float)originY + 5);
                    g.DrawString("σ₃", stressFont, textBrush,
                                (float)(originX + sig3 * scaleX) - 4, (float)originY + 5);
                }
            }

            // Draw failure envelope
            using (Pen envelopePen = new Pen(Color.Red, 2))
            {
                // Calculate envelope points
                float x1 = (float)originX;
                float y1 = (float)(originY - _cohesion * scaleY);
                float x2 = (float)(originX + maxSigma * scaleX);
                float y2 = (float)(originY - (_cohesion * cosPhi + maxSigma * sinPhi) * scaleY);

                // Draw the envelope line
                g.DrawLine(envelopePen, x1, y1, x2, y2);

                // Add label
                using (Font labelFont = new Font("Segoe UI", 8, FontStyle.Bold))
                using (SolidBrush textBrush = new SolidBrush(Color.Red))
                {
                    g.DrawString("Failure Envelope", labelFont, textBrush, x1 + 10, y1 - 20);
                }
            }

            // Add stress state label
            using (Font stateFont = new Font("Segoe UI", 9))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            {
                string stressState = $"Current Stress State: σ₁={sig1:F1} MPa, σ₃={sig3:F1} MPa";
                g.DrawString(stressState, stateFont, textBrush,
                            (float)originX + 10, (float)(originY - graphHeight + 10));

                // Add parameter info
                string paramInfo = $"Cohesion: {_cohesion:F1} MPa, Friction Angle: {_frictionAngle:F1}°";
                g.DrawString(paramInfo, stateFont, textBrush,
                            (float)originX + 10, (float)(originY - graphHeight + 30));
            }
        }
    }
}