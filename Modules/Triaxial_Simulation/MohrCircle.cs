using System.Drawing;
using System;

public class MohrCircle
{
    public double Center { get; private set; }
    public double Radius { get; private set; }
    public double Sigma1 { get; private set; }
    public double Sigma3 { get; private set; }

    public MohrCircle(double sigma1, double sigma3)
    {
        Sigma1 = Math.Max(sigma1, sigma3);
        Sigma3 = Math.Min(sigma1, sigma3);
        Center = (Sigma1 + Sigma3) / 2.0;
        Radius = (Sigma1 - Sigma3) / 2.0;
    }

    public PointF GetTangentPoint(double frictionAngle)
    {
        double phi = frictionAngle * Math.PI / 180.0;
        double sigmaF = Center - Radius * Math.Sin(phi);
        double tauF = Radius * Math.Cos(phi);
        return new PointF((float)sigmaF, (float)tauF);
    }
}