//Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
using System;
using System.Drawing;
using System.Windows.Forms;
using CTS.SharpDXIntegration;

namespace CTS
{
    public partial class MeshExportDialog : Form
    {
        private MeshExportOptions options;
        private RadioButton radVoxelMesh, radSurfaceMesh;
        private CheckBox chkExportGrayscale, chkExportMaterials, chkExcludeExterior;
        private NumericUpDown numFacetCount, numMinParticleRadius;
        private GroupBox grpSurfaceOptions;
        private Button btnOK, btnCancel;

        public MeshExportDialog(MeshExportOptions initialOptions)
        {
            this.options = initialOptions;
            InitializeComponent();
            LoadOptions();
        }

        private void InitializeComponent()
        {
            this.Text = "Export Mesh Options";
            this.Size = new Size(400, 450);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterParent;

            // Create main panel
            TableLayoutPanel mainPanel = new TableLayoutPanel();
            mainPanel.Dock = DockStyle.Fill;
            mainPanel.ColumnCount = 1;
            mainPanel.RowCount = 3;
            mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            mainPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            mainPanel.Padding = new Padding(10);

            // Mesh type selection
            GroupBox grpMeshType = new GroupBox();
            grpMeshType.Text = "Export Type";
            grpMeshType.Dock = DockStyle.Top;
            grpMeshType.Height = 80;
            grpMeshType.Padding = new Padding(10);

            radVoxelMesh = new RadioButton();
            radVoxelMesh.Text = "Voxel Mesh (cubes)";
            radVoxelMesh.Location = new Point(10, 20);
            radVoxelMesh.Width = 150;
            radVoxelMesh.CheckedChanged += MeshTypeChanged;
            grpMeshType.Controls.Add(radVoxelMesh);

            radSurfaceMesh = new RadioButton();
            radSurfaceMesh.Text = "Surface Mesh (smooth)";
            radSurfaceMesh.Location = new Point(10, 45);
            radSurfaceMesh.Width = 150;
            radSurfaceMesh.CheckedChanged += MeshTypeChanged;
            grpMeshType.Controls.Add(radSurfaceMesh);

            mainPanel.Controls.Add(grpMeshType, 0, 0);

            // Content selection
            GroupBox grpContent = new GroupBox();
            grpContent.Text = "Content to Export";
            grpContent.Dock = DockStyle.Fill;
            grpContent.Padding = new Padding(10);

            chkExportGrayscale = new CheckBox();
            chkExportGrayscale.Text = "Export Grayscale Volume";
            chkExportGrayscale.Location = new Point(10, 25);
            chkExportGrayscale.Width = 300;
            grpContent.Controls.Add(chkExportGrayscale);

            chkExportMaterials = new CheckBox();
            chkExportMaterials.Text = "Export Materials";
            chkExportMaterials.Location = new Point(10, 50);
            chkExportMaterials.Width = 300;
            grpContent.Controls.Add(chkExportMaterials);

            chkExcludeExterior = new CheckBox();
            chkExcludeExterior.Text = "Exclude Exterior Material";
            chkExcludeExterior.Location = new Point(30, 75);
            chkExcludeExterior.Width = 280;
            grpContent.Controls.Add(chkExcludeExterior);

            // Surface mesh options
            grpSurfaceOptions = new GroupBox();
            grpSurfaceOptions.Text = "Surface Mesh Options";
            grpSurfaceOptions.Location = new Point(10, 110);
            grpSurfaceOptions.Width = 340;
            grpSurfaceOptions.Height = 120;

            Label lblFacetCount = new Label();
            lblFacetCount.Text = "Target Facet Count:";
            lblFacetCount.Location = new Point(10, 25);
            lblFacetCount.Width = 150;
            grpSurfaceOptions.Controls.Add(lblFacetCount);

            numFacetCount = new NumericUpDown();
            numFacetCount.Minimum = 1000;
            numFacetCount.Maximum = 1000000;
            numFacetCount.Value = 50000;
            numFacetCount.Increment = 1000;
            numFacetCount.Location = new Point(170, 23);
            numFacetCount.Width = 160;
            grpSurfaceOptions.Controls.Add(numFacetCount);

            Label lblParticleRadius = new Label();
            lblParticleRadius.Text = "Min Particle Radius:";
            lblParticleRadius.Location = new Point(10, 55);
            lblParticleRadius.Width = 150;
            grpSurfaceOptions.Controls.Add(lblParticleRadius);

            numMinParticleRadius = new NumericUpDown();
            numMinParticleRadius.Minimum = 0;
            numMinParticleRadius.Maximum = 100;
            numMinParticleRadius.Value = 5;
            numMinParticleRadius.DecimalPlaces = 1;
            numMinParticleRadius.Increment = 0.5m;
            numMinParticleRadius.Location = new Point(170, 53);
            numMinParticleRadius.Width = 160;
            grpSurfaceOptions.Controls.Add(numMinParticleRadius);

            Label lblNote = new Label();
            lblNote.Text = "Higher facet count = better quality but larger file\n" +
                          "Particles smaller than radius will be removed";
            lblNote.Location = new Point(10, 85);
            lblNote.Width = 320;
            lblNote.Height = 30;
            lblNote.Font = new Font(lblNote.Font, FontStyle.Italic);
            lblNote.ForeColor = Color.DarkGray;
            grpSurfaceOptions.Controls.Add(lblNote);

            grpContent.Controls.Add(grpSurfaceOptions);

            mainPanel.Controls.Add(grpContent, 0, 1);

            // Buttons
            FlowLayoutPanel buttonPanel = new FlowLayoutPanel();
            buttonPanel.Dock = DockStyle.Bottom;
            buttonPanel.FlowDirection = FlowDirection.RightToLeft;
            buttonPanel.Height = 40;
            buttonPanel.Padding = new Padding(0, 5, 0, 0);

            btnCancel = new Button();
            btnCancel.Text = "Cancel";
            btnCancel.DialogResult = DialogResult.Cancel;
            btnCancel.Width = 75;
            btnCancel.Height = 25;
            buttonPanel.Controls.Add(btnCancel);

            btnOK = new Button();
            btnOK.Text = "OK";
            btnOK.DialogResult = DialogResult.OK;
            btnOK.Width = 75;
            btnOK.Height = 25;
            btnOK.Click += BtnOK_Click;
            buttonPanel.Controls.Add(btnOK);

            mainPanel.Controls.Add(buttonPanel, 0, 2);

            this.Controls.Add(mainPanel);
            this.AcceptButton = btnOK;
            this.CancelButton = btnCancel;
        }

