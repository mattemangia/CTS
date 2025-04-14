using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Data;
using System.Drawing.Imaging;

namespace CTSegmenter
{
    public class PoreNetworkModelingForm : Form
    {
        private MainForm mainForm;
        private Material selectedMaterial;
        private PoreNetworkModel networkModel = new PoreNetworkModel();

        // UI Elements
        private SplitContainer mainSplitContainer;
        private Panel previewPanel;
        private PictureBox previewPictureBox;
        private ComboBox materialComboBox;
        private NumericUpDown markerExtentNumeric;
        private CheckBox useGpuCheckBox;
        private TrackBar zoomTrackBar;
        private Button generateButton;
        private Button exportButton;
        private Button saveButton;
        private Button loadButton;
        private DataGridView poreDataGridView;
        private ProgressBar progressBar;
        private Label statusLabel;
        private Panel visualizationPanel;
        private PictureBox networkPictureBox;

        // 3d rotation
        private float rotationX = 30.0f;
        private float rotationY = 30.0f;
        private float rotationZ = 0.0f;
        private float viewScale = 1.0f;
        private Point lastMousePosition;
        private bool isDragging = false;

        // Processing data
        private CancellationTokenSource cts;
        private ParticleSeparator.SeparationResult separationResult;
        private float previewZoom = 1.0f;
        private int currentSlice = 0;

        public PoreNetworkModelingForm(MainForm mainForm)
        {
            this.mainForm = mainForm;
            InitializeComponent();
            PopulateMaterialComboBox();
            EnsureDataGridViewHeadersVisible();
        }

        private void InitializeComponent()
        {
            // Form setup with modern style
            this.Text = "Pore Network Modeling";
            this.Size = new Size(1280, 900);
            this.MinimumSize = new Size(1000, 700);
            this.BackColor = Color.FromArgb(240, 240, 240);
            this.Font = new Font("Segoe UI", 9F);

            // =====================
            // TOP RIBBON PANEL
            // =====================
            Panel ribbonPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 110,
                BackColor = Color.FromArgb(230, 230, 230),
                Padding = new Padding(5),
                BorderStyle = BorderStyle.FixedSingle
            };

            // Material Selection Group
            GroupBox materialGroup = new GroupBox
            {
                Text = "Material",
                Location = new Point(10, 5),
                Size = new Size(180, 90),
                BackColor = Color.Transparent
            };

            Label materialLabel = new Label
            {
                Text = "Select Pore Material:",
                Location = new Point(10, 20),
                AutoSize = true
            };
            materialGroup.Controls.Add(materialLabel);

            materialComboBox = new ComboBox
            {
                Location = new Point(10, 45),
                Width = 160,
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat
            };
            materialComboBox.DisplayMember = "Name";
            materialComboBox.SelectedIndexChanged += (s, e) => {
                if (materialComboBox.SelectedItem is Material material)
                {
                    selectedMaterial = material;
                    UpdatePreviewImage();  // Update the preview when material changes
                }
            };
            materialGroup.Controls.Add(materialComboBox);
            ribbonPanel.Controls.Add(materialGroup);

            // Process Group
            GroupBox processGroup = new GroupBox
            {
                Text = "Process",
                Location = new Point(200, 5),
                Size = new Size(310, 90),
                BackColor = Color.Transparent
            };

            Button separateButton = new Button
            {
                Text = "1. Separate Particles",
                Location = new Point(15, 25),
                Width = 135,
                Height = 50,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(225, 225, 225),
                Image = SystemIcons.Information.ToBitmap(),
                ImageAlign = ContentAlignment.MiddleLeft,
                TextImageRelation = TextImageRelation.ImageBeforeText,
                Padding = new Padding(5, 0, 5, 0)
            };
            separateButton.Click += async (s, e) => await SeparateParticlesAsync();
            processGroup.Controls.Add(separateButton);

            generateButton = new Button
            {
                Text = "2. Generate Network",
                Location = new Point(160, 25),
                Width = 135,
                Height = 50,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(225, 225, 225),
                Enabled = false,
                Image = SystemIcons.Application.ToBitmap(),
                ImageAlign = ContentAlignment.MiddleLeft,
                TextImageRelation = TextImageRelation.ImageBeforeText,
                Padding = new Padding(5, 0, 5, 0)
            };
            generateButton.Click += async (s, e) => await GenerateNetworkAsync();
            processGroup.Controls.Add(generateButton);
            ribbonPanel.Controls.Add(processGroup);

            // Data Group
            GroupBox dataGroup = new GroupBox
            {
                Text = "Data",
                Location = new Point(520, 5),
                Size = new Size(210, 90),
                BackColor = Color.Transparent
            };

            saveButton = new Button
            {
                Text = "Save",
                Location = new Point(15, 25),
                Width = 85,
                Height = 30,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(225, 225, 225),
                Enabled = false
            };
            saveButton.Click += (s, e) => SaveNetwork();
            dataGroup.Controls.Add(saveButton);

            loadButton = new Button
            {
                Text = "Load",
                Location = new Point(110, 25),
                Width = 85,
                Height = 30,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(225, 225, 225)
            };
            loadButton.Click += (s, e) => LoadNetwork();
            dataGroup.Controls.Add(loadButton);

            exportButton = new Button
            {
                Text = "Export Data",
                Location = new Point(15, 60),
                Width = 180,
                Height = 30,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(225, 225, 225),
                Enabled = false
            };
            exportButton.Click += (s, e) => ExportData();
            dataGroup.Controls.Add(exportButton);
            ribbonPanel.Controls.Add(dataGroup);

            // Settings Group - EXPANDED WIDTH
            GroupBox settingsGroup = new GroupBox
            {
                Text = "Settings",
                Location = new Point(740, 5),
                Size = new Size(280, 90),  // Increased width
                BackColor = Color.Transparent
            };

            Label markerExtentLabel = new Label
            {
                Text = "Marker Extent:",
                Location = new Point(10, 25),
                AutoSize = true
            };
            settingsGroup.Controls.Add(markerExtentLabel);

            markerExtentNumeric = new NumericUpDown
            {
                Location = new Point(95, 23),
                Width = 50,
                Minimum = 1,
                Maximum = 20,
                Value = 3
            };
            settingsGroup.Controls.Add(markerExtentNumeric);

            Label minPoreSizeLabel = new Label
            {
                Text = "Min Pore Size:",
                Location = new Point(10, 55),
                AutoSize = true
            };
            settingsGroup.Controls.Add(minPoreSizeLabel);

            NumericUpDown minPoreSizeNumeric = new NumericUpDown
            {
                Location = new Point(95, 53),
                Width = 50,
                Minimum = 5,
                Maximum = 1000,
                Value = 20
            };
            settingsGroup.Controls.Add(minPoreSizeNumeric);

            // Fixed checkbox positioning
            useGpuCheckBox = new CheckBox
            {
                Text = "Use GPU",
                Location = new Point(160, 25),
                AutoSize = true,
                Checked = true
            };
            settingsGroup.Controls.Add(useGpuCheckBox);

