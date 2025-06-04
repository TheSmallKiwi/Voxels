using UnityEngine;
using Unity.Mathematics;
using Tuntenfisch.Fluids;
using Tuntenfisch.Voxels;
using Tuntenfisch.World;
using Tuntenfisch.Voxels.Materials;
using UnityEngine.InputSystem;

namespace Tuntenfisch.World
{
    public class FluidSimulationDebugger : MonoBehaviour
    {
        [Header("Debug Settings")] [SerializeField]
        private bool m_enableDebugLogging = true;

        [SerializeField] private bool m_visualizeFluidVolume = false;
        [SerializeField] private float m_debugSphereSize = 0.1f;
        [SerializeField] private Color m_fluidColor = Color.blue;
        [SerializeField] private Color m_solidColor = Color.gray;

        [Header("Test Settings")] [SerializeField]
        private Vector3 m_testSourceOffset = Vector3.up * 5f;

        [SerializeField] private float m_testSourceRadius = 2f;
        [SerializeField] private float m_testSourceAmount = 10f;
        [SerializeField] private Vector3 m_testSourceVelocity = Vector3.down * 2f;

        private ComputeBuffer m_debugReadbackBuffer;
        private FluidVoxelDebugData[] m_debugData;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct FluidVoxelDebugData
        {
            public float sdfValue;
            public float velocityMagnitude;
            public float pressure;
            public uint materialIndex;
        }

        private void Start()
        {
            // Create debug readback buffer
            int voxelCount = GetComponent<VoxelConfig>().VoxelVolumeConfig.VoxelCount;
            m_debugReadbackBuffer = new ComputeBuffer(voxelCount,
                System.Runtime.InteropServices.Marshal.SizeOf<FluidVoxelDebugData>());
            m_debugData = new FluidVoxelDebugData[voxelCount];
        }

        private void OnDestroy()
        {
            m_debugReadbackBuffer?.Release();
        }

        private void Update()
        {
            if (InputSystem.GetDevice<Keyboard>().f1Key.wasPressedThisFrame)
            {
                AddTestFluidSource();
            }

            if (InputSystem.GetDevice<Keyboard>().f2Key.wasPressedThisFrame)
            {
                DebugActiveChunks();
            }

            if (InputSystem.GetDevice<Keyboard>().f3Key.wasPressedThisFrame)
            {
                ValidateFluidBuffers();
            }

            if (InputSystem.GetDevice<Keyboard>().f4Key.wasPressedThisFrame)
            {
                m_visualizeFluidVolume = !m_visualizeFluidVolume;
                Debug.Log($"Fluid volume visualization: {(m_visualizeFluidVolume ? "ON" : "OFF")}");
            }
        }

        private void AddTestFluidSource()
        {
            Vector3 sourcePos = transform.position + m_testSourceOffset;

            Debug.Log($"[FluidDebug] Adding test fluid source at {sourcePos}");
            Debug.Log(
                $"[FluidDebug] Source parameters - Radius: {m_testSourceRadius}, Amount: {m_testSourceAmount}, Velocity: {m_testSourceVelocity}");

            WorldManager.Instance.AddFluidSource(
                sourcePos,
                m_testSourceVelocity,
                m_testSourceRadius,
                m_testSourceAmount,
                MaterialIndex.Water
            );
        }

        private void DebugActiveChunks()
        {
            var chunks = WorldManager.Instance.GetActiveChunks();
            int totalChunks = chunks.Count;
            int fluidChunks = 0;
            int chunksWithFluidMesh = 0;

            foreach (var kvp in chunks)
            {
                var chunk = kvp.Value;
                if (chunk.HasActiveFluid())
                {
                    fluidChunks++;

                    // Check if fluid mesh exists
                    var fluidMeshFilter = chunk.transform.Find("FluidMesh")?.GetComponent<MeshFilter>();
                    if (fluidMeshFilter != null && fluidMeshFilter.sharedMesh != null)
                    {
                        chunksWithFluidMesh++;
                        Debug.Log(
                            $"[FluidDebug] Chunk {kvp.Key} has fluid mesh with {fluidMeshFilter.sharedMesh.vertexCount} vertices");
                    }
                }
            }

            Debug.Log(
                $"[FluidDebug] Active chunks: {totalChunks}, With fluid: {fluidChunks}, With fluid mesh: {chunksWithFluidMesh}");
        }

