// Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
// This is an adapted version of the original SharpDXControlPanel.
// It is designed to work with the D3D11ViewerForm and D3D11VolumeRenderer.

using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace CTS.D3D11
{
    public struct RenderParameters
    {
        public Vector2 Threshold;
        public float Quality;
        public Vector4 ClippingPlane;
        public Vector4 SliceInfo;
        public Vector4 CutInfo;
    }

    public partial class D3D11ControlPanel : Form
    {
        private D3D11ViewerForm viewerForm;
        private MainForm mainForm;
        private D3D11VolumeRenderer volumeRenderer;

        // UI elements from the original file
        private CheckBox chkDebugMode;
        private Timer thresholdUpdateTimer;
        private TrackBar trkMinThreshold, trkMaxThreshold;
        private NumericUpDown numMinThreshold, numMaxThreshold;
        private CheckBox chkShowGrayscale;
        private ComboBox cmbQuality;
        private CheckBox chkSlices;
        private TrackBar trkXSlice, trkYSlice, trkZSlice;
        private CheckBox chkSliceX, chkSliceY, chkSliceZ;
        private CheckBox chkCutX, chkCutY, chkCutZ;
        private TrackBar trkCutX, trkCutY, trkCutZ;
        private CheckedListBox lstMaterials;
        private TrackBar trkOpacity;
        private CheckBox chkEnableClippingPlane;
        private TrackBar trkClippingPosition, trkClippingAngle;
        private RadioButton radClippingXY, radClippingXZ, radClippingYZ;

        public D3D11ControlPanel(D3D11ViewerForm viewer, MainForm main, D3D11VolumeRenderer renderer)
        {
            viewerForm = viewer;
            mainForm = main;
            volumeRenderer = renderer;

            this.Text = "3D Control Panel (D3D11)";
            this.Size = new Size(400, 700);
            this.FormBorderStyle = FormBorderStyle.FixedToolWindow;
            this.StartPosition = FormStartPosition.Manual;
            try
            {
                this.Icon = Properties.Resources.favicon;
            }
            catch { }

            InitializeComponentLayout();
            PopulateMaterials();
            InitializeTimers();
        }

        private void InitializeComponentLayout()
        {
            var tabControl = new TabControl { Dock = DockStyle.Fill };
            var tabRendering = new TabPage("Rendering");
            var tabMaterials = new TabPage("Materials");
            var tabSlices = new TabPage("Slices & Cuts");
            var tabClipping = new TabPage("Clipping Plane");

            // Populate each tab
            SetupRenderingTab(tabRendering);
            SetupMaterialsTab(tabMaterials);
            SetupSlicesAndCutsTab(tabSlices);
            SetupClippingTab(tabClipping);

            tabControl.TabPages.AddRange(new[] { tabRendering, tabMaterials, tabSlices, tabClipping });
            this.Controls.Add(tabControl);
        }

        // Methods to set up each tab (simplified for brevity)
        private void SetupRenderingTab(TabPage page)
        {
            var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(10), AutoScroll = true };

            // Quality
            panel.Controls.Add(new Label { Text = "Rendering Quality", AutoSize = true });
            cmbQuality = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            cmbQuality.Items.AddRange(new[] { "Low (Fastest)", "Medium", "High (Slowest)" });
            cmbQuality.SelectedIndex = 1;
            cmbQuality.SelectedIndexChanged += (s, e) => UpdateRenderParams();
            panel.Controls.Add(cmbQuality);

            // Threshold
            panel.Controls.Add(new Label { Text = "Grayscale Threshold", AutoSize = true, Margin = new Padding(0, 10, 0, 0) });
            trkMinThreshold = new TrackBar { Minimum = 0, Maximum = 255, Value = 30, Width = 300 };
            trkMaxThreshold = new TrackBar { Minimum = 0, Maximum = 255, Value = 200, Width = 300 };
            trkMinThreshold.Scroll += (s, e) => { numMinThreshold.Value = trkMinThreshold.Value; thresholdUpdateTimer.Start(); };
            trkMaxThreshold.Scroll += (s, e) => { numMaxThreshold.Value = trkMaxThreshold.Value; thresholdUpdateTimer.Start(); };
            numMinThreshold = new NumericUpDown { Minimum = 0, Maximum = 255, Value = 30 };
            numMaxThreshold = new NumericUpDown { Minimum = 0, Maximum = 255, Value = 200 };
            numMinThreshold.ValueChanged += (s, e) => trkMinThreshold.Value = (int)numMinThreshold.Value;
            numMaxThreshold.ValueChanged += (s, e) => trkMaxThreshold.Value = (int)numMaxThreshold.Value;

            panel.Controls.Add(new Label { Text = "Min:", AutoSize = true });
            panel.Controls.Add(trkMinThreshold);
            panel.Controls.Add(numMinThreshold);
            panel.Controls.Add(new Label { Text = "Max:", AutoSize = true });
            panel.Controls.Add(trkMaxThreshold);
            panel.Controls.Add(numMaxThreshold);

            page.Controls.Add(panel);
        }

        private void SetupMaterialsTab(TabPage page)
        {
            var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(10), AutoScroll = true };

            panel.Controls.Add(new Label { Text = "Materials Visibility & Opacity", AutoSize = true });
            lstMaterials = new CheckedListBox { Width = 350, Height = 300, CheckOnClick = true };
            lstMaterials.ItemCheck += LstMaterials_ItemCheck;
            lstMaterials.SelectedIndexChanged += LstMaterials_SelectedIndexChanged;
            panel.Controls.Add(lstMaterials);

            panel.Controls.Add(new Label { Text = "Selected Material Opacity", AutoSize = true });
            trkOpacity = new TrackBar { Minimum = 0, Maximum = 100, Value = 100, Width = 300 };
            trkOpacity.Scroll += TrkOpacity_Scroll;
            panel.Controls.Add(trkOpacity);

            page.Controls.Add(panel);
        }

        private void SetupSlicesAndCutsTab(TabPage page)
        {
            var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(10), AutoScroll = true };

            // Slices
            panel.Controls.Add(new Label { Text = "Orthogonal Slices", Font = new Font(this.Font, FontStyle.Bold) });
            chkSliceX = new CheckBox { Text = "Show X Slice", AutoSize = true };
            chkSliceY = new CheckBox { Text = "Show Y Slice", AutoSize = true };
            chkSliceZ = new CheckBox { Text = "Show Z Slice", AutoSize = true };
            trkXSlice = new TrackBar { Width = 300, Maximum = mainForm.GetWidth() - 1, Value = mainForm.GetWidth() / 2 };
            trkYSlice = new TrackBar { Width = 300, Maximum = mainForm.GetHeight() - 1, Value = mainForm.GetHeight() / 2 };
            trkZSlice = new TrackBar { Width = 300, Maximum = mainForm.GetDepth() - 1, Value = mainForm.GetDepth() / 2 };
            chkSliceX.CheckedChanged += (s, e) => UpdateRenderParams();
            chkSliceY.CheckedChanged += (s, e) => UpdateRenderParams();
            chkSliceZ.CheckedChanged += (s, e) => UpdateRenderParams();
            trkXSlice.Scroll += (s, e) => UpdateRenderParams();
            trkYSlice.Scroll += (s, e) => UpdateRenderParams();
            trkZSlice.Scroll += (s, e) => UpdateRenderParams();
            panel.Controls.AddRange(new Control[] { chkSliceX, trkXSlice, chkSliceY, trkYSlice, chkSliceZ, trkZSlice });

            // Cuts
            panel.Controls.Add(new Label { Text = "Axis-Aligned Cutting", Font = new Font(this.Font, FontStyle.Bold), Margin = new Padding(0, 20, 0, 0) });
            chkCutX = new CheckBox { Text = "Enable X Cut", AutoSize = true };
            chkCutY = new CheckBox { Text = "Enable Y Cut", AutoSize = true };
            chkCutZ = new CheckBox { Text = "Enable Z Cut", AutoSize = true };
            trkCutX = new TrackBar { Width = 300, Minimum = -100, Maximum = 100, Value = 100 };
            trkCutY = new TrackBar { Width = 300, Minimum = -100, Maximum = 100, Value = 100 };
            trkCutZ = new TrackBar { Width = 300, Minimum = -100, Maximum = 100, Value = 100 };
            chkCutX.CheckedChanged += (s, e) => UpdateRenderParams();
            chkCutY.CheckedChanged += (s, e) => UpdateRenderParams();
            chkCutZ.CheckedChanged += (s, e) => UpdateRenderParams();
            trkCutX.Scroll += (s, e) => UpdateRenderParams();
            trkCutY.Scroll += (s, e) => UpdateRenderParams();
            trkCutZ.Scroll += (s, e) => UpdateRenderParams();
            panel.Controls.AddRange(new Control[] { chkCutX, trkCutX, chkCutY, trkCutY, chkCutZ, trkCutZ });

            page.Controls.Add(panel);
        }

        private void SetupClippingTab(TabPage page)
        {
            var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(10), AutoScroll = true };

            chkEnableClippingPlane = new CheckBox { Text = "Enable Arbitrary Clipping Plane", AutoSize = true };
            chkEnableClippingPlane.CheckedChanged += (s, e) => UpdateRenderParams();
            panel.Controls.Add(chkEnableClippingPlane);

            panel.Controls.Add(new Label { Text = "Plane Normal (Orientation)", AutoSize = true });
            radClippingXY = new RadioButton { Text = "XY Normal", AutoSize = true, Checked = true };
            radClippingXZ = new RadioButton { Text = "XZ Normal", AutoSize = true };
            radClippingYZ = new RadioButton { Text = "YZ Normal", AutoSize = true };
            radClippingXY.CheckedChanged += (s, e) => UpdateRenderParams();
            radClippingXZ.CheckedChanged += (s, e) => UpdateRenderParams();
            radClippingYZ.CheckedChanged += (s, e) => UpdateRenderParams();
            panel.Controls.AddRange(new Control[] { radClippingXY, radClippingXZ, radClippingYZ });

            panel.Controls.Add(new Label { Text = "Plane Angle", AutoSize = true });
            trkClippingAngle = new TrackBar { Width = 300, Minimum = 0, Maximum = 360, Value = 0 };
            trkClippingAngle.Scroll += (s, e) => UpdateRenderParams();
            panel.Controls.Add(trkClippingAngle);

            panel.Controls.Add(new Label { Text = "Plane Position", AutoSize = true });
            trkClippingPosition = new TrackBar { Width = 300, Minimum = -100, Maximum = 100, Value = 0 };
            trkClippingPosition.Scroll += (s, e) => UpdateRenderParams();
            panel.Controls.Add(trkClippingPosition);

            page.Controls.Add(panel);
        }

        private void InitializeTimers()
        {
            thresholdUpdateTimer = new Timer { Interval = 200 };
            thresholdUpdateTimer.Tick += (s, e) =>
            {
                thresholdUpdateTimer.Stop();
                UpdateRenderParams();
            };
        }

        private void PopulateMaterials()
        {
            if (mainForm.Materials == null) return;
            lstMaterials.Items.Clear();
            foreach (var mat in mainForm.Materials)
            {
                // Default all materials to visible when the viewer opens
                lstMaterials.Items.Add(mat, true);
            }
            if (lstMaterials.Items.Count > 0)
                lstMaterials.SelectedIndex = 0;
        }

        private void LstMaterials_ItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (e.Index < 0 || e.Index >= mainForm.Materials.Count) return;
            var material = mainForm.Materials[e.Index];
            material.IsVisible = e.NewValue == CheckState.Checked;
            volumeRenderer.UpdateMaterialsBuffer();
            volumeRenderer.NeedsRender = true;
        }

        private void LstMaterials_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (lstMaterials.SelectedIndex < 0) return;
            var material = mainForm.Materials[lstMaterials.SelectedIndex];
            trkOpacity.Value = (int)(material.GetOpacity() * 100);
        }

        private void TrkOpacity_Scroll(object sender, EventArgs e)
        {
            if (lstMaterials.SelectedIndex < 0) return;
            var material = mainForm.Materials[lstMaterials.SelectedIndex];
            material.SetOpacity(trkOpacity.Value / 100.0f);
            volumeRenderer.UpdateMaterialsBuffer();
            volumeRenderer.NeedsRender = true;
        }

        private void UpdateRenderParams()
        {
            var p = new RenderParameters();

            // Threshold
            p.Threshold = new Vector2(trkMinThreshold.Value, trkMaxThreshold.Value);

            // Quality
            p.Quality = cmbQuality.SelectedIndex;

            // Slices
            p.SliceInfo.X = chkSliceX.Checked ? (trkXSlice.Value / (float)trkXSlice.Maximum) : -1.0f;
            p.SliceInfo.Y = chkSliceY.Checked ? (trkYSlice.Value / (float)trkYSlice.Maximum) : -1.0f;
            p.SliceInfo.Z = chkSliceZ.Checked ? (trkZSlice.Value / (float)trkZSlice.Maximum) : -1.0f;

            // Cuts
            p.CutInfo.X = chkCutX.Checked ? trkCutX.Value / 100.0f : 2.0f; // Use 2.0 as "disabled"
            p.CutInfo.Y = chkCutY.Checked ? trkCutY.Value / 100.0f : 2.0f;
            p.CutInfo.Z = chkCutZ.Checked ? trkCutZ.Value / 100.0f : 2.0f;

            // Clipping Plane
            if (chkEnableClippingPlane.Checked)
            {
                Vector3 normal = Vector3.UnitZ;
                if (radClippingXZ.Checked) normal = Vector3.UnitY;
                if (radClippingYZ.Checked) normal = Vector3.UnitX;

                float angle = trkClippingAngle.Value * (float)Math.PI / 180.0f;
                var rotation = Matrix4x4.CreateFromAxisAngle(Vector3.UnitZ, angle);
                normal = Vector3Extensions.TransformNormal(normal, rotation);

                p.ClippingPlane.X = normal.X;
                p.ClippingPlane.Y = normal.Y;
                p.ClippingPlane.Z = normal.Z;
                p.ClippingPlane.W = trkClippingPosition.Value / 100.0f * mainForm.GetWidth() - (mainForm.GetWidth() / 2f); // Distance from center
            }
            else
            {
                p.ClippingPlane = new Vector4(0, 0, 0, float.MaxValue); // Disabled
            }

            volumeRenderer.SetRenderParams(p);
        }
    }
}