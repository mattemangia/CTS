// Copyright 2025 Matteo Mangiagalli - matteo.mangiagalli@unifr.ch
using CTS;
using CTS.D3D11;
using ILGPU.Util;
using Krypton.Workspace;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
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
using static CTS.MeasurementTextRenderer;
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
using Matrix4x4 = System.Numerics.Matrix4x4;
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
Texture2D<float4>     scaleBarTexture : register(t4);
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

// Draws the 2D scale bar overlay.
float4 drawScaleBar(float2 uv)
{
    if (showScaleBar < 0.5) return float4(0, 0, 0, 0);
    float worldSizePerPixel = cameraDistance * pixelSize / 500.0;
    float targetPixels = 150.0;
    float worldSize = targetPixels * worldSizePerPixel;
    float scale = pow(10, floor(log10(worldSize)));
    float normalizedSize = worldSize / scale;
    float actualSize;
    if (normalizedSize < 1.5) actualSize = scale;
    else if (normalizedSize < 3.5) actualSize = 2 * scale;
    else if (normalizedSize < 7.5) actualSize = 5 * scale;
    else actualSize = 10 * scale;
    float barLengthPixels = actualSize / worldSizePerPixel;
    float barLengthNorm = barLengthPixels / screenDimensions.x;
    float barHeight = 0.008;
    barLengthNorm = min(barLengthNorm, 0.3);
    float2 barPos;
    if (scaleBarPosition < 0.5) barPos = float2(0.05, 0.94);
    else if (scaleBarPosition < 1.5) barPos = float2(0.95 - barLengthNorm, 0.94);
    else if (scaleBarPosition < 2.5) barPos = float2(0.05, 0.06);
    else barPos = float2(0.95 - barLengthNorm, 0.06);
    if (uv.x >= barPos.x && uv.x <= barPos.x + barLengthNorm && uv.y >= barPos.y && uv.y <= barPos.y + barHeight)
    {
        float segmentSize = barLengthNorm / 5.0;
        int segment = (int)((uv.x - barPos.x) / segmentSize);
        float3 color = (segment % 2 == 0) ? float3(1, 1, 1) : float3(0, 0, 0);
        float borderDist = min(min(uv.x - barPos.x, barPos.x + barLengthNorm - uv.x), min(uv.y - barPos.y, barPos.y + barHeight - uv.y));
        if (borderDist < 0.0015) color = float3(0.5, 0.5, 0.5);
        return float4(color, 1.0);
    }
    if (showScaleText > 0.5)
    {
        float textY, textHeight = 0.05;
        if (scaleBarPosition < 2.0) textY = barPos.y - textHeight - 0.01; else textY = barPos.y + barHeight + 0.01;
        if (uv.x >= barPos.x && uv.x <= barPos.x + barLengthNorm && uv.y >= textY && uv.y <= textY + textHeight)
        {
            float2 textUV = float2((uv.x - barPos.x) / barLengthNorm, (uv.y - textY) / textHeight);
            if (all(textUV >= 0.0) && all(textUV <= 1.0))
            {
                float4 textColor = scaleBarTexture.Sample(linearSampler, textUV);
                if (textColor.a > 0.5) return float4(textColor.rgb, 1.0);
            }
        }
    }
    return float4(0, 0, 0, 0);
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
    // Process planes in pairs for mirrored planes
    int i = 0;
    while (i < (int)numClippingPlanes)
    {
        float4 plane1 = clippingPlanes[i];
        
        // Check if next plane is a mirror (opposite normal, negated distance)
        bool isMirrorPair = false;
        if (i + 1 < (int)numClippingPlanes)
        {
            float4 plane2 = clippingPlanes[i + 1];
            float3 sumNormals = plane1.xyz + plane2.xyz;
            float sumDist = plane1.w + plane2.w;
            
            // Check if normals are opposite and distances sum to ~0
            if (length(sumNormals) < 0.1 && abs(sumDist) < 0.1)
            {
                isMirrorPair = true;

                // --- START FIX: Modified mirror plane behavior ---
                // The original logic created a slab symmetric around the origin, which was confusing.
                // New logic: Create a slab between the origin (0) and the user-defined plane (plane1.w).
                // This prevents the volume from disappearing at distance=0 and provides a more intuitive curtain effect.

                float D = plane1.w; // The distance from the UI slider.
        float signedDist = dot(worldPos - volumeDimensions.xyz * 0.5, plane1.xyz);
                
                // Define the slab between the origin and the plane at distance D.
                // We clip if the point is outside this slab.
                if (D >= 0)
                {
                    // If D is positive, the slab is from 0 to D. Clip if outside [0, D].
                    if (signedDist > D || signedDist< 0) return true;
                }
                else // D is negative
                {
                    // If D is negative, the slab is from D to 0. Clip if outside [D, 0].
                    if (signedDist<D || signedDist> 0) return true;
                }
// --- END FIX ---

i += 2; // Skip the mirror plane
continue;
            }
        }
        
        // Single plane - standard plane equation
        if (!isMirrorPair)
{
    // For a single plane: dot(point - center, normal) > distance
    float dist = dot(worldPos - volumeDimensions.xyz * 0.5, plane1.xyz) - plane1.w;
    if (dist > 0)
        return true;
}

i++;
    }
    return false;
}

