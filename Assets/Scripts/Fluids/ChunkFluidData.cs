using System.Collections.Generic;
using Tuntenfisch.Voxels.Materials;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Tuntenfisch.Fluids
{
    /// <summary>
    /// Updated ChunkFluidData that works with the texture-based fluid simulation system.
    /// This class now wraps ChunkFluidData and provides additional chunk-specific functionality.
    /// </summary>
    [System.Serializable]
    public class ChunkFluidData
    {
        // Core fluid textures (managed by FluidTextureFactory)
        public ChunkFluidTextures FluidTextures { get; set; }
        public ComputeBuffer SolidVoxelBuffer => m_solidVoxelBuffer;
        
        // Chunk positioning and state
        public float3 WorldPosition { get; set; }
        public bool HasFluidSource => FluidSource is { IsActive: true };
        public FluidSourceData FluidSource { get; set; }
        
        private GameObject m_fluidChunkObject;
        
        // Simulation timing and state
        private float m_lastSimulationTime = 0f;
        private float m_accumulatedTime = 0f;
        private bool m_isSimulationActive = false;
        private int m_activeFluidVoxels = 0;
        
        // Performance tracking
        private Queue<float> m_frameTimeHistory = new Queue<float>();
        private ComputeBuffer m_solidVoxelBuffer;
        private const int MAX_FRAME_HISTORY = 30;
        private const float SIMULATION_TIMEOUT = 0.1f;

        public bool IsValid()
        {
            return FluidTextures != null && FluidTextures.IsValid();
        }

        public void Initialize(int3 dimensions, float3 worldPosition, ComputeBuffer solidVoxels, GameObject fluidChunk)
        {
            // Store solid voxel buffer
            m_solidVoxelBuffer = solidVoxels;
            
            WorldPosition = worldPosition;
            
            // Create fluid textures using the factory
            FluidTextures = FluidTextureFactory.CreateFluidTextures(dimensions);
            
            // Set up fluid volume
            m_fluidChunkObject = fluidChunk;
            m_fluidChunkObject.transform.localScale = new Vector3(dimensions.x, dimensions.y, dimensions.z);
            
            // Reset state
            ResetSimulationState();
            
            // Debug.Log($"Initialized ChunkFluidData at {WorldPosition} with dimensions {dimensions}");
        }

        public void Cleanup()
        {
            FluidTextures?.Release();
            FluidTextures = null;
            
            FluidSource?.Deactivate();
            FluidSource = null;
            
            ResetSimulationState();
            
            // Debug.Log($"Cleaned up ChunkFluidData at {WorldPosition}");
        }

        private void ResetSimulationState()
        {
            m_lastSimulationTime = 0f;
            m_accumulatedTime = 0f;
            m_isSimulationActive = false;
            m_activeFluidVoxels = 0;
            m_frameTimeHistory.Clear();
        }

        public void AddFluidSource(Vector3 position, Vector3 velocity, float radius, float amount, MaterialIndex material = MaterialIndex.Water)
        {
            FluidSource = new FluidSourceData(position, velocity, radius, amount, material);
            m_isSimulationActive = true;
            
            Debug.Log($"Added fluid source to chunk at {WorldPosition}: {FluidSource}");
        }

        public void RemoveFluidSource()
        {
            FluidSource?.Deactivate();
            FluidSource = null;
            
            Debug.Log($"Removed fluid source from chunk at {WorldPosition}");
        }

        public void UpdateSimulationTiming(float deltaTime)
        {
            m_accumulatedTime += deltaTime;
            
            // Track frame times for performance monitoring
            m_frameTimeHistory.Enqueue(deltaTime);
            if (m_frameTimeHistory.Count > MAX_FRAME_HISTORY)
            {
                m_frameTimeHistory.Dequeue();
            }
            
            // Update activity state
            if (HasFluidSource || m_activeFluidVoxels > 0)
            {
                m_isSimulationActive = true;
                m_lastSimulationTime = Time.time;
            }
            else if (Time.time - m_lastSimulationTime > SIMULATION_TIMEOUT)
            {
                m_isSimulationActive = false;
            }
        }

        public bool ShouldRunSimulation(float fixedTimeStep)
        {
            return m_isSimulationActive && m_accumulatedTime >= fixedTimeStep;
        }

        public void ConsumeSimulationTime(float fixedTimeStep)
        {
            m_accumulatedTime -= fixedTimeStep;
            m_accumulatedTime = Mathf.Max(0f, m_accumulatedTime); // Prevent negative accumulation
        }

        public void UpdateActiveFluidVoxelCount(int count)
        {
            m_activeFluidVoxels = count;
            
            // If we have active fluid, mark simulation as active
            if (count > 0)
            {
                m_isSimulationActive = true;
                m_lastSimulationTime = Time.time;
            }
        }

        public bool IsSimulationActive()
        {
            return m_isSimulationActive;
        }

        public bool IsSimulationSettled()
        {
            return !m_isSimulationActive && !HasFluidSource && m_activeFluidVoxels == 0;
        }

        public float GetSimulationProgress()
        {
            if (!m_isSimulationActive)
                return 1.0f;
                
            // Simple progress based on time since last activity
            float timeSinceActivity = Time.time - m_lastSimulationTime;
            return Mathf.Clamp01(timeSinceActivity / SIMULATION_TIMEOUT);
        }

        public float GetAverageFrameTime()
        {
            if (m_frameTimeHistory.Count == 0)
                return 0f;
                
            float total = 0f;
            foreach (float time in m_frameTimeHistory)
            {
                total += time;
            }
            return total / m_frameTimeHistory.Count;
        }

        public void DebugOutput()
        {
            Debug.Log($"=== ChunkFluidData Debug ===");
            Debug.Log($"World Position: {WorldPosition}");
            Debug.Log($"Fluid Textures Valid: {IsValid()}");
            Debug.Log($"Has Fluid Source: {HasFluidSource}");
            Debug.Log($"Fluid Source: {FluidSource?.ToString() ?? "None"}");
            Debug.Log($"Simulation Active: {m_isSimulationActive}");
            Debug.Log($"Active Fluid Voxels: {m_activeFluidVoxels}");
            Debug.Log($"Accumulated Time: {m_accumulatedTime:F3}s");
            Debug.Log($"Last Simulation Time: {m_lastSimulationTime:F3}s");
            Debug.Log($"Average Frame Time: {GetAverageFrameTime():F4}s");
            Debug.Log($"Simulation Progress: {GetSimulationProgress():F2}");
            Debug.Log($"Is Settled: {IsSimulationSettled()}");
            
            if (FluidTextures != null)
            {
                Debug.Log($"Velocity Texture: {FluidTextures.VelocityRead?.width}x{FluidTextures.VelocityRead?.height}x{FluidTextures.VelocityRead?.volumeDepth}");
                Debug.Log($"Density Texture: {FluidTextures.DensityRead?.width}x{FluidTextures.DensityRead?.height}x{FluidTextures.DensityRead?.volumeDepth}");
                Debug.Log($"Pressure Texture: {FluidTextures.PressureRead?.width}x{FluidTextures.PressureRead?.height}x{FluidTextures.PressureRead?.volumeDepth}");
            }
            
            Debug.Log($"===========================");
        }

        // Helper methods for integration with existing systems
        public void RegisterForVolumetricRendering(int3 chunkCoordinate, Vector3 volumeSize)
        {
            if (IsValid())
            {
                // FluidTextures.RegisterForVolumetricRendering(chunkCoordinate, WorldPosition, volumeSize);
                m_fluidChunkObject.SetActive(true);
                m_fluidChunkObject.GetComponent<MeshRenderer>().sharedMaterial.SetTexture("_densityTex", FluidTextures.DensityRead);
            }
        }

        public void UnregisterFromVolumetricRendering(int3 chunkCoordinate)
        {
            // VolumetricFluidExtensions.UnregisterFromVolumetricRendering(chunkCoordinate);
            m_fluidChunkObject.SetActive(false);
        }

        // Method to check if chunk needs fluid simulation update
        public bool NeedsSimulationUpdate()
        {
            return m_isSimulationActive || HasFluidSource;
        }

        // Get fluid density at a specific world position (for external queries)
        public float GetFluidDensityAtPosition(Vector3 worldPosition)
        {
            if (!IsValid())
                return 0f;
                
            // This would require implementing a sampling method that reads from the density texture
            // For now, return a simple check if we have any fluid activity
            return m_isSimulationActive ? 1f : 0f;
        }

        // Check if the chunk has any visible fluid
        public bool HasVisibleFluid()
        {
            return m_activeFluidVoxels > 0 || HasFluidSource;
        }

        // Force complete simulation step (for debugging)
        public void ForceComplete()
        {
            m_accumulatedTime = 0f;
            m_isSimulationActive = false;
            Debug.Log($"Force completed simulation for chunk at {WorldPosition}");
        }

        // Estimate memory usage for debugging
        public long GetEstimatedMemoryUsage()
        {
            if (!IsValid())
                return 0;
                
            var velTex = FluidTextures.VelocityRead;
            if (velTex == null)
                return 0;
                
            // Rough estimate: 7 textures (vel read/write, density read/write, pressure read/write, divergence)
            // Velocity is ARGB32 (16 bytes), others are RFloat (4 bytes)
            long velocitySize = velTex.width * velTex.height * velTex.volumeDepth * 16 * 2; // read + write
            long otherSize = velTex.width * velTex.height * velTex.volumeDepth * 4 * 5;     // density, pressure, divergence
            
            return velocitySize + otherSize;
        }
    }

    /// <summary>
    /// Updated FluidSourceData to match the texture-based system
    /// </summary>
    [System.Serializable]
    public class FluidSourceData
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public float Radius;
        public float Amount;
        public MaterialIndex Material;
        public bool IsActive;
        public float StartTime;
        public float Duration; // -1 for infinite

        public FluidSourceData(Vector3 position, Vector3 velocity, float radius, float amount, 
            MaterialIndex material = MaterialIndex.Water, float duration = -1f)
        {
            Position = position;
            Velocity = velocity;
            Radius = radius;
            Amount = amount;
            Material = material;
            IsActive = true;
            StartTime = Time.time;
            Duration = duration;
        }

        public void Deactivate()
        {
            IsActive = false;
        }

        public bool ShouldBeActive()
        {
            if (!IsActive)
                return false;
                
            if (Duration > 0 && Time.time - StartTime > Duration)
            {
                IsActive = false;
                return false;
            }
            
            return true;
        }

        public float GetAge()
        {
            return Time.time - StartTime;
        }

        public float GetRemainingTime()
        {
            if (Duration < 0)
                return float.MaxValue;
                
            return Mathf.Max(0f, Duration - GetAge());
        }

        public override string ToString()
        {
            return $"FluidSource(pos:{Position}, vel:{Velocity}, radius:{Radius}, amount:{Amount}, " +
                   $"material:{Material}, active:{IsActive}, age:{GetAge():F1}s)";
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