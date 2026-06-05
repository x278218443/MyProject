using UnityEditor;
using UnityEngine;

/// <summary>
/// 监听 Assets/UI/Sprites/ 下的所有图片，自动设为 UI Sprite 规格。
/// </summary>
public class UITexturePostprocessor : AssetPostprocessor
{
    const string WatchPath = "Assets/UI/Sprites/";

    void OnPreprocessTexture()
    {
        if (!assetPath.StartsWith(WatchPath)) return;

        var importer = (TextureImporter)assetImporter;
        if (importer.textureType == TextureImporterType.Sprite &&
            importer.spritePixelsPerUnit == 100 &&
            !importer.mipmapEnabled)
            return; // 已配置过，跳过

        importer.textureType           = TextureImporterType.Sprite;
        importer.spriteImportMode      = SpriteImportMode.Single;
        importer.spritePixelsPerUnit   = 100;
        importer.filterMode            = FilterMode.Bilinear;
        importer.mipmapEnabled         = false;
        importer.alphaIsTransparency   = true;
        importer.textureCompression    = TextureImporterCompression.Uncompressed;

        // 根据图片实际尺寸自动设置 MaxSize，防止被 Unity 偷偷缩小
        importer.GetSourceTextureWidthAndHeight(out int w, out int h);
        int longest = System.Math.Max(w, h);
        importer.maxTextureSize = longest <= 512  ? 512
                                : longest <= 1024 ? 1024
                                : longest <= 2048 ? 2048
                                                  : 4096;
    }
}
