using Tuntenfisch.World;
using UnityEngine;

public class WaterDebugViz : MonoBehaviour
{
    [Header("Simulation Data")]
    public RenderTexture fluidVolume3D; // Assign this from your simulation (3D RenderTexture)
    [Range(0f, 1f)]
    public float sliceDepth = 0.5f;
    public float densityScale = 1.0f;

    private Material _material;

    void Start()
    {
        _material = GetComponent<Renderer>().material;

        if (!fluidVolume3D)
        {
            Debug.LogWarning("No fluid volume texture assigned to FluidVisualizer.");
        }
    }

    void Update()
    {
        var activeFluidChunks = WorldManager.Instance.GetActiveFluidChunks();
        if (activeFluidChunks.Count == 0) return;
        fluidVolume3D = activeFluidChunks[0].FluidData.FluidTextures.DensityRead;
        
        if (_material && fluidVolume3D)
        {
            _material.SetTexture("_FluidTex", fluidVolume3D);
            _material.SetFloat("_SliceDepth", sliceDepth);
            _material.SetFloat("_DensityScale", densityScale);
        }
    }
}
