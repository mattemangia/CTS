using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace CTS.SharpDXIntegration
{
    public partial class SharpDXControlPanel : Form
    {
        private CheckBox chkDebugMode;
        private SharpDXViewerForm viewerForm;
        private MainForm mainForm;
        private SharpDXVolumeRenderer volumeRenderer;
        private Timer thresholdUpdateTimer;
        private Timer opacityUpdateTimer;
        private byte pendingMaterialId;
        private float pendingOpacity;
        private bool opacityUpdatePending = false;
        private int pendingMinThreshold;
        private int pendingMaxThreshold;
        private int pendingQualityIndex;
        private bool thresholdUpdatePending = false;
        private bool isThresholdUpdating = false;

        private TabPage tabClippingPlane;
        private CheckBox chkEnableClippingPlane;
        private RadioButton radClippingXY, radClippingXZ, radClippingYZ;
        private TrackBar trkClippingAngle;
        private TrackBar trkClippingPosition;
        private CheckBox chkClippingMirror;
        private Label lblClippingAngle;
        private Label lblClippingPosition;
        
        private TrackBar trkRotationX, trkRotationY, trkRotationZ; // Pitch, Yaw, Roll
        
        private Label lblRotationX, lblRotationY, lblRotationZ;
     
        private Panel panelPlanePreview;

        // UI elements
        private TrackBar trkMinThreshold;

        private TrackBar trkMaxThreshold;
        private NumericUpDown numMinThreshold;
        private NumericUpDown numMaxThreshold;
        private CheckBox chkShowGrayscale;
        private ComboBox cmbQuality;
        private ComboBox cmbColorMap;
        private CheckBox chkSlices;
        private TrackBar trkXSlice, trkYSlice, trkZSlice;
        private Button btnScreenshot;
        private Button btnExportModel;
        private ProgressBar progress;
        private Label lblStatus;
        private TabControl tabControl;
        private TabPage tabRendering;
        private TabPage tabMaterials;
        private TabPage tabSlices;
        private TabPage tabCutting;
        private TabPage tabInfo;
        private FlowLayoutPanel panel = new FlowLayoutPanel();

        // Material controls
        private CheckedListBox lstMaterials;

        private TrackBar trkOpacity;
        private Label lblOpacity;
        private Button btnDebugTest;

        // Cutting plane controls
        private CheckBox chkCutX, chkCutY, chkCutZ;

        private RadioButton radCutXForward, radCutXBackward;
        private RadioButton radCutYForward, radCutYBackward;
        private RadioButton radCutZForward, radCutZBackward;
        private TrackBar trkCutX, trkCutY, trkCutZ;

        private CheckBox chkSliceX, chkSliceY, chkSliceZ;

        // Info panel
        private Label lblVolumeInfo;

        private Label lblMaterialsInfo;
        private Label lblPixelSizeInfo;

        private Label lblXSliceValue;
        private Label lblYSliceValue;
        private Label lblZSliceValue;

        //Measurement Tab
        private TabPage tabMeasurements;

        private CheckedListBox lstMeasurements;
        private Button btnAddMeasure, btnDeleteMeasure, btnExportMeasures;
        private CheckBox chkShowMeasurements;

        //Streaming render
        private CheckBox chkUseStreaming;

        public SharpDXControlPanel(SharpDXViewerForm viewer, MainForm main, SharpDXVolumeRenderer renderer)
        {
            try
            {
                this.Icon = Properties.Resources.favicon;
            }
            catch { }
            viewerForm = viewer;
            mainForm = main;
            volumeRenderer = renderer;

            InitializeComponent();

            // Initialize debouncing timers
            InitializeOpacityTimer();
            InitializeThresholdTimer();
            InitializeClippingPlaneTab();
            InitializeRenderingTab();
            InitializeMaterialsTab();
            InitializeSlicesTab();
            InitializeCuttingTab();
            InitializeInfoTab();
            InitializeMeasurementsTab();

            // Select the first tab
            tabControl.SelectedIndex = 0;
        }

        private void InitializeComponent()
        {
            this.Text = "3D Control Panel (SharpDX)";
            this.Size = new Size(400, 650);
            this.FormBorderStyle = FormBorderStyle.FixedToolWindow;
            this.StartPosition = FormStartPosition.Manual;

            tabControl = new TabControl();
            tabControl.Dock = DockStyle.Fill;

            tabRendering = new TabPage("Rendering");
            tabMaterials = new TabPage("Materials");
            tabSlices = new TabPage("Slices");
            tabCutting = new TabPage("Cutting");
            tabInfo = new TabPage("Info");
            tabClippingPlane = new TabPage("Clipping Plane");
            
          
            
            tabControl.TabPages.Add(tabRendering);
            tabControl.TabPages.Add(tabMaterials);
            tabControl.TabPages.Add(tabSlices);
            tabControl.TabPages.Add(tabCutting);
            tabControl.TabPages.Add(tabClippingPlane);
            tabControl.TabPages.Add(tabInfo);
            tabMeasurements = new TabPage("Measurements");
            tabControl.TabPages.Add(tabMeasurements);

            this.Controls.Add(tabControl);
        }

        private void InitializeMeasurementsTab()
        {
            // Create panel to hold controls
            FlowLayoutPanel panel = new FlowLayoutPanel();
            panel.Dock = DockStyle.Fill;
            panel.FlowDirection = FlowDirection.TopDown;
            panel.AutoScroll = true;
            panel.Padding = new Padding(10);
            panel.WrapContents = false;

            // Add title label
            Label lblTitle = new Label();
            lblTitle.Text = "Measurement Tools";
            lblTitle.Font = new Font(lblTitle.Font.FontFamily, 10, FontStyle.Bold);
            lblTitle.AutoSize = true;
            lblTitle.Margin = new Padding(0, 0, 0, 10);
            panel.Controls.Add(lblTitle);

            // Show/Hide all measurements checkbox
            chkShowMeasurements = new CheckBox();
            chkShowMeasurements.Text = "Show All Measurements";
            chkShowMeasurements.Checked = true;
            chkShowMeasurements.CheckedChanged += chkShowMeasurements_CheckedChanged;
            panel.Controls.Add(chkShowMeasurements);

            // Measurement mode button
            btnAddMeasure = new Button();
            btnAddMeasure.Text = "Add New Measurement";
            btnAddMeasure.Width = 200;
            btnAddMeasure.Click += (s, e) =>
            {
                Logger.Log("[SharpDXControlPanel] Add measurement button clicked");
                bool currentMode = viewerForm.ToggleMeasurementMode();
                UpdateMeasurementUI(currentMode);
            };
            panel.Controls.Add(btnAddMeasure);

            // Add measurements list
            Label lblMeasurements = new Label();
            lblMeasurements.Text = "Existing Measurements:";
            lblMeasurements.AutoSize = true;
            lblMeasurements.Margin = new Padding(0, 10, 0, 5);
            panel.Controls.Add(lblMeasurements);

            lstMeasurements = new CheckedListBox();
            lstMeasurements.Width = 330;
            lstMeasurements.Height = 200;
            lstMeasurements.CheckOnClick = true;
            lstMeasurements.ItemCheck += lstMeasurements_ItemCheck;

            // Add selection changed event to update UI when selection changes
            lstMeasurements.SelectedIndexChanged += (s, e) =>
            {
                btnDeleteMeasure.Enabled = lstMeasurements.SelectedIndex >= 0;
            };

            panel.Controls.Add(lstMeasurements);

            // Delete selected measurement
            btnDeleteMeasure = new Button();
            btnDeleteMeasure.Text = "Delete Selected";
            btnDeleteMeasure.Width = 150;
            btnDeleteMeasure.Enabled = false; // Start disabled until selection is made
            btnDeleteMeasure.Click += btnDeleteMeasure_Click;
            panel.Controls.Add(btnDeleteMeasure);

            // Export measurements
            btnExportMeasures = new Button();
            btnExportMeasures.Text = "Export Measurements";
            btnExportMeasures.Width = 200;
            btnExportMeasures.Margin = new Padding(0, 10, 0, 0);
            btnExportMeasures.Click += (s, e) =>
            {
                using (var saveDlg = new SaveFileDialog())
                {
                    saveDlg.Filter = "CSV files (*.csv)|*.csv";
                    saveDlg.FileName = "measurements.csv";

                    if (saveDlg.ShowDialog(this) == DialogResult.OK)
                    {
                        try
                        {
                            viewerForm.ExportMeasurementsToCSV(saveDlg.FileName);
                            lblStatus.Text = "Measurements exported to CSV.";
                        }
                        catch (Exception ex)
                        {
                            lblStatus.Text = "Export failed.";
                            MessageBox.Show($"Error exporting measurements: {ex.Message}", "Export Error",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    }
                }
            };
            panel.Controls.Add(btnExportMeasures);

            tabMeasurements.Controls.Add(panel);

            // Refresh measurements list to show any existing measurements
            RefreshMeasurementsList();
        }

        private void chkShowMeasurements_CheckedChanged(object sender, EventArgs e)
        {
            bool isChecked = chkShowMeasurements.Checked;
            Logger.Log($"[SharpDXControlPanel] Show measurements checkbox changed to: {isChecked}");

            // Call the viewer to set all measurements visible/invisible
            viewerForm.SetMeasurementsVisible(isChecked);

            // Also update the checkbox state in the list
            try
            {
                for (int i = 0; i < lstMeasurements.Items.Count; i++)
                {
                    lstMeasurements.SetItemChecked(i, isChecked);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[SharpDXControlPanel] Error updating checkboxes: {ex.Message}");
            }
        }

        private void btnDeleteMeasure_Click(object sender, EventArgs e)
        {
            try
            {
                int selectedIndex = lstMeasurements.SelectedIndex;
                Logger.Log($"[SharpDXControlPanel] Delete button clicked, selected index: {selectedIndex}");

                if (selectedIndex >= 0)
                {
                    Logger.Log($"[SharpDXControlPanel] Deleting measurement at index: {selectedIndex}");

                    // Call the viewer to delete the measurement
                    viewerForm.DeleteMeasurement(selectedIndex);

                    // Update status
                    lblStatus.Text = "Measurement deleted.";
                }
                else
                {
                    Logger.Log("[SharpDXControlPanel] No measurement selected for deletion");
                    lblStatus.Text = "Please select a measurement to delete.";
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[SharpDXControlPanel] Error in delete measurement: {ex.Message}");
                lblStatus.Text = "Error deleting measurement.";
            }
        }

        private void InitializeShowMeasurementsCheckbox()
        {
            // Show/Hide measurements
            chkShowMeasurements = new CheckBox();
            chkShowMeasurements.Text = "Show All Measurements";
            chkShowMeasurements.Checked = true;

            // Use the new event handler
            chkShowMeasurements.CheckedChanged += chkShowMeasurements_CheckedChanged;

            // Add to panel
            panel.Controls.Add(chkShowMeasurements);
        }

        private void lstMeasurements_ItemCheck(object sender, ItemCheckEventArgs e)
        {
            try
            {
                // Toggle visibility of the measurement
                if (e.Index >= 0 && e.Index < lstMeasurements.Items.Count)
                {
                    bool newVisibility = (e.NewValue == CheckState.Checked);

                    Logger.Log($"[SharpDXControlPanel] Setting measurement {e.Index} visibility to {newVisibility}");

                    // Call the viewer to update visibility
                    viewerForm.SetMeasurementVisibility(e.Index, newVisibility);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[SharpDXControlPanel] Error toggling measurement visibility: {ex.Message}");
            }
        }

        private void InitializeRenderingTab()
        {
            // Create panel to hold controls
            FlowLayoutPanel panel = new FlowLayoutPanel();
            panel.Dock = DockStyle.Fill;
            panel.FlowDirection = FlowDirection.TopDown;
            panel.AutoScroll = true;
            panel.Padding = new Padding(10);
            panel.WrapContents = false;

            // Add title label
            Label lblTitle = new Label();
            lblTitle.Text = "Volume Rendering Controls";
            lblTitle.Font = new Font(lblTitle.Font.FontFamily, 10, FontStyle.Bold);
            lblTitle.AutoSize = true;
            lblTitle.Margin = new Padding(0, 0, 0, 10);
            panel.Controls.Add(lblTitle);

            // Debug mode checkbox
            chkDebugMode = new CheckBox();
            chkDebugMode.Text = "Debug Mode (Wireframe)";
            chkDebugMode.Checked = false;
            chkDebugMode.CheckedChanged += (s, e) =>
            {
                viewerForm.SetDebugMode(chkDebugMode.Checked);
            };
            panel.Controls.Add(chkDebugMode);
            // LOD system checkbox
            CheckBox chkUseLod = new CheckBox();
            chkUseLod.Text = "Use LOD While Moving (Faster)";
            chkUseLod.Checked = true; // Default to enabled
            chkUseLod.CheckedChanged += (s, e) =>
            {
                viewerForm.SetLodEnabled(chkUseLod.Checked);
            };
            panel.Controls.Add(chkUseLod);
            // Show grayscale checkbox
            chkShowGrayscale = new CheckBox();
            chkShowGrayscale.Text = "Show Grayscale Volume";
            chkShowGrayscale.Checked = true;
            chkShowGrayscale.CheckedChanged += (s, e) =>
            {
                viewerForm.SetGrayscaleVisible(chkShowGrayscale.Checked);
            };
            panel.Controls.Add(chkShowGrayscale);
            CheckBox chkUseStreaming = new CheckBox();
            chkUseStreaming.Text = "Use Streaming Renderer (for huge datasets)";
            chkUseStreaming.Checked = volumeRenderer.UseStreamingRenderer; // Get current state
            chkUseStreaming.CheckedChanged += (s, e) =>
            {
                viewerForm.SetStreamingRendererEnabled(chkUseStreaming.Checked);
            };
            panel.Controls.Add(chkUseStreaming);
            GroupBox grpHugeVolumes = new GroupBox();
            grpHugeVolumes.Text = "Large Volume Optimization";
            grpHugeVolumes.Width = 350;
            grpHugeVolumes.Height = 140;
            grpHugeVolumes.Margin = new Padding(0, 10, 0, 5);

            // Add a description label
            Label lblHugeVolumes = new Label();
            lblHugeVolumes.Text = "For very large datasets (30GB+), reduce resolution to improve performance:";
            lblHugeVolumes.Location = new Point(10, 20);
            lblHugeVolumes.Width = 330;
            lblHugeVolumes.Height = 30;
            grpHugeVolumes.Controls.Add(lblHugeVolumes);

            // Add a "Downsample" button
            Button btnDownsample = new Button();
            btnDownsample.Text = "Apply Downsampling (1/2 Resolution)";
            btnDownsample.Location = new Point(10, 55);
            btnDownsample.Width = 250;
            btnDownsample.Click += async (s, e) =>
            {
                // Confirm with user
                DialogResult result = MessageBox.Show(
                    "This will reduce the dataset resolution by half to save memory.\n" +
                    "Loading will take a moment but should be much faster than full resolution.\n\n" +
                    "Continue?",
                    "Apply Downsampling",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (result == DialogResult.Yes)
                {
                    btnDownsample.Enabled = false;
                    lblStatus.Text = "Applying downsampling...";
                    progress.Value = 0;
                    progress.Visible = true;
                    Application.DoEvents();

                    try
                    {
                        // Implement progress reporting
                        var progressHandler = new Progress<int>(value =>
                        {
                            progress.Value = value;
                            lblStatus.Text = $"Downsampling... {value}%";
                            Application.DoEvents();
                        });

                        // Call the downsampling function
                        await viewerForm.ApplyDownsampling(2, progressHandler);
                        lblStatus.Text = "Downsampling complete. Memory usage reduced.";
                    }
                    catch (Exception ex)
                    {
                        lblStatus.Text = "Error applying downsampling.";
                        MessageBox.Show("Error: " + ex.Message, "Downsampling Error",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    finally
                    {
                        progress.Visible = false;
                        btnDownsample.Enabled = true;
                    }
                }
            };
            grpHugeVolumes.Controls.Add(btnDownsample);

            // Add a higher downsampling button for extremely large datasets
            Button btnHigherDownsample = new Button();
            btnHigherDownsample.Text = "Apply Aggressive Downsampling (1/4 Resolution)";
            btnHigherDownsample.Location = new Point(10, 85);
            btnHigherDownsample.Width = 300;
            btnHigherDownsample.Click += async (s, e) =>
            {
                // Confirm with user
                DialogResult result = MessageBox.Show(
                    "This will reduce the dataset resolution to 1/4 for extremely large datasets.\n" +
                    "Quality will be reduced but memory usage will be dramatically lower.\n\n" +
                    "Continue?",
                    "Apply Aggressive Downsampling",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (result == DialogResult.Yes)
                {
                    btnHigherDownsample.Enabled = false;
                    lblStatus.Text = "Applying aggressive downsampling...";
                    progress.Value = 0;
                    progress.Visible = true;
                    Application.DoEvents();

                    try
                    {
                        var progressHandler = new Progress<int>(value =>
                        {
                            progress.Value = value;
                            lblStatus.Text = $"Downsampling... {value}%";
                            Application.DoEvents();
                        });

                        await viewerForm.ApplyDownsampling(4, progressHandler);
                        lblStatus.Text = "Aggressive downsampling complete.";
                    }
                    catch (Exception ex)
                    {
                        lblStatus.Text = "Error applying downsampling.";
                        MessageBox.Show("Error: " + ex.Message, "Downsampling Error",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    finally
                    {
                        progress.Visible = false;
                        btnHigherDownsample.Enabled = true;
                    }
                }
            };
            grpHugeVolumes.Controls.Add(btnHigherDownsample);

            // Add explanation label
            Label lblStreaming = new Label();
            lblStreaming.Text = "Streaming mode progressively loads volume chunks as needed.\nRecommended for datasets larger than 8GB.";
            lblStreaming.AutoSize = true;
            lblStreaming.Font = new Font(lblStreaming.Font.FontFamily, lblStreaming.Font.Size, FontStyle.Italic);
            lblStreaming.ForeColor = Color.DarkGray;
            panel.Controls.Add(lblStreaming);
            // Quality dropdown
            Label lblQuality = new Label();
            lblQuality.Text = "Rendering Quality:";
            lblQuality.AutoSize = true;
            lblQuality.Margin = new Padding(0, 10, 0, 5);
            panel.Controls.Add(lblQuality);

            cmbQuality = new ComboBox();
            cmbQuality.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbQuality.Items.AddRange(new object[] { "Low (Faster)", "Medium", "High (Slower)" });
            cmbQuality.SelectedIndex = 1; // Default to medium
            cmbQuality.Width = 200;
            cmbQuality.SelectedIndexChanged += async (s, e) =>
            {
                await viewerForm.ApplyThresholdAndRender(
                    trkMinThreshold.Value,
                    trkMaxThreshold.Value,
                    cmbQuality.SelectedIndex);
            };
            panel.Controls.Add(cmbQuality);

            // Color map dropdown
            Label lblColorMap = new Label();
            lblColorMap.Text = "Color Map:";
            lblColorMap.AutoSize = true;
            lblColorMap.Margin = new Padding(0, 10, 0, 5);
            panel.Controls.Add(lblColorMap);

            cmbColorMap = new ComboBox();
            cmbColorMap.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbColorMap.Items.AddRange(new object[] { "Grayscale", "Hot", "Cool", "Rainbow" });
            cmbColorMap.SelectedIndex = 0; // Default to grayscale
            cmbColorMap.Width = 200;
            cmbColorMap.SelectedIndexChanged += (s, e) =>
            {
                // Set the color map index in the renderer
                viewerForm.SetColorMap(cmbColorMap.SelectedIndex);
            };
            panel.Controls.Add(cmbColorMap);

            // Threshold group
            GroupBox grpThreshold = new GroupBox();
            grpThreshold.Text = "Grayscale Threshold Range";
            grpThreshold.Width = 350;
            grpThreshold.Height = 160; // Increased height to avoid overlap
            grpThreshold.Margin = new Padding(0, 10, 0, 10);

            // Min threshold
            Label lblMinThreshold = new Label();
            lblMinThreshold.Text = "Min:";
            lblMinThreshold.AutoSize = true;
            lblMinThreshold.Location = new Point(10, 25);
            grpThreshold.Controls.Add(lblMinThreshold);

            trkMinThreshold = new TrackBar();
            trkMinThreshold.Minimum = 0;
            trkMinThreshold.Maximum = 255;
            trkMinThreshold.Value = 30;
            trkMinThreshold.TickFrequency = 16;
            trkMinThreshold.LargeChange = 16;
            trkMinThreshold.Width = 250;
            trkMinThreshold.Location = new Point(40, 20);
            trkMinThreshold.Scroll += (s, e) =>
            {
                numMinThreshold.Value = trkMinThreshold.Value;
                OnThresholdChanged();
            };
            grpThreshold.Controls.Add(trkMinThreshold);

            numMinThreshold = new NumericUpDown();
            numMinThreshold.Minimum = 0;
            numMinThreshold.Maximum = 255;
            numMinThreshold.Value = 30;
            numMinThreshold.Width = 55;
            numMinThreshold.Location = new Point(295, 25);
            numMinThreshold.ValueChanged += (s, e) =>
            {
                if (numMinThreshold.Value > numMaxThreshold.Value)
                    numMinThreshold.Value = numMaxThreshold.Value;

                trkMinThreshold.Value = (int)numMinThreshold.Value;
                OnThresholdChanged();
            };
            grpThreshold.Controls.Add(numMinThreshold);

            // Max threshold - moved lower to avoid overlap
            Label lblMaxThreshold = new Label();
            lblMaxThreshold.Text = "Max:";
            lblMaxThreshold.AutoSize = true;
            lblMaxThreshold.Location = new Point(10, 85); // Increased Y position
            grpThreshold.Controls.Add(lblMaxThreshold);

            trkMaxThreshold = new TrackBar();
            trkMaxThreshold.Minimum = 0;
            trkMaxThreshold.Maximum = 255;
            trkMaxThreshold.Value = 200;
            trkMaxThreshold.TickFrequency = 16;
            trkMaxThreshold.LargeChange = 16;
            trkMaxThreshold.Width = 250;
            trkMaxThreshold.Location = new Point(40, 80); // Increased Y position
            trkMaxThreshold.Scroll += (s, e) =>
            {
                numMaxThreshold.Value = trkMaxThreshold.Value;
                OnThresholdChanged();
            };
            grpThreshold.Controls.Add(trkMaxThreshold);

            numMaxThreshold = new NumericUpDown();
            numMaxThreshold.Minimum = 0;
            numMaxThreshold.Maximum = 255;
            numMaxThreshold.Value = 200;
            numMaxThreshold.Width = 55;
            numMaxThreshold.Location = new Point(295, 85); // Increased Y position
            numMaxThreshold.ValueChanged += (s, e) =>
            {
                if (numMaxThreshold.Value < numMinThreshold.Value)
                    numMaxThreshold.Value = numMinThreshold.Value;

                trkMaxThreshold.Value = (int)numMaxThreshold.Value;
                OnThresholdChanged();
            };
            grpThreshold.Controls.Add(numMaxThreshold);

            panel.Controls.Add(grpThreshold);

            // Export buttons
            btnScreenshot = new Button();
            btnScreenshot.Text = "Take Screenshot";
            btnScreenshot.Width = 200;
            btnScreenshot.Click += (s, e) =>
            {
                using (var saveDlg = new SaveFileDialog())
                {
                    saveDlg.Filter = "JPEG Image (*.jpg)|*.jpg|PNG Image (*.png)|*.png|All Files (*.*)|*.*";
                    saveDlg.DefaultExt = ".jpg";
                    saveDlg.FileName = $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}";

                    if (saveDlg.ShowDialog(this) == DialogResult.OK)
                    {
                        // Show "Saving..." message
                        lblStatus.Text = "Saving screenshot...";
                        Application.DoEvents(); // Update UI

                        try
                        {
                            viewerForm.TakeScreenshot(saveDlg.FileName);
                            lblStatus.Text = "Screenshot saved successfully.";
                        }
                        catch (Exception ex)
                        {
                            lblStatus.Text = "Error saving screenshot.";
                            MessageBox.Show($"Error saving screenshot: {ex.Message}", "Screenshot Error",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    }
                }
            };
            panel.Controls.Add(btnScreenshot);

            btnExportModel = new Button();
            btnExportModel.Text = "Export 3D Model...";
            btnExportModel.Width = 200;
            btnExportModel.Margin = new Padding(0, 10, 0, 10);
            btnExportModel.Click += async (s, e) =>
            {
                using (var saveDlg = new SaveFileDialog())
                {
                    saveDlg.Filter = "OBJ files (*.obj)|*.obj|STL files (*.stl)|*.stl";
                    saveDlg.FileName = "ct_model.obj";
                    if (saveDlg.ShowDialog(this) == DialogResult.OK)
                    {
                        bool isObj = Path.GetExtension(saveDlg.FileName).ToLower() == ".obj";

                        // Get export options
                        bool exportLabels = true;
                        bool exportGrayscaleSurface = true;
                        float isoLevel = 120.0f; // Default iso-level for grayscale

                        // Confirm with dialog
                        var result = MessageBox.Show(
                            "Export both segmented materials and grayscale volume?\n\n" +
                            "Yes - Export both\nNo - Export only materials\nCancel - Cancel export",
                            "Export Options",
                            MessageBoxButtons.YesNoCancel,
                            MessageBoxIcon.Question);

                        if (result == DialogResult.Cancel)
                            return;

                        exportGrayscaleSurface = (result == DialogResult.Yes);

                        // Update status before export
                        lblStatus.Text = "Preparing to export...";
                        progress.Value = 0;
                        progress.Visible = true;

                        // Setup progress reporting
                        var progressHandler = new Progress<int>(value =>
                        {
                            progress.Value = value;
                            lblStatus.Text = $"Exporting 3D model... {value}%";
                            Application.DoEvents(); // Allow UI to refresh
                        });

                        Application.DoEvents();

                        // Disable UI during export
                        this.Enabled = false;

                        try
                        {
                            await viewerForm.ExportModelAsync(
                                exportLabels,
                                exportGrayscaleSurface,
                                saveDlg.FileName,
                                isoLevel,
                                progressHandler);

                            lblStatus.Text = "Export complete.";
                        }
                        catch (Exception ex)
                        {
                            lblStatus.Text = "Export failed.";
                            MessageBox.Show($"Error exporting model: {ex.Message}", "Export Error",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                        finally
                        {
                            this.Enabled = true;
                            progress.Visible = false;
                        }
                    }
                }
            };
            panel.Controls.Add(btnExportModel);

            // Progress bar and status label
            progress = new ProgressBar();
            progress.Width = 330;
            progress.Height = 20;
            progress.Visible = false;
            panel.Controls.Add(progress);

            lblStatus = new Label();
            lblStatus.Text = "Ready.";
            lblStatus.AutoSize = true;
            panel.Controls.Add(lblStatus);

            tabRendering.Controls.Add(panel);
        }

        private void OnThresholdChanged()
        {
            // Store pending threshold values
            pendingMinThreshold = trkMinThreshold.Value;
            pendingMaxThreshold = trkMaxThreshold.Value;
            pendingQualityIndex = cmbQuality.SelectedIndex;
            thresholdUpdatePending = true;

            // Update status immediately to show feedback
            lblStatus.Text = "Waiting to update...";

            // Restart the timer to delay the update
            thresholdUpdateTimer.Stop();
            thresholdUpdateTimer.Start();
        }

        private void InitializeMaterialsTab()
        {
            // Create panel to hold controls
            FlowLayoutPanel panel = new FlowLayoutPanel();
            panel.Dock = DockStyle.Fill;
            panel.FlowDirection = FlowDirection.TopDown;
            panel.AutoScroll = true;
            panel.Padding = new Padding(10);
            panel.WrapContents = false;

            // Add title label
            Label lblTitle = new Label();
            lblTitle.Text = "Materials Visibility & Opacity";
            lblTitle.Font = new Font(lblTitle.Font.FontFamily, 10, FontStyle.Bold);
            lblTitle.AutoSize = true;
            lblTitle.Margin = new Padding(0, 0, 0, 10);
            panel.Controls.Add(lblTitle);

            // Materials list
            Label lblMaterials = new Label();
            lblMaterials.Text = "Materials (Labels):";
            lblMaterials.AutoSize = true;
            panel.Controls.Add(lblMaterials);

            lstMaterials = new CheckedListBox();
            lstMaterials.CheckOnClick = true;
            lstMaterials.Width = 330;
            lstMaterials.Height = 220;
            lstMaterials.BorderStyle = BorderStyle.FixedSingle;

            // Enable owner-draw to show each material's color
            lstMaterials.DrawMode = DrawMode.OwnerDrawFixed;
            lstMaterials.DrawItem += LstMaterials_DrawItem;

            // Populate from mainForm.Materials
            if (mainForm.volumeLabels != null && mainForm.Materials != null)
            {
                for (int i = 0; i < mainForm.Materials.Count; i++)
                {
                    Material mat = mainForm.Materials[i];

                    // For visibility, can ask the viewer if the material is currently visible:
                    bool currentlyVisible = viewerForm.GetMaterialVisibility(mat.ID);

                    // Add the actual 'Material' object as the item
                    lstMaterials.Items.Add(mat, currentlyVisible);
                }
            }

            // When user toggles a checkbox, update the material's visibility in the 3D viewer:
            lstMaterials.ItemCheck += (s, e) =>
            {
                if (e.Index < 0 || e.Index >= lstMaterials.Items.Count)
                    return;

                Material mat = (Material)lstMaterials.Items[e.Index];
                bool isChecked = (e.NewValue == CheckState.Checked);

                // Debug output to verify the event is firing
                Logger.Log($"[SharpDXControlPanel] Material visibility changed: {mat.Name} (ID: {mat.ID}) to {isChecked}");

                // Direct call to volumeRenderer instead of through viewerForm
                if (volumeRenderer != null)
                {
                    // Set visibility directly on the renderer
                    volumeRenderer.SetMaterialVisibility(mat.ID, isChecked);
                    volumeRenderer.NeedsRender = true;
                }
                else
                {
                    // Fallback to viewerForm if volumeRenderer is not available
                    viewerForm.SetMaterialVisibility(mat.ID, isChecked);
                }
            };

            // When user selects a material, update the opacity slider to show its current opacity
            lstMaterials.SelectedIndexChanged += (s, e) =>
            {
                int idx = lstMaterials.SelectedIndex;
                if (idx < 0 || idx >= lstMaterials.Items.Count) return;

                Material mat = (Material)lstMaterials.Items[idx];

                // Convert to 0..100 range for the slider
                float currentAlpha = viewerForm.GetMaterialOpacity(mat.ID);
                trkOpacity.Value = (int)Math.Round(currentAlpha * 100f);
            };

            panel.Controls.Add(lstMaterials);

            // Opacity controls
            lblOpacity = new Label();
            lblOpacity.Text = "Material Opacity:";
            lblOpacity.AutoSize = true;
            lblOpacity.Margin = new Padding(0, 10, 0, 5);
            panel.Controls.Add(lblOpacity);

            trkOpacity = new TrackBar();
            trkOpacity.Minimum = 0;
            trkOpacity.Maximum = 100;
            trkOpacity.Value = 100;
            trkOpacity.TickFrequency = 10;
            trkOpacity.Width = 330;
            trkOpacity.Scroll += (s, e) =>
            {
                // Adjust currently selected material's opacity
                int idx = lstMaterials.SelectedIndex;
                if (idx < 0) return;

                Material mat = (Material)lstMaterials.Items[idx];
                float alpha = trkOpacity.Value / 100f;

                // Store the pending update values
                pendingMaterialId = mat.ID;
                pendingOpacity = alpha;
                opacityUpdatePending = true;

                // Restart the timer to delay the update
                opacityUpdateTimer.Stop();
                opacityUpdateTimer.Start();
            };
            panel.Controls.Add(trkOpacity);

            tabMaterials.Controls.Add(panel);
        }

        private void LstMaterials_DrawItem(object sender, DrawItemEventArgs e)
        {
            e.DrawBackground();

            // Validate the index
            if (e.Index < 0 || e.Index >= lstMaterials.Items.Count)
                return;

            // Get material from the item
            Material mat = (Material)lstMaterials.Items[e.Index];

            // Fill the background with the material's color
            using (SolidBrush b = new SolidBrush(mat.Color))
                e.Graphics.FillRectangle(b, e.Bounds);

            // Decide on a text color for contrast
            Color textColor = (mat.Color.GetBrightness() < 0.4f) ? Color.White : Color.Black;

            // Draw the text (the material's Name)
            TextRenderer.DrawText(
                e.Graphics,
                $"{mat.Name} (ID: {mat.ID})",
                e.Font,
                e.Bounds.Location,
                textColor);

            e.DrawFocusRectangle();
        }

        private void InitializeSlicesTab()
        {
            // Create panel to hold controls
            FlowLayoutPanel panel = new FlowLayoutPanel();
            panel.Dock = DockStyle.Fill;
            panel.FlowDirection = FlowDirection.TopDown;
            panel.AutoScroll = true;
            panel.Padding = new Padding(10);
            panel.WrapContents = false;

            // Add title label
            Label lblTitle = new Label();
            lblTitle.Text = "Orthogonal Slice Controls";
            lblTitle.Font = new Font(lblTitle.Font.FontFamily, 10, FontStyle.Bold);
            lblTitle.AutoSize = true;
            lblTitle.Margin = new Padding(0, 0, 0, 10);
            panel.Controls.Add(lblTitle);

            // Enable/disable all slices (master control)
            chkSlices = new CheckBox();
            chkSlices.Text = "Enable All Orthogonal Slices";
            chkSlices.Checked = false;
            chkSlices.CheckedChanged += (s, e) =>
            {
                // Set all individual slices to match
                chkSliceX.Checked = chkSlices.Checked;
                chkSliceY.Checked = chkSlices.Checked;
                chkSliceZ.Checked = chkSlices.Checked;

                // Update the viewer
                viewerForm.SetSlicesEnabled(chkSlices.Checked);

                // Sliders and labels will be managed by the individual checkboxes
            };
            panel.Controls.Add(chkSlices);

            // Add individual slice controls
            Label lblIndividualSlices = new Label();
            lblIndividualSlices.Text = "Individual Slice Controls:";
            lblIndividualSlices.AutoSize = true;
            lblIndividualSlices.Margin = new Padding(0, 10, 0, 5);
            panel.Controls.Add(lblIndividualSlices);

            // Add colored indicators for each slice direction
            Label lblSliceColors = new Label();
            lblSliceColors.Text = "Slice Direction Colors:";
            lblSliceColors.AutoSize = true;
            lblSliceColors.Margin = new Padding(0, 10, 0, 5);
            panel.Controls.Add(lblSliceColors);

            // Create a panel to hold the color indicators
            TableLayoutPanel colorPanel = new TableLayoutPanel();
            colorPanel.ColumnCount = 3;
            colorPanel.RowCount = 1;
            colorPanel.Width = 330;
            colorPanel.Height = 30;
            colorPanel.Margin = new Padding(0, 0, 0, 10);

            // X slice indicator (red)
            Panel xColorPanel = new Panel();
            xColorPanel.BackColor = Color.Red;
            xColorPanel.Dock = DockStyle.Fill;
            Label xLabel = new Label();
            xLabel.Text = "X Slice";
            xLabel.ForeColor = Color.White;
            xLabel.TextAlign = ContentAlignment.MiddleCenter;
            xLabel.Dock = DockStyle.Fill;
            xColorPanel.Controls.Add(xLabel);
            colorPanel.Controls.Add(xColorPanel, 0, 0);

            // Y slice indicator (green)
            Panel yColorPanel = new Panel();
            yColorPanel.BackColor = Color.Green;
            yColorPanel.Dock = DockStyle.Fill;
            Label yLabel = new Label();
            yLabel.Text = "Y Slice";
            yLabel.ForeColor = Color.White;
            yLabel.TextAlign = ContentAlignment.MiddleCenter;
            yLabel.Dock = DockStyle.Fill;
            yColorPanel.Controls.Add(yLabel);
            colorPanel.Controls.Add(yColorPanel, 1, 0);

            // Z slice indicator (blue)
            Panel zColorPanel = new Panel();
            zColorPanel.BackColor = Color.Blue;
            zColorPanel.Dock = DockStyle.Fill;
            Label zLabel = new Label();
            zLabel.Text = "Z Slice";
            zLabel.ForeColor = Color.White;
            zLabel.TextAlign = ContentAlignment.MiddleCenter;
            zLabel.Dock = DockStyle.Fill;
            zColorPanel.Controls.Add(zLabel);
            colorPanel.Controls.Add(zColorPanel, 2, 0);

            panel.Controls.Add(colorPanel);

            // X Slice Control (YZ plane) - Individual CheckBox
            chkSliceX = new CheckBox();
            chkSliceX.Text = "Show X Slice (YZ plane - Red)";
            chkSliceX.Checked = viewerForm.GetSliceXEnabled();
            chkSliceX.CheckedChanged += OnIndividualSliceCheckChanged;
            panel.Controls.Add(chkSliceX);

            // Add the value label
            lblXSliceValue = new Label();
            lblXSliceValue.Text = $"Slice: {trkXSlice?.Value ?? 0}/{trkXSlice?.Maximum ?? 0}";
            lblXSliceValue.AutoSize = true;
            lblXSliceValue.Enabled = chkSliceX.Checked;
            lblXSliceValue.TextAlign = ContentAlignment.MiddleRight;
            panel.Controls.Add(lblXSliceValue);

            trkXSlice = new TrackBar();
            trkXSlice.Minimum = 0;
            trkXSlice.Maximum = Math.Max(0, mainForm.GetWidth() - 1);
            trkXSlice.Value = trkXSlice.Maximum / 2;
            trkXSlice.TickFrequency = Math.Max(1, mainForm.GetWidth() / 20);
            trkXSlice.Width = 330;
            trkXSlice.Enabled = chkSliceX.Checked;
            trkXSlice.Scroll += (s, e) =>
            {
                UpdateSlices();
                lblXSliceValue.Text = $"Slice: {trkXSlice.Value}/{trkXSlice.Maximum}";
            };
            panel.Controls.Add(trkXSlice);

            // Y Slice Control (XZ plane) - Individual CheckBox
            chkSliceY = new CheckBox();
            chkSliceY.Text = "Show Y Slice (XZ plane - Green)";
            chkSliceY.Checked = viewerForm.GetSliceYEnabled();
            chkSliceY.CheckedChanged += OnIndividualSliceCheckChanged;
            panel.Controls.Add(chkSliceY);

            // Add the value label
            lblYSliceValue = new Label();
            lblYSliceValue.Text = $"Slice: {trkYSlice?.Value ?? 0}/{trkYSlice?.Maximum ?? 0}";
            lblYSliceValue.AutoSize = true;
            lblYSliceValue.Enabled = chkSliceY.Checked;
            panel.Controls.Add(lblYSliceValue);

            trkYSlice = new TrackBar();
            trkYSlice.Minimum = 0;
            trkYSlice.Maximum = Math.Max(0, mainForm.GetHeight() - 1);
            trkYSlice.Value = trkYSlice.Maximum / 2;
            trkYSlice.TickFrequency = Math.Max(1, mainForm.GetHeight() / 20);
            trkYSlice.Width = 330;
            trkYSlice.Enabled = chkSliceY.Checked;
            trkYSlice.Scroll += (s, e) =>
            {
                UpdateSlices();
                lblYSliceValue.Text = $"Slice: {trkYSlice.Value}/{trkYSlice.Maximum}";
            };
            panel.Controls.Add(trkYSlice);

            // Z Slice Control (XY plane) - Individual CheckBox
            chkSliceZ = new CheckBox();
            chkSliceZ.Text = "Show Z Slice (XY plane - Blue)";
            chkSliceZ.Checked = viewerForm.GetSliceZEnabled();
            chkSliceZ.CheckedChanged += OnIndividualSliceCheckChanged;
            panel.Controls.Add(chkSliceZ);

            // Add the value label
            lblZSliceValue = new Label();
            lblZSliceValue.Text = $"Slice: {trkZSlice?.Value ?? 0}/{trkZSlice?.Maximum ?? 0}";
            lblZSliceValue.AutoSize = true;
            lblZSliceValue.Enabled = chkSliceZ.Checked;
            panel.Controls.Add(lblZSliceValue);

            trkZSlice = new TrackBar();
            trkZSlice.Minimum = 0;
            trkZSlice.Maximum = Math.Max(0, mainForm.GetDepth() - 1);
            trkZSlice.Value = trkZSlice.Maximum / 2;
            trkZSlice.TickFrequency = Math.Max(1, mainForm.GetDepth() / 20);
            trkZSlice.Width = 330;
            trkZSlice.Enabled = chkSliceZ.Checked;
            trkZSlice.Scroll += (s, e) =>
            {
                UpdateSlices();
                lblZSliceValue.Text = $"Slice: {trkZSlice.Value}/{trkZSlice.Maximum}";
            };
            panel.Controls.Add(trkZSlice);

            // "Reset to center" button
            Button btnResetSlices = new Button();
            btnResetSlices.Text = "Reset Slices to Center";
            btnResetSlices.Width = 180;
            btnResetSlices.Click += (s, e) =>
            {
                // Set each slider to its midpoint
                trkXSlice.Value = trkXSlice.Maximum / 2;
                trkYSlice.Value = trkYSlice.Maximum / 2;
                trkZSlice.Value = trkZSlice.Maximum / 2;

                // Update labels
                lblXSliceValue.Text = $"Slice: {trkXSlice.Value}/{trkXSlice.Maximum}";
                lblYSliceValue.Text = $"Slice: {trkYSlice.Value}/{trkYSlice.Maximum}";
                lblZSliceValue.Text = $"Slice: {trkZSlice.Value}/{trkZSlice.Maximum}";

                UpdateSlices();
            };
            panel.Controls.Add(btnResetSlices);

            tabSlices.Controls.Add(panel);
        }

        private void UpdateMasterSliceCheckbox()
        {
            bool allChecked = chkSliceX.Checked && chkSliceY.Checked && chkSliceZ.Checked;
            RemoveAllEventHandlers(chkSlices, "CheckedChanged");
            chkSlices.Checked = allChecked;
            chkSlices.CheckedChanged += OnMasterSliceCheckChanged;
        }

        private void OnIndividualSliceCheckChanged(object sender, EventArgs e)
        {
            CheckBox checkbox = sender as CheckBox;

            // Determine which slice was changed
            if (checkbox == chkSliceX)
            {
                viewerForm.SetSliceXEnabled(checkbox.Checked);
                trkXSlice.Enabled = checkbox.Checked;
                lblXSliceValue.Enabled = checkbox.Checked;
            }
            else if (checkbox == chkSliceY)
            {
                viewerForm.SetSliceYEnabled(checkbox.Checked);
                trkYSlice.Enabled = checkbox.Checked;
                lblYSliceValue.Enabled = checkbox.Checked;
            }
            else if (checkbox == chkSliceZ)
            {
                viewerForm.SetSliceZEnabled(checkbox.Checked);
                trkZSlice.Enabled = checkbox.Checked;
                lblZSliceValue.Enabled = checkbox.Checked;
            }

            // Update master checkbox correctly
            UpdateMasterSliceCheckbox();
        }

        private void RemoveAllEventHandlers(Control control, string eventName)
        {
            try
            {
                // Use reflection to get the events field
                var fi = typeof(Control).GetField("EVENT_HANDLERLIST",
                                                 System.Reflection.BindingFlags.Static |
                                                 System.Reflection.BindingFlags.NonPublic);

                if (fi == null)
                {
                    // For newer .NET versions, the approach might be different
                    // Just do the best we can - create a new checkbox with the same properties
                    var oldCheckbox = control as CheckBox;
                    if (oldCheckbox != null)
                    {
                        var newCheckbox = new CheckBox();
                        newCheckbox.Text = oldCheckbox.Text;
                        newCheckbox.Location = oldCheckbox.Location;
                        newCheckbox.Size = oldCheckbox.Size;
                        newCheckbox.Checked = oldCheckbox.Checked;

                        var parent = oldCheckbox.Parent;
                        if (parent != null)
                        {
                            int index = parent.Controls.GetChildIndex(oldCheckbox);
                            parent.Controls.RemoveAt(index);
                            parent.Controls.Add(newCheckbox);
                            parent.Controls.SetChildIndex(newCheckbox, index);
                            chkSlices = newCheckbox;

                            // Add event handler
                            newCheckbox.CheckedChanged += OnMasterSliceCheckChanged;
                        }
                    }
                    return;
                }

                object handlerList = fi.GetValue(control);
                var property = handlerList.GetType().GetProperty("Item", System.Reflection.BindingFlags.Public |
                                                              System.Reflection.BindingFlags.Instance);
                var key = control.GetType().GetField(eventName.ToUpper(),
                                                    System.Reflection.BindingFlags.Static |
                                                    System.Reflection.BindingFlags.NonPublic).GetValue(control);

                var handlers = property.GetValue(handlerList, new object[] { key });
                if (handlers != null)
                {
                    var getInvocationListMethod = handlers.GetType().GetMethod("GetInvocationList");
                    var delegates = (Delegate[])getInvocationListMethod.Invoke(handlers, null);

                    var eventInfo = control.GetType().GetEvent(eventName);
                    foreach (var del in delegates)
                    {
                        eventInfo.RemoveEventHandler(control, del);
                    }
                }
            }
            catch (Exception ex)
            {
                // If reflection approach fails, use a simpler workaround
                Logger.Log($"[SharpDXControlPanel] Error removing event handlers: {ex.Message}");

                // Simpler approach: Just update the checkbox state without triggering event handlers
                if (control is CheckBox checkbox)
                {
                    bool currentState = checkbox.Checked;

                    // This removes the event handlers, but may be platform-dependent
                    try
                    {
                        control.Parent.Controls.Remove(control);
                        control.Parent.Controls.Add(control);

                        // Reset the state and add our new handler
                        checkbox.Checked = currentState;
                        checkbox.CheckedChanged += OnMasterSliceCheckChanged;
                    }
                    catch
                    {
                        // If that fails too, just try to reset the handler
                        try
                        {
                            // As a last resort, try to be clever with BeginInvoke
                            checkbox.BeginInvoke(new Action(() =>
                            {
                                checkbox.CheckedChanged -= OnMasterSliceCheckChanged;
                                checkbox.Checked = currentState;
                                checkbox.CheckedChanged += OnMasterSliceCheckChanged;
                            }));
                        }
                        catch
                        {
                            // If all else fails, log the issue
                            Logger.Log("[SharpDXControlPanel] Failed to reset event handlers");
                        }
                    }
                }
            }
        }

        private void OnMasterSliceCheckChanged(object sender, EventArgs e)
        {
            // Set all individual slices to match
            chkSliceX.Checked = chkSlices.Checked;
            chkSliceY.Checked = chkSlices.Checked;
            chkSliceZ.Checked = chkSlices.Checked;

            // Update the viewer
            viewerForm.SetSlicesEnabled(chkSlices.Checked);
        }

        private void InitializeCuttingTab()
        {
            // Create panel to hold controls
            FlowLayoutPanel panel = new FlowLayoutPanel();
            panel.Dock = DockStyle.Fill;
            panel.FlowDirection = FlowDirection.TopDown;
            panel.AutoScroll = true;
            panel.Padding = new Padding(10);
            panel.WrapContents = false;

            // Add title label
            Label lblTitle = new Label();
            lblTitle.Text = "Dataset Cutting Controls";
            lblTitle.Font = new Font(lblTitle.Font.FontFamily, 10, FontStyle.Bold);
            lblTitle.AutoSize = true;
            lblTitle.Margin = new Padding(0, 0, 0, 10);
            panel.Controls.Add(lblTitle);

            // Add description
            Label lblDescription = new Label();
            lblDescription.Text = "Use these controls to cut away parts of the dataset along each axis.";
            lblDescription.AutoSize = true;
            lblDescription.Width = 330;
            panel.Controls.Add(lblDescription);

            // X Cutting plane
            GroupBox grpCutX = new GroupBox();
            grpCutX.Text = "X Cutting Plane";
            grpCutX.Width = 330;
            grpCutX.Height = 120;
            grpCutX.Margin = new Padding(0, 10, 0, 5);

            // Enable checkbox
            chkCutX = new CheckBox();
            chkCutX.Text = "Enable X Cutting";
            chkCutX.Location = new Point(10, 20);
            chkCutX.CheckedChanged += (s, e) =>
            {
                bool enabled = chkCutX.Checked;
                viewerForm.SetCutXEnabled(enabled);
                radCutXForward.Enabled = enabled;
                radCutXBackward.Enabled = enabled;
                trkCutX.Enabled = enabled;
            };
            grpCutX.Controls.Add(chkCutX);

            // Direction radio buttons
            radCutXForward = new RadioButton();
            radCutXForward.Text = "Forward";
            radCutXForward.Checked = true;
            radCutXForward.Location = new Point(120, 20);
            radCutXForward.Enabled = false;
            radCutXForward.CheckedChanged += (s, e) =>
            {
                if (radCutXForward.Checked)
                    viewerForm.SetCutXDirection(1.0f);
            };
            grpCutX.Controls.Add(radCutXForward);

            radCutXBackward = new RadioButton();
            radCutXBackward.Text = "Backward";
            radCutXBackward.Location = new Point(220, 20);
            radCutXBackward.Enabled = false;
            radCutXBackward.CheckedChanged += (s, e) =>
            {
                if (radCutXBackward.Checked)
                    viewerForm.SetCutXDirection(-1.0f);
            };
            grpCutX.Controls.Add(radCutXBackward);

            // Position slider
            trkCutX = new TrackBar();
            trkCutX.Minimum = 0;
            trkCutX.Maximum = 100;
            trkCutX.Value = 50;
            trkCutX.TickFrequency = 10;
            trkCutX.Width = 310;
            trkCutX.Location = new Point(10, 50);
            trkCutX.Enabled = false;
            trkCutX.Scroll += (s, e) =>
            {
                viewerForm.SetCutXPosition(trkCutX.Value / 100.0f);
            };
            grpCutX.Controls.Add(trkCutX);

            panel.Controls.Add(grpCutX);

            // Y Cutting plane
            GroupBox grpCutY = new GroupBox();
            grpCutY.Text = "Y Cutting Plane";
            grpCutY.Width = 330;
            grpCutY.Height = 120;
            grpCutY.Margin = new Padding(0, 5, 0, 5);

            // Enable checkbox
            chkCutY = new CheckBox();
            chkCutY.Text = "Enable Y Cutting";
            chkCutY.Location = new Point(10, 20);
            chkCutY.CheckedChanged += (s, e) =>
            {
                bool enabled = chkCutY.Checked;
                viewerForm.SetCutYEnabled(enabled);
                radCutYForward.Enabled = enabled;
                radCutYBackward.Enabled = enabled;
                trkCutY.Enabled = enabled;
            };
            grpCutY.Controls.Add(chkCutY);

            // Direction radio buttons
            radCutYForward = new RadioButton();
            radCutYForward.Text = "Forward";
            radCutYForward.Checked = true;
            radCutYForward.Location = new Point(120, 20);
            radCutYForward.Enabled = false;
            radCutYForward.CheckedChanged += (s, e) =>
            {
                if (radCutYForward.Checked)
                    viewerForm.SetCutYDirection(1.0f);
            };
            grpCutY.Controls.Add(radCutYForward);

            radCutYBackward = new RadioButton();
            radCutYBackward.Text = "Backward";
            radCutYBackward.Location = new Point(220, 20);
            radCutYBackward.Enabled = false;
            radCutYBackward.CheckedChanged += (s, e) =>
            {
                if (radCutYBackward.Checked)
                    viewerForm.SetCutYDirection(-1.0f);
            };
            grpCutY.Controls.Add(radCutYBackward);

            // Position slider
            trkCutY = new TrackBar();
            trkCutY.Minimum = 0;
            trkCutY.Maximum = 100;
            trkCutY.Value = 50;
            trkCutY.TickFrequency = 10;
            trkCutY.Width = 310;
            trkCutY.Location = new Point(10, 50);
            trkCutY.Enabled = false;
            trkCutY.Scroll += (s, e) =>
            {
                viewerForm.SetCutYPosition(trkCutY.Value / 100.0f);
            };
            grpCutY.Controls.Add(trkCutY);

            panel.Controls.Add(grpCutY);

            // Z Cutting plane
            GroupBox grpCutZ = new GroupBox();
            grpCutZ.Text = "Z Cutting Plane";
            grpCutZ.Width = 330;
            grpCutZ.Height = 120;
            grpCutZ.Margin = new Padding(0, 5, 0, 5);

            // Enable checkbox
            chkCutZ = new CheckBox();
            chkCutZ.Text = "Enable Z Cutting";
            chkCutZ.Location = new Point(10, 20);
            chkCutZ.CheckedChanged += (s, e) =>
            {
                bool enabled = chkCutZ.Checked;
                viewerForm.SetCutZEnabled(enabled);
                radCutZForward.Enabled = enabled;
                radCutZBackward.Enabled = enabled;
                trkCutZ.Enabled = enabled;
            };
            grpCutZ.Controls.Add(chkCutZ);

            // Direction radio buttons
            radCutZForward = new RadioButton();
            radCutZForward.Text = "Forward";
            radCutZForward.Checked = true;
            radCutZForward.Location = new Point(120, 20);
            radCutZForward.Enabled = false;
            radCutZForward.CheckedChanged += (s, e) =>
            {
                if (radCutZForward.Checked)
                    viewerForm.SetCutZDirection(1.0f);
            };
            grpCutZ.Controls.Add(radCutZForward);

            radCutZBackward = new RadioButton();
            radCutZBackward.Text = "Backward";
            radCutZBackward.Location = new Point(220, 20);
            radCutZBackward.Enabled = false;
            radCutZBackward.CheckedChanged += (s, e) =>
            {
                if (radCutZBackward.Checked)
                    viewerForm.SetCutZDirection(-1.0f);
            };
            grpCutZ.Controls.Add(radCutZBackward);

            // Position slider
            trkCutZ = new TrackBar();
            trkCutZ.Minimum = 0;
            trkCutZ.Maximum = 100;
            trkCutZ.Value = 50;
            trkCutZ.TickFrequency = 10;
            trkCutZ.Width = 310;
            trkCutZ.Location = new Point(10, 50);
            trkCutZ.Enabled = false;
            trkCutZ.Scroll += (s, e) =>
            {
                viewerForm.SetCutZPosition(trkCutZ.Value / 100.0f);
            };
            grpCutZ.Controls.Add(trkCutZ);

            panel.Controls.Add(grpCutZ);

            // Reset button
            Button btnResetCuts = new Button();
            btnResetCuts.Text = "Reset All Cuts";
            btnResetCuts.Width = 150;
            btnResetCuts.Click += (s, e) =>
            {
                // Reset all cutting planes
                chkCutX.Checked = false;
                chkCutY.Checked = false;
                chkCutZ.Checked = false;
                trkCutX.Value = 50;
                trkCutY.Value = 50;
                trkCutZ.Value = 50;
                radCutXForward.Checked = true;
                radCutYForward.Checked = true;
                radCutZForward.Checked = true;
                viewerForm.ResetAllCuts();
            };
            panel.Controls.Add(btnResetCuts);

            tabCutting.Controls.Add(panel);
        }

        private void InitializeInfoTab()
        {
            // Create panel to hold controls
            FlowLayoutPanel panel = new FlowLayoutPanel();
            panel.Dock = DockStyle.Fill;
            panel.FlowDirection = FlowDirection.TopDown;
            panel.AutoScroll = true;
            panel.Padding = new Padding(10);
            panel.WrapContents = false;

            // Add title label
            Label lblTitle = new Label();
            lblTitle.Text = "Dataset Information";
            lblTitle.Font = new Font(lblTitle.Font.FontFamily, 10, FontStyle.Bold);
            lblTitle.AutoSize = true;
            lblTitle.Margin = new Padding(0, 0, 0, 15);
            panel.Controls.Add(lblTitle);

            // Volume info
            lblVolumeInfo = new Label();
            lblVolumeInfo.Text = "Volume Information:";
            lblVolumeInfo.AutoSize = true;
            panel.Controls.Add(lblVolumeInfo);

            // Get volume info
            int width = mainForm.GetWidth();
            int height = mainForm.GetHeight();
            int depth = mainForm.GetDepth();
            double pixelSize = mainForm.GetPixelSize();

            // Create infobox
            TextBox txtVolumeInfo = new TextBox();
            txtVolumeInfo.Multiline = true;
            txtVolumeInfo.ReadOnly = true;
            txtVolumeInfo.ScrollBars = ScrollBars.Vertical;
            txtVolumeInfo.Width = 330;
            txtVolumeInfo.Height = 120;
            txtVolumeInfo.Text = $"Dimensions: {width} × {height} × {depth} voxels\r\n";
            txtVolumeInfo.Text += $"Volume: {width * height * depth:N0} voxels total\r\n";

            // Calculate volume in real-world units
            double voxelVolume = pixelSize * pixelSize * pixelSize;
            double totalVolumeCubicMm = voxelVolume * width * height * depth * 1e9; // in mm³

            txtVolumeInfo.Text += $"Pixel Size: {FormatSize(pixelSize)}\r\n";
            txtVolumeInfo.Text += $"Physical Size: {FormatSize(width * pixelSize)} × " +
                                 $"{FormatSize(height * pixelSize)} × " +
                                 $"{FormatSize(depth * pixelSize)}\r\n";
            txtVolumeInfo.Text += $"Total Volume: {totalVolumeCubicMm:N2} mm³";

            panel.Controls.Add(txtVolumeInfo);

            // Material info
            lblMaterialsInfo = new Label();
            lblMaterialsInfo.Text = "Materials Information:";
            lblMaterialsInfo.AutoSize = true;
            lblMaterialsInfo.Margin = new Padding(0, 15, 0, 5);
            panel.Controls.Add(lblMaterialsInfo);

            // Create infobox
            ListBox lstMatInfo = new ListBox();
            lstMatInfo.Width = 330;
            lstMatInfo.Height = 150;
            lstMatInfo.DrawMode = DrawMode.OwnerDrawFixed;
            lstMatInfo.DrawItem += (s, e) =>
            {
                e.DrawBackground();

                if (e.Index < 0 || e.Index >= mainForm.Materials.Count)
                    return;

                Material mat = mainForm.Materials[e.Index];

                // Draw color block
                Rectangle colorRect = new Rectangle(
                    e.Bounds.X + 2,
                    e.Bounds.Y + 2,
                    20,
                    e.Bounds.Height - 4);

                using (SolidBrush brush = new SolidBrush(mat.Color))
                {
                    e.Graphics.FillRectangle(brush, colorRect);
                    e.Graphics.DrawRectangle(Pens.Black, colorRect);
                }

                // Draw text
                string text = $"{mat.Name} (ID: {mat.ID})";
                if (mat.IsExterior)
                    text += " - Exterior";

                Rectangle textRect = new Rectangle(
                    colorRect.Right + 5,
                    e.Bounds.Y,
                    e.Bounds.Width - colorRect.Width - 7,
                    e.Bounds.Height);

                TextRenderer.DrawText(e.Graphics, text, e.Font, textRect, Color.Black);

                e.DrawFocusRectangle();
            };

            foreach (Material mat in mainForm.Materials)
            {
                lstMatInfo.Items.Add(mat);
            }

            panel.Controls.Add(lstMatInfo);

            // Viewing instructions
            Label lblInstructions = new Label();
            lblInstructions.Text = "Viewing Instructions:";
            lblInstructions.Font = new Font(lblInstructions.Font, FontStyle.Bold);
            lblInstructions.AutoSize = true;
            lblInstructions.Margin = new Padding(0, 15, 0, 5);
            panel.Controls.Add(lblInstructions);

            TextBox txtInstructions = new TextBox();
            txtInstructions.Multiline = true;
            txtInstructions.ReadOnly = true;
            txtInstructions.ScrollBars = ScrollBars.Vertical;
            txtInstructions.Width = 330;
            txtInstructions.Height = 100;
            txtInstructions.Text = "• Left drag: Rotate camera\r\n";
            txtInstructions.Text += "• Right drag: Pan camera\r\n";
            txtInstructions.Text += "• Mouse wheel: Zoom in/out\r\n";
            txtInstructions.Text += "• Use the Materials tab to control visibility and opacity\r\n";
            txtInstructions.Text += "• Use the Slices tab to enable and position orthogonal slice planes\r\n";
            txtInstructions.Text += "• Use the Cutting tab to cut away parts of the volume\r\n";
            panel.Controls.Add(txtInstructions);

            tabInfo.Controls.Add(panel);
        }

        public void UpdateMeasurementUI(bool isMeasuring)
        {
            try
            {
                Logger.Log($"[SharpDXControlPanel] UpdateMeasurementUI called, isMeasuring: {isMeasuring}");

                // Update the button text
                if (btnAddMeasure != null)
                {
                    btnAddMeasure.Text = isMeasuring ? "Cancel Measurement" : "Add New Measurement";
                }

                // Update the status label
                if (lblStatus != null)
                {
                    lblStatus.Text = isMeasuring ?
                        "Click and drag to measure in 3D view..." :
                        "Ready.";
                }

                // This is important: we need to refresh the measurements list to show current state
                RefreshMeasurementsList();

                // Log the state change
                Logger.Log($"[SharpDXControlPanel] Measurement UI updated, mode: {(isMeasuring ? "measuring" : "normal")}");
            }
            catch (Exception ex)
            {
                Logger.Log($"[SharpDXControlPanel] Error updating measurement UI: {ex.Message}");
            }
        }
        public void RefreshMaterialsList()
        {
            try
            {
                Logger.Log("[SharpDXControlPanel] Refreshing materials list");

                if (lstMaterials == null)
                    return;

                // Store current selection
                int selectedIndex = lstMaterials.SelectedIndex;

                // Clear and repopulate the list
                lstMaterials.Items.Clear();

                if (mainForm.volumeLabels != null && mainForm.Materials != null)
                {
                    for (int i = 0; i < mainForm.Materials.Count; i++)
                    {
                        Material mat = mainForm.Materials[i];

                        // Get current visibility from the renderer
                        bool currentlyVisible = volumeRenderer.GetMaterialVisibility(mat.ID);

                        // Add the material to the list
                        lstMaterials.Items.Add(mat, currentlyVisible);
                    }
                }

                // Restore selection if possible
                if (selectedIndex >= 0 && selectedIndex < lstMaterials.Items.Count)
                {
                    lstMaterials.SelectedIndex = selectedIndex;
                }

                // Update opacity slider for selected material
                if (lstMaterials.SelectedIndex >= 0)
                {
                    Material mat = (Material)lstMaterials.Items[lstMaterials.SelectedIndex];
                    float currentAlpha = volumeRenderer.GetMaterialOpacity(mat.ID);
                    trkOpacity.Value = (int)Math.Round(currentAlpha * 100f);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[SharpDXControlPanel] Error refreshing materials list: {ex.Message}");
            }
        }
        #region Clipping plane
        private void InitializeClippingPlaneTab()
        {
            // Create panel to hold controls
            FlowLayoutPanel panel = new FlowLayoutPanel();
            panel.Dock = DockStyle.Fill;
            panel.FlowDirection = FlowDirection.TopDown;
            panel.AutoScroll = true;
            panel.Padding = new Padding(10);
            panel.WrapContents = false;

            // Add title label
            Label lblTitle = new Label();
            lblTitle.Text = "Rotating Clipping Plane";
            lblTitle.Font = new Font(lblTitle.Font.FontFamily, 10, FontStyle.Bold);
            lblTitle.AutoSize = true;
            lblTitle.Margin = new Padding(0, 0, 0, 10);
            panel.Controls.Add(lblTitle);

            // Enable/disable clipping plane
            chkEnableClippingPlane = new CheckBox();
            chkEnableClippingPlane.Text = "Enable Clipping Plane";
            chkEnableClippingPlane.CheckedChanged += (s, e) =>
            {
                bool enabled = chkEnableClippingPlane.Checked;
                viewerForm.SetClippingPlaneEnabled(enabled);

                // Enable/disable other controls
                trkRotationX.Enabled = enabled;
                trkRotationY.Enabled = enabled;
                trkRotationZ.Enabled = enabled;
                trkClippingPosition.Enabled = enabled;
                chkClippingMirror.Enabled = enabled;
                lblRotationX.Enabled = enabled;
                lblRotationY.Enabled = enabled;
                lblRotationZ.Enabled = enabled;
                lblClippingPosition.Enabled = enabled;
                panelPlanePreview.Enabled = enabled;

                UpdateClippingPlane();
            };
            panel.Controls.Add(chkEnableClippingPlane);

            // Orientation controls - 3D rotation
            Label lblOrientation = new Label();
            lblOrientation.Text = "Plane Orientation (Euler Angles):";
            lblOrientation.AutoSize = true;
            lblOrientation.Margin = new Padding(0, 15, 0, 5);
            panel.Controls.Add(lblOrientation);

            // X rotation (Pitch)
            Label lblXRot = new Label();
            lblXRot.Text = "Pitch (X-axis rotation):";
            lblXRot.AutoSize = true;
            lblXRot.Margin = new Padding(0, 5, 0, 0);
            panel.Controls.Add(lblXRot);

            trkRotationX = new TrackBar();
            trkRotationX.Minimum = 0;
            trkRotationX.Maximum = 360;
            trkRotationX.Value = 0;
            trkRotationX.TickFrequency = 45;
            trkRotationX.Width = 300;
            trkRotationX.Enabled = false;
            trkRotationX.Scroll += (s, e) =>
            {
                lblRotationX.Text = $"Pitch: {trkRotationX.Value}°";
                UpdateClippingPlane();
            };
            panel.Controls.Add(trkRotationX);

            lblRotationX = new Label();
            lblRotationX.Text = "Pitch: 0°";
            lblRotationX.AutoSize = true;
            lblRotationX.Enabled = false;
            panel.Controls.Add(lblRotationX);

            // Y rotation (Yaw)
            Label lblYRot = new Label();
            lblYRot.Text = "Yaw (Y-axis rotation):";
            lblYRot.AutoSize = true;
            lblYRot.Margin = new Padding(0, 10, 0, 0);
            panel.Controls.Add(lblYRot);

            trkRotationY = new TrackBar();
            trkRotationY.Minimum = 0;
            trkRotationY.Maximum = 360;
            trkRotationY.Value = 0;
            trkRotationY.TickFrequency = 45;
            trkRotationY.Width = 300;
            trkRotationY.Enabled = false;
            trkRotationY.Scroll += (s, e) =>
            {
                lblRotationY.Text = $"Yaw: {trkRotationY.Value}°";
                UpdateClippingPlane();
            };
            panel.Controls.Add(trkRotationY);

            lblRotationY = new Label();
            lblRotationY.Text = "Yaw: 0°";
            lblRotationY.AutoSize = true;
            lblRotationY.Enabled = false;
            panel.Controls.Add(lblRotationY);

            // Z rotation (Roll)
            Label lblZRot = new Label();
            lblZRot.Text = "Roll (Z-axis rotation):";
            lblZRot.AutoSize = true;
            lblZRot.Margin = new Padding(0, 10, 0, 0);
            panel.Controls.Add(lblZRot);

            trkRotationZ = new TrackBar();
            trkRotationZ.Minimum = 0;
            trkRotationZ.Maximum = 360;
            trkRotationZ.Value = 0;
            trkRotationZ.TickFrequency = 45;
            trkRotationZ.Width = 300;
            trkRotationZ.Enabled = false;
            trkRotationZ.Scroll += (s, e) =>
            {
                lblRotationZ.Text = $"Roll: {trkRotationZ.Value}°";
                UpdateClippingPlane();
            };
            panel.Controls.Add(trkRotationZ);

            lblRotationZ = new Label();
            lblRotationZ.Text = "Roll: 0°";
            lblRotationZ.AutoSize = true;
            lblRotationZ.Enabled = false;
            panel.Controls.Add(lblRotationZ);

            // Position along normal
            Label lblPosition = new Label();
            lblPosition.Text = "Position along normal (0-100%):";
            lblPosition.AutoSize = true;
            lblPosition.Margin = new Padding(0, 15, 0, 5);
            panel.Controls.Add(lblPosition);

            trkClippingPosition = new TrackBar();
            trkClippingPosition.Minimum = 0;
            trkClippingPosition.Maximum = 100;
            trkClippingPosition.Value = 50;
            trkClippingPosition.TickFrequency = 10;
            trkClippingPosition.Width = 300;
            trkClippingPosition.Enabled = false;
            trkClippingPosition.Scroll += (s, e) =>
            {
                lblClippingPosition.Text = $"Position: {trkClippingPosition.Value}%";
                UpdateClippingPlane();
            };
            panel.Controls.Add(trkClippingPosition);

            lblClippingPosition = new Label();
            lblClippingPosition.Text = "Position: 50%";
            lblClippingPosition.AutoSize = true;
            lblClippingPosition.Enabled = false;
            panel.Controls.Add(lblClippingPosition);

            // Mirror option
            chkClippingMirror = new CheckBox();
            chkClippingMirror.Text = "Mirror Clipping (cut other side)";
            chkClippingMirror.Enabled = false;
            chkClippingMirror.CheckedChanged += (s, e) =>
            {
                UpdateClippingPlane();
            };
            panel.Controls.Add(chkClippingMirror);

            // Add visual preview panel
            GroupBox grpPreview = new GroupBox();
            grpPreview.Text = "Plane Orientation Preview";
            grpPreview.Width = 330;
            grpPreview.Height = 150;
            grpPreview.Margin = new Padding(0, 15, 0, 5);

            panelPlanePreview = new Panel();
            panelPlanePreview.Dock = DockStyle.Fill;
            panelPlanePreview.BackColor = Color.Black;
            panelPlanePreview.Paint += PanelPlanePreview_Paint;
            grpPreview.Controls.Add(panelPlanePreview);

            panel.Controls.Add(grpPreview);

            // Add some preset buttons for common orientations
            Label lblPresets = new Label();
            lblPresets.Text = "Quick Presets:";
            lblPresets.AutoSize = true;
            lblPresets.Margin = new Padding(0, 10, 0, 5);
            panel.Controls.Add(lblPresets);

            FlowLayoutPanel presetPanel = new FlowLayoutPanel();
            presetPanel.FlowDirection = FlowDirection.LeftToRight;
            presetPanel.Height = 30;
            presetPanel.Width = 330;

            Button btnXY = new Button();
            btnXY.Text = "XY";
            btnXY.Width = 40;
            btnXY.Click += (s, e) => SetPresetOrientation(0, 0, 0);
            presetPanel.Controls.Add(btnXY);

            Button btnXZ = new Button();
            btnXZ.Text = "XZ";
            btnXZ.Width = 40;
            btnXZ.Click += (s, e) => SetPresetOrientation(90, 0, 0);
            presetPanel.Controls.Add(btnXZ);

            Button btnYZ = new Button();
            btnYZ.Text = "YZ";
            btnYZ.Width = 40;
            btnYZ.Click += (s, e) => SetPresetOrientation(0, 90, 0);
            presetPanel.Controls.Add(btnYZ);

            Button btnDiagonal = new Button();
            btnDiagonal.Text = "Diagonal";
            btnDiagonal.Width = 60;
            btnDiagonal.Click += (s, e) => SetPresetOrientation(45, 45, 0);
            presetPanel.Controls.Add(btnDiagonal);

            Button btnReset = new Button();
            btnReset.Text = "Reset";
            btnReset.Width = 50;
            btnReset.Click += (s, e) => SetPresetOrientation(0, 0, 0);
            presetPanel.Controls.Add(btnReset);

            panel.Controls.Add(presetPanel);

            // Add visual guide
            Label lblGuide = new Label();
            lblGuide.Text = "Rotate plane freely in 3D using Pitch (X), Yaw (Y), Roll (Z).\n" +
                           "Preview shows current orientation.";
            lblGuide.AutoSize = false;
            lblGuide.Width = 330;
            lblGuide.Height = 35; // Fixed height to prevent truncation
            lblGuide.Margin = new Padding(0, 15, 0, 10);
            lblGuide.Font = new Font(lblGuide.Font, FontStyle.Italic);
            lblGuide.ForeColor = Color.DarkGray;
            panel.Controls.Add(lblGuide);

            tabClippingPlane.Controls.Add(panel);
        }
        private void SetPresetOrientation(int pitch, int yaw, int roll)
        {
            trkRotationX.Value = pitch;
            trkRotationY.Value = yaw;
            trkRotationZ.Value = roll;
            lblRotationX.Text = $"Pitch: {pitch}°";
            lblRotationY.Text = $"Yaw: {yaw}°";
            lblRotationZ.Text = $"Roll: {roll}°";
            UpdateClippingPlane();
        }
        private void PanelPlanePreview_Paint(object sender, PaintEventArgs e)
        {
            if (!chkEnableClippingPlane.Checked)
                return;

            Graphics g = e.Graphics;
            int centerX = panelPlanePreview.Width / 2;
            int centerY = panelPlanePreview.Height / 2;
            int size = Math.Min(panelPlanePreview.Width, panelPlanePreview.Height) - 20;

            // Clear background
            g.Clear(Color.Black);

            // Draw coordinate axes
            using (Pen axisPen = new Pen(Color.DarkGray, 1))
            {
                // X axis (red)
                g.DrawLine(new Pen(Color.Red, 1), centerX - size / 3, centerY, centerX + size / 3, centerY);
                // Y axis (green)
                g.DrawLine(new Pen(Color.Green, 1), centerX, centerY - size / 3, centerX, centerY + size / 3);
                // Z axis (blue) - simulated as diagonal
                g.DrawLine(new Pen(Color.Blue, 1), centerX - size / 4, centerY + size / 4, centerX + size / 4, centerY - size / 4);
            }

            // Calculate normal vector from current rotation
            float pitchRad = trkRotationX.Value * (float)Math.PI / 180.0f;
            float yawRad = trkRotationY.Value * (float)Math.PI / 180.0f;
            float rollRad = trkRotationZ.Value * (float)Math.PI / 180.0f;

            // Create rotation matrices
            var rotX = SharpDX.Matrix.RotationX(pitchRad);
            var rotY = SharpDX.Matrix.RotationY(yawRad);
            var rotZ = SharpDX.Matrix.RotationZ(rollRad);
            var rotation = rotX * rotY * rotZ;

            // Apply rotation to the normal vector (initially pointing along Z axis)
            var normal = SharpDX.Vector3.TransformNormal(SharpDX.Vector3.UnitZ, rotation);

            // Draw the plane as a circle with the normal as an arrow
            using (Pen planePen = new Pen(Color.Yellow, 2))
            {
                // Draw circle representing the plane
                int radius = size / 3;
                g.DrawEllipse(planePen, centerX - radius, centerY - radius, radius * 2, radius * 2);

                // Draw normal arrow
                int arrowLength = size / 2;
                int endX = centerX + (int)(normal.X * arrowLength);
                int endY = centerY - (int)(normal.Y * arrowLength); // Invert Y for screen coordinates

                using (Pen arrowPen = new Pen(Color.Cyan, 2))
                {
                    g.DrawLine(arrowPen, centerX, centerY, endX, endY);

                    // Draw arrowhead
                    double angle = Math.Atan2(endY - centerY, endX - centerX);
                    int arrowSize = 8;
                    PointF[] arrowHead = new PointF[3];
                    arrowHead[0] = new PointF(endX, endY);
                    arrowHead[1] = new PointF(
                        endX - arrowSize * (float)Math.Cos(angle - Math.PI / 6),
                        endY - arrowSize * (float)Math.Sin(angle - Math.PI / 6));
                    arrowHead[2] = new PointF(
                        endX - arrowSize * (float)Math.Cos(angle + Math.PI / 6),
                        endY - arrowSize * (float)Math.Sin(angle + Math.PI / 6));
                    g.FillPolygon(new SolidBrush(Color.Cyan), arrowHead);
                }
            }

            // Add labels
            using (var font = new Font("Arial", 8))
            {
                g.DrawString("Normal", font, Brushes.Cyan, centerX + 5, centerY - 15);
                g.DrawString($"P:{trkRotationX.Value}° Y:{trkRotationY.Value}° R:{trkRotationZ.Value}°",
                             font, Brushes.White, 5, 5);
            }
        }

        private void UpdateClippingPlaneOrientation(object sender, EventArgs e)
        {
            if (!((RadioButton)sender).Checked) return;
            UpdateClippingPlane();
        }

        private void UpdateClippingPlane()
        {
            if (!chkEnableClippingPlane.Checked) return;

            // Convert angles to radians
            float pitchRad = trkRotationX.Value * (float)Math.PI / 180.0f;
            float yawRad = trkRotationY.Value * (float)Math.PI / 180.0f;
            float rollRad = trkRotationZ.Value * (float)Math.PI / 180.0f;

            // Create rotation matrices
            var rotX = SharpDX.Matrix.RotationX(pitchRad);
            var rotY = SharpDX.Matrix.RotationY(yawRad);
            var rotZ = SharpDX.Matrix.RotationZ(rollRad);
            var rotation = rotX * rotY * rotZ;

            // Apply rotation to the normal vector (initially pointing along Z axis)
            var normal = SharpDX.Vector3.TransformNormal(SharpDX.Vector3.UnitZ, rotation);
            normal.Normalize();

            // Get position (0-1)
            float position = trkClippingPosition.Value / 100.0f;

            // Update the viewer
            viewerForm.SetClippingPlane(normal, position, chkClippingMirror.Checked);

            // Refresh the preview
            panelPlanePreview.Invalidate();
        }


        private SharpDX.Vector3 RotateNormal(SharpDX.Vector3 normal, float angle)
        {
            // Create rotation matrix
            // For simplicity, rotate around the Y axis for YZ and XY planes
            // and around the X axis for XZ plane
            SharpDX.Matrix rotation;

            if (radClippingXY.Checked)
            {
                // Rotate around Y axis
                rotation = SharpDX.Matrix.RotationY(angle);
            }
            else if (radClippingXZ.Checked)
            {
                // Rotate around X axis for XZ plane
                rotation = SharpDX.Matrix.RotationX(angle);
            }
            else // YZ plane
            {
                // Rotate around Z axis
                rotation = SharpDX.Matrix.RotationZ(angle);
            }

            // Apply rotation
            var rotated = SharpDX.Vector3.TransformNormal(normal, rotation);
            rotated.Normalize();
            return rotated;
        }
        #endregion
        private void InitializeThresholdTimer()
        {
            thresholdUpdateTimer = new Timer();
            thresholdUpdateTimer.Interval = 150; // Update threshold every 150ms during slider drag
            thresholdUpdateTimer.Tick += async (s, e) =>
            {
                if (thresholdUpdatePending && !isThresholdUpdating)
                {
                    try
                    {
                        isThresholdUpdating = true;
                        lblStatus.Text = "Updating...";

                        await viewerForm.ApplyThresholdAndRender(
                            pendingMinThreshold,
                            pendingMaxThreshold,
                            pendingQualityIndex);

                        lblStatus.Text = "Ready.";
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[SharpDXControlPanel] Error updating threshold: {ex.Message}");
                        lblStatus.Text = "Update error. Try again.";
                    }
                    finally
                    {
                        isThresholdUpdating = false;
                        thresholdUpdatePending = false;
                        thresholdUpdateTimer.Stop();
                    }
                }
            };
        }

        public void RefreshMeasurementsList()
        {
            try
            {
                if (lstMeasurements == null)
                    return;

                // Store selection
                int selectedIndex = lstMeasurements.SelectedIndex;

                // Clear list
                lstMeasurements.Items.Clear();

                // Get the measurements from the viewer
                var measurements = viewerForm.GetMeasurements();
                if (measurements != null)
                {
                    // Log how many measurements we're adding
                    Logger.Log($"[SharpDXControlPanel] Refreshing measurements list with {measurements.Count} measurements");

                    // Temporarily remove the item check event handler to avoid cascading events
                    lstMeasurements.ItemCheck -= lstMeasurements_ItemCheck;

                    foreach (var measurement in measurements)
                    {
                        // Create a descriptive string for the measurement
                        string itemText = $"{measurement.Label}: {measurement.RealDistance:F2} {measurement.Unit}";

                        // Add to list and set visibility checkbox state
                        lstMeasurements.Items.Add(itemText, measurement.Visible);

                        // Log each measurement for debugging
                        Logger.Log($"[SharpDXControlPanel] Added measurement to list: {itemText}, Visible: {measurement.Visible}");
                    }

                    // Restore the event handler
                    lstMeasurements.ItemCheck += lstMeasurements_ItemCheck;
                }
                else
                {
                    Logger.Log("[SharpDXControlPanel] No measurements returned from viewer");
                }

                // Restore selection if possible
                if (selectedIndex >= 0 && selectedIndex < lstMeasurements.Items.Count)
                {
                    lstMeasurements.SelectedIndex = selectedIndex;
                }

                // Update the UI
                lstMeasurements.Refresh();
            }
            catch (Exception ex)
            {
                Logger.Log($"[SharpDXControlPanel] Error refreshing measurements list: {ex.Message}");
            }
        }

        private string FormatSize(double meters)
        {
            if (meters >= 1)
                return $"{meters:F3} m";
            if (meters >= 1e-3)
                return $"{meters * 1e3:F3} mm";
            if (meters >= 1e-6)
                return $"{meters * 1e6:F3} μm";
            return $"{meters * 1e9:F3} nm";
        }

        private void InitializeOpacityTimer()
        {
            opacityUpdateTimer = new Timer();
            opacityUpdateTimer.Interval = 100; // Update every 100ms during slider drag
            opacityUpdateTimer.Tick += (s, e) =>
            {
                if (opacityUpdatePending)
                {
                    try
                    {
                        viewerForm.SetMaterialOpacity(pendingMaterialId, pendingOpacity);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[SharpDXControlPanel] Error updating opacity: {ex.Message}");
                    }
                    opacityUpdatePending = false;
                }
                opacityUpdateTimer.Stop();
            };
        }

        private void UpdateSlices()
        {
            viewerForm.SetSliceIndices(trkXSlice.Value, trkYSlice.Value, trkZSlice.Value);
        }
    }
}