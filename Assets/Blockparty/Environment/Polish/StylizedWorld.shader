// POLISH-WORLD (#216): cartoony world surface for the Alpha block.
// Wrap-lit (half-Lambert style) main light with real-time shadows, ambient
// SH, additional point lights, fog; optional world-projected tiling so one
// material reads at a constant texel size on any box; procedural world noise
// for broad colour patches; vertex sway for foliage. No textures are needed
// for the noise or sway, so the cost is a few ALU ops per pixel.
Shader "Blockparty/StylizedWorld"
{
    Properties
    {
        _BaseColor ("Base colour", Color) = (1,1,1,1)
        _BaseMap ("Base map", 2D) = "white" {}
        _WorldTile ("World tile size (m, 0 = mesh UV)", Float) = 0
        _VarColor ("Patch tint (multiplier)", Color) = (1,1,1,1)
        _VarScale ("Patch size (m)", Float) = 6
        _VarStrength ("Patch strength", Range(0,1)) = 0
        _Wrap ("Light wrap", Range(0,1)) = 0.4
        _ShadeColor ("Shade tint", Color) = (0.86,0.84,0.95,1)
        _Sway ("Foliage sway (m)", Float) = 0
        _EmissionColor ("Emission", Color) = (0,0,0,1)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4 _BaseColor;
            half4 _VarColor;
            half4 _ShadeColor;
            half4 _EmissionColor;
            float _WorldTile;
            float _VarScale;
            half _VarStrength;
            half _Wrap;
            float _Sway;
        CBUFFER_END
        TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);

        float PolishHash(float2 p)
        {
            p = frac(p * float2(123.34, 456.21));
            p += dot(p, p + 45.32);
            return frac(p.x * p.y);
        }

        float PolishNoise(float2 p)
        {
            float2 i = floor(p);
            float2 f = frac(p);
            float2 u = f * f * (3.0 - 2.0 * f);
            float a = PolishHash(i);
            float b = PolishHash(i + float2(1, 0));
            float c = PolishHash(i + float2(0, 1));
            float d = PolishHash(i + float2(1, 1));
            return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
        }

        // World height drives the sway (same result with or without static
        // batching), so trunk bases stay put and crowns move.
        float3 PolishSway(float3 positionOS, float3 positionWS)
        {
            float h = max(0.0, positionWS.y);
            float phase = _Time.y * 1.3 + positionWS.x * 0.35 + positionWS.z * 0.27;
            return float3(sin(phase), 0.0, cos(phase * 0.8)) * _Sway * h;
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float2 uv : TEXCOORD2;
                half fog : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                positionWS += PolishSway(input.positionOS.xyz, positionWS);
                output.positionWS = positionWS;
                output.positionCS = TransformWorldToHClip(positionWS);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.uv = input.uv;
                output.fog = ComputeFogFactor(output.positionCS.z);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                float3 n = normalize(input.normalWS);
                float3 p = input.positionWS;
                float2 uv = input.uv;
                if (_WorldTile > 0.0)
                {
                    float3 a = abs(n);
                    uv = (a.y >= a.x && a.y >= a.z) ? p.xz : (a.x >= a.z ? p.zy : p.xy);
                    uv /= _WorldTile;
                }
                half3 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv * _BaseMap_ST.xy + _BaseMap_ST.zw).rgb * _BaseColor.rgb;
                float patches = PolishNoise(p.xz / _VarScale) * 0.65 + PolishNoise(p.xz / (_VarScale * 0.37) + 17.0) * 0.35;
                albedo *= lerp(half3(1, 1, 1), _VarColor.rgb, smoothstep(0.35, 0.75, patches) * _VarStrength);

                Light main = GetMainLight(TransformWorldToShadowCoord(p));
                half ndl = saturate((dot(n, main.direction) + _Wrap) / (1.0 + _Wrap));
                half lit = smoothstep(0.0, 0.85, ndl) * main.shadowAttenuation * main.distanceAttenuation;
                half3 ambient = SampleSH(n);
                half3 color = albedo * (ambient * _ShadeColor.rgb + main.color * lit);
                #if defined(_ADDITIONAL_LIGHTS)
                int count = GetAdditionalLightsCount();
                for (int i = 0; i < count; ++i)
                {
                    Light light = GetAdditionalLight((uint)i, p);
                    half d = saturate((dot(n, light.direction) + _Wrap) / (1.0 + _Wrap));
                    color += albedo * light.color * (d * light.distanceAttenuation * light.shadowAttenuation);
                }
                #endif
                color += _EmissionColor.rgb;
                color = MixFog(color, input.fog);
                return half4(color, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            float3 _LightDirection;
            float3 _LightPosition;
            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; };
            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                positionWS += PolishSway(input.positionOS.xyz, positionWS);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                float3 lightDirectionWS = normalize(_LightPosition - positionWS);
                #else
                float3 lightDirectionWS = _LightDirection;
                #endif
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
                #if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                output.positionCS = positionCS;
                return output;
            }
            half4 Frag(Varyings input) : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ZWrite On ColorMask R
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            struct Attributes { float4 positionOS : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; };
            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                positionWS += PolishSway(input.positionOS.xyz, positionWS);
                output.positionCS = TransformWorldToHClip(positionWS);
                return output;
            }
            half4 Frag(Varyings input) : SV_Target { return 0; }
            ENDHLSL
        }
    }
    FallBack Off
}
