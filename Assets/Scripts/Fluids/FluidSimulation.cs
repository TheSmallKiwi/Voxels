using System.Collections.Generic;
using Tuntenfisch.Extensions;
using Tuntenfisch.Voxels;
using Tuntenfisch.Voxels.Materials;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Tuntenfisch.Fluids
{
    /// <summary>
    /// Manages fluid simulation across multiple chunks using CommandBuffers to spread computation across frames.
    /// </summary>
    [RequireComponent(typeof(VoxelConfig))]
    public class FluidSimulation : MonoBehaviour
    {
        [Header("Simulation Parameters")] [SerializeField]
        private bool m_enableSimulation = true;

        [SerializeField] private float m_timeStep = 0.016f;
        [SerializeField] private int m_pressureIterations = 20;
        [SerializeField] private int m_maxCommandsPerFrame = 3;

        private VoxelConfig m_voxelConfig;
        private ComputeBuffer m_divergenceBuffer;

        // Compute shader kernel IDs
        private int m_initializeFluidVolumeKernel;
        private int m_advectionKernel;
        private int m_externalForcesKernel;
        private int m_computeDivergenceKernel;
        private int m_pressureProjectionKernel;
        private int m_applyBoundaryConditionsKernel;
        private int m_addFluidSourceKernel;
        private int m_updateBoundariesFromSolidsKernel;
        private int m_convertFluidToVoxelVolumeKernel;

        // Active fluid chunks being simulated
        private Queue<ChunkFluidData> m_pendingChunks;
        private HashSet<ChunkFluidData> m_activeChunks;

        private bool m_initialized = false;

        public bool IsSimulationEnabled => m_enableSimulation;

        private void Awake()
        {
            m_voxelConfig = GetComponent<VoxelConfig>();
            m_pendingChunks = new Queue<ChunkFluidData>();
            m_activeChunks = new HashSet<ChunkFluidData>();

            if (m_voxelConfig.FluidSimulationConfig == null)
            {
                Debug.LogError("FluidSimulation requires a FluidSimulationConfig in VoxelConfig!");
                enabled = false;
                return;
            }

            InitializeKernels();
            CreateSharedBuffers();
            m_initialized = true;
        }

        private void Update()
        {
            if (!m_initialized || !m_enableSimulation)
                return;

            ProcessPendingChunks();
            ExecuteChunkCommands();
        }

        private void OnDestroy()
        {
            ReleaseSharedBuffers();

            // Clean up any remaining command buffers
            foreach (var chunkData in m_activeChunks)
            {
                chunkData.CleanupCommandBuffer();
            }

            m_activeChunks.Clear();
            m_pendingChunks.Clear();
        }

        private void ProcessPendingChunks()
        {
            while (m_pendingChunks.Count > 0)
            {
                var chunkData = m_pendingChunks.Dequeue();
                if (chunkData.IsValid() && !m_activeChunks.Contains(chunkData))
                {
                    InitializeChunkSimulation(chunkData);
                    m_activeChunks.Add(chunkData);
                }
            }
        }

        private void ExecuteChunkCommands()
        {
            int commandsExecuted = 0;
            var chunksToRemove = new List<ChunkFluidData>();

            foreach (var chunkData in m_activeChunks)
            {
                if (commandsExecuted >= m_maxCommandsPerFrame)
                    break;

                if (!chunkData.IsValid())
                {
                    chunksToRemove.Add(chunkData);
                    continue;
                }

                bool hasMoreCommands = chunkData.ExecuteNextCommand();
                if (!hasMoreCommands)
                {
                    chunksToRemove.Add(chunkData);
                }

                commandsExecuted++;
            }

            // Remove completed chunks
            foreach (var chunk in chunksToRemove)
            {
                m_activeChunks.Remove(chunk);
                chunk.CleanupCommandBuffer();
            }
        }

        private void InitializeChunkSimulation(ChunkFluidData chunkData)
        {
            if (!chunkData.IsValid())
                return;

            // Create and populate command buffer for this chunk
            chunkData.InitializeCommandBuffer();
            BuildSimulationCommands(chunkData);
        }

        private void BuildSimulationCommands(ChunkFluidData chunkData)
        {
            var compute = m_voxelConfig.FluidSimulationConfig.Compute;
            var numberOfVoxels = m_voxelConfig.VoxelVolumeConfig.NumberOfVoxels;
            var cmd = chunkData.CommandBuffer;

            // 1. Update boundaries from solid geometry
            AddUpdateBoundariesCommand(cmd, chunkData, compute, numberOfVoxels);

            // 2. Add fluid sources (if any)
            if (chunkData.HasFluidSource)
            {
                AddFluidSourceCommand(cmd, chunkData, compute, numberOfVoxels);
            }

            // // 3. Advection step
            // AddAdvectionCommand(cmd, chunkData, compute, numberOfVoxels);
            //
            // // 4. Apply external forces
            // AddExternalForcesCommand(cmd, chunkData, compute, numberOfVoxels);
            //
            // // 5. Pressure projection (multiple iterations)
            // for (int i = 0; i < m_pressureIterations; i++)
            // {
            //     AddDivergenceCommand(cmd, chunkData, compute, numberOfVoxels);
            //     AddPressureProjectionCommand(cmd, chunkData, compute, numberOfVoxels);
            // }
            //
            // // 6. Apply boundary conditions
            // AddBoundaryConditionsCommand(cmd, chunkData, compute, numberOfVoxels);
            //
            // 7. Final buffer swap
            AddBufferSwapCommand(cmd, chunkData);
        }

        private void AddUpdateBoundariesCommand(CommandBuffer cmd, ChunkFluidData chunkData, ComputeShader compute,
            int3 numberOfVoxels)
        {
            if (chunkData.VoxelVolumeBuffer != null)
            {
                SetGlobalParametersToCommand(cmd, compute, chunkData.WorldPosition);
                cmd.SetComputeBufferParam(compute, m_updateBoundariesFromSolidsKernel, "voxelVolume",
                    chunkData.VoxelVolumeBuffer);
                cmd.SetComputeBufferParam(compute, m_updateBoundariesFromSolidsKernel, "fluidVolume",
                    chunkData.FluidVolumeBuffer);
                cmd.SetComputeBufferParam(compute, m_updateBoundariesFromSolidsKernel, "fluidVolumeBackBuffer",
                    chunkData.FluidVolumeBackBuffer);
                var fence = cmd.CreateGraphicsFence(GraphicsFenceType.AsyncQueueSynchronisation, SynchronisationStageFlags.AllGPUOperations);
                cmd.DispatchCompute(compute, m_updateBoundariesFromSolidsKernel,
                    numberOfVoxels.x / 4 + 1, numberOfVoxels.y / 4 + 1, numberOfVoxels.z / 4 + 1);
                cmd.WaitOnAsyncGraphicsFence(fence);
            }
        }

        private void AddFluidSourceCommand(CommandBuffer cmd, ChunkFluidData chunkData, ComputeShader compute,
            int3 numberOfVoxels)
        {
            SetGlobalParametersToCommand(cmd, compute, chunkData.WorldPosition);
            SetFluidSourceParametersToCommand(cmd, compute, chunkData.FluidSource);
            cmd.SetComputeBufferParam(compute, m_addFluidSourceKernel, "fluidVolume", chunkData.FluidVolumeBuffer);
            cmd.SetComputeBufferParam(compute, m_addFluidSourceKernel, "fluidVolumeBackBuffer",
                chunkData.FluidVolumeBackBuffer);
            var fence = cmd.CreateGraphicsFence(GraphicsFenceType.AsyncQueueSynchronisation, SynchronisationStageFlags.AllGPUOperations);
            cmd.DispatchCompute(compute, m_addFluidSourceKernel,
                numberOfVoxels.x / 4 + 1, numberOfVoxels.y / 4 + 1, numberOfVoxels.z / 4 + 1);
            cmd.WaitOnAsyncGraphicsFence(fence);
        }

        private void AddAdvectionCommand(CommandBuffer cmd, ChunkFluidData chunkData, ComputeShader compute,
            int3 numberOfVoxels)
        {
            SetGlobalParametersToCommand(cmd, compute, chunkData.WorldPosition);
            cmd.SetComputeBufferParam(compute, m_advectionKernel, "fluidVolume", chunkData.FluidVolumeBuffer);
            cmd.SetComputeBufferParam(compute, m_advectionKernel, "fluidVolumeBackBuffer",
                chunkData.FluidVolumeBackBuffer);
            var fence = cmd.CreateGraphicsFence(GraphicsFenceType.AsyncQueueSynchronisation, SynchronisationStageFlags.AllGPUOperations);
            cmd.DispatchCompute(compute, m_advectionKernel,
                numberOfVoxels.x / 4 + 1, numberOfVoxels.y / 4 + 1, numberOfVoxels.z / 4 + 1);
            cmd.WaitOnAsyncGraphicsFence(fence);
            
            // Add buffer swap after advection
            AddBufferSwapCommand(cmd, chunkData);
        }

        private void AddExternalForcesCommand(CommandBuffer cmd, ChunkFluidData chunkData, ComputeShader compute,
            int3 numberOfVoxels)
        {
            SetGlobalParametersToCommand(cmd, compute, chunkData.WorldPosition);
            cmd.SetComputeBufferParam(compute, m_externalForcesKernel, "fluidVolume", chunkData.FluidVolumeBuffer);
            cmd.SetComputeBufferParam(compute, m_externalForcesKernel, "fluidVolumeBackBuffer",
                chunkData.FluidVolumeBackBuffer);
            var fence = cmd.CreateGraphicsFence(GraphicsFenceType.AsyncQueueSynchronisation, SynchronisationStageFlags.AllGPUOperations);
            cmd.DispatchCompute(compute, m_externalForcesKernel,
                numberOfVoxels.x / 4 + 1, numberOfVoxels.y / 4 + 1, numberOfVoxels.z / 4 + 1);
            cmd.WaitOnAsyncGraphicsFence(fence);
        }

        private void AddDivergenceCommand(CommandBuffer cmd, ChunkFluidData chunkData, ComputeShader compute,
            int3 numberOfVoxels)
        {
            SetGlobalParametersToCommand(cmd, compute, chunkData.WorldPosition);
            cmd.SetComputeBufferParam(compute, m_computeDivergenceKernel, "fluidVolume", chunkData.FluidVolumeBuffer);
            cmd.SetComputeBufferParam(compute, m_computeDivergenceKernel, "divergenceBuffer", m_divergenceBuffer);
            var fence = cmd.CreateGraphicsFence(GraphicsFenceType.AsyncQueueSynchronisation, SynchronisationStageFlags.AllGPUOperations);
            cmd.DispatchCompute(compute, m_computeDivergenceKernel,
                numberOfVoxels.x / 4 + 1, numberOfVoxels.y / 4 + 1, numberOfVoxels.z / 4 + 1);
            cmd.WaitOnAsyncGraphicsFence(fence);
        }

        private void AddPressureProjectionCommand(CommandBuffer cmd, ChunkFluidData chunkData, ComputeShader compute,
            int3 numberOfVoxels)
        {
            SetGlobalParametersToCommand(cmd, compute, chunkData.WorldPosition);
            cmd.SetComputeBufferParam(compute, m_pressureProjectionKernel, "fluidVolume", chunkData.FluidVolumeBuffer);
            cmd.SetComputeBufferParam(compute, m_pressureProjectionKernel, "fluidVolumeBackBuffer",
                chunkData.FluidVolumeBackBuffer);
            cmd.SetComputeBufferParam(compute, m_pressureProjectionKernel, "divergenceBuffer", m_divergenceBuffer);
            var fence = cmd.CreateGraphicsFence(GraphicsFenceType.AsyncQueueSynchronisation, SynchronisationStageFlags.AllGPUOperations);
            cmd.DispatchCompute(compute, m_pressureProjectionKernel,
                numberOfVoxels.x / 4 + 1, numberOfVoxels.y / 4 + 1, numberOfVoxels.z / 4 + 1);
            cmd.WaitOnAsyncGraphicsFence(fence);

            // Add buffer swap after pressure projection
            AddBufferSwapCommand(cmd, chunkData);
        }

        private void AddBoundaryConditionsCommand(CommandBuffer cmd, ChunkFluidData chunkData, ComputeShader compute,
            int3 numberOfVoxels)
        {
            SetGlobalParametersToCommand(cmd, compute, chunkData.WorldPosition);
            cmd.SetComputeBufferParam(compute, m_applyBoundaryConditionsKernel, "fluidVolume",
                chunkData.FluidVolumeBuffer);
            cmd.SetComputeBufferParam(compute, m_applyBoundaryConditionsKernel, "fluidVolumeBackBuffer",
                chunkData.FluidVolumeBackBuffer);
            var fence = cmd.CreateGraphicsFence(GraphicsFenceType.AsyncQueueSynchronisation, SynchronisationStageFlags.AllGPUOperations);
            cmd.DispatchCompute(compute, m_applyBoundaryConditionsKernel,
                numberOfVoxels.x / 4 + 1, numberOfVoxels.y / 4 + 1, numberOfVoxels.z / 4 + 1);
            cmd.WaitOnAsyncGraphicsFence(fence);
        }

        private void AddBufferSwapCommand(CommandBuffer cmd, ChunkFluidData chunkData)
        {
            // Buffer swap happens on the CPU side, so we'll store this as a special command
            chunkData.AddBufferSwapCommand();
        }

        // Public API methods
        public void InitializeChunkFluidVolume(ComputeBuffer fluidVolumeBuffer, ComputeBuffer fluidVolumeBackBuffer,
            float3 chunkWorldPosition)
        {
            if (!m_initialized || fluidVolumeBuffer == null || fluidVolumeBackBuffer == null)
                return;

            var compute = m_voxelConfig.FluidSimulationConfig.Compute;
            var cmd = new CommandBuffer { name = "InitializeFluidVolume" };

            SetGlobalParametersToCommand(cmd, compute, chunkWorldPosition);
            cmd.SetComputeBufferParam(compute, m_initializeFluidVolumeKernel, "fluidVolumeBackBuffer",
                fluidVolumeBackBuffer);
            cmd.DispatchCompute(compute, m_initializeFluidVolumeKernel,
                m_voxelConfig.VoxelVolumeConfig.NumberOfVoxels.x / 4 + 1,
                m_voxelConfig.VoxelVolumeConfig.NumberOfVoxels.y / 4 + 1,
                m_voxelConfig.VoxelVolumeConfig.NumberOfVoxels.z / 4 + 1);

            cmd.SetComputeBufferParam(compute, m_initializeFluidVolumeKernel, "fluidVolumeBackBuffer",
                fluidVolumeBuffer);
            cmd.DispatchCompute(compute, m_initializeFluidVolumeKernel,
                m_voxelConfig.VoxelVolumeConfig.NumberOfVoxels.x / 4 + 1,
                m_voxelConfig.VoxelVolumeConfig.NumberOfVoxels.y / 4 + 1,
                m_voxelConfig.VoxelVolumeConfig.NumberOfVoxels.z / 4 + 1);

            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();
        }

        public void SimulateChunkFluidStep(ChunkFluidData chunkData)
        {
            if (!m_initialized || !m_enableSimulation || !chunkData.IsValid())
                return;

            // Add chunk to pending simulation queue
            if (!m_activeChunks.Contains(chunkData) && !m_pendingChunks.Contains(chunkData))
            {
                m_pendingChunks.Enqueue(chunkData);
            }
        }

        public void ConvertChunkFluidToVoxelVolume(ComputeBuffer fluidVolumeBuffer,
            ComputeBuffer outputVoxelVolumeBuffer, float3 chunkWorldPosition)
        {
            if (!m_initialized || fluidVolumeBuffer == null || outputVoxelVolumeBuffer == null)
                return;

            var compute = m_voxelConfig.FluidSimulationConfig.Compute;
            var cmd = new CommandBuffer { name = "ConvertFluidToVoxel" };

            SetGlobalParametersToCommand(cmd, compute, chunkWorldPosition);
            cmd.SetComputeBufferParam(compute, m_convertFluidToVoxelVolumeKernel, "fluidVolume", fluidVolumeBuffer);
            cmd.SetComputeBufferParam(compute, m_convertFluidToVoxelVolumeKernel, "outputVoxelVolume",
                outputVoxelVolumeBuffer);
            cmd.DispatchCompute(compute, m_convertFluidToVoxelVolumeKernel,
                m_voxelConfig.VoxelVolumeConfig.NumberOfVoxels.x / 4 + 1,
                m_voxelConfig.VoxelVolumeConfig.NumberOfVoxels.y / 4 + 1,
                m_voxelConfig.VoxelVolumeConfig.NumberOfVoxels.z / 4 + 1);

            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();
        }

        public void AddFluidSourceToChunk(ChunkFluidData chunkData, FluidSourceData sourceData)
        {
            if (!m_initialized || !chunkData.IsValid())
                return;

            var compute = m_voxelConfig.FluidSimulationConfig.Compute;
            var cmd = new CommandBuffer { name = "AddFluidSource" };

            SetGlobalParametersToCommand(cmd, compute, chunkData.WorldPosition);
            SetFluidSourceParametersToCommand(cmd, compute, sourceData);
            cmd.SetComputeBufferParam(compute, m_addFluidSourceKernel, "fluidVolume", chunkData.FluidVolumeBuffer);
            cmd.SetComputeBufferParam(compute, m_addFluidSourceKernel, "fluidVolumeBackBuffer",
                chunkData.FluidVolumeBackBuffer);
            cmd.DispatchCompute(compute, m_addFluidSourceKernel,
                m_voxelConfig.VoxelVolumeConfig.NumberOfVoxels.x / 4 + 1,
                m_voxelConfig.VoxelVolumeConfig.NumberOfVoxels.y / 4 + 1,
                m_voxelConfig.VoxelVolumeConfig.NumberOfVoxels.z / 4 + 1);

            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();
        }

        // Helper methods
        private void InitializeKernels()
        {
            var compute = m_voxelConfig.FluidSimulationConfig.Compute;

            m_initializeFluidVolumeKernel = compute.FindKernel("InitializeFluidVolume");
            m_advectionKernel = compute.FindKernel("Advection");
            m_externalForcesKernel = compute.FindKernel("ExternalForces");
            m_computeDivergenceKernel = compute.FindKernel("ComputeDivergence");
            m_pressureProjectionKernel = compute.FindKernel("PressureProjection");
            m_applyBoundaryConditionsKernel = compute.FindKernel("ApplyBoundaryConditions");
            m_addFluidSourceKernel = compute.FindKernel("AddFluidSource");
            m_updateBoundariesFromSolidsKernel = compute.FindKernel("UpdateBoundariesFromSolids");
            m_convertFluidToVoxelVolumeKernel = compute.FindKernel("ConvertFluidToVoxelVolume");
        }

        private void CreateSharedBuffers()
        {
            ReleaseSharedBuffers();
            int voxelCount = m_voxelConfig.VoxelVolumeConfig.VoxelCount;
            m_divergenceBuffer = new ComputeBuffer(voxelCount, sizeof(float));
        }

        private void ReleaseSharedBuffers()
        {
            m_divergenceBuffer?.Release();
            m_divergenceBuffer = null;
        }

        private void SetGlobalParametersToCommand(CommandBuffer cmd, ComputeShader compute, float3 chunkWorldPosition)
        {
            cmd.SetComputeFloatParam(compute, "deltaTime", m_timeStep);
            cmd.SetComputeFloatParam(compute, "viscosity", m_voxelConfig.FluidSimulationConfig.Viscosity);
            cmd.SetComputeVectorParam(compute, "gravity", m_voxelConfig.FluidSimulationConfig.Gravity);
            cmd.SetComputeFloatParam(compute, "fluidDensityThreshold",
                m_voxelConfig.FluidSimulationConfig.FluidDensityThreshold);
            cmd.SetComputeFloatParam(compute, "minFluidDensity", m_voxelConfig.FluidSimulationConfig.MinFluidDensity);
            cmd.SetComputeFloatParam(compute, "dampingFactor", m_voxelConfig.FluidSimulationConfig.DampingFactor);

            var volumeConfig = m_voxelConfig.VoxelVolumeConfig;
            cmd.SetComputeIntParams(compute, "numberOfVoxels", volumeConfig.NumberOfVoxels.x,
                volumeConfig.NumberOfVoxels.y, volumeConfig.NumberOfVoxels.z);
            cmd.SetComputeFloatParam(compute, "voxelSpacing", volumeConfig.VoxelSpacing);
            cmd.SetComputeVectorParam(compute, "fluidVolumeToWorldSpaceOffset", (Vector3)chunkWorldPosition);
        }

        private void SetFluidSourceParametersToCommand(CommandBuffer cmd, ComputeShader compute,
            FluidSourceData sourceData)
        {
            cmd.SetComputeVectorParam(compute, "sourcePosition", sourceData.Position);
            cmd.SetComputeVectorParam(compute, "sourceVelocity", sourceData.Velocity);
            cmd.SetComputeFloatParam(compute, "sourceRadius", sourceData.Radius);
            cmd.SetComputeFloatParam(compute, "sourceAmount", sourceData.Amount);
            cmd.SetComputeIntParam(compute, "sourceMaterial", (int)sourceData.Material);
        }
    }
}