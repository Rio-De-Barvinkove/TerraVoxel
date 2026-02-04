using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using TerraVoxel.Voxel.GPU;

namespace TerraVoxel.Editor
{
    /// <summary>
    /// Menu item to add GpuDrivenRenderFeature to the active URP Renderer when it does not appear in Add Renderer Feature dropdown.
    /// After adding, assign the GpuDrivenRenderer reference in the feature's Inspector.
    /// </summary>
    public static class GpuDrivenRenderFeatureEditor
    {
        const string MenuPath = "Tools/TerraVoxel/Add Gpu Driven Render Feature to URP Renderer";

        [MenuItem(MenuPath, false, 200)]
        public static void AddGpuDrivenRenderFeature()
        {
            var handled = new List<ScriptableRendererData>();
            int levels = QualitySettings.names.Length;
            for (int level = 0; level < levels; level++)
            {
                var asset = QualitySettings.GetRenderPipelineAssetAt(level) as UniversalRenderPipelineAsset;
                if (asset == null)
                    asset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
                if (asset == null)
                    continue;
                var data = GetDefaultRenderer(asset);
                if (data == null || handled.Contains(data))
                    continue;
                handled.Add(data);
                if (HasFeature<GpuDrivenRenderFeature>(data))
                {
                    Debug.Log("[TerraVoxel] Gpu Driven Render Feature already present on " + data.name + ".");
                    continue;
                }
                var feature = ScriptableObject.CreateInstance<GpuDrivenRenderFeature>();
                feature.name = typeof(GpuDrivenRenderFeature).Name;
                AddRenderFeature(data, feature);
                Debug.Log("[TerraVoxel] Added Gpu Driven Render Feature to " + data.name + ". Assign GpuDrivenRenderer reference in the feature's Inspector.");
            }
            if (handled.Count == 0)
                Debug.LogWarning("[TerraVoxel] No URP Renderer found. Set Graphics > Scriptable Render Pipeline Settings to your URP Asset, or add the feature manually: select your Renderer asset (e.g. PC_Renderer), Add Renderer Feature, look for 'Gpu Driven Render Feature'.");
        }

        static bool HasFeature<T>(ScriptableRendererData data) where T : ScriptableRendererFeature
        {
            foreach (var f in data.rendererFeatures)
            {
                if (f is T)
                    return true;
            }
            return false;
        }

        static int GetDefaultRendererIndex(UniversalRenderPipelineAsset asset)
        {
            var field = typeof(UniversalRenderPipelineAsset).GetField("m_DefaultRendererIndex", BindingFlags.NonPublic | BindingFlags.Instance);
            return field != null ? (int)field.GetValue(asset) : 0;
        }

        static ScriptableRendererData GetDefaultRenderer(UniversalRenderPipelineAsset asset)
        {
            if (asset == null) return null;
            var field = typeof(UniversalRenderPipelineAsset).GetField("m_RendererDataList", BindingFlags.NonPublic | BindingFlags.Instance);
            if (field == null) return null;
            var list = field.GetValue(asset) as ScriptableRendererData[];
            if (list == null || list.Length == 0) return null;
            int idx = Mathf.Clamp(GetDefaultRendererIndex(asset), 0, list.Length - 1);
            return list[idx];
        }

        static void AddRenderFeature(ScriptableRendererData data, ScriptableRendererFeature feature)
        {
            var so = new SerializedObject(data);
            var featuresProp = so.FindProperty("m_RendererFeatures");
            var mapProp = so.FindProperty("m_RendererFeatureMap");
            if (featuresProp == null || mapProp == null)
            {
                Debug.LogError("[TerraVoxel] URP renderer serialization layout changed (m_RendererFeatures/m_RendererFeatureMap). Add the feature manually.");
                return;
            }
            so.Update();
            if (EditorUtility.IsPersistent(data))
                AssetDatabase.AddObjectToAsset(feature, data);
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out long localId);
            featuresProp.arraySize++;
            featuresProp.GetArrayElementAtIndex(featuresProp.arraySize - 1).objectReferenceValue = feature;
            mapProp.arraySize++;
            mapProp.GetArrayElementAtIndex(mapProp.arraySize - 1).longValue = localId;
            if (EditorUtility.IsPersistent(data))
                AssetDatabase.SaveAssetIfDirty(data);
            so.ApplyModifiedProperties();
        }
    }
}
