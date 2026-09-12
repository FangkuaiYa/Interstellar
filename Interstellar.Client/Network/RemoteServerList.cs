using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.Json;
using Il2CppInterop.Runtime.Attributes;
using Interstellar.Voice;
using UnityEngine;
using UnityEngine.Networking;

namespace Interstellar.Network;

public static class RemoteServerList
{
    private const string RemoteListUrl = "https://api.amongusclub.cn/Interstellar/ServerList.json";
    private const float TimeoutSeconds = 6f;
    private const float RefetchIntervalSeconds = 30f * 60f;

    private static readonly List<(string Name, string URL)> _cached = new();
    private static bool _fetchedOnce;
    private static float _lastFetchTime = float.NegativeInfinity;
    private static bool _fetching;
    private static FetchRunner? _runner;

    public static IReadOnlyList<(string Name, string URL)>? Cached => _fetchedOnce && _cached.Count > 0 ? _cached : null;

    public static void RefreshIfNeeded(MonoBehaviour host)
    {
        if (_fetching) return;
        if (_fetchedOnce && Time.unscaledTime - _lastFetchTime < RefetchIntervalSeconds) return;
        if (host == null) return;

        if (_runner == null)
        {
            var go = new GameObject("VC_FetchRunner");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            _runner = go.AddComponent<FetchRunner>();
        }
        _runner.StartFetch();
    }

    public class FetchRunner : MonoBehaviour
    {
        public FetchRunner(IntPtr ptr) : base(ptr) { }

        public void StartFetch()
        {
            StartCoroutine(nameof(CoFetch));
        }

        [HideFromIl2Cpp]
        private IEnumerator CoFetch()
        {
            _fetching = true;
            var req = UnityWebRequest.Get(RemoteListUrl);
            req.timeout = (int)TimeoutSeconds;
            yield return req.SendWebRequest();

            try
            {
                if (req.result != UnityWebRequest.Result.Success)
                {
                    InterstellarPlugin.Logger?.LogWarning($"[VC:RemoteServers] Fetch failed ({req.result}): {req.error}. Keeping existing list.");
                    yield break;
                }

                var parsed = Parse(req.downloadHandler.text);
                if (parsed == null || parsed.Count == 0)
                {
                    InterstellarPlugin.Logger?.LogWarning("[VC:RemoteServers] Fetched list had no valid entries. Keeping existing list.");
                    yield break;
                }

                _cached.Clear();
                _cached.AddRange(parsed);
                _fetchedOnce = true;
                InterstellarPlugin.Logger?.LogInfo($"[VC:RemoteServers] Loaded {_cached.Count} server(s) from remote list.");
            }
            finally
            {
                _lastFetchTime = Time.unscaledTime;
                _fetching = false;
                req.Dispose();
            }
        }
    }

    private static List<(string Name, string URL)>? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("servers", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return null;

            var result = new List<(string, string)>();
            foreach (var entry in arr.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                if (!entry.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String) continue;
                if (!entry.TryGetProperty("url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String) continue;

                string name = nameEl.GetString() ?? "";
                string url = urlEl.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) continue;

                result.Add((name, url));
            }
            return result;
        }
        catch (Exception e)
        {
            InterstellarPlugin.Logger?.LogWarning($"[VC:RemoteServers] Parse failed: {e.Message}");
            return null;
        }
    }
}
