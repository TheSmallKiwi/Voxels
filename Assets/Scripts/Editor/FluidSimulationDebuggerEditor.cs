using UnityEngine;
using UnityEditor;
using Tuntenfisch.World;

namespace Tuntenfisch.Editor.World
{
    [CustomEditor(typeof(FluidSimulationDebugger))]
    public class FluidSimulationDebuggerEditor : UnityEditor.Editor
    {
        private FluidSimulationDebugger m_debugger;
        private bool m_showTextureSettings = true;
        private bool m_showDebugControls = true;
        private bool m_showTextureDisplay = true;
        
        // Texture display settings
        private const float TEXTURE_PREVIEW_SIZE = 200f;
        private const float TEXTURE_SPACING = 10f;

        private void OnEnable()
        {
            m_debugger = (FluidSimulationDebugger)target;
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            // Header
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Fluid Simulation Debugger", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            // Debug Controls Section
            DrawDebugControls();
            EditorGUILayout.Space();

            // Texture Settings Section
            DrawTextureSettings();
            EditorGUILayout.Space();

            // Texture Display Section
            DrawTextureDisplay();
            EditorGUILayout.Space();

            // Debug Information Section
            DrawDebugInformation();

            serializedObject.ApplyModifiedProperties();

            // Auto-repaint during play mode for real-time updates
            if (Application.isPlaying)
            {
                Repaint();
            }
        }

