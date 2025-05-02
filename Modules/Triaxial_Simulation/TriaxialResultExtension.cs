using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Documents;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;
using System.Windows.Media.Imaging;

namespace CTSegmenter
{
    /// <summary>
    /// Extension class to handle the enhanced Results tab functionality
    /// </summary>
    public class TriaxialResultsExtension : IDisposable
    {
        // Parent form reference
        private TriaxialSimulationForm _parentForm;

        // UI Controls
        private TabControl _resultsTabControl;
        private TabPage _stressStrainTab;
        private TabPage _mohrCoulombTab;
        private TabPage _failurePointTab;
        private TabPage _faultingPlaneTab;
        private CheckBox _chkRealtimeUpdates;
        private Button _btnExportComposite;

        // Charts and visualizations
        private Chart _stressStrainChart;
        private Chart _mohrCoulombChart;
        private PictureBox _failurePointViewer;
        private PictureBox _faultingPlaneViewer;
        private ComboBox _cmbColorMapMode;
        private CheckBox _chkShowVolume;

        // Data
        private List<PointF> _stressStrainData = new List<PointF>();
        private double _peakStress = 0;
        private double _peakStrain = 0;
        private int _failureStep = -1;
        private bool _failureDetected = false;

        // Visualization state
        private bool _realtimeUpdatesEnabled = true;
        private ColorMapMode _selectedColorMapMode = ColorMapMode.Stress;
        private bool _showVolume = true;

        // Rendering cache
        private Bitmap _failurePointCache;
        private Bitmap _faultingPlaneCache;

        private float _zoom = 1.0f;
        private float _rotationY = 30;
        private float _rotationX = 30;
        private PointF _pan = new PointF(0, 0);

        /// <summary>
        /// Color map modes for 3D visualizations
        /// </summary>
        public enum ColorMapMode
        {
            Stress,
            Strain,
            Damage
        }

        /// <summary>
        /// Constructor
        /// </summary>
        public TriaxialResultsExtension(TriaxialSimulationForm parentForm)
        {
            _parentForm = parentForm;
        }

        /// <summary>
        /// Initialize the enhanced Results tab
        /// </summary>
        public void Initialize(TabPage resultsTabPage)
        {
            // Create nested tab control within the Results tab
            _resultsTabControl = new TabControl();
            _resultsTabControl.Dock = DockStyle.Fill;
            _resultsTabControl.DrawMode = TabDrawMode.OwnerDrawFixed;
            _resultsTabControl.DrawItem += TabControl_DrawItem;

            // Create tabs
            _stressStrainTab = new TabPage("Stress-Strain");
            _mohrCoulombTab = new TabPage("Mohr-Coulomb");
            _failurePointTab = new TabPage("Failure Point");
            _faultingPlaneTab = new TabPage("Faulting Plane");

            // Add tabs to control
            _resultsTabControl.TabPages.Add(_stressStrainTab);
            _resultsTabControl.TabPages.Add(_mohrCoulombTab);
            _resultsTabControl.TabPages.Add(_failurePointTab);
            _resultsTabControl.TabPages.Add(_faultingPlaneTab);

            // Initialize each tab
            InitializeStressStrainTab();
            InitializeMohrCoulombTab();
            InitializeFailurePointTab();
            InitializeFaultingPlaneTab();

            // Add controls panel at the bottom
            Panel controlPanel = new Panel();
            controlPanel.Dock = DockStyle.Bottom;
            controlPanel.Height = 40;
            controlPanel.BackColor = Color.FromArgb(45, 45, 48);

            // Add real-time updates checkbox
            _chkRealtimeUpdates = new CheckBox();
            _chkRealtimeUpdates.Text = "Real-time Updates";
            _chkRealtimeUpdates.Checked = _realtimeUpdatesEnabled;
            _chkRealtimeUpdates.ForeColor = Color.White;
            _chkRealtimeUpdates.Location = new Point(10, 10);
            _chkRealtimeUpdates.Width = 140;
            _chkRealtimeUpdates.CheckedChanged += (s, e) => _realtimeUpdatesEnabled = _chkRealtimeUpdates.Checked;
            controlPanel.Controls.Add(_chkRealtimeUpdates);

            // Add export button
            _btnExportComposite = new Button();
            _btnExportComposite.Text = "Export Composite Image";
            _btnExportComposite.Location = new Point(160, 8);
            _btnExportComposite.Width = 180;
            _btnExportComposite.ForeColor = Color.White;
            _btnExportComposite.Click += BtnExportComposite_Click;
            controlPanel.Controls.Add(_btnExportComposite);

            // Add to Results tab
            resultsTabPage.Controls.Clear();
            resultsTabPage.Controls.Add(_resultsTabControl);
            resultsTabPage.Controls.Add(controlPanel);
        }

        /// <summary>
        /// Initialize the Stress-Strain tab
        /// </summary>
        private void InitializeStressStrainTab()
        {
            // Create chart for stress-strain curve
            _stressStrainChart = new Chart();
            _stressStrainChart.Dock = DockStyle.Fill;
            _stressStrainChart.BackColor = Color.FromArgb(40, 40, 40);
            _stressStrainChart.ForeColor = Color.White;
            _stressStrainChart.AntiAliasing = AntiAliasingStyles.All;
            _stressStrainChart.TextAntiAliasingQuality = TextAntiAliasingQuality.High;

            ChartArea ca = new ChartArea("CA");
            ca.BackColor = Color.FromArgb(50, 50, 50);
            ca.AxisX.LabelStyle.ForeColor = Color.White;
            ca.AxisY.LabelStyle.ForeColor = Color.White;
            ca.AxisX.LineColor = Color.White;
            ca.AxisY.LineColor = Color.White;
            ca.AxisX.MajorGrid.LineColor = Color.FromArgb(70, 70, 70);
            ca.AxisY.MajorGrid.LineColor = Color.FromArgb(70, 70, 70);
            ca.AxisX.Title = "Axial Strain";
            ca.AxisY.Title = "Axial Stress (MPa)";
            ca.AxisX.TitleForeColor = Color.White;
            ca.AxisY.TitleForeColor = Color.White;
            _stressStrainChart.ChartAreas.Add(ca);

            // Add regular curve
            Series series = new Series("Curve");
            series.ChartType = SeriesChartType.Line;
            series.Color = Color.LightGreen;
            series.BorderWidth = 2;
            _stressStrainChart.Series.Add(series);

            // Add series for failure point
            Series failureSeries = new Series("Failure");
            failureSeries.ChartType = SeriesChartType.Point;
            failureSeries.Color = Color.Red;
            failureSeries.MarkerSize = 10;
            failureSeries.MarkerStyle = MarkerStyle.Circle;
            _stressStrainChart.Series.Add(failureSeries);

            // Add peak stress point
            Series peakSeries = new Series("Peak");
            peakSeries.ChartType = SeriesChartType.Point;
            peakSeries.Color = Color.Blue;
            peakSeries.MarkerSize = 10;
            peakSeries.MarkerStyle = MarkerStyle.Diamond;
            _stressStrainChart.Series.Add(peakSeries);

            // Add legend
            Legend legend = new Legend("Legend");
            legend.BackColor = Color.FromArgb(50, 50, 50);
            legend.ForeColor = Color.White;
            _stressStrainChart.Legends.Add(legend);

            // Add to tab
            _stressStrainTab.Controls.Add(_stressStrainChart);
        }

