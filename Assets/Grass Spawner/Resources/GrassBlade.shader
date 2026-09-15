// Lightweight stylized grass blade shader (WebGL / mobile first, PC features opt-in).
//
// SubShader 1 (URP): Forward + ShadowCaster + DepthOnly + DepthNormals, optional per-vertex received shadows.
// SubShader 2 (Built-in): untagged forward pass + ShadowCaster.
// All displacement / shading lives in GrassBladeCommon.hlsl; every pass just calls GrassDisplace().
Shader "Grass/Blade"
{
    Properties
    {
        _MainTex ("Blade Texture (alpha = shape)", 2D) = "white" {}
        [HideInInspector] _Cutoff ("Alpha Cutoff", Range(0.01, 1)) = 0.5
        [Toggle] _UseTextureColor ("Multiply Texture RGB (off = alpha only, set by GrassSpawner)", Float) = 0

        [Header(Color)]
        [NoScaleOffset] _ColorRamp ("Color Ramp (root -> tip, baked by GrassSpawner)", 2D) = "white" {}
        [Toggle] _GradientByHeight ("Gradient by blade height instead of card UV (set by GrassSpawner)", Float) = 1
        _MaxBladeHeight ("Max Blade Height (set by GrassSpawner)", Float) = 1
        _ColorVariation ("Per-blade Variation", Range(0, 1)) = 0.12
        _TintMap ("Tint Map (sampled across field)", 2D) = "white" {}
        _TintBounds ("Tint Bounds (offsetX, offsetZ, 1/sizeX, 1/sizeZ) - set by GrassSpawner", Vector) = (5, 5, 0.1, 0.1)
        _TintTiling ("Tint Tiling (xy) / Offset (zw) - set by GrassSpawner", Vector) = (1, 1, 0, 0)
        _TintParams ("Tint Strength (x) / Blur mip level (y) - set by GrassSpawner", Vector) = (1, 0, 0, 0)
        _TintColor ("Tint Map Color (set by GrassSpawner)", Color) = (1, 1, 1, 1)
        _LightInfluence ("Light Influence (0 = unlit, 1 = follows scene light + ambient)", Range(0, 1)) = 0.7

        [Header(View)]
        _ViewFacing ("Align cards to the camera view direction (set by GrassSpawner)", Range(0, 1)) = 0

        [Header(Wind)]
        _WindDir ("Wind Direction (x, z)", Vector) = (1, 0, 0.35, 0)
        _WindStrength ("Wind Strength", Range(0, 2)) = 0.35
        _WindSpeed ("Wind Speed", Range(0, 10)) = 2.0
        _WindFrequency ("Wind Frequency", Range(0, 4)) = 0.6
        _WindHighlight ("Wind Highlight", Range(0, 1)) = 0.08

        [Header(Interaction)]
        _InteractStrength ("Push Strength", Range(0, 3)) = 1.0
        [NoScaleOffset] _TrampleMap ("Trample Map (set by GrassSpawner)", 2D) = "black" {}
        _BendDarken ("Trample Darken", Range(0, 1)) = 0.5

        [Header(Shadows URP)]
        [Toggle(_GRASS_RECEIVE_SHADOWS)] _ReceiveShadows ("Receive main light shadows (per vertex)", Float) = 0
        _ShadowStrength ("Received Shadow Strength", Range(0, 1)) = 0.6

        [Header(Ground Blend)]
        _GroundColor ("Ground Color / tint over baked vertex color (set by GrassSpawner)", Color) = (1, 1, 1, 1)
        _GroundBlend ("Blend Height (x) / Strength (y) / Mode (z: 0 color, 1 alpha fade) - set by GrassSpawner", Vector) = (0.35, 0, 1, 0)
    }

    // =====================================================================================================
    // URP
    // =====================================================================================================
    SubShader
    {
        // DisableBatching is required: blades are reconstructed from their root stored in UV1 (object space);
        // dynamic batching would pre-transform vertices to world space without touching UV1.
        Tags { "RenderType"="TransparentCutout" "Queue"="AlphaTest" "IgnoreProjector"="True"
               "DisableBatching"="True" "RenderPipeline"="UniversalPipeline" }
        Cull Off
        ZWrite On

        Pass
        {
            Name "GrassForward"
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            // multi_compile, not shader_feature: the keyword is switched at runtime on a material instance, and
            // shader_feature variants that no saved material uses are stripped from builds (works in the editor,
            // silently missing in WebGL / player builds).
            #pragma multi_compile_vertex _ _GRASS_RECEIVE_SHADOWS
            // Screen-space shadows are deliberately not compiled: they can't be sampled from a vertex shader.
            #pragma multi_compile_vertex _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_vertex _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "GrassBladeCommon.hlsl"

            struct v2f
            {
                float4 pos    : SV_POSITION;
                float3 uv     : TEXCOORD0;
                half4  tint   : TEXCOORD1; // rgb = shading, a = received shadow factor
                half3  ground : TEXCOORD2;
            };

            v2f vert (GrassAttributes v)
            {
                GrassSurface s = GrassDisplace(v);
                v2f o;
                o.pos = TransformWorldToHClip(s.posWS);
                o.uv = s.uv;
                half shadow = 1.0h;
            #if defined(_GRASS_RECEIVE_SHADOWS) && (defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE))
                // One shadow tap per vertex: cheap, and soft enough on thin cards.
                shadow = lerp(1.0h, MainLightRealtimeShadow(TransformWorldToShadowCoord(s.posWS)), _ShadowStrength);
            #endif
                o.tint = half4(s.tint * shadow, shadow);
                o.ground = s.ground;
                return o;
            }

            half4 frag (v2f i) : SV_Target
            {
                GrassClip(i.uv, i.pos.xy);
                return half4(GrassColor(i.uv, i.tint.rgb, i.tint.a, i.ground), 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            #include "GrassBladeCommon.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct v2f { float4 pos : SV_POSITION; float3 uv : TEXCOORD0; };

            v2f vert (GrassAttributes v)
            {
                GrassSurface s = GrassDisplace(v);
            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDir = normalize(_LightPosition - s.posWS);
            #else
                float3 lightDir = _LightDirection;
            #endif
                float4 posCS = TransformWorldToHClip(ApplyShadowBias(s.posWS, s.normalWS, lightDir));
            #if UNITY_REVERSED_Z
                posCS.z = min(posCS.z, UNITY_NEAR_CLIP_VALUE);
            #else
                posCS.z = max(posCS.z, UNITY_NEAR_CLIP_VALUE);
            #endif
                v2f o;
                o.pos = posCS;
                o.uv = s.uv;
                return o;
            }

            half4 frag (v2f i) : SV_Target
            {
                GrassClip(i.uv, i.pos.xy);
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "GrassBladeCommon.hlsl"

            struct v2f { float4 pos : SV_POSITION; float3 uv : TEXCOORD0; };

            v2f vert (GrassAttributes v)
            {
                GrassSurface s = GrassDisplace(v);
                v2f o;
                o.pos = TransformWorldToHClip(s.posWS);
                o.uv = s.uv;
                return o;
            }

            half4 frag (v2f i) : SV_Target
            {
                GrassClip(i.uv, i.pos.xy);
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode"="DepthNormals" }
            ZWrite On

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "GrassBladeCommon.hlsl"

            struct v2f { float4 pos : SV_POSITION; float3 uv : TEXCOORD0; float3 normalWS : TEXCOORD1; };

            v2f vert (GrassAttributes v)
            {
                GrassSurface s = GrassDisplace(v);
                v2f o;
                o.pos = TransformWorldToHClip(s.posWS);
                o.uv = s.uv;
                o.normalWS = s.normalWS;
                return o;
            }

            half4 frag (v2f i) : SV_Target
            {
                GrassClip(i.uv, i.pos.xy);
                return half4(normalize(i.normalWS), 0.0);
            }
            ENDHLSL
        }
    }

    // =====================================================================================================
    // Built-in render pipeline fallback
    // =====================================================================================================
    SubShader
    {
        Tags { "RenderType"="TransparentCutout" "Queue"="AlphaTest" "IgnoreProjector"="True" "DisableBatching"="True" }
        Cull Off
        ZWrite On

        Pass
        {
            Name "GrassForward"

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            #include "GrassBladeCommon.hlsl"

            struct v2f
            {
                float4 pos    : SV_POSITION;
                float3 uv     : TEXCOORD0;
                half3  tint   : TEXCOORD1;
                half3  ground : TEXCOORD2;
            };

            v2f vert (GrassAttributes v)
            {
                GrassSurface s = GrassDisplace(v);
                v2f o;
                o.pos = mul(UNITY_MATRIX_VP, float4(s.posWS, 1.0));
                o.uv = s.uv;
                o.tint = s.tint;
                o.ground = s.ground;
                return o;
            }

            half4 frag (v2f i) : SV_Target
            {
                GrassClip(i.uv, i.pos.xy);
                return half4(GrassColor(i.uv, i.tint, 1.0h, i.ground), 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_shadowcaster
            #include "UnityCG.cginc"
            #include "GrassBladeCommon.hlsl"

            struct casterInput { float4 vertex : POSITION; float3 normal : NORMAL; };
            struct v2f { V2F_SHADOW_CASTER; float3 uv : TEXCOORD1; };

            v2f vert (GrassAttributes a)
            {
                GrassSurface s = GrassDisplace(a);
                // Built-in shadow macros want object-space position/normal: bring the displaced blade back.
                casterInput v;
                v.vertex = mul(unity_WorldToObject, float4(s.posWS, 1.0));
                v.normal = normalize(mul((float3x3)unity_WorldToObject, s.normalWS));
                v2f o;
                TRANSFER_SHADOW_CASTER_NORMALOFFSET(o)
                o.uv = s.uv;
                return o;
            }

            float4 frag (v2f i) : SV_Target
            {
                GrassClip(i.uv, i.pos.xy);
                SHADOW_CASTER_FRAGMENT(i)
            }
            ENDHLSL
        }
    }
    Fallback Off
}
