using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;

namespace CTSegmenter
{
    /// <summary>
    /// Factory class to create appropriate simulations based on parameters
    /// </summary>
    public static class SimulationFactory
    {
        /// <summary>
        /// Creates either a standard or inhomogeneous acoustic velocity simulation based on the parameters
        /// </summary>
        public static AcousticVelocitySimulation CreateAcousticSimulation(
            Material material,
            List<Triangle> triangles,
            float confiningPressure,
            string waveType,
            int timeSteps,
            float frequency,
            float amplitude,
            float energy,
            string direction,
            bool useExtendedSimulationTime,
            bool useInhomogeneousDensity = false,
            ConcurrentDictionary<Vector3, float> densityMap = null,
            SimulationResult previousTriaxialResult = null,
            MainForm mainForm = null)
        {
            // Log creation parameters
            Logger.Log($"[SimulationFactory] Creating acoustic simulation - Material: {material?.Name}, " +
                       $"Wave type: {waveType}, Inhomogeneous density: {useInhomogeneousDensity}");

            // If inhomogeneous density is enabled and we have a density map, use the inhomogeneous simulation
            if (useInhomogeneousDensity && densityMap != null && densityMap.Count > 0)
            {
                Logger.Log($"[SimulationFactory] Using inhomogeneous acoustic simulation with {densityMap.Count} density points");

                return new InhomogeneousAcousticSimulation(
                    material,
                    triangles,
                    confiningPressure,
                    waveType,
                    timeSteps,
                    frequency,
                    amplitude,
                    energy,
                    direction,
                    useExtendedSimulationTime,
                    useInhomogeneousDensity,
                    densityMap,
                    previousTriaxialResult,
                    mainForm);
            }
            else
            {
                // Use the standard acoustic simulation
                Logger.Log("[SimulationFactory] Using standard acoustic simulation");

                return new AcousticVelocitySimulation(
                    material,
                    triangles,
                    confiningPressure,
                    waveType,
                    timeSteps,
                    frequency,
                    amplitude,
                    energy,
                    direction,
                    useExtendedSimulationTime,
                    previousTriaxialResult,
                    mainForm);
            }
        }

        /// <summary>
        /// Creates either a standard or inhomogeneous triaxial simulation based on the parameters
        /// </summary>
        public static TriaxialSimulation CreateTriaxialSimulation(
            Material material,
            List<Triangle> triangles,
            float confiningPressure,
            float minAxialPressure,
            float maxAxialPressure,
            int pressureSteps,
            string direction,
            bool useInhomogeneousDensity = false,
            ConcurrentDictionary<Vector3, float> densityMap = null)
        {
            // Log creation parameters
            Logger.Log($"[SimulationFactory] Creating triaxial simulation - Material: {material?.Name}, " +
                       $"Confining pressure: {confiningPressure}, Inhomogeneous density: {useInhomogeneousDensity}");

            // If inhomogeneous density is enabled and we have a density map, use the inhomogeneous simulation
            if (useInhomogeneousDensity && densityMap != null && densityMap.Count > 0)
            {
                Logger.Log($"[SimulationFactory] Using inhomogeneous triaxial simulation with {densityMap.Count} density points");

                return new InhomogeneousTriaxialSimulation(
                    material,
                    triangles,
                    confiningPressure,
                    minAxialPressure,
                    maxAxialPressure,
                    pressureSteps,
                    direction,
                    useInhomogeneousDensity,
                    densityMap);
            }
            else
            {
                // Use the standard triaxial simulation
                Logger.Log("[SimulationFactory] Using standard triaxial simulation");

                return new TriaxialSimulation(
                    material,
                    triangles,
                    confiningPressure,
                    minAxialPressure,
                    maxAxialPressure,
                    pressureSteps,
                    direction);
            }
        }
    }
}