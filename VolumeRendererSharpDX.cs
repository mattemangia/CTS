using System;
using System.Threading;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using SharpDX;
using System.Drawing;
using System.Drawing.Imaging;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System.IO;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;
using Color = SharpDX.Color;
using SharpDX.Direct3D9;
using SwapChain = SharpDX.DXGI.SwapChain;
using SamplerState = SharpDX.Direct3D11.SamplerState;
using VertexShader = SharpDX.Direct3D11.VertexShader;
using PixelShader = SharpDX.Direct3D11.PixelShader;
using Format = SharpDX.DXGI.Format;
using Usage = SharpDX.DXGI.Usage;
using SwapEffect = SharpDX.DXGI.SwapEffect;
using Filter = SharpDX.Direct3D11.Filter;
using BlendOperation = SharpDX.Direct3D11.BlendOperation;
using FillMode = SharpDX.Direct3D11.FillMode;
using PresentFlags = SharpDX.DXGI.PresentFlags;

namespace CTSegmenter.SharpDXIntegration
{
    public class SharpDXVolumeRenderer : IDisposable
    {
        private MainForm mainForm;
        private Panel renderPanel;
        private Device device;
        private DeviceContext context;
        private SwapChain swapChain;
        private RenderTargetView renderTargetView;
        private Texture2D depthBuffer;
        private DepthStencilView depthView;
        private Texture2D exitTexture;
        private RenderTargetView exitRTV;
        private ShaderResourceView exitSRV;

        // 3D textures for grayscale + label
        private Texture3D grayVolumeTex;
        private ShaderResourceView grayVolumeSRV;
        private Texture3D labelVolumeTex;
        private ShaderResourceView labelVolumeSRV;

        // States
        private SamplerState samplerLinear;
        private SamplerState samplerPoint;
        private BlendState blendState;
        private RasterizerState rStateCullFront;
        private RasterizerState rStateCullBack;

        private Buffer constantBuffer;
        private VertexShader rayVs;
        private PixelShader rayFrontPs;
        private PixelShader rayBackPs;
        private PixelShader rayCompositePs;
        private VertexShader sliceVs;
        private PixelShader slicePs;
        private InputLayout inputLayout;

        private Buffer cubeVb;
        private Buffer cubeIb;
        private int cubeIndexCount = 36;

        private Buffer quadVb;
        private Buffer quadIb;

        // Volume dimensions
        private int volW, volH, volD;
        // For threshold
        private float minThresholdNorm = 30 / 255f;
        private float maxThresholdNorm = 1.0f;
        // For step size in raymarch
        private float stepSize = 1.0f;

        // Slices
        private bool showSlices = false;
        private int sliceX, sliceY, sliceZ;

        // Label material visibility and opacity
        private const int MaxLabels = 256;
        private bool[] labelVisible = new bool[MaxLabels];
        private float[] labelOpacity = new float[MaxLabels];

        // Whether to show grayscale
        private bool showGray = true;

        // For synergy in the shader
        private struct ConstantData
        {
            public Matrix worldViewProj;
            public Matrix invView;
            public Vector4 thresholds; // (minT, maxT, stepSize, showGray? 1 or 0)
            public Vector4 dims;       // (volW, volH, volD, #labels?)
            public Vector4 sliceCoords; // (xSliceN, ySliceN, zSliceN, showSlices? 1 or 0)
            // We'll store label visibility in a 256-bit mask or partial table...
            // But for simplicity, let's store it in a float array we bind as a separate buffer or a Texture1D
        }

        // We also need a structured buffer or texture for label alpha and vis.
        private Texture1D labelVisTex;
        private ShaderResourceView labelVisSrv;
        private Texture1D labelOpacTex;
        private ShaderResourceView labelOpacSrv;

        public SharpDXVolumeRenderer(MainForm mainForm, Panel panel)
        {
            this.mainForm = mainForm;
            this.renderPanel = panel;

            // volume dims
            volW = mainForm.GetWidth();
            volH = mainForm.GetHeight();
            volD = mainForm.GetDepth();

            // Initialize label arrays
            for (int i = 0; i < MaxLabels; i++)
            {
                labelVisible[i] = false; // default off
                labelOpacity[i] = 1.0f;
            }

            // By default, label 0 is “background” so also invisible
            // If you want material 1..N visible, user must check them in the UI

            CreateDeviceAndSwapchain();
            CreateDynamicSliceVertexBuffer();
            CreateRenderTargets();
            CreateStates();
            CreateVolumeTextures();
            CreateShadersAndLayouts();
            CreateCubeGeometry();
            CreateQuadGeometry();
        }

        public void Dispose()
        {
            // Dispose GPU resources
            labelVisSrv?.Dispose();
            labelVisTex?.Dispose();
            labelOpacSrv?.Dispose();
            labelOpacTex?.Dispose();

            quadIb?.Dispose();
            quadVb?.Dispose();
            cubeIb?.Dispose();
            cubeVb?.Dispose();
            inputLayout?.Dispose();
            slicePs?.Dispose();
            sliceVs?.Dispose();
            rayCompositePs?.Dispose();
            rayBackPs?.Dispose();
            rayFrontPs?.Dispose();
            rayVs?.Dispose();
            constantBuffer?.Dispose();
            rStateCullFront?.Dispose();
            rStateCullBack?.Dispose();
            blendState?.Dispose();
            samplerLinear?.Dispose();
            samplerPoint?.Dispose();
            grayVolumeSRV?.Dispose();
            grayVolumeTex?.Dispose();
            labelVolumeSRV?.Dispose();
            labelVolumeTex?.Dispose();
            exitSRV?.Dispose();
            exitRTV?.Dispose();
            exitTexture?.Dispose();
            depthView?.Dispose();
            depthBuffer?.Dispose();
            renderTargetView?.Dispose();
            swapChain?.Dispose();
            device?.Dispose();
            context?.Dispose();
        }
        public bool GetMaterialVisibility(byte matId)
        {
            if (matId >= 0 && matId < MaxLabels)
                return labelVisible[matId];
            return false;
        }

