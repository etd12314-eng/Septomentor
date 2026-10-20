// Double-sided translucent fresnel overlay for CT-derived meshes registered onto the patient.
Shader "SeptoMentor/NavGhost"
{
    Properties
    {
        _Color ("Colour", Color) = (0.4, 0.8, 1, 0.35)
        _Rim ("Rim power", Range(0.5, 8)) = 2.0
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            float4 _Color;
            float _Rim;

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 pos : SV_POSITION; float3 wn : TEXCOORD0; float3 wv : TEXCOORD1; UNITY_VERTEX_OUTPUT_STEREO };

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.wn = UnityObjectToWorldNormal(v.normal);
                o.wv = WorldSpaceViewDir(v.vertex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float f = 1.0 - saturate(abs(dot(normalize(i.wn), normalize(i.wv))));
                return fixed4(_Color.rgb, saturate(_Color.a * (0.3 + pow(f, _Rim))));
            }
            ENDCG
        }
    }
}
