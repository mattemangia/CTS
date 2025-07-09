//Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using Krypton.Toolkit;
using CTS.Modules.Simulation.NMR;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading;
using System.IO;
using System.Drawing.Imaging;

namespace CTS
{
    public partial class NMRSimulationForm : KryptonForm
    {
        private MainForm _mainForm;
        private NMRSimulation _simulation;
        private NMRCalibration _calibration;
        private NMRResultPlotter _plotter;
        private NMRSimulationResult _lastResult;
        private bool _isRunning = false;

        // UI Controls
        private SplitContainer mainSplitContainer;
        private KryptonPanel settingsPanel;
        private KryptonPanel resultsPanel;
        private TabControl settingsTabControl;
        private TabControl resultsTabControl;
        private KryptonButton btnRun;
        private KryptonButton btnStop;
        private KryptonButton btnSaveResults;
        private KryptonButton btnCalibration;
        private KryptonButton btnMaterialProperties;
        private KryptonButton btnSaveSimulation;  // New button
        private KryptonButton btnLoadSimulation;  // New button
        private KryptonButton btnLoadPNM;         // New button
        private CheckBox chkUseGPU;
        private NumericUpDown numThreads;
        private NumericUpDown numMaxTime;
        private NumericUpDown numTimePoints;
        private NumericUpDown numT2Components;
        private NumericUpDown numMinT2;
        private NumericUpDown numMaxT2;
        private ProgressBar progressBar;
        private ToolStripStatusLabel lblStatus;
        private RichTextBox txtResults;
        private PictureBox pbDecayCurve;
        private PictureBox pbT2Distribution;
        private PictureBox pbOverview;
        private CheckBox chkLogScale;
        private CheckBox chkShowComponents;

        // Material properties controls
        private DataGridView dgvMaterials;
        private CancellationTokenSource _cancellationTokenSource;

        public NMRSimulationForm(MainForm mainForm)
        {
            try
            {
                this.Icon = CTS.Properties.Resources.favicon;
            }
            catch { }

            _mainForm = mainForm ?? throw new ArgumentNullException(nameof(mainForm));
            _simulation = new NMRSimulation(_mainForm);
            _calibration = new NMRCalibration();
            _plotter = new NMRResultPlotter();

            InitializeComponent();
            this.ResizeEnd += (s, e) => UpdatePlots();
            LoadMaterialProperties();
        }

        private void InitializeComponent()
        {
            this.Text = "NMR Simulation";
            this.Size = new Size(1400, 900);
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.StartPosition = FormStartPosition.CenterParent;
            this.ShowInTaskbar = true;

            // Create the main layout with a horizontal split (50/50)
            mainSplitContainer = new SplitContainer
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                Orientation = Orientation.Vertical,
                Panel1MinSize = 400
            };

            // Create right panel vertical split (plots on top, materials on bottom)
            var rightSplitContainer = new SplitContainer
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                Orientation = Orientation.Horizontal,
                Panel1MinSize = 300,
                Panel2MinSize = 150
            };

