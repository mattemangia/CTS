using System;
using System.Windows.Forms;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Linq;

using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;
using Format = SharpDX.DXGI.Format;

namespace CTSegmenter
{
    public class SharpDXVolumeRenderer : IDisposable
    {
        #region Fields
        private bool debugMode = false;
        private MainForm mainForm;
        private Panel renderPanel;

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

        // Frame counter for logging
        private int frameCount = 0;

        private PictureBox scaleBarPictureBox;

        // LOD system for large datasets
        private bool useLodSystem = true;
        private int currentLodLevel = 0;
        private const int MAX_LOD_LEVELS = 3;
        private float[] lodStepSizes = new float[] { 0.5f, 1.0f, 2.0f, 4.0f }; // Different step sizes for each LOD level
        private Texture3D[] lodVolumeTextures = new Texture3D[MAX_LOD_LEVELS + 1];
        private ShaderResourceView[] lodVolumeSRVs = new ShaderResourceView[MAX_LOD_LEVELS + 1];

        // Constant buffer structure - matches shader layout exactly
        [StructLayout(LayoutKind.Sequential)]
        private struct ConstantBufferData
        {
            public Matrix WorldViewProj;
            public Matrix InvViewMatrix;
            public Vector4 Thresholds;  // x=min, y=max, z=stepSize, w=showGrayscale
            public Vector4 Dimensions;  // xyz=volume dimensions, w=unused
            public Vector4 SliceCoords; // xyz=slice positions, w=showSlices
            public Vector4 CameraPosition; // Camera position for ray origin calculation
            public Vector4 ColorMapParams; // x=colorMapIndex, y=slice border thickness, z,w=unused
            public Vector4 CutPlaneX; // x=enabled, y=direction, z=position, w=unused
            public Vector4 CutPlaneY; // x=enabled, y=direction, z=position, w=unused
            public Vector4 CutPlaneZ; // x=enabled, y=direction, z=position, w=unused
        }
        #endregion

        #region Properties
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

