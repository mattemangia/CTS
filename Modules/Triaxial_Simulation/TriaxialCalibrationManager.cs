using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using CTS;
using MathNet.Numerics;
using MathNet.Numerics.LinearRegression;

namespace CTS.Modules.Triaxial_Simulation
{
    public class TriaxialCalibrationManager
    {
        private TriaxialSimulationForm simulationForm;

        // Stress-strain data from simulation
        private List<Point> stressStrainData = new List<Point>();

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

            // We could get this from the form's stressStrainCurve
            // For now, we'll rebuild it from the simulation's history

            // Get failure state from last simulation
            float failureShearStress = simulationForm.GetFailureShearStress();
            float failureSigma1 = simulationForm.GetFailureSigma1();
            float failureSigma3 = simulationForm.GetFailureSigma3();
            float failureStrain = simulationForm.GetFailureStrain();

            // For a more realistic calibration, we would use the full stress-strain curve
            // However, for demonstration, we'll use derived points

            // Generate points from linear elastic up to failure
            const int numPoints = 20;
            for (int i = 0; i < numPoints; i++)
            {
                float strain = failureStrain * (i / (float)(numPoints - 1));
                float stress;

                // Linear elastic region (first third)
                if (i < numPoints / 3)
                {
                    stress = simulationForm.GetYoungsModulus() * strain;
                }
                // Hardening region (middle third)
                else if (i < 2 * numPoints / 3)
                {
                    float elasticPortion = simulationForm.GetYoungsModulus() * (failureStrain / 3);
                    float hardeningPortion = (failureSigma1 - failureSigma3 - elasticPortion) *
                                            ((i - numPoints / 3) / (float)(numPoints / 3));
                    stress = elasticPortion + hardeningPortion;
                }
                // Failure region (last third)
                else
                {
                    float softeningFactor = 1.0f - 0.2f * ((i - 2 * numPoints / 3) / (float)(numPoints / 3));
                    stress = (failureSigma1 - failureSigma3) * softeningFactor;
                }

                // X = strain*1000 (in strain percentage * 10), Y = stress*10 (in MPa * 10)
                stressStrainData.Add(new Point((int)(strain * 1000), (int)(stress * 10)));
            }
        }

        private float CalibrateYoungsModulus()
        {
            if (stressStrainData.Count == 0)
                return simulationForm.GetYoungsModulus(); // Default to current value if no data

            // Get points only in the elastic region (up to a percentage of expected yield strain)
            float estimatedYieldStrain = 0.003f; // Typical yield strain for many materials

            // Convert to arrays for regression analysis
            double[] strains = new double[stressStrainData.Count];
            double[] stresses = new double[stressStrainData.Count];
            int elasticPointCount = 0;

            for (int i = 0; i < stressStrainData.Count; i++)
            {
                // Convert from form's units
                float strain = stressStrainData[i].X / 1000.0f;
                float stress = stressStrainData[i].Y / 10.0f;

                // Only use points in elastic region
                if (strain <= estimatedYieldStrain * ELASTIC_ZONE_LIMIT)
                {
                    strains[elasticPointCount] = strain;
                    stresses[elasticPointCount] = stress;
                    elasticPointCount++;
                }
            }

            // Ensure we have enough points for regression
            if (elasticPointCount < MINIMUM_POINTS_FOR_REGRESSION)
            {
                // If not enough points in elastic region, use all available points
                elasticPointCount = Math.Min(MINIMUM_POINTS_FOR_REGRESSION, stressStrainData.Count);
                for (int i = 0; i < elasticPointCount; i++)
                {
                    strains[i] = stressStrainData[i].X / 1000.0f;
                    stresses[i] = stressStrainData[i].Y / 10.0f;
                }
            }

            // Perform linear regression (stress = E * strain)
            // MathNet.Numerics.LinearRegression is abstracted here, but would be used in real implementation
            // We're calculating the slope with proper regression analysis
            double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
            for (int i = 0; i < elasticPointCount; i++)
            {
                sumX += strains[i];
                sumY += stresses[i];
                sumXY += strains[i] * stresses[i];
                sumX2 += strains[i] * strains[i];
            }

            // Calculate slope (Young's modulus)
            double slope;
            if (Math.Abs(elasticPointCount * sumX2 - sumX * sumX) < 1e-10)
            {
                // Avoid division by zero
                slope = simulationForm.GetYoungsModulus();
            }
            else
            {
                slope = (elasticPointCount * sumXY - sumX * sumY) / (elasticPointCount * sumX2 - sumX * sumX);
            }

            // Apply reasonable bounds to Young's modulus
            float youngModulus = (float)slope;
            youngModulus = Math.Max(1000f, Math.Min(200000f, youngModulus));

            return youngModulus;
        }

