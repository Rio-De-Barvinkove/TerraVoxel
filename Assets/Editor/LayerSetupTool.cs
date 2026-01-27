using UnityEditor;
using UnityEngine;

public static class LayerSetupTool
{
    [MenuItem("Tools/TerraVoxel/Layers/Assign Selected To Terrain (Recursive)")]
    static void AssignSelectedToTerrain() => AssignSelected("Terrain");

    [MenuItem("Tools/TerraVoxel/Layers/Assign Selected To Objects (Recursive)")]
    static void AssignSelectedToObjects() => AssignSelected("Objects");

    [MenuItem("Tools/TerraVoxel/Layers/Assign Selected To Player (Recursive)")]
    static void AssignSelectedToPlayer() => AssignSelected("Player");

    static void AssignSelected(string layerName)
    {
        int layer = LayerMask.NameToLayer(layerName);
        if (layer < 0)
        {
            Debug.LogError($"Layer '{layerName}' not found. Check ProjectSettings/TagManager.asset.");
            return;
        }

        foreach (var t in Selection.transforms)
        {
            SetLayerRecursively(t.gameObject, layer);
        }
    }

    static void SetLayerRecursively(GameObject obj, int layer)
    {
        obj.layer = layer;
        foreach (Transform child in obj.transform)
        {
            SetLayerRecursively(child.gameObject, layer);
        }
    }
}

