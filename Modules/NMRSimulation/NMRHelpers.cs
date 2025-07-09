//Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
using System;
using System.Drawing;
using System.Windows.Controls;
using System.Windows.Forms;
using Krypton.Toolkit;
using Button = System.Windows.Forms.Button;
using ComboBox = System.Windows.Forms.ComboBox;
using Label = System.Windows.Forms.Label;
using Panel = System.Windows.Forms.Panel;
using ProgressBar = System.Windows.Forms.ProgressBar;
using RichTextBox = System.Windows.Forms.RichTextBox;
using TabControl = System.Windows.Forms.TabControl;

namespace CTS.Modules.Simulation.NMR
{
    /// <summary>
    /// Dialog for adding a single calibration point
    /// </summary>
    public partial class AddCalibrationPointDialog : KryptonForm
    {
        public double SimulatedT2 { get; private set; }
        public double SimulatedAmplitude { get; private set; }
        public double ReferenceT2 { get; private set; }
        public double ReferenceAmplitude { get; private set; }
        public string Description { get; private set; }

        private KryptonNumericUpDown numSimT2;
        private KryptonNumericUpDown numSimAmplitude;
        private KryptonNumericUpDown numRefT2;
        private KryptonNumericUpDown numRefAmplitude;
        private KryptonTextBox txtDescription;

        public AddCalibrationPointDialog(CalibrationPoint existing = null)
        {
            try
            {
                this.Icon = Properties.Resources.favicon;
            }
            catch { }

            InitializeComponent();

            if (existing != null)
            {
                LoadExistingPoint(existing);
            }
        }

        private void InitializeComponent()
        {
            this.Text = "Add Calibration Point";
            this.Size = new Size(400, 250);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.StartPosition = FormStartPosition.CenterParent;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            var mainPanel = new KryptonPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(10)
            };

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 6,
                Padding = new Padding(5)
            };

            // Simulated T2
            layout.Controls.Add(new KryptonLabel { Text = "Simulated T2 (ms):" }, 0, 0);
            numSimT2 = new KryptonNumericUpDown
            {
                Minimum = 0.01M,
                Maximum = 10000M,
                DecimalPlaces = 2,
                Value = 100M,
                Dock = DockStyle.Fill
            };
            layout.Controls.Add(numSimT2, 1, 0);

            // Simulated Amplitude
            layout.Controls.Add(new KryptonLabel { Text = "Simulated Amplitude:" }, 0, 1);
            numSimAmplitude = new KryptonNumericUpDown
            {
                Minimum = 0M,
                Maximum = 1M,
                DecimalPlaces = 4,
                Value = 0.5M,
                Dock = DockStyle.Fill
            };
            layout.Controls.Add(numSimAmplitude, 1, 1);

            // Reference T2
            layout.Controls.Add(new KryptonLabel { Text = "Reference T2 (ms):" }, 0, 2);
            numRefT2 = new KryptonNumericUpDown
            {
                Minimum = 0.01M,
                Maximum = 10000M,
                DecimalPlaces = 2,
                Value = 100M,
                Dock = DockStyle.Fill
            };
            layout.Controls.Add(numRefT2, 1, 2);

            // Reference Amplitude
            layout.Controls.Add(new KryptonLabel { Text = "Reference Amplitude:" }, 0, 3);
            numRefAmplitude = new KryptonNumericUpDown
            {
                Minimum = 0M,
                Maximum = 1M,
                DecimalPlaces = 4,
                Value = 0.5M,
                Dock = DockStyle.Fill
            };
            layout.Controls.Add(numRefAmplitude, 1, 3);

            // Description
            layout.Controls.Add(new KryptonLabel { Text = "Description:" }, 0, 4);
            txtDescription = new KryptonTextBox
            {
                Dock = DockStyle.Fill
            };
            layout.Controls.Add(txtDescription, 1, 4);

