// Label pass: writes the class id (Structure enum value) as R = id/255.
// Rendered into an R8 target with no MSAA, so values stay exact.
Shader "SeptoMentor/LabelId"
{
    Properties
    {
        _ClassId ("Class id", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }
        Cull Off
        ZWrite On
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float _ClassId;

            float4 vert (float4 v : POSITION) : SV_POSITION { return UnityObjectToClipPos(v); }

            float4 frag () : SV_Target { return float4((_ClassId + 0.25) / 255.0, 0, 0, 1); }
            ENDCG
        }
    }
}
