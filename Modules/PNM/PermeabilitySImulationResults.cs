using CTS;
using System.Collections.Generic;

public class PermeabilitySimulationResult
{
    // Input parameters
    public PoreNetworkModel Model { get; set; }
    public PermeabilitySimulator.FlowAxis FlowAxis { get; set; }
    public double Viscosity { get; set; }
    public double InputPressure { get; set; }
    public double OutputPressure { get; set; }
    public double Tortuosity { get; set; }
    public double ModelLength { get; set; }
    public double ModelArea { get; set; }

    // Flags for used methods
    public bool UsedDarcyMethod { get; set; }
    public bool UsedLatticeBoltzmannMethod { get; set; }
    public bool UsedNavierStokesMethod { get; set; }

    // Results - Darcy's Law Method
    public double PermeabilityDarcy { get; set; }
    public double PermeabilityMilliDarcy { get; set; }
    public double CorrectedPermeabilityDarcy { get; set; }
    public double CorrectedPermeabilityMilliDarcy { get; set; }

    // Results - Lattice Boltzmann Method
    public double LatticeBoltzmannPermeabilityDarcy { get; set; }
    public double LatticeBoltzmannPermeabilityMilliDarcy { get; set; }
    public double CorrectedLatticeBoltzmannPermeabilityDarcy { get; set; }
    public double CorrectedLatticeBoltzmannPermeabilityMilliDarcy { get; set; }

    // Results - Kozeny-Carman Method (used for tortuosity correction validation)
    public double KozenyCarmanPermeabilityDarcy { get; set; }
    public double KozenyCarmanPermeabilityMilliDarcy { get; set; }
    public double CorrectedKozenyCarmanPermeabilityDarcy { get; set; }
    public double CorrectedKozenyCarmanPermeabilityMilliDarcy { get; set; }

    // Results - Navier-Stokes Method
    public double NavierStokesPermeabilityDarcy { get; set; }
    public double NavierStokesPermeabilityMilliDarcy { get; set; }
    public double CorrectedNavierStokesPermeabilityDarcy { get; set; }
    public double CorrectedNavierStokesPermeabilityMilliDarcy { get; set; }

    // Visualization data - Darcy's Law
    public Dictionary<int, double> PressureField { get; set; }

    // Visualization data - Lattice Boltzmann
    public Dictionary<int, double> LatticeBoltzmannPressureField { get; set; }

    // Visualization data - Navier-Stokes
    public Dictionary<int, double> NavierStokesPressureField { get; set; }

    // Flow data
    public Dictionary<int, double> ThroatFlowRates { get; set; }
    public double TotalFlowRate { get; set; }

    // Boundary conditions
    public List<int> InletPores { get; set; }
    public List<int> OutletPores { get; set; }
}