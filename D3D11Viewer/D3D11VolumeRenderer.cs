// Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
using CTS;
using CTS.D3D11;
using Krypton.Workspace;
using SharpGen.Runtime;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using BindFlags = Vortice.Direct3D11.BindFlags;
using BufferDescription = Vortice.Direct3D11.BufferDescription;
using Color4 = Vortice.Mathematics.Color4;
using CpuAccessFlags = Vortice.Direct3D11.CpuAccessFlags;
using DeviceCreationFlags = Vortice.Direct3D11.DeviceCreationFlags;
using Filter = Vortice.Direct3D11.Filter;
using Format = Vortice.DXGI.Format;
using MapMode = Vortice.Direct3D11.MapMode;
using PresentFlags = Vortice.DXGI.PresentFlags;
using ResourceOptionFlags = Vortice.Direct3D11.ResourceOptionFlags;
using ResourceUsage = Vortice.Direct3D11.ResourceUsage;
using SampleDescription = Vortice.DXGI.SampleDescription;
using SwapChainFlags = Vortice.DXGI.SwapChainFlags;
using SwapEffect = Vortice.DXGI.SwapEffect;
using Texture2DDescription = Vortice.Direct3D11.Texture2DDescription;
using TextureAddressMode = Vortice.Direct3D11.TextureAddressMode;
using Usage = Vortice.DXGI.Usage;
using SysMatrix4x4 = System.Numerics.Matrix4x4;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;
using Viewport = Vortice.Mathematics.Viewport;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;
using ModeDescription = Vortice.DXGI.ModeDescription;
using Color = System.Drawing.Color;
using Rectangle = System.Drawing.Rectangle;
using Buffer = System.Buffer;
using System.Drawing.Drawing2D;
using SharpDX;

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
        public float DrawClippingPlanes;

        // New scale bar parameters
        public float ShowScaleText;
        public float ScaleBarLength; // in mm
        public float PixelSize;      // meters per pixel
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SceneConstants
    {
        public SysMatrix4x4 InverseViewProjection; // Must be System.Numerics for shader marshalling
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
        public float PixelSize;         // meters per pixel
        public float CameraDistance;    // For dynamic scale bar
        public float DrawClippingPlanes;
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
    float maxTextureSlices;
    float showScaleText;
    float scaleBarLength;    // in mm
    float pixelSize;         // meters per pixel
    float cameraDistance;    // For dynamic scale bar
    float drawClippingPlanes;
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

// Applies a 1D Transfer Function to map intensity to color and opacity.
float4 applyTransferFunction(float intensity)
{
    float grayInt = intensity * 255.0f;

    if (grayInt < threshold.x)
        return float4(0, 0, 0, 0);

    float t = saturate((grayInt - threshold.x) / (threshold.y - threshold.x));

    float3 color = float3(t, t, t);
    float opacity = pow(t, 2.0f);

    return float4(color, opacity);
}

// Samples the volume and determines the color/opacity at a point.
float4 sampleAndClassifyVolume(float3 worldPos, out uint labelIndex)
{
    labelIndex = 0;
    float3 uvw = worldPos / volumeDimensions.xyz;
    if (any(uvw < 0.0) || any(uvw > 1.0)) 
        return float4(0, 0, 0, 0);
    
    float3 voxelCoord = uvw * volumeDimensions.xyz;
    int3 chunkCoord = int3(voxelCoord / chunkDimensions.x);
    
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
    float sliceZ = localCoord.z * (chunkDimensions.x - 1);
    float sliceIndex = gpuSlot * chunkDimensions.x + sliceZ;
    
    if (sliceIndex >= maxTextureSlices)
        return float4(0, 0, 0, 0);
    
    float grayValue = grayscaleAtlas.SampleLevel(linearSampler, float3(localCoord.xy, sliceIndex), 0);
    uint labelValue = labelAtlas.Load(int4(int3(localCoord.xy * chunkDimensions.x, sliceIndex), 0)).r;
    labelIndex = labelValue;
    
    float4 sampleColor = float4(0, 0, 0, 0);

    if (showGrayscale > 0.5)
    {
        sampleColor = applyTransferFunction(grayValue);
    }

    if (labelValue > 0 && labelValue < 256)
    {
        Material mat = materials[labelValue];
        if (mat.settings.y > 0.5)
        {
            sampleColor = float4(mat.color.rgb, mat.settings.x);
        }
    }
    
    return sampleColor;
}


// Renders the 2D slice planes.
float4 checkSlice(float3 worldPos)
{
    float3 uvw = worldPos / volumeDimensions.xyz;
    float sliceThickness = 0.5 / min(volumeDimensions.x, min(volumeDimensions.y, volumeDimensions.z));
    uint labelIdx;
    if (sliceInfo.x >= 0 && abs(uvw.x - sliceInfo.x) < sliceThickness)
        return sampleAndClassifyVolume(float3(sliceInfo.x * volumeDimensions.x, worldPos.y, worldPos.z), labelIdx);
    if (sliceInfo.y >= 0 && abs(uvw.y - sliceInfo.y) < sliceThickness)
         return sampleAndClassifyVolume(float3(worldPos.x, sliceInfo.y * volumeDimensions.y, worldPos.z), labelIdx);
    if (sliceInfo.z >= 0 && abs(uvw.z - sliceInfo.z) < sliceThickness)
        return sampleAndClassifyVolume(float3(worldPos.x, worldPos.y, sliceInfo.z * volumeDimensions.z), labelIdx);
    return float4(0, 0, 0, 0);
}

// Check if point is clipped
bool isClipped(float3 worldPos)
{
    for (int i = 0; i < (int)numClippingPlanes; ++i)
    {
        float4 plane = clippingPlanes[i];
        float dist = dot(worldPos - volumeDimensions.xyz * 0.5, plane.xyz) - plane.w;
        if (dist > 0)
        {
            return true;
        }
    }
    return false;
}

// Check distance to nearest clipping plane
float getClippingPlaneDistance(float3 worldPos)
{
    float minDist = 1e6;
    for (int i = 0; i < (int)numClippingPlanes; ++i)
    {
        minDist = min(minDist, abs(dot(worldPos - volumeDimensions.xyz * 0.5, clippingPlanes[i].xyz) - clippingPlanes[i].w));
    }
    return minDist;
}

// Improved clipping plane visualization
float4 drawClippingPlaneVisual(float3 worldPos)
{
    if (drawClippingPlanes < 0.5 || numClippingPlanes < 1)
        return float4(0, 0, 0, 0);

    float4 finalPlaneColor = float4(0, 0, 0, 0);
    float planeThickness = max(stepSize, 1.0);

    for (int i = 0; i < (int)numClippingPlanes; ++i)
    {
        float4 currentPlane = clippingPlanes[i];
        float dist = abs(dot(worldPos - volumeDimensions.xyz * 0.5, currentPlane.xyz) - currentPlane.w);

        if (dist < planeThickness)
        {
            float alpha = pow(1.0 - dist / planeThickness, 2.0);

            // Assign color based on plane index
            float3 color;
            if (i == 0) color = float3(1.0, 0.4, 0.4);   // Red
            else if (i == 1) color = float3(0.4, 1.0, 0.4); // Green
            else if (i == 2) color = float3(0.4, 0.4, 1.0); // Blue
            else if (i == 3) color = float3(1.0, 1.0, 0.4); // Yellow
            else if (i == 4) color = float3(0.4, 1.0, 1.0); // Cyan
            else if (i == 5) color = float3(1.0, 0.4, 1.0); // Magenta
            else color = float3(0.8, 0.8, 0.8);             // White

            finalPlaneColor.rgb = lerp(finalPlaneColor.rgb, color, alpha);
            finalPlaneColor.a = max(finalPlaneColor.a, alpha * 0.5);
        }
    }
    return finalPlaneColor;
}

float4 main(float4 pos : SV_POSITION, float3 screenPos : TEXCOORD0) : SV_TARGET
{
    float3 boxMin = float3(0, 0, 0);
    float3 boxMax = volumeDimensions.xyz;
    float4 clip = float4((pos.xy / screenDimensions) * 2.0 - 1.0, 0.0, 1.0);
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

    int maxSteps = quality == 0 ? 256 : (quality == 1 ? 512 : 768);
    float baseStepSize = max(stepSize, (tmax - tmin) / (float)maxSteps);

    int emptySteps = 0;
    float3 accumulatedColor = float3(0, 0, 0);
    float accumulatedAlpha = 0.0;
    
    // Combine volume and plane rendering in one loop
    [loop]
    for (int i = 0; i < maxSteps; i++)
    {
        if (accumulatedAlpha > 0.98f) break;
        if (t > tmax) break;

        float3 worldPos = rayOrigin + rayDir * t;

        // Skip clipped regions early
        if (isClipped(worldPos))
        {
            t += baseStepSize;
            continue;
        }

        // Sample volume
        uint labelIndex;
        float4 sampleColor = sampleAndClassifyVolume(worldPos, labelIndex);

        if (sampleColor.a > 0.001f)
        {
            float sampleAlpha;
            if (labelIndex > 0 && labelIndex < 256)
            {
                float materialOpacity = sampleColor.a;
                sampleAlpha = 1.0f - exp(-materialOpacity * baseStepSize * 2.0f);
                if (materialOpacity > 0.0f && materialOpacity < 0.1f)
                {
                    sampleAlpha = max(sampleAlpha, materialOpacity * baseStepSize * 0.5f);
                }
            }
            else
            {
                float density_multiplier = 4.0f;
                sampleAlpha = 1.0f - exp(-sampleColor.a * baseStepSize * density_multiplier);
            }

            float light = 0.8f + 0.2f * dot(normalize(worldPos - cameraPosition.xyz), -rayDir);
            sampleColor.rgb *= light;

            accumulatedColor += sampleColor.rgb * sampleAlpha * (1.0f - accumulatedAlpha);
            accumulatedAlpha += sampleAlpha * (1.0f - accumulatedAlpha);
            emptySteps = 0;
        }
        else
        {
            emptySteps++;
        }

        // Add clipping plane visualization
        float4 planeVisColor = drawClippingPlaneVisual(worldPos);
        if (planeVisColor.a > 0.01)
        {
             accumulatedColor = lerp(accumulatedColor, planeVisColor.rgb, planeVisColor.a);
             accumulatedAlpha = max(accumulatedAlpha, planeVisColor.a);
        }


        // Adaptive step size
        float currentStepSize = baseStepSize;
        if (emptySteps > 4)
        {
            currentStepSize = baseStepSize * 2.0f;
        }
        if (drawClippingPlanes > 0.5 && numClippingPlanes > 0)
        {
            float planeDist = getClippingPlaneDistance(worldPos);
            if (planeDist < baseStepSize * 4.0)
            {
                currentStepSize = min(currentStepSize, baseStepSize * 0.5);
            }
        }
        t += currentStepSize;
    }

    float brightness = 1.8f;
    accumulatedColor *= brightness;
    float gamma = 0.75f;
    accumulatedColor = pow(accumulatedColor, gamma);
    finalColor = float4(accumulatedColor, accumulatedAlpha);

    return finalColor;
}
        ";
        private const string PickingPixelShaderCode = @"
// Same constant buffer as main shader
cbuffer SceneConstants : register(b0)
{
    matrix inverseViewProjection;
    float4 cameraPosition;
    float4 volumeDimensions;
    float4 chunkDimensions;
    float4 sliceInfo; 
    float4 clippingPlanes[8];
    float2 screenDimensions;
    float2 threshold;
    float stepSize;
    float quality;
    float showGrayscale;
    float showScaleBar;
    float scaleBarPosition;
    float numClippingPlanes;
    float maxTextureSlices;
    float showScaleText;
    float scaleBarLength;
    float pixelSize;
    float cameraDistance;
    float drawClippingPlanes;
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

bool isClipped(float3 worldPos)
{
    for (int i = 0; i < (int)numClippingPlanes; ++i)
    {
        float4 plane = clippingPlanes[i];
        float dist = dot(worldPos - volumeDimensions.xyz * 0.5, plane.xyz) - plane.w;
        if (dist > 0)
        {
            return true;
        }
    }
    return false;
}

// Simplified sampling, just need to know if it's empty or not
float sampleVolume(float3 worldPos)
{
    float3 uvw = worldPos / volumeDimensions.xyz;
    if (any(uvw < 0.0) || any(uvw > 1.0)) 
        return 0.0;
    
    float3 voxelCoord = uvw * volumeDimensions.xyz;
    int3 chunkCoord = int3(voxelCoord / chunkDimensions.x);
    
    if (any(chunkCoord < 0) || chunkCoord.x >= (int)chunkDimensions.y || 
        chunkCoord.y >= (int)chunkDimensions.z || chunkCoord.z >= (int)chunkDimensions.w)
        return 0.0;
    
    int chunkIndex = chunkCoord.z * (int)(chunkDimensions.y * chunkDimensions.z) + 
                    chunkCoord.y * (int)chunkDimensions.y + chunkCoord.x;
    
    int totalChunks = (int)(chunkDimensions.y * chunkDimensions.z * chunkDimensions.w);
    if (chunkIndex < 0 || chunkIndex >= totalChunks) 
        return 0.0;
    
    int gpuSlot = chunkInfos[chunkIndex].gpuSlotIndex;
    if (gpuSlot < 0)
        return 0.0;
    
    float3 localCoord = frac(voxelCoord / chunkDimensions.x);
    float sliceZ = localCoord.z * (chunkDimensions.x - 1);
    float sliceIndex = gpuSlot * chunkDimensions.x + sliceZ;
    
    if (sliceIndex >= maxTextureSlices)
        return 0.0;
    
    float grayValue = grayscaleAtlas.SampleLevel(linearSampler, float3(localCoord.xy, sliceIndex), 0);
    uint labelValue = labelAtlas.Load(int4(int3(localCoord.xy * chunkDimensions.x, sliceIndex), 0)).r;
    
    float intensity = 0.0;
    if (showGrayscale > 0.5 && grayValue * 255.0f > threshold.x)
    {
        intensity = 1.0;
    }
    if (labelValue > 0 && labelValue < 256)
    {
        if (materials[labelValue].settings.y > 0.5)
        {
            intensity = 1.0;
        }
    }
    return intensity;
}


float4 main(float4 pos : SV_POSITION, float3 screenPos : TEXCOORD0) : SV_TARGET
{
    float3 boxMin = float3(0, 0, 0);
    float3 boxMax = volumeDimensions.xyz;
    float4 clip = float4((pos.xy / screenDimensions) * 2.0 - 1.0, 0.0, 1.0);
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

    if (tmin > tmax || tmax < 0)
    {
        discard;
        return float4(0,0,0,0);
    }
    tmin = max(tmin, 0.0);

    float t = tmin;
    int maxSteps = 768; // High quality for picking accuracy
    float step = max(stepSize, (tmax - tmin) / (float)maxSteps);

    [loop]
    for (int i = 0; i < maxSteps; i++)
    {
        if (t > tmax) break;
        float3 worldPos = rayOrigin + rayDir * t;
        
        if (isClipped(worldPos))
        {
            t += step;
            continue;
        }
        
        if (sampleVolume(worldPos) > 0.1)
        {
            // We hit something, output the world position and exit.
            return float4(worldPos, 1.0);
        }
        t += step;
    }

    // Hit nothing
    discard;
    return float4(0,0,0,0);
}";
        private const string CompositeShaderCode = @"
            cbuffer SceneConstants : register(b0)
            {
                matrix inverseViewProjection;
                float4 cameraPosition;
                float4 volumeDimensions;
                float4 chunkDimensions;
                float4 sliceInfo;
                float4 clippingPlanes[8];
                float2 screenDimensions;
                float2 threshold;
                float stepSize;
                float quality;
                float showGrayscale;
                float showScaleBar;
                float scaleBarPosition;
                float numClippingPlanes;
                float maxTextureSlices;
                float showScaleText;
                float scaleBarLength;
                float pixelSize;
                float cameraDistance;
                float drawClippingPlanes;
            };

            Texture2D sceneTexture : register(t0);
            Texture2D overlayTexture : register(t1);
            SamplerState linearSampler : register(s0);

            float4 main(float4 pos : SV_POSITION) : SV_TARGET
            {
                float2 uv = pos.xy / screenDimensions.xy;
                float4 sceneColor = sceneTexture.Sample(linearSampler, uv);
                float4 overlayColor = overlayTexture.Sample(linearSampler, uv);
                
                // Alpha blend the overlay on top of the scene
                sceneColor.rgb = overlayColor.rgb * overlayColor.a + sceneColor.rgb * (1.0 - overlayColor.a);
                sceneColor.a = overlayColor.a + sceneColor.a * (1.0 - overlayColor.a);
                return sceneColor;
            }";
        #endregion

        private ID3D11Device device;
        private ID3D11DeviceContext context;
        private IDXGISwapChain swapChain;
        private ID3D11RenderTargetView renderTargetView;

        private ID3D11VertexShader vertexShader;
        private ID3D11PixelShader pixelShader;
        private ID3D11PixelShader pickingPixelShader;
        private ID3D11PixelShader compositePixelShader;
        private ID3D11SamplerState samplerState;

        private ID3D11Buffer sceneConstantBuffer;
        private ID3D11Buffer materialBuffer;
        private ID3D11Buffer chunkInfoBuffer;

        // New resources for picking
        private ID3D11Texture2D pickingTexture;
        private ID3D11RenderTargetView pickingRtv;
        private ID3D11Texture2D pickingStagingTexture;

        // New resources for overlay
        private ID3D11Texture2D sceneTexture;
        private ID3D11RenderTargetView sceneRtv;
        private ID3D11ShaderResourceView sceneSrv;
        private ID3D11Texture2D overlayTexture;
        private ID3D11ShaderResourceView overlaySrv;
        private List<MeasurementObject> localMeasurements = new List<MeasurementObject>();

        private int GpuCacheSize = 32;
        private int totalTextureSlices = 0;

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
        private int deviceLostRetryCount = 0;
        private DateTime lastDeviceLostTime = DateTime.MinValue;
        private bool useWarpAdapter = false; // Fallback to software rendering

        // Thread safety
        private volatile bool _isDisposed = false;
        private volatile bool _isRendering = false;
        private readonly object disposeLock = new object();
        private readonly object renderLock = new object();
        private readonly ManualResetEventSlim renderComplete = new ManualResetEventSlim(true);

        public bool IsDisposed => _isDisposed;
        private Color4 backgroundColor = new Color4(0.1f, 0.1f, 0.1f, 1.0f);

        // GPU info
        private string gpuDescription = "Unknown";
        private bool isIntelGpu = false;
        private bool isIntegratedGpu = false;

        // --- ADDED: Public properties for the Info tab ---
        public string GpuDescription => gpuDescription;
        public int GpuCacheSizeInChunks => GpuCacheSize;

        public D3D11VolumeRenderer(IntPtr hwnd, int width, int height, MainForm mainForm)
        {
            this.mainForm = mainForm;
            this.volumeData = mainForm.volumeData;
            this.labelData = mainForm.volumeLabels;

            try
            {
                InitializeD3D11(hwnd, width, height);
                DetectGPUCapabilities();

                if (volumeData != null && labelData != null)
                {
                    totalChunks = volumeData.ChunkCountX * volumeData.ChunkCountY * volumeData.ChunkCountZ;

                    int maxArraySize = 2048;
                    int chunkDim = volumeData.ChunkDim;
                    int maxCacheSize = maxArraySize / chunkDim;

                    // Reduce cache size for Intel GPUs
                    if (isIntelGpu)
                    {
                        if (chunkDim >= 256)
                            GpuCacheSize = Math.Min(4, Math.Min(maxCacheSize, totalChunks));
                        else if (chunkDim >= 128)
                            GpuCacheSize = Math.Min(8, Math.Min(maxCacheSize, totalChunks));
                        else
                            GpuCacheSize = Math.Min(16, Math.Min(maxCacheSize, totalChunks));
                    }
                    else
                    {
                        if (chunkDim >= 256)
                            GpuCacheSize = Math.Min(8, Math.Min(maxCacheSize, totalChunks));
                        else if (chunkDim >= 128)
                            GpuCacheSize = Math.Min(16, Math.Min(maxCacheSize, totalChunks));
                        else
                            GpuCacheSize = Math.Min(32, Math.Min(maxCacheSize, totalChunks));
                    }

                    totalTextureSlices = GpuCacheSize * chunkDim;

                    Logger.Log($"[D3D11VolumeRenderer] GPU: {gpuDescription}");
                    Logger.Log($"[D3D11VolumeRenderer] Volume info: {volumeData.Width}x{volumeData.Height}x{volumeData.Depth}");
                    Logger.Log($"[D3D11VolumeRenderer] Chunk info: dim={chunkDim}, count={volumeData.ChunkCountX}x{volumeData.ChunkCountY}x{volumeData.ChunkCountZ}");
                    Logger.Log($"[D3D11VolumeRenderer] Using GPU cache size: {GpuCacheSize} chunks, {totalTextureSlices} total texture slices");

                    CreateGpuCache();
                    CreateShaders();
                    CreateConstantBuffers();
                    CreateSamplerState();
                    CreateRenderTargets(width, height); // New method for all render targets

                    streamingManager = new ChunkStreamingManager(device, context, volumeData, labelData, grayscaleTextureCache, labelTextureCache);
                }
                else
                {
                    Logger.Log("[D3D11VolumeRenderer] Warning: Volume data is null");
                    totalChunks = 1;
                    CreateShaders();
                    CreateConstantBuffers();
                    CreateSamplerState();
                    CreateRenderTargets(width, height);
                }

                UpdateMaterialsBuffer();

                // Initialize scene constants with reduced quality for Intel GPUs
                sceneConstants.ScreenDimensions = new Vector2(width, height);
                sceneConstants.StepSize = isIntelGpu ? 4.0f : 2.0f;
                sceneConstants.Quality = 0.0f; // Start with lowest quality
                sceneConstants.Threshold = new Vector2(30, 200);
                sceneConstants.ShowGrayscale = 1.0f;
                sceneConstants.ShowScaleBar = 1.0f;
                sceneConstants.ScaleBarPosition = 0.0f;
                sceneConstants.NumClippingPlanes = 0.0f;
                sceneConstants.MaxTextureSlices = totalTextureSlices;
                sceneConstants.SliceInfo = new Vector4(-1, -1, -1, 0);
                sceneConstants.ShowScaleText = 1.0f;
                sceneConstants.ScaleBarLength = 100.0f;
                sceneConstants.PixelSize = (float)mainForm.pixelSize;
                sceneConstants.CameraDistance = 1000.0f;
                sceneConstants.DrawClippingPlanes = 1.0f;
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

        private void DetectGPUCapabilities()
        {
            try
            {
                using (var factory = device.QueryInterface<IDXGIDevice>()?.GetAdapter()?.GetParent<IDXGIFactory>())
                {
                    if (factory != null)
                    {
                        using (var adapter = device.QueryInterface<IDXGIDevice>()?.GetAdapter())
                        {
                            if (adapter != null)
                            {
                                var desc = adapter.Description;
                                gpuDescription = desc.Description;

                                // Detect Intel GPU
                                isIntelGpu = gpuDescription.ToLower().Contains("intel");

                                // Detect integrated GPU
                                isIntegratedGpu = isIntelGpu ||
                                                 gpuDescription.ToLower().Contains("integrated") ||
                                                 desc.DedicatedVideoMemory < 1024 * 1024 * 1024; // Less than 1GB VRAM

                                Logger.Log($"[DetectGPUCapabilities] GPU: {gpuDescription}");
                                Logger.Log($"[DetectGPUCapabilities] Intel GPU: {isIntelGpu}, Integrated: {isIntegratedGpu}");
                                Logger.Log($"[DetectGPUCapabilities] VRAM: {desc.DedicatedVideoMemory / 1024 / 1024} MB");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[DetectGPUCapabilities] Error: {ex.Message}");
            }
        }

        private void InitializeD3D11(IntPtr hwnd, int width, int height)
        {
            var swapChainDesc = new Vortice.DXGI.SwapChainDescription
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

            // Supported feature levels (highest first)
            var featureLevels = new[]
            {
        FeatureLevel.Level_11_0,
        FeatureLevel.Level_10_1,
        FeatureLevel.Level_10_0
    };

            try
            {
                // Try a hardware device first
                Vortice.Direct3D11.D3D11.D3D11CreateDeviceAndSwapChain(
                    null,
                    DriverType.Hardware,
                    flags,
                    featureLevels,
                    swapChainDesc,
                    out swapChain,
                    out device,
                    out _,
                    out context);
            }
            catch (Exception ex)
            {
                Logger.Log($"[InitializeD3D11] Hardware device creation failed: {ex.Message}, falling back to WARP");

                // Fall back to software (WARP) device
                useWarpAdapter = true;
                Vortice.Direct3D11.D3D11.D3D11CreateDeviceAndSwapChain(
                    null,
                    DriverType.Warp,
                    flags,
                    featureLevels,
                    swapChainDesc,
                    out swapChain,
                    out device,
                    out _,
                    out context);

                Logger.Log("[InitializeD3D11] Using WARP software renderer");
            }

            // ------------------------------------------------------------------
            // Make the immediate context thread-safe for the streaming thread
            using (var mt = context.QueryInterface<ID3D11Multithread>())
            {
                mt.SetMultithreadProtected(true);
            }
            // ------------------------------------------------------------------

            CreateFinalRenderTargetView();
        }


        private void CreateFinalRenderTargetView()
        {
            lock (disposeLock)
            {
                if (_isDisposed || swapChain == null || device == null) return;

                renderTargetView?.Dispose();
                renderTargetView = null;

                try
                {
                    using (var backBuffer = swapChain.GetBuffer<ID3D11Texture2D>(0))
                    {
                        renderTargetView = device.CreateRenderTargetView(backBuffer);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[CreateFinalRenderTargetView] Error: {ex.Message}");
                    throw;
                }
            }
        }

        private void CreateShaders()
        {
            var vsByteCode = Compiler.Compile(VertexShaderCode, "main", string.Empty, "vs_5_0", ShaderFlags.None, EffectFlags.None);
            vertexShader = device.CreateVertexShader(vsByteCode.Span);
            var psByteCode = Compiler.Compile(PixelShaderCode, "main", string.Empty, "ps_5_0", ShaderFlags.None, EffectFlags.None);
            pixelShader = device.CreatePixelShader(psByteCode.Span);
            var pickingPsByteCode = Compiler.Compile(PickingPixelShaderCode, "main", string.Empty, "ps_5_0", ShaderFlags.None, EffectFlags.None);
            pickingPixelShader = device.CreatePixelShader(pickingPsByteCode.Span);
            var compositePsByteCode = Compiler.Compile(CompositeShaderCode, "main", string.Empty, "ps_5_0", ShaderFlags.None, EffectFlags.None);
            compositePixelShader = device.CreatePixelShader(compositePsByteCode.Span);
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

            int chunkBufferSize = Math.Max(1, totalChunks) * Marshal.SizeOf<ChunkInfoGPU>();
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

        private void CreateRenderTargets(int width, int height)
        {
            // Dispose previous resources
            pickingTexture?.Dispose();
            pickingRtv?.Dispose();
            pickingStagingTexture?.Dispose();
            sceneTexture?.Dispose();
            sceneRtv?.Dispose();
            sceneSrv?.Dispose();
            overlayTexture?.Dispose();
            overlaySrv?.Dispose();

            // Scene texture
            var sceneDesc = new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource
            };
            sceneTexture = device.CreateTexture2D(sceneDesc);
            sceneRtv = device.CreateRenderTargetView(sceneTexture);
            sceneSrv = device.CreateShaderResourceView(sceneTexture);

            // Picking texture
            var pickingDesc = new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R32G32B32A32_Float,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget
            };
            pickingTexture = device.CreateTexture2D(pickingDesc);
            pickingRtv = device.CreateRenderTargetView(pickingTexture);

            // Staging texture for reading picking result
            var stagingDesc = new Texture2DDescription
            {
                Width = 1,
                Height = 1,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R32G32B32A32_Float,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                CPUAccessFlags = CpuAccessFlags.Read
            };
            pickingStagingTexture = device.CreateTexture2D(stagingDesc);

            // Overlay texture
            var overlayDesc = new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Dynamic,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.Write
            };
            overlayTexture = device.CreateTexture2D(overlayDesc);
            overlaySrv = device.CreateShaderResourceView(overlayTexture);
        }

        public void UpdateMeasurementData(List<MeasurementObject> measurements)
        {
            lock (renderLock)
            {
                localMeasurements = new List<MeasurementObject>(measurements);
                NeedsRender = true;
            }
        }

        private void UpdateOverlayTexture(Camera camera)
        {
            if (_isDisposed || overlayTexture == null || context == null) return;

            int width = overlayTexture.Description.Width;
            int height = overlayTexture.Description.Height;

            try
            {
                using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
                using (var g = Graphics.FromImage(bitmap))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAlias;

                    // Draw Scale Bar and Text
                    if (sceneConstants.ShowScaleBar > 0.5f)
                    {
                        DrawScaleBar(g, width, height);
                    }

                    // Draw Measurement Objects
                    DrawMeasurements(g, camera, new Size(width, height));

                    // Upload to GPU
                    var rect = new Rectangle(0, 0, width, height);
                    var bitmapData = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

                    try
                    {
                        var mapped = context.Map(overlayTexture, 0, MapMode.WriteDiscard);
                        if (mapped.RowPitch == bitmapData.Stride)
                        {
                            Kernel32.CopyMemory(mapped.DataPointer, bitmapData.Scan0, height * bitmapData.Stride);
                        }
                        else
                        {
                            for (int i = 0; i < height; i++)
                            {
                                Kernel32.CopyMemory(mapped.DataPointer + i * mapped.RowPitch,
                                                   bitmapData.Scan0 + i * bitmapData.Stride,
                                                   width * 4);
                            }
                        }
                        context.Unmap(overlayTexture, 0);
                    }
                    finally
                    {
                        bitmap.UnlockBits(bitmapData);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[UpdateOverlayTexture] Warning: Failed to update overlay texture: {ex.Message}");
            }
        }

        private void DrawScaleBar(Graphics g, int width, int height)
        {
            float worldSizePerPixel = sceneConstants.CameraDistance * sceneConstants.PixelSize / 500.0f;
            float targetPixels = 150.0f;
            if (width < 500) targetPixels = 80.0f;

            float worldSize = targetPixels * worldSizePerPixel;
            float scale = (float)Math.Pow(10, Math.Floor(Math.Log10(worldSize)));
            float normalizedSize = worldSize / scale;

            float actualSize; // in meters
            if (normalizedSize < 1.5f) actualSize = scale;
            else if (normalizedSize < 3.5f) actualSize = 2 * scale;
            else if (normalizedSize < 7.5f) actualSize = 5 * scale;
            else actualSize = 10 * scale;

            float barLengthPixels = actualSize / worldSizePerPixel;
            float barHeight = 8;
            barLengthPixels = Math.Min(barLengthPixels, width * 0.3f);

            PointF barPos;
            var pos = sceneConstants.ScaleBarPosition;
            if (pos < 0.5) barPos = new PointF(20, height - 40); // Bottom Left
            else if (pos < 1.5) barPos = new PointF(width - 20 - barLengthPixels, height - 40); // Bottom Right
            else if (pos < 2.5) barPos = new PointF(20, 40); // Top Left
            else barPos = new PointF(width - 20 - barLengthPixels, 40); // Top Right

            // Draw bar with segments
            using (var blackBrush = new SolidBrush(Color.Black))
            using (var whiteBrush = new SolidBrush(Color.White))
            {
                int numSegments = 5;
                float segmentWidth = barLengthPixels / numSegments;
                for (int i = 0; i < numSegments; i++)
                {
                    g.FillRectangle(i % 2 == 0 ? whiteBrush : blackBrush, barPos.X + i * segmentWidth, barPos.Y, segmentWidth, barHeight);
                }
                g.DrawRectangle(Pens.Gray, barPos.X, barPos.Y, barLengthPixels, barHeight);
            }

            // Draw text
            if (sceneConstants.ShowScaleText > 0.5f)
            {
                string text;
                if (actualSize * 1000 < 1) text = $"{actualSize * 1e6:F0} µm"; // meters to um
                else if (actualSize * 100 < 1) text = $"{actualSize * 1000:F1} mm"; // meters to mm
                else text = $"{actualSize * 100:F1} cm"; // meters to cm

                using (var font = new Font("Arial", 10, FontStyle.Bold))
                {
                    var textSize = g.MeasureString(text, font);
                    var textPos = new PointF(barPos.X + (barLengthPixels - textSize.Width) / 2, barPos.Y - textSize.Height - 2);
                    if (pos > 2) textPos.Y = barPos.Y + barHeight + 2;

                    g.DrawString(text, font, Brushes.White, textPos);
                }
            }
        }

        private void DrawMeasurements(Graphics g, Camera camera, Size viewport)
        {
            if (localMeasurements == null || localMeasurements.Count == 0) return;

            var vp = camera.ViewMatrix * camera.ProjectionMatrix;
            var pointPen = new Pen(Color.FromArgb(200, Color.Cyan), 2);
            var linePen = new Pen(Color.FromArgb(220, Color.Yellow), 2);
            var textBrush = new SolidBrush(Color.Yellow);
            var font = new Font("Arial", 10);

            foreach (var obj in localMeasurements)
            {
                if (obj is MeasurementPoint point)
                {
                    var screenPos = Project(point.Position, vp, viewport);
                    if (screenPos.HasValue)
                    {
                        var p = screenPos.Value;
                        g.DrawEllipse(pointPen, p.X - 4, p.Y - 4, 8, 8);
                    }
                }
                else if (obj is MeasurementLine line)
                {
                    var start = Project(line.Start, vp, viewport);
                    var end = Project(line.End, vp, viewport);
                    if (start.HasValue && end.HasValue)
                    {
                        var p1 = start.Value;
                        var p2 = end.Value;
                        g.DrawLine(linePen, p1, p2);

                        // Calculate length and draw text
                        float lengthMeters = Vector3.Distance(line.Start, line.End) * sceneConstants.PixelSize;
                        string text;
                        if (lengthMeters * 1000 < 1) text = $"{lengthMeters * 1e6:F1} µm";
                        else if (lengthMeters * 100 < 1) text = $"{lengthMeters * 1000:F1} mm";
                        else text = $"{lengthMeters * 100:F1} cm";

                        var midPoint = new PointF((p1.X + p2.X) / 2, (p1.Y + p2.Y) / 2);
                        g.DrawString(text, font, textBrush, midPoint.X + 5, midPoint.Y);
                    }
                }
            }

            pointPen.Dispose();
            linePen.Dispose();
            textBrush.Dispose();
            font.Dispose();
        }

        private PointF? Project(Vector3 worldPos, CTS.Matrix4x4 viewProjection, Size viewport)
        {
            var clipPos = Vector4Extensions.Transform(new SharpDX.Vector4(worldPos.X, worldPos.Y, worldPos.Z, 1.0f), viewProjection);

            if (clipPos.W < 0.1f) return null;

            var ndc = new Vector3(clipPos.X / clipPos.W, clipPos.Y / clipPos.W, clipPos.Z / clipPos.W);

            if (ndc.X < -1.1f || ndc.X > 1.1f || ndc.Y < -1.1f || ndc.Y > 1.1f) return null;

            var screenPos = new PointF(
                (ndc.X + 1.0f) / 2.0f * viewport.Width,
                (1.0f - (ndc.Y + 1.0f) / 2.0f) * viewport.Height
            );

            return screenPos;
        }

        public void UpdateMaterialsBuffer()
        {
            if (materialBuffer == null || _isDisposed) return;

            lock (renderLock)
            {
                if (_isDisposed || materialBuffer == null) return;

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

        private bool HandleDeviceLost()
        {
            if (!deviceLost) return true;

            lock (disposeLock)
            {
                if (_isDisposed || device == null || swapChain == null) return false;
            }

            // Check if we should attempt recovery
            var timeSinceLastLost = DateTime.Now - lastDeviceLostTime;
            if (timeSinceLastLost < TimeSpan.FromSeconds(5))
            {
                deviceLostRetryCount++;
                if (deviceLostRetryCount > 3)
                {
                    Logger.Log("[HandleDeviceLost] Too many device lost errors, giving up");
                    return false;
                }
            }
            else
            {
                deviceLostRetryCount = 0;
            }

            lastDeviceLostTime = DateTime.Now;

            Logger.Log($"[HandleDeviceLost] Attempting device recovery (attempt {deviceLostRetryCount + 1})");

            try
            {
                // Log device removed reason
                if (device != null)
                {
                    try
                    {
                        var reason = device.DeviceRemovedReason;
                        Logger.Log($"[HandleDeviceLost] Device removed reason: {reason.Code.ToString("X")}");
                    }
                    catch { }
                }

                // Wait for any ongoing render to complete
                renderComplete.Wait(1000);

                // For Intel GPUs, reduce quality further
                if (isIntelGpu)
                {
                    sceneConstants.Quality = 0;
                    sceneConstants.StepSize = 8.0f;

                    // Reduce cache size
                    if (GpuCacheSize > 4)
                    {
                        GpuCacheSize = 4;
                        Logger.Log("[HandleDeviceLost] Reduced GPU cache size to 4 for Intel GPU recovery");
                    }
                }

                // Clean up resources
                lock (disposeLock)
                {
                    if (_isDisposed) return false;

                    renderTargetView?.Dispose();
                    renderTargetView = null;
                }

                // Try to recreate render target
                CreateFinalRenderTargetView();

                deviceLost = false;
                Logger.Log("[HandleDeviceLost] Device recovery successful");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[HandleDeviceLost] Device recovery failed: {ex.Message}");

                // If recovery fails on Intel GPU, consider using WARP
                if (isIntelGpu && !useWarpAdapter)
                {
                    Logger.Log("[HandleDeviceLost] Intel GPU recovery failed, restart with WARP may be needed");
                }

                return false;
            }
        }

        public void Render(Camera camera)
        {
            if (!isInitialized || _isDisposed) return;
            if (_isRendering) return;

            try
            {
                _isRendering = true;
                renderComplete.Reset();

                bool canRender;
                lock (disposeLock)
                {
                    canRender = !_isDisposed && device != null && context != null && swapChain != null &&
                                renderTargetView != null && sceneRtv != null && streamingManager != null;
                }
                if (!canRender) return;

                lock (renderLock)
                {
                    lock (disposeLock)
                    {
                        if (_isDisposed || renderTargetView == null || sceneRtv == null) return;
                    }

                    try
                    {
                        if (deviceLost && !HandleDeviceLost()) return;

                        streamingManager.Update(camera);
                        if (!NeedsRender && !streamingManager.IsDirty()) return;

                        var volumeCenter = new Vector3(sceneConstants.VolumeDimensions.X * 0.5f, sceneConstants.VolumeDimensions.Y * 0.5f, sceneConstants.VolumeDimensions.Z * 0.5f);
                        sceneConstants.CameraDistance = Vector3.Distance(camera.Position, volumeCenter);

                        UpdateOverlayTexture(camera);

                        // --- PASS 1: Render Volume to Scene Texture ---
                        context.OMSetRenderTargets(sceneRtv);
                        context.ClearRenderTargetView(sceneRtv, backgroundColor);
                        context.RSSetViewports(new[] { new Viewport(0, 0, sceneConstants.ScreenDimensions.X, sceneConstants.ScreenDimensions.Y) });

                        CTS.Matrix4x4 viewProjMatrix = camera.ViewMatrix * camera.ProjectionMatrix;
                        CTS.Matrix4x4 invViewProjCTS;
                        if (!CTS.Matrix4x4.Invert(viewProjMatrix, out invViewProjCTS))
                        {
                            Logger.Log("[Render] Failed to invert view projection matrix");
                            return;
                        }
                        sceneConstants.InverseViewProjection = CTS.Matrix4x4.Transpose(invViewProjCTS).ToSystemNumerics();
                        sceneConstants.CameraPosition = new Vector4(camera.Position, 1);

                        var mapped = context.Map(sceneConstantBuffer, 0, MapMode.WriteDiscard);
                        unsafe { Marshal.StructureToPtr(sceneConstants, mapped.DataPointer, false); }
                        context.Unmap(sceneConstantBuffer, 0);

                        var chunkData = streamingManager.GetGpuChunkInfo();
                        mapped = context.Map(chunkInfoBuffer, 0, MapMode.WriteDiscard);
                        unsafe
                        {
                            var ptr = (ChunkInfoGPU*)mapped.DataPointer.ToPointer();
                            for (int i = 0; i < chunkData.Length; i++) ptr[i] = chunkData[i];
                        }
                        context.Unmap(chunkInfoBuffer, 0);

                        ID3D11ShaderResourceView chunkInfoSrv = device.CreateShaderResourceView(chunkInfoBuffer);
                        ID3D11ShaderResourceView materialSrv = device.CreateShaderResourceView(materialBuffer);

                        context.VSSetShader(vertexShader);
                        context.PSSetShader(pixelShader);
                        context.PSSetConstantBuffers(0, new[] { sceneConstantBuffer });
                        context.PSSetSamplers(0, new[] { samplerState });
                        context.PSSetShaderResources(0, new[] { materialSrv, chunkInfoSrv, grayscaleTextureSrv, labelTextureSrv });

                        context.IASetInputLayout(null);
                        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                        context.Draw(3, 0);

                        chunkInfoSrv?.Dispose();
                        materialSrv?.Dispose();


                        // --- PASS 2: Composite Scene and Overlay to Back Buffer ---
                        context.OMSetRenderTargets(renderTargetView);
                        context.PSSetShader(compositePixelShader);
                        context.PSSetConstantBuffers(0, new[] { sceneConstantBuffer });
                        context.PSSetSamplers(0, new[] { samplerState });
                        context.PSSetShaderResources(0, new[] { sceneSrv, overlaySrv });
                        context.Draw(3, 0);

                        context.PSSetShaderResources(0, new ID3D11ShaderResourceView[] { null, null });


                        lock (disposeLock)
                        {
                            if (!_isDisposed && swapChain != null)
                            {
                                try
                                {
                                    swapChain.Present(isIntelGpu ? 0 : 1, PresentFlags.None);
                                    NeedsRender = false;
                                }
                                catch (SharpGenException sgex) when ((uint)sgex.HResult == 0x887A0005 || (uint)sgex.HResult == 0x887A0007 || (uint)sgex.HResult == 0x887A0001)
                                {
                                    Logger.Log($"[Render] Present failed with DXGI error: 0x{sgex.HResult:X}");
                                    deviceLost = true;
                                }
                            }
                        }
                    }
                    catch (SharpGenException sgex) when ((uint)sgex.HResult == 0x887A0005)
                    {
                        deviceLost = true;
                        var reason = device.DeviceRemovedReason;
                        Logger.Log($"[Render] Device removed detected. Reason: {reason.Code.ToString("X")}");
                        sceneConstants.Quality = 0;
                        sceneConstants.StepSize = isIntelGpu ? 8.0f : 4.0f;
                    }
                    catch (SharpGenException sgex) when ((uint)sgex.HResult == 0x887A0007)
                    {
                        deviceLost = true;
                        Logger.Log("[Render] Device reset detected");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[Render] Error during rendering: {ex.Message}");
                        if (ex.Message.IndexOf("device", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            deviceLost = true;
                        }
                    }
                }
            }
            finally
            {
                _isRendering = false;
                renderComplete.Set();
            }
        }


        public Vector3 Pick(System.Drawing.Point screenPos, Camera camera)
        {
            if (!isInitialized || _isDisposed || _isRendering) return Vector3.Zero;

            Vector3 result = Vector3.Zero;
            lock (renderLock)
            {
                try
                {
                    context.OMSetRenderTargets(pickingRtv);
                    context.ClearRenderTargetView(pickingRtv, new Color4(0, 0, 0, 0));
                    context.RSSetViewports(new[] { new Viewport(0, 0, sceneConstants.ScreenDimensions.X, sceneConstants.ScreenDimensions.Y) });

                    CTS.Matrix4x4 viewProjMatrix = camera.ViewMatrix * camera.ProjectionMatrix;
                    CTS.Matrix4x4 invViewProjCTS;
                    CTS.Matrix4x4.Invert(viewProjMatrix, out invViewProjCTS);
                    sceneConstants.InverseViewProjection = CTS.Matrix4x4.Transpose(invViewProjCTS).ToSystemNumerics();
                    sceneConstants.CameraPosition = new Vector4(camera.Position, 1);

                    var mapped = context.Map(sceneConstantBuffer, 0, MapMode.WriteDiscard);
                    unsafe { Marshal.StructureToPtr(sceneConstants, mapped.DataPointer, false); }
                    context.Unmap(sceneConstantBuffer, 0);

                    var chunkData = streamingManager.GetGpuChunkInfo();
                    mapped = context.Map(chunkInfoBuffer, 0, MapMode.WriteDiscard);
                    unsafe
                    {
                        var ptr = (ChunkInfoGPU*)mapped.DataPointer.ToPointer();
                        for (int i = 0; i < chunkData.Length; i++) ptr[i] = chunkData[i];
                    }
                    context.Unmap(chunkInfoBuffer, 0);

                    ID3D11ShaderResourceView chunkInfoSrv = device.CreateShaderResourceView(chunkInfoBuffer);
                    ID3D11ShaderResourceView materialSrv = device.CreateShaderResourceView(materialBuffer);

                    context.VSSetShader(vertexShader);
                    context.PSSetShader(pickingPixelShader);
                    context.PSSetConstantBuffers(0, new[] { sceneConstantBuffer });
                    context.PSSetSamplers(0, new[] { samplerState });
                    context.PSSetShaderResources(0, new[] { materialSrv, chunkInfoSrv, grayscaleTextureSrv, labelTextureSrv });

                    context.IASetInputLayout(null);
                    context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

                    context.Draw(3, 0);

                    chunkInfoSrv?.Dispose();
                    materialSrv?.Dispose();

                    var srcBox = new Box(screenPos.X, screenPos.Y, 0, screenPos.X + 1, screenPos.Y + 1, 1);
                    context.CopySubresourceRegion(pickingStagingTexture, 0, 0, 0, 0, pickingTexture, 0, srcBox);

                    var mappedResult = context.Map(pickingStagingTexture, 0, MapMode.Read);
                    if (mappedResult.DataPointer != IntPtr.Zero)
                    {
                        unsafe
                        {
                            var vecPtr = (Vector4*)mappedResult.DataPointer;
                            if (vecPtr->W > 0.5f)
                            {
                                result = new Vector3(vecPtr->X, vecPtr->Y, vecPtr->Z);
                            }
                        }
                    }
                    context.Unmap(pickingStagingTexture, 0);
                }
                catch (Exception ex)
                {
                    Logger.Log($"[Pick] Error during picking: {ex.Message}");
                    return Vector3.Zero;
                }
            }
            return result;
        }


        public void Resize(int width, int height)
        {
            if (_isDisposed || device == null || width <= 0 || height <= 0) return;

            renderComplete.Wait(1000);

            lock (renderLock)
            {
                lock (disposeLock)
                {
                    if (_isDisposed || swapChain == null) return;

                    try
                    {
                        renderTargetView?.Dispose();
                        renderTargetView = null;
                        sceneRtv?.Dispose();

                        context.OMSetRenderTargets(new ID3D11RenderTargetView[0], null);
                        context.Flush();
                        swapChain.ResizeBuffers(2, width, height, Format.R8G8B8A8_UNorm, SwapChainFlags.None);

                        CreateFinalRenderTargetView();
                        CreateRenderTargets(width, height);
                        sceneConstants.ScreenDimensions = new Vector2(width, height);
                        NeedsRender = true;
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[Resize] Error: {ex.Message}");
                        deviceLost = true;
                    }
                }
            }
        }

        public void SetIsCameraMoving(bool moving)
        {
            if (_isDisposed) return;

            if (isCameraMoving != moving)
            {
                isCameraMoving = moving;

                if (isIntelGpu)
                {
                    sceneConstants.StepSize = moving ? 8.0f : 4.0f;
                }
                else
                {
                    sceneConstants.StepSize = moving ? 4.0f : 2.0f;
                }

                NeedsRender = true;
            }
        }

        public void SetRenderParams(RenderParameters p)
        {
            if (_isDisposed) return;

            sceneConstants.Threshold = p.Threshold;

            if (isIntelGpu && p.Quality > 0)
            {
                sceneConstants.Quality = 0;
                Logger.Log("[SetRenderParams] Limiting quality to lowest for Intel GPU");
            }
            else
            {
                sceneConstants.Quality = p.Quality;
            }

            sceneConstants.ShowGrayscale = p.ShowGrayscale;
            sceneConstants.ShowScaleBar = p.ShowScaleBar;
            sceneConstants.ScaleBarPosition = p.ScaleBarPosition;
            sceneConstants.ShowScaleText = p.ShowScaleText;
            sceneConstants.ScaleBarLength = p.ScaleBarLength;
            sceneConstants.PixelSize = p.PixelSize;
            sceneConstants.DrawClippingPlanes = p.DrawClippingPlanes;

            if (volumeData != null)
            {
                sceneConstants.VolumeDimensions = new Vector4(mainForm.GetWidth(), mainForm.GetHeight(), mainForm.GetDepth(), 0);
                sceneConstants.ChunkDimensions = new Vector4(volumeData.ChunkDim, volumeData.ChunkCountX, volumeData.ChunkCountY, volumeData.ChunkCountZ);
            }

            sceneConstants.SliceInfo = new Vector4(p.SlicePositions, 0);
            sceneConstants.NumClippingPlanes = Math.Min(p.ClippingPlanes?.Count ?? 0, 8);

            sceneConstants.ClippingPlane0 = Vector4.Zero;
            sceneConstants.ClippingPlane1 = Vector4.Zero;
            sceneConstants.ClippingPlane2 = Vector4.Zero;
            sceneConstants.ClippingPlane3 = Vector4.Zero;
            sceneConstants.ClippingPlane4 = Vector4.Zero;
            sceneConstants.ClippingPlane5 = Vector4.Zero;
            sceneConstants.ClippingPlane6 = Vector4.Zero;
            sceneConstants.ClippingPlane7 = Vector4.Zero;

            if (p.ClippingPlanes != null)
            {
                for (int i = 0; i < sceneConstants.NumClippingPlanes; i++)
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
                sceneConstants.StepSize = isIntelGpu ? 4.0f : 2.0f;

            NeedsRender = true;
        }

        public void Dispose()
        {
            Logger.Log("[D3D11VolumeRenderer] Dispose called");

            lock (disposeLock)
            {
                if (_isDisposed) return;
                _isDisposed = true;
                isInitialized = false;
            }

            if (!renderComplete.Wait(5000))
            {
                Logger.Log("[D3D11VolumeRenderer] Warning: Render did not complete in time");
            }

            lock (renderLock)
            {
                context?.ClearState();
                context?.Flush();
            }

            streamingManager?.Dispose();

            pickingTexture?.Dispose();
            pickingRtv?.Dispose();
            pickingStagingTexture?.Dispose();
            sceneTexture?.Dispose();
            sceneRtv?.Dispose();
            sceneSrv?.Dispose();
            overlayTexture?.Dispose();
            overlaySrv?.Dispose();

            grayscaleTextureSrv?.Dispose();
            labelTextureSrv?.Dispose();
            grayscaleTextureCache?.Dispose();
            labelTextureCache?.Dispose();

            chunkInfoBuffer?.Dispose();
            materialBuffer?.Dispose();
            sceneConstantBuffer?.Dispose();

            samplerState?.Dispose();
            pixelShader?.Dispose();
            pickingPixelShader?.Dispose();
            compositePixelShader?.Dispose();
            vertexShader?.Dispose();

            lock (disposeLock)
            {
                renderTargetView?.Dispose();
                renderTargetView = null;

                if (swapChain != null)
                {
                    try
                    {
                        swapChain.SetFullscreenState(false, null);
                        swapChain.Dispose();
                        swapChain = null;
                    }
                    catch { }
                }
            }

            context?.Dispose();
            device?.Dispose();

            renderComplete?.Dispose();

            Logger.Log("[D3D11VolumeRenderer] Disposed");
        }
    }

    internal static class Kernel32
    {
        [DllImport("kernel32.dll", EntryPoint = "CopyMemory", SetLastError = false)]
        public static extern void CopyMemory(IntPtr dest, IntPtr src, int count);
    }
}