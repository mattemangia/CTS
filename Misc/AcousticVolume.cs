using System.Collections.Generic;
using System.Numerics;

public class AcousticVolume
{
    public List<Vector3> Vertices { get; set; } = new List<Vector3>();
    public List<int> Indices { get; set; } = new List<int>();
    public List<Vector3> Normals { get; set; } = new List<Vector3>();
    public Dictionary<int, double> VoxelDensities { get; set; } = new Dictionary<int, double>();
    public byte[,,] VolumeData { get; set; }
    public Vector3 MinBounds { get; set; }
    public Vector3 MaxBounds { get; set; }
}
