
using Tuntenfisch.Extensions;
using Tuntenfisch.Voxels;
using Tuntenfisch.Voxels.Materials;
using Unity.Mathematics;
using UnityEngine;

namespace Tuntenfisch.Fluids
{
    /// <summary>
    /// Manages fluid simulation across multiple chunks by operating on their individual fluid volume buffers.
    /// Acts as a centralized service that chunks can request fluid simulation operations from.
    /// </summary>
    [RequireComponent(typeof(VoxelConfig))]
    public class FluidSimulation : MonoBehaviour
    {
        [Header("Simulation Parameters")]
        [SerializeField] private bool m_enableSimulation = true;
        [SerializeField] private float m_timeStep = 0.016f;
        [SerializeField] private int m_pressureIterations = 20;

        private VoxelConfig m_voxelConfig;
        private ComputeBuffer m_divergenceBuffer; // Shared temporary buffer
        
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
        
        private bool m_initialized = false;
        
        public bool IsSimulationEnabled => m_enableSimulation;

        private void Awake()
        {
            m_voxelConfig = GetComponent<VoxelConfig>();

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

        private void OnDestroy()
        {
            ReleaseSharedBuffers();
        }

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
            
            // Only create shared temporary buffers
            m_divergenceBuffer = new ComputeBuffer(voxelCount, sizeof(float));
        }

        private void ReleaseSharedBuffers()
        {
            m_divergenceBuffer?.Release();
            m_divergenceBuffer = null;
        }

        // Initialize a chunk's fluid volume buffers
        public void InitializeChunkFluidVolume(ComputeBuffer fluidVolumeBuffer, ComputeBuffer fluidVolumeBackBuffer, float3 chunkWorldPosition)
        {
            if (!m_initialized || fluidVolumeBuffer == null || fluidVolumeBackBuffer == null)
                return;

            var compute = m_voxelConfig.FluidSimulationConfig.Compute;
            
            SetGlobalParameters(compute, chunkWorldPosition);
            
            compute.SetBuffer(m_initializeFluidVolumeKernel, "fluidVolume", fluidVolumeBuffer);
            compute.SetBuffer(m_initializeFluidVolumeKernel, "fluidVolumeBackBuffer", fluidVolumeBackBuffer);
            compute.Dispatch(m_initializeFluidVolumeKernel, m_voxelConfig.VoxelVolumeConfig.NumberOfVoxels);
        }

