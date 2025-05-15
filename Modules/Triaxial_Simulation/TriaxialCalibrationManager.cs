using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using CTS;
using MathNet.Numerics;
using MathNet.Numerics.LinearRegression;
using static CTS.StressAnalysisForm;

namespace CTS.Modules.Triaxial_Simulation
{
    public class TriaxialCalibrationManager
    {
        private TriaxialSimulationForm simulationForm;

        // Stress-strain data from simulation
        private List<Point> stressStrainData = new List<Point>();
        private List<double> volumetricStrainData = new List<double>();
        private List<double> axialStrainData = new List<double>();
        private List<double> lateralStrainData = new List<double>();

        // Constants for calibration
        private const float ELASTIC_ZONE_LIMIT = 0.2f; // Percentage of expected yield strain to use for elastic calibration
        private const int MINIMUM_POINTS_FOR_REGRESSION = 5;
        private const float YIELD_OFFSET_STRAIN = 0.002f; // 0.2% offset for yield determination

        public TriaxialCalibrationManager(TriaxialSimulationForm form)
        {
            this.simulationForm = form;
        }

        public CalibrationParameters PerformCalibration()
        {
            // Create a new calibration parameters object
            CalibrationParameters parameters = new CalibrationParameters();

            // Get stress-strain data from simulation
            CollectStressStrainData();

            if (stressStrainData.Count < MINIMUM_POINTS_FOR_REGRESSION)
            {
                throw new InvalidOperationException("Insufficient stress-strain data for calibration. Please run a simulation first.");
            }

            // 1. Calibrate Young's Modulus using linear regression
            parameters.YoungModulus = CalibrateYoungsModulus();

            // 2. Calibrate Poisson's Ratio
            parameters.PoissonRatio = CalibratePoissonRatio();

            // 3. Calibrate Yield Strength using 0.2% offset method
            parameters.YieldStrength = CalibrateYieldStrength(parameters.YoungModulus);

            // 4. Calibrate Brittle Strength from peak stress
            parameters.BrittleStrength = CalibrateBrittleStrength();

            // 5. Calibrate Mohr-Coulomb parameters
            CalibrateMohrCoulombParameters(parameters);

            return parameters;
        }

        private void CollectStressStrainData()
        {
            // Clear existing data
            stressStrainData.Clear();
            volumetricStrainData.Clear();
            axialStrainData.Clear();
            lateralStrainData.Clear();

            // Get the actual stress-strain curve from the simulation form
            var chartData = simulationForm.GetStressStrainChartData();

            if (chartData == null || chartData.Count == 0)
            {
                // If no chart data is available, extract from particle states
                ExtractDataFromParticleStates();
                return;
            }

            // Convert chart points to our data format
            foreach (var point in chartData)
            {
                stressStrainData.Add(point);

                // Calculate strains from the point data
                float axialStrain = point.X / 1000.0f; // Convert from chart units
                axialStrainData.Add(axialStrain);

                // If we have dimensional data, calculate lateral and volumetric strains
                if (simulationForm.HasDimensionalData())
                {
                    var dimensions = simulationForm.GetCurrentDimensions();
                    var initialDimensions = simulationForm.GetInitialDimensions();

                    double lateralStrain = (dimensions.Width - initialDimensions.Width) / initialDimensions.Width;
                    lateralStrainData.Add(lateralStrain);

                    double volumetricStrain = (dimensions.Volume - initialDimensions.Volume) / initialDimensions.Volume;
                    volumetricStrainData.Add(volumetricStrain);
                }
            }
        }
        private void ExtractDataFromParticleStates()
        {
            // Get particle state history from the simulation
            var stateHistory = simulationForm.GetParticleStateHistory();

            if (stateHistory == null || stateHistory.Count == 0)
                return;

            var initialState = stateHistory[0];
            var confiningPressure = simulationForm.GetConfiningPressure();

            foreach (var state in stateHistory)
            {
                // Calculate average stress from particle positions and forces
                double avgStress = CalculateAverageStress(state, confiningPressure);

                // Calculate strain from particle displacements
                double avgStrain = CalculateAverageStrain(state, initialState);

                // Convert to chart units and add to data
                int chartX = (int)(avgStrain * 1000); // strain * 1000
                int chartY = (int)(avgStress * 10);   // stress * 10
                stressStrainData.Add(new Point(chartX, chartY));

                // Store raw strain values
                axialStrainData.Add(avgStrain);

                // Calculate lateral and volumetric strains if possible
                if (state.BoundingBox != null && initialState.BoundingBox != null)
                {
                    double lateralStrain = (state.BoundingBox.Width - initialState.BoundingBox.Width) /
                                         initialState.BoundingBox.Width;
                    lateralStrainData.Add(lateralStrain);

                    double volumetricStrain = (state.BoundingBox.Volume - initialState.BoundingBox.Volume) /
                                            initialState.BoundingBox.Volume;
                    volumetricStrainData.Add(volumetricStrain);
                }
            }
        }
        private double CalculateAverageStress(ParticleState state, float confiningPressure)
        {
            // Calculate average deviatoric stress from particle forces
            double totalStress = 0;
            int particleCount = 0;

            foreach (var particle in state.Particles)
            {
                if (particle.IsSolid)
                {
                    // Calculate local stress from contact forces
                    double localStress = particle.ContactForces.Sum(f => f.Magnitude) / particle.Volume;
                    totalStress += localStress;
                    particleCount++;
                }
            }

            return particleCount > 0 ? totalStress / particleCount : confiningPressure;
        }

