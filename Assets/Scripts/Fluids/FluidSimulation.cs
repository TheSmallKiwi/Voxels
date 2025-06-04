using System.Collections.Generic;
using Tuntenfisch.Extensions;
using Tuntenfisch.Voxels;
using Tuntenfisch.Voxels.Materials;
using Unity.Mathematics;
using UnityEngine;

namespace Tuntenfisch.Fluids
{
    /// <summary>
    /// Manages fluid simulation across multiple chunks by dispatching compute shaders
    /// on chunk-provided RenderTextures. Acts as a centralized service for fluid operations.
    /// </summary>
    [RequireComponent(typeof(VoxelConfig))]
    public class FluidSimulation : MonoBehaviour
    {
        [Header("Simulation Settings")] [SerializeField]
        private bool m_enableSimulation = true;

        [SerializeField] private float m_timeStep = 0.016f;
        [SerializeField] private int m_pressureIterations = 15;
        [SerializeField] private float m_viscosity = 0.01f;
        [SerializeField] private Vector3 m_gravity = new Vector3(0, -9.81f, 0);
        [SerializeField] private float m_densityDissipation = 0.999f;
        [SerializeField] private float m_velocityDissipation = 0.995f;

        private VoxelConfig m_voxelConfig;
        private ComputeShader m_fluidCompute;

        // Compute kernel IDs
        private int m_initializeFluidKernel;
        private int m_addSourcesKernel;
        private int m_advectionKernel;
        private int m_diffusionKernel;
        private int m_computeDivergenceKernel;
        private int m_pressureSolveKernel;
        private int m_pressureProjectionKernel;
        private int m_applyBoundariesKernel;
        private int m_updateBoundariesFromSolidsKernel;

        private bool m_initialized = false;

        public bool IsSimulationEnabled => m_enableSimulation && m_initialized;

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
            m_initialized = true;
        }

        private void InitializeKernels()
        {
            m_fluidCompute = m_voxelConfig.FluidSimulationConfig.Compute;

            if (m_fluidCompute == null)
            {
                Debug.LogError("Fluid compute shader not found in FluidSimulationConfig!");
                return;
            }

            // Find all kernel IDs
            m_initializeFluidKernel = m_fluidCompute.FindKernel("InitializeFluid");
            m_addSourcesKernel = m_fluidCompute.FindKernel("AddSources");
            m_advectionKernel = m_fluidCompute.FindKernel("Advection");
            m_diffusionKernel = m_fluidCompute.FindKernel("Diffusion");
            m_computeDivergenceKernel = m_fluidCompute.FindKernel("ComputeDivergence");
            m_pressureSolveKernel = m_fluidCompute.FindKernel("PressureSolve");
            m_pressureProjectionKernel = m_fluidCompute.FindKernel("PressureProjection");
            m_applyBoundariesKernel = m_fluidCompute.FindKernel("ApplyBoundaries");
            m_updateBoundariesFromSolidsKernel = m_fluidCompute.FindKernel("UpdateBoundariesFromSolids");
        }

        /// <summary>
        /// Initialize fluid textures for a chunk
        /// </summary>
        public void InitializeChunkFluidTextures(ChunkFluidData fluidData)
        {
            if (!IsSimulationEnabled || !fluidData.IsValid())
                return;

            SetGlobalParameters(fluidData.WorldPosition);
            BindTexturesForKernel(m_initializeFluidKernel, fluidData.FluidTextures, fluidData.SolidVoxelBuffer, true);

            m_fluidCompute.Dispatch(m_initializeFluidKernel, m_voxelConfig.VoxelVolumeConfig.NumberOfVoxels);
            // Debug.Log($"Initialized fluid textures for chunk at position {fluidData.WorldPosition}");
        }

        /// <summary>
        /// Run a complete simulation step for a chunk
        /// </summary>
        public void SimulateChunkFluidStep(ChunkFluidData fluidData)
        {
            if (!IsSimulationEnabled || !fluidData.IsValid())
                return;

            SetGlobalParameters(fluidData.WorldPosition);

            // 1. Update boundaries from solid geometry
            UpdateBoundariesFromSolids(fluidData);

            // 2. Add fluid sources if present
            if (fluidData.FluidSource != null)
            {
                AddFluidSource(fluidData);
            }

            // 3. Advection step
            ExecuteAdvection(fluidData);

            // 4. Diffusion (viscosity) - optional
            if (m_viscosity > 0.001f)
            {
                ExecuteDiffusion(fluidData);
            }

            // 5. Pressure projection (incompressibility)
            ExecutePressureProjection(fluidData);

            // 6. Apply boundary conditions
            ApplyBoundaryConditions(fluidData);

            // 7. Swap read/write textures
            fluidData.FluidTextures.SwapTextures();
        }