        public bool ShowOrthoslices
        {
            get { return showSlices; }
            set
            {
                showSlices = value;
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
        #endregion

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
                CreateVolumeTextures();
                CreateLabelTextures();
                CreateMaterialColorTexture();
                CreateColorMapTexture();
                CreateLodTextures();
                Vector3 volumeCenter = new Vector3(volW / 2.0f, volH / 2.0f, volD / 2.0f);
                cameraYaw = 0.8f; // Approximately 45 degrees
                cameraPitch = 0.6f; // Slightly elevated view
                cameraDistance = Math.Max(volW, Math.Max(volH, volD)) * 2.0f;
                panOffset = Vector3.Zero;
                NeedsRender = true;

               

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
            // Load the updated shader code from the artifact
            return @"
// Volume rendering shader with support for:
// - Grayscale volume visualization with multiple color maps
// - Material/label visualization with colors
// - Orthogonal slicing planes with colored borders
// - Thresholding
// - Dataset cutting along each axis

// Constant buffer with rendering parameters
cbuffer ConstantBuffer : register(b0)
{
    matrix worldViewProj;        // World-view-projection matrix
    matrix invViewMatrix;        // Inverse view matrix for ray calculation
    float4 thresholds;           // x=min, y=max, z=stepSize, w=showGrayscale
    float4 dimensions;           // xyz=volume dimensions, w=unused
    float4 sliceCoords;          // xyz=slice positions normalized (0-1), w=showSlices
    float4 cameraPosition;       // Camera position in world space
    float4 colorMapIndex;        // x=colorMapIndex, y=slice border thickness, z,w=unused
    float4 cutPlaneX;            // x=enabled, y=direction(1=forward,-1=backward), z=position, w=unused
    float4 cutPlaneY;            // x=enabled, y=direction(1=forward,-1=backward), z=position, w=unused
    float4 cutPlaneZ;            // x=enabled, y=direction(1=forward,-1=backward), z=position, w=unused
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

// Helper function for slice planes
bool IsOnSlicePlane(float3 pos, float3 slicePos, float epsilon, out int sliceType)
{
    // Check if the position is on any of the three slice planes
    bool onXSlice = abs(pos.x - slicePos.x * dimensions.x) < epsilon;
    bool onYSlice = abs(pos.y - slicePos.y * dimensions.y) < epsilon;
    bool onZSlice = abs(pos.z - slicePos.z * dimensions.z) < epsilon;
    
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
    
    // Whether to show orthogonal slices
    bool showSlices = sliceCoords.w > 0.5;
    
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
        
        // Handle slice planes with higher priority
        int sliceType = 0;
        if (showSlices && IsOnSlicePlane(pos, slicePos, stepSize * 1.5, sliceType))
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
            try
            {
                // Load grayscale volume data
                if (mainForm.volumeData != null)
                {
                    volumeTexture = CreateTexture3DFromChunkedVolume(mainForm.volumeData, Format.R8_UNorm);
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
                        Logger.Log($"[SharpDXVolumeRenderer] Created volume texture: {volW}x{volH}x{volD}");
                    }
                }

                // Load label volume if available
                if (mainForm.volumeLabels != null)
                {
                    // Change from R8_UInt to R32_Float
                    labelTexture = CreateTexture3DFromChunkedLabelVolume(mainForm.volumeLabels, Format.R32_Float);
                    if (labelTexture != null)
                    {
                        ShaderResourceViewDescription srvDesc = new ShaderResourceViewDescription
                        {
                            // Change from R8_UInt to R32_Float
                            Format = Format.R32_Float,
                            Dimension = ShaderResourceViewDimension.Texture3D,
                            Texture3D = new ShaderResourceViewDescription.Texture3DResource
                            {
                                MipLevels = 1,
                                MostDetailedMip = 0
                            }
                        };

                        labelSRV = new ShaderResourceView(device, labelTexture, srvDesc);
                        Logger.Log("[SharpDXVolumeRenderer] Created label volume texture");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log("[SharpDXVolumeRenderer] Error creating volume textures: " + ex.Message);
                // Don't throw here - the application should still work for basic cube rendering
                Logger.Log("[SharpDXVolumeRenderer] Continuing with basic rendering only");
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
                ChunkedVolume originalVolume = mainForm.volumeData;
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
        private void UpdateMaterialColors()
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

            // Create the 3D texture
            Texture3DDescription desc = new Texture3DDescription
            {
                Width = volume.Width,
                Height = volume.Height,
                Depth = volume.Depth,
                MipLevels = 1,
                Format = format,
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
                CpuAccessFlags = CpuAccessFlags.None,
                OptionFlags = ResourceOptionFlags.None
            };

            Texture3D texture = new Texture3D(device, desc);

            // Upload data chunk by chunk
            int chunkDim = volume.ChunkDim;

            for (int cz = 0; cz < volume.ChunkCountZ; cz++)
            {
                int zBase = cz * chunkDim;
                int zSize = Math.Min(chunkDim, volume.Depth - zBase);

                for (int cy = 0; cy < volume.ChunkCountY; cy++)
                {
                    int yBase = cy * chunkDim;
                    int ySize = Math.Min(chunkDim, volume.Height - yBase);

                    for (int cx = 0; cx < volume.ChunkCountX; cx++)
                    {
                        int xBase = cx * chunkDim;
                        int xSize = Math.Min(chunkDim, volume.Width - xBase);

                        int chunkIndex = volume.GetChunkIndex(cx, cy, cz);
                        byte[] chunkData = volume.GetChunkBytes(chunkIndex);

                        // Copy slice by slice
                        for (int z = 0; z < zSize; z++)
                        {
                            byte[] sliceData = new byte[xSize * ySize];
                            int chunkZOffset = z * chunkDim * chunkDim;

                            for (int y = 0; y < ySize; y++)
                            {
                                System.Buffer.BlockCopy(
                                    chunkData,
                                    chunkZOffset + y * chunkDim,
                                    sliceData,
                                    y * xSize,
                                    xSize);
                            }

                            // Upload slice to texture
                            GCHandle handle = GCHandle.Alloc(sliceData, GCHandleType.Pinned);
                            try
                            {
                                DataBox dataBox = new DataBox(handle.AddrOfPinnedObject(), xSize, xSize * ySize);
                                ResourceRegion region = new ResourceRegion(
                                    xBase, yBase, zBase + z,
                                    xBase + xSize, yBase + ySize, zBase + z + 1);

                                device.ImmediateContext.UpdateSubresource(dataBox, texture, 0, region);
                            }
                            finally
                            {
                                handle.Free();
                            }
                        }
                    }
                }
            }

            return texture;
        }

        private Texture3D CreateTexture3DFromChunkedLabelVolume(ChunkedLabelVolume volume, Format format)
        {
            if (volume == null) return null;

            // Create the 3D texture
            Texture3DDescription desc = new Texture3DDescription
            {
                Width = volume.Width,
                Height = volume.Height,
                Depth = volume.Depth,
                MipLevels = 1,
                Format = format,
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
                CpuAccessFlags = CpuAccessFlags.None,
                OptionFlags = ResourceOptionFlags.None
            };

            Texture3D texture = new Texture3D(device, desc);

            // Upload data chunk by chunk
            int chunkDim = volume.ChunkDim;

            for (int cz = 0; cz < volume.ChunkCountZ; cz++)
            {
                int zBase = cz * chunkDim;
                int zSize = Math.Min(chunkDim, volume.Depth - zBase);

                for (int cy = 0; cy < volume.ChunkCountY; cy++)
                {
                    int yBase = cy * chunkDim;
                    int ySize = Math.Min(chunkDim, volume.Height - yBase);

                    for (int cx = 0; cx < volume.ChunkCountX; cx++)
                    {
                        int xBase = cx * chunkDim;
                        int xSize = Math.Min(chunkDim, volume.Width - xBase);

                        int chunkIndex = volume.GetChunkIndex(cx, cy, cz);
                        byte[] chunkData = volume.GetChunkBytes(chunkIndex);

                        // Copy slice by slice
                        for (int z = 0; z < zSize; z++)
                        {
                            // Different handling based on format
                            if (format == Format.R32_Float)
                            {
                                // For Float format, convert bytes to floats
                                float[] sliceData = new float[xSize * ySize];
                                int chunkZOffset = z * chunkDim * chunkDim;

                                for (int y = 0; y < ySize; y++)
                                {
                                    for (int x = 0; x < xSize; x++)
                                    {
                                        // Convert byte to float
                                        int srcIndex = chunkZOffset + y * chunkDim + x;
                                        int destIndex = y * xSize + x;
                                        sliceData[destIndex] = chunkData[srcIndex]; // Implicit conversion from byte to float
                                    }
                                }

                                // Upload slice to texture with correct stride for floats
                                GCHandle handle = GCHandle.Alloc(sliceData, GCHandleType.Pinned);
                                try
                                {
                                    // Note: float is 4 bytes
                                    DataBox dataBox = new DataBox(handle.AddrOfPinnedObject(), xSize * sizeof(float), xSize * ySize * sizeof(float));
                                    ResourceRegion region = new ResourceRegion(
                                        xBase, yBase, zBase + z,
                                        xBase + xSize, yBase + ySize, zBase + z + 1);

                                    device.ImmediateContext.UpdateSubresource(dataBox, texture, 0, region);
                                }
                                finally
                                {
                                    handle.Free();
                                }
                            }
                            else
                            {
                                // Original code for byte formats
                                byte[] sliceData = new byte[xSize * ySize];
                                int chunkZOffset = z * chunkDim * chunkDim;

                                for (int y = 0; y < ySize; y++)
                                {
                                    System.Buffer.BlockCopy(
                                        chunkData,
                                        chunkZOffset + y * chunkDim,
                                        sliceData,
                                        y * xSize,
                                        xSize);
                                }

                                // Upload slice to texture
                                GCHandle handle = GCHandle.Alloc(sliceData, GCHandleType.Pinned);
                                try
                                {
                                    DataBox dataBox = new DataBox(handle.AddrOfPinnedObject(), xSize, xSize * ySize);
                                    ResourceRegion region = new ResourceRegion(
                                        xBase, yBase, zBase + z,
                                        xBase + xSize, yBase + ySize, zBase + z + 1);

                                    device.ImmediateContext.UpdateSubresource(dataBox, texture, 0, region);
                                }
                                finally
                                {
                                    handle.Free();
                                }
                            }
                        }
                    }
                }
            }

            return texture;
        }
        #endregion

        #region Rendering
        public void Render()
        {
            if (device == null || swapChain == null || renderPanel == null)
            {
                Logger.Log("[SharpDXVolumeRenderer] Cannot render: Device, SwapChain, or Panel is null");
                return;
            }

            // Check if the panel is minimized or has zero dimensions
            if (renderPanel.ClientSize.Width < 1 || renderPanel.ClientSize.Height < 1)
            {
                return; // Skip rendering to invisible panels
            }

            try
            {
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
                if (useLodSystem && isMoving)
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

                // Render the volume
                RenderVolume();

                // Draw scale bar and pixel size info
                // We'll skip this during camera movement to improve performance
                if (frameCount > 10 && !isMoving && frameCount % 30 == 0)
                {
                    DrawScaleBar();
                }
                else if (isMoving && scaleBarPictureBox != null)
                {
                    // Hide the scale bar during camera movement
                    scaleBarPictureBox.Visible = false;
                }
                else if (!isMoving && scaleBarPictureBox != null && !scaleBarPictureBox.Visible)
                {
                    // Show the scale bar when camera is stationary
                    scaleBarPictureBox.Visible = true;
                }

                // Present the scene - MODIFIED: use 0 for sync interval during movement for smoother rotation
                try
                {
                    // During movement, disable VSync (0) for more responsive feel
                    // When stationary, use VSync (1) to prevent tearing
                    swapChain.Present(isMoving ? 0 : 1, PresentFlags.None);
                }
                catch (SharpDXException ex)
                {
                    // Handle device errors as before...
                    if (ex.ResultCode.Code == SharpDX.DXGI.ResultCode.DeviceRemoved.Result.Code)
                    {
                        var reason = device.DeviceRemovedReason;
                        Logger.Log($"[SharpDXVolumeRenderer] Device Removed Error: {reason}");
                        try
                        {
                            RecreateDevice();
                        }
                        catch (Exception recEx)
                        {
                            Logger.Log($"[SharpDXVolumeRenderer] Failed to recover from device removed: {recEx.Message}");
                            throw;
                        }
                    }
                    else if (ex.ResultCode.Code == SharpDX.DXGI.ResultCode.DeviceReset.Result.Code)
                    {
                        Logger.Log("[SharpDXVolumeRenderer] Device Reset detected, recreating device");
                        try
                        {
                            RecreateDevice();
                        }
                        catch (Exception recEx)
                        {
                            Logger.Log($"[SharpDXVolumeRenderer] Failed to recover from device reset: {recEx.Message}");
                            throw;
                        }
                    }
                    else
                    {
                        Logger.Log("[SharpDXVolumeRenderer] Present error: " + ex.Message);
                        throw;
                    }
                }

                // Reset the needs render flag for infrequent renders when nothing changes
                if (!isMoving)
                {
                    // Only clear the flag if we're not in an interactive state
                    NeedsRender = false;
                }
                else
                {
                    // Always need to render during movement
                    NeedsRender = true;
                }

                // Log occasionally
                if (frameCount++ % 300 == 0)
                {
                    Logger.Log("[SharpDXVolumeRenderer] Frame rendered");
                }
            }
            catch (Exception ex)
            {
                Logger.Log("[SharpDXVolumeRenderer] Render error: " + ex.Message + "\n" + ex.StackTrace);
                throw;
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

                // Use wireframe in debug mode
                context.Rasterizer.State = debugMode ? wireframeRasterState : solidRasterState;

                // Setup rendering pipeline
                context.InputAssembler.InputLayout = inputLayout;
                context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
                context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(cubeVertexBuffer, Utilities.SizeOf<Vector3>(), 0));
                context.InputAssembler.SetIndexBuffer(cubeIndexBuffer, Format.R32_UInt, 0);

                // Clear any previous resources to avoid driver state confusion
                ShaderResourceView[] nullResources = new ShaderResourceView[6]; // Increased to 6 for color map
                context.PixelShader.SetShaderResources(0, 6, nullResources);

                // Set samplers
                context.PixelShader.SetSampler(0, linearSampler);
                context.PixelShader.SetSampler(1, pointSampler);

                // Set the blend state explicitly
                context.OutputMerger.SetBlendState(alphaBlendState, new Color4(0, 0, 0, 0), 0xFFFFFFFF);

                // Prepare resources array
                ShaderResourceView[] resources = new ShaderResourceView[6]; // Increased to 6 for color map

                // Fill the resources array with available resources
                if (labelVisibilitySRV != null) resources[0] = labelVisibilitySRV;
                if (labelOpacitySRV != null) resources[1] = labelOpacitySRV;

                // Always ensure we have a valid texture at resource position 2
                bool isMoving = isDragging || isPanning;
                bool useLodForMovement = useLodSystem && isMoving && currentLodLevel > 0 &&
                                       currentLodLevel <= MAX_LOD_LEVELS;

                // Set the volume texture resource - use LOD if available during movement, otherwise use original
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

                // Create a PictureBox if it doesn't exist
                if (scaleBarPictureBox == null)
                {
                    scaleBarPictureBox = new PictureBox();
                    scaleBarPictureBox.SizeMode = PictureBoxSizeMode.AutoSize;
                    scaleBarPictureBox.BackColor = System.Drawing.Color.Transparent;
                    renderPanel.Controls.Add(scaleBarPictureBox);
                    scaleBarPictureBox.BringToFront();
                }

                // Update the PictureBox
                scaleBarPictureBox.Image?.Dispose();
                scaleBarPictureBox.Image = bitmap;
                scaleBarPictureBox.Location = new System.Drawing.Point(barX - 10, barY - 25);
            }
            catch (Exception ex)
            {
                Logger.Log("[SharpDXVolumeRenderer] RenderScaleBar error: " + ex.Message);
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
                        showSlices ? 1.0f : 0.0f),
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
                        0.0f)
                };

                // Update the constant buffer
                context.UpdateSubresource(ref cbData, constantBuffer);

                if (frameCount % 60 == 0)
                {
                    Logger.Log("[SharpDXVolumeRenderer] Updated constant buffer");
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
                // Update visibility texture
                DataStream visibilityStream;
                context.MapSubresource(
                    labelVisibilityTexture,
                    0,
                    MapMode.WriteDiscard,
                    SharpDX.Direct3D11.MapFlags.None,
                    out visibilityStream);

                for (int i = 0; i < MAX_LABELS; i++)
                {
                    visibilityStream.Write(labelVisible[i] ? 1.0f : 0.0f);
                }

                context.UnmapSubresource(labelVisibilityTexture, 0);
                visibilityStream.Dispose();

                // Update opacity texture
                DataStream opacityStream;
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
                opacityStream.Dispose();

                // Also update material colors if needed
                if (mainForm.Materials != null && mainForm.Materials.Count > 0)
                {
                    UpdateMaterialColors();
                }
            }
            catch (Exception ex)
            {
                Logger.Log("[SharpDXVolumeRenderer] UpdateLabelTextures error: " + ex.Message);
            }
        }
        #endregion

        #region Public Methods
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
                labelVisible[materialId] = visible;
                UpdateLabelTextures();

                // Mark that we need rendering
                NeedsRender = true;
            }
        }

