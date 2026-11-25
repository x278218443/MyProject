using UnityEngine;

namespace Starlight.LocalUIConverter
{
    /// <summary>Temporary authoring metadata. LocalUIConverter archives and removes it after Bind generation.</summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Local UI Converter/节点描述 (LocalPSDNodeDescription)")]
    public sealed class LocalPSDNodeDescription : MonoBehaviour
    {
        [InspectorName("类型")] public LocalPSDNodeType type;
        [InspectorName("描述"), TextArea(2, 6)] public string description = "";
    }
    
    public enum LocalPSDNodeType
    {
        None = 0, Button = 1, Text = 2, Scroll = 3, Toggle = 4, Slider = 5,
        [InspectorName("纯节点描述")] Description = 6
    }

}
