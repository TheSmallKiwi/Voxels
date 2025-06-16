Shader "Fluids/VolumetricFluid"
{
    HLSLINCLUDE
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
    // The Blit.hlsl file provides the vertex shader (Vert),
    // the input structure (Attributes), and the output structure (Varyings)
    #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

    // Textures and Samplers
    TEXTURE3D(_DensityTexture);
    TEXTURE3D(_VelocityTexture);
    SAMPLER(sampler_linear_clamp);

    // Global Parameters
    // float4x4 _CameraToWorld;
    float4x4 _CameraInvProjection;
    float3 _VolumePosition;
    float3 _VolumeSize;

    // Material Properties
    float4 _FluidColor;
    float _AbsorptionStrength;
    float _ScatteringStrength;
    float _DensityThreshold;
    float _StepSize;
    int _MaxSteps;

    // Ray-box intersection
    float2 RayBoxIntersection(float3 rayOrigin, float3 rayDir, float3 boxMin, float3 boxMax)
    {
        float3 invDir = 1.0 / rayDir;
        float3 t0 = (boxMin - rayOrigin) * invDir;
        float3 t1 = (boxMax - rayOrigin) * invDir;

        float3 tmin = min(t0, t1);
        float3 tmax = max(t0, t1);

        float tNear = max(max(tmin.x, tmin.y), tmin.z);
        float tFar = min(min(tmax.x, tmax.y), tmax.z);

        if (tNear > tFar || tFar < 0.0)
            return float2(-1.0, -1.0);

        return float2(max(tNear, 0.0), tFar);
    }


    // Convert world position to volume texture coordinates
    float3 WorldToVolumeUV(float3 worldPos)
    {
        float3 localPos = worldPos - _VolumePosition + _VolumeSize * 0.5;
        return localPos / _VolumeSize;
    }

    // Sample density with trilinear filtering
    float SampleDensity(float3 uvw)
    {
        if (any(uvw < 0.0) || any(uvw > 1.0))
            return 0.0;

        return SAMPLE_TEXTURE3D(_DensityTexture, sampler_linear_clamp, uvw).r;
    }

    // Sample velocity with trilinear filtering
    float3 SampleVelocity(float3 uvw)
    {
        if (any(uvw < 0.0) || any(uvw > 1.0))
            return float3(0, 0, 0);

        return SAMPLE_TEXTURE3D(_VelocityTexture, sampler_linear_clamp, uvw).rgb;
    }

    // Reconstruct world position from screen UV
    float3 ReconstructWorldPosition(float2 screenUV, float depth)
    {
        // Convert screen UV to NDC
        float4 ndc = float4(screenUV * 2.0 - 1.0, depth, 1.0);

        // Transform to view space
        float4 viewPos = mul(_CameraInvProjection, ndc);
        viewPos /= viewPos.w;

        // Transform to world space
        float3 worldPos = mul(unity_CameraToWorld, float4(viewPos.xyz, 1.0)).xyz;
        return worldPos;
    }

    // Get ray direction from screen UV
    float3 GetRayDirection(float2 screenUV)
    {
        // Convert screen UV to NDC
        float2 ndc = screenUV * 2.0 - 1.0;

        // Create ray in view space pointing to far plane
        float4 viewDir = mul(_CameraInvProjection, float4(ndc, 1.0, 1.0));
        viewDir.xyz /= viewDir.w;

        // Transform to world space and normalize
        float3 worldDir = mul((float3x3)unity_CameraToWorld, normalize(viewDir.xyz));

        return worldDir;
    }

    // Simplified lighting calculation for volumetric scattering
    float3 CalculateVolumetricLighting(float3 worldPos, float3 rayDirection, float density)
    {
        Light mainLight = GetMainLight();
        float3 lightDir = mainLight.direction;
        float3 lightColor = mainLight.color;

        // Phase function for scattering (Henyey-Greenstein approximation)
        float cosTheta = dot(-rayDirection, lightDir);
        float g = 0.3; // Anisotropy factor
        float phase = (1.0 - g * g) / pow(abs(1.0 + g * g - 2.0 * g * cosTheta), 1.5);

        // Simplified light attenuation - single sample shadow approximation
        float lightAttenuation = 1.0;
        float3 lightUVW = WorldToVolumeUV(worldPos + lightDir * _StepSize * 2.0);
        float shadowDensity = SampleDensity(lightUVW);
        lightAttenuation *= exp(-shadowDensity * _AbsorptionStrength * _StepSize);

        return lightColor * phase * lightAttenuation * _ScatteringStrength;
    }

    float4 VolumetricFluidPass(Varyings input) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

        // Get camera position and ray direction
        float3 cameraPos = unity_CameraToWorld._m03_m13_m23;
        float3 rayDir = GetRayDirection(input.texcoord);

        // Calculate volume bounds
        float3 volumeMin = _VolumePosition - _VolumeSize * 0.5;
        float3 volumeMax = _VolumePosition + _VolumeSize * 0.5;

        // Ray-volume intersection
        float2 intersection = RayBoxIntersection(cameraPos, rayDir, volumeMin, volumeMax);
        float tNear = intersection.x;
        float tFar = intersection.y;

        // Early exit if no intersection
        if (tFar <= tNear || tFar <= 0.0)
            return float4(0, 0, 0, 0);

        // Sample scene depth for intersection with solid geometry
        float sceneDepth = SampleSceneDepth(input.texcoord);
        float linearDepth = LinearEyeDepth(sceneDepth, _ZBufferParams);

        if (linearDepth > 0.0)
            tFar = min(tFar, linearDepth);

        if (tFar <= tNear)
            return float4(0, 0, 0, 0);

        // Calculate total ray distance and ensure we don't exceed max steps
        float totalDistance = tFar - tNear;
        float actualStepSize = max(_StepSize, totalDistance / float(_MaxSteps));
        int actualMaxSteps = min(_MaxSteps, int(totalDistance / actualStepSize) + 1);

        // Ray marching
        float3 currentPos = cameraPos + rayDir * tNear;
        float3 rayStep = rayDir * actualStepSize;

        float4 accumulatedColor = float4(0, 0, 0, 0);
        float transmittance = 1.0;

        float t = tNear;

        // Ray marching loop with explicit bounds
        [unroll(64)]
        for (int stepCount = 0; stepCount < 64 && stepCount < actualMaxSteps && t < tFar && transmittance > 0.01;
                                                 stepCount++)
        {
            float3 uvw = WorldToVolumeUV(currentPos);
            float density = SampleDensity(uvw);

            if (density > _DensityThreshold)
            {
                // Calculate absorption and scattering
                float absorption = density * _AbsorptionStrength * actualStepSize;
                float scattering = density * _ScatteringStrength * actualStepSize;

                // Calculate lighting
                float3 lighting = CalculateVolumetricLighting(currentPos, rayDir, density);

                // Add velocity-based color variation
                float3 velocity = SampleVelocity(uvw);
                float velocityMagnitude = length(velocity);
                float3 velocityColor = lerp(float3(1, 1, 1), float3(1.2, 0.8, 0.6),
                                                         saturate(velocityMagnitude * 0.1));

                // Combine colors
                float3 sampleColor = _FluidColor.rgb * velocityColor * (lighting + 0.1);

                // Apply volume rendering equation
                float sampleAlpha = 1.0 - exp(-scattering);
                sampleAlpha = clamp(sampleAlpha, 0.0, 1.0);

                accumulatedColor.rgb += sampleColor * sampleAlpha * transmittance;
                accumulatedColor.a += sampleAlpha * transmittance;

                // Update transmittance
                transmittance *= exp(-absorption);
            }

            // Advance ray
            currentPos += rayStep;
            t += actualStepSize;
        }

        // Final color output
        accumulatedColor.rgb = lerp(accumulatedColor.rgb, _FluidColor.rgb,
             saturate(accumulatedColor.a * 0.5));
        accumulatedColor.a = saturate(accumulatedColor.a);

        return accumulatedColor;
    }

    float4 CompositePass(Varyings input) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

        // Sample the original scene color
        float4 sceneColor = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.texcoord);

        // Sample the volumetric fluid contribution
        float4 volumetricColor = VolumetricFluidPass(input);

        // Alpha blend volumetric fluid over scene
        float3 finalColor = lerp(sceneColor.rgb, volumetricColor.rgb, volumetricColor.a);

        return float4(finalColor, sceneColor.a);
    }

    float4 UpscalePass(Varyings input) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

        // Simple bilinear upscale of the volumetric texture
        return SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.texcoord);
    }
    ENDHLSL

    SubShader
    {
        Tags
        {
            "RenderType"="Transparent" "RenderPipeline" = "UniversalPipeline"
        }
        LOD 100
        ZWrite Off Cull Off

        Pass
        {
            Name "VolumetricFluidPass"
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment VolumetricFluidPass
            ENDHLSL
        }

        Pass
        {
            Name "CompositePass"
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment CompositePass
            ENDHLSL
        }

        Pass
        {
            Name "UpscalePass"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment UpscalePass
            ENDHLSL
        }
    }
}