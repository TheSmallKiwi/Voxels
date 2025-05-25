using UnityEngine;
using UnityEditor;
using Tuntenfisch.Voxels.Procedural;
using System.IO;
using Unity.Mathematics;

namespace Tuntenfisch.Editor.Voxels
{
    [CustomEditor(typeof(NoiseNode))]
    public class NoiseNodeEditor : UnityEditor.Editor
    {
        private SerializedProperty m_positionProperty;
        private SerializedProperty m_valueAndGradientProperty;
        private SerializedProperty m_noiseParametersProperty;

        private bool m_noisePreviewFoldout = true;
        private const int m_previewSize = 256;

        // Compute shader resources
        private ComputeShader m_noisePreviewCompute;
        [SerializeField] private RenderTexture m_previewRenderTexture;
        private ComputeBuffer m_noiseParametersBuffer;
        private int m_noisePreviewKernel = -1;
        private bool m_isInitialized = false;

        private void OnEnable()
        {
            m_positionProperty = serializedObject.FindProperty("m_position");
            m_valueAndGradientProperty = serializedObject.FindProperty("m_valueAndGradient");
            m_noiseParametersProperty = serializedObject.FindProperty("m_noiseParameters");

            // Create texture for noise preview
            new Texture2D(m_previewSize, m_previewSize, TextureFormat.RGBA32, false);

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

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            // Draw default XNode inputs/outputs
            DrawDefaultInspector();

            // Add custom preview section
            EditorGUILayout.Space(10);
            m_noisePreviewFoldout =
                EditorGUILayout.Foldout(m_noisePreviewFoldout, "Noise Preview", EditorStyles.foldoutHeader);
            if (m_noisePreviewFoldout)
            {
                if (!m_isInitialized)
                {
                    EditorGUILayout.HelpBox("Compute resources could not be initialized. Check console for errors.",
                        MessageType.Error);
                    if (GUILayout.Button("Retry Initialization"))
                    {
                        InitializeCompute();
                    }

                    return;
                }

                // Draw preview options
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("2D XZ Preview"))
                {
                    GenerateNoisePreview(0); // XZ plane
                }

                if (GUILayout.Button("2D XY Preview"))
                {
                    GenerateNoisePreview(1); // XY plane
                }

                EditorGUILayout.EndHorizontal();

                // Display the preview texture
                if (m_previewRenderTexture != null)
                {
                    Rect rect = GUILayoutUtility.GetRect(m_previewSize, m_previewSize);
                    EditorGUI.DrawPreviewTexture(rect, m_previewRenderTexture);
                }
            }

            serializedObject.ApplyModifiedProperties();
        }

        private void GenerateNoisePreview(int previewPlane)
        {
            if (!m_isInitialized || m_noisePreviewCompute == null || m_previewRenderTexture == null ||
                m_noiseParametersBuffer == null)
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

        private void OnDisable()
        {
            // Clean up
            if (m_previewRenderTexture != null)
            {
                m_previewRenderTexture.Release();
                m_previewRenderTexture = null;
            }

            if (m_noiseParametersBuffer != null)
            {
                m_noiseParametersBuffer.Release();
                m_noiseParametersBuffer = null;
            }
        }
    }
}