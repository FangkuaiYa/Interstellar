using Interstellar.Routing;
using Interstellar.Routing.Router;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

public class VCPlayer
{
    private readonly StereoRouter.Property _imager;
    private readonly VolumeRouter.Property _normalVolume, _ghostVolume, _radioVolume, _clientVolume;
    private readonly LevelMeterRouter.Property _levelMeter;

    private byte _playerId = byte.MaxValue;
    private string _playerName = "Unknown";
    private PlayerControl? _mappedPlayer;

    public string PlayerName => _playerName;
    public byte PlayerId => _playerId;
    public float Volume => _clientVolume.Volume;
    public float Level => _levelMeter.Level;
    public bool IsMapped => _mappedPlayer != null && _mappedPlayer;

    public VCPlayer(
        VoiceChatRoom room,
        AudioRoutingInstance instance,
        StereoRouter imager,
        VolumeRouter normalVolume,
        VolumeRouter ghostVolume,
        VolumeRouter radioVolume,
        VolumeRouter clientVolume,
        LevelMeterRouter levelMeter)
    {
        _imager = imager.GetProperty(instance);
        _normalVolume = normalVolume.GetProperty(instance);
        _ghostVolume = ghostVolume.GetProperty(instance);
        _radioVolume = radioVolume.GetProperty(instance);
        _clientVolume = clientVolume.GetProperty(instance);
        _levelMeter = levelMeter.GetProperty(instance);
        _clientVolume.Volume = 1f;
        MuteAll();
    }

    public void UpdateProfile(byte playerId, string playerName)
    {
        _playerId = playerId;
        _playerName = playerName;
        _mappedPlayer = null;
        MuteAll();
    }

    public void ResetMapping()
    {
        _mappedPlayer = null;
        MuteAll();
    }

    private void CheckMapping()
    {
        if (_mappedPlayer != null && _mappedPlayer && _mappedPlayer.PlayerId == _playerId) return;
        _mappedPlayer = null;
        if (_playerId == byte.MaxValue) return;
        foreach (var p in PlayerControl.AllPlayerControls.ToArray())
            if (p.PlayerId == _playerId) { _mappedPlayer = p; break; }
    }

    public void SetVolume(float v) => _clientVolume.Volume = v;

    private void MuteAll()
    {
        _normalVolume.Volume = 0f;
        _ghostVolume.Volume = 0f;
        _radioVolume.Volume = 0f;
    }

    public void UpdateLobby()
    {
        CheckMapping();
        _imager.Pan = 0f;
        _normalVolume.Volume = 1f;
        _ghostVolume.Volume = 0f;
        _radioVolume.Volume = 0f;
    }

    public void UpdateMeeting()
    {
        CheckMapping();
        if (!IsMapped) { MuteAll(); return; }

        var s = VoiceChatConfig.SyncedRoomSettings;
        bool localDead = PlayerControl.LocalPlayer && PlayerControl.LocalPlayer.Data?.IsDead == true;
        bool targetDead = _mappedPlayer!.Data?.IsDead == true;
        bool localImp = PlayerControl.LocalPlayer && PlayerControl.LocalPlayer.Data?.Role?.IsImpostor == true;
        bool targetImp = _mappedPlayer.Data?.Role?.IsImpostor == true;

        _imager.Pan = 0f;

        if (s.OnlyGhostsCanTalk && !localDead) { MuteAll(); return; }

        // Dead players hear everyone during meetings
        if (localDead)
        {
            _normalVolume.Volume = targetDead ? 0f : 1f;
            _ghostVolume.Volume = targetDead ? 1f : 0f;
            _radioVolume.Volume = 0f;
            return;
        }

        // ImpostorPrivateRadio: non-impostors don't hear non-dead impostor speakers
        if (s.ImpostorPrivateRadio && targetImp && !targetDead && !localImp)
        {
            MuteAll();
            return;
        }

        if (s.ImpostorPrivateRadio && localImp && targetImp && !targetDead)
        {
            _normalVolume.Volume = 0f;
            _ghostVolume.Volume = 0f;
            _radioVolume.Volume = 1f;
            return;
        }

        // Local mic in impostor-only mode: hear impostors on radio, normal on normal
        if (VoiceChatHudState.IsImpostorRadioOnly && localImp)
        {
            if (targetImp && !targetDead)
            {
                _normalVolume.Volume = 0f;
                _ghostVolume.Volume = 0f;
                _radioVolume.Volume = 1f;
            }
            else if (!targetDead)
            {
                _normalVolume.Volume = 1f;
                _ghostVolume.Volume = 0f;
                _radioVolume.Volume = 0f;
            }
            else
            {
                MuteAll();
            }
            return;
        }

        _normalVolume.Volume = targetDead ? 0f : 1f;
        _ghostVolume.Volume = 0f;
        _radioVolume.Volume = 0f;
    }

    private float _wallCoeff = 1f;

