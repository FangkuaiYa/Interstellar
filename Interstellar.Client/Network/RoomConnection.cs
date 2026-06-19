using Concentus;
using Interstellar.Messages;
using Interstellar.Messages.Variation;
using NAudio.Wave;
using SIPSorcery.Net;
using System;
using System.Text;
using VoiceChatPlugin;
using VoiceChatPlugin.VoiceChat;
using WebSocketSharp;

namespace Interstellar.Network;

internal interface IConnectionContext
{
    /// <summary>
    /// Called when an audio frame is received.
    /// </summary>
    /// <param name="clientId">The sender client ID.</param>
    /// <param name="samples">Audio sample array. The array is reused after this call; do not use for other purposes.</param>
    /// <param name="length">Length of the audio data.</param>
    void OnAudioFrameReceived(int clientId, float[] samples, int length);

    /// <summary>
    /// Called when a client disconnects.
    /// </summary>
    void OnClientDisconnected(int clientId); 

    /// <summary>
    /// Called when a client profile is updated.
    /// </summary>
    void OnClientProfileUpdated(int clientId, string playerName, byte playerId);

    /// <summary>
    /// Called when a mute status update is received.
    /// </summary>
    void OnReceiveMuteStatus(int clientId, bool isMute);

    /// <summary>
    /// Called when a custom message is received.
    /// </summary>
    void OnCustomMessageReceived(byte[] message);

    /// <summary>
    /// Called when host room settings are received from the server.
    /// </summary>
    void OnHostSettingsReceived(HostSettingsMessage settings);

    /// <summary>
    /// Called when server info is received (optimal players, current count, VC URL).
    /// </summary>
    void OnServerInfoReceived(ServerInfoMessage message);
}

/// <summary>
/// Connects to the server and handles audio/data transmission and reception.
/// </summary>
internal class RoomConnection : IMessageProcessor
{
    private readonly string roomCode;
    private readonly string region;
    private readonly WebSocket socket;
    private RTCPeerConnection? connection = null;
    private ProfileMessage? profileMessage = null;
    private int? myClientId = null;

    public int MyClientId => myClientId ?? -1;

    private AudioStream? localAudioStream;

    IConnectionContext context;
    public RoomConnection(IConnectionContext context, string roomCode, string region, string url)
    {
        this.context = context;
        this.roomCode = roomCode;
        this.region = region;

        this.socket = new WebSocket(url) { Compression = WebSocketSharp.CompressionMethod.Deflate };
        if (url.StartsWith("wss:")) this.socket.SslConfiguration.EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls11 | System.Security.Authentication.SslProtocols.Tls12;
        this.socket.OnMessage += (sender, e) =>
        {
            if (e.IsBinary) MessagePacker.UnpackMessages(e.RawData, this);
        };
        this.socket.OnError += (sender, e) =>
        {
            InterstellarPlugin.Logger.LogError($"[VC] WebSocket error: {e.Message} (url={url})");
        };
        this.socket.OnClose += (sender, e) =>
        {
            InterstellarPlugin.Logger.LogWarning($"[VC] WebSocket closed: code={e.Code} reason={e.Reason} (url={url})");
        };
        Connect();
    }

    public void UpdateMuteStatus(bool mute)
    {
        var message = new UpdateMuteStatusMessage(mute);
        socket.SendMessage(message);
    }

    /// <summary>
    /// Updates the player's in-game profile information.
    /// </summary>
    public void UpdateProfile(string playerName, byte playerId)
    {
        var message = new ProfileMessage(playerName, playerId);
        profileMessage = message;
        TrySendProfile();
    }

    private void TrySendProfile()
    {
        if (profileMessage != null && socket.IsAlive)
        {
            this.socket.SendMessage(profileMessage);
            profileMessage = null;
        }
    }