        /// <summary>
        /// Update fluid boundaries based on solid voxel geometry
        /// </summary>
        public void UpdateBoundariesFromSolids(ChunkFluidData fluidData)
        {
            if (!IsSimulationEnabled || !fluidData.IsValid())
                return;

            BindTexturesForKernel(m_updateBoundariesFromSolidsKernel, fluidData.FluidTextures,
                fluidData.SolidVoxelBuffer, true);
            m_fluidCompute.Dispatch(m_updateBoundariesFromSolidsKernel, m_voxelConfig.VoxelVolumeConfig.NumberOfVoxels);
            // Debug.Log($"Updated fluid boundaries from solids for chunk at position {fluidData.WorldPosition}");
        }

        /// <summary>
        /// Add a fluid source to the simulation
        /// </summary>
        public void AddFluidSource(ChunkFluidData fluidData)
        {
            if (!IsSimulationEnabled || !fluidData.IsValid())
                return;

            SetFluidSourceParameters(fluidData.FluidSource);
            BindTexturesForKernel(m_addSourcesKernel, fluidData.FluidTextures, fluidData.SolidVoxelBuffer, true);

            m_fluidCompute.Dispatch(m_addSourcesKernel, m_voxelConfig.VoxelVolumeConfig.NumberOfVoxels);
            // Debug.Log($"Added fluid source to chunk at position {fluidData.WorldPosition}");
        }

        private void ExecuteAdvection(ChunkFluidData fluidData)
        {
            BindTexturesForKernel(m_advectionKernel, fluidData.FluidTextures, fluidData.SolidVoxelBuffer, true);
            m_fluidCompute.Dispatch(m_advectionKernel, m_voxelConfig.VoxelVolumeConfig.NumberOfVoxels);
            // Debug.Log($"Completed fluid advection for chunk at position {fluidData.WorldPosition}");
        }

        private void ExecuteDiffusion(ChunkFluidData fluidData)
        {
            BindTexturesForKernel(m_diffusionKernel, fluidData.FluidTextures, fluidData.SolidVoxelBuffer, true);
            m_fluidCompute.Dispatch(m_diffusionKernel, m_voxelConfig.VoxelVolumeConfig.NumberOfVoxels);
            fluidData.FluidTextures.SwapTextures(); // Swap after diffusion
        }

        private void ExecutePressureProjection(ChunkFluidData fluidData)
        {
            var numberOfVoxels = m_voxelConfig.VoxelVolumeConfig.NumberOfVoxels;
            var textures = fluidData.FluidTextures;

            // Compute divergence
            BindTexturesForKernel(m_computeDivergenceKernel, textures, fluidData.SolidVoxelBuffer, false);
            m_fluidCompute.Dispatch(m_computeDivergenceKernel, numberOfVoxels);

            // Iterative pressure solve
            for (int i = 0; i < m_pressureIterations; i++)
            {
                BindTexturesForKernel(m_pressureSolveKernel, textures, fluidData.SolidVoxelBuffer, true);
                m_fluidCompute.Dispatch(m_pressureSolveKernel, numberOfVoxels);
                textures.SwapPressureTextures(); // Only swap pressure textures
            }

            // Apply pressure gradient to velocity
            BindTexturesForKernel(m_pressureProjectionKernel, textures, fluidData.SolidVoxelBuffer, true);
            m_fluidCompute.Dispatch(m_pressureProjectionKernel, numberOfVoxels);
        }

        private void ApplyBoundaryConditions(ChunkFluidData fluidData)
        {
            BindTexturesForKernel(m_applyBoundariesKernel, fluidData.FluidTextures, fluidData.SolidVoxelBuffer, true);
            m_fluidCompute.Dispatch(m_applyBoundariesKernel, m_voxelConfig.VoxelVolumeConfig.NumberOfVoxels);
        }

        private void BindTexturesForKernel(int kernelId, ChunkFluidTextures fluidData,
            ComputeBuffer solidVoxelBuffer, bool bindWriteTextures)
        {
            // Bind read textures
            m_fluidCompute.SetTexture(kernelId, "velocityRead", fluidData.VelocityRead);
            m_fluidCompute.SetTexture(kernelId, "densityRead", fluidData.DensityRead);
            m_fluidCompute.SetTexture(kernelId, "pressureRead", fluidData.PressureRead);

            if (bindWriteTextures)
            {
                // Bind write textures
                m_fluidCompute.SetTexture(kernelId, "velocityWrite", fluidData.VelocityWrite);
                m_fluidCompute.SetTexture(kernelId, "densityWrite", fluidData.DensityWrite);
                m_fluidCompute.SetTexture(kernelId, "pressureWrite", fluidData.PressureWrite);
            }

            // Bind divergence texture (always writable)
            m_fluidCompute.SetTexture(kernelId, "divergence", fluidData.Divergence);

            // Bind solid voxel buffer if provided
            if (solidVoxelBuffer != null)
            {
                m_fluidCompute.SetBuffer(kernelId, "solidVoxelVolume", solidVoxelBuffer);
            }
        }