        // Simulate one fluid step for a specific chunk
        public void SimulateChunkFluidStep(ChunkFluidData chunkData)
        {
            if (!m_initialized || !m_enableSimulation || !chunkData.IsValid())
                return;

            var compute = m_voxelConfig.FluidSimulationConfig.Compute;
            var numberOfVoxels = m_voxelConfig.VoxelVolumeConfig.NumberOfVoxels;
            
            // Set global parameters for this chunk
            SetGlobalParameters(compute, chunkData.WorldPosition);
            
            // Set fluid source parameters if this chunk has one
            if (chunkData.HasFluidSource)
            {
                SetFluidSourceParameters(compute, chunkData.FluidSource);
            }

            // 1. Update boundaries from solid geometry
            if (chunkData.VoxelVolumeBuffer != null)
            {
                compute.SetBuffer(m_updateBoundariesFromSolidsKernel, "voxelVolume", chunkData.VoxelVolumeBuffer);
                compute.SetBuffer(m_updateBoundariesFromSolidsKernel, "fluidVolume", chunkData.FluidVolumeBuffer);
                compute.SetBuffer(m_updateBoundariesFromSolidsKernel, "fluidVolumeBackBuffer", chunkData.FluidVolumeBackBuffer);
                compute.Dispatch(m_updateBoundariesFromSolidsKernel, numberOfVoxels);
            }

            // 2. Add fluid sources
            if (chunkData.HasFluidSource)
            {
                compute.SetBuffer(m_addFluidSourceKernel, "fluidVolume", chunkData.FluidVolumeBuffer);
                compute.SetBuffer(m_addFluidSourceKernel, "fluidVolumeBackBuffer", chunkData.FluidVolumeBackBuffer);
                compute.Dispatch(m_addFluidSourceKernel, numberOfVoxels);
            }

            // 3. Advection step
            compute.SetBuffer(m_advectionKernel, "fluidVolume", chunkData.FluidVolumeBuffer);
            compute.SetBuffer(m_advectionKernel, "fluidVolumeBackBuffer", chunkData.FluidVolumeBackBuffer);
            compute.Dispatch(m_advectionKernel, numberOfVoxels);
            chunkData.SwapBuffers();

            // 4. Apply external forces
            compute.SetBuffer(m_externalForcesKernel, "fluidVolume", chunkData.FluidVolumeBuffer);
            compute.SetBuffer(m_externalForcesKernel, "fluidVolumeBackBuffer", chunkData.FluidVolumeBackBuffer);
            compute.Dispatch(m_externalForcesKernel, numberOfVoxels);

            // 5. Pressure projection (multiple iterations)
            for (int i = 0; i < m_pressureIterations; i++)
            {
                // Compute divergence
                compute.SetBuffer(m_computeDivergenceKernel, "fluidVolume", chunkData.FluidVolumeBuffer);
                compute.SetBuffer(m_computeDivergenceKernel, "divergenceBuffer", m_divergenceBuffer);
                compute.Dispatch(m_computeDivergenceKernel, numberOfVoxels);

                // Update pressure and apply pressure gradient
                compute.SetBuffer(m_pressureProjectionKernel, "fluidVolume", chunkData.FluidVolumeBuffer);
                compute.SetBuffer(m_pressureProjectionKernel, "fluidVolumeBackBuffer", chunkData.FluidVolumeBackBuffer);
                compute.SetBuffer(m_pressureProjectionKernel, "divergenceBuffer", m_divergenceBuffer);
                compute.Dispatch(m_pressureProjectionKernel, numberOfVoxels);
                chunkData.SwapBuffers();
            }

            // 6. Apply boundary conditions
            compute.SetBuffer(m_applyBoundaryConditionsKernel, "fluidVolume", chunkData.FluidVolumeBuffer);
            compute.SetBuffer(m_applyBoundaryConditionsKernel, "fluidVolumeBackBuffer", chunkData.FluidVolumeBackBuffer);
            compute.Dispatch(m_applyBoundaryConditionsKernel, numberOfVoxels);
        }

        // Convert chunk fluid to voxel volume for mesh generation
        public void ConvertChunkFluidToVoxelVolume(ComputeBuffer fluidVolumeBuffer, ComputeBuffer outputVoxelVolumeBuffer, float3 chunkWorldPosition)
        {
            if (!m_initialized || fluidVolumeBuffer == null || outputVoxelVolumeBuffer == null)
                return;

            var compute = m_voxelConfig.FluidSimulationConfig.Compute;
            
            SetGlobalParameters(compute, chunkWorldPosition);
            
            compute.SetBuffer(m_convertFluidToVoxelVolumeKernel, "fluidVolume", fluidVolumeBuffer);
            compute.SetBuffer(m_convertFluidToVoxelVolumeKernel, "outputVoxelVolume", outputVoxelVolumeBuffer);
            compute.Dispatch(m_convertFluidToVoxelVolumeKernel, m_voxelConfig.VoxelVolumeConfig.NumberOfVoxels);
        }

