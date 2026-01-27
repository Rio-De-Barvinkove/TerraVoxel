Shader "TerraVoxel/VoxelTriplanarURP"
{
    Properties
    {
        [NoScaleOffset]_MainTexArr ("Texture Array", 2DArray) = "" {}
        _TriplanarScale ("Triplanar Scale", Float) = 0.1
        _NormalStrength ("Normal Strength", Range(0,1)) = 1
        _LayerIndex ("Layer Index", Int) = 0
    }

    SubShader
    {
        Tags { "RenderPipeline"="UniversalRenderPipeline" "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 100

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma require 2darray
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS
            #pragma multi_compile _ _ADDITIONAL_LIGHTS

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D_ARRAY(_MainTexArr);
            SAMPLER(sampler_MainTexArr);

            CBUFFER_START(UnityPerMaterial)
                float _TriplanarScale;
                float _NormalStrength;
                int _LayerIndex;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 color      : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float  fogFactor  : TEXCOORD2;
                float4 color      : COLOR;
            };

            Varyings vert (Attributes v)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.fogFactor = ComputeFogFactor(o.positionCS.z);
                o.color = v.color;
                return o;
            }

            float3 SampleTriplanar(float3 posWS, float3 normalWS, float layer)
            {
                float3 n = normalize(normalWS);
                float3 w = pow(abs(n), 4.0);
                w /= max(dot(w, 1.0), 1e-4);

                float scale = _TriplanarScale;
                float2 uvX = posWS.zy * scale;
                float2 uvY = posWS.xz * scale;
                float2 uvZ = posWS.xy * scale;

                float3 cx = SAMPLE_TEXTURE2D_ARRAY(_MainTexArr, sampler_MainTexArr, uvX, layer).rgb;
                float3 cy = SAMPLE_TEXTURE2D_ARRAY(_MainTexArr, sampler_MainTexArr, uvY, layer).rgb;
                float3 cz = SAMPLE_TEXTURE2D_ARRAY(_MainTexArr, sampler_MainTexArr, uvZ, layer).rgb;

                return cx * w.x + cy * w.y + cz * w.z;
            }

            half4 frag (Varyings i) : SV_Target
            {
                float3 n = normalize(i.normalWS);
                float layer = (float)_LayerIndex;
                float layerColor = round(saturate(i.color.r) * 255.0);
                if (layerColor > 0.5) layer = layerColor;
                float3 albedo = SampleTriplanar(i.positionWS, n, layer);

                // Simple Lambert with main light.
                Light mainLight = GetMainLight();
                float ndotl = saturate(dot(n, mainLight.direction));
                float3 lit = albedo * (0.2 + ndotl * mainLight.color);

                half4 color = half4(lit, 1);
                color.rgb = MixFog(color.rgb, i.fogFactor);
                return color;
            }
            ENDHLSL
        }
    }
    FallBack Off
}

