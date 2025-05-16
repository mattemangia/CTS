using System;
using System.Collections.Generic;

namespace CTS
{
    /// <summary>
    /// Stores the results of a permeability simulation with multiple calculation methods
    /// </summary>
    [Serializable]
    public class PermeabilitySimulationResult
    {
        public PoreNetworkModel Model { get; set; }
        public PermeabilitySimulator.FlowAxis FlowAxis { get; set; }
        public double Viscosity { get; set; }
        public double InputPressure { get; set; }
        public double OutputPressure { get; set; }

        // Darcy's Law results
        public double PermeabilityDarcy { get; set; }
        public double PermeabilityMilliDarcy { get; set; }
        public double Tortuosity { get; set; }
        public double CorrectedPermeabilityDarcy { get; set; }
        public double CorrectedPermeabilityMilliDarcy { get; set; }

        // Stefan-Boltzmann method results (placeholder)
        public double StefanBoltzmannPermeabilityDarcy { get; set; }
        public double StefanBoltzmannPermeabilityMilliDarcy { get; set; }
        public double CorrectedStefanBoltzmannPermeabilityDarcy { get; set; }
        public double CorrectedStefanBoltzmannPermeabilityMilliDarcy { get; set; }

        // Navier-Stokes method results (placeholder)
        public double NavierStokesPermeabilityDarcy { get; set; }
        public double NavierStokesPermeabilityMilliDarcy { get; set; }
        public double CorrectedNavierStokesPermeabilityDarcy { get; set; }
        public double CorrectedNavierStokesPermeabilityMilliDarcy { get; set; }

        // Common fields used by all methods
        public Dictionary<int, double> PressureField { get; set; }
        public Dictionary<int, double> ThroatFlowRates { get; set; }
        public double TotalFlowRate { get; set; }
        public double ModelLength { get; set; }
        public double ModelArea { get; set; }
        public List<int> InletPores { get; set; }
        public List<int> OutletPores { get; set; }

        // Calculation method flags
        public bool UsedDarcyMethod { get; set; }
        public bool UsedStefanBoltzmannMethod { get; set; }
        public bool UsedNavierStokesMethod { get; set; }
    }
}