// Soft contact shadow under OR furniture: a floor quad darkened toward its centre (no shadow maps).
Shader "SeptoMentor/ORShadow"
{
    Properties { _Strength ("Strength", Range(0, 1)) = 0.45 }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Geometry+10" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Offset -1, -1
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            float _Strength;

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

            fixed4 frag (v2f i) : SV_Target
            {
                float2 d = abs(i.uv - 0.5) * 2.0;
                float r = length(max(d - 0.45, 0.0)) / 0.55;   // rounded rectangle, soft edge
                return fixed4(0, 0, 0, _Strength * (1.0 - smoothstep(0.0, 1.0, r)));
            }
            ENDCG
        }
    }
}
