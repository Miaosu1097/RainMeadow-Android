#if MEADOW_STEAM_GAMEPLAY
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ProtoBuf;
using SteamKit2;
using SteamKit2.Internal;

namespace RainMeadow;

internal sealed class SteamLobbyChat : ClientMsgHandler
{
    private const int MaxPayloadBytes = 4096;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly Queue<(ulong sender, string text)> incoming = new();
    private ulong lobbyId;

    internal void SetLobby(ulong id)
    {
        lock (incoming)
        {
            if (lobbyId == id) return;
            lobbyId = id;
            incoming.Clear();
        }
    }

    internal bool TrySend(ulong id, string text)
    {
        if (id == 0 || string.IsNullOrWhiteSpace(text) || text.Length > ChatTextBox.textLimit
            || text.IndexOf('\0') >= 0 || !Client.IsConnected)
            return false;

        try
        {
            Client.Send(CreateMessage(id, text));
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static ClientMsgProtobuf<CMsgClientMMSSendLobbyChatMsg> CreateMessage(ulong id, string text)
    {
        var message = new ClientMsgProtobuf<CMsgClientMMSSendLobbyChatMsg>(EMsg.ClientMMSSendLobbyChatMsg);
        message.ProtoHeader.routing_appid = SteamLobbySession.RainWorldAppId;
        message.Body.app_id = SteamLobbySession.RainWorldAppId;
        message.Body.steam_id_lobby = id;
        // Steam's SendLobbyChatMsg uses a zero target for a lobby-wide message.
        message.Body.steam_id_target = 0;
        message.Body.lobby_message = Utf8.GetBytes(text);
        return message;
    }

    public override void HandleMsg(IPacketMsg packet)
    {
        if (packet.MsgType != EMsg.ClientMMSLobbyChatMsg || !packet.IsProto) return;

        try
        {
            var body = new ClientMsgProtobuf<CMsgClientMMSLobbyChatMsg>(packet).Body;
            if (body.app_id != SteamLobbySession.RainWorldAppId || body.steam_id_sender == 0
                || body.lobby_message is not { Length: > 0 and <= MaxPayloadBytes } payload)
                return;

            lock (incoming)
            {
                if (lobbyId == 0 || body.steam_id_lobby != lobbyId || incoming.Count >= 64) return;
                int length = payload.Length;
                if (payload[length - 1] == 0) length--;
                string text = Utf8.GetString(payload, 0, length);
                if (text.Length <= ChatTextBox.textLimit && text.IndexOf('\0') < 0
                    && !string.IsNullOrWhiteSpace(text))
                    incoming.Enqueue((body.steam_id_sender, text));
            }
        }
        catch (Exception error) when (error is ProtoException or IOException or OverflowException or DecoderFallbackException)
        {
            // Ignore malformed chat packets without disturbing the Steam session.
        }
    }

    internal bool TryReceive(out (ulong sender, string text) message)
    {
        lock (incoming)
        {
            if (incoming.Count == 0)
            {
                message = default;
                return false;
            }
            message = incoming.Dequeue();
            return true;
        }
    }
}
#endif
