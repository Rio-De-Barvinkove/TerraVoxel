using UnityEditor;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditorInternal;

[InitializeOnLoad]
public static class ForceActiveInputHandling
{
    static ForceActiveInputHandling()
    {
        EnsureBoth();
    }

    [MenuItem("Tools/TerraVoxel/Set Input Handling/Both")]
    public static void EnsureBoth()
    {
        // Force ProjectSettings.asset value regardless of Unity API changes.
        var obj = Unsupported.GetSerializedAssetInterfaceSingleton("ProjectSettings/ProjectSettings.asset");
        if (obj == null) return;
        var so = new SerializedObject(obj);
        var prop = so.FindProperty("activeInputHandler");
        if (prop != null)
        {
            prop.intValue = 2; // Both
            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
#endif