        private float CalibratePoissonRatio()
        {
            // This would ideally use lateral strain measurements
            // In absence of that data, we'll estimate based on material type

            float currentPoissonRatio = simulationForm.GetPoissonRatio();

            // In real implementation, we'd use volumetric strain data and calculations
            // For this demonstration, we'll use the current value with minor adjustment
            // based on the stress state

            float failureShearStress = simulationForm.GetFailureShearStress();
            float failureSigma1 = simulationForm.GetFailureSigma1();
            float failureSigma3 = simulationForm.GetFailureSigma3();

            // Calculate stress ratio (σ1-σ3)/σ3
            float stressRatio = (failureSigma1 - failureSigma3) / Math.Max(0.1f, failureSigma3);

            // Adjust Poisson's ratio based on stress ratio
            // Higher stress ratios typically indicate more dilatant behavior (lower Poisson's ratio)
            float adjustedPoissonRatio = currentPoissonRatio;

            if (stressRatio > 4.0f)
            {
                // Highly dilatant material
                adjustedPoissonRatio = Math.Max(0.15f, currentPoissonRatio - 0.05f);
            }
            else if (stressRatio < 2.0f)
            {
                // More compressible material
                adjustedPoissonRatio = Math.Min(0.45f, currentPoissonRatio + 0.03f);
            }

            // Ensure within valid range
            adjustedPoissonRatio = Math.Max(0.05f, Math.Min(0.49f, adjustedPoissonRatio));

            return adjustedPoissonRatio;
        }

        private float CalibrateYieldStrength(float youngModulus)
        {
            if (stressStrainData.Count < MINIMUM_POINTS_FOR_REGRESSION)
                return simulationForm.GetYoungsModulus() * 0.05f; // Default to 5% of E if no data

            // Use the 0.2% offset method to determine yield

            // First, get the stress-strain data in proper units
            double[] strains = new double[stressStrainData.Count];
            double[] stresses = new double[stressStrainData.Count];

            for (int i = 0; i < stressStrainData.Count; i++)
            {
                strains[i] = stressStrainData[i].X / 1000.0f;  // Convert to strain
                stresses[i] = stressStrainData[i].Y / 10.0f;   // Convert to MPa
            }

            // Calculate offset line: stress = E * (strain - 0.002)
            float yieldStress = 0;
            bool foundYield = false;

            // Search through data points to find where stress-strain curve intersects offset line
            for (int i = 1; i < stressStrainData.Count; i++)
            {
                float strain = (float)strains[i];
                float stress = (float)stresses[i];
                float offsetLineStress = youngModulus * (strain - YIELD_OFFSET_STRAIN);

                float prevStrain = (float)strains[i - 1];
                float prevStress = (float)stresses[i - 1];
                float prevOffsetLineStress = youngModulus * (prevStrain - YIELD_OFFSET_STRAIN);

                // Check if stress-strain curve crosses offset line
                bool curAbove = stress >= offsetLineStress;
                bool prevAbove = prevStress >= prevOffsetLineStress;

                if (curAbove != prevAbove)
                {
                    // Crossing found - linear interpolate to get precise yield point
                    float t = (prevOffsetLineStress - prevStress) / ((stress - prevStress) - (offsetLineStress - prevOffsetLineStress));
                    float yieldStrain = prevStrain + t * (strain - prevStrain);
                    yieldStress = prevStress + t * (stress - prevStress);
                    foundYield = true;
                    break;
                }
            }

            // If yield point wasn't found with offset method, estimate from peak
            if (!foundYield)
            {
                // Get peak stress
                float peakStress = stressStrainData.Max(p => p.Y) / 10.0f;

                // Estimate yield at 70% of peak for brittle materials, 85% for ductile
                bool isDuctile = stressStrainData.Count > 10 &&
                                 stressStrainData[stressStrainData.Count - 1].Y > 0.8f * stressStrainData.Max(p => p.Y);

                yieldStress = isDuctile ? peakStress * 0.85f : peakStress * 0.70f;
            }

            // Apply reasonable bounds
            yieldStress = Math.Max(youngModulus * 0.001f, Math.Min(youngModulus * 0.2f, yieldStress));

            return yieldStress;
        }

