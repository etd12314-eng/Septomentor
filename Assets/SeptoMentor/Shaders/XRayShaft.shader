// The part of a hand-held shaft (endoscope, instrument) hidden inside the patient's nose, drawn as a faint
// see-through silhouette so the resident sees where the tip is and how the shaft runs past the nostril.
// Drawn only where something is in front of it (ZTest Greater) and only near _XRayFocus (the nostrils, world space).
Shader "SeptoMentor/XRayShaft"
{
    Properties
    {
        _Color ("Colour", Color) = (0.55, 0.85, 1, 0.55)
        _Radius ("Visible within (m) of the focus", Float) = 0.07
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent+10" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        ZTest Greater
        Cull Back
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            float4 _Color;
            float _Radius;
            float4 _XRayFocus;   // xyz world, w > 0 when set

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

            fixed4 frag (v2f i) : SV_Target
            {
                if (_XRayFocus.w <= 0) discard;
                float near = saturate(1.0 - distance(i.wpos, _XRayFocus.xyz) / _Radius);
                float3 v = normalize(_WorldSpaceCameraPos - i.wpos);
                float edge = 1.0 - saturate(abs(dot(normalize(i.wn), v)));
                float a = _Color.a * near * (0.35 + 0.65 * edge);
                if (a < 0.01) discard;
                return fixed4(_Color.rgb * (0.7 + 0.5 * edge), a);
            }
            ENDCG
        }
    }
}
