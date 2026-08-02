using FeatureLab.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace FeatureLab.Features.Chat;

[Authorize(Policy = TenantMembership.Policy)]
public sealed class ChatHub(
    TimeProvider timeProvider,
    IChatMessageStore messageStore,
    TenantContext tenantContext) : Hub
{
    public const string Route = "/hubs/chat";
    public const string MessageReceived = "MessageReceived";
    public const int MaximumMessageLength = 240;
    public const int HistoryLimit = 50;

    public override async Task OnConnectedAsync()
    {
        var tenantId = EstablishTenant();
        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            GroupName(tenantId),
            Context.ConnectionAborted);
        await base.OnConnectedAsync();
    }

    public async Task SendMessage(string text)
    {
        var tenantId = EstablishTenant();
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
        await Clients.Group(GroupName(tenantId)).SendAsync(
            MessageReceived,
            message,
            Context.ConnectionAborted);
    }

    public Task<IReadOnlyList<ChatMessage>> GetRecentMessages()
    {
        EstablishTenant();
        return messageStore.ListRecentAsync(
            HistoryLimit,
            Context.ConnectionAborted);
    }

    private Guid EstablishTenant()
    {
        if (Context.User is not { } principal
            || !TenantMembership.TryGetTenantId(
                principal,
                out var tenantId))
        {
            throw new HubException(
                "Unable to establish the chat tenant.");
        }

        tenantContext.Set(tenantId);
        return tenantId;
    }

    private static string GroupName(Guid tenantId) =>
        $"tenant:{tenantId:N}";
}

public sealed record ChatMessage(
    Guid Id,
    string Sender,
    string Text,
    DateTimeOffset SentAtUtc);
