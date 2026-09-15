// Shared blade logic for every pass of Grass/Blade, in both the URP and the Built-in SubShader.
// Include this AFTER the pipeline header (Core.hlsl or UnityCG.cginc): it relies on unity_ObjectToWorld,
// UNITY_MATRIX_V, UNITY_MATRIX_VP, _Time and _WorldSpaceCameraPos being defined by that header.
//
// Per-vertex data baked by GrassSpawner:
//   uv0     : blade texture UV (v = 0 root, 1 tip)
//   uv1.x   : per-blade random 0..1
//   uv1.yzw : blade root position (object space)
#ifndef GRASS_BLADE_COMMON_INCLUDED
#define GRASS_BLADE_COMMON_INCLUDED

#define GRASS_MAX_INTERACTORS 8

sampler2D _MainTex;
sampler2D _TintMap;
sampler2D _ColorRamp;   // root -> tip color, baked by GrassSpawner
sampler2D _TrampleMap;  // RG = push direction, B = amount; black = no trail

// Material properties (one CBUFFER so URP's SRP Batcher can batch chunks of one spawner).
CBUFFER_START(UnityPerMaterial)
    half  _Cutoff;
    half  _UseTextureColor;
    half  _GradientByHeight;
    float _MaxBladeHeight;
    half  _ColorVariation;
    float4 _TintBounds;   // xy = offset, zw = 1/size  (local XZ -> 0..1)
    float4 _TintTiling;   // xy = tiling, zw = offset
    float4 _TintParams;   // x = strength, y = blur mip level
    half4  _TintColor;    // multiplied into the tint map sample (recolor the map without editing the texture)
    half  _LightInfluence;
    half  _ViewFacing;
    float4 _WindDir;
    float _WindStrength;
    float _WindSpeed;
    float _WindFrequency;
    half  _WindHighlight;
    float _InteractStrength;
    half  _BendDarken;
    half  _ShadowStrength; // URP only: how much received shadows darken the grass
    half4 _GroundColor;    // color the blade base blends into (set by GrassSpawner)
    float4 _GroundBlend;   // x = blend height (0..1 along the ramp), y = strength (0 = off), z = 0 color / 1 alpha fade
CBUFFER_END

// Globals uploaded by GrassInteractor / GrassSpawner each frame.
float4 _GrassInteractors[GRASS_MAX_INTERACTORS];      // xyz = world pos, w = radius
float4 _GrassInteractorParams[GRASS_MAX_INTERACTORS]; // x = strength
float  _GrassInteractorCount;                          // float for GLES/WebGL portability
float4 _GrassLightDir;                                 // direction the light travels
half4  _GrassLightColor;                               // main light color * intensity (white if no light)
half4  _GrassAmbientColor;                             // scene ambient (RenderSettings) sampled for an upward normal
float4 _GrassFade;                                     // x = fade start distance, y = 1/range (0 = off)

struct GrassAttributes
{
    float4 vertex : POSITION;
    float2 uv     : TEXCOORD0;
    float4 uv1    : TEXCOORD1;
    half4  color  : COLOR;     // per-blade ground color baked by GrassSpawner (white when not baked)
};

struct GrassSurface
{
    float3 posWS;
    float3 rootWS;
    float3 normalWS;  // stylized per-blade normal (up, leaning with wind / push)
    float3 uv;        // xy = texture, z = color ramp position
    half3  tint;      // per-vertex shading
    half3  ground;    // ground color under this blade (vertex color * _GroundColor)
};