// Check distance to nearest clipping plane
float getClippingPlaneDistance(float3 worldPos)
{
    float minDist = 1000000.0;
    int i = 0;

    while (i < (int)numClippingPlanes)
    {
        float4 plane1 = clippingPlanes[i];

        // Check if this is part of a mirror pair
        bool isMirrorPair = false;
        if (i + 1 < (int)numClippingPlanes)
        {
            float4 plane2 = clippingPlanes[i + 1];
            float3 sumNormals = plane1.xyz + plane2.xyz;
            float sumDist = plane1.w + plane2.w;

            if (length(sumNormals) < 0.1 && abs(sumDist) < 0.1)
            {
                isMirrorPair = true;

                // For mirror pairs, distance is from the center of the slab
                float signedDist = dot(worldPos - volumeDimensions.xyz * 0.5, plane1.xyz);
                float distFromSlab = max(0.0, abs(signedDist) - abs(plane1.w));
                minDist = min(minDist, distFromSlab);

                i += 2;
                continue;
            }
        }

        // Single plane
        if (!isMirrorPair)
        {
            float dist = abs(dot(worldPos - volumeDimensions.xyz * 0.5, plane1.xyz) - plane1.w);
            minDist = min(minDist, dist);
        }

        i++;
    }

    return minDist;
}