        // Add fluid source to a specific chunk
        public void AddFluidSourceToChunk(ChunkFluidData chunkData, FluidSourceData sourceData)
        {
            if (!m_initialized || !chunkData.IsValid())
                return;

            var compute = m_voxelConfig.FluidSimulationConfig.Compute;
            
            SetGlobalParameters(compute, chunkData.WorldPosition);
            SetFluidSourceParameters(compute, sourceData);
            
            compute.SetBuffer(m_addFluidSourceKernel, "fluidVolume", chunkData.FluidVolumeBuffer);
            compute.SetBuffer(m_addFluidSourceKernel, "fluidVolumeBackBuffer", chunkData.FluidVolumeBackBuffer);
            compute.Dispatch(m_addFluidSourceKernel, m_voxelConfig.VoxelVolumeConfig.NumberOfVoxels);
        }

        private void SetGlobalParameters(ComputeShader compute, float3 chunkWorldPosition)
        {
            // Set simulation parameters
            compute.SetFloat("deltaTime", m_timeStep);
            compute.SetFloat("viscosity", m_voxelConfig.FluidSimulationConfig.Viscosity);
            compute.SetVector("gravity", m_voxelConfig.FluidSimulationConfig.Gravity);
            compute.SetFloat("fluidDensityThreshold", m_voxelConfig.FluidSimulationConfig.FluidDensityThreshold);

            // Set voxel volume parameters
            var volumeConfig = m_voxelConfig.VoxelVolumeConfig;
            compute.SetInts("numberOfVoxels", volumeConfig.NumberOfVoxels.x, volumeConfig.NumberOfVoxels.y, volumeConfig.NumberOfVoxels.z);
            compute.SetFloat("voxelSpacing", volumeConfig.VoxelSpacing);

            // Set chunk world position offset
            compute.SetVector("fluidVolumeToWorldSpaceOffset", (Vector3)chunkWorldPosition);
        }

        private void SetFluidSourceParameters(ComputeShader compute, FluidSourceData sourceData)
        {
            compute.SetVector("sourcePosition", sourceData.Position);
            compute.SetVector("sourceVelocity", sourceData.Velocity);
            compute.SetFloat("sourceRadius", sourceData.Radius);
            compute.SetFloat("sourceAmount", sourceData.Amount);
            compute.SetInt("sourceMaterial", (int)sourceData.Material);
        }
    }

    // Data structures for passing chunk fluid information
    [System.Serializable]
    public class ChunkFluidData
    {
        public ComputeBuffer FluidVolumeBuffer { get; set; }
        public ComputeBuffer FluidVolumeBackBuffer { get; set; }
        public ComputeBuffer VoxelVolumeBuffer { get; set; }
        public ComputeBuffer TempVoxelVolumeBuffer { get; set; }
        public float3 WorldPosition { get; set; }
        public bool HasFluidSource { get; set; }
        public FluidSourceData FluidSource { get; set; }

        public bool IsValid()
        {
            return FluidVolumeBuffer != null && FluidVolumeBackBuffer != null;
        }

        public void SwapBuffers()
        {
            (FluidVolumeBuffer, FluidVolumeBackBuffer) = (FluidVolumeBackBuffer, FluidVolumeBuffer);
        }
        
        public void DebugOutput()
        {
            Debug.Log($"World Position: {WorldPosition}");
            Debug.Log($"Fluid Volume Buffer: {FluidVolumeBuffer.count}");
            Debug.Log($"Fluid Volume Back Buffer: {FluidVolumeBackBuffer.count}");
            Debug.Log($"Voxel Volume Buffer: {VoxelVolumeBuffer.count}");
            Debug.Log($"Temp Voxel Volume Buffer: {TempVoxelVolumeBuffer.count}");
            Debug.Log($"Has Fluid Source: {HasFluidSource}");
            Debug.Log($"Fluid Source: {FluidSource}");
        }
    }

    [System.Serializable]
    public class FluidSourceData
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public float Radius;
        public float Amount;
        public MaterialIndex Material;

        public FluidSourceData(Vector3 position, Vector3 velocity, float radius, float amount, MaterialIndex material = MaterialIndex.Water)
        {
            Position = position;
            Velocity = velocity;
            Radius = radius;
            Amount = amount;
            Material = material;
        }
    }
}