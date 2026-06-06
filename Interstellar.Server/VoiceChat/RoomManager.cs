using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Interstellar.Server.VoiceChat;

static internal class RoomManager
{
    static private Dictionary<string, VCRoom> rooms = [];

    static public VCRoom GetRoom(string region, string roomId)
    {
        string key = region + "." + roomId;
        if (rooms.TryGetValue(key, out var room))
        {
            return room;
        }
        else
        {
            room = new VCRoom(key);
            rooms[key] = room;
            return room;
        }
    }

    static public void RemoveRoom(string key) => rooms.Remove(key);

    public static int TotalClientCount
    {
        get
        {
            int count = 0;
            foreach (var room in rooms.Values)
                count += room.ClientCount;
            return count;
        }
    }

    public static int RoomCount => rooms.Count;

    public readonly record struct RoomSnapshot(string Region, string RoomId, int ClientCount, ClientSnapshot[] Clients);
    public readonly record struct ClientSnapshot(byte VoiceId, byte? PlayerId, string? PlayerName, bool IsMuted);

    public static List<RoomSnapshot> GetRoomSnapshots()
    {
        var list = new List<RoomSnapshot>();
        foreach (var kv in rooms)
        {
            var room = kv.Value;
            var clients = new List<ClientSnapshot>();
            foreach (var c in room.Clients)
            {
                c.TryGetProfile(out var name, out var pid);
                clients.Add(new ClientSnapshot(c.ClientId, pid != 0 ? pid : null, name, c.IsMute));
            }
            var parts = kv.Key.Split('.', 2);
            list.Add(new RoomSnapshot(parts[0], parts.Length > 1 ? parts[1] : "", room.ClientCount, clients.ToArray()));
        }
        return list;
    }
}
