using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace FeatureLab.Features.Chat;

[Authorize]
public sealed class ChatHub(TimeProvider timeProvider) : Hub
{
    public const string Route = "/hubs/chat";
    public const string MessageReceived = "MessageReceived";
    public const int MaximumMessageLength = 240;

    public async Task SendMessage(string text)
    {
        var normalizedText = text?.Trim() ?? string.Empty;

        if (normalizedText.Length is < 1 or > MaximumMessageLength)
        {
            throw new HubException(
                $"Messages must contain 1 to {MaximumMessageLength} characters.");
        }

        var message = new ChatMessage(
            Guid.NewGuid(),
            "Member",
            normalizedText,
            timeProvider.GetUtcNow());

        await Clients.All.SendAsync(MessageReceived, message);
    }
}

public sealed record ChatMessage(
    Guid Id,
    string Sender,
    string Text,
    DateTimeOffset SentAtUtc);
