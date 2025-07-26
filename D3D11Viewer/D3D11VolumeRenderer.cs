// Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
using System;
using System.Collections.Generic;
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
using Matrix4x4 = System.Numerics.Matrix4x4;
using Color4 = Vortice.Mathematics.Color4;

namespace CTS.D3D11
{
    public struct RenderParameters
    {
        public Vector2 Threshold;
        public float Quality;
        public float ShowGrayscale;
        public float ShowScaleBar;
        public int ScaleBarPosition;
        public Vector3 SlicePositions; // X, Y, Z slice positions (0-1 range, -1 = disabled)
        public List<Vector4> ClippingPlanes;

        // New scale bar parameters
        public float ShowScaleText;
        public float ScaleBarLength; // in mm
        public float PixelSize;      // mm per pixel
    }


    [StructLayout(LayoutKind.Sequential)]
    public struct SceneConstants
    {
        public Matrix4x4 InverseViewProjection;
        public Vector4 CameraPosition;
        public Vector4 VolumeDimensions;
        public Vector4 ChunkDimensions;
        public Vector4 SliceInfo; // xyz = slice positions (0-1), w = slice mode

        // Clipping planes (up to 8)
        public Vector4 ClippingPlane0;
        public Vector4 ClippingPlane1;
        public Vector4 ClippingPlane2;
        public Vector4 ClippingPlane3;
        public Vector4 ClippingPlane4;
        public Vector4 ClippingPlane5;
        public Vector4 ClippingPlane6;
        public Vector4 ClippingPlane7;

        public Vector2 ScreenDimensions;
        public Vector2 Threshold;
        public float StepSize;
        public float Quality;
        public float ShowGrayscale;
        public float ShowScaleBar;
        public float ScaleBarPosition;
        public float NumClippingPlanes;
        public float MaxTextureSlices;
        public float ShowScaleText;
        public float ScaleBarLength;    // in mm
        public float PixelSize;         // mm per pixel
        public Vector2 _padding;
    }