        private void SetGlobalParameters(float3 chunkWorldPosition)
        {
            var volumeConfig = m_voxelConfig.VoxelVolumeConfig;

            // Simulation parameters
            m_fluidCompute.SetFloat("deltaTime", m_timeStep);
            m_fluidCompute.SetFloat("viscosity", m_viscosity);
            m_fluidCompute.SetVector("gravity", m_gravity);
            m_fluidCompute.SetFloat("densityDissipation", m_densityDissipation);
            m_fluidCompute.SetFloat("velocityDissipation", m_velocityDissipation);

            // Volume parameters
            m_fluidCompute.SetInts("dimensions", volumeConfig.NumberOfVoxels.x,
                volumeConfig.NumberOfVoxels.y, volumeConfig.NumberOfVoxels.z);
            m_fluidCompute.SetFloat("voxelSize", volumeConfig.VoxelSpacing);
        }

        private void SetFluidSourceParameters(FluidSourceData sourceData)
        {
            m_fluidCompute.SetVector("sourcePosition", sourceData.Position);
            m_fluidCompute.SetVector("sourceVelocity", sourceData.Velocity);
            m_fluidCompute.SetFloat("sourceRadius", sourceData.Radius);
            m_fluidCompute.SetFloat("sourceDensity", sourceData.Amount);
        }
    }

    /// <summary>
    /// Container for chunk-owned fluid simulation textures
    /// </summary>
    [System.Serializable]
    public class ChunkFluidTextures
    {
        public RenderTexture VelocityRead { get; set; }
        public RenderTexture VelocityWrite { get; set; }
        public RenderTexture DensityRead { get; set; }
        public RenderTexture DensityWrite { get; set; }
        public RenderTexture PressureRead { get; set; }
        public RenderTexture PressureWrite { get; set; }
        public RenderTexture Divergence { get; set; }

        public bool IsValid()
        {
            return VelocityRead && VelocityWrite &&
                   DensityRead && DensityWrite &&
                   PressureRead && PressureWrite &&
                   Divergence;
        }

        public void SwapTextures()
        {
            SwapVelocityTextures();
            SwapDensityTextures();
            SwapPressureTextures();
        }

        public void SwapVelocityTextures()
        {
            (VelocityRead, VelocityWrite) = (VelocityWrite, VelocityRead);
        }

        public void SwapDensityTextures()
        {
            (DensityRead, DensityWrite) = (DensityWrite, DensityRead);
        }

        public void SwapPressureTextures()
        {
            (PressureRead, PressureWrite) = (PressureWrite, PressureRead);
        }

        public void Release()
        {
            VelocityRead?.Release();
            VelocityWrite?.Release();
            DensityRead?.Release();
            DensityWrite?.Release();
            PressureRead?.Release();
            PressureWrite?.Release();
            Divergence?.Release();
        }
    }

    /// <summary>
    /// Helper class for creating fluid simulation textures
    /// </summary>
    public static class FluidTextureFactory
    {
        public static ChunkFluidTextures CreateFluidTextures(int3 dimensions)
        {
            var textures = new ChunkFluidTextures();

            // Create velocity textures (RGB for x,y,z components)
            textures.VelocityRead = CreateTexture3D(dimensions, RenderTextureFormat.ARGBFloat, true);
            textures.VelocityWrite = CreateTexture3D(dimensions, RenderTextureFormat.ARGBFloat, true);

            // Create density textures (single channel)
            textures.DensityRead = CreateTexture3D(dimensions, RenderTextureFormat.RFloat, true);
            textures.DensityWrite = CreateTexture3D(dimensions, RenderTextureFormat.RFloat, true);

            // Create pressure textures (single channel)
            textures.PressureRead = CreateTexture3D(dimensions, RenderTextureFormat.RFloat, true);
            textures.PressureWrite = CreateTexture3D(dimensions, RenderTextureFormat.RFloat, true);

            // Create divergence texture (single channel, always writable)
            textures.Divergence = CreateTexture3D(dimensions, RenderTextureFormat.RFloat, true);

            return textures;
        }

        private static RenderTexture CreateTexture3D(int3 dimensions, RenderTextureFormat format,
            bool enableRandomWrite)
        {
            var texture = new RenderTexture(dimensions.x, dimensions.y, 0, format)
            {
                dimension = UnityEngine.Rendering.TextureDimension.Tex3D,
                volumeDepth = dimensions.z,
                enableRandomWrite = enableRandomWrite,
                useMipMap = false,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };

            texture.Create();
            return texture;
        }
    }
}