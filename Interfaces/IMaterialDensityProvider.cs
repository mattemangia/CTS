using System.Collections.Generic;
using static MaterialDensityLibrary;

namespace CTS
{
    /// <summary>
    /// Interface for forms that need to provide material density functionality
    /// </summary>
    public interface IMaterialDensityProvider
    {
        /// <summary>
        /// The currently selected material
        /// </summary>
        Material SelectedMaterial { get; }

        /// <summary>
        /// Calculate the total volume of the selected material in cubic meters (m³)
        /// </summary>
        double CalculateTotalVolume();

        /// <summary>
        /// Set the density of the selected material in kg/m³
        /// </summary>
        void SetMaterialDensity(double density);

        /// <summary>
        /// Apply a density calibration based on multiple calibration points
        /// </summary>
        void ApplyDensityCalibration(List<CalibrationPoint> calibrationPoints);
    }
}