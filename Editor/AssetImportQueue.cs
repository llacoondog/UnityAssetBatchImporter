using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace llacoondog.AssetBatchImporter
{
    [InitializeOnLoad]
    internal static class AssetBatchWorkflow
    {
        // Preserve a queue already stored in this Editor session during an upgrade.
        private const string SessionKey = "JinaTools.AssetBatchImporter.Workflow";
        private const string RunningKey = "JinaTools.AssetBatchImporter.WorkflowRunning";
        private const double PollIntervalSeconds = 0.35;
        private const double DownloadRetrySeconds = 8.0;
        private const double PendingImportRetrySeconds = 3.0;

        private static bool _importCallInFlight;
        private static double _nextPollTime;

        public static event Action changed;

        public static bool IsRunning => SessionState.GetBool(RunningKey, false);
        public static AssetBatchQueueStage Stage => (AssetBatchQueueStage)Load().stage;
        public static int Total => Load().productIds.Count;
        public static int ImportIndex => Load().importIndex;
        public static int Imported => Load().imported;
        public static int Skipped => Load().skipped;
        public static int Failed => Load().failed;
        public static float DownloadProgress => Load().downloadProgress;
        public static string StatusMessage => Load().statusMessage ?? string.Empty;

        static AssetBatchWorkflow()
        {
            AssetDatabase.importPackageCompleted += OnImportCompleted;
            AssetDatabase.importPackageCancelled += OnImportCancelled;
            AssetDatabase.importPackageFailed += OnImportFailed;

            UnityAssetStoreBridge.downloadError += OnDownloadError;
            UnityAssetStoreBridge.downloadFinalized += OnDownloadFinalized;
            EditorApplication.update += Update;

            if (IsRunning)
            {
                _importCallInFlight = false;
                EditorApplication.delayCall += () => changed?.Invoke();
            }
        }

        public static void Start(IEnumerable<AssetStoreAssetInfo> assets, bool downloadMissing)
        {
            if (IsRunning)
            {
                Debug.LogWarning("[Asset Batch Importer] A workflow is already running.");
                return;
            }

            var selected = assets?
                .Where(x => x != null && x.selected)
                .GroupBy(x => x.productId)
                .Select(g => g.First())
                .ToList() ?? new List<AssetStoreAssetInfo>();

            if (selected.Count == 0)
            {
                Debug.LogWarning("[Asset Batch Importer] No assets are selected.");
                return;
            }

            var snapshot = new AssetBatchQueueSnapshot();
            foreach (var asset in selected)
            {
                snapshot.productIds.Add(asset.productId);
                snapshot.displayNames.Add(asset.displayName);
            }

            var missing = snapshot.productIds
                .Where(id => UnityAssetStoreBridge.GetState(id) == AssetStoreInstallState.NotDownloaded)
                .ToArray();

            snapshot.stage = (int)(downloadMissing && missing.Length > 0
                ? AssetBatchQueueStage.Downloading
                : AssetBatchQueueStage.Importing);
            snapshot.statusMessage = downloadMissing && missing.Length > 0
                ? $"미다운로드 에셋 {missing.Length}개 다운로드 시작"
                : "Import 준비 중";
            snapshot.lastDownloadRetryUtcTicks = DateTime.UtcNow.Ticks;

            Save(snapshot);
            SessionState.SetBool(RunningKey, true);
            _importCallInFlight = false;
            _nextPollTime = 0;

            if (downloadMissing && missing.Length > 0 &&
                !UnityAssetStoreBridge.StartDownloads(missing, out string error))
            {
                snapshot.statusMessage = $"다운로드 시작 실패: {error}";
                Save(snapshot);
                Debug.LogError($"[Asset Batch Importer] {snapshot.statusMessage}");
            }

            changed?.Invoke();
        }

        public static void Cancel()
        {
            if (!IsRunning)
                return;

            var snapshot = Load();
            if ((AssetBatchQueueStage)snapshot.stage == AssetBatchQueueStage.Downloading)
                UnityAssetStoreBridge.AbortAllDownloads();

            snapshot.stage = (int)AssetBatchQueueStage.Cancelled;
            snapshot.statusMessage = "사용자가 작업을 취소함";
            Save(snapshot);
            SessionState.SetBool(RunningKey, false);
            _importCallInFlight = false;
            EditorUtility.ClearProgressBar();
            changed?.Invoke();
        }

        private static void Update()
        {
            if (!IsRunning || EditorApplication.timeSinceStartup < _nextPollTime)
                return;

            _nextPollTime = EditorApplication.timeSinceStartup + PollIntervalSeconds;
            var snapshot = Load();

            switch ((AssetBatchQueueStage)snapshot.stage)
            {
                case AssetBatchQueueStage.Downloading:
                    TickDownloads(snapshot);
                    break;
                case AssetBatchQueueStage.Importing:
                    TickImports(snapshot);
                    break;
            }
        }

        private static void TickDownloads(AssetBatchQueueSnapshot snapshot)
        {
            int total = snapshot.productIds.Count;
            if (total == 0)
            {
                SwitchToImport(snapshot);
                return;
            }

            int resolved = 0;
            float progressSum = 0f;
            var retryIds = new List<long>();

            foreach (long id in snapshot.productIds)
            {
                if (snapshot.downloadFailedIds.Contains(id))
                {
                    resolved++;
                    progressSum += 1f;
                    continue;
                }

                var localState = UnityAssetStoreBridge.GetState(id);
                if (localState != AssetStoreInstallState.NotDownloaded)
                {
                    resolved++;
                    progressSum += 1f;
                    continue;
                }

                if (UnityAssetStoreBridge.TryGetDownloadProgress(id, out var progress))
                {
                    progressSum += Mathf.Clamp01(progress.progress);
                    if (progress.IsError)
                    {
                        snapshot.downloadFailedIds.Add(id);
                        resolved++;
                        progressSum += 1f - Mathf.Clamp01(progress.progress);
                        Debug.LogError($"[Asset Batch Importer] Download failed for {id}: {progress.errorMessage}");
                    }
                }
                else
                {
                    retryIds.Add(id);
                }
            }

            snapshot.downloadProgress = Mathf.Clamp01(progressSum / total);
            snapshot.statusMessage = $"다운로드 {resolved}/{total} 완료";

            if (resolved >= total)
            {
                SwitchToImport(snapshot);
                return;
            }

            if (retryIds.Count > 0)
            {
                var lastRetry = snapshot.lastDownloadRetryUtcTicks > 0
                    ? new DateTime(snapshot.lastDownloadRetryUtcTicks, DateTimeKind.Utc)
                    : DateTime.MinValue;

                if ((DateTime.UtcNow - lastRetry).TotalSeconds >= DownloadRetrySeconds)
                {
                    snapshot.lastDownloadRetryUtcTicks = DateTime.UtcNow.Ticks;
                    snapshot.statusMessage = "다운로드 시작 대기 중 — 로그인/Asset Store 약관 확인";

                    if (!UnityAssetStoreBridge.StartDownloads(retryIds, out string error))
                    {
                        snapshot.statusMessage = $"다운로드 재시도 실패: {error}";
                        Debug.LogError($"[Asset Batch Importer] {snapshot.statusMessage}");
                    }
                }
            }

            Save(snapshot);
            changed?.Invoke();
        }

        private static void OnDownloadError(long productId, string error)
        {
            if (!IsRunning)
                return;

            var snapshot = Load();
            if ((AssetBatchQueueStage)snapshot.stage != AssetBatchQueueStage.Downloading ||
                !snapshot.productIds.Contains(productId))
                return;

            if (!snapshot.downloadFailedIds.Contains(productId))
                snapshot.downloadFailedIds.Add(productId);

            snapshot.statusMessage = $"다운로드 실패: {GetDisplayName(snapshot, productId)}";
            Save(snapshot);

            Debug.LogError(
                $"[Asset Batch Importer] Download failed: {GetDisplayName(snapshot, productId)} " +
                $"({productId}) - {error}");
            changed?.Invoke();
        }

        private static void OnDownloadFinalized(long productId)
        {
            if (!IsRunning)
                return;
            var snapshot = Load();
            if (snapshot.productIds.Contains(productId))
                changed?.Invoke();
        }

        private static void SwitchToImport(AssetBatchQueueSnapshot snapshot)
        {
            snapshot.stage = (int)AssetBatchQueueStage.Importing;
            snapshot.downloadProgress = 1f;
            snapshot.statusMessage = "다운로드 단계 완료 — Import 시작";
            snapshot.pendingImportProductId = 0;
            snapshot.pendingImportStartedUtcTicks = 0;
            Save(snapshot);
            _importCallInFlight = false;
            changed?.Invoke();
        }

        private static void TickImports(AssetBatchQueueSnapshot snapshot)
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return;

            if (snapshot.importIndex >= snapshot.productIds.Count)
            {
                Finish(snapshot);
                return;
            }

            long productId = snapshot.productIds[snapshot.importIndex];
            string displayName = GetDisplayName(snapshot, productId);

            // If scripts reloaded while ImportPackage was running, recover the pending item.
            if (snapshot.pendingImportProductId == productId)
            {
                var pendingState = UnityAssetStoreBridge.GetState(productId);
                if (pendingState == AssetStoreInstallState.Imported)
                {
                    snapshot.imported++;
                    snapshot.importIndex++;
                    snapshot.pendingImportProductId = 0;
                    snapshot.pendingImportStartedUtcTicks = 0;
                    snapshot.statusMessage = $"Import 완료: {displayName}";
                    Save(snapshot);
                    _importCallInFlight = false;
                    changed?.Invoke();
                    return;
                }

                if (_importCallInFlight)
                    return;

                var started = snapshot.pendingImportStartedUtcTicks > 0
                    ? new DateTime(snapshot.pendingImportStartedUtcTicks, DateTimeKind.Utc)
                    : DateTime.MinValue;

                if ((DateTime.UtcNow - started).TotalSeconds < PendingImportRetrySeconds)
                    return;

                snapshot.pendingImportProductId = 0;
                snapshot.pendingImportStartedUtcTicks = 0;
                Save(snapshot);
            }

            if (snapshot.downloadFailedIds.Contains(productId))
            {
                snapshot.failed++;
                snapshot.importIndex++;
                snapshot.statusMessage = $"다운로드 실패로 건너뜀: {displayName}";
                Save(snapshot);
                changed?.Invoke();
                return;
            }

            var localState = UnityAssetStoreBridge.GetState(productId);
            if (localState == AssetStoreInstallState.Imported)
            {
                snapshot.skipped++;
                snapshot.importIndex++;
                snapshot.statusMessage = $"이미 설치됨: {displayName}";
                Save(snapshot);
                changed?.Invoke();
                return;
            }

            if (localState == AssetStoreInstallState.NotDownloaded)
            {
                snapshot.failed++;
                snapshot.importIndex++;
                snapshot.statusMessage = $"캐시 없음: {displayName}";
                Save(snapshot);
                Debug.LogError($"[Asset Batch Importer] '{displayName}' is not downloaded.");
                changed?.Invoke();
                return;
            }

            if (_importCallInFlight)
                return;

            snapshot.pendingImportProductId = productId;
            snapshot.pendingImportStartedUtcTicks = DateTime.UtcNow.Ticks;
            snapshot.statusMessage =
                $"Import 중 {snapshot.importIndex + 1}/{snapshot.productIds.Count}: {displayName}";
            Save(snapshot);
            _importCallInFlight = true;

            if (!UnityAssetStoreBridge.InstallCached(productId, out string error))
            {
                _importCallInFlight = false;
                snapshot.pendingImportProductId = 0;
                snapshot.pendingImportStartedUtcTicks = 0;
                snapshot.failed++;
                snapshot.importIndex++;
                snapshot.statusMessage = $"Import 실패: {displayName}";
                Save(snapshot);
                Debug.LogError($"[Asset Batch Importer] Failed to import '{displayName}': {error}");
                changed?.Invoke();
            }
        }

        private static void OnImportCompleted(string packageName) => AdvanceImport(true, null);
        private static void OnImportCancelled(string packageName) =>
            AdvanceImport(false, $"Import cancelled: {packageName}");
        private static void OnImportFailed(string packageName, string errorMessage) =>
            AdvanceImport(false, $"Import failed: {packageName} - {errorMessage}");

        private static void AdvanceImport(bool success, string error)
        {
            if (!IsRunning)
                return;

            var snapshot = Load();
            if ((AssetBatchQueueStage)snapshot.stage != AssetBatchQueueStage.Importing ||
                snapshot.pendingImportProductId <= 0)
                return;

            string displayName = GetDisplayName(snapshot, snapshot.pendingImportProductId);
            _importCallInFlight = false;
            snapshot.pendingImportProductId = 0;
            snapshot.pendingImportStartedUtcTicks = 0;
            snapshot.importIndex++;

            if (success)
            {
                snapshot.imported++;
                snapshot.statusMessage = $"Import 완료: {displayName}";
            }
            else
            {
                snapshot.failed++;
                snapshot.statusMessage = $"Import 실패: {displayName}";
            }

            Save(snapshot);
            if (!string.IsNullOrEmpty(error))
                Debug.LogError($"[Asset Batch Importer] {error}");
            changed?.Invoke();
        }

        private static void Finish(AssetBatchQueueSnapshot snapshot)
        {
            snapshot.stage = (int)AssetBatchQueueStage.Completed;
            snapshot.statusMessage =
                $"완료 — Import {snapshot.imported}, 이미 설치 {snapshot.skipped}, 실패 {snapshot.failed}";
            Save(snapshot);
            SessionState.SetBool(RunningKey, false);
            _importCallInFlight = false;
            EditorUtility.ClearProgressBar();

            Debug.Log(
                $"[Asset Batch Importer] Finished. Imported: {snapshot.imported}, " +
                $"Already installed: {snapshot.skipped}, Failed: {snapshot.failed}, " +
                $"Total: {snapshot.productIds.Count}");
            changed?.Invoke();
        }

        private static string GetDisplayName(AssetBatchQueueSnapshot snapshot, long productId)
        {
            int index = snapshot.productIds.IndexOf(productId);
            if (index >= 0 && index < snapshot.displayNames.Count &&
                !string.IsNullOrWhiteSpace(snapshot.displayNames[index]))
                return snapshot.displayNames[index];
            return productId.ToString();
        }

        private static AssetBatchQueueSnapshot Load()
        {
            string json = SessionState.GetString(SessionKey, string.Empty);
            if (string.IsNullOrEmpty(json))
                return new AssetBatchQueueSnapshot();
            try
            {
                return JsonUtility.FromJson<AssetBatchQueueSnapshot>(json) ?? new AssetBatchQueueSnapshot();
            }
            catch
            {
                return new AssetBatchQueueSnapshot();
            }
        }

        private static void Save(AssetBatchQueueSnapshot snapshot)
        {
            SessionState.SetString(SessionKey, JsonUtility.ToJson(snapshot));
        }
    }
}
