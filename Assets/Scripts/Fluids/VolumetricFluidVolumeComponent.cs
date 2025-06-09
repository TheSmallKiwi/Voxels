using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Tuntenfisch.Rendering
{
    [Serializable]
    public class VolumetricFluidVolumeComponent : VolumeComponent
    {
        [Header("Volumetric Properties")]
        public ClampedIntParameter maxRaySteps = new ClampedIntParameter(128, 32, 256);
        public ClampedFloatParameter stepSize = new ClampedFloatParameter(0.5f, 0.1f, 2.0f);
        public ClampedFloatParameter densityThreshold = new ClampedFloatParameter(0.01f, 0.001f, 0.1f);
        
        [Header("Lighting")]
        public ClampedFloatParameter absorptionStrength = new ClampedFloatParameter(1.0f, 0.0f, 5.0f);
        public ClampedFloatParameter scatteringStrength = new ClampedFloatParameter(0.5f, 0.0f, 2.0f);
        public ColorParameter fluidColor = new ColorParameter(new Color(0.2f, 0.6f, 1.0f, 1.0f));
    }
}