using System.Drawing;

namespace CTS
{
    // ------------------------------------------------------------------------
    // Material class
    // ------------------------------------------------------------------------
    public class Material
    {
        public byte ID { get; set; } // Changed from int to byte
        public string Name { get; set; }
        public Color Color { get; set; }
        public byte Min { get; set; }
        public byte Max { get; set; }
        public bool IsExterior { get; set; } = false;
        public double Density { get; set; } = 0.0;
        public bool IsVisible { get; set; } = true;

        public Material(string name, Color color, byte min, byte max, byte id, double density = 0.0)
        {
            Name = name;
            Color = color;
            Min = min;
            Max = max;
            ID = id;
            Density = density;
            this.IsVisible = true;
        }

        public override string ToString() => Name;
    }
}