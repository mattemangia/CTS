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
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
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