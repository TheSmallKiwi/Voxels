// Updated sections of WorldManager.cs for FluidSimulation integration

using System;
using System.Collections.Generic;
using Tuntenfisch.Fluids;
using Tuntenfisch.Generics;
using Tuntenfisch.Generics.Pool;
using Tuntenfisch.Voxels;
using Tuntenfisch.Voxels.CSG;
using Tuntenfisch.Voxels.DC;
using Tuntenfisch.Voxels.Materials;
using Tuntenfisch.Voxels.Volume;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Assertions;

namespace Tuntenfisch.World
{
    /// <summary>
    /// Manages voxel-related components and operations in the world, serving as a high-level interface for voxel manipulation and rendering.
    /// Now includes fluid simulation integration through a centralized FluidSimulation manager.
    /// </summary>
    [RequireComponent(typeof(VoxelConfig), typeof(VoxelVolume), typeof(DualContouring))]
    [RequireComponent(typeof(CSGUtility), typeof(FluidSimulation))]
    public class WorldManager : SingletonComponent<WorldManager>
    {
        public static VoxelConfig VoxelConfig => Instance.m_voxelConfig;
        public static VoxelVolume VoxelVolume => Instance.m_voxelVolume;
        public static DualContouring DualContouring => Instance.m_dualContouring;
        public static FluidSimulation FluidSimulation => Instance.m_fluidSimulation;

        private float ViewDistanceSquared => m_lodDistancesSquared[m_lodDistancesSquared.Length - 1];

        [Header("World Settings")] [SerializeField]
        private Transform m_viewer;

        [SerializeField] private float m_updateInterval = 20.0f;
        [SerializeField] private GameObject m_chunkPrefab;
        [SerializeField] private int m_initialChunkPoolPopulation = 0;
        [SerializeField] private float[] m_lodDistances;

        [Header("Fluid Settings")] [SerializeField]
        private bool m_enableFluidSimulation = true;

        private VoxelConfig m_voxelConfig;
        private VoxelVolume m_voxelVolume;
        private DualContouring m_dualContouring;
        private CSGUtility m_csgUtility;
        private FluidSimulation m_fluidSimulation;
        private ObjectPool<Chunk> m_chunkPool;
        private Dictionary<int3, Chunk> m_chunks;
        private List<int3> m_chunksOutsideOfViewDistance;
        private Queue<(int3, float3, int)> m_chunksToProcess;
        private HashSet<int3> m_processedChunkCoordinates;
        private float3 m_chunkDimensions;
        private Dictionary<int3, List<GPUVoxelVolumeCSGOperation>> m_chunkModifications;

        // We don't want to update the world every frame.
        private float3 m_lastViewerPosition;
        private float m_updateIntervalSquared;
        private float[] m_lodDistancesSquared;

        private void Start()
        {
            Assert.IsFalse(m_chunkPrefab.activeSelf);

            m_voxelConfig = GetComponent<VoxelConfig>();
            m_voxelConfig.VoxelVolumeConfig.OnLateDirtied += ApplyVoxelVolumeConfig;
            m_voxelConfig.DualContouringConfig.OnLateDirtied += ApplyDualContouringConfig;
            m_voxelConfig.GenerationGraph.OnLateDirtied += ApplyGenerationGraph;

            m_voxelVolume = GetComponent<VoxelVolume>();
            m_dualContouring = GetComponent<DualContouring>();
            m_csgUtility = GetComponent<CSGUtility>();

            // FluidSimulation is now required
            m_fluidSimulation = GetComponent<FluidSimulation>();
            if (m_fluidSimulation == null && m_enableFluidSimulation)
            {
                Debug.LogError("FluidSimulation component is required but not found!");
                m_enableFluidSimulation = false;
            }

            m_chunkPool =
                new ObjectPool<Chunk>(() => { return Instantiate(m_chunkPrefab, transform).GetComponent<Chunk>(); },
                    m_initialChunkPoolPopulation);
            m_chunks = new Dictionary<int3, Chunk>();
            m_chunksOutsideOfViewDistance = new List<int3>();
            m_chunksToProcess = new Queue<(int3, float3, int)>();
            m_processedChunkCoordinates = new HashSet<int3>();
            m_chunkDimensions = CalculateChunkDimensions();

            m_lastViewerPosition = m_viewer.position;
            m_updateIntervalSquared = math.pow(m_updateInterval, 2.0f);
            m_lodDistancesSquared = CalculateLodDistancesSquared();

            m_chunkModifications = new Dictionary<int3, List<GPUVoxelVolumeCSGOperation>>();

            UpdateWorld(m_viewer.position);
        }

