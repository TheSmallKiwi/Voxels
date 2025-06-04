#ifndef TUNTENFISCH_FLUIDS_FLUID_VOLUME
#define TUNTENFISCH_FLUIDS_FLUID_VOLUME

#include "Assets/Compute/Fluids/Include/FluidVoxel.hlsl"
#include "Assets/Compute/Voxels/Include/VoxelVolume.hlsl"

StructuredBuffer<PackedFluidVoxel> fluidVolume; // Read Buffer
RWStructuredBuffer<PackedFluidVoxel> fluidVolumeBackBuffer; // Write Buffer

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

// Helper function to calculate SDF gradient using finite differences
float3 CalculateSDFGradient(uint3 coordinate, float centerSDF)
{
    float3 gradient = float3(0, 0, 0);
    
    // Calculate gradient using central differences
    if (coordinate.x > 0 && coordinate.x < numberOfVoxels.x - 1)
    {
        FluidVoxel voxelXMinus = GetFluidVoxel(coordinate - uint3(1, 0, 0));
        FluidVoxel voxelXPlus = GetFluidVoxel(coordinate + uint3(1, 0, 0));
        gradient.x = (voxelXPlus.voxel.valueAndGradient.x - voxelXMinus.voxel.valueAndGradient.x) / (2.0f * voxelSpacing);
    }
    
    if (coordinate.y > 0 && coordinate.y < numberOfVoxels.y - 1)
    {
        FluidVoxel voxelYMinus = GetFluidVoxel(coordinate - uint3(0, 1, 0));
        FluidVoxel voxelYPlus = GetFluidVoxel(coordinate + uint3(0, 1, 0));
        gradient.y = (voxelYPlus.voxel.valueAndGradient.x - voxelYMinus.voxel.valueAndGradient.x) / (2.0f * voxelSpacing);
    }
    
    if (coordinate.z > 0 && coordinate.z < numberOfVoxels.z - 1)
    {
        FluidVoxel voxelZMinus = GetFluidVoxel(coordinate - uint3(0, 0, 1));
        FluidVoxel voxelZPlus = GetFluidVoxel(coordinate + uint3(0, 0, 1));
        gradient.z = (voxelZPlus.voxel.valueAndGradient.x - voxelZMinus.voxel.valueAndGradient.x) / (2.0f * voxelSpacing);
    }
    
    // Normalize gradient for surface normal (if near surface)
    float gradientLength = length(gradient);
    if (gradientLength > 0.001f && abs(centerSDF) < 2.0f * voxelSpacing)
    {
        gradient = gradient / gradientLength;
    }
    else if (gradientLength < 0.001f)
    {
        // Default upward normal for areas with no gradient
        gradient = float3(0, 1, 0);
    }
    
    return gradient;
}

// Helper for trilinear sampling of fluid properties
FluidVoxel SampleFluidVoxelTrilinear(float3 position)
{
    // Convert world position to voxel space
    float3 voxelPos = WorldToFluidVolumeSpace(position) / voxelSpacing + 0.5f * (numberOfVoxels - 1);
    
    // Get base coordinate and fractional part
    uint3 baseCoord = uint3(floor(voxelPos));
    float3 frac = voxelPos - float3(baseCoord);
    
    // Clamp base coordinate to ensure we don't go out of bounds
    baseCoord = min(baseCoord, numberOfVoxels - 2);
    
    // Sample 8 neighboring voxels
    FluidVoxel voxels[8];
    voxels[0] = GetFluidVoxel(baseCoord); // v000
    voxels[1] = GetFluidVoxel(baseCoord + uint3(1, 0, 0)); // v100
    voxels[2] = GetFluidVoxel(baseCoord + uint3(0, 1, 0)); // v010
    voxels[3] = GetFluidVoxel(baseCoord + uint3(1, 1, 0)); // v110
    voxels[4] = GetFluidVoxel(baseCoord + uint3(0, 0, 1)); // v001
    voxels[5] = GetFluidVoxel(baseCoord + uint3(1, 0, 1)); // v101
    voxels[6] = GetFluidVoxel(baseCoord + uint3(0, 1, 1)); // v011
    voxels[7] = GetFluidVoxel(baseCoord + uint3(1, 1, 1)); // v111
    
    // Trilinear interpolation
    FluidVoxel result;

    // Count voxel materialIndex and assign dominant material
    uint material_index_count[numberOfMaterials];
    
    for (uint i = 0; i < 8; i++)
    {
        if (voxels[i].voxel.GetValue() > 0.0f) // Skip empty voxels
            continue;
        material_index_count[voxels[i].voxel.materialIndex]++;
    }

    int dominantMaterialIndex = 0;
    for (uint j = 0; j < numberOfMaterials; j++)
    {
        if (material_index_count[j] > material_index_count[dominantMaterialIndex])
        {
            dominantMaterialIndex = j;
        }
    }
    
    result.voxel.materialIndex = dominantMaterialIndex;
        

    // Interpolate Value & Gradient
    float4 v00 = lerp(voxels[0].voxel.valueAndGradient, voxels[1].voxel.valueAndGradient, frac.x);
    float4 v10 = lerp(voxels[2].voxel.valueAndGradient, voxels[3].voxel.valueAndGradient, frac.x);
    float4 v01 = lerp(voxels[4].voxel.valueAndGradient, voxels[5].voxel.valueAndGradient, frac.x);
    float4 v11 = lerp(voxels[6].voxel.valueAndGradient, voxels[7].voxel.valueAndGradient, frac.x);
    float4 v0 = lerp(v00, v10, frac.y);
    float4 v1 = lerp(v01, v11, frac.y);
    result.voxel.valueAndGradient = lerp(v0, v1, frac.z);
    
    // Interpolate velocity
    float3 vel00 = lerp(voxels[0].velocity, voxels[1].velocity, frac.x);
    float3 vel10 = lerp(voxels[2].velocity, voxels[3].velocity, frac.x);
    float3 vel01 = lerp(voxels[4].velocity, voxels[5].velocity, frac.x);
    float3 vel11 = lerp(voxels[6].velocity, voxels[7].velocity, frac.x);
    float3 vel0 = lerp(vel00, vel10, frac.y);
    float3 vel1 = lerp(vel01, vel11, frac.y);
    result.velocity = lerp(vel0, vel1, frac.z);
    
    // Interpolate pressure
    float p00 = lerp(voxels[0].pressure, voxels[1].pressure, frac.x);
    float p10 = lerp(voxels[2].pressure, voxels[3].pressure, frac.x);
    float p01 = lerp(voxels[4].pressure, voxels[5].pressure, frac.x);
    float p11 = lerp(voxels[6].pressure, voxels[7].pressure, frac.x);
    float p0 = lerp(p00, p10, frac.y);
    float p1 = lerp(p01, p11, frac.y);
    result.pressure = lerp(p0, p1, frac.z);

    // Interpolate temperature
    float t00 = lerp(voxels[0].temperature, voxels[1].temperature, frac.x);
    float t10 = lerp(voxels[2].temperature, voxels[3].temperature, frac.x);
    float t01 = lerp(voxels[4].temperature, voxels[5].temperature, frac.x);
    float t11 = lerp(voxels[6].temperature, voxels[7].temperature, frac.x);
    float t0 = lerp(t00, t10, frac.y);
    float t1 = lerp(t01, t11, frac.y);
    result.temperature = lerp(t0, t1, frac.z);
    
    return result;
}

#endif