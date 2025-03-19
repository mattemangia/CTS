// PixelShader.hlsl

// Textures and samplers
Texture3D<float4> volumeTexture : register(t0);
Texture3D<float4> labelTexture : register(t1);
SamplerState volumeSampler : register(s0);

// Constant buffer for rendering parameters
cbuffer RenderParams : register(b1) 
{
    float opacity;
    float brightness;
    float contrast;
    int renderMode;
    float4 volumeScale;
    int showLabels;
    float2 padding;
}

// Material buffer
struct Material 
{
    float4 color;
};
StructuredBuffer<Material> materials : register(t2);

// Input from vertex shader
struct PixelInput 
{
    float4 position : SV_POSITION;
    float3 texCoord : TEXCOORD0;
    float3 worldPos : TEXCOORD1;
};

// Camera position must be accessible in pixel shader
cbuffer CameraBuffer : register(b0)
{
    matrix worldMatrix;
    matrix viewMatrix;
    matrix projectionMatrix;
    float3 cameraPosition;
    float cameraPadding;
}

// Ray-casting volume rendering
float4 main(PixelInput input) : SV_TARGET 
{
    float3 rayStart = input.texCoord;
    float3 rayDir = normalize(input.worldPos - cameraPosition);
    
    // Ray casting parameters
    const int MAX_STEPS = 512;
    const float STEP_SIZE = 0.005f;
    
    // Accumulate color along ray
    float4 color = float4(0, 0, 0, 0);
    float3 pos = rayStart;
    
    // Main ray-casting loop - tell compiler NOT to unroll
    [loop]
    for (int i = 0; i < MAX_STEPS; i++) 
    {
        // Check if we're outside the volume
        if (any(pos < 0.0f) || any(pos > 1.0f))
            break;
            
        // Sample density from volume
        float density = volumeTexture.Sample(volumeSampler, pos).r;
        
        // Apply brightness and contrast
        density = (density - 0.5f) * contrast + 0.5f + brightness;
        density = saturate(density);
        
        float4 sampleColor = float4(density, density, density, density * opacity);
        
        // If showing labels, blend with material colors
        if (showLabels > 0) 
        {
            uint labelId = (uint)labelTexture.Sample(volumeSampler, pos).r;
            if (labelId > 0) 
            {
                Material mat = materials[min(labelId, 255)];
                sampleColor = lerp(sampleColor, mat.color, mat.color.a);
            }
        }
        
        // Front-to-back compositing
        color.rgb += (1.0f - color.a) * sampleColor.a * sampleColor.rgb;
        color.a += (1.0f - color.a) * sampleColor.a;
        
        // Early ray termination
        if (color.a >= 0.95f)
            break;
            
        // Move along ray
        pos += rayDir * STEP_SIZE;
    }
    
    return color;
}