// Improved clipping plane visualization
float4 drawClippingPlaneVisual(float3 worldPos)
{
    if (drawClippingPlanes < 0.5 || numClippingPlanes < 0.5)
        return float4(0, 0, 0, 0);

    float4 planeColor = float4(0, 0, 0, 0);
    float planeThickness = max(stepSize * 2.0, 2.0);

    int i = 0;
    while (i < (int)numClippingPlanes)
    {
        float4 plane1 = clippingPlanes[i];

        // Check for mirror pair
        bool isMirrorPair = false;

        if (i + 1 < (int)numClippingPlanes)
        {
            float4 plane2 = clippingPlanes[i + 1];
            float3 sumNormals = plane1.xyz + plane2.xyz;
            float sumDist = plane1.w + plane2.w;

            if (length(sumNormals) < 0.1 && abs(sumDist) < 0.1)
            {
                isMirrorPair = true;

                // --- START FIX: Modified mirror plane visualization ---
                // Original logic drew planes at +D and -D.
                // New logic draws one plane at the user-defined distance D, and the other at the origin (0).
                float signedDist = dot(worldPos - volumeDimensions.xyz * 0.5, plane1.xyz);

                // dist1: distance from the movable plane at D
                float dist1 = abs(signedDist - plane1.w);
                // dist2: distance from the fixed plane at the origin
                float dist2 = abs(signedDist);

                float minDist = min(dist1, dist2);
                // --- END FIX ---

                if (minDist < planeThickness)
                {
                    // Create a gradient effect at the plane
                    float alpha = 1.0 - (minDist / planeThickness);
                    alpha = pow(alpha, 2.0); // Make it sharper

                    // Different colors for different planes
                    float3 color;
                    int colorIndex = i / 2;

                    // Use brighter colors for mirror pairs
                    if (colorIndex == 0) color = float3(1.0, 1.0, 0.3); // Yellow
                    else if (colorIndex == 1) color = float3(0.3, 1.0, 1.0); // Cyan
                    else if (colorIndex == 2) color = float3(1.0, 0.3, 1.0); // Magenta
                    else color = float3(0.9, 0.9, 0.9); // Light gray

                    // Make the exact plane positions brighter
                    if (minDist < 0.5)
                    {
                        color = lerp(color, float3(1, 1, 1), 0.5);
                        alpha = min(alpha + 0.3, 0.8);
                    }

                    // Add a subtle fill between the planes
                    // Fill if we are between 0 and D
                    if ((signedDist < plane1.w && signedDist > 0) || (signedDist > plane1.w && signedDist < 0))
                    {
                        float fillAlpha = 0.05;
                        planeColor.rgb = planeColor.rgb + color * fillAlpha * (1.0 - planeColor.a);
                        planeColor.a = planeColor.a + fillAlpha * (1.0 - planeColor.a);
                    }

                    // Blend with existing color
                    planeColor.rgb = planeColor.rgb + color * alpha * (1.0 - planeColor.a);
                    planeColor.a = planeColor.a + alpha * (1.0 - planeColor.a);
                }
            }
        }

        if (!isMirrorPair)
        {
            // Single plane visualization (original code)
            float dist = abs(dot(worldPos - volumeDimensions.xyz * 0.5, plane1.xyz) - plane1.w);

            if (dist < planeThickness)
            {
                float alpha = 1.0 - (dist / planeThickness);
                alpha = pow(alpha, 2.0);

                float3 color;
                if (i == 0) color = float3(1.0, 0.3, 0.3); // Red
                else if (i == 1) color = float3(0.3, 1.0, 0.3); // Green
                else if (i == 2) color = float3(0.3, 0.3, 1.0); // Blue
                else color = float3(0.7, 0.7, 0.7); // Gray

                if (dist < 0.5)
                {
                    color = lerp(color, float3(1, 1, 1), 0.5);
                    alpha = min(alpha + 0.3, 0.8);
                }

                planeColor.rgb = planeColor.rgb + color * alpha * (1.0 - planeColor.a);
                planeColor.a = planeColor.a + alpha * (1.0 - planeColor.a);
            }
        }

        // Skip the mirror plane if it's a pair
        i += isMirrorPair ? 2 : 1;
    }

    return planeColor;
}

