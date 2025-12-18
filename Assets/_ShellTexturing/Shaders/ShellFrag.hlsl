#ifndef COMPUTE_FRAG_INCLUDED
#define COMPUTE_FRAG_INCLUDED

float hash(uint n) {
    // integer hash copied from Hugo Elias
    n = (n << 13U) ^ n;
    n = n * (n * n * 15731U + 0x789221U) + 0x1376312589U;
    return float(n & uint(0x7fffffffU)) / float(0x7fffffff);
}

void ShellFragCompute_float(float2 UV, float Density, float Height, float Thickness, float ShellIndex, float3 Normal, float3 LightDirection, float Attenuation, float OcclusionBias, float4 BaseColor, float NoiseOffset, out float3 Color)
{
    float2 scaledUV = UV * Density;
    float2 localUV = frac(scaledUV) * 2 - 1;
    
    float dist = length(localUV);
    
    uint2 tid = scaledUV;
    uint seed = tid.x + 100 * tid.y + 100 * 10;
    
    float rand = hash(seed);
    rand = clamp(rand, rand + NoiseOffset, .8f);

    if (dist > Thickness * (rand - Height) * BaseColor.a && ShellIndex > 0)
        discard;

    float ndotl = clamp(dot(Normal, -LightDirection) * 0.5f + 0.5f, -1.0f, 1.0f);
    ndotl = ndotl * ndotl;

    float ambientOcclusion = pow(Height, Attenuation);
    ambientOcclusion += OcclusionBias;
    ambientOcclusion = saturate(ambientOcclusion);
    
    Color = float4(BaseColor.xyz * ndotl * ambientOcclusion, 1.0f);
}

#endif

