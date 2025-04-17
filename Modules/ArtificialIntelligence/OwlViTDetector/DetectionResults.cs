/// <summary>
/// Represents a detection result
/// </summary>
public class DetectionResult
{
    /// <summary>
    /// The detected category/class name
    /// </summary>
    public string Category { get; set; }

    /// <summary>
    /// Confidence score (0.0 to 1.0)
    /// </summary>
    public float Confidence { get; set; }

    /// <summary>
    /// Normalized X coordinate (0.0 to 1.0)
    /// </summary>
    public float X { get; set; }

    /// <summary>
    /// Normalized Y coordinate (0.0 to 1.0)
    /// </summary>
    public float Y { get; set; }

    /// <summary>
    /// Normalized width (0.0 to 1.0)
    /// </summary>
    public float Width { get; set; }

    /// <summary>
    /// Normalized height (0.0 to 1.0)
    /// </summary>
    public float Height { get; set; }

    /// <summary>
    /// Slice index where the detection was found
    /// </summary>
    public int Slice { get; set; }

    /// <summary>
    /// The exact query variant that produced this detection
    /// </summary>
    public string QueryVariant { get; set; }

    /// <summary>
    /// Text representation for display in UI
    /// </summary>
    public string DisplayText => $"{Category} ({Confidence:P1})";
}