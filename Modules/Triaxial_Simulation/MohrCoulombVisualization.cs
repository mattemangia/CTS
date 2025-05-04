using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace CTS
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
        private bool _failureDetected = false;
        private double _failureStress = 0;

        /// <summary>
        /// Create a new Mohr circle visualization
        /// </summary>
        public Bitmap CreateMohrCircleVisualization(int width, int height,
                                                  double confiningPressure, double axialPressure,
                                                  double frictionAngle, double cohesion,
                                                  bool failureDetected = false, double failureStress = 0)
        {
            try
            {
                // Store parameters
                _confiningPressure = confiningPressure;
                _axialPressure = axialPressure;
                _frictionAngle = frictionAngle;
                _cohesion = cohesion;
                _failureDetected = failureDetected;
                _failureStress = failureStress;

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
            catch (Exception ex)
            {
                // Log the error
                Logger.Log($"[MohrCoulombVisualization] Error creating visualization: {ex.Message}\n{ex.StackTrace}");

                // Return a simple error bitmap
                Bitmap errorBmp = new Bitmap(Math.Max(1, width), Math.Max(1, height));
                using (Graphics g = Graphics.FromImage(errorBmp))
                {
                    g.Clear(Color.FromArgb(40, 40, 40));
                    using (Font errorFont = new Font("Segoe UI", 10))
                    using (SolidBrush errorBrush = new SolidBrush(Color.White))
                    {
                        g.DrawString($"Error creating Mohr-Coulomb visualization: {ex.Message}",
                                     errorFont, errorBrush, 10, 10);
                    }
                }
                return errorBmp;
            }
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
            double radius = Math.Max((sig1 - sig3) / 2, 0.001); // Ensure non-zero radius

            // Calculate failure envelope parameters
            double phi = _frictionAngle * Math.PI / 180.0;
            double sinPhi = Math.Sin(phi);
            double cosPhi = Math.Cos(phi);

            // Guard against extreme values
            if (double.IsNaN(sinPhi) || double.IsInfinity(sinPhi)) sinPhi = 0;
            if (double.IsNaN(cosPhi) || double.IsInfinity(cosPhi)) cosPhi = 1;

            // Round up to "nice" axis limits
            double maxSigma = Math.Ceiling(sig1 * 1.2 / 5) * 5;
            if (maxSigma <= 0) maxSigma = 10; // Ensure positive value

            double maxTau = Math.Ceiling((_cohesion * cosPhi + maxSigma * sinPhi) / 5) * 5;
            if (maxTau <= 0) maxTau = 5; // Ensure positive value

            // Scaling factors
            double scaleX = graphWidth / maxSigma;
            double scaleY = graphHeight / (2 * maxTau);

            // Origin (0,0) in screen coords
            double originX = margin;
            double originY = height - margin;

            //
            // 1) Draw axes + arrowheads
            //
            using (Pen axisPen = new Pen(Color.White, 1))
            {
                // X-axis
                g.DrawLine(axisPen,
                           (float)originX, (float)originY,
                           (float)(originX + graphWidth), (float)originY);
                // X arrow
                const float arrowSize = 6;
                PointF[] arrowX = {
                    new PointF((float)(originX+graphWidth),           (float)originY),
                    new PointF((float)(originX+graphWidth - arrowSize),(float)(originY - arrowSize)),
                    new PointF((float)(originX+graphWidth - arrowSize),(float)(originY + arrowSize))
                };
                g.FillPolygon(Brushes.White, arrowX);

                // Y-axis
                g.DrawLine(axisPen,
                           (float)originX, (float)originY,
                           (float)originX, (float)(originY - graphHeight));
                // Y arrow
                PointF[] arrowY = {
                    new PointF((float)originX,                (float)(originY-graphHeight)),
                    new PointF((float)(originX - arrowSize), (float)(originY-graphHeight + arrowSize)),
                    new PointF((float)(originX + arrowSize), (float)(originY-graphHeight + arrowSize))
                };
                g.FillPolygon(Brushes.White, arrowY);
            }

            //
            // 2) Axis labels
            //
            using (Font labelFont = new Font("Segoe UI", 10))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            {
                // X-axis label
                g.DrawString("Normal Stress (σ) [MPa]", labelFont, textBrush,
                             new PointF((float)(originX + graphWidth / 2 - 60), (float)(originY + 10)));

                // Y-axis label (rotated)
                StringFormat sf = new StringFormat { Alignment = StringAlignment.Center };
                Matrix m = g.Transform;
                Matrix rot = new Matrix();
                rot.RotateAt(-90, new PointF((float)(originX - 35), (float)(originY - graphHeight / 2)));
                g.Transform = rot;
                g.DrawString("Shear Stress (τ) [MPa]", labelFont, textBrush,
                             new PointF((float)(originX - 35), (float)(originY - graphHeight / 2)), sf);
                g.Transform = m;
            }

            //
            // 3) Tick marks & numeric labels
            //
            using (Pen tickPen = new Pen(Color.White, 1))
            using (Font tickFont = new Font("Segoe UI", 8))
            using (SolidBrush tickBrush = new SolidBrush(Color.White))
            {
                // X-axis ticks
                int xTickCount = 5;
                double xStep = maxSigma / xTickCount;
                for (int i = 0; i <= xTickCount; i++)
                {
                    double val = xStep * i;
                    float x = (float)(originX + val * scaleX);
                    g.DrawLine(tickPen, x, (float)originY, x, (float)(originY + 5));
                    StringFormat sf = new StringFormat { Alignment = StringAlignment.Center };
                    g.DrawString(((int)val).ToString(), tickFont, tickBrush,
                                 new PointF(x, (float)(originY + 7)), sf);
                }

                // Y-axis ticks (positive only)
                int yTickCount = 4;
                double yStep = maxTau / yTickCount;
                for (int i = 0; i <= yTickCount; i++)
                {
                    double val = yStep * i;
                    float y = (float)(originY - val * scaleY);
                    g.DrawLine(tickPen, (float)originX - 5, y, (float)originX, y);
                    g.DrawString(((int)val).ToString(), tickFont, tickBrush,
                                 new PointF((float)originX - 25, y - 6));
                }
            }

            //
            // 4) Mohr circle
            //
            using (Pen circlePen = new Pen(Color.LightBlue, 2))
            {
                float cx = (float)(originX + center * scaleX);
                float cy = (float)originY;
                float rx = (float)(radius * scaleX);
                float ry = (float)(radius * scaleY);
                g.DrawEllipse(circlePen, cx - rx, cy - ry, 2 * rx, 2 * ry);

                // mark principal σ₁,σ₃
                float s1x = (float)(originX + sig1 * scaleX);
                float s3x = (float)(originX + sig3 * scaleX);
                g.FillEllipse(Brushes.Red, s1x - 4, cy - 4, 8, 8);
                g.FillEllipse(Brushes.Red, s3x - 4, cy - 4, 8, 8);
                using (Font f2 = new Font("Segoe UI", 10, FontStyle.Bold))
                using (SolidBrush b2 = new SolidBrush(Color.White))
                {
                    g.DrawString("σ₁", f2, b2, s1x - 6, cy + 5);
                    g.DrawString("σ₃", f2, b2, s3x - 6, cy + 5);
                    g.DrawString($"({(int)sig1} MPa)", new Font("Segoe UI", 8), b2, s1x + 10, cy + 5);
                    g.DrawString($"({(int)sig3} MPa)", new Font("Segoe UI", 8), b2, s3x + 10, cy + 5);
                }
            }

            //
            // 5) Failure envelope
            //
            using (Pen envPen = new Pen(Color.Red, 2))
            using (SolidBrush envBrush = new SolidBrush(Color.Red))
            {
                float x1 = (float)originX;
                float y1 = (float)(originY - _cohesion * scaleY);
                float x2 = (float)(originX + maxSigma * scaleX);
                float y2 = (float)(originY - (_cohesion * cosPhi + maxSigma * sinPhi) * scaleY);
                g.DrawLine(envPen, x1, y1, x2, y2);
                using (Font f3 = new Font("Segoe UI", 8, FontStyle.Bold))
                    g.DrawString("Failure Envelope", f3, envBrush, x1 + 10, y1 - 20);
            }

            //
            // 6) Tangent at failure point (if failure detected)
            //
            if (_failureDetected)
            {
                try
                {
                    using (Pen tPen = new Pen(Color.Yellow, 2))
                    using (SolidBrush tBrush = new SolidBrush(Color.Yellow))
                    using (Font tFont = new Font("Segoe UI", 9, FontStyle.Bold))
                    {
                        // Calculate Mohr circle at failure
                        double failureSig1 = Math.Max(_failureStress, _confiningPressure);
                        double failureSig3 = Math.Min(_failureStress, _confiningPressure);
                        double failureCenter = (failureSig1 + failureSig3) / 2;
                        double failureRadius = Math.Max((failureSig1 - failureSig3) / 2, 0.001); // Ensure non-zero radius

                        // a) slope of envelope (tan of friction angle)
                        double mEnv = Math.Tan(phi);
                        if (double.IsNaN(mEnv) || double.IsInfinity(mEnv)) mEnv = 0;

                        // b) solve for the point where the failure envelope is tangent to the Mohr circle
                        // The distance from center to envelope = radius
                        double k = _cohesion * cosPhi;
                        double sinPhiSq = sinPhi * sinPhi;

                        // Avoid division by zero or near-zero
                        if (Math.Abs(1 - sinPhiSq) < 0.0001)
                        {
                            sinPhiSq = 0.9999;
                        }

                        double sigmaF = (failureCenter * (1 - sinPhiSq) - 2 * k * sinPhi) / (1 - sinPhiSq);

                        // Safety checks
                        if (double.IsNaN(sigmaF) || double.IsInfinity(sigmaF))
                        {
                            sigmaF = failureCenter;
                        }

                        double tauF = k + sigmaF * sinPhi;

                        // Safety checks
                        if (double.IsNaN(tauF) || double.IsInfinity(tauF))
                        {
                            tauF = failureRadius;
                        }

                        // c) Convert to screen coordinates
                        float xF = (float)(originX + sigmaF * scaleX);
                        float yF = (float)(originY - tauF * scaleY);

                        // d) Draw tangent point (failure point)
                        g.FillEllipse(tBrush, xF - 6, yF - 6, 12, 12);

                        // e) Calculate slope of radial line from center to tangent point
                        double dx = sigmaF - failureCenter;
                        double mRadial = 0;

                        // Avoid division by zero
                        if (Math.Abs(dx) > 0.0001)
                        {
                            mRadial = tauF / dx;
                        }
                        else
                        {
                            // Vertical line case
                            mRadial = 1000; // Very steep
                        }

                        // f) Calculate slope of tangent line (perpendicular to radial)
                        double mTangent = 0;

                        // Avoid division by zero
                        if (Math.Abs(mRadial) > 0.0001)
                        {
                            mTangent = -1.0 / mRadial;
                        }
                        else
                        {
                            // Horizontal radial case
                            mTangent = -1000; // Very steep
                        }

                        // g) Find endpoints for tangent line spanning entire visible area
                        double x0 = 0;
                        double t0 = tauF + mTangent * (x0 - sigmaF);
                        double x1 = maxSigma;
                        double t1 = tauF + mTangent * (x1 - sigmaF);

                        // Convert to screen coordinates
                        float xTan0 = (float)(originX + x0 * scaleX);
                        float yTan0 = (float)(originY - t0 * scaleY);
                        float xTan1 = (float)(originX + x1 * scaleX);
                        float yTan1 = (float)(originY - t1 * scaleY);

                        // Bounds check - ensure points are within drawable area
                        if (yTan0 < 0) yTan0 = 0;
                        if (yTan0 > height) yTan0 = height;
                        if (yTan1 < 0) yTan1 = 0;
                        if (yTan1 > height) yTan1 = height;

                        // h) Draw the tangent line
                        g.DrawLine(tPen, xTan0, yTan0, xTan1, yTan1);

                        // i) Add label
                        g.DrawString("Tangent at Failure", tFont, tBrush, xF + 8, yF - 15);

                        // j) Draw a line from center to tangent point to show the radius
                        float xCenter = (float)(originX + failureCenter * scaleX);
                        using (Pen radialPen = new Pen(Color.FromArgb(128, 255, 255, 0), 1))
                        {
                            g.DrawLine(radialPen, xCenter, (float)originY, xF, yF);
                        }

                        // k) Add failure data label
                        using (Font dataFont = new Font("Segoe UI", 8))
                        {
                            string failureData = $"Failure: σ={sigmaF:F1}, τ={tauF:F1} MPa";
                            g.DrawString(failureData, dataFont, tBrush, xF + 8, yF + 2);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // If there's an error in the tangent calculation, log it but continue
                    Logger.Log($"[MohrCoulombVisualization] Error calculating tangent: {ex.Message}");

                    // Add a message on the visualization about the error
                    using (Font errorFont = new Font("Segoe UI", 8))
                    using (SolidBrush errorBrush = new SolidBrush(Color.Red))
                    {
                        g.DrawString("Error calculating tangent line", errorFont, errorBrush, (float)originX + 20, (float)originY - 40);
                    }
                }
            }
            else
            {
                // Display analytical failure point even when failure is not detected
                try
                {
                    using (Pen tPen = new Pen(Color.Yellow, 2))
                    using (SolidBrush tBrush = new SolidBrush(Color.Yellow))
                    using (Font tFont = new Font("Segoe UI", 9, FontStyle.Bold))
                    {
                        // a) slope of envelope
                        double mEnv = Math.Tan(phi);
                        if (double.IsNaN(mEnv) || double.IsInfinity(mEnv)) mEnv = 0;

                        // b) solve for σf, τf
                        double sigmaF = 0;
                        double denom = (1 + mEnv * mEnv);

                        // Avoid division by zero
                        if (Math.Abs(denom) > 0.0001)
                        {
                            sigmaF = (center - _cohesion * mEnv) / denom;
                        }
                        else
                        {
                            sigmaF = center;
                        }

                        double tauF = _cohesion + mEnv * sigmaF;

                        // c) screen
                        float xF = (float)(originX + sigmaF * scaleX);
                        float yF = (float)(originY - tauF * scaleY);

                        // d) draw point
                        g.FillEllipse(tBrush, xF - 5, yF - 5, 10, 10);

                        // e) radial slope
                        double dx = sigmaF - center;
                        double mRadial = 0;

                        // Avoid division by zero
                        if (Math.Abs(dx) > 0.0001)
                        {
                            mRadial = tauF / dx;
                        }
                        else
                        {
                            mRadial = 1000; // Very steep
                        }

                        // Calculate tangent slope (perpendicular to radial)
                        double mTangent = 0;
                        if (Math.Abs(mRadial) > 0.0001)
                        {
                            mTangent = -1.0 / mRadial;
                        }
                        else
                        {
                            mTangent = -1000; // Very steep
                        }

                        // f) endpoints from σ=0…maxSigma
                        double s0 = 0, t0 = tauF + mTangent * (s0 - sigmaF);
                        double s1 = maxSigma, t1 = tauF + mTangent * (s1 - sigmaF);
                        float x0 = (float)(originX + s0 * scaleX);
                        float y0 = (float)(originY - t0 * scaleY);
                        float x1 = (float)(originX + s1 * scaleX);
                        float y1 = (float)(originY - t1 * scaleY);

                        // Bounds check
                        if (y0 < 0) y0 = 0;
                        if (y0 > height) y0 = height;
                        if (y1 < 0) y1 = 0;
                        if (y1 > height) y1 = height;

                        // g) draw line + label
                        g.DrawLine(tPen, x0, y0, x1, y1);
                        g.DrawString("Analytical Failure Point", tFont, tBrush, xF + 6, yF - 14);
                    }
                }
                catch (Exception ex)
                {
                    // If there's an error in the analytical failure point calculation, log it but continue
                    Logger.Log($"[MohrCoulombVisualization] Error calculating analytical failure point: {ex.Message}");
                }
            }

            //
            // 7) Stress‐state & parameter info
            //
            using (Font f4 = new Font("Segoe UI", 9))
            using (SolidBrush b4 = new SolidBrush(Color.White))
            {
                string state = $"Current Stress: σ₁={(int)sig1} MPa, σ₃={(int)sig3} MPa";
                g.DrawString(state, f4, b4, (float)originX + 10, (float)(originY - graphHeight + 10));
                string param = $"Cohesion: {(int)_cohesion} MPa, Friction Angle: {(int)_frictionAngle}°";
                g.DrawString(param, f4, b4, (float)originX + 10, (float)(originY - graphHeight + 30));

                if (_failureDetected)
                {
                    using (SolidBrush failureBrush = new SolidBrush(Color.Red))
                    {
                        string failureState = $"FAILURE DETECTED at {(int)_failureStress} MPa";
                        g.DrawString(failureState, new Font("Segoe UI", 10, FontStyle.Bold),
                                    failureBrush, (float)originX + 10, (float)(originY - graphHeight + 50));
                    }
                }
            }
        }
    }
}