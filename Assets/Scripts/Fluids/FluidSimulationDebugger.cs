using UnityEngine;
using Unity.Mathematics;
using Tuntenfisch.Fluids;
using Tuntenfisch.Voxels;
using Tuntenfisch.World;
using Tuntenfisch.Voxels.Materials;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using System.Collections.Generic;
using Tuntenfisch.Rendering;

namespace Tuntenfisch.World
{
    public class FluidSimulationDebugger : MonoBehaviour
    {
        [Header("Debug Settings")] [SerializeField]
        private bool m_enableDebugLogging = true;

        [SerializeField] private bool m_visualizeFluidVolume = false;
        private float m_debugSphereSize = 0.1f;
        private Color m_fluidColor = Color.blue;
        private Color m_solidColor = Color.gray;

        [Header("Texture Visualization")]

        [SerializeField][HideInInspector] private int m_textureSliceDepth = 32; // Which Z slice to display
        [SerializeField][HideInInspector] private float m_densityMultiplier = 10f; // Multiply density for visibility
        [SerializeField][HideInInspector] private float m_velocityScale = 5f; // Scale velocity vectors for visibility
        [SerializeField][HideInInspector] private bool m_showDensityTexture = true;
        [SerializeField][HideInInspector] private bool m_showVelocityTexture = true;
        [SerializeField][HideInInspector] private bool m_showPressureTexture = false;

        [Header("Texture Display Settings")] [SerializeField][HideInInspector]
        private Vector2 m_textureDisplaySize = new Vector2(256, 256);

        [SerializeField][HideInInspector] private Vector2 m_textureDisplayOffset = new Vector2(10, 10);

        [Header("Test Settings")] [SerializeField]
        private Vector3 m_testSourceOffset = Vector3.up * 5f;

        [SerializeField] private float m_testSourceRadius = 10f;
        [SerializeField] private float m_testSourceAmount = 10f;
        [SerializeField] private Vector3 m_testSourceVelocity = Vector3.up;
        [SerializeField] private float m_testSourceDuration = 10f;

        // Texture visualization resources
        private Material m_textureDisplayMaterial;
        private RenderTexture m_densitySliceTexture;
        private RenderTexture m_velocitySliceTexture;
        private RenderTexture m_pressureSliceTexture;
        private ComputeShader m_textureSliceCompute;
        private int m_extractSliceKernel;
        
        // Inspector-displayable texture references
        [Header("Current Texture Slices")] [SerializeField][HideInInspector]
        private RenderTexture m_currentDensitySlice;

        [SerializeField][HideInInspector] private RenderTexture m_currentVelocitySlice;
        [SerializeField][HideInInspector] private RenderTexture m_currentPressureSlice;

        [Header("Texture Debug Info")] [SerializeField][HideInInspector]
        private string m_debugInfo = "No active chunks";

        [SerializeField][HideInInspector] private float m_maxDensityValue = 0f;
        [SerializeField][HideInInspector] private float m_maxVelocityMagnitude = 0f;
        [SerializeField][HideInInspector] private float m_maxPressureValue = 0f;

        // Debug readback
        private ComputeBuffer m_debugReadbackBuffer;
        private FluidVoxelDebugData[] m_debugData;

        private Dictionary<int3, ChunkTextureDebugInfo> m_chunkTextureInfo =
            new Dictionary<int3, ChunkTextureDebugInfo>();

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct FluidVoxelDebugData
        {
            public float sdfValue;
            public float velocityMagnitude;
            public float pressure;
            public uint materialIndex;
        }

        private struct ChunkTextureDebugInfo
        {
            public bool hasValidTextures;
            public Vector3 worldPosition;
            public int3 textureSize;
            public float lastDensityMax;
            public float lastVelocityMax;
            public float lastPressureMax;
        }

