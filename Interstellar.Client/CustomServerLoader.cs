using System.Text.Json;
using System.Text.Json.Serialization;
using HarmonyLib;
using UnityEngine.Networking;
using UnityEngine.Events;
using UnityEngine;
using AmongUs.Data.Player;
using System.Linq;

namespace VoiceChatPlugin;

/// <summary>
/// Fetches custom servers from API, registers them as Among Us regions,
/// and blocks connections to non-custom servers.
/// </summary>
internal static class CustomServerLoader
{
	private const string ApiUrl = "https://api.amongusclub.cn/Interstellar/ServerList.json";

	private class ServerEntry
	{
		[JsonPropertyName("name")]
		public string Name { get; set; } = "";

		[JsonPropertyName("address")]
		public string Address { get; set; } = "";

		[JsonPropertyName("port")]
		public ushort Port { get; set; }

		[JsonPropertyName("vc")]
		public string? VcServer { get; set; }
	}

	private static List<ServerEntry>? _servers;
	private static IRegionInfo[]? _regions;

	private const string FallbackVcUrl = "ws://47.122.116.50:22021";
	private const string DefaultRegionName = "Modded Asia (MAS)";

	// Entry point — called by InterstellarPlugin.Load()
	internal static void Load()
	{
		FetchServers();
	}

	// Fetch server list from API
	private static void FetchServers()
	{
		try
		{
			var request = UnityWebRequest.Get(ApiUrl);
			request.timeout = 8;
			var op = request.SendWebRequest();

			// Blocking wait is OK during BepInEx load phase
			while (!op.isDone) { }

			if (request.result != UnityWebRequest.Result.Success)
				throw new Exception($"HTTP request failed: {request.error}");

			string json = request.downloadHandler.text;

			// Support both bare array and {"servers": [...]} formats
			List<ServerEntry>? list = null;
			if (json.TrimStart().StartsWith("["))
			{
				list = JsonSerializer.Deserialize<List<ServerEntry>>(json);
			}
			else
			{
				var root = JsonSerializer.Deserialize<ServerListRoot>(json);
				list = root?.Servers;
			}

			if (list is not { Count: > 0 })
				throw new Exception("Server list is empty or invalid.");

			_servers = list;
			BuildRegions(list);
			InterstellarPlugin.Logger?.LogInfo($"[VC] Loaded {list.Count} servers from API.");
		}
		catch (Exception ex)
		{
			InterstellarPlugin.Logger?.LogWarning($"[VC] Failed to fetch server list: {ex.Message}");
			_servers = null;
			_regions = null;
		}
	}

	// Convert ServerEntry list to IRegionInfo array for game registration
	private static void BuildRegions(List<ServerEntry> entries)
	{
		var list = new List<IRegionInfo>();
		foreach (var e in entries)
		{
			list.Add(new StaticHttpRegionInfo(
				e.Name,
				StringNames.NoTranslation,
				e.Address,
				new[] { new ServerInfo(e.Name + "-1", e.Address, e.Port, false) }).Cast<IRegionInfo>());
		}
		_regions = list.ToArray();
	}

	internal static IRegionInfo[]? GetRegions() => _regions;

	// Match current game region to a VC server URL
	internal static string GetVCServer()
	{
		if (_servers != null)
		{
			var currentRegion = ServerManager.Instance?.CurrentRegion;
			if (currentRegion != null)
			{
				var match = _servers.Find(e =>
					string.Equals(e.Name, currentRegion.Name, StringComparison.OrdinalIgnoreCase));
				if (match != null && !string.IsNullOrEmpty(match.VcServer))
				{
					InterstellarPlugin.Logger?.LogInfo($"[VC] Matched '{currentRegion.Name}' → VC: {match.VcServer}");
					return match.VcServer;
				}
			}
		}

		InterstellarPlugin.Logger?.LogInfo("[VC] No match, using fallback VC URL.");
		return FallbackVcUrl;
	}

	private class ServerListRoot
	{
		[JsonPropertyName("servers")]
		public List<ServerEntry> Servers { get; set; } = new();
	}

	// ═══════════════════════════════════════════════════════════════
	// Harmony Patches
	// ═══════════════════════════════════════════════════════════════

	/// <summary>Skip official Auth for custom servers.</summary>
	[HarmonyPatch(typeof(AuthManager), nameof(AuthManager.CoConnect))]
	public static class SkipAuthCoConnectPatch
	{
		public static bool Prefix()
		{
			if (_regions != null && ServerManager.Instance != null)
			{
				var cur = ServerManager.Instance.CurrentRegion;
				if (cur != null && _regions.Any(r => r.Name == cur.Name))
					return false;
			}
			return true;
		}
	}

	/// <summary>Skip nonce wait for custom servers.</summary>
	[HarmonyPatch(typeof(AuthManager), nameof(AuthManager.CoWaitForNonce))]
	public static class SkipNoncePatch
	{
		public static bool Prefix()
		{
			if (_regions != null && ServerManager.Instance != null)
			{
				var cur = ServerManager.Instance.CurrentRegion;
				if (cur != null && _regions.Any(r => r.Name == cur.Name))
					return false;
			}
			return true;
		}
	}

	/// <summary>Replace AvailableRegions with custom server list.</summary>
	[HarmonyPatch(typeof(ServerManager), nameof(ServerManager.Awake))]
	public static class ServerManagerAwakePatch
	{
		public static void Postfix(ServerManager __instance)
		{
			if (_regions == null || _regions.Length == 0) return;

			__instance.AvailableRegions = _regions;

			var cur = __instance.CurrentRegion;
			if (cur == null || !_regions.Any(r => r.Name == cur.Name))
			{
				var def = _regions.FirstOrDefault(r => r.Name == DefaultRegionName) ?? _regions[0];
				__instance.SetRegion(def);
				InterstellarPlugin.Logger?.LogInfo($"[VC] Set default region: {def.Name}");
			}
		}
	}

	/// <summary>Auto-select first custom server on main menu start.</summary>
	[HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start))]
	public static class MainMenuManagerStartPatch
	{
		private static bool _initialized;

		public static void Postfix()
		{
			if (_initialized) return;
			if (_regions == null || _regions.Length == 0) return;

			var sm = ServerManager.Instance;
			if (sm == null) return;

			var cur = sm.CurrentRegion;
			if (cur == null || !_regions.Any(r => r.Name == cur.Name))
			{
				var def = _regions.FirstOrDefault(r => r.Name == DefaultRegionName) ?? _regions[0];
				sm.SetRegion(def);
				InterstellarPlugin.Logger?.LogInfo($"[VC] MainMenu: switched to {def.Name}");
			}

			_initialized = true;
		}
	}
}