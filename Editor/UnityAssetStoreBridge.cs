using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace llacoondog.AssetBatchImporter
{
    /// <summary>
    /// Reflection adapter around Unity's internal Package Manager / Asset Store services.
    /// Keep ALL fragile Unity-internal API access in this file.
    /// </summary>
    internal static class UnityAssetStoreBridge
    {
        private const string Ns = "UnityEditor.PackageManager.UI.Internal";
        private const int PageSize = 100;

        private static Type _servicesContainerType;
        private static object _servicesContainer;

        private static object _downloadManager;
        private static EventInfo _downloadErrorEvent;
        private static EventInfo _downloadFinalizedEvent;
        private static Delegate _downloadErrorHandler;
        private static Delegate _downloadFinalizedHandler;
        private static bool _downloadEventsHooked;

        public static event Action<long, string> downloadError;
        public static event Action<long> downloadFinalized;

        public static void FetchAllPurchases(
            Action<List<AssetStoreAssetInfo>> onCompleted,
            Action<string> onError)
        {
            var accumulated = new List<AssetStoreAssetInfo>();
            FetchPurchasesPage(0, accumulated, onCompleted, onError);
        }

        public static void RefreshLocalState(IList<AssetStoreAssetInfo> assets)
        {
            if (assets == null)
                return;

            try
            {
                RefreshAssetStoreCache();
                foreach (var asset in assets)
                    ApplyLocalState(asset);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Asset Batch Importer] Could not refresh local states: {Unwrap(ex).Message}");
            }
        }

        public static void RefreshAssetStoreCache()
        {
            var client = ResolveService("IAssetStoreClient");
            InvokeIfExists(client, "FullScanLocalInfos");
            var updateImportedAssets = FindMethod(client.GetType(), "UpdateImportedAssetsOnAssetsChanged", 4);
            if (updateImportedAssets == null)
            {
                InvokeIfExists(client, "FullScanImportedAssets");
                return;
            }

            // Unity 6.6's full scan removes entries while enumerating the imported-asset cache.
            var cache = ResolveService("IAssetStoreCache");
            var cachedAssets = (IEnumerable)GetMemberValue(cache, "importedAssets");
            var currentPaths = FindImportedAssetPaths();
            var current = new HashSet<string>(currentPaths, StringComparer.Ordinal);
            var removedPaths = cachedAssets.Cast<object>()
                .Select(asset => GetStringMember(asset, "importedPath"))
                .Where(path => !current.Contains(path))
                .ToArray();
            updateImportedAssets.Invoke(client, new object[]
            {
                currentPaths, removedPaths, Array.Empty<string>(), Array.Empty<string>()
            });
        }

        private static string[] FindImportedAssetPaths()
        {
            var filterType = typeof(AssetDatabase).Assembly.GetType("UnityEditor.SearchFilter", true);
            var filter = Activator.CreateInstance(filterType, true);
            Invoke(filter, "ClearSearch");
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            filterType.GetProperty("originalText", flags).SetValue(filter, "assetorigin:");
            filterType.GetProperty("anyWithAssetOrigin", flags).SetValue(filter, true);

            var database = ResolveService("IAssetDatabaseProxy");
            var guids = (string[])Invoke(database, "FindAssets", filter);
            return guids.Select(AssetDatabase.GUIDToAssetPath).ToArray();
        }

        public static AssetStoreInstallState GetState(long productId)
        {
            var temp = new AssetStoreAssetInfo { productId = productId };
            ApplyLocalState(temp);
            return temp.state;
        }

        private static void ApplyLocalState(AssetStoreAssetInfo asset)
        {
            var cache = ResolveService("IAssetStoreCache");
            object localInfo = Invoke(cache, "GetLocalInfo", asset.productId);
            object importedPackage = Invoke(cache, "GetImportedPackage", asset.productId);

            asset.cachedPath = GetStringMember(localInfo, "packagePath");
            if (importedPackage != null)
                asset.state = AssetStoreInstallState.Imported;
            else if (!string.IsNullOrEmpty(asset.cachedPath) && File.Exists(asset.cachedPath))
                asset.state = AssetStoreInstallState.Cached;
            else
                asset.state = AssetStoreInstallState.NotDownloaded;
        }

        public static bool InstallCached(long productId, out string error)
        {
            error = null;

            try
            {
                var cache = ResolveService("IAssetStoreCache");
                object localInfo = Invoke(cache, "GetLocalInfo", productId);
                string packagePath = GetStringMember(localInfo, "packagePath");

                if (string.IsNullOrEmpty(packagePath) || !File.Exists(packagePath))
                {
                    error = "Package is not downloaded in the Asset Store cache.";
                    return false;
                }

                var installer = ResolveService("IAssetStorePackageInstaller");
                var method = FindMethod(installer.GetType(), "Install", 2);

                if (method == null)
                {
                    error = "Unity internal Asset Store installer method was not found.";
                    return false;
                }

                method.Invoke(installer, new object[] { productId, false });
                return true;
            }
            catch (Exception ex)
            {
                error = Unwrap(ex).Message;
                return false;
            }
        }

        public static bool StartDownloads(IEnumerable<long> productIds, out string error)
        {
            error = null;
            try
            {
                var ids = productIds?.Where(x => x > 0).Distinct().ToArray() ?? Array.Empty<long>();
                if (ids.Length == 0)
                    return true;

                EnsureDownloadEventsHooked();
                var manager = ResolveService("IAssetStoreDownloadManager");
                var method = manager.GetType()
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m =>
                    {
                        if (m.Name != "Download") return false;
                        var ps = m.GetParameters();
                        if (ps.Length != 1 || ps[0].ParameterType == typeof(long)) return false;
                        return ps[0].ParameterType.IsAssignableFrom(typeof(long[])) ||
                               typeof(IEnumerable<long>).IsAssignableFrom(ps[0].ParameterType);
                    });

                if (method == null)
                {
                    error = "Unity internal batch download method was not found.";
                    return false;
                }

                object result = method.Invoke(manager, new object[] { ids });
                if (result is bool ok && !ok)
                {
                    error = "Unity Asset Store download manager rejected the request.";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = Unwrap(ex).Message;
                return false;
            }
        }

        public static bool TryGetDownloadProgress(long productId, out AssetDownloadProgressInfo info)
        {
            info = null;
            try
            {
                EnsureDownloadEventsHooked();
                var manager = ResolveService("IAssetStoreDownloadManager");
                var method = manager.GetType()
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == "GetDownloadOperation" && m.GetParameters().Length == 1);
                if (method == null) return false;

                object operation = method.Invoke(manager, new object[] { productId });
                if (operation == null) return false;

                info = new AssetDownloadProgressInfo
                {
                    productId = productId,
                    progress = GetFloatMember(operation, "progressPercentage"),
                    state = GetMemberValue(operation, "state")?.ToString() ?? string.Empty,
                    errorMessage = GetStringMember(operation, "errorMessage"),
                    isInProgress = GetBoolMember(operation, "isInProgress"),
                    isPaused = GetBoolMember(operation, "isInPause"),
                    isVisible = GetBoolMember(operation, "isProgressVisible")
                };
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void AbortAllDownloads()
        {
            try
            {
                var manager = ResolveService("IAssetStoreDownloadManager");
                InvokeIfExists(manager, "AbortAllDownloads");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Asset Batch Importer] Could not abort downloads: {Unwrap(ex).Message}");
            }
        }

        private static void EnsureDownloadEventsHooked()
        {
            if (_downloadEventsHooked)
                return;

            _downloadManager = ResolveService("IAssetStoreDownloadManager");

            _downloadErrorEvent = _downloadManager.GetType().GetEvent(
                "onDownloadError", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (_downloadErrorEvent != null)
            {
                Action<object, object> callback = (operation, error) =>
                {
                    long id = GetLongMember(operation, "productId");
                    string message = GetStringMember(error, "message");
                    if (string.IsNullOrWhiteSpace(message))
                        message = GetStringMember(operation, "errorMessage");
                    downloadError?.Invoke(id, message);
                };
                _downloadErrorHandler = CreateTwoArgumentEventDelegate(_downloadErrorEvent.EventHandlerType, callback);
                _downloadErrorEvent.AddEventHandler(_downloadManager, _downloadErrorHandler);
            }

            _downloadFinalizedEvent = _downloadManager.GetType().GetEvent(
                "onDownloadFinalized", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (_downloadFinalizedEvent != null)
            {
                Action<object> callback = operation =>
                {
                    long id = GetLongMember(operation, "productId");
                    downloadFinalized?.Invoke(id);
                };
                _downloadFinalizedHandler = CreateSingleArgumentEventDelegate(_downloadFinalizedEvent.EventHandlerType, callback);
                _downloadFinalizedEvent.AddEventHandler(_downloadManager, _downloadFinalizedHandler);
            }

            _downloadEventsHooked = true;
        }

        public static bool IsAvailable(out string message)
        {
            try
            {
                ResolveService("IAssetStoreClient");
                ResolveService("IAssetStoreCache");
                ResolveService("IAssetStorePackageInstaller");
                ResolveService("IAssetStoreDownloadManager");
                message = "Unity Asset Store bridge ready.";
                return true;
            }
            catch (Exception ex)
            {
                message = Unwrap(ex).Message;
                return false;
            }
        }

        private static void FetchPurchasesPage(
            int startIndex,
            List<AssetStoreAssetInfo> accumulated,
            Action<List<AssetStoreAssetInfo>> onCompleted,
            Action<string> onError)
        {
            object client = null;
            EventInfo eventInfo = null;
            Delegate eventHandler = null;
            EditorApplication.CallbackFunction updateTimeout = null;

            try
            {
                client = ResolveService("IAssetStoreClient");
                var queryType = FindType($"{Ns}.PurchasesQueryArgs");

                if (queryType == null)
                    throw new InvalidOperationException("PurchasesQueryArgs type was not found.");

                object query = CreatePurchasesQuery(queryType, startIndex, PageSize);

                eventInfo = client.GetType().GetEvent(
                    "onProductListFetched",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (eventInfo == null)
                    throw new InvalidOperationException("onProductListFetched event was not found.");

                bool finished = false;
                double deadline = EditorApplication.timeSinceStartup + 30.0;

                Action<object> callback = purchases =>
                {
                    if (finished)
                        return;

                    finished = true;
                    EditorApplication.update -= updateTimeout;

                    try
                    {
                        if (eventHandler != null)
                            eventInfo.RemoveEventHandler(client, eventHandler);

                        var pageItems = ParsePurchases(purchases);
                        accumulated.AddRange(pageItems);

                        long total = GetLongMember(purchases, "total");
                        int nextIndex = startIndex + pageItems.Count;

                        if (pageItems.Count > 0 && nextIndex < total)
                        {
                            EditorApplication.delayCall += () =>
                                FetchPurchasesPage(nextIndex, accumulated, onCompleted, onError);
                        }
                        else
                        {
                            onCompleted?.Invoke(
                                accumulated
                                    .GroupBy(x => x.productId)
                                    .Select(g => g.First())
                                    .OrderBy(x => x.displayName, StringComparer.OrdinalIgnoreCase)
                                    .ToList());
                        }
                    }
                    catch (Exception ex)
                    {
                        onError?.Invoke(Unwrap(ex).Message);
                    }
                };

                eventHandler = CreateSingleArgumentEventDelegate(eventInfo.EventHandlerType, callback);
                eventInfo.AddEventHandler(client, eventHandler);

                updateTimeout = () =>
                {
                    if (finished || EditorApplication.timeSinceStartup <= deadline)
                        return;

                    finished = true;
                    EditorApplication.update -= updateTimeout;

                    try
                    {
                        if (eventHandler != null)
                            eventInfo.RemoveEventHandler(client, eventHandler);
                    }
                    catch
                    {
                        // ignored
                    }

                    onError?.Invoke("Timed out while reading My Assets. Open Package Manager > My Assets once and make sure you are signed in.");
                };

                EditorApplication.update += updateTimeout;

                var listMethod = FindMethod(client.GetType(), "ListPurchases", 1);
                if (listMethod == null)
                    throw new InvalidOperationException("ListPurchases method was not found.");

                listMethod.Invoke(client, new[] { query });
            }
            catch (Exception ex)
            {
                if (updateTimeout != null)
                    EditorApplication.update -= updateTimeout;

                try
                {
                    if (client != null && eventInfo != null && eventHandler != null)
                        eventInfo.RemoveEventHandler(client, eventHandler);
                }
                catch
                {
                    // ignored
                }

                onError?.Invoke(Unwrap(ex).Message);
            }
        }

        private static object CreatePurchasesQuery(
            Type queryType,
            int startIndex,
            int limit)
        {
            foreach (var ctor in queryType.GetConstructors(
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var p = ctor.GetParameters();
                if (p.Length != 4)
                    continue;

                if (p[0].ParameterType == typeof(int) &&
                    p[1].ParameterType == typeof(int) &&
                    p[2].ParameterType == typeof(string) &&
                    (p[3].ParameterType.FullName == $"{Ns}.IPageFilters" ||
                     p[3].ParameterType.FullName == $"{Ns}.PageFilters"))
                {
                    return ctor.Invoke(new object[] { startIndex, limit, string.Empty, null });
                }
            }

            throw new MissingMethodException("Could not construct PurchasesQueryArgs.");
        }

        private static List<AssetStoreAssetInfo> ParsePurchases(object purchases)
        {
            var result = new List<AssetStoreAssetInfo>();
            object rawList = GetMemberValue(purchases, "list");

            if (rawList is not IEnumerable enumerable)
                return result;

            foreach (var item in enumerable)
            {
                if (item == null)
                    continue;

                var info = new AssetStoreAssetInfo
                {
                    productId = GetLongMember(item, "productId"),
                    displayName = GetStringMember(item, "displayName"),
                    labels = GetStringArrayMember(item, "tags")
                };

                if (info.productId <= 0)
                    continue;

                if (string.IsNullOrWhiteSpace(info.displayName))
                    info.displayName = $"Asset {info.productId}";

                result.Add(info);
            }

            return result;
        }

        private static object ResolveService(string serviceTypeName)
        {
            EnsureServicesContainer();

            var serviceType = FindType($"{Ns}.{serviceTypeName}");
            if (serviceType == null)
                throw new TypeLoadException($"Unity internal type '{serviceTypeName}' was not found.");

            var resolveMethods = _servicesContainerType.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            foreach (var method in resolveMethods)
            {
                if (method.Name != "Resolve" || !method.IsGenericMethodDefinition)
                    continue;

                if (method.GetGenericArguments().Length != 1 || method.GetParameters().Length != 0)
                    continue;

                object resolved = method.MakeGenericMethod(serviceType).Invoke(_servicesContainer, null);
                if (resolved != null)
                    return resolved;
            }

            throw new MissingMethodException($"Could not resolve Unity service '{serviceTypeName}'.");
        }

        private static void EnsureServicesContainer()
        {
            if (_servicesContainer != null && _servicesContainerType != null)
                return;

            _servicesContainerType = FindType($"{Ns}.ServicesContainer");
            if (_servicesContainerType == null)
                throw new TypeLoadException("Unity Package Manager ServicesContainer was not found.");

            Type type = _servicesContainerType;
            PropertyInfo instanceProperty = null;

            while (type != null && instanceProperty == null)
            {
                instanceProperty = type.GetProperty(
                    "instance",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                type = type.BaseType;
            }

            if (instanceProperty == null)
            {
                var singletonType = typeof(ScriptableSingleton<>).MakeGenericType(_servicesContainerType);
                instanceProperty = singletonType.GetProperty(
                    "instance",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            }

            _servicesContainer = instanceProperty?.GetValue(null);
            if (_servicesContainer == null)
                throw new InvalidOperationException("Could not obtain Unity Package Manager ServicesContainer.instance.");
        }

        private static Type FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(fullName, false);
                if (type != null)
                    return type;
            }

            return Type.GetType($"{fullName}, UnityEditor", false);
        }

        private static MethodInfo FindMethod(Type type, string name, int parameterCount)
        {
            return type
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(x => x.Name == name && x.GetParameters().Length == parameterCount);
        }

        private static object Invoke(object target, string methodName, params object[] args)
        {
            if (target == null)
                return null;

            var method = FindMethod(target.GetType(), methodName, args?.Length ?? 0);
            if (method == null)
                throw new MissingMethodException($"{target.GetType().Name}.{methodName} was not found.");

            return method.Invoke(target, args);
        }

        private static void InvokeIfExists(object target, string methodName)
        {
            if (target == null)
                return;

            var method = FindMethod(target.GetType(), methodName, 0);
            method?.Invoke(target, null);
        }

        private static object GetMemberValue(object target, string name)
        {
            if (target == null)
                return null;

            var type = target.GetType();

            var field = type.GetField(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
                return field.GetValue(target);

            var property = type.GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return property?.GetValue(target);
        }

        private static string GetStringMember(object target, string name)
        {
            return GetMemberValue(target, name)?.ToString() ?? string.Empty;
        }

        private static long GetLongMember(object target, string name)
        {
            object value = GetMemberValue(target, name);
            if (value == null)
                return 0;

            try
            {
                return Convert.ToInt64(value);
            }
            catch
            {
                return 0;
            }
        }

        private static float GetFloatMember(object target, string name)
        {
            object value = GetMemberValue(target, name);
            if (value == null) return 0f;
            try { return Convert.ToSingle(value); }
            catch { return 0f; }
        }

        private static bool GetBoolMember(object target, string name)
        {
            object value = GetMemberValue(target, name);
            if (value == null) return false;
            try { return Convert.ToBoolean(value); }
            catch { return false; }
        }

        private static string[] GetStringArrayMember(object target, string name)
        {
            object value = GetMemberValue(target, name);

            if (value is string[] array)
                return array;

            if (value is IEnumerable enumerable)
            {
                var result = new List<string>();
                foreach (var item in enumerable)
                {
                    if (item != null)
                        result.Add(item.ToString());
                }

                return result.ToArray();
            }

            return Array.Empty<string>();
        }

        private static Delegate CreateSingleArgumentEventDelegate(
            Type delegateType,
            Action<object> callback)
        {
            var invoke = delegateType.GetMethod("Invoke");
            var parameters = invoke?.GetParameters();

            if (parameters == null || parameters.Length != 1)
                throw new InvalidOperationException("Unexpected Unity Asset Store event signature.");

            var parameter = Expression.Parameter(parameters[0].ParameterType, "arg");
            var callbackConstant = Expression.Constant(callback);
            var callbackInvoke = typeof(Action<object>).GetMethod(nameof(Action<object>.Invoke));

            var body = Expression.Call(
                callbackConstant,
                callbackInvoke!,
                Expression.Convert(parameter, typeof(object)));

            return Expression.Lambda(delegateType, body, parameter).Compile();
        }

        private static Delegate CreateTwoArgumentEventDelegate(
            Type delegateType,
            Action<object, object> callback)
        {
            var invoke = delegateType.GetMethod("Invoke");
            var parameters = invoke?.GetParameters();
            if (parameters == null || parameters.Length != 2)
                throw new InvalidOperationException("Unexpected Unity Asset Store error event signature.");

            var p0 = Expression.Parameter(parameters[0].ParameterType, "arg0");
            var p1 = Expression.Parameter(parameters[1].ParameterType, "arg1");
            var callbackConstant = Expression.Constant(callback);
            var callbackInvoke = typeof(Action<object, object>).GetMethod(nameof(Action<object, object>.Invoke));
            var body = Expression.Call(
                callbackConstant,
                callbackInvoke!,
                Expression.Convert(p0, typeof(object)),
                Expression.Convert(p1, typeof(object)));
            return Expression.Lambda(delegateType, body, p0, p1).Compile();
        }

        private static Exception Unwrap(Exception ex)
        {
            while (ex is TargetInvocationException tie && tie.InnerException != null)
                ex = tie.InnerException;

            return ex;
        }
    }
}
