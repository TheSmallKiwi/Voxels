using System.Collections.Generic;
using System.Linq;
using Tuntenfisch.Fluids;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace Tuntenfisch.Rendering
{
    /// <summary>
    /// Volumetric renderer for fluid simulation using ray marching through 3D textures.
    /// Renders fluids directly from simulation textures without mesh conversion.
    /// </summary>
    public class VolumetricFluidRenderer : MonoBehaviour
    {
        [Header("Rendering Settings")] [SerializeField]
        private Material m_volumetricFluidMaterial;

        [SerializeField] private int m_maxRaySteps = 128;
        [SerializeField] private float m_stepSize = 0.5f;
        [SerializeField] private float m_densityThreshold = 0.01f;
        [SerializeField] private float m_absorptionStrength = 1.0f;
        [SerializeField] private float m_scatteringStrength = 0.5f;
        [SerializeField] private Color m_fluidColor = new Color(0.2f, 0.6f, 1.0f, 1.0f);

        [Header("Performance")] [SerializeField]
        private bool m_enableTemporalReprojection = true;

        [SerializeField] private bool m_enableDepthCulling = true;
        [SerializeField] private float m_maxRenderDistance = 100f;

        private Camera m_camera;
        private CommandBuffer m_commandBuffer;
        [SerializeField] private Mesh m_fullscreenQuad;

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

        // In VolumetricFluidRenderer
        private void OnEnable()
        {
            if (m_camera != null)
            {
                Debug.Log($"[VolumetricRenderer] Adding command buffer to camera: {m_camera.name}");
                m_camera.AddCommandBuffer(CameraEvent.AfterForwardOpaque, m_commandBuffer);

                // Verify it was added
                var buffers = m_camera.GetCommandBuffers(CameraEvent.AfterForwardOpaque);
                Debug.Log(
                    $"[VolumetricRenderer] Camera now has {buffers.Length} command buffers for AfterForwardOpaque");
            }
            else
            {
                Debug.LogError("[VolumetricRenderer] No camera found for command buffer!");
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

            // Validate material
            if (m_volumetricFluidMaterial == null)
            {
                Debug.LogError("Volumetric fluid material not assigned!");
                enabled = false;
                return;
            }

            // Check if material has the correct shader
            if (m_volumetricFluidMaterial.shader == null)
            {
                Debug.LogError("Volumetric fluid material has no shader!");
                return;
            }

            Debug.Log($"[VolumetricRenderer] Using material: {m_volumetricFluidMaterial.name}");
            Debug.Log($"[VolumetricRenderer] Shader: {m_volumetricFluidMaterial.shader.name}");

            // Test if shader compiles
            if (!m_volumetricFluidMaterial.shader.isSupported)
            {
                Debug.LogError("Volumetric fluid shader is not supported on this platform!");
            }
        }

        // Add this test method to VolumetricFluidRenderer
        [ContextMenu("Test with Simple Material")]
        private void TestWithSimpleMaterial()
        {
            // Create a simple test material
            var testMaterial = new Material(Shader.Find("Unlit/Color"));
            testMaterial.color = Color.red;

            // Temporarily replace the volumetric material
            var originalMaterial = m_volumetricFluidMaterial;
            m_volumetricFluidMaterial = testMaterial;

            Debug.Log("[VolumetricRenderer] Testing with simple red material");

            // Restore after a few seconds
            Invoke(nameof(RestoreOriginalMaterial), 2f);
        }

        private void RestoreOriginalMaterial()
        {
            // You'll need to store the original material reference
            Debug.Log("[VolumetricRenderer] Restoring original material");
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
        }

        /// <summary>
        /// Register a chunk for volumetric rendering
        /// </summary>
        public void RegisterChunk(int3 chunkCoordinate, ChunkFluidTextures fluidData, Vector3 worldPosition,
            Vector3 volumeSize)
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
            TestDirectRender();
            // if (InputSystem.GetDevice<Keyboard>()?.tKey.isPressed == true) // Test input system
            // {
            //     
            // }
            UpdateCommandBuffer();
        }

        private void UpdateCommandBuffer()
        {
            m_commandBuffer.Clear();

            if (m_camera == null || m_volumetricFluidMaterial == null)
            {
                Debug.LogError("Missing camera or material for volumetric rendering");
                return;
            }

            // Debug.Log($"[VolumetricRenderer] Updating command buffer with {m_activeChunks.Count} chunks");

            // Add a clear debug marker
            m_commandBuffer.BeginSample("Volumetric Fluid Debug");

            SetGlobalRenderingParameters();

            int chunksRendered = 0;
            foreach (var kvp in m_activeChunks)
            {
                var chunkData = kvp.Value;

                float distanceToCamera = Vector3.Distance(m_camera.transform.position, chunkData.WorldPosition);
                Debug.Log(
                    $"[VolumetricRenderer] Chunk at {chunkData.WorldPosition}, distance: {distanceToCamera}, max: {m_maxRenderDistance}");

                if (distanceToCamera > m_maxRenderDistance)
                {
                    Debug.Log($"[VolumetricRenderer] Skipping chunk - too far");
                    continue;
                }

                Debug.Log($"[VolumetricRenderer] Rendering chunk {chunksRendered}");
                RenderChunkVolume(chunkData);
                chunksRendered++;
            }

            m_commandBuffer.EndSample("Volumetric Fluid Debug");

            // Debug.Log($"[VolumetricRenderer] Command buffer updated. Rendered {chunksRendered} chunks");
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
        }

        private void RenderChunkVolume(ChunkVolumetricData chunkData)
        {
            // Set chunk-specific textures and parameters
            m_commandBuffer.SetGlobalTexture(m_densityTextureID, chunkData.fluidData.DensityRead);
            m_commandBuffer.SetGlobalTexture(m_velocityTextureID, chunkData.fluidData.VelocityRead);
            m_commandBuffer.SetGlobalVector(m_volumePositionID, chunkData.WorldPosition);
            m_commandBuffer.SetGlobalVector(m_volumeSizeID, chunkData.VolumeSize);

            // Draw fullscreen quad with volumetric material
            m_commandBuffer.DrawMesh(m_fullscreenQuad, Matrix4x4.identity, m_volumetricFluidMaterial, 0, 0);
        }

        private void TestDirectRender()
        {
            if (m_fullscreenQuad == null || m_volumetricFluidMaterial == null)
                return;

            // Set up material properties
            SetGlobalRenderingParameters();

            if (m_activeChunks.Count > 0)
            {
                var firstChunk = m_activeChunks.Values.First();
                RenderChunkVolume(firstChunk);
            }

            // Draw immediately
            RenderParams rp = new RenderParams(m_volumetricFluidMaterial);
            Graphics.RenderMesh(rp, m_fullscreenQuad, 0, m_camera.cameraToWorldMatrix);
        }

        private Mesh CreateFullscreenQuad()
        {
            var mesh = new Mesh();
            mesh.name = "Volumetric Fluid Quad";

            Vector3[] vertices = new Vector3[]
            {
                new Vector3(-1, -1, -0.5f),
                new Vector3(-1, 1, -0.5f),
                new Vector3(1, 1, -0.5f),
                new Vector3(1, -1, -0.5f)
            };

            Vector2[] uvs = new Vector2[]
            {
                new Vector2(0, 0),
                new Vector2(0, 1),
                new Vector2(1, 1),
                new Vector2(1, 0)
            };

            int[] triangles = new int[]
            {
                0, 1, 2,
                0, 2, 3
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
                Debug.Log(
                    $"Registered chunk at {chunkCoordinate} for volumetric rendering. Position: {worldPosition}, Size: {volumeSize}");
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