using UnityEngine;
using UnityEditor;
using XNodeEditor;
using Tuntenfisch.Voxels.Procedural;

namespace Tuntenfisch.Editor.Voxels
{
    [CustomNodeEditor(typeof(NoiseNode))]
    public class NoiseNodeView : XNodeEditor.NodeEditor
    {
        private RenderTexture m_previewRenderTexture;
        private ComputeShader m_noisePreviewCompute;
        private ComputeBuffer m_noiseParametersBuffer;
        private int m_noisePreviewKernel = -1;
        private bool m_isInitialized = false;
        private const int m_previewSize = 200; // Smaller size for node view
        
        public override void OnCreate()
        {
            base.OnCreate();
            InitializeCompute();
        }

        private void InitializeCompute()
        {
            try
            {
                // Load the compute shader
                m_noisePreviewCompute = Resources.Load<ComputeShader>("Compute/NoisePreview");
                if (m_noisePreviewCompute == null)
                {
                    Debug.LogError("Could not find NoisePreviewCompute.compute in Resources folder!");
                    return;
                }

                // Find the kernel
                m_noisePreviewKernel = m_noisePreviewCompute.FindKernel("GenerateNoisePreview");
                
                // Set up resources
                SetupRenderTexture();
                CreateNoiseParametersBuffer();
                
                m_isInitialized = true;
                
                // Generate initial preview
                GenerateNoisePreview(0);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Error initializing compute resources: {e.Message}\n{e.StackTrace}");
                m_isInitialized = false;
            }
        }

        private void SetupRenderTexture()
        {
            // Create a render texture to hold the compute shader output
            if (m_previewRenderTexture != null)
                m_previewRenderTexture.Release();
                
            m_previewRenderTexture = new RenderTexture(m_previewSize, m_previewSize, 0, RenderTextureFormat.ARGB32);
            m_previewRenderTexture.enableRandomWrite = true;
            m_previewRenderTexture.Create();
        }

        private void CreateNoiseParametersBuffer()
        {
            // Create a compute buffer to hold noise parameters
            if (m_noiseParametersBuffer != null)
                m_noiseParametersBuffer.Release();
                
            // Create buffer for NoiseParameters struct
            m_noiseParametersBuffer = new ComputeBuffer(1, GPUNoiseParameters.SizeInBytes, ComputeBufferType.Default);
        }

        public override void OnBodyGUI()
        {
            // First draw the default node contents (inputs/outputs)
            base.OnBodyGUI();
            
            EditorGUILayout.Space(5);
            
            InitializeCompute();
            
            // Draw preview texture if initialized
            if (m_isInitialized && m_previewRenderTexture != null)
            {
                // Update preview when values change
                if (serializedObject.hasModifiedProperties || Event.current.type == EventType.ExecuteCommand)
                {
                    GenerateNoisePreview(0); // Use XZ plane as default for node view
                    serializedObject.ApplyModifiedProperties();
                }
                
                // Display the preview texture
                Rect rect = GUILayoutUtility.GetRect(m_previewSize, m_previewSize);
                EditorGUI.DrawPreviewTexture(rect, m_previewRenderTexture);
            }
            else
            {
                EditorGUILayout.LabelField("Preview not available");
            }
        }

        private void GenerateNoisePreview(int previewPlane)
        {
            if (!m_isInitialized || m_noisePreviewCompute == null || m_previewRenderTexture == null || m_noiseParametersBuffer == null)
            {
                Debug.LogError("Cannot generate preview: compute resources not initialized");
                return;
            }

            try
            {
                NoiseNode noiseNode = (NoiseNode)target;
                
                // Update the noise parameters buffer with current values
                m_noiseParametersBuffer.SetData(new[] { noiseNode.NoiseParameters });
                
                // Set compute shader parameters
                m_noisePreviewCompute.SetTexture(m_noisePreviewKernel, "Result", m_previewRenderTexture);
                m_noisePreviewCompute.SetBuffer(m_noisePreviewKernel, "noiseParameterBuffer", m_noiseParametersBuffer);
                m_noisePreviewCompute.SetInt("PreviewPlane", previewPlane);
                m_noisePreviewCompute.SetInt("TextureSize", m_previewSize);
                
                // Dispatch the compute shader
                int threadGroupsX = Mathf.CeilToInt(m_previewSize / 8.0f);
                int threadGroupsY = Mathf.CeilToInt(m_previewSize / 8.0f);
                m_noisePreviewCompute.Dispatch(m_noisePreviewKernel, threadGroupsX, threadGroupsY, 1);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Error generating noise preview: {e.Message}\n{e.StackTrace}");
            }
        }
        
        // public override void OnDisable()
        // {
        //     base.OnDisable();
        //     
        // }
    }
}