        public float GetMaterialOpacity(byte matId)
        {
            if (matId >= 0 && matId < MaxLabels)
                return labelOpacity[matId];
            return 1.0f;
        }
        private void CreateDeviceAndSwapchain()
        {
            var scd = new SwapChainDescription()
            {
                BufferCount = 1,
                ModeDescription = new ModeDescription(
                    renderPanel.ClientSize.Width,
                    renderPanel.ClientSize.Height,
                    new Rational(60, 1),
                    Format.R8G8B8A8_UNorm),
                Usage = Usage.RenderTargetOutput,
                OutputHandle = renderPanel.Handle,
                SampleDescription = new SampleDescription(1, 0),
                IsWindowed = true,
                SwapEffect = SwapEffect.Discard,
            };
            Device.CreateWithSwapChain(DriverType.Hardware, DeviceCreationFlags.BgraSupport, scd, out device, out swapChain);
            context = device.ImmediateContext;

            // Prevent alt+enter
            using (var factory = swapChain.GetParent<Factory>())
            {
                factory.MakeWindowAssociation(renderPanel.Handle, WindowAssociationFlags.IgnoreAltEnter);
            }
        }

        private void CreateRenderTargets()
        {
            using (var backBuffer = Texture2D.FromSwapChain<Texture2D>(swapChain, 0))
            {
                renderTargetView = new RenderTargetView(device, backBuffer);
            }

            var depthDesc = new Texture2DDescription
            {
                Format = Format.D24_UNorm_S8_UInt,
                Width = renderPanel.ClientSize.Width,
                Height = renderPanel.ClientSize.Height,
                MipLevels = 1,
                ArraySize = 1,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.DepthStencil,
                CpuAccessFlags = CpuAccessFlags.None,
                OptionFlags = ResourceOptionFlags.None
            };
            depthBuffer = new Texture2D(device, depthDesc);
            depthView = new DepthStencilView(device, depthBuffer);

            var exitDesc = new Texture2DDescription
            {
                Format = Format.R32G32B32A32_Float,
                Width = renderPanel.ClientSize.Width,
                Height = renderPanel.ClientSize.Height,
                MipLevels = 1,
                ArraySize = 1,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                CpuAccessFlags = CpuAccessFlags.None,
                OptionFlags = ResourceOptionFlags.None
            };
            exitTexture = new Texture2D(device, exitDesc);
            exitRTV = new RenderTargetView(device, exitTexture);
            exitSRV = new ShaderResourceView(device, exitTexture);
        }

        private void CreateStates()
        {
            // Samplers
            var sampDesc = new SamplerStateDescription()
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
            };
            samplerLinear = new SamplerState(device, sampDesc);

            sampDesc.Filter = Filter.MinMagMipPoint;
            samplerPoint = new SamplerState(device, sampDesc);

            // Blend
            var blendDesc = new BlendStateDescription();
            blendDesc.RenderTarget[0].IsBlendEnabled = true;
            blendDesc.RenderTarget[0].SourceBlend = BlendOption.SourceAlpha;
            blendDesc.RenderTarget[0].DestinationBlend = BlendOption.InverseSourceAlpha;
            blendDesc.RenderTarget[0].BlendOperation = BlendOperation.Add;
            blendDesc.RenderTarget[0].SourceAlphaBlend = BlendOption.One;
            blendDesc.RenderTarget[0].DestinationAlphaBlend = BlendOption.Zero;
            blendDesc.RenderTarget[0].AlphaBlendOperation = BlendOperation.Add;
            blendDesc.RenderTarget[0].RenderTargetWriteMask = ColorWriteMaskFlags.All;
            blendState = new BlendState(device, blendDesc);

            // Rasterizer (cull front/back)
            var rsFront = new RasterizerStateDescription()
            {
                CullMode = CullMode.Front,
                FillMode = FillMode.Solid,
                IsDepthClipEnabled = true
            };
            rStateCullFront = new RasterizerState(device, rsFront);

            var rsBack = new RasterizerStateDescription()
            {
                CullMode = CullMode.Back,
                FillMode = FillMode.Solid,
                IsDepthClipEnabled = true
            };
            rStateCullBack = new RasterizerState(device, rsBack);
        }

        private void CreateVolumeTextures()
        {
            // Load grayscale volume
            grayVolumeTex = VolumeLoader.CreateTexture3DFromChunked(device, mainForm.volumeData, Format.R8_UNorm);
            grayVolumeSRV = new ShaderResourceView(device, grayVolumeTex);

            // Load label volume if available
            if (mainForm.volumeLabels != null)
            {
                labelVolumeTex = VolumeLoader.CreateTexture3DFromChunked(device, mainForm.volumeLabels, Format.R8_UInt);
                labelVolumeSRV = new ShaderResourceView(device, labelVolumeTex);
            }

            // Also create label-visibility textures
            // We'll re-upload them as needed
            UpdateLabelVisAndOpacityTextures();
        }

