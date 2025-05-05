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

namespace CTS
{
    public partial class TriaxialSimulationForm : KryptonForm
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

        // Mohr-Coulomb parameters
        private float cohesion = 50.0f; // MPa
        private float frictionAngle = 30.0f; // degrees
        private float normalStress = 0.0f; // MPa
        private float shearStress = 0.0f; // MPa
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
        private KryptonNumericUpDown numMinPressure;
        private KryptonNumericUpDown numMaxPressure;
        private KryptonNumericUpDown numYoungModulus;
        private KryptonNumericUpDown numPoissonRatio;
        private KryptonNumericUpDown numYieldStrength;
        private KryptonNumericUpDown numBrittleStrength;
        private KryptonNumericUpDown numCohesion;
        private KryptonNumericUpDown numFrictionAngle;
        private KryptonButton btnStartSimulation;
        private KryptonButton btnStopSimulation;
        private KryptonCheckBox chkWireframe;
        private KryptonTrackBar trackSamplingRate;
        private KryptonPanel renderPanel;
        private KryptonPanel controlPanel;
        private KryptonPanel simulationPanel;
        private PictureBox stressStrainGraph;

        public TriaxialSimulationForm(MainForm mainForm)
        {
            this.mainForm = mainForm;
            this.pixelSize = mainForm.GetPixelSize();

            InitializeComponent();
            InitializeGLControl();
            InitializeBackgroundWorker();
            LoadMaterialsFromMainForm();
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
                Height = 700, // Make this taller than the visible area to enable scrolling
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

            // Sampling rate
            Label lblSampling = new Label
            {
                Text = "Quality (Sampling Rate):",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(10, 70)
            };
            controlsContent.Controls.Add(lblSampling);

            trackSamplingRate = new KryptonTrackBar
            {
                Location = new Point(140, 70),
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
                Location = new Point(10, 100)
            };
            controlsContent.Controls.Add(lblDirection);

            comboDirection = new KryptonComboBox
            {
                Location = new Point(140, 100),
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
                Location = new Point(10, 130)
            };
            controlsContent.Controls.Add(lblBehavior);

            // Elastic checkbox
            chkElastic = new KryptonCheckBox
            {
                Text = "Elastic",
                Checked = true,
                Location = new Point(140, 130),
                StateCommon = { ShortText = { Color1 = Color.White } }
            };
            controlsContent.Controls.Add(chkElastic);

            // Plastic checkbox
            chkPlastic = new KryptonCheckBox
            {
                Text = "Plastic",
                Checked = false,
                Location = new Point(200, 130),
                StateCommon = { ShortText = { Color1 = Color.White } }
            };
            controlsContent.Controls.Add(chkPlastic);

            // Brittle checkbox
            chkBrittle = new KryptonCheckBox
            {
                Text = "Brittle",
                Checked = false,
                Location = new Point(260, 130),
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
                Location = new Point(10, 170)
            };
            controlsContent.Controls.Add(lblMatProperties);

            // Min pressure
            Label lblMinPressure = new Label
            {
                Text = "Min Pressure (kPa):",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(10, 200)
            };
            controlsContent.Controls.Add(lblMinPressure);

            numMinPressure = new KryptonNumericUpDown
            {
                Location = new Point(140, 200),
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
                Location = new Point(10, 230)
            };
            controlsContent.Controls.Add(lblMaxPressure);

            numMaxPressure = new KryptonNumericUpDown
            {
                Location = new Point(140, 230),
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
                Location = new Point(10, 260)
            };
            controlsContent.Controls.Add(lblYoungModulus);

            numYoungModulus = new KryptonNumericUpDown
            {
                Location = new Point(140, 260),
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
            controlsContent.Controls.Add(numYoungModulus);

            // Poisson's ratio
            Label lblPoissonRatio = new Label
            {
                Text = "Poisson's Ratio:",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(10, 290)
            };
            controlsContent.Controls.Add(lblPoissonRatio);

            numPoissonRatio = new KryptonNumericUpDown
            {
                Location = new Point(140, 290),
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
            controlsContent.Controls.Add(numPoissonRatio);

            // Yield strength
            Label lblYieldStrength = new Label
            {
                Text = "Yield Strength (MPa):",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(10, 320)
            };
            controlsContent.Controls.Add(lblYieldStrength);

            numYieldStrength = new KryptonNumericUpDown
            {
                Location = new Point(140, 320),
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
                Location = new Point(10, 350)
            };
            controlsContent.Controls.Add(lblBrittleStrength);

            numBrittleStrength = new KryptonNumericUpDown
            {
                Location = new Point(140, 350),
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

            // Cohesion
            Label lblCohesion = new Label
            {
                Text = "Cohesion (MPa):",
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(10, 380)
            };
            controlsContent.Controls.Add(lblCohesion);

            numCohesion = new KryptonNumericUpDown
            {
                Location = new Point(140, 380),
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
                Location = new Point(10, 410)
            };
            controlsContent.Controls.Add(lblFrictionAngle);

            numFrictionAngle = new KryptonNumericUpDown
            {
                Location = new Point(140, 410),
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
                Location = new Point(10, 440)
            };
            controlsContent.Controls.Add(lblWireframe);

            chkWireframe = new KryptonCheckBox
            {
                Location = new Point(140, 440),
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
                Location = new Point(10, 470)
            };
            controlsContent.Controls.Add(lblProgress);

            progressBar = new ProgressBar
            {
                Location = new Point(140, 470),
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
                Location = new Point(140, 500),
                Width = 180,
                Height = 20
            };
            controlsContent.Controls.Add(progressLabel);

            // Generate Mesh Button
            KryptonButton btnGenerateMesh = new KryptonButton
            {
                Text = "Generate Mesh",
                Location = new Point(10, 530),
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
                Location = new Point(10, 570),
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
                Location = new Point(10, 610),
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
            glControl = new GLControl
            {
                Dock = DockStyle.Fill,
                VSync = true
            };

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

        private void InitializeGLControl()
        {
            glControl = new GLControl
            {
                Dock = DockStyle.Fill,
                VSync = true
            };

            glControl.Load += GlControl_Load;
            glControl.Paint += GlControl_Paint;
            glControl.Resize += GlControl_Resize;
            glControl.MouseDown += GlControl_MouseDown;
            glControl.MouseMove += GlControl_MouseMove;
            glControl.MouseUp += GlControl_MouseUp;
            glControl.MouseWheel += GlControl_MouseWheel;

            renderPanel.Controls.Add(glControl);
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

            // Reset simulation state
            stressStrainCurve.Clear();
            currentStrain = 0.0f;
            deformedVertices = new List<Vector3>(vertices);

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
            float stress = CalculateStress(currentStrain);

            // Add to stress-strain curve
            stressStrainCurve.Add(new Point((int)(currentStrain * 1000), (int)stress));

            // Update mesh deformation
            UpdateDeformation(currentStrain, stress);

            // Redraw
            stressStrainGraph.Invalidate();
            mohrCoulombGraph.Invalidate();
            glControl.Invalidate();
        }

        private float CalculateStress(float strain)
        {
            float stress = 0;
            float yieldStrain = yieldStrength / youngModulus;
            float brittleStrain = brittleStrength / youngModulus;

            // Start with elastic contribution
            if (isElasticEnabled)
            {
                stress = youngModulus * strain;
            }

            // Apply plastic effect if enabled and strain exceeds yield point
            if (isPlasticEnabled && strain > yieldStrain)
            {
                // Replace elastic component beyond yield point with plastic behavior
                float elasticComponent = isElasticEnabled ? yieldStrength : 0;
                float hardening = youngModulus * 0.1f; // Hardening modulus (10% of Young's)
                float plasticComponent = hardening * (strain - yieldStrain);

                // If both elastic and plastic are enabled, we combine them
                if (isElasticEnabled)
                {
                    stress = elasticComponent + plasticComponent;
                }
                else
                {
                    stress = plasticComponent;
                }
            }

            // Apply brittle effect if enabled and strain exceeds brittle point
            if (isBrittleEnabled && strain > brittleStrain)
            {
                // Dramatic stress drop after brittle failure
                float reduction = 0.8f; // 80% stress reduction

                // If no other behaviors are enabled, start from brittle strength
                if (!isElasticEnabled && !isPlasticEnabled)
                {
                    stress = brittleStrength * (1.0f - reduction);
                }
                else
                {
                    // Reduce the current stress from other behaviors
                    stress *= (1.0f - reduction);
                }
            }

            return stress;
        }

        private void UpdateDeformation(float strain, float stress)
        {
            // Calculate deformation based on current strain
            deformedVertices = new List<Vector3>(vertices.Count);

            for (int i = 0; i < vertices.Count; i++)
            {
                Vector3 vertex = vertices[i];
                Vector3 deformed = vertex;

                // Apply deformation along selected axis
                switch (selectedDirection)
                {
                    case SimulationDirection.X:
                        // Deform along X-axis
                        deformed.X = vertex.X * (1 + strain);

                        // Apply lateral contraction (Poisson effect)
                        deformed.Y = vertex.Y * (1 - strain * poissonRatio);
                        deformed.Z = vertex.Z * (1 - strain * poissonRatio);
                        break;

                    case SimulationDirection.Y:
                        // Deform along Y-axis
                        deformed.Y = vertex.Y * (1 + strain);

                        // Apply lateral contraction (Poisson effect)
                        deformed.X = vertex.X * (1 - strain * poissonRatio);
                        deformed.Z = vertex.Z * (1 - strain * poissonRatio);
                        break;

                    case SimulationDirection.Z:
                        // Deform along Z-axis
                        deformed.Z = vertex.Z * (1 + strain);

                        // Apply lateral contraction (Poisson effect)
                        deformed.X = vertex.X * (1 - strain * poissonRatio);
                        deformed.Y = vertex.Y * (1 - strain * poissonRatio);
                        break;
                }

                // Apply density-dependent deformation scaling if we have density data
                if (i < densityValues.Count)
                {
                    // Areas with lower density deform more
                    float densityFactor = densityValues[i];
                    float normDensity = (densityFactor - minDensity) / (maxDensity - minDensity);

                    // Density influence factor (0 = no influence, 1 = full influence)
                    float densityInfluence = 0.5f;

                    // Interpolate between original position and deformed position based on density
                    deformed = Vector3.Lerp(deformed, vertex, normDensity * densityInfluence);
                }

                // Add to deformed vertices list
                deformedVertices.Add(deformed);
            }

            // Apply brittle fracturing effects
            if (isBrittleEnabled && strain > brittleStrength / youngModulus)
            {
                // For brittle failure, add some randomization to vertices after failure
                Random rand = new Random(0); // Fixed seed for reproducibility
                for (int i = 0; i < deformedVertices.Count; i++)
                {
                    if (rand.NextDouble() < 0.3) // Only displace some vertices
                    {
                        Vector3 displacement = new Vector3(
                            (float)(rand.NextDouble() - 0.5) * 0.05f,
                            (float)(rand.NextDouble() - 0.5) * 0.05f,
                            (float)(rand.NextDouble() - 0.5) * 0.05f
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

                // Store the density value
                float density = densityVolume[voxel.Item1, voxel.Item2, voxel.Item3] / 255.0f;
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

            // Generate some basic tetrahedral elements for simulation
            CreateTetrahedralElements(materialVoxels, voxelToVertexIndex, samplingRate);

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
                                              int samplingRate)
        {
            // We'll create tetrahedral elements by selecting groups of 4 adjacent voxels
            int elementsCreated = 0;
            int targetElements = Math.Min(500, materialVoxels.Count / 4); // Limit to a reasonable number

            for (int i = 0; i < materialVoxels.Count && elementsCreated < targetElements; i++)
            {
                var v0 = materialVoxels[i];

                // Try to find three adjacent voxels to form a tetrahedron
                Tuple<int, int, int> v1 = new Tuple<int, int, int>(
                    v0.Item1 + samplingRate, v0.Item2, v0.Item3);

                Tuple<int, int, int> v2 = new Tuple<int, int, int>(
                    v0.Item1, v0.Item2 + samplingRate, v0.Item3);

                Tuple<int, int, int> v3 = new Tuple<int, int, int>(
                    v0.Item1, v0.Item2, v0.Item3 + samplingRate);

                // Make sure all vertices exist
                if (voxelToVertexIndex.TryGetValue(v0, out int i0) &&
                    voxelToVertexIndex.TryGetValue(v1, out int i1) &&
                    voxelToVertexIndex.TryGetValue(v2, out int i2) &&
                    voxelToVertexIndex.TryGetValue(v3, out int i3))
                {
                    // Create a tetrahedral element
                    tetrahedralElements.Add(new TetrahedralElement(i0, i1, i2, i3));
                    elementsCreated++;
                }
            }
        }

        private void GlControl_Load(object sender, EventArgs e)
        {
            // Initialize OpenGL
            GL.ClearColor(0.2f, 0.2f, 0.2f, 1.0f);
            GL.Enable(EnableCap.DepthTest);
            GL.Enable(EnableCap.CullFace);
            GL.CullFace(CullFaceMode.Back);
            GL.Enable(EnableCap.Lighting);
            GL.Enable(EnableCap.Light0);

            // Setup light
            GL.Light(LightName.Light0, LightParameter.Position, new float[] { 0.0f, 5.0f, 5.0f, 1.0f });
            GL.Light(LightName.Light0, LightParameter.Ambient, new float[] { 0.2f, 0.2f, 0.2f, 1.0f });
            GL.Light(LightName.Light0, LightParameter.Diffuse, new float[] { 0.8f, 0.8f, 0.8f, 1.0f });
            GL.Light(LightName.Light0, LightParameter.Specular, new float[] { 1.0f, 1.0f, 1.0f, 1.0f });
        }

        private void GlControl_Resize(object sender, EventArgs e)
        {
            if (glControl.Width <= 0 || glControl.Height <= 0) return;

            GL.Viewport(0, 0, glControl.Width, glControl.Height);

            GL.MatrixMode(MatrixMode.Projection);
            GL.LoadIdentity();
            Matrix4 perspective = Matrix4.CreatePerspectiveFieldOfView(
                MathHelper.DegreesToRadians(45.0f),
                (float)glControl.Width / glControl.Height,
                0.1f,
                100.0f);
            GL.LoadMatrix(ref perspective);

            GL.MatrixMode(MatrixMode.Modelview);

            glControl.Invalidate();
        }

        private void GlControl_Paint(object sender, PaintEventArgs e)
        {
            if (!glControl.IsHandleCreated || !glControl.Context.IsCurrent) return;

            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            GL.MatrixMode(MatrixMode.Modelview);
            GL.LoadIdentity();

            // Position the camera
            GL.Translate(0.0f, 0.0f, -5.0f * (1.0f / zoom));
            GL.Rotate(rotationX, 1.0f, 0.0f, 0.0f);
            GL.Rotate(rotationY, 0.0f, 1.0f, 0.0f);

            // Draw wireframe or solid model
            if (wireframeMode)
            {
                GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Line);
            }
            else
            {
                GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
            }

            // Render mesh
            if (deformedVertices.Count > 0 && indices.Count > 0)
            {
                GL.Enable(EnableCap.ColorMaterial);
                GL.ColorMaterial(MaterialFace.Front, ColorMaterialParameter.AmbientAndDiffuse);

                GL.Begin(PrimitiveType.Triangles);

                for (int i = 0; i < indices.Count; i += 3)
                {
                    for (int j = 0; j < 3; j++)
                    {
                        int index = indices[i + j];

                        // Set color based on density (if we have density values)
                        if (index < densityValues.Count)
                        {
                            float density = densityValues[index];
                            float normalizedDensity = (density - minDensity) / (maxDensity - minDensity);

                            // Create a color gradient based on density (blue->green->red)
                            if (normalizedDensity < 0.5f)
                            {
                                // Blue to green
                                float t = normalizedDensity * 2.0f;
                                GL.Color4(0.0f, t, 1.0f - t, 1.0f);
                            }
                            else
                            {
                                // Green to red
                                float t = (normalizedDensity - 0.5f) * 2.0f;
                                GL.Color4(t, 1.0f - t, 0.0f, 1.0f);
                            }
                        }
                        else
                        {
                            // Default color if no density data
                            GL.Color4(0.7f, 0.7f, 0.7f, 1.0f);
                        }

                        // Set normal and vertex
                        if (index < normals.Count)
                            GL.Normal3(normals[index]);

                        if (index < deformedVertices.Count)
                            GL.Vertex3(deformedVertices[index]);
                    }
                }

                GL.End();

                GL.Disable(EnableCap.ColorMaterial);
            }

            // Draw coordinate axes
            GL.Disable(EnableCap.Lighting);
            GL.Begin(PrimitiveType.Lines);

            // X-axis (red)
            GL.Color3(1.0f, 0.0f, 0.0f);
            GL.Vertex3(0.0f, 0.0f, 0.0f);
            GL.Vertex3(1.0f, 0.0f, 0.0f);

            // Y-axis (green)
            GL.Color3(0.0f, 1.0f, 0.0f);
            GL.Vertex3(0.0f, 0.0f, 0.0f);
            GL.Vertex3(0.0f, 1.0f, 0.0f);

            // Z-axis (blue)
            GL.Color3(0.0f, 0.0f, 1.0f);
            GL.Vertex3(0.0f, 0.0f, 0.0f);
            GL.Vertex3(0.0f, 0.0f, 1.0f);

            GL.End();
            GL.Enable(EnableCap.Lighting);

            glControl.SwapBuffers();
        }

        private Point lastMousePos;
        private bool isDragging = false;

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

            // Draw stress-strain curve
            if (stressStrainCurve.Count >= 2)
            {
                using (Pen curvePen = new Pen(Color.Blue, 2))
                {
                    // Scale points to fit in the graph
                    int maxStrain = 200; // 20% strain
                    int maxStress = (int)(youngModulus * 0.3f); // 30% of Young's modulus

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

                // Y-axis grid (stress)
                float maxStress = youngModulus * 0.3f;
                for (int i = 1; i <= 3; i++)
                {
                    int y = stressStrainGraph.Height - padding - (int)(i * height / 3.0);
                    g.DrawLine(gridPen, padding, y, stressStrainGraph.Width - padding, y);
                    g.DrawString((i * maxStress / 3.0).ToString("0"), new Font("Arial", 8),
                                Brushes.Black, padding - 25, y - 5);
                }
            }

            // Draw current stress/strain point
            if (stressStrainCurve.Count > 0)
            {
                Point lastPoint = stressStrainCurve.Last();

                // Scale point to fit in the graph
                int maxStrain = 200; // 20% strain
                int maxStress = (int)(youngModulus * 0.3f);

                int x = padding + (int)(lastPoint.X * width / maxStrain);
                int y = stressStrainGraph.Height - padding - (int)(lastPoint.Y * height / maxStress);

                using (SolidBrush pointBrush = new SolidBrush(Color.Red))
                {
                    g.FillEllipse(pointBrush, x - 5, y - 5, 10, 10);
                }

                // Display current values
                using (Font valueFont = new Font("Arial", 10, FontStyle.Bold))
                {
                    string stressStr = (lastPoint.Y / 10.0f).ToString("0.0") + " MPa";
                    string strainStr = (lastPoint.X / 10.0f).ToString("0.0") + "%";

                    g.DrawString("Strain: " + strainStr, valueFont, Brushes.Black, padding, padding);
                    g.DrawString("Stress: " + stressStr, valueFont, Brushes.Black, padding, padding + 20);

                    // Show active behaviors
                    string behaviors = "Behaviors: ";
                    if (isElasticEnabled) behaviors += "Elastic ";
                    if (isPlasticEnabled) behaviors += "Plastic ";
                    if (isBrittleEnabled) behaviors += "Brittle";
                    g.DrawString(behaviors, valueFont, Brushes.Blue, padding, padding + 40);
                }
            }
        }

        // Helper classes
        private class MeshGenerationParameters
        {
            public Material Material { get; set; }
            public int SamplingRate { get; set; }
        }

        private class TetrahedralElement
        {
            public int[] Vertices { get; private set; }

            public TetrahedralElement(int v1, int v2, int v3, int v4)
            {
                Vertices = new int[] { v1, v2, v3, v4 };
            }
        }
    }
}