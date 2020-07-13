Shader "Hidden/FullScreen/CloudsRaymarch2D"
{
	HLSLINCLUDE

#pragma vertex Vert

#pragma target 4.5
#pragma only_renderers d3d11 ps4 xboxone vulkan metal switch
#pragma enable_d3d11_debug_symbols

#include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/RenderPass/CustomPass/CustomPassCommon.hlsl"

		// The PositionInputs struct allow you to retrieve a lot of useful information for your fullScreenShader:
		// struct PositionInputs
		// {
		//     float3 positionWS;  // World space position (could be camera-relative)
		//     float2 positionNDC; // Normalized screen coordinates within the viewport    : [0, 1) (with the half-pixel offset)
		//     uint2  positionSS;  // Screen space pixel coordinates                       : [0, NumPixels)
		//     uint2  tileCoord;   // Screen tile coordinates                              : [0, NumTiles)
		//     float  deviceDepth; // Depth from the depth buffer                          : [0, 1] (typically reversed)
		//     float  linearDepth; // View space Z coordinate                              : [Near, Far]
		// };

		// To sample custom buffers, you have access to these functions:
		// But be careful, on most platforms you can't sample to the bound color buffer. It means that you
		// can't use the SampleCustomColor when the pass color buffer is set to custom (and same for camera the buffer).
		// float3 SampleCustomColor(float2 uv);
		// float3 LoadCustomColor(uint2 pixelCoords);
		// float LoadCustomDepth(uint2 pixelCoords);
		// float SampleCustomDepth(float2 uv);

		// There are also a lot of utility function you can use inside Common.hlsl and Color.hlsl,
		// you can check them out in the source code of the core SRP package.

	TEXTURE2D_X(_Source);
	TEXTURE2D_X_HALF(_SourceHalfRes);
	TEXTURE2D_X_HALF(_LightBuffer);

	float4 _ViewPortSize; // We need the viewport size because we have a non fullscreen render target (blur buffers are downsampled in half res)
	float _ThicknessScale;
	float _ThicknessSoftKnee;
	float4 _Sunlight;


#pragma enable_d3d11_debug_symbols

	// We need to clamp the UVs to avoid bleeding from bigger render tragets (when we have multiple cameras)
	float2 ClampUVs(float2 uv)
	{
		uv = clamp(uv, 0, _RTHandleScale - _ScreenSize.zw * 2); // clamp UV to 1 pixel to avoid bleeding
		return uv;
	}

	float IntegrateOpacity(float thickness)
	{
		return pow(1.0-(1.0/(_ThicknessScale *thickness + 1)), _ThicknessSoftKnee);
	}

	float2 GetSampleUVs(Varyings varyings, float2 offset)
	{
		float depth = LoadCameraDepth(varyings.positionCS.xy);
		PositionInputs posInput = GetPositionInput(varyings.positionCS.xy, _ViewPortSize.zw, depth, UNITY_MATRIX_I_VP, UNITY_MATRIX_V);
		return (posInput.positionNDC.xy * _RTHandleScale.xy) + offset;
	}

	float2 GetSampleUVs(Varyings varyings)
	{
		return GetSampleUVs(varyings, float2(0, 0));
	}

	float4 RenderClouds(Varyings varyings) : SV_Target
	{
		float2 uv = ClampUVs(GetSampleUVs(varyings));

		float cloudMask = SAMPLE_TEXTURE2D_X_LOD(_Source, s_linear_clamp_sampler, uv, 0).r;
		float3 light = SAMPLE_TEXTURE2D_X_LOD(_LightBuffer, s_linear_clamp_sampler, uv, 0).rgb;

		return float4(light * GetCurrentExposureMultiplier(), IntegrateOpacity(cloudMask));
	}

#define ITERATIONS 16

	float3 IntegrateLighting(Varyings varyings, float3 lightDirection, float3 light)
	{
		float depth = LoadCameraDepth(varyings.positionCS.xy);

		float2 uv = ClampUVs(GetSampleUVs(varyings));
		float d = SAMPLE_TEXTURE2D_X_LOD(_SourceHalfRes, s_linear_clamp_sampler, uv, 0).r;
		
		int i = 1;
		float t = 1.0f;

		while (i <= ITERATIONS)
		{
			float3 uvw = lightDirection * ((float)i / ITERATIONS);
			uv = ClampUVs(GetSampleUVs(varyings, uvw.xy));
			d = SAMPLE_TEXTURE2D_X_LOD(_SourceHalfRes, s_linear_clamp_sampler, uv, 0).r;

			if (_ThicknessScale * d > abs(uvw.z))
				t *= 0.5;

			i++;
		}

		return t * light;
	}

	float4 ComputeLighting(Varyings varyings) : SV_Target
	{
		float3 direction = float3(0.07,0.07,0.00);
		return float4(IntegrateLighting(varyings, direction, _Sunlight.xyz * GetCurrentExposureMultiplier()), 1);
	}

	ENDHLSL

	SubShader
	{
		Pass
		{
			Name "Render Clouds"

			ZWrite Off
			ZTest Always
			Blend SrcAlpha OneMinusSrcAlpha
			Cull Off

			HLSLPROGRAM
				#pragma fragment RenderClouds
			ENDHLSL
		}

		Pass
		{
			Name "Compute Lighting"

			ZWrite Off
			ZTest Always
			Blend SrcAlpha OneMinusSrcAlpha
			Cull Off

			HLSLPROGRAM
				#pragma fragment ComputeLighting
			ENDHLSL
		}
	}

	Fallback Off
}
