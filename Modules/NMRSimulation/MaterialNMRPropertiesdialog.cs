//Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Krypton.Toolkit;

namespace CTS.Modules.Simulation.NMR
{
    /// <summary>
    /// Dialog for editing individual material NMR properties
    /// </summary>
    public partial class MaterialNMRPropertiesDialog : KryptonForm
    {
        public MaterialNMRProperties Properties { get; private set; }

        // UI Controls
        private KryptonTextBox txtMaterialName;
        private KryptonNumericUpDown numRelaxationTime;
        private KryptonNumericUpDown numDensity;
        private KryptonNumericUpDown numTortuosity;
        private KryptonNumericUpDown numRelaxationStrength;
        private KryptonNumericUpDown numPorosityEffect;
        private KryptonRichTextBox txtNotes;
        private PictureBox pbHelpImage;

        public MaterialNMRPropertiesDialog(MaterialNMRProperties properties)
        {
            try
            {
                this.Icon = CTS.Properties.Resources.favicon;
            }
            catch { }

            Properties = properties?.Copy() ?? new MaterialNMRProperties();
            InitializeComponent();
            LoadProperties();
        }

        private void InitializeComponent()
        {
            this.Text = "Edit Material NMR Properties";
            this.Size = new Size(500, 650);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.StartPosition = FormStartPosition.CenterParent;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.BackColor = Color.Black;

            var mainPanel = new KryptonPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(10)
            };
            //mainPanel.StateCommon.Back.Color1 = Color.Black;
            //mainPanel.StateCommon.Back.Color2 = Color.Black;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                Padding = new Padding(5),
                BackColor = Color.Black
            };