        private double CalculateAverageStrain(ParticleState currentState, ParticleState initialState)
        {
            // Calculate average axial strain from particle displacements
            double totalDisplacement = 0;
            double initialHeight = initialState.BoundingBox?.Height ?? 1.0;
            int particleCount = 0;

            for (int i = 0; i < currentState.Particles.Count && i < initialState.Particles.Count; i++)
            {
                var current = currentState.Particles[i];
                var initial = initialState.Particles[i];

                if (current.IsSolid && initial.IsSolid)
                {
                    double displacement = current.Position.Y - initial.Position.Y;
                    totalDisplacement += displacement;
                    particleCount++;
                }
            }

            return particleCount > 0 ? totalDisplacement / (particleCount * initialHeight) : 0;
        }

        private float CalibrateYoungsModulus()
        {
            if (stressStrainData.Count == 0)
                return simulationForm.GetYoungsModulus(); // Default to current value if no data

            // Extract data points in the elastic region
            var elasticData = ExtractElasticRegionData();

            if (elasticData.strains.Length < MINIMUM_POINTS_FOR_REGRESSION)
            {
                // Use early points if not enough in elastic region
                elasticData = ExtractEarlyData(MINIMUM_POINTS_FOR_REGRESSION);
            }

            // Perform linear regression using MathNet.Numerics
            var regression = SimpleRegression.Fit(elasticData.strains, elasticData.stresses);

            // The slope is Young's modulus
            float youngModulus = (float)regression.Item2;

            // Calculate R-squared to assess fit quality
            double rSquared = GoodnessOfFit.RSquared(
                elasticData.strains.Select(x => regression.Item1 + regression.Item2 * x),
                elasticData.stresses);

            // If fit is poor, try alternative method
            if (rSquared < 0.9)
            {
                youngModulus = CalculateSecantModulus(elasticData);
            }

            // Apply reasonable bounds to Young's modulus
            youngModulus = Math.Max(1000f, Math.Min(200000f, youngModulus));

            return youngModulus;
        }

        private (double[] strains, double[] stresses) ExtractElasticRegionData()
        {
            // Find the elastic limit using proportional limit method
            double elasticLimit = FindProportionalLimit();

            var strains = new List<double>();
            var stresses = new List<double>();

            foreach (var point in stressStrainData)
            {
                double strain = point.X / 1000.0;
                double stress = point.Y / 10.0;

                if (strain <= elasticLimit)
                {
                    strains.Add(strain);
                    stresses.Add(stress);
                }
            }

            return (strains.ToArray(), stresses.ToArray());
        }

