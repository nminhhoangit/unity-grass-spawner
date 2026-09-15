// Fullscreen pass that maintains the trample map for one GrassSpawner.
// RG = push direction (encoded 0..1), B = trample amount (0..1), A = recovery time of that texel / MAX_RECOVERY.
// Each frame the amount decays by dt / recovery and the current interactors are stamped in with max-blend,
// so grass stays flat where something passed and springs back over that interactor's recovery time.
// Runs on a tiny RT (default 256x256): one cheap blit per spawner per frame.
Shader "Hidden/Grass/TrampleMap"
{
    Properties { _MainTex ("Previous map", 2D) = "black" {} }
    SubShader
    {
        Pass
        {
            ZTest Always Cull Off ZWrite Off
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            #define GRASS_MAX_INTERACTORS 8

            sampler2D _MainTex;
            float4 _MapBounds;                                  // xy = local min (x,z), zw = size
            #define MAX_RECOVERY 60.0   // seconds encoded as 1.0 in the A channel

            float4 _TrampleInteractors[GRASS_MAX_INTERACTORS];  // xy = local x,z; z = radius; w = strength (0 = skip)
            float4 _TrampleRecovery[GRASS_MAX_INTERACTORS];     // x = recovery time in seconds for this interactor
            float _TrampleCount;
            float _Dt;        // seconds since the last blit
            float _MinDecay;  // minimum step per blit (8-bit RT), 0 for float RTs

            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert (appdata_img v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord;
                return o;
            }

            half4 frag (v2f i) : SV_Target
            {
                half4 old = tex2D(_MainTex, i.uv);
                float2 dir = old.rg * 2.0 - 1.0;
                float recovery = max(old.a * MAX_RECOVERY, 0.05);
                float amt = max(old.b - max(_Dt / recovery, _MinDecay), 0.0);
                float recNorm = old.a;

                float2 p = _MapBounds.xy + i.uv * _MapBounds.zw; // local x,z of this texel
                int count = (int)_TrampleCount;
                [loop]
                for (int k = 0; k < count; k++)
                {
                    float4 it = _TrampleInteractors[k];
                    float2 d = p - it.xy;
                    float dist = length(d);
                    float f = saturate((it.z - dist) / max(it.z, 1e-3)) * it.w;
                    if (f > amt)
                    {
                        amt = f;
                        dir = dist > 1e-4 ? d / dist : float2(0.0, 1.0);
                        recNorm = saturate(_TrampleRecovery[k].x / MAX_RECOVERY);
                    }
                }
                return half4(dir * 0.5 + 0.5, amt, recNorm);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
