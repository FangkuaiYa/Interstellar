using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Interstellar.Server.VoiceChat;

static internal class RoomManager
{
    static private Dictionary<string, VCRoom> rooms = [];

    /// <summary>Total connected clients across all rooms.</summary>
    static public int TotalClientCount
    {
        get
        {
            int count = 0;
            foreach (var room in rooms.Values)
                count += room.ClientCount;
            return count;
        }
    }

    /// <summary>Number of active rooms.</summary>
    static public int RoomCount => rooms.Count;

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
}