        private double FindProportionalLimit()
        {
            if (stressStrainData.Count < 10)
                return 0.002; // Default elastic limit

            // Use the deviation method to find proportional limit
            const double deviationThreshold = 0.02; // 2% deviation from linearity

            // Fit line to first 5 points
            var initialStrains = new double[5];
            var initialStresses = new double[5];

            for (int i = 0; i < 5 && i < stressStrainData.Count; i++)
            {
                initialStrains[i] = stressStrainData[i].X / 1000.0;
                initialStresses[i] = stressStrainData[i].Y / 10.0;
            }

            var initialFit = SimpleRegression.Fit(initialStrains, initialStresses);
            double slope = initialFit.Item2;
            double intercept = initialFit.Item1;

            // Find where curve deviates from initial linear fit
            for (int i = 5; i < stressStrainData.Count; i++)
            {
                double strain = stressStrainData[i].X / 1000.0;
                double actualStress = stressStrainData[i].Y / 10.0;
                double predictedStress = intercept + slope * strain;

                double deviation = Math.Abs(actualStress - predictedStress) / predictedStress;

                if (deviation > deviationThreshold)
                {
                    return strain;
                }
            }

            // If no deviation found, use 0.2% as default
            return 0.002;
        }

        private float CalculateSecantModulus((double[] strains, double[] stresses) data)
        {
            // Calculate secant modulus at 50% of yield stress
            float yieldStress = CalibrateYieldStrength(simulationForm.GetYoungsModulus());
            float targetStress = yieldStress * 0.5f;

            // Find the point closest to target stress
            int closestIndex = 0;
            double minDiff = double.MaxValue;

            for (int i = 0; i < data.stresses.Length; i++)
            {
                double diff = Math.Abs(data.stresses[i] - targetStress);
                if (diff < minDiff)
                {
                    minDiff = diff;
                    closestIndex = i;
                }
            }

            // Calculate secant modulus
            if (closestIndex > 0 && data.strains[closestIndex] > 0)
            {
                return (float)(data.stresses[closestIndex] / data.strains[closestIndex]);
            }

            // Fallback to tangent at origin
            return (float)((data.stresses[1] - data.stresses[0]) / (data.strains[1] - data.strains[0]));
        }

        private (double[] strains, double[] stresses) ExtractEarlyData(int count)
        {
            count = Math.Min(count, stressStrainData.Count);
            var strains = new double[count];
            var stresses = new double[count];

            for (int i = 0; i < count; i++)
            {
                strains[i] = stressStrainData[i].X / 1000.0;
                stresses[i] = stressStrainData[i].Y / 10.0;
            }

            return (strains, stresses);
        }

        private float CalibratePoissonRatio()
        {
            // Calculate Poisson's ratio from volumetric and axial strain data
            if (lateralStrainData.Count > 0 && axialStrainData.Count > 0)
            {
                return CalculatePoissonFromStrains();
            }

            // If no direct strain data, calculate from bulk and shear moduli
            if (volumetricStrainData.Count > 0)
            {
                return CalculatePoissonFromElasticModuli();
            }

            // Fallback: estimate from material behavior
            return EstimatePoissonFromFailureMode();
        }

        private float CalculatePoissonFromStrains()
        {
            // Find elastic region data
            var elasticLimit = FindProportionalLimit();
            var elasticLateral = new List<double>();
            var elasticAxial = new List<double>();

            for (int i = 0; i < Math.Min(lateralStrainData.Count, axialStrainData.Count); i++)
            {
                if (axialStrainData[i] <= elasticLimit && axialStrainData[i] > 0)
                {
                    elasticLateral.Add(lateralStrainData[i]);
                    elasticAxial.Add(axialStrainData[i]);
                }
            }

            if (elasticLateral.Count < MINIMUM_POINTS_FOR_REGRESSION)
                return EstimatePoissonFromFailureMode();

            // Poisson's ratio = -lateral_strain / axial_strain
            // Use regression to get the best fit
            var regression = SimpleRegression.Fit(
                elasticAxial.ToArray(),
                elasticLateral.Select(x => -x).ToArray()
            );

            float poissonRatio = (float)regression.Item2;

            // Ensure within valid range [0, 0.5)
            return Math.Max(0.0f, Math.Min(0.499f, poissonRatio));
        }

