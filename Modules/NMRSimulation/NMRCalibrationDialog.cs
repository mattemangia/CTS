using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Krypton.Toolkit;

namespace CTS.Modules.Simulation.NMR
{
    public partial class NMRCalibrationDialog : KryptonForm
    {
        // Dark theme colors
        private readonly Color DarkBackground = Color.FromArgb(20, 20, 20);
        private readonly Color DarkPanel = Color.FromArgb(30, 30, 30);
        private readonly Color DarkControl = Color.FromArgb(40, 40, 40);
        private readonly Color DarkBorder = Color.FromArgb(60, 60, 60);
        private readonly Color LightText = Color.FromArgb(240, 240, 240);
        private readonly Color DimText = Color.FromArgb(180, 180, 180);
        private readonly Color AccentColor = Color.FromArgb(0, 120, 215);

        public NMRCalibration Calibration { get; private set; }

        // UI Controls
        private DataGridView dgvCalibrationPoints;
        private KryptonButton btnAddPoint;
        private KryptonButton btnRemovePoint;
        private KryptonButton btnEditPoint;
        private KryptonButton btnClear;
        private KryptonButton btnLoad;
        private KryptonButton btnSave;
        private KryptonButton btnImport;
        private KryptonTextBox txtName;
        private KryptonTextBox txtDescription;
        private KryptonTextBox txtAuthor;
        private KryptonTextBox txtLaboratory;
        private KryptonTextBox txtInstrument;
        private KryptonNumericUpDown numTemperature;
        private KryptonNumericUpDown numPressure;
        private KryptonNumericUpDown numFieldStrength;
        private KryptonRichTextBox txtSampleDescription;
        private Label lblT2R2;
        private Label lblAmplitudeR2;
        private Label lblT2RMSE;
        private Label lblAmplitudeRMSE;
        private CheckBox chkApplyCalibration;
        private PictureBox pbCalibrationPlot;

        public NMRCalibrationDialog(NMRCalibration calibration)
        {
            try
            {
                this.Icon = Properties.Resources.favicon;
            }
            catch { }

            Calibration = calibration ?? new NMRCalibration();

            InitializeComponent();
            ApplyDarkTheme();
            LoadCalibrationData();
            UpdatePlot();
        }

        private void InitializeComponent()
        {
            this.Text = "NMR Calibration Manager";
            this.Size = new Size(900, 700);
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.StartPosition = FormStartPosition.CenterParent;
            this.MaximizeBox = true;
            this.MinimizeBox = true;

            // Create tab control
            var tabControl = new TabControl
            {
                Dock = DockStyle.Fill,
                DrawMode = TabDrawMode.OwnerDrawFixed,
                ItemSize = new Size(120, 40),
                SizeMode = TabSizeMode.Fixed,
                Padding = new Point(20, 5)
            };
            tabControl.DrawItem += TabControl_DrawItem;

            // Calibration Points tab
            var pointsTab = CreateCalibrationPointsTab();
            tabControl.TabPages.Add(pointsTab);

            // Metadata tab
            var metadataTab = CreateMetadataTab();
            tabControl.TabPages.Add(metadataTab);

            // Visualization tab
            var visualTab = CreateVisualizationTab();
            tabControl.TabPages.Add(visualTab);

            this.Controls.Add(tabControl);

            // Bottom panel with buttons
            var bottomPanel = CreateBottomPanel();
            this.Controls.Add(bottomPanel);
        }

        private void ApplyDarkTheme()
        {
            this.BackColor = DarkBackground;
            this.StateCommon.Back.Color1 = DarkBackground;
            this.StateCommon.Back.Color2 = DarkBackground;
            this.StateCommon.Border.Color1 = DarkBorder;
            this.StateCommon.Border.Color2 = DarkBorder;
            this.StateCommon.Header.Back.Color1 = DarkPanel;
            this.StateCommon.Header.Back.Color2 = DarkPanel;
            this.StateCommon.Header.Content.ShortText.Color1 = LightText;
            this.StateCommon.Header.Content.ShortText.Color2 = LightText;
        }