    private static float CalcWallCoeff(Vector2 listener, Vector2 speaker, ref float coeff, VoiceChatRoomSettings s)
    {
        if (!s.WallsBlockSound) { coeff = 1f; return 1f; }

        if (s.OnlyHearInSight)
        {
            bool inSight = !Physics2D.Linecast(listener, speaker, LayerMask.GetMask("Shadow"));
            if (!inSight) { coeff = 0f; return 0f; }
        }

        bool hasWall = Physics2D.Linecast(listener, speaker, LayerMask.GetMask("Shadow"));
        coeff = coeff + ((hasWall ? 0f : 1f) - coeff) * Math.Clamp(Time.deltaTime * 4f, 0f, 1f);
        return coeff;
    }

    internal void UpdateTaskPhase(
        Vector2? listenerPos,
        IEnumerable<VoiceChatRoom.SpeakerCache> speakers,
        IEnumerable<IVoiceComponent> virtualMics,
        bool localInVent,
        bool commsSabActive)
    {
        CheckMapping();
        if (!IsMapped || !listenerPos.HasValue) { MuteAll(); return; }

        var s = VoiceChatConfig.SyncedRoomSettings;
        var targetPos = (Vector2)_mappedPlayer!.transform.position;
        bool localDead = PlayerControl.LocalPlayer && PlayerControl.LocalPlayer.Data?.IsDead == true;
        bool targetDead = _mappedPlayer.Data?.IsDead == true;
        bool localImp = PlayerControl.LocalPlayer && PlayerControl.LocalPlayer.Data?.Role?.IsImpostor == true;
        bool targetImp = _mappedPlayer.Data?.Role?.IsImpostor == true;
        bool targetInVent = _mappedPlayer.inVent;

        if (s.OnlyMeetingOrLobby) { MuteAll(); return; }
        if (s.OnlyGhostsCanTalk && !localDead) { MuteAll(); return; }
        if (commsSabActive && s.CommsSabDisables && !localImp && !localDead) { MuteAll(); return; }

        float dist = Vector2.Distance(targetPos, listenerPos.Value);
        float volume = VoiceChatRoom.GetVolume(dist, s.MaxChatDistance);
        float pan = VoiceChatRoom.GetPan(listenerPos.Value.x, targetPos.x);

        if (localDead)
        {
            // Dead players hear: alive on normal (spatial), dead on ghost
            if (targetDead)
            {
                _normalVolume.Volume = 0f;
                _ghostVolume.Volume = 1f;
                _radioVolume.Volume = 0f;
                _imager.Pan = 0f;
            }
            else
            {
                _normalVolume.Volume = volume * CalcWallCoeff(listenerPos.Value, targetPos, ref _wallCoeff, s);
                _ghostVolume.Volume = 0f;
                _radioVolume.Volume = 0f;
                _imager.Pan = pan;
            }
            return;
        }

        // ImpostorPrivateRadio: non-impostors don't hear non-dead impostor speakers
        if (s.ImpostorPrivateRadio && targetImp && !targetDead && !localImp)
        {
            MuteAll();
            return;
        }

        // ImpostorPrivateRadio: impostors hear each other on radio channel
        if (s.ImpostorPrivateRadio && localImp && targetImp && !targetDead)
        {
            _normalVolume.Volume = 0f;
            _ghostVolume.Volume = 0f;
            _radioVolume.Volume = 1f;
            _imager.Pan = 0f;
            return;
        }

        // Local mic in impostor-only mode: hear impostors on radio, normal players on normal
        if (VoiceChatHudState.IsImpostorRadioOnly && localImp)
        {
            if (targetImp && !targetDead)
            {
                _normalVolume.Volume = 0f;
                _ghostVolume.Volume = 0f;
                _radioVolume.Volume = 1f;
            }
            else if (!targetDead)
            {
                _normalVolume.Volume = volume * CalcWallCoeff(listenerPos.Value, targetPos, ref _wallCoeff, s);
                _ghostVolume.Volume = 0f;
                _radioVolume.Volume = 0f;
            }
            else
            {
                MuteAll();
            }
            _imager.Pan = 0f;
            return;
        }

        if (localImp && targetDead && s.ImpostorHearGhosts)
        {
            _normalVolume.Volume = 0f;
            _ghostVolume.Volume = volume;
            _radioVolume.Volume = 0f;
            _imager.Pan = pan;
            return;
        }

        if (targetDead) { MuteAll(); return; }

        if (targetInVent)
        {
            if (!s.HearInVent) { MuteAll(); return; }
            if (s.VentPrivateChat && !localInVent) { MuteAll(); return; }
        }
        else if (s.VentPrivateChat && localInVent)
        {
            MuteAll(); return;
        }

        _imager.Pan = pan;
        _normalVolume.Volume = volume * CalcWallCoeff(listenerPos.Value, targetPos, ref _wallCoeff, s);
        _ghostVolume.Volume = 0f;
        _radioVolume.Volume = 0f;
    }
}