        private float CalculatePoissonFromElasticModuli()
        {
            // Calculate bulk modulus from volumetric data
            double bulkModulus = CalculateBulkModulus();
            double shearModulus = CalculateShearModulus();

            if (bulkModulus <= 0 || shearModulus <= 0)
                return EstimatePoissonFromFailureMode();

            // Calculate Poisson's ratio from K and G
            // ν = (3K - 2G) / (6K + 2G)
            double numerator = 3 * bulkModulus - 2 * shearModulus;
            double denominator = 6 * bulkModulus + 2 * shearModulus;

            if (Math.Abs(denominator) < 1e-10)
                return 0.25f; // Default value

            float poissonRatio = (float)(numerator / denominator);

            // Ensure within valid range
            return Math.Max(0.0f, Math.Min(0.499f, poissonRatio));
        }

        private double CalculateBulkModulus()
        {
            if (volumetricStrainData.Count < MINIMUM_POINTS_FOR_REGRESSION)
                return 0;

            // Calculate bulk modulus from volumetric stress-strain data
            var volumetricStresses = new double[volumetricStrainData.Count];

            for (int i = 0; i < stressStrainData.Count && i < volumetricStrainData.Count; i++)
            {
                // Hydrostatic stress = (σ1 + σ2 + σ3) / 3
                double deviatoricStress = stressStrainData[i].Y / 10.0;
                double confiningPressure = simulationForm.GetConfiningPressure();
                double hydrostaticStress = confiningPressure + deviatoricStress / 3.0;
                volumetricStresses[i] = hydrostaticStress;
            }

            // Find elastic region
            var elasticLimit = FindProportionalLimit();
            var elasticVolStrain = new List<double>();
            var elasticVolStress = new List<double>();

            for (int i = 0; i < volumetricStrainData.Count; i++)
            {
                if (Math.Abs(volumetricStrainData[i]) <= elasticLimit && volumetricStrainData[i] != 0)
                {
                    elasticVolStrain.Add(volumetricStrainData[i]);
                    elasticVolStress.Add(volumetricStresses[i]);
                }
            }

            if (elasticVolStrain.Count < MINIMUM_POINTS_FOR_REGRESSION)
                return 0;

            // K = dP/dεv
            var regression = SimpleRegression.Fit(elasticVolStrain.ToArray(), elasticVolStress.ToArray());
            return regression.Item2;
        }

        private double CalculateShearModulus()
        {
            // G = E / (2(1 + ν))
            // Since we're trying to find ν, use initial estimate
            double youngModulus = simulationForm.GetYoungsModulus();
            double initialPoisson = simulationForm.GetPoissonRatio();

            return youngModulus / (2 * (1 + initialPoisson));
        }

        private float EstimatePoissonFromFailureMode()
        {
            // Analyze failure characteristics to estimate Poisson's ratio
            float currentPoissonRatio = simulationForm.GetPoissonRatio();

            // Get failure state
            float failureShearStress = simulationForm.GetFailureShearStress();
            float failureSigma1 = simulationForm.GetFailureSigma1();
            float failureSigma3 = simulationForm.GetFailureSigma3();

            // Check for dilatancy indicators
            bool showsDilatancy = CheckForDilatancy();

            // Calculate brittleness index
            float brittlenessIndex = CalculateBrittlenessIndex();

            // Adjust Poisson's ratio based on material behavior
            float adjustedRatio = currentPoissonRatio;

            if (showsDilatancy)
            {
                // Dilatant materials typically have lower Poisson's ratio
                adjustedRatio *= 0.85f;
            }

            if (brittlenessIndex > 0.7f)
            {
                // Brittle materials tend to have lower Poisson's ratio
                adjustedRatio = Math.Min(adjustedRatio, 0.25f);
            }
            else if (brittlenessIndex < 0.3f)
            {
                // Ductile materials can have higher Poisson's ratio
                adjustedRatio = Math.Max(adjustedRatio, 0.3f);
            }

            // Material-specific adjustments based on stress state
            float stressRatio = (failureSigma1 - failureSigma3) / Math.Max(0.1f, failureSigma3);

            if (stressRatio > 5.0f)
            {
                // High stress ratio indicates brittle behavior
                adjustedRatio = Math.Min(adjustedRatio, 0.2f);
            }
            else if (stressRatio < 2.0f)
            {
                // Low stress ratio indicates more ductile behavior
                adjustedRatio = Math.Max(adjustedRatio, 0.35f);
            }

            // Ensure within valid range
            return Math.Max(0.05f, Math.Min(0.499f, adjustedRatio));
        }

