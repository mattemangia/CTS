//Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Xml.Serialization;
using System.Text.Json;

namespace CTS.Modules.Simulation.NMR
{
    /// <summary>
    /// Represents a single calibration point
    /// </summary>
    [Serializable]
    public class CalibrationPoint
    {
        public double SimulatedT2 { get; set; }
        public double SimulatedAmplitude { get; set; }
        public double ReferenceT2 { get; set; }
        public double ReferenceAmplitude { get; set; }
        public string Description { get; set; }
        public DateTime CreatedDate { get; set; }

        public CalibrationPoint()
        {
            CreatedDate = DateTime.Now;
        }

        public CalibrationPoint(double simT2, double simAmplitude, double refT2, double refAmplitude, string description = "")
        {
            SimulatedT2 = simT2;
            SimulatedAmplitude = simAmplitude;
            ReferenceT2 = refT2;
            ReferenceAmplitude = refAmplitude;
            Description = description;
            CreatedDate = DateTime.Now;
        }
    }

    /// <summary>
    /// Represents calibration metadata
    /// </summary>
    [Serializable]
    public class CalibrationMetadata
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public DateTime CreatedDate { get; set; }
        public DateTime LastModified { get; set; }
        public string Author { get; set; }
        public string Laboratory { get; set; }
        public string InstrumentType { get; set; }
        public double TemperatureC { get; set; }
        public double PressureMPa { get; set; }
        public double FieldStrengthT { get; set; }
        public string SampleDescription { get; set; }

