//Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
using Krypton.Toolkit;
using System;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CTS
{
    /// <summary>
    /// A form that allows users to remove small, disconnected particles (islands)
    /// of a specified material from the label volume.
    /// </summary>
    public partial class RemoveIslandsForm : KryptonForm
    {
        private MainForm mainForm;
        private ComboBox cmbMaterial;
        private NumericUpDown numThreshold;
        private ComboBox cmbUnits;
        private Button btnRun;
        private ProgressBar progressBar;
        private Label lblStatus;
        private Label lblCalculatedVoxels;

        public RemoveIslandsForm(MainForm mainForm)
        {
            this.mainForm = mainForm;
            InitializeComponent();
            PopulateMaterials();
            UpdateCalculatedVoxelCount();
        }

        private void InitializeComponent()
        {
            this.Icon = Properties.Resources.favicon;
            this.Text = "Remove Islands";
            this.Size = new Size(400, 300);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.StartPosition = FormStartPosition.CenterParent;
            this.Padding = new Padding(10);
            this.PaletteMode = PaletteMode.Office2010Black;

            // Material selection
            var lblMaterial = new Label { Text = "Target Material:", Location = new Point(10, 15), AutoSize = true };
            cmbMaterial = new ComboBox { Location = new Point(140, 12), Width = 220, DropDownStyle = ComboBoxStyle.DropDownList };

            // Threshold
            var lblThreshold = new Label { Text = "Remove if radius smaller than:", Location = new Point(10, 55), AutoSize = true };
            numThreshold = new NumericUpDown { Location = new Point(190, 52), Width = 80, Minimum = 1, Maximum = 100000, Value = 10 };
            cmbUnits = new ComboBox { Location = new Point(280, 52), Width = 80, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbUnits.Items.AddRange(new object[] { "voxels", "µm", "mm" });
            cmbUnits.SelectedIndex = 0;

            // Calculated size
            lblCalculatedVoxels = new Label { Text = "Equivalent to: 10 voxels", Location = new Point(10, 90), AutoSize = true, ForeColor = Color.Gray };

            // Status and Progress
            lblStatus = new Label { Text = "Ready.", Location = new Point(10, 130), Width = 350 };
            progressBar = new ProgressBar { Location = new Point(10, 160), Width = 350, Style = ProgressBarStyle.Continuous };

            // Buttons
            btnRun = new Button { Text = "Run", Location = new Point(270, 200), Width = 90 };
            var btnCancel = new Button { Text = "Close", Location = new Point(170, 200), Width = 90, DialogResult = DialogResult.Cancel };

            // Event Handlers
            btnRun.Click += BtnRun_Click;
            numThreshold.ValueChanged += (s, e) => UpdateCalculatedVoxelCount();
            cmbUnits.SelectedIndexChanged += (s, e) => UpdateCalculatedVoxelCount();

            this.Controls.AddRange(new Control[] { lblMaterial, cmbMaterial, lblThreshold, numThreshold, cmbUnits, lblCalculatedVoxels, lblStatus, progressBar, btnRun, btnCancel });
            this.AcceptButton = btnRun;
            this.CancelButton = btnCancel;
        }

        private void PopulateMaterials()
        {
            // Populate the ComboBox with all materials except "Exterior"
            var materials = mainForm.Materials.Where(m => !m.IsExterior).ToList();
            cmbMaterial.DataSource = materials;
            cmbMaterial.DisplayMember = "Name";
            cmbMaterial.ValueMember = "ID";
        }

        private void UpdateCalculatedVoxelCount()
        {
            if (cmbUnits.SelectedItem.ToString() == "voxels")
            {
                lblCalculatedVoxels.Text = $"Threshold is {numThreshold.Value} voxels.";
                return;
            }

            try
            {
                long count = CalculateMinVoxelCount();
                lblCalculatedVoxels.Text = $"Equivalent to: {count:N0} voxels.";
            }
            catch (Exception ex)
            {
                lblCalculatedVoxels.Text = $"Error: {ex.Message}";
            }
        }

        private long CalculateMinVoxelCount()
        {
            double radius = (double)numThreshold.Value;
            string unit = cmbUnits.SelectedItem.ToString();
            double pixelSizeMeters = mainForm.GetPixelSize();

            if (pixelSizeMeters <= 0)
                throw new InvalidOperationException("Pixel size is not valid.");

            // Convert radius to meters
            double radiusMeters = 0;
            if (unit == "µm") radiusMeters = radius * 1e-6;
            else if (unit == "mm") radiusMeters = radius * 1e-3;
            else return (long)radius; // Voxels

            // Calculate volumes
            double sphereVolumeMeters = (4.0 / 3.0) * Math.PI * Math.Pow(radiusMeters, 3);
            double voxelVolumeMeters = Math.Pow(pixelSizeMeters, 3);

            return (long)(sphereVolumeMeters / voxelVolumeMeters);
        }

        private async void BtnRun_Click(object sender, EventArgs e)
        {
            if (cmbMaterial.SelectedItem == null)
            {
                MessageBox.Show("Please select a material to process.", "No Material Selected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // --- Get Parameters ---
            var selectedMaterial = (Material)cmbMaterial.SelectedItem;
            long minVoxelCount;
            try
            {
                minVoxelCount = (cmbUnits.SelectedItem.ToString() == "voxels")
                    ? (long)numThreshold.Value
                    : CalculateMinVoxelCount();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error calculating voxel count: {ex.Message}", "Input Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var confirmResult = MessageBox.Show(
                $"This will remove all particles of '{selectedMaterial.Name}' smaller than {minVoxelCount:N0} voxels.\n\nThis operation cannot be undone. Continue?",
                "Confirm Operation", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (confirmResult == DialogResult.No) return;

            // --- Run Operation ---
            SetUIEnabled(false);
            var analyzer = new ParticleAnalyzer();
            var statusProgress = new Progress<string>(status => lblStatus.Text = status);
            var percentProgress = new Progress<int>(percent => progressBar.Value = percent);

            try
            {
                await analyzer.RemoveSmallIslandsAsync(
                    mainForm.volumeLabels,
                    mainForm.Materials,
                    selectedMaterial.ID,
                    minVoxelCount,
                    statusProgress,
                    percentProgress
                );

                mainForm.RenderViews(MainForm.ViewType.All);
                mainForm.SaveLabelsChk();
                MessageBox.Show("Operation completed successfully!", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Logger.Log($"[RemoveIslands] Error during operation: {ex}");
                MessageBox.Show($"An error occurred: {ex.Message}", "Operation Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetUIEnabled(true);
            }
        }

        private void SetUIEnabled(bool isEnabled)
        {
            cmbMaterial.Enabled = isEnabled;
            numThreshold.Enabled = isEnabled;
            cmbUnits.Enabled = isEnabled;
            btnRun.Enabled = isEnabled;
        }
    }
}