        private void Start()
        {
            InitializeTextureVisualization();

            // Create debug readback buffer
            int voxelCount = GetComponent<VoxelConfig>().VoxelVolumeConfig.VoxelCount;
            m_debugReadbackBuffer = new ComputeBuffer(voxelCount,
                System.Runtime.InteropServices.Marshal.SizeOf<FluidVoxelDebugData>());
            m_debugData = new FluidVoxelDebugData[voxelCount];
        }

        private void InitializeTextureVisualization()
        {
            // Load texture slice extraction compute shader
            m_textureSliceCompute = Resources.Load<ComputeShader>("Compute/TextureSliceExtractor");
            if (m_textureSliceCompute == null)
            {
                Debug.LogWarning(
                    "TextureSliceExtractor compute shader not found in Resources folder. Texture visualization disabled.");
                return;
            }

            m_extractSliceKernel = m_textureSliceCompute.FindKernel("ExtractSlice");

            // Create slice textures for 2D display
            int sliceSize = 128; // Reasonable size for debug display
            m_densitySliceTexture = new RenderTexture(sliceSize, sliceSize, 0, RenderTextureFormat.RFloat)
            {
                enableRandomWrite = true,
                filterMode = FilterMode.Point
            };
            m_densitySliceTexture.Create();

            m_velocitySliceTexture = new RenderTexture(sliceSize, sliceSize, 0, RenderTextureFormat.ARGBFloat)
            {
                enableRandomWrite = true,
                filterMode = FilterMode.Point
            };
            m_velocitySliceTexture.Create();

            m_pressureSliceTexture = new RenderTexture(sliceSize, sliceSize, 0, RenderTextureFormat.RFloat)
            {
                enableRandomWrite = true,
                filterMode = FilterMode.Point
            };
            m_pressureSliceTexture.Create();

            // Create simple unlit material for texture display
            CreateTextureDisplayMaterial();
        }

        private void CreateTextureDisplayMaterial()
        {
            m_textureDisplayMaterial = new Material(Shader.Find("Unlit/Texture"));
        }

        private void OnDestroy()
        {
            m_debugReadbackBuffer?.Release();

            // Clean up texture visualization resources
            if (m_densitySliceTexture != null)
            {
                m_densitySliceTexture.Release();
            }

            if (m_velocitySliceTexture != null)
            {
                m_velocitySliceTexture.Release();
            }

            if (m_pressureSliceTexture != null)
            {
                m_pressureSliceTexture.Release();
            }

            if (m_textureDisplayMaterial != null)
            {
                DestroyImmediate(m_textureDisplayMaterial);
            }
        }

        private void Update()
        {
            if (InputSystem.GetDevice<Keyboard>().f1Key.wasPressedThisFrame)
            {
                AddTestFluidSource();
            }

            if (InputSystem.GetDevice<Keyboard>().f2Key.wasPressedThisFrame)
            {
                DebugActiveChunks();
            }

            if (InputSystem.GetDevice<Keyboard>().f3Key.wasPressedThisFrame)
            {
                ValidateFluidBuffers();
            }

            if (InputSystem.GetDevice<Keyboard>().f4Key.wasPressedThisFrame)
            {
                m_visualizeFluidVolume = !m_visualizeFluidVolume;
                Debug.Log($"Fluid volume visualization: {(m_visualizeFluidVolume ? "ON" : "OFF")}");
            }

            if (InputSystem.GetDevice<Keyboard>().f6Key.wasPressedThisFrame)
            {
                DebugChunkTextures();
            }

            if (InputSystem.GetDevice<Keyboard>().f7Key.wasPressedThisFrame)
            {
                CycleThroughTextureSlices();
            }

            // Update Inspector textures and info
            UpdateInspectorDisplay();
        }

        private void UpdateInspectorDisplay()
        {
            var firstActiveChunk = GetFirstActiveChunk();
            if (firstActiveChunk == null || firstActiveChunk.FluidData?.IsValid() != true)
            {
                m_debugInfo = "No active fluid chunks found";
                return;
            }

            var fluidData = firstActiveChunk.FluidData;
            var textures = fluidData.FluidTextures;

            m_debugInfo = $"Chunk: {fluidData.WorldPosition}\n" +
                          $"Slice: {m_textureSliceDepth}/{textures.DensityRead.volumeDepth}\n" +
                          $"Size: {textures.DensityRead.width}x{textures.DensityRead.height}x{textures.DensityRead.volumeDepth}\n" +
                          $"Has Source: {fluidData.HasFluidSource}";

            // Extract texture slices for Inspector display
            ExtractTextureSlicesForInspector(textures);
        }

