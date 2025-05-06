using CTS;
using Krypton.Toolkit;
using Microsoft.Office.Interop.Word;
using Microsoft.Vbe.Interop;
using OpenTK;
using OpenTK.Graphics.OpenGL;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using static MaterialDensityLibrary;
using Font = System.Drawing.Font;
using Point = System.Drawing.Point;
using System.Collections.Concurrent;
using Rectangle = System.Drawing.Rectangle;
using System.IO;
using static CTS.MeshGenerator;


namespace CTS
{
    public partial class TriaxialSimulationForm : KryptonForm, IMaterialDensityProvider
    {
        private MainForm mainForm;
        private Material selectedMaterial;
        private KryptonButton btnGenerateMesh;
        private object labelVolume;
        private byte[,,] densityVolume;
        private double pixelSize;
        private int width, height, depth;
        private List<Vector3> vertices = new List<Vector3>();
        private List<Vector3> normals = new List<Vector3>();
        private List<int> indices = new List<int>();
        private List<float> densityValues = new List<float>();
        private List<TetrahedralElement> tetrahedralElements = new List<TetrahedralElement>();
        private KryptonButton btnSaveImage;
        private GLControl glControl;
        private float rotationX = 0.0f;
        private float rotationY = 0.0f;
        private float zoom = 1.0f;
        private bool wireframeMode = true;
        private float minDensity = float.MaxValue;
        private float maxDensity = float.MinValue;
        private bool glControlInitialized = false;
        private Panel colorLegendPanel;

        // Density calibration data
        private List<CalibrationPoint> calibrationPoints = new List<CalibrationPoint>();
        private bool isDensityCalibrated = false;
        private double densityCalibrationSlope = 1.0;
        private double densityCalibrationIntercept = 0.0;
        private double materialVolume = 0.0; // in m³

        // Simulation parameters
        private enum SimulationDirection { X, Y, Z }
        private SimulationDirection selectedDirection = SimulationDirection.Z;
        private bool isElasticEnabled = true;
        private bool isPlasticEnabled = false;
        private bool isBrittleEnabled = false;
        private float minPressure = 0.0f;
        private float maxPressure = 1000.0f; // kPa
        private float youngModulus = 10000.0f; // MPa
        private float poissonRatio = 0.3f;
        private float yieldStrength = 500.0f; // MPa
        private float brittleStrength = 800.0f; // MPa
        private float currentStrain = 0.0f;
        private float maxStrain = 0.2f; // 20% strain
        private bool simulationRunning = false;
        private List<System.Drawing.Point> stressStrainCurve = new List<Point>();
        private List<Vector3> deformedVertices = new List<Vector3>();
        private System.Windows.Forms.Timer simulationTimer;

        // Petrophysical parameters
        private float cohesion = 50.0f; // MPa
        private float frictionAngle = 30.0f; // degrees
        private float normalStress = 0.0f; // MPa
        private float shearStress = 0.0f; // MPa
        private float bulkDensity = 2500.0f; // kg/m³ (default value)
        private float porosity = 0.2f; // Default porosity (0-1)
        private float bulkModulus = 10000.0f; // MPa
        private float permeability = 0.01f; // Darcy
        private Dictionary<int, float> elementStresses = new Dictionary<int, float>();
        private PictureBox mohrCoulombGraph;
        private bool isRotating = false;
        private System.Diagnostics.Stopwatch dragTimer = new System.Diagnostics.Stopwatch();
        private const int REDRAW_INTERVAL_MS = 16;
        // Progress tracking
        private ProgressBar progressBar;
        private Label progressLabel;
        private BackgroundWorker meshWorker;
        private bool meshGenerationComplete = false;
        private bool fastSimulationMode = false;
        private KryptonCheckBox chkFastSimulation;

        // Sampling parameters for mesh generation - to improve performance
        private int samplingRate = 2; // Sample every Nth voxel 

        // UI components
        private KryptonComboBox comboMaterials;
        private KryptonComboBox comboDirection;
        private KryptonCheckBox chkElastic;
        private KryptonCheckBox chkPlastic;
        private KryptonCheckBox chkBrittle;
        private KryptonCheckBox chkShowDensity;
        private KryptonNumericUpDown numMinPressure;
        private KryptonNumericUpDown numMaxPressure;
        private KryptonNumericUpDown numYoungModulus;
        private KryptonNumericUpDown numPoissonRatio;
        private KryptonNumericUpDown numYieldStrength;
        private KryptonNumericUpDown numBrittleStrength;
        private KryptonNumericUpDown numCohesion;
        private KryptonNumericUpDown numFrictionAngle;
        private KryptonNumericUpDown numBulkDensity;
        private KryptonNumericUpDown numPorosity;
        private KryptonNumericUpDown numBulkModulus;
        private KryptonNumericUpDown numPermeability;
        private KryptonButton btnStartSimulation;
        private KryptonButton btnStopSimulation;
        private KryptonButton btnDensitySettings;
        private KryptonCheckBox chkWireframe;
        private KryptonTrackBar trackSamplingRate;
        private KryptonPanel renderPanel;
        private KryptonPanel controlPanel;
        private KryptonPanel simulationPanel;
        private PictureBox stressStrainGraph;
        private KryptonGroupBox petrophysicalGroup;
        private Point lastMousePos;
        private bool isDragging = false;
        private int vertexBufferId = 0;
        private int normalBufferId = 0;
        private int colorBufferId = 0;
        private int indexBufferId = 0;
        private bool buffersInitialized = false;
        private System.Windows.Forms.Timer rotationTimer;
        private bool hardwareAccelerated = false;
        private TriaxialDiagramsForm diagramsForm;
        private int fontTextureId = 0;
        private bool fontTextureInitialized = false;
        private Bitmap fontTexture = null;
        private readonly Dictionary<char, Rectangle> characterRects = new Dictionary<char, Rectangle>();

        // Implement IMaterialDensityProvider interface
        public Material SelectedMaterial => selectedMaterial;
        private void InitializeDiagramsForm()
        {
            diagramsForm = new TriaxialDiagramsForm();
        }
        private void AddDiagramsButton()
        {
            // Create the button
            KryptonButton btnShowDiagrams = new KryptonButton
            {
                Text = "Show Diagrams",
                Width = 310,
                Height = 30,
                StateCommon = {
            Back = { Color1 = Color.FromArgb(80, 80, 120) },
            Content = { ShortText = { Color1 = Color.White } }
        }
            };

            // Add the button click event
            btnShowDiagrams.Click += (s, e) => {
                if (diagramsForm == null || diagramsForm.IsDisposed)
                {
                    InitializeDiagramsForm();
                }
                diagramsForm.Show();
                UpdateDiagramsForm();
            };

            // Find a suitable container for the button
            // Option 1: Add to the form directly with absolute positioning
            btnShowDiagrams.Location = new Point(10, this.ClientSize.Height - 50);
            this.Controls.Add(btnShowDiagrams);

            // Or Option 2: Find an existing container panel if available
            // Find the last panel in the form that might be our controls panel
            Panel controlPanel = null;
            foreach (Control control in this.Controls)
            {
                if (control is TableLayoutPanel mainLayout)
                {
                    foreach (Control panelControl in mainLayout.Controls)
                    {
                        if (panelControl is Panel panel &&
                            (panel.Name.Contains("control") || panel.Name.Contains("Control") ||
                             panel.Tag?.ToString() == "controls"))
                        {
                            controlPanel = panel;
                            break;
                        }
                    }
                }
            }

            // If we found a control panel, use that instead
            if (controlPanel != null)
            {
                // Remove from form
                this.Controls.Remove(btnShowDiagrams);

                // Find the right position - find the last button and position after it
                int maxY = 10;
                foreach (Control c in controlPanel.Controls)
                {
                    if (c is Button || c is KryptonButton)
                    {
                        int bottom = c.Location.Y + c.Height;
                        if (bottom > maxY)
                            maxY = bottom;
                    }
                }

                btnShowDiagrams.Location = new Point(10, maxY + 10);
                controlPanel.Controls.Add(btnShowDiagrams);
            }
        }
        private void AddColorLegendPanel()
        {
            // Create a panel for the color legend
            colorLegendPanel = new Panel
            {
                BackColor = Color.FromArgb(40, 40, 40),
                Size = new Size(100, 300),
                Location = new Point(glControl.Width - 105, 10),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Visible = chkShowDensity.Checked
            };

            // Paint handler for the legend
            colorLegendPanel.Paint += ColorLegendPanel_Paint;

            // Add the panel to the render panel (alongside the GLControl)
            renderPanel.Controls.Add(colorLegendPanel);

            // Make sure panel is on top
            colorLegendPanel.BringToFront();

            // Update the checkbox event to toggle visibility
            chkShowDensity.CheckedChanged -= ChkShowDensity_CheckedChanged;
            chkShowDensity.CheckedChanged += (s, e) => {
                colorLegendPanel.Visible = chkShowDensity.Checked;
                glControl.Invalidate();
            };
        }

        private void CreateGLControl()
        {
            // Create a hardware-accelerated graphics mode with anti-aliasing
            var graphicsMode = new OpenTK.Graphics.GraphicsMode(
                new OpenTK.Graphics.ColorFormat(8, 8, 8, 8),
                24, // Depth bits
                8,  // Stencil bits
                4   // Anti-aliasing samples
            );

            // Create GLControl with hardware acceleration
            glControl = new GLControl(graphicsMode);
            glControl.VSync = true;

            glControl.Dock = DockStyle.Fill;
            glControl.Load += GlControl_Load;
            glControl.Paint += GlControl_Paint;
            glControl.Resize += GlControl_Resize;
            glControl.MouseDown += GlControl_MouseDown;
            glControl.MouseMove += GlControl_MouseMove;
            glControl.MouseUp += GlControl_MouseUp;
            glControl.MouseWheel += GlControl_MouseWheel;
        }
        private void SetupRotationTimer()
        {
            rotationTimer = new System.Windows.Forms.Timer();
            rotationTimer.Interval = 16; // ~60 FPS
            rotationTimer.Tick += (s, e) => glControl.Invalidate();
        }
        public double CalculateTotalVolume()
        {
            return materialVolume;
        }

        public void SetMaterialDensity(double density)
        {
            // Store the original density value
            bulkDensity = (float)density;
            isDensityCalibrated = false;

            // Update material properties based on density
            UpdateMaterialPropertiesFromDensity();

            // Ensure the bulk density control is updated within its range
            try
            {
                decimal clampedValue = Math.Min((decimal)bulkDensity, numBulkDensity.Maximum);
                clampedValue = Math.Max(clampedValue, numBulkDensity.Minimum);
                numBulkDensity.Value = clampedValue;
            }
            catch (Exception ex)
            {
                // Log the error but don't disrupt the UI
                Logger.Log($"[TriaxialSimulationForm] Error in SetMaterialDensity: {ex.Message}");

                // Set a safe default value
                bulkDensity = 2500.0f;
                try
                {
                    numBulkDensity.Value = (decimal)bulkDensity;
                }
                catch
                {
                    // Last resort if UI update still fails
                }
            }

            // Refresh the legend to update the display
            if (colorLegendPanel != null)
            {
                colorLegendPanel.Invalidate();
            }
        }

        public void ApplyDensityCalibration(List<CalibrationPoint> points)
        {
            calibrationPoints = new List<CalibrationPoint>(points);

            if (calibrationPoints.Count >= 2)
            {
                // Calculate linear regression for grayscale to density conversion
                double sumX = 0, sumY = 0, sumXX = 0, sumXY = 0;
                int n = calibrationPoints.Count;

                foreach (var point in calibrationPoints)
                {
                    sumX += point.AvgGrayValue;
                    sumY += point.Density;
                    sumXX += point.AvgGrayValue * point.AvgGrayValue;
                    sumXY += point.AvgGrayValue * point.Density;
                }

                // Calculate slope and intercept
                densityCalibrationSlope = (n * sumXY - sumX * sumY) / (n * sumXX - sumX * sumX);
                densityCalibrationIntercept = (sumY - densityCalibrationSlope * sumX) / n;

                isDensityCalibrated = true;

                // Recalculate density values for all vertices
                if (densityValues.Count > 0)
                {
                    RecalibrateDensityValues();
                }

                // Calculate average bulk density
                CalculateAverageBulkDensity();

                // Update UI
                numBulkDensity.Value = (decimal)bulkDensity;

                // Update material properties based on new density
                UpdateMaterialPropertiesFromDensity();

                // Refresh the legend panel to show the new density range
                if (colorLegendPanel != null)
                {
                    colorLegendPanel.Invalidate();
                }
            }
        }
        private void CalculateAverageBulkDensity()
        {
            if (densityValues.Count == 0)
                return;

            float totalDensity = 0;
            foreach (float density in densityValues)
            {
                totalDensity += density;
            }

            bulkDensity = totalDensity / densityValues.Count;
        }

        private void RecalibrateDensityValues()
        {
            for (int i = 0; i < densityValues.Count; i++)
            {
                // Convert from normalized [0-1] to grayscale [0-255]
                float grayValue = densityValues[i] * 255f;

                // Apply calibration
                float calibratedDensity = (float)(grayValue * densityCalibrationSlope + densityCalibrationIntercept);

                // Store calibrated density
                densityValues[i] = calibratedDensity;
            }

            // Update min/max density
            minDensity = densityValues.Min();
            maxDensity = densityValues.Max();

            // Refresh the legend panel
            if (colorLegendPanel != null)
            {
                colorLegendPanel.Invalidate();
            }
        }
        /// <summary>
        /// Compute the center and radius of the mesh’s bounding sphere.
        /// </summary>
        private Vector3 ComputeBoundingSphere(out float radius)
        {
            if (deformedVertices == null || deformedVertices.Count == 0)
            {
                radius = 10.0f;
                return Vector3.Zero;
            }

            // Find the mesh center by averaging vertex positions
            Vector3 meshCenter = Vector3.Zero;
            foreach (var v in deformedVertices)
                meshCenter += v;
            meshCenter /= deformedVertices.Count;

            // Find maximum distance from center with outlier detection
            float maxDist2 = 0f;
            List<float> distances = new List<float>();

            foreach (var v in deformedVertices)
            {
                float d2 = (v - meshCenter).LengthSquared;
                distances.Add(d2);
                if (d2 > maxDist2) maxDist2 = d2;
            }

            // Sort distances and check for outliers
            distances.Sort();
            float medianDist2 = distances[distances.Count / 2];

            // If max distance is more than 100x the median, we likely have outliers
            if (maxDist2 > medianDist2 * 100 && distances.Count > 10)
            {
                // Use 95th percentile instead of maximum to avoid outliers
                int idx95 = (int)(distances.Count * 0.95);
                maxDist2 = distances[idx95];
                Logger.Log($"[TriaxialSimulationForm] Outliers detected! Using 95th percentile for radius.");
            }

            // Compute radius and set a reasonable minimum value
            radius = (float)Math.Sqrt(maxDist2);
            if (radius < 1.0f)
            {
                Logger.Log($"[TriaxialSimulationForm] Mesh is too small (r={radius}). Scaling to visible size.");
                radius = 1.0f;
            }

            //Logger.Log($"[TriaxialSimulationForm] Mesh center: {meshCenter}, radius: {radius}");
            return meshCenter;
        }

        /// <summary>
        /// Given a sphere radius and a vertical FOV, returns the distance
        /// so that the sphere just fits in view (with 10% margin).
        /// </summary>
        private float ComputeCameraDistance(float radius, float fovRadians)
        {
            // Calculate distance so sphere fills most of view (with some margin)
            float distance = radius / (float)Math.Sin(fovRadians * 0.5f) * 1.5f;

            // Ensure a minimum distance
            distance = Math.Max(distance, radius * 3.0f);

            // Log for debugging
            //Logger.Log($"[TriaxialSimulationForm] Camera distance: {distance}");

            return distance;
        }
        /// <summary>
        /// Rebuilds the projection matrix based on current control size,
        /// FOV, and the sphere’s extent.
        /// </summary>
        private void UpdateProjection(int width, int height, float fovRadians, float camDistance, float sphereRadius)
        {
            float aspect = width / (float)height;
            float near = Math.Max(0.01f, camDistance - sphereRadius);
            float far = camDistance + sphereRadius;

            GL.MatrixMode(MatrixMode.Projection);
            GL.LoadIdentity();

            Matrix4 proj = Matrix4.CreatePerspectiveFieldOfView(
                fovRadians, aspect, near, far);
            GL.LoadMatrix(ref proj);

            GL.MatrixMode(MatrixMode.Modelview);
        }
        private void UpdateMaterialPropertiesFromDensity()
        {
            // 1. Young's modulus relationship with density (empirical relationship for rocks)
            // E = a * ρ^b, where a and b are empirical constants (typical values: a=0.1, b=3)
            float a = 0.1f;
            float b = 3.0f;
            float scaledDensity = bulkDensity / 1000.0f; // Convert to g/cm³
            youngModulus = a * (float)Math.Pow(scaledDensity, b) * 10000.0f; // Scale to reasonable MPa value

            // 2. Update porosity based on density (assuming grain density of ~2650 kg/m³ for typical rocks)
            float grainDensity = 2650.0f; // kg/m³ (typical for quartz/feldspar)
            porosity = 1.0f - (bulkDensity / grainDensity);
            porosity = Math.Max(0.01f, Math.Min(0.5f, porosity)); // Clamp to reasonable range

            // 3. Bulk modulus from porosity and Young's modulus (Gassmann's equation simplified)
            bulkModulus = youngModulus / (3 * (1 - 2 * poissonRatio));

            // 4. Strength parameters based on density
            yieldStrength = youngModulus * 0.05f; // Typical yield at ~5% of Young's modulus
            brittleStrength = youngModulus * 0.08f; // Typical brittle failure at ~8% of Young's modulus

            // 5. Cohesion and friction angle (based on typical relationships for geomaterials)
            cohesion = yieldStrength * 0.1f; // Cohesion typically ~10% of yield strength
            frictionAngle = 25.0f + (bulkDensity / 2650.0f) * 20.0f; // Range from 25° to 45° based on density

            // 6. Permeability from porosity using Kozeny-Carman equation (simplified)
            // IMPORTANT CHANGE: Convert permeability from Darcy to milliDarcy (mD)
            float porosityTerm = (float)Math.Pow(porosity, 3) / (float)Math.Pow(1 - porosity, 2);
            float permDarcy = 0.1f * porosityTerm;
            permeability = permDarcy * 1000.0f; // Convert from Darcy to milliDarcy

            // Apply minimum bound to prevent errors (assuming 0.0001 mD is the control's minimum)
            permeability = Math.Max(0.1f, permeability);

            // Ensure all values are within the control limits before updating UI
            if (youngModulus > (float)numYoungModulus.Maximum)
                youngModulus = (float)numYoungModulus.Maximum;

            if (bulkModulus > (float)numBulkModulus.Maximum)
                bulkModulus = (float)numBulkModulus.Maximum;

            if (yieldStrength > (float)numYieldStrength.Maximum)
                yieldStrength = (float)numYieldStrength.Maximum;

            if (brittleStrength > (float)numBrittleStrength.Maximum)
                brittleStrength = (float)numBrittleStrength.Maximum;

            // 7. Update UI controls - CONVERTING UNITS AS NEEDED
            try
            {
                numYoungModulus.Value = (decimal)youngModulus;
                numPorosity.Value = (decimal)porosity;
                numBulkModulus.Value = (decimal)bulkModulus;
                numYieldStrength.Value = (decimal)yieldStrength;
                numBrittleStrength.Value = (decimal)brittleStrength;
                numCohesion.Value = (decimal)cohesion;
                numFrictionAngle.Value = (decimal)frictionAngle;
                numPermeability.Value = (decimal)permeability; // Now in milliDarcy
            }
            catch (Exception ex)
            {
                // Log error or show message without interrupting the process
                Logger.Log("[TriaxialSimulationForm] Error updating UI controls: "+ex.Message);
                if (numPermeability.Value < numPermeability.Minimum)
                    numPermeability.Value = numPermeability.Minimum;

                if (numPorosity.Value < numPorosity.Minimum)
                    numPorosity.Value = numPorosity.Minimum;

                // Ensure other controls have valid values
                if (numYoungModulus.Value < numYoungModulus.Minimum)
                    numYoungModulus.Value = numYoungModulus.Minimum;
            }
        }