        private void ValidateFluidBuffers()
        {
            Debug.Log("[FluidDebug] Validating fluid system configuration...");

            // Check FluidSimulation component
            if (WorldManager.FluidSimulation == null)
            {
                Debug.LogError("[FluidDebug] FluidSimulation component is null!");
                return;
            }

            if (!WorldManager.FluidSimulation.IsSimulationEnabled)
            {
                Debug.LogWarning("[FluidDebug] Fluid simulation is disabled!");
            }

            // Check VoxelConfig
            if (WorldManager.VoxelConfig.FluidSimulationConfig == null)
            {
                Debug.LogError("[FluidDebug] FluidSimulationConfig is null in VoxelConfig!");
                return;
            }

            var config = WorldManager.VoxelConfig.FluidSimulationConfig;
            Debug.Log(
                $"[FluidDebug] Fluid config - Viscosity: {config.Viscosity}, Gravity: {config.Gravity}, TimeStep: {config.MaxTimeStep}");

            // Check compute shader
            if (config.Compute == null)
            {
                Debug.LogError("[FluidDebug] Fluid compute shader is null!");
                return;
            }

            // Validate kernel indices
            var kernelNames = new string[]
            {
                "InitializeFluidVolume", "Advection", "ExternalForces",
                "ComputeDivergence", "PressureProjection", "ApplyBoundaryConditions",
                "AddFluidSource", "UpdateBoundariesFromSolids", "ConvertFluidToVoxelVolume"
            };

            foreach (var kernelName in kernelNames)
            {
                try
                {
                    int kernelIndex = config.Compute.FindKernel(kernelName);
                    Debug.Log($"[FluidDebug] Kernel '{kernelName}' found at index {kernelIndex}");
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[FluidDebug] Kernel '{kernelName}' not found: {e.Message}");
                }
            }

            // Check material config
            if (WorldManager.VoxelConfig.MaterialConfig.FluidRenderMaterial == null)
            {
                Debug.LogWarning("[FluidDebug] Fluid render material is null!");
            }

            Debug.Log("[FluidDebug] Validation complete");
        }

        public void DebugFluidVolume(ChunkFluidData chunkData)
        {
            if (!m_enableDebugLogging || chunkData == null || !chunkData.IsValid())
                return;

            // This would require a custom compute shader to extract debug data
            // For now, just log basic info
            Debug.Log($"[FluidDebug] Chunk at {chunkData.WorldPosition} - Has source: {chunkData.HasFluidSource}");

            if (chunkData.HasFluidSource && chunkData.FluidSource != null)
            {
                var source = chunkData.FluidSource;
                Debug.Log(
                    $"[FluidDebug] Source: Pos={source.Position}, Vel={source.Velocity}, Radius={source.Radius}, Amount={source.Amount}");
            }
        }

        private void OnDrawGizmos()
        {
            if (!m_visualizeFluidVolume || !Application.isPlaying)
                return;

            // This is a simplified visualization - in practice you'd need to read back the fluid buffer
            // and visualize the actual fluid voxels

            var chunks = WorldManager.Instance.GetActiveChunks();
            foreach (var kvp in chunks)
            {
                var chunk = kvp.Value;
                if (chunk.HasActiveFluid())
                {
                    // Draw chunk bounds
                    Gizmos.color = Color.cyan;
                    Gizmos.DrawWireCube(chunk.transform.position,
                        WorldManager.VoxelConfig.VoxelVolumeConfig.VoxelVolumeDimensions);

                    // Draw fluid source if present
                    var fluidData = chunk.FluidData;
                    if (fluidData != null && fluidData.HasFluidSource && fluidData.FluidSource != null)
                    {
                        Gizmos.color = m_fluidColor;
                        Gizmos.DrawWireSphere(fluidData.FluidSource.Position, fluidData.FluidSource.Radius);

                        // Draw velocity vector
                        Gizmos.color = Color.yellow;
                        Gizmos.DrawRay(fluidData.FluidSource.Position, fluidData.FluidSource.Velocity);
                    }
                }
            }
        }
    }
}