        private void DrawDebugControls()
        {
            m_showDebugControls = EditorGUILayout.BeginFoldoutHeaderGroup(m_showDebugControls, "Debug Controls");
            if (m_showDebugControls)
            {
                EditorGUILayout.HelpBox("Use these controls to test and debug the fluid simulation:", MessageType.Info);
                
                EditorGUILayout.BeginHorizontal();
                
                if (GUILayout.Button("Add Test Fluid Source (F1)", GUILayout.Height(30)))
                {
                    if (Application.isPlaying)
                    {
                        m_debugger.SendMessage("AddTestFluidSource", SendMessageOptions.DontRequireReceiver);
                    }
                    else
                    {
                        EditorGUILayout.HelpBox("Must be in Play Mode", MessageType.Warning);
                    }
                }

                if (GUILayout.Button("Debug Active Chunks (F2)", GUILayout.Height(30)))
                {
                    if (Application.isPlaying)
                    {
                        m_debugger.SendMessage("DebugActiveChunks", SendMessageOptions.DontRequireReceiver);
                    }
                }

                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();

                if (GUILayout.Button("Validate Fluid System (F3)", GUILayout.Height(30)))
                {
                    if (Application.isPlaying)
                    {
                        m_debugger.SendMessage("ValidateFluidBuffers", SendMessageOptions.DontRequireReceiver);
                    }
                }

                if (GUILayout.Button("Debug Chunk Textures (F6)", GUILayout.Height(30)))
                {
                    if (Application.isPlaying)
                    {
                        m_debugger.SendMessage("DebugChunkTextures", SendMessageOptions.DontRequireReceiver);
                    }
                }

                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();

                if (GUILayout.Button("Cycle Texture Slice (F7)", GUILayout.Height(30)))
                {
                    if (Application.isPlaying)
                    {
                        m_debugger.SendMessage("CycleThroughTextureSlices", SendMessageOptions.DontRequireReceiver);
                    }
                }

                EditorGUILayout.EndHorizontal();

                if (!Application.isPlaying)
                {
                    EditorGUILayout.HelpBox("Debug controls are only available during Play Mode", MessageType.Info);
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        private void DrawTextureSettings()
        {
            m_showTextureSettings = EditorGUILayout.BeginFoldoutHeaderGroup(m_showTextureSettings, "Texture Settings");
            if (m_showTextureSettings)
            {
                // Slice depth control
                var textureSliceDepthProp = serializedObject.FindProperty("m_textureSliceDepth");
                EditorGUILayout.PropertyField(textureSliceDepthProp, new GUIContent("Texture Slice Depth", "Which Z slice to display from the 3D textures (0 to texture depth - 1)"));

                EditorGUILayout.Space();

                // Display toggles
                var showDensityProp = serializedObject.FindProperty("m_showDensityTexture");
                var showVelocityProp = serializedObject.FindProperty("m_showVelocityTexture");
                var showPressureProp = serializedObject.FindProperty("m_showPressureTexture");

                EditorGUILayout.LabelField("Display Options", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(showDensityProp, new GUIContent("Show Density Texture"));
                EditorGUILayout.PropertyField(showVelocityProp, new GUIContent("Show Velocity Texture"));
                EditorGUILayout.PropertyField(showPressureProp, new GUIContent("Show Pressure Texture"));

                EditorGUILayout.Space();

                // Visualization multipliers
                var densityMultiplierProp = serializedObject.FindProperty("m_densityMultiplier");
                var velocityScaleProp = serializedObject.FindProperty("m_velocityScale");

                EditorGUILayout.LabelField("Visualization Scaling", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(densityMultiplierProp, new GUIContent("Density Multiplier", "Multiply density values for better visibility"));
                EditorGUILayout.PropertyField(velocityScaleProp, new GUIContent("Velocity Scale", "Scale velocity values for better visibility"));
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        private void DrawTextureDisplay()
        {
            m_showTextureDisplay = EditorGUILayout.BeginFoldoutHeaderGroup(m_showTextureDisplay, "Texture Display");
            if (m_showTextureDisplay)
            {
                if (!Application.isPlaying)
                {
                    EditorGUILayout.HelpBox("Texture display is only available during Play Mode", MessageType.Info);
                    EditorGUILayout.EndFoldoutHeaderGroup();
                    return;
                }

                // Get texture properties
                var densityTextureProp = serializedObject.FindProperty("m_currentDensitySlice");
                var velocityTextureProp = serializedObject.FindProperty("m_currentVelocitySlice");
                var pressureTextureProp = serializedObject.FindProperty("m_currentPressureSlice");
                var showDensityProp = serializedObject.FindProperty("m_showDensityTexture");
                var showVelocityProp = serializedObject.FindProperty("m_showVelocityTexture");
                var showPressureProp = serializedObject.FindProperty("m_showPressureTexture");

                // Calculate layout
                int textureCount = 0;
                if (showDensityProp.boolValue) textureCount++;
                if (showVelocityProp.boolValue) textureCount++;
                if (showPressureProp.boolValue) textureCount++;

                if (textureCount == 0)
                {
                    EditorGUILayout.HelpBox("Enable at least one texture display option above to see fluid data", MessageType.Info);
                    EditorGUILayout.EndFoldoutHeaderGroup();
                    return;
                }

                // Arrange textures horizontally or vertically based on available space
                bool useHorizontalLayout = textureCount <= 2;

                if (useHorizontalLayout)
                {
                    EditorGUILayout.BeginHorizontal();
                }

                // Draw density texture
                if (showDensityProp.boolValue)
                {
                    DrawTexturePreview("Density", densityTextureProp.objectReferenceValue as RenderTexture, 
                        "Shows fluid density. Black = no fluid, Blue to White = increasing density");
                    
                    if (useHorizontalLayout && textureCount > 1)
                        GUILayout.Space(TEXTURE_SPACING);
                }

                // Draw velocity texture
                if (showVelocityProp.boolValue)
                {
                    DrawTexturePreview("Velocity", velocityTextureProp.objectReferenceValue as RenderTexture,
                        "Shows fluid velocity. Color indicates direction (R=X, G=Y, B=Z), brightness indicates magnitude");
                    
                    if (useHorizontalLayout && showPressureProp.boolValue)
                        GUILayout.Space(TEXTURE_SPACING);
                }

                // Draw pressure texture
                if (showPressureProp.boolValue)
                {
                    DrawTexturePreview("Pressure", pressureTextureProp.objectReferenceValue as RenderTexture,
                        "Shows fluid pressure. Red = positive pressure, Blue = negative pressure");
                }

                if (useHorizontalLayout)
                {
                    EditorGUILayout.EndHorizontal();
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        private void DrawTexturePreview(string label, RenderTexture texture, string tooltip)
        {
            EditorGUILayout.BeginVertical(GUI.skin.box);

            // Label with tooltip
            EditorGUILayout.LabelField(new GUIContent(label, tooltip), EditorStyles.boldLabel);

            if (texture != null)
            {
                // Calculate aspect ratio
                float aspectRatio = (float)texture.width / texture.height;
                float previewWidth = TEXTURE_PREVIEW_SIZE;
                float previewHeight = TEXTURE_PREVIEW_SIZE / aspectRatio;

                // Draw texture preview
                Rect textureRect = GUILayoutUtility.GetRect(previewWidth, previewHeight, GUILayout.ExpandWidth(false));
                EditorGUI.DrawPreviewTexture(textureRect, texture);

                // Show texture info
                EditorGUILayout.LabelField($"Size: {texture.width}x{texture.height}", EditorStyles.miniLabel);
                EditorGUILayout.LabelField($"Format: {texture.format}", EditorStyles.miniLabel);
            }
            else
            {
                // Show placeholder
                Rect placeholderRect = GUILayoutUtility.GetRect(TEXTURE_PREVIEW_SIZE, TEXTURE_PREVIEW_SIZE * 0.6f);
                EditorGUI.DrawRect(placeholderRect, Color.grey);
                
                GUIStyle centeredStyle = new GUIStyle(GUI.skin.label);
                centeredStyle.alignment = TextAnchor.MiddleCenter;
                centeredStyle.normal.textColor = Color.white;
                GUI.Label(placeholderRect, "No Texture", centeredStyle);
                
                EditorGUILayout.LabelField("No texture available", EditorStyles.miniLabel);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawDebugInformation()
        {
            EditorGUILayout.LabelField("Debug Information", EditorStyles.boldLabel);

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Debug information is only available during Play Mode", MessageType.Info);
                return;
            }

            // Debug info text
            var debugInfoProp = serializedObject.FindProperty("m_debugInfo");
            if (debugInfoProp.stringValue != null && debugInfoProp.stringValue.Length > 0)
            {
                EditorGUILayout.TextArea(debugInfoProp.stringValue, EditorStyles.helpBox);
            }
            else
            {
                EditorGUILayout.HelpBox("No debug information available", MessageType.Info);
            }

            EditorGUILayout.Space();

            // Statistics
            EditorGUILayout.LabelField("Texture Statistics", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(GUI.skin.box);

            var maxDensityProp = serializedObject.FindProperty("m_maxDensityValue");
            var maxVelocityProp = serializedObject.FindProperty("m_maxVelocityMagnitude");
            var maxPressureProp = serializedObject.FindProperty("m_maxPressureValue");

            EditorGUILayout.LabelField($"Max Density: {maxDensityProp.floatValue:F4}");
            EditorGUILayout.LabelField($"Max Velocity: {maxVelocityProp.floatValue:F4}");
            EditorGUILayout.LabelField($"Max Pressure: {maxPressureProp.floatValue:F4}");

            EditorGUILayout.EndVertical();

            // Keyboard shortcuts reminder
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Keyboard Shortcuts", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(GUI.skin.box);
            EditorGUILayout.LabelField("F1 - Add Test Fluid Source", EditorStyles.miniLabel);
            EditorGUILayout.LabelField("F2 - Debug Active Chunks", EditorStyles.miniLabel);
            EditorGUILayout.LabelField("F3 - Validate Fluid System", EditorStyles.miniLabel);
            EditorGUILayout.LabelField("F4 - Toggle Volume Gizmos", EditorStyles.miniLabel);
            EditorGUILayout.LabelField("F6 - Debug Chunk Textures", EditorStyles.miniLabel);
            EditorGUILayout.LabelField("F7 - Cycle Texture Slice", EditorStyles.miniLabel);
            EditorGUILayout.EndVertical();
        }

        // Override to add custom scene GUI
        private void OnSceneGUI()
        {
            if (!Application.isPlaying)
                return;

            // Add scene view debugging visualization if needed
            Handles.BeginGUI();
            
            // Show fluid debug info in scene view
            GUILayout.BeginArea(new Rect(10, 10, 300, 100));
            GUILayout.BeginVertical(GUI.skin.box);
            
            GUILayout.Label("Fluid Debug Info", EditorStyles.boldLabel);
            var debugInfoProp = serializedObject.FindProperty("m_debugInfo");
            if (debugInfoProp.stringValue != null && debugInfoProp.stringValue.Length > 0)
            {
                GUILayout.Label(debugInfoProp.stringValue, EditorStyles.miniLabel);
            }
            
            GUILayout.EndVertical();
            GUILayout.EndArea();
            
            Handles.EndGUI();
        }
    }
}