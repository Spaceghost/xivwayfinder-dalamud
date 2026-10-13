Texture2D<float> SceneDepth : register(t0);
Texture2D<float4> BaseColor : register(t1);
SamplerState ModelSampler : register(s0);
cbuffer Guide : register(b0) {
    row_major float4x4 ViewProjection;
    row_major float4x4 InverseViewProjection;
    float4 Camera;
    float4 View; // viewport x/y, scene depth width/height
    float4 Material; // has texture, opacity, alpha cutoff, unlit
};
struct Fragment { float4 Position : SV_POSITION; float3 World : TEXCOORD0; float3 Normal : TEXCOORD1; float4 Color : COLOR; float2 UV : TEXCOORD2; };
struct Output { float4 Color : SV_TARGET; float Depth : SV_DEPTH; };
Output main(Fragment input) {
    float2 uv=input.Position.xy/View.xy;
    float depth=SceneDepth.Load(int3(min(uint2(uv*View.zw),uint2(View.zw)-1),0));
    if (depth>0 && depth<1) {
        float4 h=mul(float4(uv.x*2-1,1-uv.y*2,depth,1),InverseViewProjection);
        if (abs(h.w)>1e-7) {
            float3 scene=h.xyz/h.w;
            if (length(input.World-Camera.xyz)>length(scene-Camera.xyz)+0.015) discard;
        }
    }
    float4 color=input.Color;
    if (Material.x>0.5) color*=BaseColor.Sample(ModelSampler,input.UV);
    if (Material.z > -1.5 && Material.z < -0.5) color.a=1; // OPAQUE
    if (Material.z >= 0) { // MASK: surviving coverage is opaque.
        if (color.a<Material.z) discard;
        color.a=1;
    }
    if (color.a*Material.y<=0.001) discard;
    float3 normal=normalize(input.Normal);
    float light=Material.w>0.5 ? 1 : 0.48+0.52*abs(dot(normal,normalize(float3(-0.4,1,-0.6))));
    Output o;
    o.Color=float4(color.rgb*light,color.a*Material.y);
    float distance=length(input.World-Camera.xyz);
    o.Depth=distance/(1+distance);
    return o;
}
