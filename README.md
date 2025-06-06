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
  Automatic pore/throat extraction and Darcy/Kozeny‑Carman or LBM flow simulation.
* **Acoustic & Mechanical Simulations**
  Elastic wave propagation, triaxial testing and NMR emulation on segmented cores.
* **Petrophysical Toolkit**
  Core extraction, band detection and a suite of filtering/resampling utilities.

---

## Installation

1. **Prerequisites**

   * **Visual Studio 2022** with **.NET Framework 4.8.1** workload.
   * A 64‑bit Windows OS (GPU recommended for large datasets).
2. **Clone the repository**

   ```bash
   git clone https://github.com/your‑org/CTS.git
   cd CTS
   ```
3. **Open the solution**
   Launch *CTS.sln* in Visual Studio.
4. **Restore NuGet packages**
   VS will automatically restore on load, or run:

   ```bash
   dotnet restore
   ```
5. **Build**
   Select **x64 | Release** (or Debug) and choose **Build > Build Solution**.
6. **Run**
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
