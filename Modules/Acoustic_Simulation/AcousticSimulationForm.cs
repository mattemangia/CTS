using Krypton.Toolkit;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using static MaterialDensityLibrary;
using FontStyle = System.Drawing.FontStyle;
using MessageBox = System.Windows.Forms.MessageBox;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

namespace CTSegmenter
{
    public class AcousticSimulationForm : KryptonForm, IMaterialDensityProvider
    {
        private bool isInitializing = true;
        private MainForm mainForm;
        private dynamic simulator;
        private Material selectedMaterial;
        private double baseDensity = 0.0; // in kg/m³
        private bool hasDensityVariation = false;
        private byte selectedMaterialID = 0;
        private bool isSimDragging = false;
        private System.Drawing.Point lastSimMousePosition;
        private float simRotationX = 30;
        private float simRotationY = 30;
        private float simZoom = 1.0f;
        private PointF simPan = new PointF(0, 0);
        private KryptonCheckBox chkAutoElasticProps;
        private VolumeRenderer simulationRenderer;
        private KryptonCheckBox chkRunOnGpu;
        private AcousticSimulator cpuSimulator;
        private AcousticSimulatorGPUWrapper gpuSimulator;
        private bool usingGpuSimulator = false;
        private AcousticSimulationVisualizer visualizer;
        // UI Controls
        private TabControl tabControl;
        private TabPage tabVolume;
        private TabPage tabSimulation;
        private TabPage tabResults;
        private ToolStrip toolStrip;
        private double materialVolume;
        private double totalMaterialVolume;
        private double volumePercentage;
        private string volumeUnit;
        private KryptonComboBox comboMaterials;
        private KryptonButton btnSetDensity;
        private KryptonButton btnApplyVariation;
        private KryptonLabel lblDensityInfo;
        private KryptonCheckBox chkFullVolumeRendering;
        private bool useFullVolumeRendering = true;
        private PictureBox pictureBoxVolume;
        //More Simulation COntrols
        private KryptonComboBox comboMaterialLibrary;
        private KryptonComboBox comboAxis;
        private KryptonComboBox comboWaveType;
        private KryptonNumericUpDown numConfiningPressure;
        private KryptonNumericUpDown numTensileStrength;
        private KryptonNumericUpDown numFailureAngle;
        private KryptonNumericUpDown numCohesion;
        private KryptonNumericUpDown numEnergy;
        private KryptonNumericUpDown numFrequency;
        private KryptonNumericUpDown numAmplitude;
        private KryptonNumericUpDown numTimeSteps;
        private KryptonButton btnCalculateExtent;
        private KryptonLabel lblExtentX;
        private KryptonLabel lblExtentY;
        private KryptonLabel lblExtentZ;
        private KryptonCheckBox chkElasticModel;
        private KryptonCheckBox chkPlasticModel;
        private KryptonCheckBox chkBrittleModel;
        private KryptonButton btnStartSimulation;
        private PictureBox pictureBoxSimulation;
        // Dictionary to store material properties
        private Dictionary<string, MaterialProperties> materialLibrary = new Dictionary<string, MaterialProperties>();
        
        private bool simulationRunning = false;
        private CancellableProgressForm simulationProgressForm;
        private SimulationResults simulationResults;
        private KryptonNumericUpDown numYoungsModulus;
        private KryptonNumericUpDown numPoissonRatio;

        // A class to store simulation results
        private class SimulationResults
        {
            public double PWaveVelocity { get; set; } // m/s
            public double SWaveVelocity { get; set; } // m/s
            public double VpVsRatio { get; set; }
            public int PWaveTravelTime { get; set; } // time steps
            public int SWaveTravelTime { get; set; } // time steps
        }
        // Material properties class
        private class MaterialProperties
        {
            public double ConfiningPressure { get; set; }
            public double TensileStrength { get; set; }
            public double FailureAngle { get; set; }
            public double Cohesion { get; set; }
            public double Energy { get; set; }
            public double Frequency { get; set; }
            public int Amplitude { get; set; }
            public int TimeSteps { get; set; }

          
            /// <summary>Young’s modulus in MPa</summary>
            public double YoungsModulus { get; set; }
            /// <summary>Poisson’s ratio (dimensionless)</summary>
            public double PoissonRatio { get; set; }
        }


        // Volume data
        private float[,,] densityVolume; // Stores density values for each voxel

        // Visualization parameters
        private float rotationX = 30;
        private float rotationY = 30;
        private float zoom = 1.0f;
        private PointF pan = new PointF(0, 0);

        // Rendering objects
        private VolumeRenderer volumeRenderer;