        private bool CheckForDilatancy()
        {
            if (volumetricStrainData.Count == 0)
                return false;

            // Check if volume increases during shearing (negative volumetric strain)
            int dilationCount = 0;
            int contractionCount = 0;

            // Look at the later stages of deformation
            int startIndex = volumetricStrainData.Count / 2;

            for (int i = startIndex; i < volumetricStrainData.Count; i++)
            {
                if (volumetricStrainData[i] < 0)
                    dilationCount++;
                else
                    contractionCount++;
            }

            return dilationCount > contractionCount;
        }

        private float CalculateBrittlenessIndex()
        {
            if (stressStrainData.Count < 5)
                return 0.5f; // Default to neutral

            // Find peak stress and corresponding strain
            int peakIndex = 0;
            int maxStress = 0;

            for (int i = 0; i < stressStrainData.Count; i++)
            {
                if (stressStrainData[i].Y > maxStress)
                {
                    maxStress = stressStrainData[i].Y;
                    peakIndex = i;
                }
            }

            // Calculate post-peak behavior
            if (peakIndex >= stressStrainData.Count - 1)
                return 0.5f; // Can't determine post-peak behavior

            // Measure stress drop and strain at failure
            float peakStress = maxStress / 10.0f;
            float finalStress = stressStrainData[stressStrainData.Count - 1].Y / 10.0f;
            float stressDrop = (peakStress - finalStress) / peakStress;

            float peakStrain = stressStrainData[peakIndex].X / 1000.0f;
            float finalStrain = stressStrainData[stressStrainData.Count - 1].X / 1000.0f;
            float postPeakStrain = finalStrain - peakStrain;

            // Brittleness index based on stress drop and post-peak strain
            float brittleness = 0.5f;

            if (stressDrop > 0.5f && postPeakStrain < 0.01f)
            {
                // Large stress drop with little post-peak strain = very brittle
                brittleness = 0.9f;
            }
            else if (stressDrop > 0.3f && postPeakStrain < 0.02f)
            {
                // Moderate stress drop with limited post-peak strain = brittle
                brittleness = 0.7f;
            }
            else if (stressDrop < 0.1f && postPeakStrain > 0.05f)
            {
                // Small stress drop with large post-peak strain = ductile
                brittleness = 0.2f;
            }
            else
            {
                // Intermediate behavior
                brittleness = 0.5f - (postPeakStrain * 2.0f) + (stressDrop * 0.5f);
            }

            return Math.Max(0.0f, Math.Min(1.0f, brittleness));
        }

        private float CalibrateYieldStrength(float youngModulus)
        {
            if (stressStrainData.Count < MINIMUM_POINTS_FOR_REGRESSION)
                return youngModulus * 0.002f; // Default to 0.2% proof stress

            // Use the 0.2% offset method as per standard practice
            float yieldStress = Calculate02PercentOffsetYield(youngModulus);

            // If offset method fails, try alternative methods
            if (yieldStress <= 0)
            {
                yieldStress = CalculateProportionalLimitYield();
            }

            if (yieldStress <= 0)
            {
                yieldStress = CalculateTangentIntersectionYield(youngModulus);
            }

            // Apply reasonable bounds based on material type
            float brittlenessIndex = CalculateBrittlenessIndex();

            if (brittlenessIndex > 0.7f)
            {
                // Brittle materials: yield close to ultimate
                float ultimateStress = stressStrainData.Max(p => p.Y) / 10.0f;
                yieldStress = Math.Max(yieldStress, ultimateStress * 0.8f);
            }
            else if (brittlenessIndex < 0.3f)
            {
                // Ductile materials: distinct yield point
                float ultimateStress = stressStrainData.Max(p => p.Y) / 10.0f;
                yieldStress = Math.Min(yieldStress, ultimateStress * 0.6f);
            }

            // Final bounds check
            yieldStress = Math.Max(youngModulus * 0.0005f, Math.Min(youngModulus * 0.01f, yieldStress));

            return yieldStress;
        }

