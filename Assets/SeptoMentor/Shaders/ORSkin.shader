// Patient skin in the operating room: ORSurface's lighting (ceiling ambient + the two surgical lamps) with
// vertex colour (lips, brows, nostrils), wrapped diffuse and a warm sub-surface tint so it does not look like plastic.
Shader "SeptoMentor/ORSkin"
{
    Properties
    {
        _Color ("Skin colour", Color) = (0.88, 0.69, 0.6, 1)
        _Gloss ("Gloss", Range(0, 1)) = 0.3
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
            float _Gloss;
            float4 _ORLamp0, _ORLamp1, _ORLampDir0, _ORLampDir1;

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; float4 color : COLOR; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 pos : SV_POSITION; float3 wpos : TEXCOORD0; float3 wn : TEXCOORD1; float4 col : COLOR; UNITY_VERTEX_OUTPUT_STEREO };

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.wn = UnityObjectToWorldNormal(v.normal);
                o.col = v.color;
                return o;
            }

            float Lamp(float4 lamp, float4 dir, float3 p, float3 n, float3 v, inout float spec)
            {
                if (lamp.w <= 0) return 0;
                float3 l = lamp.xyz - p;
                float d2 = dot(l, l);
                l *= rsqrt(d2);
                float cone = smoothstep(0.80, 0.97, dot(-l, normalize(dir.xyz)));
                float k = lamp.w * cone / (1.0 + d2);
                spec += k * pow(saturate(dot(n, normalize(l + v))), 8.0 + 60.0 * _Gloss);
                return k * saturate((dot(n, l) + 0.35) / 1.35);   // wrapped: light bleeds past the terminator
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 n = normalize(i.wn);
                float3 v = normalize(_WorldSpaceCameraPos - i.wpos);
                float3 albedo = _Color.rgb * i.col.rgb;
                float3 ambient = lerp(float3(0.30, 0.31, 0.32), float3(0.76, 0.78, 0.80), n.y * 0.5 + 0.5);
                float spec = 0;
                float lit = Lamp(_ORLamp0, _ORLampDir0, i.wpos, n, v, spec) + Lamp(_ORLamp1, _ORLampDir1, i.wpos, n, v, spec);
                float rim = pow(1.0 - saturate(dot(n, v)), 3.0);
                float3 col = albedo * (ambient + lit * float3(1.0, 0.98, 0.93))
                           + albedo * float3(0.10, 0.03, 0.02) * (1.0 + lit)          // warm sub-surface
                           + spec * _Gloss * 0.5 + rim * 0.04;
                return fixed4(col, 1);
            }
            ENDCG
        }
    }
}
