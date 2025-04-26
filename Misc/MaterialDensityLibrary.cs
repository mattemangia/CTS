using System;
using System.Collections.Generic;
using System.Linq;

public class MaterialDensity
{
    public string Name { get; set; }
    public double Density { get; set; }  // kg/m³
    public string Category { get; set; }

    public MaterialDensity(string name, double density, string category)
    {
        Name = name;
        Density = density;
        Category = category;
    }

    public override string ToString()
    {
        return $"{Name} ({Density} kg/m³)";
    }
}

public static class MaterialDensityLibrary
{
    public static List<MaterialDensity> Materials { get; private set; }

    static MaterialDensityLibrary()
    {
        // Initialize the materials list with common materials
        Materials = new List<MaterialDensity>
        {
            // Gases
            new MaterialDensity("Air", 1.225, "Gas"),

            // Liquids
            new MaterialDensity("Water", 1000, "Liquid"),
            new MaterialDensity("Seawater", 1025, "Liquid"),
            new MaterialDensity("Oil (light)", 850, "Liquid"),

            // Organic Materials
            new MaterialDensity("Wood (light)", 300, "Organic"),
            new MaterialDensity("Wood (dense)", 900, "Organic"),

            // Plastics
            new MaterialDensity("PLA", 1250, "Plastic"),
            new MaterialDensity("ABS", 1050, "Plastic"),
            new MaterialDensity("PVC", 1400, "Plastic"),

            // Common Minerals & Rocks
            new MaterialDensity("Quartz", 2650, "Mineral"),
            new MaterialDensity("Calcite", 2710, "Mineral"),
            new MaterialDensity("Feldspar", 2600, "Mineral"),
            new MaterialDensity("Clay", 1800, "Mineral"),
            new MaterialDensity("Limestone", 2500, "Rock"),
            new MaterialDensity("Sandstone", 2350, "Rock"),
            new MaterialDensity("Granite", 2700, "Rock"),
            new MaterialDensity("Basalt", 3000, "Rock"),

            // Metals
            new MaterialDensity("Aluminum", 2700, "Metal"),
            new MaterialDensity("Iron", 7850, "Metal"),
            new MaterialDensity("Steel", 7800, "Metal"),
            new MaterialDensity("Copper", 8960, "Metal"),
            new MaterialDensity("Gold", 19300, "Metal"),

            // Dense Minerals
            new MaterialDensity("Diamond", 3510, "Dense Mineral"),
            new MaterialDensity("Pyrite", 5000, "Dense Mineral"),
            new MaterialDensity("Hematite", 5300, "Dense Mineral")
        };
    }

    public static List<MaterialDensity> GetByCategory(string category)
    {
        return Materials.Where(m => m.Category == category).ToList();
    }

    public static List<string> GetCategories()
    {
        return Materials.Select(m => m.Category).Distinct().ToList();
    }

    public class CalibrationPoint
    {
        public string Region { get; set; }
        public string Material { get; set; }
        public double Density { get; set; }
        public double AvgGrayValue { get; set; }
    }
    /// <summary>
    /// Calculate density based on calibration points and grayscale value
    /// </summary>
    public static double CalculateDensityFromGrayValue(List<CalibrationPoint> calibrationPoints, double grayValue)
    {
        if (calibrationPoints == null || calibrationPoints.Count < 2)
        {
            throw new ArgumentException("At least two calibration points are required");
        }

        // Sort calibration points by gray value
        calibrationPoints.Sort((a, b) => a.AvgGrayValue.CompareTo(b.AvgGrayValue));

        // Find the two closest points for interpolation
        int lowerIndex = -1;
        int upperIndex = -1;

        for (int i = 0; i < calibrationPoints.Count; i++)
        {
            if (calibrationPoints[i].AvgGrayValue <= grayValue)
            {
                lowerIndex = i;
            }
            else
            {
                upperIndex = i;
                break;
            }
        }

        // Handle edge cases
        if (lowerIndex == -1)
        {
            // Below the lowest point, use the first point
            return calibrationPoints[0].Density;
        }
        else if (upperIndex == -1)
        {
            // Above the highest point, use the last point
            return calibrationPoints[calibrationPoints.Count - 1].Density;
        }

        // Interpolate between the two points
        CalibrationPoint lower = calibrationPoints[lowerIndex];
        CalibrationPoint upper = calibrationPoints[upperIndex];

        double t = (grayValue - lower.AvgGrayValue) / (upper.AvgGrayValue - lower.AvgGrayValue);

        return lower.Density * (1 - t) + upper.Density * t;
    }

    /// <summary>
    /// Performs a linear regression to find the relationship between gray values and density
    /// </summary>
    public static (double slope, double intercept) CalculateLinearDensityModel(List<CalibrationPoint> calibrationPoints)
    {
        if (calibrationPoints == null || calibrationPoints.Count < 2)
        {
            throw new ArgumentException("At least two calibration points are required");
        }

        int n = calibrationPoints.Count;
        double sumX = 0;
        double sumY = 0;
        double sumXY = 0;
        double sumX2 = 0;

        // Calculate sums for linear regression
        foreach (var point in calibrationPoints)
        {
            sumX += point.AvgGrayValue;
            sumY += point.Density;
            sumXY += point.AvgGrayValue * point.Density;
            sumX2 += point.AvgGrayValue * point.AvgGrayValue;
        }

        // Calculate slope (m) and intercept (b) for y = mx + b
        double denominator = n * sumX2 - sumX * sumX;

        if (denominator == 0)
        {
            return (0, sumY / n); // Horizontal line, average density
        }

        double slope = (n * sumXY - sumX * sumY) / denominator;
        double intercept = (sumY * sumX2 - sumX * sumXY) / denominator;

        return (slope, intercept);
    }

}