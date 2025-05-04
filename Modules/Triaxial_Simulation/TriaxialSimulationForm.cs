using Krypton.Toolkit;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;
using static MaterialDensityLibrary;
using Point = System.Drawing.Point;
using MessageBox = System.Windows.Forms.MessageBox;
using Size = System.Drawing.Size;
using System.Linq;
using MediaFoundation.OPM;
using System.Reflection;

namespace CTSegmenter
{
    public partial class TriaxialSimulationForm : KryptonForm, IMaterialDensityProvider
    {
        private TriaxialResultsExtension resultsExtension;
        private bool isInitializing = true;
        private MainForm mainForm;
        private Material selectedMaterial;
        private double baseDensity = 0.0; // in kg/m³
        private bool hasDensityVariation = false;
        private byte selectedMaterialID = 0;
        private KryptonCheckBox chkDebugMode;

        // Volume visualization
        private float[,,] densityVolume; // Stores density values for each voxel
        private float rotationX = 30;
        private float rotationY = 30;
        private float zoom = 1.0f;
        private PointF pan = new PointF(0, 0);
        private bool isDragging = false;
        private Point lastMousePosition;
        private VolumeRenderer volumeRenderer;
        private bool renderedOnce = false;
        private bool useFullVolumeRendering = true;
        private double materialVolume;
        private double totalMaterialVolume;
        private double volumePercentage;
        private string volumeUnit;

        // Simulation parameters
        private int width, height, depth;
        private volatile bool simulationRunning;
        private volatile bool simulationPaused;

        // Either/or simulator
        private TriaxialSimulator cpuSim;
        private TriaxialSimulatorGPU gpuSim;

        // UI Controls
        private TabControl tabControl;
        private TabPage tabVolume;
        private TabPage tabSimulation;
        private TabPage tabResults;
        private ToolStrip toolStrip;
        private KryptonComboBox comboMaterials;
        private KryptonButton btnSetDensity;
        private KryptonButton btnApplyVariation;
        private KryptonLabel lblDensityInfo;
        private KryptonCheckBox chkFullVolumeRendering;
        private PictureBox pictureBoxVolume;
        private KryptonButton btnAutoCalculateProps;
        private KryptonCheckBox chkAutoCalculateProps;

        // Simulation Controls
        private KryptonComboBox cmbStressAxis;
        private KryptonNumericUpDown nudConfiningP;
        private KryptonNumericUpDown nudInitialP;
        private KryptonNumericUpDown nudFinalP;
        private KryptonNumericUpDown nudSteps;
        private KryptonNumericUpDown nudE;
        private KryptonNumericUpDown nudNu;
        private KryptonNumericUpDown nudTensile;
        private KryptonNumericUpDown nudFriction;
        private KryptonNumericUpDown nudCohesion;
        private KryptonCheckBox chkElastic;
        private KryptonCheckBox chkPlastic;
        private KryptonCheckBox chkBrittle;
        private KryptonCheckBox chkUseGPU;
        private KryptonButton btnRun;
        private KryptonButton btnPause;
        private KryptonButton btnCancel;
        private KryptonButton btnContinue;
        private PictureBox pictureBoxSimulation;
        // Form Extension
        private TriaxialVisualizationAdapter triaxialVisualization;

        // Results Controls
        private Chart chart;
        private KryptonProgressBar progressBar;
        private KryptonLabel lblStatus;

        // CancellationTokenSource for safe cancellation
        private CancellationTokenSource cts;

        // Results data
        private List<PointF> stressStrainCurve = new List<PointF>();
        private double peakStress = 0;
        private int failureStep = -1;
        private bool failureDetected = false;

        // damage array
        private double[,,] damage;
        public double[,,] DamageData => damage;

