using Interstellar.Messages;
using Interstellar.Messages.Variation;
using Interstellar.Server.Services;
using System.Diagnostics.CodeAnalysis;

namespace Interstellar.Server.VoiceChat;

internal class VCClient
{
    internal record Profile(string PlayerName, byte PlayerId);

    VCClientService service;
    VCRoom myRoom;
    Profile? profile = null;

    public bool IsClosed => service.ConnectionState == WebSocketSharp.WebSocketState.Closed;

    public bool IsMute { get; private set; } = false;
    public bool IsImpostorRadio { get; private set; } = false;

    public VCRoom Room => myRoom;

    public byte ClientId { get; }

    public VCClient(VCClientService service, byte clientId, VCRoom room)
    {
        this.service = service;
        this.ClientId = clientId;
        this.myRoom = room;
    }

    public void UpdateMuteStatus(bool isMute, bool isImpostorRadio = false)
    {
        if(this.IsMute == isMute && this.IsImpostorRadio == isImpostorRadio) return;
        this.IsMute = isMute;
        this.IsImpostorRadio = isImpostorRadio;
        myRoom.Broadcast(ClientId, new ShareMuteStatusMessage(ClientId, isMute, isImpostorRadio));
    }

    /// <summary>Called when someone other than this client joins or leaves.</summary>
    public void OnJoinOrLeaveAnyone(long currentMask) {
        this.service.SendTracksMask(currentMask);
    }

    public void NoticeLeaveClient(byte clientId)
    {
        this.service.SendClientLeft(clientId);
    }

    /// <summary>Broadcasts own audio to the room. Skips forwarding when muted or frame is effectively silence.</summary>
    public void BroadcastAudio(uint durationRtpUnits, byte[] encodedAudio)
    {
        if (IsMute) return; // Server-side mute guard: don't forward audio from muted clients
        // Skip frames ≤ 2 bytes: DTX comfort noise or near-silence — saves pointless relay bandwidth
        if (encodedAudio.Length <= 2) return;
        myRoom.Broadcast(ClientId, durationRtpUnits, encodedAudio);
    }

    /// <summary>Broadcasts host room settings to all other clients and caches them for new joins.</summary>
    public void BroadcastHostSettings(HostSettingsMessage message)
    {
        myRoom.LastHostSettings = message;
        myRoom.Broadcast(ClientId, message);
    }

    public void BroadcastRawMessage(ReadOnlySpan<byte> message)
    {
        myRoom.BroadcastRawMessage(ClientId, message.ToArray());
    }

    public void SendAudio(int id, uint durationRtpUnits, byte[] encodedAudio)
    {
        this.service.SendAudio(id, durationRtpUnits, encodedAudio);
    }

    public void Send(byte[] rawMessage)
    {
        this.service.SendRawMessage(rawMessage);
    }

    public void Send(IMessage message) => this.service.SendMessage(message);

    public void UpdateProfile(string playerName, byte playerId)
    {
        this.profile = new Profile(playerName, playerId);
        myRoom.Broadcast(ClientId, new ShareProfileMessage(ClientId, playerName, playerId));
    }

    public bool TryGetProfile([MaybeNullWhen(false)]out string playerName, out byte playerId)
    {
        if(this.profile != null)
        {
            playerName = this.profile.PlayerName;
            playerId = this.profile.PlayerId;
            return true;
        }
        playerName = null;
        playerId = 0;
        return false;
    }

    public void Close()
    {
        myRoom.Leave(this);
    }

    internal IEnumerable<ShareProfileMessage> ShareExistingProfiles()
    {
        foreach (var c in myRoom.Clients)
        {
            if (c.ClientId != this.ClientId && c.TryGetProfile(out var name, out var pid))
            {
                yield return new ShareProfileMessage(c.ClientId, name, pid);
            }
        }
    }
}
