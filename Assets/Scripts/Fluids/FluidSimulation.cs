using System;
using System.Collections.Generic;
using Tuntenfisch.Extensions;
using Tuntenfisch.Voxels;
using Tuntenfisch.Voxels.Volume;
using Unity.Mathematics;
using UnityEngine;

namespace Tuntenfisch.Fluids
{
    /// <summary>
    /// Manages fluid simulation using compute shaders. Handles the execution of fluid simulation
    /// steps including advection, forces, pressure projection, and boundary conditions.
    /// </summary>
    /// <remarks>
    /// This component works alongside the voxel system to simulate fluid dynamics within
    /// the same voxel grid. It requires a VoxelConfig component for shared parameters
    /// and coordinate system information.
    /// </remarks>
    [RequireComponent(typeof(VoxelConfig))]
    public class FluidSimulation : MonoBehaviour
    {
        [Header("Simulation")] [SerializeField]
        private bool m_enableSimulation = true;

        [SerializeField] private float m_timeStep = 0.016f; // ~60 FPS
        [SerializeField] private int m_pressureIterations = 20;

        [Header("Fluid Source")] [SerializeField]
        private bool m_enableFluidSource = false;

        [SerializeField] private Transform m_sourceTransform;
        [SerializeField] private float m_sourceRadius = 2.0f;
        [SerializeField] private float m_sourceAmount = 1.0f;
        [SerializeField] private Vector3 m_sourceVelocity = Vector3.up;
        [SerializeField] private uint m_sourceMaterial = 4; // Water material index

        private VoxelConfig m_voxelConfig;
        private ComputeBuffer m_fluidVolumeBuffer;
        private ComputeBuffer m_fluidVolumeBackBuffer;
        private ComputeBuffer m_divergenceBuffer;
        private ComputeBuffer m_tempVoxelVolumeBuffer; // For mesh generation

        // Compute shader kernel IDs
        private int m_advectionKernel;
        private int m_externalForcesKernel;
        private int m_computeDivergenceKernel;
        private int m_pressureProjectionKernel;
        private int m_applyBoundaryConditionsKernel;
        private int m_addFluidSourceKernel;
        private int m_updateBoundariesFromSolidsKernel;
        private int m_convertFluidToVoxelVolumeKernel;

        private float m_accumulatedTime = 0f;
        private bool m_buffersInitialized = false;

        public ComputeBuffer FluidVolumeBuffer => m_fluidVolumeBuffer;
        public ComputeBuffer TempVoxelVolumeBuffer => m_tempVoxelVolumeBuffer;
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
            m_voxelConfig.VoxelVolumeConfig.OnDirtied += HandleVoxelVolumeConfigChanged;
            m_voxelConfig.FluidSimulationConfig.OnDirtied += HandleFluidConfigChanged;
        }

        private void Start()
        {
            CreateBuffers();
            ApplyFluidSimulationConfig();
            InitializeFluidVolume();
        }

        private void Update()
        {
            if (!m_enableSimulation || !m_buffersInitialized)
                return;

            m_accumulatedTime += Time.deltaTime;

            // Fixed timestep simulation
            while (m_accumulatedTime >= m_timeStep)
            {
                SimulateFluidStep();
                m_accumulatedTime -= m_timeStep;
            }
        }

        private void OnDestroy()
        {
            if (m_voxelConfig != null)
            {
                m_voxelConfig.VoxelVolumeConfig.OnDirtied -= HandleVoxelVolumeConfigChanged;
                m_voxelConfig.FluidSimulationConfig.OnDirtied -= HandleFluidConfigChanged;
            }

            ReleaseBuffers();
        }

        private void OnValidate()
        {
            if (Application.isPlaying && gameObject.activeSelf && m_voxelConfig != null)
            {
                ApplyFluidSimulationConfig();
            }
        }

        private void InitializeKernels()
        {
            var compute = m_voxelConfig.FluidSimulationConfig.Compute;

            m_advectionKernel = compute.FindKernel("Advection");
            m_externalForcesKernel = compute.FindKernel("ExternalForces");
            m_computeDivergenceKernel = compute.FindKernel("ComputeDivergence");
            m_pressureProjectionKernel = compute.FindKernel("PressureProjection");
            m_applyBoundaryConditionsKernel = compute.FindKernel("ApplyBoundaryConditions");
            m_addFluidSourceKernel = compute.FindKernel("AddFluidSource");
            m_updateBoundariesFromSolidsKernel = compute.FindKernel("UpdateBoundariesFromSolids");
            m_convertFluidToVoxelVolumeKernel = compute.FindKernel("ConvertFluidToVoxelVolume");
        }