        private void UpdateLabelVisAndOpacityTextures()
        {
            // Create a 256-element array of floats, 1=visible, 0=invisible
            // Then we do same for alpha
            float[] visData = new float[MaxLabels];
            float[] opacData = new float[MaxLabels];
            for (int i = 0; i < MaxLabels; i++)
            {
                visData[i] = labelVisible[i] ? 1.0f : 0.0f;
                opacData[i] = labelOpacity[i];
            }

            // Each as Texture1D
            // Recreate from scratch
            labelVisTex?.Dispose();
            labelVisSrv?.Dispose();
            labelOpacTex?.Dispose();
            labelOpacSrv?.Dispose();

            var texDesc = new Texture1DDescription()
            {
                ArraySize = 1,
                MipLevels = 1,
                Width = MaxLabels,
                Format = Format.R32_Float,
                BindFlags = BindFlags.ShaderResource,
                Usage = ResourceUsage.Immutable,
                CpuAccessFlags = CpuAccessFlags.None,
                OptionFlags = ResourceOptionFlags.None
            };

            // vis
            GCHandle handleV = GCHandle.Alloc(visData, GCHandleType.Pinned);
            try
            {
                var box = new DataBox(handleV.AddrOfPinnedObject(), 4 * MaxLabels, 0);
                labelVisTex = new Texture1D(device, texDesc, new DataBox[] { box });
            }
            finally
            {
                handleV.Free();
            }
            labelVisSrv = new ShaderResourceView(device, labelVisTex);

            // opac
            GCHandle handleO = GCHandle.Alloc(opacData, GCHandleType.Pinned);
            try
            {
                var box = new DataBox(handleO.AddrOfPinnedObject(), 4 * MaxLabels, 0);
                labelOpacTex = new Texture1D(device, texDesc, new DataBox[] { box });
            }
            finally
            {
                handleO.Free();
            }
            labelOpacSrv = new ShaderResourceView(device, labelOpacTex);
        }

        private void CreateShadersAndLayouts()
        {
            
            var shaderSource = ShaderStrings.VolumeRaymarchHlsl; // See below "ShaderStrings" class

            var vsByte = SharpDX.D3DCompiler.ShaderBytecode.Compile(shaderSource, "VSMain", "vs_5_0");
            var psFrontByte = SharpDX.D3DCompiler.ShaderBytecode.Compile(shaderSource, "PSBackface", "ps_5_0");
            var psBackByte = SharpDX.D3DCompiler.ShaderBytecode.Compile(shaderSource, "PSRaymarch", "ps_5_0");
            var psSliceByte = SharpDX.D3DCompiler.ShaderBytecode.Compile(shaderSource, "PSSlice", "ps_5_0");

            rayVs = new VertexShader(device, vsByte);
            rayFrontPs = new PixelShader(device, psFrontByte);
            rayBackPs = new PixelShader(device, psBackByte);
            rayCompositePs = rayBackPs; // naming difference
            sliceVs = new VertexShader(device, vsByte);
            slicePs = new PixelShader(device, psSliceByte);

            // Input layout
            var layoutElems = new[] {
                new SharpDX.Direct3D11.InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0)
            };
            inputLayout = new InputLayout(device, vsByte, layoutElems);

            constantBuffer = new Buffer(device, Utilities.SizeOf<ConstantData>(), ResourceUsage.Default,
                BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        }

        private void CreateCubeGeometry()
        {
            // A unit cube in [0,volW] x [0, volH] x [0, volD], then we scale in the shader if needed
            // Actually let's store the corners explicitly:
            var verts = new Vector3[8];
            verts[0] = new Vector3(0, 0, 0);
            verts[1] = new Vector3(volW, 0, 0);
            verts[2] = new Vector3(volW, volH, 0);
            verts[3] = new Vector3(0, volH, 0);
            verts[4] = new Vector3(0, 0, volD);
            verts[5] = new Vector3(volW, 0, volD);
            verts[6] = new Vector3(volW, volH, volD);
            verts[7] = new Vector3(0, volH, volD);

            var inds = new int[]
            {
                0,2,1, 0,3,2,  // front
                4,5,6, 4,6,7,  // back
                0,4,7, 0,7,3,  // left
                1,2,6, 1,6,5,  // right
                0,1,5, 0,5,4,  // bottom
                2,3,7, 2,7,6   // top
            };
            cubeIndexCount = inds.Length;

            cubeVb = Buffer.Create(device, BindFlags.VertexBuffer, verts);
            cubeIb = Buffer.Create(device, BindFlags.IndexBuffer, inds);
        }

        private void CreateQuadGeometry()
        {
            // For slices we reuse a single quad VB, then update it
            var quadVerts = new Vector3[]
            {
                new Vector3(0,0,0),
                new Vector3(0,1,0),
                new Vector3(1,1,0),
                new Vector3(1,0,0)
            };
            var quadInds = new int[] { 0, 1, 2, 0, 2, 3 };

            quadVb = Buffer.Create(device, BindFlags.VertexBuffer, quadVerts);
            quadIb = Buffer.Create(device, BindFlags.IndexBuffer, quadInds);
        }