// Displaces one vertex: view-facing, distance fade, wind, interaction, trail. Also computes shading.
GrassSurface GrassDisplace(GrassAttributes v)
{
    GrassSurface s;

    float rand = v.uv1.x;
    float h = saturate(v.uv.y);          // 0 = root, 1 = tip
    float bend = h * h;                  // root never moves, tip moves most

    float3 rootWS = mul(unity_ObjectToWorld, float4(v.uv1.yzw, 1.0)).xyz;
    float3 posWS  = mul(unity_ObjectToWorld, v.vertex).xyz;

    // ---- Perspective optimisation: align every card to the camera view direction (one shared plane, no
    // per-blade swivel) and lean tips away from a camera that looks down. Per-vertex, no extra geometry.
    {
        float3 camFwd = -UNITY_MATRIX_V[2].xyz;
        float2 fwdXZ = camFwd.xz;
        float fwdLen = length(fwdXZ);
        fwdXZ = fwdLen > 1e-3 ? fwdXZ / fwdLen : float2(0.0, 1.0);
        float2 rightXZ = float2(fwdXZ.y, -fwdXZ.x);

        float2 offXZ = posWS.xz - rootWS.xz;        // baked half-width offset (root +/- right)
        float halfW = length(offXZ);
        float side = v.uv.x * 2.0 - 1.0;
        posWS.xz = rootWS.xz + lerp(offXZ, rightXZ * halfW * side, _ViewFacing);

        float pitch = saturate(-camFwd.y);
        float tipH = max(posWS.y - rootWS.y, 0.0);
        posWS.xz += fwdXZ * (_ViewFacing * pitch * h * h * tipH);
    }

    // Color ramp position: by real height (short blades stay in root colors) or by card UV.
    float heightRatio = saturate((posWS.y - rootWS.y) / max(_MaxBladeHeight, 1e-3));
    float rampT = lerp(h, heightRatio, _GradientByHeight);

    // ---- Distance fade: shrink blades into the ground near the cull distance
    float camDist = distance(rootWS, _WorldSpaceCameraPos.xyz);
    float fade = 1.0 - saturate((camDist - _GrassFade.x) * _GrassFade.y);
    posWS = rootWS + (posWS - rootWS) * fade;

    float bladeHeight = max(posWS.y - rootWS.y, 1e-4);

    // ---- Wind
    float2 windDir = normalize(_WindDir.xz + float2(1e-4, 0));
    float phase = dot(rootWS.xz, windDir) * _WindFrequency + _Time.y * _WindSpeed;
    float slow  = sin(_Time.y * _WindSpeed * 0.31 + rootWS.x * 0.07 + rootWS.z * 0.05) * 0.15;
    float gustField = sin(phase) * 0.6 + sin(phase * 2.17 + 1.3) * 0.25 + slow;                 // coherent: shading
    float gust = sin(phase + rand * 6.2831853) * 0.6 + sin(phase * 2.17 + 1.3 + rand * 3.1) * 0.25 + slow; // per blade: motion
    float2 windOffset = windDir * (gust + 0.35) * _WindStrength * bend * bladeHeight;
    float2 leanField = windDir * (gustField + 0.35) * _WindStrength;

    // ---- Interaction: direct push from up to 8 spheres
    float2 push = 0;
    float pushAmt = 0;
    int count = (int)_GrassInteractorCount;
    [loop]
    for (int i = 0; i < count; i++)
    {
        float4 it = _GrassInteractors[i];
        float3 d = rootWS - it.xyz;
        float distXZ = length(d.xz);
        float radial = saturate((it.w - distXZ) / max(it.w, 1e-3));
        float vertical = saturate(1.0 - abs(d.y) / max(it.w * 2.0, 1e-3));
        float f = radial * vertical * _GrassInteractorParams[i].x;
        float2 dir = distXZ > 1e-4 ? d.xz / distXZ : float2(0.0, 1.0);
        push += dir * f;
        pushAmt = max(pushAmt, f);
    }
    // Trail left by interactors that already passed (decays in the spawner's trample map)
    float2 trampleUV = (v.uv1.yw + _TintBounds.xy) * _TintBounds.zw;
    half4 tr = tex2Dlod(_TrampleMap, float4(trampleUV, 0, 0));
    push += (tr.rg * 2.0 - 1.0) * tr.b;
    pushAmt = max(pushAmt, tr.b);

    float pushLen = length(push);
    if (pushLen > 1.0) push /= pushLen;
    float pushMag = saturate(pushLen);
    float2 pushOffset = push * _InteractStrength * bend * bladeHeight;

    posWS.xz += windOffset + pushOffset;
    posWS.y -= (posWS.y - rootWS.y) * pushMag * 0.65 * bend; // flatten trampled blades

    // ---- Per-vertex shading
    float2 tintUV = (v.uv1.yw + _TintBounds.xy) * _TintBounds.zw * _TintTiling.xy + _TintTiling.zw;
    half3 tintSample = tex2Dlod(_TintMap, float4(tintUV, 0, _TintParams.y)).rgb * _TintColor.rgb;
    half3 tint = lerp(half3(1, 1, 1), tintSample, _TintParams.x);
    tint *= lerp(1.0 - _ColorVariation, 1.0, rand);
    tint *= 1.0 + gustField * _WindHighlight;
    tint *= 1.0 - pushAmt * _BendDarken;

    // Stylized lighting that follows the scene: main light color * intensity with a soft wrap term, plus
    // ambient. _LightInfluence blends from unlit (1) to fully lit so the look can stay flat if wanted.
    float2 lean = leanField + push * _InteractStrength;
    float3 n = normalize(float3(lean.x, 1.0, lean.y));
    float ndl = saturate(dot(n, -normalize(_GrassLightDir.xyz + float3(0, -1e-4, 0))));
    half3 lit = _GrassLightColor.rgb * (0.7h + 0.3h * ndl) + _GrassAmbientColor.rgb;
    tint *= lerp(half3(1, 1, 1), lit, _LightInfluence);

    s.posWS = posWS;
    s.rootWS = rootWS;
    s.normalWS = n;
    s.uv = float3(v.uv, rampT);
    s.tint = tint;
    s.ground = v.color.rgb * _GroundColor.rgb;
    return s;
}

