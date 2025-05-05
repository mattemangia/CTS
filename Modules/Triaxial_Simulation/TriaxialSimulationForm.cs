using CTS;
using Krypton.Toolkit;
using OpenTK;
using OpenTK.Graphics.OpenGL;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using static MaterialDensityLibrary;

namespace CTS
{
    public partial class TriaxialSimulationForm : KryptonForm, IMaterialDensityProvider
    {
        private MainForm mainForm;
        private Material selectedMaterial;

        private object labelVolume;
        private byte[,,] densityVolume;
        private double pixelSize;
        private int width, height, depth;
        private List<Vector3> vertices = new List<Vector3>();
        private List<Vector3> normals = new List<Vector3>();
        private List<int> indices = new List<int>();
        private List<float> densityValues = new List<float>();
        private List<TetrahedralElement> tetrahedralElements = new List<TetrahedralElement>();

        private GLControl glControl;
        private float rotationX = 0.0f;
        private float rotationY = 0.0f;
        private float zoom = 1.0f;
        private bool wireframeMode = true;
        private float minDensity = float.MaxValue;
        private float maxDensity = float.MinValue;
        private bool glControlInitialized = false;

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
        private List<Point> stressStrainCurve = new List<Point>();
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

        // Progress tracking
        private ProgressBar progressBar;
        private Label progressLabel;
        private BackgroundWorker meshWorker;
        private bool meshGenerationComplete = false;

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

        // Implement IMaterialDensityProvider interface
        public Material SelectedMaterial => selectedMaterial;

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
            InitializeBackgroundWorker();
            LoadMaterialsFromMainForm();
            Logger.Log("[TriaxialSimulationForm] Constructor Ended");
        }

        private void InitializeComponent()
        {
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
                RowCount = 2,
                ColumnCount = 1,
                BackColor = Color.FromArgb(42, 42, 42)
            };

            rightLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 60F)); // Controls area
            rightLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 40F)); // Results graphs area

            // Create scrollable panel for controls
            Panel scrollablePanel = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Color.FromArgb(42, 42, 42)
            };

            // Create results panel
            TableLayoutPanel resultsPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 2,
                ColumnCount = 1,
                BackColor = Color.FromArgb(42, 42, 42)
            };

            resultsPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            resultsPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));

            // Add to right layout
            rightLayout.Controls.Add(scrollablePanel, 0, 0);
            rightLayout.Controls.Add(resultsPanel, 0, 1);
            rightPanel.Controls.Add(rightLayout);

            // Create controls for the scrollable panel
            Panel controlsContent = new Panel
            {
                Width = 330,
                Height = 850, // Make this taller to accommodate petrophysical parameters
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

            // Progress label
            progressLabel = new Label
            {
                Text = "Ready",
                ForeColor = Color.White,
                TextAlign = ContentAlignment.MiddleCenter,
                Location = new Point(140, 720),
                Width = 180,
                Height = 20
            };
            controlsContent.Controls.Add(progressLabel);

            // Generate Mesh Button
            KryptonButton btnGenerateMesh = new KryptonButton
            {
                Text = "Generate Mesh",
                Location = new Point(10, 750),
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
                Location = new Point(10, 790),
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
                Location = new Point(10, 830),
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

            // ================= RESULTS PANELS =================

            // Stress-strain graph panel
            Panel stressStrainPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(5),
                BackColor = Color.FromArgb(32, 32, 32)
            };

            Label lblStressStrain = new Label
            {
                Text = "Stress-Strain Curve",
                Dock = DockStyle.Top,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                Height = 20
            };
            stressStrainPanel.Controls.Add(lblStressStrain);

            stressStrainGraph = new PictureBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(0, 20, 0, 0)
            };
            stressStrainGraph.Paint += StressStrainGraph_Paint;
            stressStrainPanel.Controls.Add(stressStrainGraph);

            // Mohr-Coulomb graph panel
            Panel mohrCoulombPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(5),
                BackColor = Color.FromArgb(32, 32, 32)
            };

            Label lblMohrCoulomb = new Label
            {
                Text = "Mohr-Coulomb Diagram",
                Dock = DockStyle.Top,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                Height = 20
            };
            mohrCoulombPanel.Controls.Add(lblMohrCoulomb);

            mohrCoulombGraph = new PictureBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(0, 20, 0, 0)
            };
            mohrCoulombGraph.Paint += MohrCoulombGraph_Paint;
            mohrCoulombPanel.Controls.Add(mohrCoulombGraph);

            // Add graphs to results panel
            resultsPanel.Controls.Add(stressStrainPanel, 0, 0);
            resultsPanel.Controls.Add(mohrCoulombPanel, 0, 1);

            // Initialize OpenGL Control FIRST
            glControl = new GLControl(new OpenTK.Graphics.GraphicsMode(32, 24, 0, 8));
            glControl.Dock = DockStyle.Fill;
            glControl.VSync = true;

            glControl.Load += GlControl_Load;
            glControl.Paint += GlControl_Paint;
            glControl.Resize += GlControl_Resize;
            glControl.MouseDown += GlControl_MouseDown;
            glControl.MouseMove += GlControl_MouseMove;
            glControl.MouseUp += GlControl_MouseUp;
            glControl.MouseWheel += GlControl_MouseWheel;

            // Add GLControl to render panel
            renderPanel.Controls.Add(glControl);

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

            // Add main layout to form
            this.Controls.Add(mainLayout);
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

        private void MeshWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            if (e.Error != null)
            {
                MessageBox.Show($"Error generating mesh: {e.Error.Message}", "Error",
                               MessageBoxButtons.OK, MessageBoxIcon.Error);
                progressLabel.Text = "Mesh generation failed";
                progressBar.Value = 0;
            }
            else if (e.Cancelled)
            {
                progressLabel.Text = "Mesh generation cancelled";
                progressBar.Value = 0;
            }
            else
            {
                bool success = (bool)e.Result;
                if (success)
                {
                    meshGenerationComplete = true;
                    btnStartSimulation.Enabled = true;
                    progressLabel.Text = $"Mesh generated: {vertices.Count} vertices, {indices.Count / 3} triangles";
                    progressBar.Value = 100;

                    // Set bulk density based on average grayscale value if calibrated
                    if (isDensityCalibrated)
                    {
                        CalculateAverageBulkDensity();
                        numBulkDensity.Value = (decimal)bulkDensity;
                        UpdateMaterialPropertiesFromDensity();
                    }

                    // Initialize deformed vertices
                    deformedVertices = new List<Vector3>(vertices);
                    glControl.Invalidate();
                }
                else
                {
                    progressLabel.Text = "No voxels found for selected material";
                    progressBar.Value = 0;
                }
            }
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

                // Clear old mesh data
                vertices.Clear();
                indices.Clear();
                normals.Clear();
                densityValues.Clear();
                tetrahedralElements.Clear();

                // Start mesh generation in background
                MeshGenerationParameters parameters = new MeshGenerationParameters
                {
                    Material = material,
                    SamplingRate = samplingRate
                };

                meshWorker.RunWorkerAsync(parameters);
            }
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
            simulationTimer.Start();
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

            // Update mesh deformation
            UpdateDeformation(currentStrain, averageStress);

            // Redraw
            stressStrainGraph.Invalidate();
            mohrCoulombGraph.Invalidate();
            glControl.Invalidate();
        }

        private float CalculateStress(float strain)
        {
            float totalStress = 0;
            float yieldStrain = yieldStrength / youngModulus;
            float brittleStrain = brittleStrength / youngModulus;

            // Clear element stresses dictionary
            elementStresses.Clear();

            // Stress calculation for each element considering density variations
            for (int i = 0; i < tetrahedralElements.Count; i++)
            {
                float elementStress = 0;

                // Calculate average density for this element
                float elementDensity = GetElementDensity(tetrahedralElements[i]);

                // Adjust modulus based on element density
                float elementYoungModulus = youngModulus;
                if (isDensityCalibrated && elementDensity > 0)
                {
                    // Scale modulus with density - stiffer materials have higher density
                    float densityRatio = elementDensity / bulkDensity;
                    elementYoungModulus *= (float)Math.Pow(densityRatio, 2.0); // Squared relationship
                }

                // Calculate element yield and brittle strains based on adjusted modulus
                float elementYieldStrain = yieldStrength / elementYoungModulus;
                float elementBrittleStrain = brittleStrength / elementYoungModulus;

                // Start with elastic contribution
                if (isElasticEnabled)
                {
                    elementStress = elementYoungModulus * strain;
                }

                // Apply plastic effect if enabled and strain exceeds yield point
                if (isPlasticEnabled && strain > elementYieldStrain)
                {
                    // Replace elastic component beyond yield point with plastic behavior
                    float elasticComponent = isElasticEnabled ? yieldStrength : 0;

                    // Density affects plastic hardening (higher density = more hardening)
                    float densityFactor = isDensityCalibrated ? Math.Max(0.5f, Math.Min(1.5f, elementDensity / bulkDensity)) : 1.0f;
                    float hardening = elementYoungModulus * 0.1f * densityFactor; // Hardening modulus with density factor

                    float plasticComponent = hardening * (strain - elementYieldStrain);

                    // If both elastic and plastic are enabled, we combine them
                    if (isElasticEnabled)
                    {
                        elementStress = elasticComponent + plasticComponent;
                    }
                    else
                    {
                        elementStress = plasticComponent;
                    }
                }

                // Apply brittle effect if enabled and strain exceeds brittle point
                if (isBrittleEnabled && strain > elementBrittleStrain)
                {
                    // Density affects brittleness (higher density = more brittle behavior)
                    float densityFactor = isDensityCalibrated ? Math.Max(0.5f, Math.Min(1.5f, elementDensity / bulkDensity)) : 1.0f;

                    // Dramatic stress drop after brittle failure - more severe for dense materials
                    float reduction = 0.8f * densityFactor; // 80% stress reduction scaled by density
                    reduction = Math.Min(reduction, 0.95f); // Cap at 95% reduction

                    // If no other behaviors are enabled, start from brittle strength
                    if (!isElasticEnabled && !isPlasticEnabled)
                    {
                        elementStress = brittleStrength * (1.0f - reduction);
                    }
                    else
                    {
                        // Reduce the current stress from other behaviors
                        elementStress *= (1.0f - reduction);
                    }
                }

                // Store element stress
                elementStresses[i] = elementStress;

                // Add to total stress (weighted by element volume)
                totalStress += elementStress;
            }

            // Calculate average stress across all elements
            float averageStress = tetrahedralElements.Count > 0 ? totalStress / tetrahedralElements.Count : 0;

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
            // Calculate deformation based on current strain
            deformedVertices = new List<Vector3>(vertices.Count);

            for (int i = 0; i < vertices.Count; i++)
            {
                Vector3 vertex = vertices[i];
                Vector3 deformed = vertex;

                // Get vertex density value for deformation scaling
                float vertexDensity = (i < densityValues.Count) ? densityValues[i] : bulkDensity;

                // Calculate porosity-dependent Poisson ratio
                // Higher porosity materials have higher Poisson's ratio
                float effectivePoissonRatio = poissonRatio;
                if (isDensityCalibrated)
                {
                    // Estimate porosity from density
                    float grainDensity = 2650.0f; // kg/m³ (typical for quartz/feldspar)
                    float vertexPorosity = 1.0f - (vertexDensity / grainDensity);
                    vertexPorosity = Math.Max(0.01f, Math.Min(0.5f, vertexPorosity));

                    // Adjust Poisson's ratio based on porosity (empirical relationship)
                    effectivePoissonRatio = poissonRatio * (1.0f + 0.5f * vertexPorosity);
                    effectivePoissonRatio = Math.Min(effectivePoissonRatio, 0.49f); // Cap at 0.49
                }

                // Apply deformation along selected axis
                switch (selectedDirection)
                {
                    case SimulationDirection.X:
                        // Deform along X-axis
                        deformed.X = vertex.X * (1 + strain);

                        // Apply lateral contraction (Poisson effect)
                        deformed.Y = vertex.Y * (1 - strain * effectivePoissonRatio);
                        deformed.Z = vertex.Z * (1 - strain * effectivePoissonRatio);
                        break;

                    case SimulationDirection.Y:
                        // Deform along Y-axis
                        deformed.Y = vertex.Y * (1 + strain);

                        // Apply lateral contraction (Poisson effect)
                        deformed.X = vertex.X * (1 - strain * effectivePoissonRatio);
                        deformed.Z = vertex.Z * (1 - strain * effectivePoissonRatio);
                        break;

                    case SimulationDirection.Z:
                        // Deform along Z-axis
                        deformed.Z = vertex.Z * (1 + strain);

                        // Apply lateral contraction (Poisson effect)
                        deformed.X = vertex.X * (1 - strain * effectivePoissonRatio);
                        deformed.Y = vertex.Y * (1 - strain * effectivePoissonRatio);
                        break;
                }

                // Apply density-dependent deformation scaling if we have density data
                if (isDensityCalibrated && i < densityValues.Count)
                {
                    // Higher porosity (lower density) materials deform more
                    // Estimate porosity from density
                    float grainDensity = 2650.0f; // kg/m³ (typical for quartz/feldspar)
                    float vertexPorosity = 1.0f - (vertexDensity / grainDensity);
                    vertexPorosity = Math.Max(0.01f, Math.Min(0.5f, vertexPorosity));

                    // Density influence factor (higher porosity = more deformation)
                    float porosityInfluence = 0.5f * vertexPorosity;

                    // Interpolate between original position and deformed position based on density
                    Vector3 additionalDeformation = deformed - vertex;
                    deformed = vertex + additionalDeformation * (1.0f + porosityInfluence);
                }

                // Add to deformed vertices list
                deformedVertices.Add(deformed);
            }

            // Apply brittle fracturing effects
            if (isBrittleEnabled && strain > brittleStrength / youngModulus)
            {
                // Get a deterministic but varied seed based on current strain
                int seed = (int)(currentStrain * 10000);
                Random rand = new Random(seed); // Use fixed seed for reproducibility

                for (int i = 0; i < deformedVertices.Count; i++)
                {
                    // Get vertex stress state - prone to fracture if high stress
                    float vertexStressFactor = 0.0f;

                    // Calculate stress factor for this vertex
                    foreach (var tetra in tetrahedralElements)
                    {
                        if (Array.IndexOf(tetra.Vertices, i) >= 0)
                        {
                            // This vertex is part of this tetrahedron
                            int tetraIndex = tetrahedralElements.IndexOf(tetra);
                            if (elementStresses.TryGetValue(tetraIndex, out float elementStress))
                            {
                                // Normalize stress by brittle strength
                                vertexStressFactor = Math.Max(vertexStressFactor, elementStress / brittleStrength);
                            }
                        }
                    }

                    // Higher stress regions are more likely to fracture
                    if (rand.NextDouble() < 0.3 * vertexStressFactor)
                    {
                        // Only displace vertices in high stress regions
                        Vector3 displacement = new Vector3(
                            (float)(rand.NextDouble() - 0.5) * 0.05f * vertexStressFactor,
                            (float)(rand.NextDouble() - 0.5) * 0.05f * vertexStressFactor,
                            (float)(rand.NextDouble() - 0.5) * 0.05f * vertexStressFactor
                        );
                        deformedVertices[i] += displacement;
                    }
                }
            }
        }

        private void ExtractVolumeData(Material material)
        {
            // Get volume data from MainForm
            labelVolume = mainForm.volumeLabels;

            // Get dimensions
            width = LabelVolumeHelper.GetWidth(labelVolume);
            height = LabelVolumeHelper.GetHeight(labelVolume);
            depth = LabelVolumeHelper.GetDepth(labelVolume);

            // Create a byte array to store density values
            densityVolume = new byte[width, height, depth];

            // Extract density data from grayscale volume for selected material
            byte materialID = material.ID;

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
                        }
                        else
                        {
                            // Not part of the material
                            densityVolume[x, y, z] = 0;
                        }
                    }
                }
            });
        }

        private void GenerateMesh(int samplingRate, BackgroundWorker worker)
        {
            // Clear previous mesh data
            vertices.Clear();
            indices.Clear();
            normals.Clear();
            densityValues.Clear();
            tetrahedralElements.Clear();

            // Reset min/max density
            minDensity = float.MaxValue;
            maxDensity = float.MinValue;

            // Create a list to hold voxel coordinates for the selected material
            List<Tuple<int, int, int>> materialVoxels = new List<Tuple<int, int, int>>();

            // Calculate the center of the volume
            float centerX = width / 2.0f;
            float centerY = height / 2.0f;
            float centerZ = depth / 2.0f;

            // Calculate the radius for the cylindrical core (40% of the minimum dimension)
            float radius = Math.Min(Math.Min(width, height), depth) * 0.4f;

            // First pass: collect voxels of the selected material that are within the cylindrical core
            for (int z = 0; z < depth; z += samplingRate)
            {
                for (int y = 0; y < height; y += samplingRate)
                {
                    for (int x = 0; x < width; x += samplingRate)
                    {
                        if (densityVolume[x, y, z] > 0) // Part of the material
                        {
                            // Check if within the cylindrical core along the selected direction
                            bool isInCylinder = false;

                            switch (selectedDirection)
                            {
                                case SimulationDirection.X:
                                    // Cylinder along X-axis
                                    float distYZ = (float)Math.Sqrt(
                                        Math.Pow(y - centerY, 2) +
                                        Math.Pow(z - centerZ, 2));
                                    isInCylinder = distYZ <= radius;
                                    break;

                                case SimulationDirection.Y:
                                    // Cylinder along Y-axis
                                    float distXZ = (float)Math.Sqrt(
                                        Math.Pow(x - centerX, 2) +
                                        Math.Pow(z - centerZ, 2));
                                    isInCylinder = distXZ <= radius;
                                    break;

                                case SimulationDirection.Z:
                                    // Cylinder along Z-axis (default)
                                    float distXY = (float)Math.Sqrt(
                                        Math.Pow(x - centerX, 2) +
                                        Math.Pow(y - centerY, 2));
                                    isInCylinder = distXY <= radius;
                                    break;
                            }

                            if (isInCylinder)
                            {
                                materialVoxels.Add(new Tuple<int, int, int>(x, y, z));

                                // Update min/max density
                                float density = densityVolume[x, y, z] / 255.0f;
                                minDensity = Math.Min(minDensity, density);
                                maxDensity = Math.Max(maxDensity, density);
                            }
                        }
                    }
                }

                // Report progress periodically
                if (z % Math.Max(1, depth / 10) == 0)
                {
                    int progressPercentage = 30 + (z * 30 / depth);
                    worker.ReportProgress(progressPercentage, $"Found {materialVoxels.Count} voxels in cylindrical core...");

                    // Check for cancellation
                    if (worker.CancellationPending)
                        return;
                }
            }

            // If no voxels were found, return
            if (materialVoxels.Count == 0)
                return;

            worker.ReportProgress(60, $"Creating mesh from {materialVoxels.Count} voxels...");

            // Create simplified surface mesh using marching cubes-inspired approach
            // First, create a dictionary mapping voxel coordinates to vertex indices
            Dictionary<Tuple<int, int, int>, int> voxelToVertexIndex = new Dictionary<Tuple<int, int, int>, int>();

            // Create vertices at each sample point
            foreach (var voxel in materialVoxels)
            {
                // Create a vertex at the center of the voxel
                float px = (voxel.Item1) * (float)pixelSize;
                float py = (voxel.Item2) * (float)pixelSize;
                float pz = (voxel.Item3) * (float)pixelSize;

                // Normalize coordinates to center the model
                px -= (width / 2.0f) * (float)pixelSize;
                py -= (height / 2.0f) * (float)pixelSize;
                pz -= (depth / 2.0f) * (float)pixelSize;

                // Add vertex
                vertices.Add(new Vector3(px, py, pz));

                // Store the density value - normalized to [0-1] range
                float density = densityVolume[voxel.Item1, voxel.Item2, voxel.Item3] / 255.0f;

                // Apply calibration if available
                if (isDensityCalibrated)
                {
                    // Convert from normalized [0-1] to grayscale [0-255]
                    float grayValue = density * 255f;

                    // Apply calibration to get density in kg/m³
                    density = (float)(grayValue * densityCalibrationSlope + densityCalibrationIntercept);
                }

                densityValues.Add(density);

                // Store mapping from voxel coordinates to vertex index
                voxelToVertexIndex[voxel] = vertices.Count - 1;
            }

            worker.ReportProgress(70, $"Created {vertices.Count} vertices, generating surface...");

            // Initialize normals
            for (int i = 0; i < vertices.Count; i++)
            {
                normals.Add(new Vector3(0, 0, 0));
            }

            // Counter for progress tracking
            int processedVoxels = 0;
            int totalVoxels = materialVoxels.Count;

            // Create triangles connecting adjacent voxels
            foreach (var voxel in materialVoxels)
            {
                int x = voxel.Item1;
                int y = voxel.Item2;
                int z = voxel.Item3;

                // Check neighbors in all 6 directions
                Tuple<int, int, int>[] neighbors = new Tuple<int, int, int>[]
                {
                    new Tuple<int, int, int>(x + samplingRate, y, z), // +X
                    new Tuple<int, int, int>(x, y + samplingRate, z), // +Y
                    new Tuple<int, int, int>(x, y, z + samplingRate), // +Z
                    new Tuple<int, int, int>(x - samplingRate, y, z), // -X
                    new Tuple<int, int, int>(x, y - samplingRate, z), // -Y
                    new Tuple<int, int, int>(x, y, z - samplingRate)  // -Z
                };

                Vector3[] directions = new Vector3[]
                {
                    new Vector3(1, 0, 0),  // +X
                    new Vector3(0, 1, 0),  // +Y
                    new Vector3(0, 0, 1),  // +Z
                    new Vector3(-1, 0, 0), // -X
                    new Vector3(0, -1, 0), // -Y
                    new Vector3(0, 0, -1)  // -Z
                };

                // Get the vertex index for this voxel
                int vIndex = voxelToVertexIndex[voxel];

                // Check each neighbor
                for (int i = 0; i < neighbors.Length; i++)
                {
                    var neighbor = neighbors[i];

                    // If the neighbor is not in the material but within bounds, 
                    // then it's on the surface in that direction
                    if (!voxelToVertexIndex.ContainsKey(neighbor) &&
                        neighbor.Item1 >= 0 && neighbor.Item1 < width &&
                        neighbor.Item2 >= 0 && neighbor.Item2 < height &&
                        neighbor.Item3 >= 0 && neighbor.Item3 < depth)
                    {
                        // Record this surface normal direction
                        normals[vIndex] += directions[i];
                    }
                }

                // Periodically report progress
                processedVoxels++;
                if (processedVoxels % Math.Max(1, totalVoxels / 20) == 0)
                {
                    int progressPercentage = 70 + (processedVoxels * 20 / totalVoxels);
                    worker.ReportProgress(progressPercentage, $"Generating surface mesh: {processedVoxels}/{totalVoxels}");

                    // Check for cancellation
                    if (worker.CancellationPending)
                        return;
                }
            }

            worker.ReportProgress(90, "Finalizing mesh...");

            // Normalize the normals
            for (int i = 0; i < normals.Count; i++)
            {
                if (normals[i] != Vector3.Zero)
                {
                    normals[i].Normalize();
                }
                else
                {
                    // Default normal if none was calculated
                    normals[i] = new Vector3(0, 1, 0);
                }
            }

            // Create quad faces for each voxel facing an empty neighbor
            HashSet<string> processedEdges = new HashSet<string>();

            foreach (var voxel in materialVoxels)
            {
                int x = voxel.Item1;
                int y = voxel.Item2;
                int z = voxel.Item3;

                // Only process if we have this voxel in our mapping
                if (!voxelToVertexIndex.TryGetValue(voxel, out int centerIndex))
                    continue;

                // Check for neighbors in 6 directions to create face quads
                ProcessQuadDirection(voxel, new Tuple<int, int, int>(x + samplingRate, y, z),
                                    new Tuple<int, int, int>(x, y + samplingRate, z),
                                    voxelToVertexIndex, processedEdges);

                ProcessQuadDirection(voxel, new Tuple<int, int, int>(x, y + samplingRate, z),
                                    new Tuple<int, int, int>(x, y, z + samplingRate),
                                    voxelToVertexIndex, processedEdges);

                ProcessQuadDirection(voxel, new Tuple<int, int, int>(x, y, z + samplingRate),
                                    new Tuple<int, int, int>(x + samplingRate, y, z),
                                    voxelToVertexIndex, processedEdges);
            }

            worker.ReportProgress(95, $"Mesh complete: {vertices.Count} vertices, {indices.Count / 3} triangles");

            // Generate tetrahedral elements for simulation
            CreateTetrahedralElements(materialVoxels, voxelToVertexIndex, samplingRate, worker);

            worker.ReportProgress(100, "Cylindrical core mesh generation complete!");
        }

        private void ProcessQuadDirection(Tuple<int, int, int> voxel,
                                         Tuple<int, int, int> neighbor1,
                                         Tuple<int, int, int> neighbor2,
                                         Dictionary<Tuple<int, int, int>, int> voxelToVertexIndex,
                                         HashSet<string> processedEdges)
        {
            // Get vertex indices
            if (!voxelToVertexIndex.TryGetValue(voxel, out int v0))
                return;

            if (!voxelToVertexIndex.TryGetValue(neighbor1, out int v1))
                return;

            if (!voxelToVertexIndex.TryGetValue(neighbor2, out int v2))
                return;

            // Calculate the fourth vertex of the quad (diagonal from v0)
            int nx = neighbor1.Item1 + (neighbor2.Item1 - voxel.Item1);
            int ny = neighbor1.Item2 + (neighbor2.Item2 - voxel.Item2);
            int nz = neighbor1.Item3 + (neighbor2.Item3 - voxel.Item3);

            Tuple<int, int, int> diagNeighbor = new Tuple<int, int, int>(nx, ny, nz);

            if (!voxelToVertexIndex.TryGetValue(diagNeighbor, out int v3))
                return;

            // Create a unique key for this quad to avoid duplicates
            int[] sortedVertices = new[] { v0, v1, v2, v3 };
            Array.Sort(sortedVertices);
            string quadKey = string.Join(",", sortedVertices);

            if (processedEdges.Contains(quadKey))
                return;

            // Add the quad as two triangles
            indices.Add(v0);
            indices.Add(v1);
            indices.Add(v2);

            indices.Add(v2);
            indices.Add(v3);
            indices.Add(v0);

            // Mark this quad as processed
            processedEdges.Add(quadKey);
        }

        private void CreateTetrahedralElements(List<Tuple<int, int, int>> materialVoxels,
                                              Dictionary<Tuple<int, int, int>, int> voxelToVertexIndex,
                                              int samplingRate, BackgroundWorker worker)
        {
            // We'll create tetrahedral elements using a more systematic approach for better results
            int elementsCreated = 0;
            int maxElements = 5000; // Limit to a reasonable number

            worker.ReportProgress(95, "Creating tetrahedral elements for simulation...");

            // Create a regular grid of tetrahedra
            for (int i = 0; i < materialVoxels.Count; i++)
            {
                var v0 = materialVoxels[i];

                // Define the various tetrahedra patterns
                // Each pattern consists of vertices relative to v0
                // These patterns ensure complete coverage of the space
                List<Tuple<int, int, int>[]> patterns = new List<Tuple<int, int, int>[]>();

                // Pattern 1: Basic tetrahedron
                patterns.Add(new Tuple<int, int, int>[] {
                    v0,
                    new Tuple<int, int, int>(v0.Item1 + samplingRate, v0.Item2, v0.Item3),
                    new Tuple<int, int, int>(v0.Item1, v0.Item2 + samplingRate, v0.Item3),
                    new Tuple<int, int, int>(v0.Item1, v0.Item2, v0.Item3 + samplingRate)
                });

                // Pattern 2: Complementary tetrahedron
                patterns.Add(new Tuple<int, int, int>[] {
                    new Tuple<int, int, int>(v0.Item1 + samplingRate, v0.Item2 + samplingRate, v0.Item3 + samplingRate),
                    new Tuple<int, int, int>(v0.Item1 + samplingRate, v0.Item2, v0.Item3),
                    new Tuple<int, int, int>(v0.Item1, v0.Item2 + samplingRate, v0.Item3),
                    new Tuple<int, int, int>(v0.Item1, v0.Item2, v0.Item3 + samplingRate)
                });

                // Pattern 3: Diagonal tetrahedron 1
                patterns.Add(new Tuple<int, int, int>[] {
                    v0,
                    new Tuple<int, int, int>(v0.Item1 + samplingRate, v0.Item2 + samplingRate, v0.Item3),
                    new Tuple<int, int, int>(v0.Item1 + samplingRate, v0.Item2, v0.Item3 + samplingRate),
                    new Tuple<int, int, int>(v0.Item1, v0.Item2 + samplingRate, v0.Item3 + samplingRate)
                });

                // Pattern 4: Diagonal tetrahedron 2
                patterns.Add(new Tuple<int, int, int>[] {
                    v0,
                    new Tuple<int, int, int>(v0.Item1 + samplingRate, v0.Item2, v0.Item3),
                    new Tuple<int, int, int>(v0.Item1, v0.Item2 + samplingRate, v0.Item3),
                    new Tuple<int, int, int>(v0.Item1 + samplingRate, v0.Item2 + samplingRate, v0.Item3 + samplingRate)
                });

                // Try to create tetrahedra from each pattern
                foreach (var pattern in patterns)
                {
                    // Check if all vertices in this pattern exist
                    bool allVerticesExist = true;
                    int[] vertexIndices = new int[4];

                    for (int j = 0; j < 4; j++)
                    {
                        if (!voxelToVertexIndex.TryGetValue(pattern[j], out vertexIndices[j]))
                        {
                            allVerticesExist = false;
                            break;
                        }
                    }

                    if (allVerticesExist)
                    {
                        // Create a tetrahedral element
                        tetrahedralElements.Add(new TetrahedralElement(
                            vertexIndices[0], vertexIndices[1], vertexIndices[2], vertexIndices[3]));

                        elementsCreated++;
                        if (elementsCreated >= maxElements)
                        {
                            worker.ReportProgress(98, $"Created {elementsCreated} tetrahedral elements (max limit)");
                            return;
                        }
                    }
                }

                // Report progress periodically
                if (elementsCreated % 1000 == 0)
                {
                    worker.ReportProgress(96, $"Created {elementsCreated} tetrahedral elements...");

                    // Check for cancellation
                    if (worker.CancellationPending)
                        return;
                }
            }

            worker.ReportProgress(98, $"Created {elementsCreated} tetrahedral elements");
        }
        private void DrawColorLegend()
        {
            // Draw a color legend in viewport space (2D overlay)
            GL.MatrixMode(MatrixMode.Projection);
            GL.PushMatrix();
            GL.LoadIdentity();
            GL.Ortho(0, glControl.Width, 0, glControl.Height, -1, 1);

            GL.MatrixMode(MatrixMode.Modelview);
            GL.PushMatrix();
            GL.LoadIdentity();

            GL.Disable(EnableCap.Lighting);
            GL.Disable(EnableCap.DepthTest);

            // Position and size of legend
            int legendWidth = 20;
            int legendHeight = 150;
            int startX = glControl.Width - legendWidth - 20;
            int startY = 50;

            // Draw the color gradient
            GL.Begin(PrimitiveType.Quads);

            // Blue to cyan (0.0-0.25)
            GL.Color4(0.0f, 0.0f, 1.0f, 1.0f);
            GL.Vertex2(startX, startY);
            GL.Vertex2(startX + legendWidth, startY);

            GL.Color4(0.0f, 1.0f, 1.0f, 1.0f);
            GL.Vertex2(startX + legendWidth, startY + legendHeight * 0.25f);
            GL.Vertex2(startX, startY + legendHeight * 0.25f);

            // Cyan to green (0.25-0.5)
            GL.Color4(0.0f, 1.0f, 1.0f, 1.0f);
            GL.Vertex2(startX, startY + legendHeight * 0.25f);
            GL.Vertex2(startX + legendWidth, startY + legendHeight * 0.25f);

            GL.Color4(0.0f, 1.0f, 0.0f, 1.0f);
            GL.Vertex2(startX + legendWidth, startY + legendHeight * 0.5f);
            GL.Vertex2(startX, startY + legendHeight * 0.5f);

            // Green to yellow (0.5-0.75)
            GL.Color4(0.0f, 1.0f, 0.0f, 1.0f);
            GL.Vertex2(startX, startY + legendHeight * 0.5f);
            GL.Vertex2(startX + legendWidth, startY + legendHeight * 0.5f);

            GL.Color4(1.0f, 1.0f, 0.0f, 1.0f);
            GL.Vertex2(startX + legendWidth, startY + legendHeight * 0.75f);
            GL.Vertex2(startX, startY + legendHeight * 0.75f);

            // Yellow to red (0.75-1.0)
            GL.Color4(1.0f, 1.0f, 0.0f, 1.0f);
            GL.Vertex2(startX, startY + legendHeight * 0.75f);
            GL.Vertex2(startX + legendWidth, startY + legendHeight * 0.75f);

            GL.Color4(1.0f, 0.0f, 0.0f, 1.0f);
            GL.Vertex2(startX + legendWidth, startY + legendHeight);
            GL.Vertex2(startX, startY + legendHeight);

            GL.End();

            // Draw border around the legend
            GL.Color4(1.0f, 1.0f, 1.0f, 1.0f);
            GL.Begin(PrimitiveType.LineLoop);
            GL.Vertex2(startX, startY);
            GL.Vertex2(startX + legendWidth, startY);
            GL.Vertex2(startX + legendWidth, startY + legendHeight);
            GL.Vertex2(startX, startY + legendHeight);
            GL.End();

            // Draw tick marks and labels
            GL.Begin(PrimitiveType.Lines);
            for (int i = 0; i <= 4; i++)
            {
                float y = startY + (i * legendHeight / 4.0f);
                GL.Vertex2(startX, y);
                GL.Vertex2(startX - 5, y);
            }
            GL.End();

            // Restore matrices
            GL.MatrixMode(MatrixMode.Projection);
            GL.PopMatrix();
            GL.MatrixMode(MatrixMode.Modelview);
            GL.PopMatrix();

            // Restore states
            GL.Enable(EnableCap.DepthTest);
            GL.Enable(EnableCap.Lighting);
        }
        private void DrawPressureArrow(Vector3 position, Vector3 direction, float scale)
        {
            // Scale the arrow size
            float arrowLength = 0.4f * scale;
            float arrowWidth = 0.1f * scale;

            // Calculate arrow head position
            Vector3 tip = position + direction * arrowLength;

            // Draw the main arrow line
            GL.Vertex3(position);
            GL.Vertex3(tip);

            // Calculate perpendicular vectors for arrow head
            Vector3 perp1, perp2;
            if (Math.Abs(direction.Y) < 0.99f)
            {
                perp1 = Vector3.Cross(direction, new Vector3(0, 1, 0));
                perp1.Normalize();
            }
            else
            {
                perp1 = Vector3.Cross(direction, new Vector3(1, 0, 0));
                perp1.Normalize();
            }

            perp2 = Vector3.Cross(direction, perp1);
            perp2.Normalize();

            // Draw arrow head
            Vector3 base1 = tip - direction * arrowWidth;

            // Draw 4 triangular arrow head faces
            GL.Vertex3(tip);
            GL.Vertex3(base1 + perp1 * arrowWidth);

            GL.Vertex3(tip);
            GL.Vertex3(base1 - perp1 * arrowWidth);

            GL.Vertex3(tip);
            GL.Vertex3(base1 + perp2 * arrowWidth);

            GL.Vertex3(tip);
            GL.Vertex3(base1 - perp2 * arrowWidth);
        }
        private void DrawPressureIndicators()
        {
            // Draw arrows indicating pressure direction with appropriate magnitudes
            GL.LineWidth(3.0f);
            GL.Begin(PrimitiveType.Lines);

            // Calculate arrow size based on pressure
            float confiningScale = minPressure / 1000.0f; // Scale to reasonable size
            float axialScale = maxPressure / 1000.0f;     // Scale to reasonable size

            // Calculate arrow positions based on model size
            float modelSize = 1.5f; // Adjust based on your model dimensions

            if (selectedDirection == SimulationDirection.X)
            {
                // Axial pressure in X direction
                GL.Color3(1.0f, 0.0f, 0.0f); // Red for axial
                DrawPressureArrow(new Vector3(-modelSize, 0, 0), new Vector3(1, 0, 0), axialScale);
                DrawPressureArrow(new Vector3(modelSize, 0, 0), new Vector3(-1, 0, 0), axialScale);

                // Confining pressure in Y and Z
                GL.Color3(0.0f, 0.7f, 1.0f); // Cyan for confining
                DrawPressureArrow(new Vector3(0, -modelSize, 0), new Vector3(0, 1, 0), confiningScale);
                DrawPressureArrow(new Vector3(0, modelSize, 0), new Vector3(0, -1, 0), confiningScale);
                DrawPressureArrow(new Vector3(0, 0, -modelSize), new Vector3(0, 0, 1), confiningScale);
                DrawPressureArrow(new Vector3(0, 0, modelSize), new Vector3(0, 0, -1), confiningScale);
            }
            else if (selectedDirection == SimulationDirection.Y)
            {
                // Axial pressure in Y direction
                GL.Color3(0.0f, 1.0f, 0.0f); // Green for axial
                DrawPressureArrow(new Vector3(0, -modelSize, 0), new Vector3(0, 1, 0), axialScale);
                DrawPressureArrow(new Vector3(0, modelSize, 0), new Vector3(0, -1, 0), axialScale);

                // Confining pressure in X and Z
                GL.Color3(0.0f, 0.7f, 1.0f); // Cyan for confining
                DrawPressureArrow(new Vector3(-modelSize, 0, 0), new Vector3(1, 0, 0), confiningScale);
                DrawPressureArrow(new Vector3(modelSize, 0, 0), new Vector3(-1, 0, 0), confiningScale);
                DrawPressureArrow(new Vector3(0, 0, -modelSize), new Vector3(0, 0, 1), confiningScale);
                DrawPressureArrow(new Vector3(0, 0, modelSize), new Vector3(0, 0, -1), confiningScale);
            }
            else // Z direction
            {
                // Axial pressure in Z direction
                GL.Color3(0.0f, 0.0f, 1.0f); // Blue for axial
                DrawPressureArrow(new Vector3(0, 0, -modelSize), new Vector3(0, 0, 1), axialScale);
                DrawPressureArrow(new Vector3(0, 0, modelSize), new Vector3(0, 0, -1), axialScale);

                // Confining pressure in X and Y
                GL.Color3(0.0f, 0.7f, 1.0f); // Cyan for confining
                DrawPressureArrow(new Vector3(-modelSize, 0, 0), new Vector3(1, 0, 0), confiningScale);
                DrawPressureArrow(new Vector3(modelSize, 0, 0), new Vector3(-1, 0, 0), confiningScale);
                DrawPressureArrow(new Vector3(0, -modelSize, 0), new Vector3(0, 1, 0), confiningScale);
                DrawPressureArrow(new Vector3(0, modelSize, 0), new Vector3(0, -1, 0), confiningScale);
            }

            GL.End();
            GL.LineWidth(1.0f);
        }
        private void GlControl_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                lastMousePos = e.Location;
                isDragging = true;
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
                glControl.Invalidate();
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
            if (!glControl.IsHandleCreated || !glControl.Context.IsCurrent)
            {
                if (glControl.IsHandleCreated)
                {
                    glControl.MakeCurrent();
                }
                else
                {
                    return; // Can't render if the handle isn't created yet
                }
            }

            // If the control hasn't been initialized yet, initialize it
            if (!glControlInitialized)
            {
                GlControl_Load(sender, e);
            }

            // Clear the buffers
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            // Set up model view matrix
            GL.MatrixMode(MatrixMode.Modelview);
            GL.LoadIdentity();

            // Position the camera - moved further back to better see the model
            GL.Translate(0.0f, 0.0f, -5.0f * (1.0f / zoom));
            GL.Rotate(rotationX, 1.0f, 0.0f, 0.0f);
            GL.Rotate(rotationY, 0.0f, 1.0f, 0.0f);

            // Enable/disable wireframe mode
            if (wireframeMode)
            {
                GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Line);
            }
            else
            {
                GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
            }

            // Draw the mesh if we have vertices and indices
            if (deformedVertices.Count > 0 && indices.Count > 0)
            {
                // Check if number of indices is valid (must be multiple of 3 for triangles)
                int numTriangles = indices.Count / 3;
                if (numTriangles > 0)
                {
                    // Enable lighting for better visualization
                    GL.Enable(EnableCap.Lighting);
                    GL.Enable(EnableCap.Light0);
                    GL.Enable(EnableCap.Light1);
                    GL.Enable(EnableCap.ColorMaterial);

                    // Draw mesh using vertex arrays for better performance
                    GL.EnableClientState(ArrayCap.VertexArray);
                    GL.EnableClientState(ArrayCap.NormalArray);
                    GL.EnableClientState(ArrayCap.ColorArray);

                    try
                    {
                        // Prepare vertex array
                        float[] vertexArray = new float[deformedVertices.Count * 3];
                        for (int i = 0; i < deformedVertices.Count; i++)
                        {
                            vertexArray[i * 3] = deformedVertices[i].X;
                            vertexArray[i * 3 + 1] = deformedVertices[i].Y;
                            vertexArray[i * 3 + 2] = deformedVertices[i].Z;
                        }

                        // Prepare normal array
                        float[] normalArray = new float[normals.Count * 3];
                        for (int i = 0; i < normals.Count; i++)
                        {
                            normalArray[i * 3] = normals[i].X;
                            normalArray[i * 3 + 1] = normals[i].Y;
                            normalArray[i * 3 + 2] = normals[i].Z;
                        }

                        // Prepare color array based on density or stress
                        float[] colorArray = new float[densityValues.Count * 4]; // RGBA
                        for (int i = 0; i < densityValues.Count; i++)
                        {
                            // Determine if we're showing density or element stress
                            float colorValue;

                            if (chkShowDensity.Checked)
                            {
                                // Use density for coloring
                                if (isDensityCalibrated)
                                {
                                    // Normalize based on real density values in kg/m³
                                    colorValue = (densityValues[i] - minDensity) / (maxDensity - minDensity);
                                }
                                else
                                {
                                    // Use normalized grayscale value
                                    colorValue = (densityValues[i] - minDensity) / (maxDensity - minDensity);
                                }
                            }
                            else
                            {
                                // Use stress for coloring (if simulation is running)
                                colorValue = 0.5f; // Default mid-value

                                if (simulationRunning && elementStresses.Count > 0)
                                {
                                    // Find the element(s) this vertex belongs to
                                    float vertexStress = 0.0f;
                                    int elementCount = 0;

                                    foreach (var tetra in tetrahedralElements)
                                    {
                                        if (Array.IndexOf(tetra.Vertices, i) >= 0)
                                        {
                                            // This vertex is part of this tetrahedron
                                            int tetraIndex = tetrahedralElements.IndexOf(tetra);
                                            if (elementStresses.TryGetValue(tetraIndex, out float elementStress))
                                            {
                                                vertexStress += elementStress;
                                                elementCount++;
                                            }
                                        }
                                    }

                                    if (elementCount > 0)
                                    {
                                        // Average stress for this vertex
                                        vertexStress /= elementCount;

                                        // Normalize by yield strength for coloring
                                        colorValue = Math.Min(1.0f, vertexStress / yieldStrength);
                                    }
                                }
                            }

                            // Apply color gradient based on the value
                            // Using a multistage gradient: blue -> cyan -> green -> yellow -> red
                            // This provides better visual discrimination of values
                            if (colorValue < 0.25f)
                            {
                                // Blue to cyan (0.0-0.25)
                                float t = colorValue * 4.0f;
                                colorArray[i * 4] = 0.0f;         // R
                                colorArray[i * 4 + 1] = t;        // G
                                colorArray[i * 4 + 2] = 1.0f;     // B
                                colorArray[i * 4 + 3] = 1.0f;     // A
                            }
                            else if (colorValue < 0.5f)
                            {
                                // Cyan to green (0.25-0.5)
                                float t = (colorValue - 0.25f) * 4.0f;
                                colorArray[i * 4] = 0.0f;         // R
                                colorArray[i * 4 + 1] = 1.0f;     // G
                                colorArray[i * 4 + 2] = 1.0f - t; // B
                                colorArray[i * 4 + 3] = 1.0f;     // A
                            }
                            else if (colorValue < 0.75f)
                            {
                                // Green to yellow (0.5-0.75)
                                float t = (colorValue - 0.5f) * 4.0f;
                                colorArray[i * 4] = t;            // R
                                colorArray[i * 4 + 1] = 1.0f;     // G
                                colorArray[i * 4 + 2] = 0.0f;     // B
                                colorArray[i * 4 + 3] = 1.0f;     // A
                            }
                            else
                            {
                                // Yellow to red (0.75-1.0)
                                float t = (colorValue - 0.75f) * 4.0f;
                                colorArray[i * 4] = 1.0f;         // R
                                colorArray[i * 4 + 1] = 1.0f - t; // G
                                colorArray[i * 4 + 2] = 0.0f;     // B
                                colorArray[i * 4 + 3] = 1.0f;     // A
                            }

                            // Add slight transparency to make internal structure visible in solid mode
                            if (!wireframeMode)
                            {
                                colorArray[i * 4 + 3] = 0.9f;  // Slight transparency
                            }
                        }

                        // Set up the arrays
                        GL.VertexPointer(3, VertexPointerType.Float, 0, vertexArray);
                        GL.NormalPointer(NormalPointerType.Float, 0, normalArray);
                        GL.ColorPointer(4, ColorPointerType.Float, 0, colorArray);

                        // Draw the triangles
                        GL.DrawElements(PrimitiveType.Triangles, indices.Count, DrawElementsType.UnsignedInt, indices.ToArray());
                    }
                    finally
                    {
                        // Clean up state
                        GL.DisableClientState(ArrayCap.VertexArray);
                        GL.DisableClientState(ArrayCap.NormalArray);
                        GL.DisableClientState(ArrayCap.ColorArray);
                    }
                }
            }

            // Draw coordinate axes for reference
            GL.Disable(EnableCap.Lighting);
            GL.LineWidth(2.0f);
            GL.Begin(PrimitiveType.Lines);

            // X-axis (red) with arrow
            GL.Color3(1.0f, 0.0f, 0.0f);
            GL.Vertex3(0.0f, 0.0f, 0.0f);
            GL.Vertex3(1.0f, 0.0f, 0.0f);

            // X-axis arrow
            GL.Vertex3(1.0f, 0.0f, 0.0f);
            GL.Vertex3(0.9f, 0.05f, 0.0f);
            GL.Vertex3(1.0f, 0.0f, 0.0f);
            GL.Vertex3(0.9f, -0.05f, 0.0f);

            // Y-axis (green) with arrow
            GL.Color3(0.0f, 1.0f, 0.0f);
            GL.Vertex3(0.0f, 0.0f, 0.0f);
            GL.Vertex3(0.0f, 1.0f, 0.0f);

            // Y-axis arrow
            GL.Vertex3(0.0f, 1.0f, 0.0f);
            GL.Vertex3(0.05f, 0.9f, 0.0f);
            GL.Vertex3(0.0f, 1.0f, 0.0f);
            GL.Vertex3(-0.05f, 0.9f, 0.0f);

            // Z-axis (blue) with arrow
            GL.Color3(0.0f, 0.0f, 1.0f);
            GL.Vertex3(0.0f, 0.0f, 0.0f);
            GL.Vertex3(0.0f, 0.0f, 1.0f);

            // Z-axis arrow
            GL.Vertex3(0.0f, 0.0f, 1.0f);
            GL.Vertex3(0.05f, 0.0f, 0.9f);
            GL.Vertex3(0.0f, 0.0f, 1.0f);
            GL.Vertex3(-0.05f, 0.0f, 0.9f);

            GL.End();

            // Add text labels for axes
            GL.RasterPos3(1.1f, 0.0f, 0.0f);
            // Draw "X" (OpenGL doesn't have text rendering by default)

            GL.RasterPos3(0.0f, 1.1f, 0.0f);
            // Draw "Y"

            GL.RasterPos3(0.0f, 0.0f, 1.1f);
            // Draw "Z"

            // Draw pressure indicators for triaxial test
            if (simulationRunning)
            {
                DrawPressureIndicators();
            }

            GL.LineWidth(1.0f);
            GL.Enable(EnableCap.Lighting);

            // Draw color legend if in density visualization mode
            if (chkShowDensity.Checked && !wireframeMode)
            {
                DrawColorLegend();
            }

            // Swap buffers to display the rendered image
            glControl.SwapBuffers();
        }
        private void GlControl_Load(object sender, EventArgs e)
        {
            // Make sure we're using the OpenGL control's context
            glControl.MakeCurrent();

            // Initialize OpenGL
            GL.ClearColor(0.2f, 0.2f, 0.2f, 1.0f);
            GL.Enable(EnableCap.DepthTest);
            GL.Enable(EnableCap.CullFace);
            GL.CullFace(CullFaceMode.Back);

            // Set up better lighting
            GL.Enable(EnableCap.Lighting);
            GL.Enable(EnableCap.Light0);
            GL.Enable(EnableCap.Light1); // Add a second light for better illumination
            GL.Enable(EnableCap.ColorMaterial);
            GL.ColorMaterial(MaterialFace.FrontAndBack, ColorMaterialParameter.AmbientAndDiffuse);

            // Set up primary light properties (white light from upper right)
            float[] lightPosition = new float[] { 10.0f, 10.0f, 10.0f, 1.0f };
            float[] lightAmbient = new float[] { 0.3f, 0.3f, 0.3f, 1.0f };
            float[] lightDiffuse = new float[] { 0.8f, 0.8f, 0.8f, 1.0f };
            float[] lightSpecular = new float[] { 1.0f, 1.0f, 1.0f, 1.0f };

            GL.Light(LightName.Light0, LightParameter.Position, lightPosition);
            GL.Light(LightName.Light0, LightParameter.Ambient, lightAmbient);
            GL.Light(LightName.Light0, LightParameter.Diffuse, lightDiffuse);
            GL.Light(LightName.Light0, LightParameter.Specular, lightSpecular);

            // Set up secondary light (softer blue light from lower left for fill)
            float[] light2Position = new float[] { -8.0f, -5.0f, -3.0f, 1.0f };
            float[] light2Ambient = new float[] { 0.0f, 0.0f, 0.1f, 1.0f };  // Slight blue ambient
            float[] light2Diffuse = new float[] { 0.2f, 0.2f, 0.3f, 1.0f };  // Soft blue diffuse
            float[] light2Specular = new float[] { 0.0f, 0.0f, 0.0f, 1.0f }; // No specular

            GL.Light(LightName.Light1, LightParameter.Position, light2Position);
            GL.Light(LightName.Light1, LightParameter.Ambient, light2Ambient);
            GL.Light(LightName.Light1, LightParameter.Diffuse, light2Diffuse);
            GL.Light(LightName.Light1, LightParameter.Specular, light2Specular);

            // Add attenuation to make lighting more realistic
            GL.Light(LightName.Light0, LightParameter.ConstantAttenuation, 1.0f);
            GL.Light(LightName.Light0, LightParameter.LinearAttenuation, 0.02f);
            GL.Light(LightName.Light0, LightParameter.QuadraticAttenuation, 0.0f);

            GL.Light(LightName.Light1, LightParameter.ConstantAttenuation, 1.0f);
            GL.Light(LightName.Light1, LightParameter.LinearAttenuation, 0.05f);
            GL.Light(LightName.Light1, LightParameter.QuadraticAttenuation, 0.0f);

            // Set up material properties
            float[] materialAmbient = new float[] { 0.2f, 0.2f, 0.2f, 1.0f };
            float[] materialDiffuse = new float[] { 0.8f, 0.8f, 0.8f, 1.0f };
            float[] materialSpecular = new float[] { 0.5f, 0.5f, 0.5f, 1.0f }; // Reduced specular for more realism
            float materialShininess = 32.0f; // Reduced shininess for rock-like materials

            GL.Material(MaterialFace.Front, MaterialParameter.Ambient, materialAmbient);
            GL.Material(MaterialFace.Front, MaterialParameter.Diffuse, materialDiffuse);
            GL.Material(MaterialFace.Front, MaterialParameter.Specular, materialSpecular);
            GL.Material(MaterialFace.Front, MaterialParameter.Shininess, materialShininess);

            // Enable blending for transparency
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

            // Enable smoother shading
            GL.ShadeModel(ShadingModel.Smooth);

            // Enable face culling to improve performance
            GL.Enable(EnableCap.CullFace);
            GL.CullFace(CullFaceMode.Back);

            // Enable normalization of normals
            GL.Enable(EnableCap.Normalize);

            glControlInitialized = true;
            GlControl_Resize(sender, e); // Initialize the viewport and projection
        }
        private void GlControl_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                isDragging = false;
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
            if (!glControl.IsHandleCreated || !glControl.Context.IsCurrent)
                return;

            if (glControl.Width <= 0 || glControl.Height <= 0)
                return;

            // Set viewport to cover the entire control
            GL.Viewport(0, 0, glControl.Width, glControl.Height);

            // Set up the projection matrix
            GL.MatrixMode(MatrixMode.Projection);
            GL.LoadIdentity();

            // Create perspective projection with better parameters
            float aspectRatio = (float)glControl.Width / glControl.Height;
            Matrix4 perspective = Matrix4.CreatePerspectiveFieldOfView(
                MathHelper.DegreesToRadians(45.0f), // FOV
                aspectRatio,                        // Aspect ratio
                0.1f,                               // Near plane
                1000.0f);                           // Far plane (increased for better depth)
            GL.LoadMatrix(ref perspective);

            // Reset back to model view matrix
            GL.MatrixMode(MatrixMode.Modelview);
            GL.LoadIdentity();

            // Request a redraw
            glControl.Invalidate();
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