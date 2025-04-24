cbuffer ShaderParams : register(b0)
{
	float4x4 worldViewProj;
	float4x4 world;
	float4x4 invView;
	float4 thresholds;    // x = minThresholdNorm, y = maxThresholdNorm, z,w unused
	float4 volumeDims;    // x = volWidth, y = volHeight, z = volDepth, w unused
	float4 sliceCoords;   // (not used explicitly in shader logic, reserved)
};

struct VSOut
{
	float4 posH : SV_POSITION;
	float3 localPos : TEXCOORD0;
};

// Vertex Shader (for volume cube and slice planes)
VSOut VSMain(float3 position : POSITION)
{
	VSOut output;
	float4 worldPos = float4(position, 1.0f);
	output.posH = mul(worldPos, worldViewProj);
	// Normalize local coordinates to [0,1] range
	float3 dimsMinus1 = float3(volumeDims.x - 1.0, volumeDims.y - 1.0, volumeDims.z - 1.0);
	output.localPos = position / dimsMinus1;
	return output;
}

// Pixel Shader for volume back faces (store exit position in normalized volume space)
float4 PSVolumeBack(VSOut input) : SV_TARGET
{
	// Output the normalized local position (XYZ) and 1.0 in W
	return float4(input.localPos, 1.0f);
}

// Pixel Shader for volume front faces (raymarch through volume)
Texture2D<float4> ExitTex : register(t0);   // exit positions from back-face pass
Texture3D<float>  VolumeTex : register(t1); // volume intensity data
SamplerState sampLinear : register(s0);
SamplerState sampPoint : register(s1);

float4 PSVolumeFront(VSOut input) : SV_TARGET
{
	// Entry and exit positions in volume (normalized [0,1] coordinate space)
	float3 entryPos = input.localPos;
	int2 pixelCoord = int2(round(input.posH.xy));
	float4 exitSample = ExitTex.Load(int3(pixelCoord, 0));
	float3 exitPos = exitSample.xyz;
	float3 rayDir = exitPos - entryPos;
	// Ray marching parameters
	float maxDim = max(volumeDims.x, max(volumeDims.y, volumeDims.z));
	float stepSize = 0.5f / maxDim;    // step half a voxel length for sampling
	float3 colorAccum = float3(0.0, 0.0, 0.0);
	float hitAlpha = 0.0f;
	// March along the ray from entry to exit
	[loop]
	for (float t = 0.0f; t <= 1.0f; t += stepSize)
	{
		float3 samplePos = entryPos + t * rayDir;
		float intensity = VolumeTex.SampleLevel(sampLinear, samplePos, 0).r;
		// Check if intensity is within threshold range
		if (intensity >= thresholds.x && intensity <= thresholds.y)
		{
			// Record grayscale color and mark hit
			colorAccum = float3(intensity, intensity, intensity);
			hitAlpha = 1.0f;
			break;
		}
	}
	// Output color if hit, else transparent
	if (hitAlpha < 0.5f)
	{
		return float4(0.0, 0.0, 0.0, 0.0);   // no values in range: fully transparent
	}
	else
	{
		return float4(colorAccum, 1.0);
	}
}

// Pixel Shader for slice planes (sample single-voxel slice with threshold)
Texture3D<float> VolumeTex : register(t1);
SamplerState sampPoint : register(s1);

float4 PSSlice(VSOut input) : SV_TARGET
{
	float intensity = VolumeTex.SampleLevel(sampPoint, input.localPos, 0).r;
	if (intensity < thresholds.x || intensity > thresholds.y)
	{
		// Out of threshold range: transparent pixel
		return float4(0.0, 0.0, 0.0, 0.0);
	}
	else
	{
		// Within range: output grayscale color at full opacity
		return float4(intensity, intensity, intensity, 1.0);
	}
}