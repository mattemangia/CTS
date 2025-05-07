using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using System.Windows.Forms;

namespace CTS
{
    /// <summary>
    /// Manages calibration data for the acoustic simulator to accurately match experimental results.
    /// Provides functions for 2-3 point calibration, save/load capabilities, and parameter adjustment.
    /// </summary>
    [Serializable]
    public class CalibrationPoint
    {
        // Known measured values from real CT data
        public double KnownVpVsRatio { get; set; }
        public double MeasuredDensity { get; set; }
        public double MeasuredVolume { get; set; }
        [System.Xml.Serialization.XmlIgnore]
        public double Density
        {
            get { return MeasuredDensity; }
            set { MeasuredDensity = value; }
        }
        [System.Xml.Serialization.XmlIgnore]
        public string Region
        {
            get { return Notes ?? "Default Region"; }
            set { Notes = value; }
        }

        [System.Xml.Serialization.XmlIgnore]
        public string Material
        {
            get { return MaterialName; }
            set { MaterialName = value; }
        }
        // Simulation parameters
        public double YoungsModulus { get; set; }
        public double PoissonRatio { get; set; }

        // Simulation results
        public double SimulatedVp { get; set; }
        public double SimulatedVs { get; set; }
        public double SimulatedVpVsRatio { get; set; }

        // CT data reference
        public double AvgGrayValue { get; set; }
        public string MaterialName { get; set; }
        public byte MaterialID { get; set; }

        // Calibration metadata
        public DateTime CalibrationDate { get; set; }
        public string Notes { get; set; }

        // Default constructor for serialization
        public CalibrationPoint()
        {
            CalibrationDate = DateTime.Now;
        }

        // Constructor with most common parameters
        public CalibrationPoint(string materialName, byte materialID, double knownVpVsRatio,
                               double measuredDensity, double youngModulus, double poissonRatio,
                               double avgGrayValue = 0)
        {
            MaterialName = materialName;
            MaterialID = materialID;
            KnownVpVsRatio = knownVpVsRatio;
            MeasuredDensity = measuredDensity;
            YoungsModulus = youngModulus;
            PoissonRatio = poissonRatio;
            AvgGrayValue = avgGrayValue;
            CalibrationDate = DateTime.Now;
        }

        public override string ToString()
        {
            return $"{MaterialName}: VpVs={KnownVpVsRatio:F3}, ρ={MeasuredDensity:F1} kg/m³";
        }
    }

    /// <summary>
    /// Holds a collection of calibration points and provides methods to perform calibrations,
    /// calculate calibration curves, and adjust simulation parameters.
    /// </summary>
    [Serializable]
    public class CalibrationDataset
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public DateTime CreationDate { get; set; }
        public DateTime LastModifiedDate { get; set; }
        public List<CalibrationPoint> CalibrationPoints { get; set; }

        // Calibration relationship models
        [XmlIgnore] // Don't serialize these - they're computed
        public CalibrationModel DensityToYoungsModulusModel { get; set; }

        [XmlIgnore]
        public CalibrationModel VpVsToPoissonRatioModel { get; set; }

        public CalibrationDataset()
        {
            CalibrationPoints = new List<CalibrationPoint>();
            CreationDate = DateTime.Now;
            LastModifiedDate = DateTime.Now;
            Name = "New Calibration Set";
        }

        /// <summary>
        /// Adds a calibration point to the dataset and updates models
        /// </summary>
        public void AddCalibrationPoint(CalibrationPoint point)
        {
            CalibrationPoints.Add(point);
            LastModifiedDate = DateTime.Now;

            // Recalculate calibration models if we have enough points
            if (CalibrationPoints.Count >= 2)
            {
                RecalculateCalibrationModels();
            }
        }

