using System;
using System.Collections.Generic;
using Tuntenfisch.Fluids;
using Tuntenfisch.Generics;
using Tuntenfisch.Generics.Pool;
using Tuntenfisch.Voxels.CSG;
using Tuntenfisch.Voxels.DC;
using Tuntenfisch.Voxels.Materials;
using Tuntenfisch.Voxels.Volume;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Tuntenfisch.World
{
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider))]
    public class Chunk : MonoBehaviour, IPoolable
    {
        // Solid mesh rendering (existing functionality)
        private int m_currentLOD;
        private int m_targetLOD;
        private int m_vertexCount;
        private int m_triangleCount;
        private int3 m_chunkCoordinate;

        private Mesh m_mesh;
        private MeshFilter m_meshFilter;
        private MeshRenderer m_meshRenderer;
        private MeshCollider m_meshCollider;
        private OnMeshGenerated m_onMeshGeneratedDelegate;

        private ComputeBuffer m_voxelVolumeBuffer;
        private IRequest m_request;
        private JobHandle m_bakeJobHandle;
        private List<GPUVoxelVolumeCSGOperation> m_voxelVolumeCSGOperations;
        private ChunkFlags m_flags;

        [Header("Fluid Simulation")] [SerializeField]
        private bool m_enableFluidSimulation = true;

        [SerializeField] private float m_fluidTimeStep = 0.016f; // 60 FPS target
        [SerializeField] private bool m_enableVolumetricRendering = true;
        [SerializeField] private int m_maxSimulationStepsPerFrame = 3;

        // Updated fluid simulation data (texture-based)
        private ChunkFluidData m_fluidData;
        private bool m_registeredForVolumetricRendering = false;
        private float m_lastBoundaryUpdateTime = 0f;
        private const float BOUNDARY_UPDATE_INTERVAL = 0.1f; // Update boundaries every 100ms

        // Performance monitoring
        private float m_lastFluidUpdateTime = 0f;
        private int m_simulationStepsThisFrame = 0;

        public ComputeBuffer VoxelVolumeBuffer => m_voxelVolumeBuffer;
        public ChunkFluidData FluidData => m_fluidData;

        private void Awake()
        {
            WorldManager.VoxelConfig.MaterialConfig.OnDirtied += OnMaterialConfigChanged;
            InitializeMeshComponents();
            ApplyRenderMaterial();
            m_voxelVolumeCSGOperations = new List<GPUVoxelVolumeCSGOperation>();
        }

        private void Update()
        {
            m_simulationStepsThisFrame = 0;

            if (m_flags == 0 && !ShouldUpdateFluid())
            {
                return;
            }

            // Handle solid voxel volume regeneration
            if ((m_flags & ChunkFlags.VoxelVolumeRegenerationRequested) == ChunkFlags.VoxelVolumeRegenerationRequested)
            {
                m_flags &= ~ChunkFlags.VoxelVolumeRegenerationRequested;
                WorldManager.VoxelVolume.GenerateVoxelVolume(m_voxelVolumeBuffer, transform.position);

                // Mark fluid boundaries for update when voxel volume changes
                if (m_enableFluidSimulation && m_fluidData != null && m_fluidData.IsValid())
                {
                    m_flags |= ChunkFlags.FluidBoundaryUpdateRequired;
                }
            }

            // Handle CSG operations
            if ((m_flags & ChunkFlags.CSGOperationPerformed) == ChunkFlags.CSGOperationPerformed)
            {
                m_flags &= ~ChunkFlags.CSGOperationPerformed;
                WorldManager.VoxelVolume.ApplyVoxelVolumeCSGOperations(m_voxelVolumeBuffer, transform.position,
                    m_voxelVolumeCSGOperations);
                m_voxelVolumeCSGOperations.Clear();

                // Mark fluid boundaries for update after CSG operations
                if (m_enableFluidSimulation && m_fluidData != null && m_fluidData.IsValid())
                {
                    m_flags |= ChunkFlags.FluidBoundaryUpdateRequired;
                }
            }

            // Handle solid mesh regeneration
            if ((m_flags & ChunkFlags.MeshRegenerationRequested) == ChunkFlags.MeshRegenerationRequested &&
                (m_flags & ChunkFlags.IsBakingMesh) != ChunkFlags.IsBakingMesh && m_request == null)
            {
                m_flags &= ~ChunkFlags.MeshRegenerationRequested;
                m_request = WorldManager.DualContouring.RequestMeshAsync
                (
                    m_voxelVolumeBuffer,
                    m_currentLOD,
                    m_targetLOD,
                    m_vertexCount,
                    m_triangleCount,
                    transform.position,
                    m_onMeshGeneratedDelegate
                );
            }

            // Handle mesh baking completion
            if ((m_flags & ChunkFlags.IsBakingMesh) == ChunkFlags.IsBakingMesh && m_bakeJobHandle.IsCompleted)
            {
                m_flags &= ~ChunkFlags.IsBakingMesh;
                m_meshCollider.sharedMesh = null;
                m_meshCollider.sharedMesh = m_mesh;
            }

            // Handle fluid simulation updates
            if (m_enableFluidSimulation && m_fluidData != null && m_fluidData.IsValid())
            {
                UpdateFluidSimulation();
            }
        }

        private bool ShouldUpdateFluid()
        {
            return m_enableFluidSimulation &&
                   m_fluidData != null &&
                   m_fluidData.IsValid() &&
                   m_fluidData.NeedsSimulationUpdate();
        }

        private void UpdateFluidSimulation()
        {
            // Update timing
            m_fluidData.UpdateSimulationTiming(Time.deltaTime);

            // Handle boundary updates (less frequent than simulation)
            if ((m_flags & ChunkFlags.FluidBoundaryUpdateRequired) == ChunkFlags.FluidBoundaryUpdateRequired ||
                Time.time - m_lastBoundaryUpdateTime > BOUNDARY_UPDATE_INTERVAL)
            {
                UpdateFluidBoundaries();
            }

            // Run simulation steps as needed
            while (m_fluidData.ShouldRunSimulation(m_fluidTimeStep) &&
                   m_simulationStepsThisFrame < m_maxSimulationStepsPerFrame)
            {
                RunFluidSimulationStep();
                m_fluidData.ConsumeSimulationTime(m_fluidTimeStep);
                m_simulationStepsThisFrame++;
            }

            // Update volumetric rendering registration
            UpdateVolumetricRenderingRegistration();

            // Performance tracking
            m_lastFluidUpdateTime = Time.time;
        }

        private void UpdateFluidBoundaries()
        {
            m_flags &= ~ChunkFlags.FluidBoundaryUpdateRequired;
            m_lastBoundaryUpdateTime = Time.time;

            if (WorldManager.FluidSimulation != null && WorldManager.FluidSimulation.IsSimulationEnabled)
            {
                WorldManager.FluidSimulation.UpdateBoundariesFromSolids(m_fluidData);
            }
        }

        private void RunFluidSimulationStep()
        {
            if (WorldManager.FluidSimulation == null || !WorldManager.FluidSimulation.IsSimulationEnabled)
                return;

            // Get current fluid source (if any)
            FluidSourceData activeSource = null;
            if (m_fluidData.HasFluidSource && m_fluidData.FluidSource.ShouldBeActive())
            {
                activeSource = m_fluidData.FluidSource;
            }
            else if (m_fluidData.FluidSource != null && !m_fluidData.FluidSource.ShouldBeActive())
            {
                // Remove expired source
                m_fluidData.RemoveFluidSource();
            }

            // Run complete simulation step
            WorldManager.FluidSimulation.SimulateChunkFluidStep(m_fluidData);

            // TODO: Count active fluid voxels and update ChunkFluidData
            // This would require an additional compute shader pass or readback
            // For now, we'll estimate based on whether we have a source
            int estimatedActiveVoxels = activeSource != null ? 100 : 0;
            m_fluidData.UpdateActiveFluidVoxelCount(estimatedActiveVoxels);
        }

        private void UpdateVolumetricRenderingRegistration()
        {
            bool shouldBeRegistered = m_enableVolumetricRendering &&
                                      m_fluidData.HasVisibleFluid() &&
                                      m_fluidData.IsValid();

            if (shouldBeRegistered && !m_registeredForVolumetricRendering)
            {
                RegisterForVolumetricRendering();
            }
            else if (!shouldBeRegistered && m_registeredForVolumetricRendering)
            {
                UnregisterFromVolumetricRendering();
            }
        }

        private void OnDestroy()
        {
            WorldManager.VoxelConfig.MaterialConfig.OnDirtied -= OnMaterialConfigChanged;
            ReleaseBuffers();
            CleanupFluidData();
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.green;
            Gizmos.DrawWireCube(transform.position, WorldManager.VoxelConfig.VoxelVolumeConfig.VoxelVolumeDimensions);

            // Draw fluid debug info
            if (m_enableFluidSimulation && m_fluidData != null && m_fluidData.HasFluidSource)
            {
                var source = m_fluidData.FluidSource;

                // Draw source sphere
                Gizmos.color = Color.cyan;
                Gizmos.DrawWireSphere(source.Position, source.Radius);

                // Draw velocity vector
                Gizmos.color = Color.yellow;
                Gizmos.DrawRay(source.Position, source.Velocity);

                // Draw remaining time indicator
                if (source.Duration > 0)
                {
                    float remainingRatio = source.GetRemainingTime() / source.Duration;
                    Gizmos.color = Color.Lerp(Color.red, Color.green, remainingRatio);
                    Gizmos.DrawWireCube(source.Position + Vector3.up * 2f, Vector3.one * remainingRatio);
                }
            }
        }

        #region IPoolable Implementation

        public void OnAcquire()
        {
            m_currentLOD = m_targetLOD = m_vertexCount = m_triangleCount = -1;
            CreateBuffers();
            InitializeFluidData();
            gameObject.SetActive(true);
        }

        public void OnRelease()
        {
            m_meshFilter.sharedMesh = null;
            m_meshCollider.sharedMesh = null;
            m_request?.Cancel();
            m_request = null;
            m_voxelVolumeCSGOperations.Clear();
            m_flags = 0;

            CleanupFluidData();
            gameObject.SetActive(false);
        }

        #endregion

        #region Fluid Management

        private void InitializeFluidData()
        {
            if (!m_enableFluidSimulation)
                return;

            CleanupFluidData(); // Ensure clean state

            var dimensions = WorldManager.VoxelConfig.VoxelVolumeConfig.NumberOfVoxels;
            m_fluidData = new ChunkFluidData();
            m_fluidData.Initialize(dimensions, transform.position, m_voxelVolumeBuffer);
            ;

            // Initialize fluid textures through simulation system
            if (WorldManager.FluidSimulation != null && WorldManager.FluidSimulation.IsSimulationEnabled)
            {
                WorldManager.FluidSimulation.InitializeChunkFluidTextures(m_fluidData);
            }

            // Debug.Log($"Initialized fluid data for chunk at {transform.position}");
        }

        private void CleanupFluidData()
        {
            if (m_registeredForVolumetricRendering)
            {
                UnregisterFromVolumetricRendering();
            }

            m_fluidData?.Cleanup();
            m_fluidData = null;
        }

        public void AddFluidSource(Vector3 worldPosition, Vector3 velocity, float radius, float amount,
            MaterialIndex material = MaterialIndex.Water, float duration = -1f)
        {
            if (!m_enableFluidSimulation || m_fluidData == null || !m_fluidData.IsValid())
            {
                Debug.LogWarning(
                    $"Cannot add fluid source to chunk at {transform.position}: fluid simulation not available");
                return;
            }

            var sourceData = new FluidSourceData(worldPosition, velocity, radius, amount, material, duration);
            m_fluidData.FluidSource = sourceData;

            // Immediately add the source to simulation
            if (WorldManager.FluidSimulation && WorldManager.FluidSimulation.IsSimulationEnabled)
            {
                WorldManager.FluidSimulation.AddFluidSource(m_fluidData);
            }

            Debug.Log($"Added fluid source to chunk at {transform.position}: {sourceData}");
        }

        public void RemoveFluidSource()
        {
            if (!m_enableFluidSimulation || m_fluidData == null)
                return;

            m_fluidData.RemoveFluidSource();
            Debug.Log($"Removed fluid source from chunk at {transform.position}");
        }

        public bool HasActiveFluid()
        {
            return m_enableFluidSimulation &&
                   m_fluidData != null &&
                   m_fluidData.IsValid() &&
                   m_fluidData.HasVisibleFluid();
        }

        public float GetFluidDensityAtPosition(Vector3 worldPosition)
        {
            if (!m_enableFluidSimulation || m_fluidData == null || !m_fluidData.IsValid())
                return 0f;

            return m_fluidData.GetFluidDensityAtPosition(worldPosition);
        }

        #endregion

        #region Volumetric Rendering

        private void RegisterForVolumetricRendering()
        {
            if (!m_enableVolumetricRendering || m_fluidData == null || !m_fluidData.IsValid())
                return;

            var volumeSize = WorldManager.VoxelConfig.VoxelVolumeConfig.VoxelVolumeDimensions;
            m_fluidData.RegisterForVolumetricRendering(m_chunkCoordinate, volumeSize);
            m_registeredForVolumetricRendering = true;

            Debug.Log($"Registered chunk {m_chunkCoordinate} for volumetric fluid rendering");
        }

        private void UnregisterFromVolumetricRendering()
        {
            if (m_registeredForVolumetricRendering)
            {
                m_fluidData?.UnregisterFromVolumetricRendering(m_chunkCoordinate);
                m_registeredForVolumetricRendering = false;

                Debug.Log($"Unregistered chunk {m_chunkCoordinate} from volumetric fluid rendering");
            }
        }

        #endregion

        #region Buffer Management

        private void CreateBuffers()
        {
            // Create solid voxel volume buffer
            if (m_voxelVolumeBuffer?.count != WorldManager.VoxelConfig.VoxelVolumeConfig.VoxelCount)
            {
                m_voxelVolumeBuffer?.Release();
                m_voxelVolumeBuffer = new ComputeBuffer(WorldManager.VoxelConfig.VoxelVolumeConfig.VoxelCount,
                    2 * sizeof(uint));
            }
        }

        private void ReleaseBuffers()
        {
            // Release solid voxel buffer
            if (m_voxelVolumeBuffer != null)
            {
                m_voxelVolumeBuffer.Release();
                m_voxelVolumeBuffer = null;
            }
        }

        #endregion

        #region Existing Chunk Functionality (unchanged)

        public void RegenerateVoxelVolume() => m_flags |= ChunkFlags.VoxelVolumeRegenerationRequested;

        public void RegenerateMesh(int lod = -1)
        {
            if (lod != -1 && lod != m_targetLOD)
            {
                m_targetLOD = lod;
                m_flags |= ChunkFlags.MeshRegenerationRequested;
            }
            else if (lod == -1)
            {
                m_flags |= ChunkFlags.MeshRegenerationRequested;
            }
        }

        public void ApplyCSGPrimitiveOperation(GPUCSGOperator csgOperator, GPUCSGPrimitive csgPrimitive,
            MaterialIndex materialIndex, Matrix4x4 worldToObjectMatrix)
        {
            GPUVoxelVolumeCSGOperation operation =
                new GPUVoxelVolumeCSGOperation(csgOperator, csgPrimitive, materialIndex, worldToObjectMatrix);

            m_voxelVolumeCSGOperations.Add(operation);

            if (!WorldManager.Instance.ChunkHasModifications(m_chunkCoordinate))
            {
                WorldManager.Instance.InitializeChunkModifications(m_chunkCoordinate);
            }

            WorldManager.Instance.AddChunkModification(m_chunkCoordinate, operation);
            m_flags |= ChunkFlags.CSGOperationPerformed | ChunkFlags.MeshRegenerationRequested;
        }

        public bool GetMaterialFromRaycastHit(RaycastHit hit, out MaterialIndex materialIndex)
        {
            materialIndex = default;

            if (hit.triangleIndex >= m_triangleCount)
            {
                return false;
            }

            using Mesh.MeshDataArray meshDataArray = Mesh.AcquireReadOnlyMeshData(m_mesh);
            {
                Mesh.MeshData meshData = meshDataArray[0];
                NativeArray<int> triangles = meshData.GetIndexData<int>();
                NativeArray<GPUVertex> vertices = meshData.GetVertexData<GPUVertex>();

                float shortestDistanceSquared = float.MaxValue;

                for (int index = 0; index < 3; index++)
                {
                    GPUVertex vertex = vertices[triangles[3 * hit.triangleIndex + index]];
                    float distanceSquared = math.lengthsq(hit.transform.TransformPoint(vertex.Position) - hit.point);

                    if (distanceSquared < shortestDistanceSquared)
                    {
                        shortestDistanceSquared = distanceSquared;
                        materialIndex = vertex.MaterialIndex;
                    }
                }
            }

            return true;
        }

        public void ApplyStoredModifications(int3 chunkCoordinate)
        {
            var storedModifications = WorldManager.Instance.GetChunkModifications(chunkCoordinate);
            if (storedModifications != null && storedModifications.Count > 0)
            {
                foreach (var operation in storedModifications)
                {
                    m_voxelVolumeCSGOperations.Add(operation);
                }

                m_flags |= ChunkFlags.CSGOperationPerformed | ChunkFlags.MeshRegenerationRequested;
            }
        }

        public void SetCoordinate(int3 chunkCoordinate)
        {
            m_chunkCoordinate = chunkCoordinate;
        }

        private void OnMeshGenerated(NativeArray<GPUVertex> vertices, int vertexCount, int vertexStartIndex,
            NativeArray<int> triangles, int triangleCount, int triangleStartIndex)
        {
            m_request = null;
            m_currentLOD = m_targetLOD;
            m_vertexCount = vertexCount;
            m_triangleCount = triangleCount;

            if (vertexCount == 0 || triangleCount == 0)
            {
                m_meshFilter.sharedMesh = null;
                m_meshCollider.sharedMesh = null;
                return;
            }

            m_mesh.SetVertexBufferParams(vertexCount, GPUVertex.Attributes);
            m_mesh.SetIndexBufferParams(triangleCount, IndexFormat.UInt32);

#if !UNITY_EDITOR
            MeshUpdateFlags flags = MeshUpdateFlags.DontNotifyMeshUsers | MeshUpdateFlags.DontRecalculateBounds | 
                                  MeshUpdateFlags.DontResetBoneBounds | MeshUpdateFlags.DontValidateIndices;
            m_mesh.SetVertexBufferData(vertices, vertexStartIndex, 0, vertexCount, 0, flags);
            m_mesh.SetIndexBufferData(triangles, triangleStartIndex, 0, triangleCount, flags);
            m_mesh.SetSubMesh(0, new SubMeshDescriptor(0, triangleCount), flags);
            m_mesh.RecalculateBounds(flags);
#else
            m_mesh.SetVertexBufferData(vertices, vertexStartIndex, 0, vertexCount);
            m_mesh.SetIndexBufferData(triangles, triangleStartIndex, 0, triangleCount);
            m_mesh.SetSubMesh(0, new SubMeshDescriptor(0, triangleCount));
            m_mesh.RecalculateBounds(MeshUpdateFlags.DontValidateIndices);
#endif

            m_meshFilter.sharedMesh = null;
            m_meshFilter.sharedMesh = m_mesh;

            m_bakeJobHandle = new BakeJob(m_mesh.GetInstanceID()).Schedule();
            m_flags |= ChunkFlags.IsBakingMesh;
        }

        private void InitializeMeshComponents()
        {
            m_mesh = new Mesh();
            m_mesh.MarkDynamic();
            m_meshFilter = GetComponent<MeshFilter>();
            m_meshRenderer = GetComponent<MeshRenderer>();
            m_meshCollider = GetComponent<MeshCollider>();
            m_onMeshGeneratedDelegate = OnMeshGenerated;
        }

        private void OnMaterialConfigChanged()
        {
            ApplyRenderMaterial();
        }

        private void ApplyRenderMaterial() =>
            m_meshRenderer.material = WorldManager.VoxelConfig.MaterialConfig.RenderMaterial;

        #endregion

        #region Debug Methods

        public void RunStep()
        {
            if (m_enableFluidSimulation && m_fluidData != null && m_fluidData.IsValid())
            {
                RunFluidSimulationStep();
                Debug.Log($"Manual fluid simulation step completed for chunk at {transform.position}");
            }
        }

        public void DebugFluidState()
        {
            if (m_fluidData != null)
            {
                m_fluidData.DebugOutput();
            }
            else
            {
                Debug.Log($"No fluid data for chunk at {transform.position}");
            }
        }

        public void RegenerateFluidMesh()
        {
            // Not needed for volumetric rendering, but kept for compatibility
            Debug.Log($"RegenerateFluidMesh called for chunk at {transform.position} - using volumetric rendering");
        }

        #endregion

        [Flags]
        private enum ChunkFlags
        {
            VoxelVolumeRegenerationRequested = 1,
            CSGOperationPerformed = 2,
            MeshRegenerationRequested = 4,
            IsBakingMesh = 8,
            FluidBoundaryUpdateRequired = 16
        }
    }
}