        private void ApplyDarkThemeToControl(Control control)
        {
            if (control is KryptonButton button)
            {
                button.StateCommon.Back.Color1 = DarkControl;
                button.StateCommon.Back.Color2 = DarkControl;
                button.StateCommon.Border.Color1 = DarkBorder;
                button.StateCommon.Border.Color2 = DarkBorder;
                button.StateCommon.Content.ShortText.Color1 = LightText;
                button.StateCommon.Content.ShortText.Color2 = LightText;
                button.StateTracking.Back.Color1 = Color.FromArgb(50, 50, 50);
                button.StateTracking.Back.Color2 = Color.FromArgb(60, 60, 60);
            }
            else if (control is KryptonTextBox textBox)
            {
                textBox.StateCommon.Back.Color1 = DarkControl;
                textBox.StateCommon.Border.Color1 = DarkBorder;
                textBox.StateCommon.Content.Color1 = LightText;
                textBox.StateCommon.Content.Font = new Font("Segoe UI", 9);
            }
            else if (control is KryptonNumericUpDown numeric)
            {
                numeric.StateCommon.Back.Color1 = DarkControl;
                numeric.StateCommon.Border.Color1 = DarkBorder;
                numeric.StateCommon.Content.Color1 = LightText;
                numeric.StateCommon.Content.Font = new Font("Segoe UI", 9);
            }
            else if (control is KryptonRichTextBox richTextBox)
            {
                richTextBox.StateCommon.Back.Color1 = DarkControl;
                richTextBox.StateCommon.Border.Color1 = DarkBorder;
                richTextBox.StateCommon.Content.Color1 = LightText;
                richTextBox.StateCommon.Content.Font = new Font("Segoe UI", 9);
            }
            else if (control is KryptonLabel label)
            {
                label.StateCommon.ShortText.Color1 = LightText;
                label.StateCommon.ShortText.Color2 = LightText;
                label.StateCommon.ShortText.Font = new Font("Segoe UI", 9);
            }
            else if (control is Label standardLabel)
            {
                standardLabel.ForeColor = LightText;
                standardLabel.BackColor = Color.Transparent;
                standardLabel.Font = new Font("Segoe UI", 9);
            }
            else if (control is CheckBox checkBox)
            {
                checkBox.ForeColor = LightText;
                checkBox.BackColor = Color.Transparent;
                checkBox.Font = new Font("Segoe UI", 9);
            }
            else if (control is Panel panel)
            {
                panel.BackColor = DarkPanel;
            }
            else if (control is FlowLayoutPanel flowPanel)
            {
                flowPanel.BackColor = DarkPanel;
            }
            else if (control is TabPage tabPage)
            {
                tabPage.BackColor = DarkPanel;
            }
            else if (control is PictureBox pictureBox)
            {
                pictureBox.BackColor = DarkBackground;
            }

            // Recursively apply to all children
            foreach (Control child in control.Controls)
            {
                ApplyDarkThemeToControl(child);
            }
        }

        private TabPage CreateCalibrationPointsTab()
        {
            var tab = new TabPage("Calibration Points");
            var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };

            // Data grid view with full dark theme
            dgvCalibrationPoints = new DataGridView
            {
                Dock = DockStyle.Fill,
                AutoGenerateColumns = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                BackgroundColor = DarkBackground,
                BorderStyle = BorderStyle.None,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                EnableHeadersVisualStyles = false,
                RowHeadersVisible = false,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                ColumnHeadersHeight = 35,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    BackColor = DarkPanel,
                    ForeColor = LightText,
                    SelectionBackColor = AccentColor,
                    SelectionForeColor = Color.White,
                    Font = new Font("Segoe UI", 9)
                },
                AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
                {
                    BackColor = DarkControl,
                    ForeColor = LightText,
                    SelectionBackColor = AccentColor,
                    SelectionForeColor = Color.White
                },
                ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
                {
                    BackColor = DarkControl,
                    ForeColor = LightText,
                    Font = new Font("Segoe UI", 9, FontStyle.Bold),
                    SelectionBackColor = DarkControl,
                    SelectionForeColor = LightText,
                    Alignment = DataGridViewContentAlignment.MiddleLeft,
                    Padding = new Padding(5, 0, 0, 0)
                },
                GridColor = DarkBorder
            };

            // Columns
            dgvCalibrationPoints.Columns.Add("SimT2", "Simulated T2 (ms)");
            dgvCalibrationPoints.Columns.Add("SimAmplitude", "Simulated Amplitude");
            dgvCalibrationPoints.Columns.Add("RefT2", "Reference T2 (ms)");
            dgvCalibrationPoints.Columns.Add("RefAmplitude", "Reference Amplitude");
            dgvCalibrationPoints.Columns.Add("Description", "Description");
            dgvCalibrationPoints.Columns.Add("Created", "Created");

