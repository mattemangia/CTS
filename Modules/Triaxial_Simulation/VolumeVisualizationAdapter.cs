using CTS;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace CTS
{
    /// <summary>
    /// Adapter class that provides connectivity between the TriaxialSimulationForm
    /// and the various visualization extensions
    /// </summary>
    public class VolumeVisualizationAdapter : IDisposable
    {
        // Parent form reference (to access data)
        private TriaxialSimulationForm _parentForm;

        // Extension components
        private MohrCoulombVisualization _mohrCoulombVisualizer;
        private FailurePointVisualizer _failurePointVisualizer;
        private FaultingPlaneVisualizer _faultingPlaneVisualizer;

        // Current data references
        private ILabelVolumeData _volumeLabels;
        private float[,,] _densityVolume;
        private double[,,] _damage;
        private byte _materialId;
        private int _width, _height, _depth;
        private float _pixelSize;

        // View parameters
        private float _rotationX = 30;
        private float _rotationY = 30;
        private float _zoom = 1.0f;
        private PointF _pan = new PointF(0, 0);

        /// <summary>
        /// Constructor
        /// </summary>
        public VolumeVisualizationAdapter(TriaxialSimulationForm parentForm)
        {
            _parentForm = parentForm;

            // Create extension components
            _mohrCoulombVisualizer = new MohrCoulombVisualization();

            // We'll initialize the 3D visualizers once we have volume dimensions
        }

        /// <summary>
        /// Initialize the adapter with volume data
        /// </summary>
        public void Initialize(ILabelVolumeData labels, float[,,] density, byte materialId,
                              int width, int height, int depth, float pixelSize)
        {
            _volumeLabels = labels;
            _densityVolume = density;
            _materialId = materialId;
            _width = width;
            _height = height;
            _depth = depth;
            _pixelSize = pixelSize;

            // Initialize 3D visualizers now that we have dimensions
            _failurePointVisualizer = new FailurePointVisualizer(width, height, depth, materialId);
            _faultingPlaneVisualizer = new FaultingPlaneVisualizer(width, height, depth, materialId);

            // Set view parameters
            _failurePointVisualizer.SetViewParameters(_rotationX, _rotationY, _zoom, _pan);
            _faultingPlaneVisualizer.SetViewParameters(_rotationX, _rotationY, _zoom, _pan);
        }

        /// <summary>
        /// Update simulation data after each step
        /// </summary>
        public void UpdateSimulationData(double[,,] damage, float[,,] stress = null, float[,,] strain = null)
        {
            _damage = damage;

            // Update the visualizers with the new data
            if (_failurePointVisualizer != null)
            {
                _failurePointVisualizer.SetData(_volumeLabels, damage, stress, strain);
            }

            if (_faultingPlaneVisualizer != null)
            {
                _faultingPlaneVisualizer.SetData(_volumeLabels, damage, _densityVolume);
            }
        }

        /// <summary>
        /// Update failure point information
        /// </summary>
        public void UpdateFailurePoint(bool detected, FailurePointVisualizer.Point3D failurePoint)
        {
            if (_failurePointVisualizer != null)
            {
                _failurePointVisualizer.SetFailurePoint(detected, failurePoint);
            }
        }

        /// <summary>
        /// Set view transformation parameters for 3D visualizations
        /// </summary>
        public void SetViewParameters(float rotationX, float rotationY, float zoom, PointF pan)
        {
            _rotationX = rotationX;
            _rotationY = rotationY;
            _zoom = zoom;
            _pan = pan;

            // Update the visualizers
            if (_failurePointVisualizer != null)
            {
                _failurePointVisualizer.SetViewParameters(rotationX, rotationY, zoom, pan);
            }

            if (_faultingPlaneVisualizer != null)
            {
                _faultingPlaneVisualizer.SetViewParameters(rotationX, rotationY, zoom, pan);
            }
        }

        /// <summary>
        /// Show or hide volume in faulting plane visualization
        /// </summary>
        public void SetShowVolume(bool showVolume)
        {
            if (_faultingPlaneVisualizer != null)
            {
                _faultingPlaneVisualizer.SetShowVolume(showVolume);
            }
        }

        /// <summary>
        /// Create a Mohr-Coulomb visualization
        /// </summary>
        public Bitmap CreateMohrCoulombVisualization(int width, int height,
                                                  double confiningPressure, double axialPressure,
                                                  double frictionAngle, double cohesion)
        {
            return _mohrCoulombVisualizer.CreateMohrCircleVisualization(
                width, height, confiningPressure, axialPressure, frictionAngle, cohesion);
        }

        /// <summary>
        /// Create a failure point visualization
        /// </summary>
        public Bitmap CreateFailurePointVisualization(int width, int height,
                                                   FailurePointVisualizer.ColorMapMode colorMode)
        {
            // Ensure visualizer is initialized
            if (_failurePointVisualizer == null)
            {
                return CreatePlaceholderImage(width, height, "Failure Point Visualizer not initialized");
            }

            return _failurePointVisualizer.CreateVisualization(width, height, colorMode);
        }

        /// <summary>
        /// Create a faulting plane visualization
        /// </summary>
        public Bitmap CreateFaultingPlaneVisualization(int width, int height)
        {
            // Ensure visualizer is initialized
            if (_faultingPlaneVisualizer == null)
            {
                return CreatePlaceholderImage(width, height, "Faulting Plane Visualizer not initialized");
            }

            return _faultingPlaneVisualizer.CreateVisualization(width, height);
        }

        /// <summary>
        /// Handle mouse interaction for 3D visualizations
        /// </summary>
        public void HandleMouseDown(MouseEventArgs e, bool isFaultingPlaneView)
        {
            if (isFaultingPlaneView && _faultingPlaneVisualizer != null)
            {
                _faultingPlaneVisualizer.HandleMouseDown(e);
            }
        }

        public void HandleMouseMove(MouseEventArgs e, bool isFaultingPlaneView)
        {
            if (isFaultingPlaneView && _faultingPlaneVisualizer != null)
            {
                _faultingPlaneVisualizer.HandleMouseMove(e);
            }
        }

        public void HandleMouseUp(MouseEventArgs e, bool isFaultingPlaneView)
        {
            if (isFaultingPlaneView && _faultingPlaneVisualizer != null)
            {
                _faultingPlaneVisualizer.HandleMouseUp(e);
            }
        }

        public void HandleMouseWheel(MouseEventArgs e, bool isFaultingPlaneView)
        {
            if (isFaultingPlaneView && _faultingPlaneVisualizer != null)
            {
                _faultingPlaneVisualizer.HandleMouseWheel(e);
            }
        }

        /// <summary>
        /// Create a placeholder image when a visualizer is not initialized
        /// </summary>
        private Bitmap CreatePlaceholderImage(int width, int height, string message)
        {
            Bitmap bmp = new Bitmap(Math.Max(1, width), Math.Max(1, height));

            using (Graphics g = Graphics.FromImage(bmp))
            {
                // Fill background
                g.Clear(Color.FromArgb(30, 30, 30));

                // Draw message
                using (Font font = new Font("Segoe UI", 12))
                using (SolidBrush textBrush = new SolidBrush(Color.White))
                using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    g.DrawString(message, font, textBrush, width / 2, height / 2, sf);
                }
            }

            return bmp;
        }

        /// <summary>
        /// Create and export a composite image with all visualizations
        /// </summary>
        public string ExportCompositeImage(double confiningPressure, double axialPressure,
                                         double peakStress, double peakStrain,
                                         bool failureDetected, int failureStep)
        {
            try
            {
                // Parameters from the parent form (would be passed in a full implementation)
                double frictionAngle = 30;
                double cohesion = 5;

                try
                {
                    // Try to get the parameters through reflection
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
                catch
                {
                    // If reflection fails, use default values
                }

                // Create composite image
                int width = 1200;
                int height = 900;
                Bitmap compositeBmp = new Bitmap(width, height);

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

                    // Get Mohr-Coulomb visualization
                    Bitmap mohrBmp = _mohrCoulombVisualizer.CreateMohrCircleVisualization(
                        quadWidth, quadHeight, confiningPressure, peakStress, frictionAngle, cohesion);
                    g.DrawImage(mohrBmp, 10, 60);
                    mohrBmp.Dispose();

                    // Add quadrant label
                    using (Font labelFont = new Font("Segoe UI", 10, FontStyle.Bold))
                    using (SolidBrush textBrush = new SolidBrush(Color.White))
                    {
                        g.DrawString("Mohr-Coulomb Diagram", labelFont, textBrush, 10, 40);
                    }

                    // Get Failure point visualization
                    if (_failurePointVisualizer != null)
                    {
                        Bitmap failureBmp = _failurePointVisualizer.CreateVisualization(
                            quadWidth, quadHeight, FailurePointVisualizer.ColorMapMode.Damage);
                        g.DrawImage(failureBmp, width / 2, 60);
                        failureBmp.Dispose();

                        // Add quadrant label
                        using (Font labelFont = new Font("Segoe UI", 10, FontStyle.Bold))
                        using (SolidBrush textBrush = new SolidBrush(Color.White))
                        {
                            g.DrawString("Failure Point Visualization", labelFont, textBrush, width / 2, 40);
                        }
                    }

                    // Get Stress visualization 
                    if (_failurePointVisualizer != null)
                    {
                        Bitmap stressBmp = _failurePointVisualizer.CreateVisualization(
                            quadWidth, quadHeight, FailurePointVisualizer.ColorMapMode.Stress);
                        g.DrawImage(stressBmp, 10, height / 2 + 10);
                        stressBmp.Dispose();

                        // Add quadrant label
                        using (Font labelFont = new Font("Segoe UI", 10, FontStyle.Bold))
                        using (SolidBrush textBrush = new SolidBrush(Color.White))
                        {
                            g.DrawString("Stress Visualization", labelFont, textBrush, 10, height / 2 - 10);
                        }
                    }

                    // Get Faulting plane visualization
                    if (_faultingPlaneVisualizer != null)
                    {
                        Bitmap faultingBmp = _faultingPlaneVisualizer.CreateVisualization(
                            quadWidth, quadHeight);
                        g.DrawImage(faultingBmp, width / 2, height / 2 + 10);
                        faultingBmp.Dispose();

                        // Add quadrant label
                        using (Font labelFont = new Font("Segoe UI", 10, FontStyle.Bold))
                        using (SolidBrush textBrush = new SolidBrush(Color.White))
                        {
                            g.DrawString("Faulting Plane Visualization", labelFont, textBrush, width / 2, height / 2 - 10);
                        }
                    }

                    // Add summary information
                    using (Font infoFont = new Font("Segoe UI", 9))
                    using (SolidBrush textBrush = new SolidBrush(Color.White))
                    {
                        string summary = $"Confining Pressure: {confiningPressure:F2} MPa";
                        summary += $" | Peak Stress: {peakStress:F2} MPa";
                        summary += $" | Peak Strain: {peakStrain:F4}";

                        if (failureDetected)
                        {
                            summary += $" | Failure detected at step {failureStep}";
                        }

                        g.DrawString(summary, infoFont, textBrush, 10, height - 30);
                    }

                    // Add timestamp
                    using (Font timeFont = new Font("Segoe UI", 8))
                    using (SolidBrush textBrush = new SolidBrush(Color.FromArgb(200, 200, 200)))
                    using (StringFormat sf = new StringFormat() { Alignment = StringAlignment.Far })
                    {
                        g.DrawString(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), timeFont, textBrush, width - 10, height - 20, sf);
                    }
                }

                // Save the image
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string filePath = $"TriaxialResults_Composite_{timestamp}.png";
                compositeBmp.Save(filePath, ImageFormat.Png);
                compositeBmp.Dispose();

                return filePath;
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        /// <summary>
        /// Cleanup resources
        /// </summary>
        public void Dispose()
        {
            // Nothing to dispose in the MohrCoulombVisualizer
            _mohrCoulombVisualizer = null;

            // These are IDisposable, but they don't have any unmanaged resources
            // that need explicit cleanup
            _failurePointVisualizer = null;
            _faultingPlaneVisualizer = null;
        }
    }
}