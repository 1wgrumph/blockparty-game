// POLISH-WORLD (#216): warm stylised skybox for the Alpha block. Three-stop
// gradient from the approved target (top #8ec5d6, mid #cfe6df, horizon
// #fbe7c6), soft procedural clouds that drift slowly (the cheap ambient-life
// touch), and a warm glow around the sun. One pass, no textures.
Shader "Blockparty/PolishSky"
{
    Properties
    {
        _Top ("Top", Color) = (0.557,0.773,0.839,1)
        _Mid ("Mid", Color) = (0.776,0.886,0.894,1)
        _Horizon ("Horizon", Color) = (0.984,0.906,0.776,1)
        _Ground ("Below horizon", Color) = (0.62,0.70,0.52,1)
        _CloudColor ("Cloud", Color) = (1,0.995,0.975,1)
        _CloudShade ("Cloud underside", Color) = (0.95,0.92,0.89,1)
        _CloudCover ("Cloud cover", Range(0,1)) = 0.3
        _CloudScale ("Cloud scale", Float) = 1.6
        _CloudSpeed ("Cloud drift", Float) = 0.004
        _SunGlow ("Sun glow", Color) = (1,0.89,0.65,1)
    }
    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Background" "PreviewType"="Skybox" "RenderPipeline"="UniversalPipeline" }
        Cull Off ZWrite Off
        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Input.hlsl"
            float4 _Top, _Mid, _Horizon, _Ground, _CloudColor, _CloudShade, _SunGlow;
            float _CloudCover, _CloudScale, _CloudSpeed;
            struct Varyings { float4 positionCS : SV_POSITION; float3 direction : TEXCOORD0; };

            float Hash(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }
            float Noise(float2 p)
            {
                float2 i = floor(p), f = frac(p);
                float2 u = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(Hash(i), Hash(i + float2(1, 0)), u.x), lerp(Hash(i + float2(0, 1)), Hash(i + 1), u.x), u.y);
            }
            float Fbm(float2 p)
            {
                return Noise(p) * 0.55 + Noise(p * 2.03 + 7.1) * 0.28 + Noise(p * 4.1 + 3.7) * 0.17;
            }

            Varyings Vert(float3 positionOS : POSITION)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(positionOS);
                output.direction = positionOS;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float3 d = normalize(input.direction);
                float y = d.y;
                float3 sky = y < 0.0
                    ? lerp(_Horizon.rgb, _Ground.rgb, saturate(-y * 6.0))
                    : (y < 0.14 ? lerp(_Horizon.rgb, _Mid.rgb, smoothstep(0.0, 0.14, y))
                                : lerp(_Mid.rgb, _Top.rgb, smoothstep(0.14, 0.5, y)));
                float sunDot = saturate(dot(d, _MainLightPosition.xyz));
                sky += _SunGlow.rgb * (pow(sunDot, 24.0) * 0.35 + pow(sunDot, 400.0) * 0.6);
                if (y > 0.02)
                {
                    // Flat cloud deck: project onto a plane, drift along x.
                    float2 uv = d.xz / (y + 0.12) * _CloudScale + float2(_Time.y * _CloudSpeed, 0.0);
                    float n = Fbm(uv);
                    float c = smoothstep(1.0 - _CloudCover, 1.0 - _CloudCover + 0.06, n);
                    float under = smoothstep(0.0, 0.25, n - (1.0 - _CloudCover));
                    float3 cloud = lerp(_CloudShade.rgb, _CloudColor.rgb, under);
                    sky = lerp(sky, cloud, c * smoothstep(0.02, 0.18, y) * 0.92);
                }
                return half4(sky, 1);
            }
            ENDHLSL
        }
    }
}
