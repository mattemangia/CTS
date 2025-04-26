using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CTSegmenter.Misc
{
    public static class MiscUtils
    {
        public static T Clamp<T>(this T val, T min, T max) where T : IComparable<T>
        {
            if (val.CompareTo(min) < 0) return min;
            else if (val.CompareTo(max) > 0) return max;
            else return val;
        }
    }
    public static class MathHelper
    {
        public static float ToRadians(float degrees)
        {
            return degrees * (float)Math.PI / 180f;
        }

        public static float ToDegrees(float radians)
        {
            return radians * 180f / (float)Math.PI;
        }
    }
}