float4 main(float4 pos : SV_POSITION, float3 screenPos : TEXCOORD0) : SV_TARGET
{
    float2 uv = pos.xy / screenDimensions;
float4 scaleBarColor = drawScaleBar(uv);
if (scaleBarColor.a > 0.5) { return scaleBarColor; }

float3 boxMin = float3(0, 0, 0);
float3 boxMax = volumeDimensions.xyz;
float4 clip = float4(uv * 2.0 - 1.0, 0.0, 1.0);
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

// First pass: render clipping planes if enabled
if (drawClippingPlanes > 0.5 && numClippingPlanes > 0)
{
    // Ray march specifically for plane visualization
    float planeT = tmin;
    float planeStepSize = min(baseStepSize, 1.0);

    [loop]
    for (int j = 0; j < 100; j++) // Limited steps for performance
    {
        if (planeT > tmax) break;

        float3 planePos = rayOrigin + rayDir * planeT;
        float4 planeVis = drawClippingPlaneVisual(planePos);

        if (planeVis.a > 0.01)
        {
            // Accumulate plane visualization
            accumulatedColor = planeVis.rgb * planeVis.a + accumulatedColor * (1.0 - planeVis.a);
            accumulatedAlpha = planeVis.a + accumulatedAlpha * (1.0 - planeVis.a);

            if (accumulatedAlpha > 0.95) break;
        }

        planeT += planeStepSize;
    }
}

// Main volume rendering pass
[loop]
for (int i = 0; i < maxSteps; i++)
{
    if (accumulatedAlpha > 0.98f) break;
    if (t > tmax) break;

    float3 worldPos = rayOrigin + rayDir * t;

    // Check for slice planes first
    float4 sliceColor = checkSlice(worldPos);
    if (sliceColor.a > 0.01f)
    {
        finalColor = float4(sliceColor.rgb * sliceColor.a + accumulatedColor * (1.0f - sliceColor.a),
                            sliceColor.a + accumulatedAlpha * (1.0f - sliceColor.a));
        return finalColor;
    }

    // Skip clipped regions
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
        // FIXED: Proper opacity handling
        float sampleAlpha;

        if (labelIndex > 0 && labelIndex < 256)
        {
            // For labeled materials, use the opacity more directly
            // This gives better control across the full range
            float materialOpacity = sampleColor.a;

            // Apply a gentler exponential function for natural blending
            // This preserves the full range of the opacity slider
            sampleAlpha = 1.0f - exp(-materialOpacity * baseStepSize * 2.0f);

            // For very low opacity values, ensure minimum visibility
            if (materialOpacity > 0.0f && materialOpacity < 0.1f)
            {
                sampleAlpha = max(sampleAlpha, materialOpacity * baseStepSize * 0.5f);
            }
        }
        else
        {
            // For grayscale data, use stronger accumulation
            float density_multiplier = 4.0f;
            sampleAlpha = 1.0f - exp(-sampleColor.a * baseStepSize * density_multiplier);
        }

        // Apply lighting
        float light = 0.8f + 0.2f * dot(normalize(worldPos - cameraPosition.xyz), -rayDir);
        sampleColor.rgb *= light;

        // Accumulate color and alpha
        accumulatedColor += sampleColor.rgb * sampleAlpha * (1.0f - accumulatedAlpha);
        accumulatedAlpha += sampleAlpha * (1.0f - accumulatedAlpha);

        emptySteps = 0;
    }
    else
    {
        emptySteps++;
    }

    // Adaptive step size
    float currentStepSize = baseStepSize;
    if (emptySteps > 4)
    {
        currentStepSize = baseStepSize * 2.0f;
    }

    // Near clipping planes, use smaller steps
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

// Scale bar text texture
private ID3D11Texture2D scaleBarTexture;
private ID3D11ShaderResourceView scaleBarTextureSrv;
private float lastScaleBarValue = -1;

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
            CreateScaleBarTexture();

            streamingManager = new ChunkStreamingManager(device, context, volumeData, labelData, grayscaleTextureCache, labelTextureCache);
        }
        else
        {
            Logger.Log("[D3D11VolumeRenderer] Warning: Volume data is null");
            totalChunks = 1;
            CreateShaders();
            CreateConstantBuffers();
            CreateSamplerState();
            CreateScaleBarTexture();
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

    CreateRenderTargetView();
}


private void CreateRenderTargetView()
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
            Logger.Log($"[CreateRenderTargetView] Error: {ex.Message}");
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

private void CreateScaleBarTexture()
{
    try
    {
        // Create a blank texture for scale bar text
        var desc = new Texture2DDescription
        {
            Width = 256,
            Height = 64,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.Write
        };

        scaleBarTexture = device.CreateTexture2D(desc);
        scaleBarTextureSrv = device.CreateShaderResourceView(scaleBarTexture);

        // Initialize with transparent texture
        var mapped = context.Map(scaleBarTexture, 0, MapMode.WriteDiscard);
        unsafe
        {
            uint* dst = (uint*)mapped.DataPointer;
            int pixelCount = 256 * 64;
            for (int i = 0; i < pixelCount; i++)
            {
                dst[i] = 0; // Transparent black
            }
        }
        context.Unmap(scaleBarTexture, 0);
    }
    catch (Exception ex)
    {
        Logger.Log($"[CreateScaleBarTexture] Warning: Failed to create scale bar texture: {ex.Message}");
        // Continue without scale bar text - the shader will handle the null case
    }
}

