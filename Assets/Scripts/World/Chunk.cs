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

        [Header("Fluid Rendering")] [SerializeField]
        private GameObject m_fluidMeshObject;

        [SerializeField] private bool m_enableFluidRendering = true;

        // Fluid simulation data
        private ChunkFluidData m_fluidData;
        private FluidSourceData m_fluidSource;
        private bool m_hasFluidSource = false;
        private float m_fluidAccumulatedTime = 0f;
        private float m_fluidTimeStep = 0.016f; // ~60 FPS for fluid simulation

        // Fluid mesh components
        private MeshFilter m_fluidMeshFilter;
        private MeshRenderer m_fluidMeshRenderer;
        private Mesh m_fluidMesh;
        private OnMeshGenerated m_onFluidMeshGeneratedDelegate;
        private IRequest m_fluidMeshRequest;
        private JobHandle m_fluidBakeJobHandle;
        private int m_fluidVertexCount;
        private int m_fluidTriangleCount;

        public ComputeBuffer VoxelVolumeBuffer => m_voxelVolumeBuffer;
        public ChunkFluidData FluidData => m_fluidData;

        private void Awake()
        {
            WorldManager.VoxelConfig.MaterialConfig.OnDirtied += OnMaterialConfigChanged;
            InitializeMeshComponents();
            ApplyRenderMaterial();
            m_voxelVolumeCSGOperations = new List<GPUVoxelVolumeCSGOperation>();

            // Initialize fluid components if enabled
            if (m_enableFluidRendering && WorldManager.VoxelConfig.FluidSimulationConfig != null)
            {
                InitializeFluidComponents();
                m_onFluidMeshGeneratedDelegate = OnFluidMeshGenerated;
            }
        }

        private void Update()
        {
            if (m_flags == 0)
            {
                return;
            }

            if ((m_flags & ChunkFlags.VoxelVolumeRegenerationRequested) == ChunkFlags.VoxelVolumeRegenerationRequested)
            {
                m_flags &= ~ChunkFlags.VoxelVolumeRegenerationRequested;
                WorldManager.VoxelVolume.GenerateVoxelVolume(m_voxelVolumeBuffer, transform.position);
            }

            if ((m_flags & ChunkFlags.CSGOperationPerformed) == ChunkFlags.CSGOperationPerformed)
            {
                m_flags &= ~ChunkFlags.CSGOperationPerformed;
                WorldManager.VoxelVolume.ApplyVoxelVolumeCSGOperations(m_voxelVolumeBuffer, transform.position,
                    m_voxelVolumeCSGOperations);
                m_voxelVolumeCSGOperations.Clear();
            }

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

            if ((m_flags & ChunkFlags.IsBakingMesh) == ChunkFlags.IsBakingMesh && m_bakeJobHandle.IsCompleted)
            {
                m_flags &= ~ChunkFlags.IsBakingMesh;
                m_meshCollider.sharedMesh = null;
                m_meshCollider.sharedMesh = m_mesh;
            }

            if (m_enableFluidRendering && m_fluidData != null && m_fluidData.IsValid())
            {
                UpdateFluidSimulation();
                HandleFluidMeshGeneration();
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

            // Reset fluid state
            if (m_enableFluidRendering && m_fluidData != null)
            {
                m_hasFluidSource = false;
                m_fluidSource = null;
                m_fluidData.HasFluidSource = false;
                m_fluidData.FluidSource = null;
                m_fluidVertexCount = 0;
                m_fluidTriangleCount = 0;

                CreateFluidBuffers();

                if (m_fluidMeshObject != null)
                {
                    m_fluidMeshObject.SetActive(true);
                }
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
                ClearFluidMesh();
                m_fluidMeshRequest?.Cancel();
                m_fluidMeshRequest = null;
                m_hasFluidSource = false;

                if (m_fluidMeshObject != null)
                {
                    m_fluidMeshObject.SetActive(false);
                }
            }
        }

        private void InitializeFluidComponents()
        {
            // Create fluid data container
            m_fluidData = new ChunkFluidData
            {
                WorldPosition = transform.position,
                HasFluidSource = false,
                FluidSource = null
            };

            // Initialize fluid mesh components
            InitializeFluidMeshComponents();
        }

        private void InitializeFluidMeshComponents()
        {
            if (m_fluidMeshObject == null)
            {
                m_fluidMeshObject = new GameObject("FluidMesh");
                m_fluidMeshObject.transform.SetParent(transform);
                m_fluidMeshObject.transform.localPosition = Vector3.zero;
                m_fluidMeshObject.transform.localRotation = Quaternion.identity;
                m_fluidMeshObject.transform.localScale = Vector3.one;

                m_fluidMeshObject.AddComponent<MeshFilter>();
                m_fluidMeshObject.AddComponent<MeshRenderer>();
            }

            m_fluidMesh = new Mesh();
            m_fluidMesh.MarkDynamic();
            m_fluidMesh.name = $"FluidMesh_{name}";

            m_fluidMeshFilter = m_fluidMeshObject.GetComponent<MeshFilter>();
            m_fluidMeshRenderer = m_fluidMeshObject.GetComponent<MeshRenderer>();

            ApplyFluidRenderMaterial();
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

        private void UpdateFluidSimulation()
        {
            if (WorldManager.FluidSimulation == null || !WorldManager.FluidSimulation.IsSimulationEnabled)
                return;

            m_fluidAccumulatedTime += Time.deltaTime;

            // Fixed timestep fluid simulation
            while (m_fluidAccumulatedTime >= m_fluidTimeStep)
            {
                // Update fluid data with current position
                m_fluidData.WorldPosition = transform.position;
                m_fluidData.VoxelVolumeBuffer = m_voxelVolumeBuffer;
                m_fluidData.HasFluidSource = m_hasFluidSource;
                m_fluidData.FluidSource = m_fluidSource;
            
                // Run fluid simulation step
                WorldManager.FluidSimulation.SimulateChunkFluidStep(m_fluidData);
            
                m_fluidAccumulatedTime -= m_fluidTimeStep;
            }
        }

        private void HandleFluidMeshGeneration()
        {
            // Check if fluid mesh regeneration is needed/requested
            if ((m_flags & ChunkFlags.FluidMeshRegenerationRequested) == ChunkFlags.FluidMeshRegenerationRequested &&
                (m_flags & ChunkFlags.IsBakingFluidMesh) != ChunkFlags.IsBakingFluidMesh &&
                m_fluidMeshRequest == null)
            {
                m_flags &= ~ChunkFlags.FluidMeshRegenerationRequested;
                GenerateFluidMeshAsync();
            }

            // Handle fluid mesh baking completion
            if ((m_flags & ChunkFlags.IsBakingFluidMesh) == ChunkFlags.IsBakingFluidMesh &&
                m_fluidBakeJobHandle.IsCompleted)
            {
                m_flags &= ~ChunkFlags.IsBakingFluidMesh;
                // Fluid meshes typically don't need colliders
            }
        }

        private void CreateBuffers()
        {
            // Create solid voxel volume buffer (existing code)
            if (m_voxelVolumeBuffer?.count != WorldManager.VoxelConfig.VoxelVolumeConfig.VoxelCount)
            {
                m_voxelVolumeBuffer?.Release();
                m_voxelVolumeBuffer = new ComputeBuffer(WorldManager.VoxelConfig.VoxelVolumeConfig.VoxelCount,
                    2 * sizeof(uint));
            }

            // Create fluid buffers if fluid rendering is enabled
            if (m_enableFluidRendering && m_fluidData != null)
            {
                CreateFluidBuffers();
            }
        }

        private void CreateFluidBuffers()
        {
            int voxelCount = WorldManager.VoxelConfig.VoxelVolumeConfig.VoxelCount;
            int fluidVoxelSize = GetFluidVoxelSizeInBytes();

            // Release existing fluid buffers
            ReleaseFluidBuffers();

            // Create new fluid buffers
            m_fluidData.FluidVolumeBuffer = new ComputeBuffer(voxelCount, fluidVoxelSize);
            m_fluidData.FluidVolumeBackBuffer = new ComputeBuffer(voxelCount, fluidVoxelSize);
            m_fluidData.TempVoxelVolumeBuffer = new ComputeBuffer(voxelCount, GetFluidVoxelSizeInBytes()); // PackedVoxel size

            // Initialize fluid volume
            if (WorldManager.FluidSimulation != null)
            {
                WorldManager.FluidSimulation.InitializeChunkFluidVolume(
                    m_fluidData.FluidVolumeBuffer,
                    m_fluidData.FluidVolumeBackBuffer,
                    transform.position
                );
            }
        }

        private void ReleaseBuffers()
        {
            // Release solid voxel buffer (existing code)
            if (m_voxelVolumeBuffer != null)
            {
                m_voxelVolumeBuffer.Release();
                m_voxelVolumeBuffer = null;
            }

            // Release fluid buffers
            ReleaseFluidBuffers();
        }

        private void ReleaseFluidBuffers()
        {
            if (m_fluidData != null)
            {
                m_fluidData.FluidVolumeBuffer?.Release();
                m_fluidData.FluidVolumeBackBuffer?.Release();
                m_fluidData.TempVoxelVolumeBuffer?.Release();

                m_fluidData.FluidVolumeBuffer = null;
                m_fluidData.FluidVolumeBackBuffer = null;
                m_fluidData.TempVoxelVolumeBuffer = null;
            }
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
            MeshUpdateFlags flags =
 MeshUpdateFlags.DontNotifyMeshUsers | MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontResetBoneBounds | MeshUpdateFlags.DontValidateIndices;
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
            // Update both solid and fluid materials
            ApplyRenderMaterial(); // existing method for solid

            if (m_enableFluidRendering)
            {
                ApplyFluidRenderMaterial();
            }
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
            if (!m_enableFluidRendering || m_fluidData == null)
                return;

            m_hasFluidSource = true;
            m_fluidSource = new FluidSourceData(worldPosition, velocity, radius, amount, material);
            m_fluidData.HasFluidSource = true;
            m_fluidData.FluidSource = m_fluidSource;

            // Immediately add the fluid source
            if (WorldManager.FluidSimulation != null)
            {
                WorldManager.FluidSimulation.AddFluidSourceToChunk(m_fluidData, m_fluidSource);
            }

            // Request fluid mesh regeneration
            m_flags |= ChunkFlags.FluidMeshRegenerationRequested;
        }

        public void RemoveFluidSource()
        {
            if (!m_enableFluidRendering)
                return;

            m_hasFluidSource = false;
            m_fluidSource = null;

            if (m_fluidData != null)
            {
                m_fluidData.HasFluidSource = false;
                m_fluidData.FluidSource = null;
            }
        }


        public void RegenerateFluidMesh()
        {
            if (!m_enableFluidRendering || m_fluidData == null || !m_fluidData.IsValid())
                return;

            m_flags |= ChunkFlags.FluidMeshRegenerationRequested;
        }

        private void GenerateFluidMeshAsync()
        {
            if (!m_enableFluidRendering || m_fluidData == null || !m_fluidData.IsValid())
                return;

            // Convert fluid to voxel volume for mesh generation
            if (WorldManager.FluidSimulation != null)
            {
                WorldManager.FluidSimulation.ConvertChunkFluidToVoxelVolume(
                    m_fluidData.FluidVolumeBuffer,
                    m_fluidData.TempVoxelVolumeBuffer,
                    transform.position
                );
            }

            // Use the same LOD as solid mesh
            int fluidLOD = m_targetLOD;
            int estimatedVertexCount = m_fluidVertexCount > 0 ? m_fluidVertexCount : 1000;
            int estimatedTriangleCount = m_fluidTriangleCount > 0 ? m_fluidTriangleCount : 3000;

            // Request mesh generation using DualContouring
            m_fluidMeshRequest = WorldManager.DualContouring.RequestMeshAsync(
                m_fluidData.TempVoxelVolumeBuffer,
                -1, // currentLOD
                fluidLOD,
                estimatedVertexCount,
                estimatedTriangleCount,
                transform.position,
                m_onFluidMeshGeneratedDelegate
            );
        }

        private void OnFluidMeshGenerated(NativeArray<GPUVertex> vertices, int vertexCount, int vertexStartIndex,
            NativeArray<int> triangles, int triangleCount, int triangleStartIndex)
        {
            m_fluidMeshRequest = null;
            m_fluidVertexCount = vertexCount;
            m_fluidTriangleCount = triangleCount;

            if (vertexCount == 0 || triangleCount == 0)
            {
                ClearFluidMesh();
                return;
            }

            // Configure fluid mesh with generated data
            m_fluidMesh.SetVertexBufferParams(vertexCount, GPUVertex.Attributes);
            m_fluidMesh.SetIndexBufferParams(triangleCount, IndexFormat.UInt32);

#if !UNITY_EDITOR
            MeshUpdateFlags flags = MeshUpdateFlags.DontNotifyMeshUsers | MeshUpdateFlags.DontRecalculateBounds |
                                  MeshUpdateFlags.DontResetBoneBounds | MeshUpdateFlags.DontValidateIndices;
            m_fluidMesh.SetVertexBufferData(vertices, vertexStartIndex, 0, vertexCount, 0, flags);
            m_fluidMesh.SetIndexBufferData(triangles, triangleStartIndex, 0, triangleCount, flags);
            m_fluidMesh.SetSubMesh(0, new SubMeshDescriptor(0, triangleCount), flags);
            m_fluidMesh.RecalculateBounds(flags);
#else
            m_fluidMesh.SetVertexBufferData(vertices, vertexStartIndex, 0, vertexCount);
            m_fluidMesh.SetIndexBufferData(triangles, triangleStartIndex, 0, triangleCount);
            m_fluidMesh.SetSubMesh(0, new SubMeshDescriptor(0, triangleCount));
            m_fluidMesh.RecalculateBounds(MeshUpdateFlags.DontValidateIndices);
#endif

            // Apply the generated mesh
            m_fluidMeshFilter.sharedMesh = null;
            m_fluidMeshFilter.sharedMesh = m_fluidMesh;

            Debug.Log($"Fluid mesh generated: {vertexCount} vertices, {triangleCount} triangles");
        }

        private void ClearFluidMesh()
        {
            if (m_fluidMeshFilter != null)
            {
                m_fluidMeshFilter.sharedMesh = null;
            }

            m_fluidVertexCount = 0;
            m_fluidTriangleCount = 0;
        }

        private void ApplyFluidRenderMaterial()
        {
            if (m_fluidMeshRenderer != null && WorldManager.VoxelConfig?.MaterialConfig?.FluidRenderMaterial != null)
            {
                m_fluidMeshRenderer.material = WorldManager.VoxelConfig.MaterialConfig.FluidRenderMaterial;
            }
        }

        public bool HasActiveFluid()
        {
            return m_enableFluidRendering && m_fluidData != null && m_fluidData.IsValid() &&
                   (m_hasFluidSource || m_fluidVertexCount > 0);
        }

        private int GetFluidVoxelSizeInBytes()
        {
            // PackedFluidVoxel structure size: PackedVoxel (8 bytes) + 3 uints (12 bytes) = 20 bytes
            return 20;
        }

        // Update flags enum to include fluid flags
        [Flags]
        private enum ChunkFlags
        {
            VoxelVolumeRegenerationRequested = 1,
            CSGOperationPerformed = 2,
            MeshRegenerationRequested = 4,
            IsBakingMesh = 8,
            FluidMeshRegenerationRequested = 16,
            IsBakingFluidMesh = 32
        }

        public void RunStep()
        {
            WorldManager.FluidSimulation.StepSimulation(m_fluidData);
            m_flags |= ChunkFlags.FluidMeshRegenerationRequested;
        }
    }
}

