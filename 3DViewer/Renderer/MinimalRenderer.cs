using System;
using System.Diagnostics;
using System.Windows.Forms;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;

namespace CTSegmenter.SharpDXIntegration
{
    public class MinimalRenderer : IDisposable
    {
        private Panel renderPanel;
        private SharpDX.Direct3D11.Device device;
        private DeviceContext context;
        private SwapChain swapChain;
        private RenderTargetView renderTarget;

        // For simple triangle
        private VertexShader vertexShader;

        private PixelShader pixelShader;
        private InputLayout inputLayout;
        private SharpDX.Direct3D11.Buffer vertexBuffer;

        public MinimalRenderer(Panel panel)
        {
            this.renderPanel = panel;
            CreateDeviceAndSwapchain();
            CreateShaders();
            CreateGeometry();
        }

        private void CreateDeviceAndSwapchain()
        {
            // Very basic SwapChain description
            var desc = new SwapChainDescription()
            {
                BufferCount = 1,
                ModeDescription = new ModeDescription(
                    renderPanel.ClientSize.Width,
                    renderPanel.ClientSize.Height,
                    new Rational(60, 1),
                    Format.R8G8B8A8_UNorm),
                IsWindowed = true,
                OutputHandle = renderPanel.Handle,
                SampleDescription = new SampleDescription(1, 0),
                SwapEffect = SwapEffect.Discard,
                Usage = Usage.RenderTargetOutput
            };

            // Create device and swap chain
            SharpDX.Direct3D11.Device.CreateWithSwapChain(
                DriverType.Hardware,
                DeviceCreationFlags.Debug,
                new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 },
                desc,
                out device,
                out swapChain);

            context = device.ImmediateContext;

            // Create render target view from the backbuffer
            using (var backBuffer = SharpDX.Direct3D11.Texture2D.FromSwapChain<SharpDX.Direct3D11.Texture2D>(swapChain, 0))
            {
                renderTarget = new RenderTargetView(device, backBuffer);
            }
        }

        private void CreateShaders()
        {
            // Very simple shaders that draw a colored triangle
            string shaderCode = @"
                struct VS_INPUT { float4 Pos : POSITION; };
                struct PS_INPUT { float4 Pos : SV_POSITION; float4 Col : COLOR; };

                PS_INPUT VSMain(VS_INPUT input)
                {
                    PS_INPUT output = (PS_INPUT)0;
                    output.Pos = input.Pos;

                    // Generate color based on position
                    output.Col = float4(
                        (input.Pos.x + 1.0) * 0.5,
                        (input.Pos.y + 1.0) * 0.5,
                        0.5,
                        1.0);

                    return output;
                }

                float4 PSMain(PS_INPUT input) : SV_Target
                {
                    return input.Col;
                }";

            try
            {
                // Compile shaders
                var vsResult = SharpDX.D3DCompiler.ShaderBytecode.Compile(
                    shaderCode, "VSMain", "vs_4_0", SharpDX.D3DCompiler.ShaderFlags.Debug);
                var psResult = SharpDX.D3DCompiler.ShaderBytecode.Compile(
                    shaderCode, "PSMain", "ps_4_0", SharpDX.D3DCompiler.ShaderFlags.Debug);

                // Check for errors
                if (vsResult.HasErrors)
                {
                    Debug.WriteLine("VS compilation failed: " + vsResult.Message);
                    Logger.Log("[MinimalRenderer] VS compilation failed: " + vsResult.Message);
                    return;
                }
                if (psResult.HasErrors)
                {
                    Debug.WriteLine("PS compilation failed: " + psResult.Message);
                    Logger.Log("[MinimalRenderer] PS compilation failed: " + psResult.Message);
                    return;
                }

                // Create shaders
                vertexShader = new VertexShader(device, vsResult);
                pixelShader = new PixelShader(device, psResult);

                // Create input layout
                var elements = new[] { new InputElement("POSITION", 0, Format.R32G32B32A32_Float, 0) };
                inputLayout = new InputLayout(device, vsResult, elements);

                Logger.Log("[MinimalRenderer] Shaders created successfully");
            }
            catch (Exception ex)
            {
                Logger.Log("[MinimalRenderer] Shader creation error: " + ex.Message);
            }
        }

        private void CreateGeometry()
        {
            // Create a simple triangle in normalized device coordinates
            // (-1,1) is top-left, (1,-1) is bottom-right of screen
            Vector4[] vertices = new Vector4[]
            {
                new Vector4(-0.5f, 0.5f, 0.5f, 1.0f),  // Top-left
                new Vector4(0.5f, 0.5f, 0.5f, 1.0f),   // Top-right
                new Vector4(0.0f, -0.5f, 0.5f, 1.0f)   // Bottom-center
            };

            // Create the vertex buffer
            vertexBuffer = SharpDX.Direct3D11.Buffer.Create(
                device,
                BindFlags.VertexBuffer,
                vertices);

            Logger.Log("[MinimalRenderer] Geometry created");
        }

        public void Render()
        {
            if (device == null || context == null) return;

            try
            {
                // Clear the render target to a bright color for visibility
                context.ClearRenderTargetView(renderTarget, new Color4(1.0f, 0.3f, 0.3f, 1.0f));

                // Set the viewport explicitly
                context.Rasterizer.SetViewport(0, 0, renderPanel.ClientSize.Width, renderPanel.ClientSize.Height);

                // Set the render target
                context.OutputMerger.SetRenderTargets(renderTarget);

                // Set the vertex buffer
                context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(vertexBuffer, Utilities.SizeOf<Vector4>(), 0));
                context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
                context.InputAssembler.InputLayout = inputLayout;

                // Set the shaders
                context.VertexShader.Set(vertexShader);
                context.PixelShader.Set(pixelShader);

                // Draw the triangle
                context.Draw(3, 0);

                // Present the frame
                swapChain.Present(0, PresentFlags.None);

                Logger.Log("[MinimalRenderer] Frame rendered");
            }
            catch (Exception ex)
            {
                Logger.Log("[MinimalRenderer] Render error: " + ex.Message);
            }
        }

        public void Dispose()
        {
            renderTarget?.Dispose();
            vertexBuffer?.Dispose();
            inputLayout?.Dispose();
            pixelShader?.Dispose();
            vertexShader?.Dispose();
            swapChain?.Dispose();
            context?.Dispose();
            device?.Dispose();
        }

        public void Resize()
        {
            if (swapChain == null) return;

            // Release the render target
            renderTarget?.Dispose();

            // Resize the swap chain
            swapChain.ResizeBuffers(
                1,
                renderPanel.ClientSize.Width,
                renderPanel.ClientSize.Height,
                Format.R8G8B8A8_UNorm,
                SwapChainFlags.None);

            // Recreate the render target
            using (var backBuffer = SharpDX.Direct3D11.Texture2D.FromSwapChain<SharpDX.Direct3D11.Texture2D>(swapChain, 0))
            {
                renderTarget = new RenderTargetView(device, backBuffer);
            }
        }
    }
}