using System.Globalization;
using System.Numerics;

namespace CTS
{
    /// <summary>
    /// Converts the free-form text coming from the parent form
    /// (combo-box, text field, stored settings, …) into an axis vector.
    /// </summary>
    internal static class DirectionParser
    {
        public static Vector3 Parse(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return Vector3.UnitZ;          // default → Z

            string s = raw
                .Trim()
                .ToLower(CultureInfo.InvariantCulture)
                .Replace(" ", "")             // “x axis” → “xaxis”
                .Replace("-", "")             // “x-axis” → “xaxis”
                .Replace("_", "");            // “x_axis” → “xaxis”

            switch (s)
            {
                // -------------- X --------------
                case "x":          // “x”
                case "xaxis":      // “x-axis”, “x axis”
                case "axisx":      // “axis-x”
                case "1,0,0":      // raw vector text
                    return Vector3.UnitX;

                // -------------- Y --------------
                case "y":
                case "yaxis":
                case "axisy":
                case "0,1,0":
                    return Vector3.UnitY;

                // -------------- Z --------------
                case "z":
                case "zaxis":
                case "axisz":
                case "0,0,1":
                    return Vector3.UnitZ;

                // ----------- fallback ----------
                default:
                    // Try to parse something like “0,1,0”
                    if (TryParseNumeric(s, out Vector3 v) && v.Length() > 0)
                        return Vector3.Normalize(v);

                    // Unknown → log & fall back
                    Logger.Log($"[DirectionParser] Unrecognised direction “{raw}”, defaulting to Z-axis");
                    return Vector3.UnitZ;
            }
        }

        private static bool TryParseNumeric(string txt, out Vector3 v)
        {
            v = Vector3.Zero;
            var parts = txt.Split(',');
            if (parts.Length != 3) return false;

            if (float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
                float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
            {
                v = new Vector3(x, y, z);
                return true;
            }
            return false;
        }
    }
}