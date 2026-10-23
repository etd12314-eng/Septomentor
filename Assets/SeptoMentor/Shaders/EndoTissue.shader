// Endoscope-look tissue: headlight at the camera (no scene lights needed),
// inverse-square falloff, wet specular, procedural mottling.
// Plain CG with no LightMode tag so it renders in Built-in (and URP as SRPDefaultUnlit).
Shader "SeptoMentor/EndoTissue"
{
    Properties
    {
        _TissueColor ("Tissue colour", Color) = (0.85, 0.45, 0.45, 1)
        _Wetness ("Wet specular", Range(0, 2)) = 0.6
        _Shininess ("Shininess", Range(4, 128)) = 40
        _FalloffDist ("Light falloff distance (m)", Float) = 0.018
        _Mottle ("Mottling", Range(0, 1)) = 0.25
        _Vessels ("Mucosal vessels", Range(0, 1)) = 0.7
        _VertexLook ("Use vertex colour + uv (vessels, wetness)", Float) = 0
        _ClassId ("Class id (label pass only)", Float) = 0
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull (0 off: cavity walls seen from both sides; 2 back: the outer skin)", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }
        Cull [_Cull]
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            float4 _TissueColor;
            float _Wetness, _Shininess, _FalloffDist, _Mottle, _Vessels, _VertexLook;

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; float4 color : COLOR; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f
            {
                float4 pos : SV_POSITION; float3 wpos : TEXCOORD0; float3 wn : TEXCOORD1; float4 tint : TEXCOORD2; float2 look : TEXCOORD3;
                float3 opos : TEXCOORD4;   // object-space position (m): the patient sits 500 m away, too far for fine world-space noise
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
                o.opos = v.vertex.xyz;
                // Generated anatomy: per-vertex colour multiplier, alpha = ambient occlusion, uv = (vessel density, wetness). Primitives: neutral.
                o.tint = lerp(float4(1, 1, 1, 1), v.color, _VertexLook);
                o.look = lerp(float2(1, 1), v.uv, _VertexLook);
                return o;
            }

            float hash(float3 p) { p = frac(p * 0.3183099 + 0.1); p *= 17.0; return frac(p.x * p.y * p.z * (p.x + p.y + p.z)); }
            float noise(float3 x)
            {
                float3 i = floor(x); float3 f = frac(x); f = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(lerp(hash(i), hash(i + float3(1,0,0)), f.x), lerp(hash(i + float3(0,1,0)), hash(i + float3(1,1,0)), f.x), f.y),
                            lerp(lerp(hash(i + float3(0,0,1)), hash(i + float3(1,0,1)), f.x), lerp(hash(i + float3(0,1,1)), hash(i + float3(1,1,1)), f.x), f.y), f.z);
            }
            // Branching vessel lines: ridges of domain-warped noise (thin where the noise crosses 0.5).
            float vessels(float3 p, float width)
            {
                float3 w = p + (float3(noise(p * 0.5 + 11.0), noise(p * 0.5 + 23.0), noise(p * 0.5 + 37.0)) - 0.5) * 1.6;
                float r = 1.0 - abs(noise(w) * 2.0 - 1.0);
                return smoothstep(1.0 - width, 1.0 - width * 0.25, r);
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 toCam = _WorldSpaceCameraPos - i.wpos;
                float d = length(toCam);
                float3 v = toCam / max(d, 1e-5);
                float3 p = i.opos * 1000.0;                       // mm
                float mucosa = saturate((_TissueColor.r - _TissueColor.g) * 3.0) * saturate(i.look.x + 0.3);

                // Mucosal micro-relief (bump from the screen-space slope of a fine height noise): glints break up.
                float h = noise(p * 1.7) * 0.6 + noise(p * 4.3) * 0.4;
                float3 dpx = ddx(i.wpos), dpy = ddy(i.wpos);
                float3 n = normalize(i.wn);
                float3 grad = (cross(dpy, n) * ddx(h) + cross(n, dpx) * ddy(h)) / max(abs(dot(dpx, cross(dpy, n))), 1e-12);
                n = normalize(n - grad * 0.00004 * mucosa);
                float ndv = abs(dot(n, v));                        // headlight: light dir == view dir

                float atten = 1.6 / (1.0 + (d / _FalloffDist) * (d / _FalloffDist));
                float m = noise(p * 0.9) * 0.6 + noise(p * 2.6) * 0.4;
                float ao = i.tint.a;
                float3 base = _TissueColor.rgb * i.tint.rgb * (1.0 - _Mottle * 0.5 + _Mottle * m);
                // Capillary blush in patches.
                base *= lerp(1.0, float3(1.04, 0.86, 0.88), mucosa * smoothstep(0.45, 0.8, noise(p * 0.35 + 5.0)));
                // Vessels: fine bright-red superficial ones, broader bluish-purple deeper ones seen through the mucosa.
                float vs = vessels(p * 0.55, 0.06) * 0.85 + vessels(p * 1.4 + 9.0, 0.05) * 0.5;
                float vd = vessels(p * 0.22 + 4.0, 0.12);
                float vk = _Vessels * mucosa * i.look.x;
                base = lerp(base, base * float3(0.55, 0.38, 0.62), saturate(vd * 0.45 * vk));
                base = lerp(base, base * float3(0.72, 0.1, 0.14), saturate(vs * vk));

                // Diffuse with wrap and a red sub-surface glow at grazing angles (light scattered inside the tissue).
                float wrap = saturate((ndv + 0.35) / 1.35);
                float3 sss = base * float3(0.55, 0.12, 0.1) * pow(1.0 - ndv, 2.0) * mucosa;
                // Recesses: less light scattered back in from the surrounding walls; deep crevices turn a darker, redder hue.
                float occ = lerp(0.45, 1.0, ao);
                base *= lerp(float3(0.8, 0.6, 0.62), float3(1, 1, 1), ao) * occ;
                sss *= 0.5 + 0.5 * ao;
                // Wet film: patches of thicker mucus are glossier and slightly milky; a tight highlight plus a broad sheen and a Fresnel rim.
                float film = saturate(i.look.y) * (0.6 + 0.8 * smoothstep(0.35, 0.75, noise(p * 0.25 + 17.0)));
                base = lerp(base, base + float3(0.05, 0.05, 0.06), saturate((film - 1.0) * 0.6) * mucosa);
                float spec = _Wetness * film * (pow(ndv, _Shininess) + 0.1 * pow(ndv, _Shininess * 0.15)) + _Wetness * 0.12 * film * pow(1.0 - ndv, 5.0);
                spec *= 0.55 + 0.45 * ao;
                float3 col = (base * (0.06 + wrap) + sss + spec) * atten;
                col = 1.0 - exp(-col * 1.4);                        // soft shoulder, like the camera's
                return fixed4(col, 1);
            }
            ENDCG
        }
    }
}
