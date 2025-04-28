using System.Collections.Generic;

public class Pore
{
    public int Id { get; set; }
    public double Volume { get; set; }      // in µm³
    public double Area { get; set; }        // in µm²
    public double Radius { get; set; }      // in µm
    public Point3D Center { get; set; }
    public int ConnectionCount { get; set; }
}

public class Throat
{
    public int Id { get; set; }
    public int PoreId1 { get; set; }
    public int PoreId2 { get; set; }
    public double Radius { get; set; }      // in µm
    public double Length { get; set; }      // in µm
    public double Volume { get; set; }      // in µm³
}

public class Point3D
{
    // Add default parameterless constructor
    public Point3D()
    {
        // Initialize to origin (0,0,0)
        X = 0;
        Y = 0;
        Z = 0;
    }

    // Keep existing constructor
    public Point3D(int x, int y, int z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    // Add a constructor that takes doubles directly
    public Point3D(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    // Add a copy constructor for convenience
    public Point3D(Point3D other)
    {
        if (other == null)
        {
            X = Y = Z = 0;
        }
        else
        {
            X = other.X;
            Y = other.Y;
            Z = other.Z;
        }
    }

    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }

    // Add ToString for easier debugging
    public override string ToString()
    {
        return $"({X}, {Y}, {Z})";
    }
}


public class PoreNetworkModel
{
    public List<Pore> Pores { get; set; } = new List<Pore>();
    public List<Throat> Throats { get; set; } = new List<Throat>();
    public double PixelSize { get; set; }   // in meters
    public double Porosity { get; set; }
    public double TotalPoreVolume { get; set; }
    public double TotalThroatVolume { get; set; }
}