    private void Connect()
    {
        this.socket.OnOpen += (sender, e) =>
        {
            InterstellarPlugin.Logger.LogInfo($"[VC] WebSocket connected. Setting up RTC...");
            SetUpRTCConnection();
            this.socket.SendMessage(new JoinMessage(this.roomCode, this.region));
            InterstellarPlugin.Logger.LogInfo($"[VC] JoinMessage sent: room={this.roomCode} region={this.region}");
        };
        this.socket.Connect();
        InterstellarPlugin.Logger.LogInfo($"[VC] WebSocket connecting...");
    }

    private void SetUpRTCConnection()
    {
        // Function to pass audio frames
        float[] buffer = new float[2048];
        Dictionary<int, IOpusDecoder> decoders = new(64);
        HashSet<int> decodeErrors = new();
        void DecodeAndAddSample(int id, byte[] encodedAudio)
        {
            try
            {
                if (!decoders.ContainsKey(id)) decoders[id] = AudioHelpers.GetOpusDecoder();

                var decoder = decoders[id];
                int length = decoder.Decode(encodedAudio, buffer, buffer.Length);
                context.OnAudioFrameReceived(id, buffer, length);
            }
            catch (Exception excep)
            {
                // Opus decode errors are normal on network jitter — log once per decoder
                if (!decodeErrors.Contains(id))
                {
                    decodeErrors.Add(id);
                    System.Console.WriteLine("[VC] Opus decode error for client " + id + ": " + excep.Message);
                }
            }
        }


        TrySendProfile();
        this.connection = new RTCPeerConnection(WebSocketHelpers.GetRTCConfiguration());
        this.connection.OnAudioFrameReceived += frame =>
        {
            DecodeAndAddSample(frame.AudioFormat.FormatID, frame.EncodedAudio);
        };
        this.connection.onicecandidate += (candidate) =>
        {
            this.socket.SendMessage(new IceCandMessage(candidate.candidate, candidate.sdpMid, candidate.sdpMLineIndex, candidate.usernameFragment));
        };
    }

    private IOpusEncoder encoder = AudioHelpers.GetOpusEncoder();
    byte[] encodedBuffer = new byte[2048];
    public void SendAudio(float[] sampleBuffer, int sampleLength, double bufferMilliseconds)
    {
        if(localAudioStream == null) return;

        var durationRtpUnits = bufferMilliseconds.ToRtpUnits(AudioHelpers.ClockRate);
        int encodedLength = encoder.Encode(sampleBuffer, sampleLength, encodedBuffer, encodedBuffer.Length);
        // Skip frames ≤ 2 bytes: DTX comfort noise or near-silence — saves bandwidth
        if (encodedLength <= 2) return;
        localAudioStream?.SendAudio(durationRtpUnits, new ArraySegment<byte>(encodedBuffer, 0, encodedLength));
    }


    int IMessageProcessor.Process(MessageTag tag, ReadOnlySpan<byte> bytes)
    {
        int read = -1;
        switch (tag)
        {
            case MessageTag.ShareId:
                OnReceiveMyClientId(ShareIdMessage.DeserializeWithoutTag(bytes, out read));
                break;
            case MessageTag.SdpOffer:
                OnReceiveSdpOffer(SdpOfferMessage.DeserializeWithoutTag(bytes, out read));
                break;
            case MessageTag.AddIceCand:
                OnReceiveIceCandMessage(IceCandMessage.DeserializeWithoutTag(bytes, out read));
                break;
            case MessageTag.ShareProfile:
                var profile = ShareProfileMessage.DeserializeWithoutTag(bytes, out read);
                context.OnClientProfileUpdated(profile.AudioId, profile.PlayerName, profile.PlayerId);
                break;
            case MessageTag.NoticeDisconnect:
                var disconnect =NoticeDisconnectMessage.DeserializeWithoutTag(bytes, out read);
                context.OnClientDisconnected(disconnect.ClientId);
                break;
            case MessageTag.ShareMuteStatus:
                var muteStatus = ShareMuteStatusMessage.DeserializeWithoutTag(bytes, out read);
                context.OnReceiveMuteStatus(muteStatus.ClientId, muteStatus.IsMute);
                break;
            case MessageTag.Custom:
                CustomMessage.DeserializeForServerWithoutTag(bytes, out read);
                context.OnCustomMessageReceived(bytes.ToArray());
                break;
            case MessageTag.HostSettings:
                var hostSettings = HostSettingsMessage.DeserializeWithoutTag(bytes, out read);
                context.OnHostSettingsReceived(hostSettings);
                break;
            case MessageTag.ServerInfo:
                var serverInfo = ServerInfoMessage.DeserializeWithoutTag(bytes, out read);
                context.OnServerInfoReceived(serverInfo);
                break;
        }
        return read;
    }

