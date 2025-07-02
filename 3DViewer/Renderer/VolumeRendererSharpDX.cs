using CTS.SharpDXIntegration;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;
using Format = SharpDX.DXGI.Format;
using Timer = System.Windows.Forms.Timer;

namespace CTS
{
    public class SharpDXVolumeRenderer : IDisposable
    {
        #region Fields

        private bool debugMode = false;
        private readonly object textureLock = new object();
        private MainForm mainForm;
        private Panel renderPanel;
        private SharpDXControlPanel controlPanel;
        private bool clippingPlaneEnabled = false;
        private Vector3 clippingPlaneNormal = Vector3.UnitX;
        private float clippingPlaneDistance = 0.5f;
        private bool clippingPlaneMirror = false;
        public void SetControlPanel(SharpDXControlPanel panel)
        {
            controlPanel = panel;
        }

        // Core DirectX objects
        private Device device;

        private DeviceContext context;
        private SwapChain swapChain;
        private RenderTargetView renderTargetView;
        private Texture2D depthBuffer;
        private DepthStencilView depthView;

        // Volume dimensions
        private int volW, volH, volD;

        // Volume data textures
        private Texture3D volumeTexture;

        private ShaderResourceView volumeSRV;
        private Texture3D labelTexture;
        private ShaderResourceView labelSRV;

        // Cube geometry
        private Buffer cubeVertexBuffer;

        private Buffer cubeIndexBuffer;
        private int cubeIndexCount = 36;

        // Rendering states
        private RasterizerState solidRasterState;

        private RasterizerState wireframeRasterState;
        private BlendState alphaBlendState;
        private SamplerState linearSampler;
        private SamplerState pointSampler;
        public bool NeedsRender { get; set; } = true;

        // Camera parameters
        private float cameraYaw = 0.8f;

        private float cameraPitch = 0.6f;
        private float cameraDistance = 500.0f;
        private Vector3 panOffset = Vector3.Zero;

        // Shaders for volume rendering
        private VertexShader volumeVertexShader;

        private PixelShader volumePixelShader;
        private InputLayout inputLayout;
        private Buffer constantBuffer;

        // Mouse interaction
        private bool isDragging = false;

        private System.Drawing.Point lastMousePosition;
        private bool isPanning = false;

        //Render Streamin Cancellation Token
        private CancellationTokenSource initStreamingCts;

        private Task streamingInitTask;
        private ProgressForm streamingProgressForm;

        // Label properties
        private const int MAX_LABELS = 256;

        private bool[] labelVisible = new bool[MAX_LABELS];
        private float[] labelOpacity = new float[MAX_LABELS];
        private Texture1D labelVisibilityTexture;
        private ShaderResourceView labelVisibilitySRV;
        private Texture1D labelOpacityTexture;
        private ShaderResourceView labelOpacitySRV;
        private Texture1D materialColorTexture;
        private ShaderResourceView materialColorSRV;
        private Texture1D colorMapTexture;
        private ShaderResourceView colorMapSRV;

        //RenderTimer
        private bool isRendering = false;

        private DateTime lastRenderTime = DateTime.MinValue;
        private int renderFailCount = 0;
        private const int MAX_FAILURES = 3;
        private Timer renderTimer;

        // Volume rendering parameters
        private float minThresholdNorm = 0.1f;

        private float maxThresholdNorm = 1.0f;
        private float stepSize = 1.0f;
        private bool showGrayscale = true;
        private int colorMapIndex = 0;
        private float sliceBorderThickness = 0.02f;

        // Slices
        private int sliceX, sliceY, sliceZ;

        private bool showSlices = false;

        // Cutting planes
        private bool cutXEnabled = false;

        private bool cutYEnabled = false;
        private bool cutZEnabled = false;
        private float cutXPosition = 0.5f;
        private float cutYPosition = 0.5f;
        private float cutZPosition = 0.5f;
        private float cutXDirection = 1.0f;  // 1.0 = forward, -1.0 = backward
        private float cutYDirection = 1.0f;
        private float cutZDirection = 1.0f;

        //Measures
        private bool measurementMode = false;

        public List<MeasurementLine> measurements = new List<MeasurementLine>();
        private bool isDrawingMeasurement = false;
        private SharpDX.Vector3 measureStartPoint;
        private SharpDX.Vector3 measureEndPoint;
        private PictureBox measurementOverlay;
        private MeasurementTextRenderer textRenderer;

        // Frame counter for logging
        private int frameCount = 0;

        private PictureBox scaleBarPictureBox;

        //DirectX measurements
        private Buffer lineVertexBuffer;

        private VertexShader lineVertexShader;
        private PixelShader linePixelShader;
        private InputLayout lineInputLayout;
        private List<Vector3> measurementVertices = new List<Vector3>();
        private List<Vector4> measurementColors = new List<Vector4>();

        // LOD system for large datasets
        private bool useLodSystem = true;

        private int currentLodLevel = 0;
        private const int MAX_LOD_LEVELS = 3;
        private float[] lodStepSizes = new float[] { 0.5f, 1.0f, 2.0f, 4.0f }; // Different step sizes for each LOD level
        private Texture3D[] lodVolumeTextures = new Texture3D[MAX_LOD_LEVELS + 1];
        private ShaderResourceView[] lodVolumeSRVs = new ShaderResourceView[MAX_LOD_LEVELS + 1];

        private bool showXSlice = false;
        private bool showYSlice = false;
        private bool showZSlice = false;

        public bool ShowXSlice
        {
            get { return showXSlice; }
            set
            {
                showXSlice = value;
                NeedsRender = true;
            }
        }

        public bool ShowYSlice
        {
            get { return showYSlice; }
            set
            {
                showYSlice = value;
                NeedsRender = true;
            }
        }

        public bool ShowZSlice
        {
            get { return showZSlice; }
            set
            {
                showZSlice = value;
                NeedsRender = true;
            }
        }

        public bool ShowOrthoslices
        {
            get { return showXSlice || showYSlice || showZSlice; }
            set
            {
                // When setting all slices at once
                showXSlice = value;
                showYSlice = value;
                showZSlice = value;
                NeedsRender = true;
            }
        }

        //Streaming rendering
        private bool useStreamingRenderer = false;

        private Dictionary<Vector3, Texture3D> loadedChunks = new Dictionary<Vector3, Texture3D>();
        private Dictionary<Vector3, ShaderResourceView> loadedChunkSRVs = new Dictionary<Vector3, ShaderResourceView>();
        private Queue<Vector3> chunkLoadQueue = new Queue<Vector3>();
        private HashSet<Vector3> visibleChunks = new HashSet<Vector3>();
        private Timer chunkLoadTimer;
        private const int MAX_LOADED_CHUNKS = 32; // Adjust based on GPU memory
        private Vector3 cameraPositionPrevious;
        private float cameraDistancePrevious;
        private readonly object chunkLock = new object();

        private Texture3D[] lodTextures = new Texture3D[5]; // Different LOD levels (0-4)
        private ShaderResourceView[] lodSRVs = new ShaderResourceView[5];
        private int currentStreamingLOD = 0;
        private bool isInitializingStreaming = false;

        public bool UseStreamingRenderer
        {
            get { return useStreamingRenderer; }
            set
            {
                if (useStreamingRenderer != value)
                {
                    Logger.Log($"[SharpDXVolumeRenderer] Switching streaming renderer: {useStreamingRenderer} -> {value}");

                    // If turning OFF streaming render, restore original state
                    if (useStreamingRenderer && !value)
                    {
                        // Cancel any ongoing initialization task
                        if (initStreamingCts != null && !initStreamingCts.IsCancellationRequested)
                        {
                            initStreamingCts.Cancel();
                        }

                        // Close progress form if open
                        if (streamingProgressForm != null && !streamingProgressForm.IsDisposed)
                        {
                            streamingProgressForm.Close();
                            streamingProgressForm = null;
                        }

                        // Clean up streaming resources
                        DisposeStreamingResources();

                        // Force recreation of the original textures
                        if (mainForm.volumeData != null)
                        {
                            Logger.Log("[SharpDXVolumeRenderer] Recreating standard textures after disabling streaming");

                            try
                            {
                                // Clean up existing textures first
                                Utilities.Dispose(ref volumeSRV);
                                Utilities.Dispose(ref volumeTexture);

                                // Create new volume texture from the volume data
                                volumeTexture = CreateTexture3DFromChunkedVolume((ChunkedVolume)mainForm.volumeData, Format.R8_UNorm);

                                if (volumeTexture != null)
                                {
                                    ShaderResourceViewDescription srvDesc = new ShaderResourceViewDescription
                                    {
                                        Format = Format.R8_UNorm,
                                        Dimension = ShaderResourceViewDimension.Texture3D,
                                        Texture3D = new ShaderResourceViewDescription.Texture3DResource
                                        {
                                            MipLevels = 1,
                                            MostDetailedMip = 0
                                        }
                                    };

                                    volumeSRV = new ShaderResourceView(device, volumeTexture, srvDesc);
                                    Logger.Log("[SharpDXVolumeRenderer] Standard volume texture recreated successfully");
                                }
                                else
                                {
                                    Logger.Log("[SharpDXVolumeRenderer] ERROR: Failed to recreate volume texture!");
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Log($"[SharpDXVolumeRenderer] Error recreating standard textures: {ex.Message}");
                                // Even if recreation fails, continue with disabling streaming
                            }
                        }
                    }

                    // Store original volume texture before switching to streaming mode
                    Texture3D originalVolumeTex = null;
                    ShaderResourceView originalVolumeSRV = null;
                    if (!useStreamingRenderer && value && volumeTexture != null)
                    {
                        originalVolumeTex = volumeTexture;
                        originalVolumeSRV = volumeSRV;
                    }

                    // Update the flag immediately
                    useStreamingRenderer = value;

                    if (useStreamingRenderer)
                    {
                        // Initialize streaming textures ASYNCHRONOUSLY
                        InitializeStreamingRendererAsync();
                    }

                    NeedsRender = true;
                }
            }
        }

        // Constant buffer structure - matches shader layout exactly
        [StructLayout(LayoutKind.Sequential)]
        private struct ConstantBufferData
        {
            public Matrix WorldViewProj;
            public Matrix InvViewMatrix;
            public Vector4 Thresholds;  // x=min, y=max, z=stepSize, w=showGrayscale
            public Vector4 Dimensions;  // xyz=volume dimensions, w=unused
            public Vector4 SliceCoords; // xyz=slice positions, w=slice visibility flags
            public Vector4 CameraPosition; // Camera position for ray origin calculation
            public Vector4 ColorMapParams; // x=colorMapIndex, y=slice border thickness, z,w=unused
            public Vector4 CutPlaneX; // x=enabled, y=direction, z=position, w=unused
            public Vector4 CutPlaneY; // x=enabled, y=direction, z=position, w=unused
            public Vector4 CutPlaneZ; // x=enabled, y=direction, z=position, w=unused                                    
            public Vector4 ClippingPlane1; // x=enabled, yzw=normal
            public Vector4 ClippingPlane2; // x=distance, y=mirror, z,w=unused
        }

        #endregion Fields

        #region Properties
        public bool ClippingPlaneEnabled
        {
            get { return clippingPlaneEnabled; }
            set
            {
                clippingPlaneEnabled = value;
                NeedsRender = true;
            }
        }

        public Vector3 ClippingPlaneNormal
        {
            get { return clippingPlaneNormal; }
            set
            {
                clippingPlaneNormal = value;
                NeedsRender = true;
            }
        }

        public float ClippingPlaneDistance
        {
            get { return clippingPlaneDistance; }
            set
            {
                clippingPlaneDistance = value;
                NeedsRender = true;
            }
        }

        public bool ClippingPlaneMirror
        {
            get { return clippingPlaneMirror; }
            set
            {
                clippingPlaneMirror = value;
                NeedsRender = true;
            }
        }
        public bool DebugMode
        {
            get { return debugMode; }
            set
            {
                debugMode = value;
                NeedsRender = true; // Mark that rendering is needed
            }
        }

        public int MinThreshold
        {
            get { return (int)(minThresholdNorm * 255f); }
            set
            {
                minThresholdNorm = Math.Max(0.0f, Math.Min(1.0f, value / 255f));
                NeedsRender = true; // Mark that rendering is needed
            }
        }

        public int MaxThreshold
        {
            get { return (int)(maxThresholdNorm * 255f); }
            set
            {
                maxThresholdNorm = Math.Max(0.0f, Math.Min(1.0f, value / 255f));
                NeedsRender = true; // Mark that rendering is needed
            }
        }

        public bool ShowGrayscale
        {
            get { return showGrayscale; }
            set
            {
                showGrayscale = value;
                NeedsRender = true; // Mark that rendering is needed
            }
        }

        public int ColorMapIndex
        {
            get { return colorMapIndex; }
            set
            {
                colorMapIndex = value;
                NeedsRender = true;
            }
        }

        public int SliceX => sliceX;
        public int SliceY => sliceY;
        public int SliceZ => sliceZ;

        public bool CutXEnabled
        {
            get { return cutXEnabled; }
            set
            {
                cutXEnabled = value;
                NeedsRender = true;
            }
        }

        public bool CutYEnabled
        {
            get { return cutYEnabled; }
            set
            {
                cutYEnabled = value;
                NeedsRender = true;
            }
        }

        public bool CutZEnabled
        {
            get { return cutZEnabled; }
            set
            {
                cutZEnabled = value;
                NeedsRender = true;
            }
        }

        public float CutXPosition
        {
            get { return cutXPosition; }
            set
            {
                cutXPosition = Math.Max(0.0f, Math.Min(1.0f, value));
                NeedsRender = true;
            }
        }

        public float CutYPosition
        {
            get { return cutYPosition; }
            set
            {
                cutYPosition = Math.Max(0.0f, Math.Min(1.0f, value));
                NeedsRender = true;
            }
        }

        public float CutZPosition
        {
            get { return cutZPosition; }
            set
            {
                cutZPosition = Math.Max(0.0f, Math.Min(1.0f, value));
                NeedsRender = true;
            }
        }

        public float CutXDirection
        {
            get { return cutXDirection; }
            set
            {
                cutXDirection = (value >= 0) ? 1.0f : -1.0f;
                NeedsRender = true;
            }
        }

        public float CutYDirection
        {
            get { return cutYDirection; }
            set
            {
                cutYDirection = (value >= 0) ? 1.0f : -1.0f;
                NeedsRender = true;
            }
        }

        public float CutZDirection
        {
            get { return cutZDirection; }
            set
            {
                cutZDirection = (value >= 0) ? 1.0f : -1.0f;
                NeedsRender = true;
            }
        }

        public bool UseLodSystem
        {
            get { return useLodSystem; }
            set
            {
                useLodSystem = value;
                NeedsRender = true;
            }
        }

        #endregion Properties

        #region Initialization

        public SharpDXVolumeRenderer(MainForm mainForm, Panel panel)
        {
            this.mainForm = mainForm;
            this.renderPanel = panel;

            // Get volume dimensions
            volW = mainForm.GetWidth();
            volH = mainForm.GetHeight();
            volD = mainForm.GetDepth();

            // Default slice positions
            sliceX = volW / 2;
            sliceY = volH / 2;
            sliceZ = volD / 2;

            // Set initial camera distance based on volume size
            float maxDim = Math.Max(volW, Math.Max(volH, volD));
            cameraDistance = maxDim * 2.0f;

            // Set default visibility and opacity for all materials
            for (int i = 0; i < MAX_LABELS; i++)
            {
                labelVisible[i] = true;
                labelOpacity[i] = 1.0f;
            }

            // Special case for exterior (material 0)
            labelVisible[0] = false;
            labelOpacity[0] = 0.0f;

            // Register mouse handlers
            renderPanel.MouseDown += OnMouseDown;
            renderPanel.MouseMove += OnMouseMove;
            renderPanel.MouseUp += OnMouseUp;
            renderPanel.MouseWheel += OnMouseWheel;
            renderPanel.BackColor = System.Drawing.Color.Black;

            try
            {
                // Initialize DirectX
                CreateDeviceAndSwapChain();
                CreateRenderTargets();
                CreateRenderStates();
                CreateShaders();
                CreateCubeGeometry();

                // Check the dataset size to determine rendering approach
                long volumeSizeBytes = (long)volW * volH * volD;
                bool isLargeDataset = volumeSizeBytes > 8L * 1024L * 1024L * 1024L; // 8GB threshold

                if (isLargeDataset)
                {
                    // For large datasets, enable streaming renderer by default
                    useStreamingRenderer = true;
                    Logger.Log($"[SharpDXVolumeRenderer] Large dataset detected ({volumeSizeBytes / (1024 * 1024 * 1024)}GB), using streaming renderer");
                }

                // Create textures - modified to handle streaming for large datasets
                CreateVolumeTextures();
                CreateLabelTextures();
                CreateMaterialColorTexture();
                CreateColorMapTexture();
                CreateLodTextures();

                // Initialize streaming renderer if enabled
                if (useStreamingRenderer)
                {
                    InitializeStreamingRenderer();
                }

                CreateMeasurementResources();
                renderTimer = new Timer();
                renderTimer.Interval = 50; // 20 FPS for real-time updates
                renderTimer.Tick += (s, e) =>
                {
                    if (NeedsRender && !isRendering)
                    {
                        Render();
                    }
                };
                renderTimer.Start();
                Vector3 volumeCenter = new Vector3(volW / 2.0f, volH / 2.0f, volD / 2.0f);
                cameraYaw = 0.8f; // Approximately 45 degrees
                cameraPitch = 0.6f; // Slightly elevated view
                cameraDistance = Math.Max(volW, Math.Max(volH, volD)) * 2.0f;
                panOffset = Vector3.Zero;
                NeedsRender = true;
                textRenderer = new MeasurementTextRenderer(renderPanel);

                Logger.Log($"[SharpDXVolumeRenderer] Successfully created {volW}x{volH}x{volD} volume renderer");
            }
            catch (Exception ex)
            {
                Logger.Log("[SharpDXVolumeRenderer] Initialization error: " + ex.Message);
                MessageBox.Show("Failed to initialize volume renderer: " + ex.Message);
            }
        }

        private void CreateDeviceAndSwapChain()
        {
            try
            {
                // Ensure panel size is valid
                int width = Math.Max(1, renderPanel.ClientSize.Width);
                int height = Math.Max(1, renderPanel.ClientSize.Height);

                // Create SwapChain description with improved settings for stability
                SwapChainDescription swapChainDesc = new SwapChainDescription
                {
                    BufferCount = 2, // Double buffering for better stability
                    ModeDescription = new ModeDescription(width, height, new Rational(60, 1), Format.B8G8R8A8_UNorm), // BGRA format can be more efficient
                    IsWindowed = true,
                    OutputHandle = renderPanel.Handle,
                    SampleDescription = new SampleDescription(1, 0), // No MSAA
                    SwapEffect = SwapEffect.Discard, // Discard is more compatible with older hardware
                    Usage = Usage.RenderTargetOutput,
                    Flags = SwapChainFlags.AllowModeSwitch, // Add mode switch to support alpha rendering
                };

                // Try different device options in case of failure
                DeviceCreationFlags deviceFlags = DeviceCreationFlags.BgraSupport; // Add BGRA support

                try
                {
                    // Create device with no debugging for better performance
                    Device.CreateWithSwapChain(
                        DriverType.Hardware,
                        deviceFlags,
                        new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0, FeatureLevel.Level_9_3 },
                        swapChainDesc,
                        out device,
                        out swapChain);

                    Logger.Log("[SharpDXVolumeRenderer] Created device with hardware acceleration");
                }
                catch (Exception ex)
                {
                    Logger.Log("[SharpDXVolumeRenderer] Failed to create hardware device: " + ex.Message);

                    // Try again with reference driver
                    try
                    {
                        Device.CreateWithSwapChain(
                            DriverType.Reference, // Software reference renderer
                            deviceFlags,
                            new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0, FeatureLevel.Level_9_3 },
                            swapChainDesc,
                            out device,
                            out swapChain);

                        Logger.Log("[SharpDXVolumeRenderer] Created device with reference renderer");
                    }
                    catch (Exception refEx)
                    {
                        Logger.Log("[SharpDXVolumeRenderer] Failed to create reference device: " + refEx.Message);

                        // Last resort - WARP software renderer
                        try
                        {
                            Device.CreateWithSwapChain(
                                DriverType.Warp, // WARP software renderer
                                deviceFlags,
                                new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0, FeatureLevel.Level_9_3 },
                                swapChainDesc,
                                out device,
                                out swapChain);

                            Logger.Log("[SharpDXVolumeRenderer] Created device with WARP (software) renderer");
                        }
                        catch (Exception warpEx)
                        {
                            Logger.Log("[SharpDXVolumeRenderer] Failed to create any device: " + warpEx.Message);
                            throw new Exception("Failed to initialize graphics device. Please update your graphics drivers.", warpEx);
                        }
                    }
                }

                context = device.ImmediateContext;

                // Prevent Alt+Enter fullscreen toggle
                using (Factory factory = swapChain.GetParent<Factory>())
                {
                    factory.MakeWindowAssociation(renderPanel.Handle, WindowAssociationFlags.IgnoreAltEnter);
                }