        /// <summary>
        /// Removes a calibration point from the dataset and updates models
        /// </summary>
        public void RemoveCalibrationPoint(CalibrationPoint point)
        {
            CalibrationPoints.Remove(point);
            LastModifiedDate = DateTime.Now;

            // Recalculate calibration models if we still have enough points
            if (CalibrationPoints.Count >= 2)
            {
                RecalculateCalibrationModels();
            }
            else
            {
                // Not enough points for calibration
                DensityToYoungsModulusModel = null;
                VpVsToPoissonRatioModel = null;
            }
        }

        /// <summary>
        /// Calculate the best-fit models between material properties and simulation parameters
        /// </summary>
        public void RecalculateCalibrationModels()
        {
            if (CalibrationPoints.Count < 2)
                return;

            // Create model for density-to-Young's modulus relationship
            var densityYoungPoints = CalibrationPoints
                .Select(p => new Tuple<double, double>(p.MeasuredDensity, p.YoungsModulus))
                .ToList();
            DensityToYoungsModulusModel = CalculateLinearModel(densityYoungPoints);

            // Create model for VpVs-to-Poisson's ratio relationship
            var vpvsPoissonPoints = CalibrationPoints
                .Select(p => new Tuple<double, double>(p.KnownVpVsRatio, p.PoissonRatio))
                .ToList();
            VpVsToPoissonRatioModel = CalculateLinearModel(vpvsPoissonPoints);
        }

        /// <summary>
        /// Calculate a linear model from a set of data points
        /// </summary>
        private CalibrationModel CalculateLinearModel(List<Tuple<double, double>> points)
        {
            if (points.Count < 2)
                return null;

            int n = points.Count;
            double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;

            foreach (var point in points)
            {
                double x = point.Item1;
                double y = point.Item2;

                sumX += x;
                sumY += y;
                sumXY += x * y;
                sumX2 += x * x;
            }

            double slope = (n * sumXY - sumX * sumY) / (n * sumX2 - sumX * sumX);
            double intercept = (sumY - slope * sumX) / n;

            double r2 = CalculateR2(points, slope, intercept);

            return new CalibrationModel { Slope = slope, Intercept = intercept, R2 = r2 };
        }

        /// <summary>
        /// Calculate the coefficient of determination (R²) for a linear model
        /// </summary>
        private double CalculateR2(List<Tuple<double, double>> points, double slope, double intercept)
        {
            double yMean = points.Average(p => p.Item2);
            double ssTot = points.Sum(p => Math.Pow(p.Item2 - yMean, 2));
            double ssRes = points.Sum(p => Math.Pow(p.Item2 - (slope * p.Item1 + intercept), 2));

            return 1 - (ssRes / ssTot);
        }

        /// <summary>
        /// Calculate Young's modulus based on material density using the calibration model
        /// </summary>
        public double PredictYoungsModulus(double density)
        {
            if (DensityToYoungsModulusModel == null)
                return 0.0;

            return DensityToYoungsModulusModel.Slope * density + DensityToYoungsModulusModel.Intercept;
        }

        /// <summary>
        /// Calculate Poisson's ratio based on known Vp/Vs ratio using the calibration model
        /// </summary>
        public double PredictPoissonRatio(double vpVsRatio)
        {
            if (VpVsToPoissonRatioModel == null)
                return 0.25; // Default value

            return VpVsToPoissonRatioModel.Slope * vpVsRatio + VpVsToPoissonRatioModel.Intercept;
        }

        /// <summary>
        /// Predict the Vp/Vs ratio based on the material density using calibration models
        /// </summary>
        public double PredictVpVsRatio(double density, double poissonRatio = 0.0)
        {
            if (DensityToYoungsModulusModel == null || VpVsToPoissonRatioModel == null)
                return 0.0;

            // If poisson ratio not specified, calculate based on density correlation
            if (poissonRatio <= 0.0)
            {
                // Use theoretical relation for Vp/Vs from Poisson's ratio
                // Vp/Vs = sqrt((2*(1-v))/(1-2*v))

                // First we need a reliable Poisson's ratio estimate
                // We'll use either our calibration or a default
                poissonRatio = 0.25; // Default value

                // Find the nearest calibration point by density
                var closestPoint = CalibrationPoints
                    .OrderBy(p => Math.Abs(p.MeasuredDensity - density))
                    .FirstOrDefault();

                if (closestPoint != null)
                {
                    poissonRatio = closestPoint.PoissonRatio;
                }
            }

            // Calculate Vp/Vs from Poisson's ratio
            return Math.Sqrt((2 * (1 - poissonRatio)) / (1 - 2 * poissonRatio));
        }