        /// <summary>
        /// Initialize the Mohr-Coulomb tab
        /// </summary>
        private void InitializeMohrCoulombTab()
        {
            // Create chart for Mohr-Coulomb visualization
            _mohrCoulombChart = new Chart();
            _mohrCoulombChart.Dock = DockStyle.Fill;
            _mohrCoulombChart.BackColor = Color.FromArgb(40, 40, 40);
            _mohrCoulombChart.ForeColor = Color.White;
            _mohrCoulombChart.AntiAliasing = AntiAliasingStyles.All;
            _mohrCoulombChart.TextAntiAliasingQuality = TextAntiAliasingQuality.High;

            ChartArea ca = new ChartArea("CA");
            ca.BackColor = Color.FromArgb(50, 50, 50);
            ca.AxisX.LabelStyle.ForeColor = Color.White;
            ca.AxisY.LabelStyle.ForeColor = Color.White;
            ca.AxisX.LineColor = Color.White;
            ca.AxisY.LineColor = Color.White;
            ca.AxisX.MajorGrid.LineColor = Color.FromArgb(70, 70, 70);
            ca.AxisY.MajorGrid.LineColor = Color.FromArgb(70, 70, 70);
            ca.AxisX.Title = "Normal Stress (MPa)";
            ca.AxisY.Title = "Shear Stress (MPa)";
            ca.AxisX.TitleForeColor = Color.White;
            ca.AxisY.TitleForeColor = Color.White;
            _mohrCoulombChart.ChartAreas.Add(ca);

            // Add Mohr circles series
            Series circlesSeries = new Series("Circles");
            circlesSeries.ChartType = SeriesChartType.Line;
            circlesSeries.Color = Color.LightBlue;
            circlesSeries.BorderWidth = 2;
            _mohrCoulombChart.Series.Add(circlesSeries);

            // Add failure envelope series
            Series envelopeSeries = new Series("Envelope");
            envelopeSeries.ChartType = SeriesChartType.Line;
            envelopeSeries.Color = Color.Red;
            envelopeSeries.BorderWidth = 2;
            _mohrCoulombChart.Series.Add(envelopeSeries);

            // Add stress state points 
            Series pointsSeries = new Series("Points");
            pointsSeries.ChartType = SeriesChartType.Point;
            pointsSeries.MarkerStyle = MarkerStyle.Circle;
            pointsSeries.Color = Color.Yellow;
            pointsSeries.MarkerSize = 8;
            _mohrCoulombChart.Series.Add(pointsSeries);

            // Add legend
            Legend legend = new Legend("Legend");
            legend.BackColor = Color.FromArgb(50, 50, 50);
            legend.ForeColor = Color.White;
            _mohrCoulombChart.Legends.Add(legend);

            // Add to tab
            _mohrCoulombTab.Controls.Add(_mohrCoulombChart);
        }

        /// <summary>
        /// Initialize the Failure Point 3D View tab
        /// </summary>
        private void InitializeFailurePointTab()
        {
            // Create picture box for custom visualization
            _failurePointViewer = new PictureBox();
            _failurePointViewer.Dock = DockStyle.Fill;
            _failurePointViewer.BackColor = Color.FromArgb(30, 30, 30);
            _failurePointViewer.SizeMode = PictureBoxSizeMode.Zoom;
            _failurePointViewer.Paint += FailurePointViewer_Paint;

            // Create panel for controls
            Panel controlPanel = new Panel();
            controlPanel.Dock = DockStyle.Top;
            controlPanel.Height = 40;
            controlPanel.BackColor = Color.FromArgb(45, 45, 48);

            // Create color map mode selector
            Label lblColorMap = new Label();
            lblColorMap.Text = "Color Map:";
            lblColorMap.ForeColor = Color.White;
            lblColorMap.Location = new Point(10, 10);
            lblColorMap.Width = 80;
            controlPanel.Controls.Add(lblColorMap);

            _cmbColorMapMode = new ComboBox();
            _cmbColorMapMode.Location = new Point(90, 8);
            _cmbColorMapMode.Width = 150;
            _cmbColorMapMode.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbColorMapMode.Items.AddRange(new object[] { "Stress", "Strain", "Damage" });
            _cmbColorMapMode.SelectedIndex = 0;
            _cmbColorMapMode.SelectedIndexChanged += (s, e) =>
            {
                _selectedColorMapMode = (ColorMapMode)_cmbColorMapMode.SelectedIndex;
                _failurePointViewer.Invalidate();
            };
            controlPanel.Controls.Add(_cmbColorMapMode);

            // Add to tab
            _failurePointTab.Controls.Add(_failurePointViewer);
            _failurePointTab.Controls.Add(controlPanel);
        }

        /// <summary>
        /// Initialize the Faulting Plane tab
        /// </summary>
        private void InitializeFaultingPlaneTab()
        {
            // Create picture box for custom visualization
            _faultingPlaneViewer = new PictureBox();
            _faultingPlaneViewer.Dock = DockStyle.Fill;
            _faultingPlaneViewer.BackColor = Color.FromArgb(30, 30, 30);
            _faultingPlaneViewer.SizeMode = PictureBoxSizeMode.Zoom;
            _faultingPlaneViewer.Paint += FaultingPlaneViewer_Paint;

            // Create panel for controls
            Panel controlPanel = new Panel();
            controlPanel.Dock = DockStyle.Top;
            controlPanel.Height = 40;
            controlPanel.BackColor = Color.FromArgb(45, 45, 48);

            // Create show volume checkbox
            _chkShowVolume = new CheckBox();
            _chkShowVolume.Text = "Show Volume";
            _chkShowVolume.Checked = _showVolume;
            _chkShowVolume.ForeColor = Color.White;
            _chkShowVolume.Location = new Point(10, 10);
            _chkShowVolume.Width = 120;
            _chkShowVolume.CheckedChanged += (s, e) =>
            {
                _showVolume = _chkShowVolume.Checked;
                _faultingPlaneViewer.Invalidate();
            };
            controlPanel.Controls.Add(_chkShowVolume);

            // Add to tab
            _faultingPlaneTab.Controls.Add(_faultingPlaneViewer);
            _faultingPlaneTab.Controls.Add(controlPanel);
        }

