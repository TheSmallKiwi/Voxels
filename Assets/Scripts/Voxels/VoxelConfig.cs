using Tuntenfisch.Fluids;
using Tuntenfisch.Voxels.DC;
using Tuntenfisch.Voxels.Materials;
using Tuntenfisch.Voxels.Procedural;
using Tuntenfisch.Voxels.Volume;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Assertions;

namespace Tuntenfisch.Voxels
{
    public class VoxelConfig : MonoBehaviour
    {
        public VoxelVolumeConfig VoxelVolumeConfig => m_voxelVolumeConfig;
        public MaterialConfig MaterialConfig => m_materialConfig;
        public DualContouringConfig DualContouringConfig => m_dualContouringConfig;
        public GenerationGraph GenerationGraph => m_generationGraph;
        public FluidSimulationConfig FluidSimulationConfig => m_fluidSimulationConfig;

        [SerializeField]
        private VoxelVolumeConfig m_voxelVolumeConfig;
        [SerializeField]
        private MaterialConfig m_materialConfig;
        [SerializeField]
        private DualContouringConfig m_dualContouringConfig;
        [SerializeField]
        private GenerationGraph m_generationGraph;
        [SerializeField]
        private FluidSimulationConfig m_fluidSimulationConfig;

        private void Awake()
        {
            Assert.IsNotNull(m_voxelVolumeConfig);
            Assert.IsNotNull(m_materialConfig);
            Assert.IsNotNull(m_dualContouringConfig);
            Assert.IsNotNull(m_generationGraph);
            // FluidSimulationConfig is optional

            VoxelVolumeConfig.OnDirtied += ApplyVoxelVolumeConfig;
            DualContouringConfig.OnDirtied += ApplyDualContouringConfig;
            
            if (m_fluidSimulationConfig)
            {
                FluidSimulationConfig.OnDirtied += ApplyFluidSimulationConfig;
            }

            ApplyVoxelVolumeConfig();
            ApplyDualContouringConfig();
            
            if (m_fluidSimulationConfig)
            {
                ApplyFluidSimulationConfig();
            }
        }

        private void OnDestroy()
        {
            VoxelVolumeConfig.OnDirtied -= ApplyVoxelVolumeConfig;
            DualContouringConfig.OnDirtied -= ApplyDualContouringConfig;
            
            if (m_fluidSimulationConfig)
            {
                FluidSimulationConfig.OnDirtied -= ApplyFluidSimulationConfig;
            }
        }

        private void ApplyVoxelVolumeConfig()
        {
            VoxelVolumeConfig.Compute.SetInts(ComputeShaderProperties.NumberOfVoxels, VoxelVolumeConfig.NumberOfVoxels.x, VoxelVolumeConfig.NumberOfVoxels.y, VoxelVolumeConfig.NumberOfVoxels.z);
            VoxelVolumeConfig.Compute.SetFloat(ComputeShaderProperties.VoxelSpacing, VoxelVolumeConfig.VoxelSpacing);

            int3 numberOfVoxels = VoxelVolumeConfig.NumberOfVoxels;
            DualContouringConfig.Compute.SetInts(ComputeShaderProperties.NumberOfVoxels, numberOfVoxels.x, numberOfVoxels.y, numberOfVoxels.z);
            DualContouringConfig.Compute.SetFloat(ComputeShaderProperties.VoxelSpacing, VoxelVolumeConfig.VoxelSpacing);
            
            // Apply to fluid simulation config if available
            if (m_fluidSimulationConfig != null && m_fluidSimulationConfig.Compute != null)
            {
                m_fluidSimulationConfig.Compute.SetInts(ComputeShaderProperties.NumberOfVoxels, numberOfVoxels.x, numberOfVoxels.y, numberOfVoxels.z);
                m_fluidSimulationConfig.Compute.SetFloat(ComputeShaderProperties.VoxelSpacing, VoxelVolumeConfig.VoxelSpacing);
            }
        }

        private void ApplyDualContouringConfig()
        {
            float cosOfHalfSharpFeatureAngle = math.cos(math.radians(0.5f * DualContouringConfig.SharpFeatureAngle));
            Shader.SetGlobalFloat(ShaderProperties.CosOfHalfSharpFeatureAngle, cosOfHalfSharpFeatureAngle);

            DualContouringConfig.Compute.SetInt(ComputeShaderProperties.SchmitzParticleIterations, DualContouringConfig.SchmitzParticleIterations);
            DualContouringConfig.Compute.SetFloat(ComputeShaderProperties.SchmitzParticleStepSize, DualContouringConfig.SchmitzParticleStepSize);
        }

        private void ApplyFluidSimulationConfig()
        {
            if (m_fluidSimulationConfig == null || m_fluidSimulationConfig.Compute == null)
                return;

            var compute = m_fluidSimulationConfig.Compute;

            // Apply fluid simulation parameters
            compute.SetFloat("viscosity", m_fluidSimulationConfig.Viscosity);
            compute.SetVector("gravity", m_fluidSimulationConfig.Gravity);
            compute.SetFloat("fluidDensityThreshold", m_fluidSimulationConfig.FluidDensityThreshold);
            compute.SetFloat("pressureRelaxation", m_fluidSimulationConfig.PressureRelaxation);
            compute.SetFloat("boundaryFriction", m_fluidSimulationConfig.BoundaryFriction);
            compute.SetFloat("dampingFactor", m_fluidSimulationConfig.DampingFactor);
            compute.SetFloat("minFluidDensity", m_fluidSimulationConfig.MinFluidDensity);
            compute.SetBool("enableNoSlipBoundaries", m_fluidSimulationConfig.EnableNoSlipBoundaries);

            // Apply shared voxel volume parameters
            int3 numberOfVoxels = VoxelVolumeConfig.NumberOfVoxels;
            compute.SetInts(ComputeShaderProperties.NumberOfVoxels, numberOfVoxels.x, numberOfVoxels.y, numberOfVoxels.z);
            compute.SetFloat(ComputeShaderProperties.VoxelSpacing, VoxelVolumeConfig.VoxelSpacing);
        }
    }
}