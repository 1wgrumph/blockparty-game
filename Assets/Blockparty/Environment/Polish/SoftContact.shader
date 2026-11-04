// POLISH-WORLD (#216): soft contact shadow under trees, bushes and
// buildings. A multiply-blended quad with a radial (or rounded-box) falloff
// computed from its UVs, so it needs no texture and adds no lights. The
// project's URP asset has soft shadows off (TOOLCHAIN-bound), so this gives
// the target's soft grounding without changing it.
Shader "Blockparty/SoftContact"
{
    Properties
    {
        _ShadowColor ("Shadow colour", Color) = (0.55,0.58,0.52,1)
        _Box ("Rounded box (0 = ellipse)", Range(0,1)) = 0
        _Softness ("Softness", Range(0.05,1)) = 0.55
    }
    SubShader
    {
        Tags { "Queue"="Geometry+20" "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" "IgnoreProjector"="True" }
        Blend DstColor Zero
        ZWrite Off
        Offset -1, -1
        Pass
        {
            Tags { "LightMode"="SRPDefaultUnlit" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            CBUFFER_START(UnityPerMaterial)
                half4 _ShadowColor;
                half _Box;
                half _Softness;
            CBUFFER_END
            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };
            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv * 2.0 - 1.0;
                return output;
            }
            half4 Frag(Varyings input) : SV_Target
            {
                float2 q = abs(input.uv);
                float ellipse = length(input.uv);
                float box = length(max(q - 0.55, 0.0)) / 0.45;
                float d = lerp(ellipse, box, _Box);
                half a = 1.0 - smoothstep(1.0 - _Softness, 1.0, d);
                return half4(lerp(half3(1, 1, 1), _ShadowColor.rgb, a), 1);
            }
            ENDHLSL
        }
    }
}
