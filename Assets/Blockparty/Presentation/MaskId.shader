// CONTENT-IMPORT render-mask shader (MainSceneBuild.Capture): writes _Colour exactly
// (a Vector, so no colour-space conversion) into a linear target. Depth test, depth
// write, culling and channel mask are properties: depth-only occluders, depth-tested
// surface IDs, and depth-free per-part coverage all use this one shader. Skinning is
// applied by Unity to POSITION, so masks follow the evaluated pose.
Shader "Blockparty/MaskId"
{
    Properties
    {
        _Colour ("Colour", Vector) = (0, 0, 0, 1)
        _ZTest ("ZTest", Float) = 4
        _ZWrite ("ZWrite", Float) = 1
        _Cull ("Cull", Float) = 2
        _ColorMask ("ColorMask", Float) = 15
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }
        Pass
        {
            Cull [_Cull]
            ZWrite [_ZWrite]
            ZTest [_ZTest]
            ColorMask [_ColorMask]
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 _Colour;
            struct appdata { float4 vertex : POSITION; };
            struct v2f { float4 pos : SV_POSITION; };
            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                return o;
            }
            float4 frag(v2f i) : SV_Target { return _Colour; }
            ENDCG
        }
    }
    Fallback Off
}
