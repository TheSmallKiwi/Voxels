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

        [Header("Fluid Rendering")] 
        [SerializeField] private bool m_enableFluidRendering = true;
        [SerializeField] private bool m_useVolumetricRendering = true;

        // Fluid simulation data (texture-based)
        private ChunkFluidTextures m_fluidTextures;
        private FluidSourceData m_fluidSource;
        private bool m_hasFluidSource = false;
        private float m_fluidAccumulatedTime = 0f;
        private float m_fluidTimeStep = 0.016f;
        
        // Volumetric rendering registration
        private bool m_registeredForVolumetricRendering = false;

        public ComputeBuffer VoxelVolumeBuffer => m_voxelVolumeBuffer;

        private void Awake()
        {
            WorldManager.VoxelConfig.MaterialConfig.OnDirtied += OnMaterialConfigChanged;
            InitializeMeshComponents();
            ApplyRenderMaterial();
            m_voxelVolumeCSGOperations = new List<GPUVoxelVolumeCSGOperation>();
        }

        private void Update()
        {
            if (m_flags == 0)
            {
                return;
            }

            // Handle solid voxel volume regeneration
            if ((m_flags & ChunkFlags.VoxelVolumeRegenerationRequested) == ChunkFlags.VoxelVolumeRegenerationRequested)
            {
                m_flags &= ~ChunkFlags.VoxelVolumeRegenerationRequested;
                WorldManager.VoxelVolume.GenerateVoxelVolume(m_voxelVolumeBuffer, transform.position);
                
                // Update fluid boundaries when voxel volume changes
                if (m_enableFluidRendering && m_fluidTextures != null && m_fluidTextures.IsValid())
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
                
                // Update fluid boundaries after CSG operations
                if (m_enableFluidRendering && m_fluidTextures != null && m_fluidTextures.IsValid())
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

            // Handle fluid simulation and volumetric rendering
            if (m_enableFluidRendering && m_fluidTextures != null && m_fluidTextures.IsValid())
            {
                HandleFluidBoundaryUpdates();
                UpdateFluidSimulation();
                UpdateVolumetricRendering();
            }
        }

        private void OnDestroy()
        {
            WorldManager.VoxelConfig.MaterialConfig.OnDirtied -= OnMaterialConfigChanged;
            ReleaseBuffers();
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.green;
            Gizmos.DrawWireCube(transform.position, WorldManager.VoxelConfig.VoxelVolumeConfig.VoxelVolumeDimensions);
        }

        public void OnAcquire()
        {
            m_currentLOD = m_targetLOD = m_vertexCount = m_triangleCount = -1;
            CreateBuffers();
            gameObject.SetActive(true);

            // Reset and initialize fluid state
            if (m_enableFluidRendering)
            {
                ResetFluidState();
                CreateFluidTextures();
                RegisterForVolumetricRendering();
            }
        }

        public void OnRelease()
        {
            m_meshFilter.sharedMesh = null;
            m_meshCollider.sharedMesh = null;
            m_request?.Cancel();
            m_request = null;
            m_voxelVolumeCSGOperations.Clear();
            m_flags = 0;
            gameObject.SetActive(false);

            // Clear fluid state
            if (m_enableFluidRendering)
            {
                UnregisterFromVolumetricRendering();
                ResetFluidState();
            }
        }

        private void HandleFluidBoundaryUpdates()
        {
            if ((m_flags & ChunkFlags.FluidBoundaryUpdateRequired) == ChunkFlags.FluidBoundaryUpdateRequired)
            {
                m_flags &= ~ChunkFlags.FluidBoundaryUpdateRequired;

                if (WorldManager.FluidSimulation != null && WorldManager.FluidSimulation.IsSimulationEnabled)
                {
                    WorldManager.FluidSimulation.UpdateBoundariesFromSolids(
                        m_fluidTextures, 
                        m_voxelVolumeBuffer
                    );
                }
            }
        }

        private void UpdateFluidSimulation()
        {
            if (WorldManager.FluidSimulation == null || !WorldManager.FluidSimulation.IsSimulationEnabled)
                return;

            m_fluidAccumulatedTime += Time.deltaTime;

            // Fixed timestep fluid simulation
            while (m_fluidAccumulatedTime >= m_fluidTimeStep)
            {
                // Run fluid simulation step
                WorldManager.FluidSimulation.SimulateChunkFluidStep(
                    m_fluidTextures,
                    m_voxelVolumeBuffer,
                    transform.position,
                    m_hasFluidSource ? m_fluidSource : null
                );

                m_fluidAccumulatedTime -= m_fluidTimeStep;

                // Update volumetric rendering registration
                UpdateVolumetricRendering();
            }
        }

        private void UpdateVolumetricRendering()
        {
            if (!m_useVolumetricRendering || !m_registeredForVolumetricRendering)
                return;

            // Volumetric renderer automatically uses the latest texture data
            // No explicit update needed - textures are bound by reference
        }

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

        private void CreateFluidTextures()
        {
            if (!m_enableFluidRendering)
                return;

            // Release existing textures
            ReleaseFluidTextures();

            // Create new fluid textures
            var dimensions = WorldManager.VoxelConfig.VoxelVolumeConfig.NumberOfVoxels;
            m_fluidTextures = FluidTextureFactory.CreateFluidTextures(dimensions);

            // Initialize fluid textures
            if (WorldManager.FluidSimulation != null)
            {
                WorldManager.FluidSimulation.InitializeChunkFluidTextures(m_fluidTextures, transform.position);
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

            // Release fluid textures
            ReleaseFluidTextures();
        }

        private void ReleaseFluidTextures()
        {
            m_fluidTextures?.Release();
            m_fluidTextures = null;
        }

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

            // Add to local list for immediate processing
            m_voxelVolumeCSGOperations.Add(operation);

            // Store in WorldManager's dictionary for persistence
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

        public void ApplyStoredModifications(int3 chunkCoordinate)
        {
            var storedModifications = WorldManager.Instance.GetChunkModifications(chunkCoordinate);
            if (storedModifications != null && storedModifications.Count > 0)
            {
                // Apply all stored modifications at once
                foreach (var operation in storedModifications)
                {
                    m_voxelVolumeCSGOperations.Add(operation);
                }

                m_flags |= ChunkFlags.CSGOperationPerformed | ChunkFlags.MeshRegenerationRequested;
            }
        }

        private void OnMaterialConfigChanged()
        {
            ApplyRenderMaterial();
        }

        public void SetCoordinate(int3 chunkCoordinate)
        {
            m_chunkCoordinate = chunkCoordinate;
        }

        private void ApplyRenderMaterial() =>
            m_meshRenderer.material = WorldManager.VoxelConfig.MaterialConfig.RenderMaterial;

        public void AddFluidSource(Vector3 worldPosition, Vector3 velocity, float radius, float amount,
            MaterialIndex material = MaterialIndex.Water)
        {
            if (!m_enableFluidRendering || m_fluidTextures == null || !m_fluidTextures.IsValid())
                return;

            m_hasFluidSource = true;
            m_fluidSource = new FluidSourceData(worldPosition, velocity, radius, amount, material);

            // Immediately add the fluid source via simulation manager
            if (WorldManager.FluidSimulation != null)
            {
                WorldManager.FluidSimulation.AddFluidSource(m_fluidTextures, m_fluidSource);
            }
        }

        public void RemoveFluidSource()
        {
            if (!m_enableFluidRendering)
                return;

            m_hasFluidSource = false;
            m_fluidSource = null;
        }

        public void RegenerateFluidMesh()
        {
            // Not needed for volumetric rendering
        }

        private void RegisterForVolumetricRendering()
        {
            if (!m_useVolumetricRendering || m_fluidTextures == null || !m_fluidTextures.IsValid())
                return;

            var volumeSize = WorldManager.VoxelConfig.VoxelVolumeConfig.VoxelVolumeDimensions;
            m_fluidTextures.RegisterForVolumetricRendering(m_chunkCoordinate, transform.position, volumeSize);
            m_registeredForVolumetricRendering = true;
        }

        private void UnregisterFromVolumetricRendering()
        {
            if (m_registeredForVolumetricRendering)
            {
                VolumetricFluidExtensions.UnregisterFromVolumetricRendering(m_chunkCoordinate);
                m_registeredForVolumetricRendering = false;
            }
        }

        private void ResetFluidState()
        {
            m_hasFluidSource = false;
            m_fluidSource = null;
            m_fluidAccumulatedTime = 0f;
        }

        public bool HasActiveFluid()
        {
            return m_enableFluidRendering && m_fluidTextures != null && m_fluidTextures.IsValid() &&
                   m_hasFluidSource;
        }

        [Flags]
        private enum ChunkFlags
        {
            VoxelVolumeRegenerationRequested = 1,
            CSGOperationPerformed = 2,
            MeshRegenerationRequested = 4,
            IsBakingMesh = 8,
            FluidBoundaryUpdateRequired = 16
        }

        public void RunStep()
        {
            if (m_enableFluidRendering && m_fluidTextures != null && m_fluidTextures.IsValid())
            {
                WorldManager.FluidSimulation.SimulateChunkFluidStep(
                    m_fluidTextures,
                    m_voxelVolumeBuffer,
                    transform.position,
                    m_hasFluidSource ? m_fluidSource : null
                );
            }
        }
    }
}