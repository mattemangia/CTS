//Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Reflection;
using System.IO;
using System.Threading;
using CTS;

namespace CTS.UI
{
    public partial class SplashScreen : Form
    {
        private bool _hasError = false;
        private string _errorMessage = "";

        public bool HasError => _hasError;
        public string ErrorMessage => _errorMessage;

        public SplashScreen()
        {
            InitializeComponent();
            this.TopMost = true;
            // Make sure we're centered
            this.StartPosition = FormStartPosition.CenterScreen;
            this.labelVersion.Text = $"Version: {Assembly.GetExecutingAssembly().GetName().Version}"+" - BETA";
            // Disable close button
            this.ControlBox = false;
        }

        private async void SplashScreen_Load(object sender, EventArgs e)
        {
            // Ensure UI updates are visible
            this.Refresh();
            await Task.Delay(100);

            // Start checking assemblies
            await CheckAssembliesAsync();

            // Always wait and close, error handling is done differently
            if (!_hasError)
            {
                UpdateStatus("All checks completed successfully!");
                await Task.Delay(1000);
            }
            else
            {
                // Show error message but still close the splash screen
                UpdateStatus("Checks completed with errors!");
                await Task.Delay(500); // Brief delay to show the error status
            }

            // Always close the splash screen
            this.DialogResult = _hasError ? DialogResult.Abort : DialogResult.OK;
            this.Close();
        }

