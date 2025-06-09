using System.Collections.Generic;
using System.Linq;
using Tuntenfisch.Fluids;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Tuntenfisch.Rendering
{
    /// <summary>
    /// Volumetric renderer for fluid simulation using Graphics.RenderMesh with RenderParams.
    /// Renders fluids directly from simulation textures without mesh conversion.
    /// </summary>
    public class VolumetricFluidRenderer : SceneViewFilter
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
        [SerializeField] private bool m_enableDepthCulling = true;
        [SerializeField] private float m_maxRenderDistance = 100f;
        [SerializeField] private int m_maxChunksPerFrame = 16;

        [Header("Debug")]
        [SerializeField] private bool m_enableDebugLogging;

        private Camera m_camera;
        private Mesh m_fullscreenQuad;
        private MaterialPropertyBlock m_propertyBlock;

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

        // Registered chunks for rendering
        private Dictionary<int3, ChunkVolumetricData> m_activeChunks = new Dictionary<int3, ChunkVolumetricData>();
        private List<ChunkVolumetricData> m_visibleChunks = new List<ChunkVolumetricData>();

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

        private void OnDestroy()
        {
            ReleaseResources();
        }

        private void InitializeResources()
        {
            // Create fullscreen quad for ray marching
            m_fullscreenQuad = CreateFullscreenQuad();

            // Create material property block for per-chunk properties
            m_propertyBlock = new MaterialPropertyBlock();

            // Validate material
            if (m_volumetricFluidMaterial == null)
            {
                Debug.LogError("Volumetric fluid material not assigned!");
                enabled = false;
                return;
            }

            if (m_enableDebugLogging)
            {
                Debug.Log($"[VolumetricRenderer] Using material: {m_volumetricFluidMaterial.name}");
                Debug.Log($"[VolumetricRenderer] Shader: {m_volumetricFluidMaterial.shader.name}");
            }

            // Test if shader compiles
            if (!m_volumetricFluidMaterial.shader.isSupported)
            {
                Debug.LogError("Volumetric fluid shader is not supported on this platform!");
            }
        }

        private void ReleaseResources()
        {
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
                LastUpdateTime = Time.time,
                ChunkCoordinate = chunkCoordinate
            };

            m_activeChunks[chunkCoordinate] = chunkData;

            if (m_enableDebugLogging)
            {
                Debug.Log($"[VolumetricRenderer] Registered chunk {chunkCoordinate} at {worldPosition}");
            }
        }

        /// <summary>
        /// Unregister a chunk from volumetric rendering
        /// </summary>
        public void UnregisterChunk(int3 chunkCoordinate)
        {
            if (m_activeChunks.Remove(chunkCoordinate) && m_enableDebugLogging)
            {
                Debug.Log($"[VolumetricRenderer] Unregistered chunk {chunkCoordinate}");
            }
        }

        private void Update()
        {
            if (m_camera == null || m_volumetricFluidMaterial == null || m_fullscreenQuad == null)
                return;

            // Cull and sort chunks
            UpdateVisibleChunks();

            // Set global rendering parameters
            SetGlobalRenderingParameters();

            // Render visible chunks using Graphics.RenderMesh
            RenderVisibleChunks();
        }

        private void UpdateVisibleChunks()
        {
            m_visibleChunks.Clear();

            Vector3 cameraPos = m_camera.transform.position;

            foreach (var chunkData in m_activeChunks.Values)
            {
                // Distance culling
                float distanceToCamera = Vector3.Distance(cameraPos, chunkData.WorldPosition);
                if (m_enableDepthCulling && distanceToCamera > m_maxRenderDistance)
                    continue;

                // Frustum culling (simple bounds check)
                if (m_enableDepthCulling && !IsChunkInCameraFrustum(chunkData))
                    continue;

                chunkData.DistanceToCamera = distanceToCamera;
                m_visibleChunks.Add(chunkData);
            }

            // Sort by distance (back to front for transparency)
            m_visibleChunks.Sort((a, b) => b.DistanceToCamera.CompareTo(a.DistanceToCamera));

            // Limit chunks per frame for performance
            if (m_visibleChunks.Count > m_maxChunksPerFrame)
            {
                m_visibleChunks.RemoveRange(m_maxChunksPerFrame, m_visibleChunks.Count - m_maxChunksPerFrame);
            }
        }

        private bool IsChunkInCameraFrustum(ChunkVolumetricData chunkData)
        {
            // Simple sphere-frustum test
            var bounds = new Bounds(chunkData.WorldPosition, chunkData.VolumeSize);
            return GeometryUtility.TestPlanesAABB(GeometryUtility.CalculateFrustumPlanes(m_camera), bounds);
        }

        private void SetGlobalRenderingParameters()
        {
            // Camera matrices for ray reconstruction
            Matrix4x4 cameraToWorld = m_camera.cameraToWorldMatrix;
            Matrix4x4 invProjection = m_camera.projectionMatrix.inverse;

            Shader.SetGlobalMatrix(m_cameraMatrixID, cameraToWorld);
            Shader.SetGlobalMatrix(m_invCameraMatrixID, invProjection);

            // Global rendering parameters
            Shader.SetGlobalInt(m_maxStepsID, m_maxRaySteps);
            Shader.SetGlobalFloat(m_stepSizeID, m_stepSize);
            Shader.SetGlobalFloat(m_densityThresholdID, m_densityThreshold);
            Shader.SetGlobalFloat(m_absorptionID, m_absorptionStrength);
            Shader.SetGlobalFloat(m_scatteringID, m_scatteringStrength);
            Shader.SetGlobalColor(m_fluidColorID, m_fluidColor);
        }

        private void RenderVisibleChunks()
        {
            foreach (var chunkData in m_visibleChunks)
            {
                RenderChunkVolume(chunkData);
            }

            if (m_enableDebugLogging && m_visibleChunks.Count > 0)
            {
                Debug.Log($"[VolumetricRenderer] Rendered {m_visibleChunks.Count} chunks");
            }
        }

        private void RenderChunkVolume(ChunkVolumetricData chunkData)
        {
            // Set chunk-specific properties in MaterialPropertyBlock
            m_propertyBlock.SetTexture(m_densityTextureID, chunkData.fluidData.DensityRead);
            m_propertyBlock.SetTexture(m_velocityTextureID, chunkData.fluidData.VelocityRead);
            m_propertyBlock.SetVector(m_volumePositionID, chunkData.WorldPosition);
            m_propertyBlock.SetVector(m_volumeSizeID, chunkData.VolumeSize);

            // Create RenderParams with material and property block
            RenderParams renderParams = new RenderParams(m_volumetricFluidMaterial)
            {
                matProps = m_propertyBlock,
                layer = gameObject.layer,
                rendererPriority = 0,
                worldBounds = new Bounds(chunkData.WorldPosition, chunkData.VolumeSize),
                // camera = m_camera,
                motionVectorMode = MotionVectorGenerationMode.Camera,
                reflectionProbeUsage = ReflectionProbeUsage.Off,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
                lightProbeUsage = LightProbeUsage.Off
            };

            // Render the fullscreen quad with volumetric material
            Graphics.RenderMesh(renderParams, m_fullscreenQuad, 0, m_camera.cameraToWorldMatrix);
        }

        private Mesh CreateFullscreenQuad()
        {
            var mesh = new Mesh();
            mesh.name = "Volumetric Fluid Quad";

            Vector3[] vertices = {
                new(-1, -1, -0.5f),
                new(-1, 1, -0.5f),
                new(1, 1, -0.5f),
                new(1, -1, -0.5f)
            };

            Vector2[] uvs = {
                new(0, 0),
                new(0, 1),
                new(1, 1),
                new(1, 0)
            };

            int[] triangles = {
                0, 1, 2,
                0, 2, 3
            };

            mesh.vertices = vertices;
            mesh.uv = uvs;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

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
        /// Get visible chunk count for debugging
        /// </summary>
        public int GetVisibleChunkCount() => m_visibleChunks.Count;

        /// <summary>
        /// Clear all registered chunks
        /// </summary>
        public void ClearAllChunks()
        {
            m_activeChunks.Clear();
            m_visibleChunks.Clear();
        }

        /// <summary>
        /// Force render a single chunk for testing
        /// </summary>
        [ContextMenu("Test Render First Chunk")]
        public void TestRenderFirstChunk()
        {
            if (m_activeChunks.Count > 0)
            {
                var firstChunk = m_activeChunks.Values.First();
                SetGlobalRenderingParameters();
                RenderChunkVolume(firstChunk);
                Debug.Log($"[VolumetricRenderer] Test rendered chunk at {firstChunk.WorldPosition}");
            }
            else
            {
                Debug.LogWarning("[VolumetricRenderer] No chunks available for test render");
            }
        }

        /// <summary>
        /// Debug information about current state
        /// </summary>
        [ContextMenu("Debug Renderer State")]
        public void DebugRendererState()
        {
            Debug.Log("=== VolumetricFluidRenderer Debug ===");
            Debug.Log($"Active Chunks: {m_activeChunks.Count}");
            Debug.Log($"Visible Chunks: {m_visibleChunks.Count}");
            Debug.Log($"Camera: {(m_camera ? m_camera.name : "None")}");
            Debug.Log($"Material: {(m_volumetricFluidMaterial ? m_volumetricFluidMaterial.name : "None")}");
            Debug.Log($"Mesh: {(m_fullscreenQuad ? "Valid" : "None")}");
            Debug.Log($"Max Render Distance: {m_maxRenderDistance}");
            Debug.Log($"Max Chunks Per Frame: {m_maxChunksPerFrame}");

            if (m_activeChunks.Count > 0)
            {
                Debug.Log($"Sample chunk position: {m_activeChunks.Values.First().WorldPosition}");
                Debug.Log($"Sample chunk size: {m_activeChunks.Values.First().VolumeSize}");
            }
            Debug.Log("=====================================");
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
        public int3 ChunkCoordinate;
        public float DistanceToCamera; // Used for sorting
    }
}