            // Left panel - Controls tab panel
            settingsPanel = new KryptonPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(5)
            };

            // Top right panel - Result plots
            resultsPanel = new KryptonPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(5)
            };

            // Initialize tab controls with proper settings to avoid truncation
            InitializeSettingsTabControl();
            InitializeResultsTabControl();
            InitializeMaterialsPanel(rightSplitContainer.Panel2);

            // Add panels to their containers
            mainSplitContainer.Panel1.Controls.Add(settingsPanel);
            mainSplitContainer.Panel2.Controls.Add(rightSplitContainer);
            rightSplitContainer.Panel1.Controls.Add(resultsPanel);

            this.Controls.Add(mainSplitContainer);

            // Status bar
            var statusStrip = new StatusStrip();
            lblStatus = new ToolStripStatusLabel { Text = "Ready", ForeColor = Color.White, BackColor = Color.Black };
            progressBar = new ProgressBar { Width = 200, Height = 16 };

            statusStrip.Items.Add(lblStatus);
            statusStrip.Items.Add(new ToolStripSeparator());
            statusStrip.Items.Add(new ToolStripControlHost(progressBar));

            this.Controls.Add(statusStrip);

            // Set split distances after adding to form - prevents errors
            this.Load += (s, e) => {
                // Equal split for main container
                mainSplitContainer.SplitterDistance = mainSplitContainer.Width / 2;
                // 70/30 split for right container
                rightSplitContainer.SplitterDistance = (int)(rightSplitContainer.Height * 0.7);
            };
        }

        private void InitializeSettingsTabControl()
        {
            settingsTabControl = new TabControl
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                DrawMode = TabDrawMode.OwnerDrawFixed,
                ItemSize = new Size(100, 30), // Make tabs wider to avoid truncation
                SizeMode = TabSizeMode.Fixed,
                Padding = new Point(15, 4)
            };
            settingsTabControl.DrawItem += TabControl_DrawItem;

            // Create tabs
            var simulationTab = new TabPage("Simulation");
            simulationTab.BackColor = Color.Black;
            var simulationPanel = CreateSimulationControlsPanel();
            simulationTab.Controls.Add(simulationPanel);

            var performanceTab = new TabPage("Performance");
            performanceTab.BackColor = Color.Black;
            var performancePanel = CreatePerformancePanel();
            performanceTab.Controls.Add(performancePanel);

            var controlsTab = new TabPage("Controls");
            controlsTab.BackColor = Color.Black;
            var controlPanel = CreateControlButtons();
            controlsTab.Controls.Add(controlPanel);

            // Add tabs to control
            settingsTabControl.TabPages.Add(simulationTab);
            settingsTabControl.TabPages.Add(performanceTab);
            settingsTabControl.TabPages.Add(controlsTab);

            settingsPanel.Controls.Add(settingsTabControl);
        }

        private void InitializeResultsTabControl()
        {
            resultsTabControl = new TabControl
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                DrawMode = TabDrawMode.OwnerDrawFixed,
                ItemSize = new Size(100, 30),
                SizeMode = TabSizeMode.Fixed,
                Padding = new Point(15, 4)
            };
            resultsTabControl.DrawItem += TabControl_DrawItem;

            // Create tabs
            var resultsTab = new TabPage("Results");
            resultsTab.BackColor = Color.Black;
            txtResults = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackColor = Color.Black,
                ForeColor = Color.White,
                Font = new Font("Consolas", 9)
            };
            resultsTab.Controls.Add(txtResults);

            // Decay curve tab with export button
            var decayTab = new TabPage("Decay Curve");
            decayTab.BackColor = Color.Black;
            var decayPanel = CreateDecayCurvePanel();
            decayTab.Controls.Add(decayPanel);

            // T2 distribution tab with export button  
            var t2Tab = new TabPage("T2 Distribution");
            t2Tab.BackColor = Color.Black;

            // Create panel for T2 distribution with export button
            var t2Panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };

            var t2ExportButton = new KryptonButton
            {
                Text = "Export Image",
                Values = { Image = CreateExportIcon() },
                Dock = DockStyle.Bottom,
                Height = 30
            };
            t2ExportButton.Click += (s, e) => ExportT2DistributionImage();

            pbT2Distribution = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.StretchImage,
                BackColor = Color.Black
            };

            t2Panel.Controls.Add(pbT2Distribution);
            t2Panel.Controls.Add(t2ExportButton);
            t2Tab.Controls.Add(t2Panel);

            // Overview tab (already has export button)
            var overviewTab = new TabPage("Overview");
            overviewTab.BackColor = Color.Black;

            var overviewPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };

            var exportButton = new KryptonButton
            {
                Text = "Export Image",
                Values = { Image = CreateExportIcon() },
                Dock = DockStyle.Bottom,
                Height = 30
            };
            exportButton.Click += (s, e) => ExportOverviewImage();

            pbOverview = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.StretchImage,
                BackColor = Color.Black
            };

            overviewPanel.Controls.Add(pbOverview);
            overviewPanel.Controls.Add(exportButton);
            overviewTab.Controls.Add(overviewPanel);

            // Add tabs to control
            resultsTabControl.TabPages.Add(resultsTab);
            resultsTabControl.TabPages.Add(decayTab);
            resultsTabControl.TabPages.Add(t2Tab);
            resultsTabControl.TabPages.Add(overviewTab);

            resultsPanel.Controls.Add(resultsTabControl);
        }
        private Image CreateExportIcon()
        {
            var bitmap = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                using (var pen = new Pen(Color.White, 2))
                {
                    // Draw image icon
                    g.DrawRectangle(pen, 2, 2, 12, 10);

                    // Draw lines representing image content
                    g.DrawLine(pen, 4, 7, 7, 10);
                    g.DrawLine(pen, 7, 10, 10, 5);

                    // Draw circle for sun/mountain
                    g.DrawEllipse(pen, 9, 4, 3, 3);

                    // Draw arrow pointing down
                    g.DrawLine(pen, 8, 13, 8, 16);
                    g.DrawLine(pen, 6, 14, 8, 16);
                    g.DrawLine(pen, 10, 14, 8, 16);
                }
            }
            return bitmap;
        }

        private void InitializeMaterialsPanel(Control parent)
        {
            var materialsGroup = new KryptonGroupBox
            {
                Text = "Material Properties",
                Dock = DockStyle.Fill
            };

            materialsGroup.StateCommon.Back.Color1 = Color.Black;
            materialsGroup.StateCommon.Back.Color2 = Color.Black;
            materialsGroup.StateCommon.Border.Color1 = Color.DarkGray;
            materialsGroup.StateCommon.Border.Color2 = Color.DarkGray;

            // Create materials data grid
            dgvMaterials = new DataGridView
            {
                Dock = DockStyle.Fill,
                AutoGenerateColumns = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                BackgroundColor = Color.Black,
                ForeColor = Color.White,
                ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
                {
                    BackColor = Color.FromArgb(40, 40, 40),
                    ForeColor = Color.White,
                    Font = new Font("Segoe UI", 9, FontStyle.Bold),
                    SelectionBackColor = Color.FromArgb(60, 60, 60),
                    SelectionForeColor = Color.White
                },
                RowHeadersDefaultCellStyle = new DataGridViewCellStyle
                {
                    BackColor = Color.FromArgb(40, 40, 40),
                    ForeColor = Color.White,
                    SelectionBackColor = Color.FromArgb(60, 60, 60)
                },
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    BackColor = Color.FromArgb(20, 20, 20),
                    ForeColor = Color.White,
                    SelectionBackColor = Color.FromArgb(80, 80, 80),
                    SelectionForeColor = Color.White
                },
                AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
                {
                    BackColor = Color.FromArgb(30, 30, 30),
                    ForeColor = Color.White,
                    SelectionBackColor = Color.FromArgb(80, 80, 80),
                    SelectionForeColor = Color.White
                },
                GridColor = Color.FromArgb(60, 60, 60),
                BorderStyle = BorderStyle.None,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                EnableHeadersVisualStyles = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };

            // Columns
            dgvMaterials.Columns.Add("Material", "Material");
            dgvMaterials.Columns.Add("T2", "T2 (ms)");
            dgvMaterials.Columns.Add("Density", "H Density");
            dgvMaterials.Columns.Add("Tortuosity", "Tortuosity");
            dgvMaterials.Columns.Add("Strength", "Rel. Strength");

            // Event handlers
            dgvMaterials.CellValueChanged += DgvMaterials_CellValueChanged;
            dgvMaterials.CellDoubleClick += DgvMaterials_CellDoubleClick;

            materialsGroup.Panel.Controls.Add(dgvMaterials);
            parent.Controls.Add(materialsGroup);
        }

        private Panel CreateSimulationControlsPanel()
        {
            var panel = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Color.Black
            };

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                ColumnCount = 2,
                AutoSize = true,
                Padding = new Padding(10),
                BackColor = Color.Black
            };

            int row = 0;

            // Max time
            layout.Controls.Add(new Label { Text = "Max Time (ms):", ForeColor = Color.White, AutoSize = true }, 0, row);
            numMaxTime = new NumericUpDown
            {
                Minimum = 10,
                Maximum = 10000,
                Value = (decimal)_simulation.MaxTime,
                DecimalPlaces = 1,
                Increment = 10,
                BackColor = Color.Black,
                ForeColor = Color.White,
                Width = 150
            };
            numMaxTime.ValueChanged += (s, e) => _simulation.MaxTime = (double)numMaxTime.Value;
            layout.Controls.Add(numMaxTime, 1, row++);

            // Time points
            layout.Controls.Add(new Label { Text = "Time Points:", ForeColor = Color.White, AutoSize = true }, 0, row);
            numTimePoints = new NumericUpDown
            {
                Minimum = 100,
                Maximum = 10000,
                Value = _simulation.TimePoints,
                Increment = 100,
                BackColor = Color.Black,
                ForeColor = Color.White,
                Width = 150
            };
            numTimePoints.ValueChanged += (s, e) => _simulation.TimePoints = (int)numTimePoints.Value;
            layout.Controls.Add(numTimePoints, 1, row++);

            // T2 components
            layout.Controls.Add(new Label { Text = "T2 Components:", ForeColor = Color.White, AutoSize = true }, 0, row);
            numT2Components = new NumericUpDown
            {
                Minimum = 8,
                Maximum = 128,
                Value = _simulation.T2Components,
                Increment = 4,
                BackColor = Color.Black,
                ForeColor = Color.White,
                Width = 150
            };
            numT2Components.ValueChanged += (s, e) => _simulation.T2Components = (int)numT2Components.Value;
            layout.Controls.Add(numT2Components, 1, row++);

            // Min T2
            layout.Controls.Add(new Label { Text = "Min T2 (ms):", ForeColor = Color.White, AutoSize = true }, 0, row);
            numMinT2 = new NumericUpDown
            {
                Minimum = 0.01m,
                Maximum = 100,
                Value = (decimal)_simulation.MinT2,
                DecimalPlaces = 2,
                Increment = 0.1m,
                BackColor = Color.Black,
                ForeColor = Color.White,
                Width = 150
            };
            numMinT2.ValueChanged += (s, e) => _simulation.MinT2 = (double)numMinT2.Value;
            layout.Controls.Add(numMinT2, 1, row++);

            // Max T2
            layout.Controls.Add(new Label { Text = "Max T2 (ms):", ForeColor = Color.White, AutoSize = true }, 0, row);
            numMaxT2 = new NumericUpDown
            {
                Minimum = 100,
                Maximum = 10000,
                Value = (decimal)_simulation.MaxT2,
                DecimalPlaces = 1,
                Increment = 100,
                BackColor = Color.Black,
                ForeColor = Color.White,
                Width = 150
            };
            numMaxT2.ValueChanged += (s, e) => _simulation.MaxT2 = (double)numMaxT2.Value;
            layout.Controls.Add(numMaxT2, 1, row++);

            // Set row styles for consistent spacing
            for (int i = 0; i < row; i++)
            {
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            }
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

            panel.Controls.Add(layout);
            return panel;
        }

        private Panel CreatePerformancePanel()
        {
            var panel = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Color.Black
            };

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                ColumnCount = 2,
                AutoSize = true,
                Padding = new Padding(10),
                BackColor = Color.Black
            };

            int row = 0;

            // GPU checkbox
            chkUseGPU = new CheckBox
            {
                Text = "Use GPU Acceleration",
                Checked = _simulation.UseGPU && NMRGPUDirectCompute.IsGPUAvailable(),
                Enabled = NMRGPUDirectCompute.IsGPUAvailable(),
                ForeColor = Color.White,
                AutoSize = true
            };
            chkUseGPU.CheckedChanged += (s, e) => _simulation.UseGPU = chkUseGPU.Checked;
            layout.Controls.Add(chkUseGPU, 0, row);
            layout.SetColumnSpan(chkUseGPU, 2);
            row++;

            // Threads
            layout.Controls.Add(new Label { Text = "CPU Threads:", ForeColor = Color.White, AutoSize = true }, 0, row);
            numThreads = new NumericUpDown
            {
                Minimum = 1,
                Maximum = Environment.ProcessorCount,
                Value = _simulation.MaxThreads,
                BackColor = Color.Black,
                ForeColor = Color.White,
                Width = 150
            };
            numThreads.ValueChanged += (s, e) => _simulation.MaxThreads = (int)numThreads.Value;
            layout.Controls.Add(numThreads, 1, row++);

            // Set row styles for consistent spacing
            for (int i = 0; i < row; i++)
            {
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            }
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

            panel.Controls.Add(layout);
            return panel;
        }

        private void TabControl_DrawItem(object sender, DrawItemEventArgs e)
        {
            TabControl tabControl = sender as TabControl;
            if (tabControl == null) return;

            Graphics g = e.Graphics;
            Rectangle tabBounds = tabControl.GetTabRect(e.Index);

            // Add extra padding to avoid text truncation
            tabBounds.Inflate(-2, 0);

            // Set colors based on selection state
            if (e.State == DrawItemState.Selected)
            {
                g.FillRectangle(new SolidBrush(Color.FromArgb(50, 50, 50)), tabBounds);
                g.DrawRectangle(new Pen(Color.FromArgb(70, 70, 70)), tabBounds);
                tabBounds.Offset(0, 1); // Offset text slightly for selected tab
            }
            else
            {
                g.FillRectangle(new SolidBrush(Color.FromArgb(25, 25, 25)), tabBounds);
            }

            // Draw tab text
            StringFormat stringFormat = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            };

            using (Font font = new Font("Segoe UI", 9, FontStyle.Regular))
            {
                string tabText = tabControl.TabPages[e.Index].Text;
                g.DrawString(tabText, font, new SolidBrush(Color.White), tabBounds, stringFormat);
            }
        }

        private Panel CreateControlButtons()
        {
            var panel = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Color.Black
            };

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Top,  // Changed from Fill to Top
                ColumnCount = 1,
                RowCount = 8,
                Padding = new Padding(10),
                BackColor = Color.Black,
                AutoSize = true  // Added to prevent expansion
            };

            // Run button
            btnRun = new KryptonButton
            {
                Text = "Run Simulation",
                Dock = DockStyle.Fill,
                Values = { Image = CreatePlayIcon() }
            };
            btnRun.Click += async (s, e) => await RunSimulationAsync();
            layout.Controls.Add(btnRun, 0, 0);

            // Stop button
            btnStop = new KryptonButton
            {
                Text = "Stop Simulation",
                Dock = DockStyle.Fill,
                Enabled = false,
                Values = { Image = CreateStopIcon() }
            };
            btnStop.Click += (s, e) => StopSimulation();
            layout.Controls.Add(btnStop, 0, 1);

            // Save results button
            btnSaveResults = new KryptonButton
            {
                Text = "Save Results",
                Dock = DockStyle.Fill,
                Enabled = false,
                Values = { Image = CreateSaveIcon() }
            };
            btnSaveResults.Click += SaveResults;
            layout.Controls.Add(btnSaveResults, 0, 2);

            // Calibration button
            btnCalibration = new KryptonButton
            {
                Text = "Calibration...",
                Dock = DockStyle.Fill,
                Values = { Image = CreateCalibrationIcon() }
            };
            btnCalibration.Click += OpenCalibrationDialog;
            layout.Controls.Add(btnCalibration, 0, 3);

            // Material properties button
            btnMaterialProperties = new KryptonButton
            {
                Text = "Material Properties...",
                Dock = DockStyle.Fill,
                Values = { Image = CreatePropertiesIcon() }
            };
            btnMaterialProperties.Click += OpenMaterialPropertiesDialog;
            layout.Controls.Add(btnMaterialProperties, 0, 4);

            // Save simulation settings button
            btnSaveSimulation = new KryptonButton
            {
                Text = "Save Settings",
                Dock = DockStyle.Fill,
                Values = { Image = CreateSaveIcon() }
            };
            btnSaveSimulation.Click += SaveSimulationSettings;
            layout.Controls.Add(btnSaveSimulation, 0, 5);

            // Load simulation settings button
            btnLoadSimulation = new KryptonButton
            {
                Text = "Load Settings",
                Dock = DockStyle.Fill,
                Values = { Image = CreateLoadIcon() }
            };
            btnLoadSimulation.Click += LoadSimulationSettings;
            layout.Controls.Add(btnLoadSimulation, 0, 6);

            // Load PNM button
            btnLoadPNM = new KryptonButton
            {
                Text = "Load PNM Data",
                Dock = DockStyle.Fill,
                Values = { Image = CreatePNMIcon() }
            };
            btnLoadPNM.Click += LoadPNMData;
            layout.Controls.Add(btnLoadPNM, 0, 7);

            // Set row styles for buttons - ALL with fixed height
            for (int i = 0; i < 8; i++)
            {
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            }
            
            panel.Controls.Add(layout);
            return panel;
        }

        private Panel CreateDecayCurvePanel()
        {
            var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };

            var optionsPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 35,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(5),
                BackColor = Color.Black
            };

            chkLogScale = new CheckBox
            {
                Text = "Log Scale",
                Checked = true,
                ForeColor = Color.White,
                BackColor = Color.Black,
                Margin = new Padding(5, 5, 15, 5)
            };
            chkLogScale.CheckedChanged += (s, e) => UpdatePlots();

            chkShowComponents = new CheckBox
            {
                Text = "Show Components",
                Checked = true,
                ForeColor = Color.White,
                BackColor = Color.Black
            };
            chkShowComponents.CheckedChanged += (s, e) => UpdatePlots();

            optionsPanel.Controls.Add(chkLogScale);
            optionsPanel.Controls.Add(chkShowComponents);

            // Add export button to bottom
            var exportButton = new KryptonButton
            {
                Text = "Export Image",
                Values = { Image = CreateExportIcon() },
                Dock = DockStyle.Bottom,
                Height = 30
            };
            exportButton.Click += (s, e) => ExportDecayCurveImage();

            pbDecayCurve = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.StretchImage,
                BackColor = Color.Black
            };

            panel.Controls.Add(pbDecayCurve);
            panel.Controls.Add(optionsPanel);
            panel.Controls.Add(exportButton);

            return panel;
        }
        private void LoadMaterialProperties()
        {
            dgvMaterials.Rows.Clear();

            foreach (var material in _mainForm.Materials)
            {
                // Skip exterior material (ID 0)
                if (material.ID == 0)
                    continue;

                var properties = _simulation.GetMaterialProperties(material.ID);
                var row = new DataGridViewRow();
                row.CreateCells(dgvMaterials);

                row.Cells[0].Value = material.Name;
                row.Cells[1].Value = properties.RelaxationTime;
                row.Cells[2].Value = properties.Density;
                row.Cells[3].Value = properties.Tortuosity;
                row.Cells[4].Value = properties.RelaxationStrength;
                row.Tag = material.ID;

                dgvMaterials.Rows.Add(row);
            }
        }

        private void DgvMaterials_CellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0)
                return;

            var row = dgvMaterials.Rows[e.RowIndex];
            byte materialID = (byte)row.Tag;
            var properties = _simulation.GetMaterialProperties(materialID);

            try
            {
                switch (e.ColumnIndex)
                {
                    case 1: // T2
                        properties.RelaxationTime = Convert.ToDouble(row.Cells[1].Value);
                        break;
                    case 2: // Density
                        properties.Density = Convert.ToDouble(row.Cells[2].Value);
                        break;
                    case 3: // Tortuosity
                        properties.Tortuosity = Convert.ToDouble(row.Cells[3].Value);
                        break;
                    case 4: // Relaxation Strength
                        properties.RelaxationStrength = Convert.ToDouble(row.Cells[4].Value);
                        break;
                }

                _simulation.SetMaterialProperties(materialID, properties);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Invalid value: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                LoadMaterialProperties(); // Reload to reset the value
            }
        }

        private void DgvMaterials_CellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0)
                return;

            var row = dgvMaterials.Rows[e.RowIndex];
            byte materialID = (byte)row.Tag;

            using (var dialog = new MaterialNMRPropertiesDialog(_simulation.GetMaterialProperties(materialID)))
            {
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    _simulation.SetMaterialProperties(materialID, dialog.Properties);
                    LoadMaterialProperties();
                }
            }
        }

        // NEW METHODS FOR SAVE/LOAD FUNCTIONALITY
        private void SaveSimulationSettings(object sender, EventArgs e)
        {
            using (var dialog = new SaveFileDialog())
            {
                dialog.Filter = "NMR Settings|*.nmr|JSON Files|*.json|All Files|*.*";
                dialog.Title = "Save NMR Simulation Settings";
                dialog.DefaultExt = "nmr";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        var settings = new
                        {
                            SimulationSettings = new
                            {
                                MaxTime = _simulation.MaxTime,
                                TimePoints = _simulation.TimePoints,
                                T2Components = _simulation.T2Components,
                                MinT2 = _simulation.MinT2,
                                MaxT2 = _simulation.MaxT2
                            },
                            PerformanceSettings = new
                            {
                                UseGPU = _simulation.UseGPU,
                                MaxThreads = _simulation.MaxThreads
                            },
                            MaterialProperties = _mainForm.Materials
                                .Where(m => m.ID != 0)  // Skip exterior material
                                .ToDictionary(
                                    m => m.Name,
                                    m => _simulation.GetMaterialProperties(m.ID)
                                ),
                            SaveDate = DateTime.Now
                        };

                        string json = System.Text.Json.JsonSerializer.Serialize(settings, new System.Text.Json.JsonSerializerOptions
                        {
                            WriteIndented = true
                        });

                        File.WriteAllText(dialog.FileName, json);
                        MessageBox.Show("Settings saved successfully!", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error saving settings: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void LoadSimulationSettings(object sender, EventArgs e)
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Filter = "NMR Settings|*.nmr|JSON Files|*.json|All Files|*.*";
                dialog.Title = "Load NMR Simulation Settings";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        string json = File.ReadAllText(dialog.FileName);
                        using (var document = System.Text.Json.JsonDocument.Parse(json))
                        {
                            var root = document.RootElement;

                            // Load simulation settings
                            if (root.TryGetProperty("SimulationSettings", out var simSettings))
                            {
                                if (simSettings.TryGetProperty("MaxTime", out var maxTime))
                                {
                                    _simulation.MaxTime = maxTime.GetDouble();
                                    numMaxTime.Value = (decimal)_simulation.MaxTime;
                                }
                                if (simSettings.TryGetProperty("TimePoints", out var timePoints))
                                {
                                    _simulation.TimePoints = timePoints.GetInt32();
                                    numTimePoints.Value = _simulation.TimePoints;
                                }
                                if (simSettings.TryGetProperty("T2Components", out var t2Components))
                                {
                                    _simulation.T2Components = t2Components.GetInt32();
                                    numT2Components.Value = _simulation.T2Components;
                                }
                                if (simSettings.TryGetProperty("MinT2", out var minT2))
                                {
                                    _simulation.MinT2 = minT2.GetDouble();
                                    numMinT2.Value = (decimal)_simulation.MinT2;
                                }
                                if (simSettings.TryGetProperty("MaxT2", out var maxT2))
                                {
                                    _simulation.MaxT2 = maxT2.GetDouble();
                                    numMaxT2.Value = (decimal)_simulation.MaxT2;
                                }
                            }

                            // Load performance settings
                            if (root.TryGetProperty("PerformanceSettings", out var perfSettings))
                            {
                                if (perfSettings.TryGetProperty("UseGPU", out var useGpu))
                                {
                                    _simulation.UseGPU = useGpu.GetBoolean();
                                    chkUseGPU.Checked = _simulation.UseGPU;
                                }
                                if (perfSettings.TryGetProperty("MaxThreads", out var maxThreads))
                                {
                                    _simulation.MaxThreads = maxThreads.GetInt32();
                                    numThreads.Value = _simulation.MaxThreads;
                                }
                            }

                            // Load material properties
                            if (root.TryGetProperty("MaterialProperties", out var materialProps))
                            {
                                foreach (var material in _mainForm.Materials)
                                {
                                    if (material.ID == 0) continue; // Skip exterior

                                    if (materialProps.TryGetProperty(material.Name, out var propData))
                                    {
                                        var properties = new MaterialNMRProperties
                                        {
                                            MaterialName = material.Name
                                        };

                                        if (propData.TryGetProperty("RelaxationTime", out var relaxationTime))
                                            properties.RelaxationTime = relaxationTime.GetDouble();
                                        if (propData.TryGetProperty("Density", out var density))
                                            properties.Density = density.GetDouble();
                                        if (propData.TryGetProperty("Tortuosity", out var tortuosity))
                                            properties.Tortuosity = tortuosity.GetDouble();
                                        if (propData.TryGetProperty("RelaxationStrength", out var relaxationStrength))
                                            properties.RelaxationStrength = relaxationStrength.GetDouble();

                                        _simulation.SetMaterialProperties(material.ID, properties);
                                    }
                                }
                            }
                        }

                        // Refresh the UI
                        LoadMaterialProperties();
                        MessageBox.Show("Settings loaded successfully!", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error loading settings: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void LoadPNMData(object sender, EventArgs e)
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Filter = "All PNM Files|*.dat;*.csv|Binary Files|*.dat|CSV Files|*.csv|All Files|*.*";
                dialog.Title = "Load PNM Data";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        // Ask user which material to apply the data to
                        var materialDialog = new MaterialSelectorDialog(_mainForm.Materials);
                        if (materialDialog.ShowDialog() != DialogResult.OK)
                            return;

                        var selectedMaterial = materialDialog.SelectedMaterial;
                        if (selectedMaterial == null || selectedMaterial.ID == 0)
                        {
                            MessageBox.Show("Please select a valid material.", "Invalid Selection",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }

                        // Load and parse PNM data
                        PoreNetworkProperties pnmProperties = null;

                        string extension = Path.GetExtension(dialog.FileName).ToLower();
                        if (extension == ".dat")
                        {
                            pnmProperties = LoadBinaryPNMData(dialog.FileName);
                        }
                        else if (extension == ".csv")
                        {
                            pnmProperties = LoadCSVPNMData(dialog.FileName);
                        }
                        else
                        {
                            // Try to detect format based on content
                            pnmProperties = TryLoadPNMData(dialog.FileName);
                        }

                        if (pnmProperties == null)
                        {
                            MessageBox.Show("Unable to parse the PNM data file.", "Error",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return;
                        }

                        // Apply PNM properties to selected material
                        ApplyPNMToMaterial(selectedMaterial.ID, pnmProperties);

                        MessageBox.Show($"PNM data successfully applied to material '{selectedMaterial.Name}'!",
                            "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error loading PNM data: {ex.Message}", "Error",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void ApplyPNMToMaterial(byte materialId, PoreNetworkProperties pnmProperties)
        {
            var current = _simulation.GetMaterialProperties(materialId);

            // Map PNM properties to NMR properties
            var nmrProperties = new MaterialNMRProperties
            {
                MaterialName = current.MaterialName,
                // Use average pore radius for T2 estimation (Brownstein-Tarr model)
                RelaxationTime = EstimateT2FromPoreSize(pnmProperties.AveragePoreRadius, pnmProperties.Porosity),
                // Porosity as density approximation
                Density = pnmProperties.Porosity * 1.5, // Scale factor for typical rock-water systems
                // Use tortuosity directly
                Tortuosity = pnmProperties.Tortuosity,
                // Estimate relaxation strength based on connectivity
                RelaxationStrength = EstimateRelaxationStrength(pnmProperties.AverageConnectivity),
                // Keep existing porosity effect or use default
                PorosityEffect = current.PorosityEffect
            };

            _simulation.SetMaterialProperties(materialId, nmrProperties);
            LoadMaterialProperties(); // Refresh the UI
        }

        private double EstimateT2FromPoreSize(double avgRadius, double porosity)
        {
            // Brownstein-Tarr model: T2 = rho * (V/S)
            // Where rho is surface relaxivity (~1-10 μm/ms for water-wet surfaces)
            double surfaceRelaxivity = 2.0; // μm/ms - typical for water-wet surfaces

            // Estimate surface-to-volume ratio from pore radius
            double volumeToSurfaceRatio = avgRadius / 3.0; // Sphere approximation

            double t2 = surfaceRelaxivity * volumeToSurfaceRatio;

            // Adjust based on porosity (higher porosity = less surface relaxation)
            t2 *= (1 + porosity * 0.5);

            // Clamp to reasonable range
            return Math.Max(1.0, Math.Min(5000.0, t2));
        }

        private double EstimateRelaxationStrength(double avgConnectivity)
        {
            // Higher connectivity typically means more uniform relaxation
            // Lower connectivity means more varied environments
            if (avgConnectivity < 2)
                return 0.6; // Low connectivity = high variability
            else if (avgConnectivity < 4)
                return 1.0; // Average connectivity = normal variability
            else
                return 1.5; // High connectivity = low variability
        }

        // Create icon methods
        private Image CreatePlayIcon()
        {
            var bitmap = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                using (var brush = new SolidBrush(Color.Green))
                {
                    Point[] points = new Point[]
                    {
                        new Point(4, 2),
                        new Point(4, 14),
                        new Point(14, 8)
                    };
                    g.FillPolygon(brush, points);
                }
            }
            return bitmap;
        }

        private Image CreateStopIcon()
        {
            var bitmap = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                using (var brush = new SolidBrush(Color.Red))
                {
                    g.FillRectangle(brush, 3, 3, 10, 10);
                }
            }
            return bitmap;
        }

        private Image CreateSaveIcon()
        {
            var bitmap = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                using (var pen = new Pen(Color.Blue, 2))
                {
                    g.DrawRectangle(pen, 2, 2, 10, 12);
                    g.DrawRectangle(pen, 3, 8, 8, 6);
                    g.DrawLine(pen, 5, 2, 5, 8);
                    g.DrawLine(pen, 9, 2, 9, 8);
                }
            }
            return bitmap;
        }

        private Image CreateLoadIcon()
        {
            var bitmap = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                using (var pen = new Pen(Color.Green, 2))
                {
                    // Folder icon
                    g.DrawRectangle(pen, 2, 4, 12, 10);
                    g.DrawLine(pen, 2, 4, 6, 4);
                    g.DrawLine(pen, 6, 4, 8, 2);
                    g.DrawLine(pen, 8, 2, 14, 2);
                    g.DrawLine(pen, 14, 2, 14, 4);

                    // Arrow pointing up
                    g.DrawLine(pen, 8, 11, 8, 6);
                    g.DrawLine(pen, 6, 8, 8, 6);
                    g.DrawLine(pen, 10, 8, 8, 6);
                }
            }
            return bitmap;
        }

        private Image CreatePNMIcon()
        {
            var bitmap = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                using (var pen = new Pen(Color.Orange, 2))
                using (var brush = new SolidBrush(Color.Orange))
                {
                    // Draw network of connected nodes
                    // Nodes
                    g.FillEllipse(brush, 2, 2, 4, 4);
                    g.FillEllipse(brush, 11, 2, 4, 4);
                    g.FillEllipse(brush, 2, 11, 4, 4);
                    g.FillEllipse(brush, 11, 11, 4, 4);
                    g.FillEllipse(brush, 6, 6, 4, 4);

                    // Connections
                    g.DrawLine(pen, 4, 4, 8, 8);
                    g.DrawLine(pen, 12, 4, 8, 8);
                    g.DrawLine(pen, 4, 12, 8, 8);
                    g.DrawLine(pen, 12, 12, 8, 8);
                }
            }
            return bitmap;
        }

        private Image CreateCalibrationIcon()
        {
            var bitmap = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                using (var pen = new Pen(Color.Purple, 2))
                {
                    g.DrawLine(pen, 2, 14, 14, 2);
                    g.DrawEllipse(pen, 1, 1, 4, 4);
                    g.DrawEllipse(pen, 11, 11, 4, 4);
                }
            }
            return bitmap;
        }

        private Image CreatePropertiesIcon()
        {
            var bitmap = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                using (var pen = new Pen(Color.DarkBlue, 2))
                {
                    g.DrawRectangle(pen, 1, 1, 14, 14);
                    g.DrawLine(pen, 1, 5, 15, 5);
                    g.DrawLine(pen, 1, 9, 15, 9);
                    g.DrawLine(pen, 5, 1, 5, 15);
                }
            }
            return bitmap;
        }

        private void ExportOverviewImage()
        {
            if (_lastResult == null)
            {
                MessageBox.Show("No simulation results to export.", "Export Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using (var dialog = new SaveFileDialog())
            {
                dialog.Filter = "PNG Image|*.png|JPEG Image|*.jpg";
                dialog.Title = "Export NMR Overview";
                dialog.DefaultExt = "png";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        _plotter.ExportOverview(_lastResult, dialog.FileName);
                        MessageBox.Show("Overview image exported successfully!", "Export Complete",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error exporting image: {ex.Message}", "Export Error",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        // Missing methods from the original implementation
        private async Task RunSimulationAsync()
        {
            if (_isRunning)
                return;

            _isRunning = true;
            btnRun.Enabled = false;
            btnStop.Enabled = true;
            btnSaveResults.Enabled = false;
            progressBar.Style = ProgressBarStyle.Marquee;

            // Create new cancellation token source
            _cancellationTokenSource = new CancellationTokenSource();

            try
            {
                lblStatus.Text = "Running NMR simulation...";
                txtResults.Clear();
                txtResults.AppendText("Starting NMR simulation...\n");
                txtResults.AppendText($"Volume size: {_mainForm.GetWidth()}x{_mainForm.GetHeight()}x{_mainForm.GetDepth()}\n");
                txtResults.AppendText($"Pixel size: {_mainForm.GetPixelSize() * 1e6:F2} µm\n");
                txtResults.AppendText($"GPU acceleration: {(chkUseGPU.Checked ? "Yes" : "No")}\n");
                txtResults.AppendText($"CPU threads: {numThreads.Value}\n");
                txtResults.AppendText("----------------------------------------\n");

                // Run simulation with cancellation token
                _lastResult = await Task.Run(() => _simulation.RunSimulationAsync(
                    _calibration.IsCalibrated ? _calibration : null,
                    _cancellationTokenSource.Token), _cancellationTokenSource.Token);

                // Check if cancelled
                _cancellationTokenSource.Token.ThrowIfCancellationRequested();

                // Display results
                DisplayResults(_lastResult);

                // Update plots
                UpdatePlots();

                lblStatus.Text = "Simulation completed";
                btnSaveResults.Enabled = true;
            }
            catch (OperationCanceledException)
            {
                lblStatus.Text = "Simulation cancelled";
                txtResults.AppendText("Simulation was cancelled by user.\n");
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Simulation failed";
                txtResults.AppendText($"ERROR: {ex.Message}\n");
                Logger.Log($"[NMRSimulationForm] Simulation error: {ex.Message}");
                MessageBox.Show($"Simulation failed: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _isRunning = false;
                btnRun.Enabled = true;
                btnStop.Enabled = false;
                progressBar.Style = ProgressBarStyle.Continuous;
                progressBar.Value = 0;

                // Dispose the cancellation token source
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
            }
        }

        private void StopSimulation()
        {
            if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
            {
                lblStatus.Text = "Stopping simulation...";
                _cancellationTokenSource.Cancel();
            }
        }

        private void DisplayResults(NMRSimulationResult result)
        {
            txtResults.AppendText($"Simulation completed in {result.SimulationTime:F1} ms\n");
            txtResults.AppendText($"Used {result.ThreadsUsed} threads\n");
            txtResults.AppendText($"GPU acceleration: {(result.UsedGPU ? "Yes" : "No")}\n");
            txtResults.AppendText("----------------------------------------\n");
            txtResults.AppendText($"Total Porosity: {result.TotalPorosity:P2}\n");
            txtResults.AppendText($"Average T2: {result.AverageT2:F1} ms\n");
            txtResults.AppendText($"Average Tortuosity: {result.AverageTortuosity:F2}\n");
            txtResults.AppendText($"Number of Components: {result.FittedComponents?.Count ?? 0}\n");
            txtResults.AppendText("----------------------------------------\n");

            if (result.FittedComponents != null && result.FittedComponents.Count > 0)
            {
                txtResults.AppendText("Top Relaxation Components:\n");
                var topComponents = result.FittedComponents
                    .OrderByDescending(c => c.Amplitude)
                    .Take(10)
                    .ToList();

                foreach (var component in topComponents)
                {
                    txtResults.AppendText($"  T2={component.RelaxationTime:F1}ms, Amplitude={component.Amplitude:F4}, Tortuosity={component.Tortuosity:F2}\n");
                }
            }

            if (_calibration.IsCalibrated)
            {
                txtResults.AppendText("----------------------------------------\n");
                txtResults.AppendText("Calibration Applied:\n");
                txtResults.AppendText($"  T2 Calibration R²: {_calibration.T2CalibrationR2:F4}\n");
                txtResults.AppendText($"  Amplitude Calibration R²: {_calibration.AmplitudeCalibrationR2:F4}\n");
            }
        }

        private void UpdatePlots()
        {
            if (_lastResult == null)
                return;

            Task.Run(() =>
            {
                try
                {
                    // Get the ACTUAL sizes of the PictureBox controls
                    Size decaySize = new Size();
                    Size t2Size = new Size();
                    Size overviewSize = new Size();

                    this.Invoke((Action)(() =>
                    {
                        decaySize = pbDecayCurve.ClientSize;
                        t2Size = pbT2Distribution.ClientSize;
                        overviewSize = pbOverview.ClientSize;
                    }));

                    // Ensure minimum sizes for stability
                    decaySize = new Size(Math.Max(decaySize.Width, 400), Math.Max(decaySize.Height, 300));
                    t2Size = new Size(Math.Max(t2Size.Width, 400), Math.Max(t2Size.Height, 300));
                    overviewSize = new Size(Math.Max(overviewSize.Width, 600), Math.Max(overviewSize.Height, 450));

                    // Generate plots with EXACT sizes
                    var decayCurve = _plotter.PlotDecayCurve(_lastResult, decaySize,
                        chkLogScale.Checked, chkShowComponents.Checked);

                    var t2Distribution = _plotter.PlotT2Distribution(_lastResult, t2Size,
                        chkLogScale.Checked);

                    var overview = _plotter.PlotComponentsOverview(_lastResult, overviewSize);

                    // Update UI on main thread
                    this.Invoke((Action)(() =>
                    {
                        pbDecayCurve.Image?.Dispose();
                        pbDecayCurve.Image = decayCurve;

                        pbT2Distribution.Image?.Dispose();
                        pbT2Distribution.Image = t2Distribution;

                        pbOverview.Image?.Dispose();
                        pbOverview.Image = overview;
                    }));
                }
                catch (Exception ex)
                {
                    Logger.Log($"[NMRSimulationForm] Error updating plots: {ex.Message}");
                }
            });
        }

        private void SaveResults(object sender, EventArgs e)
        {
            if (_lastResult == null)
                return;

            using (var dialog = new SaveFileDialog())
            {
                dialog.Title = "Save NMR Simulation Results";
                dialog.Filter = "JSON Results|*.json|CSV Data|*.csv|All Files|*.*";
                dialog.DefaultExt = "json";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        if (dialog.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                        {
                            SaveResultsAsCSV(dialog.FileName);
                        }
                        else
                        {
                            SaveResultsAsJSON(dialog.FileName);
                        }

                        // Save plots
                        string baseName = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName);
                        string directory = System.IO.Path.GetDirectoryName(dialog.FileName);
                        string plotBasePath = System.IO.Path.Combine(directory, baseName);

                        _plotter.SavePlots(_lastResult, plotBasePath);

                        MessageBox.Show("Results saved successfully!", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error saving results: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void SaveResultsAsJSON(string fileName)
        {
            var data = new
            {
                SimulationParameters = new
                {
                    MaxTime = _simulation.MaxTime,
                    TimePoints = _simulation.TimePoints,
                    T2Components = _simulation.T2Components,
                    MinT2 = _simulation.MinT2,
                    MaxT2 = _simulation.MaxT2,
                    UseGPU = _simulation.UseGPU,
                    MaxThreads = _simulation.MaxThreads
                },
                Results = _lastResult,
                Calibration = _calibration.IsCalibrated ? _calibration : null,
                Timestamp = DateTime.Now
            };

            var options = new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            string json = System.Text.Json.JsonSerializer.Serialize(data, options);
            System.IO.File.WriteAllText(fileName, json);
        }

        private void SaveResultsAsCSV(string fileName)
        {
            using (var writer = new System.IO.StreamWriter(fileName))
            {
                // Write header
                writer.WriteLine("NMR Simulation Results");
                writer.WriteLine($"Generated on: {DateTime.Now}");
                writer.WriteLine();

                // Write parameters
                writer.WriteLine("Simulation Parameters");
                writer.WriteLine($"Max Time (ms),{_simulation.MaxTime}");
                writer.WriteLine($"Time Points,{_simulation.TimePoints}");
                writer.WriteLine($"T2 Components,{_simulation.T2Components}");
                writer.WriteLine($"Min T2 (ms),{_simulation.MinT2}");
                writer.WriteLine($"Max T2 (ms),{_simulation.MaxT2}");
                writer.WriteLine($"GPU Acceleration,{_simulation.UseGPU}");
                writer.WriteLine($"CPU Threads,{_simulation.MaxThreads}");
                writer.WriteLine();

                // Write results
                writer.WriteLine("Results");
                writer.WriteLine($"Simulation Time (ms),{_lastResult.SimulationTime}");
                writer.WriteLine($"Total Porosity,{_lastResult.TotalPorosity}");
                writer.WriteLine($"Average T2 (ms),{_lastResult.AverageT2}");
                writer.WriteLine($"Average Tortuosity,{_lastResult.AverageTortuosity}");
                writer.WriteLine();

                // Write decay curve data
                writer.WriteLine("Decay Curve Data");
                writer.WriteLine("Time (ms),Magnetization");
                for (int i = 0; i < _lastResult.TimePoints.Length; i++)
                {
                    writer.WriteLine($"{_lastResult.TimePoints[i]},{_lastResult.Magnetization[i]}");
                }
                writer.WriteLine();

                // Write T2 distribution
                writer.WriteLine("T2 Distribution");
                writer.WriteLine("T2 (ms),Amplitude");
                for (int i = 0; i < _lastResult.T2Values.Length; i++)
                {
                    writer.WriteLine($"{_lastResult.T2Values[i]},{_lastResult.T2Distribution[i]}");
                }
                writer.WriteLine();

                // Write components
                if (_lastResult.FittedComponents != null)
                {
                    writer.WriteLine("Relaxation Components");
                    writer.WriteLine("T2 (ms),Amplitude,Tortuosity");
                    foreach (var component in _lastResult.FittedComponents)
                    {
                        writer.WriteLine($"{component.RelaxationTime},{component.Amplitude},{component.Tortuosity}");
                    }
                }
            }
        }

        private void OpenCalibrationDialog(object sender, EventArgs e)
        {
            using (var dialog = new NMRCalibrationDialog(_calibration))
            {
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    _calibration = dialog.Calibration;
                }
            }
        }

        private void OpenMaterialPropertiesDialog(object sender, EventArgs e)
        {
            using (var dialog = new NMRMaterialPropertiesDialog(_mainForm, _simulation))
            {
                dialog.ShowDialog();
                LoadMaterialProperties(); // Refresh the grid
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _simulation?.Dispose();
            _plotter?.Dispose();
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();
            // Dispose plot images
            pbDecayCurve.Image?.Dispose();
            pbT2Distribution.Image?.Dispose();
            pbOverview.Image?.Dispose();

            base.OnFormClosing(e);
        }

        // Helper methods for PNM data loading
        private PoreNetworkProperties LoadBinaryPNMData(string filename)
        {
            var properties = new PoreNetworkProperties();

            try
            {
                using (FileStream fs = new FileStream(filename, FileMode.Open))
                using (BinaryReader reader = new BinaryReader(fs))
                {
                    // Check file header
                    byte headerByte = reader.ReadByte();
                    if (headerByte == 0x0B || headerByte < 32)
                    {
                        // Skip control character if present
                        fs.Position = 1;
                    }
                    else
                    {
                        // Reset position if no control character
                        fs.Position = 0;
                    }

                    // Read and verify magic string
                    string magic = new string(reader.ReadChars(11));
                    if (magic != "PORENETWORK")
                    {
                        throw new Exception($"Invalid file format: '{magic}' (expected 'PORENETWORK')");
                    }

                    // Read version
                    int version = reader.ReadInt32();
                    if (version != 1)
                    {
                        throw new Exception($"Unsupported version: {version}");
                    }

                    // Read metadata
                    int poreCount = reader.ReadInt32();
                    int throatCount = reader.ReadInt32();
                    double pixelSize = reader.ReadDouble();
                    properties.Porosity = reader.ReadDouble();

                    // Try to read tortuosity if it exists
                    properties.Tortuosity = 1.0; // Default value
                    if (fs.Position < fs.Length - 8)
                    {
                        try
                        {
                            properties.Tortuosity = reader.ReadDouble();
                        }
                        catch
                        {
                            // Use default if reading fails
                        }
                    }

                    // Read pore data to calculate averages
                    List<double> poreRadii = new List<double>();
                    List<int> connectionCounts = new List<int>();

                    for (int i = 0; i < poreCount; i++)
                    {
                        try
                        {
                            int id = reader.ReadInt32();
                            double volume = reader.ReadDouble();
                            double area = reader.ReadDouble();
                            double radius = reader.ReadDouble();
                            double x = reader.ReadDouble();
                            double y = reader.ReadDouble();
                            double z = reader.ReadDouble();
                            int connectionCount = reader.ReadInt32();

                            poreRadii.Add(radius);
                            connectionCounts.Add(connectionCount);
                        }
                        catch (Exception ex)
                        {
                            // If we can't read all pores, use what we have
                            Logger.Log($"[LoadBinaryPNMData] Error reading pore {i}: {ex.Message}");
                            break;
                        }
                    }

                    // Calculate averages
                    if (poreRadii.Count > 0)
                    {
                        properties.AveragePoreRadius = poreRadii.Average();
                    }

                    if (connectionCounts.Count > 0)
                    {
                        properties.AverageConnectivity = connectionCounts.Average();
                    }

                    // Note: We skip reading throat data as we only need summary statistics
                    // but we could read it if needed for more detailed analysis
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[LoadBinaryPNMData] Error loading binary PNM data: {ex.Message}");
                throw new Exception($"Failed to load binary PNM data: {ex.Message}", ex);
            }

            return properties;
        }

        private PoreNetworkProperties LoadCSVPNMData(string filename)
        {
            var properties = new PoreNetworkProperties();

            using (var reader = new StreamReader(filename))
            {
                string line;
                bool inNetworkStats = false;
                bool inPoreData = false;
                List<double> poreRadii = new List<double>();
                List<int> connections = new List<int>();

                while ((line = reader.ReadLine()) != null)
                {
                    if (line.StartsWith("# Network Statistics"))
                    {
                        inNetworkStats = true;
                        continue;
                    }
                    else if (line.StartsWith("# Pores"))
                    {
                        inPoreData = true;
                        continue;
                    }
                    else if (line.StartsWith("#") || string.IsNullOrWhiteSpace(line))
                    {
                        inNetworkStats = false;
                        inPoreData = false;
                        continue;
                    }

                    if (inNetworkStats)
                    {
                        var parts = line.Split(',');
                        if (parts.Length == 2)
                        {
                            string key = parts[0].Trim();
                            string value = parts[1].Trim();

                            if (key == "Porosity" && double.TryParse(value, out double porosity))
                                properties.Porosity = porosity;
                            else if (key == "Tortuosity" && double.TryParse(value, out double tortuosity))
                                properties.Tortuosity = tortuosity;
                        }
                    }
                    else if (inPoreData)
                    {
                        // Skip header line
                        if (line.Contains("ID,Volume"))
                            continue;

                        var parts = line.Split(',');
                        if (parts.Length >= 8)
                        {
                            if (double.TryParse(parts[3], out double radius))
                                poreRadii.Add(radius);
                            if (int.TryParse(parts[7], out int conn))
                                connections.Add(conn);
                        }
                    }
                }

                if (poreRadii.Count > 0)
                    properties.AveragePoreRadius = poreRadii.Average();
                if (connections.Count > 0)
                    properties.AverageConnectivity = connections.Average();
            }

            return properties;
        }
        private void ExportDecayCurveImage()
        {
            if (_lastResult == null)
            {
                MessageBox.Show("No simulation results to export.", "Export Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using (var dialog = new SaveFileDialog())
            {
                dialog.Filter = "PNG Image|*.png|JPEG Image|*.jpg";
                dialog.Title = "Export Decay Curve";
                dialog.DefaultExt = "png";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        // Generate high-quality image for export
                        var bitmap = _plotter.PlotDecayCurve(_lastResult, new Size(1200, 800),
                            chkLogScale.Checked, chkShowComponents.Checked);

                        SaveBitmapWithQuality(bitmap, dialog.FileName);
                        bitmap.Dispose();

                        MessageBox.Show("Decay curve exported successfully!", "Export Complete",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error exporting image: {ex.Message}", "Export Error",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void ExportT2DistributionImage()
        {
            if (_lastResult == null)
            {
                MessageBox.Show("No simulation results to export.", "Export Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using (var dialog = new SaveFileDialog())
            {
                dialog.Filter = "PNG Image|*.png|JPEG Image|*.jpg";
                dialog.Title = "Export T2 Distribution";
                dialog.DefaultExt = "png";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        // Generate high-quality image for export
                        var bitmap = _plotter.PlotT2Distribution(_lastResult, new Size(1200, 800),
                            chkLogScale.Checked);

                        SaveBitmapWithQuality(bitmap, dialog.FileName);
                        bitmap.Dispose();

                        MessageBox.Show("T2 distribution exported successfully!", "Export Complete",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error exporting image: {ex.Message}", "Export Error",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void SaveBitmapWithQuality(Bitmap bitmap, string filename)
        {
            string extension = Path.GetExtension(filename).ToLower();

            if (extension == ".jpg" || extension == ".jpeg")
            {
                // Save JPEG with quality using EncoderParameters
                var jpegCodec = ImageCodecInfo.GetImageDecoders()
                    .FirstOrDefault(codec => codec.FormatID == ImageFormat.Jpeg.Guid);

                if (jpegCodec != null)
                {
                    var encoderParams = new EncoderParameters(1);
                    encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, 90L);
                    bitmap.Save(filename, jpegCodec, encoderParams);
                }
                else
                {
                    // Fallback if codec not found
                    bitmap.Save(filename, ImageFormat.Jpeg);
                }
            }
            else
            {
                bitmap.Save(filename, ImageFormat.Png);
            }
        }
        private PoreNetworkProperties TryLoadPNMData(string filename)
        {
            // Try to detect and load either format
            try
            {
                // First try as binary
                return LoadBinaryPNMData(filename);
            }
            catch
            {
                try
                {
                    // Then try as CSV
                    return LoadCSVPNMData(filename);
                }
                catch
                {
                    return null;
                }
            }
        }
    }

    // Helper classes for PNM data loading
    public class PoreNetworkProperties
    {
        public double Porosity { get; set; }
        public double Tortuosity { get; set; }
        public double AveragePoreRadius { get; set; }
        public double AverageConnectivity { get; set; }
    }

    public class MaterialSelectorDialog : Form
    {
        public Material SelectedMaterial { get; private set; }

        public MaterialSelectorDialog(IEnumerable<Material> materials)
        {
            this.Text = "Select Material";
            this.Size = new Size(300, 150);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.StartPosition = FormStartPosition.CenterParent;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            var comboBox = new ComboBox
            {
                Location = new Point(20, 30),
                Width = 250,
                DropDownStyle = ComboBoxStyle.DropDownList
            };

            foreach (var material in materials)
            {
                if (material.ID != 0) // Skip exterior
                {
                    comboBox.Items.Add(material);
                }
            }

            comboBox.DisplayMember = "Name";

            if (comboBox.Items.Count > 0)
                comboBox.SelectedIndex = 0;

            var okButton = new Button
            {
                Text = "OK",
                Location = new Point(130, 70),
                Width = 75,
                DialogResult = DialogResult.OK
            };

            var cancelButton = new Button
            {
                Text = "Cancel",
                Location = new Point(210, 70),
                Width = 75,
                DialogResult = DialogResult.Cancel
            };

            okButton.Click += (s, e) =>
            {
                SelectedMaterial = comboBox.SelectedItem as Material;
                this.Close();
            };

            var label = new Label
            {
                Text = "Select material to apply PNM data:",
                Location = new Point(20, 10),
                AutoSize = true
            };

            this.Controls.Add(label);
            this.Controls.Add(comboBox);
            this.Controls.Add(okButton);
            this.Controls.Add(cancelButton);
            this.AcceptButton = okButton;
            this.CancelButton = cancelButton;
        }
    }
}