            // Buttons
            var buttonPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft
            };

            var btnCancel = new KryptonButton
            {
                Text = "Cancel",
                Width = 80,
                DialogResult = DialogResult.Cancel
            };

            var btnOK = new KryptonButton
            {
                Text = "OK",
                Width = 80,
                DialogResult = DialogResult.OK
            };
            btnOK.Click += BtnOK_Click;

            buttonPanel.Controls.Add(btnCancel);
            buttonPanel.Controls.Add(btnOK);

            layout.Controls.Add(buttonPanel, 0, 5);
            layout.SetColumnSpan(buttonPanel, 2);

            // Configure row styles
            for (int i = 0; i < 5; i++)
            {
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            }
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

            mainPanel.Controls.Add(layout);
            this.Controls.Add(mainPanel);
        }

        private void LoadExistingPoint(CalibrationPoint point)
        {
            numSimT2.Value = (decimal)point.SimulatedT2;
            numSimAmplitude.Value = (decimal)point.SimulatedAmplitude;
            numRefT2.Value = (decimal)point.ReferenceT2;
            numRefAmplitude.Value = (decimal)point.ReferenceAmplitude;
            txtDescription.Text = point.Description ?? "";
        }

        private void BtnOK_Click(object sender, EventArgs e)
        {
            SimulatedT2 = (double)numSimT2.Value;
            SimulatedAmplitude = (double)numSimAmplitude.Value;
            ReferenceT2 = (double)numRefT2.Value;
            ReferenceAmplitude = (double)numRefAmplitude.Value;
            Description = txtDescription.Text;

            this.Close();
        }
    }

    /// <summary>
    /// Dialog for importing calibration data from various formats
    /// </summary>
    public partial class ImportCalibrationDialog : KryptonForm
    {
        public string FilePath { get; private set; }
        public ImportFormat ImportFormat { get; private set; }

        private ComboBox comboFormat;
        private KryptonTextBox txtFilePath;
        private KryptonButton btnBrowse;

        public ImportCalibrationDialog()
        {
            try
            {
                this.Icon = Properties.Resources.favicon;
            }
            catch { }

            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.Text = "Import Calibration Data";
            this.Size = new Size(450, 150);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.StartPosition = FormStartPosition.CenterParent;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            var mainPanel = new KryptonPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(10)
            };

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 3,
                Padding = new Padding(5)
            };

            // File path
            layout.Controls.Add(new KryptonLabel { Text = "File Path:" }, 0, 0);
            txtFilePath = new KryptonTextBox
            {
                Dock = DockStyle.Fill
            };
            layout.Controls.Add(txtFilePath, 1, 0);

            btnBrowse = new KryptonButton
            {
                Text = "Browse...",
                Width = 80
            };
            btnBrowse.Click += BtnBrowse_Click;
            layout.Controls.Add(btnBrowse, 2, 0);

            // Format
            layout.Controls.Add(new KryptonLabel { Text = "Format:" }, 0, 1);
            comboFormat = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.DarkGray,
                ForeColor = Color.White
            };
            comboFormat.Items.Add("CSV Format");
            comboFormat.Items.Add("ASCII Log");
            comboFormat.Items.Add("Binary Log");
            comboFormat.SelectedIndex = 0;
            layout.Controls.Add(comboFormat, 1, 1);
            layout.SetColumnSpan(comboFormat, 2);

            // Buttons
            var buttonPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft
            };

            var btnCancel = new KryptonButton
            {
                Text = "Cancel",
                Width = 80,
                DialogResult = DialogResult.Cancel
            };

            var btnOK = new KryptonButton
            {
                Text = "Import",
                Width = 80,
                DialogResult = DialogResult.OK
            };
            btnOK.Click += BtnOK_Click;

            buttonPanel.Controls.Add(btnCancel);
            buttonPanel.Controls.Add(btnOK);

            layout.Controls.Add(buttonPanel, 0, 2);
            layout.SetColumnSpan(buttonPanel, 3);

            mainPanel.Controls.Add(layout);
            this.Controls.Add(mainPanel);
        }

        private void BtnBrowse_Click(object sender, EventArgs e)
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "Select Calibration File";

                switch (comboFormat.SelectedIndex)
                {
                    case 0: // CSV
                        dialog.Filter = "CSV Files|*.csv|All Files|*.*";
                        break;
                    case 1: // ASCII Log
                        dialog.Filter = "Log Files|*.log;*.txt|All Files|*.*";
                        break;
                    case 2: // Binary Log
                        dialog.Filter = "Binary Files|*.bin;*.dat|All Files|*.*";
                        break;
                }

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    txtFilePath.Text = dialog.FileName;
                }
            }
        }

        private void BtnOK_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(txtFilePath.Text) || !System.IO.File.Exists(txtFilePath.Text))
            {
                MessageBox.Show("Please select a valid file.", "Invalid File",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            FilePath = txtFilePath.Text;
            ImportFormat = (ImportFormat)comboFormat.SelectedIndex;

            this.Close();
        }
    }

    /// <summary>
    /// Help dialog for NMR simulation
    /// </summary>
    public partial class NMRHelpForm : KryptonForm
    {
        public NMRHelpForm()
        {
            try
            {
                this.Icon = Properties.Resources.favicon;
            }
            catch { }

            InitializeComponent();
            LoadHelpContent();
        }

        private void InitializeComponent()
        {
            this.Text = "NMR Simulation Help";
            this.Size = new Size(800, 600);
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.StartPosition = FormStartPosition.CenterParent;

            var tabControl = new TabControl
            {
                Dock = DockStyle.Fill
            };

            // Overview tab
            var overviewTab = new TabPage("Overview");
            var overviewContent = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackColor = Color.Black,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10)
            };
            overviewTab.Controls.Add(overviewContent);
            tabControl.TabPages.Add(overviewTab);

            // Parameters tab
            var paramsTab = new TabPage("Parameters");
            var paramsContent = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackColor = Color.Black,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10)
            };
            paramsTab.Controls.Add(paramsContent);
            tabControl.TabPages.Add(paramsTab);

            // Calibration tab
            var calibTab = new TabPage("Calibration");
            var calibContent = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackColor = Color.Black,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10)
            };
            calibTab.Controls.Add(calibContent);
            tabControl.TabPages.Add(calibTab);

            // GPU tab
            var gpuTab = new TabPage("GPU Acceleration");
            var gpuContent = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackColor = Color.Black,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10)
            };
            gpuTab.Controls.Add(gpuContent);
            tabControl.TabPages.Add(gpuTab);

            this.Controls.Add(tabControl);

            // Close button
            var btnClose = new KryptonButton
            {
                Text = "Close",
                Width = 100,
                Height = 30,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                Location = new Point(this.Width - 120, this.Height - 50)
            };
            btnClose.Click += (s, e) => this.Close();
            this.Controls.Add(btnClose);
        }

        private void LoadHelpContent()
        {
            var tabControl = (TabControl)this.Controls[0];

            // Overview content
            var overviewContent = (RichTextBox)tabControl.TabPages[0].Controls[0];
            overviewContent.Rtf = @"{\rtf1\ansi\deff0{\fonttbl{\f0\fswiss\fcharset0 Segoe UI;}}
\f0\fs20 {\b NMR Simulation Overview}\par\par
The NMR (Nuclear Magnetic Resonance) simulation tool simulates the decay of magnetic resonance of hydrogen-1 atoms in the fluid-filled pore space of rock samples.\par\par
{\b Key Features:}\par
\bullet Physics-based simulation of T2 relaxation\par
\bullet Multi-exponential decay curves\par
\bullet T2 distribution histograms\par
\bullet GPU acceleration for large datasets\par
\bullet Calibration against lab/field measurements\par
\bullet Material-specific relaxation properties\par\par
{\b The Simulation Equation:}\par
M(t) = \u03A3 A\dn5i\up0 exp(-t/T\dn52i\up0)\par\par
Where M(t) is magnetization at time t, A\dn5i\up0 is amplitude, and T\dn52i\up0 is relaxation time for component i.\par\par
}";
            
            // Parameters content
            var paramsContent = (RichTextBox)tabControl.TabPages[1].Controls[0];
        paramsContent.Rtf = @"{\rtf1\ansi\deff0{\fonttbl{\f0\fswiss\fcharset0 Segoe UI;}}
