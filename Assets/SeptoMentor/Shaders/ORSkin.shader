// Patient skin in the operating room: ORSurface's lighting (ceiling ambient + the two surgical lamps) with
// vertex colour (lips, brows, nostrils), baked ambient occlusion (colour alpha) and oiliness (uv.y).
// Skin, not plastic: diffuse wrapped further in red than in blue (light scattered under the skin), creases and
// nostrils darker and redder, fine pores, and a two-lobe specular with Fresnel - broad and soft on dry skin,
// tighter and brighter on the oily T-zone.
Shader "SeptoMentor/ORSkin"
{
    Properties
    {
        _Color ("Skin colour", Color) = (0.88, 0.69, 0.6, 1)
        _Gloss ("Gloss", Range(0, 1)) = 0.3
        _Pores ("Pore relief", Range(0, 1)) = 0.6
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
            float _Gloss, _Pores;
            float4 _ORLamp0, _ORLamp1, _ORLampDir0, _ORLampDir1;

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; float4 color : COLOR; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f
            {
                float4 pos : SV_POSITION; float3 wpos : TEXCOORD0; float3 wn : TEXCOORD1; float4 col : COLOR; float oil : TEXCOORD2;
                float3 opos : TEXCOORD3;   // head frame, m
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.wn = UnityObjectToWorldNormal(v.normal);
                o.col = v.color;
                o.oil = v.uv.y;
                o.opos = v.vertex.xyz;
                return o;
            }

            float hash(float3 p) { p = frac(p * 0.3183099 + 0.1); p *= 17.0; return frac(p.x * p.y * p.z * (p.x + p.y + p.z)); }
            float noise(float3 x)
            {
                float3 i = floor(x); float3 f = frac(x); f = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(lerp(hash(i), hash(i + float3(1,0,0)), f.x), lerp(hash(i + float3(0,1,0)), hash(i + float3(1,1,0)), f.x), f.y),
                            lerp(lerp(hash(i + float3(0,0,1)), hash(i + float3(1,0,1)), f.x), lerp(hash(i + float3(0,1,1)), hash(i + float3(1,1,1)), f.x), f.y), f.z);
            }

            // Normalised Blinn-Phong as a stand-in for GGX (cheap on Quest): power from roughness.
            float Lobe(float ndh, float rough)
            {
                float a2 = max(rough * rough, 0.002);
                float pw = 2.0 / a2 - 2.0;
                return (pw + 2.0) * 0.125 * pow(ndh, pw);
            }

            void Lamp(float4 lamp, float4 dir, float3 p, float3 n, float3 v, float rough0, float rough1, inout float3 diff, inout float spec)
            {
                if (lamp.w <= 0) return;
                float3 l = lamp.xyz - p;
                float d2 = dot(l, l);
                l *= rsqrt(d2);
                float cone = smoothstep(0.80, 0.97, dot(-l, normalize(dir.xyz)));
                float k = lamp.w * cone / (1.0 + d2);
                float ndl = dot(n, l);
                // Red light travels further under the skin than blue, so the terminator is soft and warm.
                const float3 wrap = float3(0.48, 0.24, 0.16);
                diff += k * saturate((ndl + wrap) / (1.0 + wrap)) * float3(1.0, 0.98, 0.93);
                float ndh = saturate(dot(n, normalize(l + v)));
                spec += k * saturate(ndl * 4.0) * (0.6 * Lobe(ndh, rough0) + 0.4 * Lobe(ndh, rough1));
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 v = normalize(_WorldSpaceCameraPos - i.wpos);
                float3 n = normalize(i.wn);
                float ao = i.col.a;

                // Pores and fine skin relief from a height noise in head-frame mm, faded out where a pixel covers more
                // than ~0.6 mm (no shimmering at arm's length).
                float3 p = i.opos * 1000.0;
                float h = noise(p * 2.2) * 0.65 + noise(p * 5.3) * 0.35;
                float3 dpx = ddx(i.wpos), dpy = ddy(i.wpos);
                float px = max(length(dpx), length(dpy)) * 1000.0;
                float relief = _Pores * saturate(1.2 - px / 0.5);
                float3 grad = (cross(dpy, n) * ddx(h) + cross(n, dpx) * ddy(h)) / max(abs(dot(dpx, cross(dpy, n))), 1e-12);
                n = normalize(n - grad * 0.00005 * relief);

                // Colour: slight blotchiness (capillaries), creases and the nostrils' depths darker and redder.
                float blot = noise(p * 0.12) * 0.6 + noise(p * 0.45) * 0.4;
                float3 albedo = _Color.rgb * i.col.rgb * lerp(float3(1.02, 0.99, 0.98), float3(0.98, 1.0, 1.01), blot);
                albedo *= lerp(float3(0.78, 0.55, 0.52), float3(1, 1, 1), ao);

                float oil = saturate(i.oil);
                float rough0 = lerp(0.62, 0.42, oil) * (1.15 - 0.3 * _Gloss);
                float rough1 = rough0 * 0.45;
                float ndv = saturate(dot(n, v));
                float fres = 0.028 + 0.972 * pow(1.0 - ndv, 5.0);

                float3 ambient = lerp(float3(0.30, 0.31, 0.32), float3(0.76, 0.78, 0.80), n.y * 0.5 + 0.5) * lerp(0.35, 1.0, ao);
                float3 diff = 0; float spec = 0;
                Lamp(_ORLamp0, _ORLampDir0, i.wpos, n, v, rough0, rough1, diff, spec);
                Lamp(_ORLamp1, _ORLampDir1, i.wpos, n, v, rough0, rough1, diff, spec);
                float specOcc = saturate(ao * 1.4 - 0.2);
                spec *= lerp(0.04, 0.1, oil) * (0.6 + 6.0 * fres) * specOcc * (0.6 + _Gloss);
                // Ceiling-panel sheen and a faint velvet rim (fine hairs) on the face's silhouette.
                float sky = pow(saturate(dot(n, normalize(float3(0, 1, 0) + v))), 6.0 + 20.0 * oil) * fres * 0.5 * specOcc;
                float rim = pow(1.0 - ndv, 4.0) * 0.05 * ao;

                float3 col = albedo * (ambient + diff)
                           + albedo * albedo * float3(0.10, 0.025, 0.015) * (1.0 + diff.r) * ao   // warm sub-surface fill
                           + (spec + sky) * float3(1.0, 0.98, 0.95) + rim * float3(1.0, 0.92, 0.88);
                return fixed4(col, 1);
            }
            ENDCG
        }
    }
}
