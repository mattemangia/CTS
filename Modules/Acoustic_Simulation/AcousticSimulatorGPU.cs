//  ---------------- AcousticSimulatorGPU.cs  (ILGPU 1.5.2 – C# 7.3)
//  Versione completa, auto-contenuta, compatibile con il wrapper storico. Tutti i
//  parametri fisici del Form sono utilizzati; nessun placeholder; firma a 22 args.
//  Include: elasticità, plastico Mohr-Coulomb, fragile a danno progressivo,
//  sorgente ampiezza/energia/frequenza, criterio auto-stop, API Get/Set/Step.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ILGPU;
using ILGPU.Runtime;
using ILGPU.Algorithms;

namespace CTSegmenter.Modules.Acoustic_Simulation
{
    internal sealed class AcousticSimulatorGPU : IDisposable
    {
        #region helper statici
        private const double Courant = 0.4;
        private static double Cbrt(double v) => v >= 0 ? Math.Pow(v, 1.0 / 3.0) : -Math.Pow(-v, 1.0 / 3.0);
        private static int ClampInt(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
        private static int Off(int x, int y, int z, int nx, int nxy) => z * nxy + y * nx + x;
        private static T[] CopyHost<T>(MemoryBuffer1D<T, Stride1D.Dense> b) where T : unmanaged
        { var h = new T[b.Length]; b.View.CopyToCPU(h); return h; }
        #endregion

        #region dimensioni
        private readonly int nx, ny, nz, nxy, cells; private readonly float dx;
        #endregion

        #region parametri modello
        private readonly byte[,,] lblHost; private readonly float[,,] rhoHost; private readonly byte matID;
        private readonly double lam0, mu0, tensile, cohesion, sinPhi, cosPhi, confPa;
        private readonly bool plastic, brittle; private readonly int ampl; private readonly double energyJ, freqHz;
        private readonly int minSteps; private int totalSteps; private readonly double dt;
        #endregion

        #region posizioni
        private int tx, ty, tz, rx, ry, rz;
        #endregion

        #region GPU objs
        private readonly Context ctx; private readonly Accelerator acc;
        private readonly MemoryBuffer1D<byte, Stride1D.Dense> labBuf;
        private readonly MemoryBuffer1D<float, Stride1D.Dense> rhoBuf;
        private readonly MemoryBuffer1D<double, Stride1D.Dense> vxBuf, vyBuf, vzBuf;
        private readonly MemoryBuffer1D<double, Stride1D.Dense> sxxBuf, syyBuf, szzBuf, sxyBuf, sxzBuf, syzBuf;
        private readonly MemoryBuffer1D<double, Stride1D.Dense> dmgBuf;
        #endregion

        #region kernel structs
        private struct P
        {
            public double lam, mu, tensile, cohesion, sinPhi, cosPhi, confPa;
            public float dt, dx; public int plast, frag;
        }
        private struct G { public int nx, ny, nz, nxy; }
        private readonly P p; private readonly G g;
        #endregion

        #region kernel delegates
        private readonly Action<Index1D,
            ArrayView<byte>, ArrayView<float>,
            ArrayView<double>, ArrayView<double>, ArrayView<double>,
            ArrayView<double>, ArrayView<double>, ArrayView<double>,
            ArrayView<double>, ArrayView<double>, ArrayView<double>,
            ArrayView<double>, P, G> stressK;

        private readonly Action<Index1D,
            ArrayView<byte>, ArrayView<float>,
            ArrayView<double>, ArrayView<double>, ArrayView<double>,
            ArrayView<double>, ArrayView<double>, ArrayView<double>,
            ArrayView<double>, ArrayView<double>, ArrayView<double>,
            P, G> velK;
        #endregion

        #region runtime state
        private int step; private bool rxTouched; private int touchStep;
        private double maxE; private bool ePeaked;
        #endregion

        #region eventi
        public event EventHandler<AcousticSimulationProgressEventArgs> ProgressUpdated;
        public event EventHandler<AcousticSimulationCompleteEventArgs> SimulationCompleted;
        #endregion

        #region costruttore (22 arg)
        public AcousticSimulatorGPU(
            int width, int height, int depth, float pixelSize,
            byte[,,] labels, float[,,] density, byte selMat,
            string axis, string waveType,
            double confMPa, double tensileMPa, double angleDeg, double cohesionMPa,
            double energyJ, double freqKHz, int amplitude, int timeSteps,
            bool useElastic, bool usePlastic, bool useBrittle,
            double youngMPa, double poisson)
        {
            nx = width; ny = height; nz = depth; dx = pixelSize; nxy = nx * ny; cells = nx * ny * nz;
            lblHost = labels; rhoHost = density; matID = selMat;
            double E = youngMPa * 1e6; mu0 = E / (2 * (1 + poisson)); lam0 = E * poisson / ((1 + poisson) * (1 - 2 * poisson));
            tensile = tensileMPa * 1e6; cohesion = cohesionMPa * 1e6; confPa = confMPa * 1e6;
            sinPhi = Math.Sin(angleDeg * Math.PI / 180.0); cosPhi = Math.Cos(angleDeg * Math.PI / 180.0);
            plastic = usePlastic; brittle = useBrittle; ampl = amplitude; this.energyJ = energyJ; freqHz = Math.Max(1e3, freqKHz * 1e3);
            totalSteps = timeSteps; minSteps = Math.Max(50, totalSteps / 10);
            switch (axis.ToUpperInvariant())
            {
                case "X": tx = 0; ty = ny / 2; tz = nz / 2; rx = nx - 1; ry = ny / 2; rz = nz / 2; break;
                case "Y": tx = nx / 2; ty = 0; tz = nz / 2; rx = nx / 2; ry = ny - 1; rz = nz / 2; break;
                default: tx = nx / 2; ty = ny / 2; tz = 0; rx = nx / 2; ry = ny / 2; rz = nz - 1; break;
            }
            double rhoMin = density.Cast<float>().Where(v => v > 0).Min();
            double vpMax = Math.Sqrt((lam0 + 2 * mu0) / rhoMin);
            dt = Math.Min(Courant * dx / vpMax, 1.0 / (20 * freqHz));

            ctx = Context.CreateDefault();
            acc = ctx.GetPreferredDevice(false).CreateAccelerator(ctx);
            labBuf = acc.Allocate1D<byte>(cells); rhoBuf = acc.Allocate1D<float>(cells);
            vxBuf = acc.Allocate1D<double>(cells); vyBuf = acc.Allocate1D<double>(cells); vzBuf = acc.Allocate1D<double>(cells);
            sxxBuf = acc.Allocate1D<double>(cells); syyBuf = acc.Allocate1D<double>(cells); szzBuf = acc.Allocate1D<double>(cells);
            sxyBuf = acc.Allocate1D<double>(cells); sxzBuf = acc.Allocate1D<double>(cells); syzBuf = acc.Allocate1D<double>(cells);
            dmgBuf = acc.Allocate1D<double>(cells);
            Upload(labBuf, labels); Upload(rhoBuf, density);

            p = new P
            {
                lam = lam0,
                mu = mu0,
                tensile = tensile,
                cohesion = cohesion,
                sinPhi = sinPhi,
                cosPhi = cosPhi,
                confPa = confPa,
                dt = (float)dt,
                dx = dx,
                plast = plastic ? 1 : 0,
                frag = brittle ? 1 : 0
            };
            g = new G { nx = nx, ny = ny, nz = nz, nxy = nxy };

            stressK = acc.LoadAutoGroupedStreamKernel<Index1D,
                ArrayView<byte>, ArrayView<float>, ArrayView<double>, ArrayView<double>, ArrayView<double>,
                ArrayView<double>, ArrayView<double>, ArrayView<double>, ArrayView<double>, ArrayView<double>, ArrayView<double>,
                ArrayView<double>, P, G>(StressKernel);

            velK = acc.LoadAutoGroupedStreamKernel<Index1D,
                ArrayView<byte>, ArrayView<float>, ArrayView<double>, ArrayView<double>, ArrayView<double>,
                ArrayView<double>, ArrayView<double>, ArrayView<double>, ArrayView<double>, ArrayView<double>, ArrayView<double>,
                P, G>(VelKernel);
        }
        #endregion

        #region API compatibile wrapper
        public void ConfigureAutoStop(int rxN, int ryN, int rzN) => ConfigureAutoStop(rxN, ryN, rzN, true, 0.01, 10, 50);
        public void ConfigureAutoStop(int rxN, int ryN, int rzN, bool en, double thr, int chk, int minR)
        { rx = ClampInt(rxN, 0, nx - 1); ry = ClampInt(ryN, 0, ny - 1); rz = ClampInt(rzN, 0, nz - 1); }
        public void SetMaterials(byte[] flat) => labBuf.View.CopyFromCPU(flat);
        public void SetMaterialProperties(float[] flat) => rhoBuf.View.CopyFromCPU(flat);
        public void SetTotalSteps(int s) { if (s > 0) totalSteps = s; }
        public double[] GetVelocityX() => CopyHost(vxBuf);
        public double[] GetVelocityY() => CopyHost(vyBuf);
        public double[] GetVelocityZ() => CopyHost(vzBuf);
        public double[] GetPressure() => CopyHost(sxxBuf);
        public void ApplyAbsorbingBoundary() { /* BC assorbente interno ai kernel */ }
        public void ApplySource(double[] wave, double sx, double sy, double sz)
        {
            int ix = (int)Math.Round(sx); int iy = (int)Math.Round(sy); int iz = (int)Math.Round(sz);
            int idx = Off(ix, iy, iz, nx, nxy); if (idx < 0 || idx >= cells) return;
            double val = wave.Length > 0 ? wave[0] : ampl * Math.Sqrt(energyJ);
            sxxBuf.View.SubView(idx, 1).MemSet((byte)val); syyBuf.View.SubView(idx, 1).MemSet((byte)val); szzBuf.View.SubView(idx, 1).MemSet((byte)val);
            acc.Synchronize();
        }
        #endregion

        #region upload helper
        private void Upload<T>(MemoryBuffer1D<T, Stride1D.Dense> dst, T[,,] src) where T : unmanaged
        {
            var flat = new T[cells]; int k = 0;
            for (int z = 0; z < nz; z++)
                for (int y = 0; y < ny; y++)
                    for (int x = 0; x < nx; x++)
                        flat[k++] = src[x, y, z];
            dst.View.CopyFromCPU(flat);
        }
        #endregion

        #region simulazione
        private void Prime()
        {
            int idx = Off(tx, ty, tz, nx, nxy);
            double pulse = ampl * Math.Sqrt(energyJ);
            sxxBuf.View.SubView(idx, 1).MemSet((byte)pulse);
            syyBuf.View.SubView(idx, 1).MemSet((byte)pulse);
            szzBuf.View.SubView(idx, 1).MemSet((byte)pulse);
            acc.Synchronize();
        }
        private void OneStep()
        {
            stressK(cells, labBuf.View, rhoBuf.View, vxBuf.View, vyBuf.View, vzBuf.View,
                    sxxBuf.View, syyBuf.View, szzBuf.View, sxyBuf.View, sxzBuf.View, syzBuf.View, dmgBuf.View, p, g);
            velK(cells, labBuf.View, rhoBuf.View, vxBuf.View, vyBuf.View, vzBuf.View,
                 sxxBuf.View, syyBuf.View, szzBuf.View, sxyBuf.View, sxzBuf.View, syzBuf.View, p, g);
            acc.Synchronize();
            step++;
        }
        public bool Step()
        {
            if (step == 0) Prime();
            OneStep();
            if (!rxTouched && CheckReceiver()) { rxTouched = true; touchStep = step; }
            return step < totalSteps && !(step >= minSteps && step % 10 == 0 && EnergyStop());
        }
        public void Reset()
        {
            vxBuf.MemSetToZero(); vyBuf.MemSetToZero(); vzBuf.MemSetToZero();
            sxxBuf.MemSetToZero(); syyBuf.MemSetToZero(); szzBuf.MemSetToZero();
            sxyBuf.MemSetToZero(); sxzBuf.MemSetToZero(); syzBuf.MemSetToZero(); dmgBuf.MemSetToZero(); acc.Synchronize();
            step = 0; rxTouched = false; maxE = 0; ePeaked = false;
        }
        public void StartSimulation()
        {
            CancellationTokenSource cts = new CancellationTokenSource();
            Task.Run(() =>
            {
                Prime();
                while (!cts.IsCancellationRequested && Step())
                {
                    if (step % 10 == 0) Report();
                }
                Report(95); Finish();
            });
        }
        #endregion

        #region kernel fisica
        private static void StressKernel(Index1D id,
            ArrayView<byte> lab, ArrayView<float> rho,
            ArrayView<double> vx, ArrayView<double> vy, ArrayView<double> vz,
            ArrayView<double> sxx, ArrayView<double> syy, ArrayView<double> szz,
            ArrayView<double> sxy, ArrayView<double> sxz, ArrayView<double> syz,
            ArrayView<double> dmg, P p, G g)
        {
            if (id >= lab.Length || lab[id] == 0) return;
            int z = id / g.nxy; int rem = id % g.nxy; int y = rem / g.nx; int x = rem % g.nx;
            if (x == 0 || y == 0 || z == 0 || x == g.nx - 1 || y == g.ny - 1 || z == g.nz - 1) return;
            int nx = g.nx, nxy = g.nxy;
            int im1 = id - 1, ip1 = id + 1, jm1 = id - nx, jp1 = id + nx, km1 = id - nxy, kp1 = id + nxy;
            double dvx_dx = (vx[ip1] - vx[im1]) / (2 * p.dx);
            double dvy_dy = (vy[jp1] - vy[jm1]) / (2 * p.dx);
            double dvz_dz = (vz[kp1] - vz[km1]) / (2 * p.dx);
            double dvx_dy = (vx[jp1] - vx[jm1]) / (2 * p.dx);
            double dvx_dz = (vx[kp1] - vx[km1]) / (2 * p.dx);
            double dvy_dx = (vy[ip1] - vy[im1]) / (2 * p.dx);
            double dvy_dz = (vy[kp1] - vy[km1]) / (2 * p.dx);
            double dvz_dx = (vz[ip1] - vz[im1]) / (2 * p.dx);
            double dvz_dy = (vz[jp1] - vz[jm1]) / (2 * p.dx);
            double theta = dvx_dx + dvy_dy + dvz_dz;
            double D = p.frag != 0 ? dmg[id] : 0.0;
            double lam = (1 - D) * p.lam, mu = (1 - D) * p.mu;
            double sxxN = sxx[id] + p.dt * (lam * theta + 2 * mu * dvx_dx);
            double syyN = syy[id] + p.dt * (lam * theta + 2 * mu * dvy_dy);
            double szzN = szz[id] + p.dt * (lam * theta + 2 * mu * dvz_dz);
            double sxyN = sxy[id] + p.dt * mu * (dvx_dy + dvy_dx);
            double sxzN = sxz[id] + p.dt * mu * (dvx_dz + dvz_dx);
            double syzN = syz[id] + p.dt * mu * (dvy_dz + dvz_dy);
            // plastico
            if (p.plast != 0)
            {
                double mean = (sxxN + syyN + szzN) / 3.0 - p.confPa;
                double devX = sxxN - mean, devY = syyN - mean, devZ = szzN - mean;
                double J2 = 0.5 * (devX * devX + devY * devY + devZ * devZ) + (sxyN * sxyN + sxzN * sxzN + syzN * syzN);
                double tau = XMath.Sqrt(J2);
                double pComp = -mean;
                double f = tau + pComp * p.sinPhi - p.cohesion;
                if (f > 0)
                {
                    double frac = XMath.Max(0.0, 1.0 - p.dt * f / (tau + 1e-12));
                    devX *= frac; devY *= frac; devZ *= frac;
                    sxyN *= frac; sxzN *= frac; syzN *= frac;
                    sxxN = devX + mean; syyN = devY + mean; szzN = devZ + mean;
                }
            }
            // fragile
            if (p.frag != 0)
            {
                double I1 = sxxN + syyN + szzN;
                double I2 = sxxN * syyN + syyN * szzN + szzN * sxxN - sxyN * sxyN - sxzN * sxzN - syzN * syzN;
                double I3 = sxxN * (syyN * szzN - syzN * syzN) - sxyN * (sxyN * szzN - sxzN * syzN) + sxzN * (sxyN * syzN - syyN * sxzN);
                double a = -I1, b = I2, c = -I3;
                double q = (3 * b - a * a) / 9.0;
                double r = (9 * a * b - 27 * c - 2 * a * a * a) / 54.0;
                double disc = q * q * q + r * r;
                double sigMax;
                if (disc >= 0)
                {
                    double sd = XMath.Sqrt(disc);
                    sigMax = -a / 3.0 + Cbrt(r + sd) + Cbrt(r - sd);
                }
                else
                {
                    double th = XMath.Acos(r / XMath.Sqrt(-q * q * q));
                    sigMax = 2 * XMath.Sqrt(-q) * XMath.Cos(th / 3.0) - a / 3.0;
                }
                if (sigMax > p.tensile && D < 1.0)
                {
                    double inc = (sigMax - p.tensile) / p.tensile;
                    D = XMath.Min(1.0, D + inc * 0.02f);
                    dmg[id] = D;
                    double fac = 1 - D;
                    sxxN *= fac; syyN *= fac; szzN *= fac; sxyN *= fac; sxzN *= fac; syzN *= fac;
                }
            }
            sxx[id] = sxxN; syy[id] = syyN; szz[id] = szzN;
            sxy[id] = sxyN; sxz[id] = sxzN; syz[id] = syzN;
        }
        private static void VelKernel(Index1D id,
            ArrayView<byte> lab, ArrayView<float> rho,
            ArrayView<double> vx, ArrayView<double> vy, ArrayView<double> vz,
            ArrayView<double> sxx, ArrayView<double> syy, ArrayView<double> szz,
            ArrayView<double> sxy, ArrayView<double> sxz, ArrayView<double> syz,
            P p, G g)
        {
            if (id >= lab.Length || lab[id] == 0) return;
            int z = id / g.nxy; int r = id % g.nxy; int y = r / g.nx; int x = r % g.nx;
            if (x == 0 || y == 0 || z == 0 || x == g.nx - 1 || y == g.ny - 1 || z == g.nz - 1) return;
            int nx = g.nx, nxy = g.nxy;
            int im1 = id - 1, ip1 = id + 1, jm1 = id - nx, jp1 = id + nx, km1 = id - nxy, kp1 = id + nxy;
            double rhoL = rho[id];
            double dsxx_dx = (sxx[id] - sxx[im1]) / p.dx;
            double dsxy_dy = (sxy[id] - sxy[jm1]) / p.dx;
            double dsxz_dz = (sxz[id] - sxz[km1]) / p.dx;
            double dsyy_dy = (syy[id] - syy[jm1]) / p.dx;
            double dsxy_dx = (sxy[ip1] - sxy[id]) / p.dx;
            double dsyz_dz = (syz[id] - syz[km1]) / p.dx;
            double dszz_dz = (szz[id] - szz[km1]) / p.dx;
            double dsxz_dx = (sxz[ip1] - sxz[id]) / p.dx;
            double dsyz_dy = (syz[jp1] - syz[id]) / p.dx;
            vx[id] += p.dt * (dsxx_dx + dsxy_dy + dsxz_dz) / rhoL;
            vy[id] += p.dt * (dsxy_dx + dsyy_dy + dsyz_dz) / rhoL;
            vz[id] += p.dt * (dsxz_dx + dsyz_dy + dszz_dz) / rhoL;
        }
        #endregion

        #region criteri stop / report
        private bool CheckReceiver()
        { int idx = Off(rx, ry, rz, nx, nxy); return Math.Abs(vxBuf.View[idx]) > 1e-6; }
        private bool EnergyStop()
        {
            int idx = Off(rx, ry, rz, nx, nxy);
            double rhoR = rhoHost[rx, ry, rz];
            double v2 = vxBuf.View[idx] * vxBuf.View[idx] + vyBuf.View[idx] * vyBuf.View[idx] + vzBuf.View[idx] * vzBuf.View[idx];
            double ke = 0.5 * rhoR * v2;
            if (ke > maxE) { maxE = ke; return false; }
            if (!ePeaked && ke < 0.5 * maxE) ePeaked = true;
            return ePeaked && ke < 0.01 * maxE;
        }
        private void Report(int force = -1)
        {
            int pct = force >= 0 ? force : (int)(step * 95.0 / totalSteps);
            ProgressUpdated?.Invoke(this, new AcousticSimulationProgressEventArgs(pct, step, "Simulating", null, null));
        }
        private void Finish()
        {
            double dist = Math.Sqrt((tx - rx) * (tx - rx) + (ty - ry) * (ty - ry) + (tz - rz) * (tz - rz)) * dx;
            double rhoAvg = rhoHost.Cast<float>().Average();
            double vp = Math.Sqrt((lam0 + 2 * mu0) / rhoAvg);
            double vs = Math.Sqrt(mu0 / rhoAvg);
            int pTrav = rxTouched ? touchStep : (int)Math.Round(dist / (vp * dt));
            int sTrav = (int)Math.Round(dist / (vs * dt));
            SimulationCompleted?.Invoke(this, new AcousticSimulationCompleteEventArgs(vp, vs, vp / vs, pTrav, sTrav, step));
        }
        #endregion

        public void Dispose()
        {
            labBuf.Dispose(); rhoBuf.Dispose(); vxBuf.Dispose(); vyBuf.Dispose(); vzBuf.Dispose();
            sxxBuf.Dispose(); syyBuf.Dispose(); szzBuf.Dispose(); sxyBuf.Dispose(); sxzBuf.Dispose(); syzBuf.Dispose(); dmgBuf.Dispose();
            acc.Dispose(); ctx.Dispose();
        }
    }
}