        private void Update()
        {
            float3 currentViewerPosition = m_viewer.position;

            // Update world geometry
            if (math.lengthsq(currentViewerPosition - m_lastViewerPosition) >= m_updateIntervalSquared)
            {
                m_lastViewerPosition = currentViewerPosition;
                UpdateWorld(currentViewerPosition);
            }
        }

        private void OnDestroy()
        {
            m_voxelConfig.VoxelVolumeConfig.OnLateDirtied -= ApplyVoxelVolumeConfig;
            m_voxelConfig.DualContouringConfig.OnLateDirtied -= ApplyDualContouringConfig;
            m_voxelConfig.GenerationGraph.OnLateDirtied -= ApplyGenerationGraph;
        }

        private void OnValidate() => ApplySettings();

        public void DrawCSGPrimitiveHologram(CSGPrimitiveType primitiveType, float3 position, float3 scale)
        {
            m_csgUtility.DrawCSGPrimitiveHologram(primitiveType, Matrix4x4.TRS(position, quaternion.identity, scale));
        }

        public void ApplyCSGOperation(GPUCSGOperator csgOperator, GPUCSGPrimitive csgPrimitive,
            MaterialIndex materialIndex, float3 position, float3 scale)
        {
            const float scaleInflationFactor = 1.5f;

            Matrix4x4 worldToObjectMatrix = Matrix4x4.TRS(position, quaternion.identity, scale).inverse;

            // Inflate the scale a bit to ensure CSG operations near the boundary of chunks are processed by all nearby chunks.
            int3 minChunkCoordinate = CalculateChunkCoordinate(position - 0.5f * scaleInflationFactor * scale);
            int3 maxChunkCoordinate = CalculateChunkCoordinate(position + 0.5f * scaleInflationFactor * scale);

            for (int3 chunkCoordinate = minChunkCoordinate;
                 chunkCoordinate.z <= maxChunkCoordinate.z;
                 chunkCoordinate.z++)
            {
                for (chunkCoordinate.y = minChunkCoordinate.y;
                     chunkCoordinate.y <= maxChunkCoordinate.y;
                     chunkCoordinate.y++)
                {
                    for (chunkCoordinate.x = minChunkCoordinate.x;
                         chunkCoordinate.x <= maxChunkCoordinate.x;
                         chunkCoordinate.x++)
                    {
                        if (m_chunks.TryGetValue(chunkCoordinate, out Chunk chunk))
                        {
                            chunk.ApplyCSGPrimitiveOperation(csgOperator, csgPrimitive, materialIndex,
                                worldToObjectMatrix);

                            // The chunk will handle its own fluid boundary updates through its Update() method
                        }
                    }
                }
            }
        }

        // Add fluid source to the chunk containing the specified world position
        public void AddFluidSource(float3 worldPosition, float3 velocity, float radius, float amount,
            MaterialIndex fluidMaterial = MaterialIndex.Water)
        {
            if (!m_enableFluidSimulation || m_fluidSimulation == null)
            {
                Debug.LogWarning("Fluid simulation is not available. Cannot add fluid source.");
                return;
            }

            int3 chunkCoordinate = CalculateChunkCoordinate(worldPosition);

            if (m_chunks.TryGetValue(chunkCoordinate, out Chunk chunk))
            {
                chunk.AddFluidSource(worldPosition, velocity, radius, amount, fluidMaterial);
                Debug.Log($"Added fluid source to chunk {chunkCoordinate} at world position {worldPosition}");
            }
            else
            {
                Debug.LogWarning(
                    $"Cannot add fluid source: no chunk found at world position {worldPosition} (chunk coordinate: {chunkCoordinate})");
            }
        }

        // Remove fluid source from the chunk containing the specified world position
        public void RemoveFluidSource(float3 worldPosition)
        {
            if (!m_enableFluidSimulation || m_fluidSimulation == null)
                return;

            int3 chunkCoordinate = CalculateChunkCoordinate(worldPosition);

            if (m_chunks.TryGetValue(chunkCoordinate, out Chunk chunk))
            {
                chunk.RemoveFluidSource();
                Debug.Log($"Removed fluid source from chunk {chunkCoordinate}");
            }
        }

