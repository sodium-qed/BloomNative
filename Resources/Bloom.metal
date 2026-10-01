#include <metal_stdlib>
using namespace metal;
struct Uniforms { float2 resolution; float time; float progress; float breath; float imageAspect; float frameCount; float flags; };
struct VertexOut { float4 position [[position]]; float2 uv; };
vertex VertexOut bloomVertex(uint id [[vertex_id]]) {
    float2 p = float2((id << 1) & 2, id & 2);
    return {float4(p * 2.0 - 1.0, 0, 1), float2(p.x, 1.0 - p.y)};
}
float3 originalFrame(texture2d_array<float> y, texture2d_array<float> cbcr, float2 uv, uint frame, int flags) {
    constexpr sampler s(filter::linear,address::clamp_to_edge);
    float luma = y.sample(s,uv,frame).r;
    float2 c = cbcr.sample(s,uv,frame).rg - 128.0/255.0;
    if (flags & 1) { luma = (luma - 16.0/255.0) * (255.0/219.0); c *= 255.0/224.0; }
    if (flags & 2) return float3(luma+1.5748*c.y,luma-0.187324*c.x-0.468124*c.y,luma+1.8556*c.x);
    return float3(luma+1.402*c.y,luma-0.344136*c.x-0.714136*c.y,luma+1.772*c.x);
}
fragment float4 bloomFragment(VertexOut in [[stage_in]], constant Uniforms &u [[buffer(0)]], texture2d_array<float> luma [[texture(0)]], texture2d_array<float> chroma [[texture(1)]]) {
    float2 p = in.uv - 0.5;
    float aspect = u.resolution.x / u.resolution.y;
    // Fill the desktop with the actual video; no flower deformation or invented petal motion.
    if (aspect > u.imageAspect) p.y *= u.imageAspect/aspect;
    else p.x *= aspect/u.imageAspect;
    p /= 1.0 + (sin(u.time*0.42)+1.0) * 0.003 * u.breath;
    p += float2(sin(u.time*0.24),cos(u.time*0.19)) * 0.001 * u.breath;
    float2 uv = p + 0.5;
    float position = clamp(u.progress,0.0,1.0) * (u.frameCount-1.0);
    uint a = uint(floor(position)), b = min(a+1,uint(u.frameCount-1.0));
    float3 color = mix(originalFrame(luma,chroma,uv,a,int(u.flags)),originalFrame(luma,chroma,uv,b,int(u.flags)),fract(position));
    return float4(clamp(color,0.0,1.0),1);
}
