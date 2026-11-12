using UnityEngine;

namespace llacoondog.AssetBatchImporter
{
    internal static class AssetBatchImporterDiagnostics
    {
        public const string Version = "0.3.3";

        public static string BuildReport()
        {
            UnityAssetStoreBridge.IsAvailable(out string message);
            return $"Asset Batch Importer {Version}\nUnity {Application.unityVersion}\n{message}";
        }
    }
}
