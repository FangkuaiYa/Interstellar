using System.Text.Json;
using System.Text.Json.Serialization;
using UnityEngine.Networking;

namespace VoiceChatPlugin;

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

	private class ServerListRoot
	{
		[JsonPropertyName("servers")]
		public List<ServerEntry> Servers { get; set; } = new();
	}

	private static List<ServerEntry>? _servers;
	private static readonly string FallbackUrl = "ws://47.122.116.50:22021";

	internal static void Load()
	{
		FetchServers();
	}

	private static void FetchServers()
	{
		try
		{
			var request = UnityWebRequest.Get(ApiUrl);
			request.timeout = 8;
			request.SendWebRequest();

			while (!request.isDone) { }

			if (request.result != UnityWebRequest.Result.Success)
			{
				throw new Exception($"Web request failed: {request.error}");
			}

			string json = request.downloadHandler.text;
			var root = JsonSerializer.Deserialize<ServerListRoot>(json);

			if (root?.Servers is { Count: > 0 })
			{
				_servers = root.Servers;
				InterstellarPlugin.Logger?.LogInfo($"[VC] Loaded server list from API: {_servers.Count} entries.");
			}
			else
			{
				throw new Exception("Server list is empty or invalid.");
			}
		}
		catch (Exception ex)
		{
			InterstellarPlugin.Logger?.LogWarning($"[VC] Failed to fetch online server list: {ex.Message}");
			_servers = null;
		}
	}

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
					InterstellarPlugin.Logger?.LogInfo($"[VC] Matched server '{currentRegion.Name}' → VC: {match.VcServer}");
					return match.VcServer;
				}
			}
		}

		InterstellarPlugin.Logger?.LogInfo("[VC] No server match, using fallback VC URL.");
		return FallbackUrl;
	}
}