        private void ExtractTextureSlicesForInspector(ChunkFluidTextures fluidTextures)
        {
            if (m_textureSliceCompute == null || fluidTextures == null || !fluidTextures.IsValid())
                return;

            int textureDepth = fluidTextures.DensityRead.volumeDepth;
            int clampedSliceDepth = Mathf.Clamp(m_textureSliceDepth, 0, textureDepth - 1);

            // Extract density slice
            if (m_showDensityTexture)
            {
                m_textureSliceCompute.SetTexture(m_extractSliceKernel, "SourceTexture3D", fluidTextures.DensityWrite);
                m_textureSliceCompute.SetTexture(m_extractSliceKernel, "TargetTexture2D", m_densitySliceTexture);
                m_textureSliceCompute.SetInt("SliceDepth", clampedSliceDepth);
                m_textureSliceCompute.SetFloat("ValueMultiplier", m_densityMultiplier);
                m_textureSliceCompute.SetInt("TextureDepth", textureDepth);

                int threadGroupsX = Mathf.CeilToInt(m_densitySliceTexture.width / 8.0f);
                int threadGroupsY = Mathf.CeilToInt(m_densitySliceTexture.height / 8.0f);
                m_textureSliceCompute.Dispatch(m_extractSliceKernel, threadGroupsX, threadGroupsY, 1);

                // Update Inspector reference
                m_currentDensitySlice = m_densitySliceTexture;
            }

            // Extract velocity slice
            if (m_showVelocityTexture)
            {
                m_textureSliceCompute.SetTexture(m_extractSliceKernel, "SourceTexture3D", fluidTextures.VelocityWrite);
                m_textureSliceCompute.SetTexture(m_extractSliceKernel, "TargetTexture2D", m_velocitySliceTexture);
                m_textureSliceCompute.SetInt("SliceDepth", clampedSliceDepth);
                m_textureSliceCompute.SetFloat("ValueMultiplier", m_velocityScale);
                m_textureSliceCompute.SetInt("TextureDepth", textureDepth);

                int threadGroupsX = Mathf.CeilToInt(m_velocitySliceTexture.width / 8.0f);
                int threadGroupsY = Mathf.CeilToInt(m_velocitySliceTexture.height / 8.0f);
                m_textureSliceCompute.Dispatch(m_extractSliceKernel, threadGroupsX, threadGroupsY, 1);

                // Update Inspector reference
                m_currentVelocitySlice = m_velocitySliceTexture;
            }

            // Extract pressure slice
            if (m_showPressureTexture)
            {
                m_textureSliceCompute.SetTexture(m_extractSliceKernel, "SourceTexture3D", fluidTextures.PressureWrite);
                m_textureSliceCompute.SetTexture(m_extractSliceKernel, "TargetTexture2D", m_pressureSliceTexture);
                m_textureSliceCompute.SetInt("SliceDepth", clampedSliceDepth);
                m_textureSliceCompute.SetFloat("ValueMultiplier", 1.0f);
                m_textureSliceCompute.SetInt("TextureDepth", textureDepth);

                int threadGroupsX = Mathf.CeilToInt(m_pressureSliceTexture.width / 8.0f);
                int threadGroupsY = Mathf.CeilToInt(m_pressureSliceTexture.height / 8.0f);
                m_textureSliceCompute.Dispatch(m_extractSliceKernel, threadGroupsX, threadGroupsY, 1);

                // Update Inspector reference
                m_currentPressureSlice = m_pressureSliceTexture;
            }

            // Update statistics
            UpdateTextureStatistics(fluidTextures);
        }

