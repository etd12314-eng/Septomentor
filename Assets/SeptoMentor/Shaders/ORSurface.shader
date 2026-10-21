// Operating-room surfaces without scene lights or baking: hemispheric ambient from the ceiling
// panels, two surgical lamps (positions/aims set globally by OperatingRoom), Blinn specular,
// optional world-space tile grid (walls, floor, ceiling panels) and emission (screens, lamps).
Shader "SeptoMentor/ORSurface"
{
    Properties
    {
        _Color ("Colour", Color) = (0.8, 0.8, 0.8, 1)
        _Gloss ("Gloss", Range(0, 1)) = 0.2
        _Tile ("Tile size (m, 0 = none)", Float) = 0
        _Emission ("Emission", Range(0, 1)) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            float4 _Color;
            float _Gloss, _Tile, _Emission;
            // xyz position, w intensity / direction the lamp faces
            float4 _ORLamp0, _ORLamp1, _ORLampDir0, _ORLampDir1;

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 pos : SV_POSITION; float3 wpos : TEXCOORD0; float3 wn : TEXCOORD1; UNITY_VERTEX_OUTPUT_STEREO };

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.wn = UnityObjectToWorldNormal(v.normal);
                return o;
            }

            float Lamp(float4 lamp, float4 dir, float3 p, float3 n, float3 v, inout float spec)
            {
                if (lamp.w <= 0) return 0;   // lamps not set (no operating room in the scene)
                float3 l = lamp.xyz - p;
                float d2 = dot(l, l);
                l *= rsqrt(d2);
                float cone = smoothstep(0.80, 0.97, dot(-l, normalize(dir.xyz)));   // ~25 deg spot
                float k = lamp.w * cone / (1.0 + d2);
                spec += k * pow(saturate(dot(n, normalize(l + v))), 8.0 + 120.0 * _Gloss);
                return k * saturate(dot(n, l));
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 n = normalize(i.wn);
                float3 v = normalize(_WorldSpaceCameraPos - i.wpos);
                float3 albedo = _Color.rgb;

                if (_Tile > 0)
                {
                    float3 a = abs(n);
                    float2 uv = a.y > 0.5 ? i.wpos.xz : (a.x > a.z ? i.wpos.zy : i.wpos.xy);
                    float2 g = abs(frac(uv / _Tile) - 0.5);
                    float grout = smoothstep(0.485, 0.495, max(g.x, g.y));
                    albedo *= 1.0 - 0.25 * grout;
                }

                // Ceiling panels: bright from above, dimmer bounce from the floor.
                float3 ambient = lerp(float3(0.30, 0.32, 0.33), float3(0.78, 0.80, 0.82), n.y * 0.5 + 0.5);
                float spec = 0;
                float lit = Lamp(_ORLamp0, _ORLampDir0, i.wpos, n, v, spec) + Lamp(_ORLamp1, _ORLampDir1, i.wpos, n, v, spec);
                spec += 0.25 * pow(saturate(dot(n, normalize(float3(0, 1, 0) + v))), 8.0 + 60.0 * _Gloss);   // ceiling sheen

                float3 col = albedo * (ambient + lit * float3(1.0, 0.98, 0.93)) + spec * _Gloss;
                col = lerp(col, _Color.rgb * 1.2, _Emission);
                return fixed4(col, 1);
            }
            ENDCG
        }
    }
}