        private void CreateBuffers()
        {
            ReleaseBuffers();

            int voxelCount = m_voxelConfig.VoxelVolumeConfig.VoxelCount;

            // Main fluid volume buffer (double buffered for advection)
            m_fluidVolumeBuffer = new ComputeBuffer(voxelCount, GetFluidVoxelSizeInBytes());
            m_fluidVolumeBackBuffer = new ComputeBuffer(voxelCount, GetFluidVoxelSizeInBytes());

            // Temporary buffer for divergence computation
            m_divergenceBuffer = new ComputeBuffer(voxelCount, sizeof(float));

            // Temporary voxel volume buffer for mesh generation
            m_tempVoxelVolumeBuffer = new ComputeBuffer(voxelCount, 2 * sizeof(uint)); // PackedVoxel size

            m_buffersInitialized = true;
        }

        private void ReleaseBuffers()
        {
            m_fluidVolumeBuffer?.Release();
            m_fluidVolumeBackBuffer?.Release();
            m_divergenceBuffer?.Release();
            m_tempVoxelVolumeBuffer?.Release();

            m_fluidVolumeBuffer = null;
            m_fluidVolumeBackBuffer = null;
            m_divergenceBuffer = null;
            m_tempVoxelVolumeBuffer = null;

            m_buffersInitialized = false;
        }

        private void ApplyFluidSimulationConfig()
        {
            if (!m_buffersInitialized)
                return;

            var compute = m_voxelConfig.FluidSimulationConfig.Compute;
            var config = m_voxelConfig.FluidSimulationConfig;

            // Set global parameters
            compute.SetFloat("deltaTime", m_timeStep);
            compute.SetFloat("viscosity", config.Viscosity);
            compute.SetVector("gravity", config.Gravity);
            compute.SetFloat("fluidDensityThreshold", config.FluidDensityThreshold);

            // Set voxel volume parameters (shared with VoxelVolume)
            var volumeConfig = m_voxelConfig.VoxelVolumeConfig;
            compute.SetInts("numberOfVoxels", volumeConfig.NumberOfVoxels.x, volumeConfig.NumberOfVoxels.y,
                volumeConfig.NumberOfVoxels.z);
            compute.SetFloat("voxelSpacing", volumeConfig.VoxelSpacing);

            // Set fluid source parameters
            if (m_enableFluidSource && m_sourceTransform != null)
            {
                compute.SetVector("sourcePosition", m_sourceTransform.position);
                compute.SetVector("sourceVelocity", m_sourceVelocity);
                compute.SetFloat("sourceRadius", m_sourceRadius);
                compute.SetFloat("sourceAmount", m_sourceAmount);
                compute.SetInt("sourceMaterial", (int)m_sourceMaterial);
            }

            // Bind buffers to all kernels that need them
            SetBuffersOnKernels();
        }

        private void SetBuffersOnKernels()
        {
            var compute = m_voxelConfig.FluidSimulationConfig.Compute;

            // Main fluid volume buffers
            int[] fluidVolumeKernels =
            {
                m_advectionKernel, m_externalForcesKernel, m_computeDivergenceKernel,
                m_pressureProjectionKernel, m_applyBoundaryConditionsKernel,
                m_addFluidSourceKernel, m_updateBoundariesFromSolidsKernel,
                m_convertFluidToVoxelVolumeKernel
            };

            foreach (int kernel in fluidVolumeKernels)
            {
                compute.SetBuffer(kernel, "fluidVolume", m_fluidVolumeBuffer);
                compute.SetBuffer(kernel, "fluidVolumeBackBuffer", m_fluidVolumeBackBuffer);
            }

            // Divergence buffer
            compute.SetBuffer(m_computeDivergenceKernel, "divergenceBuffer", m_divergenceBuffer);
            compute.SetBuffer(m_pressureProjectionKernel, "divergenceBuffer", m_divergenceBuffer);

            // Output voxel volume buffer for mesh generation
            compute.SetBuffer(m_convertFluidToVoxelVolumeKernel, "outputVoxelVolume", m_tempVoxelVolumeBuffer);
        }