        private void UpdateTextureStatistics(ChunkFluidTextures textures)
        {
            // Simple approach: just read a few sample values to estimate ranges
            // In a full implementation, you'd use a compute shader to calculate proper min/max

            m_maxDensityValue = 1.0f; // Placeholder - would compute actual max
            m_maxVelocityMagnitude = 10.0f; // Placeholder - would compute actual max
            m_maxPressureValue = 5.0f; // Placeholder - would compute actual max
        }

        private Chunk GetFirstActiveChunk()
        {
            var chunks = WorldManager.Instance.GetActiveChunks();
            foreach (var chunk in chunks.Values)
            {
                if (chunk.HasActiveFluid() && chunk.FluidData != null && chunk.FluidData.IsValid())
                {
                    return chunk;
                }
            }

            return null;
        }

        private ChunkTextureDebugInfo GetChunkTextureInfo(Chunk chunk)
        {
            var coordinate = chunk.transform.position; // Using position as key for simplicity
            int3 coord = new int3((int)coordinate.x, (int)coordinate.y, (int)coordinate.z);

            if (m_chunkTextureInfo.TryGetValue(coord, out var info))
                return info;

            return new ChunkTextureDebugInfo();
        }

        private void UpdateChunkTextureInfo()
        {
            // This method is now simplified since we're using Inspector display
            var chunks = WorldManager.Instance.GetActiveChunks();
            m_chunkTextureInfo.Clear();

            foreach (var kvp in chunks)
            {
                var chunk = kvp.Value;
                if (chunk.FluidData != null && chunk.FluidData.IsValid())
                {
                    var info = new ChunkTextureDebugInfo
                    {
                        hasValidTextures = true,
                        worldPosition = chunk.FluidData.WorldPosition,
                        textureSize = new int3(
                            chunk.FluidData.FluidTextures.DensityRead.width,
                            chunk.FluidData.FluidTextures.DensityRead.height,
                            chunk.FluidData.FluidTextures.DensityRead.volumeDepth
                        )
                    };

                    m_chunkTextureInfo[kvp.Key] = info;
                }
            }
        }
        
        [ContextMenu("Debug Volumetric Renderer")]
        private void DebugVolumetricRenderer()
        {
            var fRenderer = FindFirstObjectByType<VolumetricFluidRenderer>();
            if (fRenderer == null)
            {
                Debug.LogError("VolumetricFluidRenderer not found!");
                return;
            }
    
            Debug.Log($"Active chunks in renderer: {fRenderer.GetActiveChunkCount()}");
    
            // Check if any chunks have valid fluid data
            var chunks = WorldManager.Instance.GetActiveChunks();
            foreach (var chunk in chunks.Values)
            {
                if (chunk.HasActiveFluid() && chunk.FluidData?.IsValid() == true)
                {
                    var textures = chunk.FluidData.FluidTextures;
                    Debug.Log($"Chunk textures - Density: {textures.DensityRead?.IsCreated()}, " +
                              $"Velocity: {textures.VelocityRead?.IsCreated()}, " +
                              $"Size: {textures.DensityRead?.width}x{textures.DensityRead?.height}x{textures.DensityRead?.volumeDepth}");
                }
            }
        }

        private void CycleThroughTextureSlices()
        {
            var firstChunk = GetFirstActiveChunk();
            if (firstChunk?.FluidData?.FluidTextures?.DensityRead == null) return;
            int maxDepth = firstChunk.FluidData.FluidTextures.DensityRead.volumeDepth;
            m_textureSliceDepth = (m_textureSliceDepth + 1) % maxDepth;
            Debug.Log($"Switched to texture slice depth: {m_textureSliceDepth}/{maxDepth}");
        }

        // Enhanced debug methods
        private void AddTestFluidSource()
        {
            Vector3 sourcePos = transform.position + m_testSourceOffset;

            Debug.Log($"[FluidDebug] Adding test fluid source at {sourcePos}");

            WorldManager.Instance.AddFluidSource(
                sourcePos,
                m_testSourceVelocity,
                m_testSourceRadius,
                m_testSourceAmount,
                MaterialIndex.Water,
                m_testSourceDuration            );
        }

