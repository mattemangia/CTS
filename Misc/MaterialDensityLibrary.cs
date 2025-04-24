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
}