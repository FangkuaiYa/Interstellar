using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Interstellar.Voice;

public static class PublicLobbyManager
{
    public class LobbyInfo
    {
        public int id { get; set; }
        public string code { get; set; } = "";
        public string title { get; set; } = "";
        public string host { get; set; } = "";
        public int current_players { get; set; }
        public int max_players { get; set; }
        public string language { get; set; } = "";
        public string mods { get; set; } = "";
        public bool isPublic { get; set; }
        public string server { get; set; } = "";
        public int gameState { get; set; }
    }

    public static Action<int, string, string>? OnLobbyJoinResult;

    /// <summary>Cached lobby list from socket.io real-time events.</summary>
    public static Dictionary<int, LobbyInfo> LobbyMap { get; } = new();
    public static List<LobbyInfo> CachedLobbies => new(LobbyMap.Values);
    public static bool IsLoading { get; set; }
    public static string? LastError { get; set; }
    public static bool IsWatching { get; set; }

    // Called by ServerConnection when socket.io events arrive
    internal static void OnNewLobbies(string json)
    {
        try
        {
            var list = JsonSerializer.Deserialize<List<LobbyInfo>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (list != null)
                foreach (var l in list) LobbyMap[l.id] = l;
            // Field names of the first entry, no values — the deployed server may
            // spell the room code differently than "code" (copy reads it from here).
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array
                    && doc.RootElement.GetArrayLength() > 0
                    && doc.RootElement[0].ValueKind == JsonValueKind.Object)
                {
                    var names = new List<string>();
                    foreach (var p in doc.RootElement[0].EnumerateObject()) names.Add(p.Name);
                    InterstellarPlugin.Logger?.LogInfo(
                        "[VC] Lobby entry fields: " + string.Join(",", names));
                }
            }
            catch { }
        }
        catch (Exception ex)
        { InterstellarPlugin.Logger?.LogWarning("[VC] Lobby new parse failed: " + ex.GetType().Name); }
        IsLoading = false;
        // A fresh snapshot: ask the server for every joinable row's code so the
        // rows show real join codes instead of the mods fallback.
        PublicLobbyWindow.Instance?.FetchMissingCodes();
    }

    internal static void OnUpdateLobby(string json)
    {
        try
        {
            var lobby = JsonSerializer.Deserialize<LobbyInfo>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (lobby != null)
            {
                // The wire object never carries `code` (BCL rebuilds the entry
                // from its own field list) — an update must not blank what a
                // fetch or a Copy press already learned about this row.
                if (string.IsNullOrEmpty(lobby.code)
                    && LobbyMap.TryGetValue(lobby.id, out var prev)
                    && !string.IsNullOrEmpty(prev.code))
                    lobby.code = prev.code;
                LobbyMap[lobby.id] = lobby;
                if (lobby.gameState == 0 && string.IsNullOrEmpty(lobby.code))
                    PublicLobbyWindow.Instance?.FetchMissingCodes();
            }
        }
        catch (Exception ex)
        { InterstellarPlugin.Logger?.LogWarning("[VC] Lobby update parse failed: " + ex.GetType().Name); }
    }

    internal static void OnRemoveLobby(int id)
    {
        LobbyMap.Remove(id);
    }

    public static void StartWatching()
    {
        IsWatching = true;
        IsLoading = true;
        LobbyMap.Clear();
    }

    public static void StopWatching()
    {
        IsWatching = false;
        LobbyMap.Clear();
    }

    /// <summary>BCL's wire enum for lobby entries: 0=Lobby 1=Tasks 2=Discussion
    /// 3=Menu 4=Unknown. The server hands out join codes only for 0 and greys
    /// every other state, so the client speaks this numbering end to end —
    /// publish, display and the Copy button all agree with it.</summary>
    public static string GetGameStateName(int state)
    {
        return state switch
        {
            0 => "Lobby", 1 => "Tasks", 2 => "Discussion", 3 => "Menu", _ => "?"
        };
    }
}
