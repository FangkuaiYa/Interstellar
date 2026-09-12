using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Interstellar.Voice;
using UnityEngine;

namespace Interstellar.Network;

public static class RemoteServerList
{
    private const string RemoteListUrl = "https://api.amongusclub.cn/Interstellar/ServerList.json";
    private const double RefetchIntervalSeconds = 30.0 * 60.0;

    private static readonly List<(string Name, string URL)> _cached = new();
    private static bool _fetchedOnce;
    private static double _lastFetchTicks;
    private static bool _fetching;
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(6) };

    public static IReadOnlyList<(string Name, string URL)>? Cached => _fetchedOnce && _cached.Count > 0 ? _cached : null;

    public static void RefreshIfNeeded(MonoBehaviour _)
    {
        if (_fetching) return;
        if (_fetchedOnce && (Time.realtimeSinceStartupAsDouble - _lastFetchTicks) < RefetchIntervalSeconds) return;

        _fetching = true;
        Task.Run(async () =>
        {
            try
            {
                var json = await _http.GetStringAsync(RemoteListUrl);
                var parsed = Parse(json);
                if (parsed == null || parsed.Count == 0)
                {
                    InterstellarPlugin.Logger?.LogWarning("[VC:RemoteServers] Fetched list had no valid entries. Keeping existing list.");
                    return;
                }

                lock (_cached)
                {
                    _cached.Clear();
                    _cached.AddRange(parsed);
                }
                _fetchedOnce = true;
                InterstellarPlugin.Logger?.LogInfo($"[VC:RemoteServers] Loaded {parsed.Count} server(s) from remote list.");
            }
            catch (Exception e)
            {
                InterstellarPlugin.Logger?.LogWarning($"[VC:RemoteServers] Fetch failed: {e.Message}. Keeping existing list.");
            }
            finally
            {
                _lastFetchTicks = Time.realtimeSinceStartupAsDouble;
                _fetching = false;
            }
        });
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