                Logger.Log("[SharpDXVolumeRenderer] Device and SwapChain created successfully");
            }
            catch (Exception ex)
            {
                Logger.Log("[SharpDXVolumeRenderer] Error creating device: " + ex.Message);
                throw;
            }
        }

        private void CreateRenderTargets()
        {
            try
            {
                // Clean up old render targets
                Utilities.Dispose(ref renderTargetView);
                Utilities.Dispose(ref depthView);
                Utilities.Dispose(ref depthBuffer);

                // Ensure valid dimensions
                int width = Math.Max(1, renderPanel.ClientSize.Width);
                int height = Math.Max(1, renderPanel.ClientSize.Height);

                // Resize swapchain buffers if needed
                swapChain.ResizeBuffers(
                    2, // Double buffering
                    width,
                    height,
                    Format.B8G8R8A8_UNorm, // RGBA format for proper transparency
                    SwapChainFlags.None);

                // Create render target view from backbuffer
                using (Texture2D backBuffer = Texture2D.FromSwapChain<Texture2D>(swapChain, 0))
                {
                    renderTargetView = new RenderTargetView(device, backBuffer);
                }

                // Create depth buffer
                Texture2DDescription depthDesc = new Texture2DDescription
                {
                    Width = width,
                    Height = height,
                    ArraySize = 1,
                    BindFlags = BindFlags.DepthStencil,
                    CpuAccessFlags = CpuAccessFlags.None,
                    Format = Format.D24_UNorm_S8_UInt,
                    MipLevels = 1,
                    OptionFlags = ResourceOptionFlags.None,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default
                };

                depthBuffer = new Texture2D(device, depthDesc);
                depthView = new DepthStencilView(device, depthBuffer);

                Logger.Log("[SharpDXVolumeRenderer] Render targets recreated successfully");
            }
            catch (Exception ex)
            {
                Logger.Log("[SharpDXVolumeRenderer] Error creating render targets: " + ex.Message);
                throw;
            }
        }

        private void CreateRenderStates()
        {
            try
            {
                // Create solid rasterizer state
                RasterizerStateDescription solidRsDesc = new RasterizerStateDescription
                {
                    CullMode = CullMode.Back,
                    FillMode = FillMode.Solid,
                    IsDepthClipEnabled = true,
                    IsFrontCounterClockwise = false
                };
                solidRasterState = new RasterizerState(device, solidRsDesc);

                // Create wireframe rasterizer state
                RasterizerStateDescription wireframeRsDesc = new RasterizerStateDescription
                {
                    CullMode = CullMode.None,
                    FillMode = FillMode.Wireframe,
                    IsDepthClipEnabled = true,
                    IsFrontCounterClockwise = false
                };
                wireframeRasterState = new RasterizerState(device, wireframeRsDesc);

                // Create alpha blend state for volume rendering
                BlendStateDescription blendDesc = new BlendStateDescription();
                blendDesc.RenderTarget[0].IsBlendEnabled = true;
                blendDesc.RenderTarget[0].SourceBlend = BlendOption.SourceAlpha;
                blendDesc.RenderTarget[0].DestinationBlend = BlendOption.InverseSourceAlpha;
                blendDesc.RenderTarget[0].BlendOperation = BlendOperation.Add;
                blendDesc.RenderTarget[0].SourceAlphaBlend = BlendOption.One;
                blendDesc.RenderTarget[0].DestinationAlphaBlend = BlendOption.InverseSourceAlpha;
                blendDesc.RenderTarget[0].AlphaBlendOperation = BlendOperation.Add;
                blendDesc.RenderTarget[0].RenderTargetWriteMask = ColorWriteMaskFlags.All;
                blendDesc.AlphaToCoverageEnable = false;
                blendDesc.IndependentBlendEnable = false;
                alphaBlendState = new BlendState(device, blendDesc);

                // Create samplers
                SamplerStateDescription linearSampDesc = new SamplerStateDescription
                {
                    Filter = Filter.MinMagMipLinear,
                    AddressU = TextureAddressMode.Clamp,
                    AddressV = TextureAddressMode.Clamp,
                    AddressW = TextureAddressMode.Clamp,
                    ComparisonFunction = Comparison.Never,
                    MinimumLod = 0,
                    MaximumLod = float.MaxValue,
                    MaximumAnisotropy = 1
                };
                linearSampler = new SamplerState(device, linearSampDesc);

                SamplerStateDescription pointSampDesc = new SamplerStateDescription
                {
                    Filter = Filter.MinMagMipPoint,
                    AddressU = TextureAddressMode.Clamp,
                    AddressV = TextureAddressMode.Clamp,
                    AddressW = TextureAddressMode.Clamp,
                    ComparisonFunction = Comparison.Never,
                    MinimumLod = 0,
                    MaximumLod = float.MaxValue,
                    MaximumAnisotropy = 1
                };
                pointSampler = new SamplerState(device, pointSampDesc);

                Logger.Log("[SharpDXVolumeRenderer] Render states created successfully");
            }
            catch (Exception ex)
            {
                Logger.Log("[SharpDXVolumeRenderer] Error creating render states: " + ex.Message);
                throw;
            }
        }

        private void CreateShaders()
        {
            try
            {
                // Load shader code from a string
                string shaderCode = LoadVolumeRenderingShader();

                // First, compile the vertex shader
                using (var vertexShaderBytecode = SharpDX.D3DCompiler.ShaderBytecode.Compile(
                    shaderCode, "VSMain", "vs_5_0", SharpDX.D3DCompiler.ShaderFlags.Debug))
                {
                    if (vertexShaderBytecode.HasErrors)
                    {
                        Logger.Log("[SharpDXVolumeRenderer] VS compilation error: " + vertexShaderBytecode.Message);
                        throw new Exception("Vertex shader compilation failed: " + vertexShaderBytecode.Message);
                    }

                    volumeVertexShader = new VertexShader(device, vertexShaderBytecode);

                    // Create input layout - MUST match vertex structure
                    InputElement[] inputElements = new[] {
                        new InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0)
                    };

                    inputLayout = new InputLayout(device, vertexShaderBytecode, inputElements);
                }

                // Then, compile the pixel shader
                using (var pixelShaderBytecode = SharpDX.D3DCompiler.ShaderBytecode.Compile(
                    shaderCode, "PSMain", "ps_5_0", SharpDX.D3DCompiler.ShaderFlags.Debug))
                {
                    if (pixelShaderBytecode.HasErrors)
                    {
                        Logger.Log("[SharpDXVolumeRenderer] PS compilation error: " + pixelShaderBytecode.Message);
                        throw new Exception("Pixel shader compilation failed: " + pixelShaderBytecode.Message);
                    }

                    volumePixelShader = new PixelShader(device, pixelShaderBytecode);
                }

                // Create constant buffer for shader parameters
                constantBuffer = new Buffer(device, Utilities.SizeOf<ConstantBufferData>(),
                    ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);

                Logger.Log("[SharpDXVolumeRenderer] Shaders created successfully");
            }
            catch (Exception ex)
            {
                Logger.Log("[SharpDXVolumeRenderer] Error creating shaders: " + ex.Message);
                throw;
            }
        }

        private string LoadVolumeRenderingShader()
        {
            return @"
// Volume rendering shader with clipping plane support
cbuffer ConstantBuffer : register(b0)
{
    matrix worldViewProj;        // World-view-projection matrix
    matrix invViewMatrix;        // Inverse view matrix for ray calculation
    float4 thresholds;           // x=min, y=max, z=stepSize, w=showGrayscale
    float4 dimensions;           // xyz=volume dimensions, w=unused
    float4 sliceCoords;          // xyz=slice positions normalized (0-1), w=slice visibility flags
    float4 cameraPosition;       // Camera position in world space
    float4 colorMapIndex;        // x=colorMapIndex, y=slice border thickness, z,w=unused
    float4 cutPlaneX;            // x=enabled, y=direction(1=forward,-1=backward), z=position, w=unused
    float4 cutPlaneY;            // x=enabled, y=direction(1=forward,-1=backward), z=position, w=unused
    float4 cutPlaneZ;            // x=enabled, y=direction(1=forward,-1=backward), z=position, w=unused
    float4 clippingPlane1;       // x=enabled, yzw=normal
    float4 clippingPlane2;       // x=distance, y=mirror, z,w=unused
};

// Material properties
Texture1D<float> materialVisibility : register(t0);    // Whether material is visible (0=hidden, 1=visible)
Texture1D<float> materialOpacity : register(t1);       // Material opacity (0-1)
Texture3D<float> volumeTexture : register(t2);         // Grayscale volume data (0-1)
Texture3D<float> labelTexture : register(t3);          // Label/material volume as float
Texture1D<float4> materialColors : register(t4);       // Material color lookup texture
Texture1D<float4> colorMaps : register(t5);            // Color maps for grayscale visualization

// Samplers
SamplerState linearSampler : register(s0);
SamplerState pointSampler : register(s1);

// Structures
struct VS_INPUT
{
    float3 Position : POSITION;
};

struct VS_OUTPUT
{
    float4 Position : SV_POSITION;
    float3 WorldPos : POSITION0;
    float3 TexCoord : TEXCOORD0;
};

// Ray-box intersection function
bool IntersectBox(float3 rayOrigin, float3 rayDir, float3 boxMin, float3 boxMax,
                 out float tNear, out float tFar)
{
    // Compute intersection with all planes
    float3 invRayDir = 1.0 / (rayDir + 0.0000001f); // Avoid division by zero
    float3 t1 = (boxMin - rayOrigin) * invRayDir;
    float3 t2 = (boxMax - rayOrigin) * invRayDir;

    // Sort t values
    float3 tMin = min(t1, t2);
    float3 tMax = max(t1, t2);

    // Find the largest tMin and smallest tMax
    tNear = max(max(tMin.x, tMin.y), tMin.z);
    tFar = min(min(tMax.x, tMax.y), tMax.z);

    return tFar > tNear && tFar > 0.0;
}

// Vertex shader
VS_OUTPUT VSMain(VS_INPUT input)
{
    VS_OUTPUT output;

    // Transform vertex to clip space
    output.Position = mul(float4(input.Position, 1.0), worldViewProj);

    // Pass through world position
    output.WorldPos = input.Position;

    // Normalize texture coordinates to [0,1]
    output.TexCoord = input.Position / dimensions.xyz;

    return output;
}

// Helper function for slice planes with individual slice visibility
bool IsOnSlicePlane(float3 pos, float3 slicePos, float epsilon, out int sliceType)
{
    // sliceCoords.w now contains a bit field for slice visibility
    // We need to convert to int for bitwise operations
    int sliceFlags = (int)(sliceCoords.w + 0.5); // Round to nearest int

    // Check which slices are enabled using integer bitwise operations
    bool xSliceEnabled = (sliceFlags & 1) != 0;
    bool ySliceEnabled = (sliceFlags & 2) != 0;
    bool zSliceEnabled = (sliceFlags & 4) != 0;

    // Check if the position is on any of the three slice planes
    bool onXSlice = xSliceEnabled && abs(pos.x - slicePos.x * dimensions.x) < epsilon;
    bool onYSlice = ySliceEnabled && abs(pos.y - slicePos.y * dimensions.y) < epsilon;
    bool onZSlice = zSliceEnabled && abs(pos.z - slicePos.z * dimensions.z) < epsilon;

    // Set slice type (1=X, 2=Y, 3=Z)
    sliceType = 0;
    if (onXSlice) sliceType = 1;
    else if (onYSlice) sliceType = 2;
    else if (onZSlice) sliceType = 3;

    return (sliceType > 0);
}

// Helper function to check if a point is within the slice border
bool IsOnSliceBorder(float3 pos, float3 slicePos, float thickness, int sliceType)
{
    if (sliceType == 1) // X slice (YZ plane)
    {
        float y = pos.y / dimensions.y;
        float z = pos.z / dimensions.z;
        return (y < thickness || y > 1.0 - thickness || z < thickness || z > 1.0 - thickness);
    }
    else if (sliceType == 2) // Y slice (XZ plane)
    {
        float x = pos.x / dimensions.x;
        float z = pos.z / dimensions.z;
        return (x < thickness || x > 1.0 - thickness || z < thickness || z > 1.0 - thickness);
    }
    else if (sliceType == 3) // Z slice (XY plane)
    {
        float x = pos.x / dimensions.x;
        float y = pos.y / dimensions.y;
        return (x < thickness || x > 1.0 - thickness || y < thickness || y > 1.0 - thickness);
    }
    return false;
}

// Check if a point is cut by any cutting plane
bool IsCutByPlane(float3 pos)
{
    // Check X cutting plane
    if (cutPlaneX.x > 0.5) { // If enabled
        if (cutPlaneX.y > 0) { // Forward cut
            if (pos.x > cutPlaneX.z * dimensions.x) return true;
        } else { // Backward cut
            if (pos.x < cutPlaneX.z * dimensions.x) return true;
        }
    }

    // Check Y cutting plane
    if (cutPlaneY.x > 0.5) { // If enabled
        if (cutPlaneY.y > 0) { // Forward cut
            if (pos.y > cutPlaneY.z * dimensions.y) return true;
        } else { // Backward cut
            if (pos.y < cutPlaneY.z * dimensions.y) return true;
        }
    }

    // Check Z cutting plane
    if (cutPlaneZ.x > 0.5) { // If enabled
        if (cutPlaneZ.y > 0) { // Forward cut
            if (pos.z > cutPlaneZ.z * dimensions.z) return true;
        } else { // Backward cut
            if (pos.z < cutPlaneZ.z * dimensions.z) return true;
        }
    }

    return false;
}

// Check if a point is cut by the clipping plane
bool IsCutByClippingPlane(float3 pos)
{
    // Check if clipping plane is enabled
    if (clippingPlane1.x < 0.5) return false;

    // Get plane normal and distance
    float3 normal = clippingPlane1.yzw;
    float distance = clippingPlane2.x;
    bool mirror = clippingPlane2.y > 0.5;

    // Convert position to normalized coordinates [0,1]
    float3 normalizedPos = pos / dimensions.xyz;

    // Calculate distance from point to plane
    // Plane equation: n·(p - p0) = 0
    // Where n is the normal, p is the point, p0 is a point on the plane
    float3 planePoint = normal * distance;
    float distanceToPlane = dot(normal, normalizedPos - planePoint);

    // Check which side of the plane the point is on
    if (mirror)
    {
        return distanceToPlane < 0; // Cut the negative side
    }
    else
    {
        return distanceToPlane > 0; // Cut the positive side
    }
}

// Get color from the selected color map
float4 ApplyColorMap(float intensity, float minThreshold, float maxThreshold, int mapIndex)
{
    // Normalize intensity to 0-1 range based on thresholds
    float normalizedIntensity = (intensity - minThreshold) / (maxThreshold - minThreshold);
    normalizedIntensity = saturate(normalizedIntensity); // Clamp to [0,1]

    // Sample from the color map based on the intensity
    // The colorMaps texture is a 1D texture with different color maps stacked
    // Each map has 256 entries, so we offset by mapIndex * 256
    float mapOffset = mapIndex * 256;
    float samplePos = (mapOffset + normalizedIntensity * 255) / 1024.0; // Assuming total size of 1024
    float4 color = colorMaps.SampleLevel(linearSampler, samplePos, 0);

    // Adjust alpha based on intensity
    float alpha = normalizedIntensity * 0.5 + 0.2;
    color.a = min(0.7, alpha);

    return color;
}

// Improved edge detection for wireframe
bool IsOnBoundingBoxEdge(float3 texCoord, float edgeThickness)
{
    // Check if we're near any of the 12 edges of the box
    // This approach is more precise than the previous one

    // We need at least two coordinates to be near the edge
    int nearEdgeCount = 0;

    // Check each dimension
    for (int i = 0; i < 3; i++)
    {
        float coord = texCoord[i];
        if (coord < edgeThickness || coord > (1.0 - edgeThickness))
        {
            nearEdgeCount++;
        }
    }

    // We're on an edge if exactly two coordinates are at extremes
    return nearEdgeCount >= 2;
}

// Pixel shader implementing ray marching through the volume
float4 PSMain(VS_OUTPUT input) : SV_TARGET
{
    // Get the ray origin and direction in world space
    float3 rayOrigin = cameraPosition.xyz;
    float3 rayDir = normalize(input.WorldPos - rayOrigin);

    // Compute intersection with the volume bounding box
    float tNear, tFar;
    float3 boxMin = float3(0, 0, 0);
    float3 boxMax = dimensions.xyz;

    // Check if ray actually hits the bounding box
    if (!IntersectBox(rayOrigin, rayDir, boxMin, boxMax, tNear, tFar))
    {
        // Ray completely misses the volume - return fully transparent
        return float4(0, 0, 0, 0);
    }

    // Ensure we start inside the volume
    tNear = max(tNear, 0.0);

    // Step size for ray marching - this must be small enough
    float stepSize = max(0.5, thresholds.z);
    int maxSteps = 2000; // Higher limit for quality

    // Initialize accumulated color
    float4 accumulatedColor = float4(0, 0, 0, 0);

    // Start ray marching from the near intersection point
    float t = tNear;

    // Thresholds
    float minThreshold = thresholds.x;
    float maxThreshold = thresholds.y;

    // Whether to show grayscale
    bool showGrayscale = thresholds.w > 0.5;

    // Whether any slices are enabled (w component contains the bit flags)
    bool anySlicesEnabled = sliceCoords.w > 0.0;

    // Slice positions
    float3 slicePos = sliceCoords.xyz;

    // Color map index
    int mapIndex = (int)(colorMapIndex.x + 0.5);

    // Border thickness for slices
    float borderThickness = colorMapIndex.y;

    // Ray marching loop
    for (int i = 0; i < maxSteps && t < tFar; i++)
    {
        // Calculate current position along the ray
        float3 pos = rayOrigin + t * rayDir;

        // Convert to texture coordinates [0,1]
        float3 texCoord = pos / dimensions.xyz;

        // Robust bounds check - add extra safety margin
        if (any(texCoord < -0.001) || any(texCoord > 1.001))
        {
            break;
        }

        // Check if this point is cut by any cutting plane
        if (IsCutByPlane(pos))
        {
            t += stepSize;
            continue;
        }

        // Check if this point is cut by the clipping plane
        if (IsCutByClippingPlane(pos))
        {
            t += stepSize;
            continue;
        }

        // Handle slice planes with higher priority
        int sliceType = 0;
        if (anySlicesEnabled && IsOnSlicePlane(pos, slicePos, stepSize * 1.5, sliceType))
        {
            // We're on a slice plane - render it
            // Clamp coordinates to valid range to avoid sampling artifacts
            float3 safeCoord = clamp(texCoord, 0.001, 0.999);

            // Sample the volume with clamped coordinates
            float intensity = volumeTexture.SampleLevel(linearSampler, safeCoord, 0);
            float labelValue = labelTexture.SampleLevel(pointSampler, safeCoord, 0);

            // Convert label to material ID
            uint materialId = (uint)(labelValue + 0.5);

            // Check if we're on a slice border
            bool onBorder = IsOnSliceBorder(pos, slicePos, borderThickness, sliceType);

            // Create the slice color - either from material, grayscale, or border
            float4 sliceColor;

            if (onBorder)
            {
                // Use different colors for borders based on slice type
                if (sliceType == 1) // X slice (YZ plane) - Red
                    sliceColor = float4(1.0, 0.0, 0.0, 0.9);
                else if (sliceType == 2) // Y slice (XZ plane) - Green
                    sliceColor = float4(0.0, 1.0, 0.0, 0.9);
                else if (sliceType == 3) // Z slice (XY plane) - Blue
                    sliceColor = float4(0.0, 0.0, 1.0, 0.9);
            }
            else if (materialId > 0 && materialVisibility[materialId] > 0.5)
            {
                // Use material color for the slice
                sliceColor = materialColors[materialId];
                sliceColor.a *= materialOpacity[materialId];
                sliceColor.a = min(sliceColor.a, 0.9); // Cap opacity
            }
            else
            {
                // Use color map or grayscale for the slice with higher opacity for better visibility
                if (mapIndex >= 0)
                    sliceColor = ApplyColorMap(intensity, minThreshold, maxThreshold, mapIndex);
                else
                    sliceColor = float4(intensity, intensity, intensity, 0.7);

                // Higher opacity for slices
                sliceColor.a = 0.7;
            }

            // Accumulate the slice color
            accumulatedColor = sliceColor;

            // Early exit after hitting a slice
            break;
        }

        // Safety check before sampling the volume
        if (any(texCoord < 0.0) || any(texCoord > 1.0))
        {
            t += stepSize;
            continue;
        }

        // Sample volume data
        float intensity = volumeTexture.SampleLevel(linearSampler, texCoord, 0);
        float labelValue = labelTexture.SampleLevel(pointSampler, texCoord, 0);

        // Convert label to material ID
        uint materialId = (uint)(labelValue + 0.5);

        // Initialize this sample's color to transparent
        float4 sampleColor = float4(0, 0, 0, 0);

        // Process the regular volume
        if (showGrayscale && intensity >= minThreshold && intensity <= maxThreshold)
        {
            // Apply color map based on selected index
            if (mapIndex >= 0)
                sampleColor = ApplyColorMap(intensity, minThreshold, maxThreshold, mapIndex);
            else
                sampleColor = float4(intensity, intensity, intensity, intensity * 0.5 + 0.2);
        }

        // Overlay material color if applicable
        if (materialId > 0 && materialVisibility[materialId] > 0.5)
        {
            // Use material color
            float4 matColor = materialColors[materialId];
            matColor.a *= materialOpacity[materialId];

            // Replace grayscale with material color
            sampleColor = matColor;
        }

        // Front-to-back compositing if the sample has color
        if (sampleColor.a > 0.01)
        {
            // Pre-multiply alpha
            sampleColor.rgb *= sampleColor.a;

            // Accumulate color
            accumulatedColor += (1.0 - accumulatedColor.a) * sampleColor;

            // Early ray termination for efficiency
            if (accumulatedColor.a > 0.95)
            {
                break;
            }
        }

        // Move along ray
        t += stepSize;
    }

    // Draw wireframe if needed - only if we didn't hit anything solid
    if (accumulatedColor.a < 0.01)
    {
        // Use a much thinner wireframe
        float edgeThickness = 0.003;

        // Check if we're on an edge of the bounding box
        if (IsOnBoundingBoxEdge(input.TexCoord, edgeThickness))
        {
            // Draw wireframe in bright white with low opacity
            return float4(1.0, 1.0, 1.0, 0.3);
        }

        // For non-edge pixels, return fully transparent color
        return float4(0, 0, 0, 0);
    }

    // Return the final accumulated color
    return accumulatedColor;
}";
        }


        private void CreateCubeGeometry()
        {
            try
            {
                // Create cube vertices - using actual volume dimensions
                Vector3[] vertices = new Vector3[8]
                {
                    new Vector3(0, 0, 0),                // 0: bottom-left-back
                    new Vector3(volW, 0, 0),             // 1: bottom-right-back
                    new Vector3(volW, volH, 0),          // 2: top-right-back
                    new Vector3(0, volH, 0),             // 3: top-left-back
                    new Vector3(0, 0, volD),             // 4: bottom-left-front
                    new Vector3(volW, 0, volD),          // 5: bottom-right-front
                    new Vector3(volW, volH, volD),       // 6: top-right-front
                    new Vector3(0, volH, volD)           // 7: top-left-front
                };

                // Create cube indices (6 faces, 2 triangles per face)
                int[] indices = new int[]
                {
                    // Back face (CCW)
                    0, 2, 1, 0, 3, 2,
                    // Front face (CCW)
                    4, 5, 6, 4, 6, 7,
                    // Left face (CCW)
                    0, 4, 7, 0, 7, 3,
                    // Right face (CCW)
                    1, 2, 6, 1, 6, 5,
                    // Bottom face (CCW)
                    0, 1, 5, 0, 5, 4,
                    // Top face (CCW)
                    3, 7, 6, 3, 6, 2
                };

                cubeIndexCount = indices.Length;

                // Create vertex buffer
                BufferDescription vbDesc = new BufferDescription(
                    Utilities.SizeOf<Vector3>() * vertices.Length,
                    ResourceUsage.Default,
                    BindFlags.VertexBuffer,
                    CpuAccessFlags.None,
                    ResourceOptionFlags.None,
                    0);

                cubeVertexBuffer = Buffer.Create(device, vertices, vbDesc);

                // Create index buffer
                BufferDescription ibDesc = new BufferDescription(
                    sizeof(int) * indices.Length,
                    ResourceUsage.Default,
                    BindFlags.IndexBuffer,
                    CpuAccessFlags.None,
                    ResourceOptionFlags.None,
                    sizeof(int));

                cubeIndexBuffer = Buffer.Create(device, indices, ibDesc);

                Logger.Log("[SharpDXVolumeRenderer] Cube geometry created successfully");
            }
            catch (Exception ex)
            {
                Logger.Log("[SharpDXVolumeRenderer] Error creating cube geometry: " + ex.Message);
                throw;
            }
        }

        private void CreateVolumeTextures()
        {
            // --- Step 1: Create the Grayscale Volume Texture ---
            try
            {
                if (useStreamingRenderer && mainForm.volumeData != null)
                {
                    // Create a single low-resolution overview texture for streaming
                    int downsampleFactor = 8; // Start with a significant downsampling
                    int dsWidth = Math.Max(1, volW / downsampleFactor);
                    int dsHeight = Math.Max(1, volH / downsampleFactor);
                    int dsDepth = Math.Max(1, volD / downsampleFactor);

                    Logger.Log($"[SharpDXVolumeRenderer] Creating low-res overview texture: {dsWidth}x{dsHeight}x{dsDepth}");

                    Texture3DDescription desc = new Texture3DDescription
                    {
                        Width = dsWidth,
                        Height = dsHeight,
                        Depth = dsDepth,
                        MipLevels = 1,
                        Format = Format.R8_UNorm,
                        Usage = ResourceUsage.Default,
                        BindFlags = BindFlags.ShaderResource,
                        CpuAccessFlags = CpuAccessFlags.None,
                        OptionFlags = ResourceOptionFlags.None
                    };
                    volumeTexture = new Texture3D(device, desc);

                    byte[] dsData = new byte[dsWidth * dsHeight * dsDepth];
                    for (int z = 0; z < dsDepth; z++)
                    {
                        for (int y = 0; y < dsHeight; y++)
                        {
                            for (int x = 0; x < dsWidth; x++)
                            {
                                int srcX = Math.Min(x * downsampleFactor, volW - 1);
                                int srcY = Math.Min(y * downsampleFactor, volH - 1);
                                int srcZ = Math.Min(z * downsampleFactor, volD - 1);
                                dsData[(z * dsHeight * dsWidth) + (y * dsWidth) + x] = mainForm.volumeData[srcX, srcY, srcZ];
                            }
                        }
                    }
                    context.UpdateSubresource(dsData, volumeTexture, 0);

                    ShaderResourceViewDescription srvDesc = new ShaderResourceViewDescription
                    {
                        Format = Format.R8_UNorm,
                        Dimension = ShaderResourceViewDimension.Texture3D,
                        Texture3D = new ShaderResourceViewDescription.Texture3DResource { MipLevels = 1, MostDetailedMip = 0 }
                    };
                    volumeSRV = new ShaderResourceView(device, volumeTexture, srvDesc);
                    Logger.Log($"[SharpDXVolumeRenderer] Created low-res overview texture for streaming.");
                }
                else if (mainForm.volumeData != null && !useStreamingRenderer)
                {
                    // For standard rendering, create the full volume texture
                    volumeTexture = CreateTexture3DFromChunkedVolume((ChunkedVolume)mainForm.volumeData, Format.R8_UNorm);
                    if (volumeTexture != null)
                    {
                        ShaderResourceViewDescription srvDesc = new ShaderResourceViewDescription
                        {
                            Format = Format.R8_UNorm,
                            Dimension = ShaderResourceViewDimension.Texture3D,
                            Texture3D = new ShaderResourceViewDescription.Texture3DResource { MipLevels = 1, MostDetailedMip = 0 }
                        };
                        volumeSRV = new ShaderResourceView(device, volumeTexture, srvDesc);
                        Logger.Log($"[SharpDXVolumeRenderer] Created volume texture: {volW}x{volH}x{volD}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[SharpDXVolumeRenderer] CRITICAL ERROR creating grayscale volume texture: {ex.Message}. Rendering may be impaired.");
                // We don't re-throw, allowing the application to continue with what it has.
            }

            // --- Step 2: Create the Label Volume Texture ---
            // This is in a separate try-catch block to allow graceful failure.
            try
            {
                if (mainForm.volumeLabels != null)
                {
                    // Using R32_Float for labels to support more than 255 materials in the shader.
                    labelTexture = CreateTexture3DFromChunkedLabelVolume((ChunkedLabelVolume)mainForm.volumeLabels, Format.R32_Float);
                    if (labelTexture != null)
                    {
                        ShaderResourceViewDescription srvDesc = new ShaderResourceViewDescription
                        {
                            Format = Format.R32_Float,
                            Dimension = ShaderResourceViewDimension.Texture3D,
                            Texture3D = new ShaderResourceViewDescription.Texture3DResource { MipLevels = 1, MostDetailedMip = 0 }
                        };
                        labelSRV = new ShaderResourceView(device, labelTexture, srvDesc);
                        Logger.Log("[SharpDXVolumeRenderer] Created label volume texture.");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[SharpDXVolumeRenderer] WARNING: Failed to create label texture: {ex.Message}. Continuing with grayscale rendering only.");
                // If label texture fails, labelSRV will be null. The shader should handle this gracefully.
                // The application will continue running with just the grayscale volume.
            }
        }

        private void CreateLabelTextures()
        {
            try
            {
                // Create 1D textures for label visibility and opacity
                Texture1DDescription texDesc = new Texture1DDescription
                {
                    Width = MAX_LABELS,
                    ArraySize = 1,
                    BindFlags = BindFlags.ShaderResource,
                    CpuAccessFlags = CpuAccessFlags.Write,
                    Format = Format.R32_Float,
                    MipLevels = 1,
                    OptionFlags = ResourceOptionFlags.None,
                    Usage = ResourceUsage.Dynamic
                };

                labelVisibilityTexture = new Texture1D(device, texDesc);
                labelOpacityTexture = new Texture1D(device, texDesc);

                // Create shader resource views
                ShaderResourceViewDescription srvDesc = new ShaderResourceViewDescription
                {
                    Format = Format.R32_Float,
                    Dimension = ShaderResourceViewDimension.Texture1D,
                    Texture1D = new ShaderResourceViewDescription.Texture1DResource
                    {
                        MipLevels = 1,
                        MostDetailedMip = 0
                    }
                };

                labelVisibilitySRV = new ShaderResourceView(device, labelVisibilityTexture, srvDesc);
                labelOpacitySRV = new ShaderResourceView(device, labelOpacityTexture, srvDesc);

                // Upload initial data
                UpdateLabelTextures();

                Logger.Log("[SharpDXVolumeRenderer] Label textures created successfully");
            }
            catch (Exception ex)
            {
                Logger.Log("[SharpDXVolumeRenderer] Error creating label textures: " + ex.Message);
                // Don't throw here - the application should still work for basic cube rendering
            }
        }

        private void CreateMaterialColorTexture()
        {
            try
            {
                // Create a color texture for materials
                // Always create this texture even if Materials collection is empty

                // Create texture descriptor
                Texture1DDescription texDesc = new Texture1DDescription
                {
                    Width = MAX_LABELS,
                    ArraySize = 1,
                    BindFlags = BindFlags.ShaderResource,
                    CpuAccessFlags = CpuAccessFlags.Write,
                    Format = Format.R32G32B32A32_Float, // Use float4 format for compatibility
                    MipLevels = 1,
                    OptionFlags = ResourceOptionFlags.None,
                    Usage = ResourceUsage.Dynamic
                };

                materialColorTexture = new Texture1D(device, texDesc);

                // Create shader resource view
                ShaderResourceViewDescription srvDesc = new ShaderResourceViewDescription
                {
                    Format = Format.R32G32B32A32_Float,
                    Dimension = ShaderResourceViewDimension.Texture1D,
                    Texture1D = new ShaderResourceViewDescription.Texture1DResource
                    {
                        MipLevels = 1,
                        MostDetailedMip = 0
                    }
                };

                materialColorSRV = new ShaderResourceView(device, materialColorTexture, srvDesc);

                // Upload default colors then override with material colors if available
                UpdateMaterialColors();

                Logger.Log("[SharpDXVolumeRenderer] Material color texture created successfully");
            }
            catch (Exception ex)
            {
                Logger.Log("[SharpDXVolumeRenderer] Error creating material color texture: " + ex.Message);
                throw; // Rethrow to signal critical error
            }
        }

        /// <summary>
        /// Creates a 1D texture containing predefined color maps
        /// </summary>
        private void CreateColorMapTexture()
        {
            try
            {
                // Create texture with 4 color maps, each 256 entries (total 1024 entries)
                Texture1DDescription texDesc = new Texture1DDescription
                {
                    Width = 1024, // 4 color maps x 256 entries each
                    ArraySize = 1,
                    BindFlags = BindFlags.ShaderResource,
                    CpuAccessFlags = CpuAccessFlags.Write,
                    Format = Format.R32G32B32A32_Float,
                    MipLevels = 1,
                    OptionFlags = ResourceOptionFlags.None,
                    Usage = ResourceUsage.Dynamic
                };

                colorMapTexture = new Texture1D(device, texDesc);

                // Create shader resource view
                ShaderResourceViewDescription srvDesc = new ShaderResourceViewDescription
                {
                    Format = Format.R32G32B32A32_Float,
                    Dimension = ShaderResourceViewDimension.Texture1D,
                    Texture1D = new ShaderResourceViewDescription.Texture1DResource
                    {
                        MipLevels = 1,
                        MostDetailedMip = 0
                    }
                };

                colorMapSRV = new ShaderResourceView(device, colorMapTexture, srvDesc);

                // Initialize color maps
                UpdateColorMaps();

                Logger.Log("[SharpDXVolumeRenderer] Color map texture created successfully");
            }
            catch (Exception ex)
            {
                Logger.Log("[SharpDXVolumeRenderer] Error creating color map texture: " + ex.Message);
                // Non-critical, continue without color maps
            }
        }

        /// <summary>
        /// Creates lower resolution (LOD) versions of the volume textures for performance
        /// </summary>
        private void CreateLodTextures()
        {
            try
            {
                // Only proceed if we have a valid volume texture
                if (volumeTexture == null || mainForm.volumeData == null)
                {
                    Logger.Log("[SharpDXVolumeRenderer] Skipping LOD texture creation - no volume data");
                    useLodSystem = false; // Disable LOD if no volume data
                    return;
                }

                // LOD level 0 is the original texture
                lodVolumeTextures[0] = volumeTexture;
                lodVolumeSRVs[0] = volumeSRV;

                // Create LOD textures with progressively lower resolution
                ChunkedVolume originalVolume = (ChunkedVolume)mainForm.volumeData;
                bool anyLodCreated = false;

                for (int i = 1; i <= MAX_LOD_LEVELS; i++)
                {
                    try
                    {
                        // Create downsampled volume at 1/2^i resolution
                        int factorX = (int)Math.Pow(2, i);
                        int factorY = (int)Math.Pow(2, i);
                        int factorZ = (int)Math.Pow(2, i);

                        int newWidth = Math.Max(1, volW / factorX);
                        int newHeight = Math.Max(1, volH / factorY);
                        int newDepth = Math.Max(1, volD / factorZ);

                        // Create a new texture with lower resolution
                        Texture3DDescription lodDesc = new Texture3DDescription
                        {
                            Width = newWidth,
                            Height = newHeight,
                            Depth = newDepth,
                            MipLevels = 1,
                            Format = Format.R8_UNorm,
                            Usage = ResourceUsage.Default,
                            BindFlags = BindFlags.ShaderResource,
                            CpuAccessFlags = CpuAccessFlags.None,
                            OptionFlags = ResourceOptionFlags.None
                        };

                        lodVolumeTextures[i] = new Texture3D(device, lodDesc);

                        // Create a downsample buffer in CPU memory
                        byte[] lodData = new byte[newWidth * newHeight * newDepth];

                        // Simple downsampling by averaging blocks of voxels
                        for (int z = 0; z < newDepth; z++)
                        {
                            for (int y = 0; y < newHeight; y++)
                            {
                                for (int x = 0; x < newWidth; x++)
                                {
                                    // Original coordinates
                                    int ox = x * factorX;
                                    int oy = y * factorY;
                                    int oz = z * factorZ;

                                    // Sample from the original volume (simplified for illustration)
                                    byte value = originalVolume[
                                        Math.Min(ox, volW - 1),
                                        Math.Min(oy, volH - 1),
                                        Math.Min(oz, volD - 1)];

                                    // Store the downsampled value
                                    lodData[z * newWidth * newHeight + y * newWidth + x] = value;
                                }
                            }
                        }

                        // Upload to the texture
                        context.UpdateSubresource(lodData, lodVolumeTextures[i], 0);

                        // Create a shader resource view for this LOD level
                        ShaderResourceViewDescription srvDesc = new ShaderResourceViewDescription
                        {
                            Format = Format.R8_UNorm,
                            Dimension = ShaderResourceViewDimension.Texture3D,
                            Texture3D = new ShaderResourceViewDescription.Texture3DResource
                            {
                                MipLevels = 1,
                                MostDetailedMip = 0
                            }
                        };

                        lodVolumeSRVs[i] = new ShaderResourceView(device, lodVolumeTextures[i], srvDesc);
                        anyLodCreated = true;
                    }
                    catch (Exception lodEx)
                    {
                        Logger.Log($"[SharpDXVolumeRenderer] Failed to create LOD level {i}: {lodEx.Message}");
                        lodVolumeTextures[i] = null;
                        lodVolumeSRVs[i] = null;
                    }
                }

                if (anyLodCreated)
                {
                    Logger.Log("[SharpDXVolumeRenderer] Created LOD textures for large volume optimization");
                }
                else
                {
                    // If no LOD textures were created, disable the LOD system
                    useLodSystem = false;
                    Logger.Log("[SharpDXVolumeRenderer] LOD system disabled - failed to create any LOD textures");
                }
            }
            catch (Exception ex)
            {
                Logger.Log("[SharpDXVolumeRenderer] Error creating LOD textures: " + ex.Message);
                useLodSystem = false; // Disable LOD on error
            }
        }

        private bool RaycastToVolume(int screenX, int screenY, out Vector3 worldPos)
        {
            worldPos = Vector3.Zero;

            try
            {
                // Convert screen coordinates to normalized device coordinates (-1 to 1)
                float ndcX = (2.0f * screenX / renderPanel.ClientSize.Width) - 1.0f;
                float ndcY = 1.0f - (2.0f * screenY / renderPanel.ClientSize.Height);

                // Create normalized device coordinates point
                Vector4 ndcPoint = new Vector4(ndcX, ndcY, 0, 1);

                // Create view and projection matrices
                float aspectRatio = (float)renderPanel.ClientSize.Width / Math.Max(1, renderPanel.ClientSize.Height);

                // Get camera matrix
                float cosPitch = (float)Math.Cos(cameraPitch);
                float sinPitch = (float)Math.Sin(cameraPitch);
                float cosYaw = (float)Math.Cos(cameraYaw);
                float sinYaw = (float)Math.Sin(cameraYaw);

                Vector3 volumeCenter = new Vector3(volW / 2.0f, volH / 2.0f, volD / 2.0f);
                Vector3 cameraDirection = new Vector3(
                    cosPitch * cosYaw,
                    sinPitch,
                    cosPitch * sinYaw);
                Vector3 cameraPosition = volumeCenter - (cameraDirection * cameraDistance) + panOffset;

                Matrix viewMatrix = Matrix.LookAtLH(
                    cameraPosition,
                    volumeCenter + panOffset,
                    Vector3.UnitY);

                Matrix projMatrix = Matrix.PerspectiveFovLH(
                    (float)Math.PI / 4.0f,
                    aspectRatio,
                    1.0f,
                    cameraDistance * 10.0f);

                // Invert the view-projection matrix
                Matrix invViewProj = Matrix.Invert(viewMatrix * projMatrix);

                // Transform from NDC space to world space
                Vector4 worldPoint = Vector4.Transform(ndcPoint, invViewProj);

                // Convert to 3D direction (normalize)
                if (worldPoint.W != 0)
                {
                    worldPoint /= worldPoint.W;
                }

                // Create ray from camera position to world point
                Vector3 rayDirection = new Vector3(
                    worldPoint.X - cameraPosition.X,
                    worldPoint.Y - cameraPosition.Y,
                    worldPoint.Z - cameraPosition.Z);
                rayDirection.Normalize();

                // Perform ray-box intersection to get the point in the volume
                Vector3 boxMin = new Vector3(0, 0, 0);
                Vector3 boxMax = new Vector3(volW, volH, volD);

                float tNear, tFar;
                if (IntersectBox(cameraPosition, rayDirection, boxMin, boxMax, out tNear, out tFar) && tNear < tFar)
                {
                    if (tNear < 0) tNear = 0; // Start from camera if inside volume

                    // Calculate intersection point
                    worldPos = cameraPosition + rayDirection * tNear;

                    // Clamp to volume boundaries
                    worldPos.X = Math.Max(0, Math.Min(volW, worldPos.X));
                    worldPos.Y = Math.Max(0, Math.Min(volH, worldPos.Y));
                    worldPos.Z = Math.Max(0, Math.Min(volD, worldPos.Z));

                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[SharpDXVolumeRenderer] Raycast error: {ex.Message}");
            }

            return false;
        }

        private bool IntersectBox(Vector3 rayOrigin, Vector3 rayDir,
                         Vector3 boxMin, Vector3 boxMax,
                         out float tNear, out float tFar)
        {
            tNear = float.MinValue;
            tFar = float.MaxValue;

            // For each axis, calculate the near and far intersection times
            for (int i = 0; i < 3; i++)
            {
                float origin = i == 0 ? rayOrigin.X : (i == 1 ? rayOrigin.Y : rayOrigin.Z);
                float direction = i == 0 ? rayDir.X : (i == 1 ? rayDir.Y : rayDir.Z);
                float boxMinValue = i == 0 ? boxMin.X : (i == 1 ? boxMin.Y : boxMin.Z);
                float boxMaxValue = i == 0 ? boxMax.X : (i == 1 ? boxMax.Y : boxMax.Z);

                if (Math.Abs(direction) < 1e-6) // Ray is parallel to the slab
                {
                    if (origin < boxMinValue || origin > boxMaxValue)
                        return false; // Ray is outside the slab
                }
                else // Ray is not parallel to the slab
                {
                    float invDirection = 1.0f / direction;
                    float t1 = (boxMinValue - origin) * invDirection;
                    float t2 = (boxMaxValue - origin) * invDirection;

                    if (t1 > t2)
                    {
                        float temp = t1;
                        t1 = t2;
                        t2 = temp;
                    }

                    tNear = Math.Max(tNear, t1);
                    tFar = Math.Min(tFar, t2);

                    if (tNear > tFar)
                        return false; // No intersection
                }
            }

            return true; // If we get here, there is an intersection
        }

        public void ForceInitialRender()
        {
            // Store previous state
            int oldLodLevel = currentLodLevel;
            bool oldDebugMode = debugMode;

            try
            {
                // Set up for best quality initial render
                currentLodLevel = 0;
                debugMode = false;
                NeedsRender = true;

                // Make sure all resources are properly set
                if (context != null)
                {
                    // Clear any previous state
                    context.ClearState();

                    // Ensure viewport is correctly set
                    int width = Math.Max(1, renderPanel.ClientSize.Width);
                    int height = Math.Max(1, renderPanel.ClientSize.Height);
                    context.Rasterizer.SetViewport(0, 0, width, height);
                }

                // Force an initial render with default camera position
                Render();

                // Log success
                Logger.Log("[SharpDXVolumeRenderer] Initial render completed successfully");
            }
            catch (Exception ex)
            {
                Logger.Log("[SharpDXVolumeRenderer] Initial render failed: " + ex.Message);
            }
            finally
            {
                // Restore previous state
                currentLodLevel = oldLodLevel;
                debugMode = oldDebugMode;
            }
        }

        /// <summary>
        /// Updates the material color texture based on main form's material list
        /// </summary>
        public void UpdateMaterialColors()
        {
            try
            {
                // Skip if material texture is null
                if (materialColorTexture == null)
                    return;

                // Create an array of default color data
                Vector4[] colorData = new Vector4[MAX_LABELS];

                // Fill the array with default colors
                for (int i = 0; i < MAX_LABELS; i++)
                {
                    switch (i % 16)
                    {
                        case 0: colorData[i] = new Vector4(0.0f, 0.0f, 0.0f, 0.0f); break; // Exterior (transparent)
                        case 1: colorData[i] = new Vector4(1.0f, 0.0f, 0.0f, 1.0f); break; // Red
                        case 2: colorData[i] = new Vector4(0.0f, 1.0f, 0.0f, 1.0f); break; // Green
                        case 3: colorData[i] = new Vector4(0.0f, 0.0f, 1.0f, 1.0f); break; // Blue
                        case 4: colorData[i] = new Vector4(1.0f, 1.0f, 0.0f, 1.0f); break; // Yellow
                        case 5: colorData[i] = new Vector4(1.0f, 0.0f, 1.0f, 1.0f); break; // Magenta
                        case 6: colorData[i] = new Vector4(0.0f, 1.0f, 1.0f, 1.0f); break; // Cyan
                        case 7: colorData[i] = new Vector4(1.0f, 0.5f, 0.0f, 1.0f); break; // Orange
                        case 8: colorData[i] = new Vector4(0.5f, 0.0f, 1.0f, 1.0f); break; // Purple
                        case 9: colorData[i] = new Vector4(0.0f, 0.5f, 0.0f, 1.0f); break; // Dark Green
                        case 10: colorData[i] = new Vector4(0.5f, 0.5f, 0.5f, 1.0f); break; // Gray
                        case 11: colorData[i] = new Vector4(1.0f, 0.75f, 0.8f, 1.0f); break; // Pink
                        case 12: colorData[i] = new Vector4(0.6f, 0.3f, 0.1f, 1.0f); break; // Brown
                        case 13: colorData[i] = new Vector4(0.9f, 0.9f, 0.9f, 1.0f); break; // Light Gray
                        case 14: colorData[i] = new Vector4(0.4f, 0.7f, 0.4f, 1.0f); break; // Light Green
                        case 15: colorData[i] = new Vector4(0.0f, 0.4f, 0.8f, 1.0f); break; // Light Blue
                    }
                }

                // Override with actual colors from materials list if available
                if (mainForm.Materials != null && mainForm.Materials.Count > 0)
                {
                    foreach (Material mat in mainForm.Materials)
                    {
                        if (mat.ID < MAX_LABELS)
                        {
                            System.Drawing.Color color = mat.Color;
                            colorData[mat.ID] = new Vector4(
                                color.R / 255.0f,
                                color.G / 255.0f,
                                color.B / 255.0f,
                                1.0f); // Full alpha

                            // Special case for exterior (material ID 0)
                            if (mat.IsExterior)
                            {
                                colorData[mat.ID].W = 0.0f; // Make exterior fully transparent
                            }
                        }
                    }
                }

                // Map the texture and update it
                try
                {
                    DataStream dataStream;
                    context.MapSubresource(
                        materialColorTexture,
                        0,
                        MapMode.WriteDiscard,
                        SharpDX.Direct3D11.MapFlags.None,
                        out dataStream);

                    foreach (Vector4 color in colorData)
                    {
                        dataStream.Write(color);
                    }

                    context.UnmapSubresource(materialColorTexture, 0);
                    dataStream.Dispose();

                    Logger.Log("[SharpDXVolumeRenderer] Material colors updated successfully");
                }
                catch (Exception ex)
                {
                    Logger.Log("[SharpDXVolumeRenderer] Failed to update material colors: " + ex.Message);
                    throw;
                }
            }
            catch (Exception ex)
            {
                Logger.Log("[SharpDXVolumeRenderer] UpdateMaterialColors error: " + ex.Message);
                throw; // Rethrow as this is critical
            }
        }

        /// <summary>
        /// Updates the color map texture with predefined color maps
        /// </summary>
        private void UpdateColorMaps()
        {
            try
            {
                // Skip if color map texture is null
                if (colorMapTexture == null)
                    return;

                // Create an array for all color map data (4 maps * 256 entries)
                Vector4[] colorMapData = new Vector4[1024];

                // Map 0: Grayscale (already handled in shader)
                for (int i = 0; i < 256; i++)
                {
                    float v = i / 255.0f;
                    colorMapData[i] = new Vector4(v, v, v, v * 0.7f + 0.1f);
                }

                // Map 1: "Hot" colormap (black-red-yellow-white)
                for (int i = 0; i < 256; i++)
                {
                    float t = i / 255.0f;
                    float r = Math.Min(1.0f, 3.0f * t);
                    float g = Math.Max(0.0f, Math.Min(1.0f, 3.0f * t - 1.0f));
                    float b = Math.Max(0.0f, Math.Min(1.0f, 3.0f * t - 2.0f));
                    colorMapData[256 + i] = new Vector4(r, g, b, t * 0.7f + 0.1f);
                }

                // Map 2: "Cool" colormap (blue-cyan-green)
                for (int i = 0; i < 256; i++)
                {
                    float t = i / 255.0f;
                    float r = Math.Max(0.0f, Math.Min(1.0f, t * 1.5f - 0.5f));
                    float g = Math.Min(1.0f, t * 1.5f);
                    float b = Math.Min(1.0f, 2.0f - t * 1.5f);
                    colorMapData[512 + i] = new Vector4(r, g, b, t * 0.7f + 0.1f);
                }

                // Map 3: "Rainbow" colormap
                for (int i = 0; i < 256; i++)
                {
                    float t = i / 255.0f;
                    // Convert to HSV and back to RGB
                    float h = (1.0f - t) * 240.0f / 360.0f; // 240° (blue) to 0° (red)
                    float s = 1.0f;
                    float v = 1.0f;

                    // HSV to RGB conversion
                    int hi = (int)(h * 6) % 6;
                    float f = h * 6 - hi;
                    float p = v * (1 - s);
                    float q = v * (1 - f * s);
                    float u = v * (1 - (1 - f) * s);

                    float r, g, b;
                    switch (hi)
                    {
                        case 0: r = v; g = u; b = p; break;
                        case 1: r = q; g = v; b = p; break;
                        case 2: r = p; g = v; b = u; break;
                        case 3: r = p; g = q; b = v; break;
                        case 4: r = u; g = p; b = v; break;
                        default: r = v; g = p; b = q; break;
                    }

                    colorMapData[768 + i] = new Vector4(r, g, b, t * 0.7f + 0.1f);
                }

                // Map the texture and update it
                try
                {
                    DataStream dataStream;
                    context.MapSubresource(
                        colorMapTexture,
                        0,
                        MapMode.WriteDiscard,
                        SharpDX.Direct3D11.MapFlags.None,
                        out dataStream);

                    foreach (Vector4 color in colorMapData)
                    {
                        dataStream.Write(color);
                    }

                    context.UnmapSubresource(colorMapTexture, 0);
                    dataStream.Dispose();

                    Logger.Log("[SharpDXVolumeRenderer] Color maps updated successfully");
                }
                catch (Exception ex)
                {
                    Logger.Log("[SharpDXVolumeRenderer] Failed to update color maps: " + ex.Message);
                }
            }
            catch (Exception ex)
            {
                Logger.Log("[SharpDXVolumeRenderer] UpdateColorMaps error: " + ex.Message);
                // Non-critical, continue without color maps
            }
        }

        private Texture3D CreateTexture3DFromChunkedVolume(ChunkedVolume volume, Format format)
        {
            if (volume == null) return null;

            try
            {
                // --- Downsampling Logic (unchanged) ---
                int width = volume.Width;
                int height = volume.Height;
                int depth = volume.Depth;
                int downsampleFactor = 1;

                bool needsDownsampling = width > 2048 || height > 2048 || depth > 2048;
                long volumeSizeInBytes = (long)width * height * depth;
                bool exceedsMemoryLimit = volumeSizeInBytes > 4L * 1024L * 1024L * 1024L;

                if (needsDownsampling || exceedsMemoryLimit)
                {
                    while ((width / downsampleFactor > 2048 || height / downsampleFactor > 2048 || depth / downsampleFactor > 2048) ||
                           ((long)(width / downsampleFactor) * (height / downsampleFactor) * (depth / downsampleFactor) > 2L * 1024L * 1024L * 1024L)) // 2GB GPU limit
                    {
                        downsampleFactor *= 2;
                    }
                    Logger.Log($"[SharpDXVolumeRenderer] Large volume detected ({volume.Width}x{volume.Height}x{volume.Depth}), creating downsampled version (factor: {downsampleFactor})");
                    width /= downsampleFactor;
                    height /= downsampleFactor;
                    depth /= downsampleFactor;
                }

                // --- Optimized Texture Creation and Upload ---
                Texture3DDescription desc = new Texture3DDescription
                {
                    Width = width,
                    Height = height,
                    Depth = depth,
                    MipLevels = 1,
                    Format = format,
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource,
                    CpuAccessFlags = CpuAccessFlags.None,
                    OptionFlags = ResourceOptionFlags.None
                };

                Logger.Log($"[SharpDXVolumeRenderer] Creating volume texture: {width}x{height}x{depth}");
                Texture3D texture = new Texture3D(device, desc);

                // Upload data chunk by chunk for performance
                int chunkDim = volume.ChunkDim;
                Parallel.For(0, volume.ChunkCountZ, cz =>
                {
                    int zBase = cz * chunkDim;
                    for (int cy = 0; cy < volume.ChunkCountY; cy++)
                    {
                        int yBase = cy * chunkDim;
                        for (int cx = 0; cx < volume.ChunkCountX; cx++)
                        {
                            int xBase = cx * chunkDim;

                            // Destination region in the downsampled texture
                            int destX = xBase / downsampleFactor;
                            int destY = yBase / downsampleFactor;
                            int destZ = zBase / downsampleFactor;

                            // If this chunk is entirely outside the downsampled volume, skip it
                            if (destX >= width || destY >= height || destZ >= depth) continue;

                            byte[] chunkData = volume.GetChunkBytes(volume.GetChunkIndex(cx, cy, cz));

                            byte[] dataToUpload;
                            int dsChunkWidth, dsChunkHeight, dsChunkDepth;

                            if (downsampleFactor > 1)
                            {
                                // Calculate dimensions of the downsampled chunk
                                dsChunkWidth = Math.Min(width - destX, chunkDim / downsampleFactor);
                                dsChunkHeight = Math.Min(height - destY, chunkDim / downsampleFactor);
                                dsChunkDepth = Math.Min(depth - destZ, chunkDim / downsampleFactor);

                                if (dsChunkWidth <= 0 || dsChunkHeight <= 0 || dsChunkDepth <= 0) continue;

                                dataToUpload = new byte[dsChunkWidth * dsChunkHeight * dsChunkDepth];

                                // Perform downsampling for this chunk
                                for (int z = 0; z < dsChunkDepth; z++)
                                {
                                    for (int y = 0; y < dsChunkHeight; y++)
                                    {
                                        for (int x = 0; x < dsChunkWidth; x++)
                                        {
                                            int srcIndex = (z * downsampleFactor * chunkDim * chunkDim) + (y * downsampleFactor * chunkDim) + (x * downsampleFactor);
                                            if (srcIndex < chunkData.Length)
                                            {
                                                dataToUpload[(z * dsChunkHeight * dsChunkWidth) + (y * dsChunkWidth) + x] = chunkData[srcIndex];
                                            }
                                        }
                                    }
                                }
                            }
                            else
                            {
                                dataToUpload = chunkData;
                                dsChunkWidth = Math.Min(width - destX, chunkDim);
                                dsChunkHeight = Math.Min(height - destY, chunkDim);
                                dsChunkDepth = Math.Min(depth - destZ, chunkDim);
                            }

                            // Upload the processed chunk to the correct region on the GPU
                            GCHandle handle = GCHandle.Alloc(dataToUpload, GCHandleType.Pinned);
                            try
                            {
                                var dataBox = new DataBox(handle.AddrOfPinnedObject(), dsChunkWidth, dsChunkWidth * dsChunkHeight);
                                var region = new ResourceRegion(destX, destY, destZ, destX + dsChunkWidth, destY + dsChunkHeight, destZ + dsChunkDepth);

                                lock (context) // The context is not thread-safe, so lock it for the upload
                                {
                                    context.UpdateSubresource(dataBox, texture, 0, region);
                                }
                            }
                            finally
                            {
                                handle.Free();
                            }
                        }
                    }
                });

                return texture;
            }
            catch (SharpDXException ex)
            {
                Logger.Log($"[SharpDXVolumeRenderer] DirectX error creating volume texture: {ex.Message} (HRESULT: {ex.HResult:X})");
                if (ex.HResult == -2147024882) // E_OUTOFMEMORY
                {
                    Logger.Log("[SharpDXVolumeRenderer] Out of memory error. This dataset is too large for available GPU memory.");
                }
                throw;
            }
            catch (Exception ex)
            {
                Logger.Log($"[SharpDXVolumeRenderer] Error creating volume texture: {ex.Message}");
                throw;
            }
        }

        private Texture3D CreateTexture3DFromChunkedLabelVolume(ChunkedLabelVolume volume, Format format)
        {
            if (volume == null) return null;

            try
            {
                // --- Downsampling Logic (unchanged) ---
                int width, height, depth, downsampleFactor = 1;
                if (volumeTexture != null)
                {
                    width = volumeTexture.Description.Width;
                    height = volumeTexture.Description.Height;
                    depth = volumeTexture.Description.Depth;
                    downsampleFactor = Math.Max(1, volume.Width / width);
                    Logger.Log($"[SharpDXVolumeRenderer] Creating label texture with same dimensions as grayscale: {width}x{height}x{depth} (factor: {downsampleFactor})");
                }
                else
                {
                    // Fallback logic if grayscale texture wasn't created
                    width = volume.Width;
                    height = volume.Height;
                    depth = volume.Depth;
                    bool needsDownsampling = width > 2048 || height > 2048 || depth > 2048;
                    long volumeSizeInBytes = (long)width * height * depth * 4; // 4 bytes for R32_Float
                    bool exceedsMemoryLimit = volumeSizeInBytes > 2L * 1024L * 1024L * 1024L;

                    if (needsDownsampling || exceedsMemoryLimit)
                    {
                        while ((width / downsampleFactor > 1024 || height / downsampleFactor > 1024 || depth / downsampleFactor > 1024) ||
                               ((long)(width / downsampleFactor) * (height / downsampleFactor) * (depth / downsampleFactor) * 4 > 1024L * 1024L * 1024L))
                        {
                            downsampleFactor *= 2;
                        }
                        width /= downsampleFactor;
                        height /= downsampleFactor;
                        depth /= downsampleFactor;
                    }
                }

                // --- Optimized Texture Creation and Upload ---
                Texture3DDescription desc = new Texture3DDescription
                {
                    Width = width,
                    Height = height,
                    Depth = depth,
                    MipLevels = 1,
                    Format = format,
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource,
                    CpuAccessFlags = CpuAccessFlags.None,
                    OptionFlags = ResourceOptionFlags.None
                };

                Logger.Log($"[SharpDXVolumeRenderer] Creating label texture: {width}x{height}x{depth}");
                Texture3D texture = new Texture3D(device, desc);

                // Upload data chunk by chunk for performance
                int chunkDim = volume.ChunkDim;
                int bytesPerPixel = (format == Format.R32_Float) ? 4 : 1;

                Parallel.For(0, volume.ChunkCountZ, cz =>
                {
                    int zBase = cz * chunkDim;
                    for (int cy = 0; cy < volume.ChunkCountY; cy++)
                    {
                        int yBase = cy * chunkDim;
                        for (int cx = 0; cx < volume.ChunkCountX; cx++)
                        {
                            int xBase = cx * chunkDim;

                            int destX = xBase / downsampleFactor;
                            int destY = yBase / downsampleFactor;
                            int destZ = zBase / downsampleFactor;

                            if (destX >= width || destY >= height || destZ >= depth) continue;

                            byte[] chunkBytes = volume.GetChunkBytes(volume.GetChunkIndex(cx, cy, cz));

                            int dsChunkWidth = Math.Min(width - destX, chunkDim / downsampleFactor);
                            int dsChunkHeight = Math.Min(height - destY, chunkDim / downsampleFactor);
                            int dsChunkDepth = Math.Min(depth - destZ, chunkDim / downsampleFactor);

                            if (dsChunkWidth <= 0 || dsChunkHeight <= 0 || dsChunkDepth <= 0) continue;

                            // Convert byte[] to float[] for R32_Float format
                            float[] floatData = new float[dsChunkWidth * dsChunkHeight * dsChunkDepth];

                            // Perform downsampling and data type conversion
                            for (int z = 0; z < dsChunkDepth; z++)
                            {
                                for (int y = 0; y < dsChunkHeight; y++)
                                {
                                    for (int x = 0; x < dsChunkWidth; x++)
                                    {
                                        int srcIndex = (z * downsampleFactor * chunkDim * chunkDim) + (y * downsampleFactor * chunkDim) + (x * downsampleFactor);
                                        if (srcIndex < chunkBytes.Length)
                                        {
                                            floatData[(z * dsChunkHeight * dsChunkWidth) + (y * dsChunkWidth) + x] = chunkBytes[srcIndex];
                                        }
                                    }
                                }
                            }

                            GCHandle handle = GCHandle.Alloc(floatData, GCHandleType.Pinned);
                            try
                            {
                                var dataBox = new DataBox(handle.AddrOfPinnedObject(), dsChunkWidth * bytesPerPixel, dsChunkWidth * dsChunkHeight * bytesPerPixel);
                                var region = new ResourceRegion(destX, destY, destZ, destX + dsChunkWidth, destY + dsChunkHeight, destZ + dsChunkDepth);

                                lock (context)
                                {
                                    context.UpdateSubresource(dataBox, texture, 0, region);
                                }
                            }
                            finally
                            {
                                handle.Free();
                            }
                        }
                    }
                });

                return texture;
            }
            catch (SharpDXException ex)
            {
                Logger.Log($"[SharpDXVolumeRenderer] DirectX error creating label texture: {ex.Message} (HRESULT: {ex.HResult:X})");
                if (ex.HResult == -2147024882) // E_OUTOFMEMORY
                {
                    Logger.Log("[SharpDXVolumeRenderer] Out of memory error. This dataset is too large for available GPU memory.");
                }
                throw;
            }
            catch (Exception ex)
            {
                Logger.Log($"[SharpDXVolumeRenderer] Error creating label texture: {ex.Message}");
                throw;
            }
        }

        #endregion Initialization

        #region Rendering

        public void Render()
        {
            if (device == null || swapChain == null || renderPanel == null || isRendering)
            {
                return; // Skip if already rendering or resources are null
            }

            // Check if the panel is minimized or has zero dimensions
            if (renderPanel.ClientSize.Width < 1 || renderPanel.ClientSize.Height < 1)
            {
                return; // Skip rendering to invisible panels
            }

            try
            {
                isRendering = true;

                // Ensure valid viewport dimensions
                int width = Math.Max(1, renderPanel.ClientSize.Width);
                int height = Math.Max(1, renderPanel.ClientSize.Height);

                // Make sure viewport is set explicitly
                context.Rasterizer.SetViewport(0, 0, width, height);

                // Verify render targets are valid
                if (renderTargetView == null || depthView == null)
                {
                    Logger.Log("[SharpDXVolumeRenderer] Cannot render: Render targets are null");
                    RecreateRenderTargets();
                    if (renderTargetView == null || depthView == null)
                        return;
                }

                // Clear the render target
                context.OutputMerger.SetRenderTargets(depthView, renderTargetView);
                context.ClearRenderTargetView(renderTargetView, new Color4(0.0f, 0.0f, 0.0f, 0.0f));
                context.ClearDepthStencilView(depthView, DepthStencilClearFlags.Depth | DepthStencilClearFlags.Stencil, 1.0f, 0);

                // Set blend state for volume rendering
                context.OutputMerger.SetBlendState(alphaBlendState);

                // Handle LOD levels for movement
                bool isMoving = isDragging || isPanning;
                if (useLodSystem && isMoving && !useStreamingRenderer)
                {
                    // During movement, use a suitable LOD level that exists
                    bool foundValidLod = false;
                    for (int i = 1; i <= MAX_LOD_LEVELS; i++)
                    {
                        if (lodVolumeSRVs[i] != null)
                        {
                            currentLodLevel = i;
                            foundValidLod = true;
                            break;
                        }
                    }

                    // If no LOD textures exist, fall back to the original
                    if (!foundValidLod || lodVolumeSRVs[currentLodLevel] == null)
                    {
                        currentLodLevel = 0;
                    }
                }
                else
                {
                    // When not moving, use highest quality
                    currentLodLevel = 0;
                }

           
                if (useStreamingRenderer)
                {
                    // Use the streaming renderer
                    RenderVolumeWithStreaming();
                }
                else
                {
                    // Use the standard renderer
                    RenderVolume();
                }

                // Then render measurements AFTER the volume is rendered
                RenderMeasurements();

                // Draw scale bar and pixel size info
                if (!isMoving || frameCount % 5 == 0) // Update more frequently during movement
                {
                    DrawScaleBar();
                }
                else if (isMoving && scaleBarPictureBox != null)
                {
                    scaleBarPictureBox.Visible = false;
                }
                else if (!isMoving && scaleBarPictureBox != null && !scaleBarPictureBox.Visible)
                {
                    scaleBarPictureBox.Visible = true;
                }

                // Present the scene with better error handling
                try
                {
                    swapChain.Present(isMoving ? 0 : 1, PresentFlags.None);
                }
                catch (SharpDXException ex)
                {
                    HandleRenderException(ex);
                }

                // Reset the needs render flag for infrequent renders when nothing changes
                if (!isMoving)
                {
                    
                    NeedsRender = false;
                }
            }
            catch (Exception ex)
            {
                Logger.Log("[SharpDXVolumeRenderer] Render error: " + ex.Message);
                // Don't rethrow - let the render loop continue
            }
            finally
            {
                isRendering = false;
            }
        }

        private System.Drawing.Point? WorldToScreen(Vector3 worldPos)
        {
            try
            {
                // Calculate view-projection matrix
                float aspectRatio = (float)renderPanel.ClientSize.Width / Math.Max(1, renderPanel.ClientSize.Height);

                // Get camera matrix
                float cosPitch = (float)Math.Cos(cameraPitch);
                float sinPitch = (float)Math.Sin(cameraPitch);
                float cosYaw = (float)Math.Cos(cameraYaw);
                float sinYaw = (float)Math.Sin(cameraYaw);

                Vector3 volumeCenter = new Vector3(volW / 2.0f, volH / 2.0f, volD / 2.0f);
                Vector3 cameraDirection = new Vector3(
                    cosPitch * cosYaw,
                    sinPitch,
                    cosPitch * sinYaw);
                Vector3 cameraPosition = volumeCenter - (cameraDirection * cameraDistance) + panOffset;

                Matrix viewMatrix = Matrix.LookAtLH(
                    cameraPosition,
                    volumeCenter + panOffset,
                    Vector3.UnitY);

                Matrix projMatrix = Matrix.PerspectiveFovLH(
                    (float)Math.PI / 4.0f,
                    aspectRatio,
                    1.0f,
                    cameraDistance * 10.0f);

                // Transform world position to clip space
                Matrix viewProj = viewMatrix * projMatrix;
                Vector4 clipPos = Vector4.Transform(new Vector4(worldPos, 1.0f), viewProj);

                // Check if the point is behind the camera
                if (clipPos.W <= 0)
                    return null;

                // Perspective divide to get normalized device coordinates
                Vector3 ndcPos = new Vector3(
                    clipPos.X / clipPos.W,
                    clipPos.Y / clipPos.W,
                    clipPos.Z / clipPos.W);

                // Check if the point is outside the view frustum
                if (ndcPos.X < -1 || ndcPos.X > 1 || ndcPos.Y < -1 || ndcPos.Y > 1)
                    return null;

                // Convert to screen coordinates
                int screenX = (int)((ndcPos.X + 1) * 0.5f * renderPanel.ClientSize.Width);
                int screenY = (int)((1 - ndcPos.Y) * 0.5f * renderPanel.ClientSize.Height);

                return new System.Drawing.Point(screenX, screenY);
            }
            catch (Exception ex)
            {
                Logger.Log($"[SharpDXVolumeRenderer] WorldToScreen error: {ex.Message}");
                return null;
            }
        }

        private void RecreateRenderTargets()
        {
            try
            {
                // Clean up old render targets
                Utilities.Dispose(ref renderTargetView);
                Utilities.Dispose(ref depthView);
                Utilities.Dispose(ref depthBuffer);

                // Ensure valid dimensions
                int width = Math.Max(1, renderPanel.ClientSize.Width);
                int height = Math.Max(1, renderPanel.ClientSize.Height);

                // Create render target view from backbuffer
                using (Texture2D backBuffer = Texture2D.FromSwapChain<Texture2D>(swapChain, 0))
                {
                    renderTargetView = new RenderTargetView(device, backBuffer);
                }

                // Create depth buffer
                Texture2DDescription depthDesc = new Texture2DDescription
                {
                    Width = width,
                    Height = height,
                    ArraySize = 1,
                    BindFlags = BindFlags.DepthStencil,
                    CpuAccessFlags = CpuAccessFlags.None,
                    Format = Format.D24_UNorm_S8_UInt,
                    MipLevels = 1,
                    OptionFlags = ResourceOptionFlags.None,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default
                };

                depthBuffer = new Texture2D(device, depthDesc);
                depthView = new DepthStencilView(device, depthBuffer);

                Logger.Log("[SharpDXVolumeRenderer] Render targets recreated successfully");
            }
            catch (Exception ex)
            {
                Logger.Log("[SharpDXVolumeRenderer] Error recreating render targets: " + ex.Message);
                throw;
            }
        }

        private void RecreateDevice()
        {
            // Clean up existing resources first
            CleanupDirectXResources();

            // Recreate device and swapchain
            CreateDeviceAndSwapChain();
            CreateRenderTargets();
            CreateRenderStates();
            CreateShaders();

            // Recreate volume resources
            CreateVolumeTextures();
            CreateLabelTextures();
            CreateMaterialColorTexture();
            CreateColorMapTexture();
            CreateLodTextures();

            Logger.Log("[SharpDXVolumeRenderer] Device and resources recreated after device removed error");
        }

        private void CleanupDirectXResources()
        {
            // Clean up render states
            Utilities.Dispose(ref solidRasterState);
            Utilities.Dispose(ref wireframeRasterState);
            Utilities.Dispose(ref alphaBlendState);
            Utilities.Dispose(ref linearSampler);
            Utilities.Dispose(ref pointSampler);

            // Clean up shaders
            Utilities.Dispose(ref volumeVertexShader);
            Utilities.Dispose(ref volumePixelShader);
            Utilities.Dispose(ref inputLayout);
            Utilities.Dispose(ref constantBuffer);

            // Clean up volume textures
            Utilities.Dispose(ref volumeSRV);
            Utilities.Dispose(ref volumeTexture);
            Utilities.Dispose(ref labelSRV);
            Utilities.Dispose(ref labelTexture);

            // Clean up LOD textures
            for (int i = 1; i <= MAX_LOD_LEVELS; i++)
            {
                Utilities.Dispose(ref lodVolumeSRVs[i]);
                Utilities.Dispose(ref lodVolumeTextures[i]);
            }

            // Clean up label textures
            Utilities.Dispose(ref labelVisibilitySRV);
            Utilities.Dispose(ref labelVisibilityTexture);
            Utilities.Dispose(ref labelOpacitySRV);
            Utilities.Dispose(ref labelOpacityTexture);
            Utilities.Dispose(ref materialColorSRV);
            Utilities.Dispose(ref materialColorTexture);
            Utilities.Dispose(ref colorMapSRV);
            Utilities.Dispose(ref colorMapTexture);

            // Clean up render targets
            Utilities.Dispose(ref renderTargetView);
            Utilities.Dispose(ref depthView);
            Utilities.Dispose(ref depthBuffer);

            // Don't dispose the device or swapchain yet - we'll recreate them
            Logger.Log("[SharpDXVolumeRenderer] Device resources cleaned up for recreation");
        }

        private void RenderVolume()
        {
            try
            {
                // Check for required resources
                if (context == null || cubeVertexBuffer == null || cubeIndexBuffer == null)
                {
                    Logger.Log("[SharpDXVolumeRenderer] Cannot render volume: Required resources are null");
                    return;
                }

                // IMPORTANT: Reset any previous shader resources to avoid state confusion
                ResetShaderResources();

                // Use wireframe in debug mode
                context.Rasterizer.State = debugMode ? wireframeRasterState : solidRasterState;

                // Setup rendering pipeline
                context.InputAssembler.InputLayout = inputLayout;
                context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
                context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(cubeVertexBuffer, Utilities.SizeOf<Vector3>(), 0));
                context.InputAssembler.SetIndexBuffer(cubeIndexBuffer, Format.R32_UInt, 0);

                // Set samplers
                context.PixelShader.SetSampler(0, linearSampler);
                context.PixelShader.SetSampler(1, pointSampler);

                // Set the blend state explicitly
                context.OutputMerger.SetBlendState(alphaBlendState, new Color4(0, 0, 0, 0), 0xFFFFFFFF);

                // Prepare resources array
                ShaderResourceView[] resources = new ShaderResourceView[6];

                // Fill the resources array with available resources
                if (labelVisibilitySRV != null) resources[0] = labelVisibilitySRV;
                if (labelOpacitySRV != null) resources[1] = labelOpacitySRV;

                // Set the volume texture resource - use LOD if available during movement, otherwise use original
                bool isMoving = isDragging || isPanning;
                bool useLodForMovement = useLodSystem && isMoving && currentLodLevel > 0 &&
                                       currentLodLevel <= MAX_LOD_LEVELS;

                if (volumeSRV != null)
                {
                    if (useLodForMovement && lodVolumeSRVs[currentLodLevel] != null)
                    {
                        // Use lower detail texture during movement
                        resources[2] = lodVolumeSRVs[currentLodLevel];
                    }
                    else
                    {
                        // Use full detail texture
                        resources[2] = volumeSRV;
                    }
                }

                if (labelSRV != null) resources[3] = labelSRV;
                if (materialColorSRV != null) resources[4] = materialColorSRV;
                if (colorMapSRV != null) resources[5] = colorMapSRV;

                // Set all resources at once
                context.PixelShader.SetShaderResources(0, resources);

                // Set shaders
                context.VertexShader.Set(volumeVertexShader);
                context.PixelShader.Set(volumePixelShader);

                // Update constant buffer for rendering
                float currentStepSize = isMoving ? Math.Min(3.0f, stepSize * 2.0f) : stepSize;

                // For LOD, adjust step size based on level
                if (useLodSystem && currentLodLevel > 0 && isMoving)
                {
                    currentStepSize = lodStepSizes[currentLodLevel];
                }

                UpdateConstantBuffer(currentStepSize);
                context.VertexShader.SetConstantBuffer(0, constantBuffer);
                context.PixelShader.SetConstantBuffer(0, constantBuffer);

                // Draw the cube
                context.DrawIndexed(cubeIndexCount, 0, 0);
            }
            catch (Exception ex)
            {
                Logger.Log("[SharpDXVolumeRenderer] RenderVolume error: " + ex.Message);
                // For render failures, switch to wireframe mode
                debugMode = true;
            }
        }

        /// <summary>
        /// Draws a scale bar and pixel size information in the corner of the render target.
        /// </summary>
        private void DrawScaleBar()
        {
            try
            {
                // Determine scale bar size based on camera distance
                float scale = cameraDistance / 500.0f;
                float barLength = Math.Min(volW, Math.Min(volH, volD)) * 0.2f * scale;

                // Get the pixel size to convert to real-world units
                double pixelSizeInMeters = mainForm.GetPixelSize();
                string lengthLabel;

                // Format with appropriate units
                if (pixelSizeInMeters > 0)
                {
                    double realWorldLength = barLength * pixelSizeInMeters;
                    lengthLabel = FormatPixelSize(realWorldLength);
                }
                else
                {
                    lengthLabel = $"{barLength:F1} voxels";
                }

                // Position the scale bar in the bottom-right corner
                int width = renderPanel.ClientSize.Width;
                int height = renderPanel.ClientSize.Height;
                int barX = width - 120;
                int barY = height - 40;

                // Create the scale bar bitmap
                System.Drawing.Bitmap bitmap = new System.Drawing.Bitmap(120, 35);
                using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(bitmap))
                {
                    // Fill with a semi-transparent background
                    using (var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(128, 0, 0, 32)))
                    {
                        g.FillRectangle(brush, 0, 0, bitmap.Width, bitmap.Height);
                    }

                    // Draw the scale bar
                    using (var pen = new System.Drawing.Pen(System.Drawing.Color.White, 2))
                    {
                        g.DrawLine(pen, 10, 25, 110, 25);
                        g.DrawLine(pen, 10, 20, 10, 30);
                        g.DrawLine(pen, 110, 20, 110, 30);
                    }

                    // Draw the scale label
                    using (var font = new System.Drawing.Font("Arial", 8))
                    {
                        g.DrawString(lengthLabel, font, System.Drawing.Brushes.White, 40, 5);
                    }
                }

                // Create or update PictureBox
                if (scaleBarPictureBox == null)
                {
                    scaleBarPictureBox = new PictureBox();
                    scaleBarPictureBox.SizeMode = PictureBoxSizeMode.AutoSize;
                    scaleBarPictureBox.BackColor = System.Drawing.Color.Transparent;
                    renderPanel.Controls.Add(scaleBarPictureBox);
                    scaleBarPictureBox.BringToFront();
                }

                // Update the PictureBox - dispose old image first
                var oldImage = scaleBarPictureBox.Image;
                scaleBarPictureBox.Image = bitmap;
                oldImage?.Dispose();

                // Update position and ensure visibility
                scaleBarPictureBox.Location = new System.Drawing.Point(barX - 10, barY - 25);
                scaleBarPictureBox.Visible = true;
            }
            catch (Exception ex)
            {
                Logger.Log("[SharpDXVolumeRenderer] DrawScaleBar error: " + ex.Message);
            }
        }

        /// <summary>
        /// Formats a pixel size in meters into a human-readable string
        /// </summary>
        private string FormatPixelSize(double meters)
        {
            if (meters >= 1e-3) // 1mm or larger
                return $"{(meters * 1000):0.###} mm";

            if (meters >= 1e-6) // 1µm or larger
                return $"{(meters * 1e6):0.###} μm";

            return $"{(meters * 1e9):0.###} nm";
        }

        private void UpdateConstantBuffer(float overrideStepSize = -1.0f)
        {
            try
            {
                // Calculate camera position from spherical coordinates
                float cosPitch = (float)Math.Cos(cameraPitch);
                float sinPitch = (float)Math.Sin(cameraPitch);
                float cosYaw = (float)Math.Cos(cameraYaw);
                float sinYaw = (float)Math.Sin(cameraYaw);

                Vector3 volumeCenter = new Vector3(volW / 2.0f, volH / 2.0f, volD / 2.0f);

                Vector3 cameraDirection = new Vector3(
                    cosPitch * cosYaw,
                    sinPitch,
                    cosPitch * sinYaw);

                Vector3 cameraPosition = volumeCenter - (cameraDirection * cameraDistance) + panOffset;

                // Create view and projection matrices
                Matrix viewMatrix = Matrix.LookAtLH(
                    cameraPosition,
                    volumeCenter + panOffset,
                    Vector3.UnitY);

                float aspectRatio = (float)renderPanel.ClientSize.Width / Math.Max(1, renderPanel.ClientSize.Height);
                Matrix projectionMatrix = Matrix.PerspectiveFovLH(
                    (float)Math.PI / 4.0f,  // 45 degrees field of view
                    aspectRatio,
                    1.0f,  // Near plane - set to 1.0 to avoid clipping
                    cameraDistance * 10.0f);

                // Create world-view-projection matrix
                Matrix worldViewProj = Matrix.Transpose(viewMatrix * projectionMatrix);
                Matrix invViewMatrix = Matrix.Transpose(Matrix.Invert(viewMatrix));

                // Use the override step size if provided
                float actualStepSize = (overrideStepSize > 0.0f) ? overrideStepSize : stepSize;

                // Create constant buffer data for shader
                ConstantBufferData cbData = new ConstantBufferData
                {
                    WorldViewProj = worldViewProj,
                    InvViewMatrix = invViewMatrix,
                    Thresholds = new Vector4(minThresholdNorm, maxThresholdNorm, actualStepSize, showGrayscale ? 1.0f : 0.0f),
                    Dimensions = new Vector4(volW, volH, volD, 0),
                    SliceCoords = new Vector4(
                    (float)sliceX / volW,
                    (float)sliceY / volH,
                    (float)sliceZ / volD,
                    (showXSlice ? 1.0f : 0.0f) +
                    (showYSlice ? 2.0f : 0.0f) +
                    (showZSlice ? 4.0f : 0.0f)),
                    CameraPosition = new Vector4(cameraPosition, 1.0f),
                    ColorMapParams = new Vector4(colorMapIndex, sliceBorderThickness, 0, 0),
                    // Add cutting plane data
                    CutPlaneX = new Vector4(
                    cutXEnabled ? 1.0f : 0.0f,  // enabled
                    cutXDirection,               // direction
                    cutXPosition,                // position
                    0.0f),                       // unused
                    CutPlaneY = new Vector4(
                    cutYEnabled ? 1.0f : 0.0f,
                    cutYDirection,
                    cutYPosition,
                    0.0f),
                    CutPlaneZ = new Vector4(
                    cutZEnabled ? 1.0f : 0.0f,
                    cutZDirection,
                    cutZPosition,
                    0.0f),
                    // Add clipping plane data
                    ClippingPlane1 = new Vector4(
                    clippingPlaneEnabled ? 1.0f : 0.0f,  // enabled
                    clippingPlaneNormal.X,               // normal.x
                    clippingPlaneNormal.Y,               // normal.y
                    clippingPlaneNormal.Z),              // normal.z
                    ClippingPlane2 = new Vector4(
                    clippingPlaneDistance,               // distance
                    clippingPlaneMirror ? 1.0f : 0.0f,  // mirror
                    0.0f,                                // unused
                    0.0f)                                // unused
                };

                // Update the constant buffer
                context.UpdateSubresource(ref cbData, constantBuffer);

                if (frameCount % 60 == 0)
                {
                    //Logger.Log("[SharpDXVolumeRenderer] Updated constant buffer");
                }
            }
            catch (Exception ex)
            {
                Logger.Log("[SharpDXVolumeRenderer] UpdateConstantBuffer error: " + ex.Message);
            }
        }

        private void UpdateLabelTextures()
        {
            try
            {
                if (labelVisibilityTexture == null || labelOpacityTexture == null)
                {
                    Logger.Log("[SharpDXVolumeRenderer] Cannot update label textures: textures are null");
                    return;
                }

                lock (textureLock)
                {
                    // Update visibility texture
                    DataStream visibilityStream = null;
                    try
                    {
                        context.MapSubresource(
                            labelVisibilityTexture,
                            0,
                            MapMode.WriteDiscard,
                            SharpDX.Direct3D11.MapFlags.None,
                            out visibilityStream);

                        for (int i = 0; i < MAX_LABELS; i++)
                        {
                            float visValue = labelVisible[i] ? 1.0f : 0.0f;
                            visibilityStream.Write(visValue);
                        }

                        context.UnmapSubresource(labelVisibilityTexture, 0);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log("[SharpDXVolumeRenderer] Error updating visibility texture: " + ex.Message);
                        throw;
                    }
                    finally
                    {
                        if (visibilityStream != null)
                        {
                            visibilityStream.Dispose();
                        }
                    }

                    // Update opacity texture
                    DataStream opacityStream = null;
                    try
                    {
                        context.MapSubresource(
                            labelOpacityTexture,
                            0,
                            MapMode.WriteDiscard,
                            SharpDX.Direct3D11.MapFlags.None,
                            out opacityStream);

                        for (int i = 0; i < MAX_LABELS; i++)
                        {
                            opacityStream.Write(labelOpacity[i]);
                        }

                        context.UnmapSubresource(labelOpacityTexture, 0);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log("[SharpDXVolumeRenderer] Error updating opacity texture: " + ex.Message);
                        throw;
                    }
                    finally
                    {
                        if (opacityStream != null)
                        {
                            opacityStream.Dispose();
                        }
                    }
                }

                // Force a render after updating textures
                NeedsRender = true;
            }
            catch (Exception ex)
            {
                Logger.Log("[SharpDXVolumeRenderer] UpdateLabelTextures error: " + ex.Message);
            }
        }

        #endregion Rendering

        #region Public Methods
        public void RecreateLabelTextures()
        {
            try
            {
                Logger.Log("[SharpDXVolumeRenderer] Recreating label textures");

                // Dispose existing textures
                Utilities.Dispose(ref labelSRV);
                Utilities.Dispose(ref labelTexture);

                // Recreate from current data
                if (mainForm.volumeLabels != null)
                {
                    labelTexture = CreateTexture3DFromChunkedLabelVolume((ChunkedLabelVolume)mainForm.volumeLabels, Format.R32_Float);
                    if (labelTexture != null)
                    {
                        ShaderResourceViewDescription srvDesc = new ShaderResourceViewDescription
                        {
                            Format = Format.R32_Float,
                            Dimension = ShaderResourceViewDimension.Texture3D,
                            Texture3D = new ShaderResourceViewDescription.Texture3DResource
                            {
                                MipLevels = 1,
                                MostDetailedMip = 0
                            }
                        };

                        labelSRV = new ShaderResourceView(device, labelTexture, srvDesc);
                        Logger.Log("[SharpDXVolumeRenderer] Label texture recreated successfully");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[SharpDXVolumeRenderer] Error recreating label textures: {ex.Message}");
            }
        }
        public void OnResize()
        {
            if (device == null || swapChain == null) return;

            try
            {
                // Release render targets
                Utilities.Dispose(ref renderTargetView);
                Utilities.Dispose(ref depthView);
                Utilities.Dispose(ref depthBuffer);

                // Ensure valid dimensions
                int width = Math.Max(1, renderPanel.ClientSize.Width);
                int height = Math.Max(1, renderPanel.ClientSize.Height);

                // Resize swap chain
                swapChain.ResizeBuffers(
                    2, // Double buffering
                    width,
                    height,
                    Format.R8G8B8A8_UNorm,
                    SwapChainFlags.None);

                // Recreate render targets
                CreateRenderTargets();

                // Update scale bar position after resize
                DrawScaleBar();

                // Mark that we need rendering
                NeedsRender = true;

                Logger.Log($"[SharpDXVolumeRenderer] Resized to {width}x{height}");
            }
            catch (Exception ex)
            {
                Logger.Log("[SharpDXVolumeRenderer] OnResize error: " + ex.Message);
            }
        }

        public void UpdateSlices(int x, int y, int z)
        {
            sliceX = Math.Max(0, Math.Min(x, volW - 1));
            sliceY = Math.Max(0, Math.Min(y, volH - 1));
            sliceZ = Math.Max(0, Math.Min(z, volD - 1));

            // Mark that we need rendering
            NeedsRender = true;
        }

        public void SetRaymarchStepSize(float step)
        {
            stepSize = Math.Max(0.1f, Math.Min(5.0f, step));

            // Mark that we need rendering
            NeedsRender = true;
        }

        public void SetMaterialVisibility(byte materialId, bool visible)
        {
            if (materialId < MAX_LABELS)
            {
                Logger.Log($"[SharpDXVolumeRenderer] Setting material {materialId} visibility to {visible}");

                // Update the visibility state
                labelVisible[materialId] = visible;

                // Make sure the visibility textures are updated immediately
                UpdateLabelTextures();

                // Force a re-render
                NeedsRender = true;
            }
        }

        public void SetMaterialOpacity(byte materialId, float opacity)
        {
            if (materialId < MAX_LABELS)
            {
                opacity = Math.Max(0.0f, Math.Min(1.0f, opacity));

                // Only update if the value has changed significantly
                if (Math.Abs(labelOpacity[materialId] - opacity) > 0.001f)
                {
                    // Update the opacity state
                    labelOpacity[materialId] = opacity;

                    try
                    {
                        // Make sure the visibility textures are updated immediately
                        UpdateLabelTextures();

                        // Force a re-render
                        NeedsRender = true;
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[SharpDXVolumeRenderer] Error setting material opacity: {ex.Message}");
                        // Don't rethrow - we want the UI to remain responsive even if an update fails
                    }
                }
            }
        }

        public bool GetMaterialVisibility(byte materialId)
        {
            if (materialId < MAX_LABELS)
            {
                return labelVisible[materialId];
            }
            return false;
        }

        public float GetMaterialOpacity(byte materialId)
        {
            if (materialId < MAX_LABELS)
            {
                return labelOpacity[materialId];
            }
            return 1.0f;
        }

        public bool[] GetLabelVisibilityArray()
        {
            bool[] result = new bool[MAX_LABELS];
            Array.Copy(labelVisible, result, MAX_LABELS);
            return result;
        }

        public void SaveScreenshot(string filePath)
        {
            // Ensure we have a fresh render
            NeedsRender = true;
            Render();

            try
            {
                // Get the dimensions of the render panel
                int width = renderPanel.ClientSize.Width;
                int height = renderPanel.ClientSize.Height;

                // Create a bitmap to hold the entire screenshot including UI elements
                using (var screenshot = new System.Drawing.Bitmap(width, height))
                {
                    // Create graphics object from the bitmap
                    using (var g = System.Drawing.Graphics.FromImage(screenshot))
                    {
                        // Capture the backbuffer first
                        using (var backBuffer = Texture2D.FromSwapChain<Texture2D>(swapChain, 0))
                        {
                            // Create a staging texture for CPU read access
                            var desc = backBuffer.Description;
                            desc.CpuAccessFlags = CpuAccessFlags.Read;
                            desc.Usage = ResourceUsage.Staging;
                            desc.BindFlags = BindFlags.None;
                            desc.OptionFlags = ResourceOptionFlags.None;

                            using (var stagingTexture = new Texture2D(device, desc))
                            {
                                // Copy to staging texture
                                context.CopyResource(backBuffer, stagingTexture);

                                // Map the staging texture
                                var dataBox = context.MapSubresource(
                                    stagingTexture,
                                    0,
                                    MapMode.Read,
                                    SharpDX.Direct3D11.MapFlags.None);

                                // Create a bitmap of the 3D content
                                using (var d3dBitmap = new System.Drawing.Bitmap(
                                    desc.Width,
                                    desc.Height,
                                    System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                                {
                                    var bitmapData = d3dBitmap.LockBits(
                                        new System.Drawing.Rectangle(0, 0, d3dBitmap.Width, d3dBitmap.Height),
                                        System.Drawing.Imaging.ImageLockMode.WriteOnly,
                                        System.Drawing.Imaging.PixelFormat.Format32bppArgb);

                                    // Copy data row by row
                                    for (int y = 0; y < desc.Height; y++)
                                    {
                                        IntPtr sourceRow = dataBox.DataPointer + y * dataBox.RowPitch;
                                        IntPtr destRow = bitmapData.Scan0 + y * bitmapData.Stride;
                                        Utilities.CopyMemory(destRow, sourceRow, desc.Width * 4);
                                    }

                                    d3dBitmap.UnlockBits(bitmapData);

                                    // Draw the 3D content onto our screenshot bitmap
                                    g.DrawImage(d3dBitmap, 0, 0, width, height);
                                }

                                // Unmap the resource
                                context.UnmapSubresource(stagingTexture, 0);
                            }
                        }

                        // Now capture all UI elements
                        // First the scale bar if it exists
                        bool wasScaleBarVisible = false;
                        if (scaleBarPictureBox != null)
                        {
                            wasScaleBarVisible = scaleBarPictureBox.Visible;
                            scaleBarPictureBox.Visible = true;
                            DrawScaleBar(); // Force an update

                            // Draw the scale bar onto the screenshot
                            g.DrawImage(scaleBarPictureBox.Image, scaleBarPictureBox.Location);
                            scaleBarPictureBox.Visible = wasScaleBarVisible;
                        }

                        // Now draw measurements if they're visible
                        foreach (var measurement in measurements)
                        {
                            if (!measurement.Visible)
                                continue;

                            // Convert 3D world coordinates to screen coordinates
                            var screenStart = WorldToScreen(measurement.Start);
                            var screenEnd = WorldToScreen(measurement.End);

                            if (!screenStart.HasValue || !screenEnd.HasValue)
                                continue;

                            var startPoint = screenStart.Value;
                            var endPoint = screenEnd.Value;

                            // Draw line
                            using (var pen = new System.Drawing.Pen(measurement.Color, 2))
                            {
                                g.DrawLine(pen, startPoint.X, startPoint.Y, endPoint.X, endPoint.Y);

                                // Draw endpoints
                                float radius = 3;
                                g.FillEllipse(System.Drawing.Brushes.White,
                                    startPoint.X - radius, startPoint.Y - radius,
                                    radius * 2, radius * 2);
                                g.FillEllipse(System.Drawing.Brushes.White,
                                    endPoint.X - radius, endPoint.Y - radius,
                                    radius * 2, radius * 2);
                            }

                            // Draw label with distance
                            string labelText = $"{measurement.Label}: {measurement.RealDistance:F2} {measurement.Unit}";

                            // Calculate text position (middle of the line)
                            int textX = (startPoint.X + endPoint.X) / 2;
                            int textY = (startPoint.Y + endPoint.Y) / 2;

                            // Draw background for the text for better visibility
                            var font = new System.Drawing.Font("Arial", 8);
                            var textSize = g.MeasureString(labelText, font);

                            g.FillRectangle(
                                new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(150, 0, 0, 0)),
                                textX - textSize.Width / 2, textY - textSize.Height / 2,
                                textSize.Width, textSize.Height);

                            // Draw text
                            g.DrawString(
                                labelText,
                                font,
                                System.Drawing.Brushes.White,
                                textX - textSize.Width / 2,
                                textY - textSize.Height / 2);
                        }
                    }

                    // Save the combined screenshot
                    string extension = Path.GetExtension(filePath).ToLower();
                    if (extension == ".jpg" || extension == ".jpeg")
                    {
                        screenshot.Save(filePath, System.Drawing.Imaging.ImageFormat.Jpeg);
                    }
                    else if (extension == ".png")
                    {
                        screenshot.Save(filePath, System.Drawing.Imaging.ImageFormat.Png);
                    }
                    else
                    {
                        // Default to JPEG
                        screenshot.Save(filePath, System.Drawing.Imaging.ImageFormat.Jpeg);
                    }
                }

                Logger.Log("[SharpDXVolumeRenderer] Screenshot saved to: " + filePath);
            }
            catch (Exception ex)
            {
                Logger.Log("[SharpDXVolumeRenderer] Screenshot error: " + ex.Message);
            }
        }

        #endregion Public Methods

        #region Mouse Handlers

        private void OnMouseDown(object sender, MouseEventArgs e)
        {
            // If in measurement mode, only handle measurement creation and block all other interactions
            if (measurementMode)
            {
                if (e.Button == MouseButtons.Left)
                {
                    // Start measurement
                    isDrawingMeasurement = true;

                    // Perform ray cast to find the start point in 3D space
                    if (RaycastToVolume(e.X, e.Y, out Vector3 worldPos))
                    {
                        measureStartPoint = worldPos;
                        measureEndPoint = worldPos; // Initialize end point to same as start point
                        Logger.Log($"[SharpDXVolumeRenderer] Started measurement at {worldPos}");
                    }

                    // Mark that we need rendering to show the measurement preview
                    NeedsRender = true;
                }

                // Important: Block ALL mouse interactions in measurement mode
                return;
            }
            else if (e.Button == MouseButtons.Left)
            {
                // Original orbit camera behavior
                isDragging = true;
                lastMousePosition = e.Location;

                // Set temporary lower quality during movement
                stepSize = Math.Min(2.0f, stepSize * 2.0f);

                // Mark that we need rendering
                NeedsRender = true;
            }
            else if (e.Button == MouseButtons.Right)
            {
                // Original pan camera behavior
                isPanning = true;
                lastMousePosition = e.Location;

                // Set temporary lower quality during movement
                stepSize = Math.Min(2.0f, stepSize * 2.0f);

                // Mark that we need rendering
                NeedsRender = true;
            }
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            // If in measurement mode, only update the measurement end point
            if (measurementMode)
            {
                if (isDrawingMeasurement)
                {
                    // Update the end point of the measurement
                    if (RaycastToVolume(e.X, e.Y, out Vector3 worldPos))
                    {
                        measureEndPoint = worldPos;
                        NeedsRender = true; // Redraw to show the measurement preview
                    }
                }

                // Important: Block ALL other mouse handling in measurement mode
                return;
            }
            else if (isDragging)
            {
                // Calculate delta with damping for smoother movement
                float dx = (e.X - lastMousePosition.X) * 0.01f;
                float dy = (e.Y - lastMousePosition.Y) * 0.01f;

                // Smaller movements produce less aggressive camera changes
                if (Math.Abs(dx) > 0.0001f || Math.Abs(dy) > 0.0001f)
                {
                    cameraYaw += dx;
                    cameraPitch = Math.Max(-1.5f, Math.Min(1.5f, cameraPitch + dy));

                    // Only update last position if we actually moved significantly
                    lastMousePosition = e.Location;

                    // Mark that we need rendering
                    NeedsRender = true;
                }
            }
            else if (isPanning)
            {
                // Pan the camera with damping
                float dx = (e.X - lastMousePosition.X) * 0.1f;
                float dy = (e.Y - lastMousePosition.Y) * 0.1f;

                // Only update for significant movements
                if (Math.Abs(dx) > 0.01f || Math.Abs(dy) > 0.01f)
                {
                    // Convert screen space to world space direction
                    float cosYaw = (float)Math.Cos(cameraYaw);
                    float sinYaw = (float)Math.Sin(cameraYaw);

                    Vector3 rightDir = new Vector3(sinYaw, 0, -cosYaw);
                    Vector3 upDir = Vector3.UnitY;

                    panOffset += rightDir * dx - upDir * dy;

                    // Only update last position if we actually moved significantly
                    lastMousePosition = e.Location;

                    // Mark that we need rendering
                    NeedsRender = true;
                }
            }
        }

        private void OnMouseUp(object sender, MouseEventArgs e)
        {
            // If in measurement mode, only handle measurement completion
            if (measurementMode)
            {
                if (isDrawingMeasurement && e.Button == MouseButtons.Left)
                {
                    // Finish measurement
                    if (RaycastToVolume(e.X, e.Y, out Vector3 worldPos))
                    {
                        measureEndPoint = worldPos;

                        // Calculate distance
                        float distance = Vector3.Distance(measureStartPoint, measureEndPoint);

                        // Don't add measurements that are too small (likely accidental clicks)
                        if (distance < 0.5f)
                        {
                            isDrawingMeasurement = false;
                            Logger.Log("[SharpDXVolumeRenderer] Measurement too small, discarded");
                            NeedsRender = true;
                            return;
                        }

                        // Convert to real-world units
                        double pixelSizeInMeters = mainForm.GetPixelSize();
                        double realWorldDistance = distance * pixelSizeInMeters;
                        string unit = "m";
                        float displayDistance = (float)realWorldDistance;

                        // Format with appropriate units
                        if (realWorldDistance < 0.001 && realWorldDistance > 0)
                        {
                            unit = "µm";
                            displayDistance = (float)(realWorldDistance * 1e6);
                        }
                        else if (realWorldDistance < 1 && realWorldDistance >= 0.001)
                        {
                            unit = "mm";
                            displayDistance = (float)(realWorldDistance * 1e3);
                        }

                        // Check if the measurement is on a slice plane
                        bool isOnSlice = false;
                        int sliceType = 0;
                        int slicePosition = 0;

                        if (showXSlice || showYSlice || showZSlice)
                        {
                            // Check X slice (YZ plane)
                            if (showXSlice && Math.Abs(measureStartPoint.X - sliceX) < 0.5f &&
                                Math.Abs(measureEndPoint.X - sliceX) < 0.5f)
                            {
                                isOnSlice = true;
                                sliceType = 1;
                                slicePosition = sliceX;
                            }
                            // Check Y slice (XZ plane)
                            else if (showYSlice && Math.Abs(measureStartPoint.Y - sliceY) < 0.5f &&
                                     Math.Abs(measureEndPoint.Y - sliceY) < 0.5f)
                            {
                                isOnSlice = true;
                                sliceType = 2;
                                slicePosition = sliceY;
                            }
                            // Check Z slice (XY plane)
                            else if (showZSlice && Math.Abs(measureStartPoint.Z - sliceZ) < 0.5f &&
                                     Math.Abs(measureEndPoint.Z - sliceZ) < 0.5f)
                            {
                                isOnSlice = true;
                                sliceType = 3;
                                slicePosition = sliceZ;
                            }
                        }

                        // Create measurement with a distinct color based on index
                        System.Drawing.Color measurementColor;
                        int colorIndex = measurements.Count % 7;
                        switch (colorIndex)
                        {
                            case 0: measurementColor = System.Drawing.Color.White; break;
                            case 1: measurementColor = System.Drawing.Color.Yellow; break;
                            case 2: measurementColor = System.Drawing.Color.Cyan; break;
                            case 3: measurementColor = System.Drawing.Color.Magenta; break;
                            case 4: measurementColor = System.Drawing.Color.LimeGreen; break;
                            case 5: measurementColor = System.Drawing.Color.Orange; break;
                            case 6: measurementColor = System.Drawing.Color.Pink; break;
                            default: measurementColor = System.Drawing.Color.White; break;
                        }

                        var measurement = new MeasurementLine
                        {
                            Start = measureStartPoint,
                            End = measureEndPoint,
                            Distance = distance,
                            RealDistance = displayDistance,
                            Unit = unit,
                            Label = $"M{measurements.Count + 1}",
                            IsOnSlice = isOnSlice,
                            SliceType = sliceType,
                            SlicePosition = slicePosition,
                            Visible = true, // Ensure the measurement is visible by default
                            Color = measurementColor
                        };

                        measurements.Add(measurement);

                        // Log the measurement creation
                        Logger.Log($"[SharpDXVolumeRenderer] Added measurement: {measurement.Label}, " +
                                  $"Distance: {displayDistance:F2} {unit}, " +
                                  $"Visible: {measurement.Visible}");

                        // IMPORTANT: Update the UI on the correct thread
                        if (controlPanel != null)
                        {
                            try
                            {
                                // Use BeginInvoke to ensure UI update happens on UI thread
                                renderPanel.BeginInvoke(new Action(() =>
                                {
                                    controlPanel.RefreshMeasurementsList();
                                }));
                            }
                            catch (Exception ex)
                            {
                                Logger.Log($"[SharpDXVolumeRenderer] Error updating control panel: {ex.Message}");
                            }
                        }
                    }

                    isDrawingMeasurement = false;
                    measurementMode = false; // Exit measurement mode after creating one

                    // Notify UI that measurement mode is complete
                    if (controlPanel != null)
                    {
                        try
                        {
                            renderPanel.BeginInvoke(new Action(() =>
                            {
                                controlPanel.UpdateMeasurementUI(false);
                            }));
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"[SharpDXVolumeRenderer] Error updating measurement UI: {ex.Message}");
                        }
                    }

                    NeedsRender = true;
                }

                // Important: Block ALL other mouse handling in measurement mode
                return;
            }
            else if (e.Button == MouseButtons.Left)
            {
                isDragging = false;

                // Force a high-quality render after movement stops
                currentLodLevel = 0;
                NeedsRender = true;
            }
            else if (e.Button == MouseButtons.Right)
            {
                isPanning = false;

                // Force a high-quality render after movement stops
                currentLodLevel = 0;
                NeedsRender = true;
            }
        }

        private void OnMouseWheel(object sender, MouseEventArgs e)
        {
            // Zoom in/out with smoother response
            float zoomFactor = e.Delta > 0 ? 0.95f : 1.05f; // More gentle zoom

            // Calculate previous distance to compare later
            float prevDistance = cameraDistance;

            // Apply zoom with smoothing
            cameraDistance *= zoomFactor;

            // Clamp distance
            float minDistance = Math.Max(1.0f, Math.Min(volW, Math.Min(volH, volD)) * 0.5f);
            float maxDistance = Math.Max(volW, Math.Max(volH, volD)) * 5.0f;
            cameraDistance = Math.Max(minDistance, Math.Min(maxDistance, cameraDistance));

            // Only render if the distance actually changed significantly
            if (Math.Abs(cameraDistance - prevDistance) > 0.1f)
            {
                // Update scale bar immediately after zoom
                DrawScaleBar();

                // Mark that we need rendering
                NeedsRender = true;
            }
        }

        #endregion Mouse Handlers

        #region IDisposable Implementation

        public void Dispose()
        {
            try
            {
                // Clean up streaming resources if enabled
                if (useStreamingRenderer)
                {
                    DisposeStreamingResources();
                }

                // First clean up UI controls
                if (measurementOverlay != null)
                {
                    try
                    {
                      
                        if (renderPanel != null && renderPanel.Controls.Contains(measurementOverlay))
                        {
                            renderPanel.Controls.Remove(measurementOverlay);
                        }

                        // Then dispose the image and picturebox
                        if (measurementOverlay != null)
                        {
                            if (renderPanel != null && renderPanel.Controls.Contains(measurementOverlay))
                            {
                                renderPanel.Controls.Remove(measurementOverlay);
                            }
                            if (measurementOverlay.Image != null)
                            {
                                measurementOverlay.Image.Dispose();
                                measurementOverlay.Image = null;
                            }
                            measurementOverlay.Dispose();
                            measurementOverlay = null;
                        }
                        if (textRenderer != null)
                        {
                            textRenderer.Dispose();
                            textRenderer = null;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log("[SharpDXVolumeRenderer] Error disposing measurementOverlay: " + ex.Message);
                    }
                }
                renderTimer?.Stop();
                renderTimer?.Dispose();
                if (scaleBarPictureBox != null)
                {
                    try
                    {
                      
                        if (renderPanel != null && renderPanel.Controls.Contains(scaleBarPictureBox))
                        {
                            renderPanel.Controls.Remove(scaleBarPictureBox);
                        }

                        // Then dispose the image and picturebox
                        if (scaleBarPictureBox.Image != null)
                        {
                            var img = scaleBarPictureBox.Image;
                            scaleBarPictureBox.Image = null;
                            img.Dispose();
                        }
                        scaleBarPictureBox.Dispose();
                        scaleBarPictureBox = null;
                    }
                    catch (Exception ex)
                    {
                        Logger.Log("[SharpDXVolumeRenderer] Error disposing scaleBarPictureBox: " + ex.Message);
                    }
                }

                // Unregister mouse event handlers
                if (renderPanel != null)
                {
                    try
                    {
                        renderPanel.MouseDown -= OnMouseDown;
                        renderPanel.MouseMove -= OnMouseMove;
                        renderPanel.MouseUp -= OnMouseUp;
                        renderPanel.MouseWheel -= OnMouseWheel;
                    }
                    catch (Exception ex)
                    {
                        Logger.Log("[SharpDXVolumeRenderer] Error removing event handlers: " + ex.Message);
                    }
                }

                // Dispose render states
                Utilities.Dispose(ref solidRasterState);
                Utilities.Dispose(ref wireframeRasterState);
                Utilities.Dispose(ref alphaBlendState);
                Utilities.Dispose(ref linearSampler);
                Utilities.Dispose(ref pointSampler);

                // Dispose shaders and buffers
                Utilities.Dispose(ref volumeVertexShader);
                Utilities.Dispose(ref volumePixelShader);
                Utilities.Dispose(ref inputLayout);
                Utilities.Dispose(ref constantBuffer);
                Utilities.Dispose(ref cubeVertexBuffer);
                Utilities.Dispose(ref cubeIndexBuffer);

                // Dispose volume textures
                Utilities.Dispose(ref volumeSRV);
                Utilities.Dispose(ref volumeTexture);
                Utilities.Dispose(ref labelSRV);
                Utilities.Dispose(ref labelTexture);

                // Dispose LOD textures
                for (int i = 1; i <= MAX_LOD_LEVELS; i++)
                {
                    Utilities.Dispose(ref lodVolumeSRVs[i]);
                    Utilities.Dispose(ref lodVolumeTextures[i]);
                }

                // Dispose label textures
                Utilities.Dispose(ref labelVisibilitySRV);
                Utilities.Dispose(ref labelVisibilityTexture);
                Utilities.Dispose(ref labelOpacitySRV);
                Utilities.Dispose(ref labelOpacityTexture);
                Utilities.Dispose(ref materialColorSRV);
                Utilities.Dispose(ref materialColorTexture);
                Utilities.Dispose(ref colorMapSRV);
                Utilities.Dispose(ref colorMapTexture);

                // Dispose render targets
                Utilities.Dispose(ref renderTargetView);
                Utilities.Dispose(ref depthView);
                Utilities.Dispose(ref depthBuffer);

                // Dispose device and swap chain
                Utilities.Dispose(ref swapChain);
                Utilities.Dispose(ref context);
                Utilities.Dispose(ref device);

                Logger.Log("[SharpDXVolumeRenderer] Resources disposed successfully");
            }
            catch (Exception ex)
            {
                Logger.Log("[SharpDXVolumeRenderer] Error during disposal: " + ex.Message);
            }
        }

        #endregion IDisposable Implementation

        #region DXMeasurements

        public void SetMeasurementMode(bool enabled)
        {
            measurementMode = enabled;
            if (!enabled)
            {
                isDrawingMeasurement = false;
            }
            NeedsRender = true;

            Logger.Log($"[SharpDXVolumeRenderer] Measurement mode {(enabled ? "enabled" : "disabled")}");
        }

        private void CreateMeasurementResources()
        {
            try
            {
                // Compile the vertex shader for lines
                string lineShaderCode = @"
        cbuffer ConstantBuffer : register(b0)
        {
            matrix worldViewProj;
        };

        struct VS_INPUT
        {
            float3 Position : POSITION;
            float4 Color : COLOR;
        };

        struct VS_OUTPUT
        {
            float4 Position : SV_POSITION;
            float4 Color : COLOR;
        };

        VS_OUTPUT VSMain(VS_INPUT input)
        {
            VS_OUTPUT output;
            output.Position = mul(float4(input.Position, 1.0), worldViewProj);
            output.Color = input.Color;
            return output;
        }

        float4 PSMain(VS_OUTPUT input) : SV_TARGET
        {
            return input.Color;
        }";

                using (var vertexShaderBytecode = SharpDX.D3DCompiler.ShaderBytecode.Compile(
                    lineShaderCode, "VSMain", "vs_5_0", SharpDX.D3DCompiler.ShaderFlags.Debug))
                {
                    lineVertexShader = new VertexShader(device, vertexShaderBytecode);

                    // Create input layout with position and color
                    InputElement[] inputElements = new[] {
                new InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElement("COLOR", 0, Format.R32G32B32A32_Float, 12, 0)
            };

                    lineInputLayout = new InputLayout(device, vertexShaderBytecode, inputElements);
                }

                using (var pixelShaderBytecode = SharpDX.D3DCompiler.ShaderBytecode.Compile(
                    lineShaderCode, "PSMain", "ps_5_0", SharpDX.D3DCompiler.ShaderFlags.Debug))
                {
                    linePixelShader = new PixelShader(device, pixelShaderBytecode);
                }

                // Create an initial empty vertex buffer for measurements
                BufferDescription vbDesc = new BufferDescription(
                    1024, // Initial size
                    ResourceUsage.Dynamic,
                    BindFlags.VertexBuffer,
                    CpuAccessFlags.Write,
                    ResourceOptionFlags.None,
                    0);

                lineVertexBuffer = new Buffer(device, vbDesc);
                EnsureTextRenderer();
                Logger.Log("[SharpDXVolumeRenderer] Measurement rendering resources created");
            }
            catch (Exception ex)
            {
                Logger.Log("[SharpDXVolumeRenderer] Error creating measurement resources: " + ex.Message);
            }
        }

        private void UpdateMeasurementBuffer()
        {
            try
            {
                // Clear existing vertex collections
                measurementVertices.Clear();
                measurementColors.Clear();

                // Create a list to hold our properly formatted vertices
                List<LineVertex> vertices = new List<LineVertex>();

                // Process all saved measurements
                foreach (var measurement in measurements)
                {
                    if (!measurement.Visible)
                        continue;

                    // Convert color to Vector4
                    System.Drawing.Color measurementColor = measurement.Color;
                    Vector4 color = new Vector4(
                        measurementColor.R / 255.0f,
                        measurementColor.G / 255.0f,
                        measurementColor.B / 255.0f,
                        1.0f);

                    // Add the line vertices (start and end points)
                    vertices.Add(new LineVertex(measurement.Start, color));
                    vertices.Add(new LineVertex(measurement.End, color));

                    // Also store in the individual arrays (for backward compatibility)
                    measurementVertices.Add(measurement.Start);
                    measurementColors.Add(color);
                    measurementVertices.Add(measurement.End);
                    measurementColors.Add(color);
                }

                // Add current measurement being drawn if active
                if (isDrawingMeasurement)
                {
                    // Use yellow for the active measurement
                    Vector4 highlightColor = new Vector4(1.0f, 1.0f, 0.0f, 1.0f);

                    // Add to the vertex list
                    vertices.Add(new LineVertex(measureStartPoint, highlightColor));
                    vertices.Add(new LineVertex(measureEndPoint, highlightColor));

                    // Also add to the arrays
                    measurementVertices.Add(measureStartPoint);
                    measurementColors.Add(highlightColor);
                    measurementVertices.Add(measureEndPoint);
                    measurementColors.Add(highlightColor);
                }

                // If there are no vertices, nothing to update
                if (vertices.Count == 0)
                    return;

                // Size of our vertex structure in bytes
                int stride = Utilities.SizeOf<LineVertex>();
                int vertexCount = vertices.Count;
                int dataSize = stride * vertexCount;

                // Recreate the buffer if needed or if it's too small
                if (lineVertexBuffer == null || lineVertexBuffer.Description.SizeInBytes < dataSize)
                {
                    Utilities.Dispose(ref lineVertexBuffer);

                    BufferDescription vbDesc = new BufferDescription(
                        Math.Max(dataSize, 1024), // Ensure minimum size
                        ResourceUsage.Dynamic,
                        BindFlags.VertexBuffer,
                        CpuAccessFlags.Write,
                        ResourceOptionFlags.None,
                        stride);

                    lineVertexBuffer = new Buffer(device, vbDesc);
                }

                // Map the buffer for writing
                DataStream dataStream;
                context.MapSubresource(
                    lineVertexBuffer,
                    0,
                    MapMode.WriteDiscard,
                    SharpDX.Direct3D11.MapFlags.None,
                    out dataStream);

                // Write all vertices in one go
                dataStream.WriteRange(vertices.ToArray());

                // Unmap the buffer
                context.UnmapSubresource(lineVertexBuffer, 0);
                dataStream.Dispose();
            }
            catch (Exception ex)
            {
                Logger.Log("[SharpDXVolumeRenderer] Error updating measurement buffer: " + ex.Message);
            }
        }

        private void EnsureTextRenderer()
        {
            try
            {
                if (textRenderer == null && renderPanel != null)
                {
                    textRenderer = new MeasurementTextRenderer(renderPanel);
                    Logger.Log("[SharpDXVolumeRenderer] Created text renderer");
                }
            }
            catch (Exception ex)
            {
                Logger.Log("[SharpDXVolumeRenderer] Error creating text renderer: " + ex.Message);
            }
        }

        private void UpdateMeasurementLabels()
        {
            try
            {
                if (textRenderer == null)
                    return;

                // Clear existing labels
                textRenderer.ClearLabels();

                // Process each visible measurement
                foreach (var measurement in measurements)
                {
                    if (!measurement.Visible)
                        continue;

                    // Convert 3D world coordinates to screen coordinates
                    var screenStart = WorldToScreen(measurement.Start);
                    var screenEnd = WorldToScreen(measurement.End);

                    if (!screenStart.HasValue || !screenEnd.HasValue)
                        continue;

                    var startPoint = screenStart.Value;
                    var endPoint = screenEnd.Value;

                    // Calculate midpoint for the label
                    int textX = (startPoint.X + endPoint.X) / 2;
                    int textY = (startPoint.Y + endPoint.Y) / 2;

                    // Format the label text
                    string labelText = $"{measurement.Label}: {measurement.RealDistance:F2} {measurement.Unit}";

                    // Add to the text renderer
                    textRenderer.AddLabel(
                        labelText,
                        new System.Drawing.Point(textX, textY),
                        System.Drawing.Color.FromArgb(150, 0, 0, 0),  // Semi-transparent background
                        System.Drawing.Color.White);  // White text
                }

                // Add label for the in-progress measurement
                if (isDrawingMeasurement)
                {
                    var screenStart = WorldToScreen(measureStartPoint);
                    var screenEnd = WorldToScreen(measureEndPoint);

                    if (screenStart.HasValue && screenEnd.HasValue)
                    {
                        var startPoint = screenStart.Value;
                        var endPoint = screenEnd.Value;

                        // Calculate the distance
                        float distance = Vector3.Distance(measureStartPoint, measureEndPoint);
                        double pixelSizeInMeters = mainForm.GetPixelSize();
                        double realWorldDistance = distance * pixelSizeInMeters;

                        // Format with appropriate units
                        string unit = "m";
                        float displayDistance = (float)realWorldDistance;

                        if (realWorldDistance < 0.001 && realWorldDistance > 0)
                        {
                            unit = "µm";
                            displayDistance = (float)(realWorldDistance * 1e6);
                        }
                        else if (realWorldDistance < 1 && realWorldDistance >= 0.001)
                        {
                            unit = "mm";
                            displayDistance = (float)(realWorldDistance * 1e3);
                        }

                        string labelText = $"Distance: {displayDistance:F2} {unit} ({distance:F1} voxels)";

                        // Calculate text position (middle of the line)
                        int textX = (startPoint.X + endPoint.X) / 2;
                        int textY = (startPoint.Y + endPoint.Y) / 2;

                        // Add the in-progress label
                        textRenderer.AddLabel(
                            labelText,
                            new System.Drawing.Point(textX, textY),
                            System.Drawing.Color.FromArgb(150, 0, 0, 0),
                            System.Drawing.Color.Yellow);  // Use yellow for active measurement
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[SharpDXVolumeRenderer] Error updating measurement labels: {ex.Message}");
            }
        }

        private void RenderMeasurements()
        {
            try
            {
                // Skip if no measurements or no buffers initialized
                if ((measurements.Count == 0 && !isDrawingMeasurement) || lineVertexBuffer == null ||
                    lineVertexShader == null || linePixelShader == null)
                {
                    return;
                }

                // First reset shader resources to avoid state conflicts
                ResetShaderResources();

                // Update the measurement vertex buffer with current measurements
                UpdateMeasurementBuffer();

                // Set shader and input layout for line rendering
                context.InputAssembler.InputLayout = lineInputLayout;
                context.InputAssembler.PrimitiveTopology = PrimitiveTopology.LineList;

                // Use the correct stride based on our vertex structure
                context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(
                    lineVertexBuffer,
                    Utilities.SizeOf<LineVertex>(),
                    0));

                // Set shaders for line rendering
                context.VertexShader.Set(lineVertexShader);
                context.PixelShader.Set(linePixelShader);

                // Set constant buffer with world-view-projection matrix
                context.VertexShader.SetConstantBuffer(0, constantBuffer);

                // Enable blending for the lines
                context.OutputMerger.SetBlendState(alphaBlendState);

                // Calculate vertex count based on visible measurements
                int vertexCount = 0;
                foreach (var measurement in measurements)
                {
                    if (measurement.Visible)
                        vertexCount += 2;
                }

                if (isDrawingMeasurement)
                    vertexCount += 2; // Add the in-progress measurement

                // Only draw if there are actually vertices to draw
                if (vertexCount > 0)
                {
                    context.Draw(vertexCount, 0);
                }

           
                context.VertexShader.Set(volumeVertexShader);
                context.PixelShader.Set(volumePixelShader);

                // Do NOT call UpdateMeasurementLabels which uses the text renderer
                // We'll handle text differently

                // Draw the measurement labels directly on the DirectX surface
                DrawMeasurementLabelsDirectly();

                // Reset shader resources again to avoid affecting subsequent rendering
                ResetShaderResources();
            }
            catch (Exception ex)
            {
                Logger.Log($"[SharpDXVolumeRenderer] Error rendering measurements: {ex.Message}");
            }
        }

        private void DrawMeasurementLabelsDirectly()
        {
            // We'll manually draw measurement labels by creating overlay controls
            // on top of the rendering surface, rather than using the text renderer

            try
            {
                // Use the context current state without changing it
                // and leverage Windows Forms controls for text instead

                // First, make sure all existing text controls are cleared
                RemoveAllMeasurementTextControls();

                // Process each visible measurement
                foreach (var measurement in measurements)
                {
                    if (!measurement.Visible)
                        continue;

                    // Convert 3D world coordinates to screen coordinates
                    var screenStart = WorldToScreen(measurement.Start);
                    var screenEnd = WorldToScreen(measurement.End);

                    if (!screenStart.HasValue || !screenEnd.HasValue)
                        continue;

                    var startPoint = screenStart.Value;
                    var endPoint = screenEnd.Value;

                    // Calculate midpoint for the label
                    int textX = (startPoint.X + endPoint.X) / 2;
                    int textY = (startPoint.Y + endPoint.Y) / 2;

                    // Format the label text
                    string labelText = $"{measurement.Label}: {measurement.RealDistance:F2} {measurement.Unit}";

                    // Create a label control for the measurement
                    AddMeasurementTextControl(labelText,
                        new System.Drawing.Point(textX, textY),
                        measurement.Color);
                }

                // Add label for the in-progress measurement
                if (isDrawingMeasurement)
                {
                    var screenStart = WorldToScreen(measureStartPoint);
                    var screenEnd = WorldToScreen(measureEndPoint);

                    if (screenStart.HasValue && screenEnd.HasValue)
                    {
                        var startPoint = screenStart.Value;
                        var endPoint = screenEnd.Value;

                        // Calculate the distance
                        float distance = Vector3.Distance(measureStartPoint, measureEndPoint);
                        double pixelSizeInMeters = mainForm.GetPixelSize();
                        double realWorldDistance = distance * pixelSizeInMeters;

                        // Format with appropriate units
                        string unit = "m";
                        float displayDistance = (float)realWorldDistance;

                        if (realWorldDistance < 0.001 && realWorldDistance > 0)
                        {
                            unit = "µm";
                            displayDistance = (float)(realWorldDistance * 1e6);
                        }
                        else if (realWorldDistance < 1 && realWorldDistance >= 0.001)
                        {
                            unit = "mm";
                            displayDistance = (float)(realWorldDistance * 1e3);
                        }

                        string labelText = $"Distance: {displayDistance:F2} {unit} ({distance:F1} voxels)";

                        // Calculate text position (middle of the line)
                        int textX = (startPoint.X + endPoint.X) / 2;
                        int textY = (startPoint.Y + endPoint.Y) / 2;

                        // Add the in-progress label
                        AddMeasurementTextControl(labelText,
                            new System.Drawing.Point(textX, textY),
                            System.Drawing.Color.Yellow);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[SharpDXVolumeRenderer] Error drawing measurement labels: {ex.Message}");
            }
        }

        private List<Label> measurementLabels = new List<Label>();

        // Method to add a measurement text control
        private void AddMeasurementTextControl(string text, System.Drawing.Point location, System.Drawing.Color color)
        {
            try
            {
                // Create a label control
                Label label = new Label();
                label.Text = text;
                label.AutoSize = true;
                label.BackColor = System.Drawing.Color.FromArgb(120, 0, 0, 0); // Semi-transparent background
                label.ForeColor = color;
                label.Font = new System.Drawing.Font("Arial", 8, System.Drawing.FontStyle.Bold);

                // Adjust location to center the text
                System.Drawing.Size textSize = TextRenderer.MeasureText(text, label.Font);
                label.Location = new System.Drawing.Point(
                    location.X - textSize.Width / 2,
                    location.Y - textSize.Height / 2);

                // Add to the render panel
                renderPanel.Controls.Add(label);
                label.BringToFront();

                // Store in our collection for later cleanup
                measurementLabels.Add(label);
            }
            catch (Exception ex)
            {
                Logger.Log($"[SharpDXVolumeRenderer] Error adding measurement label: {ex.Message}");
            }
        }

        // Method to remove all measurement text controls
        public void RemoveAllMeasurementTextControls()
        {
            try
            {
                foreach (var label in measurementLabels)
                {
                    try
                    {
                        // Remove from panel and dispose
                        if (label != null && !label.IsDisposed)
                        {
                            renderPanel.Controls.Remove(label);
                            label.Dispose();
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[SharpDXVolumeRenderer] Error removing measurement label: {ex.Message}");
                    }
                }

                // Clear the collection
                measurementLabels.Clear();
            }
            catch (Exception ex)
            {
                Logger.Log($"[SharpDXVolumeRenderer] Error removing measurement labels: {ex.Message}");
            }
        }

        private void UpdateMeasurementVertexBuffer()
        {
            try
            {
                // Calculate total required vertex count
                int totalMeasurements = measurements.Count + (isDrawingMeasurement ? 1 : 0);
                int vertexCount = totalMeasurements * 2; // 2 vertices per line

                if (vertexCount == 0)
                    return;

                // Create a list to hold properly formatted vertices
                List<LineVertex> vertices = new List<LineVertex>(vertexCount);

                // Add all visible measurements
                foreach (var measurement in measurements)
                {
                    if (!measurement.Visible)
                        continue;

                    // Convert world coordinates to view space
                    Vector3 start = measurement.Start;
                    Vector3 end = measurement.End;

                    // Get color (use white if not specified)
                    Vector4 color = new Vector4(
                        measurement.Color.R / 255.0f,
                        measurement.Color.G / 255.0f,
                        measurement.Color.B / 255.0f,
                        1.0f);

                    // Add start and end vertices
                    vertices.Add(new LineVertex(start, color));
                    vertices.Add(new LineVertex(end, color));
                }

                // Add the in-progress measurement if drawing
                if (isDrawingMeasurement)
                {
                    // Use yellow for active measurement
                    Vector4 activeColor = new Vector4(1.0f, 1.0f, 0.0f, 1.0f);
                    vertices.Add(new LineVertex(measureStartPoint, activeColor));
                    vertices.Add(new LineVertex(measureEndPoint, activeColor));
                }

                // Skip if no vertices to render
                if (vertices.Count == 0)
                    return;

                // Determine buffer size needed
                int stride = Utilities.SizeOf<LineVertex>();
                int dataSize = stride * vertices.Count;

                // Recreate buffer if needed
                if (lineVertexBuffer == null || lineVertexBuffer.Description.SizeInBytes < dataSize)
                {
                    if (lineVertexBuffer != null)
                        lineVertexBuffer.Dispose();

                    BufferDescription bufDesc = new BufferDescription(
                        Math.Max(dataSize, 1024), // Minimum 1KB buffer
                        ResourceUsage.Dynamic,
                        BindFlags.VertexBuffer,
                        CpuAccessFlags.Write,
                        ResourceOptionFlags.None,
                        stride);

                    lineVertexBuffer = new Buffer(device, bufDesc);
                }

                // Map buffer for writing
                DataStream dataStream;
                context.MapSubresource(
                    lineVertexBuffer,
                    0,
                    MapMode.WriteDiscard,
                    SharpDX.Direct3D11.MapFlags.None,
                    out dataStream);

                // Write all vertices in one go
                dataStream.WriteRange(vertices.ToArray());

                // Unmap the buffer
                context.UnmapSubresource(lineVertexBuffer, 0);
                dataStream.Dispose();
            }
            catch (Exception ex)
            {
                Logger.Log($"[SharpDXVolumeRenderer] Error updating measurement buffer: {ex.Message}");
            }
        }

        #endregion DXMeasurements

        #region shader reset

        private void ResetShaderResources()
        {
            try
            {
                // Clear any shader resources to avoid driver state confusion
                ShaderResourceView[] nullResources = new ShaderResourceView[6];
                context.PixelShader.SetShaderResources(0, 6, nullResources);
            }
            catch (Exception ex)
            {
                Logger.Log("[SharpDXVolumeRenderer] Error resetting shader resources: " + ex.Message);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LineVertex
        {
            public Vector3 Position;
            public Vector4 Color;

            public LineVertex(Vector3 position, Vector4 color)
            {
                Position = position;
                Color = color;
            }
        }

        #endregion shader reset

        #region Streaming Rendering

        private void InitializeStreamingRenderer()
        {
            if (isInitializingStreaming || device == null)
                return;

            try
            {
                isInitializingStreaming = true;
                Logger.Log("[SharpDXVolumeRenderer] Initializing streaming renderer");

                // Create a series of progressively lower-resolution versions of the volume
                // These will be used during camera movement and then progressively refined
                CreateStreamingLODTextures();

                // Important: Force render with new resources
                NeedsRender = true;

                isInitializingStreaming = false;
            }
            catch (Exception ex)
            {
                Logger.Log($"[SharpDXVolumeRenderer] Error initializing streaming renderer: {ex.Message}");
                useStreamingRenderer = false;
                isInitializingStreaming = false;
            }
        }

        private void CreateStreamingLODTextures()
        {
            // Clean up any existing textures first
            for (int i = 0; i < lodTextures.Length; i++)
            {
                Utilities.Dispose(ref lodSRVs[i]);
                Utilities.Dispose(ref lodTextures[i]);
            }

            if (mainForm.volumeData == null)
            {
                Logger.Log("[SharpDXVolumeRenderer] No volume data available for streaming LODs");
                return;
            }

            ChunkedVolume volume = (ChunkedVolume)mainForm.volumeData;
            bool anyLodCreated = false;

            // Create a series of downsampled textures at different resolutions
            for (int lodLevel = 0; lodLevel < lodTextures.Length; lodLevel++)
            {
                try
                {
                    // Level 0 is highest resolution, each level reduces by 2x
                    int downsampleFactor = (int)Math.Pow(2, lodLevel);

                    // Calculate dimensions for this LOD level
                    int width = Math.Max(1, volW / downsampleFactor);
                    int height = Math.Max(1, volH / downsampleFactor);
                    int depth = Math.Max(1, volD / downsampleFactor);

                    Logger.Log($"[SharpDXVolumeRenderer] Creating streaming LOD level {lodLevel}: {width}x{height}x{depth}");

                    // Create the texture
                    Texture3DDescription desc = new Texture3DDescription
                    {
                        Width = width,
                        Height = height,
                        Depth = depth,
                        MipLevels = 1,
                        Format = Format.R8_UNorm,
                        Usage = ResourceUsage.Default,
                        BindFlags = BindFlags.ShaderResource,
                        CpuAccessFlags = CpuAccessFlags.None,
                        OptionFlags = ResourceOptionFlags.None
                    };

                    lodTextures[lodLevel] = new Texture3D(device, desc);

                    // Create the data array for this LOD level
                    byte[] lodData = new byte[width * height * depth];

                    // Use a properly indexed approach to ensure correct memory layout
                    for (int z = 0; z < depth; z++)
                    {
                        for (int y = 0; y < height; y++)
                        {
                            for (int x = 0; x < width; x++)
                            {
                                // Calculate source coordinates in the original volume
                                int srcX = Math.Min(x * downsampleFactor, volW - 1);
                                int srcY = Math.Min(y * downsampleFactor, volH - 1);
                                int srcZ = Math.Min(z * downsampleFactor, volD - 1);

                                // Calculate linear index in the lodData array
                                int idx = (z * width * height) + (y * width) + x;

                                // Sample from the original volume with bounds checking
                                if (srcX < volW && srcY < volH && srcZ < volD)
                                {
                                    byte value = volume[srcX, srcY, srcZ];
                                    lodData[idx] = value;
                                }
                            }
                        }
                    }

                    // Upload the data to the texture
                    context.UpdateSubresource(lodData, lodTextures[lodLevel], 0);

                    // Create a shader resource view
                    ShaderResourceViewDescription srvDesc = new ShaderResourceViewDescription
                    {
                        Format = Format.R8_UNorm,
                        Dimension = ShaderResourceViewDimension.Texture3D,
                        Texture3D = new ShaderResourceViewDescription.Texture3DResource
                        {
                            MipLevels = 1,
                            MostDetailedMip = 0
                        }
                    };

                    lodSRVs[lodLevel] = new ShaderResourceView(device, lodTextures[lodLevel], srvDesc);
                    anyLodCreated = true;

                    // IMPORTANT: For LOD level 0 (highest resolution), also temporarily use as main texture
                    // Note: We'll keep the original volume texture reference for when streaming is disabled
                    if (lodLevel == 0)
                    {
                        // Update the current rendering texture (we'll switch back later if needed)
                        currentStreamingLOD = 0;
                        Logger.Log("[SharpDXVolumeRenderer] Created highest-resolution LOD texture");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[SharpDXVolumeRenderer] Error creating LOD level {lodLevel}: {ex.Message}");
                }
            }

            if (!anyLodCreated)
            {
                // If we couldn't create any LOD textures, disable streaming mode
                Logger.Log("[SharpDXVolumeRenderer] Failed to create any LOD textures, disabling streaming");
                useStreamingRenderer = false;
            }
        }

        private void RenderVolumeWithStreaming()
        {
            if (isInitializingStreaming)
            {
                // Use wireframe mode temporarily during initialization
                var oldState = context.Rasterizer.State;
                context.Rasterizer.State = wireframeRasterState;

                // Setup rendering pipeline (minimal setup just for the box)
                context.InputAssembler.InputLayout = inputLayout;
                context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
                context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(cubeVertexBuffer, Utilities.SizeOf<Vector3>(), 0));
                context.InputAssembler.SetIndexBuffer(cubeIndexBuffer, Format.R32_UInt, 0);

                // Set shaders
                context.VertexShader.Set(volumeVertexShader);
                context.PixelShader.Set(volumePixelShader);

                // Set a simple constant buffer for the initialization state
                UpdateConstantBuffer(1.0f);
                context.VertexShader.SetConstantBuffer(0, constantBuffer);
                context.PixelShader.SetConstantBuffer(0, constantBuffer);

                // Draw just the bounding box
                context.DrawIndexed(cubeIndexCount, 0, 0);

                // Restore original state
                context.Rasterizer.State = oldState;
                return;
            }
            try
            {
                if (context == null || cubeVertexBuffer == null || cubeIndexBuffer == null)
                {
                    Logger.Log("[SharpDXVolumeRenderer] Cannot render volume: Required resources are null");
                    return;
                }

                // IMPORTANT: Reset any previous shader resources to avoid state confusion
                ResetShaderResources();

                // Use wireframe in debug mode
                context.Rasterizer.State = debugMode ? wireframeRasterState : solidRasterState;

                // Setup rendering pipeline
                context.InputAssembler.InputLayout = inputLayout;
                context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
                context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(cubeVertexBuffer, Utilities.SizeOf<Vector3>(), 0));
                context.InputAssembler.SetIndexBuffer(cubeIndexBuffer, Format.R32_UInt, 0);

                // Set samplers
                context.PixelShader.SetSampler(0, linearSampler);
                context.PixelShader.SetSampler(1, pointSampler);

                // Set the blend state explicitly
                context.OutputMerger.SetBlendState(alphaBlendState, new Color4(0, 0, 0, 0), 0xFFFFFFFF);

                // Select LOD level based on camera movement
                bool isMoving = isDragging || isPanning;
                if (isMoving)
                {
                    // Use a lower resolution during movement
                    currentStreamingLOD = Math.Min(2, lodTextures.Length - 1);
                }
                else
                {
                    // When static, progressively increase resolution
                    if (currentStreamingLOD > 0)
                    {
                        currentStreamingLOD--;
                    }
                }

                // Prepare resources array
                ShaderResourceView[] resources = new ShaderResourceView[6];

                // Fill the resources array with available resources
                if (labelVisibilitySRV != null) resources[0] = labelVisibilitySRV;
                if (labelOpacitySRV != null) resources[1] = labelOpacitySRV;

                
                bool hasValidTexture = false;

                // Try streaming LOD textures first, with bounds checking
                if (currentStreamingLOD >= 0 && currentStreamingLOD < lodSRVs.Length && lodSRVs[currentStreamingLOD] != null)
                {
                    resources[2] = lodSRVs[currentStreamingLOD];
                    hasValidTexture = true;
                    if (frameCount % 60 == 0)  // Log less frequently to reduce spam
                    {
                        Logger.Log($"[RenderVolumeWithStreaming] Using LOD level {currentStreamingLOD}");
                    }
                }
                // Fall back to the original volume texture if available
                else if (volumeSRV != null)
                {
                    resources[2] = volumeSRV;
                    hasValidTexture = true;
                    Logger.Log("[RenderVolumeWithStreaming] Using original volume texture as fallback");
                }

                if (!hasValidTexture)
                {
                    Logger.Log("[RenderVolumeWithStreaming] WARNING: No valid volume texture available!");

                
                    if (resources[2] == null)
                    {
                        try
                        {
                            // Create a small texture with some gradient data for visibility
                            const int tempSize = 64;
                            Texture3DDescription tempDesc = new Texture3DDescription
                            {
                                Width = tempSize,
                                Height = tempSize,
                                Depth = tempSize,
                                MipLevels = 1,
                                Format = Format.R8_UNorm,
                                Usage = ResourceUsage.Default,
                                BindFlags = BindFlags.ShaderResource,
                                CpuAccessFlags = CpuAccessFlags.None,
                                OptionFlags = ResourceOptionFlags.None
                            };

                            Texture3D tempTexture = new Texture3D(device, tempDesc);
                            byte[] tempData = new byte[tempSize * tempSize * tempSize];

                            // Fill with simple gradient pattern
                            for (int z = 0; z < tempSize; z++)
                            {
                                for (int y = 0; y < tempSize; y++)
                                {
                                    for (int x = 0; x < tempSize; x++)
                                    {
                                        int idx = (z * tempSize * tempSize) + (y * tempSize) + x;
                                        // Simple gradient pattern
                                        tempData[idx] = (byte)(((x * 255) / tempSize + (y * 255) / tempSize + (z * 255) / tempSize) / 3);
                                    }
                                }
                            }

                            context.UpdateSubresource(tempData, tempTexture, 0);

                            ShaderResourceViewDescription tempSrvDesc = new ShaderResourceViewDescription
                            {
                                Format = Format.R8_UNorm,
                                Dimension = ShaderResourceViewDimension.Texture3D,
                                Texture3D = new ShaderResourceViewDescription.Texture3DResource
                                {
                                    MipLevels = 1,
                                    MostDetailedMip = 0
                                }
                            };

                            ShaderResourceView tempSrv = new ShaderResourceView(device, tempTexture, tempSrvDesc);
                            resources[2] = tempSrv;

                            Logger.Log("[RenderVolumeWithStreaming] Created temporary texture as last resort");
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"[RenderVolumeWithStreaming] Failed to create temporary texture: {ex.Message}");
                        }
                    }
                }

                if (labelSRV != null) resources[3] = labelSRV;
                if (materialColorSRV != null) resources[4] = materialColorSRV;
                if (colorMapSRV != null) resources[5] = colorMapSRV;

                // Set all resources at once
                context.PixelShader.SetShaderResources(0, resources);

                // Set shaders
                context.VertexShader.Set(volumeVertexShader);
                context.PixelShader.Set(volumePixelShader);

                // Update constant buffer for rendering
                float currentStepSize = isMoving ? Math.Min(3.0f, stepSize * 2.0f) : stepSize;

                // For LOD, adjust step size based on level
                currentStepSize = Math.Max(0.5f, currentStepSize * (currentStreamingLOD + 1));

                UpdateConstantBuffer(currentStepSize);
                context.VertexShader.SetConstantBuffer(0, constantBuffer);
                context.PixelShader.SetConstantBuffer(0, constantBuffer);

                // Draw the cube
                context.DrawIndexed(cubeIndexCount, 0, 0);
            }
            catch (Exception ex)
            {
                Logger.Log("[SharpDXVolumeRenderer] RenderVolumeWithStreaming error: " + ex.Message);
                // For render failures, switch to wireframe mode
                debugMode = true;
            }
        }

        private void LoadLowResolutionOverview()
        {
            // Create heavily downsampled overview of the entire volume
            try
            {
                int downsampleFactor = CalculateOptimalDownsampleFactor();
                Logger.Log($"[SharpDXVolumeRenderer] Initial low-res overview using downsample factor: {downsampleFactor}");

                // Create a heavily downsampled version for the overview
                currentLodLevel = Math.Min(MAX_LOD_LEVELS, 2); // Use higher LOD initially

                // Force creation of the LOD textures if they don't exist yet
                if (lodVolumeTextures[currentLodLevel] == null)
                {
                    CreateLodTextures();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[SharpDXVolumeRenderer] Error creating low-res overview: {ex.Message}");
                // Fallback to standard rendering if streaming fails
                useStreamingRenderer = false;
            }
        }

        private int CalculateOptimalDownsampleFactor()
        {
            long volumeSizeInBytes = (long)volW * volH * volD;

            // Target 512MB-1GB for initial overview
            long targetSize = 512 * 1024 * 1024; // 512MB

            int factor = 1;
            while ((volumeSizeInBytes / (factor * factor * factor)) > targetSize && factor < 16)
            {
                factor *= 2;
            }

            return factor;
        }

        private void UpdateVisibleChunks()
        {
            if (!useStreamingRenderer)
                return;

            // Skip if camera hasn't moved significantly
            float cameraMoveThreshold = 20.0f;
            float zoomThreshold = 50.0f;

            Vector3 cameraPos = GetCameraPosition();
            bool significantCameraMove =
                Vector3.Distance(cameraPos, cameraPositionPrevious) > cameraMoveThreshold ||
                Math.Abs(cameraDistance - cameraDistancePrevious) > zoomThreshold;

            if (!significantCameraMove && visibleChunks.Count > 0)
                return;

            // Update camera position cache
            cameraPositionPrevious = cameraPos;
            cameraDistancePrevious = cameraDistance;

            // Calculate visible chunks based on camera frustum
            HashSet<Vector3> newVisibleChunks = new HashSet<Vector3>();
            int chunkSize = 64; // Size of each streaming chunk

            // Divide the volume into chunks
            int chunksX = (volW + chunkSize - 1) / chunkSize;
            int chunksY = (volH + chunkSize - 1) / chunkSize;
            int chunksZ = (volD + chunkSize - 1) / chunkSize;

            // Get the view frustum for visibility determination
            var viewFrustum = CalculateViewFrustum();

            // Check which chunks intersect with the view frustum
            for (int z = 0; z < chunksZ; z++)
            {
                for (int y = 0; y < chunksY; y++)
                {
                    for (int x = 0; x < chunksX; x++)
                    {
                        Vector3 chunkKey = new Vector3(x, y, z);

                        // Calculate chunk bounds in world space
                        Vector3 minBounds = new Vector3(
                            x * chunkSize,
                            y * chunkSize,
                            z * chunkSize);

                        Vector3 maxBounds = new Vector3(
                            Math.Min((x + 1) * chunkSize, volW),
                            Math.Min((y + 1) * chunkSize, volH),
                            Math.Min((z + 1) * chunkSize, volD));

                        // Test if this chunk is visible in the view frustum
                        if (IsChunkVisible(minBounds, maxBounds, viewFrustum))
                        {
                            newVisibleChunks.Add(chunkKey);

                            // If not already loaded or queued, add to load queue
                            if (!loadedChunks.ContainsKey(chunkKey) && !chunkLoadQueue.Contains(chunkKey))
                            {
                                chunkLoadQueue.Enqueue(chunkKey);
                            }
                        }
                    }
                }
            }

            // Update the visible chunks set
            lock (chunkLock)
            {
                visibleChunks = newVisibleChunks;

                // Check if we need to unload any chunks
                if (loadedChunks.Count > MAX_LOADED_CHUNKS)
                {
                    // Identify chunks to unload (not visible and loaded)
                    var chunksToUnload = loadedChunks.Keys
                        .Where(chunk => !visibleChunks.Contains(chunk))
                        .ToList();

                    // Unload the least recently used chunks until we're under the limit
                    int chunksToRemove = Math.Min(chunksToUnload.Count,
                                                 loadedChunks.Count - MAX_LOADED_CHUNKS);

                    for (int i = 0; i < chunksToRemove; i++)
                    {
                        UnloadChunk(chunksToUnload[i]);
                    }
                }
            }

            Logger.Log($"[SharpDXVolumeRenderer] Visible chunks: {visibleChunks.Count}, Loaded: {loadedChunks.Count}, Queued: {chunkLoadQueue.Count}");
        }

        private Vector3 GetCameraPosition()
        {
            // Calculate current camera position
            float cosPitch = (float)Math.Cos(cameraPitch);
            float sinPitch = (float)Math.Sin(cameraPitch);
            float cosYaw = (float)Math.Cos(cameraYaw);
            float sinYaw = (float)Math.Sin(cameraYaw);

            Vector3 volumeCenter = new Vector3(volW / 2.0f, volH / 2.0f, volD / 2.0f);
            Vector3 cameraDirection = new Vector3(
                cosPitch * cosYaw,
                sinPitch,
                cosPitch * sinYaw);

            return volumeCenter - (cameraDirection * cameraDistance) + panOffset;
        }

        private ViewFrustum CalculateViewFrustum()
        {
            // Calculate the camera's view frustum for chunk visibility tests
            float aspectRatio = (float)renderPanel.ClientSize.Width / Math.Max(1, renderPanel.ClientSize.Height);
            float fov = (float)Math.PI / 4.0f;  // 45 degrees field of view

            Vector3 cameraPos = GetCameraPosition();
            float cosPitch = (float)Math.Cos(cameraPitch);
            float sinPitch = (float)Math.Sin(cameraPitch);
            float cosYaw = (float)Math.Cos(cameraYaw);
            float sinYaw = (float)Math.Sin(cameraYaw);

            Vector3 volumeCenter = new Vector3(volW / 2.0f, volH / 2.0f, volD / 2.0f);
            Vector3 forward = Vector3.Normalize(volumeCenter + panOffset - cameraPos);
            Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
            Vector3 up = Vector3.Cross(right, forward);

            return new ViewFrustum
            {
                Position = cameraPos,
                Forward = forward,
                Up = up,
                Right = right,
                NearDist = 1.0f,
                FarDist = cameraDistance * 10.0f,
                FOV = fov,
                AspectRatio = aspectRatio
            };
        }

        private bool IsChunkVisible(Vector3 minBounds, Vector3 maxBounds, ViewFrustum frustum)
        {
            

            // Check if the volume center is roughly in the view direction
            Vector3 chunkCenter = (minBounds + maxBounds) * 0.5f;
            Vector3 toCenterDir = Vector3.Normalize(chunkCenter - frustum.Position);
            float dotProduct = Vector3.Dot(toCenterDir, frustum.Forward);

            // If the chunk is behind the camera, it's not visible
            if (dotProduct < 0.2f) // Allow a wide angle to avoid popping
                return false;

            // Simple distance-based prioritization - chunks closer to camera are more visible
            float distance = Vector3.Distance(frustum.Position, chunkCenter);

            // Prioritize chunks closer to the camera, based on zoom level
            float priorityDistance = cameraDistance * 2.0f;

            return distance < priorityDistance;
        }

        private void LoadNextChunkFromQueue()
        {
            if (chunkLoadQueue.Count == 0 || !useStreamingRenderer)
                return;

            try
            {
                Vector3 chunkToLoad;

                lock (chunkLock)
                {
                    if (chunkLoadQueue.Count == 0)
                        return;

                    chunkToLoad = chunkLoadQueue.Dequeue();

                    // Skip if already loaded or no longer visible
                    if (loadedChunks.ContainsKey(chunkToLoad) || !visibleChunks.Contains(chunkToLoad))
                        return;

                    // Check if we need to make room for this chunk
                    if (loadedChunks.Count >= MAX_LOADED_CHUNKS)
                    {
                        // Find a chunk that's loaded but not visible
                        var chunkToUnload = loadedChunks.Keys
                            .FirstOrDefault(chunk => !visibleChunks.Contains(chunk));

                        if (chunkToUnload != default)
                        {
                            UnloadChunk(chunkToUnload);
                        }
                        else
                        {
                            // If all loaded chunks are visible, skip loading this one for now
                            // Put it back in the queue for later
                            chunkLoadQueue.Enqueue(chunkToLoad);
                            return;
                        }
                    }
                }

                // Load the chunk
                LoadChunk(chunkToLoad);
                NeedsRender = true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[SharpDXVolumeRenderer] Error in chunk loading: {ex.Message}");
            }
        }

        private void LoadChunk(Vector3 chunkKey)
        {
            int chunkSize = 64;
            int x = (int)chunkKey.X;
            int y = (int)chunkKey.Y;
            int z = (int)chunkKey.Z;

            try
            {
                if (mainForm.volumeData == null)
                    return;

                // Calculate chunk bounds
                int startX = x * chunkSize;
                int startY = y * chunkSize;
                int startZ = z * chunkSize;
                int endX = Math.Min(startX + chunkSize, volW);
                int endY = Math.Min(startY + chunkSize, volH);
                int endZ = Math.Min(startZ + chunkSize, volD);
                int width = endX - startX;
                int height = endY - startY;
                int depth = endZ - startZ;

                // Create texture description
                Texture3DDescription desc = new Texture3DDescription
                {
                    Width = width,
                    Height = height,
                    Depth = depth,
                    MipLevels = 1,
                    Format = Format.R8_UNorm,
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource,
                    CpuAccessFlags = CpuAccessFlags.None,
                    OptionFlags = ResourceOptionFlags.None
                };

                Texture3D texture = new Texture3D(device, desc);

                // Extract the data for this chunk
                byte[] chunkData = ExtractChunkData((ChunkedVolume)mainForm.volumeData, startX, startY, startZ, width, height, depth);

                // Upload the data
                context.UpdateSubresource(chunkData, texture, 0);

                // Create shader resource view
                ShaderResourceViewDescription srvDesc = new ShaderResourceViewDescription
                {
                    Format = Format.R8_UNorm,
                    Dimension = ShaderResourceViewDimension.Texture3D,
                    Texture3D = new ShaderResourceViewDescription.Texture3DResource
                    {
                        MipLevels = 1,
                        MostDetailedMip = 0
                    }
                };

                ShaderResourceView srv = new ShaderResourceView(device, texture, srvDesc);

                // Store in dictionaries
                lock (chunkLock)
                {
                    loadedChunks[chunkKey] = texture;
                    loadedChunkSRVs[chunkKey] = srv;
                }

                Logger.Log($"[SharpDXVolumeRenderer] Loaded chunk {chunkKey} ({width}x{height}x{depth})");
            }
            catch (Exception ex)
            {
                Logger.Log($"[SharpDXVolumeRenderer] Error loading chunk {chunkKey}: {ex.Message}");
            }
        }

        private byte[] ExtractChunkData(ChunkedVolume volume, int startX, int startY, int startZ, int width, int height, int depth)
        {
            try
            {
                byte[] chunkData = new byte[width * height * depth];

                // Extract data using explicit 3D to 1D index calculation to ensure
                // the layout matches what DirectX expects for a Texture3D
                for (int z = 0; z < depth; z++)
                {
                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            int vx = startX + x;
                            int vy = startY + y;
                            int vz = startZ + z;

                            // Bounds check
                            vx = Math.Min(vx, volW - 1);
                            vy = Math.Min(vy, volH - 1);
                            vz = Math.Min(vz, volD - 1);

                            // This index calculation matches DirectX Texture3D layout
                            int destIndex = z * (width * height) + y * width + x;

                            // Get the voxel value from the chunked volume
                            byte value = volume[vx, vy, vz];
                            chunkData[destIndex] = value;
                        }
                    }
                }

                return chunkData;
            }
            catch (Exception ex)
            {
                Logger.Log($"[ExtractChunkData] Error: {ex.Message}");
                throw;
            }
        }

        private void UnloadChunk(Vector3 chunkKey)
        {
            try
            {
                lock (chunkLock)
                {
                    if (loadedChunks.TryGetValue(chunkKey, out Texture3D texture))
                    {
                        if (loadedChunkSRVs.TryGetValue(chunkKey, out ShaderResourceView srv))
                        {
                            srv.Dispose();
                            loadedChunkSRVs.Remove(chunkKey);
                        }

                        texture.Dispose();
                        loadedChunks.Remove(chunkKey);

                        Logger.Log($"[SharpDXVolumeRenderer] Unloaded chunk {chunkKey}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[SharpDXVolumeRenderer] Error unloading chunk {chunkKey}: {ex.Message}");
            }
        }

        // ViewFrustum struct for visibility calculation
        private struct ViewFrustum
        {
            public Vector3 Position;
            public Vector3 Forward;
            public Vector3 Up;
            public Vector3 Right;
            public float NearDist;
            public float FarDist;
            public float FOV;
            public float AspectRatio;
        }

        private void RenderVolumeStreaming()
        {
            // Update which chunks are visible based on the current view
            UpdateVisibleChunks();

            // If no chunks are visible or loaded yet, render using LOD overview
            if (visibleChunks.Count == 0 || loadedChunks.Count == 0)
            {
                // Fall back to LOD-based rendering
                RenderVolume();
                return;
            }

            try
            {
                // Reset any previous shader resources
                ResetShaderResources();

                // Set up standard rendering states
                context.Rasterizer.State = debugMode ? wireframeRasterState : solidRasterState;
                context.InputAssembler.InputLayout = inputLayout;
                context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
                context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(cubeVertexBuffer, Utilities.SizeOf<Vector3>(), 0));
                context.InputAssembler.SetIndexBuffer(cubeIndexBuffer, Format.R32_UInt, 0);

                // Set samplers
                context.PixelShader.SetSampler(0, linearSampler);
                context.PixelShader.SetSampler(1, pointSampler);

                // Set blend state
                context.OutputMerger.SetBlendState(alphaBlendState, new Color4(0, 0, 0, 0), 0xFFFFFFFF);

                // Prepare resources array
                ShaderResourceView[] resources = new ShaderResourceView[6];

                // Fill the resources array with available resources
                if (labelVisibilitySRV != null) resources[0] = labelVisibilitySRV;
                if (labelOpacitySRV != null) resources[1] = labelOpacitySRV;

                // Use LOD overview or streaming chunks
                bool isMoving = isDragging || isPanning;

                if (isMoving || loadedChunks.Count < visibleChunks.Count / 2)
                {
                    // During movement or if too few chunks are loaded, use the LOD overview
                    if (lodVolumeSRVs[currentLodLevel] != null)
                    {
                        resources[2] = lodVolumeSRVs[currentLodLevel];
                    }
                    else if (volumeSRV != null)
                    {
                        resources[2] = volumeSRV;
                    }
                }
                else
                {
                    // Use streaming chunks
                    // Note: This is oversimplified - ideally we would need to modify the shader
                    // to handle multiple chunk textures together

                    // For now, we'll just use one of the loaded chunks
                    if (loadedChunks.Count > 0)
                    {
                        // Use a visible chunk
                        var visibleLoadedChunk = loadedChunks.Keys.FirstOrDefault(chunk => visibleChunks.Contains(chunk));

                        if (visibleLoadedChunk != default && loadedChunkSRVs.TryGetValue(visibleLoadedChunk, out ShaderResourceView srv))
                        {
                            resources[2] = srv;
                        }
                        else if (volumeSRV != null)
                        {
                            resources[2] = volumeSRV;
                        }
                    }
                    else if (volumeSRV != null)
                    {
                        resources[2] = volumeSRV;
                    }
                }

                if (labelSRV != null) resources[3] = labelSRV;
                if (materialColorSRV != null) resources[4] = materialColorSRV;
                if (colorMapSRV != null) resources[5] = colorMapSRV;

                // Set all resources at once
                context.PixelShader.SetShaderResources(0, resources);

                // Set shaders
                context.VertexShader.Set(volumeVertexShader);
                context.PixelShader.Set(volumePixelShader);

                // Update constant buffer for rendering
                float currentStepSize = isMoving ? Math.Min(3.0f, stepSize * 2.0f) : stepSize;

                // For LOD, adjust step size based on level
                if (useLodSystem && currentLodLevel > 0 && isMoving)
                {
                    currentStepSize = lodStepSizes[currentLodLevel];
                }

                UpdateConstantBuffer(currentStepSize);
                context.VertexShader.SetConstantBuffer(0, constantBuffer);
                context.PixelShader.SetConstantBuffer(0, constantBuffer);

                // Draw the cube
                context.DrawIndexed(cubeIndexCount, 0, 0);
            }
            catch (Exception ex)
            {
                Logger.Log("[SharpDXVolumeRenderer] RenderVolumeStreaming error: " + ex.Message);
                // For render failures, switch to wireframe mode
                debugMode = true;
            }
        }

        public void ClearMeasurementLabels()
        {
            // Clear any existing measurement label controls
            RemoveAllMeasurementTextControls();
        }

        private void DisposeStreamingResources()
        {
            try
            {
                Logger.Log("[SharpDXVolumeRenderer] Disposing streaming renderer resources");

                // Clean up the streaming LOD textures
                for (int i = 0; i < lodTextures.Length; i++)
                {
                    try
                    {
                        if (lodSRVs[i] != null)
                        {
                            lodSRVs[i].Dispose();
                            lodSRVs[i] = null;
                        }

                        if (lodTextures[i] != null)
                        {
                            lodTextures[i].Dispose();
                            lodTextures[i] = null;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[SharpDXVolumeRenderer] Error disposing LOD texture {i}: {ex.Message}");
                    }
                }

                // Clean up loaded chunks if any
                lock (chunkLock)
                {
                    try
                    {
                        // Use the existing UnloadChunk method to properly dispose each chunk
                        List<Vector3> chunkKeys = loadedChunks.Keys.ToList();
                        foreach (Vector3 key in chunkKeys)
                        {
                            UnloadChunk(key);
                        }

                        // Clear all collections
                        loadedChunks.Clear();
                        loadedChunkSRVs.Clear();
                        chunkLoadQueue.Clear();
                        visibleChunks.Clear();
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[SharpDXVolumeRenderer] Error cleaning up chunks: {ex.Message}");
                    }
                }

                Logger.Log("[SharpDXVolumeRenderer] Streaming resources disposed");
            }
            catch (Exception ex)
            {
                Logger.Log($"[SharpDXVolumeRenderer] Error disposing streaming resources: {ex.Message}");
            }
        }

        // Fixed code with renamed progress variable to avoid naming conflict
        private void InitializeStreamingRendererAsync()
        {
            // Cancel any existing initialization
            if (initStreamingCts != null && !initStreamingCts.IsCancellationRequested)
            {
                initStreamingCts.Cancel();
            }

            initStreamingCts = new CancellationTokenSource();
            var token = initStreamingCts.Token;

            // Create a progress form to show the user something is happening
            if (streamingProgressForm != null && !streamingProgressForm.IsDisposed)
            {
                streamingProgressForm.Close();
            }

            // Create and show the progress form on the UI thread
            mainForm.Invoke((Action)(() =>
            {
                streamingProgressForm = new ProgressForm("Initializing Streaming Renderer...");
                streamingProgressForm.FormClosed += (s, e) =>
                {
                    // If the user closes the form, cancel the initialization
                    if (!initStreamingCts.IsCancellationRequested)
                    {
                        initStreamingCts.Cancel();
                        useStreamingRenderer = false;
                        NeedsRender = true;
                    }
                };
                streamingProgressForm.Show(mainForm);
                streamingProgressForm.UpdateProgress(0, 100);
            }));

            // Set a flag to indicate initialization is in progress
            isInitializingStreaming = true;

            // Start the async task
            streamingInitTask = Task.Run(() =>
            {
                try
                {
                    Logger.Log("[InitializeStreamingRendererAsync] Starting initialization on background thread");

                    // Set a timer to update the progress UI periodically
                    int progressPercent = 0;
                    var timer = new System.Threading.Timer(state =>
                    {
                        if (streamingProgressForm != null && !streamingProgressForm.IsDisposed)
                        {
                            progressPercent = (progressPercent + 3) % 100; // Simple progress simulation
                            streamingProgressForm.SafeUpdateProgress(progressPercent, 100, "Creating LOD textures...");
                        }
                    }, null, 0, 300);

                    try
                    {
                        // Create a series of progressively lower-resolution versions of the volume
                        // These will be used during camera movement and then progressively refined
                        CreateStreamingLODTexturesAsync(token, progressValue =>
                        {
                            if (streamingProgressForm != null && !streamingProgressForm.IsDisposed)
                            {
                                streamingProgressForm.SafeUpdateProgress(progressValue, 100);
                            }
                        });

                        token.ThrowIfCancellationRequested();

                        // Important: Force render with new resources
                        NeedsRender = true;
                    }
                    finally
                    {
                        // Clean up the timer
                        timer.Dispose();
                    }

                    // Update progress to 100% when done
                    if (streamingProgressForm != null && !streamingProgressForm.IsDisposed)
                    {
                        streamingProgressForm.SafeUpdateProgress(100, 100, "Initialization complete!");

                        // Close the form after a short delay
                        Task.Delay(1000).ContinueWith(_ =>
                        {
                            mainForm.Invoke((Action)(() =>
                            {
                                if (streamingProgressForm != null && !streamingProgressForm.IsDisposed)
                                {
                                    streamingProgressForm.Close();
                                    streamingProgressForm = null;
                                }
                            }));
                        });
                    }

                    Logger.Log("[InitializeStreamingRendererAsync] Streaming renderer initialized successfully");
                }
                catch (OperationCanceledException)
                {
                    Logger.Log("[InitializeStreamingRendererAsync] Initialization canceled");
                    // If canceled, make sure streaming mode is disabled
                    useStreamingRenderer = false;
                    NeedsRender = true;
                }
                catch (Exception ex)
                {
                    Logger.Log($"[InitializeStreamingRendererAsync] Error initializing streaming renderer: {ex.Message}");

                    // Show error message to user
                    mainForm.Invoke((Action)(() =>
                    {
                        MessageBox.Show(mainForm,
                            $"Failed to initialize streaming renderer: {ex.Message}\n\nFalling back to standard renderer.",
                            "Initialization Error",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                    }));

                    // Disable streaming mode on error
                    useStreamingRenderer = false;
                    NeedsRender = true;
                }
                finally
                {
                    isInitializingStreaming = false;

                    // Close progress form if still open
                    mainForm.Invoke((Action)(() =>
                    {
                        if (streamingProgressForm != null && !streamingProgressForm.IsDisposed)
                        {
                            streamingProgressForm.Close();
                            streamingProgressForm = null;
                        }
                    }));
                }
            }, token);
        }

        private void CreateStreamingLODTexturesAsync(CancellationToken token, Action<int> progressCallback)
        {
            // Clean up any existing textures first
            for (int i = 0; i < lodTextures.Length; i++)
            {
                Utilities.Dispose(ref lodSRVs[i]);
                Utilities.Dispose(ref lodTextures[i]);
            }

            if (mainForm.volumeData == null)
            {
                Logger.Log("[CreateStreamingLODTexturesAsync] No volume data available for streaming LODs");
                throw new InvalidOperationException("No volume data available to create streaming LODs");
            }

            ChunkedVolume volume = (ChunkedVolume)mainForm.volumeData;
            bool anyLodCreated = false;

            // Report initial progress
            progressCallback(5);

            // Create a series of downsampled textures at different resolutions
            for (int lodLevel = 0; lodLevel < lodTextures.Length; lodLevel++)
            {
                // Check for cancellation
                token.ThrowIfCancellationRequested();

                // Update progress
                progressCallback(5 + (lodLevel * 90 / lodTextures.Length));

                try
                {
                    // Level 0 is highest resolution, each level reduces by 2x
                    int downsampleFactor = (int)Math.Pow(2, lodLevel);

                    // Calculate dimensions for this LOD level
                    int width = Math.Max(1, volW / downsampleFactor);
                    int height = Math.Max(1, volH / downsampleFactor);
                    int depth = Math.Max(1, volD / downsampleFactor);

                    Logger.Log($"[CreateStreamingLODTexturesAsync] Creating LOD level {lodLevel}: {width}x{height}x{depth}");

                    // Create the texture
                    Texture3DDescription desc = new Texture3DDescription
                    {
                        Width = width,
                        Height = height,
                        Depth = depth,
                        MipLevels = 1,
                        Format = Format.R8_UNorm,
                        Usage = ResourceUsage.Default,
                        BindFlags = BindFlags.ShaderResource,
                        CpuAccessFlags = CpuAccessFlags.None,
                        OptionFlags = ResourceOptionFlags.None
                    };

                    lodTextures[lodLevel] = new Texture3D(device, desc);

                    // Create the data array for this LOD level
                    byte[] lodData = new byte[width * height * depth];

                    // Use parallel processing for faster downsampling
                    Parallel.For(0, depth, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Environment.ProcessorCount }, z =>
                    {
                        // Check cancellation occasionally but not on every iteration
                        if (z % 10 == 0) token.ThrowIfCancellationRequested();

                        for (int y = 0; y < height; y++)
                        {
                            for (int x = 0; x < width; x++)
                            {
                                // Calculate source coordinates in the original volume
                                int srcX = Math.Min(x * downsampleFactor, volW - 1);
                                int srcY = Math.Min(y * downsampleFactor, volH - 1);
                                int srcZ = Math.Min(z * downsampleFactor, volD - 1);

                                // Calculate linear index in the lodData array
                                int idx = (z * width * height) + (y * width) + x;

                                // Sample from the original volume with bounds checking
                                if (srcX < volW && srcY < volH && srcZ < volD)
                                {
                                    byte value = volume[srcX, srcY, srcZ];
                                    lodData[idx] = value;
                                }
                            }
                        }
                    });

                    // Check for cancellation again before resource-intensive GPU operations
                    token.ThrowIfCancellationRequested();

                    // Upload the data to the texture
                    context.UpdateSubresource(lodData, lodTextures[lodLevel], 0);

                    // Create a shader resource view
                    ShaderResourceViewDescription srvDesc = new ShaderResourceViewDescription
                    {
                        Format = Format.R8_UNorm,
                        Dimension = ShaderResourceViewDimension.Texture3D,
                        Texture3D = new ShaderResourceViewDescription.Texture3DResource
                        {
                            MipLevels = 1,
                            MostDetailedMip = 0
                        }
                    };

                    lodSRVs[lodLevel] = new ShaderResourceView(device, lodTextures[lodLevel], srvDesc);
                    anyLodCreated = true;

                    // For LOD level 0 (highest resolution), also use as main texture for now
                    if (lodLevel == 0)
                    {
                        currentStreamingLOD = 0;
                        Logger.Log("[CreateStreamingLODTexturesAsync] Created highest-resolution LOD texture");
                    }
                }
                catch (OperationCanceledException)
                {
                    // Rethrow cancellation exception to be handled by the caller
                    throw;
                }
                catch (Exception ex)
                {
                    Logger.Log($"[CreateStreamingLODTexturesAsync] Error creating LOD level {lodLevel}: {ex.Message}");
                    // Continue with next LOD level
                }
            }

            // Final progress update
            progressCallback(100);

            if (!anyLodCreated)
            {
                // If we couldn't create any LOD textures, disable streaming mode
                Logger.Log("[CreateStreamingLODTexturesAsync] Failed to create any LOD textures");
                throw new InvalidOperationException("Failed to create any LOD textures for streaming");
            }
        }

        private void HandleRenderException(SharpDXException ex)
        {
            try
            {
                if (ex.ResultCode.Code == SharpDX.DXGI.ResultCode.DeviceRemoved.Result.Code)
                {
                    var reason = device.DeviceRemovedReason;
                    Logger.Log($"[SharpDXVolumeRenderer] Device Removed Error: {reason}");
                    RecreateDevice();
                }
                else if (ex.ResultCode.Code == SharpDX.DXGI.ResultCode.DeviceReset.Result.Code)
                {
                    Logger.Log("[SharpDXVolumeRenderer] Device Reset detected, recreating device");
                    RecreateDevice();
                }
                else
                {
                    Logger.Log("[SharpDXVolumeRenderer] Present error: " + ex.Message);
                }
            }
            catch (Exception recEx)
            {
                Logger.Log($"[SharpDXVolumeRenderer] Failed to recover from device error: {recEx.Message}");
                // We tried our best to recover, but failed. Let the render loop continue anyway.
            }
        }

        #endregion Streaming Rendering
    }
}