private void UpdateScaleBarTexture(float scaleValue)
{
    if (_isDisposed || scaleBarTexture == null || context == null) return;

    // Only update if value changed significantly
    if (Math.Abs(scaleValue - lastScaleBarValue) < 0.01f) return;
    lastScaleBarValue = scaleValue;

    try
    {
        // Create bitmap for text rendering
        using (var bitmap = new Bitmap(256, 64, PixelFormat.Format32bppArgb))
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

            // Determine units and format text
            string text;
            float pixelSizeMicrometers = sceneConstants.PixelSize * 1e6f;

            if (pixelSizeMicrometers < 100) // Small pixels - use µm
            {
                float displayValue = scaleValue * 1000; // mm to µm
                text = $"{displayValue:F0} µm";
            }
            else if (scaleValue >= 10) // Large scale - use cm
            {
                float displayValue = scaleValue / 10; // mm to cm
                text = $"{displayValue:F1} cm";
            }
            else // Default - use mm
            {
                text = $"{scaleValue:F1} mm";
            }

            // Draw text centered
            using (var font = new Font("Arial", 24, FontStyle.Bold))
            using (var brush = new SolidBrush(Color.White))
            {
                var textSize = g.MeasureString(text, font);
                float x = (256 - textSize.Width) / 2;
                float y = (64 - textSize.Height) / 2;

                // Draw text with black outline for better visibility
                using (var outlineBrush = new SolidBrush(Color.Black))
                {
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        for (int dy = -2; dy <= 2; dy++)
                        {
                            if (dx != 0 || dy != 0)
                                g.DrawString(text, font, outlineBrush, x + dx, y + dy);
                        }
                    }
                }

                g.DrawString(text, font, brush, x, y);
            }

            // Upload to GPU
            var rect = new Rectangle(0, 0, 256, 64);
            var bitmapData = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

            try
            {
                var mapped = context.Map(scaleBarTexture, 0, MapMode.WriteDiscard);
                unsafe
                {
                    byte* src = (byte*)bitmapData.Scan0;
                    byte* dst = (byte*)mapped.DataPointer;

                    for (int row = 0; row < 64; row++)
                    {
                        Buffer.MemoryCopy(src + row * bitmapData.Stride,
                                          dst + row * mapped.RowPitch,
                                          256 * 4, 256 * 4);
                    }
                }
                context.Unmap(scaleBarTexture, 0);
            }
            finally
            {
                bitmap.UnlockBits(bitmapData);
            }
        }
    }
    catch (Exception ex)
    {
        Logger.Log($"[UpdateScaleBarTexture] Warning: Failed to update scale bar texture: {ex.Message}");
    }
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
        CreateRenderTargetView();

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

    // Check if we're already rendering
    if (_isRendering) return;

    try
    {
        _isRendering = true;
        renderComplete.Reset();

        // Validate all resources under lock
        bool canRender = false;
        lock (disposeLock)
        {
            canRender = !_isDisposed &&
                       device != null &&
                       context != null &&
                       swapChain != null &&
                       renderTargetView != null &&
                       streamingManager != null;
        }

        if (!canRender) return;

        lock (renderLock)
        {
            // Double-check after acquiring render lock
            lock (disposeLock)
            {
                if (_isDisposed || renderTargetView == null) return;
            }

            try
            {
                // Handle device lost state
                if (deviceLost && !HandleDeviceLost())
                {
                    return;
                }

                streamingManager.Update(camera);
                if (!NeedsRender && !streamingManager.IsDirty()) return;

                // Update camera distance for dynamic scale bar
                var volumeCenter = new Vector3(
                    sceneConstants.VolumeDimensions.X * 0.5f,
                    sceneConstants.VolumeDimensions.Y * 0.5f,
                    sceneConstants.VolumeDimensions.Z * 0.5f);
                sceneConstants.CameraDistance = Vector3.Distance(camera.Position, volumeCenter);

                // Update scale bar texture if needed
                if (sceneConstants.ShowScaleText > 0.5f)
                {
                    float worldSizePerPixel = sceneConstants.CameraDistance * sceneConstants.PixelSize / 500.0f;
                    float targetPixels = 150.0f;
                    float worldSize = targetPixels * worldSizePerPixel;

                    float scale = (float)Math.Pow(10, Math.Floor(Math.Log10(worldSize)));
                    float normalizedSize = worldSize / scale;
                    float actualSize;

                    if (normalizedSize < 1.5f)
                        actualSize = scale;
                    else if (normalizedSize < 3.5f)
                        actualSize = 2 * scale;
                    else if (normalizedSize < 7.5f)
                        actualSize = 5 * scale;
                    else
                        actualSize = 10 * scale;

                    UpdateScaleBarTexture(actualSize * 1000.0f); // Convert to mm
                }

                // Set render target
                lock (disposeLock)
                {
                    if (_isDisposed || renderTargetView == null) return;
                    context.OMSetRenderTargets(renderTargetView);
                }

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

                    // Ensure scale bar texture SRV is available
                    var srvs = new ID3D11ShaderResourceView[5];
                    srvs[0] = materialSrv;
                    srvs[1] = chunkInfoSrv;
                    srvs[2] = grayscaleTextureSrv;
                    srvs[3] = labelTextureSrv;
                    srvs[4] = scaleBarTextureSrv; // Can be null, shader will handle it

                    context.PSSetShaderResources(0, srvs);

                    context.IASetInputLayout(null);
                    context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                    context.Draw(3, 0);
                }
                finally
                {
                    // Clear shader resources to prevent resource conflicts
                    context.PSSetShaderResources(0, new ID3D11ShaderResourceView[] { null, null, null, null, null });
                    context.PSSetShader(null);
                    context.VSSetShader(null);
                    context.PSSetConstantBuffers(0, new ID3D11Buffer[] { null });
                    context.PSSetSamplers(0, new ID3D11SamplerState[] { null });
                    context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>(), null);

                    chunkInfoSrv?.Dispose();
                    materialSrv?.Dispose();
                }

                // Present with validation
                lock (disposeLock)
                {
                    if (!_isDisposed && swapChain != null)
                    {
                        try
                        {
                            // For Intel GPUs, use more conservative present parameters
                            if (isIntelGpu)
                            {
                                swapChain.Present(0, PresentFlags.None); // No VSync for Intel
                            }
                            else
                            {
                                swapChain.Present(1, PresentFlags.None); // VSync for others
                            }
                            NeedsRender = false;
                        }
                        catch (SharpGenException sgex) when ((uint)sgex.HResult == 0x887A0005 ||
                                                              (uint)sgex.HResult == 0x887A0007 ||
                                                              (uint)sgex.HResult == 0x887A0001)
                        {
                            Logger.Log($"[Render] Present failed with DXGI error: 0x{sgex.HResult:X}");
                            deviceLost = true;
                        }
                    }
                }
            }
            catch (SharpGenException sgex) when ((uint)sgex.HResult == 0x887A0005) // DXGI_ERROR_DEVICE_REMOVED
            {
                deviceLost = true;
                var reason = device.DeviceRemovedReason;
                Logger.Log($"[Render] Device removed detected. Reason: {reason.Code.ToString("X")}");

                // Reduce quality for recovery
                sceneConstants.Quality = 0;
                sceneConstants.StepSize = isIntelGpu ? 8.0f : 4.0f;
            }
            catch (SharpGenException sgex) when ((uint)sgex.HResult == 0x887A0007) // DXGI_ERROR_DEVICE_RESET
            {
                deviceLost = true;
                Logger.Log("[Render] Device reset detected");
            }
            catch (Exception ex)
            {
                Logger.Log($"[Render] Error during rendering: {ex.Message}");
                if (ex.Message.Contains("device") || ex.Message.Contains("Device"))
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

public void Resize(int width, int height)
{
    if (_isDisposed || device == null || width <= 0 || height <= 0) return;

    // Wait for any ongoing render to complete
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

                context.Flush();
                swapChain.ResizeBuffers(2, width, height, Format.R8G8B8A8_UNorm, SwapChainFlags.None);

                CreateRenderTargetView();
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

        // Adjust step size based on GPU and movement
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

    // Limit quality for Intel GPUs
    if (isIntelGpu && p.Quality > 0)
    {
        sceneConstants.Quality = 0; // Force lowest quality for Intel
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

    // Ensure minimum step size
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

    // Wait for any ongoing render to complete (with timeout)
    if (!renderComplete.Wait(5000))
    {
        Logger.Log("[D3D11VolumeRenderer] Warning: Render did not complete in time");
    }

    lock (renderLock)
    {
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
    scaleBarTextureSrv?.Dispose();
    scaleBarTexture?.Dispose();
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

    lock (disposeLock)
    {
        renderTargetView?.Dispose();
        renderTargetView = null;

        // Dispose swap chain and device last
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
}