// CTViewer3DForm.cs
using System;
using System.Drawing;
using System.Windows.Forms;
using System.Threading.Tasks;

namespace CTSegmenter
{
    public partial class CTViewer3DForm : Form
    {
        private Panel renderPanel;
        private CTVolumeViewer volumeViewer;
        private MainForm mainForm;

        // UI controls
        private TrackBar opacityTrackBar;
        private TrackBar brightnessTrackBar;
        private TrackBar contrastTrackBar;
        private ComboBox renderModeComboBox;
        private CheckBox showLabelsCheckBox;
        private Button resetCameraButton;

        public CTViewer3DForm(MainForm owner)
        {
            mainForm = owner;
            InitializeComponent();
            this.FormClosing += (s, e) => volumeViewer?.Dispose();
        }

        private void InitializeComponent()
        {
            this.Text = "CT 3D Volume Viewer";
            this.Size = new Size(1024, 768);
            this.StartPosition = FormStartPosition.CenterScreen;

            // Main layout with render panel and controls
            TableLayoutPanel mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1
            };
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 80));
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));

            // DirectX rendering panel
            renderPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black
            };
            mainLayout.Controls.Add(renderPanel, 0, 0);

            // Controls panel
            Panel controlsPanel = new Panel { Dock = DockStyle.Fill };

            // Add controls inside a flow panel for scrolling
            FlowLayoutPanel flowPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(10)
            };

            // Render mode
            Label lblRenderMode = new Label { Text = "Render Mode:", AutoSize = true, Margin = new Padding(0, 10, 0, 5) };
            flowPanel.Controls.Add(lblRenderMode);

            renderModeComboBox = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 180
            };
            renderModeComboBox.Items.AddRange(new object[] { "Volume Rendering", "Maximum Intensity", "Isosurface" });
            renderModeComboBox.SelectedIndex = 0;
            renderModeComboBox.SelectedIndexChanged += (s, e) =>
            {
                if (volumeViewer != null)
                {
                    volumeViewer.RenderMode = renderModeComboBox.SelectedIndex;
                    volumeViewer.UpdateRenderingParameters();
                }
            };
            flowPanel.Controls.Add(renderModeComboBox);

            // Show labels checkbox
            showLabelsCheckBox = new CheckBox
            {
                Text = "Show Segmentation Labels",
                AutoSize = true,
                Checked = true,
                Margin = new Padding(0, 10, 0, 15)
            };
            showLabelsCheckBox.CheckedChanged += (s, e) =>
            {
                if (volumeViewer != null)
                {
                    volumeViewer.ShowLabels = showLabelsCheckBox.Checked;
                    volumeViewer.UpdateRenderingParameters();
                }
            };
            flowPanel.Controls.Add(showLabelsCheckBox);

            // Opacity control
            Label lblOpacity = new Label { Text = "Opacity:", AutoSize = true };
            flowPanel.Controls.Add(lblOpacity);

            opacityTrackBar = new TrackBar
            {
                Minimum = 1,
                Maximum = 100,
                Value = 5, // 0.05 initial opacity
                TickFrequency = 10,
                Width = 180
            };
            opacityTrackBar.Scroll += (s, e) =>
            {
                if (volumeViewer != null)
                {
                    volumeViewer.Opacity = opacityTrackBar.Value / 1000.0f;
                    volumeViewer.UpdateRenderingParameters();
                }
            };
            flowPanel.Controls.Add(opacityTrackBar);

            // Brightness control
            Label lblBrightness = new Label { Text = "Brightness:", AutoSize = true, Margin = new Padding(0, 10, 0, 5) };
            flowPanel.Controls.Add(lblBrightness);

            brightnessTrackBar = new TrackBar
            {
                Minimum = -50,
                Maximum = 50,
                Value = 0,
                TickFrequency = 10,
                Width = 180
            };
            brightnessTrackBar.Scroll += (s, e) =>
            {
                if (volumeViewer != null)
                {
                    volumeViewer.Brightness = brightnessTrackBar.Value / 50.0f;
                    volumeViewer.UpdateRenderingParameters();
                }
            };
            flowPanel.Controls.Add(brightnessTrackBar);

            // Contrast control
            Label lblContrast = new Label { Text = "Contrast:", AutoSize = true, Margin = new Padding(0, 10, 0, 5) };
            flowPanel.Controls.Add(lblContrast);

            contrastTrackBar = new TrackBar
            {
                Minimum = 10,
                Maximum = 200,
                Value = 100, // 1.0 initial contrast
                TickFrequency = 10,
                Width = 180
            };
            contrastTrackBar.Scroll += (s, e) =>
            {
                if (volumeViewer != null)
                {
                    volumeViewer.Contrast = contrastTrackBar.Value / 100.0f;
                    volumeViewer.UpdateRenderingParameters();
                }
            };
            flowPanel.Controls.Add(contrastTrackBar);

            // Reset camera button
            resetCameraButton = new Button
            {
                Text = "Reset Camera",
                Width = 180,
                Margin = new Padding(0, 20, 0, 0)
            };
            resetCameraButton.Click += (s, e) =>
            {
                volumeViewer?.ResetCamera();
            };
            flowPanel.Controls.Add(resetCameraButton);

            // Help text
            Label lblHelp = new Label
            {
                Text = "Mouse Controls:\n" +
                       "- Left click + drag to rotate\n" +
                       "- Mouse wheel to zoom",
                AutoSize = true,
                Margin = new Padding(0, 20, 0, 0)
            };
            flowPanel.Controls.Add(lblHelp);

            controlsPanel.Controls.Add(flowPanel);
            mainLayout.Controls.Add(controlsPanel, 1, 0);

            this.Controls.Add(mainLayout);

            // Initialize volume viewer after form is shown
            this.Shown += async (s, e) => await InitializeVolumeViewerAsync();
        }

        private async Task InitializeVolumeViewerAsync()
        {
            try
            {
                // Initialize the volume viewer with the render panel
                volumeViewer = new CTVolumeViewer(renderPanel);

                // Set initial parameters
                volumeViewer.Opacity = opacityTrackBar.Value / 1000.0f;
                volumeViewer.Brightness = brightnessTrackBar.Value / 50.0f;
                volumeViewer.Contrast = contrastTrackBar.Value / 100.0f;
                volumeViewer.RenderMode = renderModeComboBox.SelectedIndex;
                volumeViewer.ShowLabels = showLabelsCheckBox.Checked;
                volumeViewer.UpdateRenderingParameters();

                // Load volume data
                if (mainForm.volumeData != null)
                {
                    this.UseWaitCursor = true;
                    await volumeViewer.LoadVolumeAsync(mainForm.volumeData, mainForm.pixelSize);
                    this.UseWaitCursor = false;
                }

                // Load label data
                if (mainForm.volumeLabels != null)
                {
                    this.UseWaitCursor = true;
                    await volumeViewer.LoadLabelsAsync(mainForm.volumeLabels);
                    this.UseWaitCursor = false;
                }

                // Update material colors
                volumeViewer.UpdateMaterials(mainForm.Materials);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error initializing 3D viewer: {ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                this.Close();
            }
        }
    }
}
