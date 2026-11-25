using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace Starlight.Game.Editor.LocalUIConverter
{
    /// <summary>Serialize arbitrary PSD group trees without Unity's field-depth limit.</summary>
    public static class LocalPSDJson
    {
        public static string Serialize<T>(T value)
        {
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
        public static T Deserialize<T>(string json)
        {
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
        }
    }
}
