Shader "Blockparty/SliceSky"
{
    Properties
    {
        _Top ("Top", Color) = (0.557,0.765,0.918,1)
        _Horizon ("Horizon", Color) = (0.953,0.902,0.812,1)
    }
    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Background" "PreviewType"="Skybox" }
        Cull Off ZWrite Off
        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            float4 _Top, _Horizon;
            struct Varyings { float4 positionCS : SV_POSITION; float3 direction : TEXCOORD0; };
            Varyings Vert(float3 positionOS : POSITION)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(positionOS);
                output.direction = positionOS;
                return output;
            }
            half4 Frag(Varyings input) : SV_Target
            {
                return lerp(_Horizon, _Top, saturate(normalize(input.direction).y));
            }
            ENDHLSL
        }
    }
}
