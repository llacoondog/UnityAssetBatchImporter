using System;
using System.Collections.Generic;

namespace llacoondog.AssetBatchImporter
{
    [Serializable]
    internal sealed class AssetStoreAssetInfo
    {
        public long productId;
        public string displayName;
        public string[] labels = Array.Empty<string>();
        public AssetStoreInstallState state;
        public string cachedPath;
        public bool selected = true;

        public bool HasLabel(string label)
        {
            if (string.IsNullOrEmpty(label)) return true;
            foreach (var item in labels ?? Array.Empty<string>())
                if (string.Equals(item, label, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    internal enum AssetStoreInstallState
    {
        NotDownloaded,
        Cached,
        Imported
    }

    internal enum AssetBatchQueueStage
    {
        Idle,
        Downloading,
        Importing,
        Completed,
        Cancelled
    }

    internal sealed class AssetDownloadProgressInfo
    {
        public long productId;
        public float progress;
        public string state;
        public string errorMessage;
        public bool isInProgress;
        public bool isPaused;
        public bool isVisible;

        public bool IsError =>
            string.Equals(state, "Error", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(state, "Aborted", StringComparison.OrdinalIgnoreCase);
    }

    [Serializable]
    internal sealed class AssetBatchQueueSnapshot
    {
        public List<long> productIds = new();
        public List<string> displayNames = new();
        public int stage;
        public int importIndex;
        public int imported;
        public int skipped;
        public int failed;
        public List<long> downloadFailedIds = new();
        public long pendingImportProductId;
        public long pendingImportStartedUtcTicks;
        public long lastDownloadRetryUtcTicks;
        public float downloadProgress;
        public string statusMessage = string.Empty;
    }
}
