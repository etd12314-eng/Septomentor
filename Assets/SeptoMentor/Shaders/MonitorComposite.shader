// Monitor quad: endoscope video + class-map overlay (fill and/or outline),
// with the circular endoscope field stop.
Shader "SeptoMentor/MonitorComposite"
{
    Properties
    {
        _MainTex ("Video", 2D) = "black" {}
        _LabelTex ("Labels (R8)", 2D) = "black" {}
        _Opacity ("Fill opacity", Range(0, 1)) = 0.35
        _Outline ("Outline strength", Range(0, 1)) = 1
        _Circle ("Circular field stop", Range(0, 1)) = 1
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

            sampler2D _MainTex;
            sampler2D _LabelTex;
            float4 _LabelTex_TexelSize;
            float _Opacity, _Outline, _Circle;
            float4 _Palette[16];   // set from StructureInfo by OverlayView; alpha 0 hides a class

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

            int idAt(float2 uv) { return (int)floor(tex2D(_LabelTex, uv).r * 255.0 + 0.5); }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 col = tex2D(_MainTex, i.uv).rgb;
                int id = clamp(idAt(i.uv), 0, 15);
                float4 pc = _Palette[id];

                float2 t = _LabelTex_TexelSize.xy;
                int edge = (idAt(i.uv + float2(t.x, 0)) != id) || (idAt(i.uv - float2(t.x, 0)) != id) ||
                           (idAt(i.uv + float2(0, t.y)) != id) || (idAt(i.uv - float2(0, t.y)) != id);

                if (id != 0)
                {
                    col = lerp(col, pc.rgb, _Opacity * pc.a);
                    if (edge) col = lerp(col, pc.rgb, _Outline * pc.a);
                }

                float r = length(i.uv - 0.5) * 2.0;
                float mask = lerp(1.0, 1.0 - smoothstep(0.94, 1.0, r), _Circle);
                return fixed4(col * mask, 1);
            }
            ENDCG
        }
    }
}
