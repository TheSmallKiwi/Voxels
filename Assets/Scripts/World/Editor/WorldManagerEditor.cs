using Tuntenfisch.Editor;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace Tuntenfisch.World.Editor
{
    [CanEditMultipleObjects]
    [CustomEditor(typeof(WorldManager))]
    public class WorldManagerEditor : BaseEditor
    {
        private static int MaxNumberOfLods => 5;

        private static bool s_showWorldOptions = true;
        private static bool s_showChunkOptions = true;
        private static bool s_showLevelOfDetailOptions = true;
        private static bool s_showFluidDebugOptions = true;

        private SerializedProperty m_viewer;
        private SerializedProperty m_updateInterval;
        private SerializedProperty m_chunkPrefab;
        private SerializedProperty m_initialChunkPoolPopulation;
        private SerializedProperty m_lodDistances;
        private SerializedProperty m_enableFluidSimulation;

        private void OnEnable()
        {
            m_viewer = serializedObject.FindProperty("m_viewer");
            m_updateInterval = serializedObject.FindProperty("m_updateInterval");
            m_chunkPrefab = serializedObject.FindProperty("m_chunkPrefab");
            m_initialChunkPoolPopulation = serializedObject.FindProperty("m_initialChunkPoolPopulation");
            m_lodDistances = serializedObject.FindProperty("m_lodDistances");
            m_enableFluidSimulation = serializedObject.FindProperty("m_enableFluidSimulation");
        }

        public override void OnInspectorGUI()
        {
            WorldManager worldManager = (WorldManager)target;
            
            serializedObject.Update();

            DisplayScriptHeader();

            if (s_showWorldOptions = EditorGUILayout.BeginFoldoutHeaderGroup(s_showWorldOptions, "World"))
            {
                EditorGUILayout.PropertyField(m_viewer);
                EditorGUILayout.PropertyField(m_updateInterval);
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            if (s_showChunkOptions = EditorGUILayout.BeginFoldoutHeaderGroup(s_showChunkOptions, "Chunk"))
            {
                EditorGUILayout.PropertyField(m_chunkPrefab);
                EditorGUILayout.PropertyField(m_initialChunkPoolPopulation);
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            if (s_showLevelOfDetailOptions = EditorGUILayout.BeginFoldoutHeaderGroup(s_showLevelOfDetailOptions, "Level Of Detail"))
            {
                int lods = EditorGUILayout.IntSlider("Levels Of Detail", m_lodDistances.arraySize, 1, MaxNumberOfLods);

                if (m_lodDistances.arraySize != lods)
                {
                    m_lodDistances.arraySize = lods;
                }

                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField("Distances", EditorStyles.boldLabel);
                SerializedProperty lodDistance;
                float minValue = 0.0f;
                float maxValue;

                for (int index = 0; index < lods - 1; index++)
                {
                    maxValue = m_lodDistances.GetArrayElementAtIndex(index + 1).floatValue;
                    lodDistance = m_lodDistances.GetArrayElementAtIndex(index);
                    lodDistance.floatValue = EditorGUILayout.Slider($"Level Of Detail {index}", lodDistance.floatValue, 0.0f, m_lodDistances.GetArrayElementAtIndex(lods - 1).floatValue);
                    lodDistance.floatValue = math.clamp(lodDistance.floatValue, minValue, maxValue);
                    minValue = lodDistance.floatValue;
                }
                lodDistance = m_lodDistances.GetArrayElementAtIndex(lods - 1);
                lodDistance.floatValue = math.max(EditorGUILayout.FloatField($"Level Of Detail {lods - 1}", lodDistance.floatValue), minValue);
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            // Fluid Debug Section
            if (s_showFluidDebugOptions = EditorGUILayout.BeginFoldoutHeaderGroup(s_showFluidDebugOptions, "Fluid Debug"))
            {
                EditorGUILayout.PropertyField(m_enableFluidSimulation);
                
                EditorGUILayout.Space(5);
                
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField("Fluid Controls", EditorStyles.boldLabel);
                
                EditorGUILayout.BeginHorizontal();
                
                if (GUILayout.Button("Add Test Fluid Source"))
                {
                    if (Application.isPlaying)
                    {
                        worldManager.AddTestFluidSource();
                    }
                    else
                    {
                        EditorGUILayout.HelpBox("Must be in Play Mode to add fluid sources", MessageType.Info);
                    }
                }
                
                if (GUILayout.Button("Remove All Sources"))
                {
                    if (Application.isPlaying)
                    {
                        worldManager.RemoveAllFluidSources();
                    }
                }
                
                EditorGUILayout.EndHorizontal();
                
                EditorGUILayout.BeginHorizontal();
                
                if (GUILayout.Button("Regenerate All Fluid Meshes"))
                {
                    if (Application.isPlaying)
                    {
                        worldManager.ForceRegenerateAllFluidMeshes();
                    }
                    else
                    {
                        EditorGUILayout.HelpBox("Must be in Play Mode to regenerate fluid meshes", MessageType.Info);
                    }
                }
                
                EditorGUILayout.EndHorizontal();
                
                EditorGUILayout.Space(3);
                
                if (GUILayout.Button("Debug Chunk Fluid States"))
                {
                    if (Application.isPlaying)
                    {
                        worldManager.DebugChunkFluidStates();
                    }
                    else
                    {
                        Debug.Log("Must be in Play Mode to debug fluid states");
                    }
                }
                
                EditorGUILayout.EndVertical();
                
                // Display runtime info if in play mode
                if (Application.isPlaying)
                {
                    EditorGUILayout.Space(5);
                    EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                    EditorGUILayout.LabelField("Runtime Info", EditorStyles.boldLabel);
                    
                    var chunks = worldManager.GetActiveChunks();
                    int fluidChunks = 0;
                    foreach (var chunk in chunks.Values)
                    {
                        if (chunk.HasActiveFluid())
                            fluidChunks++;
                    }
                    
                    EditorGUILayout.LabelField($"Active Chunks: {chunks.Count}");
                    EditorGUILayout.LabelField($"Chunks with Fluid: {fluidChunks}");
                    EditorGUILayout.LabelField($"Fluid Simulation: {(WorldManager.FluidSimulation != null && WorldManager.FluidSimulation.IsSimulationEnabled ? "Enabled" : "Disabled")}");
                    
                    EditorGUILayout.EndVertical();
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            serializedObject.ApplyModifiedProperties();
            
            // Auto-refresh the inspector when in play mode to update runtime info
            if (Application.isPlaying && s_showFluidDebugOptions)
            {
                Repaint();
            }
        }
    }
}