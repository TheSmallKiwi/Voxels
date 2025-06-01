#ifndef TUNTENFISCH_FLUIDS_FLUID_VOXEL
#define TUNTENFISCH_FLUIDS_FLUID_VOXEL

#include "Assets/Compute/Voxels/Include/Voxel.hlsl"

struct FluidVoxel
{
    Voxel voxel; // valueAndGradient.x = fluidAmount; .yzw = ∇fluidAmount (for surface normals)
    float3 velocity;
    float pressure;
    float temperature;
    float density;

    bool IsFluid()
    {
        return voxel.materialIndex >= fluidMaterialStartIndex;
    }

    bool IsSolid()
    {
        return voxel.IsSolid() && voxel.materialIndex < fluidMaterialStartIndex;
    }

    static FluidVoxel Create()
    {
        Voxel voxel = Voxel::Create(float4(0, 0, 0, 1));
        return Create(voxel, float3(0, 0, 0), 0, 0, 0);
    }

    static FluidVoxel Create(Voxel voxel, float3 velocity, float pressure, float temperature, float density)
    {
        FluidVoxel fluidVoxel;
        fluidVoxel.voxel = voxel;
        fluidVoxel.velocity = velocity;
        fluidVoxel.pressure = pressure;
        fluidVoxel.temperature = temperature;
        fluidVoxel.density = density;
        return fluidVoxel;
    }
};

struct PackedFluidVoxel
{
    PackedVoxel packedVoxel;
    uint packedVelocityXY;
    uint packedVelocityZPressure;
    uint packedTemperatureDensity;

    static PackedFluidVoxel Create(PackedVoxel voxel, uint packedVelocityXY, uint packedVelocityZPressure,
                                   uint packedTemperatureDensity)
    {
        PackedFluidVoxel packedFluidVoxel;
        packedFluidVoxel.packedVoxel = voxel;
        packedFluidVoxel.packedVelocityXY = packedVelocityXY;
        packedFluidVoxel.packedVelocityZPressure = packedVelocityZPressure;
        packedFluidVoxel.packedTemperatureDensity = packedTemperatureDensity;
        return packedFluidVoxel;
    }
};

PackedFluidVoxel PackFluidVoxel(FluidVoxel fluidVoxel)
{
    PackedVoxel packedVoxel = PackVoxel(fluidVoxel.voxel);
    uint packedVelocityXY = PackFloats(fluidVoxel.velocity.xy);
    uint packedVelocityZPressure = PackFloats(float2(fluidVoxel.velocity.z, fluidVoxel.pressure));
    uint packedTemperatureDensity = PackFloats(float2(fluidVoxel.temperature, fluidVoxel.density));
    return PackedFluidVoxel::Create(packedVoxel, packedVelocityXY, packedVelocityZPressure, packedTemperatureDensity);
}

FluidVoxel UnpackFluidVoxel(PackedFluidVoxel packedFluidVoxel)
{
    Voxel voxel = UnpackVoxel(packedFluidVoxel.packedVoxel);
    float2 velocityXY = UnpackFloats(packedFluidVoxel.packedVelocityXY);
    float2 velocityZPressure = UnpackFloats(packedFluidVoxel.packedVelocityZPressure);
    float2 temperatureDensity = UnpackFloats(packedFluidVoxel.packedTemperatureDensity);
    return FluidVoxel::Create(voxel, float3(velocityXY, velocityZPressure.x), velocityZPressure.y, temperatureDensity.x,
                              temperatureDensity.y);
}

#endif
