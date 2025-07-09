//Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Krypton.Toolkit;
using System.Numerics;
using System.Threading.Tasks;
using System.Windows.Media;
using Color = System.Drawing.Color;
using System.Threading;
using Pen = System.Drawing.Pen;
using Brush = System.Drawing.Brush;
using DashStyle = System.Drawing.Drawing2D.DashStyle;
using LinearGradientBrush = System.Drawing.Drawing2D.LinearGradientBrush;
using PixelFormat = System.Drawing.Imaging.PixelFormat;
using Matrix = System.Drawing.Drawing2D.Matrix;

namespace CTS
{
    public partial class AcousticSimulationForm
    {
        #region Fields and Properties
        // UI Controls for Results Tab
        private TabControl resultsTabControl;
        private TabPage waveformTab;
        private TabPage tomographyTab;
        private TabPage resultsDetailsTab;
        private float[,,] cachedPWaveField;
        private float[,,] cachedSWaveField;
        
        private int cachedTotalTimeSteps;

        private bool showFullVolumeInTomography = false;
        private KryptonCheckBox chkShowFullVolume;
        private KryptonTrackBar volumeOpacityTrackBar;
        private float volumeOpacity = 0.7f;
        private float minVelocityValue;
        private float maxVelocityValue;
        private KryptonButton btnAutoAdjustColorScale;
        private bool useAdaptiveColorScale = true;

        // Sliders Debouncing
        private System.Threading.Timer sliceUpdateTimer;
        private readonly object sliceUpdateLock = new object();
        private bool sliceUpdatePending = false;

        // Waveform controls
        private Panel waveformPanel;
        private PictureBox waveformPictureBox;
        private KryptonTrackBar waveformZoomTrackBar;
        private KryptonCheckBox chkShowPWave;
        private KryptonCheckBox chkShowSWave;
        private KryptonCheckBox chkShowDeadTime;

        // Tomography controls
        private Panel tomographyPanel;
        private PictureBox tomographyPictureBox;
        private KryptonTrackBar sliceXTrackBar;
        private KryptonTrackBar sliceYTrackBar;
        private KryptonTrackBar sliceZTrackBar;
        private PictureBox histogramPictureBox;
        private KryptonTrackBar minVelocityTrackBar;
        private KryptonTrackBar maxVelocityTrackBar;
        private KryptonRadioButton radioSliceX;
        private KryptonRadioButton radioSliceY;
        private KryptonRadioButton radioSliceZ;
        private KryptonButton btnReset3DView;

        // Results details controls
        private KryptonPanel resultsDetailsPanel;
        private KryptonLabel lblPWaveVelocity;
        private KryptonLabel lblSWaveVelocity;
        private KryptonLabel lblVpVsRatio;
        private KryptonLabel lblPWaveTravelTime;
        private KryptonLabel lblSWaveTravelTime;
        private KryptonLabel lblDeadTime;
        private KryptonButton btnRecalculate;
        private KryptonButton btnOpenVisualizer;

        // Export controls
        private KryptonButton btnExportWaveform;
        private KryptonButton btnExportTomography;
        private KryptonButton btnExportDetails;
        private KryptonButton btnExportComposite;

        // Data storage
        private float[] velocityHistogram;
        private double minFilterVelocity = 0;
        private double maxFilterVelocity = 10000;
        private int currentSliceX, currentSliceY, currentSliceZ;
        private float waveformZoom = 1.0f;
        private float tomographyZoom = 1.0f;
        private float tomographyRotationX = 30f;
        private float tomographyRotationY = 30f;
        private PointF tomographyPan = new PointF(0, 0);

        // 3D rendering data
        private float[,,] velocityField;
        private bool isDragging3D = false;
        private Point lastMousePos;
        private int activeSliceDirection = 0; // 0=X, 1=Y, 2=Z

        // Constants
        private readonly Color PWaveColor = Color.DeepSkyBlue;
        private readonly Color SWaveColor = Color.Crimson;
        private readonly Color DeadTimeColor = Color.FromArgb(80, 255, 255, 0);
        public struct WaveFieldSnapshot
        {
            public double[,,] vx;
            public double[,,] vy;
            public double[,,] vz;
        }
        #endregion

        #region Initialization
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // Initialize the results tab
            InitializeResultsTab();
        }

        private void InitializeResultsTab()
        {
            // Clear existing controls from results tab
            tabResults.Controls.Clear();

            // Create tab control for Results tab
            resultsTabControl = new TabControl
            {
                Dock = DockStyle.Fill,
                DrawMode = TabDrawMode.OwnerDrawFixed,
                BackColor = Color.FromArgb(45, 45, 48)
            };
            resultsTabControl.DrawItem += TabControl_DrawItem; // Reuse existing dark theme drawing

            // Create tabs
            waveformTab = new TabPage("Waveform Analysis");
            waveformTab.BackColor = Color.FromArgb(45, 45, 48);

            tomographyTab = new TabPage("3D Tomography");
            tomographyTab.BackColor = Color.FromArgb(45, 45, 48);

            resultsDetailsTab = new TabPage("Detailed Results");
            resultsDetailsTab.BackColor = Color.FromArgb(45, 45, 48);

            // Add tabs to control
            resultsTabControl.TabPages.Add(waveformTab);
            resultsTabControl.TabPages.Add(tomographyTab);
            resultsTabControl.TabPages.Add(resultsDetailsTab);

            // Initialize tab content
            InitializeWaveformTab();
            InitializeTomographyTab();
            InitializeResultsDetailsTab();

            // Add tab control to results tab
            tabResults.Controls.Add(resultsTabControl);

            // Add the visualizer button to the toolbar if not already present
            EnsureVisualizerButtonExists();
        }