            CheckBox conservativeCheckBox = new CheckBox
            {
                Text = "Conservative",
                Location = new Point(160, 55),
                AutoSize = true,
                Checked = true
            };
            settingsGroup.Controls.Add(conservativeCheckBox);
            ribbonPanel.Controls.Add(settingsGroup);

            // Status Group - ADJUSTED POSITION
            GroupBox statusGroup = new GroupBox
            {
                Text = "Status",
                Location = new Point(1030, 5),
                Size = new Size(220, 90),  // Slightly reduced width
                BackColor = Color.Transparent,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };

            progressBar = new ProgressBar
            {
                Location = new Point(10, 25),
                Width = 200,
                Height = 22,
                Style = ProgressBarStyle.Continuous,
                Value = 0
            };
            statusGroup.Controls.Add(progressBar);

            statusLabel = new Label
            {
                Text = "Ready",
                Location = new Point(10, 55),
                Width = 200,
                Height = 30,
                Font = new Font("Segoe UI", 8F),
                ForeColor = Color.DarkBlue
            };
            statusGroup.Controls.Add(statusLabel);
            ribbonPanel.Controls.Add(statusGroup);

            // =====================
            // MAIN CONTENT AREA
            // =====================
            TabControl mainTabControl = new TabControl
            {
                Dock = DockStyle.Fill,
                Padding = new Point(15, 5)
            };

            // TAB 1: Slice View
            TabPage sliceViewTab = new TabPage("Slice View");
            sliceViewTab.Padding = new Padding(3);

            Panel sliceViewPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                Padding = new Padding(5)
            };

