using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CTSegmenter
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

        public Material(string name, Color color, byte min, byte max, byte id)
        {
            Name = name;
            Color = color;
            Min = min;
            Max = max;
            ID = id;
        }

        public override string ToString() => Name;
    }
}
