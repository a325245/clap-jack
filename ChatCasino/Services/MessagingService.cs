using System;
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using ChatCasino.UI;

namespace ChatCasino.Services;

public sealed class MessagingService : IMessageService
{
    private static readonly Regex LeadingGameTagRegex = new(@"^\[[^\]]+\]\s*", RegexOptions.Compiled);
    private readonly ConcurrentQueue<QueuedMessage> queue = new();
    private readonly Random delayRng = new();
    private DateTime nextSendNotBeforeUtc = DateTime.MinValue;

    public int MessageDelayMs { get; set; } = 400;
    public int TellDelayMs { get; set; } = 4000;

    public Action<string>? OnPartyMessage { get; set; }
    public Action<string, string, string>? OnTellMessage { get; set; }
    public Action<string>? OnAdminEcho { get; set; }

    public void QueuePartyMessage(string message)
        => queue.Enqueue(new QueuedMessage(MessageKind.Party, string.Empty, string.Empty, FormatPartyMessage(Sanitize(message))));

    public void QueueTell(string playerName, string server, string message)
        => queue.Enqueue(new QueuedMessage(MessageKind.Tell, playerName, server, Sanitize(message)));

    public void QueueAdminEcho(string message)
        => queue.Enqueue(new QueuedMessage(MessageKind.AdminEcho, string.Empty, String.Empty, Sanitize(message)));

    public void ProcessTick()
    {
        if (!queue.TryPeek(out var next)) return;
        if (DateTime.UtcNow < nextSendNotBeforeUtc) return;
        if (!queue.TryDequeue(out next)) return;

        switch (next.Kind)
        {
            case MessageKind.Party:
                OnPartyMessage?.Invoke(next.Message);
                break;
            case MessageKind.Tell:
                OnTellMessage?.Invoke(next.PlayerName, next.Server, next.Message);
                break;
            case MessageKind.AdminEcho:
                OnAdminEcho?.Invoke(next.Message);
                break;
        }

        var requiredDelay = next.Kind == MessageKind.Tell
            ? TellDelayMs
            : ResolveDealerMessageDelayMs();

        nextSendNotBeforeUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(0, requiredDelay));
    }

    public void ClearAllQueues()
    {
        while (queue.TryDequeue(out _)) { }
    }

    private int ResolveDealerMessageDelayMs()
    {
        if (!CasinoUI.RandomizeDealerChatDelay)
            return Math.Max(0, CasinoUI.DealerChatDelayMinMs);

        var min = Math.Max(0, Math.Min(CasinoUI.DealerChatDelayMinMs, CasinoUI.DealerChatDelayMaxMs));
        var max = Math.Max(min, Math.Max(CasinoUI.DealerChatDelayMinMs, CasinoUI.DealerChatDelayMaxMs));
        return delayRng.Next(min, max + 1);
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c == '\n' || c == '\r' || c == '\t' || !char.IsControl(c))
                sb.Append(c);
        }

        var cleaned = sb.ToString().Trim();
        return cleaned.Length <= 500 ? cleaned : cleaned[..500];
    }

    private static string FormatPartyMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message) || !CasinoUI.NaturalChatOutput)
            return message;

        var stripped = LeadingGameTagRegex.Replace(message, string.Empty);
        return string.IsNullOrWhiteSpace(stripped) ? message : stripped.TrimStart();
    }

    private enum MessageKind
    {
        Party,
        Tell,
        AdminEcho
    }

    private readonly record struct QueuedMessage(MessageKind Kind, string PlayerName, string Server, string Message);
}
