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
        private KryptonPanel controlPanel;
        private KryptonPanel plotPanel;
        private TabControl plotTabControl;
        private KryptonButton btnRun;
        private KryptonButton btnStop;
        private KryptonButton btnSaveResults;
        private KryptonButton btnCalibration;
        private KryptonButton btnMaterialProperties;
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
            LoadMaterialProperties();
        }

        private void InitializeComponent()
        {
            this.Text = "NMR Simulation";
            this.Size = new Size(1400, 900);
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.StartPosition = FormStartPosition.CenterParent;
            this.ShowInTaskbar = false;

            // Main split container
            mainSplitContainer = new SplitContainer
            {
                Dock = DockStyle.Fill,
                SplitterDistance = 300,
                BackColor = Color.Black
            };

            InitializeControlPanel();
            InitializePlotPanel();

            this.Controls.Add(mainSplitContainer);

            // Status bar
            var statusStrip = new StatusStrip();
            lblStatus = new ToolStripStatusLabel { Text = "Ready", ForeColor = Color.White, BackColor = Color.Black };
            progressBar = new ProgressBar { Width = 200, Height = 16 };

            statusStrip.Items.Add(lblStatus);
            statusStrip.Items.Add(new ToolStripSeparator());
            statusStrip.Items.Add(new ToolStripControlHost(progressBar));

            this.Controls.Add(statusStrip);
        }

        private void InitializeControlPanel()
        {
            controlPanel = new KryptonPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(10)
            };

            mainSplitContainer.Panel1.Controls.Add(controlPanel);

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                AutoSize = true
            };

            // Simulation parameters group
            var paramGroup = CreateSimulationParametersGroup();
            layout.Controls.Add(paramGroup, 0, 0);

            // GPU and threading group
            var perfGroup = CreatePerformanceGroup();
            layout.Controls.Add(perfGroup, 0, 1);

            // Control buttons
            var buttonPanel = CreateControlButtons();
            layout.Controls.Add(buttonPanel, 0, 2);

            // Material properties
            var materialGroup = CreateMaterialPropertiesGroup();
            layout.Controls.Add(materialGroup, 0, 3);

            // Results text box
            txtResults = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackColor = Color.Black,
                ForeColor = Color.White,
                Font = new Font("Consolas", 9)
            };

            layout.Controls.Add(txtResults, 0, 4);
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            controlPanel.Controls.Add(layout);
        }

        private KryptonGroupBox CreateSimulationParametersGroup()
        {
            var group = new KryptonGroupBox
            {
                Text = "Simulation Parameters",
                Dock = DockStyle.Top,
                Height = 180
            };

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                Padding = new Padding(5)
            };

            // Max time
            layout.Controls.Add(new KryptonLabel { Text = "Max Time (ms):" }, 0, 0);
            numMaxTime = new NumericUpDown
            {
                Minimum = 10,
                Maximum = 10000,
                Value = (decimal)_simulation.MaxTime,
                DecimalPlaces = 1,
                Increment = 10
            };
            numMaxTime.ValueChanged += (s, e) => _simulation.MaxTime = (double)numMaxTime.Value;
            layout.Controls.Add(numMaxTime, 1, 0);

            // Time points
            layout.Controls.Add(new KryptonLabel { Text = "Time Points:" }, 0, 1);
            numTimePoints = new NumericUpDown
            {
                Minimum = 100,
                Maximum = 10000,
                Value = _simulation.TimePoints,
                Increment = 100
            };
            numTimePoints.ValueChanged += (s, e) => _simulation.TimePoints = (int)numTimePoints.Value;
            layout.Controls.Add(numTimePoints, 1, 1);

            // T2 components
            layout.Controls.Add(new KryptonLabel { Text = "T2 Components:" }, 0, 2);
            numT2Components = new NumericUpDown
            {
                Minimum = 8,
                Maximum = 128,
                Value = _simulation.T2Components,
                Increment = 4
            };
            numT2Components.ValueChanged += (s, e) => _simulation.T2Components = (int)numT2Components.Value;
            layout.Controls.Add(numT2Components, 1, 2);

            // Min T2
            layout.Controls.Add(new KryptonLabel { Text = "Min T2 (ms):" }, 0, 3);
            numMinT2 = new NumericUpDown
            {
                Minimum = 0.01m,
                Maximum = 100,
                Value = (decimal)_simulation.MinT2,
                DecimalPlaces = 2,
                Increment = 0.1m
            };
            numMinT2.ValueChanged += (s, e) => _simulation.MinT2 = (double)numMinT2.Value;
            layout.Controls.Add(numMinT2, 1, 3);

            // Max T2
            layout.Controls.Add(new KryptonLabel { Text = "Max T2 (ms):" }, 0, 4);
            numMaxT2 = new NumericUpDown
            {
                Minimum = 100,
                Maximum = 10000,
                Value = (decimal)_simulation.MaxT2,
                DecimalPlaces = 1,
                Increment = 100
            };
            numMaxT2.ValueChanged += (s, e) => _simulation.MaxT2 = (double)numMaxT2.Value;
            layout.Controls.Add(numMaxT2, 1, 4);

            group.Panel.Controls.Add(layout);
            return group;
        }

        private KryptonGroupBox CreatePerformanceGroup()
        {
            var group = new KryptonGroupBox
            {
                Text = "Performance Settings",
                Dock = DockStyle.Top,
                Height = 80
            };

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                Padding = new Padding(5)
            };

            // GPU checkbox
            chkUseGPU = new CheckBox
            {
                Text = "Use GPU Acceleration",
                Checked = _simulation.UseGPU && NMRGPUDirectCompute.IsGPUAvailable(),
                Enabled = NMRGPUDirectCompute.IsGPUAvailable(),
                ForeColor = Color.White
            };
            chkUseGPU.CheckedChanged += (s, e) => _simulation.UseGPU = chkUseGPU.Checked;
            layout.Controls.Add(chkUseGPU, 0, 0);
            layout.SetColumnSpan(chkUseGPU, 2);

            // Threads
            layout.Controls.Add(new KryptonLabel { Text = "CPU Threads:" }, 0, 1);
            numThreads = new NumericUpDown
            {
                Minimum = 1,
                Maximum = Environment.ProcessorCount,
                Value = _simulation.MaxThreads
            };
            numThreads.ValueChanged += (s, e) => _simulation.MaxThreads = (int)numThreads.Value;
            layout.Controls.Add(numThreads, 1, 1);

            group.Panel.Controls.Add(layout);
            return group;
        }

        private Panel CreateControlButtons()
        {
            var panel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 100
            };

            var layout = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                Padding = new Padding(5)
            };

            // First row
            var row1 = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true
            };

            btnRun = new KryptonButton
            {
                Text = "Run Simulation",
                Width = 120,
                Height = 30,
                Values = { Image = CreatePlayIcon() }
            };
            btnRun.Click += async (s, e) => await RunSimulationAsync();

            btnStop = new KryptonButton
            {
                Text = "Stop",
                Width = 80,
                Height = 30,
                Enabled = false,
                Values = { Image = CreateStopIcon() }
            };
            btnStop.Click += (s, e) => StopSimulation();

            btnSaveResults = new KryptonButton
            {
                Text = "Save Results",
                Width = 120,
                Height = 30,
                Enabled = false,
                Values = { Image = CreateSaveIcon() }
            };
            btnSaveResults.Click += SaveResults;

            row1.Controls.Add(btnRun);
            row1.Controls.Add(btnStop);
            row1.Controls.Add(btnSaveResults);

            // Second row
            var row2 = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true
            };

            btnCalibration = new KryptonButton
            {
                Text = "Calibration...",
                Width = 120,
                Height = 30,
                Values = { Image = CreateCalibrationIcon() }
            };
            btnCalibration.Click += OpenCalibrationDialog;

            btnMaterialProperties = new KryptonButton
            {
                Text = "Material Properties...",
                Width = 150,
                Height = 30,
                Values = { Image = CreatePropertiesIcon() }
            };
            btnMaterialProperties.Click += OpenMaterialPropertiesDialog;

            row2.Controls.Add(btnCalibration);
            row2.Controls.Add(btnMaterialProperties);

            layout.Controls.Add(row1);
            layout.Controls.Add(row2);

            panel.Controls.Add(layout);
            return panel;
        }

        private KryptonGroupBox CreateMaterialPropertiesGroup()
        {
            var group = new KryptonGroupBox
            {
                Text = "Material Properties",
                Dock = DockStyle.Top,
                Height = 200
            };

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
                ColumnHeadersDefaultCellStyle = { BackColor = Color.DarkGray, ForeColor = Color.White }
            };

            // Columns
            dgvMaterials.Columns.Add("Material", "Material");
            dgvMaterials.Columns.Add("T2", "T2 (ms)");
            dgvMaterials.Columns.Add("Density", "H Density");
            dgvMaterials.Columns.Add("Tortuosity", "Tortuosity");
            dgvMaterials.Columns.Add("Strength", "Rel. Strength");

            dgvMaterials.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;

            // Event handlers
            dgvMaterials.CellValueChanged += DgvMaterials_CellValueChanged;
            dgvMaterials.CellDoubleClick += DgvMaterials_CellDoubleClick;

            group.Panel.Controls.Add(dgvMaterials);
            return group;
        }

        private void InitializePlotPanel()
        {
            plotPanel = new KryptonPanel
            {
                Dock = DockStyle.Fill
            };

            mainSplitContainer.Panel2.Controls.Add(plotPanel);

            plotTabControl = new TabControl
            {
                Dock = DockStyle.Fill
            };

            // Decay curve tab
            var decayTab = new TabPage("Decay Curve");
            var decayPanel = new Panel { Dock = DockStyle.Fill };

            var decayOptionsPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 30,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(5)
            };

            chkLogScale = new CheckBox
            {
                Text = "Log Scale",
                Checked = true,
                ForeColor = Color.White
            };
            chkLogScale.CheckedChanged += (s, e) => UpdatePlots();

            chkShowComponents = new CheckBox
            {
                Text = "Show Components",
                Checked = true,
                ForeColor = Color.White
            };
            chkShowComponents.CheckedChanged += (s, e) => UpdatePlots();

            decayOptionsPanel.Controls.Add(chkLogScale);
            decayOptionsPanel.Controls.Add(chkShowComponents);

            pbDecayCurve = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Black
            };

            decayPanel.Controls.Add(pbDecayCurve);
            decayPanel.Controls.Add(decayOptionsPanel);
            decayTab.Controls.Add(decayPanel);

            // T2 distribution tab
            var t2Tab = new TabPage("T2 Distribution");
            pbT2Distribution = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Black
            };
            t2Tab.Controls.Add(pbT2Distribution);

            // Overview tab
            var overviewTab = new TabPage("Overview");
            pbOverview = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Black
            };
            overviewTab.Controls.Add(pbOverview);

            plotTabControl.TabPages.Add(decayTab);
            plotTabControl.TabPages.Add(t2Tab);
            plotTabControl.TabPages.Add(overviewTab);

            plotPanel.Controls.Add(plotTabControl);
        }

        private void LoadMaterialProperties()
        {
            dgvMaterials.Rows.Clear();

            foreach (var material in _mainForm.Materials)
            {
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
        private CancellationTokenSource _cancellationTokenSource;
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
                    // Generate plots
                    var decayCurve = _plotter.PlotDecayCurve(_lastResult,
                        new Size(pbDecayCurve.Width, pbDecayCurve.Height),
                        chkLogScale.Checked, chkShowComponents.Checked);

                    var t2Distribution = _plotter.PlotT2Distribution(_lastResult,
                        new Size(pbT2Distribution.Width, pbT2Distribution.Height),
                        chkLogScale.Checked);

                    var overview = _plotter.PlotComponentsOverview(_lastResult,
                        new Size(pbOverview.Width, pbOverview.Height));

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
    }
}