        private float CalibrateBrittleStrength()
        {
            if (stressStrainData.Count == 0)
                return simulationForm.GetYoungsModulus() * 0.08f; // Default to 8% of E if no data

            // Get peak stress from stress-strain curve
            float peakStress = stressStrainData.Max(p => p.Y) / 10.0f;

            // Add safety margin to peak stress
            float brittleStrength = peakStress * 1.05f;

            // Check post-peak behavior to determine if truly brittle
            int peakIndex = 0;
            int maxY = stressStrainData.Max(p => p.Y);

            for (int i = 0; i < stressStrainData.Count; i++)
            {
                if (stressStrainData[i].Y == maxY)
                {
                    peakIndex = i;
                    break;
                }
            }

            // Check if there's significant post-peak data and if it shows brittle behavior
            if (peakIndex < stressStrainData.Count - 3)
            {
                // Calculate post-peak stress drop
                float postPeakStress = stressStrainData[peakIndex + 3].Y / 10.0f;
                float stressDrop = (peakStress - postPeakStress) / peakStress;

                // If significant stress drop, add more margin to brittle strength
                if (stressDrop > 0.2f)
                {
                    brittleStrength = peakStress * 1.1f;
                }
            }

            // Apply reasonable bounds
            float youngModulus = simulationForm.GetYoungsModulus();
            brittleStrength = Math.Max(youngModulus * 0.005f, Math.Min(youngModulus * 0.3f, brittleStrength));

            return brittleStrength;
        }

        private void CalibrateMohrCoulombParameters(CalibrationParameters parameters)
        {
            // Get failure state from simulation
            float failureShearStress = simulationForm.GetFailureShearStress();
            float failureSigma1 = simulationForm.GetFailureSigma1();
            float failureSigma3 = simulationForm.GetFailureSigma3();

            // Ideally, we'd have multiple tests at different confining pressures
            // For now, we'll use the single failure point and material properties

            // Calculate friction angle from stress ratio at failure
            float slopeM = failureShearStress / ((failureSigma1 + failureSigma3) / 2);
            float frictionAngle = (float)(Math.Asin(slopeM) * 180.0 / Math.PI);

            // Calculate cohesion using Mohr-Coulomb criterion
            float frictionRad = frictionAngle * (float)(Math.PI / 180.0);
            float sinPhi = (float)Math.Sin(frictionRad);
            float cosPhi = (float)Math.Cos(frictionRad);

            // c = (σ1 - σ3) / (2 * cos(φ)) - (σ1 + σ3) * tan(φ) / 2
            float cohesion = (failureSigma1 - failureSigma3) / (2 * cosPhi);
            cohesion -= (failureSigma1 + failureSigma3) * sinPhi / (2 * cosPhi);

            // Apply reasonable bounds
            frictionAngle = Math.Max(10f, Math.Min(60f, frictionAngle));
            cohesion = Math.Max(parameters.YieldStrength * 0.05f, Math.Min(parameters.YieldStrength, cohesion));

            // Store calibrated values
            parameters.FrictionAngle = frictionAngle;
            parameters.Cohesion = cohesion;
        }
    }
}