    private void OnReceiveMyClientId(ShareIdMessage message)
    {
        int id = message.Id;
        myClientId = id;
        InterstellarPlugin.Logger.LogInfo($"[VC] Received client ID: {id}");
        var localTrack = new MediaStreamTrack(AudioHelpers.GetOpusFormat(id), MediaStreamStatusEnum.SendOnly);
        connection!.addTrack(localTrack);
        localAudioStream = connection.AudioStreamList.Find(a => a.GetSendingFormat().ID == id);
        InterstellarPlugin.Logger.LogInfo($"[VC] Local audio track added (id={id}, stream={localAudioStream != null})");
    }

    MediaStreamTrack[] tracks = new MediaStreamTrack[AudioHelpers.MaxTracks]; 
    private void OnReceiveSdpOffer(SdpOfferMessage message)
    {
        InterstellarPlugin.Logger.LogInfo($"[VC] Received SDP offer (mask={message.Mask})");
        // Update tracks (without removing)
        long mask = message.Mask;
        for (int i = 0; i < AudioHelpers.MaxTracks; i++)
        {
            if ((mask & (1L << i)) != 0)
            {
                if (tracks[i] != null) continue;

                var format = AudioHelpers.GetOpusFormat(i);
                var track = new MediaStreamTrack(format, MediaStreamStatusEnum.RecvOnly);
                connection!.addTrack(track);
                tracks[i] = track;
            }
        }

        // Process SDP
        connection!.setRemoteDescription(new RTCSessionDescriptionInit { sdp = message.Sdp, type = RTCSdpType.offer });
        var answer = connection.createAnswer(null);
        connection.setLocalDescription(answer).Wait();
        socket.SendMessage(new SdpAnswerMessage(answer.sdp));
        InterstellarPlugin.Logger.LogInfo("[VC] SDP answer sent.");
    }

    private void OnReceiveIceCandMessage(IceCandMessage message)
    {
        InterstellarPlugin.Logger.LogInfo($"[VC] Received ICE candidate (mid={message.SdpMid})");
        connection!.addIceCandidate(new RTCIceCandidateInit
        {
            candidate = message.Candidate,
            sdpMid = message.SdpMid,
            sdpMLineIndex = (ushort)message.SdpMLineIndex,
            usernameFragment = message.UsernameFragment
        });
    }

    internal void SendCustomMessage(byte[] message)
    {
        socket.SendMessage(new CustomMessage(message));
    }

    internal void SendZeroSizeMessage(MessageTag tag)
    {
        socket.SendMessage(tag);
    }

    internal void SendHostSettings(VoiceChatRoomSettings s)
    {
        var msg = new HostSettingsMessage(
            s.MaxChatDistance, s.WallsBlockSound, s.OnlyHearInSight,
            s.ImpostorHearGhosts, s.OnlyGhostsCanTalk, s.HearInVent,
            s.VentPrivateChat, s.CommsSabDisables, s.CameraCanHear,
            s.ImpostorPrivateRadio, s.OnlyMeetingOrLobby);
        socket.SendMessage(msg);
    }

    internal void Disconnect()
    {
        connection?.Close("Client left the game.");
        socket.Close();
    }
}