        private void LoadOptions()
        {
            if (options.Mode == MeshExportMode.SurfaceMesh)
                radSurfaceMesh.Checked = true;
            else
                radVoxelMesh.Checked = true;

            chkExportGrayscale.Checked = options.ExportGrayscale;
            chkExportMaterials.Checked = options.ExportMaterials;
            chkExcludeExterior.Checked = options.ExcludeExterior;
            numFacetCount.Value = options.SurfaceFacetCount;
            numMinParticleRadius.Value = (decimal)options.MinParticleRadius;

            UpdateSurfaceOptionsVisibility();
        }

        private void MeshTypeChanged(object sender, EventArgs e)
        {
            UpdateSurfaceOptionsVisibility();
        }

        private void UpdateSurfaceOptionsVisibility()
        {
            grpSurfaceOptions.Enabled = radSurfaceMesh.Checked;
        }

        private void BtnOK_Click(object sender, EventArgs e)
        {
            if (!chkExportGrayscale.Checked && !chkExportMaterials.Checked)
            {
                MessageBox.Show("Please select at least one content type to export.", "Export Options",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                return;
            }

            SaveOptions();
        }

        private void SaveOptions()
        {
            options.Mode = radSurfaceMesh.Checked ? MeshExportMode.SurfaceMesh : MeshExportMode.VoxelMesh;
            options.ExportGrayscale = chkExportGrayscale.Checked;
            options.ExportMaterials = chkExportMaterials.Checked;
            options.ExcludeExterior = chkExcludeExterior.Checked;
            options.SurfaceFacetCount = (int)numFacetCount.Value;
            options.MinParticleRadius = (float)numMinParticleRadius.Value;
        }

        public MeshExportOptions GetOptions()
        {
            return options;
        }
    }
}