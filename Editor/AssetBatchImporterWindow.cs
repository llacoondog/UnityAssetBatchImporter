using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace llacoondog.AssetBatchImporter
{
    internal sealed class AssetBatchImporterWindow : EditorWindow
    {
        // Preserve the saved label when upgrading from the previous package.
        private const string LastLabelKey = "JinaTools.AssetBatchImporter.LastLabel";
        private readonly List<AssetStoreAssetInfo> _assets = new();
        private readonly List<string> _labels = new();
        private Vector2 _scroll;
        private int _labelIndex;
        private bool _loading;
        private string _message;
        private MessageType _messageType = MessageType.Info;

        [MenuItem("Tools/Asset Batch Importer")]
        private static void Open()
        {
            var window = GetWindow<AssetBatchImporterWindow>();
            window.titleContent = new GUIContent("Asset Batch Importer");
            window.minSize = new Vector2(650, 450);
            window.Show();
        }

        private void OnEnable()
        {
            AssetBatchWorkflow.changed += OnWorkflowChanged;
            EditorApplication.update += RepaintWhileRunning;

            if (!UnityAssetStoreBridge.IsAvailable(out string message))
            {
                _message = message;
                _messageType = MessageType.Error;
            }
        }

        private void OnDisable()
        {
            AssetBatchWorkflow.changed -= OnWorkflowChanged;
            EditorApplication.update -= RepaintWhileRunning;
        }

        private void OnWorkflowChanged()
        {
            Repaint();
            if (!AssetBatchWorkflow.IsRunning && _assets.Count > 0)
            {
                EditorApplication.delayCall += () =>
                {
                    if (this != null)
                        RefreshStates();
                };
            }
        }

        private void RepaintWhileRunning()
        {
            if (AssetBatchWorkflow.IsRunning)
                Repaint();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(6);
            DrawToolbar();
            EditorGUILayout.Space(6);

            if (!string.IsNullOrEmpty(_message))
                EditorGUILayout.HelpBox(_message, _messageType);

            if (_assets.Count > 0)
            {
                EditorGUILayout.Space(4);
                DrawAssetList();
            }

            GUILayout.FlexibleSpace();
            DrawBottomBar();
        }

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.enabled = !_loading && !AssetBatchWorkflow.IsRunning;
                if (GUILayout.Button(_loading ? "불러오는 중..." : "My Assets 새로고침", GUILayout.Width(150)))
                    RefreshPurchases();

                if (GUILayout.Button("My Assets 열기", GUILayout.Width(105)))
                    EditorApplication.ExecuteMenuItem("Window/Package Management/My Assets");

                if (GUILayout.Button("진단 복사", GUILayout.Width(85)))
                {
                    string report = AssetBatchImporterDiagnostics.BuildReport();
                    EditorGUIUtility.systemCopyBuffer = report;
                    _message = "진단 정보를 클립보드에 복사했어.";
                    _messageType = MessageType.Info;
                }

                GUI.enabled = _assets.Count > 0 && !_loading && !AssetBatchWorkflow.IsRunning;
                if (GUILayout.Button("설치 상태 새로고침", GUILayout.Width(145)))
                    RefreshStates();

                GUI.enabled = true;
                GUILayout.Space(8);

                if (_labels.Count > 0)
                {
                    EditorGUILayout.LabelField("Label", GUILayout.Width(36));
                    int newIndex = EditorGUILayout.Popup(_labelIndex, _labels.ToArray(), GUILayout.MinWidth(180));
                    if (newIndex != _labelIndex)
                    {
                        _labelIndex = newIndex;
                        EditorPrefs.SetString(LastLabelKey, _labels[_labelIndex]);
                        Repaint();
                    }
                }
            }
        }

        private void DrawAssetList()
        {
            var visible = GetVisibleAssets().ToList();
            var selectable = visible.Where(x => x.state != AssetStoreInstallState.Imported).ToList();

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                bool allSelected = selectable.Count > 0 && selectable.All(x => x.selected);
                bool newAllSelected = GUILayout.Toggle(allSelected, GUIContent.none, GUILayout.Width(20));
                if (newAllSelected != allSelected)
                    foreach (var asset in selectable) asset.selected = newAllSelected;

                EditorGUILayout.LabelField($"표시 {visible.Count} / 전체 {_assets.Count}", EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();

                int imported = visible.Count(x => x.state == AssetStoreInstallState.Imported);
                int cached = visible.Count(x => x.state == AssetStoreInstallState.Cached);
                int missing = visible.Count(x => x.state == AssetStoreInstallState.NotDownloaded);
                EditorGUILayout.LabelField(
                    $"Imported {imported}   Cached {cached}   Missing {missing}",
                    EditorStyles.miniLabel,
                    GUILayout.Width(270));
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var asset in visible)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUI.enabled = asset.state != AssetStoreInstallState.Imported && !AssetBatchWorkflow.IsRunning;
                    asset.selected = EditorGUILayout.Toggle(asset.selected, GUILayout.Width(20));
                    GUI.enabled = true;

                    EditorGUILayout.LabelField(asset.displayName, GUILayout.MinWidth(260));
                    GUILayout.FlexibleSpace();
                    DrawState(asset);
                }
            }
            EditorGUILayout.EndScrollView();
        }

        private static void DrawState(AssetStoreAssetInfo asset)
        {
            string text;
            if (asset.state == AssetStoreInstallState.Imported)
                text = "Imported";
            else if (asset.state == AssetStoreInstallState.Cached)
                text = "Cached";
            else if (UnityAssetStoreBridge.TryGetDownloadProgress(asset.productId, out var progress))
            {
                if (progress.IsError) text = "Download Error";
                else if (progress.isPaused) text = $"Paused {progress.progress:P0}";
                else text = $"Downloading {progress.progress:P0}";
            }
            else
                text = "Not Downloaded";

            EditorGUILayout.LabelField(text, EditorStyles.miniLabel, GUILayout.Width(125));
        }

        private void DrawBottomBar()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(string.Empty, GUI.skin.horizontalSlider);

            if (AssetBatchWorkflow.IsRunning)
            {
                DrawRunningWorkflow();
                return;
            }

            var selected = GetVisibleAssets()
                .Where(x => x.selected && x.state != AssetStoreInstallState.Imported)
                .ToList();
            int cachedCount = selected.Count(x => x.state == AssetStoreInstallState.Cached);

            GUI.enabled = selected.Count > 0 && !_loading;
            if (GUILayout.Button($"Download & Import Selected ({selected.Count})", GUILayout.Height(36)))
                AssetBatchWorkflow.Start(selected, downloadMissing: true);

            GUI.enabled = cachedCount > 0 && !_loading;
            if (GUILayout.Button($"캐시된 것만 Import ({cachedCount})", GUILayout.Height(25)))
            {
                var cachedOnly = selected.Where(x => x.state == AssetStoreInstallState.Cached).ToList();
                AssetBatchWorkflow.Start(cachedOnly, downloadMissing: false);
            }
            GUI.enabled = true;
        }

        private static void DrawRunningWorkflow()
        {
            var stage = AssetBatchWorkflow.Stage;
            float progress = stage == AssetBatchQueueStage.Downloading
                ? AssetBatchWorkflow.DownloadProgress
                : Mathf.Clamp01((float)AssetBatchWorkflow.ImportIndex / Mathf.Max(1, AssetBatchWorkflow.Total));

            Rect rect = GUILayoutUtility.GetRect(1, 20, GUILayout.ExpandWidth(true));
            EditorGUI.ProgressBar(
                rect,
                progress,
                stage == AssetBatchQueueStage.Downloading
                    ? $"다운로드 {progress:P0}"
                    : $"Import {AssetBatchWorkflow.ImportIndex}/{AssetBatchWorkflow.Total}");

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(AssetBatchWorkflow.StatusMessage, EditorStyles.miniLabel);
            EditorGUILayout.LabelField(
                $"Import 성공 {AssetBatchWorkflow.Imported}  이미 설치 {AssetBatchWorkflow.Skipped}  실패 {AssetBatchWorkflow.Failed}",
                EditorStyles.miniLabel);
            EditorGUILayout.Space(3);

            if (GUILayout.Button("현재 작업 취소", GUILayout.Height(28)))
                AssetBatchWorkflow.Cancel();
        }

        private IEnumerable<AssetStoreAssetInfo> GetVisibleAssets()
        {
            if (_labels.Count == 0 || _labelIndex <= 0)
                return _assets;
            string label = _labels[_labelIndex];
            return _assets.Where(x => x.HasLabel(label));
        }

        private void RefreshPurchases()
        {
            _loading = true;
            _message = null;

            UnityAssetStoreBridge.FetchAllPurchases(
                assets =>
                {
                    _assets.Clear();
                    _assets.AddRange(assets);
                    RebuildLabels();
                    RefreshStates();
                    _loading = false;
                    _message = _assets.Count == 0 ? "My Assets에 보유한 에셋이 없어." : null;
                    _messageType = MessageType.Info;
                    Repaint();
                },
                error =>
                {
                    _loading = false;
                    _message = error;
                    _messageType = MessageType.Error;
                    Repaint();
                });
        }

        private void RefreshStates()
        {
            UnityAssetStoreBridge.RefreshLocalState(_assets);
            foreach (var asset in _assets)
                asset.selected = asset.state != AssetStoreInstallState.Imported;
            Repaint();
        }

        private void RebuildLabels()
        {
            string previous = _labels.Count > 0 && _labelIndex < _labels.Count
                ? _labels[_labelIndex]
                : EditorPrefs.GetString(LastLabelKey, "전체");

            _labels.Clear();
            _labels.Add("전체");
            _labels.AddRange(
                _assets
                    .SelectMany(x => x.labels ?? Array.Empty<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

            _labelIndex = Mathf.Max(0, _labels.FindIndex(
                x => string.Equals(x, previous, StringComparison.OrdinalIgnoreCase)));

            EditorPrefs.SetString(LastLabelKey, _labels[_labelIndex]);
        }
    }
}