            foreach (DataGridViewColumn column in dgvCalibrationPoints.Columns)
            {
                column.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            }

            // Button panel
            var buttonPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 50,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(5),
                BackColor = DarkPanel
            };

            btnAddPoint = new KryptonButton
            {
                Text = "Add Point",
                Width = 120,
                Height = 35,
                Values = { Image = CreateAddIcon() }
            };
            btnAddPoint.Click += BtnAddPoint_Click;

            btnEditPoint = new KryptonButton
            {
                Text = "Edit Point",
                Width = 120,
                Height = 35,
                Values = { Image = CreateEditIcon() }
            };
            btnEditPoint.Click += BtnEditPoint_Click;

            btnRemovePoint = new KryptonButton
            {
                Text = "Remove Point",
                Width = 120,
                Height = 35,
                Values = { Image = CreateRemoveIcon() }
            };
            btnRemovePoint.Click += BtnRemovePoint_Click;

            btnClear = new KryptonButton
            {
                Text = "Clear All",
                Width = 120,
                Height = 35,
                Values = { Image = CreateClearIcon() }
            };
            btnClear.Click += BtnClear_Click;

            buttonPanel.Controls.Add(btnAddPoint);
            buttonPanel.Controls.Add(btnEditPoint);
            buttonPanel.Controls.Add(btnRemovePoint);
            buttonPanel.Controls.Add(btnClear);

            // Apply dark theme to buttons
            foreach (Control control in buttonPanel.Controls)
            {
                ApplyDarkThemeToControl(control);
            }

            // Statistics panel
            var statsPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 80,
                Padding = new Padding(5),
                BackColor = DarkControl
            };

            var statsLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 4,
                RowCount = 2,
                BackColor = Color.Transparent
            };

            // Add statistic labels
            var t2Label = new Label { Text = "T2 Calibration R²:", AutoSize = true };
            statsLayout.Controls.Add(t2Label, 0, 0);
            lblT2R2 = new Label { Text = "0.0000", AutoSize = true, ForeColor = Color.LightGreen };
            statsLayout.Controls.Add(lblT2R2, 1, 0);

            var t2RmseLabel = new Label { Text = "T2 RMSE:", AutoSize = true };
            statsLayout.Controls.Add(t2RmseLabel, 2, 0);
            lblT2RMSE = new Label { Text = "0.0000", AutoSize = true, ForeColor = Color.LightGreen };
            statsLayout.Controls.Add(lblT2RMSE, 3, 0);

            var ampLabel = new Label { Text = "Amplitude Calibration R²:", AutoSize = true };
            statsLayout.Controls.Add(ampLabel, 0, 1);
            lblAmplitudeR2 = new Label { Text = "0.0000", AutoSize = true, ForeColor = Color.LightGreen };
            statsLayout.Controls.Add(lblAmplitudeR2, 1, 1);

            var ampRmseLabel = new Label { Text = "Amplitude RMSE:", AutoSize = true };
            statsLayout.Controls.Add(ampRmseLabel, 2, 1);
            lblAmplitudeRMSE = new Label { Text = "0.0000", AutoSize = true, ForeColor = Color.LightGreen };
            statsLayout.Controls.Add(lblAmplitudeRMSE, 3, 1);

            // Apply dark theme to all stat labels
            foreach (Control control in statsLayout.Controls)
            {
                if (control is Label label && !label.Equals(lblT2R2) && !label.Equals(lblAmplitudeR2)
                    && !label.Equals(lblT2RMSE) && !label.Equals(lblAmplitudeRMSE))
                {
                    label.ForeColor = LightText;
                }
            }

            statsPanel.Controls.Add(statsLayout);

            panel.Controls.Add(dgvCalibrationPoints);
            panel.Controls.Add(buttonPanel);
            panel.Controls.Add(statsPanel);

            tab.Controls.Add(panel);
            return tab;
        }

        private TabPage CreateMetadataTab()
        {
            var tab = new TabPage("Metadata");
            var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10), BackColor = DarkPanel };

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 9,
                Padding = new Padding(5),
                BackColor = Color.Transparent
            };

            // Set column widths
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            // Set row heights
            for (int i = 0; i < 8; i++)
            {
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            }
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            // Name
            layout.Controls.Add(new KryptonLabel { Text = "Name:" }, 0, 0);
            txtName = new KryptonTextBox { Dock = DockStyle.Fill };
            layout.Controls.Add(txtName, 1, 0);

            // Description
            layout.Controls.Add(new KryptonLabel { Text = "Description:" }, 0, 1);
            txtDescription = new KryptonTextBox { Dock = DockStyle.Fill, Multiline = true };
            layout.Controls.Add(txtDescription, 1, 1);

            // Author
            layout.Controls.Add(new KryptonLabel { Text = "Author:" }, 0, 2);
            txtAuthor = new KryptonTextBox { Dock = DockStyle.Fill };
            layout.Controls.Add(txtAuthor, 1, 2);

            // Laboratory
            layout.Controls.Add(new KryptonLabel { Text = "Laboratory:" }, 0, 3);
            txtLaboratory = new KryptonTextBox { Dock = DockStyle.Fill };
            layout.Controls.Add(txtLaboratory, 1, 3);

            // Instrument
            layout.Controls.Add(new KryptonLabel { Text = "Instrument:" }, 0, 4);
            txtInstrument = new KryptonTextBox { Dock = DockStyle.Fill };
            layout.Controls.Add(txtInstrument, 1, 4);

            // Temperature
            layout.Controls.Add(new KryptonLabel { Text = "Temperature (°C):" }, 0, 5);
            numTemperature = new KryptonNumericUpDown { Dock = DockStyle.Fill, DecimalPlaces = 1, Value = 25.0M };
            layout.Controls.Add(numTemperature, 1, 5);

            // Pressure
            layout.Controls.Add(new KryptonLabel { Text = "Pressure (MPa):" }, 0, 6);
            numPressure = new KryptonNumericUpDown { Dock = DockStyle.Fill, DecimalPlaces = 3, Value = 0.101M };
            layout.Controls.Add(numPressure, 1, 6);

            // Field Strength
            layout.Controls.Add(new KryptonLabel { Text = "Field Strength (T):" }, 0, 7);
            numFieldStrength = new KryptonNumericUpDown { Dock = DockStyle.Fill, DecimalPlaces = 2, Value = 1.0M };
            layout.Controls.Add(numFieldStrength, 1, 7);

            // Sample Description
            layout.Controls.Add(new KryptonLabel { Text = "Sample Description:" }, 0, 8);
            txtSampleDescription = new KryptonRichTextBox { Dock = DockStyle.Fill };
            layout.Controls.Add(txtSampleDescription, 1, 8);

            // Apply dark theme to all controls
            foreach (Control control in layout.Controls)
            {
                ApplyDarkThemeToControl(control);
            }

            panel.Controls.Add(layout);
            tab.Controls.Add(panel);
            return tab;
        }

        private TabPage CreateVisualizationTab()
        {
            var tab = new TabPage("Visualization");
            var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10), BackColor = DarkPanel };

            pbCalibrationPlot = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = DarkBackground
            };

            panel.Controls.Add(pbCalibrationPlot);
            tab.Controls.Add(panel);
            return tab;
        }

        private Panel CreateBottomPanel()
        {
            var panel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 60,
                Padding = new Padding(10),
                BackColor = DarkPanel
            };

            var buttonLayout = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                Width = 600,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(5),
                BackColor = Color.Transparent
            };

            // OK/Cancel buttons
            var btnCancel = new KryptonButton
            {
                Text = "Cancel",
                Width = 100,
                Height = 25,
                DialogResult = DialogResult.Cancel
            };

            var btnOK = new KryptonButton
            {
                Text = "OK",
                Width = 100,
                Height = 25,
                DialogResult = DialogResult.OK,
                Values = { Image = CreateOkIcon() }
            };
            btnOK.Click += BtnOK_Click;

            // Import/Export buttons
            btnSave = new KryptonButton
            {
                Text = "Save...",
                Width = 100,
                Height = 25,
                Values = { Image = CreateSaveIcon() }
            };
            btnSave.Click += BtnSave_Click;

            btnLoad = new KryptonButton
            {
                Text = "Load...",
                Width = 100,
                Height = 25,
                Values = { Image = CreateLoadIcon() }
            };
            btnLoad.Click += BtnLoad_Click;

            btnImport = new KryptonButton
            {
                Text = "Import...",
                Width = 100,
                Height = 25,
                Values = { Image = CreateImportIcon() }
            };
            btnImport.Click += BtnImport_Click;

            // Apply calibration checkbox
            chkApplyCalibration = new CheckBox
            {
                Text = "Apply this calibration",
                Checked = true,
                AutoSize = true,
                ForeColor = LightText,
                Dock = DockStyle.Left,
                Font = new Font("Segoe UI", 9)
            };

            buttonLayout.Controls.Add(btnCancel);
            buttonLayout.Controls.Add(btnOK);
            buttonLayout.Controls.Add(btnSave);
            buttonLayout.Controls.Add(btnLoad);
            buttonLayout.Controls.Add(btnImport);

            // Apply dark theme to all buttons
            foreach (Control control in buttonLayout.Controls)
            {
                ApplyDarkThemeToControl(control);
            }

            panel.Controls.Add(buttonLayout);
            panel.Controls.Add(chkApplyCalibration);

            return panel;
        }

        private void TabControl_DrawItem(object sender, DrawItemEventArgs e)
        {
            TabControl tabControl = sender as TabControl;
            if (tabControl == null) return;

            Graphics g = e.Graphics;
            Rectangle tabBounds = tabControl.GetTabRect(e.Index);

            // Fill background
            using (Brush backBrush = new SolidBrush(e.State == DrawItemState.Selected ? DarkControl : DarkPanel))
            {
                g.FillRectangle(backBrush, tabBounds);
            }

            // Draw text
            string tabText = tabControl.TabPages[e.Index].Text;
            using (Brush textBrush = new SolidBrush(LightText))
            {
                var sf = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                };
                g.DrawString(tabText, new Font("Segoe UI", 9, FontStyle.Regular), textBrush, tabBounds, sf);
            }
        }

        private void LoadCalibrationData()
        {
            // Load metadata
            txtName.Text = Calibration.Metadata.Name ?? "";
            txtDescription.Text = Calibration.Metadata.Description ?? "";
            txtAuthor.Text = Calibration.Metadata.Author ?? "";
            txtLaboratory.Text = Calibration.Metadata.Laboratory ?? "";
            txtInstrument.Text = Calibration.Metadata.InstrumentType ?? "";
            numTemperature.Value = (decimal)Calibration.Metadata.TemperatureC;
            numPressure.Value = (decimal)Calibration.Metadata.PressureMPa;
            numFieldStrength.Value = (decimal)Calibration.Metadata.FieldStrengthT;
            txtSampleDescription.Text = Calibration.Metadata.SampleDescription ?? "";

            // Load calibration points
            dgvCalibrationPoints.Rows.Clear();
            foreach (var point in Calibration.CalibrationPoints)
            {
                var row = new DataGridViewRow();
                row.CreateCells(dgvCalibrationPoints);

                row.Cells[0].Value = point.SimulatedT2;
                row.Cells[1].Value = point.SimulatedAmplitude;
                row.Cells[2].Value = point.ReferenceT2;
                row.Cells[3].Value = point.ReferenceAmplitude;
                row.Cells[4].Value = point.Description;
                row.Cells[5].Value = point.CreatedDate.ToString("yyyy-MM-dd HH:mm");
                row.Tag = point;

                dgvCalibrationPoints.Rows.Add(row);
            }

            // Update statistics
            UpdateStatistics();
        }

        private void UpdateStatistics()
        {
            lblT2R2.Text = $"{Calibration.T2CalibrationR2:F4}";
            lblAmplitudeR2.Text = $"{Calibration.AmplitudeCalibrationR2:F4}";
            lblT2RMSE.Text = $"{Calibration.T2RMSE:F4}";
            lblAmplitudeRMSE.Text = $"{Calibration.AmplitudeRMSE:F4}";

            // Color code based on quality
            lblT2R2.ForeColor = GetQualityColor(Calibration.T2CalibrationR2);
            lblAmplitudeR2.ForeColor = GetQualityColor(Calibration.AmplitudeCalibrationR2);
        }

        private Color GetQualityColor(double r2)
        {
            if (r2 >= 0.95) return Color.LightGreen;
            if (r2 >= 0.90) return Color.Yellow;
            if (r2 >= 0.80) return Color.Orange;
            return Color.Red;
        }

        private void BtnAddPoint_Click(object sender, EventArgs e)
        {
            using (var dialog = new AddCalibrationPointDialog())
            {
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    Calibration.AddCalibrationPoint(
                        dialog.SimulatedT2,
                        dialog.SimulatedAmplitude,
                        dialog.ReferenceT2,
                        dialog.ReferenceAmplitude,
                        dialog.Description);

                    LoadCalibrationData();
                    UpdatePlot();
                }
            }
        }

        private void BtnEditPoint_Click(object sender, EventArgs e)
        {
            if (dgvCalibrationPoints.SelectedRows.Count == 0)
            {
                MessageBox.Show("Please select a calibration point to edit.", "No Selection",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var selectedRow = dgvCalibrationPoints.SelectedRows[0];
            var point = (CalibrationPoint)selectedRow.Tag;

            using (var dialog = new AddCalibrationPointDialog(point))
            {
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    int index = Calibration.CalibrationPoints.IndexOf(point);
                    if (index >= 0)
                    {
                        Calibration.UpdateCalibrationPoint(index,
                            dialog.SimulatedT2,
                            dialog.SimulatedAmplitude,
                            dialog.ReferenceT2,
                            dialog.ReferenceAmplitude);

                        point.Description = dialog.Description;

                        LoadCalibrationData();
                        UpdatePlot();
                    }
                }
            }
        }

        private void BtnRemovePoint_Click(object sender, EventArgs e)
        {
            if (dgvCalibrationPoints.SelectedRows.Count == 0)
            {
                MessageBox.Show("Please select a calibration point to remove.", "No Selection",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var selectedRow = dgvCalibrationPoints.SelectedRows[0];
            var point = (CalibrationPoint)selectedRow.Tag;

            var result = MessageBox.Show("Are you sure you want to remove this calibration point?",
                "Confirm Removal", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (result == DialogResult.Yes)
            {
                Calibration.RemoveCalibrationPoint(point);
                LoadCalibrationData();
                UpdatePlot();
            }
        }

        private void BtnClear_Click(object sender, EventArgs e)
        {
            var result = MessageBox.Show("Are you sure you want to clear all calibration points?",
                "Confirm Clear", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (result == DialogResult.Yes)
            {
                Calibration.Reset();
                LoadCalibrationData();
                UpdatePlot();
            }
        }

        private void BtnLoad_Click(object sender, EventArgs e)
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "Load NMR Calibration";
                dialog.Filter = "JSON Calibration|*.json|All Files|*.*";
                dialog.DefaultExt = "json";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        Calibration = NMRCalibration.LoadFromFile(dialog.FileName);
                        LoadCalibrationData();
                        UpdatePlot();
                        MessageBox.Show("Calibration loaded successfully!", "Success",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error loading calibration: {ex.Message}", "Error",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void BtnSave_Click(object sender, EventArgs e)
        {
            SaveMetadata();

            using (var dialog = new SaveFileDialog())
            {
                dialog.Title = "Save NMR Calibration";
                dialog.Filter = "JSON Calibration|*.json|Binary Calibration|*.bin|All Files|*.*";
                dialog.DefaultExt = "json";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        if (dialog.FileName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                        {
                            Calibration.ExportToBinary(dialog.FileName);
                        }
                        else
                        {
                            Calibration.SaveToFile(dialog.FileName);
                        }

                        MessageBox.Show("Calibration saved successfully!", "Success",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error saving calibration: {ex.Message}", "Error",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void BtnImport_Click(object sender, EventArgs e)
        {
            using (var dialog = new ImportCalibrationDialog())
            {
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        Calibration.ImportFromNMRLog(dialog.FilePath, dialog.ImportFormat);
                        LoadCalibrationData();
                        UpdatePlot();
                        MessageBox.Show("Calibration imported successfully!", "Success",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error importing calibration: {ex.Message}", "Error",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void BtnOK_Click(object sender, EventArgs e)
        {
            SaveMetadata();

            if (chkApplyCalibration.Checked)
            {
                // The calibration will be used by the caller
                this.Close();
            }
            else
            {
                // Create a new empty calibration to disable calibration
                Calibration = new NMRCalibration();
                this.Close();
            }
        }

        private void SaveMetadata()
        {
            Calibration.Metadata.Name = txtName.Text;
            Calibration.Metadata.Description = txtDescription.Text;
            Calibration.Metadata.Author = txtAuthor.Text;
            Calibration.Metadata.Laboratory = txtLaboratory.Text;
            Calibration.Metadata.InstrumentType = txtInstrument.Text;
            Calibration.Metadata.TemperatureC = (double)numTemperature.Value;
            Calibration.Metadata.PressureMPa = (double)numPressure.Value;
            Calibration.Metadata.FieldStrengthT = (double)numFieldStrength.Value;
            Calibration.Metadata.SampleDescription = txtSampleDescription.Text;
            Calibration.Metadata.LastModified = DateTime.Now;
        }

        private void UpdatePlot()
        {
            if (pbCalibrationPlot.Width <= 0 || pbCalibrationPlot.Height <= 0)
                return;

            var plot = new CalibrationPlotter();
            var bitmap = plot.PlotCalibration(Calibration,
                new Size(pbCalibrationPlot.Width, pbCalibrationPlot.Height),
                DarkBackground, LightText, DarkBorder, AccentColor);

            pbCalibrationPlot.Image?.Dispose();
            pbCalibrationPlot.Image = bitmap;
        }

        #region Icon Creation Methods

        private Image CreateAddIcon()
        {
            var bitmap = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                using (var pen = new Pen(Color.Green, 2))
                {
                    g.DrawLine(pen, 8, 4, 8, 12);
                    g.DrawLine(pen, 4, 8, 12, 8);
                }
            }
            return bitmap;
        }

        private Image CreateEditIcon()
        {
            var bitmap = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                using (var pen = new Pen(Color.Blue, 1))
                {
                    g.DrawLine(pen, 2, 14, 8, 2);
                    g.DrawLine(pen, 8, 2, 14, 8);
                    g.DrawLine(pen, 14, 8, 8, 14);
                    g.DrawLine(pen, 8, 14, 2, 14);
                }
            }
            return bitmap;
        }

        private Image CreateRemoveIcon()
        {
            var bitmap = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                using (var pen = new Pen(Color.Red, 2))
                {
                    g.DrawLine(pen, 4, 4, 12, 12);
                    g.DrawLine(pen, 4, 12, 12, 4);
                }
            }
            return bitmap;
        }

        private Image CreateClearIcon()
        {
            var bitmap = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                using (var pen = new Pen(Color.Red, 1))
                {
                    g.DrawRectangle(pen, 2, 2, 12, 12);
                    g.DrawLine(pen, 5, 2, 5, 14);
                    g.DrawLine(pen, 11, 2, 11, 14);
                }
            }
            return bitmap;
        }

        private Image CreateSaveIcon()
        {
            var bitmap = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                using (var pen = new Pen(Color.Blue, 2))
                {
                    g.DrawRectangle(pen, 2, 2, 12, 12);
                    g.DrawRectangle(pen, 5, 8, 6, 6);
                    g.DrawLine(pen, 8, 2, 8, 8);
                }
            }
            return bitmap;
        }

        private Image CreateLoadIcon()
        {
            var bitmap = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                using (var pen = new Pen(Color.Green, 2))
                {
                    g.DrawRectangle(pen, 2, 2, 12, 12);
                    g.DrawLine(pen, 5, 2, 5, 8);
                    g.DrawLine(pen, 11, 2, 11, 8);
                    g.DrawLine(pen, 1, 8, 15, 8);
                }
            }
            return bitmap;
        }

        private Image CreateImportIcon()
        {
            var bitmap = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                using (var pen = new Pen(Color.Purple, 2))
                {
                    g.DrawLine(pen, 2, 8, 14, 8);
                    g.DrawLine(pen, 11, 5, 14, 8);
                    g.DrawLine(pen, 11, 11, 14, 8);
                }
            }
            return bitmap;
        }

        private Image CreateOkIcon()
        {
            var bitmap = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                using (var pen = new Pen(Color.Green, 2))
                {
                    var points = new System.Drawing.Point[]
                    {
                        new System.Drawing.Point(3, 8),
                        new System.Drawing.Point(7, 12),
                        new System.Drawing.Point(13, 4)
                    };
                    g.DrawLines(pen, points);
                }
            }
            return bitmap;
        }

        #endregion
    }

    // Helper class for plotting calibration data with dark theme support
    public class CalibrationPlotter
    {
        public Bitmap PlotCalibration(NMRCalibration calibration, Size size,
            Color backgroundColor, Color textColor, Color gridColor, Color accentColor)
        {
            var bitmap = new Bitmap(size.Width, size.Height);

            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(backgroundColor);

                if (calibration.CalibrationPoints.Count < 2)
                {
                    // Draw message
                    string message = "Add at least 2 calibration points to see the calibration curve";
                    var font = new Font("Segoe UI", 12);
                    var stringSize = g.MeasureString(message, font);
                    using (var brush = new SolidBrush(textColor))
                    {
                        g.DrawString(message, font, brush,
                            (size.Width - stringSize.Width) / 2,
                            (size.Height - stringSize.Height) / 2);
                    }
                    font.Dispose();
                    return bitmap;
                }

                // Set up margins and plot area
                var margins = new Padding(60, 40, 40, 50);
                var plotArea = new Rectangle(margins.Left, margins.Top,
                    size.Width - margins.Left - margins.Right,
                    size.Height - margins.Top - margins.Bottom);

                // Draw grid
                using (var gridPen = new Pen(Color.FromArgb(30, gridColor)))
                {
                    for (int i = 1; i < 5; i++)
                    {
                        int x = plotArea.Left + (plotArea.Width * i) / 5;
                        g.DrawLine(gridPen, x, plotArea.Top, x, plotArea.Bottom);

                        int y = plotArea.Top + (plotArea.Height * i) / 5;
                        g.DrawLine(gridPen, plotArea.Left, y, plotArea.Right, y);
                    }
                }

                // Draw axes
                using (var pen = new Pen(textColor))
                {
                    g.DrawLine(pen, plotArea.Left, plotArea.Bottom, plotArea.Right, plotArea.Bottom);
                    g.DrawLine(pen, plotArea.Left, plotArea.Bottom, plotArea.Left, plotArea.Top);
                }

                // Get data ranges
                var simData = calibration.CalibrationPoints.Select(p => p.SimulatedT2).ToArray();
                var refData = calibration.CalibrationPoints.Select(p => p.ReferenceT2).ToArray();

                double minSim = simData.Min();
                double maxSim = simData.Max();
                double minRef = refData.Min();
                double maxRef = refData.Max();

                // Plot calibration points
                using (var pointBrush = new SolidBrush(accentColor))
                {
                    foreach (var point in calibration.CalibrationPoints)
                    {
                        float x = (float)(plotArea.Left + (Math.Log10(point.SimulatedT2) - Math.Log10(minSim)) /
                                 (Math.Log10(maxSim) - Math.Log10(minSim)) * plotArea.Width);
                        float y = (float)(plotArea.Bottom - (Math.Log10(point.ReferenceT2) - Math.Log10(minRef)) /
                                 (Math.Log10(maxRef) - Math.Log10(minRef)) * plotArea.Height);

                        g.FillEllipse(pointBrush, x - 4, y - 4, 8, 8);
                    }
                }

                // Plot calibration curve
                if (calibration.IsCalibrated)
                {
                    using (var curvePen = new Pen(Color.Orange, 2))
                    {
                        var points = new List<PointF>();
                        for (int i = 0; i < 100; i++)
                        {
                            double simT2 = Math.Pow(10, Math.Log10(minSim) + i * (Math.Log10(maxSim) - Math.Log10(minSim)) / 99);
                            double refT2 = calibration.TransformT2(simT2);

                            float x = (float)(plotArea.Left + (Math.Log10(simT2) - Math.Log10(minSim)) /
                                     (Math.Log10(maxSim) - Math.Log10(minSim)) * plotArea.Width);
                            float y = (float)(plotArea.Bottom - (Math.Log10(refT2) - Math.Log10(minRef)) /
                                     (Math.Log10(maxRef) - Math.Log10(minRef)) * plotArea.Height);

                            points.Add(new PointF(x, y));
                        }

                        if (points.Count > 1)
                            g.DrawLines(curvePen, points.ToArray());
                    }
                }

                // Draw labels
                using (var font = new Font("Segoe UI", 10))
                using (var brush = new SolidBrush(textColor))
                {
                    g.DrawString("Simulated T2 (ms)", font, brush,
                        plotArea.Left + plotArea.Width / 2 - 60, plotArea.Bottom + 25);

                    var labelFormat = new StringFormat
                    {
                        Alignment = StringAlignment.Center,
                        FormatFlags = StringFormatFlags.DirectionVertical
                    };
                    g.DrawString("Reference T2 (ms)", font, brush,
                        plotArea.Left - 45, plotArea.Top + plotArea.Height / 2, labelFormat);
                }

                // Draw statistics
                string stats = $"T2 Calibration R²: {calibration.T2CalibrationR2:F4}";
                using (var font = new Font("Segoe UI", 9))
                using (var brush = new SolidBrush(textColor))
                {
                    g.DrawString(stats, font, brush, plotArea.Left + 10, plotArea.Top + 10);
                }
            }

            return bitmap;
        }
    }
}