        public CalibrationMetadata()
        {
            CreatedDate = DateTime.Now;
            LastModified = DateTime.Now;
            TemperatureC = 25.0;
            PressureMPa = 0.101325;
            FieldStrengthT = 1.0;
        }
    }

    /// <summary>
    /// NMR calibration system for converting simulated values to lab/field measurements
    /// </summary>
    public class NMRCalibration
    {
        public CalibrationMetadata Metadata { get; set; }
        public List<CalibrationPoint> CalibrationPoints { get; set; }
        public bool IsCalibrated => CalibrationPoints != null && CalibrationPoints.Count >= 2;

        // Calibration functions
        private Func<double, double> _t2TransformFunction;
        private Func<double, double> _amplitudeTransformFunction;

        // Calibration statistics
        public double T2CalibrationR2 { get; private set; }
        public double AmplitudeCalibrationR2 { get; private set; }
        public double T2RMSE { get; private set; }
        public double AmplitudeRMSE { get; private set; }

        public NMRCalibration()
        {
            Metadata = new CalibrationMetadata();
            CalibrationPoints = new List<CalibrationPoint>();
        }

        public void AddCalibrationPoint(double simT2, double simAmplitude, double refT2, double refAmplitude, string description = "")
        {
            var point = new CalibrationPoint(simT2, simAmplitude, refT2, refAmplitude, description);
            CalibrationPoints.Add(point);
            Metadata.LastModified = DateTime.Now;

            // Recalculate calibration functions if we have enough points
            if (CalibrationPoints.Count >= 2)
            {
                CalculateCalibrationFunctions();
            }
        }

        public void RemoveCalibrationPoint(CalibrationPoint point)
        {
            CalibrationPoints.Remove(point);
            Metadata.LastModified = DateTime.Now;

            // Recalculate calibration functions
            if (CalibrationPoints.Count >= 2)
            {
                CalculateCalibrationFunctions();
            }
            else
            {
                // Reset functions if not enough points
                _t2TransformFunction = null;
                _amplitudeTransformFunction = null;
            }
        }

        public void UpdateCalibrationPoint(int index, double simT2, double simAmplitude, double refT2, double refAmplitude)
        {
            if (index >= 0 && index < CalibrationPoints.Count)
            {
                CalibrationPoints[index].SimulatedT2 = simT2;
                CalibrationPoints[index].SimulatedAmplitude = simAmplitude;
                CalibrationPoints[index].ReferenceT2 = refT2;
                CalibrationPoints[index].ReferenceAmplitude = refAmplitude;
                Metadata.LastModified = DateTime.Now;

                // Recalculate calibration functions
                if (CalibrationPoints.Count >= 2)
                {
                    CalculateCalibrationFunctions();
                }
            }
        }

        private void CalculateCalibrationFunctions()
        {
            if (CalibrationPoints.Count < 2)
                return;

            // Calculate T2 calibration
            CalculateT2Calibration();

            // Calculate amplitude calibration
            CalculateAmplitudeCalibration();
        }

        private void CalculateT2Calibration()
        {
            var simValues = CalibrationPoints.Select(p => Math.Log10(p.SimulatedT2)).ToArray();
            var refValues = CalibrationPoints.Select(p => Math.Log10(p.ReferenceT2)).ToArray();

            // Perform linear regression in log space
            var (slope, intercept, r2, rmse) = PerformLinearRegression(simValues, refValues);

            // Store statistics
            T2CalibrationR2 = r2;
            T2RMSE = rmse;

            // Create transformation function
            _t2TransformFunction = (simT2) =>
            {
                double logSimT2 = Math.Log10(simT2);
                double logRefT2 = slope * logSimT2 + intercept;
                return Math.Pow(10, logRefT2);
            };
        }

        private void CalculateAmplitudeCalibration()
        {
            var simValues = CalibrationPoints.Select(p => p.SimulatedAmplitude).ToArray();
            var refValues = CalibrationPoints.Select(p => p.ReferenceAmplitude).ToArray();

            // For amplitude, try both linear and power law relationships
            var linearResult = PerformLinearRegression(simValues, refValues);

            // Try power law (log-log regression)
            var logSimValues = simValues.Select(v => Math.Log10(Math.Max(v, 1e-10))).ToArray();
            var logRefValues = refValues.Select(v => Math.Log10(Math.Max(v, 1e-10))).ToArray();
            var logResult = PerformLinearRegression(logSimValues, logRefValues);

            // Choose the better fit
            if (logResult.r2 > linearResult.r2)
            {
                // Use power law
                AmplitudeCalibrationR2 = logResult.r2;
                AmplitudeRMSE = logResult.rmse;

                _amplitudeTransformFunction = (simAmp) =>
                {
                    double logSimAmp = Math.Log10(Math.Max(simAmp, 1e-10));
                    double logRefAmp = logResult.slope * logSimAmp + logResult.intercept;
                    return Math.Pow(10, logRefAmp);
                };
            }
            else
            {
                // Use linear
                AmplitudeCalibrationR2 = linearResult.r2;
                AmplitudeRMSE = linearResult.rmse;

                _amplitudeTransformFunction = (simAmp) =>
                {
                    return linearResult.slope * simAmp + linearResult.intercept;
                };
            }
        }

        private (double slope, double intercept, double r2, double rmse) PerformLinearRegression(double[] x, double[] y)
        {
            int n = x.Length;
            double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0, sumY2 = 0;

            for (int i = 0; i < n; i++)
            {
                sumX += x[i];
                sumY += y[i];
                sumXY += x[i] * y[i];
                sumX2 += x[i] * x[i];
                sumY2 += y[i] * y[i];
            }

            double slope = (n * sumXY - sumX * sumY) / (n * sumX2 - sumX * sumX);
            double intercept = (sumY - slope * sumX) / n;

            // Calculate R²
            double meanY = sumY / n;
            double ssTot = 0, ssRes = 0;

            for (int i = 0; i < n; i++)
            {
                double predicted = slope * x[i] + intercept;
                ssTot += (y[i] - meanY) * (y[i] - meanY);
                ssRes += (y[i] - predicted) * (y[i] - predicted);
            }

            double r2 = ssTot > 0 ? 1 - (ssRes / ssTot) : 0;
            double rmse = Math.Sqrt(ssRes / n);

            return (slope, intercept, r2, rmse);
        }

        public double TransformT2(double simulatedT2)
        {
            if (_t2TransformFunction == null)
                return simulatedT2;

            return _t2TransformFunction(simulatedT2);
        }

        public double TransformAmplitude(double simulatedAmplitude)
        {
            if (_amplitudeTransformFunction == null)
                return simulatedAmplitude;

            return _amplitudeTransformFunction(simulatedAmplitude);
        }

        public void SaveToFile(string filePath)
        {
            var calibrationData = new
            {
                Metadata,
                CalibrationPoints,
                Statistics = new
                {
                    T2CalibrationR2,
                    AmplitudeCalibrationR2,
                    T2RMSE,
                    AmplitudeRMSE
                }
            };

            try
            {
                // Use JSON for modern format
                string json = JsonSerializer.Serialize(calibrationData, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });

                File.WriteAllText(filePath, json);
                Logger.Log($"[NMRCalibration] Saved calibration to {filePath}");
            }
            catch (Exception ex)
            {
                Logger.Log($"[NMRCalibration] Error saving calibration: {ex.Message}");
                throw;
            }
        }

        public static NMRCalibration LoadFromFile(string filePath)
        {
            try
            {
                string json = File.ReadAllText(filePath);

                // Parse JSON
                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;

                    var calibration = new NMRCalibration();

                    // Load metadata
                    if (root.TryGetProperty("Metadata", out var metadataElement))
                    {
                        calibration.Metadata = JsonSerializer.Deserialize<CalibrationMetadata>(metadataElement.GetRawText());
                    }

                    // Load calibration points
                    if (root.TryGetProperty("CalibrationPoints", out var pointsElement))
                    {
                        calibration.CalibrationPoints = JsonSerializer.Deserialize<List<CalibrationPoint>>(pointsElement.GetRawText());
                    }

                    // Load statistics
                    if (root.TryGetProperty("Statistics", out var statsElement))
                    {
                        if (statsElement.TryGetProperty("T2CalibrationR2", out var r2Element))
                            calibration.T2CalibrationR2 = r2Element.GetDouble();
                        if (statsElement.TryGetProperty("AmplitudeCalibrationR2", out var ampR2Element))
                            calibration.AmplitudeCalibrationR2 = ampR2Element.GetDouble();
                        if (statsElement.TryGetProperty("T2RMSE", out var rmseElement))
                            calibration.T2RMSE = rmseElement.GetDouble();
                        if (statsElement.TryGetProperty("AmplitudeRMSE", out var ampRmseElement))
                            calibration.AmplitudeRMSE = ampRmseElement.GetDouble();
                    }

                    // Recalculate calibration functions
                    if (calibration.CalibrationPoints.Count >= 2)
                    {
                        calibration.CalculateCalibrationFunctions();
                    }

                    Logger.Log($"[NMRCalibration] Loaded calibration from {filePath}");
                    return calibration;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[NMRCalibration] Error loading calibration: {ex.Message}");
                throw;
            }
        }

        public void ImportFromNMRLog(string logFilePath, ImportFormat format)
        {
            switch (format)
            {
                case ImportFormat.CSVFormat:
                    ImportFromCSV(logFilePath);
                    break;
                case ImportFormat.ASCIILog:
                    ImportFromASCIILog(logFilePath);
                    break;
                case ImportFormat.BinaryLog:
                    ImportFromBinaryLog(logFilePath);
                    break;
                default:
                    throw new ArgumentException($"Unsupported import format: {format}");
            }

            // Recalculate calibration after import
            if (CalibrationPoints.Count >= 2)
            {
                CalculateCalibrationFunctions();
            }
        }

        private void ImportFromCSV(string csvPath)
        {
            var lines = File.ReadAllLines(csvPath);
            bool headerParsed = false;

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#"))
                    continue;

                if (!headerParsed)
                {
                    // Skip header line
                    headerParsed = true;
                    continue;
                }

                var parts = line.Split(',');
                if (parts.Length >= 4)
                {
                    if (double.TryParse(parts[0], out double simT2) &&
                        double.TryParse(parts[1], out double simAmp) &&
                        double.TryParse(parts[2], out double refT2) &&
                        double.TryParse(parts[3], out double refAmp))
                    {
                        string description = parts.Length > 4 ? parts[4] : "";
                        AddCalibrationPoint(simT2, simAmp, refT2, refAmp, description);
                    }
                }
            }
        }

        private void ImportFromASCIILog(string logPath)
        {
            var lines = File.ReadAllLines(logPath);
            bool inDataSection = false;

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();

                if (line.Contains("T2 DISTRIBUTION") || line.Contains("RELAXATION DATA"))
                {
                    inDataSection = true;
                    continue;
                }

                if (inDataSection && !string.IsNullOrEmpty(line))
                {
                    // Parse different ASCII log formats
                    if (TryParseLogLine(line, out CalibrationPoint point))
                    {
                        CalibrationPoints.Add(point);
                    }
                }
            }
        }

        private bool TryParseLogLine(string line, out CalibrationPoint point)
        {
            point = null;

            // Try various common log formats
            var patterns = new[]
            {
                // Format: T2(ms) Amplitude
                @"(\d+(?:\.\d+)?)\s+(\d+(?:\.\d+)?)",
                
                // Format: T2: 123.45 ms, Signal: 0.567
                @"T2:\s*(\d+(?:\.\d+)?)\s*ms,?\s*(?:Signal|Amplitude):\s*(\d+(?:\.\d+)?)",
                
                // Format: [123.45, 0.567]
                @"\[(\d+(?:\.\d+)?),\s*(\d+(?:\.\d+)?)\]"
            };

            foreach (var pattern in patterns)
            {
                var match = System.Text.RegularExpressions.Regex.Match(line, pattern);
                if (match.Success)
                {
                    if (double.TryParse(match.Groups[1].Value, out double t2) &&
                        double.TryParse(match.Groups[2].Value, out double amplitude))
                    {
                        // For import, we use the same values for simulated and reference initially
                        point = new CalibrationPoint(t2, amplitude, t2, amplitude, "Imported from log");
                        return true;
                    }
                }
            }

            return false;
        }

        private void ImportFromBinaryLog(string logPath)
        {
            using (var reader = new BinaryReader(File.OpenRead(logPath)))
            {
                // Read header
                int numPoints = reader.ReadInt32();

                // Read data points
                for (int i = 0; i < numPoints; i++)
                {
                    double t2 = reader.ReadDouble();
                    double amplitude = reader.ReadDouble();

                    AddCalibrationPoint(t2, amplitude, t2, amplitude, "Imported from binary log");
                }
            }
        }

        public void ExportToBinary(string outputPath)
        {
            using (var writer = new BinaryWriter(File.Create(outputPath)))
            {
                // Write header
                writer.Write(CalibrationPoints.Count);

                // Write metadata
                writer.Write(Metadata.Name ?? "");
                writer.Write(Metadata.Description ?? "");
                writer.Write(Metadata.CreatedDate.ToBinary());
                writer.Write(Metadata.FieldStrengthT);
                writer.Write(Metadata.TemperatureC);

                // Write calibration points
                foreach (var point in CalibrationPoints)
                {
                    writer.Write(point.SimulatedT2);
                    writer.Write(point.SimulatedAmplitude);
                    writer.Write(point.ReferenceT2);
                    writer.Write(point.ReferenceAmplitude);
                    writer.Write(point.Description ?? "");
                }

                // Write statistics
                writer.Write(T2CalibrationR2);
                writer.Write(AmplitudeCalibrationR2);
                writer.Write(T2RMSE);
                writer.Write(AmplitudeRMSE);
            }
        }

        public void Reset()
        {
            CalibrationPoints.Clear();
            _t2TransformFunction = null;
            _amplitudeTransformFunction = null;
            T2CalibrationR2 = 0;
            AmplitudeCalibrationR2 = 0;
            T2RMSE = 0;
            AmplitudeRMSE = 0;
            Metadata = new CalibrationMetadata();
        }
    }

    public enum ImportFormat
    {
        CSVFormat,
        ASCIILog,
        BinaryLog
    }
}