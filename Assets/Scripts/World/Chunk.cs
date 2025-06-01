using System;
using System.Collections.Generic;
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
        
        private ComputeBuffer m_fluidVoxelVolumeBuffer;
        private OnMeshGenerated m_onFluidMeshGeneratedDelegate;
        private JobHandle m_fluidBakeJobHandle;
        private bool m_hasFluidVolumeBuffer = false;

        private MeshFilter m_fluidMeshFilter;
        private MeshRenderer m_fluidMeshRenderer;
        private Mesh m_fluidMesh;
        private IRequest m_fluidMeshRequest;
        private int m_fluidVertexCount;
        private int m_fluidTriangleCount;

        public ComputeBuffer VoxelVolumeBuffer => m_voxelVolumeBuffer;

        private void Awake()
        {
            WorldManager.VoxelConfig.MaterialConfig.OnDirtied += OnMaterialConfigChanged;
            InitializeMeshComponents();
            ApplyRenderMaterial();
            m_voxelVolumeCSGOperations = new List<GPUVoxelVolumeCSGOperation>();

            // Initialize fluid mesh components if fluid rendering is enabled
            if (m_enableFluidRendering)
            {
                InitializeFluidMeshComponents();
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

            if (m_enableFluidRendering && 
                (m_flags & ChunkFlags.FluidMeshRegenerationRequested) == ChunkFlags.FluidMeshRegenerationRequested && 
                (m_flags & ChunkFlags.IsBakingFluidMesh) != ChunkFlags.IsBakingFluidMesh && 
                m_fluidMeshRequest == null &&
                m_hasFluidVolumeBuffer)
            {
                m_flags &= ~ChunkFlags.FluidMeshRegenerationRequested;
                m_fluidMeshRequest = GenerateFluidMeshAsync();
            }

            // Handle fluid mesh baking completion
            if ((m_flags & ChunkFlags.IsBakingFluidMesh) == ChunkFlags.IsBakingFluidMesh && m_fluidBakeJobHandle.IsCompleted)
            {
                m_flags &= ~ChunkFlags.IsBakingFluidMesh;
                // Note: Fluid meshes typically don't need colliders, but if needed:
                // ApplyFluidMeshCollider();
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
            if (m_enableFluidRendering)
            {
                m_fluidVertexCount = m_fluidTriangleCount = 0;
                m_hasFluidVolumeBuffer = false;
                m_fluidVoxelVolumeBuffer = null;
                
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
            // Clear fluid mesh
            if (m_enableFluidRendering)
            {
                ClearFluidMesh();
                m_fluidMeshRequest?.Cancel();
                m_fluidMeshRequest = null;
                m_hasFluidVolumeBuffer = false;
                m_fluidVoxelVolumeBuffer = null;
                
                // Clean up fluid mesh object
                if (m_fluidMeshObject != null)
                {
                    m_fluidMeshObject.SetActive(false);
                }
            }
        }

        private IRequest GenerateFluidMeshAsync()
        {
            if (!m_hasFluidVolumeBuffer || m_fluidVoxelVolumeBuffer == null)
            {
                Debug.LogWarning($"Chunk {name}: Cannot generate fluid mesh - no fluid volume buffer available");
                return null;
            }

            // Use the same LOD as the solid mesh for consistency
            int fluidLOD = m_targetLOD;
            
            // Request fluid mesh generation using DualContouring
            // We use the current vertex/triangle counts as estimates, or default values for first generation
            int estimatedVertexCount = m_fluidVertexCount > 0 ? m_fluidVertexCount : 1000;
            int estimatedTriangleCount = m_fluidTriangleCount > 0 ? m_fluidTriangleCount : 3000;

            return WorldManager.DualContouring.RequestMeshAsync(
                m_fluidVoxelVolumeBuffer,
                -1, // currentLOD (-1 indicates new/unknown)
                fluidLOD, // targetLOD
                estimatedVertexCount,
                estimatedTriangleCount,
                transform.position,
                m_onFluidMeshGeneratedDelegate
            );
        }
        public void RegenerateFluidMesh(ComputeBuffer fluidVoxelVolumeBuffer = null)
        {
            if (!m_enableFluidRendering)
                return;

            if (fluidVoxelVolumeBuffer != null)
            {
                // Store reference to the fluid voxel volume buffer
                m_fluidVoxelVolumeBuffer = fluidVoxelVolumeBuffer;
                m_hasFluidVolumeBuffer = true;
                
                // Request fluid mesh generation
                m_flags |= ChunkFlags.FluidMeshRegenerationRequested;
            }
            else
            {
                // Clear fluid mesh if no fluid data provided
                ClearFluidMesh();
                m_hasFluidVolumeBuffer = false;
                m_fluidVoxelVolumeBuffer = null;
            }
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
            
            // Apply fluid material from VoxelConfig
            ApplyFluidRenderMaterial();
        }

        private void ApplyFluidRenderMaterial()
        {
            if (m_fluidMeshRenderer != null && WorldManager.VoxelConfig?.MaterialConfig?.FluidRenderMaterial != null)
            {
                m_fluidMeshRenderer.material = WorldManager.VoxelConfig.MaterialConfig.FluidRenderMaterial;
            }
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

        private void CreateBuffers()
        {
            if (m_voxelVolumeBuffer?.count != WorldManager.VoxelConfig.VoxelVolumeConfig.VoxelCount)
            {
                m_voxelVolumeBuffer?.Release();
                m_voxelVolumeBuffer = new ComputeBuffer(WorldManager.VoxelConfig.VoxelVolumeConfig.VoxelCount,
                    2 * sizeof(uint));
            }
        }

        private void ReleaseBuffers()
        {
            if (m_voxelVolumeBuffer != null)
            {
                m_voxelVolumeBuffer.Release();
                m_voxelVolumeBuffer = null;
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
        
        private void OnFluidMeshGenerated(NativeArray<GPUVertex> vertices, int vertexCount, int vertexStartIndex, 
                                        NativeArray<int> triangles, int triangleCount, int triangleStartIndex)
        {
            m_fluidMeshRequest = null;
            m_fluidVertexCount = vertexCount;
            m_fluidTriangleCount = triangleCount;

            if (vertexCount == 0 || triangleCount == 0)
            {
                // No fluid surface to render
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

            Debug.Log("Fluid mesh set");

            // Start baking job for physics if needed (typically fluids don't need colliders)
            // m_fluidBakeJobHandle = new BakeJob(m_fluidMesh.GetInstanceID()).Schedule();
            // m_flags |= ChunkFlags.IsBakingFluidMesh;
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
        
        public bool HasActiveFluid()
        {
            return m_enableFluidRendering && m_hasFluidVolumeBuffer && m_fluidVertexCount > 0;
        }

        // Method to manually trigger fluid mesh regeneration (useful for debugging)
        public void ForceRegenerateFluidMesh()
        {
            if (m_enableFluidRendering && m_hasFluidVolumeBuffer)
            {
                m_flags |= ChunkFlags.FluidMeshRegenerationRequested;
            }
        }

        [Flags]
        private enum ChunkFlags
        {
            VoxelVolumeRegenerationRequested = 1,
            CSGOperationPerformed = 2,
            MeshRegenerationRequested = 4,
            IsBakingMesh = 8,
            FluidMeshRegenerationRequested = 16, // New flag
            IsBakingFluidMesh = 32 // New flag
        }
    }
}