            // Material Name
            layout.Controls.Add(new KryptonLabel
            {
                Text = "Material Name:",
                StateCommon = { ShortText = { Color1 = Color.White } }
            }, 0, 0);
            txtMaterialName = new KryptonTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                StateCommon = {
                    Back = { Color1 = Color.FromArgb(40, 40, 40) },
                    Content = { Color1 = Color.LightGray },
                    Border = { Color1 = Color.DarkGray }
                }
            };
            layout.Controls.Add(txtMaterialName, 1, 0);

            // Relaxation Time
            layout.Controls.Add(new KryptonLabel
            {
                Text = "T2 Relaxation Time (ms):",
                StateCommon = { ShortText = { Color1 = Color.White } }
            }, 0, 1);
            numRelaxationTime = new KryptonNumericUpDown
            {
                Dock = DockStyle.Fill,
                Minimum = 0.01M,
                Maximum = 10000M,
                DecimalPlaces = 2,
                Increment = 10,
                StateCommon = {
                    Back = { Color1 = Color.Black },
                    Content = { Color1 = Color.White },
                    Border = { Color1 = Color.DarkGray }
                }
            };
            layout.Controls.Add(numRelaxationTime, 1, 1);

            // Density
            layout.Controls.Add(new KryptonLabel
            {
                Text = "Hydrogen Density:",
                StateCommon = { ShortText = { Color1 = Color.White } }
            }, 0, 2);
            numDensity = new KryptonNumericUpDown
            {
                Dock = DockStyle.Fill,
                Minimum = 0M,
                Maximum = 2M,
                DecimalPlaces = 3,
                Increment = 0.01M,
                StateCommon = {
                    Back = { Color1 = Color.Black },
                    Content = { Color1 = Color.White },
                    Border = { Color1 = Color.DarkGray }
                }
            };
            layout.Controls.Add(numDensity, 1, 2);

            // Tortuosity
            layout.Controls.Add(new KryptonLabel
            {
                Text = "Tortuosity:",
                StateCommon = { ShortText = { Color1 = Color.White } }
            }, 0, 3);
            numTortuosity = new KryptonNumericUpDown
            {
                Dock = DockStyle.Fill,
                Minimum = 1M,
                Maximum = 10M,
                DecimalPlaces = 2,
                Increment = 0.1M,
                StateCommon = {
                    Back = { Color1 = Color.Black },
                    Content = { Color1 = Color.White },
                    Border = { Color1 = Color.DarkGray }
                }
            };
            layout.Controls.Add(numTortuosity, 1, 3);

            // Relaxation Strength
            layout.Controls.Add(new KryptonLabel
            {
                Text = "Relaxation Strength:",
                StateCommon = { ShortText = { Color1 = Color.White } }
            }, 0, 4);
            numRelaxationStrength = new KryptonNumericUpDown
            {
                Dock = DockStyle.Fill,
                Minimum = 0.1M,
                Maximum = 5M,
                DecimalPlaces = 2,
                Increment = 0.1M,
                StateCommon = {
                    Back = { Color1 = Color.Black },
                    Content = { Color1 = Color.White },
                    Border = { Color1 = Color.DarkGray }
                }
            };
            layout.Controls.Add(numRelaxationStrength, 1, 4);

            // Porosity Effect
            layout.Controls.Add(new KryptonLabel
            {
                Text = "Porosity Effect:",
                StateCommon = { ShortText = { Color1 = Color.White } }
            }, 0, 5);
            numPorosityEffect = new KryptonNumericUpDown
            {
                Dock = DockStyle.Fill,
                Minimum = 0.1M,
                Maximum = 5M,
                DecimalPlaces = 2,
                Increment = 0.1M,
                StateCommon = {
                    Back = { Color1 = Color.Black },
                    Content = { Color1 = Color.White },
                    Border = { Color1 = Color.DarkGray }
                }
            };
            layout.Controls.Add(numPorosityEffect, 1, 5);

            // Help section
            var helpGroup = new KryptonGroupBox
            {
                Text = "Parameter Guidelines",
                Dock = DockStyle.Fill,
                Height = 200
            };
            helpGroup.StateCommon.Back.Color1 = Color.Black;
            helpGroup.StateCommon.Back.Color2 = Color.Black;
            helpGroup.StateCommon.Border.Color1 = Color.DarkGray;
            helpGroup.StateCommon.Border.Color2 = Color.DarkGray;
            //helpGroup.StateCommon.Header.Content.ShortText.Color1 = Color.White;

            var helpLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Color.Black
            };

            pbHelpImage = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                Image = CreateHelpDiagram(),
                BackColor = Color.Black
            };

            var helpText = new KryptonRichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                Text = GetHelpText(),
                StateCommon = {
                    Back = { Color1 = Color.Black },
                    Content = { Color1 = Color.White },
                    Border = { Color1 = Color.DarkGray }
                }
            };

            helpLayout.Controls.Add(pbHelpImage, 0, 0);
            helpLayout.Controls.Add(helpText, 1, 0);
            helpLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40F));
            helpLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60F));

            helpGroup.Panel.Controls.Add(helpLayout);
            layout.Controls.Add(helpGroup, 0, 6);
            layout.SetColumnSpan(helpGroup, 2);

            // Notes
            layout.Controls.Add(new KryptonLabel
            {
                Text = "Notes:",
                StateCommon = { ShortText = { Color1 = Color.White } }
            }, 0, 7);
            txtNotes = new KryptonRichTextBox
            {
                Dock = DockStyle.Fill,
                Height = 100,
                StateCommon = {
                    Back = { Color1 = Color.Black },
                    Content = { Color1 = Color.White },
                    Border = { Color1 = Color.DarkGray }
                }
            };
            layout.Controls.Add(txtNotes, 0, 8);
            layout.SetColumnSpan(txtNotes, 2);

            // Configure row styles
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 200));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            mainPanel.Controls.Add(layout);
            this.Controls.Add(mainPanel);

            // Buttons
            var buttonPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 50,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(5),
                BackColor = Color.Black
            };

            var btnCancel = new KryptonButton
            {
                Text = "Cancel",
                Width = 80,
                Height = 30,
                DialogResult = DialogResult.Cancel
            };

            var btnOK = new KryptonButton
            {
                Text = "OK",
                Width = 80,
                Height = 30,
                DialogResult = DialogResult.OK
            };
            btnOK.Click += BtnOK_Click;

            var btnReset = new KryptonButton
            {
                Text = "Reset to Defaults",
                Width = 120,
                Height = 30
            };
            btnReset.Click += BtnReset_Click;

            buttonPanel.Controls.Add(btnCancel);
            buttonPanel.Controls.Add(btnOK);
            buttonPanel.Controls.Add(btnReset);

            this.Controls.Add(buttonPanel);
        }

        private void LoadProperties()
        {
            txtMaterialName.Text = Properties.MaterialName ?? "";
            numRelaxationTime.Value = (decimal)Properties.RelaxationTime;
            numDensity.Value = (decimal)Properties.Density;
            numTortuosity.Value = (decimal)Properties.Tortuosity;
            numRelaxationStrength.Value = (decimal)Properties.RelaxationStrength;
            numPorosityEffect.Value = (decimal)Properties.PorosityEffect;
        }

        private void BtnOK_Click(object sender, EventArgs e)
        {
            Properties.RelaxationTime = (double)numRelaxationTime.Value;
            Properties.Density = (double)numDensity.Value;
            Properties.Tortuosity = (double)numTortuosity.Value;
            Properties.RelaxationStrength = (double)numRelaxationStrength.Value;
            Properties.PorosityEffect = (double)numPorosityEffect.Value;

            this.Close();
        }

        private void BtnReset_Click(object sender, EventArgs e)
        {
            Properties.RelaxationTime = 100.0;
            Properties.Density = 1.0;
            Properties.Tortuosity = 1.0;
            Properties.RelaxationStrength = 1.0;
            Properties.PorosityEffect = 1.0;

            LoadProperties();
        }

        private string GetHelpText()
        {
            return @"Parameter Guidelines:

T2 Relaxation Time:
• Water: 1000-3000 ms
• Oil: 500-1500 ms
• Gas: 10-100 ms
• Bound fluids: 1-50 ms

Hydrogen Density:
• 1.0 = Pure water
• 0.8-0.9 = Oil
• 0.1-0.2 = Gas
• 0.05-0.1 = Solid minerals

Tortuosity:
• 1.0 = Free fluid
• 1.5-3.0 = Porous media
• 3.0-10.0 = Complex pore networks

Relaxation Strength:
• Higher values = narrower T2 distribution
• Typical range: 0.5-2.0

Porosity Effect:
• How porosity affects relaxation
• 1.0 = No effect
• >1.0 = Enhanced relaxation in pores";
        }

        private Image CreateHelpDiagram()
        {
            var bitmap = new Bitmap(200, 200);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Black);

                // Draw pore structures
                using (var brush = new SolidBrush(Color.DarkBlue))
                {
                    // Large pore
                    g.FillEllipse(brush, 20, 20, 60, 60);

                    // Medium pore
                    g.FillEllipse(brush, 120, 20, 40, 40);

                    // Small pore
                    g.FillEllipse(brush, 140, 100, 20, 20);

                    // Connecting channels
                    using (var pen = new Pen(Color.Blue, 3))
                    {
                        g.DrawLine(pen, 80, 50, 120, 40);
                        g.DrawLine(pen, 140, 50, 150, 100);
                    }
                }

                // Add labels
                using (var font = new Font("Arial", 8))
                {
                    g.DrawString("Large Pore\nT2=1000ms", font, Brushes.White, 10, 90);
                    g.DrawString("Medium\nT2=100ms", font, Brushes.White, 110, 70);
                    g.DrawString("Small\nT2=10ms", font, Brushes.White, 130, 130);
                }

                // Draw tortuosity representation
                using (var pen = new Pen(Color.Yellow, 2))
                {
                    pen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
                    var points = new Point[]
                    {
                        new Point(50, 150),
                        new Point(80, 130),
                        new Point(100, 160),
                        new Point(120, 140),
                        new Point(150, 170)
                    };
                    g.DrawLines(pen, points);
                }

                g.DrawString("Tortuous Path", new Font("Arial", 8), Brushes.Yellow, 60, 180);
            }

            return bitmap;
        }
    }

    /// <summary>
    /// Dialog for editing all material NMR properties at once
    /// </summary>
    public partial class NMRMaterialPropertiesDialog : KryptonForm
    {
        private MainForm _mainForm;
        private NMRSimulation _simulation;

        // UI Controls
        private DataGridView dgvMaterials;
        private KryptonButton btnPreset;
        private KryptonButton btnSave;
        private KryptonButton btnLoad;
        private KryptonButton btnReset;
        private CheckBox chkAdvancedMode;
        private TabControl tabControl;
        private TabPage advancedTabPage; // Store reference to advanced tab

        public NMRMaterialPropertiesDialog(MainForm mainForm, NMRSimulation simulation)
        {
            try
            {
                this.Icon = CTS.Properties.Resources.favicon;
            }
            catch { }

            _mainForm = mainForm ?? throw new ArgumentNullException(nameof(mainForm));
            _simulation = simulation ?? throw new ArgumentNullException(nameof(simulation));

            InitializeComponent();
            LoadMaterialProperties();
        }

        private void InitializeComponent()
        {
            this.Text = "NMR Material Properties Manager";
            this.Size = new Size(800, 700);
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.StartPosition = FormStartPosition.CenterParent;
            this.BackColor = Color.Black;

            // Create tab control
            tabControl = new TabControl
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                ForeColor = Color.White
            };

            // Basic properties tab
            var basicTab = CreateBasicPropertiesTab();
            tabControl.TabPages.Add(basicTab);

            // Advanced properties tab
            advancedTabPage = CreateAdvancedPropertiesTab();
            tabControl.TabPages.Add(advancedTabPage);

            // Presets tab
            var presetsTab = CreatePresetsTab();
            tabControl.TabPages.Add(presetsTab);

            this.Controls.Add(tabControl);

            // Button panel
            var buttonPanel = CreateButtonPanel();
            this.Controls.Add(buttonPanel);
        }

        private TabPage CreateBasicPropertiesTab()
        {
            var tab = new TabPage("Basic Properties");
            tab.BackColor = Color.Black;
            var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10), BackColor = Color.Black };

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
                EnableHeadersVisualStyles = false
            };

            // Configure columns
            dgvMaterials.Columns.Add("Material", "Material");
            dgvMaterials.Columns.Add("T2", "T2 (ms)");
            dgvMaterials.Columns.Add("Density", "H Density");
            dgvMaterials.Columns.Add("Tortuosity", "Tortuosity");

            foreach (DataGridViewColumn column in dgvMaterials.Columns)
            {
                if (column.Name != "Material")
                {
                    column.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
                    column.ValueType = typeof(double);
                }
                else
                {
                    column.ReadOnly = true;
                    column.AutoSizeMode = DataGridViewAutoSizeColumnMode.DisplayedCells;
                }
            }

            // Event handlers
            dgvMaterials.CellValueChanged += DgvMaterials_CellValueChanged;
            dgvMaterials.CellDoubleClick += DgvMaterials_CellDoubleClick;

            panel.Controls.Add(dgvMaterials);
            tab.Controls.Add(panel);
            return tab;
        }

        private TabPage CreateAdvancedPropertiesTab()
        {
            var tab = new TabPage("Advanced Properties");
            tab.BackColor = Color.Black;
            var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10), BackColor = Color.Black };

            var advancedGrid = new DataGridView
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
                EnableHeadersVisualStyles = false
            };

            // Advanced columns
            advancedGrid.Columns.Add("Material", "Material");
            advancedGrid.Columns.Add("RelaxationStrength", "Relaxation Strength");
            advancedGrid.Columns.Add("PorosityEffect", "Porosity Effect");
            advancedGrid.Columns.Add("PoreConnectivity", "Pore Connectivity");
            advancedGrid.Columns.Add("SurfaceRelaxivity", "Surface Relaxivity");

            foreach (DataGridViewColumn column in advancedGrid.Columns)
            {
                if (column.Name != "Material")
                {
                    column.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
                    column.ValueType = typeof(double);
                }
                else
                {
                    column.ReadOnly = true;
                    column.AutoSizeMode = DataGridViewAutoSizeColumnMode.DisplayedCells;
                }
            }

            panel.Controls.Add(advancedGrid);
            tab.Controls.Add(panel);
            return tab;
        }

        private TabPage CreatePresetsTab()
        {
            var tab = new TabPage("Material Presets");
            tab.BackColor = Color.Black;
            var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10), BackColor = Color.Black };

            var presetsFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                AutoScroll = true,
                BackColor = Color.Black
            };

            // Create preset buttons
            var presets = new[]
            {
                ("Water", "Pure water properties", new MaterialNMRProperties
                {
                    RelaxationTime = 2000,
                    Density = 1.0,
                    Tortuosity = 1.0,
                    RelaxationStrength = 1.0,
                    PorosityEffect = 1.0
                }),
                ("Oil", "Typical oil properties", new MaterialNMRProperties
                {
                    RelaxationTime = 800,
                    Density = 0.85,
                    Tortuosity = 1.2,
                    RelaxationStrength = 0.8,
                    PorosityEffect = 1.1
                }),
                ("Gas", "Natural gas properties", new MaterialNMRProperties
                {
                    RelaxationTime = 50,
                    Density = 0.15,
                    Tortuosity = 1.0,
                    RelaxationStrength = 1.5,
                    PorosityEffect = 0.8
                }),
                ("Sandstone Pores", "Sandstone pore fluid", new MaterialNMRProperties
                {
                    RelaxationTime = 300,
                    Density = 0.7,
                    Tortuosity = 2.0,
                    RelaxationStrength = 1.2,
                    PorosityEffect = 1.3
                }),
                ("Carbonate Pores", "Carbonate pore fluid", new MaterialNMRProperties
                {
                    RelaxationTime = 150,
                    Density = 0.6,
                    Tortuosity = 2.5,
                    RelaxationStrength = 1.5,
                    PorosityEffect = 1.4
                }),
                ("Clay Bound Water", "Water in clay minerals", new MaterialNMRProperties
                {
                    RelaxationTime = 20,
                    Density = 0.3,
                    Tortuosity = 3.0,
                    RelaxationStrength = 2.0,
                    PorosityEffect = 1.5
                })
            };

            foreach (var (name, description, properties) in presets)
            {
                var presetPanel = CreatePresetPanel(name, description, properties);
                presetsFlow.Controls.Add(presetPanel);
            }

            panel.Controls.Add(presetsFlow);
            tab.Controls.Add(panel);
            return tab;
        }

        private Panel CreatePresetPanel(string name, string description, MaterialNMRProperties properties)
        {
            var panel = new Panel
            {
                Width = 700,
                Height = 80,
                Margin = new Padding(5),
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(20, 20, 20)
            };

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 2,
                Padding = new Padding(5),
                BackColor = Color.FromArgb(20, 20, 20)
            };

            // Name and description
            var lblName = new Label
            {
                Text = name,
                Font = new Font("Arial", 11, FontStyle.Bold),
                ForeColor = Color.White,
                AutoSize = true
            };
            layout.Controls.Add(lblName, 0, 0);

            var lblDescription = new Label
            {
                Text = description,
                ForeColor = Color.LightGray,
                AutoSize = true
            };
            layout.Controls.Add(lblDescription, 0, 1);
            layout.SetColumnSpan(lblDescription, 2);

            // Properties summary
            var lblProperties = new Label
            {
                Text = $"T2={properties.RelaxationTime}ms, ρ={properties.Density}, τ={properties.Tortuosity}",
                ForeColor = Color.LightCyan,
                AutoSize = true
            };
            layout.Controls.Add(lblProperties, 1, 0);

            // Apply button
            var btnApply = new KryptonButton
            {
                Text = "Apply to Selected",
                Width = 120,
                Height = 25
            };
            btnApply.Click += (s, e) => ApplyPresetToSelected(properties);
            layout.Controls.Add(btnApply, 2, 0);
            layout.SetRowSpan(btnApply, 2);

            panel.Controls.Add(layout);
            return panel;
        }

        private Panel CreateButtonPanel()
        {
            var panel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 80,
                Padding = new Padding(10),
                BackColor = Color.Black
            };

            // Advanced mode checkbox - positioned at top of panel
            chkAdvancedMode = new CheckBox
            {
                Text = "Show Advanced Properties",
                ForeColor = Color.White,
                AutoSize = true,
                BackColor = Color.Black,
                Location = new Point(10, 5),
                Checked = true // Default to checked
            };
            chkAdvancedMode.CheckedChanged += ChkAdvancedMode_CheckedChanged;

            // Button layout panel - positioned below checkbox
            var buttonLayout = new FlowLayoutPanel
            {
                Location = new Point(0, 30),
                Size = new Size(panel.Width, 40),
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(10, 0, 10, 0),
                BackColor = Color.Black
            };

            // OK/Cancel buttons
            var btnClose = new KryptonButton
            {
                Text = "Close",
                Width = 80,
                Height = 30,
                DialogResult = DialogResult.OK
            };
            btnClose.Click += (s, e) => this.Close();

            // Save/Load buttons
            btnSave = new KryptonButton
            {
                Text = "Save Properties...",
                Width = 120,
                Height = 30
            };
            btnSave.Click += BtnSave_Click;

            btnLoad = new KryptonButton
            {
                Text = "Load Properties...",
                Width = 120,
                Height = 30
            };
            btnLoad.Click += BtnLoad_Click;

            // Reset button
            btnReset = new KryptonButton
            {
                Text = "Reset to Defaults",
                Width = 120,
                Height = 30
            };
            btnReset.Click += BtnReset_Click;

            buttonLayout.Controls.Add(btnClose);
            buttonLayout.Controls.Add(btnSave);
            buttonLayout.Controls.Add(btnLoad);
            buttonLayout.Controls.Add(btnReset);

            panel.Controls.Add(chkAdvancedMode);
            panel.Controls.Add(buttonLayout);

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
                row.Tag = material.ID;

                dgvMaterials.Rows.Add(row);
            }

            // Also update advanced tab if it exists in the tab control
            if (tabControl.TabPages.Contains(advancedTabPage))
            {
                var advancedGrid = (DataGridView)advancedTabPage.Controls[0].Controls[0];
                advancedGrid.Rows.Clear();

                foreach (var material in _mainForm.Materials)
                {
                    // Skip exterior material (ID 0)
                    if (material.ID == 0)
                        continue;

                    var properties = _simulation.GetMaterialProperties(material.ID);
                    var row = new DataGridViewRow();
                    row.CreateCells(advancedGrid);

                    row.Cells[0].Value = material.Name;
                    row.Cells[1].Value = properties.RelaxationStrength;
                    row.Cells[2].Value = properties.PorosityEffect;
                    row.Cells[3].Value = 1.0; // Placeholder for pore connectivity
                    row.Cells[4].Value = 0.1; // Placeholder for surface relaxivity
                    row.Tag = material.ID;

                    advancedGrid.Rows.Add(row);
                }
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

        private void ApplyPresetToSelected(MaterialNMRProperties preset)
        {
            if (dgvMaterials.SelectedRows.Count == 0)
            {
                MessageBox.Show("Please select a material to apply the preset to.", "No Selection",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var selectedRow = dgvMaterials.SelectedRows[0];
            byte materialID = (byte)selectedRow.Tag;

            var properties = _simulation.GetMaterialProperties(materialID);

            // Apply preset values
            properties.RelaxationTime = preset.RelaxationTime;
            properties.Density = preset.Density;
            properties.Tortuosity = preset.Tortuosity;
            properties.RelaxationStrength = preset.RelaxationStrength;
            properties.PorosityEffect = preset.PorosityEffect;

            _simulation.SetMaterialProperties(materialID, properties);
            LoadMaterialProperties();
        }

        private void BtnSave_Click(object sender, EventArgs e)
        {
            using (var dialog = new SaveFileDialog())
            {
                dialog.Title = "Save Material Properties";
                dialog.Filter = "JSON Properties|*.json|All Files|*.*";
                dialog.DefaultExt = "json";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        var properties = new Dictionary<string, MaterialNMRProperties>();

                        foreach (var material in _mainForm.Materials)
                        {
                            // Skip exterior material (ID 0)
                            if (material.ID == 0)
                                continue;

                            properties[material.Name] = _simulation.GetMaterialProperties(material.ID);
                        }

                        var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
                        string json = System.Text.Json.JsonSerializer.Serialize(properties, options);
                        System.IO.File.WriteAllText(dialog.FileName, json);

                        MessageBox.Show("Properties saved successfully!", "Success",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error saving properties: {ex.Message}", "Error",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void BtnLoad_Click(object sender, EventArgs e)
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "Load Material Properties";
                dialog.Filter = "JSON Properties|*.json|All Files|*.*";
                dialog.DefaultExt = "json";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        string json = System.IO.File.ReadAllText(dialog.FileName);
                        var properties = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, MaterialNMRProperties>>(json);

                        foreach (var material in _mainForm.Materials)
                        {
                            // Skip exterior material (ID 0)
                            if (material.ID == 0)
                                continue;

                            if (properties.ContainsKey(material.Name))
                            {
                                _simulation.SetMaterialProperties(material.ID, properties[material.Name]);
                            }
                        }

                        LoadMaterialProperties();
                        MessageBox.Show("Properties loaded successfully!", "Success",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error loading properties: {ex.Message}", "Error",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void BtnReset_Click(object sender, EventArgs e)
        {
            var result = MessageBox.Show("Are you sure you want to reset all materials to default properties?",
                "Confirm Reset", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (result == DialogResult.Yes)
            {
                foreach (var material in _mainForm.Materials)
                {
                    // Skip exterior material (ID 0)
                    if (material.ID == 0)
                        continue;

                    var defaultProps = new MaterialNMRProperties
                    {
                        MaterialName = material.Name,
                        RelaxationTime = 100.0,
                        Density = 1.0,
                        Tortuosity = 1.0,
                        RelaxationStrength = 1.0,
                        PorosityEffect = 1.0
                    };

                    _simulation.SetMaterialProperties(material.ID, defaultProps);
                }

                LoadMaterialProperties();
            }
        }

        private void ChkAdvancedMode_CheckedChanged(object sender, EventArgs e)
        {
            // Actually show/hide the advanced tab
            if (chkAdvancedMode.Checked)
            {
                // Show advanced tab if not already present
                if (!tabControl.TabPages.Contains(advancedTabPage))
                {
                    tabControl.TabPages.Insert(1, advancedTabPage);
                }
            }
            else
            {
                // Hide the advanced tab
                if (tabControl.TabPages.Contains(advancedTabPage))
                {
                    // If currently on advanced tab, switch to first tab
                    if (tabControl.SelectedTab == advancedTabPage)
                    {
                        tabControl.SelectedIndex = 0;
                    }
                    tabControl.TabPages.Remove(advancedTabPage);
                }
            }
        }
    }
}