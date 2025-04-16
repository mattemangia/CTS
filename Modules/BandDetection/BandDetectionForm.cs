using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;
using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.CPU;
using ILGPU.Runtime.Cuda;

namespace CTSegmenter
{
    public partial class BandDetectionForm : Form
    {
        private MainForm mainForm;
        private CancellationTokenSource cancellationTokenSource;

        // UI Components
        private TableLayoutPanel mainLayout;
        private Panel xyPanel, xzPanel, yzPanel;
        private PictureBox xyPictureBox, xzPictureBox, yzPictureBox;
        private TrackBar xySliceTrackBar, xzSliceTrackBar, yzSliceTrackBar;
        private NumericUpDown xySliceNumeric, xzSliceNumeric, yzSliceNumeric;
        private Chart xyChart, xzChart, yzChart;
        private GroupBox parametersGroupBox;
        private Label diskRadiusLabel, gaussianSigmaLabel, peakDistanceLabel, prominenceLabel;
        private NumericUpDown diskRadiusNumeric, gaussianSigmaNumeric, peakDistanceNumeric, prominenceNumeric;
        private Button processButton, exportButton;
        private CheckBox showPeaksCheckBox, invertImageCheckBox, cropAirCheckBox;
        private ProgressBar progressBar;

        // Processing parameters
        private int diskRadius = 50;
        private double gaussianSigma = 10.0;
        private int peakDistance = 40;
        private double peakProminence = 0.02;
        private bool showPeaks = true;
        private bool invertImage = false;
        private bool cropAir = true;

        // Current state
        private Bitmap xyProcessedImage, xzProcessedImage, yzProcessedImage;
        private double[] xyRowProfile, xzRowProfile, yzRowProfile;
        private int[] xyDarkPeaks, xyBrightPeaks;
        private int[] xzDarkPeaks, xzBrightPeaks;
        private int[] yzDarkPeaks, yzBrightPeaks;
        private float xyZoom = 1.0f, xzZoom = 1.0f, yzZoom = 1.0f;
        private PointF xyPan = PointF.Empty, xzPan = PointF.Empty, yzPan = PointF.Empty;
        private Point lastMousePosition;

        public BandDetectionForm(MainForm form)
        {
            mainForm = form;
            cancellationTokenSource = new CancellationTokenSource();
            InitializeComponents();
            InitializeVarianceDetection();
            // Set initial slice values with proper order to avoid ArgumentOutOfRangeException
            int depth = mainForm.GetDepth();
            int height = mainForm.GetHeight();
            int width = mainForm.GetWidth();

            // First set all Maximum values
            xySliceTrackBar.Maximum = depth > 0 ? depth - 1 : 0;
            xySliceNumeric.Maximum = xySliceTrackBar.Maximum;

            xzSliceTrackBar.Maximum = height > 0 ? height - 1 : 0;
            xzSliceNumeric.Maximum = xzSliceTrackBar.Maximum;

            yzSliceTrackBar.Maximum = width > 0 ? width - 1 : 0;
            yzSliceNumeric.Maximum = yzSliceTrackBar.Maximum;

            // Then set all values, ensuring they don't exceed maximums
            int xyValue = depth > 0 ? depth / 2 : 0;
            int xzValue = height > 0 ? height / 2 : 0;
            int yzValue = width > 0 ? width / 2 : 0;

            xyValue = Math.Min(xyValue, xySliceTrackBar.Maximum);
            xzValue = Math.Min(xzValue, xzSliceTrackBar.Maximum);
            yzValue = Math.Min(yzValue, yzSliceTrackBar.Maximum);

            // Set numeric values first to avoid event triggering issues
            xySliceNumeric.Value = xyValue;
            xzSliceNumeric.Value = xzValue;
            yzSliceNumeric.Value = yzValue;

            // Then set trackbar values
            xySliceTrackBar.Value = xyValue;
            xzSliceTrackBar.Value = xzValue;
            yzSliceTrackBar.Value = yzValue;
            
            // Initial processing
            Task.Run(() => ProcessAllViews());
        }

        private void InitializeComponents()
        {
            this.Text = "Band Detection";
            this.Size = new Size(1200, 900);
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.StartPosition = FormStartPosition.CenterScreen;
            xyChart = new Chart();
            xzChart = new Chart();
            yzChart = new Chart();
            // Main layout - 2 rows: Views (top) and Parameters (bottom)
            mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Padding = new Padding(5)
            };