        /// <summary>
        /// Generate calibration summary for display
        /// </summary>
        public string GetCalibrationSummary()
        {
            if (CalibrationPoints.Count < 2)
                return "Insufficient calibration points. At least 2 points are required.";

            string summary = $"Calibration Dataset: {Name}\n";
            summary += $"Points: {CalibrationPoints.Count}\n";

            if (DensityToYoungsModulusModel != null)
            {
                summary += $"Density to Young's Modulus: E = {DensityToYoungsModulusModel.Slope:F2} × ρ + {DensityToYoungsModulusModel.Intercept:F2} MPa (R² = {DensityToYoungsModulusModel.R2:F3})\n";
            }

            if (VpVsToPoissonRatioModel != null)
            {
                summary += $"Vp/Vs to Poisson's Ratio: ν = {VpVsToPoissonRatioModel.Slope:F4} × (Vp/Vs) + {VpVsToPoissonRatioModel.Intercept:F4} (R² = {VpVsToPoissonRatioModel.R2:F3})\n";
            }

            return summary;
        }
    }

    /// <summary>
    /// Linear model for calibration relationships
    /// </summary>
    [Serializable]
    public class CalibrationModel
    {
        public double Slope { get; set; }
        public double Intercept { get; set; }
        public double R2 { get; set; }
    }

    /// <summary>
    /// Main class for managing calibration data, providing save/load capabilities
    /// and interfacing with the simulator
    /// </summary>
    public class CalibrationManager
    {
        private readonly AcousticSimulationForm simulationForm;
        public CalibrationDataset CurrentCalibration { get; private set; }

        public CalibrationManager(AcousticSimulationForm form)
        {
            simulationForm = form;
            CurrentCalibration = new CalibrationDataset();
        }

        /// <summary>
        /// Create a new calibration point from the current simulation parameters and results
        /// </summary>
        public CalibrationPoint CreateCalibrationPointFromCurrentSimulation(double knownVpVsRatio)
        {
            if (simulationForm == null || simulationForm.SelectedMaterial == null)
                return null;

            // Create a new calibration point with current simulation values
            var point = new CalibrationPoint
            {
                MaterialName = simulationForm.SelectedMaterial.Name,
                MaterialID = simulationForm.SelectedMaterial.ID,
                KnownVpVsRatio = knownVpVsRatio,
                MeasuredDensity = simulationForm.SelectedMaterial.Density,
                MeasuredVolume = simulationForm.CalculateTotalVolume(),
                AvgGrayValue = CalculateAverageGrayValue(),
                YoungsModulus = (double)simulationForm.GetYoungsModulus(), // Assuming we have a getter
                PoissonRatio = (double)simulationForm.GetPoissonRatio(),   // Assuming we have a getter
                CalibrationDate = DateTime.Now
            };

            return point;
        }

        /// <summary>
        /// Calculate average gray value for the current material - used for density calibration
        /// </summary>
        private double CalculateAverageGrayValue()
        {
            // This should invoke the form's method to calculate average gray value
            // We'll assume the form already has this capability
            return simulationForm.CalculateAverageGrayValue();
        }

        /// <summary>
        /// Add the current simulation as a calibration point
        /// </summary>
        public void AddCurrentSimulationAsCalibrationPoint(double knownVpVsRatio, double simulatedVp, double simulatedVs)
        {
            var point = CreateCalibrationPointFromCurrentSimulation(knownVpVsRatio);
            if (point == null)
                return;

            // Update with simulation results
            point.SimulatedVp = simulatedVp;
            point.SimulatedVs = simulatedVs;
            point.SimulatedVpVsRatio = simulatedVp / simulatedVs;

            // Add to calibration dataset
            CurrentCalibration.AddCalibrationPoint(point);

            Logger.Log($"[CalibrationManager] Added new calibration point: {point.MaterialName}, " +
                      $"VpVs={point.KnownVpVsRatio:F3}, Density={point.MeasuredDensity:F1}");
        }

        /// <summary>
        /// Apply calibration to the current simulation based on material density
        /// </summary>
        public void ApplyCalibrationToCurrentSimulation(bool applyYoungsModulus = true, bool applyPoissonRatio = true)
        {
            if (simulationForm == null || simulationForm.SelectedMaterial == null)
                return;

            if (CurrentCalibration.CalibrationPoints.Count < 2)
            {
                MessageBox.Show("Insufficient calibration points. At least 2 points are required.",
                    "Calibration Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            double density = simulationForm.SelectedMaterial.Density;

            if (applyYoungsModulus)
            {
                double predictedE = CurrentCalibration.PredictYoungsModulus(density);
                simulationForm.SetYoungsModulus((decimal)predictedE);
                Logger.Log($"[CalibrationManager] Applied calibrated Young's modulus: {predictedE:F2} MPa " +
                          $"for density {density:F1} kg/m³");
            }

            if (applyPoissonRatio)
            {
                // For Poisson's ratio, we use our known Vp/Vs relationship
                // In a real case, we might need to estimate the Vp/Vs from other properties
                // But here we'll use the theoretical relationship based on the nearest material

                var closestPoint = CurrentCalibration.CalibrationPoints
                    .OrderBy(p => Math.Abs(p.MeasuredDensity - density))
                    .FirstOrDefault();

                if (closestPoint != null)
                {
                    double predictedPoissonRatio = CurrentCalibration.PredictPoissonRatio(closestPoint.KnownVpVsRatio);
                    simulationForm.SetPoissonRatio((decimal)predictedPoissonRatio);
                    Logger.Log($"[CalibrationManager] Applied calibrated Poisson's ratio: {predictedPoissonRatio:F4} " +
                              $"based on nearest material {closestPoint.MaterialName}");
                }
            }
        }

        /// <summary>
        /// Save calibration data to a file
        /// </summary>
        public bool SaveCalibration(string filePath)
        {
            try
            {
                // Create a serializer
                XmlSerializer serializer = new XmlSerializer(typeof(CalibrationDataset));

                // Update the last modified date
                CurrentCalibration.LastModifiedDate = DateTime.Now;

                // Save to file
                using (FileStream fs = new FileStream(filePath, FileMode.Create))
                {
                    serializer.Serialize(fs, CurrentCalibration);
                }

                Logger.Log($"[CalibrationManager] Saved calibration data to: {filePath}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[CalibrationManager] Error saving calibration: {ex.Message}");
                MessageBox.Show($"Error saving calibration: {ex.Message}",
                    "Save Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        /// <summary>
        /// Load calibration data from a file
        /// </summary>
        public bool LoadCalibration(string filePath)
        {
            try
            {
                // Create a serializer
                XmlSerializer serializer = new XmlSerializer(typeof(CalibrationDataset));

                // Load from file
                using (FileStream fs = new FileStream(filePath, FileMode.Open))
                {
                    CurrentCalibration = (CalibrationDataset)serializer.Deserialize(fs);
                }

                // Recalculate models (they aren't serialized)
                CurrentCalibration.RecalculateCalibrationModels();

                Logger.Log($"[CalibrationManager] Loaded calibration data from: {filePath}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[CalibrationManager] Error loading calibration: {ex.Message}");
                MessageBox.Show($"Error loading calibration: {ex.Message}",
                    "Load Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }
        
    }

}