// Alpha test shared by every pass.
half GrassAlpha(float2 uv)
{
    return tex2D(_MainTex, uv).a;
}

// 0..1 blend factor toward the ground at this point of the blade (1 at the root when strength is 1).
half GrassGroundFactor(float rampT)
{
    return (1.0h - saturate(rampT / max(_GroundBlend.x, 1e-3))) * _GroundBlend.y;
}

// Interleaved gradient noise (Jimenez 2014): pseudo-random 0..1 per pixel with no repeating structure,
// so a dithered fade reads as grain instead of the stripes / crosshatch an ordered Bayer matrix produces.
half GrassDither(float2 pixel)
{
    float3 magic = float3(0.06711056, 0.00583715, 52.9829189);
    return (half)frac(magic.z * frac(dot(floor(pixel), magic.xy)));
}

// Shared clip for every pass: texture cutout, plus a dithered alpha fade toward the root when the ground
// blend is in Alpha Fade mode (root alpha reaches 0). Dither keeps the shader a cheap single cutout pass.
void GrassClip(float3 uv, float2 pixel)
{
    clip(GrassAlpha(uv.xy) - _Cutoff);
    half f = GrassGroundFactor(uv.z) * _GroundBlend.z;
    half fadeAlpha = 1.0h - f * f * (3.0h - 2.0h * f);   // smoothstep: gentle start, fully gone at the root
    clip(fadeAlpha - GrassDither(pixel));
}

// Final color: ramp * (optional texture RGB, un-premultiplied to kill dark fringes) * per-vertex tint,
// then the base of the blade blends toward the ground color so roots don't cut a hard line on the terrain.
// 'shadow' is the received-shadow factor (URP) so the ground color darkens with the grass; 1 elsewhere.
half3 GrassColor(float3 uv, half3 tint, half shadow, half3 ground)
{
    half4 tex = tex2D(_MainTex, uv.xy);
    half3 texColor = lerp(half3(1, 1, 1), saturate(tex.rgb / max(tex.a, 0.001h)), _UseTextureColor);
    half3 ramp = tex2D(_ColorRamp, float2(uv.z, 0.5)).rgb;
    half3 col = ramp * texColor * tint;

    half groundF = GrassGroundFactor(uv.z) * (1.0h - _GroundBlend.z); // color mode only; fade mode clips instead
    return lerp(col, ground * shadow, groundF);
}

#endif