        private void EnsureVisualizerButtonExists()
        {
            // Check if we need to add a button to reopen visualizer
            bool hasVisualizerButton = false;
            foreach (ToolStripItem item in toolStrip.Items)
            {
                if (item.Text == "Open Visualizer" || item.ToolTipText == "Open Visualizer")
                {
                    hasVisualizerButton = true;
                    break;
                }
            }

            if (!hasVisualizerButton)
            {
                ToolStripButton btnVisualizer = new ToolStripButton("Open Visualizer");
                btnVisualizer.DisplayStyle = ToolStripItemDisplayStyle.Image;
                btnVisualizer.Image = CreateVisualizerIcon();
                btnVisualizer.ToolTipText = "Open Visualizer";
                btnVisualizer.Click += (s, e) => OpenVisualizer();

                // Add it to the toolbar
                toolStrip.Items.Add(btnVisualizer);
            }
        }
        private float CalculateAverageDensity()
        {
            // Get dimensions
            int width = mainForm.GetWidth();
            int height = mainForm.GetHeight();
            int depth = mainForm.GetDepth();

            if (width <= 0 || height <= 0 || depth <= 0)
                return 1000.0f; // Default if no valid dimensions

            float totalDensity = 0.0f;
            int count = 0;

            // Calculate average density for the selected material only
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        if (mainForm.volumeLabels[x, y, z] == selectedMaterialID)
                        {
                            totalDensity += densityVolume[x, y, z];
                            count++;
                        }
                    }
                }
            }

            // Return the average or default if no material found
            return count > 0 ? totalDensity / count : 1000.0f;
        }
        private Image CreateVisualizerIcon()
        {
            Bitmap bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                // Draw a 3D-looking cube with wave pattern
                using (Pen pen = new Pen(Color.White, 2))
                {
                    // Draw a cube
                    Point[] frontFace = {
                        new Point(7, 10),
                        new Point(22, 10),
                        new Point(22, 25),
                        new Point(7, 25),
                        new Point(7, 10)
                    };

                    Point[] backFace = {
                        new Point(12, 5),
                        new Point(27, 5),
                        new Point(27, 20),
                        new Point(12, 20),
                        new Point(12, 5)
                    };

                    // Draw back face
                    g.DrawLines(pen, backFace);

                    // Draw connecting lines
                    g.DrawLine(pen, 7, 10, 12, 5);
                    g.DrawLine(pen, 22, 10, 27, 5);
                    g.DrawLine(pen, 22, 25, 27, 20);
                    g.DrawLine(pen, 7, 25, 12, 20);

                    // Draw front face
                    g.DrawLines(pen, frontFace);

                    // Draw a wave inside
                    using (Pen wavePen = new Pen(Color.DeepSkyBlue, 2))
                    {
                        Point[] wave = {
                            new Point(10, 18),
                            new Point(12, 15),
                            new Point(14, 20),
                            new Point(16, 15),
                            new Point(18, 20),
                            new Point(20, 17)
                        };

                        g.DrawLines(wavePen, wave);
                    }
                }
            }
            return bmp;
        }

        private void InitializeWaveformTab()
        {
            // Main container panel
            waveformPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(30, 30, 30)
            };

            // Controls panel on the right
            Panel controlPanel = new Panel
            {
                Dock = DockStyle.Right,
                Width = 200,
                BackColor = Color.FromArgb(40, 40, 40)
            };

            // PictureBox for the waveform
            waveformPictureBox = new PictureBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(20, 20, 20),
                SizeMode = PictureBoxSizeMode.Normal
            };
            waveformPictureBox.Paint += WaveformPictureBox_Paint;

            // Zoom track bar
            waveformZoomTrackBar = new KryptonTrackBar
            {
                Minimum = 1,
                Maximum = 50,
                Value = 10, // Initial zoom level
                SmallChange = 1,
                LargeChange = 5,
                Orientation = Orientation.Horizontal,
                Dock = DockStyle.Bottom,
                Height = 30
            };
            waveformZoomTrackBar.ValueChanged += (s, e) => {
                waveformZoom = waveformZoomTrackBar.Value / 10.0f;
                waveformPictureBox.Invalidate();
            };

            // Wave display checkboxes
            chkShowPWave = new KryptonCheckBox
            {
                Text = "Show P-Wave",
                Checked = true,
                Location = new Point(10, 20),
                Size = new Size(180, 25)
            };
            chkShowPWave.CheckedChanged += (s, e) => waveformPictureBox.Invalidate();

            chkShowSWave = new KryptonCheckBox
            {
                Text = "Show S-Wave",
                Checked = true,
                Location = new Point(10, 50),
                Size = new Size(180, 25)
            };
            chkShowSWave.CheckedChanged += (s, e) => waveformPictureBox.Invalidate();

            chkShowDeadTime = new KryptonCheckBox
            {
                Text = "Show Dead Time",
                Checked = true,
                Location = new Point(10, 80),
                Size = new Size(180, 25)
            };
            chkShowDeadTime.CheckedChanged += (s, e) => waveformPictureBox.Invalidate();

            // Export button
            btnExportWaveform = new KryptonButton
            {
                Text = "Export Image",
                Location = new Point(10, 150),
                Size = new Size(180, 30)
            };
            btnExportWaveform.Click += (s, e) => ExportImage(waveformPictureBox, "WaveformAnalysis");

            // Add controls to the control panel
            controlPanel.Controls.Add(chkShowPWave);
            controlPanel.Controls.Add(chkShowSWave);
            controlPanel.Controls.Add(chkShowDeadTime);
            controlPanel.Controls.Add(btnExportWaveform);

            // Add main components to the waveform panel
            waveformPanel.Controls.Add(waveformPictureBox);
            waveformPanel.Controls.Add(waveformZoomTrackBar);
            waveformPanel.Controls.Add(controlPanel);

            // Add to tab
            waveformTab.Controls.Add(waveformPanel);
        }

        private void InitializeTomographyTab()
        {
            // Initialize min/max filter velocity with reasonable defaults
            minFilterVelocity = 1000; // 1000 m/s as default min
            maxFilterVelocity = 5000; // 5000 m/s as default max

            // Update with actual values if we have simulation results
            if (simulationResults != null)
            {
                double referenceVelocity = simulationResults.PWaveVelocity;
                minFilterVelocity = referenceVelocity * 0.5;
                maxFilterVelocity = referenceVelocity * 1.5;
            }

            // Main container panel
            tomographyPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(30, 30, 30)
            };

            // Controls panel on the right
            Panel controlPanel = new Panel
            {
                Dock = DockStyle.Right,
                Width = 250,
                BackColor = Color.FromArgb(40, 40, 40)
            };

            // PictureBox for the tomography view
            tomographyPictureBox = new PictureBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(20, 20, 20),
                SizeMode = PictureBoxSizeMode.Normal
            };
            tomographyPictureBox.Paint += TomographyPictureBox_Paint;
            tomographyPictureBox.MouseDown += TomographyPictureBox_MouseDown;
            tomographyPictureBox.MouseMove += TomographyPictureBox_MouseMove;
            tomographyPictureBox.MouseUp += TomographyPictureBox_MouseUp;
            tomographyPictureBox.MouseWheel += TomographyPictureBox_MouseWheel;

            // Reset view button
            btnReset3DView = new KryptonButton
            {
                Text = "Reset View",
                Location = new Point(10, 20),
                Size = new Size(230, 30)
            };
            btnReset3DView.Click += (s, e) => {
                tomographyRotationX = 30f;
                tomographyRotationY = 30f;
                tomographyZoom = 1.0f;
                tomographyPan = new PointF(0, 0);
                tomographyPictureBox.Invalidate();
            };

            // Add Volume Rendering toggle
            chkShowFullVolume = new KryptonCheckBox
            {
                Text = "Show Full Volume Rendering",
                Location = new Point(10, 60),
                Size = new Size(230, 25),
                Checked = showFullVolumeInTomography,
                StateCommon = {
                    ShortText = {
                        Color1 = Color.White
                    }
                }
            };
            chkShowFullVolume.CheckedChanged += (s, e) => {
                showFullVolumeInTomography = chkShowFullVolume.Checked;
                // Enable/disable slice controls based on volume rendering
                bool slicesEnabled = !showFullVolumeInTomography;
                radioSliceX.Enabled = slicesEnabled;
                radioSliceY.Enabled = slicesEnabled;
                radioSliceZ.Enabled = slicesEnabled;
                sliceXTrackBar.Enabled = slicesEnabled && radioSliceX.Checked;
                sliceYTrackBar.Enabled = slicesEnabled && radioSliceY.Checked;
                sliceZTrackBar.Enabled = slicesEnabled && radioSliceZ.Checked;

                // Refresh visualization
                tomographyPictureBox.Invalidate();
            };
            controlPanel.Controls.Add(chkShowFullVolume);

            // Add Volume Opacity control
            KryptonLabel lblVolumeOpacity = new KryptonLabel
            {
                Text = "Volume Opacity:",
                Location = new Point(10, 90),
                Size = new Size(230, 20),
                StateCommon = {
                    ShortText = {
                        Color1 = Color.White
                    }
                }
            };
            controlPanel.Controls.Add(lblVolumeOpacity);

            volumeOpacityTrackBar = new KryptonTrackBar
            {
                Minimum = 1,
                Maximum = 100,
                Value = (int)(volumeOpacity * 100),
                SmallChange = 5,
                LargeChange = 20,
                Location = new Point(10, 110),
                Size = new Size(230, 30)
            };
            volumeOpacityTrackBar.ValueChanged += (s, e) => {
                volumeOpacity = volumeOpacityTrackBar.Value / 100.0f;
                tomographyPictureBox.Invalidate();
            };
            controlPanel.Controls.Add(volumeOpacityTrackBar);

            // Slice direction radio buttons
            KryptonGroupBox sliceGroup = new KryptonGroupBox
            {
                Text = "Slice Direction",
                Location = new Point(10, 150),
                Size = new Size(230, 110), // Increased height to fit all three radio buttons
                StateCommon = {
                    Content = {
                        ShortText = {
                            Color1 = Color.White
                        }
                    }
                }
            };

            radioSliceX = new KryptonRadioButton
            {
                Text = "X Slice",
                Checked = true,
                Location = new Point(10, 20),
                Size = new Size(180, 20), // Reduced height for tighter spacing
                StateCommon = {
                    ShortText = {
                        Color1 = Color.White
                    }
                }
            };
            radioSliceX.CheckedChanged += (s, e) => {
                if (radioSliceX.Checked)
                {
                    activeSliceDirection = 0;
                    sliceXTrackBar.Enabled = true;
                    sliceYTrackBar.Enabled = false;
                    sliceZTrackBar.Enabled = false;
                    tomographyPictureBox.Invalidate();
                }
            };

            radioSliceY = new KryptonRadioButton
            {
                Text = "Y Slice",
                Location = new Point(10, 45),
                Size = new Size(180, 20), // Reduced height for tighter spacing
                StateCommon = {
                    ShortText = {
                        Color1 = Color.White
                    }
                }
            };
            radioSliceY.CheckedChanged += (s, e) => {
                if (radioSliceY.Checked)
                {
                    activeSliceDirection = 1;
                    sliceXTrackBar.Enabled = false;
                    sliceYTrackBar.Enabled = true;
                    sliceZTrackBar.Enabled = false;
                    tomographyPictureBox.Invalidate();
                }
            };

            radioSliceZ = new KryptonRadioButton
            {
                Text = "Z Slice",
                Location = new Point(10, 70),
                Size = new Size(180, 20), // Reduced height for tighter spacing
                StateCommon = {
                    ShortText = {
                        Color1 = Color.White
                    }
                }
            };
            radioSliceZ.CheckedChanged += (s, e) => {
                if (radioSliceZ.Checked)
                {
                    activeSliceDirection = 2;
                    sliceXTrackBar.Enabled = false;
                    sliceYTrackBar.Enabled = false;
                    sliceZTrackBar.Enabled = true;
                    tomographyPictureBox.Invalidate();
                }
            };
            sliceGroup.StateCommon.Back.Color1 = Color.FromArgb(40, 40, 40);
            sliceGroup.StateCommon.Back.Color2 = Color.FromArgb(40, 40, 40);

            sliceGroup.Panel.Controls.Add(radioSliceX);
            sliceGroup.Panel.Controls.Add(radioSliceY);
            sliceGroup.Panel.Controls.Add(radioSliceZ);
            controlPanel.Controls.Add(sliceGroup);

            // Slice position track bars
            KryptonLabel lblSliceX = new KryptonLabel
            {
                Text = "X Slice Position:",
                Location = new Point(10, 265), // Adjusted position
                Size = new Size(230, 20),
                StateCommon = {
                    ShortText = {
                        Color1 = Color.White
                    }
                }
            };
            controlPanel.Controls.Add(lblSliceX);

            sliceXTrackBar = new KryptonTrackBar
            {
                Minimum = 0,
                Maximum = mainForm.GetWidth() > 0 ? mainForm.GetWidth() - 1 : 100,
                Value = mainForm.GetWidth() > 0 ? mainForm.GetWidth() / 2 : 50,
                SmallChange = 1,
                LargeChange = 10,
                Location = new Point(10, 285),
                Size = new Size(230, 30)
            };
            sliceXTrackBar.ValueChanged += (s, e) => {
                lock (sliceUpdateLock)
                {
                    currentSliceX = sliceXTrackBar.Value;
                    if (!sliceUpdatePending)
                    {
                        sliceUpdatePending = true;
                        sliceUpdateTimer.Change(100, Timeout.Infinite); // 100ms debounce
                    }
                }
            };
            controlPanel.Controls.Add(sliceXTrackBar);

            KryptonLabel lblSliceY = new KryptonLabel
            {
                Text = "Y Slice Position:",
                Location = new Point(10, 315),
                Size = new Size(230, 20),
                StateCommon = {
                    ShortText = {
                        Color1 = Color.White
                    }
                }
            };
            controlPanel.Controls.Add(lblSliceY);

            sliceYTrackBar = new KryptonTrackBar
            {
                Minimum = 0,
                Maximum = mainForm.GetHeight() > 0 ? mainForm.GetHeight() - 1 : 100,
                Value = mainForm.GetHeight() > 0 ? mainForm.GetHeight() / 2 : 50,
                SmallChange = 1,
                LargeChange = 10,
                Location = new Point(10, 335),
                Size = new Size(230, 30),
                Enabled = false
            };
            sliceYTrackBar.ValueChanged += (s, e) => {
                lock (sliceUpdateLock)
                {
                    currentSliceY = sliceYTrackBar.Value;
                    if (!sliceUpdatePending)
                    {
                        sliceUpdatePending = true;
                        sliceUpdateTimer.Change(100, Timeout.Infinite); // 100ms debounce
                    }
                }
            };
            controlPanel.Controls.Add(sliceYTrackBar);

            KryptonLabel lblSliceZ = new KryptonLabel
            {
                Text = "Z Slice Position:",
                Location = new Point(10, 365),
                Size = new Size(230, 20),
                StateCommon = {
                    ShortText = {
                        Color1 = Color.White
                    }
                }
            };
            controlPanel.Controls.Add(lblSliceZ);

            sliceZTrackBar = new KryptonTrackBar
            {
                Minimum = 0,
                Maximum = mainForm.GetDepth() > 0 ? mainForm.GetDepth() - 1 : 100,
                Value = mainForm.GetDepth() > 0 ? mainForm.GetDepth() / 2 : 50,
                SmallChange = 1,
                LargeChange = 10,
                Location = new Point(10, 385),
                Size = new Size(230, 30),
                Enabled = false
            };
            sliceZTrackBar.ValueChanged += (s, e) => {
                lock (sliceUpdateLock)
                {
                    currentSliceZ = sliceZTrackBar.Value;
                    if (!sliceUpdatePending)
                    {
                        sliceUpdatePending = true;
                        sliceUpdateTimer.Change(100, Timeout.Infinite); // 100ms debounce
                    }
                }
            };
            controlPanel.Controls.Add(sliceZTrackBar);
            sliceUpdateTimer = new System.Threading.Timer(OnSliceUpdateTimerElapsed, null, Timeout.Infinite, Timeout.Infinite);

            // Histogram and velocity range filtering
            KryptonLabel lblHistogram = new KryptonLabel
            {
                Text = "Velocity Distribution:",
                Location = new Point(10, 420),
                Size = new Size(230, 20),
                StateCommon = {
                    ShortText = {
                        Color1 = Color.White
                    }
                }
            };
            controlPanel.Controls.Add(lblHistogram);

            histogramPictureBox = new PictureBox
            {
                Location = new Point(10, 440),
                Size = new Size(230, 100),
                BackColor = Color.FromArgb(20, 20, 20),
                BorderStyle = BorderStyle.FixedSingle
            };
            histogramPictureBox.Paint += HistogramPictureBox_Paint;
            controlPanel.Controls.Add(histogramPictureBox);

            // Add an Auto-Adjust Color Scale button
            btnAutoAdjustColorScale = new KryptonButton
            {
                Text = "Auto-Adjust Color Scale",
                Location = new Point(10, 545),
                Size = new Size(230, 30)
            };
            btnAutoAdjustColorScale.Click += (s, e) => {
                AutoAdjustColorScale();
                tomographyPictureBox.Invalidate();
                histogramPictureBox.Invalidate();
            };
            controlPanel.Controls.Add(btnAutoAdjustColorScale);

            KryptonLabel lblVelocityRange = new KryptonLabel
            {
                Text = "Velocity Range Filter:",
                Location = new Point(10, 580),
                Size = new Size(230, 20),
                StateCommon = {
                    ShortText = {
                        Color1 = Color.White
                    }
                }
            };
            controlPanel.Controls.Add(lblVelocityRange);

            minVelocityTrackBar = new KryptonTrackBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                SmallChange = 1,
                LargeChange = 10,
                Location = new Point(10, 600),
                Size = new Size(230, 30)
            };
            minVelocityTrackBar.ValueChanged += (s, e) => {
                UpdateVelocityRange();
                tomographyPictureBox.Invalidate();
                histogramPictureBox.Invalidate();
            };
            controlPanel.Controls.Add(minVelocityTrackBar);

            maxVelocityTrackBar = new KryptonTrackBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = 100,
                SmallChange = 1,
                LargeChange = 10,
                Location = new Point(10, 630),
                Size = new Size(230, 30)
            };
            maxVelocityTrackBar.ValueChanged += (s, e) => {
                UpdateVelocityRange();
                tomographyPictureBox.Invalidate();
                histogramPictureBox.Invalidate();
            };
            controlPanel.Controls.Add(maxVelocityTrackBar);

            // Add standard view buttons for easier orientation
            KryptonButton btnTopView = new KryptonButton
            {
                Text = "Top View",
                Location = new Point(10, 670),
                Size = new Size(75, 25)
            };
            btnTopView.Click += (s, e) => {
                tomographyRotationX = 90;
                tomographyRotationY = 0;
                tomographyPictureBox.Invalidate();
            };
            controlPanel.Controls.Add(btnTopView);

            KryptonButton btnFrontView = new KryptonButton
            {
                Text = "Front View",
                Location = new Point(85, 670),
                Size = new Size(75, 25)
            };
            btnFrontView.Click += (s, e) => {
                tomographyRotationX = 0;
                tomographyRotationY = 0;
                tomographyPictureBox.Invalidate();
            };
            controlPanel.Controls.Add(btnFrontView);

            KryptonButton btnSideView = new KryptonButton
            {
                Text = "Side View",
                Location = new Point(160, 670),
                Size = new Size(75, 25)
            };
            btnSideView.Click += (s, e) => {
                tomographyRotationX = 0;
                tomographyRotationY = 90;
                tomographyPictureBox.Invalidate();
            };
            controlPanel.Controls.Add(btnSideView);

            // Export button at the bottom
            btnExportTomography = new KryptonButton
            {
                Text = "Export Image",
                Location = new Point(10, 705),
                Size = new Size(230, 30)
            };
            btnExportTomography.Click += (s, e) => ExportImage(tomographyPictureBox, "Tomography");
            controlPanel.Controls.Add(btnExportTomography);

            // Add main components to the tomography panel
            tomographyPanel.Controls.Add(tomographyPictureBox);
            tomographyPanel.Controls.Add(controlPanel);

            // Make the control panel scrollable
            Panel scrollPanel = new Panel
            {
                Dock = DockStyle.Right,
                Width = 250,
                AutoScroll = true,
                BackColor = Color.FromArgb(40, 40, 40)
            };
            scrollPanel.Controls.Add(controlPanel);
            controlPanel.Size = new Size(230, 750); // Set larger height for scrolling
            controlPanel.Dock = DockStyle.None;
            tomographyPanel.Controls.Add(scrollPanel);

            // Add to tab
            tomographyTab.Controls.Add(tomographyPanel);

            // Initialize values
            currentSliceX = sliceXTrackBar.Value;
            currentSliceY = sliceYTrackBar.Value;
            currentSliceZ = sliceZTrackBar.Value;
            velocityField = new float[1, 1, 1];
            velocityHistogram = new float[100];

            // Initialize velocity data
            InitializeVelocityField();

            // Analyze velocity range for automatic color scaling
            AnalyzeVelocityRange();
        }
        private void OnSliceUpdateTimerElapsed(object state)
        {
            lock (sliceUpdateLock)
            {
                sliceUpdatePending = false;
            }

            // Invoke UI update on UI thread
            if (tomographyPictureBox.InvokeRequired)
            {
                tomographyPictureBox.BeginInvoke(new Action(() => {
                    tomographyPictureBox.Invalidate();
                }));
            }
            else
            {
                tomographyPictureBox.Invalidate();
            }
        }
        private void LogVelocityFieldStats()
        {
            if (velocityField == null)
            {
                Logger.Log("[LogVelocityFieldStats] Velocity field is null");
                return;
            }

            int width = velocityField.GetLength(0);
            int height = velocityField.GetLength(1);
            int depth = velocityField.GetLength(2);

            Logger.Log($"[LogVelocityFieldStats] Velocity field dimensions: {width}x{height}x{depth}");

            int nonZeroCount = 0;
            float minVel = float.MaxValue;
            float maxVel = float.MinValue;
            float sum = 0;

            for (int z = 0; z < depth; z += depth / 10)
            {
                for (int y = 0; y < height; y += height / 10)
                {
                    for (int x = 0; x < width; x += width / 10)
                    {
                        float vel = velocityField[x, y, z];
                        if (vel > 0)
                        {
                            nonZeroCount++;
                            minVel = Math.Min(minVel, vel);
                            maxVel = Math.Max(maxVel, vel);
                            sum += vel;
                        }
                    }
                }
            }

            Logger.Log($"[LogVelocityFieldStats] Non-zero values: {nonZeroCount}");
            if (nonZeroCount > 0)
            {
                Logger.Log($"[LogVelocityFieldStats] Min velocity: {minVel}, Max velocity: {maxVel}, Avg: {sum / nonZeroCount}");
            }
        }
        private void InitializeResultsDetailsTab()
        {
            baseDensity = CalculateAverageDensity();
            // Create scrollable panel
            KryptonPanel scrollablePanel = new KryptonPanel
            {
                Dock = DockStyle.Fill,
                StateCommon = { Color1 = Color.FromArgb(45, 45, 48), Color2 = Color.FromArgb(45, 45, 48) }
            };

            resultsDetailsPanel = new KryptonPanel
            {
                Dock = DockStyle.None,
                Location = new Point(20, 20),
                Width = 600,
                Height = 500,
                StateCommon = { Color1 = Color.FromArgb(45, 45, 48), Color2 = Color.FromArgb(45, 45, 48) }
            };

            int currentY = 20;
            int spacing = 40;

            // Results title
            KryptonLabel lblTitle = new KryptonLabel
            {
                Text = "Acoustic Simulation Results",
                Location = new Point(0, currentY),
                Size = new Size(600, 30),
                StateCommon = {
                    ShortText = {
                        Font = new Font("Segoe UI", 14, FontStyle.Bold),
                        Color1 = Color.White
                    }
                }
            };
            resultsDetailsPanel.Controls.Add(lblTitle);
            currentY += 50;

            // P-Wave Velocity
            KryptonLabel lblPWaveTitle = new KryptonLabel
            {
                Text = "P-Wave Velocity:",
                Location = new Point(0, currentY),
                Size = new Size(200, 25),
                StateCommon = {
                    ShortText = {
                        Font = new Font("Segoe UI", 10),
                        Color1 = Color.White
                    }
                }
            };
            resultsDetailsPanel.Controls.Add(lblPWaveTitle);

            lblPWaveVelocity = new KryptonLabel
            {
                Text = "0.00 m/s",
                Location = new Point(220, currentY),
                Size = new Size(350, 25),
                StateCommon = {
                    ShortText = {
                        Font = new Font("Segoe UI", 10, FontStyle.Bold),
                        Color1 = Color.DeepSkyBlue
                    }
                }
            };
            resultsDetailsPanel.Controls.Add(lblPWaveVelocity);
            currentY += spacing;

            // S-Wave Velocity
            KryptonLabel lblSWaveTitle = new KryptonLabel
            {
                Text = "S-Wave Velocity:",
                Location = new Point(0, currentY),
                Size = new Size(200, 25),
                StateCommon = {
                    ShortText = {
                        Font = new Font("Segoe UI", 10),
                        Color1 = Color.White
                    }
                }
            };
            resultsDetailsPanel.Controls.Add(lblSWaveTitle);

            lblSWaveVelocity = new KryptonLabel
            {
                Text = "0.00 m/s",
                Location = new Point(220, currentY),
                Size = new Size(350, 25),
                StateCommon = {
                    ShortText = {
                        Font = new Font("Segoe UI", 10, FontStyle.Bold),
                        Color1 = Color.Crimson
                    }
                }
            };
            resultsDetailsPanel.Controls.Add(lblSWaveVelocity);
            currentY += spacing;

            // Vp/Vs Ratio
            KryptonLabel lblVpVsTitle = new KryptonLabel
            {
                Text = "Vp/Vs Ratio:",
                Location = new Point(0, currentY),
                Size = new Size(200, 25),
                StateCommon = {
                    ShortText = {
                        Font = new Font("Segoe UI", 10),
                        Color1 = Color.White
                    }
                }
            };
            resultsDetailsPanel.Controls.Add(lblVpVsTitle);

            lblVpVsRatio = new KryptonLabel
            {
                Text = "0.00",
                Location = new Point(220, currentY),
                Size = new Size(350, 25),
                StateCommon = {
                    ShortText = {
                        Font = new Font("Segoe UI", 10, FontStyle.Bold),
                        Color1 = Color.LightGreen
                    }
                }
            };
            resultsDetailsPanel.Controls.Add(lblVpVsRatio);
            currentY += spacing;

            // P-Wave Travel Time
            KryptonLabel lblPTimeTitle = new KryptonLabel
            {
                Text = "P-Wave Travel Time:",
                Location = new Point(0, currentY),
                Size = new Size(200, 25),
                StateCommon = {
                    ShortText = {
                        Font = new Font("Segoe UI", 10),
                        Color1 = Color.White
                    }
                }
            };
            resultsDetailsPanel.Controls.Add(lblPTimeTitle);

            lblPWaveTravelTime = new KryptonLabel
            {
                Text = "0 steps (0.00 ms)",
                Location = new Point(220, currentY),
                Size = new Size(350, 25),
                StateCommon = {
                    ShortText = {
                        Font = new Font("Segoe UI", 10),
                        Color1 = Color.White
                    }
                }
            };
            resultsDetailsPanel.Controls.Add(lblPWaveTravelTime);
            currentY += spacing;

            // S-Wave Travel Time
            KryptonLabel lblSTimeTitle = new KryptonLabel
            {
                Text = "S-Wave Travel Time:",
                Location = new Point(0, currentY),
                Size = new Size(200, 25),
                StateCommon = {
                    ShortText = {
                        Font = new Font("Segoe UI", 10),
                        Color1 = Color.White
                    }
                }
            };
            resultsDetailsPanel.Controls.Add(lblSTimeTitle);

            lblSWaveTravelTime = new KryptonLabel
            {
                Text = "0 steps (0.00 ms)",
                Location = new Point(220, currentY),
                Size = new Size(350, 25),
                StateCommon = {
                    ShortText = {
                        Font = new Font("Segoe UI", 10),
                        Color1 = Color.White
                    }
                }
            };
            resultsDetailsPanel.Controls.Add(lblSWaveTravelTime);
            currentY += spacing;

            // Dead Time
            KryptonLabel lblDeadTimeTitle = new KryptonLabel
            {
                Text = "Dead Time:",
                Location = new Point(0, currentY),
                Size = new Size(200, 25),
                StateCommon = {
                    ShortText = {
                        Font = new Font("Segoe UI", 10),
                        Color1 = Color.White
                    }
                }
            };
            resultsDetailsPanel.Controls.Add(lblDeadTimeTitle);

            lblDeadTime = new KryptonLabel
            {
                Text = "0 steps (0.00 ms)",
                Location = new Point(220, currentY),
                Size = new Size(350, 25),
                StateCommon = {
                    ShortText = {
                        Font = new Font("Segoe UI", 10, FontStyle.Bold),
                        Color1 = Color.Yellow
                    }
                }
            };
            resultsDetailsPanel.Controls.Add(lblDeadTime);
            currentY += (int)(spacing * 1.5);

            // Material and physical properties section
            KryptonLabel lblMaterialTitle = new KryptonLabel
            {
                Text = "Material Properties",
                Location = new Point(0, currentY),
                Size = new Size(600, 25),
                StateCommon = {
                    ShortText = {
                        Font = new Font("Segoe UI", 12, FontStyle.Bold),
                        Color1 = Color.White
                    }
                }
            };
            resultsDetailsPanel.Controls.Add(lblMaterialTitle);
            currentY += spacing;

            // Display key material properties
            string[] propertyLabels = {
                "Young's Modulus:", "Poisson's Ratio:", "Material Density:",
                "Confining Pressure:", "Wave Source Energy:", "Wave Frequency:"
            };

            string[] propertyValues = {
                $"{numYoungsModulus.Value:N0} MPa",
                $"{numPoissonRatio.Value:N3}",
                $"{baseDensity:N1} kg/m³",
                $"{numConfiningPressure.Value:N2} MPa",
                $"{numEnergy.Value:N2} J",
                $"{numFrequency.Value:N1} kHz"
            };

            for (int i = 0; i < propertyLabels.Length; i++)
            {
                KryptonLabel lblPropertyName = new KryptonLabel
                {
                    Text = propertyLabels[i],
                    Location = new Point(0, currentY),
                    Size = new Size(200, 25),
                    StateCommon = {
                        ShortText = {
                            Font = new Font("Segoe UI", 10),
                            Color1 = Color.White
                        }
                    }
                };
                resultsDetailsPanel.Controls.Add(lblPropertyName);

                KryptonLabel lblPropertyValue = new KryptonLabel
                {
                    Text = propertyValues[i],
                    Location = new Point(220, currentY),
                    Size = new Size(350, 25),
                    StateCommon = {
                        ShortText = {
                            Font = new Font("Segoe UI", 10),
                            Color1 = Color.Silver
                        }
                    }
                };
                resultsDetailsPanel.Controls.Add(lblPropertyValue);
                currentY += spacing;
            }

            // Action buttons at the bottom
            btnRecalculate = new KryptonButton
            {
                Text = "Recalculate Results",
                Location = new Point(0, currentY + 20),
                Size = new Size(180, 35)
            };
            btnRecalculate.Click += (s, e) => BtnStartSimulation_Click(s, e);
            resultsDetailsPanel.Controls.Add(btnRecalculate);

            btnOpenVisualizer = new KryptonButton
            {
                Text = "Open Visualizer",
                Location = new Point(200, currentY + 20),
                Size = new Size(180, 35)
            };
            btnOpenVisualizer.Click += (s, e) => OpenVisualizer();
            resultsDetailsPanel.Controls.Add(btnOpenVisualizer);

            btnExportDetails = new KryptonButton
            {
                Text = "Export Results",
                Location = new Point(400, currentY + 20),
                Size = new Size(180, 35)
            };
            btnExportDetails.Click += (s, e) => ExportResults();
            resultsDetailsPanel.Controls.Add(btnExportDetails);

            btnExportComposite = new KryptonButton
            {
                Text = "Export Composite Image",
                Location = new Point(0, currentY + 70),
                Size = new Size(580, 35)
            };
            btnExportComposite.Click += (s, e) => ExportCompositeImage();
            resultsDetailsPanel.Controls.Add(btnExportComposite);

            // Update the panel height
            resultsDetailsPanel.Height = currentY + 120;

            // Add the details panel to a scroll panel
            Panel scrollPanel = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Color.FromArgb(45, 45, 48)
            };
            scrollPanel.Controls.Add(resultsDetailsPanel);

            // Add scroll panel to the tab
            resultsDetailsTab.Controls.Add(scrollPanel);

            // Update results display with current values
            UpdateResultsDisplay();
        }

        private void UpdateVelocityRange()
        {
            // Calculate min/max values based on trackbar positions and actual velocity range
            double actualMin = 1000;  // Default min velocity
            double actualMax = 5000;  // Default max velocity

            if (simulationResults != null)
            {
                // Use P-wave velocity as the basis for the range
                double midVelocity = simulationResults.PWaveVelocity;
                actualMin = midVelocity * 0.5;  // 50% of P-wave velocity
                actualMax = midVelocity * 1.5;  // 150% of P-wave velocity
            }

            // Scale min/max from trackbar values
            minFilterVelocity = actualMin + (minVelocityTrackBar.Value / 100.0) * (actualMax - actualMin);
            maxFilterVelocity = actualMin + (maxVelocityTrackBar.Value / 100.0) * (actualMax - actualMin);

            // Ensure min is less than max
            if (minFilterVelocity > maxFilterVelocity)
            {
                double temp = minFilterVelocity;
                minFilterVelocity = maxFilterVelocity;
                maxFilterVelocity = temp;
            }
        }
        private void InitializeVelocityField()
        {
            int width = mainForm.GetWidth();
            int height = mainForm.GetHeight();
            int depth = mainForm.GetDepth();

            if (width <= 0 || height <= 0 || depth <= 0 || simulationResults == null)
            {
                velocityField = new float[1, 1, 1];
                velocityHistogram = new float[100];
                return;
            }

            // Create velocity field with proper dimensions
            velocityField = new float[width, height, depth];

            try
            {
                // Get wave field data from simulation for tomographic reconstruction
                var waveFieldSnapshot = GetWaveFieldSnapshot();
                double[,,] pWaveField = waveFieldSnapshot.vx; // P-wave component
                double[,,] sWaveField = waveFieldSnapshot.vy; // S-wave component (shear)

                // Check if we have valid wave field data
                if (pWaveField == null || pWaveField.GetLength(0) != width ||
                    pWaveField.GetLength(1) != height || pWaveField.GetLength(2) != depth)
                {
                    Logger.Log("[InitializeVelocityField] Warning: Invalid P-wave field data, using density-based estimation");
                    GenerateDensityBasedVelocityField();
                    return;
                }

                // Real tomographic reconstruction based on actual wave travel times
                // For each voxel, calculate local wave velocity from travel time and distance
                int pTravelTime = simulationResults.PWaveTravelTime;
                int sTravelTime = simulationResults.SWaveTravelTime;
                double pWaveVelocity = simulationResults.PWaveVelocity;
                double sWaveVelocity = simulationResults.SWaveVelocity;

                // Prepare histogram bins
                velocityHistogram = new float[100];
                double velocityMin = pWaveVelocity * 0.5;  // Min at 50% of P-wave velocity
                double velocityMax = pWaveVelocity * 1.5;  // Max at 150% of P-wave velocity
                double velocityRange = velocityMax - velocityMin;
                int histogramBins = velocityHistogram.Length;

                // Create a Path3D between TX and RX if available
                List<Point3D> path = _pathPoints;

                // Calculate 3D distance from each voxel to the ray path or direct TX-RX line
                Parallel.For(0, depth, z =>
                {
                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            // Only process points in the selected material
                            if (mainForm.volumeLabels[x, y, z] != selectedMaterialID)
                                continue;

                            // Extract wave amplitude values for this voxel
                            double pAmplitude = Math.Abs(pWaveField[x, y, z]);
                            double sAmplitude = Math.Abs(sWaveField[x, y, z]);

                            // Calculate local velocity using the theorem that phase velocity = wavelength * frequency
                            // We don't have actual phase information, but we can use amplitudes and travel times

                            // Get the distance to TX and RX
                            double distFromTX = Math.Sqrt((x - tx) * (x - tx) +
                                                         (y - ty) * (y - ty) +
                                                         (z - tz) * (z - tz));

                            double distFromRX = Math.Sqrt((x - rx) * (x - rx) +
                                                         (y - ry) * (y - ry) +
                                                         (z - rz) * (z - rz));

                            // Get the closest point on the path if we have a path
                            double distToPath = double.MaxValue;
                            if (path != null && path.Count > 1)
                            {
                                distToPath = DistanceToPath(x, y, z, path);
                            }
                            else
                            {
                                // Calculate distance to direct line if no path
                                distToPath = DistanceToLine(x, y, z, tx, ty, tz, rx, ry, rz);
                            }

                            // Calculate local velocities based on material properties, wave amplitudes and path proximity
                            double velocityBasis = pWaveVelocity; // Use global P-velocity as reference

                            // Base local velocity modulation on signal strength (higher amplitude = faster propagation)
                            double amplitudeModulation = 0;
                            if (pAmplitude > 1e-10 || sAmplitude > 1e-10)
                            {
                                // Use the ratio of local amplitude to expected amplitude based on distance
                                double expectedAmplitude = 1.0 / Math.Max(1, distFromTX); // Inverse with distance
                                double normalizedAmp = (pAmplitude + sAmplitude) / expectedAmplitude;

                                // Convert to velocity modulation with limits
                                amplitudeModulation = Math.Min(0.3, Math.Max(-0.3, 0.2 * Math.Log10(normalizedAmp)));
                            }

                            // Proximity to wave path affects velocity (closer = more accurate)
                            double pathProximityFactor = Math.Max(0, 1.0 - Math.Min(1.0, distToPath / 10.0));

                            // Density contribution - denser material means faster wave propagation
                            double densityFactor = densityVolume[x, y, z] / baseDensity;
                            double densityModulation = 0.15 * (densityFactor - 1.0); // ±15% effect

                            // Combine all factors, emphasizing actual wave data when close to path
                            double localVelocity = velocityBasis * (
                                1.0 + (amplitudeModulation * pathProximityFactor * 0.7) + // Wave data (weighted by proximity)
                                (densityModulation * (1.0 - pathProximityFactor) * 0.5)    // Density more important away from path
                            );

                            // Ensure velocity is within reasonable bounds
                            localVelocity = Math.Max(velocityMin, Math.Min(velocityMax, localVelocity));

                            // Store the velocity
                            velocityField[x, y, z] = (float)localVelocity;

                            // Update histogram (thread-safe way)
                            int binIndex = (int)((localVelocity - velocityMin) / velocityRange * (histogramBins - 1));
                            binIndex = Math.Max(0, Math.Min(histogramBins - 1, binIndex));
                            lock (velocityHistogram)
                            {
                                velocityHistogram[binIndex]++;
                            }
                        }
                    }
                });

                // Normalize histogram
                float maxBinValue = 0;
                foreach (float bin in velocityHistogram)
                {
                    maxBinValue = Math.Max(maxBinValue, bin);
                }

                if (maxBinValue > 0)
                {
                    for (int i = 0; i < velocityHistogram.Length; i++)
                    {
                        velocityHistogram[i] /= maxBinValue;
                    }
                }

                Logger.Log($"[InitializeVelocityField] Velocity field generated using simulation data");
                LogVelocityFieldStats();

                // Analyze the actual velocity range in the data
                AnalyzeVelocityRange();

                // Auto-adjust color scale if enabled
                if (useAdaptiveColorScale)
                {
                    AutoAdjustColorScale();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[InitializeVelocityField] Error: {ex.Message}. Falling back to density estimation.");
                // Fallback to density-based estimation if we can't get actual simulation data
                GenerateDensityBasedVelocityField();
            }
        }

        private void DrawTomographyLegend(Graphics g)
        {
            int legendWidth = 200;
            int legendHeight = 100; // Increased height for more info
            int legendX = tomographyPictureBox.Width - legendWidth - 10;
            int legendY = 10;

            // Draw semi-transparent background
            using (Brush bgBrush = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
            {
                g.FillRectangle(bgBrush, legendX, legendY, legendWidth, legendHeight);
                g.DrawRectangle(Pens.Gray, legendX, legendY, legendWidth, legendHeight);
            }

            // Draw title
            using (Font titleFont = new Font("Segoe UI", 9, FontStyle.Bold))
            using (Brush textBrush = new SolidBrush(Color.White))
            {
                g.DrawString("Velocity Tomography", titleFont, textBrush, legendX + 10, legendY + 5);
            }

            // Draw colorbar
            DrawVelocityColorbar(g, legendX + 10, legendY + 25, legendWidth - 20, 20);

            // Draw velocity range
            using (Font font = new Font("Segoe UI", 8))
            using (Brush textBrush = new SolidBrush(Color.White))
            {
                string minText = $"{minFilterVelocity:F0} m/s";
                string maxText = $"{maxFilterVelocity:F0} m/s";

                // Left aligned min value
                g.DrawString(minText, font, textBrush, legendX + 10, legendY + 50);

                // Right aligned max value
                SizeF maxSize = g.MeasureString(maxText, font);
                g.DrawString(maxText, font, textBrush, legendX + legendWidth - 10 - maxSize.Width, legendY + 50);

                // Display rendering mode
                string renderMode = showFullVolumeInTomography ? "Full Volume Rendering" : "Slice View";
                g.DrawString(renderMode, font, textBrush, legendX + 10, legendY + 70);
            }
        }

        private double DistanceToPath(int x, int y, int z, List<Point3D> path)
        {
            if (path == null || path.Count < 2)
                return double.MaxValue;

            double minDistance = double.MaxValue;

            for (int i = 0; i < path.Count - 1; i++)
            {
                // Calculate distance from point to line segment
                double dist = DistanceToLineSegment(
                    x, y, z,
                    path[i].X, path[i].Y, path[i].Z,
                    path[i + 1].X, path[i + 1].Y, path[i + 1].Z);

                minDistance = Math.Min(minDistance, dist);
            }

            return minDistance;
        }
        private double DistanceToLineSegment(double x, double y, double z,
                                    double x1, double y1, double z1,
                                    double x2, double y2, double z2)
        {
            // Calculate squared length of line segment
            double length_sq = (x2 - x1) * (x2 - x1) +
                              (y2 - y1) * (y2 - y1) +
                              (z2 - z1) * (z2 - z1);

            if (length_sq < 1e-10) // Segment is a point
                return Math.Sqrt((x - x1) * (x - x1) +
                                (y - y1) * (y - y1) +
                                (z - z1) * (z - z1));

            // Calculate projection of point onto line (parametric value t)
            double t = ((x - x1) * (x2 - x1) +
                       (y - y1) * (y2 - y1) +
                       (z - z1) * (z2 - z1)) / length_sq;

            // Clamp t to [0, 1] for line segment
            t = Math.Max(0, Math.Min(1, t));

            // Calculate closest point on line segment
            double closestX = x1 + t * (x2 - x1);
            double closestY = y1 + t * (y2 - y1);
            double closestZ = z1 + t * (z2 - z1);

            // Calculate distance from point to closest point on segment
            return Math.Sqrt((x - closestX) * (x - closestX) +
                            (y - closestY) * (y - closestY) +
                            (z - closestZ) * (z - closestZ));
        }
        private double DistanceToLine(double x, double y, double z,
                             double x1, double y1, double z1,
                             double x2, double y2, double z2)
        {
            // Vector representing the line
            double dx = x2 - x1;
            double dy = y2 - y1;
            double dz = z2 - z1;

            // Line length squared
            double length_sq = dx * dx + dy * dy + dz * dz;

            // Avoid division by zero
            if (length_sq < 1e-10)
                return Math.Sqrt((x - x1) * (x - x1) + (y - y1) * (y - y1) + (z - z1) * (z - z1));

            // Calculate cross product magnitude
            double crossX = (y - y1) * dz - (z - z1) * dy;
            double crossY = (z - z1) * dx - (x - x1) * dz;
            double crossZ = (x - x1) * dy - (y - y1) * dx;

            double cross_sq = crossX * crossX + crossY * crossY + crossZ * crossZ;

            // Distance = |cross product| / |line vector|
            return Math.Sqrt(cross_sq / length_sq);
        }
        private void GenerateDensityBasedVelocityField()
        {
            int width = mainForm.GetWidth();
            int height = mainForm.GetHeight();
            int depth = mainForm.GetDepth();

            // Calculate material-specific velocities based on simulation results
            double pWaveVelocity = simulationResults != null ? simulationResults.PWaveVelocity : 3000;

            // Generate histogram bins
            velocityHistogram = new float[100];
            double velocityMin = pWaveVelocity * 0.5;
            double velocityMax = pWaveVelocity * 1.5;
            double velocityRange = velocityMax - velocityMin;

            // Fill velocity field with values based on material density
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        // Only process points in the selected material
                        if (mainForm.volumeLabels[x, y, z] == selectedMaterialID)
                        {
                            // Calculate local velocity based on density relative to base density
                            double densityRatio = densityVolume[x, y, z] / baseDensity;

                            // Wave velocity varies with square root of elastic modulus / density ratio
                            // We approximate this relationship using the density
                            double velocity = pWaveVelocity * Math.Sqrt(densityRatio);

                            // Add spatial variation based on distance from center
                            double centerX = width / 2.0;
                            double centerY = height / 2.0;
                            double centerZ = depth / 2.0;

                            double distFactor = Math.Sqrt(
                                (x - centerX) * (x - centerX) +
                                (y - centerY) * (y - centerY) +
                                (z - centerZ) * (z - centerZ)
                            ) / Math.Sqrt(centerX * centerX + centerY * centerY + centerZ * centerZ);

                            // Add slight position-based variation
                            velocity *= (1.0 - 0.1 * distFactor);

                            // Store the velocity
                            velocityField[x, y, z] = (float)velocity;

                            // Update histogram
                            int binIndex = (int)((velocity - velocityMin) / velocityRange * 99);
                            binIndex = Math.Max(0, Math.Min(99, binIndex));
                            velocityHistogram[binIndex]++;
                        }
                    }
                }
            }

            // Normalize histogram
            float maxBinValue = 0;
            foreach (float bin in velocityHistogram)
            {
                maxBinValue = Math.Max(maxBinValue, bin);
            }

            if (maxBinValue > 0)
            {
                for (int i = 0; i < velocityHistogram.Length; i++)
                {
                    velocityHistogram[i] /= maxBinValue;
                }
            }

            Logger.Log("[InitializeVelocityField] Generated density-based velocity field (fallback)");
        }
        #endregion

        #region Drawing Methods
        private void WaveformPictureBox_Paint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Color.FromArgb(20, 20, 20));

            // If no simulation results, show info message
            if (simulationResults == null)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (Font font = new Font("Segoe UI", 12))
                using (Brush brush = new SolidBrush(Color.White))
                {
                    string message = "No simulation results available.\nRun a simulation first.";
                    SizeF size = g.MeasureString(message, font);
                    g.DrawString(message, font, brush,
                        (waveformPictureBox.Width - size.Width) / 2,
                        (waveformPictureBox.Height - size.Height) / 2);
                }
                return;
            }

            int width = waveformPictureBox.Width;
            int height = waveformPictureBox.Height;

            // Draw grid
            g.SmoothingMode = SmoothingMode.None;
            using (Pen gridPen = new Pen(Color.FromArgb(40, 40, 40)))
            {
                for (int x = 0; x < width; x += 50)
                    g.DrawLine(gridPen, x, 0, x, height);
                for (int y = 0; y < height; y += 50)
                    g.DrawLine(gridPen, 0, y, width, y);
            }

            // Draw center line
            using (Pen centerPen = new Pen(Color.FromArgb(60, 60, 60), 1))
            {
                centerPen.DashStyle = DashStyle.Dash;
                g.DrawLine(centerPen, 0, height / 2, width, height / 2);
            }

            // Get time series data
            float[] pWaveSeries = GenerateWaveformData(true);
            float[] sWaveSeries = GenerateWaveformData(false);

            if (pWaveSeries == null || sWaveSeries == null || pWaveSeries.Length == 0 || sWaveSeries.Length == 0)
                return;

            int centerY = height / 2;

            // Calculate display range based on zoom
            int dataLength = Math.Max(pWaveSeries.Length, sWaveSeries.Length);
            int pointsToShow = (int)(dataLength / waveformZoom);
            int endIndex = dataLength - 1;
            int startIndex = Math.Max(0, endIndex - pointsToShow);

            // Ensure we include both P and S arrivals in view
            startIndex = Math.Min(startIndex, simulationResults.PWaveTravelTime - 50);
            endIndex = Math.Max(endIndex, simulationResults.SWaveTravelTime + 100);
            endIndex = Math.Min(endIndex, dataLength - 1);

            // Calculate x-axis scaling
            float xScale = (float)width / Math.Max(1, endIndex - startIndex);

            // Calculate P and S arrival X positions
            float pArrivalX = (simulationResults.PWaveTravelTime - startIndex) * xScale;
            float sArrivalX = (simulationResults.SWaveTravelTime - startIndex) * xScale;

            // Find max amplitude for y-scaling
            float maxAmplitude = 0.1f;
            for (int i = startIndex; i <= endIndex && i < pWaveSeries.Length; i++)
                maxAmplitude = Math.Max(maxAmplitude, Math.Abs(pWaveSeries[i]));
            for (int i = startIndex; i <= endIndex && i < sWaveSeries.Length; i++)
                maxAmplitude = Math.Max(maxAmplitude, Math.Abs(sWaveSeries[i]));

            float yScale = (height * 0.4f) / Math.Max(0.1f, maxAmplitude);

            // Draw dead time region
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (chkShowDeadTime.Checked && pArrivalX >= 0 && sArrivalX <= width)
            {
                using (Brush deadTimeBrush = new SolidBrush(DeadTimeColor))
                {
                    float startX = Math.Max(0, pArrivalX);
                    float endX = Math.Min(width, sArrivalX);
                    if (endX > startX)
                    {
                        g.FillRectangle(deadTimeBrush, startX, 0, endX - startX, height);

                        using (Font font = new Font("Arial", 9, FontStyle.Bold))
                        using (Brush textBrush = new SolidBrush(Color.Black))
                        {
                            string deadTimeText = "Dead Time";
                            SizeF textSize = g.MeasureString(deadTimeText, font);

                            if (endX - startX > textSize.Width + 10)
                            {
                                float textX = startX + (endX - startX - textSize.Width) / 2;
                                g.DrawString(deadTimeText, font, textBrush, textX, 10);
                            }
                        }
                    }
                }
            }

            // Draw arrival lines
            g.SmoothingMode = SmoothingMode.None;

            // Draw P-wave arrival line
            if (pArrivalX >= 0 && pArrivalX < width)
            {
                using (Pen arrivalPen = new Pen(PWaveColor, 2))
                {
                    arrivalPen.DashStyle = DashStyle.Dash;
                    g.DrawLine(arrivalPen, pArrivalX, 0, pArrivalX, height);
                }
            }

            // Draw S-wave arrival line
            if (sArrivalX >= 0 && sArrivalX < width)
            {
                using (Pen arrivalPen = new Pen(SWaveColor, 2))
                {
                    arrivalPen.DashStyle = DashStyle.Dash;
                    g.DrawLine(arrivalPen, sArrivalX, 0, sArrivalX, height);
                }
            }

            // Draw waveforms
            g.SmoothingMode = SmoothingMode.None;

            // Draw P-wave
            if (chkShowPWave.Checked)
            {
                DrawWaveform(g, pWaveSeries, startIndex, endIndex, xScale, yScale, centerY, PWaveColor);
            }

            // Draw S-wave
            if (chkShowSWave.Checked)
            {
                DrawWaveform(g, sWaveSeries, startIndex, endIndex, xScale, yScale, centerY, SWaveColor);
            }

            // Draw text elements with antialiasing
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Draw arrival labels
            if (pArrivalX >= 0 && pArrivalX < width)
            {
                using (Font font = new Font("Arial", 9, FontStyle.Bold))
                using (Brush textBrush = new SolidBrush(PWaveColor))
                {
                    g.DrawString("P-Wave Arrival", font, textBrush, pArrivalX + 5, height - 40);
                }
            }

            if (sArrivalX >= 0 && sArrivalX < width)
            {
                using (Font font = new Font("Arial", 9, FontStyle.Bold))
                using (Brush textBrush = new SolidBrush(SWaveColor))
                {
                    g.DrawString("S-Wave Arrival", font, textBrush, sArrivalX + 5, height - 20);
                }
            }

            // Draw legend
            DrawWaveformLegend(g, width, height);

            // Draw zoom indicator and time scale
            using (Font font = new Font("Arial", 8))
            using (Brush brush = new SolidBrush(Color.White))
            {
                g.DrawString($"Zoom: {waveformZoom:F1}x", font, brush, 10, 10);

                // Calculate the proper time values using the time step
                double timePerStep = CalculateTimeStep() * 1000; // Convert to ms

                // Fix: Calculate the actual time span of visible data
                double timeSpanMs = (endIndex - startIndex) * timePerStep;
                double startTimeMs = startIndex * timePerStep;
                double endTimeMs = endIndex * timePerStep;

                // Display with proper formatting to avoid "0.00 ms"
                g.DrawString($"Time span: {timeSpanMs:F2} ms", font, brush, 10, 30);
                g.DrawString($"Range: {startTimeMs:F2} - {endTimeMs:F2} ms", font, brush, 10, 50);
            }
        }

        private double CalculateTimeStep()
        {
            // Try to get from simulation if available
            if (simulationResults != null)
            {
                // Calculate based on wave velocity and pixel size
                double velocity = Math.Max(simulationResults.PWaveVelocity, simulationResults.SWaveVelocity);
                float pixelSize = (float)mainForm.GetPixelSize();

                // CFL condition with safety factor
                double dt = 0.2 * pixelSize / velocity;

                // Clamp between reasonable values
                return Math.Max(1e-8, Math.Min(1e-5, dt));
            }

            // Default value - 1 microsecond
            return 1e-6;
        }
        private void DrawWaveform(Graphics g, float[] data, int startIndex, int endIndex,
                         float xScale, float yScale, int centerY, Color color)
        {
            if (data == null || data.Length == 0) return;

            // Ensure we don't exceed the data bounds
            startIndex = Math.Max(0, startIndex);
            endIndex = Math.Min(data.Length - 1, endIndex);

            if (endIndex <= startIndex) return;

            // Create points array for drawing
            List<PointF> points = new List<PointF>();

            
            int step = Math.Max(1, (endIndex - startIndex) / (waveformPictureBox.Width * 2));

            for (int i = startIndex; i <= endIndex; i += step)
            {
                if (i >= data.Length) break;

                float x = (i - startIndex) * xScale;
                float y = centerY - data[i] * yScale;

                // Clamp Y to prevent drawing outside the control
                y = Math.Max(0, Math.Min(waveformPictureBox.Height, y));

                points.Add(new PointF(x, y));
            }

            if (points.Count < 2) return;

            // Draw the waveform with STRAIGHT LINES, not curves
            using (Pen wavePen = new Pen(color, 2))
            {
                // Turn off smoothing for sharp lines
                var oldMode = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.None;

                g.DrawLines(wavePen, points.ToArray());

                g.SmoothingMode = oldMode;
            }

            // Remove the fill or make it optional
            if (false) // Set to true for filling
            {
                // Create fill polygon
                List<PointF> fillPoints = new List<PointF>(points);

                // Add baseline points in reverse order
                for (int i = points.Count - 1; i >= 0; i--)
                {
                    fillPoints.Add(new PointF(points[i].X, centerY));
                }

                using (Brush fillBrush = new SolidBrush(Color.FromArgb(30, color)))
                {
                    // Use FillPolygon instead of FillClosedCurve to avoid smoothing
                    g.FillPolygon(fillBrush, fillPoints.ToArray());
                }
            }
        }
        private void DrawWaveformLegend(Graphics g, int width, int height)
        {
            // Create legend area in the top right
            int legendX = width - 150;
            int legendY = 10;
            int legendWidth = 140;
            int legendHeight = 80;

            // Draw semi-transparent background
            using (Brush bgBrush = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
            {
                g.FillRectangle(bgBrush, legendX, legendY, legendWidth, legendHeight);
                g.DrawRectangle(Pens.Gray, legendX, legendY, legendWidth, legendHeight);
            }

            // Draw legend items
            using (Font font = new Font("Segoe UI", 9))
            {
                int itemY = legendY + 10;

                // P-Wave
                if (chkShowPWave.Checked)
                {
                    g.DrawLine(new Pen(PWaveColor, 2), legendX + 10, itemY + 7, legendX + 30, itemY + 7);
                    g.DrawString("P-Wave", font, new SolidBrush(PWaveColor), legendX + 40, itemY);
                }

                itemY += 20;

                // S-Wave
                if (chkShowSWave.Checked)
                {
                    g.DrawLine(new Pen(SWaveColor, 2), legendX + 10, itemY + 7, legendX + 30, itemY + 7);
                    g.DrawString("S-Wave", font, new SolidBrush(SWaveColor), legendX + 40, itemY);
                }

                itemY += 20;

                // Dead Time
                if (chkShowDeadTime.Checked)
                {
                    g.FillRectangle(new SolidBrush(DeadTimeColor), legendX + 10, itemY + 2, 20, 12);
                    g.DrawString("Dead Time", font, new SolidBrush(Color.Yellow), legendX + 40, itemY);
                }
            }
        }

        private void TomographyPictureBox_Paint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.FromArgb(20, 20, 20));

            // If no simulation results, show info message
            if (simulationResults == null || velocityField == null ||
                velocityField.GetLength(0) <= 1 || velocityField.GetLength(1) <= 1 || velocityField.GetLength(2) <= 1)
            {
                using (Font font = new Font("Segoe UI", 12))
                using (Brush brush = new SolidBrush(Color.White))
                {
                    string message = "No simulation results available.\nRun a simulation first.";
                    SizeF size = g.MeasureString(message, font);
                    g.DrawString(message, font, brush,
                        (tomographyPictureBox.Width - size.Width) / 2,
                        (tomographyPictureBox.Height - size.Height) / 2);
                }
                return;
            }
            int width = velocityField.GetLength(0);
            int height = velocityField.GetLength(1);
            int depth = velocityField.GetLength(2);

            // CENTER OF ROTATION FIX - Always rotate around the center of the volume
            float centerX = width / 2.0f;
            float centerY = height / 2.0f;
            float centerZ = depth / 2.0f;

            // Scale to fit in view - make this constant for consistent size
            float maxDim = Math.Max(Math.Max(width, height), depth);
            float scale = Math.Min(tomographyPictureBox.Width, tomographyPictureBox.Height) * 0.7f / maxDim;

            // IMPORTANT: Apply transformations in the correct order
            // 1. Start with center of picture box
            g.TranslateTransform(tomographyPictureBox.Width / 2 + tomographyPan.X,
                                tomographyPictureBox.Height / 2 + tomographyPan.Y);

            // 2. Apply zoom
            g.ScaleTransform(tomographyZoom, tomographyZoom);

            // 3. Apply rotations
            g.RotateTransform(tomographyRotationY);
            g.RotateTransform(tomographyRotationX, MatrixOrder.Append);

            // 4. Center on volume (subtract half volume size * scale to center)
            g.TranslateTransform(-centerX * scale, -centerY * scale);

            // Draw bounding box wireframe
            using (Pen boxPen = new Pen(Color.FromArgb(150, 150, 150), 1))
            {
                // Draw a wireframe box representing the volume - using actual volume dimensions
                DrawBoundingBox(g, width * scale, height * scale, depth * scale, boxPen);
            }

            // Choose rendering method
            if (showFullVolumeInTomography)
            {
                RenderFullVolume(g, scale);
            }
            else
            {
                // Draw appropriate slice based on active direction
                switch (activeSliceDirection)
                {
                    case 0: // X slice
                        DrawXSlice(g, scale);
                        break;
                    case 1: // Y slice
                        DrawYSlice(g, scale);
                        break;
                    case 2: // Z slice
                        DrawZSlice(g, scale);
                        break;
                }
            }

            // Draw TX and RX markers
            DrawTransducerMarkers(g, scale);

            // Reset transformation
            g.ResetTransform();

            // Draw legend and information
            DrawTomographyLegend(g);
        }
        private Color GetEnhancedVelocityColor(float velocity, float localMin, float localMax)
        {
            // Use the local range to enhance contrast
            double normalizedValue;

            // Add some padding to avoid extreme values
            double padding = (localMax - localMin) * 0.05;
            double effectiveMin = Math.Max(1, localMin - padding);
            double effectiveMax = localMax + padding;

            // Normalize using the local range
            normalizedValue = (velocity - effectiveMin) / (effectiveMax - effectiveMin);
            normalizedValue = Math.Max(0, Math.Min(1, normalizedValue));

            // Create a perceptually balanced colormap (viridis-like)
            // This offers better perceptual distinction for small variations
            if (normalizedValue < 0.2)
            {
                // Dark blue to blue
                double t = normalizedValue / 0.2;
                return Color.FromArgb(
                    255,
                    0,
                    (int)(50 + 100 * t),
                    (int)(100 + 155 * t));
            }
            else if (normalizedValue < 0.4)
            {
                // Blue to cyan
                double t = (normalizedValue - 0.2) / 0.2;
                return Color.FromArgb(
                    255,
                    0,
                    (int)(150 + 105 * t),
                    (int)(255));
            }
            else if (normalizedValue < 0.6)
            {
                // Cyan to green
                double t = (normalizedValue - 0.4) / 0.2;
                return Color.FromArgb(
                    255,
                    (int)(0 + 100 * t),
                    (int)(255),
                    (int)(255 - 55 * t));
            }
            else if (normalizedValue < 0.8)
            {
                // Green to yellow
                double t = (normalizedValue - 0.6) / 0.2;
                return Color.FromArgb(
                    255,
                    (int)(100 + 155 * t),
                    (int)(255),
                    (int)(200 - 200 * t));
            }
            else
            {
                // Yellow to red
                double t = (normalizedValue - 0.8) / 0.2;
                return Color.FromArgb(
                    255,
                    255,
                    (int)(255 - 255 * t),
                    0);
            }
        }
        private void DrawBoundingBox(Graphics g, float width, float height, float depth, Pen pen)
        {
            // Front face
            g.DrawLine(pen, 0, 0, width, 0);
            g.DrawLine(pen, width, 0, width, height);
            g.DrawLine(pen, width, height, 0, height);
            g.DrawLine(pen, 0, height, 0, 0);

            // Back face
            g.DrawLine(pen, 0, 0, -depth, -depth);
            g.DrawLine(pen, width, 0, width - depth, -depth);
            g.DrawLine(pen, width, height, width - depth, height - depth);
            g.DrawLine(pen, 0, height, -depth, height - depth);

            // Connecting lines
            g.DrawLine(pen, -depth, -depth, width - depth, -depth);
            g.DrawLine(pen, width - depth, -depth, width - depth, height - depth);
            g.DrawLine(pen, width - depth, height - depth, -depth, height - depth);
            g.DrawLine(pen, -depth, height - depth, -depth, -depth);
        }

        private void DrawXSlice(Graphics g, float scale)
        {
            // Ensure slice is in valid range
            int width = velocityField.GetLength(0);
            int height = velocityField.GetLength(1);
            int depth = velocityField.GetLength(2);

            int slice = currentSliceX;
            if (slice < 0 || slice >= width)
                return;

            // Create a bitmap with minimum 4 pixels per voxel for better visibility
            Bitmap sliceBitmap = new Bitmap(depth, height);

            // Lock the bitmap for faster access
            Rectangle rect = new Rectangle(0, 0, depth, height);
            System.Drawing.Imaging.BitmapData bmpData = sliceBitmap.LockBits(rect,
                System.Drawing.Imaging.ImageLockMode.WriteOnly,
                sliceBitmap.PixelFormat);

            // Calculate stride and pointer
            int stride = bmpData.Stride;
            IntPtr ptr = bmpData.Scan0;
            int bytesPerPixel = 4; // ARGB format

            // Create byte array to hold the pixel data
            int bytes = stride * height;
            byte[] rgbValues = new byte[bytes];

            // Initialize with transparent black
            for (int i = 0; i < bytes; i++)
                rgbValues[i] = 0;

            // Find actual value range in this slice to enhance color contrast
            float sliceMin = float.MaxValue;
            float sliceMax = float.MinValue;

            // First pass - find min/max values in this slice
            for (int y = 0; y < height; y++)
            {
                for (int z = 0; z < depth; z++)
                {
                    if (slice < velocityField.GetLength(0) &&
                        y < velocityField.GetLength(1) &&
                        z < velocityField.GetLength(2) &&
                        mainForm.volumeLabels[slice, y, z] == selectedMaterialID)
                    {

                        float velocity = velocityField[slice, y, z];
                        if (velocity > 0)
                        {
                            sliceMin = Math.Min(sliceMin, velocity);
                            sliceMax = Math.Max(sliceMax, velocity);
                        }
                    }
                }
            }

            // If we found valid range, use it for enhanced contrast
            bool useSliceRange = (sliceMin < sliceMax);

            // Populate the array with velocity data
            for (int y = 0; y < height; y++)
            {
                for (int z = 0; z < depth; z++)
                {
                    // Calculate position in the bitmap
                    int index = y * stride + z * bytesPerPixel;

                    float velocity = 0;
                    if (slice < velocityField.GetLength(0) &&
                        y < velocityField.GetLength(1) &&
                        z < velocityField.GetLength(2) &&
                        mainForm.volumeLabels[slice, y, z] == selectedMaterialID)
                    {
                        velocity = velocityField[slice, y, z];
                    }

                    // Skip if not in the selected material or not in velocity range
                    if (velocity <= 0 || velocity < minFilterVelocity || velocity > maxFilterVelocity)
                        continue;

                    // Get enhanced color for this slice by using local min/max
                    Color color;
                    if (useSliceRange)
                    {
                        color = GetEnhancedVelocityColor(velocity, sliceMin, sliceMax);
                    }
                    else
                    {
                        color = GetVelocityColor(velocity);
                    }

                    // Make the color more vibrant for better visibility
                    color = Color.FromArgb(
                        255, // Full opacity
                        Math.Min(255, (int)(color.R * 1.2)),
                        Math.Min(255, (int)(color.G * 1.2)),
                        Math.Min(255, (int)(color.B * 1.2))
                    );

                    // Write the color to the bitmap data
                    rgbValues[index + 3] = color.A; // Alpha
                    rgbValues[index + 2] = color.R; // Red
                    rgbValues[index + 1] = color.G; // Green
                    rgbValues[index] = color.B;     // Blue
                }
            }

            // Copy the RGB values back to the bitmap
            System.Runtime.InteropServices.Marshal.Copy(rgbValues, 0, ptr, bytes);

            // Unlock the bitmap
            sliceBitmap.UnlockBits(bmpData);

            // Calculate the correct position for the slice within the bounding box
            // The slice should be positioned at x*scale along the X axis
            float xPos = slice * scale;

            // Draw the slice as a floating pane with proper position
            using (ImageAttributes ia = new ImageAttributes())
            {
                ColorMatrix cm = new ColorMatrix();
                cm.Matrix33 = 0.9f; // 90% opacity
                ia.SetColorMatrix(cm);

                // Draw the slice at the correct position in 3D space
                g.DrawImage(sliceBitmap,
                    new Rectangle((int)xPos, 0, (int)(depth * scale), (int)(height * scale)),
                    0, 0, depth, height,
                    GraphicsUnit.Pixel, ia);
            }

            // Clean up
            sliceBitmap.Dispose();
        }
        private void DrawYSlice(Graphics g, float scale)
        {
            // Ensure slice is in valid range
            int width = velocityField.GetLength(0);
            int height = velocityField.GetLength(1);
            int depth = velocityField.GetLength(2);

            int slice = currentSliceY;
            if (slice < 0 || slice >= height)
                return;

            // Create a bitmap for this slice
            Bitmap sliceBitmap = new Bitmap(width, depth);

            // Lock the bitmap for faster access
            Rectangle rect = new Rectangle(0, 0, width, depth);
            System.Drawing.Imaging.BitmapData bmpData = sliceBitmap.LockBits(rect,
                System.Drawing.Imaging.ImageLockMode.WriteOnly,
                sliceBitmap.PixelFormat);

            int stride = bmpData.Stride;
            IntPtr ptr = bmpData.Scan0;
            int bytesPerPixel = 4;

            int bytes = stride * depth;
            byte[] rgbValues = new byte[bytes];

            // Initialize with transparent black
            for (int i = 0; i < bytes; i++)
                rgbValues[i] = 0;

            // Find actual value range in this slice to enhance color contrast
            float sliceMin = float.MaxValue;
            float sliceMax = float.MinValue;

            // First pass - find min/max values in this slice
            for (int x = 0; x < width; x++)
            {
                for (int z = 0; z < depth; z++)
                {
                    if (x < velocityField.GetLength(0) &&
                        slice < velocityField.GetLength(1) &&
                        z < velocityField.GetLength(2) &&
                        mainForm.volumeLabels[x, slice, z] == selectedMaterialID)
                    {

                        float velocity = velocityField[x, slice, z];
                        if (velocity > 0)
                        {
                            sliceMin = Math.Min(sliceMin, velocity);
                            sliceMax = Math.Max(sliceMax, velocity);
                        }
                    }
                }
            }

            // If we found valid range, use it for enhanced contrast
            bool useSliceRange = (sliceMin < sliceMax);

            // Populate the array with velocity data
            for (int x = 0; x < width; x++)
            {
                for (int z = 0; z < depth; z++)
                {
                    int index = z * stride + x * bytesPerPixel;

                    float velocity = 0;
                    if (x < velocityField.GetLength(0) &&
                        slice < velocityField.GetLength(1) &&
                        z < velocityField.GetLength(2) &&
                        mainForm.volumeLabels[x, slice, z] == selectedMaterialID)
                    {
                        velocity = velocityField[x, slice, z];
                    }

                    // Skip if not in the selected material or velocity range
                    if (velocity <= 0 || velocity < minFilterVelocity || velocity > maxFilterVelocity)
                        continue;

                    // Get enhanced color for this slice by using local min/max
                    Color color;
                    if (useSliceRange)
                    {
                        color = GetEnhancedVelocityColor(velocity, sliceMin, sliceMax);
                    }
                    else
                    {
                        color = GetVelocityColor(velocity);
                    }

                    // Make the color more vibrant for better visibility
                    color = Color.FromArgb(
                        255, // Full opacity
                        Math.Min(255, (int)(color.R * 1.2)),
                        Math.Min(255, (int)(color.G * 1.2)),
                        Math.Min(255, (int)(color.B * 1.2)));

                    // Write the color to the bitmap data
                    rgbValues[index + 3] = color.A;
                    rgbValues[index + 2] = color.R;
                    rgbValues[index + 1] = color.G;
                    rgbValues[index] = color.B;
                }
            }

            // Copy the RGB values back to the bitmap
            System.Runtime.InteropServices.Marshal.Copy(rgbValues, 0, ptr, bytes);
            sliceBitmap.UnlockBits(bmpData);

            // Calculate the correct position for the slice within the bounding box
            float yPos = slice * scale;

            // Draw the X-Z slice at position Y=slice
            using (ImageAttributes ia = new ImageAttributes())
            {
                ColorMatrix cm = new ColorMatrix();
                cm.Matrix33 = 0.9f; // 90% opacity
                ia.SetColorMatrix(cm);

                g.DrawImage(sliceBitmap,
                    new Rectangle(0, (int)yPos, (int)(width * scale), (int)(depth * scale)),
                    0, 0, width, depth,
                    GraphicsUnit.Pixel, ia);
            }

            sliceBitmap.Dispose();
        }
        private void DrawZSlice(Graphics g, float scale)
        {
            // Ensure slice is in valid range
            int width = velocityField.GetLength(0);
            int height = velocityField.GetLength(1);
            int depth = velocityField.GetLength(2);

            int slice = currentSliceZ;
            if (slice < 0 || slice >= depth)
                return;

            // Create a bitmap with better resolution
            Bitmap sliceBitmap = new Bitmap(width, height);

            // Lock the bitmap for faster access
            Rectangle rect = new Rectangle(0, 0, width, height);
            System.Drawing.Imaging.BitmapData bmpData = sliceBitmap.LockBits(rect,
                System.Drawing.Imaging.ImageLockMode.WriteOnly,
                sliceBitmap.PixelFormat);

            int stride = bmpData.Stride;
            IntPtr ptr = bmpData.Scan0;
            int bytesPerPixel = 4;

            int bytes = stride * height;
            byte[] rgbValues = new byte[bytes];

            // Initialize with transparent black
            for (int i = 0; i < bytes; i++)
                rgbValues[i] = 0;

            // Find actual value range in this slice to enhance color contrast
            float sliceMin = float.MaxValue;
            float sliceMax = float.MinValue;

            // First pass - find min/max values in this slice
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    if (x < velocityField.GetLength(0) &&
                        y < velocityField.GetLength(1) &&
                        slice < velocityField.GetLength(2) &&
                        mainForm.volumeLabels[x, y, slice] == selectedMaterialID)
                    {

                        float velocity = velocityField[x, y, slice];
                        if (velocity > 0)
                        {
                            sliceMin = Math.Min(sliceMin, velocity);
                            sliceMax = Math.Max(sliceMax, velocity);
                        }
                    }
                }
            }

            // If we found valid range, use it for enhanced contrast
            bool useSliceRange = (sliceMin < sliceMax);

            // Populate the array with velocity data
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    int index = y * stride + x * bytesPerPixel;

                    float velocity = 0;
                    if (x < velocityField.GetLength(0) &&
                        y < velocityField.GetLength(1) &&
                        slice < velocityField.GetLength(2) &&
                        mainForm.volumeLabels[x, y, slice] == selectedMaterialID)
                    {
                        velocity = velocityField[x, y, slice];
                    }

                    // Skip if not in the selected material or velocity range
                    if (velocity <= 0 || velocity < minFilterVelocity || velocity > maxFilterVelocity)
                        continue;

                    // Get enhanced color for this slice by using local min/max
                    Color color;
                    if (useSliceRange)
                    {
                        color = GetEnhancedVelocityColor(velocity, sliceMin, sliceMax);
                    }
                    else
                    {
                        color = GetVelocityColor(velocity);
                    }

                    // Enhance visibility
                    color = Color.FromArgb(255,
                        Math.Min(255, (int)(color.R * 1.2)),
                        Math.Min(255, (int)(color.G * 1.2)),
                        Math.Min(255, (int)(color.B * 1.2)));

                    // Write the color to the bitmap data
                    rgbValues[index + 3] = color.A;
                    rgbValues[index + 2] = color.R;
                    rgbValues[index + 1] = color.G;
                    rgbValues[index] = color.B;
                }
            }

            // Copy the RGB values back to the bitmap
            System.Runtime.InteropServices.Marshal.Copy(rgbValues, 0, ptr, bytes);
            sliceBitmap.UnlockBits(bmpData);

            // Calculate the Z position in 3D space
            // We need to represent Z as depth into the model
            float zDepth = slice * scale;
            float zOffset = -zDepth; // Negative because Z increases into the screen

            // Draw the X-Y slice at position Z=slice
            using (ImageAttributes ia = new ImageAttributes())
            {
                ColorMatrix cm = new ColorMatrix();
                cm.Matrix33 = 0.9f; // 90% opacity
                ia.SetColorMatrix(cm);

                // To simulate Z position:
                // 1. Translate XY position based on the Z-depth
                // 2. This creates a perspective-like effect
                float perspectiveOffset = zOffset * 0.5f;

                g.DrawImage(sliceBitmap,
                    new Rectangle(
                        (int)perspectiveOffset, // X offset based on Z depth
                        (int)perspectiveOffset, // Y offset based on Z depth
                        (int)(width * scale),
                        (int)(height * scale)),
                    0, 0, width, height,
                    GraphicsUnit.Pixel, ia);
            }

            sliceBitmap.Dispose();
        }
        private void DrawTransducerMarkers(Graphics g, float scale)
        {
            // Draw transmitter (TX) marker
            using (Brush txBrush = new SolidBrush(Color.Yellow))
            using (Pen txPen = new Pen(Color.Black, 1))
            {
                float txSize = 8;
                g.FillEllipse(txBrush,
                              tx * scale - txSize / 2,
                              ty * scale - txSize / 2,
                              txSize, txSize);
                g.DrawEllipse(txPen,
                              tx * scale - txSize / 2,
                              ty * scale - txSize / 2,
                              txSize, txSize);
            }

            // Draw receiver (RX) marker
            using (Brush rxBrush = new SolidBrush(Color.LightGreen))
            using (Pen rxPen = new Pen(Color.Black, 1))
            {
                float rxSize = 8;
                g.FillEllipse(rxBrush,
                              rx * scale - rxSize / 2,
                              ry * scale - rxSize / 2,
                              rxSize, rxSize);
                g.DrawEllipse(rxPen,
                              rx * scale - rxSize / 2,
                              ry * scale - rxSize / 2,
                              rxSize, rxSize);
            }

            // Draw line connecting TX and RX
            using (Pen connectionPen = new Pen(Color.White, 1))
            {
                connectionPen.DashStyle = DashStyle.Dash;
                g.DrawLine(connectionPen,
                           tx * scale, ty * scale,
                           rx * scale, ry * scale);
            }
        }

        private void AnalyzeVelocityRange()
        {
            if (velocityField == null ||
                velocityField.GetLength(0) <= 1 ||
                velocityField.GetLength(1) <= 1 ||
                velocityField.GetLength(2) <= 1)
            {
                return;
            }

            int width = velocityField.GetLength(0);
            int height = velocityField.GetLength(1);
            int depth = velocityField.GetLength(2);

            minVelocityValue = float.MaxValue;
            maxVelocityValue = float.MinValue;
            int nonZeroCount = 0;

            // Analyze actual data range
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        float velocity = velocityField[x, y, z];
                        if (velocity > 0)
                        {
                            minVelocityValue = Math.Min(minVelocityValue, velocity);
                            maxVelocityValue = Math.Max(maxVelocityValue, velocity);
                            nonZeroCount++;
                        }
                    }
                }
            }

            // Log the found range
            if (nonZeroCount > 0)
            {
                Logger.Log($"[AnalyzeVelocityRange] Found velocity range: {minVelocityValue:F1} - {maxVelocityValue:F1} m/s in {nonZeroCount} voxels");
            }
            else
            {
                minVelocityValue = 0;
                maxVelocityValue = 0;
                Logger.Log("[AnalyzeVelocityRange] No non-zero velocity values found");
            }
        }
        private void AutoAdjustColorScale()
        {
            int width = velocityField.GetLength(0);
            int height = velocityField.GetLength(1);
            int depth = velocityField.GetLength(2);

            // Storage for velocity statistics
            List<float> validValues = new List<float>();
            float[] velocityQuantiles = new float[5]; // 0%, 25%, 50%, 75%, 100%

            // Collect all valid velocity values
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        if (mainForm.volumeLabels[x, y, z] == selectedMaterialID)
                        {
                            float velocity = velocityField[x, y, z];
                            if (velocity > 0)
                            {
                                validValues.Add(velocity);
                            }
                        }
                    }
                }
            }

            // Sort values and calculate quantiles
            if (validValues.Count > 0)
            {
                validValues.Sort();

                // Get 0%, 25%, 50%, 75%, 100% quantiles
                velocityQuantiles[0] = validValues[0]; // Min
                velocityQuantiles[1] = validValues[(int)(validValues.Count * 0.25f)];
                velocityQuantiles[2] = validValues[(int)(validValues.Count * 0.5f)];
                velocityQuantiles[3] = validValues[(int)(validValues.Count * 0.75f)];
                velocityQuantiles[4] = validValues[validValues.Count - 1]; // Max

                // Use 5th and 95th percentiles as min/max to avoid outliers
                int p05Index = Math.Max(0, (int)(validValues.Count * 0.05f));
                int p95Index = Math.Min(validValues.Count - 1, (int)(validValues.Count * 0.95f));

                float p05Value = validValues[p05Index];
                float p95Value = validValues[p95Index];

                // Update the filter velocities with a small buffer
                minFilterVelocity = Math.Max(1, p05Value - (p95Value - p05Value) * 0.05f);
                maxFilterVelocity = p95Value + (p95Value - p05Value) * 0.05f;

                // Update trackbar positions
                minVelocityTrackBar.Value = 0;
                maxVelocityTrackBar.Value = 100;

                // Log the results
                Logger.Log($"[AutoAdjustColorScale] Quantiles: Min={velocityQuantiles[0]:F1}, Q1={velocityQuantiles[1]:F1}, " +
                           $"Median={velocityQuantiles[2]:F1}, Q3={velocityQuantiles[3]:F1}, Max={velocityQuantiles[4]:F1}");
                Logger.Log($"[AutoAdjustColorScale] Adjusted range to: {minFilterVelocity:F1} - {maxFilterVelocity:F1} m/s");
            }
        }
        private void DrawVelocityColorbar(Graphics g, int x, int y, int width, int height)
        {
            using (LinearGradientBrush gradientBrush = new LinearGradientBrush(
                new Rectangle(x, y, width, height),
                Color.Blue, Color.Red, LinearGradientMode.Horizontal))
            {
                // Define the color blend for a standard velocity colormap (blue-cyan-green-yellow-red)
                ColorBlend blend = new ColorBlend(5);
                blend.Colors = new Color[] {
                    Color.Blue,
                    Color.Cyan,
                    Color.Green,
                    Color.Yellow,
                    Color.Red
                };
                blend.Positions = new float[] { 0.0f, 0.25f, 0.5f, 0.75f, 1.0f };
                gradientBrush.InterpolationColors = blend;

                // Draw the colorbar
                g.FillRectangle(gradientBrush, x, y, width, height);
                g.DrawRectangle(Pens.White, x, y, width, height);
            }
        }

        private void HistogramPictureBox_Paint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.FromArgb(20, 20, 20));

            // If no histogram data or simulation results, show message
            if (velocityHistogram == null || velocityHistogram.Length == 0 || simulationResults == null)
            {
                using (Font font = new Font("Segoe UI", 9))
                using (Brush brush = new SolidBrush(Color.White))
                {
                    string message = "No histogram data available.\nRun a simulation first.";
                    SizeF size = g.MeasureString(message, font);
                    g.DrawString(message, font, brush,
                        (histogramPictureBox.Width - size.Width) / 2,
                        (histogramPictureBox.Height - size.Height) / 2);
                }
                return;
            }

            int width = histogramPictureBox.Width;
            int height = histogramPictureBox.Height;

            // Draw axes
            using (Pen axisPen = new Pen(Color.White, 1))
            {
                g.DrawLine(axisPen, 0, height - 1, width, height - 1); // x-axis
                g.DrawLine(axisPen, 0, 0, 0, height - 1); // y-axis
            }

            // Calculate bin width
            float binWidth = (float)width / velocityHistogram.Length;

            // Use safe defaults if simulationResults is null
            double velocityMin = 1000;  // Default min velocity
            double velocityMax = 5000;  // Default max velocity

            if (simulationResults != null)
            {
                velocityMin = simulationResults.PWaveVelocity * 0.5;
                velocityMax = simulationResults.PWaveVelocity * 1.5;
            }

            double velocityRange = velocityMax - velocityMin;

            // Calculate filter range indexes safely
            int minFilterIndex = 0;
            int maxFilterIndex = velocityHistogram.Length - 1;

            try
            {
                minFilterIndex = (int)((minFilterVelocity - velocityMin) / velocityRange * (velocityHistogram.Length - 1));
                maxFilterIndex = (int)((maxFilterVelocity - velocityMin) / velocityRange * (velocityHistogram.Length - 1));

                minFilterIndex = Math.Max(0, Math.Min(velocityHistogram.Length - 1, minFilterIndex));
                maxFilterIndex = Math.Max(0, Math.Min(velocityHistogram.Length - 1, maxFilterIndex));
            }
            catch
            {
                // Use defaults on exception
                minFilterIndex = 0;
                maxFilterIndex = velocityHistogram.Length - 1;
            }

            // Draw histogram bars
            for (int i = 0; i < velocityHistogram.Length; i++)
            {
                float barHeight = velocityHistogram[i] * (height - 5);
                float x = i * binWidth;

                // Determine bar color based on velocity
                Color barColor;
                float velocity = (float)(velocityMin + (i / (float)(velocityHistogram.Length - 1)) * velocityRange);

                // Check if in the selected range
                bool inSelectedRange = (i >= minFilterIndex && i <= maxFilterIndex);

                // Get velocity color for the bar
                barColor = GetVelocityColor(velocity);

                // Make the bar dimmer if outside the selected range
                if (!inSelectedRange)
                {
                    barColor = Color.FromArgb(50, barColor);
                }

                using (Brush barBrush = new SolidBrush(barColor))
                {
                    g.FillRectangle(barBrush, x, height - barHeight - 1, binWidth, barHeight);
                }
            }

            // Draw filter range markers
            float minFilterX = minFilterIndex * binWidth;
            float maxFilterX = maxFilterIndex * binWidth + binWidth;

            using (Pen filterPen = new Pen(Color.White, 2))
            {
                // Min filter marker
                g.DrawLine(filterPen, minFilterX, 0, minFilterX, height);

                // Max filter marker
                g.DrawLine(filterPen, maxFilterX, 0, maxFilterX, height);
            }

            // Draw filter labels
            using (Font font = new Font("Segoe UI", 8))
            using (Brush textBrush = new SolidBrush(Color.White))
            using (Brush shadowBrush = new SolidBrush(Color.FromArgb(128, 0, 0, 0)))
            {
                string minText = $"{minFilterVelocity:F0}";
                string maxText = $"{maxFilterVelocity:F0}";

                // Draw min value with shadow
                SizeF minSize = g.MeasureString(minText, font);
                g.FillRectangle(shadowBrush, minFilterX - minSize.Width / 2, 5, minSize.Width, minSize.Height);
                g.DrawString(minText, font, textBrush, minFilterX - minSize.Width / 2, 5);

                // Draw max value with shadow
                SizeF maxSize = g.MeasureString(maxText, font);
                g.FillRectangle(shadowBrush, maxFilterX - maxSize.Width / 2, 5, maxSize.Width, maxSize.Height);
                g.DrawString(maxText, font, textBrush, maxFilterX - maxSize.Width / 2, 5);
            }
        }

        private Color GetVelocityColor(float velocity)
        {
            // Use adaptive color scale based on actual velocity data or predefined ranges
            double velocityMin, velocityMax;

            if (useAdaptiveColorScale && minVelocityValue > 0 && maxVelocityValue > minVelocityValue)
            {
                // Use actual data range with small padding
                double padding = (maxVelocityValue - minVelocityValue) * 0.05; // 5% padding
                velocityMin = minVelocityValue - padding;
                velocityMax = maxVelocityValue + padding;
            }
            else
            {
                // Fallback to simulation-based reference velocity
                double referenceVelocity = simulationResults != null ? simulationResults.PWaveVelocity : 3000;
                velocityMin = referenceVelocity * 0.5;  // 50% of P-wave velocity
                velocityMax = referenceVelocity * 1.5;  // 150% of P-wave velocity
            }

            // Ensure velocity is in the range
            velocityMin = Math.Max(1, velocityMin); // Avoid zero or negative

            // Calculate normalized value in range [0-1]
            double normalizedValue = (velocity - velocityMin) / (velocityMax - velocityMin);
            normalizedValue = Math.Max(0, Math.Min(1, normalizedValue));

            // Map to colormap
            if (normalizedValue < 0.25)
            {
                // Blue to Cyan
                double t = normalizedValue * 4;
                return Color.FromArgb(
                    255, // Full opacity
                    0,
                    (int)(255 * t),
                    255);
            }
            else if (normalizedValue < 0.5)
            {
                // Cyan to Green
                double t = (normalizedValue - 0.25) * 4;
                return Color.FromArgb(
                    255, // Full opacity
                    0,
                    255,
                    (int)(255 * (1 - t)));
            }
            else if (normalizedValue < 0.75)
            {
                // Green to Yellow
                double t = (normalizedValue - 0.5) * 4;
                return Color.FromArgb(
                    255, // Full opacity
                    (int)(255 * t),
                    255,
                    0);
            }
            else
            {
                // Yellow to Red
                double t = (normalizedValue - 0.75) * 4;
                return Color.FromArgb(
                    255, // Full opacity
                    255,
                    (int)(255 * (1 - t)),
                    0);
            }
        }
        private void RenderVolumeZSlices(Graphics g, float scale, int stepSize, bool reverse)
        {
            int width = velocityField.GetLength(0);
            int height = velocityField.GetLength(1);
            int depth = velocityField.GetLength(2);

            // Analyze the data range for this rendering to enhance contrast
            float volMin = float.MaxValue;
            float volMax = float.MinValue;
            int validVoxels = 0;

            // Find actual min/max values in the visible volume
            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        if (mainForm.volumeLabels[x, y, z] == selectedMaterialID)
                        {
                            float velocity = velocityField[x, y, z];
                            if (velocity > 0)
                            {
                                volMin = Math.Min(volMin, velocity);
                                volMax = Math.Max(volMax, velocity);
                                validVoxels++;
                            }
                        }
                    }
                }
            }

            bool useEnhancedColors = (volMin < volMax && validVoxels > 0);

            // Render each slice from back to front for proper transparency
            for (int i = 0; i < depth; i += stepSize)
            {
                int z = reverse ? i : depth - 1 - i;

                // Skip slices with no material for performance
                bool hasVisibleMaterial = false;
                for (int y = 0; y < height && !hasVisibleMaterial; y++)
                {
                    for (int x = 0; x < width && !hasVisibleMaterial; x++)
                    {
                        if (mainForm.volumeLabels[x, y, z] == selectedMaterialID)
                        {
                            hasVisibleMaterial = true;
                        }
                    }
                }

                if (!hasVisibleMaterial) continue;

                // Create a bitmap for this slice
                using (Bitmap sliceBitmap = new Bitmap(width, height))
                {
                    // Lock the bitmap for faster access
                    Rectangle rect = new Rectangle(0, 0, width, height);
                    BitmapData bmpData = sliceBitmap.LockBits(rect,
                        ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

                    int stride = bmpData.Stride;
                    IntPtr ptr = bmpData.Scan0;
                    int bytesPerPixel = 4;

                    // Create byte array to hold pixel data
                    int bytes = stride * height;
                    byte[] rgbValues = new byte[bytes];

                    // Initialize with transparent
                    for (int j = 0; j < bytes; j++)
                        rgbValues[j] = 0;

                    // Fill with velocity data for this slice
                    for (int x = 0; x < width; x++)
                    {
                        for (int y = 0; y < height; y++)
                        {
                            // Calculate position in bitmap
                            int index = y * stride + x * bytesPerPixel;

                            if (x < width && y < height && z < depth &&
                                mainForm.volumeLabels[x, y, z] == selectedMaterialID)
                            {
                                float velocity = velocityField[x, y, z];

                                // Skip if not in range
                                if (velocity <= 0 || velocity < minFilterVelocity || velocity > maxFilterVelocity)
                                    continue;

                                // Choose between enhanced or standard colors
                                Color color;
                                if (useEnhancedColors)
                                {
                                    color = GetEnhancedVelocityColor(velocity, volMin, volMax);
                                }
                                else
                                {
                                    color = GetVelocityColor(velocity);
                                }

                                // Apply volume opacity - make deeper slices more transparent
                                int alpha = (int)(255 * volumeOpacity * (0.5f + 0.5f * i / (float)depth));
                                alpha = Math.Max(0, Math.Min(255, alpha));

                                // Write BGRA values
                                rgbValues[index + 3] = (byte)alpha;  // Alpha 
                                rgbValues[index + 2] = color.R;      // Red
                                rgbValues[index + 1] = color.G;      // Green
                                rgbValues[index] = color.B;          // Blue
                            }
                        }
                    }

                    // Copy data back to bitmap
                    System.Runtime.InteropServices.Marshal.Copy(rgbValues, 0, ptr, bytes);
                    sliceBitmap.UnlockBits(bmpData);

                    // Calculate Z-offset for this slice - this creates the 3D effect
                    float zOffset = (z - depth / 2) * scale * 0.5f;

                    // Draw the slice with proper Z position by modifying XY position
                    // This simulates Z depth without using a 3D matrix
                    g.DrawImage(sliceBitmap,
                        new Rectangle(
                            (int)(zOffset * 0.5f),           // X offset based on Z position
                            (int)(zOffset * 0.5f),           // Y offset based on Z position
                            (int)(width * scale),
                            (int)(height * scale)),
                        0, 0, width, height,
                        GraphicsUnit.Pixel);
                }
            }
        }
        private void RenderVolumeYSlices(Graphics g, float scale, int stepSize, bool reverse)
        {
            int width = velocityField.GetLength(0);
            int height = velocityField.GetLength(1);
            int depth = velocityField.GetLength(2);

            // Analyze the data range for this rendering to enhance contrast
            float volMin = float.MaxValue;
            float volMax = float.MinValue;
            int validVoxels = 0;

            // Find actual min/max values in the visible volume
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int z = 0; z < depth; z++)
                    {
                        if (mainForm.volumeLabels[x, y, z] == selectedMaterialID)
                        {
                            float velocity = velocityField[x, y, z];
                            if (velocity > 0)
                            {
                                volMin = Math.Min(volMin, velocity);
                                volMax = Math.Max(volMax, velocity);
                                validVoxels++;
                            }
                        }
                    }
                }
            }

            bool useEnhancedColors = (volMin < volMax && validVoxels > 0);

            // Render each slice from back to front for proper transparency
            for (int i = 0; i < height; i += stepSize)
            {
                int y = reverse ? i : height - 1 - i;

                // Skip slices with no material for performance
                bool hasVisibleMaterial = false;
                for (int x = 0; x < width && !hasVisibleMaterial; x++)
                {
                    for (int z = 0; z < depth && !hasVisibleMaterial; z++)
                    {
                        if (mainForm.volumeLabels[x, y, z] == selectedMaterialID)
                        {
                            hasVisibleMaterial = true;
                        }
                    }
                }

                if (!hasVisibleMaterial) continue;

                // Create a bitmap for this slice
                using (Bitmap sliceBitmap = new Bitmap(width, depth))
                {
                    // Lock the bitmap for faster access
                    Rectangle rect = new Rectangle(0, 0, width, depth);
                    BitmapData bmpData = sliceBitmap.LockBits(rect,
                        ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

                    int stride = bmpData.Stride;
                    IntPtr ptr = bmpData.Scan0;
                    int bytesPerPixel = 4;

                    // Create byte array to hold pixel data
                    int bytes = stride * depth;
                    byte[] rgbValues = new byte[bytes];

                    // Initialize with transparent black
                    for (int j = 0; j < bytes; j++)
                        rgbValues[j] = 0;

                    // Fill with velocity data for this slice
                    for (int x = 0; x < width; x++)
                    {
                        for (int z = 0; z < depth; z++)
                        {
                            // Calculate position in bitmap
                            int index = z * stride + x * bytesPerPixel;

                            if (mainForm.volumeLabels[x, y, z] == selectedMaterialID)
                            {
                                float velocity = velocityField[x, y, z];

                                // Skip if not in range
                                if (velocity <= 0 || velocity < minFilterVelocity || velocity > maxFilterVelocity)
                                    continue;

                                // Choose between enhanced or standard colors
                                Color color;
                                if (useEnhancedColors)
                                {
                                    color = GetEnhancedVelocityColor(velocity, volMin, volMax);
                                }
                                else
                                {
                                    color = GetVelocityColor(velocity);
                                }

                                // Apply volume opacity
                                int alpha = (int)(255 * volumeOpacity * (0.5f + 0.5f * i / (float)height));
                                alpha = Math.Max(0, Math.Min(255, alpha));

                                // Write BGRA values
                                rgbValues[index + 3] = (byte)alpha;   // Alpha
                                rgbValues[index + 2] = color.R;      // Red
                                rgbValues[index + 1] = color.G;      // Green
                                rgbValues[index] = color.B;          // Blue
                            }
                        }
                    }

                    // Copy data back to bitmap
                    System.Runtime.InteropServices.Marshal.Copy(rgbValues, 0, ptr, bytes);
                    sliceBitmap.UnlockBits(bmpData);

                    // Calculate Y position in model space
                    float yPos = y * scale;

                    // Draw the slice at the correct Y position
                    g.DrawImage(sliceBitmap,
                        new Rectangle(
                            0,                            // X position
                            (int)yPos,                    // Y position
                            (int)(width * scale),
                            (int)(depth * scale)),
                        0, 0, width, depth,
                        GraphicsUnit.Pixel);
                }
            }
        }
        private void RenderVolumeXSlices(Graphics g, float scale, int stepSize, bool reverse)
        {
            int width = velocityField.GetLength(0);
            int height = velocityField.GetLength(1);
            int depth = velocityField.GetLength(2);

            // Analyze the data range for this rendering to enhance contrast
            float volMin = float.MaxValue;
            float volMax = float.MinValue;
            int validVoxels = 0;

            // Find actual min/max values in the visible volume
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int z = 0; z < depth; z++)
                    {
                        if (mainForm.volumeLabels[x, y, z] == selectedMaterialID)
                        {
                            float velocity = velocityField[x, y, z];
                            if (velocity > 0)
                            {
                                volMin = Math.Min(volMin, velocity);
                                volMax = Math.Max(volMax, velocity);
                                validVoxels++;
                            }
                        }
                    }
                }
            }

            bool useEnhancedColors = (volMin < volMax && validVoxels > 0);

            // Render each slice from back to front for proper transparency
            for (int i = 0; i < width; i += stepSize)
            {
                int x = reverse ? i : width - 1 - i;

                // Skip slices with no material for performance
                bool hasVisibleMaterial = false;
                for (int y = 0; y < height && !hasVisibleMaterial; y++)
                {
                    for (int z = 0; z < depth && !hasVisibleMaterial; z++)
                    {
                        if (mainForm.volumeLabels[x, y, z] == selectedMaterialID)
                        {
                            hasVisibleMaterial = true;
                        }
                    }
                }

                if (!hasVisibleMaterial) continue;

                // Create a bitmap for this slice
                using (Bitmap sliceBitmap = new Bitmap(depth, height))
                {
                    // Lock the bitmap for faster access
                    Rectangle rect = new Rectangle(0, 0, depth, height);
                    BitmapData bmpData = sliceBitmap.LockBits(rect,
                        ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

                    int stride = bmpData.Stride;
                    IntPtr ptr = bmpData.Scan0;
                    int bytesPerPixel = 4;

                    // Create byte array to hold pixel data
                    int bytes = stride * height;
                    byte[] rgbValues = new byte[bytes];

                    // Initialize with transparent black
                    for (int j = 0; j < bytes; j++)
                        rgbValues[j] = 0;

                    // Fill with velocity data for this slice
                    for (int y = 0; y < height; y++)
                    {
                        for (int z = 0; z < depth; z++)
                        {
                            // Calculate position in bitmap
                            int index = y * stride + z * bytesPerPixel;

                            if (mainForm.volumeLabels[x, y, z] == selectedMaterialID)
                            {
                                float velocity = velocityField[x, y, z];

                                // Skip if not in range
                                if (velocity <= 0 || velocity < minFilterVelocity || velocity > maxFilterVelocity)
                                    continue;

                                // Choose between enhanced or standard colors
                                Color color;
                                if (useEnhancedColors)
                                {
                                    color = GetEnhancedVelocityColor(velocity, volMin, volMax);
                                }
                                else
                                {
                                    color = GetVelocityColor(velocity);
                                }

                                // Apply volume opacity - make deeper slices more transparent
                                int alpha = (int)(255 * volumeOpacity * (0.5f + 0.5f * i / (float)width));
                                alpha = Math.Max(0, Math.Min(255, alpha));

                                // Write BGRA values
                                rgbValues[index + 3] = (byte)alpha;   // Alpha
                                rgbValues[index + 2] = color.R;      // Red
                                rgbValues[index + 1] = color.G;      // Green
                                rgbValues[index] = color.B;          // Blue
                            }
                        }
                    }

                    // Copy data back to bitmap
                    System.Runtime.InteropServices.Marshal.Copy(rgbValues, 0, ptr, bytes);
                    sliceBitmap.UnlockBits(bmpData);

                    // Calculate X position for this slice
                    float xOffset = (x - width / 2) * scale * 0.5f;

                    // Draw the slice at the correct X position
                    // We simulate depth by offsetting in the screen XY plane
                    g.DrawImage(sliceBitmap,
                        new Rectangle(
                            (int)(x * scale),             // Actual X position 
                            0,                            // Y position
                            (int)(depth * scale),
                            (int)(height * scale)),
                        0, 0, depth, height,
                        GraphicsUnit.Pixel);
                }
            }
        }
        private void RenderFullVolume(Graphics g, float scale)
        {
            int width = velocityField.GetLength(0);
            int height = velocityField.GetLength(1);
            int depth = velocityField.GetLength(2);

            // We need to render from back to front for proper transparency
            // First, determine the viewing direction based on rotation
            float cosX = (float)Math.Cos(tomographyRotationX * Math.PI / 180);
            float sinX = (float)Math.Sin(tomographyRotationX * Math.PI / 180);
            float cosY = (float)Math.Cos(tomographyRotationY * Math.PI / 180);
            float sinY = (float)Math.Sin(tomographyRotationY * Math.PI / 180);

            // Simplified volume rendering using a slice-based approach
            // This renders multiple semi-transparent slices to create a 3D effect
            int sliceCount = (int)Math.Sqrt(width * width + height * height + depth * depth); // Diagonal distance
            int stepSize = Math.Max(1, sliceCount / 40); // Limit to ~40 slices for performance

            // Determine which axis to slice based on view direction
            int sliceAxis;
            float majorY = Math.Abs(sinX);
            float majorX = Math.Abs(sinY * cosX);
            float majorZ = Math.Abs(cosY * cosX);

            if (majorX >= majorY && majorX >= majorZ)
                sliceAxis = 0; // X axis
            else if (majorY >= majorX && majorY >= majorZ)
                sliceAxis = 1; // Y axis
            else
                sliceAxis = 2; // Z axis

            // Determine slice direction (forward or backward)
            bool reverseSlices = ((sliceAxis == 0 && sinY > 0) ||
                                 (sliceAxis == 1 && sinX < 0) ||
                                 (sliceAxis == 2 && cosY < 0));

            // Render slices from back to front
            switch (sliceAxis)
            {
                case 0: // X slices
                    RenderVolumeXSlices(g, scale, stepSize, reverseSlices);
                    break;
                case 1: // Y slices
                    RenderVolumeYSlices(g, scale, stepSize, reverseSlices);
                    break;
                case 2: // Z slices
                    RenderVolumeZSlices(g, scale, stepSize, reverseSlices);
                    break;
            }
        }
        private float[] GenerateWaveformData(bool isPWave)
        {
            if (simulationResults == null)
                return new float[1];

            // Try to get cached data first
            float[] cachedData = GetCachedWaveformData(isPWave);
            if (cachedData != null && cachedData.Length > 0)
            {
                // Ensure the data is long enough to include S-wave arrival
                if (cachedData.Length < simulationResults.SWaveTravelTime + 100)
                {
                    // Extend the array to include S-wave arrival
                    float[] extendedData = new float[simulationResults.SWaveTravelTime + 200];
                    Array.Copy(cachedData, extendedData, cachedData.Length);

                    // Fill remaining with decay/noise
                    for (int i = cachedData.Length; i < extendedData.Length; i++)
                    {
                        float decay = (float)Math.Exp(-(i - cachedData.Length) / 100.0);
                        extendedData[i] = cachedData[cachedData.Length - 1] * decay * 0.1f;
                    }

                    return extendedData;
                }
                return cachedData;
            }

            // Get real-time data if possible
            float[] realTimeData = GetRealTimeWaveformData(isPWave);
            if (realTimeData != null && realTimeData.Length > 0)
            {
                // Ensure the data is long enough
                if (realTimeData.Length < simulationResults.SWaveTravelTime + 100)
                {
                    float[] extendedData = new float[simulationResults.SWaveTravelTime + 200];
                    Array.Copy(realTimeData, extendedData, realTimeData.Length);
                    return extendedData;
                }
                return realTimeData;
            }

            // Generate approximated waveform as fallback
            return GenerateApproximatedWaveform(isPWave);
        }

        private float[] GetCachedWaveformData(bool isPWave)
        {
            try
            {
                // Use existing cache manager if available
                if (cacheManager != null && cacheManager.FrameCount > 0)
                {
                    return GetCachedWaveformDataOptimized(isPWave);
                }

                // Create a read-only cache manager for loading existing data
                string metadataPath = Path.Combine(FrameCacheManager.SINGLE_CACHE_DIR, "metadata.dat");
                if (!File.Exists(metadataPath))
                    return null;

                // Create temporary cache manager for reading
                using (var tempCacheManager = new FrameCacheManager(mainForm.GetWidth(), mainForm.GetHeight(), mainForm.GetDepth(), true))
                {
                    tempCacheManager.LoadMetadata();

                    if (tempCacheManager.FrameCount == 0)
                        return null;

                    // Pre-allocate array for better performance
                    float[] timeSeries = new float[tempCacheManager.FrameCount];

                    // Load data in batches to avoid memory issues
                    const int BATCH_SIZE = 100;
                    int processed = 0;

                    while (processed < tempCacheManager.FrameCount)
                    {
                        int batchEnd = Math.Min(processed + BATCH_SIZE, tempCacheManager.FrameCount);

                        // Load batch in parallel
                        Parallel.For(processed, batchEnd, i =>
                        {
                            try
                            {
                                // Only load metadata, not full frame data
                                var metadata = tempCacheManager.GetFrameMetadata(i);
                                if (metadata != null)
                                {
                                    timeSeries[i] = isPWave ? metadata.PWaveValue : metadata.SWaveValue;
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Log($"[GetCachedWaveformData] Error loading frame {i}: {ex.Message}");
                            }
                        });

                        processed = batchEnd;

                        // Allow UI to remain responsive
                        if (processed % 500 == 0)
                        {
                            System.Windows.Forms.Application.DoEvents();
                        }
                    }

                    // Apply Ricker wavelet shaping
                    return ApplyImprovedRickerShaping(timeSeries, isPWave);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[GetCachedWaveformData] Error loading cached data: {ex.Message}");
                return null;
            }
        }
        private float[] GetCachedWaveformDataOptimized(bool isPWave)
        {
            if (cacheManager == null || cacheManager.FrameCount == 0)
                return null;

            // Pre-allocate array
            float[] timeSeries = new float[cacheManager.FrameCount];

            // Process in batches to avoid loading everything at once
            const int BATCH_SIZE = 50;
            int totalFrames = cacheManager.FrameCount;

            // Use a task to process batches asynchronously
            var tasks = new List<Task>();

            for (int start = 0; start < totalFrames; start += BATCH_SIZE)
            {
                int batchStart = start;
                int batchEnd = Math.Min(start + BATCH_SIZE, totalFrames);

                tasks.Add(Task.Run(() =>
                {
                    for (int i = batchStart; i < batchEnd; i++)
                    {
                        try
                        {
                            // First try to get just the time series values
                            var timeSeriesOnly = cacheManager.LoadTimeSeriesOnly(i);
                            if (timeSeriesOnly.pWave != null && timeSeriesOnly.sWave != null)
                            {
                                timeSeries[i] = isPWave ? timeSeriesOnly.pWave[0] : timeSeriesOnly.sWave[0];
                            }
                            else
                            {
                                // Fallback to metadata only
                                var metadata = cacheManager.GetFrameMetadata(i);
                                if (metadata != null)
                                {
                                    timeSeries[i] = isPWave ? metadata.PWaveValue : metadata.SWaveValue;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"[GetCachedWaveformDataOptimized] Error loading frame {i}: {ex.Message}");
                        }
                    }
                }));
            }

            // Wait for all tasks to complete with a timeout
            if (!Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(30)))
            {
                Logger.Log("[GetCachedWaveformDataOptimized] Warning: Timeout loading waveform data");
            }

            // Apply shaping
            return ApplyImprovedRickerShaping(timeSeries, isPWave);
        }
        private float[] ApplyImprovedRickerShaping(float[] rawData, bool isPWave)
        {
            if (rawData == null || rawData.Length == 0)
                return rawData;

            int len = rawData.Length;
            int arrivalIdx = isPWave
                ? simulationResults.PWaveTravelTime
                : simulationResults.SWaveTravelTime;

            /* -------- 1.  locate first significant sample in the trace -------- */
            float maxAbs = 0f;
            for (int i = 0; i < len; i++)
            {
                float a = Math.Abs(rawData[i]);
                if (a > maxAbs) maxAbs = a;
            }
            float threshold = maxAbs * 0.05f;            // 5 % of peak

            int firstIdx = 0;                             // default to 0 if all zeros
            for (int i = 0; i < len; i++)
            {
                if (Math.Abs(rawData[i]) >= threshold)
                {
                    firstIdx = i;
                    break;
                }
            }

            /* -------- 2.  compute shift so firstIdx → arrivalIdx --------------- */
            int shift = arrivalIdx - firstIdx;
            if (shift == 0)                               // already aligned
                return rawData;

            float[] shifted = new float[len];

            if (shift > 0)
            {
                /* move pulse to the right; pre-arrival region becomes zeros */
                int copyLen = len - shift;
                if (copyLen > 0)
                    Array.Copy(rawData, 0, shifted, shift, copyLen);
                // leading 'shift' samples remain zero
            }
            else
            {
                /* shift < 0  → pulse is too far right, move it left */
                int copyLen = len + shift;                // shift is negative here
                if (copyLen > 0)
                    Array.Copy(rawData, -shift, shifted, 0, copyLen);
                // tail that falls off the end is discarded
            }

            return shifted;
        }

        private float[] ApplyRickerShaping(float[] rawData, bool isPWave)
        {
            if (rawData == null || rawData.Length == 0)
                return rawData;

            // Calculate parameters
            double frequency = (double)(numFrequency.Value * 1000); // kHz to Hz
            double dt = CalculateTimeStep();

            // Find the arrival time by detecting the first significant amplitude
            int arrivalIndex = 0;
            float maxAbs = rawData.Max(x => Math.Abs(x));
            float threshold = 0.01f * maxAbs;

            for (int i = 0; i < rawData.Length; i++)
            {
                if (Math.Abs(rawData[i]) > threshold)
                {
                    arrivalIndex = i;
                    break;
                }
            }

            // Store the original arrival index to ensure marker alignment
            int originalArrivalIndex = isPWave ? simulationResults.PWaveTravelTime : simulationResults.SWaveTravelTime;

            // Apply a simple smoothing instead of full Ricker transformation
            float[] smoothed = new float[rawData.Length];

            // Keep the pre-arrival noise as-is
            for (int i = 0; i < arrivalIndex; i++)
            {
                smoothed[i] = rawData[i];
            }

            // Apply Ricker-like shaping only after arrival
            for (int i = arrivalIndex; i < rawData.Length; i++)
            {
                // Calculate relative time from the ORIGINAL arrival time, not the detected one
                double t = (i - originalArrivalIndex) * dt;

                if (t < 0)
                {
                    // Before the official arrival time, use original data
                    smoothed[i] = rawData[i];
                    continue;
                }

                // Ricker wavelet parameters
                double fpeak = frequency;
                double sigma = Math.Sqrt(2) / (Math.PI * fpeak);
                double tNorm = t / sigma;

                // Ricker wavelet formula
                double tSquared = tNorm * tNorm;
                double rickerEnvelope = Math.Exp(-0.25 * tSquared); // Just the envelope, not the full Ricker

                // Apply the envelope to the original signal to preserve timing
                smoothed[i] = (float)(rawData[i] * rickerEnvelope);

                // Add some high-frequency content to make it sharper
                if (i > arrivalIndex + 2)
                {
                    double highFreq = Math.Sin(2 * Math.PI * t * frequency * 2);
                    smoothed[i] += (float)(rawData[i] * highFreq * rickerEnvelope * 0.2);
                }
            }

            // Apply minimal filtering to remove extreme spikes but preserve timing
            for (int pass = 0; pass < 2; pass++)
            {
                float[] filtered = new float[smoothed.Length];
                for (int i = 1; i < smoothed.Length - 1; i++)
                {
                    filtered[i] = (smoothed[i - 1] * 0.25f + smoothed[i] * 0.5f + smoothed[i + 1] * 0.25f);
                }
                filtered[0] = smoothed[0];
                filtered[smoothed.Length - 1] = smoothed[smoothed.Length - 1];
                smoothed = filtered;
            }

            return smoothed;
        }
        private float[] ExtractEnvelope(float[] data)
        {
            float[] envelope = new float[data.Length];
            int windowSize = 10; // Adjust for smoothness

            for (int i = 0; i < data.Length; i++)
            {
                float sum = 0;
                int count = 0;

                for (int j = Math.Max(0, i - windowSize); j <= Math.Min(data.Length - 1, i + windowSize); j++)
                {
                    sum += Math.Abs(data[j]);
                    count++;
                }

                envelope[i] = sum / count;
            }

            // Smooth the envelope
            for (int pass = 0; pass < 3; pass++)
            {
                float[] smoothed = new float[envelope.Length];
                for (int i = 1; i < envelope.Length - 1; i++)
                {
                    smoothed[i] = (envelope[i - 1] + 2 * envelope[i] + envelope[i + 1]) / 4;
                }
                smoothed[0] = envelope[0];
                smoothed[envelope.Length - 1] = envelope[envelope.Length - 1];
                envelope = smoothed;
            }

            return envelope;
        }
        private float[] ApplyBandPassFilter(float[] data, double lowFreq, double highFreq, double sampleRate)
        {
            // Simple Butterworth band-pass filter implementation
            float[] filtered = new float[data.Length];

            // High-pass stage
            double rcHigh = 1.0 / (2.0 * Math.PI * lowFreq);
            double dtSample = 1.0 / sampleRate;
            double alphaHigh = rcHigh / (rcHigh + dtSample);

            float[] highPassed = new float[data.Length];
            highPassed[0] = data[0];

            for (int i = 1; i < data.Length; i++)
            {
                highPassed[i] = (float)(alphaHigh * (highPassed[i - 1] + data[i] - data[i - 1]));
            }

            // Low-pass stage
            double rcLow = 1.0 / (2.0 * Math.PI * highFreq);
            double alphaLow = dtSample / (rcLow + dtSample);

            filtered[0] = highPassed[0];

            for (int i = 1; i < data.Length; i++)
            {
                filtered[i] = (float)(alphaLow * highPassed[i] + (1 - alphaLow) * filtered[i - 1]);
            }

            return filtered;
        }
        private float[] GetRealTimeWaveformData(bool isPWave)
        {
            try
            {
                // Try to get current wave field snapshot
                var waveFieldTuple = GetWaveFieldSnapshot();

                if (waveFieldTuple.vx == null || waveFieldTuple.vy == null || waveFieldTuple.vz == null)
                    return null;

                // Extract waveform along the ray path from TX to RX
                List<float> waveformData = new List<float>();

                // Sample points along the direct path
                int numSamples = Math.Max(100, Math.Abs(rx - tx) + Math.Abs(ry - ty) + Math.Abs(rz - tz));

                for (int i = 0; i < numSamples; i++)
                {
                    float t = i / (float)(numSamples - 1);

                    // Linear interpolation along the path
                    int x = (int)(tx + t * (rx - tx));
                    int y = (int)(ty + t * (ry - ty));
                    int z = (int)(tz + t * (rz - tz));

                    // Ensure coordinates are within bounds
                    x = Math.Max(0, Math.Min(mainForm.GetWidth() - 1, x));
                    y = Math.Max(0, Math.Min(mainForm.GetHeight() - 1, y));
                    z = Math.Max(0, Math.Min(mainForm.GetDepth() - 1, z));

                    float value;
                    if (isPWave)
                    {
                        // P-wave: use the component along the propagation direction
                        double dx = rx - tx;
                        double dy = ry - ty;
                        double dz = rz - tz;
                        double length = Math.Sqrt(dx * dx + dy * dy + dz * dz);

                        if (length > 0)
                        {
                            dx /= length;
                            dy /= length;
                            dz /= length;

                            value = (float)(waveFieldTuple.vx[x, y, z] * dx +
                                           waveFieldTuple.vy[x, y, z] * dy +
                                           waveFieldTuple.vz[x, y, z] * dz);
                        }
                        else
                        {
                            value = (float)waveFieldTuple.vx[x, y, z];
                        }
                    }
                    else
                    {
                        // S-wave: use the transverse components
                        value = (float)Math.Sqrt(
                            waveFieldTuple.vy[x, y, z] * waveFieldTuple.vy[x, y, z] +
                            waveFieldTuple.vz[x, y, z] * waveFieldTuple.vz[x, y, z]);
                    }

                    waveformData.Add(value * 1e10f); // Apply amplification
                }

                return waveformData.Count > 0 ? waveformData.ToArray() : null;
            }
            catch (Exception ex)
            {
                Logger.Log($"[GetRealTimeWaveformData] Error getting real-time data: {ex.Message}");
                return null;
            }
        }

        private float[] GenerateApproximatedWaveform(bool isPWave)
        {
            int arrivalIdx = isPWave
                ? simulationResults.PWaveTravelTime
                : simulationResults.SWaveTravelTime;

            /* ——— choose a generous buffer so the tail fits ——— */
            int totalLen = Math.Max(arrivalIdx + 4000, 2000);
            float[] trace = new float[totalLen];

            /* ——— basic parameters ——— */
            double fPeak = (double)numFrequency.Value * 1000.0;  // Hz
            double dt = CalculateTimeStep();                  // s

            /* ——— tiny random noise before arrival so the scope isn't flat ——— */
            Random rng = new Random(42);
            for (int i = 0; i < arrivalIdx && i < totalLen; i++)
                trace[i] = (float)((rng.NextDouble() - 0.5) * 0.01);

            /* ——— classic Ricker pulse starting at arrivalIdx ——— */
            double amp = isPWave ? 1.0 : 0.7;                    // S a bit weaker
            for (int i = arrivalIdx; i < totalLen; i++)
            {
                double t = (i - arrivalIdx) * dt;             // seconds since arrival
                double arg = Math.PI * fPeak * t;
                double arg2 = arg * arg;

                double val = (1.0 - 2.0 * arg2) * Math.Exp(-arg2) * amp;
                trace[i] = (float)val;
            }

            return trace;
        }

        private void ApplyHighPassFilter(float[] data, double cutoffFreq, double sampleRate)
        {
            // Simple first-order high-pass filter
            double RC = 1.0 / (2.0 * Math.PI * cutoffFreq);
            double dt = 1.0 / sampleRate;
            double alpha = RC / (RC + dt);

            float[] filtered = new float[data.Length];
            filtered[0] = data[0];

            for (int i = 1; i < data.Length; i++)
            {
                filtered[i] = (float)(alpha * (filtered[i - 1] + data[i] - data[i - 1]));
            }

            // Copy back
            Array.Copy(filtered, data, data.Length);
        }
        private FrameCacheManager cacheManager;


        #endregion

        #region Event Handlers
        private void TomographyPictureBox_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                isDragging3D = true;
                lastMousePos = e.Location;
                tomographyPictureBox.Cursor = Cursors.SizeAll;
            }
        }

        private void TomographyPictureBox_MouseMove(object sender, MouseEventArgs e)
        {
            if (isDragging3D)
            {
                int dx = e.X - lastMousePos.X;
                int dy = e.Y - lastMousePos.Y;

                // Rotate the view
                tomographyRotationY += dx * 0.5f;
                tomographyRotationX += dy * 0.5f;

                // Limit rotation angles
                tomographyRotationX = Math.Max(-90, Math.Min(90, tomographyRotationX));

                lastMousePos = e.Location;
                tomographyPictureBox.Invalidate();
            }
        }

        private void TomographyPictureBox_MouseUp(object sender, MouseEventArgs e)
        {
            isDragging3D = false;
            tomographyPictureBox.Cursor = Cursors.Default;
        }

        private void TomographyPictureBox_MouseWheel(object sender, MouseEventArgs e)
        {
            // Adjust zoom based on wheel direction
            float zoomDelta = 1.1f;

            if (e.Delta > 0)
                tomographyZoom *= zoomDelta;
            else
                tomographyZoom /= zoomDelta;

            // Limit zoom range
            tomographyZoom = Math.Max(0.1f, Math.Min(10.0f, tomographyZoom));

            tomographyPictureBox.Invalidate();
        }
        #endregion

        #region Export Methods
        private void ExportImage(PictureBox pictureBox, string namePrefix)
        {
            try
            {
                using (SaveFileDialog saveDialog = new SaveFileDialog())
                {
                    saveDialog.Filter = "PNG Image|*.png|JPEG Image|*.jpg|Bitmap Image|*.bmp";
                    saveDialog.Title = "Export Image";
                    saveDialog.FileName = $"{namePrefix}_{DateTime.Now:yyyy-MM-dd_HHmmss}";

                    if (saveDialog.ShowDialog() == DialogResult.OK)
                    {
                        // Create a bitmap of the current picture box
                        Bitmap bitmap = new Bitmap(pictureBox.Width, pictureBox.Height);
                        pictureBox.DrawToBitmap(bitmap, pictureBox.ClientRectangle);

                        // Determine image format
                        ImageFormat format = ImageFormat.Png;
                        if (saveDialog.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                            format = ImageFormat.Jpeg;
                        else if (saveDialog.FileName.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
                            format = ImageFormat.Bmp;

                        // Save to file
                        bitmap.Save(saveDialog.FileName, format);

                        MessageBox.Show($"Image saved to {saveDialog.FileName}", "Export Complete",
                                      MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error exporting image: {ex.Message}", "Export Error",
                              MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ExportResults()
        {
            try
            {
                using (SaveFileDialog saveDialog = new SaveFileDialog())
                {
                    saveDialog.Filter = "CSV File|*.csv|Text File|*.txt";
                    saveDialog.Title = "Export Results";
                    saveDialog.FileName = $"AcousticResults_{DateTime.Now:yyyy-MM-dd_HHmmss}";

                    if (saveDialog.ShowDialog() == DialogResult.OK)
                    {
                        using (StreamWriter writer = new StreamWriter(saveDialog.FileName))
                        {
                            // Write CSV header
                            writer.WriteLine("Parameter,Value,Unit");

                            // Write simulation parameters
                            writer.WriteLine($"Material ID,{selectedMaterialID},");
                            writer.WriteLine($"Material Density,{baseDensity:F2},kg/m³");
                            writer.WriteLine($"Young's Modulus,{numYoungsModulus.Value:F1},MPa");
                            writer.WriteLine($"Poisson's Ratio,{numPoissonRatio.Value:F3},");
                            writer.WriteLine($"Wave Frequency,{numFrequency.Value:F1},kHz");
                            writer.WriteLine($"Wave Energy,{numEnergy.Value:F2},J");
                            writer.WriteLine($"Confining Pressure,{numConfiningPressure.Value:F2},MPa");
                            writer.WriteLine();

                            // Write key results
                            if (simulationResults != null)
                            {
                                writer.WriteLine($"P-Wave Velocity,{simulationResults.PWaveVelocity:F2},m/s");
                                writer.WriteLine($"S-Wave Velocity,{simulationResults.SWaveVelocity:F2},m/s");
                                writer.WriteLine($"Vp/Vs Ratio,{simulationResults.VpVsRatio:F3},");
                                writer.WriteLine($"P-Wave Travel Time,{simulationResults.PWaveTravelTime},steps");
                                writer.WriteLine($"S-Wave Travel Time,{simulationResults.SWaveTravelTime},steps");

                                // Calculate and write dead time
                                int deadTime = simulationResults.SWaveTravelTime - simulationResults.PWaveTravelTime;
                                writer.WriteLine($"Dead Time,{deadTime},steps");
                            }
                        }

                        MessageBox.Show($"Results exported to {saveDialog.FileName}", "Export Complete",
                                      MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error exporting results: {ex.Message}", "Export Error",
                              MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ExportCompositeImage()
        {
            try
            {
                using (SaveFileDialog saveDialog = new SaveFileDialog())
                {
                    saveDialog.Filter = "PNG Image|*.png|JPEG Image|*.jpg|Bitmap Image|*.bmp";
                    saveDialog.Title = "Export Composite Image";
                    saveDialog.FileName = $"AcousticResults_Composite_{DateTime.Now:yyyy-MM-dd_HHmmss}";

                    if (saveDialog.ShowDialog() == DialogResult.OK)
                    {
                        // Create a large bitmap for the composite
                        int width = Math.Max(1000, waveformPictureBox.Width);
                        int height = 800;

                        using (Bitmap composite = new Bitmap(width, height))
                        using (Graphics g = Graphics.FromImage(composite))
                        {
                            g.Clear(Color.FromArgb(30, 30, 30));
                            g.SmoothingMode = SmoothingMode.AntiAlias;

                            // Draw a border
                            g.DrawRectangle(Pens.White, 0, 0, width - 1, height - 1);

                            // Draw title
                            using (Font titleFont = new Font("Segoe UI", 16, FontStyle.Bold))
                            using (Brush titleBrush = new SolidBrush(Color.White))
                            {
                                string title = "Acoustic Simulation Results";
                                SizeF titleSize = g.MeasureString(title, titleFont);
                                g.DrawString(title, titleFont, titleBrush,
                                          (width - titleSize.Width) / 2, 10);
                            }

                            // Capture waveform display
                            Bitmap waveformImage = new Bitmap(waveformPictureBox.Width, waveformPictureBox.Height);
                            waveformPictureBox.DrawToBitmap(waveformImage, waveformPictureBox.ClientRectangle);

                            // Capture tomography display
                            Bitmap tomoImage = new Bitmap(tomographyPictureBox.Width, tomographyPictureBox.Height);
                            tomographyPictureBox.DrawToBitmap(tomoImage, tomographyPictureBox.ClientRectangle);

                            // Draw scaled waveform and tomography images
                            int panelWidth = width / 2 - 20;
                            int panelHeight = 300;

                            g.DrawImage(waveformImage, 10, 60, panelWidth, panelHeight);
                            g.DrawImage(tomoImage, width / 2 + 10, 60, panelWidth, panelHeight);

                            // Add panel titles
                            using (Font panelFont = new Font("Segoe UI", 12, FontStyle.Bold))
                            using (Brush textBrush = new SolidBrush(Color.White))
                            {
                                g.DrawString("Waveform Analysis", panelFont, textBrush, 10, 40);
                                g.DrawString("Tomography Visualization", panelFont, textBrush, width / 2 + 10, 40);
                            }

                            // Add results details at the bottom
                            int resultsY = 380;
                            using (Font resultsFont = new Font("Segoe UI", 11))
                            using (Font resultsBoldFont = new Font("Segoe UI", 11, FontStyle.Bold))
                            using (Brush textBrush = new SolidBrush(Color.White))
                            using (Brush pBrush = new SolidBrush(Color.DeepSkyBlue))
                            using (Brush sBrush = new SolidBrush(Color.Crimson))
                            using (Brush ratioBrush = new SolidBrush(Color.LightGreen))
                            {
                                // Add results header
                                g.DrawString("Simulation Results:", resultsBoldFont, textBrush, 10, resultsY);
                                resultsY += 30;

                                if (simulationResults != null)
                                {
                                    // Two-column layout
                                    int col1X = 30;
                                    int col2X = width / 2 + 30;

                                    // Column 1
                                    g.DrawString("P-Wave Velocity:", resultsFont, textBrush, col1X, resultsY);
                                    g.DrawString($"{simulationResults.PWaveVelocity:F2} m/s", resultsBoldFont, pBrush, col1X + 200, resultsY);
                                    resultsY += 25;

                                    g.DrawString("S-Wave Velocity:", resultsFont, textBrush, col1X, resultsY);
                                    g.DrawString($"{simulationResults.SWaveVelocity:F2} m/s", resultsBoldFont, sBrush, col1X + 200, resultsY);
                                    resultsY += 25;

                                    g.DrawString("Vp/Vs Ratio:", resultsFont, textBrush, col1X, resultsY);
                                    g.DrawString($"{simulationResults.VpVsRatio:F3}", resultsBoldFont, ratioBrush, col1X + 200, resultsY);
                                    resultsY += 25;

                                    // Calculate dead time
                                    int deadTime = simulationResults.SWaveTravelTime - simulationResults.PWaveTravelTime;
                                    g.DrawString("Dead Time:", resultsFont, textBrush, col1X, resultsY);
                                    g.DrawString($"{deadTime} steps", resultsBoldFont, textBrush, col1X + 200, resultsY);

                                    // Column 2 (Material properties)
                                    int propY = resultsY - 75;
                                    g.DrawString("Material Properties:", resultsBoldFont, textBrush, col2X, propY);
                                    propY += 30;

                                    g.DrawString("Material Density:", resultsFont, textBrush, col2X, propY);
                                    g.DrawString($"{baseDensity:F1} kg/m³", resultsFont, textBrush, col2X + 200, propY);
                                    propY += 25;

                                    g.DrawString("Young's Modulus:", resultsFont, textBrush, col2X, propY);
                                    g.DrawString($"{numYoungsModulus.Value:F1} MPa", resultsFont, textBrush, col2X + 200, propY);
                                    propY += 25;

                                    g.DrawString("Poisson's Ratio:", resultsFont, textBrush, col2X, propY);
                                    g.DrawString($"{numPoissonRatio.Value:F3}", resultsFont, textBrush, col2X + 200, propY);
                                }
                            }

                            // Add date and time at the bottom
                            using (Font footerFont = new Font("Segoe UI", 9))
                            using (Brush footerBrush = new SolidBrush(Color.Silver))
                            {
                                string footer = $"Generated on {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
                                SizeF footerSize = g.MeasureString(footer, footerFont);
                                g.DrawString(footer, footerFont, footerBrush,
                                          width - footerSize.Width - 10, height - footerSize.Height - 10);
                            }

                            // Clean up temporary images
                            waveformImage.Dispose();
                            tomoImage.Dispose();

                            // Save the composite image
                            ImageFormat format = ImageFormat.Png;
                            if (saveDialog.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                                format = ImageFormat.Jpeg;
                            else if (saveDialog.FileName.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
                                format = ImageFormat.Bmp;

                            composite.Save(saveDialog.FileName, format);
                        }

                        MessageBox.Show($"Composite image saved to {saveDialog.FileName}", "Export Complete",
                                      MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error creating composite image: {ex.Message}", "Export Error",
                              MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        #endregion

        #region Utility Methods
        private void OpenVisualizer()
        {
            try
            {
                if (simulationResults == null)
                {
                    MessageBox.Show("No simulation results available.\nRun a simulation first.",
                                  "No Results", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Check available memory before opening visualizer
                var availableMemory = GC.GetTotalMemory(false);
                var totalMemory = GC.GetTotalMemory(true);

                if (totalMemory > 2_000_000_000) // 2GB threshold
                {
                    // Clear cache and collect garbage
                    //cacheManager?.ClearMemoryCache();
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }

                // Create the visualizer
                AcousticSimulationVisualizer visualizer = new AcousticSimulationVisualizer(
                    mainForm.GetWidth(), mainForm.GetHeight(), mainForm.GetDepth(),
                    (float)mainForm.GetPixelSize(),
                    tx, ty, tz, rx, ry, rz);

                // Load data in chunks to avoid OOM
                Task.Run(() =>
                {
                    try
                    {
                        if (usingGpuSimulator && gpuSimulator != null)
                        {
                            visualizer.ConnectToGpuSimulator(gpuSimulator);
                        }
                        else if (cpuSimulator != null)
                        {
                            visualizer.ConnectToCpuSimulator(cpuSimulator);
                        }
                        else
                        {
                            // Get wave field snapshot efficiently
                            var tuple = GetWaveFieldSnapshot();

                            // Load the data into the visualizer
                            visualizer.LoadSavedSimulationData(
                                simulationResults.PWaveVelocity,
                                simulationResults.SWaveVelocity,
                                simulationResults.VpVsRatio,
                                simulationResults.PWaveTravelTime,
                                simulationResults.SWaveTravelTime,
                                simulationResults.PWaveTravelTime + simulationResults.SWaveTravelTime + 100,
                                tuple.vx,
                                tuple.vy,
                                tuple.vz);
                        }

                        // Show the visualizer on UI thread
                        this.BeginInvoke(new Action(() =>
                        {
                            visualizer.Show();
                        }));
                    }
                    catch (OutOfMemoryException)
                    {
                        this.BeginInvoke(new Action(() =>
                        {
                            MessageBox.Show("Not enough memory to open visualizer.\nTry closing other applications.",
                                          "Memory Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }));
                    }
                    catch (Exception ex)
                    {
                        this.BeginInvoke(new Action(() =>
                        {
                            MessageBox.Show($"Error opening visualizer: {ex.Message}",
                                          "Visualizer Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }));
                    }
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening visualizer: {ex.Message}", "Visualizer Error",
                              MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        private double CalculateProjectionOnLine(int x1, int y1, int z1, int x2, int y2, int z2, int px, int py, int pz)
        {
            // Calculate vector from point 1 to point 2
            double vx = x2 - x1;
            double vy = y2 - y1;
            double vz = z2 - z1;

            // Calculate vector from point 1 to the test point
            double wx = px - x1;
            double wy = py - y1;
            double wz = pz - z1;

            // Calculate dot product
            double dot = wx * vx + wy * vy + wz * vz;

            // Calculate squared length of v
            double lenSq = vx * vx + vy * vy + vz * vz;

            // Avoid division by zero
            if (lenSq < 0.0001)
                return 0.0;

            // Calculate the projection parameter
            return dot / lenSq;
        }
        public virtual void UpdateResultsDisplay()
        {
            if (simulationResults == null)
            {
                // Clear result fields
                lblPWaveVelocity.Text = "0.00 m/s";
                lblSWaveVelocity.Text = "0.00 m/s";
                lblVpVsRatio.Text = "0.00";
                lblPWaveTravelTime.Text = "0 steps (0.00 ms)";
                lblSWaveTravelTime.Text = "0 steps (0.00 ms)";
                lblDeadTime.Text = "0 steps (0.00 ms)";

                cacheManager = null;
                return;
            }

            // Create read-only cache manager to load existing data
            Task.Run(() =>
            {
                try
                {
                    // Wait a moment for cache writer to finish
                    System.Threading.Thread.Sleep(500);

                    // Create read-only cache manager
                    var newCacheManager = new FrameCacheManager(mainForm.GetWidth(), mainForm.GetHeight(), mainForm.GetDepth(), true);
                    newCacheManager.LoadMetadata();

                    // Update on UI thread
                    this.BeginInvoke(new Action(() =>
                    {
                        cacheManager = newCacheManager;
                        Logger.Log($"[UpdateResultsDisplay] Loaded cache with {cacheManager.FrameCount} frames");

                        // Update UI elements
                        UpdateResultLabels();
                        InitializeVelocityField();
                    }));
                }
                catch (Exception ex)
                {
                    Logger.Log($"[UpdateResultsDisplay] Error loading cache: {ex.Message}");
                    this.BeginInvoke(new Action(() =>
                    {
                        cacheManager = null;
                        UpdateResultLabels();
                    }));
                }
            });
        }
        private void UpdateResultLabels()
        {
            if (simulationResults == null)
                return;

            baseDensity = CalculateAverageDensity();
            lblPWaveVelocity.Text = $"{simulationResults.PWaveVelocity:F2} m/s";
            lblSWaveVelocity.Text = $"{simulationResults.SWaveVelocity:F2} m/s";
            lblVpVsRatio.Text = $"{simulationResults.VpVsRatio:F3}";

            // Calculate time in milliseconds
            double dtMs = 0.000001 * 1000; // Assuming dt is 1 μs, convert to ms
            double pTimeMs = simulationResults.PWaveTravelTime * dtMs;
            double sTimeMs = simulationResults.SWaveTravelTime * dtMs;

            lblPWaveTravelTime.Text = $"{simulationResults.PWaveTravelTime} steps ({pTimeMs:F3} ms)";
            lblSWaveTravelTime.Text = $"{simulationResults.SWaveTravelTime} steps ({sTimeMs:F3} ms)";

            // Calculate dead time
            int deadTimeSteps = simulationResults.SWaveTravelTime - simulationResults.PWaveTravelTime;
            double deadTimeMs = deadTimeSteps * dtMs;
            lblDeadTime.Text = $"{deadTimeSteps} steps ({deadTimeMs:F3} ms)";
        }
        #endregion
        #region fileops
        public void SaveSimulationToFile(string filePath)
        {
            try
            {
                if (simulationResults == null)
                {
                    MessageBox.Show("No simulation results to save.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                // Get current wave field snapshot - handle the tuple correctly
                var tuple = GetWaveFieldSnapshot();

                using (FileStream fs = new FileStream(filePath, FileMode.Create))
                using (BinaryWriter writer = new BinaryWriter(fs))
                {
                    // Write header and version
                    writer.Write("ACOUSTICSIM"); // Magic number
                    writer.Write(1); // Version

                    // Write grid dimensions
                    writer.Write(mainForm.GetWidth());
                    writer.Write(mainForm.GetHeight());
                    writer.Write(mainForm.GetDepth());

                    // Write simulation results
                    writer.Write(simulationResults.PWaveVelocity);
                    writer.Write(simulationResults.SWaveVelocity);
                    writer.Write(simulationResults.VpVsRatio);
                    writer.Write(simulationResults.PWaveTravelTime);
                    writer.Write(simulationResults.SWaveTravelTime);

                    // Write TX/RX coordinates
                    writer.Write(tx);
                    writer.Write(ty);
                    writer.Write(tz);
                    writer.Write(rx);
                    writer.Write(ry);
                    writer.Write(rz);

                    // Write wave field data using a compressed approach
                    // Correctly uses the tuple fields
                    WriteCompressedWaveField(writer, tuple.vx);
                    WriteCompressedWaveField(writer, tuple.vy);
                    WriteCompressedWaveField(writer, tuple.vz);

                    MessageBox.Show("Simulation saved successfully.", "Save Complete",
                                  MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving simulation: {ex.Message}", "Error",
                              MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        public bool LoadSimulationFromFile(string filePath)
        {
            try
            {
                using (FileStream fs = new FileStream(filePath, FileMode.Open))
                using (BinaryReader reader = new BinaryReader(fs))
                {
                    // Read and verify header
                    string header = reader.ReadString();
                    if (header != "ACOUSTICSIM")
                        throw new InvalidDataException("Not a valid acoustic simulation file");

                    int version = reader.ReadInt32();
                    if (version != 1)
                        throw new InvalidDataException("Unsupported file version");

                    // Read grid dimensions
                    int width = reader.ReadInt32();
                    int height = reader.ReadInt32();
                    int depth = reader.ReadInt32();

                    // Check if dimensions match
                    if (width != mainForm.GetWidth() || height != mainForm.GetHeight() || depth != mainForm.GetDepth())
                    {
                        throw new InvalidDataException(
                            $"Dimension mismatch. File: {width}x{height}x{depth}, Current: {mainForm.GetWidth()}x{mainForm.GetHeight()}x{mainForm.GetDepth()}");
                    }

                    // Read simulation results
                    double pWaveVelocity = reader.ReadDouble();
                    double sWaveVelocity = reader.ReadDouble();
                    double vpVsRatio = reader.ReadDouble();
                    int pWaveTravelTime = reader.ReadInt32();
                    int sWaveTravelTime = reader.ReadInt32();

                    // Read TX/RX coordinates
                    int loadedTX = reader.ReadInt32();
                    int loadedTY = reader.ReadInt32();
                    int loadedTZ = reader.ReadInt32();
                    int loadedRX = reader.ReadInt32();
                    int loadedRY = reader.ReadInt32();
                    int loadedRZ = reader.ReadInt32();

                    // Store the TX/RX coordinates
                    tx = loadedTX;
                    ty = loadedTY;
                    tz = loadedTZ;
                    rx = loadedRX;
                    ry = loadedRY;
                    rz = loadedRZ;

                    // Read wave field data
                    double[,,] vxData = ReadCompressedWaveField(reader, width, height, depth);
                    double[,,] vyData = ReadCompressedWaveField(reader, width, height, depth);
                    double[,,] vzData = ReadCompressedWaveField(reader, width, height, depth);

                    // Create simulation results object
                    simulationResults = new SimulationResults
                    {
                        PWaveVelocity = pWaveVelocity,
                        SWaveVelocity = sWaveVelocity,
                        VpVsRatio = vpVsRatio,
                        PWaveTravelTime = pWaveTravelTime,
                        SWaveTravelTime = sWaveTravelTime
                    };

                    // Store wave field data for visualization - FIX: Store all components
                    cachedPWaveField = ConvertToFloat(vxData);
                    cachedSWaveField = ConvertToFloat(vyData);
                    cachedZWaveField = ConvertToFloat(vzData); // Add this line to store z-component
                    cachedTotalTimeSteps = Math.Max(pWaveTravelTime + sWaveTravelTime,
                                                   pWaveTravelTime * 2); // Ensure sufficient steps

                    // Enable the visualizer button
                    foreach (ToolStripItem item in toolStrip.Items)
                    {
                        if (item.ToolTipText == "Open Visualizer")
                        {
                            item.Enabled = true;
                            break;
                        }
                    }

                    // Switch to the results tab to show the loaded data
                    tabControl.SelectedTab = resultsDetailsTab;

                    // Update UI with loaded results
                    UpdateResultsDisplay();

                    // Initialize velocity field for visualization
                    InitializeVelocityField();

                    // Force refresh of all visualizations
                    if (waveformPictureBox != null && !waveformPictureBox.IsDisposed)
                        waveformPictureBox.Invalidate();
                    if (tomographyPictureBox != null && !tomographyPictureBox.IsDisposed)
                        tomographyPictureBox.Invalidate();
                    if (histogramPictureBox != null && !histogramPictureBox.IsDisposed)
                        histogramPictureBox.Invalidate();

                    MessageBox.Show("Simulation loaded successfully.", "Load Complete",
                                  MessageBoxButtons.OK, MessageBoxIcon.Information);

                    return true;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading simulation: {ex.Message}", "Error",
                              MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        // Helper methods for efficient wave field storage
        private void WriteCompressedWaveField(BinaryWriter writer, double[,,] field)
        {
            int width = field.GetLength(0);
            int height = field.GetLength(1);
            int depth = field.GetLength(2);

            // Only store non-zero values to save space
            List<KeyValuePair<int, double>> nonZeroValues = new List<KeyValuePair<int, double>>();

            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        double value = field[x, y, z];
                        if (Math.Abs(value) > 1e-10)
                        {
                            // Store as flattened index and value
                            int index = z * width * height + y * width + x;
                            nonZeroValues.Add(new KeyValuePair<int, double>(index, value));
                        }
                    }
                }
            }

            // Write count of non-zero values
            writer.Write(nonZeroValues.Count);

            // Write each non-zero value and its index
            foreach (var pair in nonZeroValues)
            {
                writer.Write(pair.Key);
                writer.Write(pair.Value);
            }
        }

        private double[,,] ReadCompressedWaveField(BinaryReader reader, int width, int height, int depth)
        {
            double[,,] field = new double[width, height, depth];

            // Read count of non-zero values
            int nonZeroCount = reader.ReadInt32();

            // Read and restore each non-zero value
            for (int i = 0; i < nonZeroCount; i++)
            {
                int index = reader.ReadInt32();
                double value = reader.ReadDouble();

                // Convert flattened index back to 3D coordinates
                int z = index / (width * height);
                int remainder = index % (width * height);
                int y = remainder / width;
                int x = remainder % width;

                // Restore value to the field
                if (x < width && y < height && z < depth)
                    field[x, y, z] = value;
            }

            return field;
        }
        #endregion
    }
}
