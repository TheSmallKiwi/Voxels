using System;
using Unity.Mathematics;
using UnityEngine;

namespace Tuntenfisch.Fluids
{
    [CreateAssetMenu(fileName = "Fluid Simulation Config", menuName = "Fluids/Fluid Simulation Config")]
    public class FluidSimulationConfig : ScriptableObject
    {
        public event Action OnDirtied;
        public event Action OnLateDirtied;

        [Header("Compute Shader")]
        [SerializeField] private ComputeShader m_compute;

        [Header("Physical Properties")]
        [Range(0.0f, 1.0f)]
        [SerializeField] private float m_viscosity = 0.01f;
        
        [SerializeField] private Vector3 m_gravity = new Vector3(0, -9.81f, 0);
        
        [Range(0.01f, 2.0f)]
        [SerializeField] private float m_fluidDensityThreshold = 0.5f;

        [Header("Simulation Parameters")]
        [Range(0.001f, 0.1f)]
        [SerializeField] private float m_maxTimeStep = 0.016f;
        
        [Range(1, 50)]
        [SerializeField] private int m_maxPressureIterations = 20;
        
        [Range(0.5f, 2.0f)]
        [SerializeField] private float m_pressureRelaxation = 1.0f;

        [Header("Boundary Conditions")]
        [SerializeField] private bool m_enableNoSlipBoundaries = true;
        
        [Range(0.0f, 1.0f)]
        [SerializeField] private float m_boundaryFriction = 0.8f;

        [Header("Stability")]
        [Range(0.9f, 1.0f)]
        [SerializeField] private float m_dampingFactor = 0.99f;
        
        [Range(0.001f, 1.0f)]
        [SerializeField] private float m_minFluidDensity = 0.001f;

        // Properties
        public ComputeShader Compute => m_compute;
        public float Viscosity => m_viscosity;
        public Vector3 Gravity => m_gravity;
        public float FluidDensityThreshold => m_fluidDensityThreshold;
        public float MaxTimeStep => m_maxTimeStep;
        public int MaxPressureIterations => m_maxPressureIterations;
        public float PressureRelaxation => m_pressureRelaxation;
        public bool EnableNoSlipBoundaries => m_enableNoSlipBoundaries;
        public float BoundaryFriction => m_boundaryFriction;
        public float DampingFactor => m_dampingFactor;
        public float MinFluidDensity => m_minFluidDensity;

        private void OnValidate()
        {
            OnDirtied?.Invoke();
            OnLateDirtied?.Invoke();
        }
    }
}