    [StructLayout(LayoutKind.Sequential)]
    public struct MaterialGPU
    {
        public Vector4 Color;
        public Vector4 Settings;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ChunkInfoGPU
    {
        public int GpuSlotIndex;
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
                float4 sliceInfo; // xyz = slice positions, w = mode
                
                // Clipping planes
                float4 clippingPlanes[8];
                
                float2 screenDimensions;
                float2 threshold;
                float stepSize;
                float quality;
                float showGrayscale;
                float showScaleBar;
                float scaleBarPosition;
                float numClippingPlanes;
                float maxTextureSlices; // ADDED: Maximum texture array slices
                float showScaleText;
                float scaleBarLength;    // in mm
                float pixelSize;         // mm per pixel
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

            // Sample volume at a specific position
            float4 sampleVolume(float3 worldPos)
            {
                float3 uvw = worldPos / volumeDimensions.xyz;
                if (any(uvw < 0.0) || any(uvw > 1.0)) 
                    return float4(0, 0, 0, 0);
                
                float3 voxelCoord = uvw * volumeDimensions.xyz;
                int3 chunkCoord = int3(voxelCoord / chunkDimensions.x);
                
                // FIXED: Ensure chunk coordinates are within bounds
                if (any(chunkCoord < 0) || chunkCoord.x >= (int)chunkDimensions.y || 
                    chunkCoord.y >= (int)chunkDimensions.z || chunkCoord.z >= (int)chunkDimensions.w)
                    return float4(0, 0, 0, 0);
                
                int chunkIndex = chunkCoord.z * (int)(chunkDimensions.y * chunkDimensions.z) + 
                                chunkCoord.y * (int)chunkDimensions.y + chunkCoord.x;
                
                int totalChunks = (int)(chunkDimensions.y * chunkDimensions.z * chunkDimensions.w);
                if (chunkIndex < 0 || chunkIndex >= totalChunks) 
                    return float4(0, 0, 0, 0);
                
                int gpuSlot = chunkInfos[chunkIndex].gpuSlotIndex;
                if (gpuSlot < 0)
                    return float4(0, 0, 0, 0);
                
                float3 localCoord = frac(voxelCoord / chunkDimensions.x);
                
                // FIXED: Calculate slice index more carefully to avoid overflow
                float sliceZ = localCoord.z * (chunkDimensions.x - 1);
                float sliceIndex = gpuSlot * chunkDimensions.x + sliceZ;
                
                // FIXED: Ensure we don't exceed texture array bounds
                if (sliceIndex >= maxTextureSlices)
                    return float4(0, 0, 0, 0);
                
                float grayValue = grayscaleAtlas.SampleLevel(linearSampler, float3(localCoord.xy, sliceIndex), 0);
                
                // FIXED: Use Load with bounds checking
                int3 loadCoords = int3(localCoord.xy * chunkDimensions.x, sliceIndex);
                if (loadCoords.z >= maxTextureSlices)
                    return float4(0, 0, 0, 0);
                    
                uint labelValue = labelAtlas.Load(int4(loadCoords, 0)).r;
                
                float4 sampleColor = float4(0, 0, 0, 0);
                
                if (labelValue > 0 && labelValue < 256)
                {
                    Material mat = materials[labelValue];
                    if (mat.settings.y > 0.5)
                    {
                        sampleColor = mat.color;
                        sampleColor.a = mat.settings.x;
                    }
                }
                else if (labelValue == 0 && showGrayscale > 0.5)
                {
                    float grayInt = grayValue * 255.0;
                    if (grayInt > threshold.x && grayInt < threshold.y)
                    {
                        sampleColor = float4(grayValue, grayValue, grayValue, 1.0);
                    }
                }
                
                return sampleColor;
            }

            // Check if point is clipped
            bool isClipped(float3 worldPos)
            {
                for (int i = 0; i < (int)numClippingPlanes; i++)
                {
                    float4 plane = clippingPlanes[i];
                    float distance = dot(worldPos - volumeDimensions.xyz * 0.5, plane.xyz) - plane.w;
                    if (distance > 0) return true;
                }
                return false;
            }

            // Draw scale bar
            float4 drawScaleBar(float2 uv)
{
    if (showScaleBar < 0.5) return float4(0, 0, 0, 0);
    
    // Calculate bar dimensions based on desired length in mm
    float pixelsPerMM = 1.0 / pixelSize; // pixels per mm
    float barLengthPixels = scaleBarLength * pixelsPerMM;
    float barLengthNorm = barLengthPixels / screenDimensions.x; // normalized length
    float barHeight = 0.008;
    
    float2 barPos;
    
    // Fixed positioning: now matches UI expectations
    if (scaleBarPosition < 0.5)
        barPos = float2(0.05, 0.94); // Bottom Left
    else if (scaleBarPosition < 1.5)
        barPos = float2(0.95 - barLengthNorm, 0.94); // Bottom Right
    else if (scaleBarPosition < 2.5)
        barPos = float2(0.05, 0.05); // Top Left
    else
        barPos = float2(0.95 - barLengthNorm, 0.05); // Top Right
    
    // Draw the scale bar
    if (uv.x >= barPos.x && uv.x <= barPos.x + barLengthNorm &&
        uv.y >= barPos.y && uv.y <= barPos.y + barHeight)
    {
        // Create alternating segments (5 segments)
        float segmentSize = barLengthNorm / 5.0;
        int segment = (int)((uv.x - barPos.x) / segmentSize);
        float3 color = (segment % 2 == 0) ? float3(1, 1, 1) : float3(0, 0, 0);
        
        // Add border
        float borderDist = min(
            min(uv.x - barPos.x, barPos.x + barLengthNorm - uv.x),
            min(uv.y - barPos.y, barPos.y + barHeight - uv.y)
        );
        if (borderDist < 0.0015) color = float3(0.5, 0.5, 0.5);
        
        return float4(color, 1.0);
    }
    
    // Draw text background if enabled
    if (showScaleText > 0.5)
    {
        float textHeight = 0.025;
        float textWidth = barLengthNorm + 0.02;
        float textY = (scaleBarPosition < 2.0) ? barPos.y - textHeight - 0.01 : barPos.y + barHeight + 0.005;
        
        if (uv.x >= barPos.x - 0.01 && uv.x <= barPos.x + textWidth &&
            uv.y >= textY && uv.y <= textY + textHeight)
        {
            // Semi-transparent background for text
            return float4(0, 0, 0, 0.7);
        }
        
        // Simple text rendering (approximation - shows length value)
        // In a real implementation, you'd use a texture atlas for proper text
        float textCenterX = barPos.x + barLengthNorm * 0.5;
        float textCenterY = textY + textHeight * 0.5;
        float distToCenter = length(float2(uv.x - textCenterX, uv.y - textCenterY));
        
        // Draw a simple indicator for the scale length
        if (distToCenter < 0.003)
        {
            return float4(1, 1, 1, 1);
        }
    }
    
    return float4(0, 0, 0, 0);
}

            // Check if we're on a slice plane
            float4 checkSlice(float3 worldPos)
            {
                float3 uvw = worldPos / volumeDimensions.xyz;
                float sliceThickness = 0.5 / min(volumeDimensions.x, min(volumeDimensions.y, volumeDimensions.z));
                
                // X slice
                if (sliceInfo.x >= 0)
                {
                    if (abs(uvw.x - sliceInfo.x) < sliceThickness)
                    {
                        float3 slicePos = float3(sliceInfo.x * volumeDimensions.x, worldPos.y, worldPos.z);
                        float4 color = sampleVolume(slicePos);
                        if (color.a > 0) return color;
                    }
                }
                
                // Y slice
                if (sliceInfo.y >= 0)
                {
                    if (abs(uvw.y - sliceInfo.y) < sliceThickness)
                    {
                        float3 slicePos = float3(worldPos.x, sliceInfo.y * volumeDimensions.y, worldPos.z);
                        float4 color = sampleVolume(slicePos);
                        if (color.a > 0) return color;
                    }
                }
                
                // Z slice
                if (sliceInfo.z >= 0)
                {
                    if (abs(uvw.z - sliceInfo.z) < sliceThickness)
                    {
                        float3 slicePos = float3(worldPos.x, worldPos.y, sliceInfo.z * volumeDimensions.z);
                        float4 color = sampleVolume(slicePos);
                        if (color.a > 0) return color;
                    }
                }
                
                return float4(0, 0, 0, 0);
            }

            float4 main(float4 pos : SV_POSITION, float3 screenPos : TEXCOORD0) : SV_TARGET
            {
                float2 uv = pos.xy / screenDimensions;
                float4 scaleBarColor = drawScaleBar(uv);
                if (scaleBarColor.a > 0) return scaleBarColor;
                
                float3 boxMin = float3(0, 0, 0);
                float3 boxMax = volumeDimensions.xyz;
                
                float4 clip = float4(pos.xy / screenDimensions * 2.0 - 1.0, 0.0, 1.0);
                clip.y = -clip.y;
                
                float4 world = mul(clip, inverseViewProjection);
                world /= world.w;
                
                float3 rayDir = normalize(world.xyz - cameraPosition.xyz);
                float3 rayOrigin = cameraPosition.xyz;

                float3 invDir = 1.0f / rayDir;
                float3 t0s = (boxMin - rayOrigin) * invDir;
                float3 t1s = (boxMax - rayOrigin) * invDir;
                float3 tsmaller = min(t0s, t1s);
                float3 tbigger = max(t0s, t1s);
                float tmin = max(tsmaller.x, max(tsmaller.y, tsmaller.z));
                float tmax = min(tbigger.x, min(tbigger.y, tbigger.z));
                
                if (tmin > tmax || tmax < 0) discard;
                tmin = max(tmin, 0.0);
                
                float4 finalColor = float4(0, 0, 0, 0);
                float t = tmin;
                
                int maxSteps = quality == 0 ? 100 : (quality == 1 ? 200 : 400);
                float baseStepSize = max(stepSize, (tmax - tmin) / (float)maxSteps);
                
                int emptySteps = 0;
                
                [loop]
                for (int i = 0; i < maxSteps; i++)
                {
                    if (finalColor.a > 0.98) break;
                    if (t > tmax) break;
                    
                    float3 worldPos = rayOrigin + rayDir * t;
                    
                    // Check slices first
                    float4 sliceColor = checkSlice(worldPos);
                    if (sliceColor.a > 0)
                    {
                        finalColor = sliceColor;
                        break; // Slices are opaque
                    }
                    
                    if (isClipped(worldPos))
                    {
                        t += baseStepSize;
                        continue;
                    }
                    
                    float4 sampleColor = sampleVolume(worldPos);
                    
                    if (sampleColor.a > 0.001)
                    {
                        // Reduce opacity for grayscale volume rendering
                        if (sampleColor.r == sampleColor.g && sampleColor.r == sampleColor.b)
                        {
                            sampleColor.a *= 0.05;
                        }
                        
                        float correctedAlpha = 1.0 - pow(1.0 - sampleColor.a, baseStepSize * 50.0);
                        finalColor.rgb += sampleColor.rgb * correctedAlpha * (1.0 - finalColor.a);
                        finalColor.a += correctedAlpha * (1.0 - finalColor.a);
                        emptySteps = 0;
                    }
                    else
                    {
                        emptySteps++;
                    }
                    
                    float currentStepSize = (emptySteps > 2) ? baseStepSize * 4.0 : baseStepSize;
                    t += currentStepSize;
                }
                
                return finalColor;
            }";
        private const string UpdatedPixelShaderScaleBarFunction = @"
// Draw scale bar with text
float4 drawScaleBar(float2 uv)
{
    if (showScaleBar < 0.5) return float4(0, 0, 0, 0);
    
    // Calculate bar dimensions based on desired length in mm
    float pixelsPerMM = 1.0 / pixelSize; // pixels per mm
    float barLengthPixels = scaleBarLength * pixelsPerMM;
    float barLengthNorm = barLengthPixels / screenDimensions.x; // normalized length
    float barHeight = 0.008;
    
    float2 barPos;
    
    // Fixed positioning: now matches UI expectations
    if (scaleBarPosition < 0.5)
        barPos = float2(0.05, 0.94); // Bottom Left
    else if (scaleBarPosition < 1.5)
        barPos = float2(0.95 - barLengthNorm, 0.94); // Bottom Right
    else if (scaleBarPosition < 2.5)
        barPos = float2(0.05, 0.05); // Top Left
    else
        barPos = float2(0.95 - barLengthNorm, 0.05); // Top Right
    
    // Draw the scale bar
    if (uv.x >= barPos.x && uv.x <= barPos.x + barLengthNorm &&
        uv.y >= barPos.y && uv.y <= barPos.y + barHeight)
    {
        // Create alternating segments (5 segments)
        float segmentSize = barLengthNorm / 5.0;
        int segment = (int)((uv.x - barPos.x) / segmentSize);
        float3 color = (segment % 2 == 0) ? float3(1, 1, 1) : float3(0, 0, 0);
        
        // Add border
        float borderDist = min(
            min(uv.x - barPos.x, barPos.x + barLengthNorm - uv.x),
            min(uv.y - barPos.y, barPos.y + barHeight - uv.y)
        );
        if (borderDist < 0.0015) color = float3(0.5, 0.5, 0.5);
        
        return float4(color, 1.0);
    }
    
    // Draw text background if enabled
    if (showScaleText > 0.5)
    {
        float textHeight = 0.025;
        float textWidth = barLengthNorm + 0.02;
        float textY = (scaleBarPosition < 2.0) ? barPos.y - textHeight - 0.01 : barPos.y + barHeight + 0.005;
        
        if (uv.x >= barPos.x - 0.01 && uv.x <= barPos.x + textWidth &&
            uv.y >= textY && uv.y <= textY + textHeight)
        {
            // Semi-transparent background for text
            return float4(0, 0, 0, 0.7);
        }
        
        // Simple text rendering (approximation - shows length value)
        // In a real implementation, you'd use a texture atlas for proper text
        float textCenterX = barPos.x + barLengthNorm * 0.5;
        float textCenterY = textY + textHeight * 0.5;
        float distToCenter = length(float2(uv.x - textCenterX, uv.y - textCenterY));
        
        // Draw a simple indicator for the scale length
        if (distToCenter < 0.003)
        {
            return float4(1, 1, 1, 1);
        }
    }
    
    return float4(0, 0, 0, 0);
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

        private int GpuCacheSize = 32;
        private int totalTextureSlices = 0;  // ADDED: Track total texture slices

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
        private volatile bool _isDisposed = false;
        public bool IsDisposed => _isDisposed;
        private Color4 backgroundColor = new Color4(0.1f, 0.1f, 0.1f, 1.0f);
        private readonly object renderLock = new object();

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

                    int maxArraySize = 2048;
                    int chunkDim = volumeData.ChunkDim;
                    int maxCacheSize = maxArraySize / chunkDim;

                    // FIXED: Ensure GPU cache size doesn't exceed texture limits
                    if (chunkDim >= 256)
                        GpuCacheSize = Math.Min(8, Math.Min(maxCacheSize, totalChunks));
                    else if (chunkDim >= 128)
                        GpuCacheSize = Math.Min(16, Math.Min(maxCacheSize, totalChunks));
                    else
                        GpuCacheSize = Math.Min(32, Math.Min(maxCacheSize, totalChunks));

                    totalTextureSlices = GpuCacheSize * chunkDim;  // ADDED: Calculate total slices

                    Logger.Log($"[D3D11VolumeRenderer] Volume info: {volumeData.Width}x{volumeData.Height}x{volumeData.Depth}");
                    Logger.Log($"[D3D11VolumeRenderer] Chunk info: dim={chunkDim}, count={volumeData.ChunkCountX}x{volumeData.ChunkCountY}x{volumeData.ChunkCountZ}");
                    Logger.Log($"[D3D11VolumeRenderer] Using GPU cache size: {GpuCacheSize} chunks, {totalTextureSlices} total texture slices");

                    CreateGpuCache();
                    CreateShaders();
                    CreateConstantBuffers();
                    CreateSamplerState();

                    streamingManager = new ChunkStreamingManager(device, context, volumeData, labelData, grayscaleTextureCache, labelTextureCache);
                }
                else
                {
                    Logger.Log("[D3D11VolumeRenderer] Warning: Volume data is null");
                    totalChunks = 1;
                    CreateShaders();
                    CreateConstantBuffers();
                    CreateSamplerState();
                }

                UpdateMaterialsBuffer();

                // Initialize scene constants
                sceneConstants.ScreenDimensions = new Vector2(width, height);
                sceneConstants.StepSize = 2.0f;
                sceneConstants.Quality = 0.0f;
                sceneConstants.Threshold = new Vector2(30, 200);
                sceneConstants.ShowGrayscale = 1.0f;
                sceneConstants.ShowScaleBar = 1.0f;
                sceneConstants.ScaleBarPosition = 0.0f;
                sceneConstants.NumClippingPlanes = 0.0f;
                sceneConstants.MaxTextureSlices = totalTextureSlices;  // ADDED: Set max texture slices
                sceneConstants.SliceInfo = new Vector4(-1, -1, -1, 0); // All slices disabled
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

            int materialCount = Math.Max(256, mainForm.Materials.Count);
            int materialBufferSize = materialCount * Marshal.SizeOf<MaterialGPU>();

            materialBuffer = device.CreateBuffer(new BufferDescription(
                materialBufferSize,
                BindFlags.ShaderResource,
                ResourceUsage.Dynamic,
                CpuAccessFlags.Write,
                ResourceOptionFlags.BufferStructured,
                Marshal.SizeOf<MaterialGPU>()));

            int chunkBufferSize = Math.Max(1, totalChunks) * Marshal.SizeOf<ChunkInfoGPU>();  // FIXED: Ensure at least 1
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

            desc.Format = Format.R8_UInt;
            labelTextureCache = device.CreateTexture2D(desc);
            labelTextureSrv = device.CreateShaderResourceView(labelTextureCache);

            Logger.Log($"[CreateGpuCache] Successfully created GPU cache textures");
        }