        private void InitializeFluidVolume()
        {
            // Initialize fluid volume with empty state
            // Could be enhanced to initialize from existing voxel volume
            var compute = m_voxelConfig.FluidSimulationConfig.Compute;

            // Clear both buffers
            // Note: You might want to add an initialization kernel to the compute shader
            // For now, the buffers start with default (zero) values
        }

        private void SimulateFluidStep()
        {
            var compute = m_voxelConfig.FluidSimulationConfig.Compute;
            var numberOfVoxels = m_voxelConfig.VoxelVolumeConfig.NumberOfVoxels;

            // 1. Update boundaries from solid geometry
            compute.Dispatch(m_updateBoundariesFromSolidsKernel, numberOfVoxels);

            // 2. Add fluid sources
            if (m_enableFluidSource)
            {
                compute.Dispatch(m_addFluidSourceKernel, numberOfVoxels);
            }

            // 3. Advection step
            compute.Dispatch(m_advectionKernel, numberOfVoxels);
            SwapBuffers();

            // 4. Apply external forces
            compute.Dispatch(m_externalForcesKernel, numberOfVoxels);

            // 5. Pressure projection (multiple iterations)
            for (int i = 0; i < m_pressureIterations; i++)
            {
                // Compute divergence
                compute.Dispatch(m_computeDivergenceKernel, numberOfVoxels);

                // Update pressure and apply pressure gradient
                compute.Dispatch(m_pressureProjectionKernel, numberOfVoxels);
                SwapBuffers();
            }

            // 6. Apply boundary conditions
            compute.Dispatch(m_applyBoundaryConditionsKernel, numberOfVoxels);
        }

        public void ConvertFluidToVoxelVolume(float3 worldPosition)
        {
            if (!m_buffersInitialized)
                return;

            var compute = m_voxelConfig.FluidSimulationConfig.Compute;
            compute.SetVector("fluidVolumeToWorldSpaceOffset", (Vector3)worldPosition);
            compute.Dispatch(m_convertFluidToVoxelVolumeKernel, m_voxelConfig.VoxelVolumeConfig.NumberOfVoxels);
        }

        public void UpdateBoundariesFromVoxelVolume(ComputeBuffer voxelVolumeBuffer, float3 worldPosition)
        {
            if (!m_buffersInitialized || voxelVolumeBuffer == null)
                return;

            var compute = m_voxelConfig.FluidSimulationConfig.Compute;
            compute.SetBuffer(m_updateBoundariesFromSolidsKernel, "voxelVolume", voxelVolumeBuffer);
            compute.SetVector("voxelVolumeToWorldSpaceOffset", (Vector3)worldPosition);
            compute.Dispatch(m_updateBoundariesFromSolidsKernel, m_voxelConfig.VoxelVolumeConfig.NumberOfVoxels);
        }

        private void SwapBuffers()
        {
            (m_fluidVolumeBuffer, m_fluidVolumeBackBuffer) = (m_fluidVolumeBackBuffer, m_fluidVolumeBuffer);
            SetBuffersOnKernels(); // Rebind swapped buffers
        }

        private void HandleVoxelVolumeConfigChanged()
        {
            if (Application.isPlaying && gameObject.activeSelf)
            {
                CreateBuffers();
                ApplyFluidSimulationConfig();
            }
        }

        private void HandleFluidConfigChanged()
        {
            if (Application.isPlaying && gameObject.activeSelf)
            {
                ApplyFluidSimulationConfig();
            }
        }

        private int GetFluidVoxelSizeInBytes()
        {
            // PackedFluidVoxel structure size:
            // PackedVoxel (8 bytes) + 3 uints for velocity/pressure/temperature/density (12 bytes) = 20 bytes
            return 20;
        }

        // Public methods for external control
        public void SetFluidSource(Vector3 position, Vector3 velocity, float radius, float amount)
        {
            if (m_sourceTransform == null)
            {
                GameObject sourceGO = new GameObject("Fluid Source");
                sourceGO.transform.SetParent(transform);
                m_sourceTransform = sourceGO.transform;
            }

            m_sourceTransform.position = position;
            m_sourceVelocity = velocity;
            m_sourceRadius = radius;
            m_sourceAmount = amount;
            m_enableFluidSource = true;

            ApplyFluidSimulationConfig();
        }

        public void DisableFluidSource()
        {
            m_enableFluidSource = false;
            ApplyFluidSimulationConfig();
        }
    }
}