        public void Render()
        {
            if (device == null || swapChain == null) return;

            // Update label visibility textures if needed
            UpdateLabelVisAndOpacityTextures();

            // Clear
            context.OutputMerger.SetRenderTargets(depthView, renderTargetView);
            context.ClearRenderTargetView(renderTargetView, new Color4(0, 0, 0, 1));
            context.ClearDepthStencilView(depthView, DepthStencilClearFlags.Depth, 1.0f, 0);

            // PASS 1: draw back faces to exitTexture
            context.OutputMerger.SetRenderTargets(null as DepthStencilView, exitRTV);
            context.ClearRenderTargetView(exitRTV, new Color4(0, 0, 0, 0));
            context.Rasterizer.State = rStateCullFront;

            context.InputAssembler.InputLayout = inputLayout;
            context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
            context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(cubeVb, Utilities.SizeOf<Vector3>(), 0));
            context.InputAssembler.SetIndexBuffer(cubeIb, Format.R32_UInt, 0);

            context.VertexShader.Set(rayVs);
            context.PixelShader.Set(rayFrontPs);
            UpdateConstantBufferAndSet(0);
            context.DrawIndexed(cubeIndexCount, 0, 0);

            // PASS 2: draw front faces with raymarch
            context.OutputMerger.SetRenderTargets(depthView, renderTargetView);
            context.Rasterizer.State = rStateCullBack;

