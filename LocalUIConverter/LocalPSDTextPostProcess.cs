using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Starlight.Game.Editor.LocalUIConverter
{
    /// <summary>Fit the PSD text box to the resolved Unity font's layout metrics.</summary>
    public static class LocalPSDTextPostProcess
    {
        public static void Fit(TMP_Text text, LocalUIConverterSettings settings)
        {
            if (text == null) return;
            text.enableWordWrapping = true;
            text.overflowMode = TextOverflowModes.Overflow;
            // The generator's Canvas is inactive here. A newly added TMP component
            // may not have run Awake yet, so its preferredWidth can be uninitialized.
            // Measure on an active, hidden component using the resolved font instead.
            var probe = new GameObject("PSD Text Measurement", typeof(RectTransform), typeof(CanvasRenderer))
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            if (text.gameObject.scene.IsValid()) SceneManager.MoveGameObjectToScene(probe, text.gameObject.scene);
            try
            {
                var measure = probe.AddComponent<TextMeshProUGUI>();
                measure.font = text.font;
                measure.fontSharedMaterial = text.fontSharedMaterial;
                measure.fontSize = text.fontSize;
                measure.fontStyle = text.fontStyle;
                measure.fontWeight = text.fontWeight;
                measure.enableAutoSizing = false;
                measure.enableKerning = text.enableKerning;
                measure.characterSpacing = text.characterSpacing;
                measure.wordSpacing = text.wordSpacing;
                measure.lineSpacing = text.lineSpacing;
                measure.paragraphSpacing = text.paragraphSpacing;
                measure.richText = text.richText;
                measure.isRightToLeftText = text.isRightToLeftText;
                measure.margin = text.margin;
                // Infinite available width measures authored lines, not lines wrapped
                // to the PSD's tight glyph bounds. Keep explicit newlines intact.
                Vector2 preferred = measure.GetPreferredValues(text.text, Mathf.Infinity, Mathf.Infinity);
                var rect = text.rectTransform;
                Vector2 oldSize = rect.rect.size;
                // Round up and leave one canvas unit for TMP's line-fit precision.
                Vector2 newSize = new Vector2(
                    Mathf.Max(oldSize.x, oldSize.x * settings.textWidthScale, Mathf.Ceil(preferred.x) + 1f),
                    Mathf.Max(oldSize.y, text.fontSize * settings.textHeightScale, Mathf.Ceil(preferred.y)));
                rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, newSize.x);
                rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, newSize.y);
                Vector2 delta = newSize - oldSize;
                rect.anchoredPosition += new Vector2((rect.pivot.x - 0.5f) * delta.x, (rect.pivot.y - 0.5f) * delta.y);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(probe);
            }
        }
    }
}