\f0\fs20 {\b Simulation Parameters}\par\par
{\b Material Properties:}\par
\bullet {\b T2 Relaxation Time} - Base relaxation time for the material (ms)\par
\bullet {\b Hydrogen Density} - Relative density of hydrogen atoms (0-2)\par
\bullet {\b Tortuosity} - Measure of pore complexity (≥1)\par
\bullet {\b Relaxation Strength} - Distribution width control (0.1-5)\par
\bullet {\b Porosity Effect} - How porosity affects relaxation (0.1-5)\par\par
{\b Typical Values:}\par
\bullet Water: T2=2000ms, ρ=1.0, τ=1.0\par
\bullet Oil: T2=800ms, ρ=0.85, τ=1.2\par
\bullet Gas: T2=50ms, ρ=0.15, τ=1.0\par
\bullet Clay-bound water: T2=20ms, ρ=0.3, τ=3.0\par\par
{\b Simulation Settings:}\par
\bullet Max Time - Maximum decay time to simulate\par
\bullet Time Points - Number of time samples\par
\bullet T2 Components - Number of decay components\par
\bullet Min/Max T2 - Range of relaxation times\par
}";
            
            // Calibration content
            var calibContent = (RichTextBox)tabControl.TabPages[2].Controls[0];
        calibContent.Rtf = @"{\rtf1\ansi\deff0{\fonttbl{\f0\fswiss\fcharset0 Segoe UI;}}
\f0\fs20 {\b Calibration System}\par\par
The calibration system allows you to match simulated results with laboratory or field measurements.\par\par
{\b Calibration Process:}\par
1. Run NMR simulation on your sample\par
2. Compare with lab measurements\par
3. Add calibration points (simulated vs. reference)\par
4. System calculates transformation functions\par
5. Apply calibration to future simulations\par\par
{\b Calibration Types:}\par
\bullet {\b T2 Calibration} - Adjusts relaxation times\par
\bullet {\b Amplitude Calibration} - Adjusts signal amplitudes\par\par
{\b Quality Metrics:}\par
\bullet R² values indicate fit quality\par
\bullet RMSE shows prediction accuracy\par
\bullet Visual plots help identify outliers\par\par
{\b Import Formats:}\par
\bullet CSV files with T2 and amplitude data\par
\bullet ASCII log files from NMR instruments\par
\bullet Binary log files\par
}";
            
            // GPU content
            var gpuContent = (RichTextBox)tabControl.TabPages[3].Controls[0];
        gpuContent.Rtf = @"{\rtf1\ansi\deff0{\fonttbl{\f0\fswiss\fcharset0 Segoe UI;}}
