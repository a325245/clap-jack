using System;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;

namespace SamplePlugin.Chat;

public class ChatHandler
{
    public Action<string>? OnChatCommand { get; set; }
    public Action<string, string>? OnPlayerTell { get; set; }

    public void SendChatMessage(string message, ChatChannel channel)
    {
        string command = channel switch
        {
            ChatChannel.Party => $"/party {message}",
            ChatChannel.Say   => $"/say {message}",
            ChatChannel.Tell  => message,
            _                 => $"/say {message}"
        };
        OnChatCommand?.Invoke(command);
    }

    public void SendTell(string playerName, string server, string message)
    {
        OnPlayerTell?.Invoke($"{playerName}@{server}", $"/tell \"{playerName}@{server}\" {message}");
    }

    public ChatChannel DetectChatChannel(XivChatType chatType)
    {
        // Explicit known party types
        if (chatType == XivChatType.Party ||
            chatType == XivChatType.Alliance ||
            chatType == XivChatType.CrossParty)
        {
            return ChatChannel.Party;
        }

        // Explicit known say types
        if (chatType == XivChatType.Say ||
            chatType == XivChatType.Yell ||
            chatType == XivChatType.Shout)
        {
            return ChatChannel.Say;
        }

        // Tell types
        if (chatType == XivChatType.TellIncoming ||
            chatType == XivChatType.TellOutgoing)
        {
            return ChatChannel.Tell;
        }

        // FALLBACK: treat anything else as Say so commands are never silently dropped
        return ChatChannel.Say;
    }
}

public enum ChatChannel
{
    Say,
    Party,
    Tell
}
