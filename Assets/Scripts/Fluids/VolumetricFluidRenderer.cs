using System.Collections.Generic;
using Tuntenfisch.Fluids;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Tuntenfisch.Rendering
{
    /// <summary>
    /// Volumetric renderer for fluid simulation using ray marching through 3D textures.
    /// Renders fluids directly from simulation textures without mesh conversion.
    /// </summary>
    public class VolumetricFluidRenderer : MonoBehaviour
    {
        [Header("Rendering Settings")]
        [SerializeField] private Material m_volumetricFluidMaterial;
        [SerializeField] private int m_maxRaySteps = 128;
        [SerializeField] private float m_stepSize = 0.5f;
        [SerializeField] private float m_densityThreshold = 0.01f;
        [SerializeField] private float m_absorptionStrength = 1.0f;
        [SerializeField] private float m_scatteringStrength = 0.5f;
        [SerializeField] private Color m_fluidColor = new Color(0.2f, 0.6f, 1.0f, 1.0f);

        [Header("Performance")]
        [SerializeField] private bool m_enableTemporalReprojection = true;
        [SerializeField] private bool m_enableDepthCulling = true;
        [SerializeField] private float m_maxRenderDistance = 100f;

        private Camera m_camera;
        private CommandBuffer m_commandBuffer;
        private Mesh m_fullscreenQuad;
        
        // Material property IDs
        private int m_densityTextureID;
        private int m_velocityTextureID;
        private int m_cameraMatrixID;
        private int m_invCameraMatrixID;
        private int m_volumePositionID;
        private int m_volumeSizeID;
        private int m_maxStepsID;
        private int m_stepSizeID;
        private int m_densityThresholdID;
        private int m_absorptionID;
        private int m_scatteringID;
        private int m_fluidColorID;
        private int m_timeID;

        // Registered chunks for rendering
        private Dictionary<int3, ChunkVolumetricData> m_activeChunks = new Dictionary<int3, ChunkVolumetricData>();

        private void Awake()
        {
            m_camera = GetComponent<Camera>();
            if (m_camera == null)
            {
                m_camera = Camera.main;
            }

            InitializeResources();
            CachePropertyIDs();
        }

        private void OnEnable()
        {
            if (m_camera != null)
            {
                m_camera.AddCommandBuffer(CameraEvent.AfterForwardOpaque, m_commandBuffer);
            }
        }

        private void OnDisable()
        {
            if (m_camera != null)
            {
                m_camera.RemoveCommandBuffer(CameraEvent.AfterForwardOpaque, m_commandBuffer);
            }
        }

        private void OnDestroy()
        {
            ReleaseResources();
        }

        private void InitializeResources()
        {
            // Create command buffer for volumetric rendering
            m_commandBuffer = new CommandBuffer();
            m_commandBuffer.name = "Volumetric Fluid Rendering";

            // Create fullscreen quad for ray marching
            m_fullscreenQuad = CreateFullscreenQuad();

            // Validate material
            if (m_volumetricFluidMaterial == null)
            {
                Debug.LogError("Volumetric fluid material not assigned!");
                enabled = false;
                return;
            }
        }

        private void ReleaseResources()
        {
            m_commandBuffer?.Release();
            
            if (m_fullscreenQuad != null)
            {
                DestroyImmediate(m_fullscreenQuad);
            }
        }

        private void CachePropertyIDs()
        {
            m_densityTextureID = Shader.PropertyToID("_DensityTexture");
            m_velocityTextureID = Shader.PropertyToID("_VelocityTexture");
            m_cameraMatrixID = Shader.PropertyToID("_CameraToWorld");
            m_invCameraMatrixID = Shader.PropertyToID("_CameraInvProjection");
            m_volumePositionID = Shader.PropertyToID("_VolumePosition");
            m_volumeSizeID = Shader.PropertyToID("_VolumeSize");
            m_maxStepsID = Shader.PropertyToID("_MaxSteps");
            m_stepSizeID = Shader.PropertyToID("_StepSize");
            m_densityThresholdID = Shader.PropertyToID("_DensityThreshold");
            m_absorptionID = Shader.PropertyToID("_AbsorptionStrength");
            m_scatteringID = Shader.PropertyToID("_ScatteringStrength");
            m_fluidColorID = Shader.PropertyToID("_FluidColor");
            m_timeID = Shader.PropertyToID("_Time");
        }

        /// <summary>
        /// Register a chunk for volumetric rendering
        /// </summary>
        public void RegisterChunk(int3 chunkCoordinate, ChunkFluidTextures fluidData, Vector3 worldPosition, Vector3 volumeSize)
        {
            if (fluidData == null || !fluidData.IsValid())
                return;

            var chunkData = new ChunkVolumetricData
            {
                fluidData = fluidData,
                WorldPosition = worldPosition,
                VolumeSize = volumeSize,
                LastUpdateTime = Time.time
            };

            m_activeChunks[chunkCoordinate] = chunkData;
        }

        /// <summary>
        /// Unregister a chunk from volumetric rendering
        /// </summary>
        public void UnregisterChunk(int3 chunkCoordinate)
        {
            m_activeChunks.Remove(chunkCoordinate);
        }

        private void Update()
        {
            UpdateCommandBuffer();
        }

        private void UpdateCommandBuffer()
        {
            m_commandBuffer.Clear();

            if (m_camera == null || m_volumetricFluidMaterial == null)
                return;

            // Set global parameters
            SetGlobalRenderingParameters();

            // Render each active chunk
            foreach (var kvp in m_activeChunks)
            {
                var chunkData = kvp.Value;
                
                // Distance culling
                float distanceToCamera = Vector3.Distance(m_camera.transform.position, chunkData.WorldPosition);
                if (distanceToCamera > m_maxRenderDistance)
                    continue;

                RenderChunkVolume(chunkData);
            }
        }

        private void SetGlobalRenderingParameters()
        {
            // Camera matrices for ray reconstruction
            Matrix4x4 cameraToWorld = m_camera.cameraToWorldMatrix;
            Matrix4x4 invProjection = m_camera.projectionMatrix.inverse;

            m_commandBuffer.SetGlobalMatrix(m_cameraMatrixID, cameraToWorld);
            m_commandBuffer.SetGlobalMatrix(m_invCameraMatrixID, invProjection);

            // Rendering parameters
            m_commandBuffer.SetGlobalInt(m_maxStepsID, m_maxRaySteps);
            m_commandBuffer.SetGlobalFloat(m_stepSizeID, m_stepSize);
            m_commandBuffer.SetGlobalFloat(m_densityThresholdID, m_densityThreshold);
            m_commandBuffer.SetGlobalFloat(m_absorptionID, m_absorptionStrength);
            m_commandBuffer.SetGlobalFloat(m_scatteringID, m_scatteringStrength);
            m_commandBuffer.SetGlobalColor(m_fluidColorID, m_fluidColor);
            m_commandBuffer.SetGlobalFloat(m_timeID, Time.time);
        }

        private void RenderChunkVolume(ChunkVolumetricData chunkData)
        {
            // Set chunk-specific textures and parameters
            m_commandBuffer.SetGlobalTexture(m_densityTextureID, chunkData.fluidData.DensityRead);
            m_commandBuffer.SetGlobalTexture(m_velocityTextureID, chunkData.fluidData.VelocityRead);
            m_commandBuffer.SetGlobalVector(m_volumePositionID, chunkData.WorldPosition);
            m_commandBuffer.SetGlobalVector(m_volumeSizeID, chunkData.VolumeSize);

            // Draw fullscreen quad with volumetric material
            m_commandBuffer.DrawMesh(m_fullscreenQuad, Matrix4x4.identity, m_volumetricFluidMaterial);
        }

        private Mesh CreateFullscreenQuad()
        {
            var mesh = new Mesh();
            
            // Vertices for fullscreen quad in NDC space
            Vector3[] vertices = new Vector3[]
            {
                new Vector3(-1, -1, 0), // Bottom-left
                new Vector3(-1,  1, 0), // Top-left
                new Vector3( 1,  1, 0), // Top-right
                new Vector3( 1, -1, 0)  // Bottom-right
            };

            // UV coordinates
            Vector2[] uvs = new Vector2[]
            {
                new Vector2(0, 0), // Bottom-left
                new Vector2(0, 1), // Top-left
                new Vector2(1, 1), // Top-right
                new Vector2(1, 0)  // Bottom-right
            };

            // Triangle indices
            int[] triangles = new int[]
            {
                0, 1, 2,  // First triangle
                0, 2, 3   // Second triangle
            };

            mesh.vertices = vertices;
            mesh.uv = uvs;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            
            return mesh;
        }

        /// <summary>
        /// Update rendering settings at runtime
        /// </summary>
        public void UpdateRenderingSettings(int maxSteps, float stepSize, float densityThreshold, 
            float absorption, float scattering, Color fluidColor)
        {
            m_maxRaySteps = maxSteps;
            m_stepSize = stepSize;
            m_densityThreshold = densityThreshold;
            m_absorptionStrength = absorption;
            m_scatteringStrength = scattering;
            m_fluidColor = fluidColor;
        }

        /// <summary>
        /// Get current chunk count for debugging
        /// </summary>
        public int GetActiveChunkCount() => m_activeChunks.Count;

        /// <summary>
        /// Clear all registered chunks
        /// </summary>
        public void ClearAllChunks()
        {
            m_activeChunks.Clear();
        }
    }

    /// <summary>
    /// Data structure for chunk volumetric rendering
    /// </summary>
    [System.Serializable]
    public class ChunkVolumetricData
    {
        public ChunkFluidTextures fluidData;
        public Vector3 WorldPosition;
        public Vector3 VolumeSize;
        public float LastUpdateTime;
    }
}

// Extension to FluidSimulation for volumetric integration
namespace Tuntenfisch.Fluids
{
    public static class VolumetricFluidExtensions
    {
        /// <summary>
        /// Register chunk with volumetric renderer after simulation step
        /// </summary>
        public static void RegisterForVolumetricRendering(this ChunkFluidTextures fluidData, 
            int3 chunkCoordinate, Vector3 worldPosition, Vector3 volumeSize)
        {
            var renderer = Object.FindFirstObjectByType<Tuntenfisch.Rendering.VolumetricFluidRenderer>();
            if (renderer != null)
            {
                renderer.RegisterChunk(chunkCoordinate, fluidData, worldPosition, volumeSize);
            }
        }

        /// <summary>
        /// Unregister chunk from volumetric renderer
        /// </summary>
        public static void UnregisterFromVolumetricRendering(int3 chunkCoordinate)
        {
            var renderer = Object.FindFirstObjectByType<Tuntenfisch.Rendering.VolumetricFluidRenderer>();
            if (renderer != null)
            {
                renderer.UnregisterChunk(chunkCoordinate);
            }
        }
    }
}