        public void UpdateMaterialsBuffer()
        {
            if (materialBuffer == null || _isDisposed) return;

            lock (renderLock)
            {
                if (_isDisposed) return;

                int materialCount = Math.Max(256, mainForm.Materials.Count);
                var materialData = new MaterialGPU[materialCount];

                for (int i = 0; i < materialCount; i++)
                {
                    materialData[i] = new MaterialGPU
                    {
                        Color = new Vector4(1, 1, 1, 1),
                        Settings = new Vector4(0, 0, 0, 255)
                    };
                }

                for (int i = 0; i < mainForm.Materials.Count && i < materialCount; i++)
                {
                    var mat = mainForm.Materials[i];
                    materialData[i] = mat.ToGPU();

                    if (i == 0)
                    {
                        materialData[i].Settings.Y = 0;
                    }
                }

                try
                {
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
                catch (Exception ex)
                {
                    Logger.Log($"[UpdateMaterialsBuffer] Error: {ex.Message}");
                }
            }
        }

        public void SetBackgroundColor(System.Drawing.Color color)
        {
            if (_isDisposed) return;

            backgroundColor = new Color4(color.R / 255.0f, color.G / 255.0f, color.B / 255.0f, 1.0f);
            NeedsRender = true;
        }

        public void Render(Camera camera)
        {
            if (!isInitialized || _isDisposed) return;
            if (streamingManager == null || device == null || context == null) return;
            if (renderTargetView == null || swapChain == null) return;

            lock (renderLock)
            {
                if (_isDisposed) return;

                try
                {
                    streamingManager.Update(camera);
                    if (!NeedsRender && !streamingManager.IsDirty()) return;

                    if (deviceLost)
                    {
                        Logger.Log("[Render] Device lost, skipping render");
                        return;
                    }

                    context.OMSetRenderTargets(renderTargetView);
                    context.ClearRenderTargetView(renderTargetView, backgroundColor);
                    context.RSSetViewports(new[] { new Viewport(0, 0, sceneConstants.ScreenDimensions.X, sceneConstants.ScreenDimensions.Y) });

                    Matrix4x4 viewProjMatrix = camera.ViewMatrix * camera.ProjectionMatrix;
                    if (!Matrix4x4.Invert(viewProjMatrix, out sceneConstants.InverseViewProjection))
                    {
                        Logger.Log("[Render] Failed to invert view projection matrix");
                        return;
                    }
                    sceneConstants.InverseViewProjection = Matrix4x4.Transpose(sceneConstants.InverseViewProjection);
                    sceneConstants.CameraPosition = new Vector4(camera.Position, 1);

                    var mapped = context.Map(sceneConstantBuffer, 0, MapMode.WriteDiscard);
                    unsafe { *(SceneConstants*)mapped.DataPointer.ToPointer() = sceneConstants; }
                    context.Unmap(sceneConstantBuffer, 0);

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

                    ID3D11ShaderResourceView chunkInfoSrv = null;
                    ID3D11ShaderResourceView materialSrv = null;

                    try
                    {
                        chunkInfoSrv = device.CreateShaderResourceView(chunkInfoBuffer);
                        materialSrv = device.CreateShaderResourceView(materialBuffer);

                        context.VSSetShader(vertexShader);
                        context.PSSetShader(pixelShader);
                        context.PSSetConstantBuffers(0, new[] { sceneConstantBuffer });
                        context.PSSetSamplers(0, new[] { samplerState });

                        var srvs = new[] { materialSrv, chunkInfoSrv, grayscaleTextureSrv, labelTextureSrv };
                        context.PSSetShaderResources(0, srvs);

                        context.IASetInputLayout(null);
                        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                        context.Draw(3, 0);
                    }
                    finally
                    {
                        context.PSSetShaderResources(0, new ID3D11ShaderResourceView[] { null, null, null, null });
                        context.PSSetShader(null);
                        context.VSSetShader(null);
                        context.PSSetConstantBuffers(0, new ID3D11Buffer[] { null });
                        context.PSSetSamplers(0, new ID3D11SamplerState[] { null });
                        context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>(), null);

                        chunkInfoSrv?.Dispose();
                        materialSrv?.Dispose();
                    }

                    swapChain.Present(1, PresentFlags.None);
                    NeedsRender = false;
                }
                catch (SharpGenException sgex) when ((uint)sgex.HResult == 0x887A0005)
                {
                    deviceLost = true;
                    Logger.Log($"[Render] Device removed detected. Reason: {device.DeviceRemovedReason.Code.ToString("X")}");
                    sceneConstants.Quality = 0;
                    sceneConstants.StepSize = 8.0f;
                }
                catch (Exception ex)
                {
                    Logger.Log($"[Render] Error during rendering: {ex.Message}");
                }
            }
        }

