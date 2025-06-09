using System;
using System.Collections.Generic;
using Tuntenfisch.Fluids;
using Tuntenfisch.Rendering;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

namespace Tuntenfisch.Rendering
{
    public class VolumetricFluidRendererFeature : ScriptableRendererFeature
    {
        [SerializeField] private VolumetricFluidSettings settings;
        [SerializeField] private Shader volumetricFluidShader;
        private Material volumetricFluidMaterial;
        private VolumetricFluidRenderPass volumetricFluidRenderPass;

        public override void Create()
        {
            if (volumetricFluidShader == null)
            {
                Debug.LogError("VolumetricFluidRendererFeature: Shader not assigned!");
                return;
            }

            volumetricFluidMaterial = new Material(volumetricFluidShader);
            volumetricFluidRenderPass = new VolumetricFluidRenderPass(volumetricFluidMaterial, settings);
            
            // Render after opaque but before transparent objects
            volumetricFluidRenderPass.renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (volumetricFluidRenderPass == null)
                return;

            // Only render for game cameras (not scene view, inspector previews, etc.)
            if (renderingData.cameraData.cameraType == CameraType.Game)
            {
                renderer.EnqueuePass(volumetricFluidRenderPass);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (volumetricFluidMaterial != null)
            {
                if (Application.isPlaying)
                {
                    Destroy(volumetricFluidMaterial);
                }
                else
                {
                    DestroyImmediate(volumetricFluidMaterial);
                }
            }
        }
    }

    [Serializable]
    public class VolumetricFluidSettings
    {
        [Header("Volumetric Properties")]
        [Range(32, 256)] public int maxRaySteps = 128;
        [Range(0.1f, 2.0f)] public float stepSize = 0.5f;
        [Range(0.001f, 0.1f)] public float densityThreshold = 0.01f;
        
        [Header("Lighting")]
        [Range(0.0f, 5.0f)] public float absorptionStrength = 1.0f;
        [Range(0.0f, 2.0f)] public float scatteringStrength = 0.5f;
        public Color fluidColor = new Color(0.2f, 0.6f, 1.0f, 1.0f);
        
        [Header("Performance")]
        [Range(0.5f, 2.0f)] public float renderScale = 1.0f;
        public bool enableDepthCulling = true;
        [Range(10f, 200f)] public float maxRenderDistance = 100f;
        [Range(1, 32)] public int maxChunksPerFrame = 16;
    }

    public class VolumetricFluidRenderPass : ScriptableRenderPass
    {
        // Shader property IDs
        private static readonly int s_densityTextureID = Shader.PropertyToID("_DensityTexture");
        private static readonly int s_velocityTextureID = Shader.PropertyToID("_VelocityTexture");
        // private static readonly int s_cameraMatrixID = Shader.PropertyToID("_CameraToWorld");
        private static readonly int s_invCameraMatrixID = Shader.PropertyToID("_CameraInvProjection");
        private static readonly int s_volumePositionID = Shader.PropertyToID("_VolumePosition");
        private static readonly int s_volumeSizeID = Shader.PropertyToID("_VolumeSize");
        private static readonly int s_maxStepsID = Shader.PropertyToID("_MaxSteps");
        private static readonly int s_stepSizeID = Shader.PropertyToID("_StepSize");
        private static readonly int s_densityThresholdID = Shader.PropertyToID("_DensityThreshold");
        private static readonly int s_absorptionID = Shader.PropertyToID("_AbsorptionStrength");
        private static readonly int s_scatteringID = Shader.PropertyToID("_ScatteringStrength");
        private static readonly int s_fluidColorID = Shader.PropertyToID("_FluidColor");

        private const string k_VolumetricFluidTextureName = "_VolumetricFluidTexture";
        private const string k_PassName = "VolumetricFluidRenderPass";

        private VolumetricFluidSettings m_defaultSettings;
        private Material m_material;

        // Chunk tracking
        private static Dictionary<int3, ChunkVolumetricData> s_activeChunks = new Dictionary<int3, ChunkVolumetricData>();
        private List<ChunkVolumetricData> m_visibleChunks = new List<ChunkVolumetricData>();

        public VolumetricFluidRenderPass(Material material, VolumetricFluidSettings defaultSettings)
        {
            m_material = material;
            m_defaultSettings = defaultSettings;
        }

