using FeatureLab.Features.Chat;
using FeatureLab.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace FeatureLab.Web.Tests;

public sealed class ChatMessageStoreTests : IClassFixture<FeatureLabWebFactory>
{
    private readonly FeatureLabWebFactory _factory;

    public ChatMessageStoreTests(FeatureLabWebFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ListRecent_returns_the_latest_fifty_in_chronological_order()
    {
        using var client = _factory.CreateClient();
        await using var scope = _factory.Services.CreateAsyncScope();
        var tenant = scope.ServiceProvider
            .GetRequiredService<TenantContext>();
        tenant.Set(Guid.NewGuid());
        var store = scope.ServiceProvider
            .GetRequiredService<IChatMessageStore>();
        var start = new DateTimeOffset(
            2026,
            7,
            28,
            12,
            0,
            0,
            TimeSpan.Zero);

        for (var index = 0; index < 52; index++)
        {
            await store.AddAsync(
                new ChatMessage(
                    Guid.NewGuid(),
                    "Member",
                    $"History message {index}",
                    start.AddSeconds(index)),
                "synthetic-member-id",
                CancellationToken.None);
        }

        var history = await store.ListRecentAsync(
            ChatHub.HistoryLimit,
            CancellationToken.None);

        Assert.Equal(ChatHub.HistoryLimit, history.Count);
        Assert.Equal("History message 2", history[0].Text);
        Assert.Equal("History message 51", history[^1].Text);
        Assert.True(
            history
                .Zip(history.Skip(1))
                .All(pair => pair.First.SentAtUtc < pair.Second.SentAtUtc));
    }

    [Fact]
    public async Task Store_fails_closed_without_an_established_tenant()
    {
        using var client = _factory.CreateClient();
        await using var scope = _factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider
            .GetRequiredService<IChatMessageStore>();
        var message = new ChatMessage(
            Guid.NewGuid(),
            "Member",
            "No tenant means no chat",
            DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.AddAsync(
                message,
                "synthetic-member-id",
                CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.ListRecentAsync(
                ChatHub.HistoryLimit,
                CancellationToken.None));
    }
}