        // Add fluid source to a specific chunk by coordinate
        public void AddFluidSourceToChunk(int3 chunkCoordinate, float3 localPosition, float3 velocity, float radius,
            float amount, MaterialIndex fluidMaterial = MaterialIndex.Water)
        {
            if (!m_enableFluidSimulation || m_fluidSimulation == null)
            {
                Debug.LogWarning("Fluid simulation is not available. Cannot add fluid source.");
                return;
            }

            if (m_chunks.TryGetValue(chunkCoordinate, out Chunk chunk))
            {
                // Convert local position to world position
                float3 chunkWorldPosition = chunkCoordinate * m_chunkDimensions;
                float3 worldPosition = chunkWorldPosition + localPosition;

                chunk.AddFluidSource(worldPosition, velocity, radius, amount, fluidMaterial);
                Debug.Log($"Added fluid source to chunk {chunkCoordinate} at local position {localPosition}");
            }
            else
            {
                Debug.LogWarning($"Cannot add fluid source: chunk {chunkCoordinate} not found or not active");
            }
        }

        // Get active chunks for debugging
        public Dictionary<int3, Chunk> GetActiveChunks()
        {
            return new Dictionary<int3, Chunk>(m_chunks);
        }

        public bool GetMaterialFromRaycastHit(RaycastHit hit, out MaterialIndex materialIndex)
        {
            materialIndex = default;

            if (!(hit.collider is MeshCollider))
            {
                return false;
            }

            int3 chunkCoordinate = CalculateChunkCoordinate(hit.point);

            if (m_chunks.TryGetValue(chunkCoordinate, out Chunk chunk) &&
                chunk.GetMaterialFromRaycastHit(hit, out materialIndex))
            {
                return true;
            }

            return false;
        }

        private void UpdateWorld(float3 viewerPosition)
        {
            DestroyChunksOutsideViewDistance(viewerPosition);
            CreateChunksWithinViewDistance(viewerPosition);
        }

        private void DestroyChunksOutsideViewDistance(float3 viewerPosition)
        {
            foreach (KeyValuePair<int3, Chunk> pair in m_chunks)
            {
                float viewerToChunkDistanceSquared =
                    math.lengthsq((float3)pair.Value.transform.position - viewerPosition);

                if (viewerToChunkDistanceSquared > ViewDistanceSquared)
                {
                    m_chunksOutsideOfViewDistance.Add(pair.Key);
                }
            }

            foreach (int3 chunkCoordinate in m_chunksOutsideOfViewDistance)
            {
                m_chunkPool.Release(m_chunks[chunkCoordinate]);
                m_chunks.Remove(chunkCoordinate);
            }

            m_chunksOutsideOfViewDistance.Clear();
        }

        private void CreateChunksWithinViewDistance(float3 viewerPosition)
        {
            int3 chunkCoordinate = CalculateChunkCoordinate(viewerPosition);
            float3 chunkPosition = chunkCoordinate * m_chunkDimensions;
            float viewerToChunkDistanceSquared = math.lengthsq(chunkPosition - viewerPosition);
            int lod = CalculateChunkLod(viewerToChunkDistanceSquared);

            m_processedChunkCoordinates.Clear();
            m_chunksToProcess.Clear();

            EnqueueChunk(chunkCoordinate, viewerPosition);

            while (m_chunksToProcess.Count > 0)
            {
                (chunkCoordinate, chunkPosition, lod) = m_chunksToProcess.Dequeue();

                if (m_chunks.TryGetValue(chunkCoordinate, out Chunk chunk))
                {
                    chunk.RegenerateMesh(lod);
                }
                else
                {
                    // Create new chunk.
                    chunk = m_chunkPool.Acquire();
                    chunk.transform.position = chunkPosition;
                    chunk.RegenerateVoxelVolume();
                    chunk.RegenerateMesh(lod);
                    chunk.SetCoordinate(chunkCoordinate);
                    chunk.ApplyStoredModifications(chunkCoordinate);
                    m_chunks[chunkCoordinate] = chunk;

                    // Fluid buffers are automatically created in chunk.OnAcquire()
                }

                EnqueueChunk(chunkCoordinate + new int3(1, 0, 0), viewerPosition);
                EnqueueChunk(chunkCoordinate - new int3(1, 0, 0), viewerPosition);
                EnqueueChunk(chunkCoordinate + new int3(0, 0, 1), viewerPosition);
                EnqueueChunk(chunkCoordinate - new int3(0, 0, 1), viewerPosition);
            }
        }

        private void EnqueueChunk(int3 neighbourChunkCoordinate, float3 viewerPosition)
        {
            if (!m_processedChunkCoordinates.Contains(neighbourChunkCoordinate))
            {
                float3 neighbourChunkPosition = neighbourChunkCoordinate * m_chunkDimensions;
                float viewerToNeighbourChunkDistanceSquared = math.lengthsq(neighbourChunkPosition - viewerPosition);

                if (viewerToNeighbourChunkDistanceSquared <= ViewDistanceSquared)
                {
                    m_chunksToProcess.Enqueue((neighbourChunkCoordinate, neighbourChunkPosition,
                        CalculateChunkLod(viewerToNeighbourChunkDistanceSquared)));
                }
            }

            m_processedChunkCoordinates.Add(neighbourChunkCoordinate);
        }

