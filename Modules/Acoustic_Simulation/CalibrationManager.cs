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
        public enum CalibrationType
        {
            VpVsRatio,      // Only Vp/Vs ratio provided
            SeparateValues  // Both Vp and Vs provided
        }
        public CalibrationType InputType { get; set; }
        public double KnownVpVsRatio { get; set; }
        public double MeasuredDensity { get; set; }
        public double MeasuredVolume { get; set; }
        public double ConfiningPressureMPa { get; set; }
        public double KnownVp { get; set; }     // m/s
        public double KnownVs { get; set; }     // m/s
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
                          double measuredDensity, double confiningPressure,
                          double youngModulus, double poissonRatio, double avgGrayValue = 0)
        {
            InputType = CalibrationType.VpVsRatio;
            MaterialName = materialName;
            MaterialID = materialID;
            KnownVpVsRatio = knownVpVsRatio;
            MeasuredDensity = measuredDensity;
            ConfiningPressureMPa = confiningPressure;
            YoungsModulus = youngModulus;
            PoissonRatio = poissonRatio;
            AvgGrayValue = avgGrayValue;
            CalibrationDate = DateTime.Now;
        }
        public CalibrationPoint(string materialName, byte materialID, double knownVp, double knownVs,
                          double measuredDensity, double confiningPressure,
                          double youngModulus, double poissonRatio, double avgGrayValue = 0)
        {
            InputType = CalibrationType.SeparateValues;
            MaterialName = materialName;
            MaterialID = materialID;
            KnownVp = knownVp;
            KnownVs = knownVs;
            KnownVpVsRatio = knownVp / knownVs;
            MeasuredDensity = measuredDensity;
            ConfiningPressureMPa = confiningPressure;
            YoungsModulus = youngModulus;
            PoissonRatio = poissonRatio;
            AvgGrayValue = avgGrayValue;
            CalibrationDate = DateTime.Now;
        }

        public override string ToString()
        {
            if (InputType == CalibrationType.VpVsRatio)
                return $"{MaterialName}: VpVs={KnownVpVsRatio:F3}, ρ={MeasuredDensity:F1} kg/m³, P={ConfiningPressureMPa:F1} MPa";
            else
                return $"{MaterialName}: Vp={KnownVp:F0} m/s, Vs={KnownVs:F0} m/s, ρ={MeasuredDensity:F1} kg/m³, P={ConfiningPressureMPa:F1} MPa";
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

        // Calibration relationship models (organized by confining pressure)
        [XmlIgnore] // Don't serialize these - they're computed
        public Dictionary<double, CalibrationModel> DensityToYoungsModulusModelsByPressure { get; set; }

        [XmlIgnore]
        public Dictionary<double, CalibrationModel> VpVsToPoissonRatioModelsByPressure { get; set; }

        // Legacy models for backward compatibility
        [XmlIgnore]
        public CalibrationModel DensityToYoungsModulusModel { get; set; }

        [XmlIgnore]
        public CalibrationModel VpVsToPoissonRatioModel { get; set; }

        public CalibrationDataset()
        {
            CalibrationPoints = new List<CalibrationPoint>();
            CreationDate = DateTime.Now;
            LastModifiedDate = DateTime.Now;
            Name = "New Calibration Set";
            DensityToYoungsModulusModelsByPressure = new Dictionary<double, CalibrationModel>();
            VpVsToPoissonRatioModelsByPressure = new Dictionary<double, CalibrationModel>();
        }

        /// <summary>
        /// Adds a calibration point to the dataset and updates models
        /// </summary>
        public void AddCalibrationPoint(CalibrationPoint point)
        {
            CalibrationPoints.Add(point);
            LastModifiedDate = DateTime.Now;

            // Recalculate calibration models if we have enough points
            RecalculateCalibrationModels();
        }

        /// <summary>
        /// Removes a calibration point from the dataset and updates models
        /// </summary>
        public void RemoveCalibrationPoint(CalibrationPoint point)
        {
            CalibrationPoints.Remove(point);
            LastModifiedDate = DateTime.Now;

            // Recalculate calibration models if we still have enough points
            RecalculateCalibrationModels();
        }

        /// <summary>
        /// Calculate the best-fit models between material properties and simulation parameters
        /// </summary>
        public void RecalculateCalibrationModels()
        {
            if (CalibrationPoints.Count < 2)
            {
                // Reset all models
                DensityToYoungsModulusModelsByPressure.Clear();
                VpVsToPoissonRatioModelsByPressure.Clear();
                DensityToYoungsModulusModel = null;
                VpVsToPoissonRatioModel = null;
                return;
            }

            // Clear existing models
            DensityToYoungsModulusModelsByPressure.Clear();
            VpVsToPoissonRatioModelsByPressure.Clear();

            // Group calibration points by confining pressure (round to nearest 0.1 MPa)
            var pressureGroups = CalibrationPoints
                .GroupBy(p => Math.Round(p.ConfiningPressureMPa, 1))
                .ToList();

            Logger.Log($"[CalibrationDataset] Processing {pressureGroups.Count} pressure groups");

            // Create models for each pressure group
            foreach (var group in pressureGroups)
            {
                if (group.Count() < 2)
                {
                    Logger.Log($"[CalibrationDataset] Skipping pressure {group.Key} MPa - only {group.Count()} point(s)");
                    continue;
                }

                double pressure = group.Key;
                var points = group.ToList();

                Logger.Log($"[CalibrationDataset] Creating models for pressure {pressure} MPa with {points.Count} points");

                // Create model for density-to-Young's modulus relationship at this pressure
                var densityYoungPoints = points
                    .Where(p => p.YoungsModulus > 0) // Filter out invalid values
                    .Select(p => new Tuple<double, double>(p.MeasuredDensity, p.YoungsModulus))
                    .ToList();

                if (densityYoungPoints.Count >= 2)
                {
                    DensityToYoungsModulusModelsByPressure[pressure] = CalculateLinearModel(densityYoungPoints);
                }

                // Create model for VpVs-to-Poisson's ratio relationship at this pressure
                var vpvsPoissonPoints = points
                    .Where(p => p.PoissonRatio > 0 && p.KnownVpVsRatio > 0) // Filter out invalid values
                    .Select(p => new Tuple<double, double>(p.KnownVpVsRatio, p.PoissonRatio))
                    .ToList();

                if (vpvsPoissonPoints.Count >= 2)
                {
                    VpVsToPoissonRatioModelsByPressure[pressure] = CalculateLinearModel(vpvsPoissonPoints);
                }
            }

            // For backward compatibility, keep the main models using the largest pressure group
            if (pressureGroups.Count > 0)
            {
                var largestGroup = pressureGroups.OrderByDescending(g => g.Count()).First();
                double pressure = largestGroup.Key;

                Logger.Log($"[CalibrationDataset] Using pressure {pressure} MPa for main models (largest group)");

                if (DensityToYoungsModulusModelsByPressure.ContainsKey(pressure))
                    DensityToYoungsModulusModel = DensityToYoungsModulusModelsByPressure[pressure];

                if (VpVsToPoissonRatioModelsByPressure.ContainsKey(pressure))
                    VpVsToPoissonRatioModel = VpVsToPoissonRatioModelsByPressure[pressure];
            }
        }

        /// <summary>
        /// Calculate a linear model from a set of data points
        /// </summary>
        private CalibrationModel CalculateLinearModel(List<Tuple<double, double>> points)
        {
            if (points == null || points.Count < 2)
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

            double denominator = n * sumX2 - sumX * sumX;
            if (Math.Abs(denominator) < 1e-10)
            {
                Logger.Log("[CalibrationDataset] Linear regression failed - denominator too small");
                return null;
            }

            double slope = (n * sumXY - sumX * sumY) / denominator;
            double intercept = (sumY - slope * sumX) / n;

            double r2 = CalculateR2(points, slope, intercept);

            Logger.Log($"[CalibrationDataset] Linear model: y = {slope:F6}x + {intercept:F6}, R² = {r2:F4}");

            return new CalibrationModel { Slope = slope, Intercept = intercept, R2 = r2 };
        }

        /// <summary>
        /// Calculate the coefficient of determination (R²) for a linear model
        /// </summary>
        private double CalculateR2(List<Tuple<double, double>> points, double slope, double intercept)
        {
            if (points == null || points.Count == 0)
                return 0.0;

            double yMean = points.Average(p => p.Item2);
            double ssTot = points.Sum(p => Math.Pow(p.Item2 - yMean, 2));
            double ssRes = points.Sum(p => Math.Pow(p.Item2 - (slope * p.Item1 + intercept), 2));

            if (Math.Abs(ssTot) < 1e-10)
                return 0.0;

            return 1 - (ssRes / ssTot);
        }

        /// <summary>
        /// Calculate Young's modulus based on material density using the calibration model
        /// </summary>
        public double PredictYoungsModulus(double density, double confiningPressure = 0.0)
        {
            // Find model for the closest pressure
            var model = FindClosestPressureModel(DensityToYoungsModulusModelsByPressure, confiningPressure);

            if (model == null)
                return 0.0;

            double predicted = model.Slope * density + model.Intercept;
            Logger.Log($"[CalibrationDataset] Predicted Young's modulus: {predicted:F2} MPa for density {density:F1} kg/m³");
            return predicted;
        }

        /// <summary>
        /// Calculate Poisson's ratio based on known Vp/Vs ratio using the calibration model
        /// </summary>
        public double PredictPoissonRatio(double vpVsRatio, double confiningPressure = 0.0)
        {
            // Find model for the closest pressure
            var model = FindClosestPressureModel(VpVsToPoissonRatioModelsByPressure, confiningPressure);

            if (model == null)
                return 0.25; // Default value

            double predicted = model.Slope * vpVsRatio + model.Intercept;

            // Ensure Poisson's ratio is within physically valid range
            predicted = Math.Max(0.0, Math.Min(0.5, predicted));

            Logger.Log($"[CalibrationDataset] Predicted Poisson's ratio: {predicted:F4} for Vp/Vs {vpVsRatio:F3}");
            return predicted;
        }

        /// <summary>
        /// Find the calibration model for the closest confining pressure
        /// </summary>
        private CalibrationModel FindClosestPressureModel(Dictionary<double, CalibrationModel> models, double targetPressure)
        {
            if (models == null || models.Count == 0)
                return null;

            // Find exact match first
            if (models.ContainsKey(targetPressure))
                return models[targetPressure];

            // Find closest pressure
            var closestPressure = models.Keys
                .OrderBy(p => Math.Abs(p - targetPressure))
                .FirstOrDefault();

            if (models.ContainsKey(closestPressure))
            {
                Logger.Log($"[CalibrationDataset] Using calibration model for {closestPressure} MPa (closest to {targetPressure} MPa)");
                return models[closestPressure];
            }

            return null;
        }

        /// <summary>
        /// Predict the Vp/Vs ratio based on the material density using calibration models
        /// </summary>
        public double PredictVpVsRatio(double density, double confiningPressure = 0.0, double poissonRatio = 0.0)
        {
            // First approach: Direct prediction from density if models exist
            if (VpVsToPoissonRatioModelsByPressure.Count > 0)
            {
                // Find the nearest calibration point by density and pressure
                var candidatePoints = CalibrationPoints
                    .Where(p => Math.Abs(p.ConfiningPressureMPa - confiningPressure) <= 1.0)
                    .OrderBy(p => Math.Abs(p.MeasuredDensity - density))
                    .Take(2)
                    .ToList();

                if (candidatePoints.Count > 0)
                {
                    if (candidatePoints.Count == 1)
                    {
                        return candidatePoints[0].KnownVpVsRatio;
                    }
                    else
                    {
                        // Linear interpolation between two closest points
                        var p1 = candidatePoints[0];
                        var p2 = candidatePoints[1];

                        double t = (density - p1.MeasuredDensity) / (p2.MeasuredDensity - p1.MeasuredDensity);
                        t = Math.Max(0.0, Math.Min(1.0, t)); // Clamp to [0,1]

                        return p1.KnownVpVsRatio + t * (p2.KnownVpVsRatio - p1.KnownVpVsRatio);
                    }
                }
            }

            // Fallback to theoretical relationship if models don't exist
            if (poissonRatio <= 0.0)
            {
                // Use default Poisson's ratio
                poissonRatio = 0.25;

                // Try to predict Poisson's ratio from density
                var model = FindClosestPressureModel(VpVsToPoissonRatioModelsByPressure, confiningPressure);
                if (model != null)
                {
                    // This is a bit circular, but we need some estimate
                    poissonRatio = 0.25; // Use default for now
                }
            }

            // Calculate Vp/Vs from Poisson's ratio (theoretical relationship)
            double vpVsTheoretical = Math.Sqrt((2 * (1 - poissonRatio)) / (1 - 2 * poissonRatio));

            Logger.Log($"[CalibrationDataset] No direct calibration model found, using theoretical Vp/Vs: {vpVsTheoretical:F3}");
            return vpVsTheoretical;
        }

        /// <summary>
        /// Generate calibration summary for display
        /// </summary>
        public string GetCalibrationSummary()
        {
            if (CalibrationPoints.Count < 2)
                return "Insufficient calibration points. At least 2 points are required for each pressure level.";

            string summary = $"Calibration Dataset: {Name}\n";
            summary += $"Total Points: {CalibrationPoints.Count}\n";
            summary += $"Created: {CreationDate:yyyy-MM-dd}, Modified: {LastModifiedDate:yyyy-MM-dd}\n\n";

            // Group by material
            var materialGroups = CalibrationPoints
                .GroupBy(p => p.MaterialName)
                .OrderBy(g => g.Key)
                .ToList();

            summary += "Points by Material:\n";
            foreach (var group in materialGroups)
            {
                summary += $"  {group.Key}: {group.Count()} points\n";
            }

            // Group by pressure
            var pressureGroups = CalibrationPoints
                .GroupBy(p => Math.Round(p.ConfiningPressureMPa, 1))
                .OrderBy(g => g.Key)
                .ToList();

            summary += "\nPoints by Confining Pressure:\n";
            foreach (var group in pressureGroups)
            {
                summary += $"  {group.Key:F1} MPa: {group.Count()} points\n";

                if (DensityToYoungsModulusModelsByPressure?.ContainsKey(group.Key) == true)
                {
                    var eModel = DensityToYoungsModulusModelsByPressure[group.Key];
                    summary += $"    E = {eModel.Slope:F2} × ρ + {eModel.Intercept:F2} MPa (R² = {eModel.R2:F3})\n";
                }

                if (VpVsToPoissonRatioModelsByPressure?.ContainsKey(group.Key) == true)
                {
                    var nuModel = VpVsToPoissonRatioModelsByPressure[group.Key];
                    summary += $"    ν = {nuModel.Slope:F4} × (Vp/Vs) + {nuModel.Intercept:F4} (R² = {nuModel.R2:F3})\n";
                }
            }

            // Overall statistics
            summary += "\nCalibration Ranges:\n";
            if (CalibrationPoints.Count > 0)
            {
                double minDensity = CalibrationPoints.Min(p => p.MeasuredDensity);
                double maxDensity = CalibrationPoints.Max(p => p.MeasuredDensity);
                double minVpVs = CalibrationPoints.Min(p => p.KnownVpVsRatio);
                double maxVpVs = CalibrationPoints.Max(p => p.KnownVpVsRatio);
                double minPressure = CalibrationPoints.Min(p => p.ConfiningPressureMPa);
                double maxPressure = CalibrationPoints.Max(p => p.ConfiningPressureMPa);

                summary += $"  Density: {minDensity:F1} - {maxDensity:F1} kg/m³\n";
                summary += $"  Vp/Vs: {minVpVs:F3} - {maxVpVs:F3}\n";
                summary += $"  Pressure: {minPressure:F1} - {maxPressure:F1} MPa\n";
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
        /// Add calibration point using Vp/Vs ratio
        /// </summary>
        public void AddCurrentSimulationAsCalibrationPoint(double knownVpVsRatio,
                                                          double simulatedVp, double simulatedVs,
                                                          double confiningPressure)
        {
            var point = CreateCalibrationPointFromCurrentSimulation(
                knownVpVsRatio, 0, 0, confiningPressure);

            if (point == null)
                return;

            // Update with simulation results
            point.SimulatedVp = simulatedVp;
            point.SimulatedVs = simulatedVs;
            point.SimulatedVpVsRatio = simulatedVp / simulatedVs;

            // Add to calibration dataset
            CurrentCalibration.AddCalibrationPoint(point);

            Logger.Log($"[CalibrationManager] Added new calibration point: {point.MaterialName}, " +
                      $"VpVs={point.KnownVpVsRatio:F3}, Density={point.MeasuredDensity:F1}, P={confiningPressure:F1} MPa");
        }
        /// <summary>
        /// Add calibration point using separate Vp and Vs values
        /// </summary>
        public void AddCurrentSimulationAsCalibrationPoint(double knownVp, double knownVs,
                                                          double simulatedVp, double simulatedVs,
                                                          double confiningPressure)
        {
            var point = CreateCalibrationPointFromCurrentSimulation(
                0, knownVp, knownVs, confiningPressure);

            if (point == null)
                return;

            // Update with simulation results
            point.SimulatedVp = simulatedVp;
            point.SimulatedVs = simulatedVs;
            point.SimulatedVpVsRatio = simulatedVp / simulatedVs;

            // Add to calibration dataset
            CurrentCalibration.AddCalibrationPoint(point);

            Logger.Log($"[CalibrationManager] Added new calibration point: {point.MaterialName}, " +
                      $"Vp={knownVp:F0}, Vs={knownVs:F0}, Density={point.MeasuredDensity:F1}, P={confiningPressure:F1} MPa");
        }
        /// <summary>
        /// Create a new calibration point from the current simulation parameters and results
        /// </summary>
        private CalibrationPoint CreateCalibrationPointFromCurrentSimulation(double knownVpVsRatio,
                                                                           double knownVp = 0,
                                                                           double knownVs = 0,
                                                                           double confiningPressure = 0)
        {
            if (simulationForm == null || simulationForm.SelectedMaterial == null)
                return null;

            CalibrationPoint point;

            if (knownVp > 0 && knownVs > 0)
            {
                // Using separate Vp and Vs values
                point = new CalibrationPoint(
                    simulationForm.SelectedMaterial.Name,
                    simulationForm.SelectedMaterial.ID,
                    knownVp,
                    knownVs,
                    simulationForm.SelectedMaterial.Density,
                    confiningPressure,
                    (double)simulationForm.GetYoungsModulus(),
                    (double)simulationForm.GetPoissonRatio(),
                    CalculateAverageGrayValue());
            }
            else
            {
                // Using Vp/Vs ratio
                point = new CalibrationPoint(
                    simulationForm.SelectedMaterial.Name,
                    simulationForm.SelectedMaterial.ID,
                    knownVpVsRatio,
                    simulationForm.SelectedMaterial.Density,
                    confiningPressure,
                    (double)simulationForm.GetYoungsModulus(),
                    (double)simulationForm.GetPoissonRatio(),
                    CalculateAverageGrayValue());
            }

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
        /// Apply calibration to the current simulation based on material density and confining pressure
        /// </summary>
        public void ApplyCalibrationToCurrentSimulation(bool applyYoungsModulus = true,
                                                        bool applyPoissonRatio = true,
                                                        double confiningPressure = 0)
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

            // Find calibration points with matching confining pressure (or closest)
            var matchingPoints = CurrentCalibration.CalibrationPoints
                .Where(p => Math.Abs(p.ConfiningPressureMPa - confiningPressure) < 0.1)
                .ToList();

            if (matchingPoints.Count == 0)
            {
                // Find closest confining pressure if no exact match
                var closestPressure = CurrentCalibration.CalibrationPoints
                    .OrderBy(p => Math.Abs(p.ConfiningPressureMPa - confiningPressure))
                    .FirstOrDefault();

                if (closestPressure != null)
                {
                    matchingPoints = CurrentCalibration.CalibrationPoints
                        .Where(p => Math.Abs(p.ConfiningPressureMPa - closestPressure.ConfiningPressureMPa) < 0.1)
                        .ToList();
                }
            }

            if (matchingPoints.Count == 0)
            {
                MessageBox.Show("No calibration points found for the specified confining pressure.",
                    "Calibration Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Use the calibration points to predict properties
            if (applyYoungsModulus)
            {
                double predictedE = PredictProperty(matchingPoints, density, "E");
                simulationForm.SetYoungsModulus((decimal)predictedE);
                Logger.Log($"[CalibrationManager] Applied calibrated Young's modulus: {predictedE:F2} MPa " +
                          $"for density {density:F1} kg/m³ at P={confiningPressure:F1} MPa");
            }

            if (applyPoissonRatio)
            {
                double predictedNu = PredictProperty(matchingPoints, density, "Nu");
                simulationForm.SetPoissonRatio((decimal)predictedNu);
                Logger.Log($"[CalibrationManager] Applied calibrated Poisson's ratio: {predictedNu:F4} " +
                          $"for density {density:F1} kg/m³ at P={confiningPressure:F1} MPa");
            }
        }
        private double PredictProperty(List<CalibrationPoint> points, double density, string property)
        {
            // Simple linear interpolation based on density
            if (points.Count == 1)
            {
                return property == "E" ? points[0].YoungsModulus : points[0].PoissonRatio;
            }

            // Sort by density
            points = points.OrderBy(p => p.MeasuredDensity).ToList();

            // Find the two closest density points
            var lower = points.LastOrDefault(p => p.MeasuredDensity <= density);
            var upper = points.FirstOrDefault(p => p.MeasuredDensity >= density);

            if (lower == null) lower = points.First();
            if (upper == null) upper = points.Last();

            if (lower == upper)
            {
                return property == "E" ? lower.YoungsModulus : lower.PoissonRatio;
            }

            // Linear interpolation
            double t = (density - lower.MeasuredDensity) / (upper.MeasuredDensity - lower.MeasuredDensity);

            if (property == "E")
            {
                return lower.YoungsModulus + t * (upper.YoungsModulus - lower.YoungsModulus);
            }
            else
            {
                return lower.PoissonRatio + t * (upper.PoissonRatio - lower.PoissonRatio);
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