        private void DebugActiveChunks()
        {
            var chunks = WorldManager.Instance.GetActiveChunks();
            int totalChunks = chunks.Count;
            int fluidChunks = 0;
            int validTextureChunks = 0;

            foreach (var kvp in chunks)
            {
                var chunk = kvp.Value;
                if (chunk.HasActiveFluid())
                {
                    fluidChunks++;

                    if (chunk.FluidData?.IsValid() == true)
                    {
                        validTextureChunks++;
                        Debug.Log(
                            $"[FluidDebug] Chunk {kvp.Key} has valid fluid textures at {chunk.FluidData.WorldPosition}");
                    }
                }
            }

            Debug.Log(
                $"[FluidDebug] Active chunks: {totalChunks}, With fluid: {fluidChunks}, With valid textures: {validTextureChunks}");
        }

        private void ValidateFluidBuffers()
        {
            Debug.Log("[FluidDebug] Validating fluid system configuration...");

            // Check FluidSimulation component
            if (!WorldManager.FluidSimulation)
            {
                Debug.LogError("[FluidDebug] FluidSimulation component is null!");
                return;
            }

            Debug.Log($"[FluidDebug] FluidSimulation enabled: {WorldManager.FluidSimulation.IsSimulationEnabled}");

            // Check compute shader and kernels
            var config = WorldManager.VoxelConfig.FluidSimulationConfig;
            if (config?.Compute == null)
            {
                Debug.LogError("[FluidDebug] Fluid compute shader is null!");
                return;
            }

            Debug.Log($"[FluidDebug] Fluid config found with compute shader: {config.Compute.name}");
            Debug.Log("[FluidDebug] Validation complete");
        }

        private void DebugChunkTextures()
        {
            Debug.Log("=== Chunk Fluid Texture Debug ===");

            var chunks = WorldManager.Instance.GetActiveChunks();
            foreach (var kvp in chunks)
            {
                var chunk = kvp.Value;
                if (chunk.HasActiveFluid() && chunk.FluidData != null)
                {
                    var fluidData = chunk.FluidData;
                    Debug.Log($"Chunk {kvp.Key}:");
                    Debug.Log($"  World Position: {fluidData.WorldPosition}");
                    Debug.Log($"  Fluid Textures Valid: {fluidData.IsValid()}");
                    Debug.Log($"  Has Fluid Source: {fluidData.HasFluidSource}");

                    if (fluidData.FluidSource != null)
                    {
                        Debug.Log($"  Source Position: {fluidData.FluidSource.Position}");
                        Debug.Log($"  Source Amount: {fluidData.FluidSource.Amount}");
                        Debug.Log($"  Source Radius: {fluidData.FluidSource.Radius}");
                        Debug.Log($"  Source Active: {fluidData.FluidSource.IsActive}");
                        Debug.Log($"  Source Should Be Active: {fluidData.FluidSource.ShouldBeActive()}");
                    }

                    if (fluidData.FluidTextures != null)
                    {
                        var textures = fluidData.FluidTextures;
                        Debug.Log(
                            $"  Velocity Texture: {textures.VelocityRead?.width}x{textures.VelocityRead?.height}x{textures.VelocityRead?.volumeDepth}");
                        Debug.Log(
                            $"  Density Texture: {textures.DensityRead?.width}x{textures.DensityRead?.height}x{textures.DensityRead?.volumeDepth}");
                        Debug.Log($"  Velocity Format: {textures.VelocityRead?.format}");
                        Debug.Log($"  Density Format: {textures.DensityRead?.format}");
                        Debug.Log($"  Velocity Created: {textures.VelocityRead?.IsCreated()}");
                        Debug.Log($"  Density Created: {textures.DensityRead?.IsCreated()}");
                    }
                }
            }

            Debug.Log("==============================");
        }

        [ContextMenu("Debug Density Values")]
        private void DebugDensityValues()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("Must be in play mode to debug density values");
                return;
            }

