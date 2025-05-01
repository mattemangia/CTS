using System;

namespace CTSegmenter
{
    // Extension class to add required properties to TriaxialSimulator
    public static class TriaxialSimulatorExtension
    {
        // Gets the current strain value from the simulator
        public static double GetCurrentStrain(TriaxialSimulator simulator)
        {
            var strainHistory = simulator.GetType().GetField("strainHistory",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic);

            if (strainHistory != null)
            {
                var list = (System.Collections.Generic.List<double>)strainHistory.GetValue(simulator);
                if (list != null && list.Count > 0)
                    return list[list.Count - 1];
            }

            return 0.0;
        }

        // Gets the current stress value from the simulator
        public static double GetCurrentStress(TriaxialSimulator simulator)
        {
            var stressHistory = simulator.GetType().GetField("stressHistory",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic);

            var initialPressure = simulator.GetType().GetField("initialAxialPressureMPa",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic);

            if (stressHistory != null)
            {
                var list = (System.Collections.Generic.List<double>)stressHistory.GetValue(simulator);
                if (list != null && list.Count > 0)
                    return list[list.Count - 1];
            }

            if (initialPressure != null)
                return (double)initialPressure.GetValue(simulator);

            return 0.0;
        }
    }
}