# CTS (CT Simulation environment)

**CTS (CT Simulation environment)** is an open‑source, modular environment for visualising, segmenting and simulating X‑ray computed‑tomography (CT) data. Built in C#/.NET, CTS targets researchers and students in materials science, geology, geophysics and petrophysics who need a free alternative to commercial CT‑analysis suites.

> **Work‑in‑Progress Notice**
> The **Node Editor** and **HPC server/endpoint** integration are not yet available and are marked as *work in progress*.

---

## Key Features

* **Interactive 2D/3D Viewer**
  GPU‑accelerated volume rendering with slice planes, clipping, cutting and measurement tools.
* **Advanced Segmentation**
  Classic tools (brush, lasso, threshold, etc.) plus AI‑powered models: *SAM2*, *μSAM* and *Grounding DINO*.
* **Particle & Material Analysis**
  3‑D connected‑component labelling, particle statistics, material volume/surface metrics.
* **Pore‑Network Modelling & Permeability**
  Reconstructs a network of pores and throats from a segmented
  volume to represent the pore space of a material (e.g. limestone, sandstone, ...). This module analyzes the
  segmented pore phase to identify individual pore bodies (as network nodes) and the
  connections between them as throats. From the pore network, CTS can simulate fluid flow and
  calculate effective permeability of the material using Darcy’s law, Navier-Stokes and lattice Boltzmann. The
  output includes metrics like pore connectivity, throat size distribution, tortuosity, and permeability (with
  comparisons to classical models such as Kozeny–Carman). The final result is also expressed as tortuosity
  corrected.
* **Acoustic & Mechanical Simulations**
  Simulates the propagation of elastic waves through CT-derived
  volumes for ultrasonic testing and geophysical analysis. The acoustic module models both
  compressional (P-wave) and shear (S-wave) propagation in an elastic medium reconstructed
  from a labeled CT dataset. This enables high-fidelity non-destructive testing simulations on core
  samples – for example, assessing material integrity, detecting fractures, and performing velocity
  tomography. The simulation supports user-defined material properties (density, elastic moduli)
  per region and can utilize GPU acceleration (via ILGPU) for large-scale wavefield computations,
  providing researchers a unique open source tool to virtually probe the mechanical/acoustic response of
  scanned samples.
* **Petrophysical Toolkit**
  A collection of specialized tools for analyzing rock core samples and other
  materials. For instance, CTS can extract a cylindrical core sub-volume from a full 3D scan (useful
  for standardizing sample geometry for simulations or lab comparisons) . Additional modules
  include a Triaxial Test Simulator for applying virtual mechanical stress/strain to a core
  (mimicking laboratory triaxial compression experiments) and an NMR Simulation tool for
  predicting NMR responses from pore structures (to help correlate CT-derived porosity with NMR
  measurements).

---

## Installation

1. **Prerequisites**

   * **Visual Studio 2022** with **.NET Framework 4.8.1** workload.
   * A 64‑bit Windows OS (GPU recommended for large datasets).
2. **Clone the repository**

   ```bash
   git clone https://github.com/mattemangia/CTS.git
   cd CTS
   ```
3. **Open the solution**
   Launch *CTSegmenter.sln* in Visual Studio.
4. **Restore NuGet packages**
   VS will automatically restore on load, or run:

   ```bash
   dotnet restore
   ```
5. **Build**
   Select **x64 | Release** (or Debug) and choose **Build > Build Solution**.
   **Add ONNX models (Not Included in this repo)**
   Copy the original ONNX model files for SAM2, μSAM, and Grounding DINO into the bin/ONNX/ directory (create the folder if it does not exist). CTS expects these models here at runtime in order to use AI segmentation.
7. **Run**
   Press **F5** (Debug) or **Ctrl‑F5** (Run) from Visual Studio, or launch the executable in `bin/`.

---

## Dependencies

| Library           | Purpose                       |
| ----------------- | ----------------------------- |
| SharpDX           | DirectX 11 volume rendering   |
| ILGPU             | GPU & multi‑core computation  |
| ONNX Runtime      | AI model inference            |
| Math.NET Numerics | Linear algebra & numerics     |
| OpenTK            | OpenGL/windowing helpers      |
| Krypton Toolkit   | Modern WinForms UI components |

All dependencies are delivered via NuGet; they restore automatically during the build.

---

## License

CTS is released under the **Apache License 2.0**. See the [LICENSE](LICENSE) file for details.

---

## Author & Contact

Maintained by **Matteo Mangiagalli** (University of Fribourg).
Feedback, issues and pull requests are welcome—please use the GitHub *Issues* page to get in touch or contribute.