\f0\fs20 {\b GPU Acceleration}\par\par
The NMR simulation includes GPU acceleration using DirectCompute for faster processing of large datasets.\par\par
{\b Requirements:}\par
\bullet DirectX 11 compatible GPU\par
\bullet Compute Shader support\par
\bullet Minimum 1GB GPU memory\par\par
{\b Performance Benefits:}\par
\bullet 10-100x speedup for large volumes\par
\bullet Parallel processing of decay components\par
\bullet Optimized memory access patterns\par\par
{\b Fallback Behavior:}\par
If GPU acceleration fails, the system automatically falls back to multi-threaded CPU computation.\par\par
{\b Monitoring:}\par
\bullet GPU usage shown in status bar\par
\bullet Performance metrics in results\par
\bullet Memory usage tracking\par\par
{\b Tips for Best Performance:}\par
\bullet Use power-of-2 time points\par
\bullet Limit T2 components (32-64 optimal)\par
\bullet Close other GPU-intensive applications\par
}";
        }
}

/// <summary>
/// Progress dialog that shows detailed progress information
/// </summary>
public class ProgressFormWithProgress : Form
{
    private Label lblMessage;
    private ProgressBar progressBar;
    private Label lblPercentage;
    private Label lblTimeRemaining;
    private Label lblDetails;
    private Button btnCancel;
    private DateTime startTime;

    public bool IsCancelled { get; private set; }

    public ProgressFormWithProgress(string message)
    {
        InitializeComponent();
        lblMessage.Text = message;
        startTime = DateTime.Now;
    }

    private void InitializeComponent()
    {
        this.Text = "Processing...";
        this.Size = new Size(400, 200);
        this.FormBorderStyle = FormBorderStyle.FixedDialog;
        this.StartPosition = FormStartPosition.CenterParent;
        this.MaximizeBox = false;
        this.MinimizeBox = false;
        this.ControlBox = false;

        var mainPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20)
        };

        // Message
        lblMessage = new Label
        {
            Text = "Processing...",
            Location = new Point(20, 20),
            Size = new Size(360, 30),
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            ForeColor = Color.White
        };

        // Progress bar
        progressBar = new ProgressBar
        {
            Location = new Point(20, 60),
            Size = new Size(280, 25),
            Style = ProgressBarStyle.Continuous
        };

        // Percentage
        lblPercentage = new Label
        {
            Text = "0%",
            Location = new Point(310, 60),
            Size = new Size(50, 25),
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = Color.White
        };

        // Time remaining
        lblTimeRemaining = new Label
        {
            Text = "Time remaining: --",
            Location = new Point(20, 100),
            Size = new Size(200, 20),
            ForeColor = Color.LightGray
        };

        // Details
        lblDetails = new Label
        {
            Text = "",
            Location = new Point(20, 120),
            Size = new Size(360, 20),
            ForeColor = Color.LightGray
        };

        // Cancel button
        btnCancel = new Button
        {
            Text = "Cancel",
            Location = new Point(250, 140),
            Size = new Size(110, 30),
            BackColor = Color.DarkGray,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        btnCancel.Click += (s, e) => { IsCancelled = true; };

        mainPanel.Controls.Add(lblMessage);
        mainPanel.Controls.Add(progressBar);
        mainPanel.Controls.Add(lblPercentage);
        mainPanel.Controls.Add(lblTimeRemaining);
        mainPanel.Controls.Add(lblDetails);
        mainPanel.Controls.Add(btnCancel);

        this.Controls.Add(mainPanel);
        this.BackColor = Color.Black;
    }

    public void UpdateProgress(int percentage, string details = null)
    {
        if (this.InvokeRequired)
        {
            this.Invoke((Action)(() => UpdateProgress(percentage, details)));
            return;
        }

        progressBar.Value = Math.Max(0, Math.Min(100, percentage));
        lblPercentage.Text = $"{percentage}%";

        if (!string.IsNullOrEmpty(details))
        {
            lblDetails.Text = details;
        }

        // Calculate time remaining
        var elapsed = DateTime.Now - startTime;
        if (percentage > 0)
        {
            var totalTime = TimeSpan.FromTicks(elapsed.Ticks * 100 / percentage);
            var remaining = totalTime - elapsed;

            if (remaining.TotalSeconds > 0)
            {
                lblTimeRemaining.Text = $"Time remaining: {remaining:mm\\:ss}";
            }
        }

        Application.DoEvents();
    }
}
}