        public void SetMaterialOpacity(byte materialId, float opacity)
        {
            if (materialId < MAX_LABELS)
            {
                labelOpacity[materialId] = Math.Max(0.0f, Math.Min(1.0f, opacity));
                UpdateLabelTextures();

                // Mark that we need rendering
                NeedsRender = true;
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

                        // Create a bitmap and copy the data
                        using (var bitmap = new System.Drawing.Bitmap(
                            desc.Width,
                            desc.Height,
                            System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                        {
                            var bitmapData = bitmap.LockBits(
                                new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height),
                                System.Drawing.Imaging.ImageLockMode.WriteOnly,
                                System.Drawing.Imaging.PixelFormat.Format32bppArgb);

                            // Copy data row by row
                            for (int y = 0; y < desc.Height; y++)
                            {
                                IntPtr sourceRow = dataBox.DataPointer + y * dataBox.RowPitch;
                                IntPtr destRow = bitmapData.Scan0 + y * bitmapData.Stride;
                                Utilities.CopyMemory(destRow, sourceRow, desc.Width * 4);
                            }

                            bitmap.UnlockBits(bitmapData);
                            bitmap.Save(filePath);
                        }

                        // Unmap the resource
                        context.UnmapSubresource(stagingTexture, 0);
                    }
                }

                Logger.Log("[SharpDXVolumeRenderer] Screenshot saved to: " + filePath);
            }
            catch (Exception ex)
            {
                Logger.Log("[SharpDXVolumeRenderer] Screenshot error: " + ex.Message);
            }
        }
        #endregion

        #region Mouse Handlers
        private void OnMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                // Orbit camera
                isDragging = true;
                lastMousePosition = e.Location;

                // Set temporary lower quality during movement
                stepSize = Math.Min(2.0f, stepSize * 2.0f);

                // Mark that we need rendering
                NeedsRender = true;
            }
            else if (e.Button == MouseButtons.Right)
            {
                // Pan camera
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
            if (isDragging)
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
            if (e.Button == MouseButtons.Left)
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
                // Mark that we need rendering
                NeedsRender = true;
            }
        }
        #endregion

        #region IDisposable Implementation
        public void Dispose()
        {
            try
            {
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
                if (scaleBarPictureBox != null)
                {
                    scaleBarPictureBox.Image?.Dispose();
                    scaleBarPictureBox.Dispose();
                    scaleBarPictureBox = null;
                }

                Logger.Log("[SharpDXVolumeRenderer] Resources disposed successfully");
            }
            catch (Exception ex)
            {
                Logger.Log("[SharpDXVolumeRenderer] Error during disposal: " + ex.Message);
            }
        }
        #endregion
    }
}