            // Top controls for slice navigation
            Panel sliceControlPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 40
            };

            Label sliceLabel = new Label
            {
                Text = "Slice:",
                Location = new Point(10, 10),
                AutoSize = true
            };
            sliceControlPanel.Controls.Add(sliceLabel);

            TrackBar sliceTrackBar = new TrackBar
            {
                Location = new Point(50, 5),
                Width = 650,
                Minimum = 0,
                Maximum = mainForm.GetDepth() > 0 ? mainForm.GetDepth() - 1 : 0,
                Value = mainForm.CurrentSlice,
                TickFrequency = Math.Max(1, mainForm.GetDepth() / 20),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            sliceTrackBar.ValueChanged += (s, e) => {
                currentSlice = sliceTrackBar.Value;
                UpdatePreviewImage();
            };
            sliceControlPanel.Controls.Add(sliceTrackBar);

            // Right controls for zoom
            Label zoomLabel = new Label
            {
                Text = "Zoom:",
                Location = new Point(710, 10),
                AutoSize = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            sliceControlPanel.Controls.Add(zoomLabel);

            zoomTrackBar = new TrackBar
            {
                Location = new Point(760, 5),
                Width = 150,
                Minimum = 1,
                Maximum = 20,
                Value = 10,
                TickFrequency = 2,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            zoomTrackBar.ValueChanged += (s, e) => {
                previewZoom = zoomTrackBar.Value / 10.0f;
                UpdatePreviewImage();
            };
            sliceControlPanel.Controls.Add(zoomTrackBar);
            sliceViewPanel.Controls.Add(sliceControlPanel);

            // Main slice preview
            previewPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                BackColor = Color.Black
            };

            previewPictureBox = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Black
            };
            previewPictureBox.MouseWheel += PreviewPictureBox_MouseWheel;
            previewPanel.Controls.Add(previewPictureBox);
            sliceViewPanel.Controls.Add(previewPanel);

            sliceViewTab.Controls.Add(sliceViewPanel);
            mainTabControl.Controls.Add(sliceViewTab);

            // TAB 2: 3D Network View
            TabPage networkViewTab = new TabPage("3D Network View");
            networkViewTab.Padding = new Padding(3);

            visualizationPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                BackColor = Color.Black
            };

            Label visualizationLabel = new Label
            {
                Text = "3D Pore Network Visualization",
                TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 16),
                ForeColor = Color.White,
                BackColor = Color.Black
            };
            visualizationPanel.Controls.Add(visualizationLabel);

            networkViewTab.Controls.Add(visualizationPanel);
            mainTabControl.Controls.Add(networkViewTab);

            // =====================
            // BOTTOM DATA PANEL
            // =====================
            Panel dataPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 250,
                Padding = new Padding(5),
                BorderStyle = BorderStyle.FixedSingle
            };

            // Data panel header with collapsible button
            Panel dataPanelHeader = new Panel
            {
                Dock = DockStyle.Top,
                Height = 30,
                BackColor = Color.FromArgb(230, 230, 230)
            };

            Label dataHeaderLabel = new Label
            {
                Text = "Pore Data",
                Location = new Point(10, 8),
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                AutoSize = true
            };
            dataPanelHeader.Controls.Add(dataHeaderLabel);

            // Toggle button to collapse/expand the data panel
            Button toggleDataPanelButton = new Button
            {
                Text = "▲",
                Size = new Size(25, 23),
                Location = new Point(80, 4),
                FlatStyle = FlatStyle.Flat,
                FlatAppearance = { BorderSize = 0 },
                BackColor = Color.Transparent
            };
            toggleDataPanelButton.Click += (s, e) => {
                dataPanel.Height = dataPanel.Height == 30 ? 250 : 30;
                toggleDataPanelButton.Text = dataPanel.Height == 30 ? "▼" : "▲";
            };
            dataPanelHeader.Controls.Add(toggleDataPanelButton);
            dataPanel.Controls.Add(dataPanelHeader);
            Panel gridContainer = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(0, 5, 0, 0) // Add top padding to create space below the header
            };

            // Data grid
            poreDataGridView = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                ReadOnly = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                RowHeadersWidth = 25,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.None,
                RowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.White },
                AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(240, 240, 240) },
                GridColor = Color.FromArgb(220, 220, 220),
                ColumnHeadersVisible = true,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
                ColumnHeadersHeight = 30,
                ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
                {
                    BackColor = Color.FromArgb(220, 220, 220),
                    ForeColor = Color.Black,
                    Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                    Alignment = DataGridViewContentAlignment.MiddleCenter
                }
            };
            poreDataGridView.EnableHeadersVisualStyles = false;
            // Set up the columns for the pore data
            poreDataGridView.Columns.Add("Id", "ID");
            poreDataGridView.Columns.Add("Volume", "Volume (µm³)");
            poreDataGridView.Columns.Add("Area", "Surface Area (µm²)");
            poreDataGridView.Columns.Add("Radius", "Equiv. Radius (µm)");
            poreDataGridView.Columns.Add("X", "X (µm)");
            poreDataGridView.Columns.Add("Y", "Y (µm)");
            poreDataGridView.Columns.Add("Z", "Z (µm)");
            poreDataGridView.Columns.Add("Connections", "# Connections");

            // Setup context menu for DataGridView for exporting
            ContextMenuStrip gridContextMenu = new ContextMenuStrip();
            ToolStripMenuItem exportMenuItem = new ToolStripMenuItem("Export Selected Rows");
            exportMenuItem.Click += (s, e) => ExportSelectedRows();
            gridContextMenu.Items.Add(exportMenuItem);
            poreDataGridView.ContextMenuStrip = gridContextMenu;

            // Assemble the panel structure
            gridContainer.Controls.Add(poreDataGridView);
            dataPanel.Controls.Add(gridContainer);
            dataPanel.Controls.Add(dataPanelHeader);

            // =====================
            // ASSEMBLE FORM
            // =====================
            this.Controls.Add(mainTabControl);
            this.Controls.Add(dataPanel);
            this.Controls.Add(ribbonPanel);

            // Force an initial preview update
            this.Load += (s, e) => {
                UpdatePreviewImage();
            };
        }

        private void PopulateMaterialComboBox()
        {
            materialComboBox.Items.Clear();
            foreach (Material material in mainForm.Materials)
            {
                // Skip the Exterior material
                if (material.Name.ToLower() != "exterior")
                {
                    materialComboBox.Items.Add(material);
                }
            }

            if (materialComboBox.Items.Count > 0)
            {
                materialComboBox.SelectedIndex = 0;
            }
        }

        private async Task SeparateParticlesAsync()
        {
            if (selectedMaterial == null)
            {
                MessageBox.Show("Please select a material first", "No Material Selected",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            progressBar.Value = 0;
            statusLabel.Text = "Separating particles...";

            // Cancel any existing operation
            cts?.Cancel();
            cts = new CancellationTokenSource();

            try
            {
                // Create progress reporter
                Progress<int> progress = new Progress<int>(percent => {
                    progressBar.Value = percent;
                });

                // Create particle separator
                bool useGpu = useGpuCheckBox.Checked;
                int markerExtent = (int)markerExtentNumeric.Value;

                using (ParticleSeparator separator = new ParticleSeparator(mainForm, selectedMaterial, useGpu))
                {
                    // Separate particles (pores)
                    separationResult = await Task.Run(() => separator.SeparateParticles(
                        process3D: true,
                        conservative: true,
                        currentSlice: mainForm.CurrentSlice,
                        progress: progress,
                        cancellationToken: cts.Token
                    ), cts.Token);

                    // Update the preview
                    UpdatePreviewImage();

                    // Enable the generate button
                    generateButton.Enabled = true;

                    statusLabel.Text = $"Identified {separationResult.Particles.Count} potential pores";
                }
            }
            catch (OperationCanceledException)
            {
                statusLabel.Text = "Operation cancelled";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error separating particles: {ex.Message}",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                statusLabel.Text = "Error separating particles";
                Logger.Log($"[PoreNetworkModelingForm] Error: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private async Task GenerateNetworkAsync()
        {
            if (separationResult == null)
            {
                MessageBox.Show("Please separate particles first", "No Data",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            progressBar.Value = 0;
            statusLabel.Text = "Generating pore network...";

            try
            {
                Progress<int> progress = new Progress<int>(percent => {
                    progressBar.Value = percent;
                });

                bool useGpu = useGpuCheckBox.Checked;

                // Use the new generator class
                using (PoreNetworkGenerator generator = new PoreNetworkGenerator())
                {
                    // Generate the network model
                    networkModel = await generator.GenerateNetworkFromSeparationResult(
                        separationResult,
                        mainForm.pixelSize,
                        progress,
                        useGpu);
                }

                // Update UI
                UpdatePoreTable();
                Render3DVisualization();

                // Enable export and save buttons
                exportButton.Enabled = true;
                saveButton.Enabled = true;

                statusLabel.Text = $"Generated network with {networkModel.Pores.Count} pores and " +
                    $"{networkModel.Throats.Count} throats. Porosity: {networkModel.Porosity:P2}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error generating network: {ex.Message}",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                statusLabel.Text = "Error generating network";
                Logger.Log($"[PoreNetworkModelingForm] Error: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private void ConvertParticlesToPores()
        {
            networkModel.Pores.Clear();

            // Get the pixel size (in meters)
            double pixelSize = mainForm.pixelSize;

            // Convert each particle to a pore
            foreach (var particle in separationResult.Particles)
            {
                // Calculate pore properties
                double volume = particle.VoxelCount * Math.Pow(pixelSize, 3); // in cubic meters

                // Calculate surface area using boundary voxels
                double surfaceArea = CalculateSurfaceArea(particle, separationResult.LabelVolume);

                // Calculate equivalent radius from volume (sphere approximation)
                double radius = Math.Pow(3 * volume / (4 * Math.PI), 1.0 / 3.0);

                Pore pore = new Pore
                {
                    Id = particle.Id,
                    Volume = volume * 1e18, // Convert to cubic micrometers
                    Area = surfaceArea * 1e12, // Convert to square micrometers
                    Radius = radius * 1e6, // Convert to micrometers
                    Center = new Point3D
                    {
                        X = particle.Center.X * pixelSize * 1e6, // Convert to micrometers
                        Y = particle.Center.Y * pixelSize * 1e6,
                        Z = particle.Center.Z * pixelSize * 1e6
                    }
                };

                networkModel.Pores.Add(pore);
            }
        }

        private double CalculateSurfaceArea(ParticleSeparator.Particle particle, object labelVolume)
        {
            // Count boundary voxels for surface area calculation
            int width = LabelVolumeHelper.GetWidth(labelVolume);
            int height = LabelVolumeHelper.GetHeight(labelVolume);
            int depth = LabelVolumeHelper.GetDepth(labelVolume);
            double pixelSize = mainForm.pixelSize;

            int boundaryVoxelCount = 0;

            // Check the particle's bounding box only
            for (int z = Math.Max(0, particle.Bounds.MinZ); z <= Math.Min(depth - 1, particle.Bounds.MaxZ); z++)
            {
                for (int y = Math.Max(0, particle.Bounds.MinY); y <= Math.Min(height - 1, particle.Bounds.MaxY); y++)
                {
                    for (int x = Math.Max(0, particle.Bounds.MinX); x <= Math.Min(width - 1, particle.Bounds.MaxX); x++)
                    {
                        if (LabelVolumeHelper.GetLabel(labelVolume, x, y, z) == particle.Id)
                        {
                            // Check 6-connected neighbors
                            bool isBoundary = false;

                            // Check X direction
                            if (x > 0 && LabelVolumeHelper.GetLabel(labelVolume, x - 1, y, z) != particle.Id) isBoundary = true;
                            else if (x < width - 1 && LabelVolumeHelper.GetLabel(labelVolume, x + 1, y, z) != particle.Id) isBoundary = true;

                            // Check Y direction
                            else if (y > 0 && LabelVolumeHelper.GetLabel(labelVolume, x, y - 1, z) != particle.Id) isBoundary = true;
                            else if (y < height - 1 && LabelVolumeHelper.GetLabel(labelVolume, x, y + 1, z) != particle.Id) isBoundary = true;

                            // Check Z direction
                            else if (z > 0 && LabelVolumeHelper.GetLabel(labelVolume, x, y, z - 1) != particle.Id) isBoundary = true;
                            else if (z < depth - 1 && LabelVolumeHelper.GetLabel(labelVolume, x, y, z + 1) != particle.Id) isBoundary = true;

                            if (isBoundary)
                                boundaryVoxelCount++;
                        }
                    }
                }
            }

            // Average of 1.5 faces per boundary voxel
            return boundaryVoxelCount * 1.5 * pixelSize * pixelSize;
        }

        private void GenerateThroats()
        {
            networkModel.Throats.Clear();

            // Skip if we have too few pores
            if (networkModel.Pores.Count < 2)
                return;

            // For each pore, find closest neighbors to create throats
            int maxConnections = Math.Min(6, networkModel.Pores.Count - 1);

            for (int i = 0; i < networkModel.Pores.Count; i++)
            {
                Pore pore1 = networkModel.Pores[i];

                // Find closest pores by distance
                var closestPores = networkModel.Pores
                    .Where(p => p.Id != pore1.Id)
                    .OrderBy(p => Distance(pore1.Center, p.Center))
                    .Take(maxConnections);

                foreach (var pore2 in closestPores)
                {
                    // Avoid duplicate throats (only add if pore1.Id < pore2.Id)
                    if (pore1.Id < pore2.Id)
                    {
                        // Calculate throat radius (weighted average of the two pore radii)
                        double radius = (pore1.Radius + pore2.Radius) * 0.25; // 25% of average

                        // Calculate throat length
                        double distance = Distance(pore1.Center, pore2.Center);
                        double length = Math.Max(0.1, distance - pore1.Radius - pore2.Radius);

                        // Calculate throat volume (cylinder approximation)
                        double volume = Math.PI * radius * radius * length;

                        Throat throat = new Throat
                        {
                            Id = networkModel.Throats.Count + 1,
                            PoreId1 = pore1.Id,
                            PoreId2 = pore2.Id,
                            Radius = radius,
                            Length = length,
                            Volume = volume
                        };

                        networkModel.Throats.Add(throat);

                        // Update connection counts
                        pore1.ConnectionCount++;
                        pore2.ConnectionCount++;
                    }
                }
            }
        }

        private double Distance(Point3D p1, Point3D p2)
        {
            return Math.Sqrt(
                Math.Pow(p2.X - p1.X, 2) +
                Math.Pow(p2.Y - p1.Y, 2) +
                Math.Pow(p2.Z - p1.Z, 2)
            );
        }

        private void CalculateNetworkProperties()
        {
            // Calculate total volumes
            networkModel.TotalPoreVolume = networkModel.Pores.Sum(p => p.Volume);
            networkModel.TotalThroatVolume = networkModel.Throats.Sum(t => t.Volume);

            // Calculate porosity (rough estimate)
            double totalVolume = mainForm.GetWidth() * mainForm.GetHeight() * mainForm.GetDepth() *
                Math.Pow(mainForm.pixelSize * 1e6, 3);

            networkModel.Porosity = (networkModel.TotalPoreVolume + networkModel.TotalThroatVolume) / totalVolume;
        }
        private void UpdatePreviewImage()
        {
            try
            {
                // Case 1: No separation result yet, show raw material
                if (separationResult == null || separationResult.LabelVolume == null)
                {
                    // Make sure we have data to display
                    if (mainForm.GetWidth() > 0 && mainForm.GetHeight() > 0 && mainForm.GetDepth() > 0)
                    {
                        // Ensure current slice is valid
                        currentSlice = Math.Min(currentSlice, mainForm.GetDepth() - 1);

                        // Create bitmap for preview
                        Bitmap materialBitmap = new Bitmap(mainForm.GetWidth(), mainForm.GetHeight());
                        using (Graphics g = Graphics.FromImage(materialBitmap))
                        {
                            g.Clear(Color.Black); // Start with black background
                        }

                        // Draw the material
                        for (int y = 0; y < mainForm.GetHeight(); y++)
                        {
                            for (int x = 0; x < mainForm.GetWidth(); x++)
                            {
                                if (selectedMaterial != null &&
                                    x < LabelVolumeHelper.GetWidth(mainForm.volumeLabels) &&
                                    y < LabelVolumeHelper.GetHeight(mainForm.volumeLabels) &&
                                    currentSlice < LabelVolumeHelper.GetDepth(mainForm.volumeLabels) &&
                                    LabelVolumeHelper.GetLabel(mainForm.volumeLabels, x, y, currentSlice) == selectedMaterial.ID)
                                {
                                    materialBitmap.SetPixel(x, y, selectedMaterial.Color);
                                }
                            }
                        }

                        // Set the preview image
                        previewPictureBox.Image = materialBitmap;
                        previewPictureBox.SizeMode = PictureBoxSizeMode.Zoom;

                        // Update status
                        statusLabel.Text = $"Viewing slice {currentSlice + 1} of {mainForm.GetDepth()} - Raw material view";
                    }
                    return;
                }

                // Case 2: We have separation results, show labeled data
                // Use helper methods to get dimensions
                int width = LabelVolumeHelper.GetWidth(separationResult.LabelVolume);
                int height = LabelVolumeHelper.GetHeight(separationResult.LabelVolume);
                int depth = LabelVolumeHelper.GetDepth(separationResult.LabelVolume);

                // Ensure current slice is valid
                int slice = Math.Min(currentSlice, depth - 1);

                // Create a bitmap for the slice
                Bitmap labelBitmap = new Bitmap(width, height);

                // Create a colormap for visualizing the labels
                Dictionary<int, Color> colorMap = new Dictionary<int, Color>();
                Random random = new Random(0); // Fixed seed for consistent colors

                // Add background color
                colorMap[0] = Color.Black;

                // Draw the labeled data
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        // Use helper method to get the label value
                        int label = LabelVolumeHelper.GetLabel(separationResult.LabelVolume, x, y, slice);

                        if (!colorMap.ContainsKey(label))
                        {
                            // Generate a new random color for this label
                            Color randomColor = Color.FromArgb(
                                random.Next(100, 255),
                                random.Next(100, 255),
                                random.Next(100, 255)
                            );
                            colorMap[label] = randomColor;
                        }

                        labelBitmap.SetPixel(x, y, colorMap[label]);
                    }
                }

                // Apply zoom if needed
                if (previewZoom > 1.0f)
                {
                    int newWidth = (int)(width * previewZoom);
                    int newHeight = (int)(height * previewZoom);

                    Bitmap zoomedBitmap = new Bitmap(newWidth, newHeight);
                    using (Graphics g = Graphics.FromImage(zoomedBitmap))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                        g.DrawImage(labelBitmap, 0, 0, newWidth, newHeight);
                    }

                    previewPictureBox.Image = zoomedBitmap;
                    previewPictureBox.SizeMode = PictureBoxSizeMode.CenterImage;
                }
                else
                {
                    previewPictureBox.Image = labelBitmap;
                    previewPictureBox.SizeMode = PictureBoxSizeMode.Zoom;
                }

                // Update status
                statusLabel.Text = $"Viewing slice {slice + 1} of {depth} - {separationResult.Particles.Count} pores identified";
            }
            catch (Exception ex)
            {
                Logger.Log($"Error updating preview: {ex.Message}");
                statusLabel.Text = "Error displaying preview image";

                // Create a simple error message bitmap
                Bitmap errorBitmap = new Bitmap(200, 100);
                using (Graphics g = Graphics.FromImage(errorBitmap))
                {
                    g.Clear(Color.Black);
                    using (Font font = new Font("Arial", 10))
                    {
                        g.DrawString("Preview Error", font, Brushes.Red, 10, 10);
                        g.DrawString(ex.Message, font, Brushes.Red, 10, 30);
                    }
                }
                previewPictureBox.Image = errorBitmap;
            }
        }





        private void PreviewPictureBox_MouseWheel(object sender, MouseEventArgs e)
        {
            // Adjust zoom with mouse wheel
            if (e.Delta > 0 && zoomTrackBar.Value < zoomTrackBar.Maximum)
            {
                zoomTrackBar.Value++;
            }
            else if (e.Delta < 0 && zoomTrackBar.Value > zoomTrackBar.Minimum)
            {
                zoomTrackBar.Value--;
            }
        }

        private void UpdatePoreTable()
        {
            poreDataGridView.Rows.Clear();

            foreach (var pore in networkModel.Pores)
            {
                poreDataGridView.Rows.Add(
                    pore.Id,
                    Math.Round(pore.Volume, 2),
                    Math.Round(pore.Area, 2),
                    Math.Round(pore.Radius, 2),
                    Math.Round(pore.Center.X, 2),
                    Math.Round(pore.Center.Y, 2),
                    Math.Round(pore.Center.Z, 2),
                    pore.ConnectionCount
                );
            }
            EnsureDataGridViewHeadersVisible();
        }

        private void Render3DVisualization()
        {
            if (networkModel == null || networkModel.Pores.Count == 0)
            {
                visualizationPanel.Controls.Clear();
                Label label = new Label
                {
                    Text = "No pore network data to visualize.",
                    TextAlign = ContentAlignment.MiddleCenter,
                    Dock = DockStyle.Fill,
                    Font = new Font("Arial", 16),
                    ForeColor = Color.White,
                    BackColor = Color.Black
                };
                visualizationPanel.Controls.Add(label);
                return;
            }

            // Configure visualization panel and create controls
            visualizationPanel.Controls.Clear();

            // Create a toolbar for view controls
            Panel controlPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 40,
                BackColor = Color.FromArgb(50, 50, 50)
            };

            Label rotationLabel = new Label
            {
                Text = "Rotation:",
                Location = new Point(10, 12),
                ForeColor = Color.White,
                AutoSize = true
            };
            controlPanel.Controls.Add(rotationLabel);

            Button resetViewButton = new Button
            {
                Text = "Reset View",
                Location = new Point(150, 8),
                Width = 100,
                Height = 25,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(80, 80, 80),
                ForeColor = Color.White
            };
            Button screenshotButton = new Button
            {
                Text = "Save Screenshot",
                Location = new Point(260, 8),
                Width = 130,
                Height = 25,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(80, 80, 80),
                ForeColor = Color.White
            };
            screenshotButton.Click += (s, e) => SaveNetworkScreenshot();
            controlPanel.Controls.Add(screenshotButton);

            // Create PictureBox for 3D rendering (now moved before the event handler)
            networkPictureBox = new PictureBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                SizeMode = PictureBoxSizeMode.CenterImage
            };

            // Now the resetViewButton click event will work correctly
            resetViewButton.Click += (s, e) => {
                rotationX = 30.0f;
                rotationY = 30.0f;
                rotationZ = 0.0f;
                viewScale = 1.0f;
                RenderNetwork3D();
            };
            controlPanel.Controls.Add(resetViewButton);

            // Add mouse handling for rotation and zooming
            networkPictureBox.MouseDown += (s, e) => {
                if (e.Button == MouseButtons.Left)
                {
                    isDragging = true;
                    lastMousePosition = e.Location;
                }
            };

            networkPictureBox.MouseMove += (s, e) => {
                if (isDragging)
                {
                    // Calculate the delta movement
                    float deltaX = (e.X - lastMousePosition.X) * 0.5f;
                    float deltaY = (e.Y - lastMousePosition.Y) * 0.5f;

                    // Update rotation angles
                    rotationY += deltaX;
                    rotationX += deltaY;

                    // Render with new rotation
                    RenderNetwork3D();

                    lastMousePosition = e.Location;
                }
            };

            networkPictureBox.MouseUp += (s, e) => {
                if (e.Button == MouseButtons.Left)
                {
                    isDragging = false;
                }
            };

            networkPictureBox.MouseWheel += (s, e) => {
                // Change zoom level with mouse wheel
                float zoomFactor = 1.0f + (e.Delta > 0 ? 0.1f : -0.1f);
                viewScale *= zoomFactor;

                // Limit minimum and maximum zoom
                viewScale = Math.Max(0.2f, Math.Min(3.0f, viewScale));

                RenderNetwork3D();
            };

            // Add controls to panel
            visualizationPanel.Controls.Add(networkPictureBox);
            visualizationPanel.Controls.Add(controlPanel);

            // Create instructions label
            Label instructionsLabel = new Label
            {
                Text = "Left-click and drag to rotate | Mouse wheel to zoom",
                Dock = DockStyle.Bottom,
                Height = 25,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.LightGray,
                BackColor = Color.FromArgb(20, 20, 20)
            };
            visualizationPanel.Controls.Add(instructionsLabel);

            // Initial rendering
            RenderNetwork3D();
        }
        private void SaveNetworkScreenshot()
        {
            if (networkPictureBox?.Image == null)
            {
                MessageBox.Show("No network visualization to save.", "Screenshot Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Create a SaveFileDialog to let the user specify where to save the screenshot
            using (SaveFileDialog saveDialog = new SaveFileDialog())
            {
                saveDialog.Filter = "PNG Image|*.png|JPEG Image|*.jpg|Bitmap Image|*.bmp";
                saveDialog.Title = "Save Network Screenshot";
                saveDialog.DefaultExt = "png";

                if (saveDialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        // Create a copy of the current network image
                        using (Bitmap screenshot = new Bitmap(networkPictureBox.Image))
                        {
                            // Save the image in the format specified by the file extension
                            string extension = Path.GetExtension(saveDialog.FileName).ToLower();
                            ImageFormat format = ImageFormat.Png; // Default

                            if (extension == ".jpg" || extension == ".jpeg")
                                format = ImageFormat.Jpeg;
                            else if (extension == ".bmp")
                                format = ImageFormat.Bmp;

                            screenshot.Save(saveDialog.FileName, format);

                            // Notify the user of success
                            statusLabel.Text = "Screenshot saved successfully.";
                            MessageBox.Show("Screenshot saved successfully.", "Save Complete",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error saving screenshot: {ex.Message}",
                            "Screenshot Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }
        private void RenderNetwork3D()
        {
            if (networkPictureBox == null) return;

            int width = networkPictureBox.Width;
            int height = networkPictureBox.Height;

            if (width <= 0 || height <= 0) return;

            Bitmap networkImage = new Bitmap(Math.Max(1, width), Math.Max(1, height));

            using (Graphics g = Graphics.FromImage(networkImage))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Black);

                // Find model bounds and center point
                double minX = networkModel.Pores.Min(p => p.Center.X);
                double maxX = networkModel.Pores.Max(p => p.Center.X);
                double minY = networkModel.Pores.Min(p => p.Center.Y);
                double maxY = networkModel.Pores.Max(p => p.Center.Y);
                double minZ = networkModel.Pores.Min(p => p.Center.Z);
                double maxZ = networkModel.Pores.Max(p => p.Center.Z);

                double centerX = (minX + maxX) / 2;
                double centerY = (minY + maxY) / 2;
                double centerZ = (minZ + maxZ) / 2;

                // Calculate scale factor based on the model size
                double maxRange = Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ));
                double scaleFactor = Math.Min(width, height) * 0.4 / maxRange * viewScale;

                // Create rotation matrices
                var rotationMatrix = Create3DRotationMatrix(rotationX, rotationY, rotationZ);

                // Project and render throats first (draw from back to front)
                var throatsWithDepth = new List<(double depth, Point p1, Point p2, float thickness, Color color)>();

                foreach (var throat in networkModel.Throats)
                {
                    var pore1 = networkModel.Pores.FirstOrDefault(p => p.Id == throat.PoreId1);
                    var pore2 = networkModel.Pores.FirstOrDefault(p => p.Id == throat.PoreId2);

                    if (pore1 != null && pore2 != null)
                    {
                        // Transform 3D coordinates to project to 2D
                        var transformedP1 = Transform3DPoint(
                            pore1.Center.X - centerX,
                            pore1.Center.Y - centerY,
                            pore1.Center.Z - centerZ,
                            rotationMatrix);

                        var transformedP2 = Transform3DPoint(
                            pore2.Center.X - centerX,
                            pore2.Center.Y - centerY,
                            pore2.Center.Z - centerZ,
                            rotationMatrix);

                        // Calculate projected points
                        Point p1 = new Point(
                            (int)(width / 2 + transformedP1.x * scaleFactor),
                            (int)(height / 2 - transformedP1.y * scaleFactor));

                        Point p2 = new Point(
                            (int)(width / 2 + transformedP2.x * scaleFactor),
                            (int)(height / 2 - transformedP2.y * scaleFactor));

                        // Calculate throat thickness
                        float thickness = (float)(throat.Radius * scaleFactor * 0.25);
                        thickness = Math.Max(1, thickness);

                        // Average Z for depth sorting (to draw back-to-front)
                        double avgZ = (transformedP1.z + transformedP2.z) / 2;

                        // Create gradient color based on depth
                        int intensity = (int)(100 + Math.Min(155, Math.Max(0, 155 * (1 - avgZ / 500))));
                        Color throatColor = Color.FromArgb(intensity, intensity, intensity);

                        throatsWithDepth.Add((avgZ, p1, p2, thickness, throatColor));
                    }
                }

                // Sort throats by depth (Z) to implement basic painter's algorithm (back to front)
                throatsWithDepth = throatsWithDepth.OrderBy(t => t.depth).ToList();

                // Draw sorted throats
                foreach (var (_, p1, p2, thickness, color) in throatsWithDepth)
                {
                    using (Pen pen = new Pen(color, thickness))
                    {
                        g.DrawLine(pen, p1, p2);
                    }
                }

                // Project and render pores
                var poresWithDepth = new List<(double depth, int x, int y, int radius, Color color, Pore pore)>();

                foreach (var pore in networkModel.Pores)
                {
                    // Transform 3D coordinates to project to 2D
                    var transformed = Transform3DPoint(
                        pore.Center.X - centerX,
                        pore.Center.Y - centerY,
                        pore.Center.Z - centerZ,
                        rotationMatrix);

                    // Calculate projected point
                    int x = (int)(width / 2 + transformed.x * scaleFactor);
                    int y = (int)(height / 2 - transformed.y * scaleFactor);

                    // Calculate pore radius in screen space
                    int radius = Math.Max(3, (int)(pore.Radius * scaleFactor * 0.5));

                    // Assign color based on connection count
                    Color poreColor;
                    int connCount = pore.ConnectionCount;

                    if (connCount <= 1)
                        poreColor = Color.Red;
                    else if (connCount == 2)
                        poreColor = Color.Yellow;
                    else if (connCount <= 4)
                        poreColor = Color.Green;
                    else
                        poreColor = Color.Blue;

                    // Adjust color intensity based on Z position (depth)
                    float intensity = (float)Math.Max(0.5f, Math.Min(1.0f, (transformed.z + 500) / 1000));
                    poreColor = AdjustColorIntensity(poreColor, intensity);

                    poresWithDepth.Add((transformed.z, x, y, radius, poreColor, pore));
                }

                // Sort pores by depth (Z) to implement basic painter's algorithm
                poresWithDepth = poresWithDepth.OrderBy(p => p.depth).ToList();

                // Draw pores from back to front
                foreach (var (_, x, y, radius, color, pore) in poresWithDepth)
                {
                    g.FillEllipse(new SolidBrush(color), x - radius, y - radius, radius * 2, radius * 2);
                    g.DrawEllipse(Pens.White, x - radius, y - radius, radius * 2, radius * 2);

                    // Add ID labels for larger pores (optional)
                    if (radius > 15) // Increase minimum size threshold
                    {
                        using (Font font = new Font("Arial", 8))
                        using (SolidBrush textBrush = new SolidBrush(Color.White))
                        using (StringFormat format = new StringFormat()
                        {
                            Alignment = StringAlignment.Center,
                            LineAlignment = StringAlignment.Center
                        })
                        {
                            // Draw text with a small shadow for better visibility
                            g.DrawString(pore.Id.ToString(), font, Brushes.Black,
                                new RectangleF(x - radius + 1, y - radius + 1, radius * 2, radius * 2), format);
                            g.DrawString(pore.Id.ToString(), font, textBrush,
                                new RectangleF(x - radius, y - radius, radius * 2, radius * 2), format);
                        }
                    }
                }

                // Draw coordinate axes for orientation
                DrawCoordinateAxes(g, width, height, scaleFactor * 0.2, rotationMatrix);

                // Add legend and statistics
                DrawLegendAndStats(g, width, height);
            }

            networkPictureBox.Image = networkImage;
        }

        // Helper method to draw coordinate axes
        private void DrawCoordinateAxes(Graphics g, int width, int height, double scale, double[,] rotationMatrix)
        {
            // Move origin point to bottom right instead of bottom left to avoid overlaps with labels
            Point origin = new Point(width - 80, height - 80);

            // X-axis (red)
            var transformedX = Transform3DPoint(scale, 0, 0, rotationMatrix);
            Point xPoint = new Point(
                (int)(origin.X + transformedX.x * 50),
                (int)(origin.Y - transformedX.y * 50));
            g.DrawLine(new Pen(Color.Red, 2), origin, xPoint);
            g.DrawString("X", new Font("Arial", 8), Brushes.Red, xPoint);

            // Y-axis (green)
            var transformedY = Transform3DPoint(0, scale, 0, rotationMatrix);
            Point yPoint = new Point(
                (int)(origin.X + transformedY.x * 50),
                (int)(origin.Y - transformedY.y * 50));
            g.DrawLine(new Pen(Color.Green, 2), origin, yPoint);
            g.DrawString("Y", new Font("Arial", 8), Brushes.Green, yPoint);

            // Z-axis (blue)
            var transformedZ = Transform3DPoint(0, 0, scale, rotationMatrix);
            Point zPoint = new Point(
                (int)(origin.X + transformedZ.x * 50),
                (int)(origin.Y - transformedZ.y * 50));
            g.DrawLine(new Pen(Color.Blue, 2), origin, zPoint);
            g.DrawString("Z", new Font("Arial", 8), Brushes.Blue, zPoint);
        }


        // Helper function to draw legend and statistics
        private void DrawLegendAndStats(Graphics g, int width, int height)
        {
            // Add statistics
            int maxConnections = networkModel.Pores.Count > 0 ? networkModel.Pores.Max(p => p.ConnectionCount) : 0;
            float avgConnections = (float)(networkModel.Pores.Count > 0 ? networkModel.Pores.Average(p => p.ConnectionCount) : 0);
            double avgRadius = networkModel.Pores.Count > 0 ? networkModel.Pores.Average(p => p.Radius) : 0;

            string[] stats = {
        $"Pores: {networkModel.Pores.Count}",
        $"Throats: {networkModel.Throats.Count}",
        $"Porosity: {networkModel.Porosity:P2}",
        $"Avg. Radius: {avgRadius:F2} µm",
        $"Connectivity: {avgConnections:F1} (max: {maxConnections})"
    };

            Font font = new Font("Arial", 10, FontStyle.Bold);
            int yPos = height - 140;

            foreach (string stat in stats)
            {
                g.FillRectangle(new SolidBrush(Color.FromArgb(100, 0, 0, 0)),
                    10, yPos, g.MeasureString(stat, font).Width + 10, 20);
                g.DrawString(stat, font, Brushes.White, 15, yPos);
                yPos += 20;
            }

            // Add color legend
            string[] legends = {
        "Red: 0-1 connections",
        "Yellow: 2 connections",
        "Green: 3-4 connections",
        "Blue: 5+ connections"
    };

            yPos = 15;
            foreach (string text in legends)
            {
                g.DrawString(text, new Font("Arial", 8), Brushes.White, width - 150, yPos);
                yPos += 15;
            }
        }

        // Helper method to create 3D rotation matrix
        private double[,] Create3DRotationMatrix(float angleX, float angleY, float angleZ)
        {
            // Convert angles to radians
            double radX = angleX * Math.PI / 180.0;
            double radY = angleY * Math.PI / 180.0;
            double radZ = angleZ * Math.PI / 180.0;

            // Precompute sine and cosine values
            double sinX = Math.Sin(radX);
            double cosX = Math.Cos(radX);
            double sinY = Math.Sin(radY);
            double cosY = Math.Cos(radY);
            double sinZ = Math.Sin(radZ);
            double cosZ = Math.Cos(radZ);

            // Create rotation matrix (combined X, Y, Z rotations)
            double[,] matrix = new double[3, 3];

            matrix[0, 0] = cosY * cosZ;
            matrix[0, 1] = cosY * sinZ;
            matrix[0, 2] = -sinY;

            matrix[1, 0] = sinX * sinY * cosZ - cosX * sinZ;
            matrix[1, 1] = sinX * sinY * sinZ + cosX * cosZ;
            matrix[1, 2] = sinX * cosY;

            matrix[2, 0] = cosX * sinY * cosZ + sinX * sinZ;
            matrix[2, 1] = cosX * sinY * sinZ - sinX * cosZ;
            matrix[2, 2] = cosX * cosY;

            return matrix;
        }

        // Helper method to transform a 3D point using rotation matrix
        private (double x, double y, double z) Transform3DPoint(double x, double y, double z, double[,] rotationMatrix)
        {
            double newX = x * rotationMatrix[0, 0] + y * rotationMatrix[0, 1] + z * rotationMatrix[0, 2];
            double newY = x * rotationMatrix[1, 0] + y * rotationMatrix[1, 1] + z * rotationMatrix[1, 2];
            double newZ = x * rotationMatrix[2, 0] + y * rotationMatrix[2, 1] + z * rotationMatrix[2, 2];

            return (newX, newY, newZ);
        }

        // Helper method to adjust color intensity based on depth
        private Color AdjustColorIntensity(Color color, float intensity)
        {
            return Color.FromArgb(
                (int)(color.R * intensity),
                (int)(color.G * intensity),
                (int)(color.B * intensity)
            );
        }
        // Helper method to create 3D rotation matrix
        

        // Helper method to transform a 3D point using rotation matrix
        

        // Helper method to adjust color intensity based on depth
        

        private void ExportData()
        {
            if (networkModel.Pores == null || networkModel.Pores.Count == 0)
            {
                MessageBox.Show("No pore data to export", "No Data",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using (SaveFileDialog saveDialog = new SaveFileDialog())
            {
                saveDialog.Filter = "CSV files (*.csv)|*.csv|Excel files (*.xlsx)|*.xlsx";
                saveDialog.Title = "Export Pore Network Data";
                saveDialog.DefaultExt = "csv";

                if (saveDialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        string extension = Path.GetExtension(saveDialog.FileName).ToLower();

                        if (extension == ".csv")
                        {
                            ExportToCsv(saveDialog.FileName);
                        }
                        else if (extension == ".xlsx")
                        {
                            ExportToCsv(saveDialog.FileName);  // Simple CSV for now
                        }

                        statusLabel.Text = "Data exported successfully";
                        MessageBox.Show("Data exported successfully", "Export Complete",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error exporting data: {ex.Message}",
                            "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }
        private void EnsureDataGridViewHeadersVisible()
        {
            // Force the DataGridView to properly render its headers
            if (poreDataGridView != null)
            {
                poreDataGridView.ColumnHeadersVisible = true;
                poreDataGridView.ColumnHeadersHeight = 30;

                // Apply explicit styling to headers
                poreDataGridView.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
                {
                    BackColor = Color.FromArgb(220, 220, 220),
                    ForeColor = Color.Black,
                    Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                    Alignment = DataGridViewContentAlignment.MiddleCenter,
                    Padding = new Padding(0, 5, 0, 5)
                };

                // Ensure each column has its header text set
                foreach (DataGridViewColumn col in poreDataGridView.Columns)
                {
                    if (string.IsNullOrEmpty(col.HeaderText))
                    {
                        col.HeaderText = col.Name;
                    }
                }
            }
        }
        private void ExportToCsv(string filename)
        {
            using (StreamWriter writer = new StreamWriter(filename))
            {
                // Write pores header
                writer.WriteLine("# Pores");
                writer.WriteLine("ID,Volume (µm³),Surface Area (µm²),Equivalent Radius (µm),X (µm),Y (µm),Z (µm),Connections");

                // Write pores data
                foreach (var pore in networkModel.Pores)
                {
                    writer.WriteLine($"{pore.Id},{pore.Volume:F2},{pore.Area:F2},{pore.Radius:F2}," +
                                     $"{pore.Center.X:F2},{pore.Center.Y:F2},{pore.Center.Z:F2},{pore.ConnectionCount}");
                }

                // Write throats header
                writer.WriteLine();
                writer.WriteLine("# Throats");
                writer.WriteLine("ID,Pore1 ID,Pore2 ID,Radius (µm),Length (µm),Volume (µm³)");

                // Write throats data
                foreach (var throat in networkModel.Throats)
                {
                    writer.WriteLine($"{throat.Id},{throat.PoreId1},{throat.PoreId2},{throat.Radius:F2}," +
                                     $"{throat.Length:F2},{throat.Volume:F2}");
                }

                // Write network statistics
                writer.WriteLine();
                writer.WriteLine("# Network Statistics");
                writer.WriteLine($"Total Pore Volume (µm³),{networkModel.TotalPoreVolume:F2}");
                writer.WriteLine($"Total Throat Volume (µm³),{networkModel.TotalThroatVolume:F2}");
                writer.WriteLine($"Porosity,{networkModel.Porosity:F4}");
                writer.WriteLine($"Pixel Size (m),{networkModel.PixelSize:E12}");
            }
        }

        private void ExportSelectedRows()
        {
            if (poreDataGridView.SelectedRows.Count == 0)
            {
                MessageBox.Show("Please select at least one row to export",
                    "No Selection", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (SaveFileDialog saveDialog = new SaveFileDialog())
            {
                saveDialog.Filter = "CSV files (*.csv)|*.csv";
                saveDialog.Title = "Export Selected Pores";

                if (saveDialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        using (StreamWriter writer = new StreamWriter(saveDialog.FileName))
                        {
                            // Write header
                            writer.WriteLine("ID,Volume (µm³),Surface Area (µm²),Equivalent Radius (µm),X (µm),Y (µm),Z (µm),Connections");

                            // Write selected rows
                            foreach (DataGridViewRow row in poreDataGridView.SelectedRows)
                            {
                                string line = string.Join(",", row.Cells.Cast<DataGridViewCell>()
                                    .Select(cell => cell.Value?.ToString() ?? ""));
                                writer.WriteLine(line);
                            }
                        }

                        MessageBox.Show("Selected rows exported successfully",
                            "Export Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error exporting selection: {ex.Message}",
                            "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void SaveNetwork(string filename = null)
        {
            if (networkModel.Pores == null || networkModel.Pores.Count == 0)
            {
                MessageBox.Show("No pore network to save", "No Data",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (string.IsNullOrEmpty(filename))
            {
                using (SaveFileDialog saveDialog = new SaveFileDialog())
                {
                    saveDialog.Filter = "DAT files (*.dat)|*.dat";
                    saveDialog.Title = "Save Pore Network";
                    saveDialog.DefaultExt = "dat";

                    if (saveDialog.ShowDialog() != DialogResult.OK)
                        return;

                    filename = saveDialog.FileName;
                }
            }

            try
            {
                using (FileStream fs = new FileStream(filename, FileMode.Create))
                using (BinaryWriter writer = new BinaryWriter(fs))
                {
                    // Write file header
                    writer.Write("PORENETWORK"); // Magic string
                    writer.Write((int)1); // Version number

                    // Write metadata
                    writer.Write(networkModel.Pores.Count);
                    writer.Write(networkModel.Throats.Count);
                    writer.Write(networkModel.PixelSize);
                    writer.Write(networkModel.Porosity);

                    // Write pores
                    foreach (var pore in networkModel.Pores)
                    {
                        writer.Write(pore.Id);
                        writer.Write(pore.Volume);
                        writer.Write(pore.Area);
                        writer.Write(pore.Radius);
                        writer.Write(pore.Center.X);
                        writer.Write(pore.Center.Y);
                        writer.Write(pore.Center.Z);
                        writer.Write(pore.ConnectionCount);
                    }

                    // Write throats
                    foreach (var throat in networkModel.Throats)
                    {
                        writer.Write(throat.Id);
                        writer.Write(throat.PoreId1);
                        writer.Write(throat.PoreId2);
                        writer.Write(throat.Radius);
                        writer.Write(throat.Length);
                        writer.Write(throat.Volume);
                    }
                }

                statusLabel.Text = "Network saved successfully";
                MessageBox.Show("Network saved successfully", "Save Complete",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving network: {ex.Message}",
                    "Save Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void LoadNetwork(string filename = null)
        {
            if (string.IsNullOrEmpty(filename))
            {
                using (OpenFileDialog openDialog = new OpenFileDialog())
                {
                    openDialog.Filter = "DAT files (*.dat)|*.dat";
                    openDialog.Title = "Load Pore Network";

                    if (openDialog.ShowDialog() != DialogResult.OK)
                        return;

                    filename = openDialog.FileName;
                }
            }

            try
            {
                using (FileStream fs = new FileStream(filename, FileMode.Open))
                using (BinaryReader reader = new BinaryReader(fs))
                {
                    // Read and verify file header
                    string magic = new string(reader.ReadChars(11));
                    if (magic != "PORENETWORK")
                        throw new Exception("Invalid file format");

                    int version = reader.ReadInt32();
                    if (version != 1)
                        throw new Exception($"Unsupported version: {version}");

                    // Read metadata
                    int poreCount = reader.ReadInt32();
                    int throatCount = reader.ReadInt32();
                    double pixelSize = reader.ReadDouble();
                    double porosity = reader.ReadDouble();

                    // Create new network model
                    networkModel = new PoreNetworkModel
                    {
                        PixelSize = pixelSize,
                        Porosity = porosity,
                        Pores = new List<Pore>(poreCount),
                        Throats = new List<Throat>(throatCount)
                    };

                    // Read pores
                    for (int i = 0; i < poreCount; i++)
                    {
                        Pore pore = new Pore
                        {
                            Id = reader.ReadInt32(),
                            Volume = reader.ReadDouble(),
                            Area = reader.ReadDouble(),
                            Radius = reader.ReadDouble(),
                            Center = new Point3D
                            {
                                X = reader.ReadDouble(),
                                Y = reader.ReadDouble(),
                                Z = reader.ReadDouble()
                            },
                            ConnectionCount = reader.ReadInt32()
                        };
                        networkModel.Pores.Add(pore);
                    }

                    // Read throats
                    double totalThroatVolume = 0;
                    for (int i = 0; i < throatCount; i++)
                    {
                        Throat throat = new Throat
                        {
                            Id = reader.ReadInt32(),
                            PoreId1 = reader.ReadInt32(),
                            PoreId2 = reader.ReadInt32(),
                            Radius = reader.ReadDouble(),
                            Length = reader.ReadDouble(),
                            Volume = reader.ReadDouble()
                        };
                        networkModel.Throats.Add(throat);
                        totalThroatVolume += throat.Volume;
                    }

                    networkModel.TotalPoreVolume = networkModel.Pores.Sum(p => p.Volume);
                    networkModel.TotalThroatVolume = totalThroatVolume;
                }

                // Update UI
                UpdatePoreTable();
                Render3DVisualization();

                // Enable export and save buttons
                exportButton.Enabled = true;
                saveButton.Enabled = true;

                statusLabel.Text = $"Loaded network with {networkModel.Pores.Count} pores and {networkModel.Throats.Count} throats";
                MessageBox.Show("Network loaded successfully", "Load Complete",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading network: {ex.Message}",
                    "Load Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

    }
}
