#ifndef TUNTENFISCH_FLUIDS_FLUID_VOLUME
#define TUNTENFISCH_FLUIDS_FLUID_VOLUME

#include "Assets/Compute/Fluids/Include/FluidVoxel.hlsl"
#include "Assets/Compute/Voxels/Include/VoxelVolume.hlsl"

RWStructuredBuffer<PackedFluidVoxel> fluidVolume;
RWStructuredBuffer<PackedFluidVoxel> fluidVolumeBackBuffer; // For double buffering

float3 fluidVolumeToWorldSpaceOffset;

// Reuse the same helper functions from VoxelVolume.hlsl
bool IsOutOfFluidVolumeBounds(uint3 coordinate)
{
    return any(coordinate > numberOfVoxels - 1);
}

uint CalculateFluidVolumeIndex(uint3 coordinate)
{
    return dot(coordinate, uint3(1, numberOfVoxels.x, numberOfVoxels.x * numberOfVoxels.y));
}

FluidVoxel GetFluidVoxel(uint3 coordinate)
{
    return UnpackFluidVoxel(fluidVolume[CalculateFluidVolumeIndex(coordinate)]);
}

void SetFluidVoxel(uint3 coordinate, FluidVoxel voxel)
{
    fluidVolume[CalculateFluidVolumeIndex(coordinate)] = PackFluidVoxel(voxel);
}

FluidVoxel GetFluidVoxelBackBuffer(uint3 coordinate)
{
    return UnpackFluidVoxel(fluidVolumeBackBuffer[CalculateFluidVolumeIndex(coordinate)]);
}

void SetFluidVoxelBackBuffer(uint3 coordinate, FluidVoxel voxel)
{
    fluidVolumeBackBuffer[CalculateFluidVolumeIndex(coordinate)] = PackFluidVoxel(voxel);
}

// Coordinate space conversions (same as VoxelVolume.hlsl)
float3 FluidVoxelToVolumeSpace(uint3 coordinate, float3 position = 0.0f)
{
    return voxelSpacing * (position + coordinate - 0.5f * (numberOfVoxels - 1.0f));
}

float3 FluidVolumeToVoxelSpace(uint3 coordinate, float3 position = 0.0f)
{
    return position / voxelSpacing - coordinate + 0.5f * (numberOfVoxels - 1.0f);
}

float3 FluidVolumeToWorldSpace(float3 position)
{
    return position + fluidVolumeToWorldSpaceOffset;
}

float3 WorldToFluidVolumeSpace(float3 position)
{
    return position - fluidVolumeToWorldSpaceOffset;
}

// Helper for trilinear sampling of fluid properties
FluidVoxel SampleFluidVoxelTrilinear(float3 position)
{
    // Convert world position to voxel space
    float3 voxelPos = WorldToFluidVolumeSpace(position) / voxelSpacing + 0.5f * (numberOfVoxels - 1.0f);
    
    // Clamp to valid range
    voxelPos = clamp(voxelPos, 0.0f, float3(numberOfVoxels - 1));
    
    // Get base coordinate and fractional part
    uint3 baseCoord = uint3(floor(voxelPos));
    float3 frac = voxelPos - float3(baseCoord);
    
    // Clamp base coordinate to ensure we don't go out of bounds
    baseCoord = min(baseCoord, numberOfVoxels - 2);
    
    // Sample 8 neighboring voxels
    FluidVoxel v000 = GetFluidVoxel(baseCoord);
    FluidVoxel v100 = GetFluidVoxel(baseCoord + uint3(1, 0, 0));
    FluidVoxel v010 = GetFluidVoxel(baseCoord + uint3(0, 1, 0));
    FluidVoxel v110 = GetFluidVoxel(baseCoord + uint3(1, 1, 0));
    FluidVoxel v001 = GetFluidVoxel(baseCoord + uint3(0, 0, 1));
    FluidVoxel v101 = GetFluidVoxel(baseCoord + uint3(1, 0, 1));
    FluidVoxel v011 = GetFluidVoxel(baseCoord + uint3(0, 1, 1));
    FluidVoxel v111 = GetFluidVoxel(baseCoord + uint3(1, 1, 1));
    
    // Trilinear interpolation
    FluidVoxel result;

    result.voxel.materialIndex = v000.voxel.materialIndex;    

    // Interpolate Value & Gradient
    float4 v00 = lerp(v000.voxel.valueAndGradient, v100.voxel.valueAndGradient, frac.x);
    float4 v10 = lerp(v010.voxel.valueAndGradient, v110.voxel.valueAndGradient, frac.x);
    float4 v01 = lerp(v001.voxel.valueAndGradient, v101.voxel.valueAndGradient, frac.x);
    float4 v11 = lerp(v011.voxel.valueAndGradient, v111.voxel.valueAndGradient, frac.x);
    float4 v0 = lerp(v00, v10, frac.y);
    float4 v1 = lerp(v01, v11, frac.y);
    result.voxel.valueAndGradient = lerp(v0, v1, frac.z);
    
    // Interpolate density
    float d00 = lerp(v000.density, v100.density, frac.x);
    float d10 = lerp(v010.density, v110.density, frac.x);
    float d01 = lerp(v001.density, v101.density, frac.x);
    float d11 = lerp(v011.density, v111.density, frac.x);
    float d0 = lerp(d00, d10, frac.y);
    float d1 = lerp(d01, d11, frac.y);
    result.density = lerp(d0, d1, frac.z);
    
    // Interpolate velocity
    float3 vel00 = lerp(v000.velocity, v100.velocity, frac.x);
    float3 vel10 = lerp(v010.velocity, v110.velocity, frac.x);
    float3 vel01 = lerp(v001.velocity, v101.velocity, frac.x);
    float3 vel11 = lerp(v011.velocity, v111.velocity, frac.x);
    float3 vel0 = lerp(vel00, vel10, frac.y);
    float3 vel1 = lerp(vel01, vel11, frac.y);
    result.velocity = lerp(vel0, vel1, frac.z);
    
    // Interpolate pressure
    // float p00 = lerp(v000.pressure, v100.pressure, frac.x);
    // float p10 = lerp(v010.pressure, v110.pressure, frac.x);
    // float p01 = lerp(v001.pressure, v101.pressure, frac.x);
    // float p11 = lerp(v011.pressure, v111.pressure, frac.x);
    // float p0 = lerp(p00, p10, frac.y);
    // float p1 = lerp(p01, p11, frac.y);
    // result.pressure = lerp(p0, p1, frac.z);

    // Interpolate temperature
    float t00 = lerp(v000.temperature, v100.temperature, frac.x);
    float t10 = lerp(v010.temperature, v110.temperature, frac.x);
    float t01 = lerp(v001.temperature, v101.temperature, frac.x);
    float t11 = lerp(v011.temperature, v111.temperature, frac.x);
    float t0 = lerp(t00, t10, frac.y);
    float t1 = lerp(t01, t11, frac.y);
    result.temperature = lerp(t0, t1, frac.z);
    
    return result;
}

#endif