        public TriaxialSimulationForm(MainForm mainForm)
        {
            this.mainForm = mainForm;

            isInitializing = true;
            try
            {
                InitializeComponent();

                // Set the initial view parameters
                rotationX = 30;
                rotationY = 30;
                zoom = 1.0f;
                pan = new PointF(0, 0);
                InitializeTriaxialVisualization();
                // Set initialization flag to false after everything is set up
                isInitializing = false;
            }
            catch (Exception ex)
            {
                Logger.Log($"[TriaxialSimulationForm] Construction error: {ex.Message}\n{ex.StackTrace}");
                MessageBox.Show($"Error initializing Triaxial Simulation form: {ex.Message}",
                    "Initialization Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void InitializeComponent()
        {
            // Form settings
            this.Text = "Triaxial Simulation";
            this.Size = new Size(1000, 800);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(45, 45, 48); // Dark background
            this.FormClosing += OnClosing;

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
            AddSimulationParameterHandlers();
            AddTabSelectionHandler();
            // Setup Results tab
            InitializeResultsTab();

            // Add controls to form
            this.Controls.Add(tabControl);
            this.Controls.Add(toolStrip);

            // Load materials when form loads
            this.Load += (s, e) =>
            {
                LoadMaterials();
                EnsureCorrectMaterialSelected();
                ProbeGpu();

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

        #region Volume Tab Implementation

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
            Logger.Log($"[TriaxialSimulationForm] Rendering mode changed to: {(useFullVolumeRendering ? "Full Volume" : "Boundary Only")}");

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
                Logger.Log($"[TriaxialSimulationForm] Selected material changed to {material.Name} with ID {material.ID}");
                CalculateVolumes();

                // Re-initialize the volume with the new material, but only invalidate if not initializing
                InitializeHomogeneousDensityVolume();
            }
        }

        private void LoadMaterials()
        {
            try
            {
                comboMaterials.Items.Clear();
                bool hasNonExteriorMaterial = false;

                // First find a valid material (ID > 0) to select by default
                Material defaultMaterial = null;
                foreach (Material material in mainForm.Materials)
                {
                    if (material.ID > 0) // Skip Exterior material (ID 0)
                    {
                        defaultMaterial = material;
                        break;
                    }
                }

                // If no valid material was found, provide better error handling
                if (defaultMaterial == null)
                {
                    Logger.Log("[TriaxialSimulationForm] No valid materials (ID > 0) found!");
                    MessageBox.Show("No valid materials found. Please segment your volume with at least one non-Exterior material first.",
                        "No Materials", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Now add materials to the combo box
                foreach (Material material in mainForm.Materials)
                {
                    // Skip Exterior material (ID 0)
                    if (material.ID > 0)
                    {
                        comboMaterials.Items.Add(material);
                        hasNonExteriorMaterial = true;
                        Logger.Log($"[TriaxialSimulationForm] Added material {material.Name} (ID: {material.ID}) to dropdown");
                    }
                }

                if (hasNonExteriorMaterial)
                {
                    // Force select the first valid material
                    comboMaterials.SelectedIndex = 0;

                    // Set the material explicitly to avoid any race conditions
                    Material selectedMat = comboMaterials.SelectedItem as Material;
                    if (selectedMat != null)
                    {
                        selectedMaterial = selectedMat;
                        selectedMaterialID = selectedMat.ID;
                        Logger.Log($"[TriaxialSimulationForm] Initially selected material: {selectedMat.Name} (ID: {selectedMat.ID})");
                    }
                }
                else
                {
                    MessageBox.Show("No materials found. Please segment your volume with at least one non-Exterior material first.",
                        "No Materials", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[TriaxialSimulationForm] Error loading materials: {ex.Message}\n{ex.StackTrace}");
                MessageBox.Show($"Error loading materials: {ex.Message}", "Material Loading Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            // Always ensure a valid material is selected
            EnsureCorrectMaterialSelected();
        }

        private void EnsureCorrectMaterialSelected()
        {
            try
            {
                // If selected material is already valid, just make sure ID is stored
                if (selectedMaterial != null && selectedMaterial.ID > 0)
                {
                    selectedMaterialID = selectedMaterial.ID;
                    Logger.Log($"[TriaxialSimulationForm] Using material {selectedMaterial.Name} with ID {selectedMaterialID}");
                    return;
                }

                // Otherwise, try to find and select first valid material
                if (comboMaterials.Items.Count > 0)
                {
                    // Try all items in the combo box
                    foreach (object item in comboMaterials.Items)
                    {
                        if (item is Material mat && mat.ID > 0)
                        {
                            comboMaterials.SelectedItem = mat;
                            selectedMaterial = mat;
                            selectedMaterialID = mat.ID;
                            Logger.Log($"[TriaxialSimulationForm] Selected material {mat.Name} with ID {mat.ID}");
                            return;
                        }
                    }
                }

                // If we get here and still don't have a valid material, try to find one directly
                foreach (Material material in mainForm.Materials)
                {
                    if (material.ID > 0)
                    {
                        selectedMaterial = material;
                        selectedMaterialID = material.ID;
                        Logger.Log($"[TriaxialSimulationForm] Fallback selection: material {material.Name} with ID {material.ID}");
                        return;
                    }
                }

                // If still no valid material, log clear error
                Logger.Log("[TriaxialSimulationForm] ERROR: No valid materials available!");
                MessageBox.Show("No valid materials found. Please segment your volume with at least one non-Exterior material first.",
                    "No Materials", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                Logger.Log($"[TriaxialSimulationForm] Error ensuring material selection: {ex.Message}");
            }
        }

        // Update existing density volume with homogeneous density values
        private void UpdateHomogeneousDensityVolume()
        {
            try
            {
                // Ensure we have valid data
                if (densityVolume == null || mainForm.volumeLabels == null || selectedMaterial == null || selectedMaterialID <= 0)
                {
                    Logger.Log("[TriaxialSimulationForm] Cannot update density - data not valid");
                    return;
                }

                int width = mainForm.GetWidth();
                int height = mainForm.GetHeight();
                int depth = mainForm.GetDepth();
                byte materialID = selectedMaterialID;

                // Update density values with the current base density
                Parallel.For(0, depth, z =>
                {
                    for (int y = 0; y < height; y++)
                        for (int x = 0; x < width; x++)
                        {
                            if (mainForm.volumeLabels[x, y, z] == materialID)
                            {
                                densityVolume[x, y, z] = (float)baseDensity;
                            }
                            else
                            {
                                densityVolume[x, y, z] = 0f;
                            }
                        }
                });

                hasDensityVariation = false;
                Logger.Log($"[TriaxialSimulationForm] Updated homogeneous density volume with {baseDensity} kg/m³");
            }
            catch (Exception ex)
            {
                Logger.Log($"[TriaxialSimulationForm] Error updating density volume: {ex.Message}");
            }
        }

        private void InitializeHomogeneousDensityVolume()
        {
            try
            {
                if (mainForm.volumeData == null || mainForm.volumeLabels == null)
                {
                    Logger.Log("[TriaxialSimulationForm] Cannot initialize volume - data not loaded");
                    return;
                }

                // Ensure valid material is selected
                if (selectedMaterial == null || selectedMaterialID <= 0)
                {
                    Logger.Log("[TriaxialSimulationForm] No valid material selected before volume initialization");
                    EnsureCorrectMaterialSelected();

                    // If still no valid material, abort
                    if (selectedMaterial == null || selectedMaterialID <= 0)
                    {
                        Logger.Log("[TriaxialSimulationForm] Aborting volume initialization - no valid material");
                        return;
                    }
                }

                int width = mainForm.GetWidth();
                int height = mainForm.GetHeight();
                int depth = mainForm.GetDepth();

                // Ensure we're using valid material ID (greater than 0)
                byte materialID = selectedMaterialID;
                if (materialID == 0)
                {
                    Logger.Log("[TriaxialSimulationForm] WARNING: Attempting to use Exterior (ID 0) as material");
                    // Find first non-exterior material
                    foreach (Material mat in mainForm.Materials)
                    {
                        if (mat.ID > 0)
                        {
                            materialID = mat.ID;
                            selectedMaterialID = materialID;
                            selectedMaterial = mat;
                            Logger.Log($"[TriaxialSimulationForm] Switched to material {mat.Name} with ID {materialID}");
                            break;
                        }
                    }

                    // If still using ID 0, abort
                    if (materialID == 0)
                    {
                        Logger.Log("[TriaxialSimulationForm] ERROR: No valid material ID found, cannot initialize volume");
                        return;
                    }
                }

                Logger.Log($"[TriaxialSimulationForm] Initializing volume with material ID {materialID}");

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

                Logger.Log($"[TriaxialSimulationForm] Volume initialized with {materialVoxelCount} voxels");

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

                hasDensityVariation = false;
                CalculateVolumes();

                if (!isInitializing)
                    pictureBoxVolume.Invalidate();
            }
            catch (Exception ex)
            {
                Logger.Log($"[TriaxialSimulationForm] Error initializing volume: {ex.Message}\n{ex.StackTrace}");
                if (!isInitializing)
                {
                    MessageBox.Show($"Error initializing volume: {ex.Message}",
                                    "Error",
                                    MessageBoxButtons.OK,
                                    MessageBoxIcon.Error);
                }
            }
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

            Logger.Log($"[TriaxialSimulationForm] Initial zoom set to {zoom} for volume with max dimension {maxDimension}");
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

            Logger.Log($"[TriaxialSimulationForm] Calculated volumes: Material {materialVolume:F2} {volumeUnit}, " +
                       $"Total {totalMaterialVolume:F2} {volumeUnit}, Percentage {volumePercentage:F2}%");
        }

        public Material SelectedMaterial => selectedMaterial;

        public void SetMaterialDensity(double density)
        {
            baseDensity = density;

            // Store density in the material
            if (selectedMaterial != null)
            {
                selectedMaterial.Density = density;
            }

            // If we're using homogeneous density, update the density volume
            if (!hasDensityVariation && densityVolume != null)
            {
                UpdateHomogeneousDensityVolume();
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

        private void ApplyDensityVariation()
        {
            if (mainForm.volumeData == null || mainForm.volumeLabels == null)
            {
                MessageBox.Show("No volume data loaded.", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Show progress form with cancellation support
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

                        Logger.Log("[TriaxialSimulationForm] Applied variable density to visualization");
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

            // 4) Rebuild renderer on the UI thread
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

                hasDensityVariation = true;

                // Refresh view
                pictureBoxVolume.Invalidate();

                Logger.Log("[TriaxialSimulationForm] Applied variable density to renderer");
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

        #endregion

        #region Simulation Tab Implementation
        /// <summary>
        /// Initialize visualization when switching to the simulation tab
        /// </summary>
        private void InitializeSimulationView()
        {
            try
            {
                // Check if we have valid data
                if (mainForm.volumeLabels == null || densityVolume == null || selectedMaterial == null)
                {
                    Logger.Log("[TriaxialSimulationForm] Cannot initialize simulation view - data not valid");
                    return;
                }

                // Initialize the triaxial visualization if needed
                if (triaxialVisualization == null)
                {
                    InitializeTriaxialVisualization();
                }

                // If we have a valid visualization, update it with current parameters
                if (triaxialVisualization != null)
                {
                    // Get current simulation parameters
                    StressAxis axis = (StressAxis)cmbStressAxis.SelectedIndex;
                    double confP = (double)nudConfiningP.Value;
                    double initialP = (double)nudInitialP.Value;

                    // Update the visualization with these parameters
                    triaxialVisualization.SetPressureParameters(confP, initialP, axis);
                    triaxialVisualization.SetViewTransformation(rotationX, rotationY, zoom, pan);

                    // Force redraw
                    pictureBoxSimulation.Invalidate();

                    Logger.Log("[TriaxialSimulationForm] Initialized simulation view with current parameters");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[TriaxialSimulationForm] Error initializing simulation view: {ex.Message}");
            }
        }
        /// <summary>
        /// Update visualization when parameters change
        /// </summary>
        private void UpdateSimulationView()
        {
            try
            {
                // Skip if simulation is already running
                if (simulationRunning)
                    return;

                // Update visualization with current parameters if available
                if (triaxialVisualization != null)
                {
                    StressAxis axis = (StressAxis)cmbStressAxis.SelectedIndex;
                    double confP = (double)nudConfiningP.Value;
                    double initialP = (double)nudInitialP.Value;

                    triaxialVisualization.SetPressureParameters(confP, initialP, axis);

                    // Force redraw
                    pictureBoxSimulation.Invalidate();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[TriaxialSimulationForm] Error updating simulation view: {ex.Message}");
            }
        }

        /// <summary>
        /// Add event handlers to all simulation parameters to update the visualization
        /// </summary>
        private void AddSimulationParameterHandlers()
        {
            // Add event handlers to the simulation parameters
            cmbStressAxis.SelectedIndexChanged += SimulationParameter_Changed;
            nudConfiningP.ValueChanged += SimulationParameter_Changed;
            nudInitialP.ValueChanged += SimulationParameter_Changed;
            nudFinalP.ValueChanged += SimulationParameter_Changed;
        }

        /// <summary>
        /// Event handler for any simulation parameter change
        /// </summary>
        private void SimulationParameter_Changed(object sender, EventArgs e)
        {
            // Update the visualization with the new parameters
            UpdateSimulationView();
        }

        /// <summary>
        /// Add a tab selection event handler to initialize the simulation view when switching to the simulation tab
        /// </summary>
        private void AddTabSelectionHandler()
        {
            tabControl.SelectedIndexChanged += TabControl_SelectedIndexChanged;
        }

        /// <summary>
        /// Event handler for tab selection changes
        /// </summary>
        private void TabControl_SelectedIndexChanged(object sender, EventArgs e)
        {
            // Check if we're switching to the simulation tab
            if (tabControl.SelectedTab == tabSimulation)
            {
                // Initialize the simulation view
                InitializeSimulationView();
            }
        }
        private void InitializeSimulationTab()
        {
            // Create panel for controls
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

            // 1. Stress axis combobox
            KryptonLabel lblAxis = new KryptonLabel();
            lblAxis.Text = "Stress Axis:";
            lblAxis.Location = new Point(10, currentY);
            lblAxis.StateCommon.ShortText.Color1 = Color.White;
            controlPanel.Controls.Add(lblAxis);

            cmbStressAxis = new KryptonComboBox();
            cmbStressAxis.Location = new Point(10, currentY + 20);
            cmbStressAxis.Width = controlWidth;
            cmbStressAxis.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbStressAxis.Items.AddRange(new object[] { "X", "Y", "Z" });
            cmbStressAxis.SelectedIndex = 2; // Default to Z
            controlPanel.Controls.Add(cmbStressAxis);

            currentY += verticalSpacing + 20;

            // 2. Physical parameters numeric inputs
            // Confining Pressure
            KryptonLabel lblConfiningPressure = new KryptonLabel();
            lblConfiningPressure.Text = "Confining Pressure (MPa):";
            lblConfiningPressure.Location = new Point(10, currentY);
            lblConfiningPressure.StateCommon.ShortText.Color1 = Color.White;
            controlPanel.Controls.Add(lblConfiningPressure);

            nudConfiningP = new KryptonNumericUpDown();
            nudConfiningP.Location = new Point(10, currentY + 20);
            nudConfiningP.Width = controlWidth - 30;
            nudConfiningP.DecimalPlaces = 2;
            nudConfiningP.Minimum = 0;
            nudConfiningP.Maximum = 1000;
            nudConfiningP.Value = 10.0m;
            controlPanel.Controls.Add(nudConfiningP);

            currentY += verticalSpacing + 20;

            // Initial Pressure
            KryptonLabel lblInitialP = new KryptonLabel();
            lblInitialP.Text = "Initial Pressure (MPa):";
            lblInitialP.Location = new Point(10, currentY);
            lblInitialP.StateCommon.ShortText.Color1 = Color.White;
            controlPanel.Controls.Add(lblInitialP);

            nudInitialP = new KryptonNumericUpDown();
            nudInitialP.Location = new Point(10, currentY + 20);
            nudInitialP.Width = controlWidth - 30;
            nudInitialP.DecimalPlaces = 2;
            nudInitialP.Minimum = 0;
            nudInitialP.Maximum = 1000;
            nudInitialP.Value = 10.0m;
            controlPanel.Controls.Add(nudInitialP);

            currentY += verticalSpacing + 20;

            // Final Pressure
            KryptonLabel lblFinalP = new KryptonLabel();
            lblFinalP.Text = "Final Pressure (MPa):";
            lblFinalP.Location = new Point(10, currentY);
            lblFinalP.StateCommon.ShortText.Color1 = Color.White;
            controlPanel.Controls.Add(lblFinalP);

            nudFinalP = new KryptonNumericUpDown();
            nudFinalP.Location = new Point(10, currentY + 20);
            nudFinalP.Width = controlWidth - 30;
            nudFinalP.DecimalPlaces = 2;
            nudFinalP.Minimum = 0;
            nudFinalP.Maximum = 1000;
            nudFinalP.Value = 100.0m;
            controlPanel.Controls.Add(nudFinalP);

            currentY += verticalSpacing + 20;

            // Steps
            KryptonLabel lblSteps = new KryptonLabel();
            lblSteps.Text = "Steps:";
            lblSteps.Location = new Point(10, currentY);
            lblSteps.StateCommon.ShortText.Color1 = Color.White;
            controlPanel.Controls.Add(lblSteps);

            nudSteps = new KryptonNumericUpDown();
            nudSteps.Location = new Point(10, currentY + 20);
            nudSteps.Width = controlWidth - 30;
            nudSteps.DecimalPlaces = 0;
            nudSteps.Minimum = 1;
            nudSteps.Maximum = 1000;
            nudSteps.Value = 20;
            controlPanel.Controls.Add(nudSteps);

            currentY += verticalSpacing + 20;

            // Young's Modulus (E)
            KryptonLabel lblE = new KryptonLabel();
            lblE.Text = "Young's Modulus (GPa):";
            lblE.Location = new Point(10, currentY);
            lblE.StateCommon.ShortText.Color1 = Color.White;
            controlPanel.Controls.Add(lblE);

            nudE = new KryptonNumericUpDown();
            nudE.Location = new Point(10, currentY + 20);
            nudE.Width = controlWidth - 30;
            nudE.DecimalPlaces = 1;
            nudE.Minimum = 0;
            nudE.Maximum = 1000;
            nudE.Value = 70.0m;
            controlPanel.Controls.Add(nudE);

            currentY += verticalSpacing + 20;

            // Poisson's Ratio (ν)
            KryptonLabel lblNu = new KryptonLabel();
            lblNu.Text = "Poisson's Ratio (ν):";
            lblNu.Location = new Point(10, currentY);
            lblNu.StateCommon.ShortText.Color1 = Color.White;
            controlPanel.Controls.Add(lblNu);

            nudNu = new KryptonNumericUpDown();
            nudNu.Location = new Point(10, currentY + 20);
            nudNu.Width = controlWidth - 30;
            nudNu.DecimalPlaces = 3;
            nudNu.Minimum = 0;
            nudNu.Maximum = 0.5m;
            nudNu.Increment = 0.01m;
            nudNu.Value = 0.25m;
            controlPanel.Controls.Add(nudNu);

            currentY += verticalSpacing + 20;

            // Tensile Strength
            KryptonLabel lblTensile = new KryptonLabel();
            lblTensile.Text = "Tensile Strength (MPa):";
            lblTensile.Location = new Point(10, currentY);
            lblTensile.StateCommon.ShortText.Color1 = Color.White;
            controlPanel.Controls.Add(lblTensile);

            nudTensile = new KryptonNumericUpDown();
            nudTensile.Location = new Point(10, currentY + 20);
            nudTensile.Width = controlWidth - 30;
            nudTensile.DecimalPlaces = 2;
            nudTensile.Minimum = 0;
            nudTensile.Maximum = 500;
            nudTensile.Value = 10.0m;
            controlPanel.Controls.Add(nudTensile);

            currentY += verticalSpacing + 20;

            // Friction Angle
            KryptonLabel lblFriction = new KryptonLabel();
            lblFriction.Text = "Friction Angle (°):";
            lblFriction.Location = new Point(10, currentY);
            lblFriction.StateCommon.ShortText.Color1 = Color.White;
            controlPanel.Controls.Add(lblFriction);

            nudFriction = new KryptonNumericUpDown();
            nudFriction.Location = new Point(10, currentY + 20);
            nudFriction.Width = controlWidth - 30;
            nudFriction.DecimalPlaces = 1;
            nudFriction.Minimum = 0;
            nudFriction.Maximum = 90;
            nudFriction.Value = 30.0m;
            controlPanel.Controls.Add(nudFriction);

            currentY += verticalSpacing + 20;

            // Cohesion
            KryptonLabel lblCohesion = new KryptonLabel();
            lblCohesion.Text = "Cohesion (MPa):";
            lblCohesion.Location = new Point(10, currentY);
            lblCohesion.StateCommon.ShortText.Color1 = Color.White;
            controlPanel.Controls.Add(lblCohesion);

            nudCohesion = new KryptonNumericUpDown();
            nudCohesion.Location = new Point(10, currentY + 20);
            nudCohesion.Width = controlWidth - 30;
            nudCohesion.DecimalPlaces = 2;
            nudCohesion.Minimum = 0;
            nudCohesion.Maximum = 100;
            nudCohesion.Value = 5.0m;
            controlPanel.Controls.Add(nudCohesion);

            currentY += verticalSpacing + 20;

            // Simulation models
            KryptonLabel lblModels = new KryptonLabel();
            lblModels.Text = "Simulation Models:";
            lblModels.Location = new Point(10, currentY);
            lblModels.StateCommon.ShortText.Color1 = Color.White;
            controlPanel.Controls.Add(lblModels);

            currentY += 20;

            chkElastic = new KryptonCheckBox();
            chkElastic.Text = "Elastic Model";
            chkElastic.Location = new Point(20, currentY);
            chkElastic.Width = controlWidth - 20;
            chkElastic.Checked = true;
            controlPanel.Controls.Add(chkElastic);

            currentY += 20;

            chkPlastic = new KryptonCheckBox();
            chkPlastic.Text = "Plastic Model";
            chkPlastic.Location = new Point(20, currentY);
            chkPlastic.Width = controlWidth - 20;
            chkPlastic.Checked = true;
            controlPanel.Controls.Add(chkPlastic);

            currentY += 20;

            chkBrittle = new KryptonCheckBox();
            chkBrittle.Text = "Brittle Model";
            chkBrittle.Location = new Point(20, currentY);
            chkBrittle.Width = controlWidth - 20;
            chkBrittle.Checked = true;
            controlPanel.Controls.Add(chkBrittle);

            currentY += verticalSpacing;

            // GPU Acceleration
            chkUseGPU = new KryptonCheckBox();
            chkUseGPU.Text = "Use GPU";
            chkUseGPU.Location = new Point(10, currentY);
            chkUseGPU.Width = controlWidth - 20;
            chkUseGPU.Enabled = false;
            controlPanel.Controls.Add(chkUseGPU);
            currentY += verticalSpacing;
            chkDebugMode = new KryptonCheckBox();
            
            currentY += verticalSpacing;
            this.chkDebugMode.Text = "Debug Mode (Fast Failure)";
            this.chkDebugMode.AutoSize = true;
            this.chkDebugMode.Location = new Point(10, currentY);
            this.chkDebugMode.Checked = false;
            this.chkDebugMode.ForeColor = Color.Red;
            this.chkDebugMode.CheckedChanged += (s, e) => {
                if (chkDebugMode.Checked)
                {
                    Logger.Log("Debug mode enabled: Accelerated damage and lower thresholds.\n" +
                                    "This is for TESTING ONLY - not realistic material behavior." +
                                    " !!!Debug Mode!!!");
                }
            };
            currentY += verticalSpacing;
            controlPanel.Controls.Add(chkDebugMode);
            // Buttons
            btnRun = new KryptonButton();
            btnRun.Text = "Run Simulation";
            btnRun.Location = new Point(10, currentY);
            btnRun.Width = controlWidth - 30;
            btnRun.Values.Image = CreateStartIcon();
            btnRun.Click += OnRun;
            controlPanel.Controls.Add(btnRun);

            currentY += 40;

            btnPause = new KryptonButton();
            btnPause.Text = "Pause";
            btnPause.Location = new Point(10, currentY);
            btnPause.Width = controlWidth - 30;
            btnPause.Values.Image = CreatePauseIcon();
            btnPause.Enabled = false;
            btnPause.Click += OnPause;
            controlPanel.Controls.Add(btnPause);

            currentY += 40;

            btnCancel = new KryptonButton();
            btnCancel.Text = "Cancel";
            btnCancel.Location = new Point(10, currentY);
            btnCancel.Width = controlWidth - 30;
            btnCancel.Values.Image = CreateCancelIcon();
            btnCancel.Enabled = false;
            btnCancel.Click += OnCancel;
            controlPanel.Controls.Add(btnCancel);

            currentY += 40;

            btnContinue = new KryptonButton();
            btnContinue.Text = "Continue After Failure";
            btnContinue.Location = new Point(10, currentY);
            btnContinue.Width = controlWidth - 30;
            btnContinue.Values.Image = CreateContinueIcon();
            btnContinue.Visible = false;
            btnContinue.Click += OnContinue;
            controlPanel.Controls.Add(btnContinue);

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

            // Add picture box to panel
            visualPanel.Controls.Add(pictureBoxSimulation);

            // Add panels to tab page
            tabSimulation.Controls.Add(visualPanel);
            tabSimulation.Controls.Add(scrollContainer);
            AddAutoCalculateControls(controlPanel);
        }

        private void PictureBoxSimulation_Paint(object sender, PaintEventArgs e)
        {
            // Clear the background
            e.Graphics.Clear(Color.FromArgb(20, 20, 20));

            // If triaxial visualization is available, use it
            if (triaxialVisualization != null)
            {
                try
                {
                    // Update visualization parameters
                    StressAxis axis = (StressAxis)cmbStressAxis.SelectedIndex;
                    double confiningP = (double)nudConfiningP.Value;
                    double axialP;

                    // If simulation is running, use current stress
                    if (simulationRunning && (cpuSim != null || gpuSim != null))
                    {
                        axialP = Math.Max(confiningP, GetCurrentStress());
                    }
                    // If simulation has run before, use last value
                    else if (stressStrainCurve.Count > 0)
                    {
                        axialP = Math.Max(confiningP, stressStrainCurve[stressStrainCurve.Count - 1].Y);
                    }
                    // Otherwise, use initial pressure
                    else
                    {
                        axialP = (double)nudInitialP.Value;
                    }

                    // Update the view transformation and pressure parameters
                    triaxialVisualization.SetViewTransformation(rotationX, rotationY, zoom, pan);
                    triaxialVisualization.SetPressureParameters(confiningP, axialP, axis);

                    // Render the visualization
                    triaxialVisualization.Render(e.Graphics, pictureBoxSimulation.Width, pictureBoxSimulation.Height);

                    // Add failure detection information if applicable
                    if (failureDetected)
                    {
                        using (Font font = new Font("Segoe UI", 12, FontStyle.Bold))
                        using (SolidBrush failureBrush = new SolidBrush(Color.FromArgb(255, 100, 100)))
                        {
                            e.Graphics.DrawString("FAILURE DETECTED", font, failureBrush, 10, pictureBoxSimulation.Height - 30);
                        }
                    }

                    return; // Visualization complete
                }
                catch (Exception ex)
                {
                    // Log error and fall back to simple visualization
                    Logger.Log($"[TriaxialSimulationForm] Error in triaxial visualization: {ex.Message}");
                }
            }

            // Fall back to basic info or simple visualization
            if (!simulationRunning && stressStrainCurve.Count == 0)
            {
                // Show information about the simulation
                using (Font font = new Font("Segoe UI", 12, FontStyle.Regular))
                using (SolidBrush brush = new SolidBrush(Color.White))
                {
                    string message = "Configure simulation parameters and click 'Run Simulation'";
                    SizeF textSize = e.Graphics.MeasureString(message, font);
                    e.Graphics.DrawString(message, font, brush,
                        (pictureBoxSimulation.Width - textSize.Width) / 2,
                        (pictureBoxSimulation.Height - textSize.Height) / 2 - 40);

                    // Show current parameters
                    double confiningP = (double)nudConfiningP.Value;
                    double initialP = (double)nudInitialP.Value;
                    double finalP = (double)nudFinalP.Value;
                    string axisName = cmbStressAxis.SelectedItem?.ToString() ?? "Z";

                    string paramsInfo = $"Confining: {confiningP:F1} MPa, Initial: {initialP:F1} MPa,\nFinal: {finalP:F1} MPa, Axis: {axisName}";
                    SizeF paramSize = e.Graphics.MeasureString(paramsInfo, font);
                    e.Graphics.DrawString(paramsInfo, font, brush,
                        (pictureBoxSimulation.Width - paramSize.Width) / 2,
                        pictureBoxSimulation.Height / 2);
                }

                return;
            }

            // If triaxial visualization failed but we have simulation data, use the simple visualization
            if (stressStrainCurve.Count > 0 || simulationRunning)
            {
                DrawTriaxialVisualization(e.Graphics);
            }
        }
        public void UpdateTriaxialVisualization()
        {
            if (mainForm.volumeLabels == null || densityVolume == null || selectedMaterial == null)
                return;

            // If already initialized, dispose it first
            if (triaxialVisualization != null)
            {
                triaxialVisualization.Dispose();
                triaxialVisualization = null;
            }

            // Initialize with current data
            InitializeTriaxialVisualization();

            // Update the simulation visualization
            if (!isInitializing)
                pictureBoxSimulation.Invalidate();
        }

        // Get current stress from active simulator (either CPU or GPU)
        private double GetCurrentStress()
        {
            if (cpuSim != null)
                return TriaxialSimulatorExtension.GetCurrentStress(cpuSim);
            if (gpuSim != null)
                return gpuSim.CurrentStress;
            return 0.0;
        }

        // Get current strain from active simulator (either CPU or GPU)
        private double GetCurrentStrain()
        {
            if (cpuSim != null)
                return TriaxialSimulatorExtension.GetCurrentStrain(cpuSim);
            if (gpuSim != null)
                return gpuSim.CurrentStrain;
            return 0.0;
        }

        private void DrawTriaxialVisualization(Graphics g)
        {
            int width = pictureBoxSimulation.Width;
            int height = pictureBoxSimulation.Height;
            int margin = 50;
            int cubeSize = Math.Min(width, height) - 2 * margin;

            // Center point for the cube
            int centerX = width / 2;
            int centerY = height / 2;

            // Get current stress values
            double confiningP = (double)nudConfiningP.Value;
            double axialP = confiningP;

            if (simulationRunning && (cpuSim != null || gpuSim != null))
            {
                // Get current axial stress from simulator
                axialP = Math.Max(confiningP, GetCurrentStress());
            }
            else if (stressStrainCurve.Count > 0)
            {
                // Use the last value from the curve
                axialP = Math.Max(confiningP, stressStrainCurve[stressStrainCurve.Count - 1].Y);
            }

            // Scale for visualization (exaggerate the deformation)
            double deformationScale = 0.3;
            double axialRatio = 1.0 - (axialP - confiningP) / axialP * deformationScale;
            axialRatio = Math.Max(0.5, axialRatio); // Limit deformation

            // Determine which axis is the stress axis
            int axisIndex = cmbStressAxis.SelectedIndex;

            // Create isometric projection matrices
            Matrix isoMatrix = new Matrix();
            isoMatrix.Rotate(30);
            isoMatrix.Scale(1.0f, 0.5f);

            // Create cube corners in 3D (adjusted for stress axis)
            PointF3D[] corners = new PointF3D[8];

            // Adjusted sizes based on stress
            float baseSize = cubeSize / 2.0f;
            float xSize = baseSize;
            float ySize = baseSize;
            float zSize = baseSize;

            // Apply deformation based on stress axis
            switch (axisIndex)
            {
                case 0: // X axis
                    xSize *= (float)axialRatio;
                    break;
                case 1: // Y axis
                    ySize *= (float)axialRatio;
                    break;
                default: // Z axis
                    zSize *= (float)axialRatio;
                    break;
            }

            // Define corners of the deformed cube
            corners[0] = new PointF3D(-xSize, -ySize, -zSize);
            corners[1] = new PointF3D(xSize, -ySize, -zSize);
            corners[2] = new PointF3D(xSize, ySize, -zSize);
            corners[3] = new PointF3D(-xSize, ySize, -zSize);
            corners[4] = new PointF3D(-xSize, -ySize, zSize);
            corners[5] = new PointF3D(xSize, -ySize, zSize);
            corners[6] = new PointF3D(xSize, ySize, zSize);
            corners[7] = new PointF3D(-xSize, ySize, zSize);

            // Project to 2D
            PointF[] projectedCorners = new PointF[8];
            for (int i = 0; i < 8; i++)
            {
                PointF pt = new PointF(corners[i].X, corners[i].Y);
                isoMatrix.TransformPoints(new PointF[] { pt });
                projectedCorners[i] = new PointF(
                    centerX + pt.X,
                    centerY + pt.Y - corners[i].Z / 2
                );
            }

            // Draw the cube faces with semi-transparency
            using (SolidBrush faceBrush = new SolidBrush(Color.FromArgb(100, 100, 180, 220)))
            using (Pen edgePen = new Pen(Color.White, 2))
            {
                // Back face
                g.FillPolygon(faceBrush, new PointF[] {
                    projectedCorners[0], projectedCorners[1],
                    projectedCorners[2], projectedCorners[3]
                });

                // Left face
                g.FillPolygon(faceBrush, new PointF[] {
                    projectedCorners[0], projectedCorners[3],
                    projectedCorners[7], projectedCorners[4]
                });

                // Bottom face
                g.FillPolygon(faceBrush, new PointF[] {
                    projectedCorners[0], projectedCorners[1],
                    projectedCorners[5], projectedCorners[4]
                });

                // Right face
                g.FillPolygon(faceBrush, new PointF[] {
                    projectedCorners[1], projectedCorners[2],
                    projectedCorners[6], projectedCorners[5]
                });

                // Top face
                g.FillPolygon(faceBrush, new PointF[] {
                    projectedCorners[3], projectedCorners[2],
                    projectedCorners[6], projectedCorners[7]
                });

                // Front face
                g.FillPolygon(faceBrush, new PointF[] {
                    projectedCorners[4], projectedCorners[5],
                    projectedCorners[6], projectedCorners[7]
                });

                // Draw the edges
                // Bottom square
                g.DrawLine(edgePen, projectedCorners[0], projectedCorners[1]);
                g.DrawLine(edgePen, projectedCorners[1], projectedCorners[2]);
                g.DrawLine(edgePen, projectedCorners[2], projectedCorners[3]);
                g.DrawLine(edgePen, projectedCorners[3], projectedCorners[0]);

                // Top square
                g.DrawLine(edgePen, projectedCorners[4], projectedCorners[5]);
                g.DrawLine(edgePen, projectedCorners[5], projectedCorners[6]);
                g.DrawLine(edgePen, projectedCorners[6], projectedCorners[7]);
                g.DrawLine(edgePen, projectedCorners[7], projectedCorners[4]);

                // Connecting lines
                g.DrawLine(edgePen, projectedCorners[0], projectedCorners[4]);
                g.DrawLine(edgePen, projectedCorners[1], projectedCorners[5]);
                g.DrawLine(edgePen, projectedCorners[2], projectedCorners[6]);
                g.DrawLine(edgePen, projectedCorners[3], projectedCorners[7]);
            }

            // Draw stress information
            using (Font font = new Font("Segoe UI", 10, FontStyle.Bold))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            using (SolidBrush highlightBrush = new SolidBrush(Color.LightGreen))
            {
                int textY = height - margin - 80;
                string[] axisNames = new string[] { "X", "Y", "Z" };

                g.DrawString("Triaxial Stress Visualization:", font, highlightBrush, margin, textY);
                g.DrawString($"Confining Pressure: {confiningP:F2} MPa", font, textBrush, margin, textY + 20);
                g.DrawString($"Axial Pressure ({axisNames[axisIndex]} axis): {axialP:F2} MPa", font, highlightBrush, margin, textY + 40);
                g.DrawString($"Difference: {axialP - confiningP:F2} MPa", font, textBrush, margin, textY + 60);

                // If failure detected, show that
                if (failureDetected)
                {
                    using (SolidBrush failureBrush = new SolidBrush(Color.FromArgb(255, 100, 100)))
                    {
                        g.DrawString("FAILURE DETECTED", new Font("Segoe UI", 12, FontStyle.Bold),
                            failureBrush, margin, textY + 90);
                    }
                }
            }
        }

        #endregion

        #region Results Tab Implementation

        private void InitializeResultsTab()
        {
            // Create the main panel
            TableLayoutPanel tl = new TableLayoutPanel();
            tl.Dock = DockStyle.Fill;
            tl.RowCount = 3;
            tl.ColumnCount = 1;
            tl.Padding = new Padding(10);
            tl.RowStyles.Add(new RowStyle(SizeType.Percent, 70));
            tl.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            tl.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));

            // Create the progress bar
            progressBar = new KryptonProgressBar();
            progressBar.Dock = DockStyle.Fill;
            progressBar.Minimum = 0;
            progressBar.Maximum = 100;
            progressBar.Value = 0;

            // Create the status label
            lblStatus = new KryptonLabel();
            lblStatus.Text = "Ready";
            lblStatus.Dock = DockStyle.Fill;
            lblStatus.StateCommon.ShortText.Color1 = Color.White;

            // Add controls to layout
            tl.Controls.Add(progressBar, 0, 1);
            tl.Controls.Add(lblStatus, 0, 2);

            // Create the results extension
            resultsExtension = new TriaxialResultsExtension(this);
            resultsExtension.Initialize(tabResults);

            // Add the layout to the tab
            tabResults.Controls.Add(tl);
        }

        #endregion

        #region Simulation Implementation

        private void ProbeGpu()
        {
            Task.Run(() => {
                bool ok = TriaxialSimulatorGPU.IsGpuAvailable();
                Invoke((Action)(() =>
                {
                    chkUseGPU.Enabled = ok;
                    if (!ok) chkUseGPU.Text = "Use GPU (none)";
                    Logger.Log($"[TriaxialSimulationForm] GPU available={ok}");
                }));
            });
        }
        private void TriaxialSimulationForm_Load(object sender, EventArgs e)
        {
            // Call this after material and density are initialized
            InitializeTriaxialVisualization();
        }
        private void OnDensityChanged()
        {
            if (chkAutoCalculateProps != null && chkAutoCalculateProps.Checked)
                 {
                     AutoCalculateMaterialProperties();
                 }
                //Update the visualization if it exists
                UpdateTriaxialVisualization();
        }
        private void OnRun(object sender, EventArgs e)
        {
            if (simulationRunning) return;

            // Create new CancellationTokenSource
            cts = new CancellationTokenSource();

            // Prepare data
            width = mainForm.GetWidth();
            height = mainForm.GetHeight();
            depth = mainForm.GetDepth();

            if (mainForm.volumeLabels == null || mainForm.volumeData == null)
            {
                MessageBox.Show("No data loaded.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            InitializeDamageArray();
            // Ensure we have a valid density volume
            if (densityVolume == null)
            {
                InitializeHomogeneousDensityVolume();
                if (densityVolume == null)
                {
                    MessageBox.Show("Failed to initialize density volume.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }

            // Check if we have variable density or we need to use homogeneous density
            if (!hasDensityVariation)
            {
                Logger.Log("[TriaxialSimulationForm] No density variation found - using homogeneous density");
                // Make sure the density volume has the correct density values throughout
                UpdateHomogeneousDensityVolume();
            }
            else
            {
                Logger.Log("[TriaxialSimulationForm] Using variable density for simulation");
            }

            // Read UI parameters
            byte matID = selectedMaterialID;
            StressAxis axis = (StressAxis)cmbStressAxis.SelectedIndex;
            double confP = (double)nudConfiningP.Value;
            double p0 = (double)nudInitialP.Value;
            double p1 = (double)nudFinalP.Value;

            // The number of pressure increments - use a much higher value (100+) for smoother curve
            int pressureIncrements = (int)nudSteps.Value * 40; // Multiply by 40 to get smoother curves

            // Fewer time steps per increment to maintain reasonable performance
            int stepsPerIncrement = 5; // Reduced from 200 to 5

            double E = (double)nudE.Value; // Keep as GPa - simulator will convert
            double nu = (double)nudNu.Value;
            bool ue = chkElastic.Checked;
            bool up = chkPlastic.Checked;
            bool ub = chkBrittle.Checked;
            double ts = (double)nudTensile.Value; // Keep as MPa - simulator will convert
            double phi = (double)nudFriction.Value;
            double co = (double)nudCohesion.Value; // Keep as MPa - simulator will convert
            bool useGpu = chkUseGPU.Checked && chkUseGPU.Enabled;

            // Initialize or update the triaxial visualization with current parameters
            if (triaxialVisualization == null)
            {
                InitializeTriaxialVisualization();
            }

            // Set the pressure parameters in the visualization
            if (triaxialVisualization != null)
            {
                triaxialVisualization.SetPressureParameters(confP, p0, axis);
                triaxialVisualization.SetViewTransformation(rotationX, rotationY, zoom, pan);
            }

            // Reset UI state
            stressStrainCurve.Clear();
            progressBar.Value = 0;
            lblStatus.Text = "Starting simulation with " + pressureIncrements + " pressure increments...";
            simulationRunning = true;
            simulationPaused = false;
            failureDetected = false;
            failureStep = -1;
            peakStress = 0;
            btnPause.Enabled = btnCancel.Enabled = true;
            btnContinue.Visible = false;

            // Switch to the Results tab
            tabControl.SelectedTab = tabResults;

            // Cleanup any existing simulators
            cpuSim?.Dispose();
            gpuSim?.Dispose();
            cpuSim = null;
            gpuSim = null;

            // Log density information for debugging
            LogDensityStatistics(matID);
            bool debugMode = chkDebugMode.Checked;
            if (useGpu)
            {
                // GPU simulation code
                Logger.Log("[TriaxialSimulationForm] Starting GPU simulation");

                // Create wrapped byte array for GPU simulator
                byte[,,] labelArray = new byte[width, height, depth];
                for (int z = 0; z < depth; z++)
                    for (int y = 0; y < height; y++)
                        for (int x = 0; x < width; x++)
                            labelArray[x, y, z] = mainForm.volumeLabels[x, y, z];

                gpuSim = new TriaxialSimulatorGPU(
                    width, height, depth,
                    (float)mainForm.GetPixelSize(),
                    labelArray,
                    densityVolume,
                    matID,
                    E, nu,
                    ue, up, ub,
                    ts, phi, co, debugMode
                );

                gpuSim.ProgressUpdated += OnProgress;
                gpuSim.FailureDetected += OnFailure;
                gpuSim.SimulationCompleted += OnComplete;

                gpuSim.StartSimulationAsync(
                    confP, p0, p1, pressureIncrements, axis, stepsPerIncrement, cts.Token
                );
            }
            else
            {
                // CPU simulation code
                Logger.Log("[TriaxialSimulationForm] Starting CPU simulation");

                cpuSim = new TriaxialSimulator(
                    width, height, depth,
                    (float)mainForm.GetPixelSize(),
                    mainForm.volumeLabels,
                    densityVolume,
                    matID,
                    confP, p0, p1, pressureIncrements, axis,
                    ue, up, ub,
                    ts, phi, co,
                    E, nu,
                    stepsPerIncrement, debugMode
                );

                cpuSim.ProgressUpdated += OnProgress;
                cpuSim.FailureDetected += OnFailure;
                cpuSim.SimulationCompleted += OnComplete;

                cpuSim.StartSimulationAsync();
            }

            // Force redraw of simulation visualization
            pictureBoxSimulation.Invalidate();
        }
        private void LogDensityStatistics(byte matID)
        {
            if (hasDensityVariation)
            {
                // Calculate statistics for the density to verify variable density is being used
                double minDensity = double.MaxValue;
                double maxDensity = double.MinValue;
                double avgDensity = 0.0;
                int count = 0;

                for (int z = 0; z < depth; z++)
                {
                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            if (mainForm.volumeLabels[x, y, z] == matID)
                            {
                                float density = densityVolume[x, y, z];
                                minDensity = Math.Min(minDensity, density);
                                maxDensity = Math.Max(maxDensity, density);
                                avgDensity += density;
                                count++;
                            }
                        }
                    }
                }

                if (count > 0)
                {
                    avgDensity /= count;
                }

                Logger.Log($"[TriaxialSimulationForm] Density stats: Min={minDensity:F1}, Max={maxDensity:F1}, Avg={avgDensity:F1} kg/m³");
            }
            else
            {
                Logger.Log($"[TriaxialSimulationForm] Using homogeneous density: {baseDensity:F1} kg/m³");
            }
        }
        private void OnProgress(object sender, TriaxialSimulationProgressEventArgs e)
        {
            if (InvokeRequired)
            {
                Invoke((Action)(() => OnProgress(sender, e)));
                return;
            }

            // Get current strain and stress from active simulator
            double currentStrain = GetCurrentStrain();
            double currentStress = GetCurrentStress();
            UpdateDamageData();
            // Store data point
            stressStrainCurve.Add(new PointF((float)currentStrain, (float)currentStress));

            // Update result extension with current data
            resultsExtension?.UpdateData(
                stressStrainCurve,
                stressStrainCurve.Count > 0 ? stressStrainCurve.Max(p => p.Y) : 0,
                currentStrain,
                failureDetected,
                failureStep,
                damage // This is the damage field array from the simulator
            );

            // Update progress indicators
            progressBar.Value = e.Percent;
            lblStatus.Text = e.Status;

            // Update the triaxial visualization with current stress values
            if (triaxialVisualization != null)
            {
                // Get stress axis
                StressAxis axis = (StressAxis)cmbStressAxis.SelectedIndex;
                double confiningP = (double)nudConfiningP.Value;

                // Update pressure values in the visualization
                triaxialVisualization.SetPressureParameters(confiningP, currentStress, axis);
            }

            // Update simulation visualization
            pictureBoxSimulation.Invalidate();
        }
        private void UpdateDamageData()
        {
            try
            {
                // Handle CPU simulator
                if (cpuSim != null)
                {
                    // Use reflection to access the private damage field
                    FieldInfo damageField = typeof(TriaxialSimulator).GetField("damage",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                    if (damageField != null)
                    {
                        double[,,] simulatorDamage = (double[,,])damageField.GetValue(cpuSim);
                        if (simulatorDamage != null)
                        {
                            // Copy the damage data
                            Array.Copy(simulatorDamage, damage, damage.Length);
                        }
                    }
                }
                // Handle GPU simulator
                else if (gpuSim != null)
                {
                    // Copy damage data from GPU simulator
                    // We need to fetch it from device memory first
                    MethodInfo copyToCPUMethod = gpuSim.GetType().GetMethod("CopyDamageToCPU",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                    if (copyToCPUMethod != null)
                    {
                        copyToCPUMethod.Invoke(gpuSim, new object[] { damage });
                    }
                    else
                    {
                        // Alternative: access _damage field directly using reflection
                        FieldInfo damageField = gpuSim.GetType().GetField("_damage",
                            BindingFlags.NonPublic | BindingFlags.Instance);

                        if (damageField != null)
                        {
                            double[,,] gpuDamage = (double[,,])damageField.GetValue(gpuSim);
                            if (gpuDamage != null)
                            {
                                Array.Copy(gpuDamage, damage, damage.Length);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[TriaxialSimulationForm] Error updating damage data: {ex.Message}");
            }
        }
        private void OnFailure(object sender, FailureDetectedEventArgs e)
        {
            Logger.Log($"[TriaxialSimulationForm] Failure at step {e.CurrentStep}");

            if (InvokeRequired)
            {
                Invoke((Action)(() => OnFailure(sender, e)));
                return;
            }
            UpdateDamageData();
            btnPause.Enabled = false;
            btnContinue.Visible = true;
            lblStatus.Text = "Failure detected — click Continue or Cancel";
            simulationPaused = true;
            failureDetected = true;
            failureStep = e.CurrentStep;

            // Find peak stress
            if (stressStrainCurve.Count > 0)
            {
                peakStress = stressStrainCurve.Max(p => p.Y);

                // Get the failure stress value
                double failureStress = e.CurrentStress > 0 ? e.CurrentStress : stressStrainCurve[failureStep].Y;

                // Get simulation parameters
                double confiningPressure = 0;
                double frictionAngle = 0;
                double cohesion = 0;

                // Get current parameters from UI controls
                if (nudConfiningP != null) confiningPressure = (double)nudConfiningP.Value;
                if (nudFriction != null) frictionAngle = (double)nudFriction.Value;
                if (nudCohesion != null) cohesion = (double)nudCohesion.Value;

                // Force-draw the tangent line on the Mohr-Coulomb chart
                if (resultsExtension != null)
                {
                    // Force the chart to update with the failure data
                    Type extensionType = resultsExtension.GetType();

                    // First try to call UpdateData to refresh the chart
                    MethodInfo updateDataMethod = extensionType.GetMethod("UpdateData");
                    if (updateDataMethod != null)
                    {
                        updateDataMethod.Invoke(resultsExtension, new object[] {
            stressStrainCurve,
            peakStress,
            e.CurrentStrain,
            failureDetected,
            failureStep,
            damage
        });
                    }

                    // Then force direct drawing of the tangent line
                    // FIXED: Make sure the method name matches exactly what's in TriaxialResultsExtension
                    MethodInfo drawTangentMethod = extensionType.GetMethod("DrawTangentOnMohrChart",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                    if (drawTangentMethod != null)
                    {
                        drawTangentMethod.Invoke(resultsExtension, new object[] {
            confiningPressure,
            failureStress,
            frictionAngle,
            cohesion
        });
                    }
                    else
                    {
                        // FIXED: Update error message to match the correct method name
                        Logger.Log("[TriaxialSimulationForm] DrawTangentOnMohrChart method not found");
                    }
                }

                // Update results extension with failure data
                resultsExtension?.UpdateData(
                    stressStrainCurve,
                    peakStress,
                    e.CurrentStrain,
                    failureDetected,
                    failureStep,
                    damage // This is the damage field array from the simulator
                );
            }

            // Update simulation visualization
            pictureBoxSimulation.Invalidate();
        }

        private void OnComplete(object sender, TriaxialSimulationCompleteEventArgs e)
        {
            Logger.Log("[TriaxialSimulationForm] Simulation complete");

            if (InvokeRequired)
            {
                Invoke((Action)(() => OnComplete(sender, e)));
                return;
            }
            UpdateDamageData();
            simulationRunning = false;
            simulationPaused = false;
            btnPause.Enabled = btnCancel.Enabled = false;
            btnContinue.Visible = false;

            // Store the failure detection state
            failureDetected = e.FailureDetected;
            if (e.FailureDetected)
                failureStep = e.FailureStep;

            // Update chart with final data if available
            if (e.AxialStrain.Length > 0 && e.AxialStress.Length > 0)
            {
                stressStrainCurve.Clear();

                for (int i = 0; i < e.AxialStrain.Length; i++)
                {
                    stressStrainCurve.Add(new PointF((float)e.AxialStrain[i], (float)e.AxialStress[i]));
                }

                // Store peak stress
                peakStress = e.PeakStress;

                // Update results extension with final data
                resultsExtension?.UpdateData(
                    stressStrainCurve,
                    peakStress,
                    e.PeakStrain,
                    failureDetected,
                    failureStep,
                    damage 
                );
            }

            lblStatus.Text = e.FailureDetected
                ? $"Done - Failure at step {e.FailureStep}"
                : "Simulation completed successfully";

            // Update simulation visualization
            pictureBoxSimulation.Invalidate();
        }

        private void OnPause(object sender, EventArgs e)
        {
            if (!simulationRunning) return;

            simulationPaused = true;
            lblStatus.Text = "Paused";

            if (cpuSim != null) cpuSim.PauseSimulation();
            if (gpuSim != null) gpuSim.PauseSimulation();

            Logger.Log("[TriaxialSimulationForm] Paused");
        }

        private void OnContinue(object sender, EventArgs e)
        {
            simulationPaused = false;
            btnContinue.Visible = false;
            lblStatus.Text = "Continuing after failure";

            if (cpuSim != null) cpuSim.ContinueAfterFailure();
            if (gpuSim != null) gpuSim.ContinueAfterFailure();

            Logger.Log("[TriaxialSimulationForm] Continued after failure");
        }

        private void OnCancel(object sender, EventArgs e)
        {
            if (!simulationRunning) return;

            Logger.Log("[TriaxialSimulationForm] Cancelling simulation");

            cts?.Cancel();
            simulationRunning = false;
            simulationPaused = false;

            if (cpuSim != null) cpuSim.CancelSimulation();
            if (gpuSim != null) gpuSim.CancelSimulation();

            btnPause.Enabled = btnCancel.Enabled = false;
            btnContinue.Visible = false;
            lblStatus.Text = "Cancelled";
        }

        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            Logger.Log("[TriaxialSimulationForm] Closing");

            cts?.Cancel();

            if (cpuSim != null)
            {
                cpuSim.CancelSimulation();
                cpuSim.Dispose();
                cpuSim = null;
            }

            if (gpuSim != null)
            {
                gpuSim.CancelSimulation();
                gpuSim.Dispose();
                gpuSim = null;
            }
        }

        #endregion

        #region Icon Creation Methods

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
        public Point3D FindMaxDamagePoint()
        {
            if (damage == null)
                return new Point3D(0, 0, 0);

            double maxDamage = 0.0;
            int maxX = 0, maxY = 0, maxZ = 0;
            byte materialID = selectedMaterialID;

            // Find the point with maximum damage
            for (int z = 0; z < mainForm.GetDepth(); z++)
            {
                for (int y = 0; y < mainForm.GetHeight(); y++)
                {
                    for (int x = 0; x < mainForm.GetWidth(); x++)
                    {
                        if (mainForm.volumeLabels[x, y, z] == materialID && damage[x, y, z] > maxDamage)
                        {
                            maxDamage = damage[x, y, z];
                            maxX = x;
                            maxY = y;
                            maxZ = z;
                        }
                    }
                }
            }

            return new Point3D(maxX, maxY, maxZ);
        }
        private Image CreateStartIcon()
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

        private Image CreatePauseIcon()
        {
            Bitmap bmp = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                // Draw two pause bars
                using (SolidBrush brush = new SolidBrush(Color.White))
                {
                    g.FillRectangle(brush, 4, 3, 3, 10);
                    g.FillRectangle(brush, 9, 3, 3, 10);
                }
            }
            return bmp;
        }

        private Image CreateCancelIcon()
        {
            Bitmap bmp = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                // Draw an X
                using (Pen pen = new Pen(Color.Red, 2))
                {
                    g.DrawLine(pen, 3, 3, 13, 13);
                    g.DrawLine(pen, 13, 3, 3, 13);
                }
            }
            return bmp;
        }
        /// <summary>
        /// Adds auto-calculate properties button and checkbox to the simulation tab
        /// </summary>
        private void AddAutoCalculateControls(KryptonPanel panel)
        {
            // Create the auto-calculate button - place it immediately after Cohesion control
            // Find the Cohesion control first to position our new controls after it
            Control cohesionControl = null;
            int currentMaxY = 0;

            // Find the last control in the vertical layout
            foreach (Control c in panel.Controls)
            {
                // Track the bottom-most control
                int controlBottom = c.Location.Y + c.Height;
                if (controlBottom > currentMaxY)
                {
                    currentMaxY = controlBottom;
                }

                // Also specifically find the cohesion control
                if (c == nudCohesion)
                {
                    cohesionControl = c;
                }
            }

            // Place new controls after simulation models section with extra spacing
            int buttonY = currentMaxY + 50; // Make sure there's plenty of space

            // Create the auto-calculate button
            btnAutoCalculateProps = new KryptonButton();
            btnAutoCalculateProps.Text = "Auto-Calculate Properties";
            btnAutoCalculateProps.Location = new Point(10, buttonY);
            btnAutoCalculateProps.Width = 200;
            btnAutoCalculateProps.Height = 30;
            btnAutoCalculateProps.Click += BtnAutoCalculateProps_Click;
            panel.Controls.Add(btnAutoCalculateProps);

            // Position checkbox below button with some spacing
            int checkboxY = buttonY + btnAutoCalculateProps.Height + 10;

            // Create the auto-update checkbox
            chkAutoCalculateProps = new KryptonCheckBox();
            chkAutoCalculateProps.Text = "Auto-update with density changes";
            chkAutoCalculateProps.Location = new Point(10, checkboxY);
            chkAutoCalculateProps.Width = 200;
            chkAutoCalculateProps.Height = 24;
            chkAutoCalculateProps.CheckedChanged += ChkAutoCalculateProps_CheckedChanged;
            panel.Controls.Add(chkAutoCalculateProps);

            // Make sure the panel height is updated to include our new controls
            int newControlsBottom = checkboxY + chkAutoCalculateProps.Height + 20;
            if (panel.Height < newControlsBottom)
            {
                panel.Height = newControlsBottom;
            }

            // Log for debugging
            Logger.Log($"[TriaxialSimulationForm] Added Auto-Calculate button at Y={buttonY}, checkbox at Y={checkboxY}");
        }
        /// <summary>
        /// Event handler for the auto-calculate button
        /// </summary>
        private void BtnAutoCalculateProps_Click(object sender, EventArgs e)
        {
            AutoCalculateMaterialProperties();
        }

        /// <summary>
        /// Event handler for the auto-calculate checkbox
        /// </summary>
        private void ChkAutoCalculateProps_CheckedChanged(object sender, EventArgs e)
        {
            if (chkAutoCalculateProps.Checked)
            {
                AutoCalculateMaterialProperties();
            }
        }

        /// <summary>
        /// Auto-calculates Young's modulus and Poisson's ratio based on material density
        /// </summary>
        private void AutoCalculateMaterialProperties()
        {
            try
            {
                if (selectedMaterial == null || baseDensity <= 0)
                {
                    MessageBox.Show("Please select a material and set a valid density first.",
                        "Material Properties", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Get the current density
                double density = baseDensity; // kg/m³

                // Calculate Young's modulus (E) in GPa based on density
                // This is a simplified empirical formula based on common materials
                // It assumes that stiffer materials tend to be denser
                double youngModulus = CalculateYoungsModulus(density);

                // Calculate Poisson's ratio based on density
                // Most materials have Poisson's ratio between 0.2 and 0.45
                double poissonRatio = CalculatePoissonsRatio(density);

                // Update the UI controls
                nudE.Value = (decimal)Math.Min(Math.Max(youngModulus, (double)nudE.Minimum), (double)nudE.Maximum);
                nudNu.Value = (decimal)Math.Min(Math.Max(poissonRatio, (double)nudNu.Minimum), (double)nudNu.Maximum);

                // Show a message with the calculated values
                Logger.Log($"[TriaxialSimulationForm] Auto-calculated material properties: E={youngModulus:F1} GPa, ν={poissonRatio:F3}");
            }
            catch (Exception ex)
            {
                Logger.Log($"[TriaxialSimulationForm] Error auto-calculating material properties: {ex.Message}");
                MessageBox.Show($"Error calculating material properties: {ex.Message}",
                    "Calculation Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Calculates Young's modulus based on material density using empirical relationships
        /// </summary>
        /// <param name="density">Material density in kg/m³</param>
        /// <returns>Young's modulus in GPa</returns>
        private double CalculateYoungsModulus(double density)
        {
            // Empirical calculation for Young's modulus based on density
            // Different material types have different relationships

            // Density ranges
            bool isLightMaterial = density < 1500;     // e.g., light ceramics, some polymers
            bool isMediumMaterial = density >= 1500 && density < 4000;  // e.g., rocks, minerals, some metals
            bool isHeavyMaterial = density >= 4000;    // e.g., dense metals

            double youngModulus;

            if (isLightMaterial)
            {
                // For light materials like polymers, ceramics (< 1500 kg/m³)
                // Typical E values from 1-20 GPa
                youngModulus = 0.02 * density - 10;
                youngModulus = Math.Max(1.0, youngModulus); // Minimum 1 GPa
            }
            else if (isMediumMaterial)
            {
                // For medium density materials like most rocks, concrete, etc. (1500-4000 kg/m³)
                // Typical E values from 20-100 GPa
                youngModulus = 0.03 * density - 20;
            }
            else // Heavy materials
            {
                // For high density materials like dense metals (> 4000 kg/m³)
                // Typical E values from 100-400 GPa
                youngModulus = 0.05 * density - 100;
                youngModulus = Math.Min(youngModulus, 400); // Maximum 400 GPa
            }

            return youngModulus;
        }

        /// <summary>
        /// Calculates Poisson's ratio based on material density
        /// </summary>
        /// <param name="density">Material density in kg/m³</param>
        /// <returns>Poisson's ratio (dimensionless)</returns>
        private double CalculatePoissonsRatio(double density)
        {
            // Empirical calculation for Poisson's ratio based on density
            double poissonRatio;

            // Most materials have Poisson's ratio between 0.2 and 0.45
            if (density < 1000)
            {
                // Very light materials like foams, porous ceramics
                poissonRatio = 0.20;
            }
            else if (density < 2000)
            {
                // Light to medium materials like polymers, wood, light ceramics
                poissonRatio = 0.25;
            }
            else if (density < 3000)
            {
                // Medium density materials like rocks, concrete
                poissonRatio = 0.30;
            }
            else if (density < 5000)
            {
                // Medium-high density materials like many metals
                poissonRatio = 0.33;
            }
            else if (density < 8000)
            {
                // High density materials like steel, titanium
                poissonRatio = 0.35;
            }
            else
            {
                // Very high density materials
                poissonRatio = 0.40;
            }

            // Ensure range is valid for Poisson's ratio (0 to 0.5)
            return Math.Min(Math.Max(poissonRatio, 0.0), 0.5);
        }
        private Image CreateContinueIcon()
        {
            Bitmap bmp = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                // Draw two triangles for fast forward
                Point[] triangle1 = new Point[]
                {
                    new Point(2, 3),
                    new Point(2, 13),
                    new Point(8, 8)
                };

                Point[] triangle2 = new Point[]
                {
                    new Point(8, 3),
                    new Point(8, 13),
                    new Point(14, 8)
                };

                using (SolidBrush brush = new SolidBrush(Color.LightGreen))
                {
                    g.FillPolygon(brush, triangle1);
                    g.FillPolygon(brush, triangle2);
                }
            }
            return bmp;
        }
        private void InitializeDamageArray()
        {
            try
            {
                if (mainForm.volumeLabels == null)
                    return;

                int width = mainForm.GetWidth();
                int height = mainForm.GetHeight();
                int depth = mainForm.GetDepth();

                // Create the damage array if not already created or if dimensions changed
                if (damage == null ||
                    damage.GetLength(0) != width ||
                    damage.GetLength(1) != height ||
                    damage.GetLength(2) != depth)
                {
                    damage = new double[width, height, depth];
                    Logger.Log($"[TriaxialSimulationForm] Initialized damage array with dimensions {width}x{height}x{depth}");
                }

                // Initialize all elements to zero
                Array.Clear(damage, 0, damage.Length);
            }
            catch (Exception ex)
            {
                Logger.Log($"[TriaxialSimulationForm] Error initializing damage array: {ex.Message}");
            }
        }


        private byte[,,] CreateLabelVolumeArray()
        {
            int width = mainForm.GetWidth();
            int height = mainForm.GetHeight();
            int depth = mainForm.GetDepth();

            // Create a new byte array with the correct dimensions
            byte[,,] labelArray = new byte[width, height, depth];

            // Copy the data from the volumeLabels interface to the array
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        // Use the indexer from ILabelVolumeData to access the data
                        labelArray[x, y, z] = mainForm.volumeLabels[x, y, z];
                    }
                }
            }

            return labelArray;
        }

        private void InitializeTriaxialVisualization()
        {
            try
            {
                if (mainForm.volumeLabels == null || densityVolume == null || selectedMaterial == null)
                {
                    Logger.Log("[TriaxialSimulationForm] Cannot initialize triaxial visualization - data not valid");
                    return;
                }

                int width = mainForm.GetWidth();
                int height = mainForm.GetHeight();
                int depth = mainForm.GetDepth();
                byte materialID = selectedMaterialID;
                float pixelSize = (float)mainForm.GetPixelSize();

                // Dispose existing visualization if any
                if (triaxialVisualization != null)
                {
                    triaxialVisualization.Dispose();
                    triaxialVisualization = null;
                }

                // Create the new adapter with the ILabelVolumeData interface
                triaxialVisualization = new TriaxialVisualizationAdapter(
                    width, height, depth,
                    pixelSize,
                    mainForm.volumeLabels,  // Pass the ILabelVolumeData interface
                    densityVolume,
                    materialID
                );

                // Set initial view transformation
                triaxialVisualization.SetViewTransformation(rotationX, rotationY, zoom, pan);

                Logger.Log("[TriaxialSimulationForm] Triaxial visualization initialized successfully");
            }
            catch (Exception ex)
            {
                Logger.Log($"[TriaxialSimulationForm] Error initializing triaxial visualization: {ex.Message}\n{ex.StackTrace}");
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

        private class PointF3D
        {
            public float X { get; set; }
            public float Y { get; set; }
            public float Z { get; set; }

            public PointF3D(float x, float y, float z)
            {
                X = x;
                Y = y;
                Z = z;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Cancel any running simulation
                cts?.Cancel();

                // Dispose of unmanaged resources
                cpuSim?.Dispose();
                gpuSim?.Dispose();
                //volumeRenderer?.Dispose();
                triaxialVisualization?.Dispose(); // Add this line

                // Dispose of managed resources
                chart?.Dispose();
                resultsExtension?.Dispose();
            }
            base.Dispose(disposing);
        }
        #endregion
    }
}