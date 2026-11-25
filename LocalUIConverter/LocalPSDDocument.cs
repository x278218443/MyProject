using System;
using System.Runtime.Serialization;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Starlight.Game.Editor.LocalUIConverter
{
    [Serializable, DataContract]
    public sealed class LocalPSDDocument
    {
        [DataMember] public LocalPSDArtboard artboard;
        [DataMember] public string source;
        [DataMember] public string[] warnings;

        public static LocalPSDDocument Parse(string json)
        {
            var document = LocalPSDJson.Deserialize<LocalPSDDocument>(json);
            if (document?.artboard == null || document.artboard.width <= 0 || document.artboard.height <= 0
                || document.artboard.layers == null || document.artboard.layers.Length == 0)
                throw new InvalidDataException("PSD 布局缺少有效画板或图层。");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var layer in document.artboard.Descendants())
            {
                if (layer == null || string.IsNullOrEmpty(layer.id) || !ids.Add(layer.id))
                    throw new InvalidDataException("PSD 布局存在空图层或重复 ID。");
                if (float.IsNaN(layer.width) || float.IsInfinity(layer.width) || layer.width < 0
                    || float.IsNaN(layer.height) || float.IsInfinity(layer.height) || layer.height < 0)
                    throw new InvalidDataException("PSD 图层尺寸无效: " + layer.name);
            }
            return document;
        }
    }

    [Serializable, DataContract] public sealed class LocalPSDArtboard
    {
        [DataMember] public string name;
        [DataMember] public float width;
        [DataMember] public float height;
        [DataMember] public LocalPSDLayer[] layers;
        public IEnumerable<LocalPSDLayer> Descendants()
        {
            foreach (var layer in layers)
            {
                yield return layer;
                if (layer != null) foreach (var child in layer.Descendants()) yield return child;
            }
        }
    }

    [Serializable, DataContract] public sealed class LocalPSDLayer
    {
        [DataMember] public string id;
        [DataMember] public string name;
        [DataMember] public string type;
        [DataMember] public string roundTripId, replacementKey, bindingName, prefabKey;
        [DataMember(EmitDefaultValue = false)] public LocalPSDMarkedControl control;
        [DataMember] public int psdLayerId = -1, sourceLayerIndex;
        [DataMember] public float left, top, width, height;
        [DataMember] public float opacity = 1;
        [DataMember] public bool isVisible = true;
        [DataMember] public LocalPSDImages images;
        [DataMember] public LocalPSDText textInfo;
        [DataMember] public LocalPSDEffects layerEffects;
        [DataMember] public LocalPSDLayer[] layers;
        [OnDeserializing] private void InitializeDefaults(StreamingContext context)
        {
            // DataContract does not run field initializers for missing members.
            isVisible = true;
            opacity = 1;
            psdLayerId = -1;
        }
        public IEnumerable<LocalPSDLayer> Descendants()
        {
            if (layers == null) yield break;
            foreach (var layer in layers)
            {
                yield return layer;
                if (layer != null) foreach (var child in layer.Descendants()) yield return child;
            }
        }
    }
    [Serializable, DataContract] public sealed class LocalPSDMarkedControl
    {
        [DataMember] public string type, name;
        [DataMember(EmitDefaultValue = false)] public string note;
    }

    [Serializable, DataContract] public sealed class LocalPSDImages { [DataMember] public string png_xxxhd; }
    [Serializable, DataContract] public sealed class LocalPSDText
    {
        [DataMember] public string text, fontPostScriptName, fontName, justification;
        [DataMember] public float size, leading, tracking;
        [DataMember] public LocalPSDColor color;
        [DataMember] public LocalPSDTextGradient gradient;
    }
    [Serializable, DataContract] public sealed class LocalPSDTextGradient
    {
        [DataMember] public LocalPSDColor top, bottom;
    }
    [Serializable, DataContract] public sealed class LocalPSDColor
    {
        [DataMember] public float r, g, b, a = 1;
        [OnDeserializing] private void InitializeDefaults(StreamingContext context) { a = 1; }
        public Color Value => new Color(r, g, b, a);
    }
    [Serializable, DataContract] public sealed class LocalPSDEffects { [DataMember] public LocalPSDStroke frameFX; }
    [Serializable, DataContract] public sealed class LocalPSDStroke
    {
        [DataMember] public bool enabled;
        [DataMember] public float size;
        [DataMember] public LocalPSDStrokeColor color;
    }
    [Serializable, DataContract] public sealed class LocalPSDStrokeColor
    {
        [DataMember] public float red, green, blue;
        public Color Value => new Color(red / 255f, green / 255f, blue / 255f, 1);
    }

    public struct LocalPSDGenerationProgress
    {
        public int current, total;
        public string layerName;
    }
}
