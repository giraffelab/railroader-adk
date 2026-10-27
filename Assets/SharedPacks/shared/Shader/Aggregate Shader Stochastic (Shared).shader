Shader "Railroader/Aggregate Shader Stochastic (Shared)"
{
	Properties
	{
		[HideInInspector] _AlphaCutoff("Alpha Cutoff ", Range(0, 1)) = 0.5
		[HideInInspector] _EmissionColor("Emission Color", Color) = (1,1,1,1)
		[NoScaleOffset]_BaseMap("BaseMap", 2D) = "white" {}
		[NoScaleOffset]_MetallicGlossMap("MetallicGlossMap", 2D) = "black" {}
		[NoScaleOffset]_Height("Height", 2D) = "white" {}
		_Tiling("Tiling", Vector) = (1,3.24,0,0)
		[NoScaleOffset]_OcclusionMap("OcclusionMap", 2D) = "white" {}
		_Color("Color", Color) = (1,1,1)
		[NoScaleOffset][Normal]_BumpMap("BumpMap", 2D) = "bump" {}
		_NormalLevel("NormalLevel", Float) = 1
		_StochasticContrast("StochasticContrast", Range( 0.01 , 0.99)) = 0.15
		_StochasticScale("StochasticScale", Range( 0 , 2)) = 1

		[HideInInspector][ToggleOff] _SpecularHighlights("Specular Highlights", Float) = 1
		[HideInInspector][ToggleOff] _EnvironmentReflections("Environment Reflections", Float) = 1
		[HideInInspector][ToggleOff] _ReceiveShadows("Receive Shadows", Float) = 1.0

		[HideInInspector] _QueueOffset("_QueueOffset", Float) = 0
        [HideInInspector] _QueueControl("_QueueControl", Float) = -1

        [HideInInspector][NoScaleOffset] unity_Lightmaps("unity_Lightmaps", 2DArray) = "" {}
        [HideInInspector][NoScaleOffset] unity_LightmapsInd("unity_LightmapsInd", 2DArray) = "" {}
        [HideInInspector][NoScaleOffset] unity_ShadowMasks("unity_ShadowMasks", 2DArray) = "" {}
	}

	// Stub shader: preserves the property interface of the original stochastic shader
	// so materials compile and asset bundles build. Uses simple tiled sampling as a
	// visual fallback. The game's built-in bundle provides the real shader at runtime.
	SubShader
	{
		Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" }

		Pass
		{
			Name "ForwardLit"
			Tags { "LightMode"="UniversalForward" }

			HLSLPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
			#pragma multi_compile _ _ADDITIONAL_LIGHTS
			#pragma multi_compile_fragment _ _SHADOWS_SOFT

			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

			TEXTURE2D(_BaseMap);        SAMPLER(sampler_BaseMap);
			TEXTURE2D(_BumpMap);        SAMPLER(sampler_BumpMap);
			TEXTURE2D(_MetallicGlossMap); SAMPLER(sampler_MetallicGlossMap);
			TEXTURE2D(_OcclusionMap);   SAMPLER(sampler_OcclusionMap);

			CBUFFER_START(UnityPerMaterial)
				float4 _Tiling;
				half4 _Color;
				half _NormalLevel;
				half _StochasticContrast;
				half _StochasticScale;
				half _AlphaCutoff;
				half4 _EmissionColor;
			CBUFFER_END

			struct Attributes
			{
				float4 positionOS : POSITION;
				float3 normalOS   : NORMAL;
				float4 tangentOS  : TANGENT;
				float2 uv         : TEXCOORD0;
			};

			struct Varyings
			{
				float4 positionCS  : SV_POSITION;
				float2 uv          : TEXCOORD0;
				float3 positionWS  : TEXCOORD1;
				float3 normalWS    : TEXCOORD2;
				float3 tangentWS   : TEXCOORD3;
				float3 bitangentWS : TEXCOORD4;
			};

			Varyings vert(Attributes input)
			{
				Varyings output;
				VertexPositionInputs posInputs = GetVertexPositionInputs(input.positionOS.xyz);
				VertexNormalInputs normalInputs = GetVertexNormalInputs(input.normalOS, input.tangentOS);
				output.positionCS = posInputs.positionCS;
				output.positionWS = posInputs.positionWS;
				output.normalWS = normalInputs.normalWS;
				output.tangentWS = normalInputs.tangentWS;
				output.bitangentWS = normalInputs.bitangentWS;
				output.uv = input.uv * _Tiling.xy;
				return output;
			}

			half4 frag(Varyings input) : SV_Target
			{
				half4 baseColor = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * half4(_Color.rgb, 1);
				half4 metalGloss = SAMPLE_TEXTURE2D(_MetallicGlossMap, sampler_MetallicGlossMap, input.uv);
				half occlusion = SAMPLE_TEXTURE2D(_OcclusionMap, sampler_OcclusionMap, input.uv).r;
				half3 normalTS = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, input.uv), _NormalLevel);

				float3x3 tbn = float3x3(input.tangentWS, input.bitangentWS, input.normalWS);
				float3 normalWS = normalize(mul(normalTS, tbn));

				InputData inputData = (InputData)0;
				inputData.positionWS = input.positionWS;
				inputData.normalWS = normalWS;
				inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
				inputData.shadowCoord = TransformWorldToShadowCoord(input.positionWS);

				SurfaceData surfaceData = (SurfaceData)0;
				surfaceData.albedo = baseColor.rgb;
				surfaceData.metallic = metalGloss.r;
				surfaceData.smoothness = metalGloss.a;
				surfaceData.normalTS = normalTS;
				surfaceData.occlusion = occlusion;
				surfaceData.alpha = 1;

				return UniversalFragmentPBR(inputData, surfaceData);
			}
			ENDHLSL
		}

		Pass
		{
			Name "ShadowCaster"
			Tags { "LightMode"="ShadowCaster" }
			ZWrite On
			ColorMask 0

			HLSLPROGRAM
			#pragma vertex ShadowPassVertex
			#pragma fragment ShadowPassFragment
			#include "Packages/com.unity.render-pipelines.universal/Shaders/LitInput.hlsl"
			#include "Packages/com.unity.render-pipelines.universal/Shaders/ShadowCasterPass.hlsl"
			ENDHLSL
		}

		Pass
		{
			Name "DepthOnly"
			Tags { "LightMode"="DepthOnly" }
			ZWrite On
			ColorMask R

			HLSLPROGRAM
			#pragma vertex DepthOnlyVertex
			#pragma fragment DepthOnlyFragment
			#include "Packages/com.unity.render-pipelines.universal/Shaders/LitInput.hlsl"
			#include "Packages/com.unity.render-pipelines.universal/Shaders/DepthOnlyPass.hlsl"
			ENDHLSL
		}
	}

	FallBack "Universal Render Pipeline/Lit"
}
