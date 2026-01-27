using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public static class PaletteTextureArrayBuilder
{
    [MenuItem("Assets/Create/Palette Texture2DArray (generate)")]
    public static void CreatePaletteArray()
    {
        var colors = GetDefaultPalette256();
        const int size = 16; // 16x16 однотонні тайли

        var arr = new Texture2DArray(size, size, colors.Count, TextureFormat.RGBA32, false, true)
        {
            anisoLevel = 1,
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Repeat
        };

        var pixels = new Color[size * size];

        for (int i = 0; i < colors.Count; i++)
        {
            for (int p = 0; p < pixels.Length; p++) pixels[p] = colors[i];
            arr.SetPixels(pixels, i);
        }

        arr.Apply(false, false);

        string path = EditorUtility.SaveFilePanelInProject(
            "Save Palette Texture2DArray",
            "PaletteArray",
            "asset",
            "Pick a location for the generated palette array asset");

        if (!string.IsNullOrEmpty(path))
        {
            AssetDatabase.CreateAsset(arr, path);
            AssetDatabase.SaveAssets();
            EditorGUIUtility.PingObject(arr);
        }
    }

    static List<Color> GetDefaultPalette256()
    {
        // 32 базові кольори * 8 варіантів яскравості = 256.
        var baseColors = new List<Color>
        {
            new Color32(205, 189, 155, 255), // sand light
            new Color32(181, 161, 123, 255), // sand mid
            new Color32(138, 114,  83, 255), // dirt light
            new Color32(110,  88,  62, 255), // dirt mid
            new Color32( 78,  60,  44, 255), // dirt dark
            new Color32( 58,  50,  47, 255), // soil dark
            new Color32(105, 112, 117, 255), // stone cool
            new Color32( 88,  90,  94, 255), // stone mid
            new Color32( 60,  62,  68, 255), // stone dark
            new Color32( 46,  50,  56, 255), // basalt
            new Color32( 74,  84,  66, 255), // moss stone
            new Color32( 86, 112,  66, 255), // grass light
            new Color32( 70,  99,  55, 255), // grass mid
            new Color32( 54,  84,  44, 255), // grass dark
            new Color32( 93,  73,  44, 255), // wood light
            new Color32( 74,  57,  35, 255), // wood mid
            new Color32( 56,  42,  27, 255), // wood dark
            new Color32( 33, 120, 154, 255), // water shallow
            new Color32( 18,  88, 125, 255), // water mid
            new Color32( 10,  66, 102, 255), // water deep
            new Color32(188, 198, 210, 255), // snow light
            new Color32(160, 172, 186, 255), // snow mid
            new Color32(132, 144, 160, 255), // snow shaded
            new Color32(196, 110,  86, 255), // clay red
            new Color32(167,  96,  74, 255), // clay mid
            new Color32(137,  82,  66, 255), // clay dark
            new Color32(116, 130, 112, 255), // shale/leaf dark
            new Color32(146, 160, 124, 255), // leaf light
            new Color32(118, 148,  92, 255), // leaf mid
            new Color32(120, 120, 120, 255), // metal worn
            new Color32( 96,  96,  96, 255), // metal dark
            new Color32( 68,  68,  68, 255), // metal deep
        };

        var result = new List<Color>(256);
        float[] variants = { 0.7f, 0.8f, 0.9f, 1f, 1.1f, 1.2f, 1.3f, 1.4f };

        foreach (var c in baseColors)
        {
            foreach (var mul in variants)
            {
                var col = c * mul;
                col.a = 1f;
                col.r = Mathf.Clamp01(col.r);
                col.g = Mathf.Clamp01(col.g);
                col.b = Mathf.Clamp01(col.b);
                result.Add(col);
            }
        }

        return result;
    }
}

