#ifndef TUNTENFISCH_FLUIDS_FLUID_VOXEL
#define TUNTENFISCH_FLUIDS_FLUID_VOXEL

#include "Assets/Compute/Voxels/Include/Voxel.hlsl"

struct FluidVoxel
{
    Voxel voxel; // valueAndGradient.x = fluidAmount; .yzw = ∇fluidAmount (for surface normals)
    float3 velocity;
    float pressure;
    float temperature;

    uint IsFluid()
    {
        return (voxel.GetValue() < 0.0f && voxel.materialIndex >= fluidMaterialStartIndex);
    }

    uint IsSolid()
    {
        return (voxel.GetValue() < 0.0f && voxel.materialIndex < fluidMaterialStartIndex);
    }

    static FluidVoxel Create()
    {
        Voxel voxel = Voxel::Create(0.0f);
        return Create(voxel, 0.0f, 0.0f, 0.0f);
    }

    static FluidVoxel Create(Voxel voxel, float3 velocity, float pressure, float temperature)
    {
        FluidVoxel fluidVoxel;
        fluidVoxel.voxel = voxel;
        fluidVoxel.velocity = velocity;
        fluidVoxel.pressure = pressure;
        fluidVoxel.temperature = temperature;
        return fluidVoxel;
    }
};

struct PackedFluidVoxel
{
    PackedVoxel packedVoxel;
    uint packedVelocityXY;
    uint packedVelocityZPressure;
    float packedTemperature;

    static PackedFluidVoxel Create(PackedVoxel voxel, uint packedVelocityXY, uint packedVelocityZPressure,
                                   float packedTemperature)
    {
        PackedFluidVoxel packedFluidVoxel;
        packedFluidVoxel.packedVoxel = voxel;
        packedFluidVoxel.packedVelocityXY = packedVelocityXY;
        packedFluidVoxel.packedVelocityZPressure = packedVelocityZPressure;
        packedFluidVoxel.packedTemperature = packedTemperature;
        return packedFluidVoxel;
    }
};

PackedFluidVoxel PackFluidVoxel(FluidVoxel fluidVoxel)
{
    PackedVoxel packedVoxel = PackVoxel(fluidVoxel.voxel);
    uint packedVelocityXY = PackFloats(fluidVoxel.velocity.xy);
    uint packedVelocityZPressure = PackFloats(float2(fluidVoxel.velocity.z, fluidVoxel.pressure));
    float packedTemperature = fluidVoxel.temperature;
    return PackedFluidVoxel::Create(packedVoxel, packedVelocityXY, packedVelocityZPressure, packedTemperature);
}

FluidVoxel UnpackFluidVoxel(PackedFluidVoxel packedFluidVoxel)
{
    Voxel voxel = UnpackVoxel(packedFluidVoxel.packedVoxel);
    float2 velocityXY = UnpackFloats(packedFluidVoxel.packedVelocityXY);
    float2 velocityZPressure = UnpackFloats(packedFluidVoxel.packedVelocityZPressure);
    float temperature = packedFluidVoxel.packedTemperature;
    return FluidVoxel::Create(voxel, float3(velocityXY, velocityZPressure.x), velocityZPressure.y, temperature);
}

#endif