        private float Calculate02PercentOffsetYield(float youngModulus)
        {
            // Standard 0.2% offset method
            const float offsetStrain = 0.002f;

            // Find intersection of stress-strain curve with offset line
            for (int i = 1; i < stressStrainData.Count; i++)
            {
                float strain = stressStrainData[i].X / 1000.0f;
                float stress = stressStrainData[i].Y / 10.0f;

                // Offset line: stress = E * (strain - 0.002)
                float offsetLineStress = youngModulus * (strain - offsetStrain);

                float prevStrain = stressStrainData[i - 1].X / 1000.0f;
                float prevStress = stressStrainData[i - 1].Y / 10.0f;
                float prevOffsetLineStress = youngModulus * (prevStrain - offsetStrain);

                // Check for intersection
                if ((stress - offsetLineStress) * (prevStress - prevOffsetLineStress) <= 0)
                {
                    // Linear interpolation to find exact intersection
                    float t = (prevOffsetLineStress - prevStress) /
                             ((stress - prevStress) - (offsetLineStress - prevOffsetLineStress));

                    return prevStress + t * (stress - prevStress);
                }
            }

            return -1; // No intersection found
        }

        private float CalculateProportionalLimitYield()
        {
            // Find where stress-strain curve deviates from linearity
            double proportionalLimit = FindProportionalLimit();

            // Find stress at proportional limit
            for (int i = 0; i < stressStrainData.Count; i++)
            {
                float strain = stressStrainData[i].X / 1000.0f;

                if (strain >= proportionalLimit)
                {
                    return stressStrainData[i].Y / 10.0f;
                }
            }

            return -1;
        }

        private float CalculateTangentIntersectionYield(float youngModulus)
        {
            // Find yield by intersection of elastic tangent with ultimate tangent
            if (stressStrainData.Count < 10)
                return -1;

            // Find ultimate stress point
            int ultimateIndex = 0;
            int maxStress = 0;

            for (int i = 0; i < stressStrainData.Count; i++)
            {
                if (stressStrainData[i].Y > maxStress)
                {
                    maxStress = stressStrainData[i].Y;
                    ultimateIndex = i;
                }
            }

            // Calculate tangent at ultimate
            if (ultimateIndex < 3 || ultimateIndex > stressStrainData.Count - 3)
                return -1;

            double ultimateSlope = CalculateLocalSlope(ultimateIndex);
            double ultimateStrain = stressStrainData[ultimateIndex].X / 1000.0;
            double ultimateStress = stressStrainData[ultimateIndex].Y / 10.0;

            // Find intersection of elastic line with ultimate tangent
            // Elastic line: stress = E * strain
            // Ultimate tangent: stress = ultimateStress + ultimateSlope * (strain - ultimateStrain)

            double intersectionStrain = (ultimateStress - ultimateSlope * ultimateStrain) /
                                       (youngModulus - ultimateSlope);

            if (intersectionStrain < 0 || intersectionStrain > ultimateStrain)
                return -1;

            return (float)(youngModulus * intersectionStrain);
        }

        private double CalculateLocalSlope(int index)
        {
            // Calculate slope using neighboring points
            int startIndex = Math.Max(0, index - 2);
            int endIndex = Math.Min(stressStrainData.Count - 1, index + 2);

            if (endIndex - startIndex < 2)
                return 0;

            double[] strains = new double[endIndex - startIndex + 1];
            double[] stresses = new double[endIndex - startIndex + 1];

            for (int i = startIndex; i <= endIndex; i++)
            {
                strains[i - startIndex] = stressStrainData[i].X / 1000.0;
                stresses[i - startIndex] = stressStrainData[i].Y / 10.0;
            }

            var regression = SimpleRegression.Fit(strains, stresses);
            return regression.Item2;
        }

