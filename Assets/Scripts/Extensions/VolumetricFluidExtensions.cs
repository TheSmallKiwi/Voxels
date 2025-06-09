// Extension to FluidSimulation for volumetric integration

using Unity.Mathematics;
using UnityEngine;

namespace Tuntenfisch.Fluids
{
    public static class VolumetricFluidExtensions
    {
        /// <summary>
        /// Register chunk with volumetric renderer after simulation step
        /// </summary>
        public static void RegisterForVolumetricRendering(this ChunkFluidTextures fluidData,
            int3 chunkCoordinate, Vector3 worldPosition, Vector3 volumeSize)
        {
            var renderer = Object.FindFirstObjectByType<Tuntenfisch.Rendering.VolumetricFluidRenderer>();
            if (renderer != null)
            {
                renderer.RegisterChunk(chunkCoordinate, fluidData, worldPosition, volumeSize);
                Debug.Log(
                    $"Registered chunk at {chunkCoordinate} for volumetric rendering. Position: {worldPosition}, Size: {volumeSize}");
            }
        }

        /// <summary>
        /// Unregister chunk from volumetric renderer
        /// </summary>
        public static void UnregisterFromVolumetricRendering(int3 chunkCoordinate)
        {
            var renderer = Object.FindFirstObjectByType<Tuntenfisch.Rendering.VolumetricFluidRenderer>();
            if (renderer != null)
            {
                renderer.UnregisterChunk(chunkCoordinate);
            }
        }
    }
}