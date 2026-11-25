using System;
using System.Collections.Generic;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace Starlight.Game.Editor.LocalUIConverter
{
    [CreateAssetMenu(menuName = "Local PSD UI/Settings", fileName = "LocalUIConverterSettings")]
    public sealed class LocalUIConverterSettings : ScriptableObject
    {
        public const string DefaultPath = "Assets/Scripts/Editor/LocalUIConverter/LocalUIConverterSettings.asset";
        public string outputRoot = "Assets/__Temp";
        public string fontRoot = "Assets/Bundles/Fonts";
        public string outlineMaterialFolder = "Assets/Bundles/Fonts/OutlineMaterial";
        public string generatedFontFolder = "Assets/Bundles/Fonts/LocalPSDGenerated";
        public string commonTextureRoot = "Assets/Bundles/UI/UIImages/UICommon";
        public string artTextureRoot = "";
        public TMP_FontAsset defaultFont;
        public string uiLayer = "UI";
        [Min(1)] public float textWidthScale = 1;
        [Min(1)] public float textHeightScale = 1;
        public List<ComponentRule> componentRules = new List<ComponentRule>();
        public List<PrefabRule> prefabRules = new List<PrefabRule>();
        public List<FontRule> fontRules = new List<FontRule>();

        [Serializable] public sealed class ComponentRule
        {
            public string prefix;
            public MonoScript componentScript;
        }
        [Serializable] public sealed class PrefabRule
        {
            public string nodeName;
            public GameObject prefab;
        }
        [Serializable] public sealed class FontRule
        {
            public string psdFontName;
            public TMP_FontAsset fontAsset;
        }

        public static LocalUIConverterSettings Load()
        {
            return AssetDatabase.LoadAssetAtPath<LocalUIConverterSettings>(DefaultPath);
        }
    }
}