            var firstChunk = GetFirstActiveChunk();
            if (firstChunk?.FluidData?.FluidTextures?.DensityRead != null)
            {
                StartCoroutine(ReadbackDensityTexture(firstChunk.FluidData.FluidTextures.DensityRead));
            }
            else
            {
                Debug.LogWarning("No active chunk with valid density texture found");
            }
        }

        private System.Collections.IEnumerator ReadbackDensityTexture(RenderTexture densityTexture)
        {
            // Create a temporary 2D texture for readback
            var temp2D = new RenderTexture(densityTexture.width, densityTexture.height, 0, RenderTextureFormat.RFloat);
            temp2D.Create();

            // Extract middle slice
            int middleSlice = densityTexture.volumeDepth / 2;
            if (m_textureSliceCompute != null)
            {
                m_textureSliceCompute.SetTexture(m_extractSliceKernel, "SourceTexture3D", densityTexture);
                m_textureSliceCompute.SetTexture(m_extractSliceKernel, "TargetTexture2D", temp2D);
                m_textureSliceCompute.SetInt("SliceDepth", middleSlice);
                m_textureSliceCompute.SetFloat("ValueMultiplier", 1.0f);
                m_textureSliceCompute.SetInt("TextureDepth", densityTexture.volumeDepth);

                int threadGroupsX = Mathf.CeilToInt(temp2D.width / 8.0f);
                int threadGroupsY = Mathf.CeilToInt(temp2D.height / 8.0f);
                m_textureSliceCompute.Dispatch(m_extractSliceKernel, threadGroupsX, threadGroupsY, 1);
            }

            yield return new WaitForEndOfFrame();

            // Read back the 2D slice
            var readbackTexture = new Texture2D(temp2D.width, temp2D.height, TextureFormat.RFloat, false);
            RenderTexture.active = temp2D;
            readbackTexture.ReadPixels(new Rect(0, 0, temp2D.width, temp2D.height), 0, 0);
            readbackTexture.Apply();
            RenderTexture.active = null;

            // Analyze the data
            Color[] pixels = readbackTexture.GetPixels();
            float minValue = float.MaxValue;
            float maxValue = float.MinValue;
            int nonZeroPixels = 0;

            for (int i = 0; i < pixels.Length; i++)
            {
                float value = pixels[i].r;
                if (value != 0)
                {
                    nonZeroPixels++;
                    minValue = Mathf.Min(minValue, value);
                    maxValue = Mathf.Max(maxValue, value);
                }
            }

            Debug.Log($"=== Density Texture Analysis (Slice {middleSlice}) ===");
            Debug.Log($"Texture Size: {readbackTexture.width}x{readbackTexture.height}");
            Debug.Log($"Total Pixels: {pixels.Length}");
            Debug.Log($"Non-Zero Pixels: {nonZeroPixels}");
            Debug.Log($"Min Value: {minValue}");
            Debug.Log($"Max Value: {maxValue}");
            Debug.Log($"Percentage Non-Zero: {(nonZeroPixels * 100f / pixels.Length):F2}%");

            // Sample a few specific pixels
            int centerX = readbackTexture.width / 2;
            int centerY = readbackTexture.height / 2;
            Debug.Log($"Center pixel value: {readbackTexture.GetPixel(centerX, centerY).r}");
            Debug.Log(
                $"Corner pixel values: TL={readbackTexture.GetPixel(0, 0).r}, TR={readbackTexture.GetPixel(readbackTexture.width - 1, 0).r}, BL={readbackTexture.GetPixel(0, readbackTexture.height - 1).r}, BR={readbackTexture.GetPixel(readbackTexture.width - 1, readbackTexture.height - 1).r}");

            // Cleanup
            DestroyImmediate(readbackTexture);
            temp2D.Release();
        }

        [ContextMenu("Debug Simulation Step")]
        private void DebugSimulationStep()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("Must be in play mode to debug simulation");
                return;
            }

            var firstChunk = GetFirstActiveChunk();
            if (firstChunk?.FluidData == null)
            {
                Debug.LogWarning("No active chunk found");
                return;
            }

            var fluidData = firstChunk.FluidData;
            Debug.Log("=== Simulation Step Debug ===");
            Debug.Log($"Chunk Position: {fluidData.WorldPosition}");
            Debug.Log($"Has Fluid Source: {fluidData.HasFluidSource}");

            if (fluidData.FluidSource != null)
            {
                var source = fluidData.FluidSource;
                Debug.Log($"Source Details:");
                Debug.Log($"  Position: {source.Position}");
                Debug.Log($"  Velocity: {source.Velocity}");
                Debug.Log($"  Radius: {source.Radius}");
                Debug.Log($"  Amount (sourceDensity): {source.Amount}");
                Debug.Log($"  Material: {source.Material}");
                Debug.Log($"  Is Active: {source.IsActive}");
                Debug.Log($"  Should Be Active: {source.ShouldBeActive()}");
                Debug.Log($"  Age: {source.GetAge():F2}s");
                Debug.Log($"  Remaining Time: {source.GetRemainingTime():F2}s");

                // Check if source position is within chunk bounds
                var chunkCenter = fluidData.WorldPosition;
                var chunkSize = WorldManager.VoxelConfig.VoxelVolumeConfig.VoxelVolumeDimensions;
                var chunkMin = chunkCenter - chunkSize * 0.5f;
                var chunkMax = chunkCenter + chunkSize * 0.5f;

                bool sourceInChunk = source.Position.x >= chunkMin.x && source.Position.x <= chunkMax.x &&
                                     source.Position.y >= chunkMin.y && source.Position.y <= chunkMax.y &&
                                     source.Position.z >= chunkMin.z && source.Position.z <= chunkMax.z;

                Debug.Log($"Source within chunk bounds: {sourceInChunk}");
                Debug.Log($"Chunk bounds: Min={chunkMin}, Max={chunkMax}");
                Debug.Log($"Distance from chunk center: {Vector3.Distance(source.Position, chunkCenter):F2}");
            }

            // Check simulation state
            Debug.Log($"Simulation Active: {fluidData.IsSimulationActive()}");
            Debug.Log($"Needs Simulation Update: {fluidData.NeedsSimulationUpdate()}");
            Debug.Log(
                $"WorldManager.FluidSimulation.IsSimulationEnabled: {WorldManager.FluidSimulation?.IsSimulationEnabled}");

            // Try to manually run one simulation step and log what happens
            if (WorldManager.FluidSimulation && WorldManager.FluidSimulation.IsSimulationEnabled)
            {
                Debug.Log("Manually running simulation step...");
                WorldManager.FluidSimulation.RequestSimulationAsync(fluidData,
                    success => Debug.Log("Simulation step completed"));
            }
        }

        private void OnDrawGizmos()
        {
            if (!m_visualizeFluidVolume || !Application.isPlaying)
                return;

            var chunks = WorldManager.Instance.GetActiveChunks();
            foreach (var kvp in chunks)
            {
                var chunk = kvp.Value;
                if (chunk.HasActiveFluid())
                {
                    // Draw chunk bounds
                    Gizmos.color = Color.cyan;
                    Gizmos.DrawWireCube(chunk.transform.position,
                        WorldManager.VoxelConfig.VoxelVolumeConfig.VoxelVolumeDimensions + 1f);

                    // Draw fluid source if present
                    var fluidData = chunk.FluidData;
                    if (fluidData != null && fluidData.HasFluidSource && fluidData.FluidSource != null)
                    {
                        Gizmos.color = m_fluidColor;
                        Gizmos.DrawWireSphere(fluidData.FluidSource.Position, fluidData.FluidSource.Radius);

                        // Draw velocity vector
                        Gizmos.color = Color.yellow;
                        Gizmos.DrawRay(fluidData.FluidSource.Position, fluidData.FluidSource.Velocity);
                    }
                }
            }
        }
    }
}