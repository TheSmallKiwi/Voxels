using System.Collections.Generic;
using Tuntenfisch.Voxels.Materials;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Tuntenfisch.Fluids
{
    [System.Serializable]
    public class ChunkFluidData
    {
        public ComputeBuffer FluidVolumeBuffer { get; set; }
        public ComputeBuffer FluidVolumeBackBuffer { get; set; }
        public ComputeBuffer VoxelVolumeBuffer { get; set; }
        public ComputeBuffer TempVoxelVolumeBuffer { get; set; }
        public float3 WorldPosition { get; set; }
        public bool HasFluidSource => FluidSource is { IsActive: true };
        public FluidSourceData FluidSource { get; set; }
        
        public GraphicsFence Fence { get; set; }

        // CommandBuffer execution state
        public CommandBuffer CommandBuffer { get; private set; }
        private Queue<System.Action> m_pendingActions = new Queue<System.Action>();
        private bool m_commandsExecuted = false;
        private float m_commandStartTime = 0f;
        private const float COMMAND_TIMEOUT = 0.1f; // Maximum time to consider commands as "active"

        public bool IsValid()
        {
            return FluidVolumeBuffer != null && FluidVolumeBackBuffer != null;
        }

        public void SwapBuffers()
        {
            (FluidVolumeBuffer, FluidVolumeBackBuffer) = (FluidVolumeBackBuffer, FluidVolumeBuffer);
        }

        public void InitializeCommandBuffer()
        {
            CleanupCommandBuffer();
            CommandBuffer = new CommandBuffer { name = $"FluidSimulation_{WorldPosition}" };
            m_pendingActions.Clear();
            m_commandsExecuted = false;
            m_commandStartTime = Time.time;
        }

        public void CleanupCommandBuffer()
        {
            CommandBuffer?.Release();
            CommandBuffer = null;
            m_pendingActions?.Clear();
            m_commandsExecuted = false;
            m_commandStartTime = 0f;
        }

        public void AddBufferSwapCommand()
        {
            m_pendingActions.Enqueue(() => SwapBuffers());
        }

        public bool ExecuteNextCommand()
        {
            // Execute the main CommandBuffer first time
            if (!m_commandsExecuted && CommandBuffer != null)
            {
                Graphics.ExecuteCommandBuffer(CommandBuffer);
                m_commandsExecuted = true;
                return true; // Still has pending CPU actions
            }
            
            // Execute pending CPU-side actions (like buffer swaps)
            if (m_pendingActions.Count > 0)
            {
                var action = m_pendingActions.Dequeue();
                action?.Invoke();
                return m_pendingActions.Count > 0; // Return true if more actions remain
            }

            return false; // No more commands to execute
        }

        public bool HasActiveCommands()
        {
            // Check if we have a CommandBuffer that hasn't been executed yet
            if (!m_commandsExecuted && CommandBuffer != null)
                return true;

            // Check if we have pending CPU actions
            if (m_pendingActions.Count > 0)
                return true;

            // Use timeout to handle any GPU execution time
            if (m_commandsExecuted && (Time.time - m_commandStartTime) < COMMAND_TIMEOUT)
                return true;

            return false;
        }

        public bool HasPendingActions()
        {
            return m_pendingActions.Count > 0;
        }

        public void DebugOutput()
        {
            Debug.Log($"=== ChunkFluidData Debug ===");
            Debug.Log($"World Position: {WorldPosition}");
            Debug.Log($"Fluid Volume Buffer: {FluidVolumeBuffer?.count ?? 0} elements");
            Debug.Log($"Fluid Volume Back Buffer: {FluidVolumeBackBuffer?.count ?? 0} elements");
            Debug.Log($"Voxel Volume Buffer: {VoxelVolumeBuffer?.count ?? 0} elements");
            Debug.Log($"Temp Voxel Volume Buffer: {TempVoxelVolumeBuffer?.count ?? 0} elements");
            Debug.Log($"Has Fluid Source: {HasFluidSource}");
            Debug.Log($"Fluid Source: {FluidSource?.Position ?? Vector3.zero} (radius: {FluidSource?.Radius ?? 0f})");
            Debug.Log($"Commands Executed: {m_commandsExecuted}");
            Debug.Log($"Pending Actions: {m_pendingActions?.Count ?? 0}");
            Debug.Log($"Has Active Commands: {HasActiveCommands()}");
            Debug.Log($"Command Start Time: {m_commandStartTime}");
            Debug.Log($"Time Since Command Start: {Time.time - m_commandStartTime}");
            Debug.Log($"===========================");
        }

        // Helper method to force completion (for debugging)
        public void ForceComplete()
        {
            m_commandsExecuted = true;
            m_pendingActions.Clear();
            m_commandStartTime = 0f;
        }

        // Helper method to check if simulation should be considered "settled"
        public bool IsSimulationSettled()
        {
            // This could be expanded to check actual fluid activity
            // For now, just check if commands are complete and no fluid source is active
            return !HasActiveCommands() && !HasFluidSource;
        }

        // Get simulation progress (0.0 to 1.0)
        public float GetSimulationProgress()
        {
            if (!m_commandsExecuted && CommandBuffer != null)
                return 0.0f;

            if (m_pendingActions.Count > 0)
            {
                // Estimate progress based on remaining actions
                // This is a rough estimate - in practice you might want more sophisticated tracking
                return 0.8f; // GPU work done, CPU actions remain
            }

            if (HasActiveCommands())
            {
                // Use timeout to estimate progress
                float timeElapsed = Time.time - m_commandStartTime;
                float progress = Mathf.Clamp01(timeElapsed / COMMAND_TIMEOUT);
                return 0.8f + (0.2f * progress); // 80% to 100%
            }

            return 1.0f; // Complete
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
        public bool IsActive;

        public FluidSourceData(Vector3 position, Vector3 velocity, float radius, float amount, MaterialIndex material = MaterialIndex.Water)
        {
            Position = position;
            Velocity = velocity;
            Radius = radius;
            Amount = amount;
            Material = material;
            IsActive = true;
        }

        public void Deactivate()
        {
            IsActive = false;
        }

        public override string ToString()
        {
            return $"FluidSource(pos:{Position}, vel:{Velocity}, radius:{Radius}, amount:{Amount}, material:{Material}, active:{IsActive})";
        }
    }
}