cbuffer Guide : register(b0) {
    row_major float4x4 ViewProjection;
    row_major float4x4 InverseViewProjection;
    float4 Camera;
    float4 View;
    float4 Material;
};
struct Vertex { float3 Position : POSITION; float3 Normal : NORMAL; float4 Color : COLOR; float2 UV : TEXCOORD0; };
struct Fragment { float4 Position : SV_POSITION; float3 World : TEXCOORD0; float3 Normal : TEXCOORD1; float4 Color : COLOR; float2 UV : TEXCOORD2; };
Fragment main(Vertex input) {
    Fragment o;
    o.Position = mul(float4(input.Position,1),ViewProjection);
    // Preserve the game's clip planes. Private model depth is evaluated per
    // pixel, rather than interpolating a nonlinear vertex-distance function.
    o.World=input.Position; o.Normal=input.Normal; o.Color=input.Color; o.UV=input.UV;
    return o;
}
