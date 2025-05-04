using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Windows.Documents;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

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
        private Bitmap _mohrCoulombImage;

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
            ca.AxisX.IntervalAutoMode = IntervalAutoMode.FixedCount;
            ca.AxisX.Interval = (ca.AxisX.Maximum - ca.AxisX.Minimum) / 4;
            //ca.AxisX.LabelStyle.Format = "0.000";
            ca.AxisX.LabelStyle.Format = "0.0%";
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
            _mohrCoulombChart.BackColor = Color.Black;
            _mohrCoulombChart.ForeColor = Color.White;
            _mohrCoulombChart.AntiAliasing = AntiAliasingStyles.All;
            _mohrCoulombChart.TextAntiAliasingQuality = TextAntiAliasingQuality.High;

            ChartArea ca = new ChartArea("CA");
            ca.BackColor = Color.Black;

            // Configure X and Y axes with white labels and lines
            ca.AxisX.LabelStyle.ForeColor = Color.White;
            ca.AxisY.LabelStyle.ForeColor = Color.White;
            ca.AxisX.LineColor = Color.White;
            ca.AxisY.LineColor = Color.White;
            ca.AxisX.MajorGrid.LineColor = Color.FromArgb(70, 70, 70);
            ca.AxisY.MajorGrid.LineColor = Color.FromArgb(70, 70, 70);

            // Position X-axis at bottom and start at 0
            ca.AxisY.Minimum = 0;  // Set Y-axis minimum to 0 to position X-axis at bottom
            ca.AxisX.Minimum = 0;  // Force X-axis to start at 0

            // Fix circle appearance by setting the position and size of the chart area
            ca.Position.Auto = false;
            ca.Position.X = 10;
            ca.Position.Y = 10;
            ca.Position.Width = 80;
            ca.Position.Height = 80;

            // Set inner plot position to be square
            ca.InnerPlotPosition.Auto = false;
            ca.InnerPlotPosition.X = 10;
            ca.InnerPlotPosition.Y = 10;
            ca.InnerPlotPosition.Width = 80;
            ca.InnerPlotPosition.Height = 80;

            // Set pixel/data scaling ratio to be equal for both axes
            ca.AxisX.IsStartedFromZero = true;
            ca.AxisY.IsStartedFromZero = true;

            // Set axis titles
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
            pointsSeries.Color = Color.Red;
            pointsSeries.MarkerSize = 8;
            _mohrCoulombChart.Series.Add(pointsSeries);

            // Add legend with black background
            Legend legend = new Legend("Legend");
            legend.BackColor = Color.Black;
            legend.ForeColor = Color.White;
            
            _mohrCoulombChart.Legends.Add(legend);

            // Add PostPaint event
            _mohrCoulombChart.PostPaint += MohrCoulombChart_PostPaint;

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
        /// Implementation for an alternative, direct drawing approach to ensure the tangent always appears
        /// </summary>
        private void DrawTangentOnMohrChart(double confiningPressure, double failureStress,
                                    double frictionAngle, double cohesion)
        {
            try
            {
                if (_mohrCoulombChart == null) return;

                // Ensure we have a MohrCircle object
                if (_currentMohrCircle == null)
                {
                    _currentMohrCircle = new MohrCircle(failureStress, confiningPressure);
                }

                // Get the tangent point on the circle
                PointF tangentPoint = _currentMohrCircle.GetTangentPoint(frictionAngle);

                // Only proceed if the tangent point has a positive Y value
                if (tangentPoint.Y <= 0) return;

                // Create or retrieve the tangent series
                Series tangentSeries;
                if (_mohrCoulombChart.Series.IndexOf("Tangent") < 0)
                {
                    tangentSeries = new Series("Tangent");
                    tangentSeries.ChartType = SeriesChartType.Line;
                    tangentSeries.Color = Color.Yellow;
                    tangentSeries.BorderWidth = 2;
                    _mohrCoulombChart.Series.Add(tangentSeries);
                }
                else
                {
                    tangentSeries = _mohrCoulombChart.Series["Tangent"];
                    tangentSeries.Points.Clear();
                }

                // Create or retrieve the point series for the tangent point
                Series pointSeries;
                if (_mohrCoulombChart.Series.IndexOf("TangentPoint") < 0)
                {
                    pointSeries = new Series("TangentPoint");
                    pointSeries.ChartType = SeriesChartType.Point;
                    pointSeries.Color = Color.Lime;
                    pointSeries.MarkerStyle = MarkerStyle.Circle;
                    pointSeries.MarkerSize = 10;
                    _mohrCoulombChart.Series.Add(pointSeries);
                }
                else
                {
                    pointSeries = _mohrCoulombChart.Series["TangentPoint"];
                    pointSeries.Points.Clear();
                }

                // Add the tangent point
                pointSeries.Points.AddXY(tangentPoint.X, tangentPoint.Y);
                pointSeries.Points[0].Label = "Failure Point";

                // Calculate the tangent line
                ChartArea ca = _mohrCoulombChart.ChartAreas[0];
                double minX = ca.AxisX.Minimum;
                double maxX = ca.AxisX.Maximum;

                // Get the slope for the tangent line (tan of friction angle)
                double slope = Math.Tan(frictionAngle * Math.PI / 180.0);

                // Calculate points for tangent line
                // Start point (at x=0 or where the line intersects y=0, whichever is greater)
                double x1 = Math.Max(minX, tangentPoint.X - tangentPoint.Y / slope);
                double y1 = Math.Max(0, tangentPoint.Y - slope * (tangentPoint.X - x1));

                // End point
                double x2 = maxX;
                double y2 = tangentPoint.Y + slope * (maxX - tangentPoint.X);

                // Add the tangent line points
                tangentSeries.Points.AddXY(x1, y1);
                tangentSeries.Points.AddXY(x2, y2);

                // Ensure the chart is updated
                _mohrCoulombChart.Invalidate();
            }
            catch (Exception ex)
            {
                Logger.Log($"[TriaxialResultsExtension] Error drawing tangent: {ex.Message}");
            }
        }
        /// <summary>
        /// Calculate failure point coordinates on the Mohr circle based on Mohr-Coulomb criterion
        /// </summary>
        private void CalculateFailurePointCoordinates(double center, double radius, double frictionAngle, double cohesion,
                                                      out double sigmaF, out double tauF)
        {
            // Convert friction angle to radians
            double phi = frictionAngle * Math.PI / 180.0;
            double sinPhi = Math.Sin(phi);
            double cosPhi = Math.Cos(phi);

            // The tangent point is where the Mohr-Coulomb failure envelope touches the circle
            // For a circle with center (center, 0) and radius, the tangent point is:
            sigmaF = center - radius * sinPhi;
            tauF = radius * cosPhi;

            // Ensure values are valid
            if (double.IsNaN(sigmaF) || double.IsInfinity(sigmaF))
                sigmaF = center;

            if (double.IsNaN(tauF) || double.IsInfinity(tauF))
                tauF = 0;
        }

        private void MohrCoulombChart_PostPaint(object sender, ChartPaintEventArgs e)
        {
            // This event will draw directly on the chart graphics if needed
            // We'll just call our main method which now handles everything properly
            if (!_failureDetected || _failureStep < 0 || e.ChartElement.GetType() != typeof(ChartArea))
                return;

            try
            {
                // Get failure data
                double failureStress = _stressStrainData[_failureStep].Y;
                double confiningPressure = GetConfiningPressure();

                // Get material properties
                double frictionAngle = 30; // Default
                double cohesion = 5;      // Default

                // Try to get actual values
                try
                {
                    var frictionField = _parentForm?.GetType().GetField("nudFriction",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    var cohesionField = _parentForm?.GetType().GetField("nudCohesion",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                    if (frictionField != null && cohesionField != null)
                    {
                        var frictionControl = frictionField.GetValue(_parentForm) as NumericUpDown;
                        var cohesionControl = cohesionField.GetValue(_parentForm) as NumericUpDown;

                        if (frictionControl != null)
                            frictionAngle = (double)frictionControl.Value;

                        if (cohesionControl != null)
                            cohesion = (double)cohesionControl.Value;
                    }
                }
                catch { }

                // Draw tangent
                DrawTangentOnMohrChart(confiningPressure, failureStress, frictionAngle, cohesion);
            }
            catch (Exception ex)
            {
                Logger.Log($"[TriaxialResultsExtension] Error in post-paint event: {ex.Message}");
            }
        }

        private void UpdateMohrCoulombChart()
        {
            if (_mohrCoulombChart == null)
                return;

            // Get simulation parameters
            double confiningPressure = GetConfiningPressure();
            double frictionAngle = 30; // Default
            double cohesion = 5;      // Default

            // Try to get the parameters through reflection
            try
            {
                var frictionField = _parentForm.GetType().GetField("nudFriction",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var cohesionField = _parentForm.GetType().GetField("nudCohesion",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                if (frictionField != null && cohesionField != null)
                {
                    var frictionControl = frictionField.GetValue(_parentForm) as NumericUpDown;
                    var cohesionControl = cohesionField.GetValue(_parentForm) as NumericUpDown;

                    if (frictionControl != null && cohesionControl != null)
                    {
                        frictionAngle = (double)frictionControl.Value;
                        cohesion = (double)cohesionControl.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[TriaxialResultsExtension] Error getting parameters: {ex.Message}");
            }

            // Get current axial stress
            double axialStress = _stressStrainData.Count > 0 ? _stressStrainData.Last().Y : confiningPressure;

            // Draw the Mohr circle and failure envelope
            DrawMohrCircle(confiningPressure, axialStress);
            DrawFailureEnvelope(frictionAngle, cohesion);

            // If failure has been detected, draw the tangent
            if (_failureDetected && _failureStep >= 0 && _failureStep < _stressStrainData.Count)
            {
                double failureStress = _stressStrainData[_failureStep].Y;
                DrawTangentOnMohrChart(confiningPressure, failureStress, frictionAngle, cohesion);
            }
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
            if (_mohrCoulombChart == null) return;

            // Create a MohrCircle object to handle calculations
            MohrCircle mohrCircle = new MohrCircle(axialStress, confiningPressure);

            // Clear existing series
            _mohrCoulombChart.Series["Circles"].Points.Clear();
            _mohrCoulombChart.Series["Points"].Points.Clear();

            // Set chart area properties
            ChartArea ca = _mohrCoulombChart.ChartAreas[0];

            // Calculate axis limits with proper margins
            double margin = mohrCircle.Radius * 0.2;
            double minX = 0; // Force X-axis to start at 0
            double maxX = Math.Max(mohrCircle.Sigma1 + margin, minX + mohrCircle.Radius * 2);

            // This is the key for fixing the egg shape problem
            // Make sure Y-axis maximum is proportional to X-axis range
            // to maintain equal scaling on both axes
            double maxY = maxX - minX;

            // Set axis limits
            ca.AxisX.Minimum = minX;
            ca.AxisX.Maximum = maxX;
            ca.AxisY.Minimum = 0; // Start at 0 to position X-axis at bottom
            ca.AxisY.Maximum = maxY;

            // Draw the Mohr circle (only top half - positive shear)
            for (int i = 0; i <= 180; i++)
            {
                double angle = i * Math.PI / 180.0;
                double x = mohrCircle.Center + mohrCircle.Radius * Math.Cos(angle);
                double y = mohrCircle.Radius * Math.Sin(angle);

                // Only add points with positive y (upper half of circle)
                if (y >= 0)
                {
                    _mohrCoulombChart.Series["Circles"].Points.AddXY(x, y);
                }
            }

            // Mark principal stress points on X-axis (y=0)
            _mohrCoulombChart.Series["Points"].Points.AddXY(mohrCircle.Sigma3, 0);
            _mohrCoulombChart.Series["Points"].Points.AddXY(mohrCircle.Sigma1, 0);

            // Add labels for principal stresses
            _mohrCoulombChart.Series["Points"].Points[0].Label = $"σ₃ ({mohrCircle.Sigma3:F1})";
            _mohrCoulombChart.Series["Points"].Points[1].Label = $"σ₁ ({mohrCircle.Sigma1:F1})";

            // Store the MohrCircle object for use in other methods
            _currentMohrCircle = mohrCircle;
        }

        private MohrCircle _currentMohrCircle;

        /// <summary>
        /// Draw Mohr-Coulomb failure envelope
        /// </summary>
        private void DrawFailureEnvelope(double frictionAngle, double cohesion)
        {
            if (_mohrCoulombChart == null || _currentMohrCircle == null) return;

            // Convert friction angle to radians and calculate tangent
            double phi = frictionAngle * Math.PI / 180.0;
            double tanPhi = Math.Tan(phi);

            // Get axis limits
            ChartArea ca = _mohrCoulombChart.ChartAreas[0];
            double minX = ca.AxisX.Minimum;
            double maxX = ca.AxisX.Maximum;

            // Clear existing envelope
            _mohrCoulombChart.Series["Envelope"].Points.Clear();

            // Draw failure envelope line: tau = c + sigma * tan(phi)
            // Starting from x=0 (or minX if greater)
            double startX = Math.Max(0, minX);
            double y1 = cohesion + startX * tanPhi;
            double y2 = cohesion + maxX * tanPhi;

            _mohrCoulombChart.Series["Envelope"].Points.AddXY(startX, y1);
            _mohrCoulombChart.Series["Envelope"].Points.AddXY(maxX, y2);

            // Clear any existing tangent lines
            if (_mohrCoulombChart.Series.IndexOf("TangentPoint") >= 0)
                _mohrCoulombChart.Series["TangentPoint"].Points.Clear();

            if (_mohrCoulombChart.Series.IndexOf("Tangent") >= 0)
                _mohrCoulombChart.Series["Tangent"].Points.Clear();
        }
        /// <summary>
        /// Get the current confining pressure value
        /// </summary>
        private double GetConfiningPressure()
        {
            // Get value from parent form if available
            try
            {
                var confField = _parentForm?.GetType().GetField("nudConfiningP",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                if (confField != null)
                {
                    var confControl = confField.GetValue(_parentForm) as NumericUpDown;
                    if (confControl != null)
                        return (double)confControl.Value;
                }
            }
            catch { }

            // Default value if not available
            return 10.0;
        }

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

                    // Get stress and strain data if available
                    float[,,] stressData = null;
                    float[,,] strainData = null;

                    // Get the stress and strain data through reflection (these might be private fields)
                    try
                    {
                        var stressProperty = _parentForm.GetType().GetProperty("StressData");
                        if (stressProperty != null)
                        {
                            stressData = stressProperty.GetValue(_parentForm) as float[,,];
                        }

                        var strainProperty = _parentForm.GetType().GetProperty("StrainData");
                        if (strainProperty != null)
                        {
                            strainData = strainProperty.GetValue(_parentForm) as float[,,];
                        }
                    }
                    catch
                    {
                        // If we can't get the data, continue with null values
                    }

                    // Get volume labels
                    ILabelVolumeData volumeLabels = GetVolumeLabels();
                    byte materialId = GetSelectedMaterialID();

                    if (damageData != null && volumeLabels != null &&
                        volumeWidth > 0 && volumeHeight > 0 && volumeDepth > 0)
                    {
                        // Find the actual failure point
                        Point3D maxDamagePoint = new Point3D(0, 0, 0);

                        if (_failureDetected && _failureStep >= 0)
                        {
                            // Use the actual failure point from the parent form
                            maxDamagePoint = _parentForm.FindMaxDamagePoint();
                            Logger.Log($"[TriaxialResultsExtension] Using failure point at ({maxDamagePoint.X}, {maxDamagePoint.Y}, {maxDamagePoint.Z})");
                        }

                        // Create a FailurePointVisualizer
                        var visualizer = new FailurePointVisualizer(volumeWidth, volumeHeight, volumeDepth, materialId);

                        // Set all available data
                        if (stressData != null && strainData != null)
                        {
                            visualizer.SetData(volumeLabels, damageData, stressData, strainData);
                        }
                        else if (strainData != null)
                        {
                            visualizer.SetData(volumeLabels, damageData, strainData);
                        }
                        else
                        {
                            visualizer.SetData(volumeLabels, damageData);
                        }

                        visualizer.SetViewParameters(_rotationX, _rotationY, _zoom, _pan);

                        // Create a Point3D from our Point3D struct
                        FailurePointVisualizer.Point3D failurePoint = new FailurePointVisualizer.Point3D(
                            (int)maxDamagePoint.X, (int)maxDamagePoint.Y, (int)maxDamagePoint.Z
                        );

                        // Set the actual failure point in the visualizer
                        visualizer.SetFailurePoint(_failureDetected, failurePoint);

                        // Create visualization with the selected color mode
                        var visualizerColorMode = (FailurePointVisualizer.ColorMapMode)((int)_selectedColorMapMode);
                        bmp = visualizer.CreateVisualization(width, height, visualizerColorMode);
                    }
                    else
                    {
                        // Fall back to a simplified visualization if data is missing
                        using (Graphics g = Graphics.FromImage(bmp))
                        {
                            g.Clear(Color.FromArgb(20, 20, 20));
                            using (Font font = new Font("Segoe UI", 10))
                            using (SolidBrush brush = new SolidBrush(Color.White))
                            using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                            {
                                string message = "Insufficient data for visualization.\nRun a simulation first to see results.";
                                g.DrawString(message, font, brush, width / 2, height / 2, sf);
                            }
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
                            g.DrawString($"Error creating visualization: {ex.Message}",
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
                        // Create a FaultingPlaneVisualizer if it exists
                        bool useBuiltInVisualizer = true;

                        try
                        {
                            // Try to use FaultingPlaneVisualizer class if it exists
                            Type visualizerType = Type.GetType("CTSegmenter.FaultingPlaneVisualizer");
                            if (visualizerType != null)
                            {
                                // Create an instance of FaultingPlaneVisualizer
                                object visualizer = Activator.CreateInstance(
                                    visualizerType,
                                    new object[] { volumeWidth, volumeHeight, volumeDepth, materialId });

                                // Set data
                                var setDataMethod = visualizerType.GetMethod("SetData");
                                if (setDataMethod != null)
                                {
                                    setDataMethod.Invoke(visualizer, new object[] { volumeLabels, damageData });
                                }

                                // Set view parameters
                                var setViewParamsMethod = visualizerType.GetMethod("SetViewParameters");
                                if (setViewParamsMethod != null)
                                {
                                    setViewParamsMethod.Invoke(visualizer, new object[] { _rotationX, _rotationY, _zoom, _pan });
                                }

                                // Set show volume option
                                var setShowVolumeMethod = visualizerType.GetMethod("SetShowVolume");
                                if (setShowVolumeMethod != null)
                                {
                                    setShowVolumeMethod.Invoke(visualizer, new object[] { _showVolume });
                                }

                                // Create visualization
                                var createVisualizationMethod = visualizerType.GetMethod("CreateVisualization");
                                if (createVisualizationMethod != null)
                                {
                                    bmp = (Bitmap)createVisualizationMethod.Invoke(visualizer, new object[] { width, height });
                                    useBuiltInVisualizer = false;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"[TriaxialResultsExtension] Error using FaultingPlaneVisualizer: {ex.Message}");
                            useBuiltInVisualizer = true;
                        }

                        // If the external visualizer failed, use our built-in visualization methods
                        if (useBuiltInVisualizer)
                        {
                            using (Graphics g = Graphics.FromImage(bmp))
                            {
                                g.Clear(Color.FromArgb(20, 20, 20));
                                g.SmoothingMode = SmoothingMode.AntiAlias;
                                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                                // Actually call the drawing methods based on whether to show the volume or not
                                if (_showVolume)
                                {
                                    DrawVolumeWithFaults(g, new Rectangle(0, 0, width, height));
                                }
                                else
                                {
                                    DrawFaultingPlanes(g, new Rectangle(0, 0, width, height));
                                }

                                // Add instructions for interaction
                                DrawInteractionInstructions(g, 10, height - 60, width - 20, 50);
                            }
                        }
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
            Matrix3D transform = new Matrix3D();
            transform.RotateX(_rotationX * (float)Math.PI / 180.0f);
            transform.RotateY(_rotationY * (float)Math.PI / 180.0f);

            int width = rect.Width;
            int height = rect.Height;

            // Center point in view
            float cx = rect.X + width / 2.0f + _pan.X;
            float cy = rect.Y + height / 2.0f + _pan.Y;

            // Get volume dimensions
            int volumeWidth = 0, volumeHeight = 0, volumeDepth = 0;
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
                volumeWidth = 100;
                volumeHeight = 100;
                volumeDepth = 100;
            }

            // Calculate scaling for 3D projection
            float scaleX = width / (float)(volumeWidth + volumeDepth) * _zoom;
            float scaleY = (height - 25) / (float)(volumeHeight + volumeDepth) * _zoom;
            float scale = Math.Min(scaleX, scaleY);

            // Function to project 3D points to 2D
            Func<float, float, float, PointF> Project = (float x, float y, float z) =>
            {
                // Center coordinates to origin
                float fx = x - volumeWidth / 2.0f;
                float fy = y - volumeHeight / 2.0f;
                float fz = z - volumeDepth / 2.0f;

                // Apply 3D transformation
                Vector3 v = transform.Transform(new Vector3(fx, fy, fz));

                // Project to 2D with scaling
                return new PointF(
                    cx + v.X * scale,
                    cy + v.Y * scale
                );
            };

            // Draw transparent cube for the volume
            DrawVolumeOutline(g, Project, volumeWidth, volumeHeight, volumeDepth);

            // Get the damage data
            double[,,] damageData = null;
            var damageProperty = _parentForm.GetType().GetProperty("DamageData");
            if (damageProperty != null)
            {
                damageData = damageProperty.GetValue(_parentForm) as double[,,];
            }

            if (damageData != null)
            {
                // Draw cracks where damage exceeds threshold
                double damageThreshold = 0.7; // Adjust as needed
                using (Pen crackPen = new Pen(Color.Red, 2))
                {
                    // Find points where damage exceeds threshold
                    List<Point3D> crackPoints = new List<Point3D>();

                    for (int z = 0; z < volumeDepth; z++)
                        for (int y = 0; y < volumeHeight; y++)
                            for (int x = 0; x < volumeWidth; x++)
                                if (damageData[x, y, z] > damageThreshold)
                                    crackPoints.Add(new Point3D(x, y, z));

                    // If we have crack points, visualize them
                    if (crackPoints.Count > 0)
                    {
                        // Draw highly damaged points as a crack pattern
                        foreach (var point in crackPoints)
                        {
                            PointF p = Project((float)point.X, (float)point.Y, (float)point.Z);
                            g.FillEllipse(Brushes.Red, p.X - 2, p.Y - 2, 4, 4);

                            // Connect nearby crack points to show fault plane
                            foreach (var otherPoint in crackPoints)
                            {
                                // Only connect points that are close to each other
                                double distance = Math.Sqrt(
                                    Math.Pow(point.X - otherPoint.X, 2) +
                                    Math.Pow(point.Y - otherPoint.Y, 2) +
                                    Math.Pow(point.Z - otherPoint.Z, 2));

                                if (distance < 5) // Adjust this threshold as needed
                                {
                                    PointF p2 = Project((float)otherPoint.X, (float)otherPoint.Y, (float)otherPoint.Z);
                                    g.DrawLine(crackPen, p, p2);
                                }
                            }
                        }

                        // Show the fault plane title and info
                        using (Font titleFont = new Font("Segoe UI", 10, FontStyle.Bold))
                        using (SolidBrush textBrush = new SolidBrush(Color.White))
                        {
                            g.DrawString($"Faulting Planes ({crackPoints.Count} points)",
                                          titleFont, textBrush, rect.X + 10, rect.Y + 10);

                            g.DrawString($"Damage Threshold: {damageThreshold:F2}",
                                          new Font("Segoe UI", 8), textBrush, rect.X + 10, rect.Y + 30);
                        }
                    }
                    else
                    {
                        // No crack points found
                        using (Font font = new Font("Segoe UI", 10))
                        using (SolidBrush brush = new SolidBrush(Color.White))
                        {
                            g.DrawString("No faulting planes detected (damage below threshold)",
                                          font, brush, rect.X + 10, rect.Y + 10);
                        }
                    }
                }
            }
            else
            {
                // No damage data available
                using (Font font = new Font("Segoe UI", 12))
                using (SolidBrush brush = new SolidBrush(Color.White))
                using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    g.DrawString("No damage data available.\nRun simulation first.",
                                 font, brush, rect.X + rect.Width / 2, rect.Y + rect.Height / 2, sf);
                }
            }
        }

        /// <summary>
        /// Draw only the faulting planes without the volume
        /// </summary>
        private void DrawFaultingPlanes(Graphics g, Rectangle rect)
        {
            Matrix3D transform = new Matrix3D();
            transform.RotateX(_rotationX * (float)Math.PI / 180.0f);
            transform.RotateY(_rotationY * (float)Math.PI / 180.0f);

            int width = rect.Width;
            int height = rect.Height;

            // Center point in view
            float cx = rect.X + width / 2.0f + _pan.X;
            float cy = rect.Y + height / 2.0f + _pan.Y;

            // Get volume dimensions
            int volumeWidth = 0, volumeHeight = 0, volumeDepth = 0;
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
                volumeWidth = 100;
                volumeHeight = 100;
                volumeDepth = 100;
            }

            // Calculate scaling for 3D projection
            float scaleX = width / (float)(volumeWidth + volumeDepth) * _zoom;
            float scaleY = (height - 25) / (float)(volumeHeight + volumeDepth) * _zoom;
            float scale = Math.Min(scaleX, scaleY);

            // Function to project 3D points to 2D
            Func<float, float, float, PointF> Project = (float x, float y, float z) =>
            {
                // Center coordinates to origin
                float fx = x - volumeWidth / 2.0f;
                float fy = y - volumeHeight / 2.0f;
                float fz = z - volumeDepth / 2.0f;

                // Apply 3D transformation
                Vector3 v = transform.Transform(new Vector3(fx, fy, fz));

                // Project to 2D with scaling
                return new PointF(
                    cx + v.X * scale,
                    cy + v.Y * scale
                );
            };

            // Get the damage data
            double[,,] damageData = null;
            var damageProperty = _parentForm.GetType().GetProperty("DamageData");
            if (damageProperty != null)
            {
                damageData = damageProperty.GetValue(_parentForm) as double[,,];
            }

            if (damageData != null)
            {
                // Extract coordinates and damage values for high damage areas
                List<Point3D> highDamagePoints = new List<Point3D>();
                List<double> damageValues = new List<double>();

                // Find points with damage > 0.5 (adjust threshold as needed)
                double damageThreshold = 0.5;
                for (int z = 0; z < volumeDepth; z++)
                    for (int y = 0; y < volumeHeight; y++)
                        for (int x = 0; x < volumeWidth; x++)
                            if (damageData[x, y, z] > damageThreshold)
                            {
                                highDamagePoints.Add(new Point3D(x, y, z));
                                damageValues.Add(damageData[x, y, z]);
                            }

                if (highDamagePoints.Count > 0)
                {
                    // Draw title and info
                    using (Font titleFont = new Font("Segoe UI", 10, FontStyle.Bold))
                    using (SolidBrush textBrush = new SolidBrush(Color.White))
                    {
                        g.DrawString($"Faulting Planes Visualization",
                                      titleFont, textBrush, rect.X + 10, rect.Y + 10);

                        // Show point count and thresholds
                        g.DrawString($"High damage points: {highDamagePoints.Count}, Threshold: {damageThreshold:F2}",
                                      new Font("Segoe UI", 8), textBrush, rect.X + 10, rect.Y + 30);
                    }

                    // Set up colors for damage levels
                    Color[] damageColors = new Color[] {
                Color.Yellow,
                Color.Orange,
                Color.OrangeRed,
                Color.Red,
                Color.DarkRed
            };

                    // Try to fit a plane to the points - find the center
                    float avgX = 0, avgY = 0, avgZ = 0;
                    foreach (var pt in highDamagePoints)
                    {
                        avgX += (float)pt.X;
                        avgY += (float)pt.Y;
                        avgZ += (float)pt.Z;
                    }
                    avgX /= highDamagePoints.Count;
                    avgY /= highDamagePoints.Count;
                    avgZ /= highDamagePoints.Count;

                    // Draw the damage points with color based on damage level
                    for (int i = 0; i < highDamagePoints.Count; i++)
                    {
                        var pt = highDamagePoints[i];
                        double damage = damageValues[i];

                        // Project to 2D
                        PointF p = Project((float)pt.X, (float)pt.Y, (float)pt.Z);

                        // Calculate color based on damage value
                        int colorIndex = (int)Math.Min((damage - damageThreshold) / (1.0 - damageThreshold) * (damageColors.Length - 1), damageColors.Length - 1);
                        Color pointColor = damageColors[colorIndex];

                        // Draw the point
                        using (SolidBrush brush = new SolidBrush(pointColor))
                        {
                            float pointSize = (float)(1.0 + damage * 4); // Size based on damage value
                            g.FillEllipse(brush, p.X - pointSize / 2, p.Y - pointSize / 2, pointSize, pointSize);
                        }

                        // Connect to nearby points to visualize the crack plane
                        for (int j = i + 1; j < highDamagePoints.Count; j++)
                        {
                            var pt2 = highDamagePoints[j];

                            // Calculate 3D distance
                            double dist = Math.Sqrt(
                                Math.Pow(pt.X - pt2.X, 2) +
                                Math.Pow(pt.Y - pt2.Y, 2) +
                                Math.Pow(pt.Z - pt2.Z, 2));

                            // Connect if close enough (adjust threshold as needed)
                            if (dist < 5)
                            {
                                PointF p2 = Project((float)pt2.X, (float)pt2.Y, (float)pt2.Z);

                                // Average damage to determine line color
                                double avgDamage = (damage + damageValues[j]) / 2;
                                int lineColorIndex = (int)Math.Min((avgDamage - damageThreshold) / (1.0 - damageThreshold) * (damageColors.Length - 1), damageColors.Length - 1);
                                Color lineColor = damageColors[lineColorIndex];

                                // Draw the connection
                                using (Pen pen = new Pen(Color.FromArgb(150, lineColor), 1))
                                {
                                    g.DrawLine(pen, p, p2);
                                }
                            }
                        }
                    }

                    // Draw approximate fault plane if there are enough points
                    if (highDamagePoints.Count > 10)
                    {
                        // Try to fit a plane - this is simplified and could be improved
                        // with proper plane-fitting algorithms

                        // Draw a translucent fault plane through the centroid
                        PointF center = Project(avgX, avgY, avgZ);

                        // Calculate 4 corners for the fault plane (this is simplified)
                        // In a real implementation, you would compute the best-fit plane
                        float planeSize = 20; // Adjust size as needed

                        PointF[] planeCorners = new PointF[4];
                        planeCorners[0] = Project(avgX - planeSize, avgY - planeSize, avgZ);
                        planeCorners[1] = Project(avgX + planeSize, avgY - planeSize, avgZ);
                        planeCorners[2] = Project(avgX + planeSize, avgY + planeSize, avgZ);
                        planeCorners[3] = Project(avgX - planeSize, avgY + planeSize, avgZ);

                        // Draw the fault plane
                        using (SolidBrush planeBrush = new SolidBrush(Color.FromArgb(80, 255, 0, 0)))
                        {
                            g.FillPolygon(planeBrush, planeCorners);
                        }

                        using (Pen planePen = new Pen(Color.Red, 1.5f))
                        {
                            g.DrawPolygon(planePen, planeCorners);
                        }

                        // Label the fault plane
                        using (Font font = new Font("Segoe UI", 8, FontStyle.Bold))
                        using (SolidBrush brush = new SolidBrush(Color.White))
                        using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center })
                        {
                            g.DrawString("Approximate Fault Plane", font, brush, center.X, center.Y - 15, sf);
                        }
                    }
                }
                else
                {
                    // No high damage points found
                    using (Font font = new Font("Segoe UI", 12))
                    using (SolidBrush brush = new SolidBrush(Color.White))
                    using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    {
                        g.DrawString("No faulting planes detected\n(damage below threshold)",
                                      font, brush, rect.X + rect.Width / 2, rect.Y + rect.Height / 2, sf);
                    }
                }
            }
            else
            {
                // No damage data available
                using (Font font = new Font("Segoe UI", 12))
                using (SolidBrush brush = new SolidBrush(Color.White))
                using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    g.DrawString("No damage data available.\nRun simulation first.",
                                 font, brush, rect.X + rect.Width / 2, rect.Y + rect.Height / 2, sf);
                }
            }
        }
        /// <summary>
        /// Draw the volume outline for the 3D visualization
        /// </summary>
        private void DrawVolumeOutline(Graphics g, Func<float, float, float, PointF> Project,
                                       int volumeWidth, int volumeHeight, int volumeDepth)
        {
            // Get the 8 corners of the volume
            PointF[] corners = new PointF[8];
            corners[0] = Project(0, 0, 0);
            corners[1] = Project(volumeWidth, 0, 0);
            corners[2] = Project(volumeWidth, volumeHeight, 0);
            corners[3] = Project(0, volumeHeight, 0);
            corners[4] = Project(0, 0, volumeDepth);
            corners[5] = Project(volumeWidth, 0, volumeDepth);
            corners[6] = Project(volumeWidth, volumeHeight, volumeDepth);
            corners[7] = Project(0, volumeHeight, volumeDepth);

            // Draw the edges of the volume
            using (Pen pen = new Pen(Color.FromArgb(120, 180, 180, 180), 1))
            {
                // Bottom face
                g.DrawLine(pen, corners[0], corners[1]);
                g.DrawLine(pen, corners[1], corners[2]);
                g.DrawLine(pen, corners[2], corners[3]);
                g.DrawLine(pen, corners[3], corners[0]);

                // Top face
                g.DrawLine(pen, corners[4], corners[5]);
                g.DrawLine(pen, corners[5], corners[6]);
                g.DrawLine(pen, corners[6], corners[7]);
                g.DrawLine(pen, corners[7], corners[4]);

                // Connecting edges
                g.DrawLine(pen, corners[0], corners[4]);
                g.DrawLine(pen, corners[1], corners[5]);
                g.DrawLine(pen, corners[2], corners[6]);
                g.DrawLine(pen, corners[3], corners[7]);
            }

            // Draw translucent faces
            using (SolidBrush faceBrush = new SolidBrush(Color.FromArgb(20, 150, 150, 150)))
            {
                g.FillPolygon(faceBrush, new PointF[] { corners[0], corners[1], corners[2], corners[3] });
                g.FillPolygon(faceBrush, new PointF[] { corners[4], corners[5], corners[6], corners[7] });
                g.FillPolygon(faceBrush, new PointF[] { corners[0], corners[1], corners[5], corners[4] });
                g.FillPolygon(faceBrush, new PointF[] { corners[2], corners[3], corners[7], corners[6] });
                g.FillPolygon(faceBrush, new PointF[] { corners[0], corners[3], corners[7], corners[4] });
                g.FillPolygon(faceBrush, new PointF[] { corners[1], corners[2], corners[6], corners[5] });
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
                g.DrawString("Red areas represent cracks and fault planes in the material",
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
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
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

                        // Use the specialized Mohr-Coulomb image if available, otherwise use the chart
                        if (_mohrCoulombImage != null)
                        {
                            // Use the pre-rendered Mohr-Coulomb visualization that includes the tangent line
                            g.DrawImage(_mohrCoulombImage, width / 2, 60, quadWidth, quadHeight);

                            // Add label
                            using (Font labelFont = new Font("Segoe UI", 10, FontStyle.Bold))
                            using (SolidBrush textBrush = new SolidBrush(Color.White))
                            {
                                g.DrawString("Mohr-Coulomb Diagram", labelFont, textBrush, width / 2, 50);
                            }
                        }
                        else
                        {
                            // Create chart image for Mohr-Coulomb diagram using standard chart
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

                            // Add failure data if failure was detected
                            if (_failureDetected && _failureStep >= 0)
                            {
                                double failureStress = 0;
                                double failureStrain = 0;

                                if (_failureStep < _stressStrainData.Count)
                                {
                                    failureStress = _stressStrainData[_failureStep].Y;
                                    failureStrain = _stressStrainData[_failureStep].X;
                                }

                                using (Font dataFont = new Font("Segoe UI", 9))
                                using (SolidBrush failureBrush = new SolidBrush(Color.FromArgb(255, 100, 100)))
                                {
                                    string failureInfo = $"Failure at step {_failureStep}: " +
                                                         $"Stress = {failureStress:F2} MPa, " +
                                                         $"Strain = {failureStrain:F4}";
                                    g.DrawString(failureInfo, dataFont, failureBrush, 15, height / 2 + quadHeight - 20);
                                }
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

                        // Add metadata and timestamp
                        using (Font footerFont = new Font("Segoe UI", 8))
                        using (SolidBrush textBrush = new SolidBrush(Color.White))
                        using (StringFormat sf = new StringFormat() { Alignment = StringAlignment.Far })
                        {
                            // Bottom-right timestamp
                            g.DrawString(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), footerFont, textBrush,
                                         width - 10, height - 20, sf);

                            // Add peak stress information
                            string peakInfo = $"Peak Stress: {_peakStress:F2} MPa";
                            g.DrawString(peakInfo, footerFont, textBrush, 15, height - 40);
                        }
                    }

                    // Save the image
                    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    string filePath = $"TriaxialResults_Composite_{timestamp}.png";

                    // Ensure directory exists
                    string directory = System.IO.Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(directory) && !System.IO.Directory.Exists(directory))
                    {
                        System.IO.Directory.CreateDirectory(directory);
                    }

                    compositeBmp.Save(filePath, ImageFormat.Png);

                    // Show confirmation
                    MessageBox.Show($"Composite image saved to:\n{filePath}", "Export Complete",
                                   MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[TriaxialResultsExtension] Error exporting composite image: {ex.Message}\n{ex.StackTrace}");
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
            _mohrCoulombImage?.Dispose();
        }
        /// <summary>
        /// Simple 3D matrix for transformations
        /// </summary>
        private class Matrix3D
        {
            private float[] m = new float[16]; // 4x4 matrix in column-major order

            public Matrix3D()
            {
                // Initialize to identity matrix
                m[0] = 1; m[4] = 0; m[8] = 0; m[12] = 0;
                m[1] = 0; m[5] = 1; m[9] = 0; m[13] = 0;
                m[2] = 0; m[6] = 0; m[10] = 1; m[14] = 0;
                m[3] = 0; m[7] = 0; m[11] = 0; m[15] = 1;
            }

            public void RotateX(float angle)
            {
                float c = (float)Math.Cos(angle);
                float s = (float)Math.Sin(angle);

                float m1 = m[1], m5 = m[5], m9 = m[9], m13 = m[13];
                float m2 = m[2], m6 = m[6], m10 = m[10], m14 = m[14];

                m[1] = m1 * c + m2 * s;
                m[5] = m5 * c + m6 * s;
                m[9] = m9 * c + m10 * s;
                m[13] = m13 * c + m14 * s;

                m[2] = m2 * c - m1 * s;
                m[6] = m6 * c - m5 * s;
                m[10] = m10 * c - m9 * s;
                m[14] = m14 * c - m13 * s;
            }

            public void RotateY(float angle)
            {
                float c = (float)Math.Cos(angle);
                float s = (float)Math.Sin(angle);

                float m0 = m[0], m4 = m[4], m8 = m[8], m12 = m[12];
                float m2 = m[2], m6 = m[6], m10 = m[10], m14 = m[14];

                m[0] = m0 * c - m2 * s;
                m[4] = m4 * c - m6 * s;
                m[8] = m8 * c - m10 * s;
                m[12] = m12 * c - m14 * s;

                m[2] = m0 * s + m2 * c;
                m[6] = m4 * s + m6 * c;
                m[10] = m8 * s + m10 * c;
                m[14] = m12 * s + m14 * c;
            }

            public Vector3 Transform(Vector3 v)
            {
                return new Vector3(
                    m[0] * v.X + m[4] * v.Y + m[8] * v.Z + m[12],
                    m[1] * v.X + m[5] * v.Y + m[9] * v.Z + m[13],
                    m[2] * v.X + m[6] * v.Y + m[10] * v.Z + m[14]
                );
            }
        }
    }
}