// Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.D3DCompiler;
using Vortice.DXGI;
using Vortice.Mathematics;
using SharpGen.Runtime;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace CTS.D3D11
{
    [StructLayout(LayoutKind.Sequential)]
    public struct SceneConstants
    {
        public Matrix4x4 InverseViewProjection;
        public Vector4 CameraPosition;
        public Vector4 VolumeDimensions;
        public Vector4 ChunkDimensions;
        public Vector4 SliceInfo;
        public Vector4 CutInfo;
        public Vector4 ClippingPlane;
        public Vector2 ScreenDimensions;
        public Vector2 Threshold;
        public float StepSize;
        public float Quality;
        private float _pad1, _pad2;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MaterialGPU
    {
        public Vector4 Color;
        public Vector4 Settings; // x=Opacity, y=IsVisible, z=Min, w=Max
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ChunkInfoGPU
    {
        public int GpuSlotIndex; // Index in the GPU texture array
        public Vector3 _padding;
    }

    public class D3D11VolumeRenderer : IDisposable
    {
        #region Embedded HLSL Shaders
        private const string VertexShaderCode = @"
            struct VS_OUT
            {
                float4 pos : SV_POSITION;
                float3 screenPos : TEXCOORD0; 
            };

            VS_OUT main(uint id : SV_VertexID)
            {
                VS_OUT output;
                output.screenPos.x = (id == 1) ? 3.0 : -1.0;
                output.screenPos.y = (id == 2) ? -3.0 : 1.0;
                output.screenPos.z = 1.0;
                output.pos = float4(output.screenPos.xy, 1.0, 1.0);
                return output;
            }";

        private const string PixelShaderCode = @"
            cbuffer SceneConstants : register(b0)
            {
                matrix inverseViewProjection;
                float4 cameraPosition;
                float4 volumeDimensions;
                float4 chunkDimensions;
                float4 sliceInfo;
                float4 cutInfo;
                float4 clippingPlane;
                float2 screenDimensions;
                float2 threshold;
                float stepSize;
                float quality;
                float2 padding;
            };

            struct Material
            {
                float4 color;
                float4 settings;
            };
            StructuredBuffer<Material> materials : register(t0);

            struct ChunkInfo
            {
                int gpuSlotIndex;
                float3 padding;
            };
            StructuredBuffer<ChunkInfo> chunkInfos : register(t1);

            Texture2DArray<float> grayscaleAtlas : register(t2);
            Texture2DArray<uint>  labelAtlas     : register(t3);
            SamplerState linearSampler : register(s0);

            float4 main(float4 pos : SV_POSITION, float3 screenPos : TEXCOORD0) : SV_TARGET
            {
                // Ray setup
                float3 boxMin = float3(0, 0, 0);
                float3 boxMax = volumeDimensions.xyz;
                
                float4 clip = float4(pos.xy / screenDimensions * 2.0 - 1.0, 0.0, 1.0);
                clip.y = -clip.y;
                
                float4 world = mul(clip, inverseViewProjection);
                world /= world.w;
                
                float3 rayDir = normalize(world.xyz - cameraPosition.xyz);
                float3 rayOrigin = cameraPosition.xyz;

                // Box intersection
                float3 invDir = 1.0f / rayDir;
                float3 t0s = (boxMin - rayOrigin) * invDir;
                float3 t1s = (boxMax - rayOrigin) * invDir;
                float3 tsmaller = min(t0s, t1s);
                float3 tbigger = max(t0s, t1s);
                float tmin = max(tsmaller.x, max(tsmaller.y, tsmaller.z));
                float tmax = min(tbigger.x, min(tbigger.y, tbigger.z));
                
                if (tmin > tmax || tmax < 0) discard;
                tmin = max(tmin, 0.0);
                
                // Ray marching with aggressive early termination
                float4 finalColor = float4(0, 0, 0, 0);
                float t = tmin;
                
                // MUCH lower step count to prevent TDR
                int maxSteps = quality == 0 ? 32 : (quality == 1 ? 64 : 128);
                float baseStepSize = max(stepSize, (tmax - tmin) / (float)maxSteps) * 2.0; // Even larger steps
                
                int emptySteps = 0;
                
                [loop]
                for (int i = 0; i < maxSteps; i++)
                {
                    if (finalColor.a > 0.95) break;
                    if (t > tmax) break;
                    
                    float3 worldPos = rayOrigin + rayDir * t;
                    float3 uvw = worldPos / volumeDimensions.xyz;
                    
                    // Skip if outside volume
                    if (any(uvw < 0.0) || any(uvw > 1.0)) 
                    {
                        t += baseStepSize;
                        continue;
                    }

                    // Get chunk info
                    float3 voxelCoord = uvw * volumeDimensions.xyz;
                    int3 chunkCoord = int3(voxelCoord / chunkDimensions.x);
                    int chunkIndex = chunkCoord.z * (int)(chunkDimensions.y * chunkDimensions.z) + 
                                    chunkCoord.y * (int)chunkDimensions.y + chunkCoord.x;
                    
                    int totalChunks = (int)(chunkDimensions.y * chunkDimensions.z * chunkDimensions.w);
                    if (chunkIndex < 0 || chunkIndex >= totalChunks) 
                    {
                        t += baseStepSize;
                        continue;
                    }
                    
                    int gpuSlot = chunkInfos[chunkIndex].gpuSlotIndex;
                    if (gpuSlot < 0)
                    {
                        // Chunk not loaded - take larger step
                        t += baseStepSize * 4.0;
                        continue;
                    }
                    
                    // Sample volume
                    float3 localCoord = frac(voxelCoord / chunkDimensions.x);
                    float sliceIndex = gpuSlot * chunkDimensions.x + localCoord.z * (chunkDimensions.x - 1);
                    
                    float grayValue = grayscaleAtlas.SampleLevel(linearSampler, float3(localCoord.xy, sliceIndex), 0);
                    uint labelValue = labelAtlas.Load(int4(localCoord.xy * chunkDimensions.x, sliceIndex, 0)).r;
                    
                    float4 sampleColor = float4(0, 0, 0, 0);
                    
                    // Skip material 0 (exterior)
                    if (labelValue > 0 && labelValue < 256)
                    {
                        Material mat = materials[labelValue];
                        if (mat.settings.y > 0.5)
                        {
                            sampleColor = mat.color;
                            sampleColor.a = mat.settings.x;
                        }
                    }
                    else if (labelValue == 0 && grayValue * 255.0 > threshold.x && grayValue * 255.0 < threshold.y)
                    {
                        float opacity = 0.05;
                        sampleColor = float4(grayValue, grayValue, grayValue, opacity);
                    }
                    
                    // Accumulate color
                    if (sampleColor.a > 0.001)
                    {
                        float correctedAlpha = 1.0 - pow(1.0 - sampleColor.a, baseStepSize * 50.0);
                        finalColor.rgb += sampleColor.rgb * correctedAlpha * (1.0 - finalColor.a);
                        finalColor.a += correctedAlpha * (1.0 - finalColor.a);
                        emptySteps = 0;
                    }
                    else
                    {
                        emptySteps++;
                    }
                    
                    // Adaptive stepping based on empty space
                    float currentStepSize = (emptySteps > 2) ? baseStepSize * 4.0 : baseStepSize;
                    t += currentStepSize;
                }
                
                return finalColor;
            }";
        #endregion

        private ID3D11Device device;
        private ID3D11DeviceContext context;
        private IDXGISwapChain swapChain;
        private ID3D11RenderTargetView renderTargetView;

        private ID3D11VertexShader vertexShader;
        private ID3D11PixelShader pixelShader;
        private ID3D11SamplerState samplerState;

        private ID3D11Buffer sceneConstantBuffer;
        private ID3D11Buffer materialBuffer;
        private ID3D11Buffer chunkInfoBuffer;

        private int GpuCacheSize = 32; // Reduced from 64 to prevent memory issues

        private ID3D11Texture2D grayscaleTextureCache;
        private ID3D11ShaderResourceView grayscaleTextureSrv;
        private ID3D11Texture2D labelTextureCache;
        private ID3D11ShaderResourceView labelTextureSrv;

        private readonly MainForm mainForm;
        private readonly IGrayscaleVolumeData volumeData;
        private readonly ILabelVolumeData labelData;
        public readonly ChunkStreamingManager streamingManager;

        private SceneConstants sceneConstants;
        private bool isCameraMoving = false;
        public bool NeedsRender { get; set; } = true;

        private int totalChunks;
        private bool isInitialized = false;
        private bool deviceLost = false;

        public D3D11VolumeRenderer(IntPtr hwnd, int width, int height, MainForm mainForm)
        {
            this.mainForm = mainForm;
            this.volumeData = mainForm.volumeData;
            this.labelData = mainForm.volumeLabels;

            try
            {
                InitializeD3D11(hwnd, width, height);

                if (volumeData != null && labelData != null)
                {
                    totalChunks = volumeData.ChunkCountX * volumeData.ChunkCountY * volumeData.ChunkCountZ;

                    // Adjust cache size based on chunk dimensions to avoid exceeding texture array limits
                    int maxArraySize = 2048; // D3D11 limit
                    int chunkDim = volumeData.ChunkDim;
                    int maxCacheSize = maxArraySize / chunkDim;

                    // Use very conservative cache size to prevent memory issues
                    if (chunkDim >= 256)
                        GpuCacheSize = Math.Min(8, maxCacheSize);
                    else if (chunkDim >= 128)
                        GpuCacheSize = Math.Min(16, maxCacheSize);
                    else
                        GpuCacheSize = Math.Min(32, maxCacheSize);

                    Logger.Log($"[D3D11VolumeRenderer] Volume info: {volumeData.Width}x{volumeData.Height}x{volumeData.Depth}");
                    Logger.Log($"[D3D11VolumeRenderer] Chunk info: dim={chunkDim}, count={volumeData.ChunkCountX}x{volumeData.ChunkCountY}x{volumeData.ChunkCountZ}");
                    Logger.Log($"[D3D11VolumeRenderer] Using GPU cache size: {GpuCacheSize} chunks, {GpuCacheSize * chunkDim} total slices");

                    CreateGpuCache();
                    CreateShaders();
                    CreateConstantBuffers();
                    CreateSamplerState();

                    // Only create streaming manager after all resources are ready
                    streamingManager = new ChunkStreamingManager(device, context, volumeData, labelData, grayscaleTextureCache, labelTextureCache);
                }
                else
                {
                    Logger.Log("[D3D11VolumeRenderer] Warning: Volume data is null, creating minimal renderer");
                    totalChunks = 1;
                    CreateShaders();
                    CreateConstantBuffers();
                    CreateSamplerState();
                }

                UpdateMaterialsBuffer();

                // Initialize scene constants with default values
                sceneConstants.ScreenDimensions = new Vector2(width, height);
                sceneConstants.StepSize = 2.0f; // Start with larger steps
                sceneConstants.Quality = 0.0f; // Start with lowest quality
                sceneConstants.Threshold = new Vector2(30, 200);
                sceneConstants.VolumeDimensions = new Vector4(
                    volumeData?.Width ?? 1,
                    volumeData?.Height ?? 1,
                    volumeData?.Depth ?? 1,
                    0);
                sceneConstants.ChunkDimensions = new Vector4(
                    volumeData?.ChunkDim ?? 1,
                    volumeData?.ChunkCountX ?? 1,
                    volumeData?.ChunkCountY ?? 1,
                    volumeData?.ChunkCountZ ?? 1);

                // Mark as initialized only after everything is ready
                isInitialized = true;
                Logger.Log("[D3D11VolumeRenderer] Initialization complete");
            }
            catch (Exception ex)
            {
                Logger.Log($"[D3D11VolumeRenderer] Initialization error: {ex.Message}");
                Dispose();
                throw;
            }
        }

        private void InitializeD3D11(IntPtr hwnd, int width, int height)
        {
            var swapChainDesc = new SwapChainDescription
            {
                BufferCount = 2,
                BufferDescription = new ModeDescription(width, height, Format.R8G8B8A8_UNorm),
                Windowed = true,
                BufferUsage = Usage.RenderTargetOutput,
                OutputWindow = hwnd,
                SampleDescription = new SampleDescription(1, 0),
                SwapEffect = SwapEffect.FlipDiscard,
                Flags = SwapChainFlags.None
            };

            var flags = DeviceCreationFlags.None;
#if DEBUG
            flags |= DeviceCreationFlags.Debug;
#endif

            Vortice.Direct3D11.D3D11.D3D11CreateDeviceAndSwapChain(
                null,
                DriverType.Hardware,
                flags,
                null,
                swapChainDesc,
                out swapChain,
                out device,
                out _,
                out context);

            CreateRenderTargetView();
        }

        private void CreateRenderTargetView()
        {
            renderTargetView?.Dispose();
            using (var backBuffer = swapChain.GetBuffer<ID3D11Texture2D>(0))
            {
                renderTargetView = device.CreateRenderTargetView(backBuffer);
            }
        }

        private void CreateShaders()
        {
            var vsByteCode = Compiler.Compile(VertexShaderCode, "main", string.Empty, "vs_5_0", ShaderFlags.None, EffectFlags.None);
            vertexShader = device.CreateVertexShader(vsByteCode.Span);
            var psByteCode = Compiler.Compile(PixelShaderCode, "main", string.Empty, "ps_5_0", ShaderFlags.None, EffectFlags.None);
            pixelShader = device.CreatePixelShader(psByteCode.Span);
        }

        private void CreateConstantBuffers()
        {
            sceneConstantBuffer = device.CreateBuffer(new BufferDescription(
                Marshal.SizeOf<SceneConstants>(),
                BindFlags.ConstantBuffer,
                ResourceUsage.Dynamic,
                CpuAccessFlags.Write));

            // Ensure we have at least 256 materials (common requirement)
            int materialCount = Math.Max(256, mainForm.Materials.Count);
            int materialBufferSize = materialCount * Marshal.SizeOf<MaterialGPU>();

            materialBuffer = device.CreateBuffer(new BufferDescription(
                materialBufferSize,
                BindFlags.ShaderResource,
                ResourceUsage.Dynamic,
                CpuAccessFlags.Write,
                ResourceOptionFlags.BufferStructured,
                Marshal.SizeOf<MaterialGPU>()));

            int chunkBufferSize = totalChunks * Marshal.SizeOf<ChunkInfoGPU>();
            chunkInfoBuffer = device.CreateBuffer(new BufferDescription(
                chunkBufferSize,
                BindFlags.ShaderResource,
                ResourceUsage.Dynamic,
                CpuAccessFlags.Write,
                ResourceOptionFlags.BufferStructured,
                Marshal.SizeOf<ChunkInfoGPU>()));
        }

        private void CreateSamplerState()
        {
            samplerState = device.CreateSamplerState(new SamplerDescription(
                Filter.MinMagMipLinear,
                TextureAddressMode.Clamp,
                TextureAddressMode.Clamp,
                TextureAddressMode.Clamp,
                0, 0,
                ComparisonFunction.Never,
                new Color4(0, 0, 0, 0),
                0, 0));
        }

        private void CreateGpuCache()
        {
            int chunkDim = volumeData.ChunkDim;
            int totalSlices = GpuCacheSize * chunkDim;

            Logger.Log($"[CreateGpuCache] Creating texture arrays: {chunkDim}x{chunkDim}x{totalSlices}");

            // Grayscale texture array
            var desc = new Texture2DDescription
            {
                Width = chunkDim,
                Height = chunkDim,
                MipLevels = 1,
                ArraySize = totalSlices,
                Format = Format.R8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None
            };

            grayscaleTextureCache = device.CreateTexture2D(desc);
            grayscaleTextureSrv = device.CreateShaderResourceView(grayscaleTextureCache);

            // Label texture array
            desc.Format = Format.R8_UInt;
            labelTextureCache = device.CreateTexture2D(desc);
            labelTextureSrv = device.CreateShaderResourceView(labelTextureCache);

            Logger.Log($"[CreateGpuCache] Successfully created GPU cache textures");
        }

        public void UpdateMaterialsBuffer()
        {
            if (materialBuffer == null) return;

            // Prepare material data - ensure we have at least 256 entries
            int materialCount = Math.Max(256, mainForm.Materials.Count);
            var materialData = new MaterialGPU[materialCount];

            // Initialize all materials to default (invisible)
            for (int i = 0; i < materialCount; i++)
            {
                materialData[i] = new MaterialGPU
                {
                    Color = new Vector4(1, 1, 1, 1),
                    Settings = new Vector4(0, 0, 0, 255) // opacity=0, visible=0
                };
            }

            // Fill in actual material data
            for (int i = 0; i < mainForm.Materials.Count; i++)
            {
                var mat = mainForm.Materials[i];
                materialData[i] = mat.ToGPU();

                // Ensure material 0 (exterior) is always invisible
                if (i == 0)
                {
                    materialData[i].Settings.Y = 0; // Force invisible
                }
            }

            // Update GPU buffer
            var mapped = context.Map(materialBuffer, 0, MapMode.WriteDiscard);
            unsafe
            {
                var ptr = (MaterialGPU*)mapped.DataPointer.ToPointer();
                for (int i = 0; i < materialCount; i++)
                {
                    ptr[i] = materialData[i];
                }
            }
            context.Unmap(materialBuffer, 0);

            NeedsRender = true;
        }

        public void Render(Camera camera)
        {
            if (!isInitialized) return;
            if (streamingManager == null || device == null || context == null) return;
            if (renderTargetView == null || swapChain == null) return;

            try
            {
                streamingManager.Update(camera);
                if (!NeedsRender && !streamingManager.IsDirty()) return;

                // Check if device is lost
                if (deviceLost)
                {
                    Logger.Log("[Render] Device lost, skipping render");
                    return;
                }

                // Set render target and viewport
                context.OMSetRenderTargets(renderTargetView);
                context.ClearRenderTargetView(renderTargetView, new Color4(0.1f, 0.1f, 0.1f, 1));
                context.RSSetViewports(new[] { new Viewport(0, 0, sceneConstants.ScreenDimensions.X, sceneConstants.ScreenDimensions.Y) });

                // Update view/projection matrix
                Matrix4x4.Invert(camera.ViewMatrix * camera.ProjectionMatrix, out sceneConstants.InverseViewProjection);
                sceneConstants.InverseViewProjection = Matrix4x4.Transpose(sceneConstants.InverseViewProjection);
                sceneConstants.CameraPosition = new Vector4(camera.Position, 1);

                // Update constant buffer
                var mapped = context.Map(sceneConstantBuffer, 0, MapMode.WriteDiscard);
                unsafe { *(SceneConstants*)mapped.DataPointer.ToPointer() = sceneConstants; }
                context.Unmap(sceneConstantBuffer, 0);

                // Update chunk info buffer
                var chunkData = streamingManager.GetGpuChunkInfo();
                mapped = context.Map(chunkInfoBuffer, 0, MapMode.WriteDiscard);
                unsafe
                {
                    var ptr = (ChunkInfoGPU*)mapped.DataPointer.ToPointer();
                    for (int i = 0; i < chunkData.Length; i++)
                    {
                        ptr[i] = chunkData[i];
                    }
                }
                context.Unmap(chunkInfoBuffer, 0);

                // Create SRVs for buffers
                ID3D11ShaderResourceView chunkInfoSrv = null;
                ID3D11ShaderResourceView materialSrv = null;

                try
                {
                    chunkInfoSrv = device.CreateShaderResourceView(chunkInfoBuffer);
                    materialSrv = device.CreateShaderResourceView(materialBuffer);

                    // Set pipeline state
                    context.VSSetShader(vertexShader);
                    context.PSSetShader(pixelShader);
                    context.PSSetConstantBuffers(0, new[] { sceneConstantBuffer });
                    context.PSSetSamplers(0, new[] { samplerState });

                    // Set shader resources
                    var srvs = new[] { materialSrv, chunkInfoSrv, grayscaleTextureSrv, labelTextureSrv };
                    context.PSSetShaderResources(0, srvs);

                    // Draw fullscreen triangle
                    context.IASetInputLayout(null);
                    context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                    context.Draw(3, 0);
                }
                finally
                {
                    // Clear ALL pipeline state before present
                    context.PSSetShaderResources(0, new ID3D11ShaderResourceView[] { null, null, null, null });
                    context.PSSetShader(null);
                    context.VSSetShader(null);
                    context.PSSetConstantBuffers(0, new ID3D11Buffer[] { null });
                    context.PSSetSamplers(0, new ID3D11SamplerState[] { null });
                    
                    
                    
                    context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>(), null);
                   

                    // Clean up SRVs
                    chunkInfoSrv?.Dispose();
                    materialSrv?.Dispose();
                }

                // Present
                swapChain.Present(1, PresentFlags.None);
                NeedsRender = false;
            }
            catch (SharpGenException sgex) when ((uint)sgex.HResult == 0x887A0005) // DXGI_ERROR_DEVICE_REMOVED
            {
                deviceLost = true;
                Logger.Log($"[Render] Device removed detected. Reason: {device.DeviceRemovedReason.Code.ToString("X")}");

                // Reduce quality to prevent future TDRs
                sceneConstants.Quality = 0;
                sceneConstants.StepSize = 8.0f;

                // Try to recreate device on next frame
                // For now, just prevent further rendering
            }
            catch (Exception ex)
            {
                Logger.Log($"[Render] Error during rendering: {ex.Message}");
                // Don't throw - just log and continue
            }
        }

        public void Resize(int width, int height)
        {
            if (device == null || width <= 0 || height <= 0) return;

            renderTargetView?.Dispose();
            swapChain.ResizeBuffers(2, width, height, Format.R8G8B8A8_UNorm, SwapChainFlags.None);
            CreateRenderTargetView();
            sceneConstants.ScreenDimensions = new Vector2(width, height);
            NeedsRender = true;
        }

        public void SetIsCameraMoving(bool moving)
        {
            if (isCameraMoving != moving)
            {
                isCameraMoving = moving;
                sceneConstants.StepSize = moving ? 4.0f : 2.0f; // Much larger steps to prevent TDR
                NeedsRender = true;
            }
        }

        public void SetRenderParams(RenderParameters p)
        {
            sceneConstants.Threshold = p.Threshold;
            sceneConstants.Quality = p.Quality;
            sceneConstants.ClippingPlane = p.ClippingPlane;
            sceneConstants.SliceInfo = p.SliceInfo;
            sceneConstants.CutInfo = p.CutInfo;
            sceneConstants.VolumeDimensions = new Vector4(mainForm.GetWidth(), mainForm.GetHeight(), mainForm.GetDepth(), 0);
            sceneConstants.ChunkDimensions = new Vector4(volumeData.ChunkDim, volumeData.ChunkCountX, volumeData.ChunkCountY, volumeData.ChunkCountZ);

            // Ensure step size is never too small to prevent TDR
            if (sceneConstants.StepSize < 1.0f)
                sceneConstants.StepSize = 2.0f;

            NeedsRender = true;
        }

        public void Dispose()
        {
            isInitialized = false;

            // Clear any bound resources first
            if (context != null)
            {
                try
                {
                    context.ClearState();
                    context.Flush();
                }
                catch { }
            }

            // Dispose resources in reverse order of creation
            streamingManager?.Dispose();

            grayscaleTextureSrv?.Dispose();
            labelTextureSrv?.Dispose();
            grayscaleTextureCache?.Dispose();
            labelTextureCache?.Dispose();

            chunkInfoBuffer?.Dispose();
            materialBuffer?.Dispose();
            sceneConstantBuffer?.Dispose();

            samplerState?.Dispose();
            pixelShader?.Dispose();
            vertexShader?.Dispose();

            renderTargetView?.Dispose();

            // Dispose swap chain and device last
            if (swapChain != null)
            {
                try
                {
                    swapChain.SetFullscreenState(false, null);
                    swapChain.Dispose();
                }
                catch { }
            }

            context?.Dispose();
            device?.Dispose();
        }
    }
}