        private float CalibrateBrittleStrength()
        {
            if (stressStrainData.Count == 0)
                return simulationForm.GetYoungsModulus() * 0.005f;

            // Get peak stress from stress-strain curve
            float peakStress = stressStrainData.Max(p => p.Y) / 10.0f;

            // Analyze failure characteristics
            float brittlenessIndex = CalculateBrittlenessIndex();

            // For brittle materials, tensile strength is close to peak
            // For ductile materials, it's higher than peak
            float strengthMultiplier = 1.0f + (1.0f - brittlenessIndex) * 0.2f;

            float brittleStrength = peakStress * strengthMultiplier;

            // Check for strain softening behavior
            if (HasStrainSoftening())
            {
                // For strain-softening materials, add extra margin
                brittleStrength *= 1.1f;
            }

            // Apply reasonable bounds based on Young's modulus
            float youngModulus = simulationForm.GetYoungsModulus();
            brittleStrength = Math.Max(youngModulus * 0.001f, Math.Min(youngModulus * 0.02f, brittleStrength));

            return brittleStrength;
        }

        private bool HasStrainSoftening()
        {
            // Check if material shows strain softening (decreasing stress with increasing strain)
            int peakIndex = 0;
            int maxStress = 0;

            for (int i = 0; i < stressStrainData.Count; i++)
            {
                if (stressStrainData[i].Y > maxStress)
                {
                    maxStress = stressStrainData[i].Y;
                    peakIndex = i;
                }
            }

            // Check post-peak behavior
            if (peakIndex >= stressStrainData.Count - 3)
                return false;

            // Count decreasing stress points after peak
            int softeningPoints = 0;
            for (int i = peakIndex + 1; i < stressStrainData.Count - 1; i++)
            {
                if (stressStrainData[i + 1].Y < stressStrainData[i].Y)
                    softeningPoints++;
            }

            return softeningPoints > (stressStrainData.Count - peakIndex - 1) / 2;
        }

        private void CalibrateMohrCoulombParameters(CalibrationParameters parameters)
        {
            
            float failureShearStress = simulationForm.GetFailureShearStress();
            float failureSigma1 = simulationForm.GetFailureSigma1();
            float failureSigma3 = simulationForm.GetFailureSigma3();

            // Calculate Mohr circle parameters
            float radius = (failureSigma1 - failureSigma3) / 2.0f;
            float center = (failureSigma1 + failureSigma3) / 2.0f;

            // If we have multiple tests, perform linear regression
            var failurePoints = simulationForm.GetMultipleFailurePoints();

            if (failurePoints != null && failurePoints.Count >= 3)
            {
                // Multiple test calibration
                CalibrateFromMultipleTests(parameters, failurePoints);
            }
            else
            {
                // Single test calibration
                CalibrateFromSingleTest(parameters, radius, center, failureShearStress);
            }

            // Apply reasonable bounds
            parameters.FrictionAngle = Math.Max(15f, Math.Min(55f, parameters.FrictionAngle));
            parameters.Cohesion = Math.Max(0.1f, Math.Min(100f, parameters.Cohesion));
        }

        private void CalibrateFromMultipleTests(CalibrationParameters parameters, List<FailurePoint> failurePoints)
        {
            // Extract normal and shear stresses at failure
            double[] normalStresses = new double[failurePoints.Count];
            double[] shearStresses = new double[failurePoints.Count];

            for (int i = 0; i < failurePoints.Count; i++)
            {
                normalStresses[i] = (failurePoints[i].Sigma1 + failurePoints[i].Sigma3) / 2.0;
                shearStresses[i] = (failurePoints[i].Sigma1 - failurePoints[i].Sigma3) / 2.0;
            }

            // Linear regression: τ = c + σn * tan(φ)
            var regression = SimpleRegression.Fit(normalStresses, shearStresses);

            parameters.Cohesion = (float)regression.Item1;
            parameters.FrictionAngle = (float)(Math.Atan(regression.Item2) * 180.0 / Math.PI);

            // Check goodness of fit
            double rSquared = GoodnessOfFit.RSquared(
                normalStresses.Select(x => regression.Item1 + regression.Item2 * x),
                shearStresses);

            if (rSquared < 0.9)
            {
                // Poor linear fit, try power law or other models
                CalibrateNonlinearFailureCriterion(parameters, normalStresses, shearStresses);
            }
        }