        /// <summary>
        /// Update visualization with simulation data
        /// </summary>
        public void UpdateData(List<PointF> stressStrainData, double peakStress, double peakStrain,
                              bool failureDetected, int failureStep, double[,,] damageData)
        {
            _stressStrainData = stressStrainData;
            _peakStress = peakStress;
            _peakStrain = peakStrain;
            _failureDetected = failureDetected;
            _failureStep = failureStep;

            // Store or update damage data if provided
            if (damageData != null)
            {
                // If there's no external damage data available, create a placeholder for visualization testing
                if (_parentForm != null)
                {
                    // Find the failure point based on damage data
                    var failurePoint = _parentForm.FindMaxDamagePoint();

                    // Update visualization with failure point
                    if (_failurePointCache != null)
                    {
                        _failurePointCache.Dispose();
                        _failurePointCache = null;
                    }
                }
            }

            if (_realtimeUpdatesEnabled)
            {
                UpdateStressStrainChart();
                UpdateMohrCoulombChart();

                // For 3D visualizations, just invalidate and let the paint handlers regenerate the view
                // This avoids expensive rendering operations during simulation
                _failurePointViewer.Invalidate();
                _faultingPlaneViewer.Invalidate();
            }
        }

        /// <summary>
        /// Update the stress-strain chart with current data
        /// </summary>
        private void UpdateStressStrainChart()
        {
            if (_stressStrainChart == null || _stressStrainData == null || _stressStrainData.Count == 0)
                return;

            // Clear existing data
            _stressStrainChart.Series["Curve"].Points.Clear();
            _stressStrainChart.Series["Failure"].Points.Clear();
            _stressStrainChart.Series["Peak"].Points.Clear();

            // Add points to the curve
            foreach (var point in _stressStrainData)
            {
                _stressStrainChart.Series["Curve"].Points.AddXY(point.X, point.Y);
            }

            // Add failure point if detected
            if (_failureDetected && _failureStep >= 0 && _failureStep < _stressStrainData.Count)
            {
                var failurePoint = _stressStrainData[_failureStep];
                _stressStrainChart.Series["Failure"].Points.AddXY(failurePoint.X, failurePoint.Y);
                _stressStrainChart.Series["Failure"].Points[0].Label = "Failure";
            }

            // Add peak stress point
            if (_stressStrainData.Count > 0)
            {
                _stressStrainChart.Series["Peak"].Points.AddXY(_peakStrain, _peakStress);
                _stressStrainChart.Series["Peak"].Points[0].LabelForeColor = Color.White;
                _stressStrainChart.Series["Peak"].Points[0].Label = $"Peak: {_peakStress:F1} MPa";
            }
        }