        // Static methods for chunk registration (called from chunk systems)
        public static void RegisterChunk(int3 chunkCoordinate, ChunkFluidTextures fluidData, Vector3 worldPosition, Vector3 volumeSize)
        {
            if (fluidData == null || !fluidData.IsValid())
                return;

            var chunkData = new ChunkVolumetricData
            {
                fluidData = fluidData,
                WorldPosition = worldPosition,
                VolumeSize = volumeSize,
                LastUpdateTime = Time.time,
                ChunkCoordinate = chunkCoordinate
            };

            s_activeChunks[chunkCoordinate] = chunkData;
        }

        public static void UnregisterChunk(int3 chunkCoordinate)
        {
            s_activeChunks.Remove(chunkCoordinate);
        }

        public static void ClearAllChunks()
        {
            s_activeChunks.Clear();
        }

        public static int GetActiveChunkCount() => s_activeChunks.Count;

        private void UpdateVolumetricSettings()
        {
            if (m_material == null) return;

            // Use Volume settings or default settings
            var volumeComponent = VolumeManager.instance.stack.GetComponent<VolumetricFluidVolumeComponent>();
            
            int maxSteps = volumeComponent.maxRaySteps.overrideState ? 
                volumeComponent.maxRaySteps.value : m_defaultSettings.maxRaySteps;
            float stepSize = volumeComponent.stepSize.overrideState ? 
                volumeComponent.stepSize.value : m_defaultSettings.stepSize;
            float densityThreshold = volumeComponent.densityThreshold.overrideState ? 
                volumeComponent.densityThreshold.value : m_defaultSettings.densityThreshold;
            float absorption = volumeComponent.absorptionStrength.overrideState ? 
                volumeComponent.absorptionStrength.value : m_defaultSettings.absorptionStrength;
            float scattering = volumeComponent.scatteringStrength.overrideState ? 
                volumeComponent.scatteringStrength.value : m_defaultSettings.scatteringStrength;
            Color fluidColor = volumeComponent.fluidColor.overrideState ? 
                volumeComponent.fluidColor.value : m_defaultSettings.fluidColor;

            // Set material properties
            m_material.SetInt(s_maxStepsID, maxSteps);
            m_material.SetFloat(s_stepSizeID, stepSize);
            m_material.SetFloat(s_densityThresholdID, densityThreshold);
            m_material.SetFloat(s_absorptionID, absorption);
            m_material.SetFloat(s_scatteringID, scattering);
            m_material.SetColor(s_fluidColorID, fluidColor);
        }

        private void UpdateVisibleChunks(Camera camera)
        {
            m_visibleChunks.Clear();

            if (s_activeChunks.Count == 0)
                return;

            Vector3 cameraPos = camera.transform.position;

            foreach (var chunkData in s_activeChunks.Values)
            {
                // Distance culling
                float distanceToCamera = Vector3.Distance(cameraPos, chunkData.WorldPosition);
                if (m_defaultSettings.enableDepthCulling && distanceToCamera > m_defaultSettings.maxRenderDistance)
                    continue;

                // Simple frustum culling
                if (m_defaultSettings.enableDepthCulling && !IsChunkInCameraFrustum(camera, chunkData))
                    continue;

                chunkData.DistanceToCamera = distanceToCamera;
                m_visibleChunks.Add(chunkData);
            }

            // Sort by distance (back to front for proper alpha blending)
            m_visibleChunks.Sort((a, b) => b.DistanceToCamera.CompareTo(a.DistanceToCamera));

            // Limit chunks per frame for performance
            if (m_visibleChunks.Count > m_defaultSettings.maxChunksPerFrame)
            {
                m_visibleChunks.RemoveRange(m_defaultSettings.maxChunksPerFrame, 
                    m_visibleChunks.Count - m_defaultSettings.maxChunksPerFrame);
            }
        }