        public TriaxialSimulationForm(MainForm mainForm)
        {
            Logger.Log("[TriaxialSimulationForm] Constructor Called");
            this.mainForm = mainForm;
            this.pixelSize = mainForm.GetPixelSize();

            InitializeComponent();

            // Check if we got hardware acceleration
            CheckHardwareAcceleration();

            InitializeBackgroundWorker();
            LoadMaterialsFromMainForm();
            InitializeDiagramsForm();
            AddDiagramsButton();
            Logger.Log("[TriaxialSimulationForm] Constructor Ended");
        }
        private void CheckHardwareAcceleration()
        {
            if (glControl != null && glControl.IsHandleCreated)
            {
                // Force current context
                glControl.MakeCurrent();

                // Check if hardware acceleration is working
                string renderer = GL.GetString(StringName.Renderer);
                string vendor = GL.GetString(StringName.Vendor);

                // Software renderers usually contain these strings
                hardwareAccelerated = !(
                    renderer.Contains("Software") ||
                    renderer.Contains("software") ||
                    renderer.Contains("Mesa") ||
                    vendor.Contains("Microsoft")
                );

                // Show warning if we're in software mode
                if (!hardwareAccelerated)
                {
                    MessageBox.Show(
                        "WARNING: OpenGL is running in software mode which will be extremely slow.\n\n" +
                        "Renderer: " + renderer + "\n" +
                        "Vendor: " + vendor + "\n\n" +
                        "Please ensure you have up-to-date graphics drivers installed.",
                        "Hardware Acceleration Not Available",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
            }
        }

        private void InitializeComponent()
        {
            SetupRotationTimer();
            this.Text = "Triaxial Simulation";
            this.Size = new Size(1200, 800);
            this.StartPosition = FormStartPosition.CenterParent;
            this.BackColor = Color.FromArgb(32, 32, 32);

            // Create the main 2-column layout
            TableLayoutPanel mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 1,
                ColumnCount = 2,
                BackColor = Color.FromArgb(32, 32, 32)
            };

            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70F)); // 3D view
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30F)); // Controls & results

            // Create the left panel for OpenGL
            renderPanel = new KryptonPanel
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(3)
            };

            // Create the right panel for controls and results
            Panel rightPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(42, 42, 42)
            };

            // Add panels to main layout
            mainLayout.Controls.Add(renderPanel, 0, 0);
            mainLayout.Controls.Add(rightPanel, 1, 0);

            // Create a vertical layout for controls and results
            TableLayoutPanel rightLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 1,
                ColumnCount = 1,
                BackColor = Color.FromArgb(42, 42, 42)
            };

            rightLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F)); // Controls area
          

            // Create scrollable panel for controls
            Panel scrollablePanel = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Color.FromArgb(42, 42, 42)
            };

            

            // Add to right layout
            rightLayout.Controls.Add(scrollablePanel, 0, 0);
            
            rightPanel.Controls.Add(rightLayout);

            // Create controls for the scrollable panel
            Panel controlsContent = new Panel
            {
                Width = 330,
                Height = 900, // Make this taller to accommodate petrophysical parameters
                BackColor = Color.FromArgb(42, 42, 42),
                AutoSize = false,
                Dock = DockStyle.Top
            };

            scrollablePanel.Controls.Add(controlsContent);

            // ================= CONTROLS CONTENT =================

            // Simulation Settings Label
            Label lblSimSettings = new Label
            {
                Text = "Simulation Settings",
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(10, 10)
            };
            controlsContent.Controls.Add(lblSimSettings);

            // Material selection
            Label lblMaterial = new Label
            {
                Text = "Material:",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(10, 40)
            };
            controlsContent.Controls.Add(lblMaterial);

            comboMaterials = new KryptonComboBox
            {
                Location = new Point(140, 40),
                Width = 180,
                DropDownWidth = 180,
                StateCommon = {
                    ComboBox = { Back = { Color1 = Color.FromArgb(60, 60, 60) } },
                    Item = { Content = { ShortText = { Color1 = Color.White } } }
                }
            };
            comboMaterials.StateActive.ComboBox.Content.Color1 = Color.White;
            comboMaterials.StateNormal.ComboBox.Content.Color1 = Color.White;
            controlsContent.Controls.Add(comboMaterials);

            // Density settings button
            btnDensitySettings = new KryptonButton
            {
                Text = "Density Settings",
                Location = new Point(140, 70),
                Width = 180
            };
            btnDensitySettings.Click += BtnDensitySettings_Click;
            controlsContent.Controls.Add(btnDensitySettings);

            // Show density checkbox
            chkShowDensity = new KryptonCheckBox
            {
                Text = "Show Density Map",
                Location = new Point(140, 100),
                Checked = true,
                StateCommon = { ShortText = { Color1 = Color.White } }
            };
            chkShowDensity.CheckedChanged += ChkShowDensity_CheckedChanged;
            controlsContent.Controls.Add(chkShowDensity);

            // Sampling rate
            Label lblSampling = new Label
            {
                Text = "Quality (Sampling Rate):",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(10, 130)
            };
            controlsContent.Controls.Add(lblSampling);

            trackSamplingRate = new KryptonTrackBar
            {
                Location = new Point(140, 130),
                Width = 180,
                Minimum = 1,
                Maximum = 5,
                Value = 2,
                TickFrequency = 1,
                StateCommon = {
                    Track = { Color1 = Color.FromArgb(80, 80, 80) },
                    Tick = { Color1 = Color.Silver }
                }
            };
            controlsContent.Controls.Add(trackSamplingRate);

            // Direction selection
            Label lblDirection = new Label
            {
                Text = "Test Direction:",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(10, 160)
            };
            controlsContent.Controls.Add(lblDirection);

            comboDirection = new KryptonComboBox
            {
                Location = new Point(140, 160),
                Width = 180,
                StateCommon = {
                    ComboBox = { Back = { Color1 = Color.FromArgb(60, 60, 60) } },
                    Item = { Content = { ShortText = { Color1 = Color.White } } }
                }
            };
            comboDirection.StateActive.ComboBox.Content.Color1 = Color.White;
            comboDirection.StateNormal.ComboBox.Content.Color1 = Color.White;
            comboDirection.Items.AddRange(new object[] { "X", "Y", "Z" });
            comboDirection.SelectedIndex = 2; // Default to Z
            controlsContent.Controls.Add(comboDirection);

            // Material behavior
            Label lblBehavior = new Label
            {
                Text = "Material Behavior:",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(10, 190)
            };
            controlsContent.Controls.Add(lblBehavior);

            // Elastic checkbox
            chkElastic = new KryptonCheckBox
            {
                Text = "Elastic",
                Checked = true,
                Location = new Point(140, 190),
                StateCommon = { ShortText = { Color1 = Color.White } }
            };
            controlsContent.Controls.Add(chkElastic);

            // Plastic checkbox
            chkPlastic = new KryptonCheckBox
            {
                Text = "Plastic",
                Checked = false,
                Location = new Point(200, 190),
                StateCommon = { ShortText = { Color1 = Color.White } }
            };
            controlsContent.Controls.Add(chkPlastic);

            // Brittle checkbox
            chkBrittle = new KryptonCheckBox
            {
                Text = "Brittle",
                Checked = false,
                Location = new Point(260, 190),
                StateCommon = { ShortText = { Color1 = Color.White } }
            };
            controlsContent.Controls.Add(chkBrittle);

            // Material Properties Label
            Label lblMatProperties = new Label
            {
                Text = "Material Properties",
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(10, 230)
            };
            controlsContent.Controls.Add(lblMatProperties);

            // Min pressure
            Label lblMinPressure = new Label
            {
                Text = "Min Pressure (kPa):",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(10, 260)
            };
            controlsContent.Controls.Add(lblMinPressure);

            numMinPressure = new KryptonNumericUpDown
            {
                Location = new Point(140, 260),
                Width = 180,
                Minimum = 0,
                Maximum = 10000,
                Value = 0,
                DecimalPlaces = 1,
                StateCommon = {
                    Content = { Color1 = Color.White },
                    Back = { Color1 = Color.FromArgb(60, 60, 60) }
                }
            };
            controlsContent.Controls.Add(numMinPressure);

            // Max pressure
            Label lblMaxPressure = new Label
            {
                Text = "Max Pressure (kPa):",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(10, 290)
            };
            controlsContent.Controls.Add(lblMaxPressure);

            numMaxPressure = new KryptonNumericUpDown
            {
                Location = new Point(140, 290),
                Width = 180,
                Minimum = 1,
                Maximum = 10000,
                Value = 1000,
                DecimalPlaces = 1,
                StateCommon = {
                    Content = { Color1 = Color.White },
                    Back = { Color1 = Color.FromArgb(60, 60, 60) }
                }
            };
            controlsContent.Controls.Add(numMaxPressure);

            // Young's modulus
            Label lblYoungModulus = new Label
            {
                Text = "Young's Modulus (MPa):",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(10, 320)
            };
            controlsContent.Controls.Add(lblYoungModulus);

            numYoungModulus = new KryptonNumericUpDown
            {
                Location = new Point(140, 320),
                Width = 180,
                Minimum = 100,
                Maximum = 100000,
                Value = 10000,
                DecimalPlaces = 1,
                StateCommon = {
                    Content = { Color1 = Color.White },
                    Back = { Color1 = Color.FromArgb(60, 60, 60) }
                }
            };
            numYoungModulus.ValueChanged += MaterialProperty_ValueChanged;
            controlsContent.Controls.Add(numYoungModulus);

            // Poisson's ratio
            Label lblPoissonRatio = new Label
            {
                Text = "Poisson's Ratio:",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(10, 350)
            };
            controlsContent.Controls.Add(lblPoissonRatio);

            numPoissonRatio = new KryptonNumericUpDown
            {
                Location = new Point(140, 350),
                Width = 180,
                Minimum = 0.01m,
                Maximum = 0.49m,
                Value = 0.3m,
                DecimalPlaces = 2,
                StateCommon = {
                    Content = { Color1 = Color.White },
                    Back = { Color1 = Color.FromArgb(60, 60, 60) }
                }
            };
            numPoissonRatio.ValueChanged += MaterialProperty_ValueChanged;
            controlsContent.Controls.Add(numPoissonRatio);

            // Yield strength
            Label lblYieldStrength = new Label
            {
                Text = "Yield Strength (MPa):",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(10, 380)
            };
            controlsContent.Controls.Add(lblYieldStrength);

            numYieldStrength = new KryptonNumericUpDown
            {
                Location = new Point(140, 380),
                Width = 180,
                Minimum = 10,
                Maximum = 10000,
                Value = 500,
                DecimalPlaces = 1,
                StateCommon = {
                    Content = { Color1 = Color.White },
                    Back = { Color1 = Color.FromArgb(60, 60, 60) }
                }
            };
            controlsContent.Controls.Add(numYieldStrength);

            // Brittle strength
            Label lblBrittleStrength = new Label
            {
                Text = "Brittle Strength (MPa):",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(10, 410)
            };
            controlsContent.Controls.Add(lblBrittleStrength);

            numBrittleStrength = new KryptonNumericUpDown
            {
                Location = new Point(140, 410),
                Width = 180,
                Minimum = 10,
                Maximum = 10000,
                Value = 800,
                DecimalPlaces = 1,
                StateCommon = {
                    Content = { Color1 = Color.White },
                    Back = { Color1 = Color.FromArgb(60, 60, 60) }
                }
            };
            controlsContent.Controls.Add(numBrittleStrength);

            // Petrophysical Properties Label
            Label lblPetroProperties = new Label
            {
                Text = "Petrophysical Properties",
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(10, 450)
            };
            controlsContent.Controls.Add(lblPetroProperties);

            // Bulk Density
            Label lblBulkDensity = new Label
            {
                Text = "Bulk Density (kg/m³):",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(10, 480)
            };
            controlsContent.Controls.Add(lblBulkDensity);

            numBulkDensity = new KryptonNumericUpDown
            {
                Location = new Point(140, 480),
                Width = 180,
                Minimum = 500,
                Maximum = 5000,
                Value = 2500,
                DecimalPlaces = 1,
                StateCommon = {
                    Content = { Color1 = Color.White },
                    Back = { Color1 = Color.FromArgb(60, 60, 60) }
                }
            };
            numBulkDensity.ValueChanged += MaterialProperty_ValueChanged;
            controlsContent.Controls.Add(numBulkDensity);

            // Porosity
            Label lblPorosity = new Label
            {
                Text = "Porosity (fraction):",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(10, 510)
            };
            controlsContent.Controls.Add(lblPorosity);

            numPorosity = new KryptonNumericUpDown
            {
                Location = new Point(140, 510),
                Width = 180,
                Minimum = 0.01m,
                Maximum = 0.5m,
                Value = 0.2m,
                DecimalPlaces = 2,
                StateCommon = {
                    Content = { Color1 = Color.White },
                    Back = { Color1 = Color.FromArgb(60, 60, 60) }
                }
            };
            numPorosity.ValueChanged += MaterialProperty_ValueChanged;
            controlsContent.Controls.Add(numPorosity);

            // Bulk Modulus
            Label lblBulkModulus = new Label
            {
                Text = "Bulk Modulus (MPa):",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(10, 540)
            };
            controlsContent.Controls.Add(lblBulkModulus);

            numBulkModulus = new KryptonNumericUpDown
            {
                Location = new Point(140, 540),
                Width = 180,
                Minimum = 100,
                Maximum = 100000,
                Value = 10000,
                DecimalPlaces = 1,
                StateCommon = {
                    Content = { Color1 = Color.White },
                    Back = { Color1 = Color.FromArgb(60, 60, 60) }
                }
            };
            controlsContent.Controls.Add(numBulkModulus);

            // Permeability
            Label lblPermeability = new Label
            {
                Text = "Permeability (mDarcy):",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(10, 570)
            };
            controlsContent.Controls.Add(lblPermeability);

            numPermeability = new KryptonNumericUpDown
            {
                Location = new Point(140, 570),
                Width = 180,
                Minimum = 0.0001m,
                Maximum = 10m,
                Value = 0.01m,
                DecimalPlaces = 4,
                StateCommon = {
                    Content = { Color1 = Color.White },
                    Back = { Color1 = Color.FromArgb(60, 60, 60) }
                }
            };
            controlsContent.Controls.Add(numPermeability);

            // Cohesion
            Label lblCohesion = new Label
            {
                Text = "Cohesion (MPa):",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(10, 600)
            };
            controlsContent.Controls.Add(lblCohesion);

            numCohesion = new KryptonNumericUpDown
            {
                Location = new Point(140, 600),
                Width = 180,
                Minimum = 0,
                Maximum = 1000,
                Value = 50,
                DecimalPlaces = 1,
                StateCommon = {
                    Content = { Color1 = Color.White },
                    Back = { Color1 = Color.FromArgb(60, 60, 60) }
                }
            };
            controlsContent.Controls.Add(numCohesion);

            // Friction angle
            Label lblFrictionAngle = new Label
            {
                Text = "Friction Angle (°):",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(10, 630)
            };
            controlsContent.Controls.Add(lblFrictionAngle);

            numFrictionAngle = new KryptonNumericUpDown
            {
                Location = new Point(140, 630),
                Width = 180,
                Minimum = 0,
                Maximum = 90,
                Value = 30,
                DecimalPlaces = 1,
                StateCommon = {
                    Content = { Color1 = Color.White },
                    Back = { Color1 = Color.FromArgb(60, 60, 60) }
                }
            };
            controlsContent.Controls.Add(numFrictionAngle);

            // Wireframe mode
            Label lblWireframe = new Label
            {
                Text = "Wireframe Mode:",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(10, 660)
            };
            controlsContent.Controls.Add(lblWireframe);

            chkWireframe = new KryptonCheckBox
            {
                Location = new Point(140, 660),
                Checked = true,
                StateCommon = { ShortText = { Color1 = Color.White } }
            };
            controlsContent.Controls.Add(chkWireframe);

            // Progress bar
            Label lblProgress = new Label
            {
                Text = "Progress:",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(10, 690)
            };
            controlsContent.Controls.Add(lblProgress);

            progressBar = new ProgressBar
            {
                Location = new Point(140, 690),
                Width = 180,
                Height = 20,
                Style = ProgressBarStyle.Continuous,
                BackColor = Color.FromArgb(64, 64, 64),
                ForeColor = Color.LightSkyBlue
            };
            controlsContent.Controls.Add(progressBar);
            chkFastSimulation = new KryptonCheckBox
            {
                Text = "Fast Simulation Mode (Render only final result)",
                Location = new Point(140, 720),  
                Checked = false,
                StateCommon = { ShortText = { Color1 = Color.White } }
            };
            chkFastSimulation.CheckedChanged += (s, e) => {
                fastSimulationMode = chkFastSimulation.Checked;
            };
            controlsContent.Controls.Add(chkFastSimulation);
            // Progress label
            progressLabel = new Label
            {
                Text = "Ready",
                ForeColor = Color.White,
                TextAlign = ContentAlignment.MiddleCenter,
                Location = new Point(140, 750),
                Width = 180,
                Height = 20
            };
            controlsContent.Controls.Add(progressLabel);

            // Generate Mesh Button
            btnGenerateMesh = new KryptonButton
            {
                Text = "Generate Mesh",
                Location = new Point(10, 790),
                Width = 310,
                Height = 30,
                StateCommon = {
        Back = { Color1 = Color.FromArgb(80, 80, 120) },
        Content = { ShortText = { Color1 = Color.White } }
    }
            };
            btnGenerateMesh.Click += BtnGenerateMesh_Click;
            controlsContent.Controls.Add(btnGenerateMesh);

            // Start simulation button
            btnStartSimulation = new KryptonButton
            {
                Text = "Start Simulation",
                Location = new Point(10, 830),
                Width = 310,
                Height = 30,
                Enabled = false,
                StateCommon = {
                    Back = { Color1 = Color.FromArgb(0, 100, 0) },
                    Content = { ShortText = { Color1 = Color.White } }
                },
                StateDisabled = {
                    Back = { Color1 = Color.FromArgb(60, 60, 60) },
                    Content = { ShortText = { Color1 = Color.Silver } }
                }
            };
            btnStartSimulation.Click += BtnStartSimulation_Click;
            controlsContent.Controls.Add(btnStartSimulation);

            // Stop simulation button
            btnStopSimulation = new KryptonButton
            {
                Text = "Stop Simulation",
                Location = new Point(10, 870),
                Width = 310,
                Height = 30,
                Enabled = false,
                StateCommon = {
                    Back = { Color1 = Color.FromArgb(120, 0, 0) },
                    Content = { ShortText = { Color1 = Color.White } }
                },
                StateDisabled = {
                    Back = { Color1 = Color.FromArgb(60, 60, 60) },
                    Content = { ShortText = { Color1 = Color.Silver } }
                }
            };
            btnStopSimulation.Click += BtnStopSimulation_Click;
            controlsContent.Controls.Add(btnStopSimulation);
            // Initialize OpenGL Control FIRST
            CreateGLControl();

            glControl.Load += GlControl_Load;
            glControl.Paint += GlControl_Paint;
            glControl.Resize += GlControl_Resize;
            glControl.MouseDown += GlControl_MouseDown;
            glControl.MouseMove += GlControl_MouseMove;
            glControl.MouseUp += GlControl_MouseUp;
            glControl.MouseWheel += GlControl_MouseWheel;

            // Add GLControl to render panel
            renderPanel.Controls.Add(glControl);
            AddColorLegendPanel();
            // Set up event handlers for controls
            comboMaterials.SelectedIndexChanged += ComboMaterials_SelectedIndexChanged;
            comboDirection.SelectedIndexChanged += ComboDirection_SelectedIndexChanged;
            chkElastic.CheckedChanged += MaterialBehavior_CheckedChanged;
            chkPlastic.CheckedChanged += MaterialBehavior_CheckedChanged;
            chkBrittle.CheckedChanged += MaterialBehavior_CheckedChanged;
            chkWireframe.CheckedChanged += ChkWireframe_CheckedChanged;
            trackSamplingRate.ValueChanged += TrackSamplingRate_ValueChanged;
            numCohesion.ValueChanged += MohrCoulombParameters_Changed;
            numFrictionAngle.ValueChanged += MohrCoulombParameters_Changed;

            // Create simulation timer
            simulationTimer = new System.Windows.Forms.Timer
            {
                Interval = 50
            };
            simulationTimer.Tick += SimulationTimer_Tick;
            AddSaveImageButton();
            // Add main layout to form
            this.Controls.Add(mainLayout);
        }
        private void ColorLegendPanel_Paint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;

            // Enable high quality rendering
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            Rectangle panelRect = colorLegendPanel.ClientRectangle;

            // Legend parameters
            int barWidth = 25;
            int barHeight = 200;
            int barX = 60;
            int barY = 40;

            // Draw title
            using (Font titleFont = new Font("Arial", 10, FontStyle.Bold))
            {
                g.DrawString("Density", titleFont, Brushes.White, 10, 10);

                // Draw unit
                string unitText = isDensityCalibrated ? "kg/m³" : "(normalized)";
                g.DrawString(unitText, titleFont, Brushes.LightGray, 10, 25);
            }

            // Check for valid density range
            float displayMinDensity = minDensity;
            float displayMaxDensity = maxDensity;

            // If min and max are the same or invalid, set reasonable defaults
            if (float.IsNaN(displayMinDensity) || float.IsNaN(displayMaxDensity) ||
                displayMinDensity >= displayMaxDensity)
            {
                if (isDensityCalibrated)
                {
                    displayMinDensity = 1000f;
                    displayMaxDensity = 3000f;
                }
                else
                {
                    displayMinDensity = 0f;
                    displayMaxDensity = 1f;
                }
            }

            // Draw color gradient
            for (int i = 0; i < barHeight; i++)
            {
                float normalizedValue = 1.0f - (i / (float)barHeight);
                using (Brush brush = new SolidBrush(GetColorForValue(normalizedValue)))
                {
                    g.FillRectangle(brush, barX, barY + i, barWidth, 1);
                }
            }

            // Draw border
            g.DrawRectangle(Pens.White, barX, barY, barWidth, barHeight);

            // Draw tick marks and labels
            using (Font labelFont = new Font("Arial", 8))
            {
                int numTicks = 6;
                for (int i = 0; i < numTicks; i++)
                {
                    float normalizedValue = 1.0f - (i / (float)(numTicks - 1));
                    float value = displayMinDensity + (displayMaxDensity - displayMinDensity) * normalizedValue;
                    int y = barY + (int)(i * barHeight / (float)(numTicks - 1));

                    // Draw tick mark
                    g.DrawLine(Pens.White, barX - 5, y, barX, y);

                    // Format value
                    string valueText = isDensityCalibrated
                        ? $"{value:F0}"
                        : $"{normalizedValue:F1}";

                    // Draw value label
                    g.DrawString(valueText, labelFont, Brushes.White, barX - 45, y - 6);
                }

                // Draw min/max labels if calibrated
                if (isDensityCalibrated)
                {
                    g.DrawString("MAX", labelFont, Brushes.White, barX - 25, barY - 15);
                    g.DrawString("MIN", labelFont, Brushes.White, barX - 25, barY + barHeight + 5);

                    // Debug info to verify values
                    g.DrawString($"Range: {displayMinDensity:F0} - {displayMaxDensity:F0}",
                                 labelFont, Brushes.Yellow, 5, barHeight + barY + 20);
                }
            }
        }
        private void MaterialProperty_ValueChanged(object sender, EventArgs e)
        {
            // Update properties when numeric controls change
            if (sender == numBulkDensity)
            {
                bulkDensity = (float)numBulkDensity.Value;
                UpdateMaterialPropertiesFromDensity();
            }
            else if (sender == numPorosity)
            {
                porosity = (float)numPorosity.Value;
                // Update permeability based on porosity (Kozeny-Carman)
                permeability = 0.1f * (float)Math.Pow(porosity, 3) / (float)Math.Pow(1 - porosity, 2);
                numPermeability.Value = (decimal)permeability;
            }
            else if (sender == numYoungModulus)
            {
                youngModulus = (float)numYoungModulus.Value;
                // Update related mechanical properties
                bulkModulus = youngModulus / (3 * (1 - 2 * poissonRatio));
                numBulkModulus.Value = (decimal)bulkModulus;
            }
            else if (sender == numPoissonRatio)
            {
                poissonRatio = (float)numPoissonRatio.Value;
                // Update bulk modulus
                bulkModulus = youngModulus / (3 * (1 - 2 * poissonRatio));
                numBulkModulus.Value = (decimal)bulkModulus;
            }
        }

        private void ChkShowDensity_CheckedChanged(object sender, EventArgs e)
        {
            // Redraw the mesh with/without density coloring
            glControl.Invalidate();
        }

        private void BtnDensitySettings_Click(object sender, EventArgs e)
        {
            // Calculate material volume in m³
            CalculateMaterialVolume();

            // Open density settings dialog
            using (DensitySettingsForm densityForm = new DensitySettingsForm(this, mainForm))
            {
                if (densityForm.ShowDialog() == DialogResult.OK)
                {
                    // Density settings have been updated via the IMaterialDensityProvider interface
                    // The form will call SetMaterialDensity or ApplyDensityCalibration
                    glControl.Invalidate();
                }
            }
        }

        private void CalculateMaterialVolume()
        {
            // Calculate the volume of the material in m³
            if (vertices.Count == 0)
            {
                // Estimate from the material voxels in the volume
                int materialVoxelCount = 0;

                if (densityVolume != null)
                {
                    for (int z = 0; z < depth; z++)
                    {
                        for (int y = 0; y < height; y++)
                        {
                            for (int x = 0; x < width; x++)
                            {
                                if (densityVolume[x, y, z] > 0)
                                {
                                    materialVoxelCount++;
                                }
                            }
                        }
                    }
                }

                // Calculate volume based on voxel count and pixel size
                materialVolume = materialVoxelCount * pixelSize * pixelSize * pixelSize;
            }
            else
            {
                // More accurate volume calculation using tetrahedra
                materialVolume = CalculateTetrahedralVolume();
            }
        }

        private double CalculateTetrahedralVolume()
        {
            // Calculate volume using tetrahedra
            double totalVolume = 0.0;

            foreach (var tetra in tetrahedralElements)
            {
                int[] vIndices = tetra.Vertices;

                if (vIndices.Length == 4 &&
                    vIndices[0] < vertices.Count &&
                    vIndices[1] < vertices.Count &&
                    vIndices[2] < vertices.Count &&
                    vIndices[3] < vertices.Count)
                {
                    Vector3 v0 = vertices[vIndices[0]];
                    Vector3 v1 = vertices[vIndices[1]];
                    Vector3 v2 = vertices[vIndices[2]];
                    Vector3 v3 = vertices[vIndices[3]];

                    // Calculate tetrahedron volume
                    Vector3 v01 = v1 - v0;
                    Vector3 v02 = v2 - v0;
                    Vector3 v03 = v3 - v0;

                    double vol = Math.Abs(Vector3.Dot(Vector3.Cross(v01, v02), v03)) / 6.0;
                    totalVolume += vol;
                }
            }

            // Convert from model units (mm³) to m³
            return totalVolume * 1.0e-9;
        }

        private void MaterialBehavior_CheckedChanged(object sender, EventArgs e)
        {
            isElasticEnabled = chkElastic.Checked;
            isPlasticEnabled = chkPlastic.Checked;
            isBrittleEnabled = chkBrittle.Checked;

            // Ensure at least one behavior is selected
            if (!isElasticEnabled && !isPlasticEnabled && !isBrittleEnabled)
            {
                ((KryptonCheckBox)sender).Checked = true;
            }
        }

        private void MohrCoulombParameters_Changed(object sender, EventArgs e)
        {
            cohesion = (float)numCohesion.Value;
            frictionAngle = (float)numFrictionAngle.Value;
            mohrCoulombGraph.Invalidate();
        }

        private void MohrCoulombGraph_Paint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Color.White);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Set up coordinate system with padding
            int padding = 30;
            int width = mohrCoulombGraph.Width - 2 * padding;
            int height = mohrCoulombGraph.Height - 2 * padding;

            // Draw axes
            using (Pen axisPen = new Pen(Color.Black, 2))
            {
                // X-axis (normal stress)
                g.DrawLine(axisPen, padding, mohrCoulombGraph.Height - padding,
                           mohrCoulombGraph.Width - padding, mohrCoulombGraph.Height - padding);

                // Y-axis (shear stress)
                g.DrawLine(axisPen, padding, mohrCoulombGraph.Height - padding,
                           padding, padding);
            }

            // Draw axis labels
            using (Font labelFont = new Font("Arial", 8))
            {
                g.DrawString("Normal Stress (MPa)", labelFont, Brushes.Black,
                            mohrCoulombGraph.Width / 2, mohrCoulombGraph.Height - 15);

                // Rotate Y-axis label
                g.TranslateTransform(15, mohrCoulombGraph.Height / 2);
                g.RotateTransform(-90);
                g.DrawString("Shear Stress (MPa)", labelFont, Brushes.Black, 0, 0);
                g.ResetTransform();
            }

            // Calculate maximum stress for scaling
            float maxStress = Math.Max(1000, (float)numMaxPressure.Value * 2);

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
                    g.DrawString($"Tensile\nStrength", new Font("Arial", 8), Brushes.Blue,
                        tensileX, mohrCoulombGraph.Height - padding - 40);
                }
            }

            // Draw Mohr circles if simulation is running
            if (simulationRunning && stressStrainCurve.Count > 0)
            {
                // Get current stress values for the Mohr circle
                float sigma1 = maxPressure; // Major principal stress (vertical load)
                float sigma3 = minPressure; // Minor principal stress (confining pressure)

                float currentStress = stressStrainCurve.Last().Y / 10.0f; // Convert from graph units to MPa

                // For a triaxial test, major principal stress increases during loading
                sigma1 = sigma3 + currentStress;

                // Calculate circle center and radius
                float center = (sigma1 + sigma3) / 2;
                float radius = (sigma1 - sigma3) / 2;

                // Scale to pixel coordinates
                int centerX = padding + (int)(center * width / maxStress);
                int radiusPixels = (int)(radius * width / maxStress);

                // Draw the Mohr circle
                using (Pen circlePen = new Pen(Color.Blue, 2))
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
                    g.DrawString($"σ₃ = {sigma3:F1} MPa", stressFont, Brushes.Blue,
                        padding + (int)(sigma3 * width / maxStress) - 50,
                        mohrCoulombGraph.Height - padding + 5);

                    g.DrawString($"σ₁ = {sigma1:F1} MPa", stressFont, Brushes.Blue,
                        padding + (int)(sigma1 * width / maxStress) - 50,
                        mohrCoulombGraph.Height - padding + 5);
                }

                // Update current stresses for display
                normalStress = center;
                shearStress = radius;

                // Display current stress state
                using (Font valueFont = new Font("Arial", 10, FontStyle.Bold))
                {
                    g.DrawString($"Normal Stress: {normalStress:F1} MPa", valueFont, Brushes.Black, padding, padding);
                    g.DrawString($"Shear Stress: {shearStress:F1} MPa", valueFont, Brushes.Black, padding, padding + 20);

                    // Check if failure envelope is exceeded
                    float failureShear = cohesion + normalStress * (float)Math.Tan(frictionAngle * Math.PI / 180.0);
                    if (shearStress >= failureShear)
                    {
                        g.DrawString("Status: FAILURE", valueFont, Brushes.Red, padding, padding + 40);
                    }
                    else
                    {
                        g.DrawString("Status: Stable", valueFont, Brushes.Green, padding, padding + 40);
                    }

                    // Display petrophysical parameters
                    g.DrawString($"Bulk Density: {bulkDensity:F0} kg/m³", valueFont, Brushes.DarkBlue, padding, padding + 70);
                    g.DrawString($"Porosity: {porosity:P1}", valueFont, Brushes.DarkBlue, padding, padding + 90);
                }
            }
        }

        private void InitializeBackgroundWorker()
        {
            meshWorker = new BackgroundWorker();
            meshWorker.WorkerReportsProgress = true;
            meshWorker.WorkerSupportsCancellation = true;
            meshWorker.DoWork += MeshWorker_DoWork;
            meshWorker.ProgressChanged += MeshWorker_ProgressChanged;
            meshWorker.RunWorkerCompleted += MeshWorker_RunWorkerCompleted;
        }

        private void MeshWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            BackgroundWorker worker = sender as BackgroundWorker;
            MeshGenerationParameters parameters = (MeshGenerationParameters)e.Argument;

            // Extract volume data for the selected material
            worker.ReportProgress(0, "Extracting volume data...");
            ExtractVolumeData(parameters.Material);

            // Generate mesh with the selected sampling rate
            worker.ReportProgress(30, "Generating mesh...");
            GenerateMesh(parameters.SamplingRate, worker);

            // Calculate total volume
            worker.ReportProgress(90, "Calculating material volume...");
            materialVolume = CalculateTetrahedralVolume();

            // Result is true if mesh generation was successful
            e.Result = vertices.Count > 0 && indices.Count > 0;
        }

        private void MeshWorker_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            progressBar.Value = e.ProgressPercentage;
            progressLabel.Text = e.UserState.ToString();
        }
        private void ScaleMeshForDisplay()
        {
            if (deformedVertices == null || deformedVertices.Count == 0 || vertices == null || vertices.Count == 0)
            {
                return;
            }

            // Calculate the true mesh bounds without any artificial minimum
            Vector3 min = new Vector3(float.MaxValue);
            Vector3 max = new Vector3(float.MinValue);

            foreach (var v in vertices)
            {
                min.X = Math.Min(min.X, v.X);
                min.Y = Math.Min(min.Y, v.Y);
                min.Z = Math.Min(min.Z, v.Z);

                max.X = Math.Max(max.X, v.X);
                max.Y = Math.Max(max.Y, v.Y);
                max.Z = Math.Max(max.Z, v.Z);
            }

            Vector3 center = (min + max) * 0.5f;
            Vector3 size = max - min;
            float maxDim = Math.Max(Math.Max(size.X, size.Y), size.Z);

            // Target bounding box size
            float targetSize = 10.0f;

            // Determine if we need to scale the mesh (too small or too large)
            bool needsScaling = (maxDim < 1.0f || maxDim > 100.0f);

            if (needsScaling)
            {
                // Calculate scale factor
                float scaleFactor = targetSize / maxDim;

                // Create a scaled copy for display purposes only
                deformedVertices = new List<Vector3>(vertices.Count);

                foreach (var v in vertices)
                {
                    // Center then scale
                    Vector3 centered = v - center;
                    Vector3 scaled = centered * scaleFactor;
                    deformedVertices.Add(scaled);
                }
            }
            else
            {
                // No scaling needed, just copy the vertices
                deformedVertices = new List<Vector3>(vertices);
            }
        }
        private void MeshWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            Logger.Log("[TriaxialSimulationForm] MeshWorker_RunWorkerCompleted() - Start");

            if (e.Error != null)
            {
                MessageBox.Show($"Error generating mesh: {e.Error.Message}", "Error",
                               MessageBoxButtons.OK, MessageBoxIcon.Error);
                progressLabel.Text = "Mesh generation failed";
                progressBar.Value = 0;
                Logger.Log($"[TriaxialSimulationForm] Mesh generation failed with error: {e.Error.Message}");
                Logger.Log($"[TriaxialSimulationForm] Error stack trace: {e.Error.StackTrace}");
            }
            else if (e.Cancelled)
            {
                progressLabel.Text = "Mesh generation cancelled";
                progressBar.Value = 0;
                Logger.Log("[TriaxialSimulationForm] Mesh generation was cancelled by user");
            }
            else
            {
                bool success = (bool)e.Result;
                if (success)
                {
                    meshGenerationComplete = true;

                    Logger.Log($"[TriaxialSimulationForm] Mesh generation successful: {vertices.Count} vertices, {indices.Count / 3} triangles");

                    // Log original mesh scale
                    if (vertices.Count > 0)
                    {
                        Vector3 min = new Vector3(float.MaxValue);
                        Vector3 max = new Vector3(float.MinValue);

                        foreach (var v in vertices)
                        {
                            min.X = Math.Min(min.X, v.X);
                            min.Y = Math.Min(min.Y, v.Y);
                            min.Z = Math.Min(min.Z, v.Z);

                            max.X = Math.Max(max.X, v.X);
                            max.Y = Math.Max(max.Y, v.Y);
                            max.Z = Math.Max(max.Z, v.Z);
                        }

                        Vector3 size = max - min;
                        float maxDim = Math.Max(Math.Max(size.X, size.Y), size.Z);

                        Logger.Log($"[TriaxialSimulationForm] Original mesh bounds: min={min}, max={max}, size={size}, maxDim={maxDim}");

                        // Log a few sample vertices
                        for (int i = 0; i < Math.Min(5, vertices.Count); i++)
                        {
                            Logger.Log($"[TriaxialSimulationForm] Original vertex {i}: {vertices[i]}");
                        }
                    }

                    // First calculate the material volume from original vertices (before scaling)
                    // This ensures volume calculations are accurate
                    materialVolume = CalculateTetrahedralVolume();
                    Logger.Log($"[TriaxialSimulationForm] Calculated material volume: {materialVolume} m³");

                    // Fix mesh scaling for display purposes
                    Logger.Log("[TriaxialSimulationForm] Calling FixMeshScale() to adjust mesh size");
                    FixMeshScale();

                    // Create deformed vertices by copying current vertices
                    Logger.Log("[TriaxialSimulationForm] Creating deformed vertices for display");
                    deformedVertices = new List<Vector3>(vertices);

                    // Log deformed vertices scale
                    if (deformedVertices.Count > 0)
                    {
                        Vector3 min = new Vector3(float.MaxValue);
                        Vector3 max = new Vector3(float.MinValue);

                        foreach (var v in deformedVertices)
                        {
                            min.X = Math.Min(min.X, v.X);
                            min.Y = Math.Min(min.Y, v.Y);
                            min.Z = Math.Min(min.Z, v.Z);

                            max.X = Math.Max(max.X, v.X);
                            max.Y = Math.Max(max.Y, v.Y);
                            max.Z = Math.Max(max.Z, v.Z);
                        }

                        Vector3 size = max - min;
                        float maxDim = Math.Max(Math.Max(size.X, size.Y), size.Z);

                        Logger.Log($"[TriaxialSimulationForm] Deformed vertices bounds: min={min}, max={max}, size={size}, maxDim={maxDim}");

                        // Log a few sample deformed vertices
                        for (int i = 0; i < Math.Min(5, deformedVertices.Count); i++)
                        {
                            Logger.Log($"[TriaxialSimulationForm] Deformed vertex {i}: {deformedVertices[i]}");
                        }
                    }

                    // Update UI
                    btnStartSimulation.Enabled = true;
                    progressLabel.Text = $"Mesh generated: {vertices.Count} vertices, {indices.Count / 3} triangles";
                    progressBar.Value = 100;

                    // Set bulk density based on average grayscale value if calibrated
                    if (isDensityCalibrated)
                    {
                        Logger.Log("[TriaxialSimulationForm] Applying density calibration");
                        CalculateAverageBulkDensity();

                        try
                        {
                            numBulkDensity.Value = (decimal)Math.Min(Math.Max((decimal)bulkDensity, numBulkDensity.Minimum), numBulkDensity.Maximum);
                            Logger.Log($"[TriaxialSimulationForm] Set bulk density to {bulkDensity} kg/m³");
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"[TriaxialSimulationForm] Error setting bulk density UI: {ex.Message}");
                        }

                        UpdateMaterialPropertiesFromDensity();
                    }

                    // Enable/reenable UI controls
                    comboMaterials.Enabled = true;
                    trackSamplingRate.Enabled = true;

                    // Force update of 3D view
                    Logger.Log("[TriaxialSimulationForm] Invalidating GL control to update display");
                    glControl.Invalidate();
                }
                else
                {
                    progressLabel.Text = "No voxels found for selected material";
                    progressBar.Value = 0;
                    Logger.Log("[TriaxialSimulationForm] No voxels found for selected material - mesh generation failed");

                    // Enable/reenable UI controls
                    comboMaterials.Enabled = true;
                    trackSamplingRate.Enabled = true;
                }
            }
            comboMaterials.Enabled = true;
            trackSamplingRate.Enabled = true;

            btnGenerateMesh.Enabled = true;
            Logger.Log("[TriaxialSimulationForm] MeshWorker_RunWorkerCompleted() - End");
        }
        private void LoadMaterialsFromMainForm()
        {
            comboMaterials.Items.Clear();

            foreach (Material material in mainForm.Materials)
            {
                // Skip exterior material (usually ID 0)
                if (!material.IsExterior)
                {
                    comboMaterials.Items.Add(material);
                }
            }

            // Display material name in dropdown
            comboMaterials.DisplayMember = "Name";

            if (comboMaterials.Items.Count > 0)
            {
                comboMaterials.SelectedIndex = 0;
            }
        }

        private void BtnGenerateMesh_Click(object sender, EventArgs e)
        {
            Logger.Log("[TriaxialSimulationForm] BtnGenerateMesh_Click() - Start");

            if (comboMaterials.SelectedItem is Material material)
            {
                selectedMaterial = material;
                samplingRate = trackSamplingRate.Value;

                // Reset progress
                progressBar.Value = 0;
                progressLabel.Text = "Starting mesh generation...";

                // Disable UI during processing
                comboMaterials.Enabled = false;
                trackSamplingRate.Enabled = false;
                btnStartSimulation.Enabled = false;
                btnGenerateMesh.Enabled = false;  // Disable the Generate Mesh button

                // Clear old mesh data
                vertices.Clear();
                indices.Clear();
                normals.Clear();
                densityValues.Clear();
                tetrahedralElements.Clear();

                Logger.Log($"[TriaxialSimulationForm] Starting mesh generation for material: {material.Name}, samplingRate: {samplingRate}");

                // Start mesh generation in background
                MeshGenerationParameters parameters = new MeshGenerationParameters
                {
                    Material = material,
                    SamplingRate = samplingRate
                };

                meshWorker.RunWorkerAsync(parameters);
            }

            Logger.Log("[TriaxialSimulationForm] BtnGenerateMesh_Click() - End");
        }
        private void FixMeshScale()
        {
            Logger.Log("[TriaxialSimulationForm] FixMeshScale() - Start");

            if (vertices == null || vertices.Count == 0)
            {
                Logger.Log("[TriaxialSimulationForm] No vertices to fix - returning");
                return;
            }

            // Find the current mesh scale
            Vector3 min = new Vector3(float.MaxValue);
            Vector3 max = new Vector3(float.MinValue);

            foreach (var v in vertices)
            {
                min.X = Math.Min(min.X, v.X);
                min.Y = Math.Min(min.Y, v.Y);
                min.Z = Math.Min(min.Z, v.Z);

                max.X = Math.Max(max.X, v.X);
                max.Y = Math.Max(max.Y, v.Y);
                max.Z = Math.Max(max.Z, v.Z);
            }

            Vector3 center = (min + max) * 0.5f;
            Vector3 size = max - min;
            float maxDim = Math.Max(Math.Max(size.X, size.Y), size.Z);

            Logger.Log($"[TriaxialSimulationForm] Original mesh bounds: min={min}, max={max}, center={center}, size={size}, maxDim={maxDim}");

            // Target bounding box size - make it a reasonable size for visualization
            float targetSize = 10.0f;

            // Determine if we need to scale the mesh (too small or too large)
            bool needsRescaling = (maxDim < 1.0f || maxDim > 100.0f);

            if (needsRescaling)
            {
                float scaleFactor = targetSize / maxDim;

                Logger.Log($"[TriaxialSimulationForm] Rescaling mesh: scaleFactor={scaleFactor}, targetSize={targetSize}");

                // Create a backup copy of the original vertices for volume calculations
                List<Vector3> originalVertices = new List<Vector3>(vertices);

                // Save the scale factor for future reference
                float originalScale = maxDim;
                Logger.Log($"[TriaxialSimulationForm] Saving originalScale={originalScale} for volume calculations");

                // Scale all vertex positions relative to center
                for (int i = 0; i < vertices.Count; i++)
                {
                    // Store original position
                    Vector3 original = vertices[i];

                    // Center, scale, and replace
                    Vector3 centered = original - center;
                    vertices[i] = center + (centered * scaleFactor);

                    // Log a few vertices for debugging
                    if (i < 5)
                    {
                        Logger.Log($"[TriaxialSimulationForm] Vertex {i}: Original={original}, Centered={centered}, Scaled={vertices[i]}");
                    }
                }

                // Check the new scale
                min = new Vector3(float.MaxValue);
                max = new Vector3(float.MinValue);

                foreach (var v in vertices)
                {
                    min.X = Math.Min(min.X, v.X);
                    min.Y = Math.Min(min.Y, v.Y);
                    min.Z = Math.Min(min.Z, v.Z);

                    max.X = Math.Max(max.X, v.X);
                    max.Y = Math.Max(max.Y, v.Y);
                    max.Z = Math.Max(max.Z, v.Z);
                }

                Vector3 newCenter = (min + max) * 0.5f;
                Vector3 newSize = max - min;
                float newMaxDim = Math.Max(Math.Max(newSize.X, newSize.Y), newSize.Z);

                Logger.Log($"[TriaxialSimulationForm] Rescaled mesh bounds: min={min}, max={max}, center={newCenter}, size={newSize}, maxDim={newMaxDim}");
            }
            else
            {
                Logger.Log("[TriaxialSimulationForm] Mesh scale is within reasonable range, no rescaling needed");
            }

            Logger.Log("[TriaxialSimulationForm] FixMeshScale() - End");
        }
        private void ComboMaterials_SelectedIndexChanged(object sender, EventArgs e)
        {
            // Reset mesh generation status
            meshGenerationComplete = false;
            btnStartSimulation.Enabled = false;

            // Clear old mesh data
            vertices.Clear();
            indices.Clear();
            normals.Clear();
            densityValues.Clear();
            tetrahedralElements.Clear();

            // Set the selected material
            selectedMaterial = comboMaterials.SelectedItem as Material;

            glControl.Invalidate();
        }

        private void TrackSamplingRate_ValueChanged(object sender, EventArgs e)
        {
            samplingRate = trackSamplingRate.Value;
            string qualityLevel = "";

            switch (samplingRate)
            {
                case 1: qualityLevel = "Highest (very slow)"; break;
                case 2: qualityLevel = "High"; break;
                case 3: qualityLevel = "Medium"; break;
                case 4: qualityLevel = "Low"; break;
                case 5: qualityLevel = "Lowest (very fast)"; break;
            }

            trackSamplingRate.ToolTipValues.Description = $"Sampling Rate: {samplingRate} - Quality: {qualityLevel}";
        }

        private void ComboDirection_SelectedIndexChanged(object sender, EventArgs e)
        {
            selectedDirection = (SimulationDirection)comboDirection.SelectedIndex;
        }

        private void ChkWireframe_CheckedChanged(object sender, EventArgs e)
        {
            wireframeMode = chkWireframe.Checked;
            glControl.Invalidate();
        }

        private void BtnStartSimulation_Click(object sender, EventArgs e)
        {
            if (!meshGenerationComplete || tetrahedralElements.Count == 0)
            {
                MessageBox.Show("No mesh available. Please generate a mesh first.", "Error",
                               MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Get simulation parameters from UI
            minPressure = (float)numMinPressure.Value;
            maxPressure = (float)numMaxPressure.Value;
            youngModulus = (float)numYoungModulus.Value;
            poissonRatio = (float)numPoissonRatio.Value;
            yieldStrength = (float)numYieldStrength.Value;
            brittleStrength = (float)numBrittleStrength.Value;
            cohesion = (float)numCohesion.Value;
            frictionAngle = (float)numFrictionAngle.Value;
            bulkDensity = (float)numBulkDensity.Value;
            porosity = (float)numPorosity.Value;
            bulkModulus = (float)numBulkModulus.Value;
            permeability = (float)numPermeability.Value;

            // Reset simulation state
            stressStrainCurve.Clear();
            currentStrain = 0.0f;
            deformedVertices = new List<Vector3>(vertices);
            elementStresses.Clear();

            // Enable/disable buttons
            btnStartSimulation.Enabled = false;
            btnStopSimulation.Enabled = true;
            comboMaterials.Enabled = false;
            comboDirection.Enabled = false;

            // Start simulation
            simulationRunning = true;
            if (fastSimulationMode)
            {
                progressLabel.Text = "Fast simulation started - 3D view will update at completion";
            }
            simulationTimer.Start();
            try
            {
                if (diagramsForm == null || diagramsForm.IsDisposed)
                {
                    InitializeDiagramsForm();
                }

                if (diagramsForm != null)
                {
                    diagramsForm.Show();
                    UpdateDiagramsForm();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[TriaxialSimulationForm] Error initializing DiagramsForm: {ex.Message}");
                // Continue simulation even if diagrams fail
            }
        }

        private void BtnStopSimulation_Click(object sender, EventArgs e)
        {
            StopSimulation();
        }

        private void StopSimulation()
        {
            simulationRunning = false;
            simulationTimer.Stop();

            // Enable/disable buttons
            btnStartSimulation.Enabled = true;
            btnStopSimulation.Enabled = false;
            comboMaterials.Enabled = true;
            comboDirection.Enabled = true;
        }
        private void SimulationTimer_Tick(object sender, EventArgs e)
        {
            if (!simulationRunning)
                return;

            // Increment strain
            currentStrain += 0.001f; // 0.1% strain increment

            if (currentStrain >= maxStrain)
            {
                StopSimulation();
                return;
            }

            // Calculate current stress based on strain and material model
            float averageStress = CalculateStress(currentStrain);

            // Add to stress-strain curve
            stressStrainCurve.Add(new Point((int)(currentStrain * 1000), (int)averageStress));

            // Update mesh deformation based on original vertex positions
            UpdateDeformation(currentStrain, averageStress);

            // Only redraw UI if not in fast simulation mode
            if (!fastSimulationMode || currentStrain >= maxStrain - 0.001f)
            {
                // Redraw charts
                if (stressStrainGraph != null)
                    stressStrainGraph.Invalidate();

                if (mohrCoulombGraph != null)
                    mohrCoulombGraph.Invalidate();

                // Update DiagramsForm if available
                if (diagramsForm != null && !diagramsForm.IsDisposed)
                {
                    try
                    {
                        UpdateDiagramsForm();
                    }
                    catch (Exception ex)
                    {
                        // Log but don't crash
                        Logger.Log($"[TriaxialSimulationForm] Error updating diagrams: {ex.Message}");
                    }
                }

                // Update 3D view
                glControl.Invalidate();
            }
        }
        private void UpdateDiagramsForm()
        {
            if (diagramsForm != null && !diagramsForm.IsDisposed)
            {
                diagramsForm.UpdateData(
                    stressStrainCurve,
                    currentStrain,
                    stressStrainCurve.Count > 0 ? stressStrainCurve.Last().Y / 10.0f : 0,
                    cohesion,
                    frictionAngle,
                    normalStress,
                    shearStress,
                    bulkDensity,
                    porosity,
                    minPressure,
                    maxPressure,
                    yieldStrength,
                    brittleStrength,
                    isElasticEnabled,
                    isPlasticEnabled,
                    isBrittleEnabled,
                    simulationRunning
                );
            }
        }

        private float CalculateStress(float strain)
        {
            // Initialize thread-safe collections for parallel processing
            ConcurrentDictionary<int, float> concurrentElementStresses = new ConcurrentDictionary<int, float>();

            // Use thread-safe counter for total stress accumulation
            double totalStressSum = 0;

            // Physical constants
            float yieldStrain = yieldStrength / youngModulus;
            float brittleStrain = brittleStrength / youngModulus;

            // Process all tetrahedral elements in parallel
            Parallel.For(0, tetrahedralElements.Count,
                // Initialize local thread state (local sum accumulator)
                () => 0.0f,
                // Process each element and update local state
                (i, loop, localSum) => {
                    // Calculate average density for this element
                    float elementDensity = GetElementDensity(tetrahedralElements[i]);

                    // Adjust material properties based on density
                    // Higher density materials are typically stiffer and stronger
                    float densityRatio = isDensityCalibrated && elementDensity > 0 ?
                        elementDensity / bulkDensity : 1.0f;

                    // Scale modulus using an empirical power law relationship (realistic for rocks)
                    // E ∝ ρ^n where n~2-3 for most rocks
                    float elementYoungModulus = youngModulus * (float)Math.Pow(densityRatio, 2.5);

                    // Update yield and brittle thresholds based on adjusted modulus
                    // Maintaining constant strain thresholds for the material
                    float elementYieldStrength = yieldStrength * (float)Math.Pow(densityRatio, 1.5);
                    float elementBrittleStrength = brittleStrength * (float)Math.Pow(densityRatio, 1.2);
                    float elementYieldStrain = elementYieldStrength / elementYoungModulus;
                    float elementBrittleStrain = elementBrittleStrength / elementYoungModulus;

                    // Initialize element stress
                    float elementStress = 0;

                    // Calculate stress based on material behavior models
                    // 1. Elastic behavior (linear up to yield point)
                    if (isElasticEnabled)
                    {
                        // Linear elastic relationship: σ = E·ε
                        if (strain <= elementYieldStrain || !isPlasticEnabled)
                        {
                            elementStress = elementYoungModulus * strain;
                        }
                        else
                        {
                            // Elastic contribution limited to yield strength
                            elementStress = elementYieldStrength;
                        }
                    }

                    // 2. Plastic behavior (post-yield hardening or perfect plasticity)
                    if (isPlasticEnabled && strain > elementYieldStrain)
                    {
                        // Calculate plastic strain
                        float plasticStrain = strain - elementYieldStrain;

                        // Density-dependent hardening modulus (typically 1-10% of Young's modulus)
                        // Higher density materials usually show more strain hardening
                        float hardeningModulus = elementYoungModulus * 0.05f * densityRatio;

                        // Add plastic hardening component: σp = σy + H·εp
                        float plasticComponent = hardeningModulus * plasticStrain;

                        // Combine with elastic component if enabled
                        if (isElasticEnabled)
                        {
                            elementStress += plasticComponent;
                        }
                        else
                        {
                            elementStress = elementYieldStrength + plasticComponent;
                        }
                    }

                    // 3. Brittle behavior (stress drop after failure)
                    if (isBrittleEnabled && strain > elementBrittleStrain)
                    {
                        float residualFactor = 0.2f + 0.3f / densityRatio;
                        residualFactor = Math.Max(0.05f, Math.Min(0.5f, residualFactor));

                        
                        float postFailureStrain = strain - elementBrittleStrain;
                        float decayRate = 20.0f; // Controls how quickly strength is lost
                        float decayFactor = (float)Math.Exp(-decayRate * postFailureStrain);

                        // Transition from peak to residual strength
                        float residualStrength = elementBrittleStrength * residualFactor;
                        float brittleStress = residualStrength + (elementBrittleStrength - residualStrength) * decayFactor;

                        // If no other behaviors are enabled, use brittle stress directly
                        if (!isElasticEnabled && !isPlasticEnabled)
                        {
                            elementStress = brittleStress;
                        }
                        else
                        {
                            // Otherwise, take the minimum of calculated stress and brittle stress
                            // This ensures stress can't exceed the brittle failure envelope
                            elementStress = Math.Min(elementStress, brittleStress);
                        }
                    }

                    // Store element stress in thread-safe dictionary
                    concurrentElementStresses[i] = elementStress;

                    // Add to thread-local sum
                    return localSum + elementStress;
                },
                // Combine all local sums using a thread-safe addition
                (localSum) => {
                    // Use Interlocked.Add for thread-safe addition to shared counter
                    double currentSum = localSum;
                    Interlocked.Exchange(ref totalStressSum, totalStressSum + currentSum);
                }
            );

            // Copy concurrent dictionary to regular dictionary for compatibility with existing code
            elementStresses.Clear();
            foreach (var pair in concurrentElementStresses)
            {
                elementStresses[pair.Key] = pair.Value;
            }

            // Calculate average stress
            float averageStress = tetrahedralElements.Count > 0 ?
                (float)(totalStressSum / tetrahedralElements.Count) : 0;

            return averageStress;
        }
        private float GetElementDensity(TetrahedralElement element)
        {
            if (!isDensityCalibrated || element.Vertices.Length != 4)
                return bulkDensity;

            // Calculate average density from vertices
            float sumDensity = 0;
            int validVertices = 0;

            foreach (int vertexIndex in element.Vertices)
            {
                if (vertexIndex < densityValues.Count)
                {
                    sumDensity += densityValues[vertexIndex];
                    validVertices++;
                }
            }

            return validVertices > 0 ? sumDensity / validVertices : bulkDensity;
        }

        private void UpdateDeformation(float strain, float stress)
        {
            // Start with fresh copy of scaled vertices if needed
            if (deformedVertices == null || deformedVertices.Count != vertices.Count)
            {
                ScaleMeshForDisplay();
            }

            // Create thread-local storage array for vertex positions
            Vector3[] newPositions = new Vector3[vertices.Count];

            // Copy initial vertex positions
            for (int i = 0; i < vertices.Count; i++)
            {
                newPositions[i] = vertices[i];
            }

            // Process all vertices in parallel
            Parallel.For(0, vertices.Count, i =>
            {
                // Get original vertex position
                Vector3 vertex = newPositions[i];

                // Get vertex density for property scaling
                float vertexDensity = (i < densityValues.Count) ? densityValues[i] : bulkDensity;

               
                float grainDensity = 2650.0f; // kg/m³ (typical for quartz/feldspar)
                float vertexPorosity = 1.0f - (vertexDensity / grainDensity);
                vertexPorosity = Math.Max(0.01f, Math.Min(0.5f, vertexPorosity));

                // Calculate effective Poisson's ratio based on porosity
                // Higher porosity typically means higher Poisson's ratio up to ~0.5 limit
                float effectivePoissonRatio = Math.Min(
                    poissonRatio * (1.0f + 0.3f * vertexPorosity),
                    0.49f
                );

                // Apply deformation based on selected loading direction
                // Using Hooke's Law for anisotropic deformation
                switch (selectedDirection)
                {
                    case SimulationDirection.X:
                        // Apply axial strain in X direction
                        vertex.X *= (1 + strain);
                        // Apply lateral contraction in Y and Z (Poisson effect)
                        vertex.Y *= (1 - strain * effectivePoissonRatio);
                        vertex.Z *= (1 - strain * effectivePoissonRatio);
                        break;

                    case SimulationDirection.Y:
                        vertex.Y *= (1 + strain);
                        vertex.X *= (1 - strain * effectivePoissonRatio);
                        vertex.Z *= (1 - strain * effectivePoissonRatio);
                        break;

                    case SimulationDirection.Z:
                        vertex.Z *= (1 + strain);
                        vertex.X *= (1 - strain * effectivePoissonRatio);
                        vertex.Y *= (1 - strain * effectivePoissonRatio);
                        break;
                }

                // Apply heterogeneous deformation based on local material properties
                if (isDensityCalibrated && i < densityValues.Count)
                {
                    // Calculate deformation influence factor based on porosity
                    // Higher porosity (lower density) materials deform more
                    float stiffnessRatio = (bulkDensity / vertexDensity);
                    stiffnessRatio = Math.Max(0.5f, Math.Min(2.0f, stiffnessRatio));

                    // Calculate original position
                    Vector3 originalPos = vertices[i];

                    // Calculate elastic deformation vector
                    Vector3 deformationVector = vertex - originalPos;

                    // Scale deformation based on local stiffness
                    // Less stiff (more porous) material deforms more
                    Vector3 scaledDeformation = deformationVector * stiffnessRatio;

                    // Calculate new position with adjusted deformation
                    vertex = originalPos + scaledDeformation;
                }

                // Save the updated position to the thread-local array
                newPositions[i] = vertex;
            });

            // Now transfer the results to the deformed vertices list
            // This avoids thread contention during the parallel loop
            lock (deformedVertices)
            {
                deformedVertices.Clear();
                for (int i = 0; i < newPositions.Length; i++)
                {
                    deformedVertices.Add(newPositions[i]);
                }
            }

            // Apply brittle fracturing effects
            if (isBrittleEnabled && strain > brittleStrength / youngModulus)
            {
                // Create a fixed seed for reproducible randomness
                int seed = (int)(currentStrain * 10000);
                Random globalRand = new Random(seed);

                // Create an array to store displacements
                Vector3[] displacements = new Vector3[deformedVertices.Count];

                // Calculate vertex stress factors in parallel
                float[] vertexStressFactors = new float[deformedVertices.Count];

                Parallel.For(0, deformedVertices.Count, i =>
                {
                    // Initialize stress factor for this vertex
                    float maxStressFactor = 0.0f;

                    // Find elements containing this vertex and get their stress states
                    foreach (var tetra in tetrahedralElements)
                    {
                        if (Array.IndexOf(tetra.Vertices, i) >= 0)
                        {
                            // This vertex is part of this tetrahedron
                            int tetraIndex = tetrahedralElements.IndexOf(tetra);
                            if (elementStresses.TryGetValue(tetraIndex, out float elementStress))
                            {
                                // Normalize stress by brittle strength to get a stress factor
                                float stressFactor = elementStress / brittleStrength;
                                maxStressFactor = Math.Max(maxStressFactor, stressFactor);
                            }
                        }
                    }

                    vertexStressFactors[i] = maxStressFactor;
                });

                // Now apply fracture displacements based on stress factors
                Parallel.For(0, deformedVertices.Count, i =>
                {
                    float stressFactor = vertexStressFactors[i];

                    // Only create fractures where stress exceeds threshold
                    if (stressFactor > 0.8f)
                    {
                        // Create a thread-local random generator with a fixed seed
                        // based on the global seed and vertex index
                        Random localRand = new Random(seed + i);

                        // Scale displacement with stress factor - higher stress, more displacement
                        float displacementMagnitude = 0.05f * stressFactor * stressFactor;

                        // Generate random displacement vector
                        Vector3 displacement = new Vector3(
                            (float)(localRand.NextDouble() - 0.5) * displacementMagnitude,
                            (float)(localRand.NextDouble() - 0.5) * displacementMagnitude,
                            (float)(localRand.NextDouble() - 0.5) * displacementMagnitude
                        );

                        // Store displacement in array
                        displacements[i] = displacement;
                    }
                    else
                    {
                        displacements[i] = Vector3.Zero;
                    }
                });

                // Apply all displacements at once
                lock (deformedVertices)
                {
                    for (int i = 0; i < deformedVertices.Count; i++)
                    {
                        deformedVertices[i] += displacements[i];
                    }
                }
            }

            // Update OpenGL buffers if initialized
            if (buffersInitialized)
            {
                // Upload new vertex positions to GPU
                float[] vertexData = new float[deformedVertices.Count * 3];

                lock (deformedVertices)
                {
                    for (int i = 0; i < deformedVertices.Count; i++)
                    {
                        vertexData[i * 3] = deformedVertices[i].X;
                        vertexData[i * 3 + 1] = deformedVertices[i].Y;
                        vertexData[i * 3 + 2] = deformedVertices[i].Z;
                    }
                }

                GL.BindBuffer(BufferTarget.ArrayBuffer, vertexBufferId);
                GL.BufferData(BufferTarget.ArrayBuffer, (IntPtr)(vertexData.Length * sizeof(float)),
                                vertexData, BufferUsageHint.StaticDraw);
                GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
            }

            // Force display list recreation
            if (meshDisplayList != 0)
            {
                GL.DeleteLists(meshDisplayList, 1);
                meshDisplayList = 0;
            }

            // Recalculate normals for proper lighting
            RecalculateNormals();
        }
        private void RecalculateNormals()
        {
            int vertexCount = deformedVertices.Count;

            // Create thread-safe accumulators for normals
            Vector3[] tempNormals = new Vector3[vertexCount];

            // Reset normals array first
            for (int i = 0; i < normals.Count; i++)
            {
                normals[i] = Vector3.Zero;
            }

            // Calculate face normals and accumulate to vertices - in parallel
            Parallel.For(0, indices.Count / 3, faceIndex =>
            {
                int i = faceIndex * 3;

                // Skip if out of bounds
                if (i + 2 >= indices.Count)
                    return;

                int i1 = indices[i];
                int i2 = indices[i + 1];
                int i3 = indices[i + 2];

                // Skip if any vertex index is out of bounds
                if (i1 >= deformedVertices.Count ||
                    i2 >= deformedVertices.Count ||
                    i3 >= deformedVertices.Count)
                    return;

                // Get vertex positions
                Vector3 v1 = deformedVertices[i1];
                Vector3 v2 = deformedVertices[i2];
                Vector3 v3 = deformedVertices[i3];

                // Calculate face normal
                Vector3 edge1 = v2 - v1;
                Vector3 edge2 = v3 - v1;
                Vector3 faceNormal = Vector3.Cross(edge1, edge2);

                // Skip degenerate triangles
                if (faceNormal.LengthSquared < 0.000001f)
                    return;

                // Normalize the face normal
                float length = faceNormal.Length;
                if (length > 0.000001f)
                    faceNormal = faceNormal / length;

                // Add weighted face normal to vertex normals (using Interlocked methods for thread safety)
                // Weight by face area for more accurate normals on irregular meshes
                float area = length * 0.5f;

                // Accumulate to thread-local arrays
                Vector3 weightedNormal = faceNormal * area;

                // Using lock per vertex is more granular than a global lock
                if (i1 < tempNormals.Length)
                    lock (tempNormals)
                        tempNormals[i1] += weightedNormal;

                if (i2 < tempNormals.Length)
                    lock (tempNormals)
                        tempNormals[i2] += weightedNormal;

                if (i3 < tempNormals.Length)
                    lock (tempNormals)
                        tempNormals[i3] += weightedNormal;
            });

            // Transfer accumulated normals to the main normals array
            for (int i = 0; i < vertexCount && i < normals.Count; i++)
            {
                normals[i] = tempNormals[i];
            }

            // Normalize all vertex normals in parallel
            Parallel.For(0, vertexCount, i =>
            {
                // Skip if out of bounds
                if (i >= normals.Count)
                    return;

                if (normals[i].LengthSquared > 0.000001f)
                {
                    normals[i].Normalize();
                }
                else
                {
                    // Default normal for degenerate cases
                    normals[i] = new Vector3(0, 1, 0);
                }
            });

            // Update normal buffer if using VBOs
            if (buffersInitialized)
            {
                float[] normalData = new float[normals.Count * 3];
                for (int i = 0; i < normals.Count; i++)
                {
                    normalData[i * 3] = normals[i].X;
                    normalData[i * 3 + 1] = normals[i].Y;
                    normalData[i * 3 + 2] = normals[i].Z;
                }

                GL.BindBuffer(BufferTarget.ArrayBuffer, normalBufferId);
                GL.BufferData(BufferTarget.ArrayBuffer, (IntPtr)(normalData.Length * sizeof(float)),
                              normalData, BufferUsageHint.StaticDraw);
                GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
            }
        }
        private void ExtractVolumeData(Material material)
        {
            if (material == null)
            {
                Logger.Log("[TriaxialSimulationForm] Error: Null material in ExtractVolumeData");
                return;
            }

            // Get volume data from MainForm
            labelVolume = mainForm.volumeLabels;

            if (labelVolume == null)
            {
                Logger.Log("[TriaxialSimulationForm] Error: Null label volume in ExtractVolumeData");
                return;
            }

            // Get dimensions
            width = LabelVolumeHelper.GetWidth(labelVolume);
            height = LabelVolumeHelper.GetHeight(labelVolume);
            depth = LabelVolumeHelper.GetDepth(labelVolume);

            Logger.Log($"[TriaxialSimulationForm] Volume dimensions: {width}x{height}x{depth}");

            // Create a byte array to store density values
            densityVolume = new byte[width, height, depth];

            // Extract density data from grayscale volume for selected material
            byte materialID = material.ID;

            Logger.Log($"[TriaxialSimulationForm] Extracting volume data for material ID {materialID}, name: {material.Name}");

            // Count material voxels for validation
            int materialVoxelCount = 0;

            // Use parallel processing to speed up extraction
            Parallel.For(0, depth, z =>
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        // Check if this voxel belongs to the selected material
                        if (LabelVolumeHelper.GetLabel(labelVolume, x, y, z) == materialID)
                        {
                            // Store the grayscale value as a proxy for density
                            densityVolume[x, y, z] = mainForm.volumeData[x, y, z];
                            Interlocked.Increment(ref materialVoxelCount);
                        }
                        else
                        {
                            // Not part of the material
                            densityVolume[x, y, z] = 0;
                        }
                    }
                }
            });

            Logger.Log($"[TriaxialSimulationForm] Found {materialVoxelCount} voxels of material ID {materialID}");
        }
        /// <summary>
        /// Build a watertight surface mesh of the selected material.
        /// – samplingRate ≥ 1 : 1 = full resolution, 2 = every second voxel, …  
        /// – worker        : BackgroundWorker for UI progress/cancel
        /// 
        /// Global lists filled:
        ///     vertices, normals, indices, densityValues, tetrahedralElements
        /// Flags set/updated:
        ///     minDensity, maxDensity, meshGenerationComplete (in caller)
        /// </summary>
        /// 
        private struct Dir
        {
            public int dx, dy, dz;
            public Vector3 n;
            public Dir(int dx, int dy, int dz, Vector3 n) { this.dx = dx; this.dy = dy; this.dz = dz; this.n = n; }
        }
        private void GenerateMesh(int samplingRate, BackgroundWorker worker)
        {
            /* ---------- 0. reset ---------- */
            vertices.Clear();
            normals.Clear();
            indices.Clear();
            densityValues.Clear();
            tetrahedralElements.Clear();
            minDensity = float.MaxValue;
            maxDensity = float.MinValue;

            /* ---------- 1. collect voxel centres that belong to the specimen ---------- */
            var voxels = new List<Tuple<int, int, int>>();

            float vSize = (float)pixelSize;                 // isotropic voxel size
            float cx = width * 0.5f;
            float cy = height * 0.5f;
            float cz = depth * 0.5f;

            float radius = Math.Min(Math.Min(width, height), depth) * vSize * 0.30f;
            float halfLen;
            switch (selectedDirection)
            {
                case SimulationDirection.X: halfLen = width * vSize * 0.40f; break;
                case SimulationDirection.Y: halfLen = height * vSize * 0.40f; break;
                default: halfLen = depth * vSize * 0.40f; break; // Z
            }

            for (int z = 0; z < depth; z += samplingRate)
            {
                float pz = (z - cz) * vSize;
                for (int y = 0; y < height; y += samplingRate)
                {
                    float py = (y - cy) * vSize;
                    for (int x = 0; x < width; x += samplingRate)
                    {
                        if (densityVolume[x, y, z] == 0) continue;

                            voxels.Add(Tuple.Create(x, y, z));
                    }
                }
                if (z % Math.Max(1, depth / 10) == 0)
                    worker.ReportProgress(10 + z * 30 / depth, $"Scanning… {z}/{depth}");
                if (worker.CancellationPending) return;
            }

            if (voxels.Count == 0) return;
            worker.ReportProgress(40, $"Core voxels: {voxels.Count}");

            /* ---------- 2. build faces – every exposed side gets its own quad ---------- */
            Dir[] dirs =
            {
        new Dir( 1, 0, 0, new Vector3( 1, 0, 0)),
        new Dir(-1, 0, 0, new Vector3(-1, 0, 0)),
        new Dir( 0, 1, 0, new Vector3( 0, 1, 0)),
        new Dir( 0,-1, 0, new Vector3( 0,-1, 0)),
        new Dir( 0, 0, 1, new Vector3( 0, 0, 1)),
        new Dir( 0, 0,-1, new Vector3( 0, 0,-1))
    };

            /* quick lookup so we know if the neighbour is inside material */
            var voxelSet = new HashSet<Tuple<int, int, int>>(voxels);

            float half = vSize * samplingRate * 0.5f;       // half-edge of a sampled “supervoxel”

            foreach (var vxc in voxels)
            {
                int x = vxc.Item1, y = vxc.Item2, z = vxc.Item3;
                float dens = densityVolume[x, y, z] / 255f;
                minDensity = Math.Min(minDensity, dens);
                maxDensity = Math.Max(maxDensity, dens);
                /* voxel centre in physical space */
                float cX = (x - cx) * vSize;
                float cY = (y - cy) * vSize;
                float cZ = (z - cz) * vSize;

                foreach (Dir d in dirs)
                {
                    var nb = Tuple.Create(x + d.dx, y + d.dy, z + d.dz);
                    if (voxelSet.Contains(nb)) continue;    // neighbour also inside → hidden face

                    /* four corners of the face */
                    Vector3 p1, p2, p3, p4;     // order: CCW seen from outside
                    if (d.dx != 0)             /* ±X face */
                    {
                        float sign = d.dx > 0 ? +half : -half;
                        p1 = new Vector3(cX + sign, cY - half, cZ - half);
                        p2 = new Vector3(cX + sign, cY + half, cZ - half);
                        p3 = new Vector3(cX + sign, cY + half, cZ + half);
                        p4 = new Vector3(cX + sign, cY - half, cZ + half);
                    }
                    else if (d.dy != 0)        /* ±Y face */
                    {
                        float sign = d.dy > 0 ? +half : -half;
                        p1 = new Vector3(cX - half, cY + sign, cZ - half);
                        p2 = new Vector3(cX + half, cY + sign, cZ - half);
                        p3 = new Vector3(cX + half, cY + sign, cZ + half);
                        p4 = new Vector3(cX - half, cY + sign, cZ + half);
                    }
                    else                       /* ±Z face */
                    {
                        float sign = d.dz > 0 ? +half : -half;
                        p1 = new Vector3(cX - half, cY - half, cZ + sign);
                        p2 = new Vector3(cX + half, cY - half, cZ + sign);
                        p3 = new Vector3(cX + half, cY + half, cZ + sign);
                        p4 = new Vector3(cX - half, cY + half, cZ + sign);
                    }

                    /* add vertices & attributes */
                    int i1 = vertices.Count; vertices.Add(p1);
                    int i2 = vertices.Count; vertices.Add(p2);
                    int i3 = vertices.Count; vertices.Add(p3);
                    int i4 = vertices.Count; vertices.Add(p4);

                    normals.Add(d.n); normals.Add(d.n); normals.Add(d.n); normals.Add(d.n);

                    densityValues.Add(dens); densityValues.Add(dens);
                    densityValues.Add(dens); densityValues.Add(dens);

                    /* two triangles */
                    indices.Add(i1); indices.Add(i2); indices.Add(i3);
                    indices.Add(i3); indices.Add(i4); indices.Add(i1);
                }
            }

            worker.ReportProgress(90, $"Vertices {vertices.Count}, faces {indices.Count / 3}");

            /* ---------- 3. (unchanged) lightweight tetrahedral fill ---------- */
            int made = 0, limit = 5000, stride = samplingRate;
            foreach (var vxc in voxels)
            {
                if (made >= limit) break;
                int x = vxc.Item1, y = vxc.Item2, z = vxc.Item3;

                var v000 = Tuple.Create(x, y, z);
                var v100 = Tuple.Create(x + stride, y, z);
                var v010 = Tuple.Create(x, y + stride, z);
                var v001 = Tuple.Create(x, y, z + stride);
                var v111 = Tuple.Create(x + stride, y + stride, z + stride);

                if (!voxelSet.Contains(v100) ||
                    !voxelSet.Contains(v010) ||
                    !voxelSet.Contains(v001) ||
                    !voxelSet.Contains(v111))
                    continue;

                /* indices for the *centre* vertices, not the face vertices – use voxel order */
                int i000 = vertices.Count;    // add voxel centre as a tet node
                vertices.Add(new Vector3((x - cx) * vSize, (y - cy) * vSize, (z - cz) * vSize));
                normals.Add(Vector3.UnitY); densityValues.Add(0);

                int i100 = vertices.Count;
                vertices.Add(new Vector3((x + stride - cx) * vSize, (y - cy) * vSize, (z - cz) * vSize));
                normals.Add(Vector3.UnitY); densityValues.Add(0);

                int i010 = vertices.Count;
                vertices.Add(new Vector3((x - cx) * vSize, (y + stride - cy) * vSize, (z - cz) * vSize));
                normals.Add(Vector3.UnitY); densityValues.Add(0);

                int i001 = vertices.Count;
                vertices.Add(new Vector3((x - cx) * vSize, (y - cy) * vSize, (z + stride - cz) * vSize));
                normals.Add(Vector3.UnitY); densityValues.Add(0);

                int i111 = vertices.Count;
                vertices.Add(new Vector3((x + stride - cx) * vSize, (y + stride - cy) * vSize, (z + stride - cz) * vSize));
                normals.Add(Vector3.UnitY); densityValues.Add(0);

                tetrahedralElements.Add(new TetrahedralElement(i000, i100, i010, i001));
                if (++made >= limit) break;
                tetrahedralElements.Add(new TetrahedralElement(i111, i100, i010, i001));
                if (++made >= limit) break;
            }
            if (Math.Abs(maxDensity - minDensity) < 0.001f)
            {
                maxDensity = minDensity + 1.0f;
            }
            worker.ReportProgress(100, "Mesh generation finished");
        }
        private void GlControl_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                lastMousePos = e.Location;
                isDragging = true;
                rotationTimer.Start(); // Start continuous rendering during drag
            }
        }
        private void GlControl_MouseMove(object sender, MouseEventArgs e)
        {
            if (isDragging)
            {
                int deltaX = e.X - lastMousePos.X;
                int deltaY = e.Y - lastMousePos.Y;

                rotationY += deltaX * 0.5f;
                rotationX += deltaY * 0.5f;
                lastMousePos = e.Location;

                // No need to invalidate here, the timer does it
            }
        }
        private void StressStrainGraph_Paint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Color.White);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Draw axes
            int padding = 30;
            int width = stressStrainGraph.Width - 2 * padding;
            int height = stressStrainGraph.Height - 2 * padding;

            using (Pen axisPen = new Pen(Color.Black, 2))
            {
                // X-axis
                g.DrawLine(axisPen, padding, stressStrainGraph.Height - padding,
                           stressStrainGraph.Width - padding, stressStrainGraph.Height - padding);

                // Y-axis
                g.DrawLine(axisPen, padding, stressStrainGraph.Height - padding,
                           padding, padding);
            }

            // Draw axis labels
            using (Font labelFont = new Font("Arial", 8))
            {
                g.DrawString("Strain (%)", labelFont, Brushes.Black,
                            stressStrainGraph.Width / 2, stressStrainGraph.Height - 15);

                // Rotate Y-axis label
                g.TranslateTransform(15, stressStrainGraph.Height / 2);
                g.RotateTransform(-90);
                g.DrawString("Stress (MPa)", labelFont, Brushes.Black, 0, 0);
                g.ResetTransform();
            }

            // Calculate appropriate scale for Y-axis
            float maxStressValue = youngModulus * 0.3f; // Default max stress scale (30% of Young's modulus)

            // If we have stress-strain data, adjust scale based on actual maximum stress
            if (stressStrainCurve.Count > 0)
            {
                float maxCurrentStress = stressStrainCurve.Max(p => p.Y) / 10.0f; // Convert to MPa
                maxStressValue = Math.Max(maxStressValue, maxCurrentStress * 1.2f); // Add 20% margin
            }

            // Calculate maximum yield/failure markers
            float maxMarkerStress = Math.Max(yieldStrength, brittleStrength);
            if (maxMarkerStress > 0)
            {
                maxStressValue = Math.Max(maxStressValue, maxMarkerStress * 1.1f);
            }

            // Round max stress to a nice value
            maxStressValue = (float)Math.Ceiling(maxStressValue / 100) * 100;

            // Draw stress-strain curve
            if (stressStrainCurve.Count >= 2)
            {
                using (Pen curvePen = new Pen(Color.Blue, 2))
                {
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
                }
            }

            // Draw yield and brittle strength lines if behaviors are enabled
            if (isPlasticEnabled || isBrittleEnabled)
            {
                // Scale for graph
                int maxStress = (int)(maxStressValue * 10); // Convert to graph units

                // Draw yield strength line if plastic behavior is enabled
                if (isPlasticEnabled && yieldStrength > 0)
                {
                    using (Pen yieldPen = new Pen(Color.Orange, 1) { DashStyle = DashStyle.Dash })
                    {
                        int y = stressStrainGraph.Height - padding - (int)(yieldStrength * 10 * height / maxStress);
                        g.DrawLine(yieldPen, padding, y, stressStrainGraph.Width - padding, y);
                        g.DrawString($"Yield: {yieldStrength:F0} MPa", new Font("Arial", 8), Brushes.Orange,
                                    stressStrainGraph.Width - padding - 120, y - 15);
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
                                    stressStrainGraph.Width - padding - 120, y - 15);
                    }
                }
            }

            // Draw grid lines
            using (Pen gridPen = new Pen(Color.LightGray, 1))
            {
                // X-axis grid (strain)
                for (int i = 5; i <= 20; i += 5)
                {
                    int x = padding + (int)(i * 10 * width / 200);
                    g.DrawLine(gridPen, x, stressStrainGraph.Height - padding, x, padding);
                    g.DrawString(i.ToString() + "%", new Font("Arial", 8), Brushes.Black,
                                x - 10, stressStrainGraph.Height - padding + 5);
                }

                // Y-axis grid (stress) - more intelligent division
                int numDivisions = 5;
                float divisionSize = maxStressValue / numDivisions;

                // Round division size to a nice value
                if (divisionSize > 200) divisionSize = (float)Math.Ceiling(divisionSize / 100) * 100;
                else if (divisionSize > 20) divisionSize = (float)Math.Ceiling(divisionSize / 10) * 10;
                else divisionSize = (float)Math.Ceiling(divisionSize);

                for (int i = 1; i <= numDivisions; i++)
                {
                    float stressValue = i * divisionSize;
                    int maxStressPixels = (int)(maxStressValue * 10); // Convert to graph units
                    int y = stressStrainGraph.Height - padding - (int)(stressValue * 10 * height / maxStressPixels);
                    g.DrawLine(gridPen, padding, y, stressStrainGraph.Width - padding, y);
                    g.DrawString(stressValue.ToString("0"), new Font("Arial", 8),
                                Brushes.Black, padding - 25, y - 5);
                }
            }

            // Add title showing bulk density and any other relevant material properties
            using (Font titleFont = new Font("Arial", 9, FontStyle.Bold))
            {
                string densityInfo = $"Material: {(selectedMaterial != null ? selectedMaterial.Name : "Unknown")}";
                densityInfo += $" | Density: {bulkDensity:F0} kg/m³";

                if (porosity > 0)
                {
                    densityInfo += $" | Porosity: {porosity:P1}";
                }

                g.DrawString(densityInfo, titleFont, Brushes.Navy,
                           (stressStrainGraph.Width / 2) - 150, 10);
            }

            // Draw current stress/strain point
            if (stressStrainCurve.Count > 0)
            {
                Point lastPoint = stressStrainCurve.Last();

                // Scale point to fit in the graph
                int maxStrain = 200; // 20% strain
                int maxStress = (int)(maxStressValue * 10); // Convert to graph units

                int x = padding + (int)(lastPoint.X * width / maxStrain);
                int y = stressStrainGraph.Height - padding - (int)(lastPoint.Y * height / maxStress);

                // Draw a larger crosshair for current point
                using (Pen pointPen = new Pen(Color.Red, 2))
                {
                    g.DrawLine(pointPen, x - 5, y, x + 5, y);
                    g.DrawLine(pointPen, x, y - 5, x, y + 5);
                }

                using (SolidBrush pointBrush = new SolidBrush(Color.Red))
                {
                    g.FillEllipse(pointBrush, x - 4, y - 4, 8, 8);
                }

                // Display current values
                using (Font valueFont = new Font("Arial", 10, FontStyle.Bold))
                {
                    float currentStress = lastPoint.Y / 10.0f; // Convert to MPa
                    float currentStrain = lastPoint.X / 10.0f; // Convert to %

                    string stressStr = $"{currentStress:F1} MPa";
                    string strainStr = $"{currentStrain:F1}%";

                    // Create a semi-transparent background for readability
                    using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(200, Color.White)))
                    {
                        g.FillRectangle(bgBrush, padding, padding, 200, 80);
                    }

                    g.DrawString("Strain: " + strainStr, valueFont, Brushes.Black, padding + 5, padding + 5);
                    g.DrawString("Stress: " + stressStr, valueFont, Brushes.Black, padding + 5, padding + 25);

                    // Calculate Young's modulus from curve slope at origin (elastic region)
                    if (stressStrainCurve.Count >= 10)
                    {
                        int initialPoints = Math.Min(10, stressStrainCurve.Count / 4);
                        float initialStrain = stressStrainCurve[initialPoints - 1].X / 10.0f / 100.0f; // Convert to decimal
                        float initialStress = stressStrainCurve[initialPoints - 1].Y / 10.0f; // MPa

                        // Calculate modulus if strain is non-zero
                        if (initialStrain > 0.0001f)
                        {
                            float measuredModulus = initialStress / initialStrain;
                            g.DrawString($"Measured E: {measuredModulus:F0} MPa", valueFont, Brushes.Green, padding + 5, padding + 45);
                        }
                    }

                    // Show active behaviors
                    string behaviors = "Behaviors: ";
                    if (isElasticEnabled) behaviors += "Elastic ";
                    if (isPlasticEnabled) behaviors += "Plastic ";
                    if (isBrittleEnabled) behaviors += "Brittle";
                    g.DrawString(behaviors, valueFont, Brushes.Blue, padding + 5, padding + 65);
                }
            }

            // Add legend for material stages if both plastic and brittle are enabled
            if (isElasticEnabled && (isPlasticEnabled || isBrittleEnabled))
            {
                // Show material phase regions
                int legendWidth = 200;
                int legendHeight = 80;
                int legendX = stressStrainGraph.Width - padding - legendWidth;
                int legendY = padding;

                // Draw background
                using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(240, Color.White)))
                {
                    g.FillRectangle(bgBrush, legendX, legendY, legendWidth, legendHeight);
                }

                // Draw border
                using (Pen borderPen = new Pen(Color.Gray, 1))
                {
                    g.DrawRectangle(borderPen, legendX, legendY, legendWidth, legendHeight);
                }

                // Add title
                using (Font titleFont = new Font("Arial", 8, FontStyle.Bold))
                {
                    g.DrawString("Material Phases", titleFont, Brushes.Black, legendX + 5, legendY + 5);
                }

                // Draw phase indicators
                int lineY = legendY + 30;
                int lineLength = 15;
                int textOffset = 20;

                // Elastic phase (solid blue line)
                using (Pen elasticPen = new Pen(Color.Blue, 2))
                {
                    g.DrawLine(elasticPen, legendX + 5, lineY, legendX + 5 + lineLength, lineY);
                    g.DrawString("Elastic", new Font("Arial", 8), Brushes.Black, legendX + 5 + textOffset, lineY - 5);
                }

                // Plastic phase if enabled (solid orange line)
                if (isPlasticEnabled)
                {
                    lineY += 20;
                    using (Pen plasticPen = new Pen(Color.Orange, 2))
                    {
                        g.DrawLine(plasticPen, legendX + 5, lineY, legendX + 5 + lineLength, lineY);
                        g.DrawString("Plastic (Yielding)", new Font("Arial", 8), Brushes.Black, legendX + 5 + textOffset, lineY - 5);
                    }
                }

                // Brittle phase if enabled (dashed red line)
                if (isBrittleEnabled)
                {
                    lineY += 20;
                    using (Pen brittlePen = new Pen(Color.Red, 2) { DashStyle = DashStyle.Dash })
                    {
                        g.DrawLine(brittlePen, legendX + 5, lineY, legendX + 5 + lineLength, lineY);
                        g.DrawString("Brittle Failure", new Font("Arial", 8), Brushes.Black, legendX + 5 + textOffset, lineY - 5);
                    }
                }
            }
        }
        private void GlControl_Paint(object sender, PaintEventArgs e)
        {
            // Ensure valid context
            if (!glControl.IsHandleCreated) return;
            if (!glControl.Context.IsCurrent) glControl.MakeCurrent();

            // Clear buffers
            GL.ClearColor(0.2f, 0.2f, 0.2f, 1.0f);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            // Skip if no mesh
            if (deformedVertices.Count == 0)
            {
                glControl.SwapBuffers();
                return;
            }

            // Set up camera
            GL.MatrixMode(MatrixMode.Projection);
            GL.LoadIdentity();
            float aspect = glControl.Width / (float)glControl.Height;
            Matrix4 proj = Matrix4.CreatePerspectiveFieldOfView(
                MathHelper.DegreesToRadians(40.0f),
                aspect,
                0.1f,
                1000.0f);
            GL.LoadMatrix(ref proj);

            // Set up modelview matrix
            GL.MatrixMode(MatrixMode.Modelview);
            GL.LoadIdentity();

            // Position camera
            GL.Translate(0, 0, -20.0f * zoom);
            GL.Rotate(rotationX, 1.0f, 0.0f, 0.0f);
            GL.Rotate(rotationY, 0.0f, 1.0f, 0.0f);

            // Set up basic lighting
            if (!wireframeMode && !isRotating)
            {
                GL.Enable(EnableCap.Lighting);
                GL.Enable(EnableCap.Light0);

                float[] lightPos = { 10.0f, 10.0f, 10.0f, 1.0f };
                float[] lightAmbient = { 0.4f, 0.4f, 0.4f, 1.0f };
                float[] lightDiffuse = { 0.8f, 0.8f, 0.8f, 1.0f };

                GL.Light(LightName.Light0, LightParameter.Position, lightPos);
                GL.Light(LightName.Light0, LightParameter.Ambient, lightAmbient);
                GL.Light(LightName.Light0, LightParameter.Diffuse, lightDiffuse);
            }
            else
            {
                GL.Disable(EnableCap.Lighting);
            }

            // Draw mesh
            DrawMesh();

            // Draw color legend if enabled
            if (chkShowDensity.Checked)
            {
                //DrawColorLegend();
            }

            // Present
            glControl.SwapBuffers();
        }
        private void DisposeDisplayList()
        {
            if (meshDisplayList != 0)
            {
                GL.DeleteLists(meshDisplayList, 1);
                meshDisplayList = 0;
            }
        }
       
        private void DrawMesh()
        {
            // Only do color mapping when not rotating for better performance
            bool showDensity = chkShowDensity.Checked && !isRotating;

            if (wireframeMode)
            {
                GL.Disable(EnableCap.Lighting);
                if (!showDensity)
                {
                    GL.Color3(1.0f, 1.0f, 1.0f);
                }
                GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Line);
            }
            else
            {
                GL.Enable(EnableCap.Lighting);
                GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
            }

            // Draw the mesh as direct triangles - most efficient approach
            GL.Begin(PrimitiveType.Triangles);

            // Draw every Nth triangle when rotating for better performance
            // This is a brutal optimization but will make rotation smooth
            int stride = isRotating ? 10 : 1;

            for (int i = 0; i < indices.Count; i += 3 * stride)
            {
                if (i + 2 >= indices.Count) break;

                int i1 = indices[i];
                int i2 = indices[i + 1];
                int i3 = indices[i + 2];

                // Safety check
                if (i1 < deformedVertices.Count && i2 < deformedVertices.Count && i3 < deformedVertices.Count)
                {
                    // Apply density-based color if showing density
                    if (showDensity && i1 < densityValues.Count && i2 < densityValues.Count && i3 < densityValues.Count)
                    {
                        float density = densityValues[i1];
                        float normalizedDensity = 0.0f;
                        if (maxDensity > minDensity)
                        {
                            normalizedDensity = (density - minDensity) / (maxDensity - minDensity);
                            normalizedDensity = Math.Max(0.0f, Math.Min(1.0f, normalizedDensity));
                            SetColorForValue(normalizedDensity);
                        }
                    }

                    if (i1 < normals.Count) GL.Normal3(normals[i1]);
                    GL.Vertex3(deformedVertices[i1]);

                    if (i2 < normals.Count) GL.Normal3(normals[i2]);
                    GL.Vertex3(deformedVertices[i2]);

                    if (i3 < normals.Count) GL.Normal3(normals[i3]);
                    GL.Vertex3(deformedVertices[i3]);
                }
            }

            GL.End();

            // Reset state
            GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);

            // Draw density legend
            if (showDensity)
            {
                //DrawSimpleColorLegend();
            }
        }

        private Bitmap CaptureGLControl()
        {
            if (!glControl.IsHandleCreated)
                return new Bitmap(1, 1);

            glControl.MakeCurrent();

            // Create a bitmap to hold the image
            Bitmap bitmap = new Bitmap(glControl.Width, glControl.Height);

            // Render the scene exactly like in GlControl_Paint
            GL.ClearColor(0.2f, 0.2f, 0.2f, 1.0f);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            if (deformedVertices.Count > 0)
            {
                // Set up camera
                GL.MatrixMode(MatrixMode.Projection);
                GL.LoadIdentity();
                float aspect = glControl.Width / (float)glControl.Height;
                Matrix4 proj = Matrix4.CreatePerspectiveFieldOfView(
                    MathHelper.DegreesToRadians(40.0f),
                    aspect,
                    0.1f,
                    1000.0f);
                GL.LoadMatrix(ref proj);

                // Set up modelview matrix
                GL.MatrixMode(MatrixMode.Modelview);
                GL.LoadIdentity();

                // Position camera
                GL.Translate(0, 0, -20.0f * zoom);
                GL.Rotate(rotationX, 1.0f, 0.0f, 0.0f);
                GL.Rotate(rotationY, 0.0f, 1.0f, 0.0f);

                // Set up lighting
                bool lightingWasEnabled = GL.IsEnabled(EnableCap.Lighting);
                if (!wireframeMode)
                {
                    GL.Enable(EnableCap.Lighting);
                    GL.Enable(EnableCap.Light0);

                    float[] lightPos = { 10.0f, 10.0f, 10.0f, 1.0f };
                    float[] lightAmbient = { 0.4f, 0.4f, 0.4f, 1.0f };
                    float[] lightDiffuse = { 0.8f, 0.8f, 0.8f, 1.0f };

                    GL.Light(LightName.Light0, LightParameter.Position, lightPos);
                    GL.Light(LightName.Light0, LightParameter.Ambient, lightAmbient);
                    GL.Light(LightName.Light0, LightParameter.Diffuse, lightDiffuse);
                }
                else
                {
                    GL.Disable(EnableCap.Lighting);
                }

                // Draw mesh
                DrawMesh();

                // Draw density legend if showing density
                if (chkShowDensity.Checked)
                {
                    DrawColorLegendGL();
                }

                // Restore lighting state
                if (lightingWasEnabled && !GL.IsEnabled(EnableCap.Lighting))
                    GL.Enable(EnableCap.Lighting);
                else if (!lightingWasEnabled && GL.IsEnabled(EnableCap.Lighting))
                    GL.Disable(EnableCap.Lighting);
            }

            // Make sure rendering is complete
            GL.Flush();
            GL.Finish();

            // Copy the framebuffer to the bitmap
            System.Drawing.Imaging.BitmapData data = bitmap.LockBits(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                System.Drawing.Imaging.ImageLockMode.WriteOnly,
                System.Drawing.Imaging.PixelFormat.Format24bppRgb);

            GL.ReadPixels(0, 0, bitmap.Width, bitmap.Height,
                PixelFormat.Bgr, PixelType.UnsignedByte, data.Scan0);

            bitmap.UnlockBits(data);

            // OpenGL's origin is bottom-left, while GDI+'s is top-left, so flip the image
            bitmap.RotateFlip(RotateFlipType.RotateNoneFlipY);

            // Force a redraw to ensure normal rendering continues
            glControl.Invalidate();

            return bitmap;
        }

        
        private void DrawSimpleColorLegend()
        {
            // Save current matrices
            GL.MatrixMode(MatrixMode.Projection);
            GL.PushMatrix();
            GL.LoadIdentity();
            GL.Ortho(0, glControl.Width, 0, glControl.Height, -1, 1);

            GL.MatrixMode(MatrixMode.Modelview);
            GL.PushMatrix();
            GL.LoadIdentity();

            // Save OpenGL state
            bool lightingEnabled = GL.IsEnabled(EnableCap.Lighting);
            bool depthTestEnabled = GL.IsEnabled(EnableCap.DepthTest);

            // Disable 3D features for 2D overlay
            GL.Disable(EnableCap.Lighting);
            GL.Disable(EnableCap.DepthTest);

            // Legend dimensions and position
            int x = glControl.Width - 30;
            int width = 20;
            int y = 20;
            int height = 200;

            // Draw color gradient
            GL.Begin(PrimitiveType.Quads);

            // Draw gradient in 10 segments for smoother appearance
            for (int i = 0; i < 10; i++)
            {
                float top = y + i * (height / 10.0f);
                float bottom = y + (i + 1) * (height / 10.0f);
                float value = 1.0f - (i / 10.0f);  // Map from 1.0 to 0.0 (high to low)

                // Set color for this segment
                SetColorForValue(value);

                // Draw segment
                GL.Vertex2(x, top);
                GL.Vertex2(x + width, top);
                GL.Vertex2(x + width, bottom);
                GL.Vertex2(x, bottom);
            }

            GL.End();

            // Draw border
            GL.Color3(1.0f, 1.0f, 1.0f);
            GL.Begin(PrimitiveType.LineLoop);
            GL.Vertex2(x, y);
            GL.Vertex2(x + width, y);
            GL.Vertex2(x + width, y + height);
            GL.Vertex2(x, y + height);
            GL.End();

            // Draw tick marks
            GL.Begin(PrimitiveType.Lines);
            for (int i = 0; i <= 5; i++)
            {
                float tickY = y + i * (height / 5.0f);
                GL.Vertex2(x, tickY);
                GL.Vertex2(x - 5, tickY);
            }
            GL.End();

            // Restore 3D settings if they were enabled
            if (lightingEnabled) GL.Enable(EnableCap.Lighting);
            if (depthTestEnabled) GL.Enable(EnableCap.DepthTest);

            // Restore matrices
            GL.MatrixMode(MatrixMode.Projection);
            GL.PopMatrix();
            GL.MatrixMode(MatrixMode.Modelview);
            GL.PopMatrix();

            // Now draw the text using GDI+ (keeping the same approach)
            using (Graphics g = Graphics.FromHwnd(glControl.Handle))
            {
                // Rest of the text drawing code remains the same
                if (g != null)
                {
                    // Set high quality text rendering
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                    using (Font font = new Font("Arial", 8, FontStyle.Bold))
                    {
                        // Draw title label with dark background for better visibility
                        using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(100, 0, 0, 0)))
                        {
                            // Title background
                            g.FillRectangle(bgBrush, x - 65, 5, 60, 20);
                            g.DrawString("Density", font, Brushes.White, x - 60, 10);

                            // Units background and text
                            string unitText = isDensityCalibrated ? "kg/m³" : "(norm)";
                            g.FillRectangle(bgBrush, x - 65, 25, 60, 15);
                            g.DrawString(unitText, font, Brushes.White, x - 60, 25);
                        }

                        // Draw tick labels
                        for (int i = 0; i <= 5; i++)
                        {
                            float normalizedValue = 1.0f - (i / 5.0f);  // 1.0 to 0.0 (top to bottom)
                            float value = minDensity + (maxDensity - minDensity) * normalizedValue;

                            // Calculate screen coordinates - y is inverted in GDI+
                            int tickY = (int)(y + i * (height / 5.0f));
                            int screenY = glControl.Height - tickY - 6;  // Adjust y for GDI+

                            // Format the value based on calibration
                            string valueText = isDensityCalibrated
                                ? $"{value:F0}" // Integer format for density
                                : $"{normalizedValue:F1}"; // One decimal place for normalized value

                            // Draw background for better visibility
                            using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(100, 0, 0, 0)))
                            {
                                g.FillRectangle(bgBrush, x - 45, screenY - 6, 40, 15);
                            }

                            // Draw text
                            g.DrawString(valueText, font, Brushes.White, x - 40, screenY - 6);
                        }

                        // Add min/max labels if calibrated
                        if (isDensityCalibrated)
                        {
                            using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(100, 0, 0, 0)))
                            {
                                int maxY = glControl.Height - y - height - 6;
                                int minY = glControl.Height - y - 6;

                                g.FillRectangle(bgBrush, x - 65, maxY - 6, 20, 15);
                                g.DrawString("Max", font, Brushes.White, x - 60, maxY - 6);

                                g.FillRectangle(bgBrush, x - 65, minY - 6, 20, 15);
                                g.DrawString("Min", font, Brushes.White, x - 60, minY - 6);
                            }
                        }
                    }
                }
            }
        }
        private void SetColorForValue(float normalizedValue)
        {
            // Clamp value to 0-1 range
            normalizedValue = Math.Max(0.0f, Math.Min(1.0f, normalizedValue));

            // Blue to red color spectrum
            float r, g, b;

            if (normalizedValue < 0.25f)
            {
                // Blue to Cyan
                r = 0.0f;
                g = normalizedValue * 4.0f;
                b = 1.0f;
            }
            else if (normalizedValue < 0.5f)
            {
                // Cyan to Green
                r = 0.0f;
                g = 1.0f;
                b = 1.0f - (normalizedValue - 0.25f) * 4.0f;
            }
            else if (normalizedValue < 0.75f)
            {
                // Green to Yellow
                r = (normalizedValue - 0.5f) * 4.0f;
                g = 1.0f;
                b = 0.0f;
            }
            else
            {
                // Yellow to Red
                r = 1.0f;
                g = 1.0f - (normalizedValue - 0.75f) * 4.0f;
                b = 0.0f;
            }

            // Set color
            GL.Color3(r, g, b);
        }
        
        
        private int meshDisplayList = 0;
        private void AddSaveImageButton()
        {
            // Create the save image button
            btnSaveImage = new KryptonButton
            {
                Text = "Save Image",
                Location = new Point(10, 910), // Position below the Stop Simulation button
                Width = 310,
                Height = 30,
                StateCommon = {
            Back = { Color1 = Color.FromArgb(60, 60, 120) },
            Content = { ShortText = { Color1 = Color.White } }
        }
            };
            btnSaveImage.Click += BtnSaveImage_Click;

            // Find the controls content panel to add the button to
            Panel controlsContent = null;
            foreach (Control control in this.Controls)
            {
                if (control is TableLayoutPanel mainLayout)
                {
                    foreach (Control panelControl in mainLayout.Controls)
                    {
                        if (panelControl is Panel panel)
                        {
                            foreach (Control c in panel.Controls)
                            {
                                if (c is Panel scrollablePanel && scrollablePanel.AutoScroll)
                                {
                                    foreach (Control sc in scrollablePanel.Controls)
                                    {
                                        if (sc is Panel content && content.Height > 800)
                                        {
                                            controlsContent = content;
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            if (controlsContent != null)
            {
                controlsContent.Controls.Add(btnSaveImage);
            }
            else
            {
                // Fallback - add directly to form
                this.Controls.Add(btnSaveImage);
            }
        }
        private void GlControl_Load(object sender, EventArgs e)
        {
            if (!glControl.IsHandleCreated) return;

            glControl.MakeCurrent();

            // Set up OpenGL state
            GL.ClearColor(0.2f, 0.2f, 0.2f, 1.0f);
            GL.Enable(EnableCap.DepthTest);
            GL.DepthFunc(DepthFunction.Less);
            GL.ShadeModel(ShadingModel.Smooth);

            // Disable unused features
            GL.Disable(EnableCap.Texture2D);
            GL.Disable(EnableCap.Fog);
            GL.Disable(EnableCap.Dither);

            // Enable backface culling for better performance
            GL.Enable(EnableCap.CullFace);
            GL.CullFace(CullFaceMode.Back);

            // Set up viewport
            GL.Viewport(0, 0, glControl.Width, glControl.Height);

            // Check hardware acceleration
            CheckHardwareAcceleration();

            glControlInitialized = true;
        }
        private void GlControl_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                isDragging = false;
                rotationTimer.Stop(); // Stop continuous rendering
                glControl.Invalidate(); // Final redraw
            }
        }
        private void GlControl_MouseWheel(object sender, MouseEventArgs e)
        {
            zoom *= (float)Math.Pow(1.1, e.Delta / 120.0);
            zoom = Math.Max(0.1f, Math.Min(10.0f, zoom));
            glControl.Invalidate();
        }
        private void GlControl_Resize(object sender, EventArgs e)
        {
            if (!glControl.IsHandleCreated) return;
            if (!glControl.Context.IsCurrent) glControl.MakeCurrent();
            if (glControl.Width <= 0 || glControl.Height <= 0) return;

            // Update viewport
            GL.Viewport(0, 0, glControl.Width, glControl.Height);

            // Reposition legend panel
            if (colorLegendPanel != null)
            {
                colorLegendPanel.Location = new Point(glControl.Width - 105, 10);
            }

            // Force redraw
            glControl.Invalidate();
        }
        private void BtnSaveImage_Click(object sender, EventArgs e)
        {
            try
            {
                using (SaveFileDialog saveDialog = new SaveFileDialog())
                {
                    saveDialog.Filter = "PNG Image|*.png|JPEG Image|*.jpg|Bitmap Image|*.bmp";
                    saveDialog.Title = "Save Simulation Image";
                    saveDialog.DefaultExt = "png";
                    saveDialog.FileName = $"TriaxialSimulation_{DateTime.Now:yyyyMMdd_HHmmss}";

                    if (saveDialog.ShowDialog() == DialogResult.OK)
                    {
                        // Render the scene once to ensure it's up-to-date
                        glControl.Invalidate();
                        glControl.Update();

                        // Create the composite image
                        Bitmap compositeImage = CreateCompositeImage();

                        // Save the image
                        string extension = Path.GetExtension(saveDialog.FileName).ToLower();
                        switch (extension)
                        {
                            case ".jpg":
                            case ".jpeg":
                                compositeImage.Save(saveDialog.FileName, System.Drawing.Imaging.ImageFormat.Jpeg);
                                break;
                            case ".bmp":
                                compositeImage.Save(saveDialog.FileName, System.Drawing.Imaging.ImageFormat.Bmp);
                                break;
                            case ".png":
                            default:
                                compositeImage.Save(saveDialog.FileName, System.Drawing.Imaging.ImageFormat.Png);
                                break;
                        }

                        MessageBox.Show($"Image saved to {saveDialog.FileName}", "Save Complete",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);

                        // Force a final redraw to ensure the display is correct
                        glControl.Invalidate();
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving image: {ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                Logger.Log($"[TriaxialSimulationForm] Error saving image: {ex.Message}");

                // Force a redraw in case of error
                glControl.Invalidate();
            }
        }
        private Bitmap CreateCompositeImage()
        {
            // Capture the OpenGL render
            Bitmap glRender = CaptureGLControl();

            // Create the composite bitmap
            Bitmap compositeImage = new Bitmap(glRender.Width, glRender.Height);

            using (Graphics g = Graphics.FromImage(compositeImage))
            {
                // Draw the OpenGL render
                g.DrawImage(glRender, 0, 0, glRender.Width, glRender.Height);

                // Draw legend if enabled
                if (chkShowDensity.Checked)
                {
                    // Temporarily create a bitmap of the legend panel
                    using (Bitmap legendBitmap = new Bitmap(colorLegendPanel.Width, colorLegendPanel.Height))
                    {
                        colorLegendPanel.DrawToBitmap(legendBitmap, new Rectangle(0, 0, colorLegendPanel.Width, colorLegendPanel.Height));
                        g.DrawImage(legendBitmap, colorLegendPanel.Location);
                    }
                }

                // Add simulation info
                string info = $"Material: {(selectedMaterial != null ? selectedMaterial.Name : "Unknown")}";
                info += $"\nDensity: {bulkDensity:F0} kg/m³";

                if (simulationRunning || stressStrainCurve.Count > 0)
                {
                    float currentStress = stressStrainCurve.Count > 0 ?
                        stressStrainCurve.Last().Y / 10.0f : 0; // Convert to MPa
                    float currentStrainPercent = currentStrain * 100; // Convert to percentage

                    info += $"\nStrain: {currentStrainPercent:F1}%";
                    info += $"\nStress: {currentStress:F1} MPa";
                }

                using (Font font = new Font("Arial", 10, FontStyle.Bold))
                using (Brush brush = new SolidBrush(Color.White))
                using (Brush shadowBrush = new SolidBrush(Color.FromArgb(128, 0, 0, 0)))
                {
                    // Draw text shadow and then text for better readability
                    g.DrawString(info, font, shadowBrush, glRender.Width + 5 + 1, 11);
                    g.DrawString(info, font, brush, glRender.Width + 5, 10);
                }
            }

            return compositeImage;
        }
       
        
        private Color GetColorForValue(float normalizedValue)
        {
            // Clamp value to 0-1 range
            normalizedValue = Math.Max(0.0f, Math.Min(1.0f, normalizedValue));

            // Blue to red color spectrum (same as in SetColorForValue method)
            float r, g, b;

            if (normalizedValue < 0.25f)
            {
                // Blue to Cyan
                r = 0.0f;
                g = normalizedValue * 4.0f;
                b = 1.0f;
            }
            else if (normalizedValue < 0.5f)
            {
                // Cyan to Green
                r = 0.0f;
                g = 1.0f;
                b = 1.0f - (normalizedValue - 0.25f) * 4.0f;
            }
            else if (normalizedValue < 0.75f)
            {
                // Green to Yellow
                r = (normalizedValue - 0.5f) * 4.0f;
                g = 1.0f;
                b = 0.0f;
            }
            else
            {
                // Yellow to Red
                r = 1.0f;
                g = 1.0f - (normalizedValue - 0.75f) * 4.0f;
                b = 0.0f;
            }

            return Color.FromArgb(255, (int)(r * 255), (int)(g * 255), (int)(b * 255));
        }
        
        private void InitializeFontTexture()
        {
            if (fontTextureInitialized)
                return;

            // Create a texture bitmap for rendering text
            fontTexture = new Bitmap(256, 256);
            using (Graphics g = Graphics.FromImage(fontTexture))
            {
                g.Clear(Color.Transparent);
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

                // Use a monospace font for simplicity
                Font font = new Font("Consolas", 16, FontStyle.Bold);

                // Generate all ASCII printable characters
                for (char c = ' '; c <= '~'; c++)
                {
                    int index = c - ' ';
                    int row = index / 16;
                    int col = index % 16;

                    Rectangle rect = new Rectangle(col * 16, row * 16, 16, 16);
                    characterRects[c] = rect;

                    // Draw character
                    g.DrawString(c.ToString(), font, Brushes.White, rect.X, rect.Y);
                }
            }

            // Create OpenGL texture
            GL.GenTextures(1, out fontTextureId);
            GL.BindTexture(TextureTarget.Texture2D, fontTextureId);

            // Set texture parameters
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);

            // Get bitmap data
            System.Drawing.Imaging.BitmapData data = fontTexture.LockBits(
                new Rectangle(0, 0, fontTexture.Width, fontTexture.Height),
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            // Upload texture
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba,
                data.Width, data.Height, 0, PixelFormat.Bgra,
                PixelType.UnsignedByte, data.Scan0);

            fontTexture.UnlockBits(data);
            GL.BindTexture(TextureTarget.Texture2D, 0);

            fontTextureInitialized = true;
        }
        private void DrawColorLegend()
        {
            // Keep the 3D rendering intact by saving the current matrices and states
            GL.MatrixMode(MatrixMode.Projection);
            GL.PushMatrix();
            GL.MatrixMode(MatrixMode.Modelview);
            GL.PushMatrix();

            // Save relevant OpenGL states
            bool lighting = GL.IsEnabled(EnableCap.Lighting);
            bool texture = GL.IsEnabled(EnableCap.Texture2D);
            bool depthTest = GL.IsEnabled(EnableCap.DepthTest);

            // Set up for 2D overlay drawing
            GL.MatrixMode(MatrixMode.Projection);
            GL.LoadIdentity();
            GL.Ortho(0, glControl.Width, 0, glControl.Height, -1, 1);

            GL.MatrixMode(MatrixMode.Modelview);
            GL.LoadIdentity();

            // Disable features that would interfere with 2D drawing
            GL.Disable(EnableCap.Lighting);
            GL.Disable(EnableCap.Texture2D);
            GL.Disable(EnableCap.DepthTest);

            // Draw the colorbar
            int barWidth = 20;
            int barHeight = 200;
            int barX = glControl.Width - barWidth - 10;
            int barY = 50;

            // Draw the gradient bar
            GL.Begin(PrimitiveType.Quads);
            for (int i = 0; i < barHeight; i++)
            {
                float normalizedValue = 1.0f - (i / (float)barHeight);
                SetColorForValue(normalizedValue);

                GL.Vertex2(barX, barY + i);
                GL.Vertex2(barX + barWidth, barY + i);

                normalizedValue = 1.0f - ((i + 1) / (float)barHeight);
                SetColorForValue(normalizedValue);

                GL.Vertex2(barX + barWidth, barY + i + 1);
                GL.Vertex2(barX, barY + i + 1);
            }
            GL.End();

            // Draw border around the colorbar
            GL.Color3(1.0f, 1.0f, 1.0f);
            GL.Begin(PrimitiveType.LineLoop);
            GL.Vertex2(barX, barY);
            GL.Vertex2(barX + barWidth, barY);
            GL.Vertex2(barX + barWidth, barY + barHeight);
            GL.Vertex2(barX, barY + barHeight);
            GL.End();

            // Draw tick marks
            GL.Begin(PrimitiveType.Lines);
            for (int i = 0; i <= 5; i++)
            {
                float y = barY + i * (barHeight / 5.0f);
                GL.Vertex2(barX, y);
                GL.Vertex2(barX - 5, y);
            }
            GL.End();

            // Finish OpenGL rendering
            GL.Flush();

            // Restore OpenGL state
            if (lighting) GL.Enable(EnableCap.Lighting);
            if (texture) GL.Enable(EnableCap.Texture2D);
            if (depthTest) GL.Enable(EnableCap.DepthTest);

            // Restore matrices
            GL.MatrixMode(MatrixMode.Projection);
            GL.PopMatrix();
            GL.MatrixMode(MatrixMode.Modelview);
            GL.PopMatrix();

            // Now draw text using GDI+ in a safe, separate pass
            try
            {
                using (Graphics g = Graphics.FromHwnd(glControl.Handle))
                {
                    using (Font font = new Font("Arial", 8, FontStyle.Bold))
                    {
                        // Draw title
                        g.DrawString("Density", font, Brushes.White, glControl.Width - barWidth - 50, 20);

                        // Draw unit label
                        string unitText = isDensityCalibrated ? "kg/m³" : "(norm)";
                        g.DrawString(unitText, font, Brushes.White, glControl.Width - barWidth - 50, 35);

                        // Draw tick labels
                        for (int i = 0; i <= 5; i++)
                        {
                            float normalizedValue = 1.0f - (i / 5.0f);
                            float value = minDensity + (maxDensity - minDensity) * normalizedValue;

                            int screenY = glControl.Height - (barY + (int)(i * barHeight / 5.0f)) - 6;

                            string label = isDensityCalibrated ?
                                $"{value:F0}" :
                                $"{normalizedValue:F1}";

                            g.DrawString(label, font, Brushes.White, glControl.Width - barWidth - 40, screenY);
                        }

                        // Draw min/max labels if calibrated
                        if (isDensityCalibrated)
                        {
                            g.DrawString("Max", font, Brushes.White, glControl.Width - barWidth - 60,
                                glControl.Height - barY - barHeight);
                            g.DrawString("Min", font, Brushes.White, glControl.Width - barWidth - 60,
                                glControl.Height - barY - 15);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Just log the error but continue rendering
                Logger.Log($"[TriaxialSimulationForm] Error drawing legend text: {ex.Message}");
            }
        }
        private void DrawColorLegendGL()
        {
            // Save OpenGL state
            bool depthTestEnabled = GL.IsEnabled(EnableCap.DepthTest);
            bool lightingEnabled = GL.IsEnabled(EnableCap.Lighting);
            bool blendEnabled = GL.IsEnabled(EnableCap.Blend);

            // Setup 2D orthographic projection
            GL.MatrixMode(MatrixMode.Projection);
            GL.PushMatrix();
            GL.LoadIdentity();
            GL.Ortho(0, glControl.Width, glControl.Height, 0, -1, 1);

            GL.MatrixMode(MatrixMode.Modelview);
            GL.PushMatrix();
            GL.LoadIdentity();

            // Set up for 2D rendering
            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.Lighting);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

            // Colorbar parameters
            int barWidth = 25;
            int barHeight = 200;
            int barX = glControl.Width - barWidth - 10;
            int barY = 50;

            // Draw colorbar background
            GL.Color4(0.1f, 0.1f, 0.1f, 0.5f);
            GL.Begin(PrimitiveType.Quads);
            GL.Vertex2(barX - 60, barY - 30);
            GL.Vertex2(barX + barWidth + 5, barY - 30);
            GL.Vertex2(barX + barWidth + 5, barY + barHeight + 5);
            GL.Vertex2(barX - 60, barY + barHeight + 5);
            GL.End();

            // Draw colorbar gradient
            GL.Begin(PrimitiveType.Quads);
            for (int i = 0; i < barHeight; i++)
            {
                float normalizedValue = 1.0f - (i / (float)barHeight);

                // Set color for current position
                SetColorForValue(normalizedValue);

                GL.Vertex2(barX, barY + i);
                GL.Vertex2(barX + barWidth, barY + i);

                SetColorForValue(1.0f - ((i + 1) / (float)barHeight));

                GL.Vertex2(barX + barWidth, barY + i + 1);
                GL.Vertex2(barX, barY + i + 1);
            }
            GL.End();

            // Draw colorbar border
            GL.Color3(1.0f, 1.0f, 1.0f);
            GL.Begin(PrimitiveType.LineLoop);
            GL.Vertex2(barX, barY);
            GL.Vertex2(barX + barWidth, barY);
            GL.Vertex2(barX + barWidth, barY + barHeight);
            GL.Vertex2(barX, barY + barHeight);
            GL.End();

            // Draw tick marks and labels
            GL.Begin(PrimitiveType.Lines);
            int numTicks = 6;
            for (int i = 0; i < numTicks; i++)
            {
                float yPos = barY + i * (barHeight / (numTicks - 1));
                GL.Vertex2(barX, yPos);
                GL.Vertex2(barX - 5, yPos);
            }
            GL.End();

            // Draw title
            RenderText("Density", barX - 55, barY - 25, 1.0f);

            // Draw unit label
            string unitText = isDensityCalibrated ? "kg/m³" : "(norm)";
            RenderText(unitText, barX - 55, barY - 5, 0.8f);

            // Draw tick labels
            for (int i = 0; i < numTicks; i++)
            {
                float normalizedValue = 1.0f - (i / (float)(numTicks - 1));
                float value = minDensity + (maxDensity - minDensity) * normalizedValue;
                float yPos = barY + i * (barHeight / (numTicks - 1)) - 8;

                string label = isDensityCalibrated ?
                    $"{value:F0}" : // Density in kg/m³
                    $"{normalizedValue:F1}"; // Normalized value

                RenderText(label, barX - 45, yPos, 0.8f);
            }

            // Draw max/min labels if calibrated
            if (isDensityCalibrated)
            {
                RenderText("Max", barX - 55, barY - 15, 0.8f);
                RenderText("Min", barX - 55, barY + barHeight - 10, 0.8f);
            }

            // Restore OpenGL state
            GL.MatrixMode(MatrixMode.Projection);
            GL.PopMatrix();
            GL.MatrixMode(MatrixMode.Modelview);
            GL.PopMatrix();

            if (depthTestEnabled) GL.Enable(EnableCap.DepthTest);
            if (lightingEnabled) GL.Enable(EnableCap.Lighting);
            if (!blendEnabled) GL.Disable(EnableCap.Blend);
        }
        private void RenderText(string text, float x, float y, float scale, bool centerX = false)
        {
            if (!fontTextureInitialized)
                InitializeFontTexture();

            // Bind texture
            GL.Enable(EnableCap.Texture2D);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.BindTexture(TextureTarget.Texture2D, fontTextureId);

            // If centering, calculate width
            if (centerX)
            {
                float width = text.Length * 16 * scale;
                x -= width / 2;
            }

            GL.Begin(PrimitiveType.Quads);
            GL.Color4(1.0f, 1.0f, 1.0f, 1.0f); // White text

            float xPos = x;
            foreach (char c in text)
            {
                if (characterRects.TryGetValue(c, out Rectangle rect))
                {
                    float texX = rect.X / 256.0f;
                    float texY = rect.Y / 256.0f;
                    float texWidth = rect.Width / 256.0f;
                    float texHeight = rect.Height / 256.0f;

                    float charWidth = rect.Width * scale;
                    float charHeight = rect.Height * scale;

                    // Draw character quad
                    GL.TexCoord2(texX, texY);
                    GL.Vertex2(xPos, y);

                    GL.TexCoord2(texX + texWidth, texY);
                    GL.Vertex2(xPos + charWidth, y);

                    GL.TexCoord2(texX + texWidth, texY + texHeight);
                    GL.Vertex2(xPos + charWidth, y + charHeight);

                    GL.TexCoord2(texX, texY + texHeight);
                    GL.Vertex2(xPos, y + charHeight);

                    xPos += charWidth;
                }
            }
            GL.End();

            // Restore state
            GL.Disable(EnableCap.Texture2D);
            GL.BindTexture(TextureTarget.Texture2D, 0);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Dispose OpenGL resources
                DisposeDisplayList();
                if (meshDisplayList != 0)
                {
                    GL.DeleteLists(meshDisplayList, 1);
                    meshDisplayList = 0;
                }
                if (fontTextureInitialized && fontTextureId != 0)
                {
                    GL.DeleteTextures(1, ref fontTextureId);
                    fontTextureId = 0;
                }

                if (fontTexture != null)
                {
                    fontTexture.Dispose();
                    fontTexture = null;
                }
                // Delete VBOs
                if (buffersInitialized)
                {
                    GL.DeleteBuffers(1, ref vertexBufferId);
                    GL.DeleteBuffers(1, ref normalBufferId);
                    GL.DeleteBuffers(1, ref colorBufferId);
                    GL.DeleteBuffers(1, ref indexBufferId);
                    buffersInitialized = false;
                }
                if (rotationTimer != null)
                {
                    rotationTimer.Stop();
                    rotationTimer.Dispose();
                    rotationTimer = null;
                }
                // Dispose other managed resources if needed
                if (meshWorker != null)
                {
                    meshWorker.Dispose();
                }

                if (simulationTimer != null)
                {
                    simulationTimer.Stop();
                    simulationTimer.Dispose();
                }

                if (glControl != null)
                {
                    glControl.Dispose();
                }
                if (diagramsForm != null && !diagramsForm.IsDisposed)
                {
                    diagramsForm.Dispose();
                    diagramsForm = null;
                }
            }

            base.Dispose(disposing);
        }
    }
    
    public class TetrahedralElement
    {
        public int[] Vertices { get; private set; }

        public TetrahedralElement(int v1, int v2, int v3, int v4)
        {
            Vertices = new int[] { v1, v2, v3, v4 };
        }
    }

    public class MeshGenerationParameters
    {
        public Material Material { get; set; }
        public int SamplingRate { get; set; }
    }

    
}