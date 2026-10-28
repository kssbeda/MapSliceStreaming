Shader "MapSlice/NatureOcclusion"
{
    Properties
    {
        _BaseMap ("Albedo", 2D) = "white" {}
        _BaseColor ("Tint", Color) = (1,1,1,1)
        _BumpMap ("Normal map", 2D) = "bump" {}
        _BumpScale ("Normal strength", Range(0,2)) = 1
        [Toggle] _UseNormalMap ("Enable normal map", Float) = 0
        _Cutoff ("Leaf alpha cutoff", Range(0,1)) = .38
        [Toggle] _AlphaClip ("Leaf alpha clip", Float) = 1
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull (leaves two sided)", Float) = 0
        [Toggle] _Occlude ("Reveal player if between camera and player", Float) = 1
        [HideInInspector] _SoftFade ("Soft alpha blend mode", Float) = 0
        [HideInInspector] _SrcBlend ("Blend source", Float) = 1
        [HideInInspector] _DstBlend ("Blend destination", Float) = 0
        [HideInInspector] _ZWrite ("Write depth", Float) = 1
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="TransparentCutout" "Queue"="AlphaTest" }
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            Cull [_Cull]
            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]
            ZTest LEqual
            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_BumpMap);
            SAMPLER(sampler_BumpMap);
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half _Cutoff;
                half _AlphaClip;
                half _Occlude;
                half _SoftFade;
                half _BumpScale;
                half _UseNormalMap;
            CBUFFER_END
            // Runtime globals are independent of material so Terrain tree instancing works.
            float4 _CameraWS;
            float4 _CameraToPlayer;    // xyz normalized direction; w target depth
            float4 _ScreenCenterRadii; // x,y viewport; z,w radii in pixels
            float4 _Parameters;       // x enabled; y feather pixels; z inner alpha; w max range

            struct Attributes
            {
                float4 positionOS:POSITION;
                float3 normalOS:NORMAL;
                float4 tangentOS:TANGENT;
                float2 uv:TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings
            {
                float4 positionCS:SV_POSITION;
                float3 positionWS:TEXCOORD0;
                float3 normalWS:TEXCOORD1;
                float2 uv:TEXCOORD2;
                half fog:TEXCOORD3;
                float4 screenPos:TEXCOORD4;
                float4 tangentWS:TEXCOORD5;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            Varyings Vert(Attributes i)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_TRANSFER_INSTANCE_ID(i,o);
                o.positionWS=TransformObjectToWorld(i.positionOS.xyz);
                o.positionCS=TransformWorldToHClip(o.positionWS);
                o.screenPos=ComputeScreenPos(o.positionCS); // correct projection/Y flip in URP
                o.normalWS=TransformObjectToWorldNormal(i.normalOS);
                o.tangentWS=float4(TransformObjectToWorldDir(i.tangentOS.xyz),i.tangentOS.w*GetOddNegativeScale());
                o.uv=TRANSFORM_TEX(i.uv,_BaseMap);
                o.fog=ComputeFogFactor(o.positionCS.z);
                return o;
            }
            // Adapted from the user's ShaderLearningLab v1.4:
            // deterministic hash/value noise and alpha-clip/dissolve composition.
            float Hash31(float3 p)
            {
                p=frac(p*0.1031);
                p+=dot(p,p.yzx+33.33);
                return frac((p.x+p.y)*p.z);
            }
            float ValueNoise3D(float3 p)
            {
                float3 i=floor(p),f=frac(p);
                float3 u=f*f*(3.0-2.0*f);
                float n000=Hash31(i+float3(0,0,0));
                float n100=Hash31(i+float3(1,0,0));
                float n010=Hash31(i+float3(0,1,0));
                float n110=Hash31(i+float3(1,1,0));
                float n001=Hash31(i+float3(0,0,1));
                float n101=Hash31(i+float3(1,0,1));
                float n011=Hash31(i+float3(0,1,1));
                float n111=Hash31(i+float3(1,1,1));
                float nxy0=lerp(lerp(n000,n100,u.x),lerp(n010,n110,u.x),u.y);
                float nxy1=lerp(lerp(n001,n101,u.x),lerp(n011,n111,u.x),u.y);
                return lerp(nxy0,nxy1,u.z);
            }
            half4 Frag(Varyings i):SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                half4 albedo=SAMPLE_TEXTURE2D(_BaseMap,sampler_BaseMap,i.uv)*_BaseColor;
                if(_AlphaClip>.5)clip(albedo.a-_Cutoff);
                float visibility=1.0;
                if(_Occlude>.5 && _Parameters.x>.5)
                {
                    float depth=dot(i.positionWS-_CameraWS.xyz,_CameraToPlayer.xyz);
                    float gap=_CameraToPlayer.w-depth;
                    if(depth>.05 && gap>.20 && gap<_Parameters.w && i.screenPos.w>0.0001)
                    {
                        // Compare viewport positions from identical URP clip-space convention.
                        float2 uv=i.screenPos.xy/i.screenPos.w;
                        float2 delta=(uv-_ScreenCenterRadii.xy)*_ScaledScreenParams.xy;
                        float2 radii=max(_ScreenCenterRadii.zw,float2(8.0,8.0));
                        float d=length(delta/radii)-1.0;
                        float feather=max(1.0,_Parameters.y);
                        // d is elliptical distance. Convert to conservative pixel margin.
                        float signedPixel=d*min(radii.x,radii.y);
                        visibility=smoothstep(-feather,feather,signedPixel);
                        if(_SoftFade>.5)
                        {
                            // Actual alpha blending, just in the obstructing screen region.
                            visibility=lerp(saturate(_Parameters.z),1.0,visibility);
                        }
                        else
                        {
                            // Fast solid material path: no dots at the center, only
                            // a 2-4 px antialiased border using ShaderLearningLab noise.
                            if(signedPixel < -feather) clip(-1.0);
                            if(signedPixel < feather)
                            {
                                float threshold=ValueNoise3D(i.positionWS*14.0);
                                clip(visibility-threshold);
                            }
                        }
                    }
                }
                // Same TBN principle demonstrated by ShaderLearningLab v1.4.
                float3 n=normalize(i.normalWS);
                if(_UseNormalMap>.5)
                {
                    half4 packed=SAMPLE_TEXTURE2D(_BumpMap,sampler_BumpMap,i.uv);
                    half2 packedRG=(packed.a>.99)?packed.xy:packed.wy;
                    float2 normalXY=(packedRG*2.0-1.0)*_BumpScale;
                    float3 normalTS=normalize(float3(normalXY,sqrt(saturate(1.0-dot(normalXY,normalXY)))));
                    float3 tan=normalize(i.tangentWS.xyz);
                    float3 bi=normalize(cross(n,tan)*i.tangentWS.w);
                    n=normalize(mul(normalTS,float3x3(tan,bi,n)));
                }
                Light key=GetMainLight();
                float ndl=saturate(dot(n,key.direction));
                // ShaderLearningLab half-Lambert idea; avoids extremely dark leaf backs.
                float halfLambert=ndl*.5+.5;
                half3 lit=albedo.rgb*(0.20+key.color.rgb*halfLambert*halfLambert);
                lit=MixFog(lit,i.fog);
                return half4(lit,_SoftFade>.5 ? visibility : 1.0);
            }
            ENDHLSL
        }
        // Keep cast shadows using texture alpha, independent from camera/player reveal.
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            Cull [_Cull]
            ZWrite On
            ColorMask 0
            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half _Cutoff;
                half _AlphaClip;
                half _Occlude;
                half _SoftFade;
                half _BumpScale;
                half _UseNormalMap;
            CBUFFER_END
            struct A { float4 pos:POSITION;float2 uv:TEXCOORD0;UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct V { float4 pos:SV_POSITION;float2 uv:TEXCOORD0;UNITY_VERTEX_INPUT_INSTANCE_ID };
            V ShadowVert(A i)
            {
                V o; UNITY_SETUP_INSTANCE_ID(i);UNITY_TRANSFER_INSTANCE_ID(i,o);
                o.pos=TransformObjectToHClip(i.pos.xyz);o.uv=TRANSFORM_TEX(i.uv,_BaseMap);return o;
            }
            half4 ShadowFrag(V i):SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                if(_AlphaClip>.5)
                {
                    half a=SAMPLE_TEXTURE2D(_BaseMap,sampler_BaseMap,i.uv).a*_BaseColor.a;
                    clip(a-_Cutoff);
                }
                return 0;
            }
            ENDHLSL
        }
    }
    FallBack Off
}