        public AcousticSimulationForm(MainForm mainForm)
        {
            this.mainForm = mainForm;
            InitializeComponent();

            // Load materials first
            //adMaterials();

            // Ensure correct material is selected before initializing
            //sureCorrectMaterialSelected();

            // Set the initial view parameters
            rotationX = 30;
            rotationY = 30;
            zoom = 1.0f;
            pan = new PointF(0, 0);

            // Initialize the volume with the selected material
            //itializeHomogeneousDensityVolume();

            // Set initialization flag to false after everything is set up
            isInitializing = false;
        }
        private void InitializeComponent()
        {
            // Form settings
            this.Text = "Acoustic Simulation";
            this.Size = new Size(1000, 800);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(45, 45, 48); // Dark background

            // Create toolbar
            toolStrip = new ToolStrip();
            toolStrip.BackColor = Color.FromArgb(30, 30, 30);
            toolStrip.ForeColor = Color.White;
            toolStrip.Renderer = new DarkToolStripRenderer();
            toolStrip.Dock = DockStyle.Top;

            // Add some placeholder buttons to the toolbar
            ToolStripButton btnNew = new ToolStripButton("New");
            btnNew.DisplayStyle = ToolStripItemDisplayStyle.Image;
            btnNew.Image = CreateSimpleIcon(16, Color.White);

            ToolStripButton btnOpen = new ToolStripButton("Open");
            btnOpen.DisplayStyle = ToolStripItemDisplayStyle.Image;
            btnOpen.Image = CreateSimpleIcon(16, Color.White);

            ToolStripButton btnSave = new ToolStripButton("Save");
            btnSave.DisplayStyle = ToolStripItemDisplayStyle.Image;
            btnSave.Image = CreateSimpleIcon(16, Color.White);

            toolStrip.Items.Add(btnNew);
            toolStrip.Items.Add(btnOpen);
            toolStrip.Items.Add(btnSave);

            // Create tab control
            tabControl = new TabControl();
            tabControl.Dock = DockStyle.Fill;
            tabControl.DrawMode = TabDrawMode.OwnerDrawFixed;
            tabControl.DrawItem += TabControl_DrawItem; // Custom drawing for dark theme

            // Create tabs
            tabVolume = new TabPage();
            tabVolume.Text = "Volume";
            tabVolume.BackColor = Color.FromArgb(45, 45, 48); // Dark background

            tabSimulation = new TabPage();
            tabSimulation.Text = "Simulation";
            tabSimulation.BackColor = Color.FromArgb(45, 45, 48); // Dark background

            tabResults = new TabPage();
            tabResults.Text = "Results";
            tabResults.BackColor = Color.FromArgb(45, 45, 48); // Dark background

            // Add tabs to tab control
            tabControl.TabPages.Add(tabVolume);
            tabControl.TabPages.Add(tabSimulation);
            tabControl.TabPages.Add(tabResults);

            // Setup Volume tab
            InitializeVolumeTab();

            // Setup Simulation tab
            InitializeSimulationTab();

            // Add controls to form
            this.Controls.Add(tabControl);
            this.Controls.Add(toolStrip);

            // Load materials when form loads
            this.Load += (s, e) =>
            {
                LoadMaterials();
                EnsureCorrectMaterialSelected();
                // If we have materials, auto-select the first one and initialize the volume
                if (comboMaterials.Items.Count > 0)
                {
                    comboMaterials.SelectedIndex = 0;
                    if (selectedMaterial != null)
                    {
                        // Default density if not set
                        if (baseDensity <= 0)
                        {
                            baseDensity = 1000; // Default to water density (kg/m³)
                            selectedMaterial.Density = baseDensity;
                            UpdateMaterialInfo();
                        }

                        // Initialize the volume
                        InitializeHomogeneousDensityVolume();

                        // Force a render
                        pictureBoxVolume.Invalidate();
                    }
                }
            };
        }
        private void InitializeSimulationTab()
        {
            Panel scrollContainer = new Panel();
            scrollContainer.Dock = DockStyle.Left;
            scrollContainer.Width = 250;
            scrollContainer.BackColor = Color.FromArgb(45, 45, 48); // Dark background
            scrollContainer.AutoScroll = true; // Enable scrolling
            // Create panel for controls
            KryptonPanel controlPanel = new KryptonPanel();
            controlPanel.Dock = DockStyle.None;
            controlPanel.Width = 210;
            controlPanel.StateCommon.Color1 = Color.FromArgb(45, 45, 48); // Dark background
            controlPanel.StateCommon.Color2 = Color.FromArgb(45, 45, 48); // Dark background

            // Define vertical spacing between controls
            int verticalSpacing = 30;
            int currentY = 20;
            int controlWidth = 230;

            // 0. Material library combobox
            KryptonLabel lblMaterialLibrary = new KryptonLabel();
            lblMaterialLibrary.Text = "Material:";
            lblMaterialLibrary.Location = new Point(10, currentY);
            lblMaterialLibrary.StateCommon.ShortText.Color1 = Color.White;
            controlPanel.Controls.Add(lblMaterialLibrary);

            comboMaterialLibrary = new KryptonComboBox();
            comboMaterialLibrary.Location = new Point(10, currentY + 20);
            comboMaterialLibrary.Width = controlWidth;
            comboMaterialLibrary.DropDownStyle = ComboBoxStyle.DropDownList;
            comboMaterialLibrary.SelectedIndexChanged += comboMaterialLibrary_SelectedIndexChanged;
            controlPanel.Controls.Add(comboMaterialLibrary);

            currentY += verticalSpacing + 20;

            // 1. Axis combobox
            KryptonLabel lblAxis = new KryptonLabel();
            lblAxis.Text = "Axis:";
            lblAxis.Location = new Point(10, currentY);
            lblAxis.StateCommon.ShortText.Color1 = Color.White;
            controlPanel.Controls.Add(lblAxis);

            comboAxis = new KryptonComboBox();
            comboAxis.Location = new Point(10, currentY + 20);
            comboAxis.Width = controlWidth;
            comboAxis.DropDownStyle = ComboBoxStyle.DropDownList;
            comboAxis.Items.AddRange(new object[] { "X", "Y", "Z" });
            comboAxis.SelectedIndex = 0; // Default to X
            comboAxis.SelectedIndexChanged += comboAxis_SelectedIndexChanged;
            controlPanel.Controls.Add(comboAxis);

            currentY += verticalSpacing + 20;

            // 2. Wave type combobox
            KryptonLabel lblWaveType = new KryptonLabel();
            lblWaveType.Text = "Wave Type:";
            lblWaveType.Location = new Point(10, currentY);
            lblWaveType.StateCommon.ShortText.Color1 = Color.White;
            controlPanel.Controls.Add(lblWaveType);

            comboWaveType = new KryptonComboBox();
            comboWaveType.Location = new Point(10, currentY + 20);
            comboWaveType.Width = controlWidth;
            comboWaveType.DropDownStyle = ComboBoxStyle.DropDownList;
            comboWaveType.Items.AddRange(new object[] { "P Wave", "S Wave", "Both" });
            comboWaveType.SelectedIndex = 0; // Default to P Wave
            controlPanel.Controls.Add(comboWaveType);

            currentY += verticalSpacing + 20;

            // 3. Physical properties numeric inputs
            // Confining Pressure
            KryptonLabel lblConfiningPressure = new KryptonLabel();
            lblConfiningPressure.Text = "Confining Pressure (MPa):";
            lblConfiningPressure.Location = new Point(10, currentY);
            lblConfiningPressure.StateCommon.ShortText.Color1 = Color.White;
            controlPanel.Controls.Add(lblConfiningPressure);

            numConfiningPressure = new KryptonNumericUpDown();
            numConfiningPressure.Location = new Point(10, currentY + 20);
            numConfiningPressure.Width = controlWidth-30;
            numConfiningPressure.DecimalPlaces = 3;
            numConfiningPressure.Minimum = 0;
            numConfiningPressure.Maximum = 1000;
            numConfiningPressure.Value = 1.0m;
            controlPanel.Controls.Add(numConfiningPressure);

            currentY += verticalSpacing + 20;

            // Tensile Strength
            KryptonLabel lblTensileStrength = new KryptonLabel();
            lblTensileStrength.Text = "Tensile Strength (MPa):";
            lblTensileStrength.Location = new Point(10, currentY);
            lblTensileStrength.StateCommon.ShortText.Color1 = Color.White;
            controlPanel.Controls.Add(lblTensileStrength);

            numTensileStrength = new KryptonNumericUpDown();
            numTensileStrength.Location = new Point(10, currentY + 20);
            numTensileStrength.Width = controlWidth-30;
            numTensileStrength.DecimalPlaces = 3;
            numTensileStrength.Minimum = 0;
            numTensileStrength.Maximum = 1000;
            numTensileStrength.Value = 10.0m;
            controlPanel.Controls.Add(numTensileStrength);

            currentY += verticalSpacing + 20;

            // Failure Angle
            KryptonLabel lblFailureAngle = new KryptonLabel();
            lblFailureAngle.Text = "Failure Angle (degrees):";
            lblFailureAngle.Location = new Point(10, currentY);
            lblFailureAngle.StateCommon.ShortText.Color1 = Color.White;
            controlPanel.Controls.Add(lblFailureAngle);

            numFailureAngle = new KryptonNumericUpDown();
            numFailureAngle.Location = new Point(10, currentY + 20);
            numFailureAngle.Width = controlWidth-30;
            numFailureAngle.DecimalPlaces = 3;
            numFailureAngle.Minimum = 0;
            numFailureAngle.Maximum = 90;
            numFailureAngle.Value = 30.0m; // Default to 30 as specified
            controlPanel.Controls.Add(numFailureAngle);

            currentY += verticalSpacing + 20;

            // Cohesion
            KryptonLabel lblCohesion = new KryptonLabel();
            lblCohesion.Text = "Cohesion (MPa):";
            lblCohesion.Location = new Point(10, currentY);
            lblCohesion.StateCommon.ShortText.Color1 = Color.White;
            controlPanel.Controls.Add(lblCohesion);

            numCohesion = new KryptonNumericUpDown();
            numCohesion.Location = new Point(10, currentY + 20);
            numCohesion.Width = controlWidth - 30;
            numCohesion.DecimalPlaces = 3;
            numCohesion.Minimum = 0;
            numCohesion.Maximum = 1000;
            numCohesion.Value = 5.0m;
            controlPanel.Controls.Add(numCohesion);

            currentY += verticalSpacing + 20;

            // 4. Energy
            KryptonLabel lblEnergy = new KryptonLabel();
            lblEnergy.Text = "Energy (J):";
            lblEnergy.Location = new Point(10, currentY);
            lblEnergy.StateCommon.ShortText.Color1 = Color.White;
            controlPanel.Controls.Add(lblEnergy);

            numEnergy = new KryptonNumericUpDown();
            numEnergy.Location = new Point(10, currentY + 20);
            numEnergy.Width = controlWidth - 30;
            numEnergy.DecimalPlaces = 3;
            numEnergy.Minimum = 0;
            numEnergy.Maximum = 10000;
            numEnergy.Value = 1.0m;
            controlPanel.Controls.Add(numEnergy);

            currentY += verticalSpacing + 20;

            // 5. Frequency
            KryptonLabel lblFrequency = new KryptonLabel();
            lblFrequency.Text = "Frequency (kHz):";
            lblFrequency.Location = new Point(10, currentY);
            lblFrequency.StateCommon.ShortText.Color1 = Color.White;
            controlPanel.Controls.Add(lblFrequency);

            numFrequency = new KryptonNumericUpDown();
            numFrequency.Location = new Point(10, currentY + 20);
            numFrequency.Width = controlWidth - 30;
            numFrequency.DecimalPlaces = 3;
            numFrequency.Minimum = 0;
            numFrequency.Maximum = 1000;
            numFrequency.Value = 100.0m;
            controlPanel.Controls.Add(numFrequency);

            currentY += verticalSpacing + 20;

            // 6. Amplitude
            KryptonLabel lblAmplitude = new KryptonLabel();
            lblAmplitude.Text = "Amplitude:";
            lblAmplitude.Location = new Point(10, currentY);
            lblAmplitude.StateCommon.ShortText.Color1 = Color.White;
            controlPanel.Controls.Add(lblAmplitude);

            numAmplitude = new KryptonNumericUpDown();
            numAmplitude.Location = new Point(10, currentY + 20);
            numAmplitude.Width = controlWidth - 30;
            numAmplitude.DecimalPlaces = 0;
            numAmplitude.Minimum = 1;
            numAmplitude.Maximum = 10000;
            numAmplitude.Value = 100;
            controlPanel.Controls.Add(numAmplitude);

            currentY += verticalSpacing + 20;

            // 7. Time Steps
            KryptonLabel lblTimeSteps = new KryptonLabel();
            lblTimeSteps.Text = "Time Steps:";
            lblTimeSteps.Location = new Point(10, currentY);
            lblTimeSteps.StateCommon.ShortText.Color1 = Color.White;
            controlPanel.Controls.Add(lblTimeSteps);

            numTimeSteps = new KryptonNumericUpDown();
            numTimeSteps.Location = new Point(10, currentY + 20);
            numTimeSteps.Width = controlWidth - 30;
            numTimeSteps.DecimalPlaces = 0;
            numTimeSteps.Minimum = 1;
            numTimeSteps.Maximum = 10000000;
            numTimeSteps.Value = 100;
            controlPanel.Controls.Add(numTimeSteps);
            currentY += verticalSpacing + 20;
            chkAutoElasticProps = new KryptonCheckBox
            {
                Text = "Auto-calc E & ν from density",
                Location = new Point(10, currentY)
            };
            chkAutoElasticProps.CheckedChanged += (s, e) => {
                if (chkAutoElasticProps.Checked)
                    CalculateAutoElasticProperties();
                else
                {
                    var props = materialLibrary[comboMaterialLibrary.SelectedItem.ToString()];
                    numYoungsModulus.Value = (decimal)props.YoungsModulus;
                    numPoissonRatio.Value = (decimal)props.PoissonRatio;
                }
            };
            controlPanel.Controls.Add(chkAutoElasticProps);
            currentY += verticalSpacing+20;

            // Young's Modulus
            var lblYoung = new KryptonLabel { Text = "Young’s Modulus (MPa):", Location = new Point(10, currentY) };
            controlPanel.Controls.Add(lblYoung);
            numYoungsModulus = new KryptonNumericUpDown
            {
                Location = new Point(10, currentY + 20),
                Width = controlWidth - 30,
                DecimalPlaces = 1,
                Minimum = 0,
                Maximum = decimal.MaxValue,
                Value = 50.0m   // default
            };
            controlPanel.Controls.Add(numYoungsModulus);
            currentY += verticalSpacing + 20;

            // Poisson’s ratio
            var lblPoisson = new KryptonLabel { Text = "Poisson’s Ratio:", Location = new Point(10, currentY) };
            controlPanel.Controls.Add(lblPoisson);
            numPoissonRatio = new KryptonNumericUpDown
            {
                Location = new Point(10, currentY + 20),
                Width = controlWidth - 30,
                DecimalPlaces = 3,
                Minimum = 0m,
                Maximum = 0.5m,
                Increment = 0.01m,
                Value = 0.25m   // default
            };
            controlPanel.Controls.Add(numPoissonRatio);
            

            currentY += verticalSpacing + 20;

            // 8. Calculate Extent button
            btnCalculateExtent = new KryptonButton();
            btnCalculateExtent.Text = "Calculate Extent";
            btnCalculateExtent.Location = new Point(10, currentY);
            btnCalculateExtent.Width = controlWidth - 30;
            btnCalculateExtent.Values.Image = CreateCalculateIcon();
            btnCalculateExtent.Click += BtnCalculateExtent_Click;
            controlPanel.Controls.Add(btnCalculateExtent);

            currentY += verticalSpacing;

            // 9. Extent labels
            KryptonLabel lblExtentHeader = new KryptonLabel();
            lblExtentHeader.Text = "Calculated Extent:";
            lblExtentHeader.Location = new Point(10, currentY);
            lblExtentHeader.StateCommon.ShortText.Color1 = Color.White;
            controlPanel.Controls.Add(lblExtentHeader);

            currentY += 20;

            lblExtentX = new KryptonLabel();
            lblExtentX.Text = "X: Not calculated";
            lblExtentX.Location = new Point(20, currentY);
            lblExtentX.StateCommon.ShortText.Color1 = Color.White;
            controlPanel.Controls.Add(lblExtentX);

            currentY += 20;

            lblExtentY = new KryptonLabel();
            lblExtentY.Text = "Y: Not calculated";
            lblExtentY.Location = new Point(20, currentY);
            lblExtentY.StateCommon.ShortText.Color1 = Color.White;
            controlPanel.Controls.Add(lblExtentY);

            currentY += 20;

            lblExtentZ = new KryptonLabel();
            lblExtentZ.Text = "Z: Not calculated";
            lblExtentZ.Location = new Point(20, currentY);
            lblExtentZ.StateCommon.ShortText.Color1 = Color.White;
            controlPanel.Controls.Add(lblExtentZ);

            currentY += verticalSpacing;

            // 10. Model checkboxes
            KryptonLabel lblModels = new KryptonLabel();
            lblModels.Text = "Simulation Models:";
            lblModels.Location = new Point(10, currentY);
            lblModels.StateCommon.ShortText.Color1 = Color.White;
            controlPanel.Controls.Add(lblModels);

            currentY += 20;

            chkElasticModel = new KryptonCheckBox();
            chkElasticModel.Text = "Elastic Model";
            chkElasticModel.Location = new Point(20, currentY);
            chkElasticModel.Width = controlWidth - 20;
            chkElasticModel.Checked = true;
            chkElasticModel.CheckedChanged += ModelCheckBox_CheckedChanged;
            controlPanel.Controls.Add(chkElasticModel);

            currentY += 20;

            chkPlasticModel = new KryptonCheckBox();
            chkPlasticModel.Text = "Plastic Model";
            chkPlasticModel.Location = new Point(20, currentY);
            chkPlasticModel.Width = controlWidth - 20;
            chkPlasticModel.CheckedChanged += ModelCheckBox_CheckedChanged;
            controlPanel.Controls.Add(chkPlasticModel);

            currentY += 20;

            chkBrittleModel = new KryptonCheckBox();
            chkBrittleModel.Text = "Brittle Model";
            chkBrittleModel.Location = new Point(20, currentY);
            chkBrittleModel.Width = controlWidth - 20;
            chkBrittleModel.CheckedChanged += ModelCheckBox_CheckedChanged;
            controlPanel.Controls.Add(chkBrittleModel);

            currentY += verticalSpacing;
            chkRunOnGpu = new KryptonCheckBox
            {
                Text = "Run on GPU",
                Location = new Point(10, currentY),
                Width = controlWidth - 20,
                Checked = false
            };
            controlPanel.Controls.Add(chkRunOnGpu);
            currentY += verticalSpacing;
            // 11. Start Simulation button
            btnStartSimulation = new KryptonButton();
            btnStartSimulation.Text = "Start Simulation";
            btnStartSimulation.Location = new Point(10, currentY);
            btnStartSimulation.Width = controlWidth - 30;
            btnStartSimulation.Values.Image = CreateStartSimulationIcon();
            btnStartSimulation.Click += BtnStartSimulation_Click;
            controlPanel.Controls.Add(btnStartSimulation);

            // Set the height of the controlPanel to accommodate all controls
            controlPanel.Height = currentY + 50; // Add some padding at the bottom

            // Add the controlPanel to the scroll container
            scrollContainer.Controls.Add(controlPanel);

            // Create visualization panel
            KryptonPanel visualPanel = new KryptonPanel();
            visualPanel.Dock = DockStyle.Fill;
            visualPanel.StateCommon.Color1 = Color.FromArgb(30, 30, 30); // Dark background
            visualPanel.StateCommon.Color2 = Color.FromArgb(30, 30, 30); // Dark background

            // Create picture box for simulation visualization
            pictureBoxSimulation = new PictureBox();
            pictureBoxSimulation.Dock = DockStyle.Fill;
            pictureBoxSimulation.BackColor = Color.FromArgb(20, 20, 20); // Even darker background
            pictureBoxSimulation.SizeMode = PictureBoxSizeMode.Normal;
            pictureBoxSimulation.Paint += PictureBoxSimulation_Paint;

            // Add mouse events for rotation, panning, zooming
            pictureBoxSimulation.MouseDown += PictureBoxSimulation_MouseDown;
            pictureBoxSimulation.MouseMove += PictureBoxSimulation_MouseMove;
            pictureBoxSimulation.MouseUp += PictureBoxSimulation_MouseUp;
            pictureBoxSimulation.MouseWheel += PictureBoxSimulation_MouseWheel;

            // Add picture box to panel
            visualPanel.Controls.Add(pictureBoxSimulation);

            // Add panels to tab page
            tabSimulation.Controls.Add(visualPanel);
            
            tabSimulation.Controls.Add(scrollContainer);

            // Initialize material library
            InitializeMaterialLibrary();
        }
        private void PictureBoxSimulation_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Right)
            {
                isSimDragging = true;
                lastSimMousePosition = e.Location;
            }
        }

        private void PictureBoxSimulation_MouseMove(object sender, MouseEventArgs e)
        {
            if (isSimDragging)
            {
                int dx = e.X - lastSimMousePosition.X;
                int dy = e.Y - lastSimMousePosition.Y;

                if (e.Button == MouseButtons.Left)
                {
                    // Rotate
                    simRotationY += dx * 0.5f;
                    simRotationX += dy * 0.5f;

                    // Limit rotation angles
                    simRotationX = Math.Max(-90, Math.Min(90, simRotationX));
                }
                else if (e.Button == MouseButtons.Right)
                {
                    // Pan
                    simPan.X += dx;
                    simPan.Y += dy;
                }

                lastSimMousePosition = e.Location;

                // Update the visualization
                pictureBoxSimulation.Invalidate();
            }
        }

        private void PictureBoxSimulation_MouseUp(object sender, MouseEventArgs e)
        {
            isSimDragging = false;
        }

        private void PictureBoxSimulation_MouseWheel(object sender, MouseEventArgs e)
        {
            // Zoom in/out with more responsive adjustment
            float zoomFactor = 1.2f;
            if (e.Delta > 0)
            {
                simZoom *= zoomFactor;
            }
            else
            {
                simZoom /= zoomFactor;
            }

            // Limit zoom range - increase the upper limit for more zoom capability
            simZoom = Math.Max(0.1f, Math.Min(50.0f, simZoom));

            // Update the visualization
            pictureBoxSimulation.Invalidate();
        }

        
        private void comboMaterialLibrary_SelectedIndexChanged(object sender, EventArgs e)
        {
            string selectedMaterial = comboMaterialLibrary.SelectedItem.ToString();
            if (materialLibrary.ContainsKey(selectedMaterial))
            {
                // Load properties for the selected material
                MaterialProperties props = materialLibrary[selectedMaterial];
                if (chkAutoElasticProps.Checked)
                    CalculateAutoElasticProperties();
                else
                {
                    numYoungsModulus.Value = (decimal)props.YoungsModulus;
                    numPoissonRatio.Value = (decimal)props.PoissonRatio;
                }
                numConfiningPressure.Value = (decimal)props.ConfiningPressure;
                numTensileStrength.Value = (decimal)props.TensileStrength;
                numFailureAngle.Value = (decimal)props.FailureAngle;
                numCohesion.Value = (decimal)props.Cohesion;
                numEnergy.Value = (decimal)props.Energy;
                numFrequency.Value = (decimal)props.Frequency;
                numAmplitude.Value = props.Amplitude;
                numTimeSteps.Value = props.TimeSteps;

                // Update visualization
                UpdateSimulationVisualization();
            }
        }
        private void comboAxis_SelectedIndexChanged(object sender, EventArgs e)
        {
            // Update visualization when axis changes
            UpdateSimulationVisualization();
        }

        private void UpdateSimulationVisualization()
        {
            // Trigger redraw of the simulation visualization
            pictureBoxSimulation.Invalidate();
        }

        private void BtnCalculateExtent_Click(object sender, EventArgs e)
        {
            // Calculate the extent based on the input parameters
            CalculateExtent();
            
        }

        private void CalculateExtent()
        {
            try
            {
                // Get parameters
                double confiningPressure = (double)numConfiningPressure.Value;
                double tensileStrength = (double)numTensileStrength.Value;
                double energy = (double)numEnergy.Value;
                double frequency = (double)numFrequency.Value;

                // Get actual model dimensions in mm
                double objectSizeX = 0, objectSizeY = 0, objectSizeZ = 0;

                if (mainForm != null && mainForm.volumeLabels != null)
                {
                    int width = mainForm.GetWidth();
                    int height = mainForm.GetHeight();
                    int depth = mainForm.GetDepth();
                    double pixelSizeMM = mainForm.GetPixelSize() * 1000; // Convert from m to mm

                    // Find min/max coordinates of the material
                    int minX = width, minY = height, minZ = depth;
                    int maxX = 0, maxY = 0, maxZ = 0;

                    for (int z = 0; z < depth; z++)
                    {
                        for (int y = 0; y < height; y++)
                        {
                            for (int x = 0; x < width; x++)
                            {
                                if (mainForm.volumeLabels[x, y, z] == selectedMaterialID)
                                {
                                    minX = Math.Min(minX, x);
                                    minY = Math.Min(minY, y);
                                    minZ = Math.Min(minZ, z);

                                    maxX = Math.Max(maxX, x);
                                    maxY = Math.Max(maxY, y);
                                    maxZ = Math.Max(maxZ, z);
                                }
                            }
                        }
                    }

                    // Calculate dimensions in mm
                    objectSizeX = (maxX - minX + 1) * pixelSizeMM;
                    objectSizeY = (maxY - minY + 1) * pixelSizeMM;
                    objectSizeZ = (maxZ - minZ + 1) * pixelSizeMM;

                    Logger.Log($"[AcousticSimulationForm] Object dimensions: {objectSizeX:F2} x {objectSizeY:F2} x {objectSizeZ:F2} mm");
                }

                // If we couldn't determine the object size, use a small default
                if (objectSizeX <= 0) objectSizeX = 3.0;
                if (objectSizeY <= 0) objectSizeY = 3.0;
                if (objectSizeZ <= 0) objectSizeZ = 3.0;

                // Calculate extent for the given material and parameters
                // Base the calculation on the actual object size rather than arbitrary formulas
                double baseExtent = Math.Max(1.0, Math.Max(objectSizeX, Math.Max(objectSizeY, objectSizeZ)));

                // Factor in energy, frequency and material properties for a more realistic estimation
                double energyFactor = Math.Sqrt(energy);
                double frequencyFactor = Math.Pow(frequency / 100.0, 0.5); // Higher frequency = smaller penetration
                double materialFactor = Math.Pow(tensileStrength / (confiningPressure + 0.1), 0.3);

                double extentX = baseExtent * energyFactor * materialFactor / frequencyFactor;

                // Vary Y and Z slightly for demonstration
                double extentY = extentX * 0.95;
                double extentZ = extentX * 0.90;

                // Round to 2 decimal places
                extentX = Math.Round(extentX, 2);
                extentY = Math.Round(extentY, 2);
                extentZ = Math.Round(extentZ, 2);

                // Update extent labels
                lblExtentX.Text = $"X: {extentX} mm";
                lblExtentY.Text = $"Y: {extentY} mm";
                lblExtentZ.Text = $"Z: {extentZ} mm";

                // Update visualization
                UpdateSimulationVisualization();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error calculating extent: {ex.Message}",
                    "Calculation Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }


        private void ModelCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            // Ensure at least one model is selected
            if (!chkElasticModel.Checked && !chkPlasticModel.Checked && !chkBrittleModel.Checked)
            {
                // Recheck the checkbox that was just unchecked
                ((KryptonCheckBox)sender).Checked = true;
                MessageBox.Show("At least one model must be selected.",
                    "Model Selection", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void BtnStartSimulation_Click(object sender, EventArgs e)
        {
            try
            {
                // Prevent re-entrancy
                if ((cpuSimulator != null || gpuSimulator != null) && simulationRunning)
                {
                    MessageBox.Show("Simulation is already running.", "Simulation in Progress",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                // Ensure volume data is ready
                if (densityVolume == null || mainForm.volumeLabels == null)
                {
                    MessageBox.Show("Please initialize the volume data first.",
                        "Missing Data", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Disable UI
                btnStartSimulation.Enabled = false;

                // If auto-calc is checked, recompute E & ν now
                if (chkAutoElasticProps.Checked)
                    CalculateAutoElasticProperties();

                // Gather all parameters
                int width = mainForm.GetWidth();
                int height = mainForm.GetHeight();
                int depth = mainForm.GetDepth();
                float pixelSizeF = (float)mainForm.GetPixelSize();
                byte[,,] labels = GetVolumeLabelsArray();
                byte materialID = selectedMaterialID;
                string axis = comboAxis.SelectedItem.ToString();
                string waveType = comboWaveType.SelectedItem.ToString();
                double confiningPressure = (double)numConfiningPressure.Value;
                double tensileStrength = (double)numTensileStrength.Value;
                double failureAngle = (double)numFailureAngle.Value;
                double cohesion = (double)numCohesion.Value;
                double energy = (double)numEnergy.Value;
                double frequency = (double)numFrequency.Value;
                int amplitude = (int)numAmplitude.Value;
                int timeSteps = (int)numTimeSteps.Value;
                bool useElastic = chkElasticModel.Checked;
                bool usePlastic = chkPlasticModel.Checked;
                bool useBrittle = chkBrittleModel.Checked;
                double youngsModulus = (double)numYoungsModulus.Value;  // MPa
                double poissonRatio = (double)numPoissonRatio.Value;   // unitless
                bool useGPU = chkRunOnGpu.Checked;  // Check if GPU should be used

                // ADDED: Ensure minimum energy and amplitude values
                if (energy <= 0.001)
                {
                    Logger.Log("[AcousticSimulationForm] Warning: Energy is very low, increasing to minimum value");
                    numEnergy.Value = 1.0m; // Set minimum energy
                    energy = 1.0;
                }

                if (amplitude < 100)
                {
                    Logger.Log("[AcousticSimulationForm] Warning: Amplitude is low, increasing to 100");
                    numAmplitude.Value = 100; // Set minimum amplitude
                    amplitude = 100;
                }

                // Log key simulation parameters
                Logger.Log($"[AcousticSimulationForm] Starting simulation with: E={energy}J, A={amplitude}, f={frequency}kHz");
                Logger.Log($"[AcousticSimulationForm] Material params: E={youngsModulus}MPa, ν={poissonRatio}");
                Logger.Log($"[AcousticSimulationForm] Material density: {baseDensity} kg/m³");

                simulationRenderer = new VolumeRenderer(
                    densityVolume,
                    width,
                    height,
                    depth,
                    pixelSizeF,
                    materialID,
                    false);

                // Calculate TX and RX positions based on the axis
                int tx, ty, tz, rx, ry, rz;
                switch (axis.ToUpperInvariant())
                {
                    case "X":
                        tx = 0; ty = height / 2; tz = depth / 2;
                        rx = width - 1; ry = height / 2; rz = depth / 2;
                        break;
                    case "Y":
                        tx = width / 2; ty = 0; tz = depth / 2;
                        rx = width / 2; ry = height - 1; rz = depth / 2;
                        break;
                    default:
                        tx = width / 2; ty = height / 2; tz = 0;
                        rx = width / 2; ry = height / 2; rz = depth - 1;
                        break;
                }

                if (rx >= 0 && rx < width && ry >= 0 && ry < height && rz >= 0 && rz < depth)
                {
                    if (labels[rx, ry, rz] != materialID)
                    {
                        Logger.Log($"[AcousticSimulationForm] Warning: Receiver not in selected material! Adjusting position...");

                        // Find a valid position for the receiver that's also distant from transmitter
                        bool found = false;
                        double bestDistance = 0;
                        int bestX = rx, bestY = ry, bestZ = rz;

                        // Try to find a material point that's far from the transmitter
                        for (int z = 0; z < depth && !found; z++)
                        {
                            for (int y = 0; y < height && !found; y++)
                            {
                                for (int x = 0; x < width; x++)
                                {
                                    if (labels[x, y, z] == materialID)
                                    {
                                        // Calculate distance from transmitter
                                        double dist = Math.Sqrt(
                                            Math.Pow(x - tx, 2) +
                                            Math.Pow(y - ty, 2) +
                                            Math.Pow(z - tz, 2));

                                        // Keep track of the point farthest from transmitter
                                        if (dist > bestDistance)
                                        {
                                            bestDistance = dist;
                                            bestX = x;
                                            bestY = y;
                                            bestZ = z;

                                            // If we found a point sufficiently far away, use it
                                            if (dist > Math.Max(width, Math.Max(height, depth)) * 0.5)
                                            {
                                                found = true;
                                                break;
                                            }
                                        }
                                    }
                                }
                            }
                        }

                        // Use the best position found
                        rx = bestX;
                        ry = bestY;
                        rz = bestZ;
                        Logger.Log($"[AcousticSimulationForm] Adjusted RX position to ({rx},{ry},{rz}) at distance {bestDistance:F1}");
                    }
                }

                // Ensure minimum distance between TX and RX
                double txRxDistance = Math.Sqrt(
                    Math.Pow(rx - tx, 2) +
                    Math.Pow(ry - ty, 2) +
                    Math.Pow(rz - tz, 2));

                Logger.Log($"[AcousticSimulationForm] TX-RX Distance: {txRxDistance:F1} pixels");

                if (txRxDistance < 10) // Minimum distance in pixels
                {
                    Logger.Log($"[AcousticSimulationForm] Warning: TX and RX are too close! Simulation may not work properly.");
                    MessageBox.Show("!!!Position Warning!!! The transmitter and receiver positions are too close to each other. This may cause the simulation to fail. !!!Position Warning!!!");
                }

                Logger.Log($"[AcousticSimulationForm] TX: ({tx},{ty},{tz}), RX: ({rx},{ry},{rz})");
                if (energy <= 0.001)
                {
                    Logger.Log("[AcousticSimulationForm] Warning: Energy is very low, increasing to minimum value");
                    numEnergy.Value = 5.0m; // Increased from 1.0 to 5.0
                    energy = 5.0;
                }

                if (amplitude < 100)
                {
                    Logger.Log("[AcousticSimulationForm] Warning: Amplitude is low, increasing to minimum");
                    numAmplitude.Value = 500; // Increased from 100 to 500
                    amplitude = 500;
                }
                // Create the SimulationVisualizer before starting the simulation
                visualizer = new AcousticSimulationVisualizer(
                    width, height, depth, pixelSizeF,
                    tx, ty, tz, rx, ry, rz);

                // Prepare progress UI - CHANGED: Use Owner to set proper ownership
                simulationProgressForm = new CancellableProgressForm(
                    useGPU ? "Running acoustic simulation on GPU..." : "Running acoustic simulation on CPU...");
                simulationProgressForm.Owner = this; // Set the owner to establish parent-child relationship
                simulationProgressForm.CancelPressed += (s, args) =>
                {
                    if (usingGpuSimulator)
                        gpuSimulator?.CancelSimulation();
                    else
                        cpuSimulator?.CancelSimulation();

                    simulationRunning = false;
                    simulationProgressForm.Close();
                };

                // Clean up any existing simulators
                cpuSimulator?.Dispose();
                gpuSimulator?.Dispose();
                cpuSimulator = null;
                gpuSimulator = null;

                // Create appropriate simulator based on GPU checkbox
                if (useGPU)
                {
                    try
                    {
                        // Create GPU simulator wrapper
                        gpuSimulator = new AcousticSimulatorGPUWrapper(
                            width, height, depth, pixelSizeF, labels, densityVolume, materialID,
                            axis, waveType, confiningPressure, tensileStrength, failureAngle, cohesion,
                            energy, frequency, amplitude, timeSteps,
                            useElastic, usePlastic, useBrittle, youngsModulus, poissonRatio);

                        // Wire up events
                        gpuSimulator.ProgressUpdated += Simulator_ProgressUpdated;
                        gpuSimulator.SimulationCompleted += Simulator_SimulationCompleted;

                        // Connect visualizer to the GPU simulator
                        visualizer.ConnectToGpuSimulator(gpuSimulator);

                        usingGpuSimulator = true;
                        simulationProgressForm.UpdateMessage("Initializing GPU simulation...");
                    }
                    catch (Exception gpuEx)
                    {
                        // If GPU initialization fails, fall back to CPU with a warning
                        MessageBox.Show($"Failed to initialize GPU simulation: {gpuEx.Message}\nFalling back to CPU simulation.",
                            "GPU Initialization Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);

                        useGPU = false;
                        usingGpuSimulator = false;

                        // Create CPU simulator instead
                        cpuSimulator = new AcousticSimulator(
                     width, height, depth, pixelSizeF, labels, densityVolume, materialID,
                     axis, waveType, confiningPressure, tensileStrength, failureAngle, cohesion,
                     energy, frequency, amplitude, timeSteps,
                     useElastic, usePlastic, useBrittle, youngsModulus, poissonRatio,
                     tx, ty, tz, rx, ry, rz);

                        // Wire up events
                        cpuSimulator.ProgressUpdated += Simulator_ProgressUpdated;
                        cpuSimulator.SimulationCompleted += Simulator_SimulationCompleted;

                        // Connect visualizer to the CPU simulator
                        visualizer.ConnectToCpuSimulator(cpuSimulator);
                    }
                }
                else
                {
                    // Create CPU simulator
                    cpuSimulator = new AcousticSimulator(
                    width, height, depth, pixelSizeF, labels, densityVolume, materialID,
                    axis, waveType, confiningPressure, tensileStrength, failureAngle, cohesion,
                    energy, frequency, amplitude, timeSteps,
                    useElastic, usePlastic, useBrittle, youngsModulus, poissonRatio,
                    tx, ty, tz, rx, ry, rz);

                    // Wire up events
                    cpuSimulator.ProgressUpdated += Simulator_ProgressUpdated;
                    cpuSimulator.SimulationCompleted += Simulator_SimulationCompleted;

                    // Connect visualizer to the CPU simulator
                    visualizer.ConnectToCpuSimulator(cpuSimulator);

                    usingGpuSimulator = false;
                }

                simulator = usingGpuSimulator ? (dynamic)gpuSimulator : (dynamic)cpuSimulator;

                // Start the appropriate simulator
                simulationRunning = true;
                Task.Run(() =>
                {
                    if (usingGpuSimulator)
                        gpuSimulator.StartSimulation();
                    else
                        cpuSimulator.StartSimulation();
                });

                // CHANGED: Show the form as non-modal instead of modal
                // This allows interaction with other forms while the simulation is running
                simulationProgressForm.Show(this); // Pass owner as parameter
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error starting simulation: {ex.Message}",
                    "Simulation Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnStartSimulation.Enabled = true;
            }
        }

        private void Simulator_ProgressUpdated(object sender, AcousticSimulationProgressEventArgs e)
        {
            // This might be called from a different thread, so we need to use Invoke
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => Simulator_ProgressUpdated(sender, e)));
                return;
            }

            // Update the progress form
            if (simulationProgressForm != null && !simulationProgressForm.IsDisposed)
            {
                simulationProgressForm.UpdateProgress(e.ProgressPercent);
                simulationProgressForm.UpdateMessage(e.StatusText);
            }

            // Check if we have wave field data directly from the event
            float[,,] pField = e.PWaveField;
            float[,,] sField = e.SWaveField;

            // If not provided, get it from the snapshot
            if (pField == null || sField == null)
            {
                var snapshot = GetWaveFieldSnapshot();
                pField = ConvertToFloatArray(snapshot.vx);
                sField = ConvertToFloatArray(snapshot.vy);
            }

            // Update the visualization with current wave fields
            UpdateWaveFieldVisualization(pField, sField);
        }
        private float[,,] ConvertToFloatArray(double[,,] array)
        {
            int width = array.GetLength(0);
            int height = array.GetLength(1);
            int depth = array.GetLength(2);

            float[,,] result = new float[width, height, depth];

            for (int z = 0; z < depth; z++)
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                        result[x, y, z] = (float)array[x, y, z];

            return result;
        }
        private void InjectTestWave()
        {
            // This method injects a test signal to verify visualization works
            // Only call this if you need to debug the visualization without relying on simulators
            Logger.Log("[AcousticSimulationForm] Injecting test wave pattern for debugging");

            int width = mainForm.GetWidth();
            int height = mainForm.GetHeight();
            int depth = mainForm.GetDepth();

            // Create a simple spherical wave pattern
            int centerX = width / 2;
            int centerY = height / 2;
            int centerZ = depth / 2;
            double radius = Math.Min(Math.Min(width, height), depth) / 4.0;

            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        if (densityVolume[x, y, z] <= 0f)
                            continue;

                        // Calculate distance from center
                        double dx = x - centerX;
                        double dy = y - centerY;
                        double dz = z - centerZ;
                        double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);

                        // Create a ripple pattern
                        if (dist <= radius)
                        {
                            double value = Math.Sin(dist / radius * Math.PI) * Math.Exp(-dist / radius) * 0.0001;

                            // Call the wave field update methods with test values
                            if (usingGpuSimulator && gpuSimulator != null)
                            {
                                var simSnapshot = gpuSimulator.GetWaveFieldSnapshot();
                                var vx = simSnapshot.vx;
                                var vy = simSnapshot.vy;
                                var vz = simSnapshot.vz;

                                vx[x, y, z] = value;
                                vy[x, y, z] = value * 0.5; // Different pattern for S-wave
                                vz[x, y, z] = value * 0.5;
                            }
                            else if (cpuSimulator != null)
                            {
                                var simSnapshot = cpuSimulator.GetWaveFieldSnapshot();
                                var vx = simSnapshot.vx;
                                var vy = simSnapshot.vy;
                                var vz = simSnapshot.vz;

                                vx[x, y, z] = value;
                                vy[x, y, z] = value * 0.5; // Different pattern for S-wave
                                vz[x, y, z] = value * 0.5;
                            }
                        }
                    }
                }
            }

            // Force redraw
            pictureBoxSimulation.Invalidate();
        }
        private void Simulator_SimulationCompleted(object sender, AcousticSimulationCompleteEventArgs e)
        {
            // This might be called from a different thread, so we need to use Invoke
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => Simulator_SimulationCompleted(sender, e)));
                return;
            }

            // Close the progress form
            if (simulationProgressForm != null && !simulationProgressForm.IsDisposed)
            {
                simulationProgressForm.Close();
            }

            // Update simulation results
            simulationRunning = false;

            // Store the results
            simulationResults = new SimulationResults
            {
                PWaveVelocity = e.PWaveVelocity,
                SWaveVelocity = e.SWaveVelocity,
                VpVsRatio = e.VpVsRatio,
                PWaveTravelTime = e.PWaveTravelTime,
                SWaveTravelTime = e.SWaveTravelTime
            };

            // Display results
            DisplaySimulationResults();

            // Switch to the results tab
            tabControl.SelectedTab = tabResults;
        }


        private void UpdateWaveFieldVisualization(float[,,] pWaveField, float[,,] sWaveField)
        {
            // This method updates the visualization with the current wave fields
            // Just invalidate the picturebox to trigger a redraw with the updated wave fields
            pictureBoxSimulation.Invalidate();
        }

        private void DisplaySimulationResults()
        {
            // Create a formatted results message
            MessageBox.Show(
                $"Simulation completed!\n\n" +
                $"P-Wave Velocity: {simulationResults.PWaveVelocity:F1} m/s\n" +
                $"S-Wave Velocity: {simulationResults.SWaveVelocity:F1} m/s\n" +
                $"Vp/Vs Ratio: {simulationResults.VpVsRatio:F2}\n\n" +
                $"P-Wave Travel Time: {simulationResults.PWaveTravelTime} steps\n" +
                $"S-Wave Travel Time: {simulationResults.SWaveTravelTime} steps",
                "Simulation Results", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        public (double[,,] vx, double[,,] vy, double[,,] vz) GetWaveFieldSnapshot()
        {
            if (usingGpuSimulator && gpuSimulator != null)
                return gpuSimulator.GetWaveFieldSnapshot();
            else if (cpuSimulator != null)
                return cpuSimulator.GetWaveFieldSnapshot();
            else
                return (new double[0, 0, 0], new double[0, 0, 0], new double[0, 0, 0]);
        }
        private void DrawWavePropagation(Graphics g, VolumeRenderer renderer)
        {
            if (!simulationRunning || simulator == null)
                return;

            // grab the latest wavefields
            var snapshot = GetWaveFieldSnapshot();

            // Check for invalid snapshot
            if (snapshot.vx == null || snapshot.vx.GetLength(0) == 0 ||
                snapshot.vy == null || snapshot.vy.GetLength(0) == 0 ||
                snapshot.vz == null || snapshot.vz.GetLength(0) == 0)
            {
                g.DrawString("No valid wave data", new Font("Arial", 12), Brushes.Red, 10, 10);
                return;
            }

            var pWaveField = snapshot.vx;
            var sWaveField = snapshot.vy;
            var zField = snapshot.vz; // Also visualize Z component

            int width = mainForm.GetWidth();
            int height = mainForm.GetHeight();
            int depth = mainForm.GetDepth();

            // reduce point density for performance/clarity
            int sampleRate = Math.Max(1, Math.Min(Math.Min(width, height), depth) / 40);

            // EXTREME AMPLIFICATION - Dramatically increased
            const double WAVE_VIS_AMPLIFICATION = 100000000.0;

            // ULTRA-LOW THRESHOLD - catch virtually any signal
            const double WAVE_VISIBILITY_THRESHOLD = 0.000000001;

            // Find maximum values for adaptive scaling
            double maxVal = 0.0;
            Parallel.For(0, depth, z => {
                for (int y = 0; y < height; y += sampleRate)
                    for (int x = 0; x < width; x += sampleRate)
                    {
                        if (mainForm.volumeLabels[x, y, z] != selectedMaterialID)
                            continue;

                        double pAmp = Math.Abs(pWaveField[x, y, z]);
                        double sAmp = Math.Abs(sWaveField[x, y, z]);
                        double zAmp = Math.Abs(zField[x, y, z]);
                        double combined = Math.Sqrt(pAmp * pAmp + sAmp * sAmp + zAmp * zAmp);

                        // Thread-safe max update
                        double current = maxVal;
                        if (combined > current)
                            Interlocked.CompareExchange(ref maxVal, combined, current);
                    }
            });

            // Apply adaptive scaling if we have non-zero signals
            double adaptiveAmp = WAVE_VIS_AMPLIFICATION;
            if (maxVal > 1e-12)
            {
                adaptiveAmp = Math.Max(WAVE_VIS_AMPLIFICATION, 0.1 / maxVal);
                Logger.Log($"[DrawWavePropagation] Using adaptive amplification: {adaptiveAmp:E2}");
            }

            // Use more visible colors with higher opacity
            using (var pBrush = new SolidBrush(Color.FromArgb(230, 255, 50, 50)))  // bright red
            using (var sBrush = new SolidBrush(Color.FromArgb(230, 50, 50, 255)))  // bright blue
            {
                double maxPAmp = 0.0; // For logging
                double maxSAmp = 0.0; // For logging
                double maxZAmp = 0.0; // For logging
                int pointsDrawn = 0;  // For logging

                for (int z = 0; z < depth; z += sampleRate)
                    for (int y = 0; y < height; y += sampleRate)
                        for (int x = 0; x < width; x += sampleRate)
                        {
                            // Only visualize points in the material
                            if (mainForm.volumeLabels[x, y, z] != selectedMaterialID)
                                continue;

                            double pAmp = Math.Abs(pWaveField[x, y, z]) * adaptiveAmp;
                            double sAmp = Math.Abs(sWaveField[x, y, z]) * adaptiveAmp;
                            double zAmp = Math.Abs(zField[x, y, z]) * adaptiveAmp;

                            maxPAmp = Math.Max(maxPAmp, pAmp);
                            maxSAmp = Math.Max(maxSAmp, sAmp);
                            maxZAmp = Math.Max(maxZAmp, zAmp);

                            // Use combined amplitude for visualization threshold
                            double combinedAmp = Math.Sqrt(pAmp * pAmp + sAmp * sAmp + zAmp * zAmp);

                            if (combinedAmp > WAVE_VISIBILITY_THRESHOLD)
                            {
                                // project to screen and draw a dot
                                var pt = renderer.ProjectToScreen(
                                    x, y, z,
                                    pictureBoxSimulation.Width,
                                    pictureBoxSimulation.Height);

                                // Determine color based on dominant component
                                Brush dotBrush;
                                if (pAmp > sAmp && pAmp > zAmp)
                                    dotBrush = pBrush;
                                else
                                    dotBrush = sBrush;

                                // Ensure minimum dot size but scale with amplitude
                                int size = Math.Min(14, Math.Max(5, (int)(combinedAmp * 0.5)));
                                g.FillEllipse(dotBrush,
                                    pt.X - size / 2, pt.Y - size / 2,
                                    size, size);
                                pointsDrawn++;
                            }
                        }

                // Log the maximum amplitudes found to help with debugging
                Logger.Log($"[DrawWavePropagation] Max amplitudes: P={maxPAmp:E3}, S={maxSAmp:E3}, Z={maxZAmp:E3}, Points drawn: {pointsDrawn}");
            }

            // Draw informational overlay
            using (Font font = new Font("Arial", 9))
            using (SolidBrush brush = new SolidBrush(Color.Yellow))
            using (SolidBrush shadowBrush = new SolidBrush(Color.FromArgb(100, 0, 0, 0)))
            {
                string info = "Wave visualization active...";
                g.FillRectangle(shadowBrush, 10, 10, 200, 20);
                g.DrawString(info, font, brush, 15, 12);
            }
        }

        private void DrawSimulationResults(Graphics g)
        {
            // Draw the simulation results at the bottom of the screen
            using (Font font = new Font("Segoe UI", 9, FontStyle.Bold))
            using (SolidBrush brush = new SolidBrush(Color.White))
            using (SolidBrush highlightBrush = new SolidBrush(Color.LightGreen))
            {
                int y = pictureBoxSimulation.Height - 120;
                int lineHeight = 20;

                // Draw a semi-transparent background for better readability
                using (SolidBrush backBrush = new SolidBrush(Color.FromArgb(150, 30, 30, 30)))
                {
                    g.FillRectangle(backBrush, 10, y - 5, 300, 110);
                }

                // Results title
                g.DrawString("Simulation Results:", font, highlightBrush, 10, y);
                y += lineHeight;

                // P-Wave Velocity
                g.DrawString($"P-Wave Velocity: {simulationResults.PWaveVelocity:F1} m/s", font, brush, 20, y);
                y += lineHeight;

                // S-Wave Velocity
                g.DrawString($"S-Wave Velocity: {simulationResults.SWaveVelocity:F1} m/s", font, brush, 20, y);
                y += lineHeight;

                // Vp/Vs Ratio (highlighted as it's the main result)
                g.DrawString($"Vp/Vs Ratio: {simulationResults.VpVsRatio:F2}", font, highlightBrush, 20, y);
                y += lineHeight;

                // Travel times
                g.DrawString($"P-Wave Travel Time: {simulationResults.PWaveTravelTime} steps", font, brush, 20, y);
                y += lineHeight;

                g.DrawString($"S-Wave Travel Time: {simulationResults.SWaveTravelTime} steps", font, brush, 20, y);
            }
        }
        private bool simulationRenderedOnce = false;
        private void PictureBoxSimulation_Paint(object sender, PaintEventArgs e)
        {
            // Skip any first, phantom paint while initializing
            if (isInitializing && simulationRenderedOnce)
                return;
            simulationRenderedOnce = true;

            // Clear the background
            e.Graphics.Clear(Color.FromArgb(20, 20, 20));

            if (simulationRenderer != null)
            {
                // Sync the camera transform every frame
                simulationRenderer.SetTransformation(simRotationX, simRotationY, simZoom, simPan);

                if (simulationRunning && simulator != null)
                {
                    // Simulation is running → draw JUST the waves
                    DrawWavePropagation(e.Graphics, simulationRenderer);
                }
                else
                {
                    // Not running → draw the static, density-colored mesh
                    simulationRenderer.Render(
                        e.Graphics,
                        pictureBoxSimulation.Width,
                        pictureBoxSimulation.Height
                    );

                    // If we have final results, overlay them
                    if (simulationResults != null)
                    {
                        DrawSimulationResults(e.Graphics);
                    }
                }

                // Always draw the transducers + info on top
                DrawTransducers(e.Graphics, simulationRenderer);
                DrawSimulationInfo(e.Graphics);
            }
            else
            {
                // Fallback: should never happen now
                using (Font font = new Font("Segoe UI", 12, FontStyle.Regular))
                using (SolidBrush brush = new SolidBrush(Color.White))
                {
                    string msg = "Simulation not initialized.";
                    SizeF sz = e.Graphics.MeasureString(msg, font);
                    e.Graphics.DrawString(
                        msg,
                        font,
                        brush,
                        (pictureBoxSimulation.Width - sz.Width) / 2f,
                        (pictureBoxSimulation.Height - sz.Height) / 2f
                    );
                }
            }
        }


        private void DrawTransducers(Graphics g, VolumeRenderer renderer)
        {
            if (renderer == null) return;

            // Get the selected axis
            string axis = comboAxis.SelectedItem.ToString();

            // Get the dimensions of the volume
            int width = mainForm.GetWidth();
            int height = mainForm.GetHeight();
            int depth = mainForm.GetDepth();

            // Calculate positions for transmitter and receiver
            Point3D transmitter = new Point3D(0, 0, 0);
            Point3D receiver = new Point3D(0, 0, 0);

            // Position them at the center point of the faces in the selected axis
            switch (axis)
            {
                case "X":
                    transmitter = new Point3D(0, height / 2, depth / 2);
                    receiver = new Point3D(width - 1, height / 2, depth / 2);
                    break;
                case "Y":
                    transmitter = new Point3D(width / 2, 0, depth / 2);
                    receiver = new Point3D(width / 2, height - 1, depth / 2);
                    break;
                case "Z":
                    transmitter = new Point3D(width / 2, height / 2, 0);
                    receiver = new Point3D(width / 2, height / 2, depth - 1);
                    break;
            }

            // Convert to screen coordinates - use the renderer's transformation
            PointF transmitterScreen = renderer.ProjectToScreen(
                transmitter.X, transmitter.Y, transmitter.Z,
                pictureBoxSimulation.Width, pictureBoxSimulation.Height);

            PointF receiverScreen = renderer.ProjectToScreen(
                receiver.X, receiver.Y, receiver.Z,
                pictureBoxSimulation.Width, pictureBoxSimulation.Height);

            // Draw transmitter (yellow)
            using (SolidBrush brush = new SolidBrush(Color.Yellow))
            {
                g.FillEllipse(brush, transmitterScreen.X - 8, transmitterScreen.Y - 8, 16, 16);

                // Add a border for better visibility
                using (Pen pen = new Pen(Color.Black, 1))
                {
                    g.DrawEllipse(pen, transmitterScreen.X - 8, transmitterScreen.Y - 8, 16, 16);
                }
            }

            // Draw receiver (green)
            using (SolidBrush brush = new SolidBrush(Color.Green))
            {
                g.FillEllipse(brush, receiverScreen.X - 8, receiverScreen.Y - 8, 16, 16);

                // Add a border for better visibility
                using (Pen pen = new Pen(Color.Black, 1))
                {
                    g.DrawEllipse(pen, receiverScreen.X - 8, receiverScreen.Y - 8, 16, 16);
                }
            }

            // Draw a line connecting them
            using (Pen pen = new Pen(Color.White, 1.5f))
            {
                pen.DashStyle = DashStyle.Dash;
                g.DrawLine(pen, transmitterScreen, receiverScreen);
            }

            // Add labels to identify transmitter and receiver
            using (Font font = new Font("Segoe UI", 9, FontStyle.Bold))
            {
                using (SolidBrush brush = new SolidBrush(Color.White))
                {
                    g.DrawString("T", font, brush, transmitterScreen.X - 4, transmitterScreen.Y - 22);
                    g.DrawString("R", font, brush, receiverScreen.X - 4, receiverScreen.Y - 22);
                }
            }
        }

        private void DrawSimulationInfo(Graphics g)
        {
            // Draw information about the current simulation settings
            using (Font font = new Font("Segoe UI", 9))
            using (SolidBrush brush = new SolidBrush(Color.White))
            {
                int y = 30;
                int lineHeight = 18;

                // Draw material and axis info
                g.DrawString($"Material: {comboMaterialLibrary.SelectedItem}", font, brush, 10, y);
                y += lineHeight;

                g.DrawString($"Axis: {comboAxis.SelectedItem}", font, brush, 10, y);
                y += lineHeight;

                g.DrawString($"Wave Type: {comboWaveType.SelectedItem}", font, brush, 10, y);
                y += lineHeight;

                // Add model information
                string models = "Models: ";
                if (chkElasticModel.Checked) models += "Elastic ";
                if (chkPlasticModel.Checked) models += "Plastic ";
                if (chkBrittleModel.Checked) models += "Brittle";

                g.DrawString(models, font, brush, 10, y);
            }
        }

        private Point WorldToScreen(Point3D worldPos)
        {
            // A simplified projection method
            // In a real implementation, this would use the same transformation as the volume renderer

            // Get the size of the picture box
            int width = pictureBoxSimulation.Width;
            int height = pictureBoxSimulation.Height;

            // Apply rotation
            float x = worldPos.X;
            float y = worldPos.Y;
            float z = worldPos.Z;

            // Convert to radians
            float rotX = rotationX * (float)Math.PI / 180;
            float rotY = rotationY * (float)Math.PI / 180;

            // Apply X rotation
            float y1 = y * (float)Math.Cos(rotX) - z * (float)Math.Sin(rotX);
            float z1 = y * (float)Math.Sin(rotX) + z * (float)Math.Cos(rotX);

            // Apply Y rotation
            float x2 = x * (float)Math.Cos(rotY) + z1 * (float)Math.Sin(rotY);
            float z2 = -x * (float)Math.Sin(rotY) + z1 * (float)Math.Cos(rotY);

            // Apply zoom and pan
            float screenX = x2 * zoom + pan.X + width / 2;
            float screenY = y1 * zoom + pan.Y + height / 2;

            return new Point((int)screenX, (int)screenY);
        }

        private Image CreateCalculateIcon()
        {
            Bitmap bmp = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                // Draw a calculator-like icon
                Rectangle rect = new Rectangle(1, 1, 14, 14);
                using (Pen pen = new Pen(Color.White, 1))
                {
                    g.DrawRectangle(pen, rect);

                    // Draw calculation symbol
                    g.DrawLine(pen, 4, 8, 12, 8);  // Horizontal line
                    g.DrawLine(pen, 8, 4, 8, 12);  // Vertical line
                }
            }
            return bmp;
        }
        private class Point3D
        {
            public float X { get; set; }
            public float Y { get; set; }
            public float Z { get; set; }

            public Point3D(float x, float y, float z)
            {
                X = x;
                Y = y;
                Z = z;
            }

            public Point3D(Point3D other)
            {
                X = other.X;
                Y = other.Y;
                Z = other.Z;
            }

            public Point3D(int x, int y, int z)
            {
                X = x;
                Y = y;
                Z = z;
            }
        }
        private Image CreateStartSimulationIcon()
        {
            Bitmap bmp = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                // Draw a play button
                Point[] trianglePoints = new Point[]
                {
            new Point(3, 2),
            new Point(3, 14),
            new Point(14, 8)
                };

                using (SolidBrush brush = new SolidBrush(Color.LightGreen))
                {
                    g.FillPolygon(brush, trianglePoints);
                }

                using (Pen pen = new Pen(Color.White, 1))
                {
                    g.DrawPolygon(pen, trianglePoints);
                }
            }
            return bmp;
        }

        private void InitializeMaterialLibrary()
        {
            // Clear existing items
            materialLibrary.Clear();
            comboMaterialLibrary.Items.Clear();

            // Add various materials with predefined properties

            // Limestone
            materialLibrary["Limestone"] = new MaterialProperties
            {
                ConfiningPressure = 5.0,
                TensileStrength = 10.0,
                FailureAngle = 30.0,
                Cohesion = 20.0,
                Energy = 2.0,
                Frequency = 200.0,
                Amplitude = 500,
                TimeSteps = 200,
                YoungsModulus = 50000.0,
                PoissonRatio = 0.25
            };

            // Sandstone
            materialLibrary["Sandstone"] = new MaterialProperties
            {
                ConfiningPressure = 3.0,
                TensileStrength = 5.0,
                FailureAngle = 28.0,
                Cohesion = 10.0,
                Energy = 1.5,
                Frequency = 180.0,
                Amplitude = 450,
                TimeSteps = 180,
                YoungsModulus = 30000.0,
                PoissonRatio = 0.20
            };

            // Granite
            materialLibrary["Granite"] = new MaterialProperties
            {
                ConfiningPressure = 10.0,
                TensileStrength = 15.0,
                FailureAngle = 35.0,
                Cohesion = 25.0,
                Energy = 3.0,
                Frequency = 250.0,
                Amplitude = 600,
                TimeSteps = 220,
                YoungsModulus = 50000.0,
                PoissonRatio = 0.25
            };

            // Marble
            materialLibrary["Marble"] = new MaterialProperties
            {
                ConfiningPressure = 7.0,
                TensileStrength = 12.0,
                FailureAngle = 32.0,
                Cohesion = 22.0,
                Energy = 2.5,
                Frequency = 220.0,
                Amplitude = 550,
                TimeSteps = 210,
                YoungsModulus = 50000.0,
                PoissonRatio = 0.28
            };

            // Basalt
            materialLibrary["Basalt"] = new MaterialProperties
            {
                ConfiningPressure = 12.0,
                TensileStrength = 18.0,
                FailureAngle = 38.0,
                Cohesion = 30.0,
                Energy = 3.5,
                Frequency = 280.0,
                Amplitude = 650,
                TimeSteps = 240,
                YoungsModulus = 100000.0,
                PoissonRatio = 0.25
            };

            // Shale
            materialLibrary["Shale"] = new MaterialProperties
            {
                ConfiningPressure = 2.0,
                TensileStrength = 3.0,
                FailureAngle = 25.0,
                Cohesion = 8.0,
                Energy = 1.0,
                Frequency = 150.0,
                Amplitude = 400,
                TimeSteps = 160,
                YoungsModulus = 10000.0,
                PoissonRatio = 0.30
            };

            // Concrete
            materialLibrary["Concrete"] = new MaterialProperties
            {
                ConfiningPressure = 8.0,
                TensileStrength = 4.0,
                FailureAngle = 30.0,
                Cohesion = 15.0,
                Energy = 2.0,
                Frequency = 200.0,
                Amplitude = 500,
                TimeSteps = 200,
                YoungsModulus = 30000.0,
                PoissonRatio = 0.20
            };

            // Brick
            materialLibrary["Brick"] = new MaterialProperties
            {
                ConfiningPressure = 4.0,
                TensileStrength = 3.0,
                FailureAngle = 28.0,
                Cohesion = 12.0,
                Energy = 1.5,
                Frequency = 180.0,
                Amplitude = 450,
                TimeSteps = 180,
                YoungsModulus = 20000.0,
                PoissonRatio = 0.15
            };

            // Glass
            materialLibrary["Glass"] = new MaterialProperties
            {
                ConfiningPressure = 1.0,
                TensileStrength = 50.0,
                FailureAngle = 45.0,
                Cohesion = 30.0,
                Energy = 1.0,
                Frequency = 300.0,
                Amplitude = 300,
                TimeSteps = 150,
                YoungsModulus = 70000.0,
                PoissonRatio = 0.22
            };

            // Steel
            materialLibrary["Steel"] = new MaterialProperties
            {
                ConfiningPressure = 50.0,
                TensileStrength = 500.0,
                FailureAngle = 45.0,
                Cohesion = 200.0,
                Energy = 5.0,
                Frequency = 400.0,
                Amplitude = 800,
                TimeSteps = 300,
                YoungsModulus = 200000.0,
                PoissonRatio = 0.30
            };

            // Aluminum
            materialLibrary["Aluminum"] = new MaterialProperties
            {
                ConfiningPressure = 30.0,
                TensileStrength = 300.0,
                FailureAngle = 45.0,
                Cohesion = 150.0,
                Energy = 4.0,
                Frequency = 350.0,
                Amplitude = 700,
                TimeSteps = 280,
                YoungsModulus = 70000.0,
                PoissonRatio = 0.33
            };

            // Copper
            materialLibrary["Copper"] = new MaterialProperties
            {
                ConfiningPressure = 40.0,
                TensileStrength = 400.0,
                FailureAngle = 45.0,
                Cohesion = 180.0,
                Energy = 4.5,
                Frequency = 380.0,
                Amplitude = 750,
                TimeSteps = 290,
                YoungsModulus = 110000.0,
                PoissonRatio = 0.34
            };

            // Plastic (Generic)
            materialLibrary["Plastic"] = new MaterialProperties
            {
                ConfiningPressure = 2.0,
                TensileStrength = 60.0,
                FailureAngle = 30.0,
                Cohesion = 40.0,
                Energy = 1.0,
                Frequency = 150.0,
                Amplitude = 400,
                TimeSteps = 160,
                YoungsModulus = 3000.0,
                PoissonRatio = 0.35
            };

            // Wood (Generic)
            materialLibrary["Wood"] = new MaterialProperties
            {
                ConfiningPressure = 1.5,
                TensileStrength = 80.0,
                FailureAngle = 30.0,
                Cohesion = 10.0,
                Energy = 1.2,
                Frequency = 120.0,
                Amplitude = 350,
                TimeSteps = 140,
                YoungsModulus = 10000.0,
                PoissonRatio = 0.30
            };
            // Gabbro: E≈80 GPa, ν≈0.26
            materialLibrary["Gabbro"] = new MaterialProperties
            {
                ConfiningPressure = 15.0,
                TensileStrength = 20.0,
                FailureAngle = 36.0,
                Cohesion = 35.0,
                Energy = 3.0,
                Frequency = 300.0,
                Amplitude = 600,
                TimeSteps = 230,
                YoungsModulus = 80000.0,
                PoissonRatio = 0.26
            };

            // Serpentinite: E≈50 GPa, ν≈0.27
            materialLibrary["Serpentinite"] = new MaterialProperties
            {
                ConfiningPressure = 8.0,
                TensileStrength = 12.0,
                FailureAngle = 32.0,
                Cohesion = 18.0,
                Energy = 2.5,
                Frequency = 220.0,
                Amplitude = 500,
                TimeSteps = 200,
                YoungsModulus = 50000.0,
                PoissonRatio = 0.27
            };

            // Peridotite: E≈100 GPa, ν≈0.28
            materialLibrary["Peridotite"] = new MaterialProperties
            {
                ConfiningPressure = 20.0,
                TensileStrength = 25.0,
                FailureAngle = 40.0,
                Cohesion = 40.0,
                Energy = 4.0,
                Frequency = 350.0,
                Amplitude = 700,
                TimeSteps = 240,
                YoungsModulus = 100000.0,
                PoissonRatio = 0.28
            };

            // Add materials to combobox
            foreach (string materialName in materialLibrary.Keys)
            {
                comboMaterialLibrary.Items.Add(materialName);
            }

            // Select first material by default
            if (comboMaterialLibrary.Items.Count > 0)
            {
                comboMaterialLibrary.SelectedIndex = 0;
            }
        }


        // Custom tab drawing for dark theme
        private void TabControl_DrawItem(object sender, DrawItemEventArgs e)
        {
            TabControl tabControl = (TabControl)sender;
            TabPage tabPage = tabControl.TabPages[e.Index];
            Rectangle tabBounds = tabControl.GetTabRect(e.Index);

            // Create a custom brush based on whether the tab is selected
            Color textColor = e.State == DrawItemState.Selected ? Color.White : Color.LightGray;
            Color backColor = e.State == DrawItemState.Selected ? Color.FromArgb(70, 70, 75) : Color.FromArgb(45, 45, 48);

            using (SolidBrush backBrush = new SolidBrush(backColor))
            using (SolidBrush textBrush = new SolidBrush(textColor))
            {
                // Draw tab background
                e.Graphics.FillRectangle(backBrush, tabBounds);

                // Draw tab text
                StringFormat sf = new StringFormat();
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;

                e.Graphics.DrawString(tabPage.Text, e.Font, textBrush, tabBounds, sf);
            }
        }

        // Custom renderer for dark themed toolbar
        private class DarkToolStripRenderer : ToolStripProfessionalRenderer
        {
            public DarkToolStripRenderer() : base(new DarkColorTable()) { }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                // No border
            }
        }

        private class DarkColorTable : ProfessionalColorTable
        {
            public override Color ToolStripGradientBegin => Color.FromArgb(30, 30, 30);
            public override Color ToolStripGradientMiddle => Color.FromArgb(30, 30, 30);
            public override Color ToolStripGradientEnd => Color.FromArgb(30, 30, 30);
            public override Color ButtonSelectedHighlight => Color.FromArgb(50, 50, 50);
            public override Color ButtonSelectedHighlightBorder => Color.FromArgb(80, 80, 80);
            public override Color ButtonPressedHighlight => Color.FromArgb(70, 70, 70);
            public override Color ButtonPressedHighlightBorder => Color.FromArgb(100, 100, 100);
            public override Color ButtonSelectedBorder => Color.FromArgb(80, 80, 80);
            public override Color ButtonCheckedHighlight => Color.FromArgb(70, 70, 70);
            public override Color ButtonCheckedHighlightBorder => Color.FromArgb(100, 100, 100);
            public override Color ButtonPressedBorder => Color.FromArgb(100, 100, 100);
            public override Color ButtonSelectedGradientBegin => Color.FromArgb(50, 50, 50);
            public override Color ButtonSelectedGradientMiddle => Color.FromArgb(50, 50, 50);
            public override Color ButtonSelectedGradientEnd => Color.FromArgb(50, 50, 50);
            public override Color ButtonPressedGradientBegin => Color.FromArgb(70, 70, 70);
            public override Color ButtonPressedGradientMiddle => Color.FromArgb(70, 70, 70);
            public override Color ButtonPressedGradientEnd => Color.FromArgb(70, 70, 70);
            public override Color CheckBackground => Color.FromArgb(70, 70, 70);
            public override Color CheckSelectedBackground => Color.FromArgb(100, 100, 100);
            public override Color CheckPressedBackground => Color.FromArgb(120, 120, 120);
            public override Color GripDark => Color.FromArgb(60, 60, 60);
            public override Color GripLight => Color.FromArgb(80, 80, 80);
            public override Color SeparatorDark => Color.FromArgb(60, 60, 60);
            public override Color SeparatorLight => Color.FromArgb(80, 80, 80);
            public override Color ToolStripDropDownBackground => Color.FromArgb(40, 40, 40);
            public override Color MenuBorder => Color.FromArgb(80, 80, 80);
            public override Color MenuItemBorder => Color.FromArgb(80, 80, 80);
            public override Color MenuItemSelected => Color.FromArgb(70, 70, 70);
            public override Color MenuStripGradientBegin => Color.FromArgb(30, 30, 30);
            public override Color MenuStripGradientEnd => Color.FromArgb(30, 30, 30);
            public override Color MenuItemSelectedGradientBegin => Color.FromArgb(70, 70, 70);
            public override Color MenuItemSelectedGradientEnd => Color.FromArgb(70, 70, 70);
            public override Color MenuItemPressedGradientBegin => Color.FromArgb(90, 90, 90);
            public override Color MenuItemPressedGradientMiddle => Color.FromArgb(90, 90, 90);
            public override Color MenuItemPressedGradientEnd => Color.FromArgb(90, 90, 90);
        }

        // Helper method to create a simple icon for toolbar buttons
        private Image CreateSimpleIcon(int size, Color color)
        {
            Bitmap bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                using (SolidBrush brush = new SolidBrush(color))
                {
                    g.FillRectangle(brush, 2, 2, size - 4, size - 4);
                }
            }
            return bmp;
        }

        private void InitializeVolumeTab()
        {
            // Create panel for controls
            KryptonPanel controlPanel = new KryptonPanel();
            controlPanel.Dock = DockStyle.Left;
            controlPanel.Width = 250;
            controlPanel.StateCommon.Color1 = Color.FromArgb(45, 45, 48); // Dark background
            controlPanel.StateCommon.Color2 = Color.FromArgb(45, 45, 48); // Dark background

            // Material selector
            KryptonLabel lblMaterial = new KryptonLabel();
            lblMaterial.Text = "Material:";
            lblMaterial.Location = new Point(10, 20);
            lblMaterial.StateCommon.ShortText.Color1 = Color.White;

            comboMaterials = new KryptonComboBox();
            comboMaterials.Location = new Point(10, 45);
            comboMaterials.Width = 230;
            comboMaterials.DropDownStyle = ComboBoxStyle.DropDownList;
            comboMaterials.SelectedIndexChanged += comboMaterials_SelectedIndexChanged;

            // Density info label
            lblDensityInfo = new KryptonLabel();
            lblDensityInfo.Text = "Current Density: Not set";
            lblDensityInfo.Location = new Point(10, 80);
            lblDensityInfo.Width = 230;
            lblDensityInfo.StateCommon.ShortText.Color1 = Color.White;

            // Density setting button
            btnSetDensity = new KryptonButton();
            btnSetDensity.Text = "Set Material Density";
            btnSetDensity.Location = new Point(10, 110);
            btnSetDensity.Width = 230;
            btnSetDensity.Values.Image = CreateDensityIcon();
            btnSetDensity.Click += BtnSetDensity_Click;

            // Apply variation button
            btnApplyVariation = new KryptonButton();
            btnApplyVariation.Text = "Apply Density Variation";
            btnApplyVariation.Location = new Point(10, 150);
            btnApplyVariation.Width = 230;
            btnApplyVariation.Values.Image = CreateVariationIcon();
            btnApplyVariation.Enabled = false; // Disabled until density is set
            btnApplyVariation.Click += BtnApplyVariation_Click;

            // Full Rendering
            chkFullVolumeRendering = new KryptonCheckBox();
            chkFullVolumeRendering.Text = "Full Volume Rendering";
            chkFullVolumeRendering.Location = new Point(10, 190);
            chkFullVolumeRendering.Width = 230;
            chkFullVolumeRendering.Checked = useFullVolumeRendering;
            chkFullVolumeRendering.CheckedChanged += ChkFullVolumeRendering_CheckedChanged;
            controlPanel.Controls.Add(chkFullVolumeRendering);

            // Add controls to panel
            controlPanel.Controls.Add(lblMaterial);
            controlPanel.Controls.Add(comboMaterials);
            controlPanel.Controls.Add(lblDensityInfo);
            controlPanel.Controls.Add(btnSetDensity);
            controlPanel.Controls.Add(btnApplyVariation);

            // Create visualization panel
            KryptonPanel visualPanel = new KryptonPanel();
            visualPanel.Dock = DockStyle.Fill;
            visualPanel.StateCommon.Color1 = Color.FromArgb(30, 30, 30); // Dark background
            visualPanel.StateCommon.Color2 = Color.FromArgb(30, 30, 30); // Dark background

            // Create picture box for volume visualization
            pictureBoxVolume = new PictureBox();
            pictureBoxVolume.Dock = DockStyle.Fill;
            pictureBoxVolume.BackColor = Color.FromArgb(20, 20, 20); // Even darker background
            pictureBoxVolume.SizeMode = PictureBoxSizeMode.Normal;

            // Add mouse events for rotation, panning, zooming
            pictureBoxVolume.MouseDown += PictureBoxVolume_MouseDown;
            pictureBoxVolume.MouseMove += PictureBoxVolume_MouseMove;
            pictureBoxVolume.MouseUp += PictureBoxVolume_MouseUp;
            pictureBoxVolume.MouseWheel += PictureBoxVolume_MouseWheel;
            pictureBoxVolume.Paint += PictureBoxVolume_Paint;

            // Add picture box to panel
            visualPanel.Controls.Add(pictureBoxVolume);

            // Add panels to tab page
            tabVolume.Controls.Add(visualPanel);
            tabVolume.Controls.Add(controlPanel);
        }
        private void ChkFullVolumeRendering_CheckedChanged(object sender, EventArgs e)
        {
            useFullVolumeRendering = chkFullVolumeRendering.Checked;
            Logger.Log($"[AcousticSimulationForm] Rendering mode changed to: {(useFullVolumeRendering ? "Full Volume" : "Boundary Only")}");

            // Re-initialize the volume with the new rendering mode
            InitializeHomogeneousDensityVolume();
        }
        private void comboMaterials_SelectedIndexChanged(object sender, EventArgs e)
        {
            // Get the selected material
            if (comboMaterials.SelectedItem is Material material)
            {
                selectedMaterial = material;
                selectedMaterialID = material.ID; // Store the ID explicitly

                // Update base density from the material
                baseDensity = material.Density > 0 ? material.Density : 1000.0;

                // Display current density in UI (if you have a label for it)
                lblDensityInfo.Text = $"Current Density: {baseDensity} kg/m³";

                // Log the change
                Logger.Log($"[AcousticSimulationForm] Selected material changed to {material.Name} with ID {material.ID}");
                CalculateVolumes();

                // Re-initialize the volume with the new material, but only invalidate if not initializing
                InitializeHomogeneousDensityVolume();
            }
        }
        private void LoadMaterials()
        {
            comboMaterials.Items.Clear();
            bool hasNonExteriorMaterial = false;

            foreach (Material material in mainForm.Materials)
            {
                // Skip Exterior material (ID 0)
                if (material.ID != 0)
                {
                    comboMaterials.Items.Add(material);
                    hasNonExteriorMaterial = true;
                    Logger.Log($"[AcousticSimulationForm] Added material {material.Name} (ID: {material.ID}) to dropdown");
                }
            }

            if (hasNonExteriorMaterial)
            {
                comboMaterials.SelectedIndex = 0;

                // Get the selected material and log it
                Material selectedMat = comboMaterials.SelectedItem as Material;
                if (selectedMat != null)
                {
                    Logger.Log($"[AcousticSimulationForm] Initially selected material: {selectedMat.Name} (ID: {selectedMat.ID})");
                }
            }
            else
            {
                MessageBox.Show("No materials found. Please segment your volume with at least one non-Exterior material first.",
                    "No Materials", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            // Make sure the selected material ID is set
            EnsureCorrectMaterialSelected();
        }

        
        private void EnsureCorrectMaterialSelected()
        {
            // If we have a valid selection, make sure the ID is stored
            if (selectedMaterial != null && selectedMaterial.ID > 0)
            {
                selectedMaterialID = selectedMaterial.ID;
                Logger.Log($"[AcousticSimulationForm] Using material {selectedMaterial.Name} with ID {selectedMaterialID}");
            }
            // If not, try to find a valid material
            else if (comboMaterials.Items.Count > 0)
            {
                // Trigger selection of first item
                comboMaterials.SelectedIndex = 0;
            }
            else
            {
                Logger.Log("[AcousticSimulationForm] Warning: No valid materials available");
            }
        }
        private void InitializeHomogeneousDensityVolume()
        {
            try
            {
                if (mainForm.volumeData == null ||
                    mainForm.volumeLabels == null ||
                    selectedMaterial == null)
                    return;

                int width = mainForm.GetWidth();
                int height = mainForm.GetHeight();
                int depth = mainForm.GetDepth();

                // Ensure we’re not on the “Exterior” material
                byte materialID = selectedMaterialID;
                if (materialID == 0 || selectedMaterial.ID == 0)
                    return;

                // Build a homogeneous density volume
                float[,,] homogeneousDensityVolume = new float[width, height, depth];
                int materialVoxelCount = 0;

                Parallel.For(0, depth, z =>
                {
                    for (int y = 0; y < height; y++)
                        for (int x = 0; x < width; x++)
                        {
                            if (mainForm.volumeLabels[x, y, z] == materialID)
                            {
                                homogeneousDensityVolume[x, y, z] = (float)baseDensity;
                                Interlocked.Increment(ref materialVoxelCount);
                            }
                            else
                            {
                                homogeneousDensityVolume[x, y, z] = 0f;
                            }
                        }
                });

                // Store for later rendering
                densityVolume = homogeneousDensityVolume;

                // Primary volumeRenderer (used on the Volume tab)
                volumeRenderer = new VolumeRenderer(
                    densityVolume,
                    width, height, depth,
                    mainForm.GetPixelSize(),
                    materialID,
                    useFullVolumeRendering
                );
                CalculateInitialZoomAndPosition();
                volumeRenderer.SetTransformation(rotationX, rotationY, zoom, pan);

                // **Seed the simulationRenderer** (ready for the Simulation tab)
                simulationRenderer = new VolumeRenderer(
                    densityVolume,
                    width, height, depth,
                    mainForm.GetPixelSize(),
                    materialID,
                    useFullVolumeRendering
                );
                simulationRenderer.SetTransformation(simRotationX, simRotationY, simZoom, simPan);

                hasDensityVariation = false;
                CalculateVolumes();

                if (!isInitializing)
                    pictureBoxVolume.Invalidate();
            }
            catch (Exception ex)
            {
                Logger.Log($"[AcousticSimulationForm] Error initializing homogeneous density: {ex.Message}");
                if (!isInitializing)
                {
                    MessageBox.Show($"Error initializing volume: {ex.Message}",
                                    "Error",
                                    MessageBoxButtons.OK,
                                    MessageBoxIcon.Error);
                }
            }
        }

        private byte[,,] GetVolumeLabelsArray()
        {
            int width = mainForm.GetWidth();
            int height = mainForm.GetHeight();
            int depth = mainForm.GetDepth();

            byte[,,] result = new byte[width, height, depth];

            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        result[x, y, z] = mainForm.volumeLabels[x, y, z];
                    }
                }
            }

            return result;
        }
        private float[,,] PreprocessVolumeForFullRendering(float[,,] originalVolume, byte materialID)
        {
            int width = originalVolume.GetLength(0);
            int height = originalVolume.GetLength(1);
            int depth = originalVolume.GetLength(2);

            // Create a copy of the original volume
            float[,,] processedVolume = new float[width, height, depth];

            // Create a grid pattern of "surfaces" throughout the material
            // This tricks the renderer into showing the interior
            int gridSpacing = 4; // Smaller value = denser wireframe

            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        if (originalVolume[x, y, z] > 0) // This is a material voxel
                        {
                            // Check if this voxel should be part of our grid
                            if (x % gridSpacing == 0 || y % gridSpacing == 0 || z % gridSpacing == 0)
                            {
                                // This will be treated as a "boundary" voxel by the renderer
                                processedVolume[x, y, z] = originalVolume[x, y, z];
                            }
                            else
                            {
                                // Keep the original density but mark it in a way that it's
                                // still part of the material but not a "boundary"
                                processedVolume[x, y, z] = originalVolume[x, y, z];
                            }
                        }
                    }
                }
            }

            return processedVolume;
        }

        private void CalculateInitialZoomAndPosition()
        {
            // Calculate a proper initial zoom value based on the volume size
            int maxDimension = Math.Max(Math.Max(mainForm.GetWidth(), mainForm.GetHeight()), mainForm.GetDepth());

            // Use a more appropriate initial zoom value that shows the object at a good size
            zoom = Math.Max(1.5f, 1200.0f / maxDimension);

            // Center the view
            pan = new PointF(0, 0);

            // Set an initial rotation for better 3D perception
            rotationX = 30;
            rotationY = 30;

            Logger.Log($"[AcousticSimulationForm] Initial zoom set to {zoom} for volume with max dimension {maxDimension}");
        }


        private void UpdateMaterialInfo()
        {
            if (selectedMaterial != null)
            {
                lblDensityInfo.Text = $"Current Density: {baseDensity:F1} kg/m³";
            }
            else
            {
                lblDensityInfo.Text = "Current Density: Not set";
            }

            // Only invalidate if not initializing
            if (!isInitializing)
            {
                pictureBoxVolume.Invalidate();
            }
        }

        private void BtnSetDensity_Click(object sender, EventArgs e)
        {
            if (selectedMaterial != null)
            {
                using (DensitySettingsForm densityForm = new DensitySettingsForm(this, mainForm))
                {
                    if (densityForm.ShowDialog() == DialogResult.OK)
                    {
                        // Density will be set via the SetMaterialDensity method
                        btnApplyVariation.Enabled = true;
                        UpdateMaterialInfo();

                        // Reinitialize the volume with the new density
                        InitializeHomogeneousDensityVolume();

                        // Refresh the visualization
                        pictureBoxVolume.Invalidate();
                    }
                }
            }
            else
            {
                MessageBox.Show("Please select a material first.", "No Material Selected",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        private void CalculateAutoElasticProperties()
        {
            // use your baseDensity in kg/m³
            double rho = baseDensity;
            // estimate Vp from density (same law you use in simulator)
            double rhoGcm3 = rho / 1000.0;
            double vp_km = 39.128 * Math.Pow(rhoGcm3, 0.37);
            double vp = vp_km * 1000.0;          // m/s
            double vs = vp / Math.Sqrt(3.0);    // approximate
            double G = rho * vs * vs;           // shear modulus (Pa)
            double K = rho * (vp * vp - (4.0 / 3.0) * vs * vs);  // bulk modulus (Pa)
            double E = 9.0 * K * G / (3.0 * K + G);        // Young’s modulus (Pa)
            double nu = (3.0 * K - 2.0 * G) / (2.0 * (3.0 * K + G)); // Poisson’s ratio

            // fill the UI (convert Pa→MPa)
            numYoungsModulus.Value = (decimal)(E / 1e6);
            numPoissonRatio.Value = (decimal)nu;
        }
        private void CalculateVolumes()
        {
            if (mainForm.volumeLabels == null || selectedMaterial == null)
                return;

            // Get pixel size and determine appropriate unit
            double pixelSize = mainForm.GetPixelSize(); // in meters

            // Determine appropriate unit based on pixel size
            if (pixelSize <= 1e-6) // 1μm or less
            {
                pixelSize *= 1e6; // Convert to μm
                volumeUnit = "μm³";
            }
            else if (pixelSize <= 1e-3) // 1mm or less
            {
                pixelSize *= 1e3; // Convert to mm
                volumeUnit = "mm³";
            }
            else
            {
                volumeUnit = "m³";
            }

            double pixelVolume = pixelSize * pixelSize * pixelSize;

            int width = mainForm.GetWidth();
            int height = mainForm.GetHeight();
            int depth = mainForm.GetDepth();
            byte selectedID = selectedMaterialID;

            int selectedMaterialVoxels = 0;
            int totalVoxels = width * height * depth; // Total voxels including exterior

            // Count selected material voxels
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        // Count selected material voxels
                        if (mainForm.volumeLabels[x, y, z] == selectedID)
                        {
                            selectedMaterialVoxels++;
                        }
                    }
                }
            }

            // Calculate volumes using the appropriate unit
            materialVolume = selectedMaterialVoxels * pixelVolume;
            totalMaterialVolume = totalVoxels * pixelVolume; // Include ALL voxels

            // Calculate percentage of selected material relative to entire volume
            volumePercentage = (materialVolume / totalMaterialVolume) * 100;

            Logger.Log($"[AcousticSimulationForm] Calculated volumes: Material {materialVolume:F2} {volumeUnit}, " +
                       $"Total {totalMaterialVolume:F2} {volumeUnit}, Percentage {volumePercentage:F2}%");
        }


        // Additional method for the DensitySettingsForm
        public double CalculateTotalVolume()
        {
            if (selectedMaterial == null || mainForm.volumeLabels == null)
                return 0.0;

            int width = mainForm.GetWidth();
            int height = mainForm.GetHeight();
            int depth = mainForm.GetDepth();
            byte materialID = selectedMaterial.ID;

            // Count voxels
            int voxelCount = 0;
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        if (mainForm.volumeLabels[x, y, z] == materialID)
                        {
                            voxelCount++;
                        }
                    }
                }
            }

            // Calculate volume in cubic meters
            double pixelSize = mainForm.GetPixelSize(); // in meters
            double voxelVolume = pixelSize * pixelSize * pixelSize; // m³

            return voxelCount * voxelVolume;
        }

        private void BtnApplyVariation_Click(object sender, EventArgs e)
        {
            if (selectedMaterial == null || selectedMaterialID == 0)
            {
                MessageBox.Show("Please select a valid material first.",
                    "No Material Selected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Verify we have a valid density value
            if (baseDensity <= 0)
            {
                MessageBox.Show("Please set a valid density value first.",
                    "Invalid Density", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Apply density variation directly
            ApplyDensityVariation();
        }
        private double CalculateMaterialVolumeForImage()
        {
            if (selectedMaterial == null || mainForm.volumeLabels == null)
                return 0.0;

            int width = mainForm.GetWidth();
            int height = mainForm.GetHeight();
            int depth = mainForm.GetDepth();
            byte materialID = selectedMaterialID;

            // Count voxels of this material
            int voxelCount = 0;
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        if (mainForm.volumeLabels[x, y, z] == materialID)
                        {
                            voxelCount++;
                        }
                    }
                }
            }

            // Calculate volume in cubic meters
            double pixelSize = mainForm.GetPixelSize(); // in meters
            double voxelVolume = pixelSize * pixelSize * pixelSize; // m³

            return voxelCount * voxelVolume;
        }

        // Calculate total dataset volume in cubic meters
        private double CalculateTotalVolumeForImage()
        {
            if (mainForm.volumeLabels == null)
                return 0.0;

            int width = mainForm.GetWidth();
            int height = mainForm.GetHeight();
            int depth = mainForm.GetDepth();

            // Calculate volume in cubic meters
            double pixelSize = mainForm.GetPixelSize(); // in meters
            return width * height * depth * pixelSize * pixelSize * pixelSize;
        }

        // Format volume value to appropriate units (mm³, cm³, etc.)
        private string FormatVolume(double volumeInCubicMeters)
        {
            if (volumeInCubicMeters < 1e-12) // Less than 1 mm³
            {
                return $"{volumeInCubicMeters * 1e18:F2} µm³";
            }
            else if (volumeInCubicMeters < 1e-6) // Less than 1 cm³
            {
                return $"{volumeInCubicMeters * 1e9:F2} mm³";
            }
            else if (volumeInCubicMeters < 1e-3) // Less than 1 liter
            {
                return $"{volumeInCubicMeters * 1e6:F2} cm³";
            }
            else
            {
                return $"{volumeInCubicMeters:F4} m³";
            }
        }
        // Method called by DensitySettingsForm to set the material density
        public Material SelectedMaterial => selectedMaterial;

        public void SetMaterialDensity(double density)
        {
            baseDensity = density;

            // Store density in the material
            if (selectedMaterial != null)
            {
                selectedMaterial.Density = density;
            }
        }

        public void ApplyDensityCalibration(List<CalibrationPoint> calibrationPoints)
        {
            // Apply the calibration - use the linear regression model to calculate density
            if (calibrationPoints.Count >= 2)
            {
                var model = MaterialDensityLibrary.CalculateLinearDensityModel(calibrationPoints);
                double avgGrayValue = CalculateAverageGrayValue();

                // Calculate density based on the model
                double calculatedDensity = model.slope * avgGrayValue + model.intercept;

                // Set the density
                SetMaterialDensity(calculatedDensity);

                // Update UI
                UpdateMaterialInfo();

                // Reinitialize volume
                InitializeHomogeneousDensityVolume();

                // Refresh visualization
                pictureBoxVolume.Invalidate();
            }
        }

        private double CalculateAverageGrayValue()
        {
            if (mainForm.volumeData == null || mainForm.volumeLabels == null || selectedMaterial == null)
                return 128.0;

            long totalGray = 0;
            int count = 0;
            byte materialID = selectedMaterial.ID;

            // Calculate average gray value for the selected material
            for (int z = 0; z < mainForm.GetDepth(); z++)
            {
                for (int y = 0; y < mainForm.GetHeight(); y++)
                {
                    for (int x = 0; x < mainForm.GetWidth(); x++)
                    {
                        if (mainForm.volumeLabels[x, y, z] == materialID)
                        {
                            totalGray += mainForm.volumeData[x, y, z];
                            count++;
                        }
                    }
                }
            }

            return count > 0 ? (double)totalGray / count : 128.0;
        }

        private void ApplyDensityVariation()
        {
            if (mainForm.volumeData == null || mainForm.volumeLabels == null)
            {
                MessageBox.Show("No volume data loaded.", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Show progress form with cancellation support - CHANGED: non-modal with proper ownership
            CancellableProgressForm progressForm = new CancellableProgressForm("Applying density variation...");
            progressForm.Owner = this; // Set owner to establish parent-child relationship
            progressForm.Show(this);   // Show as non-modal dialog with owner

            // Create a cancellation token source
            var cts = new CancellationTokenSource();

            // Start the calculation in a background task
            Task.Run(() =>
            {
                try
                {
                    CalculateDensityVariation(progressForm, cts.Token);

                    // Initialize volume renderer
                    BeginInvoke(new Action(() =>
                    {
                        volumeRenderer = new VolumeRenderer(densityVolume,
                                                          mainForm.GetWidth(),
                                                          mainForm.GetHeight(),
                                                          mainForm.GetDepth(),
                                                          mainForm.GetPixelSize());

                        volumeRenderer.SetTransformation(rotationX, rotationY, zoom, pan);

                        // Mark that we have density variation
                        hasDensityVariation = true;

                        // Update visualization
                        pictureBoxVolume.Invalidate();

                        Logger.Log("[AcousticSimulationForm] Applied variable density to visualization");
                    }));
                }
                catch (OperationCanceledException)
                {
                    // Task was canceled
                    this.Invoke(new Action(() =>
                    {
                        MessageBox.Show("Operation was cancelled.",
                            "Cancelled", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }));
                }
                catch (Exception ex)
                {
                    // Handle exception
                    this.Invoke(new Action(() =>
                    {
                        MessageBox.Show($"Error applying density variation: {ex.Message}",
                            "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }));
                }
                finally
                {
                    // Close progress form
                    this.Invoke(new Action(() => progressForm.Close()));
                }
            }, cts.Token);

            // Handle cancellation
            progressForm.CancelPressed += (s, args) => cts.Cancel();
        }
        private void CalculateDensityVariation(CancellableProgressForm progressForm, CancellationToken ct)
        {
            int width = mainForm.GetWidth();
            int height = mainForm.GetHeight();
            int depth = mainForm.GetDepth();
            byte materialID = selectedMaterial.ID;

            // Ensure our densityVolume array matches the current volume size
            if (densityVolume == null ||
                densityVolume.GetLength(0) != width ||
                densityVolume.GetLength(1) != height ||
                densityVolume.GetLength(2) != depth)
            {
                densityVolume = new float[width, height, depth];
            }

            // 1) First pass: find min/max grayscale and average
            byte minGray = byte.MaxValue;
            byte maxGray = byte.MinValue;
            long totalGray = 0;
            int voxelCount = 0;

            for (int z = 0; z < depth; z++)
            {
                if (ct.IsCancellationRequested) return;
                progressForm.UpdateProgress((int)(30.0 * z / depth));  // 0–30%

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        if (mainForm.volumeLabels[x, y, z] == materialID)
                        {
                            byte g = mainForm.volumeData[x, y, z];
                            if (g < minGray) minGray = g;
                            if (g > maxGray) maxGray = g;
                            totalGray += g;
                            voxelCount++;
                        }
                    }
                }
            }

            if (voxelCount == 0)
                throw new InvalidOperationException($"Material '{selectedMaterial.Name}' not found in the volume.");

            double avgGray = (double)totalGray / voxelCount;
            int grayRange = maxGray - minGray;
            if (grayRange == 0) grayRange = 1;

            // 2) Determine density mapping: ±20% about baseDensity
            double variation = 0.2;  // 20%
            double minDensity = baseDensity * (1 - variation);
            double maxDensity = baseDensity * (1 + variation);
            double densityRange = maxDensity - minDensity;

            progressForm.UpdateMessage(
                $"Applying density variation [{minDensity:F1} – {maxDensity:F1} kg/m³]..."
            );

            // 3) Second pass: fill densityVolume
            for (int z = 0; z < depth; z++)
            {
                if (ct.IsCancellationRequested) return;
                progressForm.UpdateProgress(30 + (int)(70.0 * z / depth));  // 30–100%

                Parallel.For(0, height, y =>
                {
                    if (ct.IsCancellationRequested) return;

                    for (int x = 0; x < width; x++)
                    {
                        densityVolume[x, y, z] = 0f;
                        if (mainForm.volumeLabels[x, y, z] == materialID)
                        {
                            byte g = mainForm.volumeData[x, y, z];
                            double norm = (g - minGray) / (double)grayRange;
                            double d = minDensity + norm * densityRange;
                            densityVolume[x, y, z] = (float)d;
                        }
                    }
                });
            }

            // 4) Rebuild both renderers on the UI thread
            BeginInvoke(new Action(() =>
            {
                // Volume tab renderer
                volumeRenderer = new VolumeRenderer(
                    densityVolume,
                    width, height, depth,
                    mainForm.GetPixelSize(),
                    materialID,
                    useFullVolumeRendering
                );
                volumeRenderer.SetTransformation(rotationX, rotationY, zoom, pan);

                // Simulation tab renderer
                simulationRenderer = new VolumeRenderer(
                    densityVolume,
                    width, height, depth,
                    mainForm.GetPixelSize(),
                    materialID,
                    useFullVolumeRendering
                );
                simulationRenderer.SetTransformation(simRotationX, simRotationY, simZoom, simPan);

                hasDensityVariation = true;

                // Refresh both views
                pictureBoxVolume.Invalidate();
                pictureBoxSimulation.Invalidate();

                Logger.Log("[AcousticSimulationForm] Applied variable density to both renderers");
            }));

            // 5) (Optional) ensure connectivity afterward
            if (!ct.IsCancellationRequested)
            {
                progressForm.UpdateMessage("Ensuring connectivity...");
                EnsureConnectivity(progressForm, ct);
            }
        }

        private void EnsureConnectivity(CancellableProgressForm progressForm, CancellationToken ct)
        {
            // This method would implement a flood fill or similar algorithm to ensure
            // all voxels of the material are connected (removing isolated voxels)
            // For now, just a placeholder that could be expanded in the future

            progressForm.UpdateProgress(100);
        }

        #region Mouse Interaction Handlers

        private bool isDragging = false;
        private Point lastMousePosition;

        private void PictureBoxVolume_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Right)
            {
                isDragging = true;
                lastMousePosition = e.Location;
            }
        }

        private void PictureBoxVolume_MouseMove(object sender, MouseEventArgs e)
        {
            if (isDragging)
            {
                int dx = e.X - lastMousePosition.X;
                int dy = e.Y - lastMousePosition.Y;

                if (e.Button == MouseButtons.Left)
                {
                    // Rotate
                    rotationY += dx * 0.5f;
                    rotationX += dy * 0.5f;

                    // Limit rotation angles
                    rotationX = Math.Max(-90, Math.Min(90, rotationX));
                }
                else if (e.Button == MouseButtons.Right)
                {
                    // Pan
                    pan.X += dx;
                    pan.Y += dy;
                }

                lastMousePosition = e.Location;

                // Update the visualization
                if (volumeRenderer != null)
                {
                    volumeRenderer.SetTransformation(rotationX, rotationY, zoom, pan);
                }

                pictureBoxVolume.Invalidate();
            }
        }

        private void PictureBoxVolume_MouseUp(object sender, MouseEventArgs e)
        {
            isDragging = false;
        }

        private void PictureBoxVolume_MouseWheel(object sender, MouseEventArgs e)
        {
            // Zoom in/out with more responsive adjustment
            float zoomFactor = 1.2f;
            if (e.Delta > 0)
            {
                zoom *= zoomFactor;
            }
            else
            {
                zoom /= zoomFactor;
            }

            // Limit zoom range - increase the upper limit for more zoom capability
            zoom = Math.Max(0.1f, Math.Min(50.0f, zoom));

            // Update the visualization
            if (volumeRenderer != null)
            {
                volumeRenderer.SetTransformation(rotationX, rotationY, zoom, pan);
            }

            pictureBoxVolume.Invalidate();
        }
        private bool renderedOnce = false;
        private void PictureBoxVolume_Paint(object sender, PaintEventArgs e)
        {
            // If we're still initializing and this isn't the first render, skip
            if (isInitializing && e.ClipRectangle.Width > 0 && renderedOnce)
                return;

            renderedOnce = true;

            // If we have a volume renderer, use it to render the volume
            if (volumeRenderer != null)
            {
                volumeRenderer.Render(e.Graphics, pictureBoxVolume.Width, pictureBoxVolume.Height);

                // Draw additional information at the top
                string densityInfo = hasDensityVariation
                    ? "Variable Density Visualization"
                    : "Homogeneous Density Visualization";

                using (Font font = new Font("Segoe UI", 10, System.Drawing.FontStyle.Bold))
                using (SolidBrush brush = new SolidBrush(Color.White))
                {
                    // Top info
                    e.Graphics.DrawString(densityInfo, font, brush, 10, 10);
                    e.Graphics.DrawString($"Material: {selectedMaterial.Name} (ID: {selectedMaterialID})", font, brush, 10, 30);
                    e.Graphics.DrawString($"Density: {baseDensity:F1} kg/m³", font, brush, 10, 50);

                    // Bottom info - use pre-calculated values
                    int bottomY = pictureBoxVolume.Height - 70;
                    e.Graphics.DrawString($"Material Volume: {materialVolume:F2} {volumeUnit}", font, brush, 10, bottomY);
                    e.Graphics.DrawString($"Total Volume: {totalMaterialVolume:F2} {volumeUnit}", font, brush, 10, bottomY + 20);
                    e.Graphics.DrawString($"Volume Percentage: {volumePercentage:F2}%", font, brush, 10, bottomY + 40);
                }
            }
            else
            {
                // Draw default message
                using (Font font = new Font("Segoe UI", 12))
                using (SolidBrush brush = new SolidBrush(Color.White))
                {
                    string message;
                    if (selectedMaterial == null)
                    {
                        message = "Please select a material";
                    }
                    else if (baseDensity <= 0)
                    {
                        message = "Please set material density";
                    }
                    else
                    {
                        message = "Initializing volume visualization...";
                    }

                    SizeF textSize = e.Graphics.MeasureString(message, font);
                    e.Graphics.DrawString(message, font, brush,
                        (pictureBoxVolume.Width - textSize.Width) / 2,
                        (pictureBoxVolume.Height - textSize.Height) / 2);
                }
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Dispose of the simulator if it implements IDisposable
                if (simulator is IDisposable disposableSimulator)
                {
                    disposableSimulator.Dispose();
                   cpuSimulator?.Dispose();
                    gpuSimulator?.Dispose();
                }
                visualizer?.Dispose();
            }
            base.Dispose(disposing);
        }
        #endregion

        #region Icon Creation Methods

        private Image CreateDensityIcon()
        {
            Bitmap bmp = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                // Draw a beaker/cylinder
                Rectangle rect = new Rectangle(2, 5, 12, 9);
                using (Pen pen = new Pen(Color.DodgerBlue, 1.5f))
                {
                    g.DrawRectangle(pen, rect);
                    g.DrawLine(pen, 2, 5, 2, 2);
                    g.DrawLine(pen, 14, 5, 14, 2);
                }

                // Fill with "density" levels
                using (SolidBrush brush = new SolidBrush(Color.DodgerBlue))
                {
                    g.FillRectangle(brush, 3, 10, 10, 3);
                }
            }
            return bmp;
        }

        private Image CreateVariationIcon()
        {
            Bitmap bmp = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                // Draw a gradient bar representing density variation
                Rectangle rect = new Rectangle(2, 4, 12, 8);
                using (LinearGradientBrush brush = new LinearGradientBrush(
                    rect, Color.LightBlue, Color.DarkBlue, LinearGradientMode.Horizontal))
                {
                    g.FillRectangle(brush, rect);
                }

                // Draw outline
                using (Pen pen = new Pen(Color.DarkGray, 1))
                {
                    g.DrawRectangle(pen, rect);
                }
            }
            return bmp;
        }

        #endregion

    }
}