        private float3 CalculateChunkDimensions()
        {
            const int voxelOverlap = 1;

            float inflationFactor = 1.0f + (float)voxelOverlap /
                (VoxelConfig.VoxelVolumeConfig.NumberOfCellsAlongAxis - voxelOverlap);

            return VoxelConfig.VoxelVolumeConfig.VoxelVolumeDimensions / inflationFactor;
        }

        private int3 CalculateChunkCoordinate(float3 position) => new int3(
            (int)math.round(position.x / m_chunkDimensions.x), 0, (int)math.round(position.z / m_chunkDimensions.z));

        private float[] CalculateLodDistancesSquared()
        {
            float[] lodDistancesSquared = new float[m_lodDistances.Length];

            for (int index = 0; index < lodDistancesSquared.Length; index++)
            {
                lodDistancesSquared[index] = math.pow(m_lodDistances[index], 2.0f);
            }

            return lodDistancesSquared;
        }

        private int CalculateChunkLod(float viewerToChunkDistanceSquared)
        {
            int lod = Array.BinarySearch(m_lodDistancesSquared, viewerToChunkDistanceSquared);

            if (lod < 0)
            {
                lod = ~lod;
            }

            if (lod == m_lodDistancesSquared.Length)
            {
                lod = 0;
            }

            return lod;
        }

        private void ApplySettings()
        {
            if (!Application.isPlaying || !gameObject.activeSelf || m_voxelConfig == null)
            {
                return;
            }

            m_updateIntervalSquared = math.pow(m_updateInterval, 2.0f);
            m_lodDistancesSquared = CalculateLodDistancesSquared();
            m_chunkDimensions = CalculateChunkDimensions();

            foreach (Chunk chunk in m_chunks.Values)
            {
                m_chunkPool.Release(chunk);
            }

            m_chunks.Clear();

            UpdateWorld(m_viewer.position);
        }

        private void ApplyVoxelVolumeConfig() => ApplySettings();

        private void ApplyDualContouringConfig()
        {
            foreach (Chunk chunk in m_chunks.Values)
            {
                chunk.RegenerateMesh();
            }
        }

        private void ApplyGenerationGraph()
        {
            foreach (Chunk chunk in m_chunks.Values)
            {
                chunk.RegenerateVoxelVolume();
                chunk.RegenerateMesh();
            }
        }

        // Chunk modification tracking (existing methods)
        public bool ChunkHasModifications(int3 chunkCoordinate)
        {
            return m_chunkModifications.ContainsKey(chunkCoordinate);
        }

        public void InitializeChunkModifications(int3 chunkCoordinate)
        {
            m_chunkModifications[chunkCoordinate] = new List<GPUVoxelVolumeCSGOperation>();
        }

        public void AddChunkModification(int3 chunkCoordinate, GPUVoxelVolumeCSGOperation operation)
        {
            m_chunkModifications[chunkCoordinate].Add(operation);
        }

        public List<GPUVoxelVolumeCSGOperation> GetChunkModifications(int3 chunkCoordinate)
        {
            if (m_chunkModifications.TryGetValue(chunkCoordinate, out var modifications))
            {
                return modifications;
            }

            return null;
        }
        
        public void AddTestFluidSource()
        {
            var viewerPos = m_viewer.position;
            AddFluidSource(viewerPos + Vector3.forward * 10f, Vector3.up * 2f, 3f, 1f);
        }

        public void RemoveAllFluidSources()
        {
            foreach (var chunk in m_chunks.Values)
            {
                chunk.RemoveFluidSource();
            }
        }

        public void DebugChunkFluidStates()
        {
            foreach (var chunk in m_chunks.Values)
            {
                chunk.FluidData.DebugOutput();
            }
        }

        // Public method for editor to force regenerate all fluid meshes
        public void ForceRegenerateAllFluidMeshes()
        {
            if (!m_enableFluidSimulation)
            {
                Debug.LogWarning("Fluid simulation is disabled");
                return;
            }

            int fluidChunkCount = 0;
            foreach (var chunk in m_chunks.Values)
            {
                if (chunk.HasActiveFluid())
                {
                    fluidChunkCount++;
                }

                chunk.RegenerateFluidMesh();
            }

            Debug.Log(
                $"Force regenerated fluid meshes for {m_chunks.Count} chunks ({fluidChunkCount} have active fluid)");
        }
    }
}