// DiagramsForm.cs
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Krypton.Toolkit;

namespace CTS
{
    public partial class TriaxialDiagramsForm : KryptonForm
    {
        // Data from simulation
        private float currentStrain = 0.0f;
        private float currentStress = 0.0f;
        private List<Point> stressStrainCurve = new List<Point>();
        private float cohesion = 50.0f;
        private float frictionAngle = 30.0f;
        private float normalStress = 0.0f;
        private float shearStress = 0.0f;
        private float bulkDensity = 2500.0f;
        private float porosity = 0.2f;
        private float minPressure = 0.0f;
        private float maxPressure = 1000.0f;
        private float yieldStrength = 500.0f;
        private float brittleStrength = 800.0f;
        private bool isElasticEnabled = true;
        private bool isPlasticEnabled = false;
        private bool isBrittleEnabled = false;
        private bool simulationRunning = false;

        // UI Elements
        private PictureBox stressStrainGraph;
        private PictureBox mohrCoulombGraph;
        private KryptonButton btnSaveImage;
        private SaveFileDialog saveFileDialog;

        public TriaxialDiagramsForm()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.Text = "Simulation Diagrams";
            this.Size = new Size(800, 650);
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.Black;
            this.ForeColor = Color.White;

            // Create main layout
            TableLayoutPanel mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 3,
                ColumnCount = 1,
                BackColor = Color.Black
            };

            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 48F));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 48F));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 4F));

            // Stress-Strain Graph
            Panel stressStrainPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black
            };

            Label lblStressStrain = new Label
            {
                Text = "Stress-Strain Curve",
                Dock = DockStyle.Top,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 12, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                Height = 30
            };
            stressStrainPanel.Controls.Add(lblStressStrain);

            stressStrainGraph = new PictureBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(5, 35, 5, 5)
            };
            stressStrainGraph.Paint += StressStrainGraph_Paint;
            stressStrainPanel.Controls.Add(stressStrainGraph);

            // Mohr-Coulomb Graph
            Panel mohrCoulombPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black
            };

            Label lblMohrCoulomb = new Label
            {
                Text = "Mohr-Coulomb Diagram",
                Dock = DockStyle.Top,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 12, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                Height = 30
            };
            mohrCoulombPanel.Controls.Add(lblMohrCoulomb);

            mohrCoulombGraph = new PictureBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(5, 35, 5, 5)
            };
            mohrCoulombGraph.Paint += MohrCoulombGraph_Paint;
            mohrCoulombPanel.Controls.Add(mohrCoulombGraph);

            // Button panel
            Panel buttonPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black
            };

            btnSaveImage = new KryptonButton
            {
                Text = "Save Image",
                Location = new Point(10, 0),
                Size = new Size(120, 15),
                StateCommon = {
                    Back = { Color1 = Color.FromArgb(80, 80, 120) },
                    Content = { ShortText = { Color1 = Color.White } }
                }
            };
            btnSaveImage.Click += BtnSaveImage_Click;
            buttonPanel.Controls.Add(btnSaveImage);

            // Add panels to main layout
            mainLayout.Controls.Add(stressStrainPanel, 0, 0);
            mainLayout.Controls.Add(mohrCoulombPanel, 0, 1);
            mainLayout.Controls.Add(buttonPanel, 0, 2);

            // Add main layout to form
            this.Controls.Add(mainLayout);

            // Create save file dialog
            saveFileDialog = new SaveFileDialog
            {
                Filter = "PNG Image|*.png|JPEG Image|*.jpg|Bitmap Image|*.bmp",
                Title = "Save Diagrams Image",
                DefaultExt = "png"
            };

            // Handle form closing - hide instead of close
            this.FormClosing += (s, e) => {
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    this.Hide();
                }
            };
        }

        // Method to update data from simulation
        public void UpdateData(
            List<Point> stressStrainData,
            float strain,
            float stress,
            float coh,
            float frAngle,
            float normStress,
            float shrStress,
            float density,
            float por,
            float minP,
            float maxP,
            float yieldS,
            float brittleS,
            bool elastic,
            bool plastic,
            bool brittle,
            bool running)
        {
            stressStrainCurve = new List<Point>(stressStrainData);
            currentStrain = strain;
            currentStress = stress;
            cohesion = coh;
            frictionAngle = frAngle;
            normalStress = normStress;
            shearStress = shrStress;
            bulkDensity = density;
            porosity = por;
            minPressure = minP;
            maxPressure = maxP;
            yieldStrength = yieldS;
            brittleStrength = brittleS;
            isElasticEnabled = elastic;
            isPlasticEnabled = plastic;
            isBrittleEnabled = brittle;
            simulationRunning = running;

            // Redraw diagrams
            stressStrainGraph.Invalidate();
            mohrCoulombGraph.Invalidate();
        }

        // Save combined diagrams as image
        private void BtnSaveImage_Click(object sender, EventArgs e)
        {
            if (saveFileDialog.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    // Create a composite image
                    using (Bitmap compositeBitmap = new Bitmap(this.Width, this.Height))
                    {
                        using (Graphics g = Graphics.FromImage(compositeBitmap))
                        {
                            g.Clear(Color.Black);

                            // Draw stress-strain graph
                            Bitmap stressStrainBitmap = new Bitmap(stressStrainGraph.Width, stressStrainGraph.Height);
                            stressStrainGraph.DrawToBitmap(stressStrainBitmap, new Rectangle(0, 0, stressStrainGraph.Width, stressStrainGraph.Height));
                            g.DrawImage(stressStrainBitmap,
                                new Rectangle(10, 40, stressStrainGraph.Width - 20, stressStrainGraph.Height - 20),
                                new Rectangle(0, 0, stressStrainGraph.Width, stressStrainGraph.Height),
                                GraphicsUnit.Pixel);

                            // Draw mohr-coulomb graph
                            Bitmap mohrCoulombBitmap = new Bitmap(mohrCoulombGraph.Width, mohrCoulombGraph.Height);
                            mohrCoulombGraph.DrawToBitmap(mohrCoulombBitmap, new Rectangle(0, 0, mohrCoulombGraph.Width, mohrCoulombGraph.Height));
                            g.DrawImage(mohrCoulombBitmap,
                                new Rectangle(10, stressStrainGraph.Height + 50, mohrCoulombGraph.Width - 20, mohrCoulombGraph.Height - 20),
                                new Rectangle(0, 0, mohrCoulombGraph.Width, mohrCoulombGraph.Height),
                                GraphicsUnit.Pixel);

                            // Add title and timestamp
                            using (Font titleFont = new Font("Segoe UI", 14, FontStyle.Bold))
                            {
                                g.DrawString("Triaxial Simulation Results", titleFont, Brushes.White, 10, 10);
                                g.DrawString("Date: " + DateTime.Now.ToString(), new Font("Segoe UI", 10), Brushes.White, 10, this.Height - 50);
                            }
                        }

                        // Save the image in the selected format
                        ImageFormat format = ImageFormat.Png;
                        if (saveFileDialog.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                            format = ImageFormat.Jpeg;
                        else if (saveFileDialog.FileName.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
                            format = ImageFormat.Bmp;

                        compositeBitmap.Save(saveFileDialog.FileName, format);
                    }

                    MessageBox.Show("Image saved successfully!", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error saving image: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void StressStrainGraph_Paint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Color.Black);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Set up coordinate system with padding
            int padding = 40;
            int width = stressStrainGraph.Width - 2 * padding;
            int height = stressStrainGraph.Height - 2 * padding;

            // Draw grid
            using (Pen gridPen = new Pen(Color.FromArgb(60, 60, 60), 1))
            {
                // Vertical grid lines (strain)
                for (int i = 0; i <= 4; i++)
                {
                    int x = padding + i * width / 4;
                    g.DrawLine(gridPen, x, padding, x, stressStrainGraph.Height - padding);
                }

                // Horizontal grid lines (stress)
                for (int i = 0; i <= 6; i++)
                {
                    int y = stressStrainGraph.Height - padding - i * height / 6;
                    g.DrawLine(gridPen, padding, y, stressStrainGraph.Width - padding, y);
                }
            }

            // Draw axes
            using (Pen axisPen = new Pen(Color.White, 2))
            {
                // X-axis (strain)
                g.DrawLine(axisPen, padding, stressStrainGraph.Height - padding,
                           stressStrainGraph.Width - padding, stressStrainGraph.Height - padding);

                // Y-axis (stress)
                g.DrawLine(axisPen, padding, stressStrainGraph.Height - padding,
                           padding, padding);
            }

            // Draw axis labels
            using (Font labelFont = new Font("Arial", 9))
            {
                // X-axis label (strain)
                g.DrawString("Strain (%)", labelFont, Brushes.White,
                            stressStrainGraph.Width / 2 - 30, stressStrainGraph.Height - 25);

                // Y-axis label (stress)
                g.TranslateTransform(15, stressStrainGraph.Height / 2);
                g.RotateTransform(-90);
                g.DrawString("Stress (MPa)", labelFont, Brushes.White, 0, 0);
                g.ResetTransform();

                // X-axis values
                for (int i = 0; i <= 4; i++)
                {
                    int x = padding + i * width / 4;
                    string value = (i * 5).ToString() + "%";
                    g.DrawString(value, labelFont, Brushes.White, x - 10, stressStrainGraph.Height - padding + 5);
                }

                // Y-axis values - calculate a reasonable scale
                float maxStressValue = Math.Max(1000, Math.Max(yieldStrength, brittleStrength) * 1.2f);
                if (stressStrainCurve.Count > 0)
                {
                    float maxCurrentStress = stressStrainCurve.Max(p => p.Y) / 10.0f;
                    maxStressValue = Math.Max(maxStressValue, maxCurrentStress * 1.2f);
                }
                maxStressValue = (float)Math.Ceiling(maxStressValue / 100) * 100;

                for (int i = 0; i <= 6; i++)
                {
                    int y = stressStrainGraph.Height - padding - i * height / 6;
                    string value = ((int)(i * maxStressValue / 6)).ToString();
                    g.DrawString(value, labelFont, Brushes.White, padding - 35, y - 7);
                }
            }

            // Draw title with material properties
            using (Font titleFont = new Font("Arial", 9, FontStyle.Bold))
            {
                string densityInfo = $"Material: Test | Density: {bulkDensity:F0} kg/m³ | Porosity: {porosity:P1}";
                g.DrawString(densityInfo, titleFont, Brushes.Cyan, padding, 10);
            }

            // Draw yield and brittle strength lines if behaviors are enabled
            if (isPlasticEnabled || isBrittleEnabled)
            {
                // Calculate max stress for scaling
                float maxStressValue = Math.Max(1000, Math.Max(yieldStrength, brittleStrength) * 1.2f);
                if (stressStrainCurve.Count > 0)
                {
                    float maxCurrentStress = stressStrainCurve.Max(p => p.Y) / 10.0f;
                    maxStressValue = Math.Max(maxStressValue, maxCurrentStress * 1.2f);
                }
                maxStressValue = (float)Math.Ceiling(maxStressValue / 100) * 100;

                int maxStress = (int)(maxStressValue * 10); // Convert to graph units

                // Draw yield strength line if plastic behavior is enabled
                if (isPlasticEnabled && yieldStrength > 0)
                {
                    using (Pen yieldPen = new Pen(Color.Orange, 1) { DashStyle = DashStyle.Dash })
                    {
                        int y = stressStrainGraph.Height - padding - (int)(yieldStrength * 10 * height / maxStress);
                        g.DrawLine(yieldPen, padding, y, stressStrainGraph.Width - padding, y);
                        g.DrawString($"Yield: {yieldStrength:F0} MPa", new Font("Arial", 8), Brushes.Orange,
                                    stressStrainGraph.Width - padding - 140, y - 15);
                    }
                }

                // Draw brittle strength line if brittle behavior is enabled
                if (isBrittleEnabled && brittleStrength > 0)
                {
                    using (Pen brittlePen = new Pen(Color.Red, 1) { DashStyle = DashStyle.Dash })
                    {
                        int y = stressStrainGraph.Height - padding - (int)(brittleStrength * 10 * height / maxStress);
                        g.DrawLine(brittlePen, padding, y, stressStrainGraph.Width - padding, y);
                        g.DrawString($"Failure: {brittleStrength:F0} MPa", new Font("Arial", 8), Brushes.Red,
                                    stressStrainGraph.Width - padding - 140, y - 15);
                    }
                }
            }

            // Draw stress-strain curve
            if (stressStrainCurve.Count >= 2)
            {
                using (Pen curvePen = new Pen(Color.Cyan, 2))
                {
                    // Calculate max stress for scaling
                    float maxStressValue = Math.Max(1000, Math.Max(yieldStrength, brittleStrength) * 1.2f);
                    if (stressStrainCurve.Count > 0)
                    {
                        float maxCurrentStress = stressStrainCurve.Max(p => p.Y) / 10.0f;
                        maxStressValue = Math.Max(maxStressValue, maxCurrentStress * 1.2f);
                    }
                    maxStressValue = (float)Math.Ceiling(maxStressValue / 100) * 100;

                    // Scale points to fit in the graph
                    int maxStrain = 200; // 20% strain
                    int maxStress = (int)(maxStressValue * 10); // Convert to graph units

                    List<Point> scaledPoints = new List<Point>();
                    foreach (var point in stressStrainCurve)
                    {
                        int x = padding + (int)(point.X * width / maxStrain);
                        int y = stressStrainGraph.Height - padding - (int)(point.Y * height / maxStress);
                        scaledPoints.Add(new Point(x, y));
                    }

                    // Draw curve
                    g.DrawLines(curvePen, scaledPoints.ToArray());

                    // Draw current point
                    if (simulationRunning && scaledPoints.Count > 0)
                    {
                        Point lastPoint = scaledPoints.Last();
                        using (SolidBrush pointBrush = new SolidBrush(Color.Yellow))
                        {
                            g.FillEllipse(pointBrush, lastPoint.X - 5, lastPoint.Y - 5, 10, 10);
                        }

                        // Display current values
                        using (Font valueFont = new Font("Arial", 10, FontStyle.Bold))
                        {
                            float currentStressVal = stressStrainCurve.Last().Y / 10.0f; // Convert to MPa
                            float currentStrainVal = stressStrainCurve.Last().X / 10.0f; // Convert to %

                            string stressStr = $"{currentStressVal:F1} MPa";
                            string strainStr = $"{currentStrainVal:F1}%";

                            // Background for readability
                            using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(80, 0, 0, 0)))
                            {
                                g.FillRectangle(bgBrush, padding + 5, padding + 5, 200, 80);
                            }

                            g.DrawString("Strain: " + strainStr, valueFont, Brushes.White, padding + 10, padding + 10);
                            g.DrawString("Stress: " + stressStr, valueFont, Brushes.White, padding + 10, padding + 35);

                            // Show active behaviors
                            string behaviors = "Behaviors: ";
                            if (isElasticEnabled) behaviors += "Elastic ";
                            if (isPlasticEnabled) behaviors += "Plastic ";
                            if (isBrittleEnabled) behaviors += "Brittle";
                            g.DrawString(behaviors, valueFont, Brushes.Cyan, padding + 10, padding + 60);
                        }
                    }
                }
            }
        }

        private void MohrCoulombGraph_Paint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Color.Black);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Set up coordinate system with padding
            int padding = 40;
            int width = mohrCoulombGraph.Width - 2 * padding;
            int height = mohrCoulombGraph.Height - 2 * padding;

            // Draw grid
            using (Pen gridPen = new Pen(Color.FromArgb(60, 60, 60), 1))
            {
                // Vertical grid lines (normal stress)
                for (int i = 0; i <= 4; i++)
                {
                    int x = padding + i * width / 4;
                    g.DrawLine(gridPen, x, padding, x, mohrCoulombGraph.Height - padding);
                }

                // Horizontal grid lines (shear stress)
                for (int i = 0; i <= 4; i++)
                {
                    int y = mohrCoulombGraph.Height - padding - i * height / 4;
                    g.DrawLine(gridPen, padding, y, mohrCoulombGraph.Width - padding, y);
                }
            }

            // Draw axes
            using (Pen axisPen = new Pen(Color.White, 2))
            {
                // X-axis (normal stress)
                g.DrawLine(axisPen, padding, mohrCoulombGraph.Height - padding,
                           mohrCoulombGraph.Width - padding, mohrCoulombGraph.Height - padding);

                // Y-axis (shear stress)
                g.DrawLine(axisPen, padding, mohrCoulombGraph.Height - padding,
                           padding, padding);
            }

            // Draw axis labels
            using (Font labelFont = new Font("Arial", 9))
            {
                // X-axis label
                g.DrawString("Normal Stress (MPa)", labelFont, Brushes.White,
                            mohrCoulombGraph.Width / 2 - 50, mohrCoulombGraph.Height - 25);

                // Y-axis label (rotated)
                g.TranslateTransform(15, mohrCoulombGraph.Height / 2);
                g.RotateTransform(-90);
                g.DrawString("Shear Stress (MPa)", labelFont, Brushes.White, 0, 0);
                g.ResetTransform();
            }

            // Calculate maximum stress for scaling
            float maxStress = Math.Max(1000, (float)maxPressure * 2);

            // Draw Mohr-Coulomb failure envelope
            using (Pen failurePen = new Pen(Color.Red, 2))
            {
                // Convert friction angle from degrees to radians
                float frictionAngleRad = (float)(frictionAngle * Math.PI / 180.0);

                // Calculate points for the failure line
                float tanPhi = (float)Math.Tan(frictionAngleRad);

                // Start at cohesion on y-axis
                int x1 = padding;
                int y1 = mohrCoulombGraph.Height - padding - (int)(cohesion * height / maxStress);

                // End at right edge
                int x2 = mohrCoulombGraph.Width - padding;
                float normalStressAtX2 = (x2 - padding) * maxStress / width;
                int y2 = mohrCoulombGraph.Height - padding - (int)((cohesion + normalStressAtX2 * tanPhi) * height / maxStress);

                // Draw the failure envelope line
                g.DrawLine(failurePen, x1, y1, x2, y2);

                // Label the line
                string equation = $"τ = {cohesion:F1} + σ·tan({frictionAngle:F1}°)";
                g.DrawString(equation, new Font("Arial", 9, FontStyle.Bold), Brushes.Red, x1 + 10, y1 - 20);
            }

            // Draw tensile cutoff if applicable
            float tensileStrength = cohesion / (float)Math.Tan(frictionAngle * Math.PI / 180.0f);
            if (tensileStrength > 0)
            {
                using (Pen tensionPen = new Pen(Color.Blue, 2))
                {
                    int tensileX = padding - (int)(tensileStrength * width / maxStress);
                    tensileX = Math.Max(padding - 50, tensileX); // Don't go too far left

                    g.DrawLine(tensionPen,
                        tensileX, mohrCoulombGraph.Height - padding,
                        padding, mohrCoulombGraph.Height - padding - (int)(cohesion * height / maxStress));

                    // Add label
                    g.DrawString("Tensile\nStrength", new Font("Arial", 8), Brushes.Blue,
                        tensileX, mohrCoulombGraph.Height - padding - 40);
                }
            }

            // Draw Mohr circles if simulation is running
            if (simulationRunning && stressStrainCurve.Count > 0)
            {
                // Get current stress values for the Mohr circle
                float sigma1 = maxPressure; // Major principal stress (vertical load)
                float sigma3 = minPressure; // Minor principal stress (confining pressure)

                float currentStressVal = stressStrainCurve.Last().Y / 10.0f; // Convert from graph units to MPa

                // For a triaxial test, major principal stress increases during loading
                sigma1 = sigma3 + currentStressVal;

                // Calculate circle center and radius
                float center = (sigma1 + sigma3) / 2;
                float radius = (sigma1 - sigma3) / 2;

                // Scale to pixel coordinates
                int centerX = padding + (int)(center * width / maxStress);
                int radiusPixels = (int)(radius * width / maxStress);

                // Draw the Mohr circle
                using (Pen circlePen = new Pen(Color.Cyan, 2))
                {
                    g.DrawEllipse(circlePen,
                        centerX - radiusPixels,
                        mohrCoulombGraph.Height - padding - radiusPixels,
                        radiusPixels * 2,
                        radiusPixels * 2);
                }

                // Draw the center line
                using (Pen centerPen = new Pen(Color.Gray, 1))
                {
                    g.DrawLine(centerPen,
                        centerX,
                        mohrCoulombGraph.Height - padding - radiusPixels,
                        centerX,
                        mohrCoulombGraph.Height - padding + radiusPixels);
                }

                // Label principal stresses
                using (Font stressFont = new Font("Arial", 8))
                {
                    g.DrawString($"σ₃ = {sigma3:F1} MPa", stressFont, Brushes.Cyan,
                        padding + (int)(sigma3 * width / maxStress) - 50,
                        mohrCoulombGraph.Height - padding + 5);

                    g.DrawString($"σ₁ = {sigma1:F1} MPa", stressFont, Brushes.Cyan,
                        padding + (int)(sigma1 * width / maxStress) - 50,
                        mohrCoulombGraph.Height - padding + 5);
                }

                // Display current stress state
                using (Font valueFont = new Font("Arial", 10, FontStyle.Bold))
                {
                    g.DrawString($"Normal Stress: {center:F1} MPa", valueFont, Brushes.White, padding, padding);
                    g.DrawString($"Shear Stress: {radius:F1} MPa", valueFont, Brushes.White, padding, padding + 20);

                    // Check if failure envelope is exceeded
                    float failureShear = cohesion + center * (float)Math.Tan(frictionAngle * Math.PI / 180.0);
                    if (radius >= failureShear)
                    {
                        g.DrawString("Status: FAILURE", valueFont, Brushes.Red, padding, padding + 40);
                    }
                    else
                    {
                        g.DrawString("Status: Stable", valueFont, Brushes.Green, padding, padding + 40);
                    }
                }
            }
        }
    }
}