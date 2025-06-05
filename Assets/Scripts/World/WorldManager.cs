using System;
using System.Collections.Generic;
using System.Linq;
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
    /// Integrates texture-based fluid simulation with centralized FluidSimulation manager.
    /// </summary>
    [RequireComponent(typeof(VoxelConfig), typeof(VoxelVolume), typeof(DualContouring))]
    [RequireComponent(typeof(CSGUtility), typeof(AsyncFluidSimulation))]
    public class WorldManager : SingletonComponent<WorldManager>
    {
        public static VoxelConfig VoxelConfig => Instance.m_voxelConfig;
        public static VoxelVolume VoxelVolume => Instance.m_voxelVolume;
        public static DualContouring DualContouring => Instance.m_dualContouring;
        public static AsyncFluidSimulation FluidSimulation => Instance.m_fluidSimulation;

        private float ViewDistanceSquared => m_lodDistancesSquared[m_lodDistancesSquared.Length - 1];

        [Header("World Settings")] [SerializeField]
        private Transform m_viewer;

        [SerializeField] private float m_updateInterval = 20.0f;
        [SerializeField] private GameObject m_chunkPrefab;
        [SerializeField] private int m_initialChunkPoolPopulation = 0;
        [SerializeField] private float[] m_lodDistances;

        [Header("Fluid Settings")] [SerializeField]
        private bool m_enableFluidSimulation = true;

        [SerializeField] private float m_fluidUpdateInterval = 0.016f; // 60 FPS for fluid updates
        [SerializeField] private int m_maxFluidChunksPerFrame = 5;
        [SerializeField] private float m_fluidSourceDuration = 10f; // Default duration for test sources
        [SerializeField] private bool m_enableFluidDebugLogging = false;

        [Header("Performance")] [SerializeField]
        private bool m_enableFluidLOD = true;

        [SerializeField] private float m_fluidDisableDistance = 100f;
        [SerializeField] private bool m_pauseDistantFluidSources = true;

        private VoxelConfig m_voxelConfig;
        private VoxelVolume m_voxelVolume;
        private DualContouring m_dualContouring;
        private CSGUtility m_csgUtility;
        private AsyncFluidSimulation m_fluidSimulation;
        private ObjectPool<Chunk> m_chunkPool;
        private Dictionary<int3, Chunk> m_chunks;
        private List<int3> m_chunksOutsideOfViewDistance;
        private Queue<(int3, float3, int)> m_chunksToProcess;
        private HashSet<int3> m_processedChunkCoordinates;
        private float3 m_chunkDimensions;
        private Dictionary<int3, List<GPUVoxelVolumeCSGOperation>> m_chunkModifications;

        // Fluid management
        private List<int3> m_activeFluidChunks = new List<int3>();
        private Queue<int3> m_fluidUpdateQueue = new Queue<int3>();
        private float m_lastFluidUpdate = 0f;
        private int m_fluidChunksUpdatedThisFrame = 0;

        // Performance tracking
        private float m_lastPerformanceLog = 0f;
        private const float PERFORMANCE_LOG_INTERVAL = 5f;
        private FluidPerformanceStats m_performanceStats = new FluidPerformanceStats();

        // World update timing
        private float3 m_lastViewerPosition;
        private float m_updateIntervalSquared;
        private float[] m_lodDistancesSquared;

        private void Start()
        {
            Assert.IsFalse(m_chunkPrefab.activeSelf);

            InitializeVoxelComponents();
            InitializeFluidSimulation();
            InitializeChunkManagement();
            InitializeSettings();

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

            // Update fluid simulation
            if (m_enableFluidSimulation && Time.time - m_lastFluidUpdate >= m_fluidUpdateInterval)
            {
                UpdateFluidSystem(currentViewerPosition);
                m_lastFluidUpdate = Time.time;
            }

            // Performance logging
            if (m_enableFluidDebugLogging && Time.time - m_lastPerformanceLog >= PERFORMANCE_LOG_INTERVAL)
            {
                LogPerformanceStats();
                m_lastPerformanceLog = Time.time;
            }
        }

        private void OnDestroy()
        {
            m_voxelConfig.VoxelVolumeConfig.OnLateDirtied -= ApplyVoxelVolumeConfig;
            m_voxelConfig.DualContouringConfig.OnLateDirtied -= ApplyDualContouringConfig;
            m_voxelConfig.GenerationGraph.OnLateDirtied -= ApplyGenerationGraph;
        }

        private void OnValidate() => ApplySettings();

        #region Initialization

        private void InitializeVoxelComponents()
        {
            m_voxelConfig = GetComponent<VoxelConfig>();
            m_voxelConfig.VoxelVolumeConfig.OnLateDirtied += ApplyVoxelVolumeConfig;
            m_voxelConfig.DualContouringConfig.OnLateDirtied += ApplyDualContouringConfig;
            m_voxelConfig.GenerationGraph.OnLateDirtied += ApplyGenerationGraph;

            m_voxelVolume = GetComponent<VoxelVolume>();
            m_dualContouring = GetComponent<DualContouring>();
            m_csgUtility = GetComponent<CSGUtility>();
        }

        private void InitializeFluidSimulation()
        {
            m_fluidSimulation = GetComponent<AsyncFluidSimulation>();

            if (m_fluidSimulation == null)
            {
                Debug.LogError("FluidSimulation component is required but not found!");
                m_enableFluidSimulation = false;
                return;
            }

            if (!m_fluidSimulation.IsSimulationEnabled)
            {
                Debug.LogWarning("FluidSimulation is disabled. Fluid features will not be available.");
                m_enableFluidSimulation = false;
            }

            if (m_enableFluidDebugLogging)
            {
                Debug.Log($"FluidSimulation initialized. Enabled: {m_enableFluidSimulation}");
            }
        }

        private void InitializeChunkManagement()
        {
            m_chunkPool = new ObjectPool<Chunk>(
                () => { return Instantiate(m_chunkPrefab, transform).GetComponent<Chunk>(); },
                m_initialChunkPoolPopulation);

            m_chunks = new Dictionary<int3, Chunk>();
            m_chunksOutsideOfViewDistance = new List<int3>();
            m_chunksToProcess = new Queue<(int3, float3, int)>();
            m_processedChunkCoordinates = new HashSet<int3>();
            m_chunkModifications = new Dictionary<int3, List<GPUVoxelVolumeCSGOperation>>();
        }

        private void InitializeSettings()
        {
            m_chunkDimensions = CalculateChunkDimensions();
            m_lastViewerPosition = m_viewer.position;
            m_updateIntervalSquared = math.pow(m_updateInterval, 2.0f);
            m_lodDistancesSquared = CalculateLodDistancesSquared();
        }

        #endregion

        #region Fluid System Management

        private void UpdateFluidSystem(float3 viewerPosition)
        {
            m_fluidChunksUpdatedThisFrame = 0;

            // Update active fluid chunks list
            UpdateActiveFluidChunksList();

            // Apply LOD and distance culling to fluid simulation
            if (m_enableFluidLOD)
            {
                ApplyFluidLOD(viewerPosition);
            }

            // Process fluid update queue
            ProcessFluidUpdateQueue();

            // Update performance stats
            UpdatePerformanceStats();
        }

        private void UpdateActiveFluidChunksList()
        {
            m_activeFluidChunks.Clear();

            foreach (var kvp in m_chunks)
            {
                var chunk = kvp.Value;
                if (chunk.HasActiveFluid() || chunk.FluidData?.NeedsSimulationUpdate() == true)
                {
                    m_activeFluidChunks.Add(kvp.Key);
                }
            }
        }

        private void ApplyFluidLOD(float3 viewerPosition)
        {
            foreach (var chunkCoord in m_activeFluidChunks)
            {
                if (m_chunks.TryGetValue(chunkCoord, out Chunk chunk))
                {
                    float3 chunkPosition = chunk.transform.position;
                    float distanceSquared = math.lengthsq(chunkPosition - viewerPosition);

                    // Disable fluid simulation for very distant chunks
                    if (distanceSquared > m_fluidDisableDistance * m_fluidDisableDistance)
                    {
                        if (m_pauseDistantFluidSources && chunk.FluidData?.HasFluidSource == true)
                        {
                            // Temporarily pause distant sources instead of removing them
                            continue;
                        }
                    }

                    // Add to update queue if not already processed this frame
                    if (m_fluidChunksUpdatedThisFrame < m_maxFluidChunksPerFrame)
                    {
                        if (!m_fluidUpdateQueue.Contains(chunkCoord))
                        {
                            m_fluidUpdateQueue.Enqueue(chunkCoord);
                        }
                    }
                }
            }
        }

        private void ProcessFluidUpdateQueue()
        {
            while (m_fluidUpdateQueue.Count > 0 && m_fluidChunksUpdatedThisFrame < m_maxFluidChunksPerFrame)
            {
                var chunkCoord = m_fluidUpdateQueue.Dequeue();

                if (m_chunks.TryGetValue(chunkCoord, out Chunk chunk))
                {
                    if (chunk.FluidData?.NeedsSimulationUpdate() == true)
                    {
                        // Chunk will handle its own fluid update in its Update() method
                        m_fluidChunksUpdatedThisFrame++;
                    }
                }
            }
        }

        private void UpdatePerformanceStats()
        {
            m_performanceStats.TotalChunks = m_chunks.Count;
            m_performanceStats.ActiveFluidChunks = m_activeFluidChunks.Count;
            m_performanceStats.FluidChunksUpdatedThisFrame = m_fluidChunksUpdatedThisFrame;

            // Calculate memory usage
            long totalFluidMemory = 0;
            foreach (var chunk in m_chunks.Values)
            {
                if (chunk.FluidData != null)
                {
                    totalFluidMemory += chunk.FluidData.GetEstimatedMemoryUsage();
                }
            }

            m_performanceStats.EstimatedFluidMemoryUsage = totalFluidMemory;
        }

        private void LogPerformanceStats()
        {
            Debug.Log($"[FluidPerformance] Chunks: {m_performanceStats.TotalChunks}, " +
                      $"Active Fluid: {m_performanceStats.ActiveFluidChunks}, " +
                      $"Updated This Frame: {m_performanceStats.FluidChunksUpdatedThisFrame}, " +
                      $"Memory: {m_performanceStats.EstimatedFluidMemoryUsage / (1024 * 1024)}MB");
        }

        #endregion

        #region Public Fluid API

        public void DrawCSGPrimitiveHologram(CSGPrimitiveType primitiveType, float3 position, float3 scale)
        {
            m_csgUtility.DrawCSGPrimitiveHologram(primitiveType, Matrix4x4.TRS(position, quaternion.identity, scale));
        }

        public void ApplyCSGOperation(GPUCSGOperator csgOperator, GPUCSGPrimitive csgPrimitive,
            MaterialIndex materialIndex, float3 position, float3 scale)
        {
            const float scaleInflationFactor = 1.5f;

            Matrix4x4 worldToObjectMatrix = Matrix4x4.TRS(position, quaternion.identity, scale).inverse;

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
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Add a fluid source to the chunk containing the specified world position
        /// </summary>
        public void AddFluidSource(float3 worldPosition, float3 velocity, float radius, float amount,
            MaterialIndex fluidMaterial = MaterialIndex.Water, float duration = -1f)
        {
            if (!m_enableFluidSimulation || m_fluidSimulation == null || !m_fluidSimulation.IsSimulationEnabled)
            {
                Debug.LogWarning("Fluid simulation is not available. Cannot add fluid source.");
                return;
            }

            int3 chunkCoordinate = CalculateChunkCoordinate(worldPosition);

            if (m_chunks.TryGetValue(chunkCoordinate, out Chunk chunk))
            {
                float actualDuration = duration > 0 ? duration : m_fluidSourceDuration;
                chunk.AddFluidSource(worldPosition, velocity, radius, amount, fluidMaterial, actualDuration);

                if (m_enableFluidDebugLogging)
                {
                    Debug.Log($"Added fluid source to chunk {chunkCoordinate} at {worldPosition} " +
                              $"(duration: {actualDuration}s)");
                }
            }
            else
            {
                Debug.LogWarning($"Cannot add fluid source: no chunk found at world position {worldPosition} " +
                                 $"(chunk coordinate: {chunkCoordinate})");
            }
        }

        /// <summary>
        /// Remove fluid source from the chunk containing the specified world position
        /// </summary>
        public void RemoveFluidSource(float3 worldPosition)
        {
            if (!m_enableFluidSimulation || m_fluidSimulation == null)
                return;

            int3 chunkCoordinate = CalculateChunkCoordinate(worldPosition);

            if (m_chunks.TryGetValue(chunkCoordinate, out Chunk chunk))
            {
                chunk.RemoveFluidSource();

                if (m_enableFluidDebugLogging)
                {
                    Debug.Log($"Removed fluid source from chunk {chunkCoordinate}");
                }
            }
        }

        /// <summary>
        /// Remove all fluid sources from all chunks
        /// </summary>
        public void RemoveAllFluidSources()
        {
            if (!m_enableFluidSimulation)
                return;

            int removedCount = 0;
            foreach (var chunk in m_chunks.Values)
            {
                if (chunk.FluidData?.HasFluidSource == true)
                {
                    chunk.RemoveFluidSource();
                    removedCount++;
                }
            }

            if (m_enableFluidDebugLogging)
            {
                Debug.Log($"Removed {removedCount} fluid sources from {m_chunks.Count} chunks");
            }
        }

        /// <summary>
        /// Add a test fluid source at viewer position + offset
        /// </summary>
        public void AddTestFluidSource()
        {
            var viewerPos = m_viewer.position;
            var sourcePos = viewerPos + Vector3.forward * 10f + Vector3.up * 5f;

            AddFluidSource(
                sourcePos,
                Vector3.down * 2f + Vector3.forward * 1f, // Slight forward velocity
                3f, // radius
                50f, // amount
                MaterialIndex.Water,
                m_fluidSourceDuration
            );

            if (m_enableFluidDebugLogging)
            {
                Debug.Log($"Added test fluid source at {sourcePos} with duration {m_fluidSourceDuration}s");
            }
        }

        /// <summary>
        /// Force regenerate all fluid meshes (for volumetric rendering, this updates registration)
        /// </summary>
        public void ForceRegenerateAllFluidMeshes()
        {
            if (!m_enableFluidSimulation)
            {
                Debug.LogWarning("Fluid simulation is disabled");
                return;
            }

            int processedCount = 0;
            int activeFluidCount = 0;

            foreach (var chunk in m_chunks.Values)
            {
                if (chunk.HasActiveFluid())
                {
                    activeFluidCount++;

                    // For volumetric rendering, we re-register with the renderer
                    if (chunk.FluidData?.IsValid() == true)
                    {
                        var volumeSize = VoxelConfig.VoxelVolumeConfig.VoxelVolumeDimensions;
                        chunk.FluidData.RegisterForVolumetricRendering(
                            CalculateChunkCoordinate(chunk.transform.position),
                            volumeSize
                        );
                    }
                }

                processedCount++;
            }

            Debug.Log($"Force regenerated fluid rendering for {processedCount} chunks " +
                      $"({activeFluidCount} have active fluid)");
        }

        /// <summary>
        /// Debug chunk fluid states
        /// </summary>
        public void DebugChunkFluidStates()
        {
            if (!m_enableFluidSimulation)
            {
                Debug.Log("Fluid simulation is disabled");
                return;
            }

            Debug.Log("=== Chunk Fluid States Debug ===");

            int totalChunks = 0;
            int chunksWithFluidData = 0;
            int chunksWithActiveSources = 0;
            int chunksWithActiveFluid = 0;

            foreach (var kvp in m_chunks)
            {
                var chunk = kvp.Value;
                totalChunks++;

                if (chunk.FluidData != null)
                {
                    chunksWithFluidData++;

                    if (chunk.FluidData.HasFluidSource)
                    {
                        chunksWithActiveSources++;
                        Debug.Log($"Chunk {kvp.Key}: {chunk.FluidData.FluidSource}");
                    }

                    if (chunk.HasActiveFluid())
                    {
                        chunksWithActiveFluid++;
                    }
                }
            }

            Debug.Log($"Total chunks: {totalChunks}");
            Debug.Log($"Chunks with fluid data: {chunksWithFluidData}");
            Debug.Log($"Chunks with active sources: {chunksWithActiveSources}");
            Debug.Log($"Chunks with active fluid: {chunksWithActiveFluid}");
            Debug.Log($"Performance stats: {m_performanceStats}");
            Debug.Log("================================");
        }

        /// <summary>
        /// Get fluid density at a specific world position
        /// </summary>
        public float GetFluidDensityAtPosition(float3 worldPosition)
        {
            if (!m_enableFluidSimulation)
                return 0f;

            int3 chunkCoordinate = CalculateChunkCoordinate(worldPosition);

            if (m_chunks.TryGetValue(chunkCoordinate, out Chunk chunk))
            {
                return chunk.GetFluidDensityAtPosition(worldPosition);
            }

            return 0f;
        }

        /// <summary>
        /// Get active chunks for external access
        /// </summary>
        public Dictionary<int3, Chunk> GetActiveChunks()
        {
            return new Dictionary<int3, Chunk>(m_chunks);
        }

        /// <summary>
        /// Get chunks with active fluid
        /// </summary>
        public List<Chunk> GetActiveFluidChunks()
        {
            return m_chunks.Values.Where(chunk => chunk.HasActiveFluid()).ToList();
        }

        /// <summary>
        /// Get fluid performance statistics
        /// </summary>
        public FluidPerformanceStats GetFluidPerformanceStats()
        {
            return m_performanceStats;
        }

        #endregion

        #region Material Raycast

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

        #endregion

        #region World Management (existing functionality)

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
                if (m_enableFluidDebugLogging && m_chunks[chunkCoordinate].HasActiveFluid())
                {
                    Debug.Log($"Destroying chunk {chunkCoordinate} with active fluid");
                }

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
                    // Create new chunk
                    chunk = m_chunkPool.Acquire();
                    chunk.transform.position = chunkPosition;
                    chunk.RegenerateVoxelVolume();
                    chunk.RegenerateMesh(lod);
                    chunk.SetCoordinate(chunkCoordinate);
                    chunk.ApplyStoredModifications(chunkCoordinate);
                    m_chunks[chunkCoordinate] = chunk;

                    if (m_enableFluidDebugLogging)
                    {
                        Debug.Log(
                            $"Created new chunk at {chunkCoordinate} with fluid simulation enabled: {m_enableFluidSimulation}");
                    }
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

        #endregion

        #region Utility Methods

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

        #endregion

        #region Settings Application

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
            m_activeFluidChunks.Clear();
            m_fluidUpdateQueue.Clear();

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

        #endregion

        #region Chunk Modification Tracking

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

        #endregion
    }

    /// <summary>
    /// Performance statistics for fluid simulation monitoring
    /// </summary>
    [System.Serializable]
    public class FluidPerformanceStats
    {
        public int TotalChunks;
        public int ActiveFluidChunks;
        public int FluidChunksUpdatedThisFrame;
        public long EstimatedFluidMemoryUsage;
        public float AverageFluidUpdateTime;
        public int TotalFluidSources;

        public override string ToString()
        {
            return $"FluidPerformance(Chunks: {TotalChunks}, ActiveFluid: {ActiveFluidChunks}, " +
                   $"UpdatedThisFrame: {FluidChunksUpdatedThisFrame}, Memory: {EstimatedFluidMemoryUsage / (1024 * 1024)}MB, " +
                   $"AvgUpdateTime: {AverageFluidUpdateTime:F3}ms, Sources: {TotalFluidSources})";
        }
    }
}