        private async Task CheckAssembliesAsync()
        {
            try
            {
                UpdateStatus("Starting required assembly verification...");
                await Task.Delay(300);

                string applicationPath = Application.StartupPath;

                // Check for the main executable first
                string mainExePath = Path.Combine(applicationPath, "CTS.exe");
                UpdateStatus("Checking main executable: CTS.exe");
                if (!File.Exists(mainExePath))
                {
                    _hasError = true;
                    _errorMessage = "Main executable CTS.exe is missing!";
                    UpdateStatus("✗ Missing: CTS.exe");
                    return;
                }
                else
                {
                    UpdateStatus("✓ Found: CTS.exe");
                }
                await Task.Delay(300);

                // Check for required dependencies
                UpdateStatus("Checking required assemblies...");
                await Task.Delay(300);

                // List of required assemblies   
                // Note: CTS.dll is excluded as CTS is the main executable
                string[] requiredAssemblies = new string[]
                {
                    
                    "CpuMathNative.dll",
                    "Cyotek.Drawing.BitmapFont.dll",
                    "DirectML.Debug.dll",
                    "DirectML.dll",
                    
                    "Google.Protobuf.dll",
                    "ILGPU.Algorithms.dll",
                    "ILGPU.dll",
                    "ILGPU.SharpDX.dll",
                    "Krypton.Docking.dll",
                    "Krypton.Navigator.dll",
                    "Krypton.Ribbon.dll",
                    "Krypton.Toolkit.dll",
                    "Krypton.Workspace.dll",
                    "LdaNative.dll",
                    
                    "MediaFoundation.Net.dll",
                    "Microsoft.Bcl.AsyncInterfaces.dll",
                    "Microsoft.Bcl.Numerics.dll",
                   
                    "Microsoft.Extensions.DependencyInjection.Abstractions.dll",
                    "Microsoft.Extensions.Logging.Abstractions.dll",
                    "Microsoft.ML.Core.dll",
                    "Microsoft.ML.CpuMath.dll",
                    "Microsoft.ML.Data.dll",
                    "Microsoft.ML.DataView.dll",
                    "Microsoft.ML.KMeansClustering.dll",
                    "Microsoft.ML.OnnxRuntime.dll",
                    "Microsoft.ML.OnnxRuntimeGenAI.dll",
                    "Microsoft.ML.OnnxTransformer.dll",
                    "Microsoft.ML.PCA.dll",
                    "Microsoft.ML.StandardTrainers.dll",
                    "Microsoft.ML.Transforms.dll",
                    "Newtonsoft.Json.dll",
                   
                    "onnxruntime-genai.dll",
                    "onnxruntime.dll",
                   
                    "OpenTK.dll",
                    "OpenTK.GLControl.dll",
                    "ortextensions.dll",
                    "SharpDX.D3DCompiler.dll",
                    "SharpDX.Direct3D11.dll",
                    "SharpDX.dll",
                    "SharpDX.DXGI.dll",
                    "SharpDX.Mathematics.dll",
                    "ST.Library.UI.dll",
                    "System.Buffers.dll",
                    "System.CodeDom.dll",
                    "System.Collections.Immutable.dll",
                    "System.Diagnostics.DiagnosticSource.dll",
                    "System.IO.Pipelines.dll",
                    "System.Memory.dll",
                    "System.Numerics.Tensors.dll",
                    "System.Numerics.Vectors.dll",
                    "System.Reflection.Metadata.dll",
                    "System.Reflection.TypeExtensions.dll",
                    "System.Runtime.CompilerServices.Unsafe.dll",
                    "System.Text.Encodings.Web.dll",
                    "System.Text.Json.dll",
                    "System.Threading.Channels.dll",
                    "System.Threading.Tasks.Extensions.dll"
                };

                int checkedCount = 0;
                List<string> missingAssemblies = new List<string>();
                List<string> invalidAssemblies = new List<string>();

                foreach (string requiredAssembly in requiredAssemblies)
                {
                    checkedCount++;
                    UpdateStatus($"Checking {checkedCount}/{requiredAssemblies.Length}: {requiredAssembly}...");

                    string fullPath = Path.Combine(applicationPath, requiredAssembly);

                    if (!File.Exists(fullPath))
                    {
                        missingAssemblies.Add(requiredAssembly);
                        UpdateStatus($"✗ Missing: {requiredAssembly}");
                    }
                    else
                    {
                        try
                        {
                            // Try to load the assembly to verify it's valid
                            Assembly.LoadFile(fullPath);
                            UpdateStatus($"✓ Valid: {requiredAssembly}");
                        }
                        catch (BadImageFormatException)
                        {
                            // If it's not a .NET assembly, that's OK for some files
                            UpdateStatus($"✓ Exists: {requiredAssembly} (native)");
                        }
                        catch (Exception ex)
                        {
                            invalidAssemblies.Add($"{requiredAssembly}: {ex.Message}");
                            UpdateStatus($"✗ Invalid: {requiredAssembly}");
                        }
                    }

                    await Task.Delay(50); // Small delay to show progress
                }

                // Report results
                if (missingAssemblies.Count > 0 || invalidAssemblies.Count > 0)
                {
                    _hasError = true;
                    StringBuilder errorMsg = new StringBuilder("Required assemblies check failed:\n\n");

                    if (missingAssemblies.Count > 0)
                    {
                        errorMsg.AppendLine("Missing assemblies:");
                        foreach (string missing in missingAssemblies)
                        {
                            errorMsg.AppendLine($"- {missing}");
                        }
                        errorMsg.AppendLine();
                    }

                    if (invalidAssemblies.Count > 0)
                    {
                        errorMsg.AppendLine("Invalid assemblies:");
                        foreach (string invalid in invalidAssemblies)
                        {
                            errorMsg.AppendLine($"- {invalid}");
                        }
                    }

                    _errorMessage = errorMsg.ToString();
                    UpdateStatus($"✗ {missingAssemblies.Count} missing, {invalidAssemblies.Count} invalid");
                    return;
                }

                UpdateStatus($"✓ All {requiredAssemblies.Length} required assemblies verified!");

                UpdateStatus("✓ All dependencies verified!");
                await Task.Delay(300);
            }
            catch (Exception ex)
            {
                _hasError = true;
                _errorMessage = $"Assembly verification failed: {ex.Message}";
                UpdateStatus($"✗ Verification failed: {ex.Message}");
            }
        }

        private void UpdateStatus(string message)
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new Action<string>(UpdateStatus), message);
            }
            else
            {
               
                if (this.Controls.ContainsKey("statusLabel"))
                {
                    Label statusLabel = this.Controls["statusLabel"] as Label;
                    if (statusLabel != null)
                    {
                        statusLabel.Text = message;
                        statusLabel.Refresh();
                    }
                }

                // Log the status
                Logger.Log($"Dependencies Checker: {message}");
            }
        }
    }
}