        private bool IsChunkInCameraFrustum(Camera camera, ChunkVolumetricData chunkData)
        {
            var bounds = new Bounds(chunkData.WorldPosition, chunkData.VolumeSize);
            return GeometryUtility.TestPlanesAABB(GeometryUtility.CalculateFrustumPlanes(camera), bounds);
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

            // Don't render if we're rendering to back buffer or no chunks are active
            if (resourceData.isActiveTargetBackBuffer || s_activeChunks.Count == 0)
                return;

            // Update visible chunks and settings
            UpdateVisibleChunks(cameraData.camera);
            UpdateVolumetricSettings();

            if (m_visibleChunks.Count == 0)
                return;

            // Set up camera matrices for ray reconstruction
            // Matrix4x4 cameraToWorld = cameraData.camera.cameraToWorldMatrix;
            Matrix4x4 invProjection = cameraData.camera.projectionMatrix.inverse;
            // m_material.SetMatrix(s_cameraMatrixID, cameraToWorld);
            m_material.SetMatrix(s_invCameraMatrixID, invProjection);

            // Get source and create destination texture
            TextureHandle srcCamColor = resourceData.activeColorTexture;
            var volumetricTextureDesc = srcCamColor.GetDescriptor(renderGraph);
            volumetricTextureDesc.name = k_VolumetricFluidTextureName;
            volumetricTextureDesc.depthBufferBits = 0;
            
            // Apply render scale for performance
            if (!Mathf.Approximately(m_defaultSettings.renderScale, 1.0f))
            {
                volumetricTextureDesc.width = Mathf.RoundToInt(volumetricTextureDesc.width * m_defaultSettings.renderScale);
                volumetricTextureDesc.height = Mathf.RoundToInt(volumetricTextureDesc.height * m_defaultSettings.renderScale);
            }

            var volumetricTexture = renderGraph.CreateTexture(volumetricTextureDesc);

            // Validity check
            if (!srcCamColor.IsValid() || !volumetricTexture.IsValid())
                return;

            // Clear volumetric texture first
            var clearParams = new RenderGraphUtils.BlitMaterialParameters(srcCamColor, volumetricTexture, m_material, -1);
            renderGraph.AddBlitPass(clearParams, "ClearVolumetricTexture");

            // Add volumetric pass for each visible chunk
            for (int i = 0; i < m_visibleChunks.Count; i++)
            {
                var chunkData = m_visibleChunks[i];
                AddVolumetricFluidChunkPass(renderGraph, chunkData, volumetricTexture, srcCamColor, i);
            }

            // Final composite pass
            if (!Mathf.Approximately(m_defaultSettings.renderScale, 1.0f))
            {
                // Upscale back to full resolution
                RenderGraphUtils.BlitMaterialParameters upscaleParams = 
                    new(volumetricTexture, srcCamColor, m_material, 2); // Pass 2 is upscale
                renderGraph.AddBlitPass(upscaleParams, "VolumetricFluidUpscale");
            }
            else
            {
                // Direct composite
                RenderGraphUtils.BlitMaterialParameters compositeParams = 
                    new(volumetricTexture, srcCamColor, m_material, 1); // Pass 1 is composite
                renderGraph.AddBlitPass(compositeParams, "VolumetricFluidComposite");
            }
        }

        private void AddVolumetricFluidChunkPass(RenderGraph renderGraph, ChunkVolumetricData chunkData, 
            TextureHandle destination, TextureHandle source, int chunkIndex)
        {
            // Set chunk-specific material properties before the blit
            m_material.SetTexture(s_densityTextureID, chunkData.fluidData.DensityRead);
            m_material.SetTexture(s_velocityTextureID, chunkData.fluidData.VelocityRead);
            m_material.SetVector(s_volumePositionID, chunkData.WorldPosition);
            m_material.SetVector(s_volumeSizeID, chunkData.VolumeSize);

            // Use blit with volumetric pass (pass 0)
            RenderGraphUtils.BlitMaterialParameters blitParams = 
                new(source, destination, m_material, 0);
            renderGraph.AddBlitPass(blitParams, $"{k_PassName}_Chunk{chunkIndex}");
        }
    }

    // Updated chunk data structure
    [Serializable]
    public class ChunkVolumetricData
    {
        public ChunkFluidTextures fluidData;
        public Vector3 WorldPosition;
        public Vector3 VolumeSize;
        public float LastUpdateTime;
        public int3 ChunkCoordinate;
        public float DistanceToCamera; // Used for sorting
    }
}

// Extension methods for integration
namespace Tuntenfisch.Fluids
{
    public static class VolumetricFluidExtensions
    {
        public static void RegisterForVolumetricRendering(this ChunkFluidTextures fluidData,
            int3 chunkCoordinate, Vector3 worldPosition, Vector3 volumeSize)
        {
            Tuntenfisch.Rendering.VolumetricFluidRenderPass.RegisterChunk(chunkCoordinate, fluidData, worldPosition, volumeSize);
        }

        public static void UnregisterFromVolumetricRendering(int3 chunkCoordinate)
        {
            Tuntenfisch.Rendering.VolumetricFluidRenderPass.UnregisterChunk(chunkCoordinate);
        }
    }
}