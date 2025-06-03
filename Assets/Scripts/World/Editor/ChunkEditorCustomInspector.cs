using UnityEngine;
using UnityEditor;
using Tuntenfisch.World;

[CustomEditor(typeof(Chunk))]
public class ChunkEditor : Editor
{
    public override void OnInspectorGUI()
    {
        // Draw the default inspector layout
        DrawDefaultInspector();

        // Get a reference to the target Chunk
        Chunk chunk = (Chunk)target;

        // Add a button to the inspector
        if (GUILayout.Button("Run Simulation Step"))
        {
            // Call the method on the target Chunk.
            // Replace "RunStep" with the actual method name from your Chunk component.
            chunk.RunStep();
        }
    }
}