        private void CalibrateFromSingleTest(CalibrationParameters parameters, float radius, float center, float shearStress)
        {
            // For single test, estimate based on stress path and failure mode

            // Initial estimate using Mohr-Coulomb theory
            float sinPhi = radius / (center + radius * 0.1f); // Add small factor for stability
            float phi = (float)(Math.Asin(Math.Min(1.0, sinPhi)) * 180.0 / Math.PI);

            // Estimate cohesion
            float cosPhi = (float)Math.Cos(phi * Math.PI / 180.0);
            float cohesion = (radius - center * sinPhi) / cosPhi;

            // Adjust based on material behavior
            float brittlenessIndex = CalculateBrittlenessIndex();

            if (brittlenessIndex > 0.7f)
            {
                // Brittle materials typically have higher friction angle
                phi *= 1.1f;
                cohesion *= 0.9f;
            }
            else if (brittlenessIndex < 0.3f)
            {
                // Ductile materials may have lower friction angle
                phi *= 0.9f;
                cohesion *= 1.1f;
            }

            // Check for tension cutoff
            if (HasTensionCutoff())
            {
                // Materials with tension cutoff need adjusted parameters
                float tensileStrength = parameters.BrittleStrength;
                cohesion = Math.Max(cohesion, tensileStrength / (2.0f * cosPhi));
            }

            parameters.FrictionAngle = phi;
            parameters.Cohesion = cohesion;
        }

        private void CalibrateNonlinearFailureCriterion(CalibrationParameters parameters,
                                                       double[] normalStresses,
                                                       double[] shearStresses)
        {
            // Try power law: τ = A * σn^B
            var logNormal = normalStresses.Select(x => Math.Log(Math.Max(0.01, x))).ToArray();
            var logShear = shearStresses.Select(x => Math.Log(Math.Max(0.01, x))).ToArray();

            var powerRegression = SimpleRegression.Fit(logNormal, logShear);

            double A = Math.Exp(powerRegression.Item1);
            double B = powerRegression.Item2;

            // Convert to equivalent linear parameters at mean stress
            double meanNormal = normalStresses.Average();
            double tanPhi = B * A * Math.Pow(meanNormal, B - 1);
            double cohesion = A * Math.Pow(meanNormal, B) - tanPhi * meanNormal;

            parameters.FrictionAngle = (float)(Math.Atan(tanPhi) * 180.0 / Math.PI);
            parameters.Cohesion = (float)Math.Max(0, cohesion);
        }

        private bool HasTensionCutoff()
        {
            // Check if material fails in tension at lower stress than predicted by Mohr-Coulomb
            var tensileTests = simulationForm.GetTensileTestResults();

            if (tensileTests == null || tensileTests.Count == 0)
            {
                // Estimate from failure mode
                return CalculateBrittlenessIndex() > 0.6f;
            }

            // Compare tensile strength with Mohr-Coulomb prediction
            float measuredTensile = tensileTests.Average();
            float predictedTensile = simulationForm.GetCohesion() /
                                   (float)Math.Tan(simulationForm.GetFrictionAngle() * Math.PI / 180.0);

            return measuredTensile < predictedTensile * 0.8f;
        }
    }

    // Supporting classes/structs
    public class ParticleState
    {
        public List<Particle> Particles { get; set; }
        public BoundingBox BoundingBox { get; set; }
    }

    public class Particle
    {
        public Vector3 Position { get; set; }
        public bool IsSolid { get; set; }
        public double Volume { get; set; }
        public List<ContactForce> ContactForces { get; set; }
    }

    public class ContactForce
    {
        public double Magnitude { get; set; }
        public Vector3 Direction { get; set; }
    }

    public class BoundingBox
    {
        public double Width { get; set; }
        public double Height { get; set; }
        public double Depth { get; set; }
        public double Volume => Width * Height * Depth;
    }

    public class FailurePoint
    {
        public float Sigma1 { get; set; }
        public float Sigma3 { get; set; }
        public float ConfiningPressure { get; set; }
    }

    public class CalibrationParameters
    {
        public float YoungModulus { get; set; }
        public float PoissonRatio { get; set; }
        public float YieldStrength { get; set; }
        public float BrittleStrength { get; set; }
        public float FrictionAngle { get; set; }
        public float Cohesion { get; set; }
    }
}