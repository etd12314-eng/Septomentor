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
        _ClassId ("Class id (label pass only)", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }
        Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            float4 _TissueColor;
            float _Wetness, _Shininess, _FalloffDist, _Mottle;

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

            float hash(float3 p) { p = frac(p * 0.3183099 + 0.1); p *= 17.0; return frac(p.x * p.y * p.z * (p.x + p.y + p.z)); }
            float noise(float3 x)
            {
                float3 i = floor(x); float3 f = frac(x); f = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(lerp(hash(i), hash(i + float3(1,0,0)), f.x), lerp(hash(i + float3(0,1,0)), hash(i + float3(1,1,0)), f.x), f.y),
                            lerp(lerp(hash(i + float3(0,0,1)), hash(i + float3(1,0,1)), f.x), lerp(hash(i + float3(0,1,1)), hash(i + float3(1,1,1)), f.x), f.y), f.z);
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 toCam = _WorldSpaceCameraPos - i.wpos;
                float d = length(toCam);
                float3 v = toCam / max(d, 1e-5);
                float ndv = abs(dot(normalize(i.wn), v));          // headlight: light dir == view dir
                float atten = 1.6 / (1.0 + (d / _FalloffDist) * (d / _FalloffDist));
                float m = noise(i.wpos * 900.0) * 0.6 + noise(i.wpos * 2600.0) * 0.4;
                float3 base = _TissueColor.rgb * (1.0 - _Mottle * 0.5 + _Mottle * m);
                float3 col = base * (0.08 + ndv) * atten + _Wetness * pow(ndv, _Shininess) * atten;
                return fixed4(saturate(col), 1);
            }
            ENDCG
        }
    }
}
