// One CT slice on a quad: panel UV -> volume UVW via _PanelToUvw, window/level in HU,
// optional label colours, and a crosshair at the panel centre (the pointer tip).
Shader "SeptoMentor/CtSlice"
{
    Properties
    {
        _Volume ("CT (HU)", 3D) = "" {}
        _Labels ("Labels", 3D) = "" {}
        _Window ("Window (HU)", Float) = 2000
        _Level ("Level (HU)", Float) = 300
        _Packed ("R8 packed -1000..2000", Float) = 0
        _UseLabels ("Show labels", Float) = 0
        _Cross ("Crosshair colour", Color) = (1, 0.9, 0.2, 1)
        _ShowCross ("Show crosshair", Float) = 1
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

            sampler3D _Volume;
            sampler3D _Labels;
            float4x4 _PanelToUvw;
            float _Window, _Level, _Packed, _UseLabels, _ShowCross;
            float4 _Cross;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float3 LabelColour(float id)
            {
                if (id < 0.5) return float3(0, 0, 0);
                if (id < 1.5) return float3(0.31, 0.67, 1.0);   // septal cartilage
                if (id < 2.5) return float3(1.0, 0.67, 0.0);    // bony septum
                if (id < 3.5) return float3(0.24, 0.86, 0.47);  // turbinates
                return float3(0.8, 0.5, 1.0);
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 uvw = mul(_PanelToUvw, float4(i.uv, 0, 1)).xyz;
                float3 col;
                if (any(uvw < 0) || any(uvw > 1)) col = float3(0.03, 0.03, 0.05);
                else
                {
                    float v = tex3D(_Volume, uvw).r;
                    float hu = _Packed > 0.5 ? v * 3000 - 1000 : v;
                    float g = saturate((hu - (_Level - 0.5 * _Window)) / _Window);
                    col = float3(g, g, g);
                    if (_UseLabels > 0.5)
                    {
                        float id = round(tex3D(_Labels, uvw).r * 255);
                        if (id > 0.5) col = lerp(col, LabelColour(id), 0.45);
                    }
                }
                if (_ShowCross > 0.5)
                {
                    float2 d = abs(i.uv - 0.5);
                    float w = 0.004;
                    bool line = (d.x < w && d.y > 0.03 && d.y < 0.12) || (d.y < w && d.x > 0.03 && d.x < 0.12);
                    if (line) col = _Cross.rgb;
                }
                float2 e = min(i.uv, 1 - i.uv);
                if (min(e.x, e.y) < 0.006) col = float3(0.35, 0.4, 0.5);   // frame
                return fixed4(col, 1);
            }
            ENDCG
        }
    }
}