        public void Resize(int width, int height)
        {
            if (_isDisposed || device == null || width <= 0 || height <= 0) return;

            lock (renderLock)
            {
                if (_isDisposed) return;

                try
                {
                    renderTargetView?.Dispose();
                    renderTargetView = null;

                    swapChain.ResizeBuffers(2, width, height, Format.R8G8B8A8_UNorm, SwapChainFlags.None);
                    CreateRenderTargetView();
                    sceneConstants.ScreenDimensions = new Vector2(width, height);
                    NeedsRender = true;
                }
                catch (Exception ex)
                {
                    Logger.Log($"[Resize] Error: {ex.Message}");
                }
            }
        }

        public void SetIsCameraMoving(bool moving)
        {
            if (_isDisposed) return;

            if (isCameraMoving != moving)
            {
                isCameraMoving = moving;
                sceneConstants.StepSize = moving ? 4.0f : 2.0f;
                NeedsRender = true;
            }
        }

        public void SetRenderParams(RenderParameters p)
        {
            if (_isDisposed) return;

            sceneConstants.Threshold = p.Threshold;
            sceneConstants.Quality = p.Quality;
            sceneConstants.ShowGrayscale = p.ShowGrayscale;
            sceneConstants.ShowScaleBar = p.ShowScaleBar;
            sceneConstants.ScaleBarPosition = p.ScaleBarPosition;
            sceneConstants.ShowScaleText = p.ShowScaleText;
            sceneConstants.ScaleBarLength = p.ScaleBarLength;
            sceneConstants.PixelSize = p.PixelSize;

            if (volumeData != null)
            {
                sceneConstants.VolumeDimensions = new Vector4(mainForm.GetWidth(), mainForm.GetHeight(), mainForm.GetDepth(), 0);
                sceneConstants.ChunkDimensions = new Vector4(volumeData.ChunkDim, volumeData.ChunkCountX, volumeData.ChunkCountY, volumeData.ChunkCountZ);
            }

            // Set slice positions
            sceneConstants.SliceInfo = new Vector4(p.SlicePositions, 0);

            // Set clipping planes
            sceneConstants.NumClippingPlanes = Math.Min(p.ClippingPlanes?.Count ?? 0, 8);

            // Clear all planes first
            sceneConstants.ClippingPlane0 = new Vector4(0, 0, 0, float.MaxValue);
            sceneConstants.ClippingPlane1 = new Vector4(0, 0, 0, float.MaxValue);
            sceneConstants.ClippingPlane2 = new Vector4(0, 0, 0, float.MaxValue);
            sceneConstants.ClippingPlane3 = new Vector4(0, 0, 0, float.MaxValue);
            sceneConstants.ClippingPlane4 = new Vector4(0, 0, 0, float.MaxValue);
            sceneConstants.ClippingPlane5 = new Vector4(0, 0, 0, float.MaxValue);
            sceneConstants.ClippingPlane6 = new Vector4(0, 0, 0, float.MaxValue);
            sceneConstants.ClippingPlane7 = new Vector4(0, 0, 0, float.MaxValue);

            // Set active planes
            if (p.ClippingPlanes != null)
            {
                for (int i = 0; i < Math.Min(p.ClippingPlanes.Count, 8); i++)
                {
                    switch (i)
                    {
                        case 0: sceneConstants.ClippingPlane0 = p.ClippingPlanes[i]; break;
                        case 1: sceneConstants.ClippingPlane1 = p.ClippingPlanes[i]; break;
                        case 2: sceneConstants.ClippingPlane2 = p.ClippingPlanes[i]; break;
                        case 3: sceneConstants.ClippingPlane3 = p.ClippingPlanes[i]; break;
                        case 4: sceneConstants.ClippingPlane4 = p.ClippingPlanes[i]; break;
                        case 5: sceneConstants.ClippingPlane5 = p.ClippingPlanes[i]; break;
                        case 6: sceneConstants.ClippingPlane6 = p.ClippingPlanes[i]; break;
                        case 7: sceneConstants.ClippingPlane7 = p.ClippingPlanes[i]; break;
                    }
                }
            }

            if (sceneConstants.StepSize < 1.0f)
                sceneConstants.StepSize = 2.0f;

            NeedsRender = true;
        }
        public void Dispose()
        {
            Logger.Log("[D3D11VolumeRenderer] Dispose called");

            lock (renderLock)
            {
                if (_isDisposed) return;
                _isDisposed = true;
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
            }

            // Dispose streaming manager first (it uses the textures)
            try
            {
                streamingManager?.Dispose();
            }
            catch (Exception ex)
            {
                Logger.Log($"[D3D11VolumeRenderer] Error disposing streaming manager: {ex.Message}");
            }

            // Dispose resources in reverse order of creation
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

            Logger.Log("[D3D11VolumeRenderer] Disposed");
        }
    }
}