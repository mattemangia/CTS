// VertexShader.hlsl

// Input/output structures
struct VertexInput {
    float3 position : POSITION;
    float3 texCoord : TEXCOORD;
};

struct PixelInput {
    float4 position : SV_POSITION;
    float3 texCoord : TEXCOORD0;
    float3 worldPos : TEXCOORD1;
};

// Constant buffer
cbuffer CameraBuffer : register(b0) {
    matrix worldMatrix;
    matrix viewMatrix;
    matrix projectionMatrix;
    float3 cameraPosition;
    float padding;
};

// Vertex shader main function
PixelInput main(VertexInput input) {
    PixelInput output;
    
    // Transform vertices
    float4 worldPos = mul(float4(input.position, 1.0f), worldMatrix);
    output.worldPos = worldPos.xyz;
    float4 viewPos = mul(worldPos, viewMatrix);
    output.position = mul(viewPos, projectionMatrix);
    
    // Pass texture coordinates through
    output.texCoord = input.texCoord;
    
    return output;
}