        /// <summary>
        /// Update the Mohr-Coulomb chart with current data
        /// </summary>
        private void UpdateMohrCoulombChart()
        {
            if (_mohrCoulombChart == null)
                return;

            // Get simulation parameters from parent form
            double confiningPressure = 0;
            double frictionAngle = 0;
            double cohesion = 0;

            // Try to get the parameters through reflection to avoid modifying the parent class
            try
            {
                var confField = _parentForm.GetType().GetField("nudConfiningP",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var frictionField = _parentForm.GetType().GetField("nudFriction",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var cohesionField = _parentForm.GetType().GetField("nudCohesion",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                if (confField != null && frictionField != null && cohesionField != null)
                {
                    var confControl = confField.GetValue(_parentForm) as NumericUpDown;
                    var frictionControl = frictionField.GetValue(_parentForm) as NumericUpDown;
                    var cohesionControl = cohesionField.GetValue(_parentForm) as NumericUpDown;

                    if (confControl != null && frictionControl != null && cohesionControl != null)
                    {
                        confiningPressure = (double)confControl.Value;
                        frictionAngle = (double)frictionControl.Value;
                        cohesion = (double)cohesionControl.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                // Fallback values if reflection fails
                Logger.Log($"[TriaxialResultsExtension] Error getting parameters: {ex.Message}");
                confiningPressure = 10;
                frictionAngle = 30;
                cohesion = 5;
            }

            // Clear existing series
            _mohrCoulombChart.Series["Circles"].Points.Clear();
            _mohrCoulombChart.Series["Envelope"].Points.Clear();
            _mohrCoulombChart.Series["Points"].Points.Clear();

            // Get current axial stress from stress-strain data
            double axialStress = _stressStrainData.Count > 0 ? _stressStrainData.Last().Y : confiningPressure;

            // Draw Mohr circles for current stress state
            DrawMohrCircle(confiningPressure, axialStress);

            // Draw failure envelope (Mohr-Coulomb criterion)
            DrawFailureEnvelope(frictionAngle, cohesion);
        }
        private ILabelVolumeData GetVolumeLabels()
        {
            if (_parentForm != null)
            {
                try
                {
                    // Use reflection to access mainForm.volumeLabels
                    var mainFormField = _parentForm.GetType().GetField("mainForm",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (mainFormField != null)
                    {
                        var mainForm = mainFormField.GetValue(_parentForm);
                        if (mainForm != null)
                        {
                            var volumeLabelsField = mainForm.GetType().GetProperty("volumeLabels");
                            if (volumeLabelsField != null)
                            {
                                return volumeLabelsField.GetValue(mainForm) as ILabelVolumeData;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Log error but continue
                    Logger.Log($"[TriaxialResultsExtension] Error accessing volumeLabels: {ex.Message}");
                }
            }

            return null;
        }
        private byte GetSelectedMaterialID()
        {
            if (_parentForm != null)
            {
                try
                {
                    var materialIDField = _parentForm.GetType().GetField("selectedMaterialID",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (materialIDField != null)
                    {
                        return (byte)materialIDField.GetValue(_parentForm);
                    }
                }
                catch (Exception ex)
                {
                    // Log error but continue
                    Logger.Log($"[TriaxialResultsExtension] Error accessing selectedMaterialID: {ex.Message}");
                }
            }

            return 1; // Default to material ID 1 if not found
        }
        /// <summary>
        /// Draw Mohr circle for current stress state
        /// </summary>
        private void DrawMohrCircle(double confiningPressure, double axialStress)
        {
            // Calculate circle center and radius
            double center = (confiningPressure + axialStress) / 2;
            double radius = Math.Abs(axialStress - confiningPressure) / 2;

            // Draw the Mohr circle
            for (double angle = 0; angle <= Math.PI; angle += Math.PI / 180)
            {
                double x = center + radius * Math.Cos(angle);
                double y = radius * Math.Sin(angle);
                _mohrCoulombChart.Series["Circles"].Points.AddXY(x, y);
            }

            // Mark the principal stress points
            _mohrCoulombChart.Series["Points"].Points.AddXY(confiningPressure, 0);
            _mohrCoulombChart.Series["Points"].Points.AddXY(axialStress, 0);

            // Set chart area to fit the circle
            ChartArea ca = _mohrCoulombChart.ChartAreas[0];
            double margin = radius * 0.2;

            ca.AxisX.Minimum = Math.Min(0, confiningPressure - margin);
            ca.AxisX.Maximum = axialStress + radius + margin;
            ca.AxisY.Minimum = -radius - margin;
            ca.AxisY.Maximum = radius + margin;
        }

        /// <summary>
        /// Draw Mohr-Coulomb failure envelope
        /// </summary>
        private void DrawFailureEnvelope(double frictionAngle, double cohesion)
        {
            // Convert friction angle to radians
            double phi = frictionAngle * Math.PI / 180.0;

            // Calculate envelope parameters
            double alpha = Math.Tan(phi);

            // Get axis limits
            ChartArea ca = _mohrCoulombChart.ChartAreas[0];
            double minX = ca.AxisX.Minimum;
            double maxX = ca.AxisX.Maximum;

            // Draw failure envelope line
            _mohrCoulombChart.Series["Envelope"].Points.AddXY(minX, cohesion + minX * alpha);
            _mohrCoulombChart.Series["Envelope"].Points.AddXY(maxX, cohesion + maxX * alpha);
        }

        /// <summary>
        /// Paint handler for failure point visualization
        /// </summary>
        private void FailurePointViewer_Paint(object sender, PaintEventArgs e)
        {
            if (!_realtimeUpdatesEnabled && _failurePointCache != null)
            {
                e.Graphics.DrawImage(_failurePointCache, 0, 0, _failurePointViewer.Width, _failurePointViewer.Height);
                return;
            }

            // Create a new visualization or use the cached one
            if (_failurePointCache == null || _realtimeUpdatesEnabled)
            {
                // Create a new visualization
                int width = _failurePointViewer.Width;
                int height = _failurePointViewer.Height;

                if (width <= 0 || height <= 0)
                    return;

                // Get volume dimensions
                int volumeWidth = 0, volumeHeight = 0, volumeDepth = 0;

                // Try to get volume dimensions through reflection
                try
                {
                    var mainFormField = _parentForm.GetType().GetField("mainForm",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (mainFormField != null)
                    {
                        var mainForm = mainFormField.GetValue(_parentForm);
                        if (mainForm != null)
                        {
                            var getWidthMethod = mainForm.GetType().GetMethod("GetWidth");
                            var getHeightMethod = mainForm.GetType().GetMethod("GetHeight");
                            var getDepthMethod = mainForm.GetType().GetMethod("GetDepth");

                            if (getWidthMethod != null && getHeightMethod != null && getDepthMethod != null)
                            {
                                volumeWidth = (int)getWidthMethod.Invoke(mainForm, null);
                                volumeHeight = (int)getHeightMethod.Invoke(mainForm, null);
                                volumeDepth = (int)getDepthMethod.Invoke(mainForm, null);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Use fallback values if reflection fails
                    Logger.Log($"[TriaxialResultsExtension] Error getting volume dimensions: {ex.Message}");
                    volumeWidth = 100;
                    volumeHeight = 100;
                    volumeDepth = 100;
                }

                // Create new bitmap for visualization
                Bitmap bmp = new Bitmap(width, height);

                // Try to create a visualization with the actual data
                try
                {
                    // Get the damage data from parent form
                    double[,,] damageData = null;
                    var damageProperty = _parentForm.GetType().GetProperty("DamageData");
                    if (damageProperty != null)
                    {
                        damageData = damageProperty.GetValue(_parentForm) as double[,,];
                    }

                    // Get volume labels
                    ILabelVolumeData volumeLabels = GetVolumeLabels();
                    byte materialId = GetSelectedMaterialID();

                    if (damageData != null && volumeLabels != null &&
                        volumeWidth > 0 && volumeHeight > 0 && volumeDepth > 0)
                    {
                        // Create a FailurePointVisualizer
                        var visualizer = new FailurePointVisualizer(volumeWidth, volumeHeight, volumeDepth, materialId);
                        visualizer.SetData(volumeLabels, damageData);
                        visualizer.SetViewParameters(_rotationX, _rotationY, _zoom, _pan);

                        // Set failure point
                        var maxDamagePoint = _parentForm.FindMaxDamagePoint();
                        // Create a new instance of FailurePointVisualizer.Point3D using the values from the returned Point3D
                        FailurePointVisualizer.Point3D failurePoint = new FailurePointVisualizer.Point3D(
                            (int)maxDamagePoint.X,
                            (int)maxDamagePoint.Y,
                            (int)maxDamagePoint.Z
                        );
                        visualizer.SetFailurePoint(_failureDetected, failurePoint);

                        // Create visualization
                        var visualizerColorMode = (FailurePointVisualizer.ColorMapMode)((int)_selectedColorMapMode);
                        bmp = visualizer.CreateVisualization(width, height, visualizerColorMode);
                    }
                    else
                    {
                        // Fall back to the simplified visualization
                        using (Graphics g = Graphics.FromImage(bmp))
                        {
                            g.Clear(Color.FromArgb(20, 20, 20));

                            // Draw orthogonal views (3-view drawing)
                            int viewWidth = width / 2;
                            int viewHeight = height / 2;

                            // Top-left: Top view (XY)
                            DrawOrthogonalView(g, new Rectangle(0, 0, viewWidth, viewHeight), "Top View (XY)");

                            // Top-right: Front view (XZ)
                            DrawOrthogonalView(g, new Rectangle(viewWidth, 0, viewWidth, viewHeight), "Front View (XZ)");

                            // Bottom-left: Side view (YZ)
                            DrawOrthogonalView(g, new Rectangle(0, viewHeight, viewWidth, viewHeight), "Side View (YZ)");

                            // Bottom-right: 3D view
                            Draw3DView(g, new Rectangle(viewWidth, viewHeight, viewWidth, viewHeight), "3D View");

                            // Draw color legend
                            DrawColorLegend(g, width - 150, 20, 130, 200);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // On error, create a message bitmap
                    Logger.Log($"[TriaxialResultsExtension] Error creating visualization: {ex.Message}");
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.FromArgb(20, 20, 20));
                        using (Font font = new Font("Segoe UI", 10))
                        using (SolidBrush brush = new SolidBrush(Color.White))
                        using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        {
                            g.DrawString("Error creating visualization. Please run simulation first.",
                                         font, brush, width / 2, height / 2, sf);
                        }
                    }
                }

                // Store the visualization
                _failurePointCache = bmp;
            }

            // Draw the visualization
            e.Graphics.DrawImage(_failurePointCache, 0, 0, _failurePointViewer.Width, _failurePointViewer.Height);
        }

        /// <summary>
        /// Paint handler for faulting plane visualization
        /// </summary>
        private void FaultingPlaneViewer_Paint(object sender, PaintEventArgs e)
        {
            if (!_realtimeUpdatesEnabled && _faultingPlaneCache != null)
            {
                e.Graphics.DrawImage(_faultingPlaneCache, 0, 0, _faultingPlaneViewer.Width, _faultingPlaneViewer.Height);
                return;
            }

            // Create a new visualization or use the cached one
            if (_faultingPlaneCache == null || _realtimeUpdatesEnabled)
            {
                // Create a new visualization
                int width = _faultingPlaneViewer.Width;
                int height = _faultingPlaneViewer.Height;

                if (width <= 0 || height <= 0)
                    return;

                // Create bitmap for visualization
                Bitmap bmp = new Bitmap(width, height);

                // Try to create a visualization with the actual data
                try
                {
                    // Get the damage data from parent form
                    double[,,] damageData = null;
                    var damageProperty = _parentForm.GetType().GetProperty("DamageData");
                    if (damageProperty != null)
                    {
                        damageData = damageProperty.GetValue(_parentForm) as double[,,];
                    }

                    // Get volume dimensions
                    int volumeWidth = 0, volumeHeight = 0, volumeDepth = 0;

                    // Try to get volume dimensions through reflection
                    try
                    {
                        var mainFormField = _parentForm.GetType().GetField("mainForm",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (mainFormField != null)
                        {
                            var mainForm = mainFormField.GetValue(_parentForm);
                            if (mainForm != null)
                            {
                                var getWidthMethod = mainForm.GetType().GetMethod("GetWidth");
                                var getHeightMethod = mainForm.GetType().GetMethod("GetHeight");
                                var getDepthMethod = mainForm.GetType().GetMethod("GetDepth");

                                if (getWidthMethod != null && getHeightMethod != null && getDepthMethod != null)
                                {
                                    volumeWidth = (int)getWidthMethod.Invoke(mainForm, null);
                                    volumeHeight = (int)getHeightMethod.Invoke(mainForm, null);
                                    volumeDepth = (int)getDepthMethod.Invoke(mainForm, null);
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Use fallback values if reflection fails
                        volumeWidth = 100;
                        volumeHeight = 100;
                        volumeDepth = 100;
                    }

                    // Get volume labels
                    ILabelVolumeData volumeLabels = GetVolumeLabels();
                    byte materialId = GetSelectedMaterialID();

                    if (damageData != null && volumeLabels != null &&
                        volumeWidth > 0 && volumeHeight > 0 && volumeDepth > 0)
                    {
                        // Create a FaultingPlaneVisualizer
                        var visualizer = new FaultingPlaneVisualizer(volumeWidth, volumeHeight, volumeDepth, materialId);
                        visualizer.SetData(volumeLabels, damageData);
                        visualizer.SetViewParameters(_rotationX, _rotationY, _zoom, _pan);
                        visualizer.SetShowVolume(_showVolume);

                        // Create visualization
                        bmp = visualizer.CreateVisualization(width, height);
                    }
                    else
                    {
                        // Fall back to the simplified visualization
                        using (Graphics g = Graphics.FromImage(bmp))
                        {
                            g.Clear(Color.FromArgb(20, 20, 20));

                            // Draw placeholder message
                            using (Font font = new Font("Segoe UI", 12))
                            using (SolidBrush brush = new SolidBrush(Color.White))
                            using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                            {
                                string message = _showVolume ?
                                    "3D Volume with Cracks Visualization\n(Run simulation first)" :
                                    "3D Cracks Only Visualization\n(Run simulation first)";
                                g.DrawString(message, font, brush, width / 2, height / 2, sf);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // On error, create a message bitmap
                    Logger.Log($"[TriaxialResultsExtension] Error creating faulting plane visualization: {ex.Message}");
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.FromArgb(20, 20, 20));
                        using (Font font = new Font("Segoe UI", 10))
                        using (SolidBrush brush = new SolidBrush(Color.White))
                        using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        {
                            g.DrawString("Error creating visualization. Please run simulation first.",
                                         font, brush, width / 2, height / 2, sf);
                        }
                    }
                }

                // Store the visualization
                _faultingPlaneCache = bmp;
            }

            // Draw the visualization
            e.Graphics.DrawImage(_faultingPlaneCache, 0, 0, _faultingPlaneViewer.Width, _faultingPlaneViewer.Height);
        }

        /// <summary>
        /// Draw an orthogonal view for the failure point visualization
        /// </summary>
        private void DrawOrthogonalView(Graphics g, Rectangle rect, string title)
        {
            // Draw background
            using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(40, 40, 40)))
            {
                g.FillRectangle(bgBrush, rect);
            }

            // Draw border
            using (Pen borderPen = new Pen(Color.FromArgb(100, 100, 100)))
            {
                g.DrawRectangle(borderPen, rect);
            }

            // Draw title
            using (Font titleFont = new Font("Segoe UI", 10, FontStyle.Bold))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            using (StringFormat sf = new StringFormat() { Alignment = StringAlignment.Center })
            {
                g.DrawString(title, titleFont, textBrush, new Rectangle(rect.X, rect.Y + 5, rect.Width, 20), sf);
            }

            // Get simulation data
            int volumeWidth = 0, volumeHeight = 0, volumeDepth = 0;

            // Try to get volume dimensions through reflection
            try
            {
                var mainFormField = _parentForm.GetType().GetField("mainForm", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (mainFormField != null)
                {
                    var mainForm = mainFormField.GetValue(_parentForm);
                    if (mainForm != null)
                    {
                        var getWidthMethod = mainForm.GetType().GetMethod("GetWidth");
                        var getHeightMethod = mainForm.GetType().GetMethod("GetHeight");
                        var getDepthMethod = mainForm.GetType().GetMethod("GetDepth");

                        if (getWidthMethod != null && getHeightMethod != null && getDepthMethod != null)
                        {
                            volumeWidth = (int)getWidthMethod.Invoke(mainForm, null);
                            volumeHeight = (int)getHeightMethod.Invoke(mainForm, null);
                            volumeDepth = (int)getDepthMethod.Invoke(mainForm, null);
                        }
                    }
                }
            }
            catch
            {
                // Use fallback values if reflection fails
                volumeWidth = 100;
                volumeHeight = 100;
                volumeDepth = 100;
            }

            // Draw dummy content for now (would be replaced with actual visualization)
            using (Font font = new Font("Segoe UI", 9))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            {
                string info;

                switch (_selectedColorMapMode)
                {
                    case ColorMapMode.Strain:
                        info = "Strain Visualization";
                        break;
                    case ColorMapMode.Damage:
                        info = "Damage Visualization";
                        break;
                    case ColorMapMode.Stress:
                    default:
                        info = "Stress Visualization";
                        break;
                }

                string content = $"Volume: {volumeWidth}x{volumeHeight}x{volumeDepth}\n{info}";

                g.DrawString(content, font, textBrush, rect.X + 10, rect.Y + 30);

                // Draw a placeholder visualization
                int margin = 50;
                Rectangle visRect = new Rectangle(rect.X + margin, rect.Y + margin,
                                                 rect.Width - 2 * margin, rect.Height - 2 * margin);

                using (SolidBrush fillBrush = new SolidBrush(Color.FromArgb(100, 70, 130, 180)))
                {
                    g.FillEllipse(fillBrush, visRect);
                }

                // Mark failure point if detected
                if (_failureDetected)
                {
                    using (SolidBrush pointBrush = new SolidBrush(Color.Red))
                    {
                        int x = visRect.X + visRect.Width / 2;
                        int y = visRect.Y + visRect.Height / 2;
                        g.FillEllipse(pointBrush, x - 5, y - 5, 10, 10);
                    }

                    // Add failure label
                    using (Font labelFont = new Font("Segoe UI", 8, FontStyle.Bold))
                    {
                        int x = visRect.X + visRect.Width / 2 + 10;
                        int y = visRect.Y + visRect.Height / 2 - 5;
                        g.DrawString("Failure Point", labelFont, textBrush, x, y);
                    }
                }
            }
        }

        /// <summary>
        /// Draw a 3D view for the failure point visualization
        /// </summary>
        private void Draw3DView(Graphics g, Rectangle rect, string title)
        {
            // Draw background
            using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(40, 40, 40)))
            {
                g.FillRectangle(bgBrush, rect);
            }

            // Draw border
            using (Pen borderPen = new Pen(Color.FromArgb(100, 100, 100)))
            {
                g.DrawRectangle(borderPen, rect);
            }

            // Draw title
            using (Font titleFont = new Font("Segoe UI", 10, FontStyle.Bold))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            using (StringFormat sf = new StringFormat() { Alignment = StringAlignment.Center })
            {
                g.DrawString(title, titleFont, textBrush, new Rectangle(rect.X, rect.Y + 5, rect.Width, 20), sf);
            }

            // Draw a placeholder 3D cube
            int margin = 50;
            Rectangle cubeRect = new Rectangle(rect.X + margin, rect.Y + margin,
                                             rect.Width - 2 * margin, rect.Height - 2 * margin);

            // 3D cube corners
            Point[] frontFace = {
                new Point(cubeRect.X, cubeRect.Y + cubeRect.Height / 2),
                new Point(cubeRect.X + cubeRect.Width / 2, cubeRect.Y),
                new Point(cubeRect.X + cubeRect.Width, cubeRect.Y + cubeRect.Height / 2),
                new Point(cubeRect.X + cubeRect.Width / 2, cubeRect.Y + cubeRect.Height)
            };

            // Front face
            using (SolidBrush fillBrush = new SolidBrush(Color.FromArgb(150, 70, 130, 180)))
            {
                g.FillPolygon(fillBrush, frontFace);
            }

            // Draw as wireframe
            using (Pen linePen = new Pen(Color.White, 1))
            {
                g.DrawPolygon(linePen, frontFace);
            }

            // Mark failure point if detected
            if (_failureDetected)
            {
                using (SolidBrush pointBrush = new SolidBrush(Color.Red))
                {
                    int x = cubeRect.X + cubeRect.Width / 2;
                    int y = cubeRect.Y + cubeRect.Height / 2;
                    g.FillEllipse(pointBrush, x - 5, y - 5, 10, 10);
                }

                // Add failure label
                using (Font labelFont = new Font("Segoe UI", 8, FontStyle.Bold))
                using (SolidBrush textBrush = new SolidBrush(Color.White))
                {
                    int x = cubeRect.X + cubeRect.Width / 2 + 10;
                    int y = cubeRect.Y + cubeRect.Height / 2 - 5;
                    g.DrawString("Failure Point", labelFont, textBrush, x, y);
                }
            }
        }

        /// <summary>
        /// Draw a color legend for the visualization
        /// </summary>
        private void DrawColorLegend(Graphics g, int x, int y, int width, int height)
        {
            // Draw background
            using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(40, 40, 40)))
            {
                g.FillRectangle(bgBrush, x, y, width, height);
            }

            // Draw border
            using (Pen borderPen = new Pen(Color.FromArgb(100, 100, 100)))
            {
                g.DrawRectangle(borderPen, x, y, width, height);
            }

            // Draw title
            using (Font titleFont = new Font("Segoe UI", 9, FontStyle.Bold))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            using (StringFormat sf = new StringFormat() { Alignment = StringAlignment.Center })
            {
                string title;

                switch (_selectedColorMapMode)
                {
                    case ColorMapMode.Strain:
                        title = "Strain";
                        break;
                    case ColorMapMode.Damage:
                        title = "Damage";
                        break;
                    case ColorMapMode.Stress:
                    default:
                        title = "Stress (MPa)";
                        break;
                }

                g.DrawString(title, titleFont, textBrush, new Rectangle(x, y + 5, width, 20), sf);
            }

            // Draw color gradient
            int gradientHeight = height - 60;
            int gradientWidth = 30;
            int gradientX = x + (width - gradientWidth) / 2;
            int gradientY = y + 30;

            // Create gradient brush
            using (LinearGradientBrush lgb = new LinearGradientBrush(
                new Point(gradientX, gradientY),
                new Point(gradientX, gradientY + gradientHeight),
                Color.Red, Color.Blue))
            {
                // Add intermediate colors
                ColorBlend blend = new ColorBlend(5);
                blend.Colors = new Color[] {
                    Color.Red, Color.Yellow, Color.Green, Color.Cyan, Color.Blue
                };
                blend.Positions = new float[] { 0.0f, 0.25f, 0.5f, 0.75f, 1.0f };
                lgb.InterpolationColors = blend;

                g.FillRectangle(lgb, gradientX, gradientY, gradientWidth, gradientHeight);
            }

            // Draw border around gradient
            using (Pen borderPen = new Pen(Color.White))
            {
                g.DrawRectangle(borderPen, gradientX, gradientY, gradientWidth, gradientHeight);
            }

            // Draw tick marks and labels
            using (Font labelFont = new Font("Segoe UI", 8))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            using (StringFormat sf = new StringFormat() { Alignment = StringAlignment.Far })
            {
                // Generate labels based on selected mode
                string[] labels;

                switch (_selectedColorMapMode)
                {
                    case ColorMapMode.Strain:
                        labels = new string[] { "0.00", "0.25", "0.50", "0.75", "1.00" };
                        break;
                    case ColorMapMode.Damage:
                        labels = new string[] { "1.0", "0.75", "0.5", "0.25", "0.0" };
                        break;
                    case ColorMapMode.Stress:
                    default:
                        double maxStress = _peakStress > 0 ? _peakStress : 100.0;
                        labels = new string[] {
                            maxStress.ToString("F0"),
                            (maxStress * 0.75).ToString("F0"),
                            (maxStress * 0.5).ToString("F0"),
                            (maxStress * 0.25).ToString("F0"),
                            "0"
                        };
                        break;
                }

                // Draw tick marks and labels
                for (int i = 0; i < 5; i++)
                {
                    int tickY = gradientY + (i * gradientHeight / 4);
                    g.DrawLine(Pens.White, gradientX - 3, tickY, gradientX, tickY);
                    g.DrawString(labels[i], labelFont, textBrush,
                                 new Rectangle(x, tickY - 7, gradientX - 5, 15), sf);
                }
            }
        }

        /// <summary>
        /// Draw the volume with faulting planes
        /// </summary>
        private void DrawVolumeWithFaults(Graphics g, Rectangle rect)
        {
            // Draw placeholder for volume with faults
            using (Font font = new Font("Segoe UI", 12))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            using (StringFormat sf = new StringFormat() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.DrawString("3D Volume with Faults Visualization\n(Volume Enabled)",
                            font, textBrush, rect, sf);
            }

            // Draw a placeholder 3D cube with "cracks"
            int margin = 100;
            Rectangle cubeRect = new Rectangle(rect.X + margin, rect.Y + margin,
                                             rect.Width - 2 * margin, rect.Height - 2 * margin);

            // 3D cube corners
            Point[] frontFace = {
                new Point(cubeRect.X, cubeRect.Y + cubeRect.Height / 2),
                new Point(cubeRect.X + cubeRect.Width / 2, cubeRect.Y),
                new Point(cubeRect.X + cubeRect.Width, cubeRect.Y + cubeRect.Height / 2),
                new Point(cubeRect.X + cubeRect.Width / 2, cubeRect.Y + cubeRect.Height)
            };

            // Front face
            using (SolidBrush fillBrush = new SolidBrush(Color.FromArgb(100, 70, 130, 180)))
            {
                g.FillPolygon(fillBrush, frontFace);
            }

            // Draw as wireframe
            using (Pen linePen = new Pen(Color.White, 1))
            {
                g.DrawPolygon(linePen, frontFace);
            }

            // Draw cracks
            using (Pen crackPen = new Pen(Color.Red, 2))
            {
                // Draw some diagonal cracks
                g.DrawLine(crackPen,
                          cubeRect.X + cubeRect.Width / 4, cubeRect.Y + cubeRect.Height / 4,
                          cubeRect.X + 3 * cubeRect.Width / 4, cubeRect.Y + 3 * cubeRect.Height / 4);

                g.DrawLine(crackPen,
                          cubeRect.X + cubeRect.Width / 4, cubeRect.Y + 3 * cubeRect.Height / 4,
                          cubeRect.X + cubeRect.Width / 2, cubeRect.Y + cubeRect.Height / 2);
            }
        }

        /// <summary>
        /// Draw only the faulting planes
        /// </summary>
        private void DrawFaultingPlanes(Graphics g, Rectangle rect)
        {
            // Draw placeholder for faulting planes only
            using (Font font = new Font("Segoe UI", 12))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            using (StringFormat sf = new StringFormat() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.DrawString("3D Faulting Planes Only\n(Volume Disabled)",
                            font, textBrush, rect, sf);
            }

            // Draw placeholder faults
            int margin = 100;
            Rectangle faultRect = new Rectangle(rect.X + margin, rect.Y + margin,
                                              rect.Width - 2 * margin, rect.Height - 2 * margin);

            // Draw some crack planes (simplified as lines)
            using (Pen crackPen = new Pen(Color.Red, 3))
            {
                // Draw a few cracks
                g.DrawLine(crackPen,
                          faultRect.X, faultRect.Y + faultRect.Height / 2,
                          faultRect.X + faultRect.Width, faultRect.Y + faultRect.Height / 2);

                g.DrawLine(crackPen,
                          faultRect.X + faultRect.Width / 4, faultRect.Y,
                          faultRect.X + 3 * faultRect.Width / 4, faultRect.Y + faultRect.Height);

                g.DrawLine(crackPen,
                          faultRect.X + faultRect.Width / 3, faultRect.Y + 2 * faultRect.Height / 3,
                          faultRect.X + 2 * faultRect.Width / 3, faultRect.Y + faultRect.Height / 3);
            }
        }

        /// <summary>
        /// Draw interaction instructions
        /// </summary>
        private void DrawInteractionInstructions(Graphics g, int x, int y, int width, int height)
        {
            // Draw background
            using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(60, 60, 60)))
            {
                g.FillRectangle(bgBrush, x, y, width, height);
            }

            // Draw border
            using (Pen borderPen = new Pen(Color.FromArgb(120, 120, 120)))
            {
                g.DrawRectangle(borderPen, x, y, width, height);
            }

            // Draw instructions
            using (Font font = new Font("Segoe UI", 8))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            {
                g.DrawString("Click + Drag: Rotate | Right-Click + Drag: Pan | Scroll: Zoom",
                            font, textBrush, x + 5, y + 5);
                g.DrawString("The faulting plane view would be interactive in a full implementation",
                            font, textBrush, x + 5, y + 23);
            }
        }

        /// <summary>
        /// Custom tab drawing for dark theme
        /// </summary>
        private void TabControl_DrawItem(object sender, DrawItemEventArgs e)
        {
            TabControl tabControl = (TabControl)sender;
            TabPage tabPage = tabControl.TabPages[e.Index];
            Rectangle tabBounds = tabControl.GetTabRect(e.Index);

            // Create a custom brush based on whether the tab is selected
            Color textColor = e.State == DrawItemState.Selected ? Color.White : Color.LightGray;
            Color backColor = e.State == DrawItemState.Selected ? Color.FromArgb(70, 70, 75) : Color.FromArgb(45, 45, 48);

            using (SolidBrush backBrush = new SolidBrush(backColor))
            using (SolidBrush textBrush = new SolidBrush(textColor))
            {
                // Draw tab background
                e.Graphics.FillRectangle(backBrush, tabBounds);

                // Draw tab text
                StringFormat sf = new StringFormat();
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;

                e.Graphics.DrawString(tabPage.Text, e.Font, textBrush, tabBounds, sf);
            }
        }

        /// <summary>
        /// Export a composite image with all visualizations
        /// </summary>
        private void BtnExportComposite_Click(object sender, EventArgs e)
        {
            try
            {
                // Make sure visualizations are up-to-date
                UpdateStressStrainChart();
                UpdateMohrCoulombChart();

                // Force paint handlers to update cached bitmaps
                _failurePointViewer.Invalidate();
                _failurePointViewer.Update();
                _faultingPlaneViewer.Invalidate();
                _faultingPlaneViewer.Update();

                // Create a composite image
                int width = 1200;
                int height = 1000;

                using (Bitmap compositeBmp = new Bitmap(width, height))
                {
                    using (Graphics g = Graphics.FromImage(compositeBmp))
                    {
                        g.Clear(Color.FromArgb(30, 30, 30));

                        // Add a title
                        using (Font titleFont = new Font("Segoe UI", 16, FontStyle.Bold))
                        using (SolidBrush textBrush = new SolidBrush(Color.White))
                        using (StringFormat sf = new StringFormat() { Alignment = StringAlignment.Center })
                        {
                            g.DrawString("Triaxial Simulation Results", titleFont, textBrush, width / 2, 20);
                        }

                        // Divide the space into 4 quadrants
                        int quadWidth = width / 2 - 10;
                        int quadHeight = (height - 60) / 2 - 10;

                        // Create chart image for stress-strain curve
                        using (Bitmap chartBmp = new Bitmap(quadWidth, quadHeight))
                        {
                            _stressStrainChart.Size = new Size(quadWidth, quadHeight);
                            _stressStrainChart.DrawToBitmap(chartBmp, new Rectangle(0, 0, quadWidth, quadHeight));
                            g.DrawImage(chartBmp, 10, 60);

                            // Add label
                            using (Font labelFont = new Font("Segoe UI", 10, FontStyle.Bold))
                            using (SolidBrush textBrush = new SolidBrush(Color.White))
                            {
                                g.DrawString("Stress-Strain Curve", labelFont, textBrush, 10, 50);
                            }
                        }

                        // Create chart image for Mohr-Coulomb diagram
                        using (Bitmap chartBmp = new Bitmap(quadWidth, quadHeight))
                        {
                            _mohrCoulombChart.Size = new Size(quadWidth, quadHeight);
                            _mohrCoulombChart.DrawToBitmap(chartBmp, new Rectangle(0, 0, quadWidth, quadHeight));
                            g.DrawImage(chartBmp, width / 2, 60);

                            // Add label
                            using (Font labelFont = new Font("Segoe UI", 10, FontStyle.Bold))
                            using (SolidBrush textBrush = new SolidBrush(Color.White))
                            {
                                g.DrawString("Mohr-Coulomb Diagram", labelFont, textBrush, width / 2, 50);
                            }
                        }

                        // Add failure point view
                        if (_failurePointCache != null)
                        {
                            g.DrawImage(_failurePointCache, 10, height / 2 + 10, quadWidth, quadHeight);

                            // Add label
                            using (Font labelFont = new Font("Segoe UI", 10, FontStyle.Bold))
                            using (SolidBrush textBrush = new SolidBrush(Color.White))
                            {
                                g.DrawString("Failure Point Visualization", labelFont, textBrush, 10, height / 2);
                            }
                        }

                        // Add faulting plane view
                        if (_faultingPlaneCache != null)
                        {
                            g.DrawImage(_faultingPlaneCache, width / 2, height / 2 + 10, quadWidth, quadHeight);

                            // Add label
                            using (Font labelFont = new Font("Segoe UI", 10, FontStyle.Bold))
                            using (SolidBrush textBrush = new SolidBrush(Color.White))
                            {
                                g.DrawString("Faulting Plane Visualization", labelFont, textBrush, width / 2, height / 2);
                            }
                        }

                        // Add timestamp
                        using (Font footerFont = new Font("Segoe UI", 8))
                        using (SolidBrush textBrush = new SolidBrush(Color.White))
                        using (StringFormat sf = new StringFormat() { Alignment = StringAlignment.Far })
                        {
                            g.DrawString(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), footerFont, textBrush, width - 10, height - 20);
                        }
                    }

                    // Save the image
                    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    string filePath = $"TriaxialResults_Composite_{timestamp}.png";
                    compositeBmp.Save(filePath, ImageFormat.Png);

                    // Show confirmation
                    MessageBox.Show($"Composite image saved to:\n{filePath}", "Export Complete",
                                   MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error exporting composite image: {ex.Message}",
                               "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Dispose of resources
        /// </summary>
        public void Dispose()
        {
            // Dispose chart resources
            _stressStrainChart?.Dispose();
            _mohrCoulombChart?.Dispose();

            // Dispose bitmap resources
            _failurePointCache?.Dispose();
            _faultingPlaneCache?.Dispose();
        }
    }
}