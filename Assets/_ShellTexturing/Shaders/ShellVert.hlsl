#pragma enable_d3d11_debug_symbols

struct DrawVertex
{
    float3 position;
    float3 normal;
    float2 uv;
    float4 attributes;
} drawVertices;

struct DrawTriangle
{
    DrawVertex drawVertices[3];
};

StructuredBuffer<DrawTriangle> _DrawTrianglesBuffer;

void ComputeShellVert_float(uint VertexID, out float3 OutPosition, out float3 OutNormal, out float2 OutUV, out float4 OutColor)
{
    DrawTriangle tri = _DrawTrianglesBuffer[VertexID / 3];
    DrawVertex v = tri.drawVertices[VertexID % 3];
    
    OutPosition = v.position;
    OutNormal = v.normal;
    OutUV = v.uv;
    OutColor = v.attributes;
}