            mainLayout.RowStyles.Clear();
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 80F));  // Views row - reduced from 85%
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 20F));

            // Top row: Panel containing all views side by side
            TableLayoutPanel viewsPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1,
                Padding = new Padding(0),
                Margin = new Padding(0)
            };

            viewsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));
            viewsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));
            viewsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));

            // Initialize view panels
            xyPanel = CreateViewPanel("XY View");
            xzPanel = CreateViewPanel("XZ View");
            yzPanel = CreateViewPanel("YZ View");

            viewsPanel.Controls.Add(xyPanel, 0, 0);
            viewsPanel.Controls.Add(xzPanel, 1, 0);
            viewsPanel.Controls.Add(yzPanel, 2, 0);

            mainLayout.Controls.Add(viewsPanel, 0, 0);

            // Bottom row: Parameters panel
            InitializeParameters();
            mainLayout.Controls.Add(parametersGroupBox, 0, 1);

            this.Controls.Add(mainLayout);

            // Initialize each view with its controls
            InitializeXYView();
            InitializeXZView();
            InitializeYZView();

            // Form closing event
            this.FormClosing += BandDetectionForm_FormClosing;
        }

        private Panel CreateViewPanel(string title)
        {
            Panel panel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(5),
                BorderStyle = BorderStyle.FixedSingle
            };

            GroupBox titleBox = new GroupBox
            {
                Text = title,
                Dock = DockStyle.Top,
                Height = 10,
                Padding = new Padding(0),
                Margin = new Padding(0)
            };

            panel.Controls.Add(titleBox);
            return panel;
        }

        private void BandDetectionForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            // Cancel any ongoing processing
            cancellationTokenSource.Cancel();

            // Dispose resources
            xyProcessedImage?.Dispose();
            xzProcessedImage?.Dispose();
            yzProcessedImage?.Dispose();
        }

        private void InitializeXYView()
        {
            // Create a single-row layout with the image and chart side by side
            TableLayoutPanel xyLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0, 15, 0, 0)  // Add margin at top for GroupBox title
            };

            // Make chart wider - increase chart column proportion
            xyLayout.ColumnStyles.Clear();
            xyLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65F));  // Image
            xyLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35F));  // Chart - wider

            // Image panel
            Panel imagePanel = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true
            };

            xyPictureBox = new PictureBox
            {
                SizeMode = PictureBoxSizeMode.StretchImage,
                Dock = DockStyle.Fill
            };

            // Mouse events for pan/zoom
            //xyPictureBox.MouseDown += XYPictureBox_MouseDown;
            //xyPictureBox.MouseMove += XYPictureBox_MouseMove;
            //xyPictureBox.MouseUp += XYPictureBox_MouseUp;
            //xyPictureBox.MouseWheel += XYPictureBox_MouseWheel;
            xyPictureBox.Paint += XYPictureBox_Paint;

            imagePanel.Controls.Add(xyPictureBox);

            // Slice control panel
            Panel slicePanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 30
            };

            Label sliceLabel = new Label
            {
                Text = "Z:",
                AutoSize = true,
                Location = new Point(5, 8)
            };

            xySliceTrackBar = new TrackBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Width = 120,
                Location = new Point(25, 5),
                TickStyle = TickStyle.None
            };

            xySliceNumeric = new NumericUpDown
            {
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Width = 50,
                Location = new Point(150, 5)
            };

            xySliceTrackBar.ValueChanged += (s, e) =>
            {
                // Only update if value is in valid range
                if (xySliceTrackBar.Value >= xySliceNumeric.Minimum &&
                    xySliceTrackBar.Value <= xySliceNumeric.Maximum)
                {
                    xySliceNumeric.Value = xySliceTrackBar.Value;
                }
                Task.Run(() => ProcessXYView());
            };

            xySliceNumeric.ValueChanged += (s, e) =>
            {
                // Only update if value is in valid range
                if ((int)xySliceNumeric.Value >= xySliceTrackBar.Minimum &&
                    (int)xySliceNumeric.Value <= xySliceTrackBar.Maximum)
                {
                    xySliceTrackBar.Value = (int)xySliceNumeric.Value;
                }
            };

            slicePanel.Controls.Add(sliceLabel);
            slicePanel.Controls.Add(xySliceTrackBar);
            slicePanel.Controls.Add(xySliceNumeric);
            imagePanel.Controls.Add(slicePanel);

            xyLayout.Controls.Add(imagePanel, 0, 0);

            // Chart panel - keep panel for compatibility
            Panel chartPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(2, 0, 0, 0)  // Minimal padding
            };

            xyChart = new Chart
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White
            };

            // Configure chart for showing row profile
            ConfigureChart(xyChart, "XY Row Profile", "Intensity", "Row");
            chartPanel.Controls.Add(xyChart);

            xyLayout.Controls.Add(chartPanel, 1, 0);

            xyPanel.Controls.Add(xyLayout);
        }
        private void InitializeXZView()
        {
            // Create a single-row layout with the image and chart side by side
            TableLayoutPanel xzLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0, 15, 0, 0)  // Add margin at top for GroupBox title
            };

            // Make chart wider - increase chart column proportion
            xzLayout.ColumnStyles.Clear();
            xzLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65F));  // Image
            xzLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35F));  // Chart - wider

            // Image panel
            Panel imagePanel = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true
            };

            xzPictureBox = new PictureBox
            {
                SizeMode = PictureBoxSizeMode.StretchImage,
                Dock = DockStyle.Fill
            };

            // Mouse events for pan/zoom
            //xzPictureBox.MouseDown += XZPictureBox_MouseDown;
            //xzPictureBox.MouseMove += XZPictureBox_MouseMove;
            //xzPictureBox.MouseUp += XZPictureBox_MouseUp;
            //xzPictureBox.MouseWheel += XZPictureBox_MouseWheel;
            xzPictureBox.Paint += XZPictureBox_Paint;

            imagePanel.Controls.Add(xzPictureBox);

            // Slice control panel
            Panel slicePanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 30
            };

            Label sliceLabel = new Label
            {
                Text = "Y:",
                AutoSize = true,
                Location = new Point(5, 8)
            };

            xzSliceTrackBar = new TrackBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Width = 120,
                Location = new Point(25, 5),
                TickStyle = TickStyle.None
            };

            xzSliceNumeric = new NumericUpDown
            {
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Width = 50,
                Location = new Point(150, 5)
            };

            xzSliceTrackBar.ValueChanged += (s, e) =>
            {
                // Only update if value is in valid range
                if (xzSliceTrackBar.Value >= xzSliceNumeric.Minimum &&
                    xzSliceTrackBar.Value <= xzSliceNumeric.Maximum)
                {
                    xzSliceNumeric.Value = xzSliceTrackBar.Value;
                }
                Task.Run(() => ProcessXZView());
            };

            xzSliceNumeric.ValueChanged += (s, e) =>
            {
                // Only update if value is in valid range
                if ((int)xzSliceNumeric.Value >= xzSliceTrackBar.Minimum &&
                    (int)xzSliceNumeric.Value <= xzSliceTrackBar.Maximum)
                {
                    xzSliceTrackBar.Value = (int)xzSliceNumeric.Value;
                }
            };

            slicePanel.Controls.Add(sliceLabel);
            slicePanel.Controls.Add(xzSliceTrackBar);
            slicePanel.Controls.Add(xzSliceNumeric);
            imagePanel.Controls.Add(slicePanel);

            xzLayout.Controls.Add(imagePanel, 0, 0);

            // Chart panel - keep panel for compatibility
            Panel chartPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(2, 0, 0, 0)  // Minimal padding
            };

            xzChart = new Chart
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White
            };

            // Configure chart for showing row profile
            ConfigureChart(xzChart, "XZ Row Profile", "Intensity", "Row");
            chartPanel.Controls.Add(xzChart);

            xzLayout.Controls.Add(chartPanel, 1, 0);

            xzPanel.Controls.Add(xzLayout);
        }
        private void InitializeYZView()
        {
            // Create a single-row layout with the image and chart side by side
            TableLayoutPanel yzLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0, 15, 0, 0)  // Add margin at top for GroupBox title
            };

            // Make chart wider - increase chart column proportion
            yzLayout.ColumnStyles.Clear();
            yzLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65F));  // Image
            yzLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35F));  // Chart - wider

            // Image panel
            Panel imagePanel = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true
            };

            yzPictureBox = new PictureBox
            {
                SizeMode = PictureBoxSizeMode.StretchImage,
                Dock = DockStyle.Fill
            };

            // Mouse events for pan/zoom
            //yzPictureBox.MouseDown += YZPictureBox_MouseDown;
            //yzPictureBox.MouseMove += YZPictureBox_MouseMove;
            //yzPictureBox.MouseWheel += YZPictureBox_MouseWheel;
            yzPictureBox.Paint += YZPictureBox_Paint;

            imagePanel.Controls.Add(yzPictureBox);

            // Slice control panel
            Panel slicePanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 30
            };

            Label sliceLabel = new Label
            {
                Text = "X:",
                AutoSize = true,
                Location = new Point(5, 8)
            };

            yzSliceTrackBar = new TrackBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Width = 120,
                Location = new Point(25, 5),
                TickStyle = TickStyle.None
            };

            yzSliceNumeric = new NumericUpDown
            {
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Width = 50,
                Location = new Point(150, 5)
            };

            yzSliceTrackBar.ValueChanged += (s, e) =>
            {
                // Only update if value is in valid range
                if (yzSliceTrackBar.Value >= yzSliceNumeric.Minimum &&
                    yzSliceTrackBar.Value <= yzSliceNumeric.Maximum)
                {
                    yzSliceNumeric.Value = yzSliceTrackBar.Value;
                }
                Task.Run(() => ProcessYZView());
            };

            yzSliceNumeric.ValueChanged += (s, e) =>
            {
                // Only update if value is in valid range
                if ((int)yzSliceNumeric.Value >= yzSliceTrackBar.Minimum &&
                    (int)yzSliceNumeric.Value <= yzSliceTrackBar.Maximum)
                {
                    yzSliceTrackBar.Value = (int)yzSliceNumeric.Value;
                }
            };

            slicePanel.Controls.Add(sliceLabel);
            slicePanel.Controls.Add(yzSliceTrackBar);
            slicePanel.Controls.Add(yzSliceNumeric);
            imagePanel.Controls.Add(slicePanel);

            yzLayout.Controls.Add(imagePanel, 0, 0);

            // Chart panel - keep panel for compatibility
            Panel chartPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(2, 0, 0, 0)  // Minimal padding
            };

            yzChart = new Chart
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White
            };

            // Configure chart for showing row profile
            ConfigureChart(yzChart, "YZ Row Profile", "Intensity", "Row");
            chartPanel.Controls.Add(yzChart);

            yzLayout.Controls.Add(chartPanel, 1, 0);

            yzPanel.Controls.Add(yzLayout);
        }

        private void InitializeParameters()
        {
            // Parameters panel
            TableLayoutPanel parametersPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1
            };

            parametersPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60F));
            parametersPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20F));
            parametersPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20F));

            // Parameters group box
            parametersGroupBox = new GroupBox
            {
                Text = "Processing Parameters",
                Dock = DockStyle.Fill
            };

            TableLayoutPanel paramsLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 4,
                RowCount = 2
            };

            // First row of parameters
            diskRadiusLabel = new Label { Text = "Disk Radius:", Anchor = AnchorStyles.Left };
            diskRadiusNumeric = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 200,
                Value = diskRadius,
                Width = 60,
                Anchor = AnchorStyles.Left
            };

            gaussianSigmaLabel = new Label { Text = "Gaussian Sigma:", Anchor = AnchorStyles.Left };
            gaussianSigmaNumeric = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 50,
                Value = (decimal)gaussianSigma,
                DecimalPlaces = 1,
                Increment = 0.5m,
                Width = 60,
                Anchor = AnchorStyles.Left
            };

            paramsLayout.Controls.Add(diskRadiusLabel, 0, 0);
            paramsLayout.Controls.Add(diskRadiusNumeric, 1, 0);
            paramsLayout.Controls.Add(gaussianSigmaLabel, 2, 0);
            paramsLayout.Controls.Add(gaussianSigmaNumeric, 3, 0);

            // Second row of parameters
            peakDistanceLabel = new Label { Text = "Peak Distance:", Anchor = AnchorStyles.Left };
            peakDistanceNumeric = new NumericUpDown
            {
                Minimum = 5,
                Maximum = 500,
                Value = peakDistance,
                Width = 60,
                Anchor = AnchorStyles.Left
            };

            prominenceLabel = new Label { Text = "Peak Prominence:", Anchor = AnchorStyles.Left };
            prominenceNumeric = new NumericUpDown
            {
                Minimum = 0.001m,
                Maximum = 1.0m,
                Value = (decimal)peakProminence,
                DecimalPlaces = 3,
                Increment = 0.005m,
                Width = 60,
                Anchor = AnchorStyles.Left
            };

            paramsLayout.Controls.Add(peakDistanceLabel, 0, 1);
            paramsLayout.Controls.Add(peakDistanceNumeric, 1, 1);
            paramsLayout.Controls.Add(prominenceLabel, 2, 1);
            paramsLayout.Controls.Add(prominenceNumeric, 3, 1);

            parametersGroupBox.Controls.Add(paramsLayout);
            parametersPanel.Controls.Add(parametersGroupBox, 0, 0);

            // Options panel
            Panel optionsPanel = new Panel
            {
                Dock = DockStyle.Fill
            };

            showPeaksCheckBox = new CheckBox
            {
                Text = "Show Peaks",
                Checked = showPeaks,
                Location = new Point(10, 10)
            };

            invertImageCheckBox = new CheckBox
            {
                Text = "Invert Image",
                Checked = invertImage,
                Location = new Point(10, 35)
            };

            cropAirCheckBox = new CheckBox
            {
                Text = "Crop Air",
                Checked = cropAir,
                Location = new Point(10, 60)
            };

            optionsPanel.Controls.Add(showPeaksCheckBox);
            optionsPanel.Controls.Add(invertImageCheckBox);
            optionsPanel.Controls.Add(cropAirCheckBox);
            parametersPanel.Controls.Add(optionsPanel, 1, 0);

            // Buttons panel
            Panel buttonsPanel = new Panel
            {
                Dock = DockStyle.Fill
            };

            processButton = new Button
            {
                Text = "Process All",
                Location = new Point(10, 10),
                Width = 100
            };

            exportButton = new Button
            {
                Text = "Export Results",
                Location = new Point(10, 40),
                Width = 100
            };
            Button createCompositeButton = new Button
            {
                Text = "Create Composite",
                Location = new Point(10, 70),
                Width = 100
            };

            createCompositeButton.Click += CreateCompositeButton_Click;
            

            progressBar = new ProgressBar
            {
                Location = new Point(10, 100),
                Width = 100,
                Height = 20
            };

            processButton.Click += ProcessButton_Click;
            exportButton.Click += ExportButton_Click;

            diskRadiusNumeric.ValueChanged += (s, e) => { diskRadius = (int)diskRadiusNumeric.Value; };
            gaussianSigmaNumeric.ValueChanged += (s, e) => { gaussianSigma = (double)gaussianSigmaNumeric.Value; };
            peakDistanceNumeric.ValueChanged += (s, e) => { peakDistance = (int)peakDistanceNumeric.Value; };
            prominenceNumeric.ValueChanged += (s, e) => { peakProminence = (double)prominenceNumeric.Value; };

            showPeaksCheckBox.CheckedChanged += (s, e) =>
            {
                showPeaks = showPeaksCheckBox.Checked;
                UpdateCharts();
            };

            invertImageCheckBox.CheckedChanged += (s, e) =>
            {
                invertImage = invertImageCheckBox.Checked;
                Task.Run(() => ProcessAllViews());
            };

            cropAirCheckBox.CheckedChanged += (s, e) =>
            {
                cropAir = cropAirCheckBox.Checked;
                Task.Run(() => ProcessAllViews());
            };

            buttonsPanel.Controls.Add(processButton);
            buttonsPanel.Controls.Add(exportButton);
            buttonsPanel.Controls.Add(createCompositeButton);
            buttonsPanel.Controls.Add(progressBar);
            parametersPanel.Controls.Add(buttonsPanel, 2, 0);

            mainLayout.Controls.Add(parametersPanel, 0, 3);
        }

        private void ConfigureChart(Chart chart, string title, string xAxisTitle, string yAxisTitle)
        {
            chart.Titles.Clear();

            // Create a chart area
            ChartArea chartArea = new ChartArea();
            chart.ChartAreas.Clear();

            // Set up borders and position
            chartArea.BorderWidth = 0;
            chartArea.Position.Auto = false;
            chartArea.Position = new ElementPosition(0, 0, 100, 95);
            chartArea.InnerPlotPosition = new ElementPosition(5, 0, 90, 100);

            // Set up grid lines
            chartArea.AxisX.MajorGrid.LineColor = Color.LightGray;
            chartArea.AxisX.MajorGrid.LineWidth = 1;
            chartArea.AxisY.MajorGrid.LineColor = Color.LightGray;
            chartArea.AxisY.MajorGrid.LineWidth = 1;

            // Don't reverse Y axis - matches PictureBox coordinates
            chartArea.AxisY.IsReversed = true;

            chart.ChartAreas.Add(chartArea);

            // Add series
            chart.Series.Clear();

            Series profileSeries = new Series("Profile");
            profileSeries.ChartType = SeriesChartType.Line;
            profileSeries.Color = Color.Blue;
            profileSeries.BorderWidth = 2;
            chart.Series.Add(profileSeries);

            Series darkPeaksSeries = new Series("Dark Peaks");
            darkPeaksSeries.ChartType = SeriesChartType.Point;
            darkPeaksSeries.Color = Color.Red;
            darkPeaksSeries.MarkerStyle = MarkerStyle.Cross;
            darkPeaksSeries.MarkerSize = 10;
            chart.Series.Add(darkPeaksSeries);

            Series brightPeaksSeries = new Series("Bright Peaks");
            brightPeaksSeries.ChartType = SeriesChartType.Point;
            brightPeaksSeries.Color = Color.Green;
            brightPeaksSeries.MarkerStyle = MarkerStyle.Circle;
            brightPeaksSeries.MarkerSize = 10;
            chart.Series.Add(brightPeaksSeries);

            // Legend setup
            chart.Legends.Clear();
            Legend legend = new Legend("Legend");
            legend.Docking = Docking.Bottom;
            legend.Font = new Font("Arial", 7);
            legend.IsDockedInsideChartArea = true;
            legend.BackColor = Color.FromArgb(200, Color.White);
            chart.Legends.Add(legend);
        }

        #region Mouse Events

        // XY PictureBox mouse events
        private void XYPictureBox_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                lastMousePosition = e.Location;
                xyPictureBox.Capture = true;
            }
        }

        private void XYPictureBox_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && xyPictureBox.Capture)
            {
                int dx = e.Location.X - lastMousePosition.X;
                int dy = e.Location.Y - lastMousePosition.Y;
                xyPan = new PointF(xyPan.X + dx, xyPan.Y + dy);
                lastMousePosition = e.Location;

                xyPictureBox.Invalidate();

                // Update chart to match pan for proper alignment
                UpdateXYChart();
            }
        }
        private void XYPictureBox_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
                xyPictureBox.Capture = false;
        }

        private void XYPictureBox_MouseWheel(object sender, MouseEventArgs e)
        {
            float oldZoom = xyZoom;
            float factor = (e.Delta > 0) ? 1.1f : 0.9f;
            xyZoom = Math.Max(0.1f, Math.Min(10f, xyZoom * factor));

            // Adjust pan to zoom around mouse position
            Point mousePos = e.Location;
            float zoomRatio = xyZoom / oldZoom;
            xyPan.X = mousePos.X - (zoomRatio * (mousePos.X - xyPan.X));
            xyPan.Y = mousePos.Y - (zoomRatio * (mousePos.Y - xyPan.Y));

            xyPictureBox.Invalidate();

            // Update chart to match zoom level - CRITICAL for alignment
            UpdateXYChart();
        }

        private void XYPictureBox_Paint(object sender, PaintEventArgs e)
        {
            if (xyProcessedImage == null)
                return;

            // The image is automatically drawn by the PictureBox control
            // We'll just draw overlays on top of it

            // Draw scale bar
            DrawScaleBar(e.Graphics, xyPictureBox.ClientRectangle, 1.0f);

            // Draw view label
            using (Font font = new Font("Arial", 12, FontStyle.Bold))
            using (SolidBrush brush = new SolidBrush(Color.Yellow))
            {
                e.Graphics.DrawString("XY", font, brush, new PointF(10, 10));
            }

            // Draw slice indicator
            using (Font font = new Font("Arial", 10))
            using (SolidBrush brush = new SolidBrush(Color.White))
            {
                string sliceText = $"Slice: {(int)xySliceNumeric.Value}";
                SizeF textSize = e.Graphics.MeasureString(sliceText, font);
                PointF pos = new PointF(xyPictureBox.ClientSize.Width - textSize.Width - 10, 10);
                e.Graphics.DrawString(sliceText, font, brush, pos);
            }

            // Draw peak lines if enabled - HORIZONTAL LINES with scaling
            if (showPeaks && xyDarkPeaks != null && xyRowProfile != null)
            {
                // Calculate scaling factor based on the stretched image height
                float scaleY = (float)xyPictureBox.ClientSize.Height / xyProcessedImage.Height;

                using (Pen darkPen = new Pen(Color.Red, 1))
                {
                    foreach (int peak in xyDarkPeaks)
                    {
                        int y1 = (int)(peak * scaleY);
                        e.Graphics.DrawLine(darkPen, 0, y1, xyPictureBox.Width, y1);
                    }
                }

                using (Pen brightPen = new Pen(Color.Green, 1))
                {
                    foreach (int peak in xyBrightPeaks)
                    {
                        int y1 = (int)(peak * scaleY);
                        e.Graphics.DrawLine(brightPen, 0, y1, xyPictureBox.Width, y1);
                    }
                }
            }
        }

        // XZ PictureBox mouse events
        private void XZPictureBox_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                lastMousePosition = e.Location;
                xzPictureBox.Capture = true;
            }
        }

        private void XZPictureBox_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && xzPictureBox.Capture)
            {
                int dx = e.Location.X - lastMousePosition.X;
                int dy = e.Location.Y - lastMousePosition.Y;
                xzPan = new PointF(xzPan.X + dx, xzPan.Y + dy);
                lastMousePosition = e.Location;

                xzPictureBox.Invalidate();

                // Update chart to match pan for proper alignment
                UpdateXZChart();
            }
        }

        private void XZPictureBox_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
                xzPictureBox.Capture = false;
        }

        private void XZPictureBox_MouseWheel(object sender, MouseEventArgs e)
        {
            float oldZoom = xzZoom;
            float factor = (e.Delta > 0) ? 1.1f : 0.9f;
            xzZoom = Math.Max(0.1f, Math.Min(10f, xzZoom * factor));

            // Adjust pan to zoom around mouse position
            Point mousePos = e.Location;
            float zoomRatio = xzZoom / oldZoom;
            xzPan.X = mousePos.X - (zoomRatio * (mousePos.X - xzPan.X));
            xzPan.Y = mousePos.Y - (zoomRatio * (mousePos.Y - xzPan.Y));

            xzPictureBox.Invalidate();

            // Update chart to match zoom level - CRITICAL for alignment
            UpdateXZChart();
        }


        private void XZPictureBox_Paint(object sender, PaintEventArgs e)
        {
            if (xzProcessedImage == null)
                return;

            // The image is automatically drawn by the PictureBox control
            // We'll just draw overlays on top of it

            // Draw scale bar
            DrawScaleBar(e.Graphics, xzPictureBox.ClientRectangle, 1.0f);

            // Draw view label
            using (Font font = new Font("Arial", 12, FontStyle.Bold))
            using (SolidBrush brush = new SolidBrush(Color.Yellow))
            {
                e.Graphics.DrawString("XZ", font, brush, new PointF(10, 10));
            }

            // Draw slice indicator
            using (Font font = new Font("Arial", 10))
            using (SolidBrush brush = new SolidBrush(Color.White))
            {
                string sliceText = $"Y: {(int)xzSliceNumeric.Value}";
                SizeF textSize = e.Graphics.MeasureString(sliceText, font);
                PointF pos = new PointF(xzPictureBox.ClientSize.Width - textSize.Width - 10, 10);
                e.Graphics.DrawString(sliceText, font, brush, pos);
            }

            // Draw peak lines if enabled - HORIZONTAL LINES with scaling
            if (showPeaks && xzDarkPeaks != null && xzRowProfile != null)
            {
                // Calculate scaling factor based on the stretched image height
                float scaleY = (float)xzPictureBox.ClientSize.Height / xzProcessedImage.Height;

                using (Pen darkPen = new Pen(Color.Red, 1))
                {
                    foreach (int peak in xzDarkPeaks)
                    {
                        int y1 = (int)(peak * scaleY);
                        e.Graphics.DrawLine(darkPen, 0, y1, xzPictureBox.Width, y1);
                    }
                }

                using (Pen brightPen = new Pen(Color.Green, 1))
                {
                    foreach (int peak in xzBrightPeaks)
                    {
                        int y1 = (int)(peak * scaleY);
                        e.Graphics.DrawLine(brightPen, 0, y1, xzPictureBox.Width, y1);
                    }
                }
            }
        }
        // YZ PictureBox mouse events
        private void YZPictureBox_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                lastMousePosition = e.Location;
                yzPictureBox.Capture = true;
            }
        }
        private void YZPictureBox_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && yzPictureBox.Capture)
            {
                int dx = e.Location.X - lastMousePosition.X;
                int dy = e.Location.Y - lastMousePosition.Y;
                yzPan = new PointF(yzPan.X + dx, yzPan.Y + dy);
                lastMousePosition = e.Location;

                yzPictureBox.Invalidate();

                // Update chart to match pan for proper alignment
                UpdateYZChart();
            }
        }
        private void YZPictureBox_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
                yzPictureBox.Capture = false;
        }

        private void YZPictureBox_MouseWheel(object sender, MouseEventArgs e)
        {
            float oldZoom = yzZoom;
            float factor = (e.Delta > 0) ? 1.1f : 0.9f;
            yzZoom = Math.Max(0.1f, Math.Min(10f, yzZoom * factor));

            // Adjust pan to zoom around mouse position
            Point mousePos = e.Location;
            float zoomRatio = yzZoom / oldZoom;
            yzPan.X = mousePos.X - (zoomRatio * (mousePos.X - yzPan.X));
            yzPan.Y = mousePos.Y - (zoomRatio * (mousePos.Y - yzPan.Y));

            yzPictureBox.Invalidate();

            // Update chart to match zoom level - CRITICAL for alignment
            UpdateYZChart();
        }
        private void YZPictureBox_Paint(object sender, PaintEventArgs e)
        {
            if (yzProcessedImage == null)
                return;

            // The image is automatically drawn by the PictureBox control
            // We'll just draw overlays on top of it

            // Draw scale bar
            DrawScaleBar(e.Graphics, yzPictureBox.ClientRectangle, 1.0f);

            // Draw view label
            using (Font font = new Font("Arial", 12, FontStyle.Bold))
            using (SolidBrush brush = new SolidBrush(Color.Yellow))
            {
                e.Graphics.DrawString("YZ", font, brush, new PointF(10, 10));
            }

            // Draw slice indicator
            using (Font font = new Font("Arial", 10))
            using (SolidBrush brush = new SolidBrush(Color.White))
            {
                string sliceText = $"X: {(int)yzSliceNumeric.Value}";
                SizeF textSize = e.Graphics.MeasureString(sliceText, font);
                PointF pos = new PointF(yzPictureBox.ClientSize.Width - textSize.Width - 10, 10);
                e.Graphics.DrawString(sliceText, font, brush, pos);
            }

            // Draw peak lines if enabled - HORIZONTAL LINES with scaling
            if (showPeaks && yzDarkPeaks != null && yzRowProfile != null)
            {
                // Calculate scaling factor based on the stretched image height
                float scaleY = (float)yzPictureBox.ClientSize.Height / yzProcessedImage.Height;

                using (Pen darkPen = new Pen(Color.Red, 1))
                {
                    foreach (int peak in yzDarkPeaks)
                    {
                        int y1 = (int)(peak * scaleY);
                        e.Graphics.DrawLine(darkPen, 0, y1, yzPictureBox.Width, y1);
                    }
                }

                using (Pen brightPen = new Pen(Color.Green, 1))
                {
                    foreach (int peak in yzBrightPeaks)
                    {
                        int y1 = (int)(peak * scaleY);
                        e.Graphics.DrawLine(brightPen, 0, y1, yzPictureBox.Width, y1);
                    }
                }
            }
        }

        #endregion

        #region Button Events

        private void ProcessButton_Click(object sender, EventArgs e)
        {
            // Update parameters from UI
            diskRadius = (int)diskRadiusNumeric.Value;
            gaussianSigma = (double)gaussianSigmaNumeric.Value;
            peakDistance = (int)peakDistanceNumeric.Value;
            peakProminence = (double)prominenceNumeric.Value;

            // Process all views
            Task.Run(() => ProcessAllViews());
        }

        private void ExportButton_Click(object sender, EventArgs e)
        {
            // Disable the button to prevent multiple clicks
            exportButton.Enabled = false;
            progressBar.Value = 0;
            progressBar.Visible = true;

            try
            {
                // Run the export operation in a background task
                Task.Run(async () =>
                {
                    try
                    {
                        await ExportResults();

                        // Update UI on the main thread
                        this.Invoke(new Action(() =>
                        {
                            progressBar.Value = 100;
                            exportButton.Enabled = true;
                           // MessageBox.Show("Export completed successfully!", "Export Results",
                           //     MessageBoxButtons.OK, MessageBoxIcon.Information);
                            progressBar.Value = 0;
                        }));
                    }
                    catch (Exception ex)
                    {
                        // Show error on the main thread
                        this.Invoke(new Action(() =>
                        {
                            Logger.Log($"[BandDetectionForm] Error exporting results: {ex.Message}");
                            MessageBox.Show($"Error exporting results: {ex.Message}",
                                "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            exportButton.Enabled = true;
                            progressBar.Value = 0;
                        }));
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Log($"[BandDetectionForm] Error initiating export: {ex.Message}");
                MessageBox.Show($"Error initiating export: {ex.Message}",
                    "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                exportButton.Enabled = true;
                progressBar.Value = 0;
            }
        }


        #endregion

        #region Processing Methods

        private async Task ProcessAllViews()
        {
            // Cancel any previous processing
            cancellationTokenSource.Cancel();
            cancellationTokenSource = new CancellationTokenSource();
            var token = cancellationTokenSource.Token;

            try
            {
                UpdateProgressBar(0);

                // Process all three views truly concurrently
                var tasks = new List<Task>();

                // Instead of sequentially adding tasks one after another,
                // create all tasks at once and let them run in parallel
                tasks.Add(ProcessXYView(token));
                tasks.Add(ProcessXZView(token));
                tasks.Add(ProcessYZView(token));

                // Wait for all tasks to complete
                int completedTasks = 0;
                foreach (var task in tasks)
                {
                    await task;
                    completedTasks++;
                    UpdateProgressBar(completedTasks * 30); // Update progress proportionally
                }

                // Update all charts on UI thread
                this.Invoke(new Action(() => {
                    // Explicitly reconfigure charts if needed
                    if (xyChart.Series.Count == 0 || xyChart.Series["Profile"] == null)
                        ConfigureChart(xyChart, "XY Row Profile", "Intensity", "Row");

                    if (xzChart.Series.Count == 0 || xzChart.Series["Profile"] == null)
                        ConfigureChart(xzChart, "XZ Row Profile", "Intensity", "Row");

                    if (yzChart.Series.Count == 0 || yzChart.Series["Profile"] == null)
                        ConfigureChart(yzChart, "YZ Row Profile", "Intensity", "Row");

                    UpdateCharts();
                }));

                UpdateProgressBar(100);
            }
            catch (OperationCanceledException)
            {
                // Processing was cancelled
                Logger.Log("[BandDetectionForm] Processing cancelled");
            }
            catch (Exception ex)
            {
                Logger.Log($"[BandDetectionForm] Error processing views: {ex.Message}");
                MessageBox.Show($"Error processing views: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                UpdateProgressBar(0);
            }
        }

        private async Task ProcessXYView(CancellationToken token = default)
        {
            try
            {
                // Get the current slice
                int sliceZ = (int)xySliceNumeric.Value;

                // Check if token is provided, if not use the current one
                if (token == default)
                    token = cancellationTokenSource.Token;

                // Dispose old image
                if (xyProcessedImage != null)
                {
                    var oldImage = xyProcessedImage;
                    xyProcessedImage = null;
                    oldImage.Dispose();
                }

                // Get raw image data from MainForm
                Bitmap rawSlice = mainForm.GetSliceBitmap(sliceZ);

                // Process the image
                var result = await ProcessImage(rawSlice, token);
                xyProcessedImage = result.Item1;
                xyRowProfile = result.Item2;
                xyDarkPeaks = result.Item3;
                xyBrightPeaks = result.Item4;

                // Update the UI
                this.Invoke(new Action(() =>
                {
                    xyPictureBox.Image = xyProcessedImage;
                    xyPictureBox.Invalidate();
                    UpdateXYChart();
                }));

                // Clean up raw slice
                rawSlice.Dispose();
            }
            catch (OperationCanceledException)
            {
                // Processing was cancelled
                Logger.Log("[BandDetectionForm] XY processing cancelled");
            }
            catch (Exception ex)
            {
                Logger.Log($"[BandDetectionForm] Error processing XY view: {ex.Message}");
                MessageBox.Show($"Error processing XY view: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async Task ProcessXZView(CancellationToken token = default)
        {
            try
            {
                // Get the current slice
                int sliceY = (int)xzSliceNumeric.Value;

                // Check if token is provided, if not use the current one
                if (token == default)
                    token = cancellationTokenSource.Token;

                // Dispose old image
                if (xzProcessedImage != null)
                {
                    var oldImage = xzProcessedImage;
                    xzProcessedImage = null;
                    oldImage.Dispose();
                }

                // Get raw image data from MainForm
                Bitmap rawSlice = mainForm.GetXZSliceBitmap(sliceY);

                // Process the image
                var result = await ProcessImage(rawSlice, token);
                xzProcessedImage = result.Item1;
                xzRowProfile = result.Item2;
                xzDarkPeaks = result.Item3;
                xzBrightPeaks = result.Item4;

                // Update the UI
                this.Invoke(new Action(() =>
                {
                    xzPictureBox.Image = xzProcessedImage;
                    xzPictureBox.Invalidate();
                    UpdateXZChart();
                }));

                // Clean up raw slice
                rawSlice.Dispose();
            }
            catch (OperationCanceledException)
            {
                // Processing was cancelled
                Logger.Log("[BandDetectionForm] XZ processing cancelled");
            }
            catch (Exception ex)
            {
                Logger.Log($"[BandDetectionForm] Error processing XZ view: {ex.Message}");
                MessageBox.Show($"Error processing XZ view: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async Task ProcessYZView(CancellationToken token = default)
        {
            try
            {
                // Get the current slice
                int sliceX = (int)yzSliceNumeric.Value;

                // Check if token is provided, if not use the current one
                if (token == default)
                    token = cancellationTokenSource.Token;

                // Dispose old image
                if (yzProcessedImage != null)
                {
                    var oldImage = yzProcessedImage;
                    yzProcessedImage = null;
                    oldImage.Dispose();
                }

                // Get raw image data from MainForm
                Bitmap rawSlice = mainForm.GetYZSliceBitmap(sliceX);

                // Process the image
                var result = await ProcessImage(rawSlice, token);
                yzProcessedImage = result.Item1;
                yzRowProfile = result.Item2;
                yzDarkPeaks = result.Item3;
                yzBrightPeaks = result.Item4;

                // Update the UI
                this.Invoke(new Action(() =>
                {
                    yzPictureBox.Image = yzProcessedImage;
                    yzPictureBox.Invalidate();
                    UpdateYZChart();
                }));

                // Clean up raw slice
                rawSlice.Dispose();
            }
            catch (OperationCanceledException)
            {
                // Processing was cancelled
                Logger.Log("[BandDetectionForm] YZ processing cancelled");
            }
            catch (Exception ex)
            {
                Logger.Log($"[BandDetectionForm] Error processing YZ view: {ex.Message}");
                MessageBox.Show($"Error processing YZ view: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async Task<Tuple<Bitmap, double[], int[], int[]>> ProcessImage(Bitmap sourceImage, CancellationToken token)
        {
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                int width = sourceImage.Width;
                int height = sourceImage.Height;

                // Get pixel data
                BitmapData sourceData = sourceImage.LockBits(
                    new Rectangle(0, 0, width, height),
                    ImageLockMode.ReadOnly,
                    PixelFormat.Format24bppRgb);

                int bytesPerPixel = 3; // Format24bppRgb
                int stride = sourceData.Stride;
                byte[] pixels = new byte[stride * height];
                Marshal.Copy(sourceData.Scan0, pixels, 0, pixels.Length);
                sourceImage.UnlockBits(sourceData);

                // Convert to grayscale float array - PARALLEL!
                float[,] grayImage = new float[width, height];

                Parallel.For(0, height, y =>
                {
                    for (int x = 0; x < width; x++)
                    {
                        int offset = y * stride + x * bytesPerPixel;
                        byte b = pixels[offset];
                        byte g = pixels[offset + 1];
                        byte r = pixels[offset + 2];

                        // Convert to grayscale using luminance formula
                        grayImage[x, y] = (0.299f * r + 0.587f * g + 0.114f * b) / 255.0f;
                    }
                });

                // Crop top (remove air) if enabled - PARALLEL row sum calculation
                int cropRow = 0;
                if (cropAir)
                {
                    // Compute row sums to detect top boundary - in parallel
                    double[] rowSums = new double[height];

                    Parallel.For(0, height, y =>
                    {
                        double sum = 0;
                        for (int x = 0; x < width; x++)
                        {
                            sum += grayImage[x, y];
                        }
                        rowSums[y] = sum;
                    });

                    // Find the first row with significant content
                    double maxSum = rowSums.Max();
                    double threshold = 0.05 * maxSum;
                    for (int y = 0; y < height; y++)
                    {
                        if (rowSums[y] > threshold)
                        {
                            cropRow = y;
                            break;
                        }
                    }
                }

                // Adjust height after cropping
                int newHeight = height - cropRow;

                // Invert if requested - PARALLEL!
                if (invertImage)
                {
                    Parallel.For(0, height, y =>
                    {
                        for (int x = 0; x < width; x++)
                        {
                            grayImage[x, y] = 1.0f - grayImage[x, y];
                        }
                    });
                }

                // Apply morphological top-hat filtering - PARALLEL IMPLEMENTATION
                token.ThrowIfCancellationRequested();
                float[,] topHatImage = WhiteTopHatParallel(grayImage, diskRadius, cropRow, token);

                // Apply horizontal Gaussian filter - PARALLEL IMPLEMENTATION
                token.ThrowIfCancellationRequested();
                float[,] smoothedImage = HorizontalGaussianFilterParallel(topHatImage, (float)gaussianSigma, token);

                // Create result bitmap
                token.ThrowIfCancellationRequested();
                Bitmap resultImage = new Bitmap(width, newHeight, PixelFormat.Format24bppRgb);
                BitmapData resultData = resultImage.LockBits(
                    new Rectangle(0, 0, width, newHeight),
                    ImageLockMode.WriteOnly,
                    PixelFormat.Format24bppRgb);

                // Prepare output array
                byte[] resultPixels = new byte[resultData.Stride * newHeight];

                // Find min/max values for normalization - PARALLEL!
                float min = float.MaxValue;
                float max = float.MinValue;

                // Use parallel reduction to find min/max values
                object lockObj = new object();
                Parallel.For(0, newHeight, y =>
                {
                    float localMin = float.MaxValue;
                    float localMax = float.MinValue;

                    for (int x = 0; x < width; x++)
                    {
                        float val = smoothedImage[x, y];
                        if (val < localMin) localMin = val;
                        if (val > localMax) localMax = val;
                    }

                    lock (lockObj)
                    {
                        if (localMin < min) min = localMin;
                        if (localMax > max) max = localMax;
                    }
                });

                // Create normalized output image - PARALLEL!
                Parallel.For(0, newHeight, y =>
                {
                    for (int x = 0; x < width; x++)
                    {
                        // Normalize to 0-255 range
                        float normalizedValue = (smoothedImage[x, y] - min) / (max - min);
                        byte pixelValue = (byte)(normalizedValue * 255);

                        int offset = y * resultData.Stride + x * bytesPerPixel;
                        resultPixels[offset] = pixelValue;     // Blue
                        resultPixels[offset + 1] = pixelValue; // Green
                        resultPixels[offset + 2] = pixelValue; // Red
                    }
                });

                // Copy the result pixels to the bitmap
                Marshal.Copy(resultPixels, 0, resultData.Scan0, resultPixels.Length);
                resultImage.UnlockBits(resultData);

                // Calculate row profile (sum each row) - PARALLEL!
                token.ThrowIfCancellationRequested();
                double[] rowProfile = new double[newHeight];

                Parallel.For(0, newHeight, y =>
                {
                    double sum = 0;
                    for (int x = 0; x < width; x++)
                    {
                        sum += smoothedImage[x, y];
                    }
                    rowProfile[y] = sum;
                });

                // Apply Gaussian smoothing to the row profile
                double[] smoothRowProfile = GaussianSmoothArray(rowProfile, (float)gaussianSigma);

                // Find peaks (dark minima and bright maxima)
                token.ThrowIfCancellationRequested();
                int[] darkPeaks = FindDarkPeaks(smoothRowProfile, peakDistance, (float)peakProminence);
                int[] brightPeaks = FindBrightPeaks(smoothRowProfile, peakDistance, (float)peakProminence);

                return new Tuple<Bitmap, double[], int[], int[]>(resultImage, smoothRowProfile, darkPeaks, brightPeaks);
            }, token);
        }
        private float[,] WhiteTopHatParallel(float[,] image, int radius, int cropRow, CancellationToken token)
        {
            int width = image.GetLength(0);
            int height = image.GetLength(1);
            int newHeight = height - cropRow;

            // Create output image
            float[,] result = new float[width, newHeight];

            // Create disk structuring element
            bool[,] disk = CreateDiskStructuringElement(radius);
            int diskWidth = disk.GetLength(0);
            int diskHeight = disk.GetLength(1);
            int halfDiskWidth = diskWidth / 2;
            int halfDiskHeight = diskHeight / 2;

            // Apply morphological opening
            float[,] opened = new float[width, newHeight];

            // Initialize with maximum value - PARALLEL!
            Parallel.For(0, newHeight, y =>
            {
                for (int x = 0; x < width; x++)
                {
                    opened[x, y] = float.MaxValue;
                }
            });

            // Perform dilation on the inverted image (erosion on original) - PARALLEL!
            Parallel.For(0, newHeight, y =>
            {
                if (token.IsCancellationRequested) return;

                for (int x = 0; x < width; x++)
                {
                    float minVal = float.MaxValue;

                    for (int dy = -halfDiskHeight; dy <= halfDiskHeight; dy++)
                    {
                        for (int dx = -halfDiskWidth; dx <= halfDiskWidth; dx++)
                        {
                            int nx = x + dx;
                            int ny = y + dy + cropRow;

                            if (nx >= 0 && nx < width && ny >= 0 && ny < height &&
                                disk[dx + halfDiskWidth, dy + halfDiskHeight])
                            {
                                minVal = Math.Min(minVal, image[nx, ny]);
                            }
                        }
                    }

                    opened[x, y] = minVal;
                }
            });

            // Copy opened to temp - PARALLEL for better memory access pattern
            float[,] temp = new float[width, newHeight];

            Parallel.For(0, newHeight, y =>
            {
                for (int x = 0; x < width; x++)
                {
                    temp[x, y] = opened[x, y];
                }
            });

            // Perform erosion on the result (dilation on original) - PARALLEL!
            Parallel.For(0, newHeight, y =>
            {
                if (token.IsCancellationRequested) return;

                for (int x = 0; x < width; x++)
                {
                    float maxVal = float.MinValue;

                    for (int dy = -halfDiskHeight; dy <= halfDiskHeight; dy++)
                    {
                        for (int dx = -halfDiskWidth; dx <= halfDiskWidth; dx++)
                        {
                            int nx = x + dx;
                            int ny = y + dy;

                            if (nx >= 0 && nx < width && ny >= 0 && ny < newHeight &&
                                disk[dx + halfDiskWidth, dy + halfDiskHeight])
                            {
                                maxVal = Math.Max(maxVal, temp[nx, ny]);
                            }
                        }
                    }

                    opened[x, y] = maxVal;
                }
            });

            // Compute top-hat: original - opening - PARALLEL!
            Parallel.For(0, newHeight, y =>
            {
                if (token.IsCancellationRequested) return;

                for (int x = 0; x < width; x++)
                {
                    result[x, y] = image[x, y + cropRow] - opened[x, y];
                }
            });

            return result;
        }

        // Optimized parallel implementation of HorizontalGaussianFilter
        private float[,] HorizontalGaussianFilterParallel(float[,] image, float sigma, CancellationToken token)
        {
            int width = image.GetLength(0);
            int height = image.GetLength(1);

            // Create output image
            float[,] result = new float[width, height];

            // Create 1D Gaussian kernel
            int kernelSize = (int)(6.0 * sigma);
            if (kernelSize % 2 == 0) kernelSize++; // Make sure kernel size is odd
            float[] kernel = new float[kernelSize];
            float sum = 0;
            int halfKernelSize = kernelSize / 2;

            for (int i = 0; i < kernelSize; i++)
            {
                float x = i - halfKernelSize;
                kernel[i] = (float)Math.Exp(-(x * x) / (2 * sigma * sigma));
                sum += kernel[i];
            }

            // Normalize kernel
            for (int i = 0; i < kernelSize; i++)
            {
                kernel[i] /= sum;
            }

            // Apply horizontal Gaussian filter - PARALLEL!
            Parallel.For(0, height, y =>
            {
                if (token.IsCancellationRequested) return;

                for (int x = 0; x < width; x++)
                {
                    float sum2 = 0;
                    float weightSum = 0;

                    for (int i = 0; i < kernelSize; i++)
                    {
                        int xpos = x + i - halfKernelSize;

                        if (xpos >= 0 && xpos < width)
                        {
                            sum2 += image[xpos, y] * kernel[i];
                            weightSum += kernel[i];
                        }
                    }

                    result[x, y] = sum2 / weightSum;
                }
            });

            return result;
        }

        private float[,] WhiteTopHat(float[,] image, int radius, int cropRow, CancellationToken token)
        {
            int width = image.GetLength(0);
            int height = image.GetLength(1);
            int newHeight = height - cropRow;

            // Create output image
            float[,] result = new float[width, newHeight];

            // Create disk structuring element
            bool[,] disk = CreateDiskStructuringElement(radius);
            int diskWidth = disk.GetLength(0);
            int diskHeight = disk.GetLength(1);
            int halfDiskWidth = diskWidth / 2;
            int halfDiskHeight = diskHeight / 2;

            // Apply morphological opening
            float[,] opened = new float[width, newHeight];

            // Initialize with maximum value
            for (int y = 0; y < newHeight; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    opened[x, y] = float.MaxValue;
                }
            }

            // Perform dilation on the inverted image (erosion on original)
            for (int y = 0; y < newHeight; y++)
            {
                token.ThrowIfCancellationRequested();

                for (int x = 0; x < width; x++)
                {
                    float minVal = float.MaxValue;

                    for (int dy = -halfDiskHeight; dy <= halfDiskHeight; dy++)
                    {
                        for (int dx = -halfDiskWidth; dx <= halfDiskWidth; dx++)
                        {
                            int nx = x + dx;
                            int ny = y + dy + cropRow;

                            if (nx >= 0 && nx < width && ny >= 0 && ny < height &&
                                disk[dx + halfDiskWidth, dy + halfDiskHeight])
                            {
                                minVal = Math.Min(minVal, image[nx, ny]);
                            }
                        }
                    }

                    opened[x, y] = minVal;
                }
            }

            // Perform erosion on the result (dilation on original)
            float[,] temp = new float[width, newHeight];
            Array.Copy(opened, temp, opened.Length);

            for (int y = 0; y < newHeight; y++)
            {
                token.ThrowIfCancellationRequested();

                for (int x = 0; x < width; x++)
                {
                    float maxVal = float.MinValue;

                    for (int dy = -halfDiskHeight; dy <= halfDiskHeight; dy++)
                    {
                        for (int dx = -halfDiskWidth; dx <= halfDiskWidth; dx++)
                        {
                            int nx = x + dx;
                            int ny = y + dy;

                            if (nx >= 0 && nx < width && ny >= 0 && ny < newHeight &&
                                disk[dx + halfDiskWidth, dy + halfDiskHeight])
                            {
                                maxVal = Math.Max(maxVal, temp[nx, ny]);
                            }
                        }
                    }

                    opened[x, y] = maxVal;
                }
            }

            // Compute top-hat: original - opening
            for (int y = 0; y < newHeight; y++)
            {
                token.ThrowIfCancellationRequested();

                for (int x = 0; x < width; x++)
                {
                    result[x, y] = image[x, y + cropRow] - opened[x, y];
                }
            }

            return result;
        }

        private bool[,] CreateDiskStructuringElement(int radius)
        {
            int diameter = 2 * radius + 1;
            bool[,] disk = new bool[diameter, diameter];
            int radiusSquared = radius * radius;

            for (int y = 0; y < diameter; y++)
            {
                for (int x = 0; x < diameter; x++)
                {
                    int dx = x - radius;
                    int dy = y - radius;
                    disk[x, y] = (dx * dx + dy * dy) <= radiusSquared;
                }
            }

            return disk;
        }

        private float[,] HorizontalGaussianFilter(float[,] image, float sigma, CancellationToken token)
        {
            int width = image.GetLength(0);
            int height = image.GetLength(1);

            // Create output image
            float[,] result = new float[width, height];

            // Create 1D Gaussian kernel
            int kernelSize = (int)(6.0 * sigma);
            if (kernelSize % 2 == 0) kernelSize++; // Make sure kernel size is odd
            float[] kernel = new float[kernelSize];
            float sum = 0;
            int halfKernelSize = kernelSize / 2;

            for (int i = 0; i < kernelSize; i++)
            {
                float x = i - halfKernelSize;
                kernel[i] = (float)Math.Exp(-(x * x) / (2 * sigma * sigma));
                sum += kernel[i];
            }

            // Normalize kernel
            for (int i = 0; i < kernelSize; i++)
            {
                kernel[i] /= sum;
            }

            // Apply horizontal Gaussian filter
            for (int y = 0; y < height; y++)
            {
                token.ThrowIfCancellationRequested();

                for (int x = 0; x < width; x++)
                {
                    float sum2 = 0;
                    float weightSum = 0;

                    for (int i = 0; i < kernelSize; i++)
                    {
                        int xpos = x + i - halfKernelSize;

                        if (xpos >= 0 && xpos < width)
                        {
                            sum2 += image[xpos, y] * kernel[i];
                            weightSum += kernel[i];
                        }
                    }

                    result[x, y] = sum2 / weightSum;
                }
            }

            return result;
        }

        private double[] GaussianSmoothArray(double[] array, float sigma)
        {
            int length = array.Length;
            double[] result = new double[length];

            // Create 1D Gaussian kernel
            int kernelSize = (int)(6.0 * sigma);
            if (kernelSize % 2 == 0) kernelSize++; // Make sure kernel size is odd
            double[] kernel = new double[kernelSize];
            double sum = 0;
            int halfKernelSize = kernelSize / 2;

            for (int i = 0; i < kernelSize; i++)
            {
                double x = i - halfKernelSize;
                kernel[i] = Math.Exp(-(x * x) / (2 * sigma * sigma));
                sum += kernel[i];
            }

            // Normalize kernel
            for (int i = 0; i < kernelSize; i++)
            {
                kernel[i] /= sum;
            }

            // Apply 1D Gaussian filter
            for (int i = 0; i < length; i++)
            {
                double sum2 = 0;
                double weightSum = 0;

                for (int j = 0; j < kernelSize; j++)
                {
                    int pos = i + j - halfKernelSize;

                    if (pos >= 0 && pos < length)
                    {
                        sum2 += array[pos] * kernel[j];
                        weightSum += kernel[j];
                    }
                }

                result[i] = sum2 / weightSum;
            }

            return result;
        }

        private int[] FindDarkPeaks(double[] profile, int minDistance, float prominence)
        {
            // For dark peaks, we invert the profile
            double[] invertedProfile = new double[profile.Length];
            for (int i = 0; i < profile.Length; i++)
            {
                invertedProfile[i] = -profile[i];
            }

            return FindPeaks(invertedProfile, minDistance, prominence);
        }

        private int[] FindBrightPeaks(double[] profile, int minDistance, float prominence)
        {
            return FindPeaks(profile, minDistance, prominence);
        }

        private int[] FindPeaks(double[] profile, int minDistance, float prominence)
        {
            List<int> peaks = new List<int>();

            // Find all local maxima
            for (int i = 1; i < profile.Length - 1; i++)
            {
                if (profile[i] > profile[i - 1] && profile[i] > profile[i + 1])
                {
                    peaks.Add(i);
                }
            }

            // Filter peaks by prominence
            List<int> filteredPeaks = new List<int>();
            foreach (int peak in peaks)
            {
                double peakValue = profile[peak];

                // Find nearest higher peaks on left and right
                int leftIdx = -1;
                for (int i = peak - 1; i >= 0; i--)
                {
                    if (profile[i] > peakValue)
                    {
                        leftIdx = i;
                        break;
                    }
                }

                int rightIdx = -1;
                for (int i = peak + 1; i < profile.Length; i++)
                {
                    if (profile[i] > peakValue)
                    {
                        rightIdx = i;
                        break;
                    }
                }

                // If no higher peak on left, use left edge
                if (leftIdx == -1)
                    leftIdx = 0;

                // If no higher peak on right, use right edge
                if (rightIdx == -1)
                    rightIdx = profile.Length - 1;

                // Find minimum in range [leftIdx, peak]
                double leftMin = double.MaxValue;
                for (int i = leftIdx; i <= peak; i++)
                {
                    if (profile[i] < leftMin)
                        leftMin = profile[i];
                }

                // Find minimum in range [peak, rightIdx]
                double rightMin = double.MaxValue;
                for (int i = peak; i <= rightIdx; i++)
                {
                    if (profile[i] < rightMin)
                        rightMin = profile[i];
                }

                // Calculate prominence
                double peakProminence = peakValue - Math.Max(leftMin, rightMin);

                // Add peak if prominence is high enough
                if (peakProminence >= prominence)
                {
                    filteredPeaks.Add(peak);
                }
            }

            // Filter peaks by minimum distance
            List<int> distanceFilteredPeaks = new List<int>();
            if (filteredPeaks.Count > 0)
            {
                // Sort peaks by prominence
                List<Tuple<int, double>> peaksWithValues = new List<Tuple<int, double>>();
                foreach (int peak in filteredPeaks)
                {
                    peaksWithValues.Add(new Tuple<int, double>(peak, profile[peak]));
                }

                peaksWithValues.Sort((a, b) => b.Item2.CompareTo(a.Item2));

                // Keep higher peaks, removing neighbors within minDistance
                bool[] isKept = new bool[profile.Length];
                foreach (var peak in peaksWithValues)
                {
                    int idx = peak.Item1;
                    bool keep = true;

                    // Check if there is already a peak within minDistance
                    for (int j = Math.Max(0, idx - minDistance); j <= Math.Min(profile.Length - 1, idx + minDistance); j++)
                    {
                        if (isKept[j])
                        {
                            keep = false;
                            break;
                        }
                    }

                    if (keep)
                    {
                        isKept[idx] = true;
                        distanceFilteredPeaks.Add(idx);
                    }
                }

                // Sort peaks by position
                distanceFilteredPeaks.Sort();
            }

            return distanceFilteredPeaks.ToArray();
        }

        #endregion

        #region UI Update Methods

        private void UpdateProgressBar(int value)
        {
            this.Invoke(new Action(() => progressBar.Value = value));
        }

        private void UpdateCharts()
        {
            UpdateXYChart();
            UpdateXZChart();
            UpdateYZChart();
        }

        private void UpdateXYChart()
        {
            if (xyRowProfile == null || xyProcessedImage == null)
                return;

            try
            {
                // Check if chart has series and recreate if missing
                if (xyChart.Series.Count == 0 || xyChart.Series["Profile"] == null)
                {
                    xyChart.Series.Clear();
                    xyChart.ChartAreas.Clear();
                    ConfigureChart(xyChart, "XY Row Profile", "Intensity", "Row");
                }

                // Clear all points
                foreach (Series series in xyChart.Series)
                {
                    series.Points.Clear();
                }

                // CRITICAL: Set chart Y axis to exactly match image height
                xyChart.ChartAreas[0].AxisY.Minimum = 0;
                xyChart.ChartAreas[0].AxisY.Maximum = xyProcessedImage.Height;

                // Find min/max values for X axis
                double min = double.MaxValue;
                double max = double.MinValue;
                for (int i = 0; i < xyRowProfile.Length; i++)
                {
                    if (xyRowProfile[i] < min) min = xyRowProfile[i];
                    if (xyRowProfile[i] > max) max = xyRowProfile[i];
                }

                double buffer = (max - min) * 0.05; // 5% buffer
                xyChart.ChartAreas[0].AxisX.Minimum = min - buffer;
                xyChart.ChartAreas[0].AxisX.Maximum = max + buffer;

                // Add profile data
                for (int i = 0; i < xyRowProfile.Length; i++)
                {
                    xyChart.Series["Profile"].Points.AddXY(xyRowProfile[i], i);
                }

                // Add dark peaks
                if (showPeaks && xyDarkPeaks != null)
                {
                    foreach (int peak in xyDarkPeaks)
                    {
                        if (peak < xyRowProfile.Length)
                            xyChart.Series["Dark Peaks"].Points.AddXY(xyRowProfile[peak], peak);
                    }
                }

                // Add bright peaks
                if (showPeaks && xyBrightPeaks != null)
                {
                    foreach (int peak in xyBrightPeaks)
                    {
                        if (peak < xyRowProfile.Length)
                            xyChart.Series["Bright Peaks"].Points.AddXY(xyRowProfile[peak], peak);
                    }
                }

                // Set visibility based on showPeaks
                xyChart.Series["Dark Peaks"].Enabled = showPeaks;
                xyChart.Series["Bright Peaks"].Enabled = showPeaks;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error processing XY chart: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        private void UpdateXZChart()
        {
            if (xzRowProfile == null || xzProcessedImage == null)
                return;

            try
            {
                // Check if chart has series and recreate if missing
                if (xzChart.Series.Count == 0 || xzChart.Series["Profile"] == null)
                {
                    xzChart.Series.Clear();
                    xzChart.ChartAreas.Clear();
                    ConfigureChart(xzChart, "XZ Row Profile", "Intensity", "Row");
                }

                // Clear all points
                foreach (Series series in xzChart.Series)
                {
                    series.Points.Clear();
                }

                // CRITICAL: Set chart Y axis to exactly match image height
                // This ensures the Y coordinates in the chart match the image rows
                xzChart.ChartAreas[0].AxisY.Minimum = 0;
                xzChart.ChartAreas[0].AxisY.Maximum = xzProcessedImage.Height;

                // Find min/max values for X axis
                double min = double.MaxValue;
                double max = double.MinValue;
                for (int i = 0; i < xzRowProfile.Length; i++)
                {
                    if (xzRowProfile[i] < min) min = xzRowProfile[i];
                    if (xzRowProfile[i] > max) max = xzRowProfile[i];
                }

                double buffer = (max - min) * 0.05; // 5% buffer
                xzChart.ChartAreas[0].AxisX.Minimum = min - buffer;
                xzChart.ChartAreas[0].AxisX.Maximum = max + buffer;

                // Add profile data - Y values match image row indices
                for (int i = 0; i < xzRowProfile.Length; i++)
                {
                    xzChart.Series["Profile"].Points.AddXY(xzRowProfile[i], i);
                }

                // Add dark peaks - Y values match peak positions in image
                if (showPeaks && xzDarkPeaks != null)
                {
                    foreach (int peak in xzDarkPeaks)
                    {
                        if (peak < xzRowProfile.Length)
                            xzChart.Series["Dark Peaks"].Points.AddXY(xzRowProfile[peak], peak);
                    }
                }

                // Add bright peaks - Y values match peak positions in image
                if (showPeaks && xzBrightPeaks != null)
                {
                    foreach (int peak in xzBrightPeaks)
                    {
                        if (peak < xzRowProfile.Length)
                            xzChart.Series["Bright Peaks"].Points.AddXY(xzRowProfile[peak], peak);
                    }
                }

                // Set visibility based on showPeaks
                xzChart.Series["Dark Peaks"].Enabled = showPeaks;
                xzChart.Series["Bright Peaks"].Enabled = showPeaks;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error processing XZ chart: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        private void UpdateYZChart()
        {
            if (yzRowProfile == null || yzProcessedImage == null)
                return;

            try
            {
                // Check if chart has series and recreate if missing
                if (yzChart.Series.Count == 0 || yzChart.Series["Profile"] == null)
                {
                    yzChart.Series.Clear();
                    yzChart.ChartAreas.Clear();
                    ConfigureChart(yzChart, "YZ Row Profile", "Intensity", "Row");
                }

                // Clear all points
                foreach (Series series in yzChart.Series)
                {
                    series.Points.Clear();
                }

                // CRITICAL: Set chart Y axis to exactly match image height
                // This ensures the Y coordinates in the chart match the image rows
                yzChart.ChartAreas[0].AxisY.Minimum = 0;
                yzChart.ChartAreas[0].AxisY.Maximum = yzProcessedImage.Height;

                // Find min/max values for X axis
                double min = double.MaxValue;
                double max = double.MinValue;
                for (int i = 0; i < yzRowProfile.Length; i++)
                {
                    if (yzRowProfile[i] < min) min = yzRowProfile[i];
                    if (yzRowProfile[i] > max) max = yzRowProfile[i];
                }

                double buffer = (max - min) * 0.05; // 5% buffer
                yzChart.ChartAreas[0].AxisX.Minimum = min - buffer;
                yzChart.ChartAreas[0].AxisX.Maximum = max + buffer;

                // Add profile data - Y values match image row indices
                for (int i = 0; i < yzRowProfile.Length; i++)
                {
                    yzChart.Series["Profile"].Points.AddXY(yzRowProfile[i], i);
                }

                // Add dark peaks - Y values match peak positions in image
                if (showPeaks && yzDarkPeaks != null)
                {
                    foreach (int peak in yzDarkPeaks)
                    {
                        if (peak < yzRowProfile.Length)
                            yzChart.Series["Dark Peaks"].Points.AddXY(yzRowProfile[peak], peak);
                    }
                }

                // Add bright peaks - Y values match peak positions in image
                if (showPeaks && yzBrightPeaks != null)
                {
                    foreach (int peak in yzBrightPeaks)
                    {
                        if (peak < yzRowProfile.Length)
                            yzChart.Series["Bright Peaks"].Points.AddXY(yzRowProfile[peak], peak);
                    }
                }

                // Set visibility based on showPeaks
                yzChart.Series["Dark Peaks"].Enabled = showPeaks;
                yzChart.Series["Bright Peaks"].Enabled = showPeaks;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error processing YZ chart: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void DrawScaleBar(Graphics g, Rectangle clientRect, float zoom)
        {
            double pixelSize = mainForm.GetPixelSize();

            const float baseScreenLength = 100f;
            double candidateLengthMeters = baseScreenLength / zoom * pixelSize;
            double labelInMeters;
            string labelText;

            if (candidateLengthMeters < 1e-3)
            {
                double candidateMicrometers = candidateLengthMeters * 1e6;
                double roundedMicrometers = Math.Max(10, Math.Round(candidateMicrometers / 10.0) * 10);
                labelInMeters = roundedMicrometers / 1e6;
                labelText = $"{roundedMicrometers:0} µm";
            }
            else
            {
                double candidateMillimeters = candidateLengthMeters * 1e3;
                double roundedMillimeters = Math.Max(1, Math.Round(candidateMillimeters / 10.0) * 10);
                labelInMeters = roundedMillimeters / 1e3;
                labelText = $"{roundedMillimeters:0} mm";
            }

            float screenLength = (float)(labelInMeters / pixelSize * zoom);

            using (SolidBrush brush = new SolidBrush(Color.White))
            using (Font font = new Font("Arial", 10))
            {
                float x = 20;
                float y = clientRect.Height - 40;
                g.FillRectangle(brush, x, y, screenLength, 3);
                g.DrawString(labelText, font, brush, x, y + 5);
            }
        }

        #endregion

        #region Export Methods
        private async Task ExportResults()
        {
            // Show save file dialog on UI thread
            SaveFileDialog saveDialog = new SaveFileDialog
            {
                Filter = "Excel Files|*.xlsx|CSV Files|*.csv|All Files|*.*",
                Title = "Export Results",
                FileName = "band_detection_results.xlsx"
            };

            bool dialogResult = false;
            string fileName = string.Empty;

            // Show the dialog on UI thread
            this.Invoke(new Action(() => {
                dialogResult = saveDialog.ShowDialog() == DialogResult.OK;
                if (dialogResult)
                    fileName = saveDialog.FileName;
            }));

            if (!dialogResult)
                return; // User cancelled

            try
            {
                // Create export folder for images
                string folderPath = Path.GetDirectoryName(fileName);
                string baseFileName = Path.GetFileNameWithoutExtension(fileName);
                string extension = Path.GetExtension(fileName);

                // Update progress
                UpdateProgressBar(20);

                // Export data
                if (extension.ToLower() == ".xlsx")
                {
                    await ExportToExcel(fileName);
                }
                else
                {
                    await ExportToCSV(fileName);
                }

                // Update progress
                UpdateProgressBar(50);

                // Export composite images
                await ExportCompositeImages(folderPath, baseFileName);

                // Update progress
                UpdateProgressBar(100);
            }
            catch (Exception ex)
            {
                Logger.Log($"[BandDetectionForm] Error in ExportResults: {ex.Message}");
                throw; // Re-throw to be caught by the caller
            }
        }
        private async Task ExportToExcel(string fileName)
        {
            await Task.Run(() =>
            {
                // Create Excel XML content
                StringBuilder excelXml = new StringBuilder();

                // XML header and workbook start
                excelXml.AppendLine("<?xml version=\"1.0\"?>");
                excelXml.AppendLine("<?mso-application progid=\"Excel.Sheet\"?>");
                excelXml.AppendLine("<Workbook xmlns=\"urn:schemas-microsoft-com:office:spreadsheet\"");
                excelXml.AppendLine(" xmlns:o=\"urn:schemas-microsoft-com:office:office\"");
                excelXml.AppendLine(" xmlns:x=\"urn:schemas-microsoft-com:office:excel\"");
                excelXml.AppendLine(" xmlns:ss=\"urn:schemas-microsoft-com:office:spreadsheet\"");
                excelXml.AppendLine(" xmlns:html=\"http://www.w3.org/TR/REC-html40\">");

                // Styles
                excelXml.AppendLine("<Styles>");
                excelXml.AppendLine(" <Style ss:ID=\"Default\" ss:Name=\"Normal\">");
                excelXml.AppendLine("  <Alignment ss:Vertical=\"Bottom\"/>");
                excelXml.AppendLine("  <Borders/><Font/><Interior/><NumberFormat/><Protection/>");
                excelXml.AppendLine(" </Style>");
                excelXml.AppendLine(" <Style ss:ID=\"s21\">");
                excelXml.AppendLine("  <Font x:Family=\"Swiss\" ss:Bold=\"1\"/>");
                excelXml.AppendLine(" </Style>");
                excelXml.AppendLine(" <Style ss:ID=\"s22\">");
                excelXml.AppendLine("  <Interior ss:Color=\"#DDEBF7\" ss:Pattern=\"Solid\"/>");
                excelXml.AppendLine("  <Font x:Family=\"Swiss\" ss:Bold=\"1\"/>");
                excelXml.AppendLine(" </Style>");
                excelXml.AppendLine(" <Style ss:ID=\"s23\">");
                excelXml.AppendLine("  <NumberFormat ss:Format=\"0.000\"/>");
                excelXml.AppendLine(" </Style>");
                excelXml.AppendLine("</Styles>");

                // XY Worksheet
                AddExcelWorksheet(excelXml, "XY_Data", xyRowProfile, xyDarkPeaks, xyBrightPeaks);

                // XZ Worksheet
                AddExcelWorksheet(excelXml, "XZ_Data", xzRowProfile, xzDarkPeaks, xzBrightPeaks);

                // YZ Worksheet
                AddExcelWorksheet(excelXml, "YZ_Data", yzRowProfile, yzDarkPeaks, yzBrightPeaks);

                // Peaks Analysis Worksheet
                AddPeakAnalysisWorksheet(excelXml);

                // Close workbook
                excelXml.AppendLine("</Workbook>");

                // Write to file
                File.WriteAllText(fileName, excelXml.ToString());
            });
        }

        private void AddExcelWorksheet(StringBuilder xml, string name, double[] profile, int[] darkPeaks, int[] brightPeaks)
        {
            if (profile == null)
                return;

            xml.AppendLine($"<Worksheet ss:Name=\"{name}\">");
            xml.AppendLine("<Table>");

            // Column widths
            xml.AppendLine("<Column ss:Width=\"60\"/>");
            xml.AppendLine("<Column ss:Width=\"80\"/>");
            xml.AppendLine("<Column ss:Width=\"80\"/>");
            xml.AppendLine("<Column ss:Width=\"80\"/>");

            // Headers
            xml.AppendLine("<Row ss:StyleID=\"s22\">");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Row</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Value</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Dark Peak</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Bright Peak</Data></Cell>");
            xml.AppendLine("</Row>");

            // Data rows
            for (int i = 0; i < profile.Length; i++)
            {
                bool isDarkPeak = darkPeaks != null && darkPeaks.Contains(i);
                bool isBrightPeak = brightPeaks != null && brightPeaks.Contains(i);

                xml.AppendLine("<Row>");
                xml.AppendLine($"<Cell><Data ss:Type=\"Number\">{i}</Data></Cell>");
                xml.AppendLine($"<Cell><Data ss:Type=\"Number\">{profile[i].ToString("F6")}</Data></Cell>");
                xml.AppendLine($"<Cell><Data ss:Type=\"Number\">{(isDarkPeak ? 1 : 0)}</Data></Cell>");
                xml.AppendLine($"<Cell><Data ss:Type=\"Number\">{(isBrightPeak ? 1 : 0)}</Data></Cell>");
                xml.AppendLine("</Row>");
            }

            xml.AppendLine("</Table>");
            xml.AppendLine("<WorksheetOptions xmlns=\"urn:schemas-microsoft-com:office:excel\">");
            xml.AppendLine("<FreezePanes/>");
            xml.AppendLine("<FrozenNoSplit/>");
            xml.AppendLine("<SplitHorizontal>1</SplitHorizontal>");
            xml.AppendLine("<TopRowBottomPane>1</TopRowBottomPane>");
            xml.AppendLine("</WorksheetOptions>");
            xml.AppendLine("</Worksheet>");
        }

        private void AddPeakAnalysisWorksheet(StringBuilder xml)
        {
            double pixelSize = mainForm.GetPixelSize() * 1000; // mm

            xml.AppendLine("<Worksheet ss:Name=\"Peak_Analysis\">");
            xml.AppendLine("<Table>");

            // Column widths
            xml.AppendLine("<Column ss:Width=\"80\"/>");
            xml.AppendLine("<Column ss:Width=\"80\"/>");
            xml.AppendLine("<Column ss:Width=\"100\"/>");
            xml.AppendLine("<Column ss:Width=\"100\"/>");
            xml.AppendLine("<Column ss:Width=\"100\"/>");

            // XY DARK PEAKS SECTION
            xml.AppendLine("<Row ss:StyleID=\"s22\">");
            xml.AppendLine("<Cell ss:MergeAcross=\"4\"><Data ss:Type=\"String\">XY Dark Peaks</Data></Cell>");
            xml.AppendLine("</Row>");

            xml.AppendLine("<Row ss:StyleID=\"s21\">");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Type</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Position</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Value</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Distance (px)</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Distance (mm)</Data></Cell>");
            xml.AppendLine("</Row>");

            if (xyDarkPeaks != null && xyRowProfile != null)
            {
                for (int i = 0; i < xyDarkPeaks.Length; i++)
                {
                    int peak = xyDarkPeaks[i];
                    double distance = (i < xyDarkPeaks.Length - 1) ? xyDarkPeaks[i + 1] - peak : 0;
                    double distanceInMm = distance * pixelSize;

                    xml.AppendLine("<Row>");
                    xml.AppendLine($"<Cell><Data ss:Type=\"String\">Dark</Data></Cell>");
                    xml.AppendLine($"<Cell><Data ss:Type=\"Number\">{peak}</Data></Cell>");
                    xml.AppendLine($"<Cell><Data ss:Type=\"Number\">{xyRowProfile[peak].ToString("F6")}</Data></Cell>");
                    xml.AppendLine($"<Cell><Data ss:Type=\"Number\">{distance}</Data></Cell>");
                    xml.AppendLine($"<Cell ss:StyleID=\"s23\"><Data ss:Type=\"Number\">{distanceInMm}</Data></Cell>");
                    xml.AppendLine("</Row>");
                }
            }

            // Add empty row
            xml.AppendLine("<Row><Cell><Data ss:Type=\"String\"></Data></Cell></Row>");

            // XY BRIGHT PEAKS SECTION
            xml.AppendLine("<Row ss:StyleID=\"s22\">");
            xml.AppendLine("<Cell ss:MergeAcross=\"4\"><Data ss:Type=\"String\">XY Bright Peaks</Data></Cell>");
            xml.AppendLine("</Row>");

            xml.AppendLine("<Row ss:StyleID=\"s21\">");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Type</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Position</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Value</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Distance (px)</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Distance (mm)</Data></Cell>");
            xml.AppendLine("</Row>");

            if (xyBrightPeaks != null && xyRowProfile != null)
            {
                for (int i = 0; i < xyBrightPeaks.Length; i++)
                {
                    int peak = xyBrightPeaks[i];
                    double distance = (i < xyBrightPeaks.Length - 1) ? xyBrightPeaks[i + 1] - peak : 0;
                    double distanceInMm = distance * pixelSize;

                    xml.AppendLine("<Row>");
                    xml.AppendLine($"<Cell><Data ss:Type=\"String\">Bright</Data></Cell>");
                    xml.AppendLine($"<Cell><Data ss:Type=\"Number\">{peak}</Data></Cell>");
                    xml.AppendLine($"<Cell><Data ss:Type=\"Number\">{xyRowProfile[peak].ToString("F6")}</Data></Cell>");
                    xml.AppendLine($"<Cell><Data ss:Type=\"Number\">{distance}</Data></Cell>");
                    xml.AppendLine($"<Cell ss:StyleID=\"s23\"><Data ss:Type=\"Number\">{distanceInMm}</Data></Cell>");
                    xml.AppendLine("</Row>");
                }
            }

            // Add empty row
            xml.AppendLine("<Row><Cell><Data ss:Type=\"String\"></Data></Cell></Row>");

            // XZ DARK PEAKS SECTION
            xml.AppendLine("<Row ss:StyleID=\"s22\">");
            xml.AppendLine("<Cell ss:MergeAcross=\"4\"><Data ss:Type=\"String\">XZ Dark Peaks</Data></Cell>");
            xml.AppendLine("</Row>");

            xml.AppendLine("<Row ss:StyleID=\"s21\">");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Type</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Position</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Value</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Distance (px)</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Distance (mm)</Data></Cell>");
            xml.AppendLine("</Row>");

            if (xzDarkPeaks != null && xzRowProfile != null)
            {
                for (int i = 0; i < xzDarkPeaks.Length; i++)
                {
                    int peak = xzDarkPeaks[i];
                    double distance = (i < xzDarkPeaks.Length - 1) ? xzDarkPeaks[i + 1] - peak : 0;
                    double distanceInMm = distance * pixelSize;

                    xml.AppendLine("<Row>");
                    xml.AppendLine($"<Cell><Data ss:Type=\"String\">Dark</Data></Cell>");
                    xml.AppendLine($"<Cell><Data ss:Type=\"Number\">{peak}</Data></Cell>");
                    xml.AppendLine($"<Cell><Data ss:Type=\"Number\">{xzRowProfile[peak].ToString("F6")}</Data></Cell>");
                    xml.AppendLine($"<Cell><Data ss:Type=\"Number\">{distance}</Data></Cell>");
                    xml.AppendLine($"<Cell ss:StyleID=\"s23\"><Data ss:Type=\"Number\">{distanceInMm}</Data></Cell>");
                    xml.AppendLine("</Row>");
                }
            }

            // Add empty row
            xml.AppendLine("<Row><Cell><Data ss:Type=\"String\"></Data></Cell></Row>");

            // XZ BRIGHT PEAKS SECTION
            xml.AppendLine("<Row ss:StyleID=\"s22\">");
            xml.AppendLine("<Cell ss:MergeAcross=\"4\"><Data ss:Type=\"String\">XZ Bright Peaks</Data></Cell>");
            xml.AppendLine("</Row>");

            xml.AppendLine("<Row ss:StyleID=\"s21\">");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Type</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Position</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Value</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Distance (px)</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Distance (mm)</Data></Cell>");
            xml.AppendLine("</Row>");

            if (xzBrightPeaks != null && xzRowProfile != null)
            {
                for (int i = 0; i < xzBrightPeaks.Length; i++)
                {
                    int peak = xzBrightPeaks[i];
                    double distance = (i < xzBrightPeaks.Length - 1) ? xzBrightPeaks[i + 1] - peak : 0;
                    double distanceInMm = distance * pixelSize;

                    xml.AppendLine("<Row>");
                    xml.AppendLine($"<Cell><Data ss:Type=\"String\">Bright</Data></Cell>");
                    xml.AppendLine($"<Cell><Data ss:Type=\"Number\">{peak}</Data></Cell>");
                    xml.AppendLine($"<Cell><Data ss:Type=\"Number\">{xzRowProfile[peak].ToString("F6")}</Data></Cell>");
                    xml.AppendLine($"<Cell><Data ss:Type=\"Number\">{distance}</Data></Cell>");
                    xml.AppendLine($"<Cell ss:StyleID=\"s23\"><Data ss:Type=\"Number\">{distanceInMm}</Data></Cell>");
                    xml.AppendLine("</Row>");
                }
            }

            // Add empty row
            xml.AppendLine("<Row><Cell><Data ss:Type=\"String\"></Data></Cell></Row>");

            // YZ DARK PEAKS SECTION
            xml.AppendLine("<Row ss:StyleID=\"s22\">");
            xml.AppendLine("<Cell ss:MergeAcross=\"4\"><Data ss:Type=\"String\">YZ Dark Peaks</Data></Cell>");
            xml.AppendLine("</Row>");

            xml.AppendLine("<Row ss:StyleID=\"s21\">");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Type</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Position</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Value</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Distance (px)</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Distance (mm)</Data></Cell>");
            xml.AppendLine("</Row>");

            if (yzDarkPeaks != null && yzRowProfile != null)
            {
                for (int i = 0; i < yzDarkPeaks.Length; i++)
                {
                    int peak = yzDarkPeaks[i];
                    double distance = (i < yzDarkPeaks.Length - 1) ? yzDarkPeaks[i + 1] - peak : 0;
                    double distanceInMm = distance * pixelSize;

                    xml.AppendLine("<Row>");
                    xml.AppendLine($"<Cell><Data ss:Type=\"String\">Dark</Data></Cell>");
                    xml.AppendLine($"<Cell><Data ss:Type=\"Number\">{peak}</Data></Cell>");
                    xml.AppendLine($"<Cell><Data ss:Type=\"Number\">{yzRowProfile[peak].ToString("F6")}</Data></Cell>");
                    xml.AppendLine($"<Cell><Data ss:Type=\"Number\">{distance}</Data></Cell>");
                    xml.AppendLine($"<Cell ss:StyleID=\"s23\"><Data ss:Type=\"Number\">{distanceInMm}</Data></Cell>");
                    xml.AppendLine("</Row>");
                }
            }

            // Add empty row
            xml.AppendLine("<Row><Cell><Data ss:Type=\"String\"></Data></Cell></Row>");

            // YZ BRIGHT PEAKS SECTION
            xml.AppendLine("<Row ss:StyleID=\"s22\">");
            xml.AppendLine("<Cell ss:MergeAcross=\"4\"><Data ss:Type=\"String\">YZ Bright Peaks</Data></Cell>");
            xml.AppendLine("</Row>");

            xml.AppendLine("<Row ss:StyleID=\"s21\">");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Type</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Position</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Value</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Distance (px)</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Distance (mm)</Data></Cell>");
            xml.AppendLine("</Row>");

            if (yzBrightPeaks != null && yzRowProfile != null)
            {
                for (int i = 0; i < yzBrightPeaks.Length; i++)
                {
                    int peak = yzBrightPeaks[i];
                    double distance = (i < yzBrightPeaks.Length - 1) ? yzBrightPeaks[i + 1] - peak : 0;
                    double distanceInMm = distance * pixelSize;

                    xml.AppendLine("<Row>");
                    xml.AppendLine($"<Cell><Data ss:Type=\"String\">Bright</Data></Cell>");
                    xml.AppendLine($"<Cell><Data ss:Type=\"Number\">{peak}</Data></Cell>");
                    xml.AppendLine($"<Cell><Data ss:Type=\"Number\">{yzRowProfile[peak].ToString("F6")}</Data></Cell>");
                    xml.AppendLine($"<Cell><Data ss:Type=\"Number\">{distance}</Data></Cell>");
                    xml.AppendLine($"<Cell ss:StyleID=\"s23\"><Data ss:Type=\"Number\">{distanceInMm}</Data></Cell>");
                    xml.AppendLine("</Row>");
                }
            }

            // Add summary statistics section
            xml.AppendLine("<Row><Cell><Data ss:Type=\"String\"></Data></Cell></Row>");
            xml.AppendLine("<Row ss:StyleID=\"s22\">");
            xml.AppendLine("<Cell ss:MergeAcross=\"4\"><Data ss:Type=\"String\">Summary Statistics</Data></Cell>");
            xml.AppendLine("</Row>");

            // Add mean distances
            xml.AppendLine("<Row ss:StyleID=\"s21\">");
            xml.AppendLine("<Cell ss:MergeAcross=\"1\"><Data ss:Type=\"String\">View</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Mean Dark Peak Distance (mm)</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Mean Bright Peak Distance (mm)</Data></Cell>");
            xml.AppendLine("<Cell><Data ss:Type=\"String\">Number of Peaks</Data></Cell>");
            xml.AppendLine("</Row>");

            // XY stats
            double xyMeanDarkDistance = 0;
            double xyMeanBrightDistance = 0;
            int xyDarkCount = 0, xyBrightCount = 0;

            if (xyDarkPeaks != null && xyDarkPeaks.Length > 1)
            {
                double sum = 0;
                int count = 0;
                for (int i = 0; i < xyDarkPeaks.Length - 1; i++)
                {
                    double distance = (xyDarkPeaks[i + 1] - xyDarkPeaks[i]) * pixelSize;
                    sum += distance;
                    count++;
                }
                xyMeanDarkDistance = count > 0 ? sum / count : 0;
                xyDarkCount = xyDarkPeaks.Length;
            }

            if (xyBrightPeaks != null && xyBrightPeaks.Length > 1)
            {
                double sum = 0;
                int count = 0;
                for (int i = 0; i < xyBrightPeaks.Length - 1; i++)
                {
                    double distance = (xyBrightPeaks[i + 1] - xyBrightPeaks[i]) * pixelSize;
                    sum += distance;
                    count++;
                }
                xyMeanBrightDistance = count > 0 ? sum / count : 0;
                xyBrightCount = xyBrightPeaks.Length;
            }

            xml.AppendLine("<Row>");
            xml.AppendLine("<Cell ss:MergeAcross=\"1\"><Data ss:Type=\"String\">XY</Data></Cell>");
            xml.AppendLine($"<Cell ss:StyleID=\"s23\"><Data ss:Type=\"Number\">{xyMeanDarkDistance}</Data></Cell>");
            xml.AppendLine($"<Cell ss:StyleID=\"s23\"><Data ss:Type=\"Number\">{xyMeanBrightDistance}</Data></Cell>");
            xml.AppendLine($"<Cell><Data ss:Type=\"String\">Dark: {xyDarkCount}, Bright: {xyBrightCount}</Data></Cell>");
            xml.AppendLine("</Row>");

            // XZ stats
            double xzMeanDarkDistance = 0;
            double xzMeanBrightDistance = 0;
            int xzDarkCount = 0, xzBrightCount = 0;

            if (xzDarkPeaks != null && xzDarkPeaks.Length > 1)
            {
                double sum = 0;
                int count = 0;
                for (int i = 0; i < xzDarkPeaks.Length - 1; i++)
                {
                    double distance = (xzDarkPeaks[i + 1] - xzDarkPeaks[i]) * pixelSize;
                    sum += distance;
                    count++;
                }
                xzMeanDarkDistance = count > 0 ? sum / count : 0;
                xzDarkCount = xzDarkPeaks.Length;
            }

            if (xzBrightPeaks != null && xzBrightPeaks.Length > 1)
            {
                double sum = 0;
                int count = 0;
                for (int i = 0; i < xzBrightPeaks.Length - 1; i++)
                {
                    double distance = (xzBrightPeaks[i + 1] - xzBrightPeaks[i]) * pixelSize;
                    sum += distance;
                    count++;
                }
                xzMeanBrightDistance = count > 0 ? sum / count : 0;
                xzBrightCount = xzBrightPeaks.Length;
            }

            xml.AppendLine("<Row>");
            xml.AppendLine("<Cell ss:MergeAcross=\"1\"><Data ss:Type=\"String\">XZ</Data></Cell>");
            xml.AppendLine($"<Cell ss:StyleID=\"s23\"><Data ss:Type=\"Number\">{xzMeanDarkDistance}</Data></Cell>");
            xml.AppendLine($"<Cell ss:StyleID=\"s23\"><Data ss:Type=\"Number\">{xzMeanBrightDistance}</Data></Cell>");
            xml.AppendLine($"<Cell><Data ss:Type=\"String\">Dark: {xzDarkCount}, Bright: {xzBrightCount}</Data></Cell>");
            xml.AppendLine("</Row>");

            // YZ stats
            double yzMeanDarkDistance = 0;
            double yzMeanBrightDistance = 0;
            int yzDarkCount = 0, yzBrightCount = 0;

            if (yzDarkPeaks != null && yzDarkPeaks.Length > 1)
            {
                double sum = 0;
                int count = 0;
                for (int i = 0; i < yzDarkPeaks.Length - 1; i++)
                {
                    double distance = (yzDarkPeaks[i + 1] - yzDarkPeaks[i]) * pixelSize;
                    sum += distance;
                    count++;
                }
                yzMeanDarkDistance = count > 0 ? sum / count : 0;
                yzDarkCount = yzDarkPeaks.Length;
            }

            if (yzBrightPeaks != null && yzBrightPeaks.Length > 1)
            {
                double sum = 0;
                int count = 0;
                for (int i = 0; i < yzBrightPeaks.Length - 1; i++)
                {
                    double distance = (yzBrightPeaks[i + 1] - yzBrightPeaks[i]) * pixelSize;
                    sum += distance;
                    count++;
                }
                yzMeanBrightDistance = count > 0 ? sum / count : 0;
                yzBrightCount = yzBrightPeaks.Length;
            }

            xml.AppendLine("<Row>");
            xml.AppendLine("<Cell ss:MergeAcross=\"1\"><Data ss:Type=\"String\">YZ</Data></Cell>");
            xml.AppendLine($"<Cell ss:StyleID=\"s23\"><Data ss:Type=\"Number\">{yzMeanDarkDistance}</Data></Cell>");
            xml.AppendLine($"<Cell ss:StyleID=\"s23\"><Data ss:Type=\"Number\">{yzMeanBrightDistance}</Data></Cell>");
            xml.AppendLine($"<Cell><Data ss:Type=\"String\">Dark: {yzDarkCount}, Bright: {yzBrightCount}</Data></Cell>");
            xml.AppendLine("</Row>");

            xml.AppendLine("</Table>");
            xml.AppendLine("<WorksheetOptions xmlns=\"urn:schemas-microsoft-com:office:excel\">");
            xml.AppendLine("<FreezePanes/>");
            xml.AppendLine("<FrozenNoSplit/>");
            xml.AppendLine("<SplitHorizontal>2</SplitHorizontal>");
            xml.AppendLine("<TopRowBottomPane>2</TopRowBottomPane>");
            xml.AppendLine("</WorksheetOptions>");
            xml.AppendLine("</Worksheet>");
        }


        private async Task ExportToCSV(string fileName)
        {
            await Task.Run(() =>
            {
                // Prepare data for XY view
                List<string> lines = new List<string>();

                // Header
                lines.Add("Slice Type,Row/Col,Value,Dark Peak,Bright Peak");

                // XY data
                if (xyRowProfile != null)
                {
                    for (int i = 0; i < xyRowProfile.Length; i++)
                    {
                        bool isDarkPeak = xyDarkPeaks != null && xyDarkPeaks.Contains(i);
                        bool isBrightPeak = xyBrightPeaks != null && xyBrightPeaks.Contains(i);

                        lines.Add($"XY,{i},{xyRowProfile[i]},{(isDarkPeak ? "1" : "0")},{(isBrightPeak ? "1" : "0")}");
                    }
                }

                // XZ data
                if (xzRowProfile != null)
                {
                    for (int i = 0; i < xzRowProfile.Length; i++)
                    {
                        bool isDarkPeak = xzDarkPeaks != null && xzDarkPeaks.Contains(i);
                        bool isBrightPeak = xzBrightPeaks != null && xzBrightPeaks.Contains(i);

                        lines.Add($"XZ,{i},{xzRowProfile[i]},{(isDarkPeak ? "1" : "0")},{(isBrightPeak ? "1" : "0")}");
                    }
                }

                // YZ data
                if (yzRowProfile != null)
                {
                    for (int i = 0; i < yzRowProfile.Length; i++)
                    {
                        bool isDarkPeak = yzDarkPeaks != null && yzDarkPeaks.Contains(i);
                        bool isBrightPeak = yzBrightPeaks != null && yzBrightPeaks.Contains(i);

                        lines.Add($"YZ,{i},{yzRowProfile[i]},{(isDarkPeak ? "1" : "0")},{(isBrightPeak ? "1" : "0")}");
                    }
                }

                // Write to file
                File.WriteAllLines(fileName, lines);

                // Create separate peak files
                string folder = Path.GetDirectoryName(fileName);
                string baseName = Path.GetFileNameWithoutExtension(fileName);

                // XY peaks
                if (xyDarkPeaks != null && xyBrightPeaks != null)
                {
                    List<string> xyPeakLines = new List<string>();
                    xyPeakLines.Add("Type,Position,Value,Distance to Next");

                    // Dark peaks
                    for (int i = 0; i < xyDarkPeaks.Length; i++)
                    {
                        int peak = xyDarkPeaks[i];
                        double distance = (i < xyDarkPeaks.Length - 1) ? xyDarkPeaks[i + 1] - peak : 0;
                        double pixelSize = mainForm.GetPixelSize() * 1000; // mm
                        double distanceInMm = distance * pixelSize;

                        xyPeakLines.Add($"Dark,{peak},{xyRowProfile[peak]},{distanceInMm.ToString("F3")}");
                    }

                    // Bright peaks
                    for (int i = 0; i < xyBrightPeaks.Length; i++)
                    {
                        int peak = xyBrightPeaks[i];
                        double distance = (i < xyBrightPeaks.Length - 1) ? xyBrightPeaks[i + 1] - peak : 0;
                        double pixelSize = mainForm.GetPixelSize() * 1000; // mm
                        double distanceInMm = distance * pixelSize;

                        xyPeakLines.Add($"Bright,{peak},{xyRowProfile[peak]},{distanceInMm.ToString("F3")}");
                    }

                    File.WriteAllLines(Path.Combine(folder, baseName + "_xy_peaks.csv"), xyPeakLines);
                }

                // XZ peaks
                if (xzDarkPeaks != null && xzBrightPeaks != null)
                {
                    List<string> xzPeakLines = new List<string>();
                    xzPeakLines.Add("Type,Position,Value,Distance to Next");

                    // Dark peaks
                    for (int i = 0; i < xzDarkPeaks.Length; i++)
                    {
                        int peak = xzDarkPeaks[i];
                        double distance = (i < xzDarkPeaks.Length - 1) ? xzDarkPeaks[i + 1] - peak : 0;
                        double pixelSize = mainForm.GetPixelSize() * 1000; // mm
                        double distanceInMm = distance * pixelSize;

                        xzPeakLines.Add($"Dark,{peak},{xzRowProfile[peak]},{distanceInMm.ToString("F3")}");
                    }

                    // Bright peaks
                    for (int i = 0; i < xzBrightPeaks.Length; i++)
                    {
                        int peak = xzBrightPeaks[i];
                        double distance = (i < xzBrightPeaks.Length - 1) ? xzBrightPeaks[i + 1] - peak : 0;
                        double pixelSize = mainForm.GetPixelSize() * 1000; // mm
                        double distanceInMm = distance * pixelSize;

                        xzPeakLines.Add($"Bright,{peak},{xzRowProfile[peak]},{distanceInMm.ToString("F3")}");
                    }

                    File.WriteAllLines(Path.Combine(folder, baseName + "_xz_peaks.csv"), xzPeakLines);
                }

                // YZ peaks
                if (yzDarkPeaks != null && yzBrightPeaks != null)
                {
                    List<string> yzPeakLines = new List<string>();
                    yzPeakLines.Add("Type,Position,Value,Distance to Next");

                    // Dark peaks
                    for (int i = 0; i < yzDarkPeaks.Length; i++)
                    {
                        int peak = yzDarkPeaks[i];
                        double distance = (i < yzDarkPeaks.Length - 1) ? yzDarkPeaks[i + 1] - peak : 0;
                        double pixelSize = mainForm.GetPixelSize() * 1000; // mm
                        double distanceInMm = distance * pixelSize;

                        yzPeakLines.Add($"Dark,{peak},{yzRowProfile[peak]},{distanceInMm.ToString("F3")}");
                    }

                    // Bright peaks
                    for (int i = 0; i < yzBrightPeaks.Length; i++)
                    {
                        int peak = yzBrightPeaks[i];
                        double distance = (i < yzBrightPeaks.Length - 1) ? yzBrightPeaks[i + 1] - peak : 0;
                        double pixelSize = mainForm.GetPixelSize() * 1000; // mm
                        double distanceInMm = distance * pixelSize;

                        yzPeakLines.Add($"Bright,{peak},{yzRowProfile[peak]},{distanceInMm.ToString("F3")}");
                    }

                    File.WriteAllLines(Path.Combine(folder, baseName + "_yz_peaks.csv"), yzPeakLines);
                }
            });
        }

        private async Task ExportCompositeImages(string folderPath, string baseFileName)
        {
            await Task.Run(() =>
            {
                // Create export folder
                string imagesFolderPath = Path.Combine(folderPath, baseFileName + "_images");
                Directory.CreateDirectory(imagesFolderPath);

                // Export XY composite
                if (xyProcessedImage != null && xyRowProfile != null)
                {
                    using (Bitmap composite = CreateCompositeImage(xyProcessedImage, xyRowProfile, xyDarkPeaks, xyBrightPeaks, "XY"))
                    {
                        composite.Save(Path.Combine(imagesFolderPath, baseFileName + "_xy.png"), ImageFormat.Png);
                    }
                }

                // Export XZ composite
                if (xzProcessedImage != null && xzRowProfile != null)
                {
                    using (Bitmap composite = CreateCompositeImage(xzProcessedImage, xzRowProfile, xzDarkPeaks, xzBrightPeaks, "XZ"))
                    {
                        composite.Save(Path.Combine(imagesFolderPath, baseFileName + "_xz.png"), ImageFormat.Png);
                    }
                }

                // Export YZ composite
                if (yzProcessedImage != null && yzRowProfile != null)
                {
                    using (Bitmap composite = CreateCompositeImage(yzProcessedImage, yzRowProfile, yzDarkPeaks, yzBrightPeaks, "YZ"))
                    {
                        composite.Save(Path.Combine(imagesFolderPath, baseFileName + "_yz.png"), ImageFormat.Png);
                    }
                }
            });
        }
        private Bitmap CreateCompositeImage(Bitmap image, double[] profile, int[] darkPeaks, int[] brightPeaks, string viewLabel)
        {
            int imageWidth = image.Width;
            int imageHeight = image.Height;
            int chartWidth = 200;  // Reduced chart width
            int infoWidth = 150;   // Width for text information

            // Create composite bitmap with three sections: image, chart, info text
            Bitmap composite = new Bitmap(imageWidth + chartWidth + infoWidth, imageHeight);

            using (Graphics g = Graphics.FromImage(composite))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                // Draw background
                g.Clear(Color.Black);

                // Draw original image
                g.DrawImage(image, 0, 0, imageWidth, imageHeight);

                // Define chart rectangle - DIRECTLY adjacent to the image
                Rectangle chartRect = new Rectangle(imageWidth, 0, chartWidth, imageHeight);

                // Define info rectangle - right of the chart
                Rectangle infoRect = new Rectangle(imageWidth + chartWidth, 0, infoWidth, imageHeight);

                // Fill chart background
                g.FillRectangle(Brushes.White, chartRect);

                // Fill info area with light gray
                g.FillRectangle(Brushes.WhiteSmoke, infoRect);

                // Compute min/max for scaling
                double min = profile.Min();
                double max = profile.Max();
                double range = max - min;
                if (range < 0.0001) range = 1.0; // Prevent division by zero

                // Draw grid lines
                using (Pen gridPen = new Pen(Color.LightGray, 1))
                {
                    gridPen.DashStyle = DashStyle.Dash;

                    // Horizontal grid lines - exactly matching image rows
                    int numGridLines = 5;
                    for (int i = 0; i <= numGridLines; i++)
                    {
                        int y = (i * imageHeight) / numGridLines;
                        g.DrawLine(gridPen, chartRect.Left, y, chartRect.Right, y);
                    }

                    // Vertical grid lines
                    int numVerticalLines = 4;
                    for (int i = 0; i <= numVerticalLines; i++)
                    {
                        int x = chartRect.Left + (i * chartRect.Width) / numVerticalLines;
                        g.DrawLine(gridPen, x, 0, x, imageHeight);
                    }
                }

                // Draw profile line - PERFECTLY ALIGNED with image rows
                if (profile.Length > 1)
                {
                    using (Pen profilePen = new Pen(Color.Blue, 2))
                    {
                        Point[] points = new Point[profile.Length];

                        for (int i = 0; i < profile.Length; i++)
                        {
                            // Calculate Y position - DIRECT MAPPING to image pixels
                            int y = (int)((double)i / profile.Length * imageHeight);

                            // Calculate X position based on profile value
                            float xRatio = 0;
                            if (range > 0)
                                xRatio = (float)((profile[i] - min) / range);
                            int x = chartRect.Left + (int)(xRatio * chartRect.Width);

                            points[i] = new Point(x, y);
                        }

                        g.DrawLines(profilePen, points);
                    }
                }

                // Draw dark peaks - exactly aligned with image rows
                if (darkPeaks != null)
                {
                    using (Pen darkPeakPen = new Pen(Color.Red, 2))
                    {
                        foreach (int peak in darkPeaks)
                        {
                            if (peak < profile.Length)
                            {
                                // Direct mapping to image pixel position
                                int y = (int)((double)peak / profile.Length * imageHeight);

                                // X position based on profile value
                                float xRatio = 0;
                                if (range > 0)
                                    xRatio = (float)((profile[peak] - min) / range);
                                int x = chartRect.Left + (int)(xRatio * chartRect.Width);

                                // Draw peak marker (X shape)
                                g.DrawLine(darkPeakPen, x - 5, y - 5, x + 5, y + 5);
                                g.DrawLine(darkPeakPen, x - 5, y + 5, x + 5, y - 5);

                                // Draw horizontal line across image
                                g.DrawLine(darkPeakPen, 0, y, imageWidth, y);
                            }
                        }
                    }
                }

                // Draw bright peaks - exactly aligned with image rows
                if (brightPeaks != null)
                {
                    using (Pen brightPeakPen = new Pen(Color.Green, 2))
                    {
                        foreach (int peak in brightPeaks)
                        {
                            if (peak < profile.Length)
                            {
                                // Direct mapping to image pixel position
                                int y = (int)((double)peak / profile.Length * imageHeight);

                                // X position based on profile value
                                float xRatio = 0;
                                if (range > 0)
                                    xRatio = (float)((profile[peak] - min) / range);
                                int x = chartRect.Left + (int)(xRatio * chartRect.Width);

                                // Draw peak marker (triangle)
                                Point[] triangle = new Point[]
                                {
                            new Point(x, y - 5),
                            new Point(x - 5, y + 5),
                            new Point(x + 5, y + 5)
                                };
                                g.DrawPolygon(brightPeakPen, triangle);

                                // Draw horizontal line across image
                                g.DrawLine(brightPeakPen, 0, y, imageWidth, y);
                            }
                        }
                    }
                }

                // Draw title in the info area
                using (Font titleFont = new Font("Arial", 12, FontStyle.Bold))
                {
                    string title = viewLabel + " Row Profile";
                    g.DrawString(title, titleFont, Brushes.Black, infoRect.Left + 5, infoRect.Top + 10);
                }

                // Draw parameters in the info area
                using (Font paramFont = new Font("Arial", 9))
                {
                    int paramY = infoRect.Top + 40;
                    g.DrawString("Parameters:", paramFont, Brushes.Black, infoRect.Left + 5, paramY);
                    paramY += 15;
                    g.DrawString($"Disk radius: {diskRadius}px", paramFont, Brushes.Black, infoRect.Left + 5, paramY);
                    paramY += 15;
                    g.DrawString($"Gaussian sigma: {gaussianSigma:F1}", paramFont, Brushes.Black, infoRect.Left + 5, paramY);
                    paramY += 15;
                    g.DrawString($"Peak distance: {peakDistance}px", paramFont, Brushes.Black, infoRect.Left + 5, paramY);
                    paramY += 15;
                    g.DrawString($"Prominence: {peakProminence:F3}", paramFont, Brushes.Black, infoRect.Left + 5, paramY);
                }

                // Draw legend in the info area
                using (Font legendFont = new Font("Arial", 9))
                {
                    int legendY = infoRect.Top + 130;

                    g.DrawLine(new Pen(Color.Blue, 2), infoRect.Left + 10, legendY, infoRect.Left + 30, legendY);
                    g.DrawString("Profile", legendFont, Brushes.Black, infoRect.Left + 35, legendY - 7);

                    legendY += 20;
                    // X marker for dark peaks
                    g.DrawLine(new Pen(Color.Red, 2), infoRect.Left + 10, legendY - 5, infoRect.Left + 30, legendY + 5);
                    g.DrawLine(new Pen(Color.Red, 2), infoRect.Left + 10, legendY + 5, infoRect.Left + 30, legendY - 5);
                    g.DrawString("Dark Bands", legendFont, Brushes.Black, infoRect.Left + 35, legendY - 7);

                    legendY += 20;
                    Point[] triangle = new Point[]
                    {
                new Point(infoRect.Left + 20, legendY - 5),
                new Point(infoRect.Left + 10, legendY + 5),
                new Point(infoRect.Left + 30, legendY + 5)
                    };
                    g.DrawPolygon(new Pen(Color.Green, 2), triangle);
                    g.DrawString("Bright Bands", legendFont, Brushes.Black, infoRect.Left + 35, legendY - 7);
                }

                // Draw scale bar
                double pixelSize = mainForm.GetPixelSize(); // in meters
                if (pixelSize > 0)
                {
                    // Choose appropriate scale bar length
                    int scaleBarPixels = (int)(imageWidth * 0.2); // 20% of image width
                    double realWorldLength = scaleBarPixels * pixelSize; // in meters

                    string scaleLabel;
                    if (realWorldLength >= 0.001) // 1mm or larger
                    {
                        // Use millimeters
                        double lengthInMm = realWorldLength * 1000;
                        // Round to a nice value, but ensure we never show "0 mm"
                        double niceMm = Math.Max(0.1, Math.Round(lengthInMm * 10) / 10);
                        scaleLabel = $"{niceMm:G4} mm";
                    }
                    else
                    {
                        // Use micrometers
                        double lengthInUm = realWorldLength * 1000000;
                        // Round to a nice value, but ensure we never show "0 µm"
                        double niceUm = Math.Max(1, Math.Round(lengthInUm));
                        scaleLabel = $"{niceUm:G4} µm";
                    }

                    // Draw the scale bar
                    int scaleBarY = imageHeight - 20; // Position near bottom
                    int scaleBarX = 20;
                    int scaleBarHeight = 5;

                    using (SolidBrush whiteBrush = new SolidBrush(Color.White))
                    {
                        g.FillRectangle(whiteBrush, scaleBarX, scaleBarY, scaleBarPixels, scaleBarHeight);

                        // Draw label with background to ensure visibility
                        SizeF labelSize = g.MeasureString(scaleLabel, new Font("Arial", 9));

                        // Draw semi-transparent background behind text
                        using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(180, Color.Black)))
                        {
                            g.FillRectangle(bgBrush, scaleBarX, scaleBarY + scaleBarHeight + 2,
                                labelSize.Width, labelSize.Height);
                        }

                        g.DrawString(scaleLabel, new Font("Arial", 9), whiteBrush,
                            scaleBarX, scaleBarY + scaleBarHeight + 2);
                    }
                }
            }
            return composite;
        }

        private void CreateCompositeButton_Click(object sender, EventArgs e)
        {
            try
            {
                // Show save dialog
                SaveFileDialog saveDialog = new SaveFileDialog
                {
                    Filter = "PNG Files|*.png|All Files|*.*",
                    Title = "Save Composite Image",
                    FileName = "composite_image.png"
                };

                if (saveDialog.ShowDialog() == DialogResult.OK)
                {
                    string folderPath = Path.GetDirectoryName(saveDialog.FileName);
                    string baseFileName = Path.GetFileNameWithoutExtension(saveDialog.FileName);

                    // Determine which view is active/selected (can be improved with radio buttons)
                    if (xyProcessedImage != null && xyRowProfile != null)
                    {
                        using (Bitmap composite = CreateCompositeImage(xyProcessedImage, xyRowProfile, xyDarkPeaks, xyBrightPeaks, "XY"))
                        {
                            composite.Save(Path.Combine(folderPath, baseFileName + "_xy.png"), ImageFormat.Png);
                        }
                    }

                    if (xzProcessedImage != null && xzRowProfile != null)
                    {
                        using (Bitmap composite = CreateCompositeImage(xzProcessedImage, xzRowProfile, xzDarkPeaks, xzBrightPeaks, "XZ"))
                        {
                            composite.Save(Path.Combine(folderPath, baseFileName + "_xz.png"), ImageFormat.Png);
                        }
                    }

                    if (yzProcessedImage != null && yzRowProfile != null)
                    {
                        using (Bitmap composite = CreateCompositeImage(yzProcessedImage, yzRowProfile, yzDarkPeaks, yzBrightPeaks, "YZ"))
                        {
                            composite.Save(Path.Combine(folderPath, baseFileName + "_yz.png"), ImageFormat.Png);
                        }
                    }

                    MessageBox.Show("Composite images created successfully!", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error creating composite image: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
    
    #endregion
}