            context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(cubeVb, Utilities.SizeOf<Vector3>(), 0));
            context.InputAssembler.SetIndexBuffer(cubeIb, Format.R32_UInt, 0);

            context.VertexShader.Set(rayVs);
            context.PixelShader.Set(rayBackPs);
            UpdateConstantBufferAndSet(0);

            // Set resources
            context.PixelShader.SetShaderResource(0, exitSRV);
            context.PixelShader.SetShaderResource(1, grayVolumeSRV);
            context.PixelShader.SetShaderResource(2, labelVolumeSRV);
            context.PixelShader.SetShaderResource(3, labelVisSrv);
            context.PixelShader.SetShaderResource(4, labelOpacSrv);
            context.PixelShader.SetSampler(0, samplerLinear);
            context.PixelShader.SetSampler(1, samplerPoint);

            context.DrawIndexed(cubeIndexCount, 0, 0);

            // Optionally draw slices if showSlices
            if (showSlices)
            {
                DrawSlicePlanes();
            }

            swapChain.Present(0, PresentFlags.None);
        }
        private Buffer dynamicSliceVb;
        private const int NumSliceVertices = 4; // only need 4 per plane
        private const int MaxSlices = 3;       // x-plane, y-plane, z-plane

        private void CreateDynamicSliceVertexBuffer()
        {
            var vbDesc = new BufferDescription()
            {
                SizeInBytes = Utilities.SizeOf<Vector3>() * NumSliceVertices * MaxSlices,
                Usage = ResourceUsage.Dynamic,
                BindFlags = BindFlags.VertexBuffer,
                CpuAccessFlags = CpuAccessFlags.Write,
                OptionFlags = ResourceOptionFlags.None,
                StructureByteStride = 0
            };
            dynamicSliceVb = new Buffer(device, vbDesc);
        }
        private void DrawSlicePlanes()
        {
            // Set the correct render targets
            context.OutputMerger.SetRenderTargets(depthView,renderTargetView);
            // Use whichever rasterizer state
            // e.g., context.Rasterizer.State = null (no culling) or a custom RasterizerState
            context.Rasterizer.State = null;

            // Set pipeline states
            context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
            context.InputAssembler.InputLayout = inputLayout;

            // Use our slice vertex and pixel shaders
            context.VertexShader.Set(sliceVs);
            context.PixelShader.Set(slicePs);

            // We already have an index buffer for a single quad (two triangles):
            context.InputAssembler.SetIndexBuffer(quadIb, Format.R32_UInt, 0);

            // Make sure our constant buffer is up to date
            UpdateConstantBufferAndSet(0);

            // Bind the grayscale 3D volume to slot t1 in the pixel shader
            // (Check your code - if you're also using t0 for something else, adjust accordingly.)
            context.PixelShader.SetShaderResource(1, grayVolumeSRV);

            // We'll do three calls, one for each orthogonal slice: X, Y, Z
            // For each slice, we update the dynamic vertex buffer with that plane's corners in local volume coords.
            // Then we draw using the same index buffer.

            // -------------------------
            // 1) X-plane
            // -------------------------
            float xVal = sliceX; // A local volume coordinate in [0, volW]
            var xVerts = new Vector3[]
            {
        new Vector3(xVal,    0,    0),
        new Vector3(xVal, volH,    0),
        new Vector3(xVal, volH, volD),
        new Vector3(xVal,    0, volD),
            };
            UploadPlaneVerticesToDynamicVb(xVerts);
            // Bind the dynamic VB
            context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(dynamicSliceVb, Utilities.SizeOf<Vector3>(), 0));
            // Draw
            context.DrawIndexed(6, 0, 0);

            // -------------------------
            // 2) Y-plane
            // -------------------------
            float yVal = sliceY; // A local volume coordinate in [0, volH]
            var yVerts = new Vector3[]
            {
        new Vector3(   0, yVal,    0),
        new Vector3(   0, yVal, volD),
        new Vector3(volW, yVal, volD),
        new Vector3(volW, yVal,    0)
            };
            UploadPlaneVerticesToDynamicVb(yVerts);
            context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(dynamicSliceVb, Utilities.SizeOf<Vector3>(), 0));
            context.DrawIndexed(6, 0, 0);

            // -------------------------
            // 3) Z-plane
            // -------------------------
            float zVal = sliceZ; // A local volume coordinate in [0, volD]
            var zVerts = new Vector3[]
            {
        new Vector3(   0,    0, zVal),
        new Vector3(volW,    0, zVal),
        new Vector3(volW, volH, zVal),
        new Vector3(   0, volH, zVal)
            };
            UploadPlaneVerticesToDynamicVb(zVerts);
            context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(dynamicSliceVb, Utilities.SizeOf<Vector3>(), 0));
            context.DrawIndexed(6, 0, 0);
        }
        /// <summary>
        /// Writes the 4 vertices of a slice plane into the dynamic VB.
        /// </summary>
        private void UploadPlaneVerticesToDynamicVb(Vector3[] verts)
        {
            DataStream stream;
            var dataBox = context.MapSubresource(
                dynamicSliceVb,
                0,
                MapMode.WriteDiscard,
                SharpDX.Direct3D11.MapFlags.None,
                out stream
            );

            // Write exactly 4 vertices
            for (int i = 0; i < 4; i++)
                stream.Write(verts[i]);

            context.UnmapSubresource(dynamicSliceVb, 0);
            stream.Dispose();
        }
        private void UpdateConstantBufferAndSet(int slot)
        {
            // 1) -----------------------------------------------
            // Compute bounding box & bounding sphere for the volume.
            // This helps us ensure the camera is set up so the entire volume is in view.

            // Volume min/max in local space. Suppose the volume extends from (0,0,0) to (volW, volH, volD).
            Vector3 volMin = new Vector3(0, 0, 0);
            Vector3 volMax = new Vector3(volW, volH, volD);
            Vector3 volCenter = (volMin + volMax) * 0.5f;

            // Diagonal length:
            float diag = (volMax - volMin).Length();
            // Bounding sphere radius:
            float radius = diag * 0.5f;


            // 2) -----------------------------------------------
            // Pick a camera position using some orbit controls. 
            // For example, an Arcball approach with Yaw, Pitch, Distance.
            // In a real app, might store yaw, pitch, distance as class-level fields
            // and let the user interact with them. Below are just placeholders.

            float yaw = 0.7f;    // in radians
            float pitch = 0.4f;    // in radians
            float distance = radius * 2.5f; // how far the camera sits from center. Adjust as needed.

            // Convert spherical coords => cartesian (an example approach).
            // We want to orbit around volCenter.
            float cosPitch = (float)Math.Cos(pitch);
            float sinPitch = (float)Math.Sin(pitch);
            float cosYaw = (float)Math.Cos(yaw);
            float sinYaw = (float)Math.Sin(yaw);

            Vector3 camPosLocal = new Vector3(
                distance * cosPitch * cosYaw,
                distance * sinPitch,
                distance * cosPitch * sinYaw);

            // Our final camera position in world space:
            Vector3 cameraPosition = volCenter + camPosLocal;
            // Our camera look-target is the center of the volume:
            Vector3 cameraTarget = volCenter;
            // Typically "up" is +Y:
            Vector3 cameraUp = Vector3.UnitY;

            // Build the View matrix:
            Matrix viewMat = Matrix.LookAtLH(cameraPosition, cameraTarget, cameraUp);


            // 3) -----------------------------------------------
            // Build a Perspective projection matrix.
            // FOV, aspect ratio, near & far planes can be user defined or just set here.
            float fovDegrees = 60.0f;
            float fovRadians = MathUtil.DegreesToRadians(fovDegrees);

            float aspect = (float)renderPanel.ClientSize.Width /
                           (float)renderPanel.ClientSize.Height;

            // If wanted to ensure aspect is valid:
            if (aspect < 0.1f) aspect = 1f;

            // We can pick near & far to comfortably enclose the volume:
            float nearPlane = 0.1f;
            float farPlane = distance * 10f; // or something bigger than the volume distance

            Matrix projMat = Matrix.PerspectiveFovLH(fovRadians, aspect, nearPlane, farPlane);

            // 4) -----------------------------------------------
            // Combine into a single view-projection:
            Matrix viewProj = viewMat * projMat;

            // 5) -----------------------------------------------
            // Fill out our ConstantData struct with everything we need:
            var cdata = new ConstantData();

            cdata.worldViewProj = viewProj;
            cdata.invView = Matrix.Invert(viewMat);

            // thresholds.x = min threshold
            // thresholds.y = max threshold
            // thresholds.z = ray step size
            // thresholds.w = showGray ? 1 : 0
            cdata.thresholds = new Vector4(
                minThresholdNorm,
                maxThresholdNorm,
                stepSize,
                showGray ? 1.0f : 0.0f
            );

            // dims = (volW, volH, volD, optionalValue)
            cdata.dims = new Vector4(volW, volH, volD, 0);

            // sliceCoords = ( sliceXNormalized, sliceYNormalized, sliceZNormalized, showSlices? 1.0 : 0.0 )
            cdata.sliceCoords = new Vector4(
                sliceX / (float)Math.Max(1, volW - 1),
                sliceY / (float)Math.Max(1, volH - 1),
                sliceZ / (float)Math.Max(1, volD - 1),
                showSlices ? 1.0f : 0.0f
            );

            // 6) -----------------------------------------------
            // Push this data into the GPU’s constant buffer:
            context.UpdateSubresource(ref cdata, constantBuffer);

            // Finally bind the constant buffer to both VS and PS (or GS, CS, etc. if needed).
            context.VertexShader.SetConstantBuffer(slot, constantBuffer);
            context.PixelShader.SetConstantBuffer(slot, constantBuffer);
        }

        public int SliceX => sliceX;
        public int SliceY => sliceY;
        public int SliceZ => sliceZ;
        public bool[] GetLabelVisibilityArray()
        {
            var copy = new bool[256];
            Array.Copy(labelVisible, copy, 256);
            return copy;
        }
        public void OnResize()
        {
            if (swapChain == null) return;
            context.OutputMerger.SetRenderTargets(depthView, renderTargetView);

            renderTargetView?.Dispose();
            depthView?.Dispose();
            depthBuffer?.Dispose();
            exitRTV?.Dispose();
            exitSRV?.Dispose();
            exitTexture?.Dispose();

            swapChain.ResizeBuffers(1, renderPanel.ClientSize.Width, renderPanel.ClientSize.Height,
                Format.R8G8B8A8_UNorm, SwapChainFlags.None);

            CreateRenderTargets();
        }

        // Called externally to set threshold
        public int MinThreshold
        {
            get => (int)(minThresholdNorm * 255f);
            set => minThresholdNorm = Math.Min(1f, Math.Max(0f, value / 255f));
        }
        public int MaxThreshold
        {
            get => (int)(maxThresholdNorm * 255f);
            set => maxThresholdNorm = Math.Min(1f, Math.Max(0f, value / 255f));
        }

        public bool ShowGrayscale
        {
            get => showGray;
            set => showGray = value;
        }

        public void SetRaymarchStepSize(float step)
        {
            stepSize = step;
        }

        public bool ShowOrthoslices
        {
            get => showSlices;
            set => showSlices = value;
        }
        public void UpdateSlices(int sx, int sy, int sz)
        {
            sliceX = Math.Max(0, Math.Min(sx, volW - 1));
            sliceY = Math.Max(0, Math.Min(sy, volH - 1));
            sliceZ = Math.Max(0, Math.Min(sz, volD - 1));
        }

        public void SetMaterialVisibility(byte matId, bool visible)
        {
            if (matId < MaxLabels)
            {
                labelVisible[matId] = visible;
            }
        }
        public void SetMaterialOpacity(byte matId, float opacity)
        {
            if (matId < MaxLabels)
            {
                labelOpacity[matId] = Math.Min(1f, Math.Max(0f, opacity));
            }
        }

        public void SaveScreenshot(string filePath)
        {
            Render(); // Ensure latest frame

            using (var backBuffer = Texture2D.FromSwapChain<Texture2D>(swapChain, 0))
            {
                var textureDesc = backBuffer.Description;

                // Create a CPU-readable copy
                var copyDesc = new Texture2DDescription
                {
                    Width = textureDesc.Width,
                    Height = textureDesc.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = textureDesc.Format,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None,
                    CpuAccessFlags = CpuAccessFlags.Read,
                    OptionFlags = ResourceOptionFlags.None
                };

                using (var copyTex = new Texture2D(device, copyDesc))
                {
                    context.CopyResource(backBuffer, copyTex);

                    var map = context.MapSubresource(copyTex, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);

                    using (var bmp = new System.Drawing.Bitmap(textureDesc.Width, textureDesc.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                    {
                        var bmpData = bmp.LockBits(
                            new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height),
                            System.Drawing.Imaging.ImageLockMode.WriteOnly,
                            System.Drawing.Imaging.PixelFormat.Format32bppArgb);

                        unsafe
                        {
                            byte* srcPtr = (byte*)map.DataPointer;
                            byte* dstPtr = (byte*)bmpData.Scan0;

                            for (int y = 0; y < textureDesc.Height; y++)
                            {
                                System.Buffer.MemoryCopy(srcPtr + y * map.RowPitch, dstPtr + y * bmpData.Stride, bmpData.Stride, bmpData.Stride);
                            }
                        }

                        bmp.UnlockBits(bmpData);
                        bmp.RotateFlip(System.Drawing.RotateFlipType.RotateNoneFlipY);
                        bmp.Save(filePath);
                    }

                    context.UnmapSubresource(copyTex, 0);
                }
            }
        }
    }

    // Helper class that loads a chunked volume into a SharpDX Texture3D
    public static class VolumeLoader
    {
        public static Texture3D CreateTexture3DFromChunked(Device device, ChunkedVolume vol, Format format)
        {
            if (vol == null) return null;
            var desc = new Texture3DDescription()
            {
                Width = vol.Width,
                Height = vol.Height,
                Depth = vol.Depth,
                MipLevels = 1,
                Format = format, // e.g. R8_UNorm
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
                CpuAccessFlags = CpuAccessFlags.None,
                OptionFlags = ResourceOptionFlags.None
            };
            var tex3D = new Texture3D(device, desc);

            // Upload chunk by chunk
            int cd = vol.ChunkDim;
            for (int cz = 0; cz < vol.ChunkCountZ; cz++)
            {
                int zBase = cz * cd;
                int zSize = Math.Min(cd, vol.Depth - zBase);
                for (int cy = 0; cy < vol.ChunkCountY; cy++)
                {
                    int yBase = cy * cd;
                    int ySize = Math.Min(cd, vol.Height - yBase);
                    for (int cx = 0; cx < vol.ChunkCountX; cx++)
                    {
                        int xBase = cx * cd;
                        int xSize = Math.Min(cd, vol.Width - xBase);
                        var chunkIdx = vol.GetChunkIndex(cx, cy, cz);
                        var chunkBytes = vol.GetChunkBytes(chunkIdx);
                        // Copy subregion
                        for (int zz = 0; zz < zSize; zz++)
                        {
                            var sliceBytes = new byte[xSize * ySize];
                            int chunkZOff = zz * cd * cd;
                            for (int yy = 0; yy < ySize; yy++)
                            {
                                System.Buffer.BlockCopy(chunkBytes,
                                    chunkZOff + yy * cd + 0,
                                    sliceBytes,
                                    yy * xSize,
                                    xSize);
                            }
                            // Now upload slice
                            var handle = GCHandle.Alloc(sliceBytes, GCHandleType.Pinned);
                            try
                            {
                                var box = new DataBox(handle.AddrOfPinnedObject(), xSize, xSize * ySize);
                                var region = new ResourceRegion(
                                    xBase, yBase, zBase + zz,
                                    xBase + xSize, yBase + ySize, zBase + zz + 1);
                                device.ImmediateContext.UpdateSubresource(box, tex3D, 0, region);
                            }
                            finally
                            {
                                handle.Free();
                            }
                        }
                    }
                }
            }
            return tex3D;
        }

        public static Texture3D CreateTexture3DFromChunked(Device device, ChunkedLabelVolume vol, Format format)
        {
            if (vol == null) return null;
            var desc = new Texture3DDescription()
            {
                Width = vol.Width,
                Height = vol.Height,
                Depth = vol.Depth,
                MipLevels = 1,
                Format = format, // R8_UInt or similar
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
                CpuAccessFlags = CpuAccessFlags.None,
                OptionFlags = ResourceOptionFlags.None
            };
            var tex3D = new Texture3D(device, desc);

            int cd = vol.ChunkDim;
            for (int cz = 0; cz < vol.ChunkCountZ; cz++)
            {
                int zBase = cz * cd;
                int zSize = Math.Min(cd, vol.Depth - zBase);
                for (int cy = 0; cy < vol.ChunkCountY; cy++)
                {
                    int yBase = cy * cd;
                    int ySize = Math.Min(cd, vol.Height - yBase);
                    for (int cx = 0; cx < vol.ChunkCountX; cx++)
                    {
                        int xBase = cx * cd;
                        int xSize = Math.Min(cd, vol.Width - xBase);
                        var chunkIdx = vol.GetChunkIndex(cx, cy, cz);
                        var chunkBytes = vol.GetChunkBytes(chunkIdx);
                        // Copy subregion
                        for (int zz = 0; zz < zSize; zz++)
                        {
                            var sliceBytes = new byte[xSize * ySize];
                            int chunkZOff = zz * cd * cd;
                            for (int yy = 0; yy < ySize; yy++)
                            {
                                System.Buffer.BlockCopy(
                                    chunkBytes,
                                    chunkZOff + yy * cd,
                                    sliceBytes,
                                    yy * xSize,
                                    xSize);
                            }
                            var handle = GCHandle.Alloc(sliceBytes, GCHandleType.Pinned);
                            try
                            {
                                var box = new DataBox(handle.AddrOfPinnedObject(), xSize, xSize * ySize);
                                var region = new ResourceRegion(
                                    xBase, yBase, zBase + zz,
                                    xBase + xSize, yBase + ySize, zBase + zz + 1);
                                device.ImmediateContext.UpdateSubresource(box, tex3D, 0, region);
                            }
                            finally
                            {
                                handle.Free();
                            }
                        }
                    }
                }
            }
            return tex3D;
        }
    }

    internal static class ShaderStrings
    {
        public const string VolumeRaymarchHlsl = @"
//--------------------------------------------------------------------------------------
// Constant buffer definition
//--------------------------------------------------------------------------------------
cbuffer ConstantData : register(b0)
{
    float4x4 worldViewProj; // Combined view-projection matrix
    float4x4 invView;       // Inverse of the view matrix

    // thresholds.x = min threshold
    // thresholds.y = max threshold
    // thresholds.z = step size for raymarching
    // thresholds.w = whether to show grayscale (1.0) or not (0.0)
    float4 thresholds;

    // dims.x = volume width
    // dims.y = volume height
    // dims.z = volume depth
    // dims.w = unused or #labels if needed
    float4 dims;

    // sliceCoords.x = normalized sliceX
    // sliceCoords.y = normalized sliceY
    // sliceCoords.z = normalized sliceZ
    // sliceCoords.w = showSlices? 1.0 : 0.0
    float4 sliceCoords;
}

//--------------------------------------------------------------------------------------
// Resources
//--------------------------------------------------------------------------------------
Texture2D<float4> ExitTex : register(t0);

// GrayTex is the 3D grayscale volume (0..1 range)
Texture3D<float> GrayTex : register(t1);

// LabelTex is the 3D label volume (uint IDs)
Texture3D<uint> LabelTex : register(t2);

// LabelVis: 1D float array with 256 entries (label ID => visible?)
Texture1D<float> LabelVis : register(t3);

// LabelOpac: 1D float array with 256 entries (label ID => alpha)
Texture1D<float> LabelOpac : register(t4);

// Sampler states
SamplerState samLinear : register(s0);
SamplerState samPoint  : register(s1);

//--------------------------------------------------------------------------------------
// VS_OUT struct used by all passes
//--------------------------------------------------------------------------------------
struct VS_OUT
{
    float4 posH : SV_POSITION;  // Projected position
    float3 posW : POSITION0;    // local volume coords
};

//--------------------------------------------------------------------------------------
// Forward declaration or simply place HueFromLabel above usage
//--------------------------------------------------------------------------------------
float3 HueFromLabel(uint labelId);

//--------------------------------------------------------------------------------------
// Vertex Shader (VSMain): transforms volume or slice geometry into clip space
//--------------------------------------------------------------------------------------
VS_OUT VSMain(float3 position : POSITION)
{
    VS_OUT o;
    float4 worldPos = float4(position, 1.0f);

    // Transform by worldViewProj
    o.posH = mul(worldPos, worldViewProj);
    // Pass through the local coords for sampling
    o.posW = position;

    return o;
}

//--------------------------------------------------------------------------------------
// PSBackface: Renders the back faces of the cube to a float4 texture
//             The RGB stores the normalized exit position of the ray
//--------------------------------------------------------------------------------------
float4 PSBackface(VS_OUT input) : SV_TARGET
{
    // Normalize volume position to [0,1]
    float3 uvw = float3(
        input.posW.x / dims.x,
        input.posW.y / dims.y,
        input.posW.z / dims.z
    );

    // Write uvw into the RGBA, alpha=1 for convenience
    return float4(uvw, 1.0f);
}

//--------------------------------------------------------------------------------------
// PSRaymarch: Renders the front faces, sampling from the back-face texture
//             to determine the exit position, then accumulates color from the volume.
//--------------------------------------------------------------------------------------
float4 PSRaymarch(VS_OUT input) : SV_TARGET
{
    float minT     = thresholds.x;
    float maxT     = thresholds.y;
    float stepSize = thresholds.z;
    bool  showGray = (thresholds.w > 0.5);

    // Entry in [0..1]^3
    float3 uvwEntry = float3(
        input.posW.x / dims.x,
        input.posW.y / dims.y,
        input.posW.z / dims.z
    );

    // Retrieve exit from ExitTex
    int2 pixCoord = int2(round(input.posH.xy));
    float4 exitVal = ExitTex.Load(int3(pixCoord, 0));
    float3 uvwExit = exitVal.xyz;

    float3 rayDir = uvwExit - uvwEntry;
    float dist = length(rayDir);

    // Steps
    int steps = (int)(dist / stepSize) + 1;
    float3 stepVec = rayDir / (float)steps;

    float4 finalColor = float4(0, 0, 0, 0);

    // Raymarch front->back
    [loop]
    for (int i = 0; i < steps; i++)
    {
        float3 samplePos = uvwEntry + stepVec * i;

        // If out of [0..1], stop
        if (any(samplePos < 0.0f) || any(samplePos > 1.0f))
            break;

        // If grayscale is enabled, sample GrayTex & threshold
        float grayVal = 0;
        if (showGray)
        {
            grayVal = GrayTex.SampleLevel(samLinear, samplePos, 0).r;
            if (grayVal < minT || grayVal > maxT)
                grayVal = 0.0f;
        }

        // Sample label
        uint labelVal = LabelTex.SampleLevel(samLinear, samplePos, 0);
        if (labelVal > 0 && labelVal < 256)
        {
            float vis = LabelVis.Load(labelVal);
            if (vis > 0.5f)
            {
                float alpha = LabelOpac.Load(labelVal);
                float3 colorLabel = HueFromLabel(labelVal);
                float4 curSample  = float4(colorLabel, alpha);

                // Blend front->back
                finalColor.rgb = lerp(finalColor.rgb, curSample.rgb, curSample.a);
                finalColor.a    = finalColor.a + curSample.a * (1 - finalColor.a);

                // Early out if nearly opaque
                if (finalColor.a > 0.95f)
                    break;
            }
        }

        // If grayscale is valid, blend it as well
        if (showGray && grayVal > 0.0f)
        {
            float4 grayC = float4(grayVal, grayVal, grayVal, 0.5f);
            finalColor.rgb = lerp(finalColor.rgb, grayC.rgb, grayC.a);
            finalColor.a    = finalColor.a + grayC.a * (1 - finalColor.a);
            if (finalColor.a > 0.95f)
                break;
        }
    }

    return finalColor;
}

//--------------------------------------------------------------------------------------
// HueFromLabel: Maps a label ID to a pseudo-random color
//--------------------------------------------------------------------------------------
float3 HueFromLabel(uint labelId)
{
    // e.g. labelId -> some pseudo-random hue in [0..360)
    float h = fmod((labelId * 37), 360);

    // Convert hue to RGB with fixed S=0.8, V=1.0
    float s = 0.8;
    float v = 1.0;
    float c = s * v;
    float x = c * (1 - abs(fmod(h / 60.0, 2.0) - 1));
    float3 rgb;

         if (h < 60)  rgb = float3(c, x, 0);
    else if (h < 120) rgb = float3(x, c, 0);
    else if (h < 180) rgb = float3(0, c, x);
    else if (h < 240) rgb = float3(0, x, c);
    else if (h < 300) rgb = float3(x, 0, c);
    else              rgb = float3(c, 0, x);

    float m = v - c;
    return rgb + m;
}

//--------------------------------------------------------------------------------------
// PSSlice: Renders a single slice of the volume in the XY, YZ, or XZ plane
//--------------------------------------------------------------------------------------
float4 PSSlice(VS_OUT input) : SV_TARGET
{
    float3 uvw = float3(
        input.posW.x / dims.x,
        input.posW.y / dims.y,
        input.posW.z / dims.z
    );

    float g = GrayTex.SampleLevel(samPoint, uvw, 0).r;

    // Discard if out of threshold range
    if (g < thresholds.x || g > thresholds.y)
        discard;

    return float4(g, g, g, 1);
}
";
    }


}
