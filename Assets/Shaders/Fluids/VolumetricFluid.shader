Shader "Fluids/VolumetricFluid"
{
    Properties
    {
        _DensityTexture ("Density Volume", 3D) = "" {}
        _VelocityTexture ("Velocity Volume", 3D) = "" {}
        _FluidColor ("Fluid Color", Color) = (0.2, 0.6, 1.0, 1.0)
        _AbsorptionStrength ("Absorption", Range(0, 5)) = 1.0
        _ScatteringStrength ("Scattering", Range(0, 2)) = 0.5
        _DensityThreshold ("Density Threshold", Range(0, 1)) = 0.01
        _StepSize ("Step Size", Range(0.1, 2.0)) = 0.5
        _MaxSteps ("Max Steps", Range(32, 256)) = 128
    }
    
    SubShader
    {
        Tags 
        { 
            "RenderPipeline" = "UniversalPipeline" 
            "RenderType" = "Transparent" 
            "Queue" = "Transparent"
        }
        
        Pass
        {
            Name "VolumetricFluid"
            
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off
            
            HLSLPROGRAM
            #pragma vertex VertexMain
            #pragma fragment FragmentMain
            
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            
            struct VertexInput
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };
            
            struct FragmentInput
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 rayDirection : TEXCOORD1;
            };
            
            // Textures and Samplers
            TEXTURE3D(_DensityTexture);
            TEXTURE3D(_VelocityTexture);
            SAMPLER(sampler_linear_clamp);
            
            // Global Parameters
            float4x4 _CameraToWorld;
            float4x4 _CameraInvProjection;
            float3 _VolumePosition;
            float3 _VolumeSize;
            float4 _FluidColor;
            float _AbsorptionStrength;
            float _ScatteringStrength;
            float _DensityThreshold;
            float _StepSize;
            int _MaxSteps;
            float _Time;
            
            // Ray-box intersection
            float2 RayBoxIntersection(float3 rayOrigin, float3 rayDirection, float3 boxMin, float3 boxMax)
            {
                float3 invDir = 1.0 / rayDirection;
                float3 t1 = (boxMin - rayOrigin) * invDir;
                float3 t2 = (boxMax - rayOrigin) * invDir;
                
                float3 tMin = min(t1, t2);
                float3 tMax = max(t1, t2);
                
                float tNear = max(max(tMin.x, tMin.y), tMin.z);
                float tFar = min(min(tMax.x, tMax.y), tMax.z);
                
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
            
            // Lighting calculation for volumetric scattering
            float3 CalculateVolumetricLighting(float3 worldPos, float3 rayDirection, float density)
            {
                Light mainLight = GetMainLight();
                float3 lightDir = mainLight.direction;
                float3 lightColor = mainLight.color;
                
                // Phase function for scattering (Henyey-Greenstein approximation)
                float cosTheta = dot(-rayDirection, lightDir);
                float g = 0.3; // Anisotropy factor
                float phase = (1.0 - g * g) / pow(1.0 + g * g - 2.0 * g * cosTheta, 1.5);
                
                // Simple light attenuation through volume
                float3 lightUVW = WorldToVolumeUV(worldPos);
                float lightAttenuation = 1.0;
                
                // Sample along light ray for shadow approximation
                float3 lightStep = lightDir * _StepSize * 0.5;
                for (int i = 0; i < 8; i++)
                {
                    lightUVW += lightStep / _VolumeSize;
                    float shadowDensity = SampleDensity(lightUVW);
                    lightAttenuation *= exp(-shadowDensity * _AbsorptionStrength * _StepSize * 0.5);
                }
                
                return lightColor * phase * lightAttenuation * _ScatteringStrength;
            }
            
            FragmentInput VertexMain(VertexInput input)
            {
                FragmentInput output;
                
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                
                // Calculate ray direction in world space
                float4 clipPos = float4(input.uv * 2.0 - 1.0, 1.0, 1.0);
                float4 viewPos = mul(_CameraInvProjection, clipPos);
                viewPos.xyz /= viewPos.w;
                output.rayDirection = mul(_CameraToWorld, float4(viewPos.xyz, 0.0)).xyz;
                
                return output;
            }
            
            float4 FragmentMain(FragmentInput input) : SV_Target
            {
                // Get camera position and ray direction
                float3 cameraPos = _CameraToWorld._m03_m13_m23;
                float3 rayDir = normalize(input.rayDirection);
                
                // Calculate volume bounds
                float3 volumeMin = _VolumePosition - _VolumeSize * 0.5;
                float3 volumeMax = _VolumePosition + _VolumeSize * 0.5;
                
                // Ray-volume intersection
                float2 intersection = RayBoxIntersection(cameraPos, rayDir, volumeMin, volumeMax);
                float tNear = intersection.x;
                float tFar = intersection.y;
                
                // Early exit if no intersection
                if (tFar <= tNear || tFar <= 0.0)
                    discard;
                
                // Sample scene depth for intersection with solid geometry
                float sceneDepth = SampleSceneDepth(input.uv);
                float linearDepth = LinearEyeDepth(sceneDepth, _ZBufferParams);
                tFar = min(tFar, linearDepth);
                
                if (tFar <= tNear)
                    discard;
                
                // Ray marching
                float3 currentPos = cameraPos + rayDir * tNear;
                float3 rayStep = rayDir * _StepSize;
                
                float4 accumulatedColor = float4(0, 0, 0, 0);
                float transmittance = 1.0;
                
                float t = tNear;
                int stepCount = 0;
                
                while (t < tFar && stepCount < _MaxSteps && transmittance > 0.01)
                {
                    float3 uvw = WorldToVolumeUV(currentPos);
                    float density = SampleDensity(uvw);
                    
                    if (density > _DensityThreshold)
                    {
                        // Calculate absorption and scattering
                        float absorption = density * _AbsorptionStrength * _StepSize;
                        float scattering = density * _ScatteringStrength * _StepSize;
                        
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
                    t += _StepSize;
                    stepCount++;
                }
                
                // Final color output
                accumulatedColor.rgb = lerp(accumulatedColor.rgb, _FluidColor.rgb, 
                                          saturate(accumulatedColor.a * 0.5));
                accumulatedColor.a = saturate(accumulatedColor.a);
                
                return accumulatedColor;
            }
            
            ENDHLSL
        }
    }
    
    FallBack "Hidden/InternalErrorShader"
}