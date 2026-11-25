using System;
using System.IO;
using UnityEngine;

namespace Starlight.Game.Editor.LocalUIConverter
{
    // Only local authoring state; do not write review notes into Assets/layout.json.
    [Serializable] internal sealed class LocalPSDWorkflow
    {
        public string layoutJson, psdPath, previewPath, referencePath, prefabGuid;
        public string functionId, prefabDirectory, scriptDirectory, artTextures;
        public string[] prefabGuids;
        public long sourceWriteTicks;
        public bool rasterText, hasCandidates;
        public LocalPSDInteractionPreview.Candidate[] candidates;

        private static string PathFor(string directory) => Path.Combine(Directory.GetParent(Application.dataPath).FullName,
            "__Temp/LocalUIConverter/Workflows", Hash128.Compute(Path.GetFullPath(directory).Replace('\\', '/').TrimEnd('/').ToLowerInvariant()).ToString() + ".json");

        public static void Save(string directory, LocalPSDWorkflow state)
        {
            string path = PathFor(directory);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + ".writing";
            File.WriteAllText(temporary, JsonUtility.ToJson(state, true));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }

        public static LocalPSDWorkflow Load(string directory)
        {
            string path = PathFor(directory);
            return File.Exists(path) ? JsonUtility.FromJson<LocalPSDWorkflow>(File.ReadAllText(path)) : null;
        }
    }
}
