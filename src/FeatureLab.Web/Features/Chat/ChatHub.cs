using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace FeatureLab.Features.Chat;

[Authorize]
public sealed class ChatHub(
    TimeProvider timeProvider,
    IChatMessageStore messageStore) : Hub
{
    public const string Route = "/hubs/chat";
    public const string MessageReceived = "MessageReceived";
    public const int MaximumMessageLength = 240;
    public const int HistoryLimit = 50;

    public async Task SendMessage(string text)
    {
        var normalizedText = text?.Trim() ?? string.Empty;

        if (normalizedText.Length is < 1 or > MaximumMessageLength)
        {
            throw new HubException(
                $"Messages must contain 1 to {MaximumMessageLength} characters.");
        }

        var authorId = Context.UserIdentifier;
        if (string.IsNullOrWhiteSpace(authorId))
        {
            throw new HubException("Unable to identify the chat member.");
        }

        var message = new ChatMessage(
            Guid.NewGuid(),
            "Member",
            normalizedText,
            timeProvider.GetUtcNow());

        await messageStore.AddAsync(
            message,
            authorId,
            Context.ConnectionAborted);
        await Clients.All.SendAsync(
            MessageReceived,
            message,
            Context.ConnectionAborted);
    }

    public Task<IReadOnlyList<ChatMessage>> GetRecentMessages() =>
        messageStore.ListRecentAsync(
            HistoryLimit,
            Context.ConnectionAborted);
}

public sealed record ChatMessage(
    Guid Id,
    string Sender,
    string Text,
    DateTimeOffset SentAtUtc);
