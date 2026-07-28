using System.Net;
using System.Net.Http.Json;
using FeatureLab.Data;
using FeatureLab.Features.Chat;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FeatureLab.Web.Tests;

public sealed class ChatHubTests : IClassFixture<FeatureLabWebFactory>
{
    private readonly FeatureLabWebFactory _factory;

    public ChatHubTests(FeatureLabWebFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Anonymous_connections_are_rejected()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsync(
            $"{ChatHub.Route}/negotiate?negotiateVersion=1",
            content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SendMessage_broadcasts_server_authored_message_to_connected_members()
    {
        var senderToken = await RegisterAndGetAccessTokenAsync();
        var observerToken = await RegisterAndGetAccessTokenAsync();
        await using var sender = CreateConnection(senderToken);
        await using var observer = CreateConnection(observerToken);
        var senderReceived = MessageReceivedBy(sender);
        var observerReceived = MessageReceivedBy(observer);

        await sender.StartAsync();
        await observer.StartAsync();
        await sender.InvokeAsync("SendMessage", "  Hello from SignalR  ");

        var senderMessage = await senderReceived.WaitAsync(TimeSpan.FromSeconds(5));
        var observerMessage = await observerReceived.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(senderMessage, observerMessage);
        Assert.NotEqual(Guid.Empty, senderMessage.Id);
        Assert.Equal("Member", senderMessage.Sender);
        Assert.Equal("Hello from SignalR", senderMessage.Text);
        Assert.InRange(
            senderMessage.SentAtUtc,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddMinutes(1));
    }

    [Fact]
    public async Task SendMessage_rejects_invalid_text_with_a_safe_error()
    {
        var bearerValue = await RegisterAndGetAccessTokenAsync();
        await using var connection = CreateConnection(bearerValue);
        await connection.StartAsync();

        var error = await Assert.ThrowsAsync<HubException>(
            () => connection.InvokeAsync("SendMessage", "   "));

        Assert.Contains(
            $"Messages must contain 1 to {ChatHub.MaximumMessageLength} characters.",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecentMessages_returns_server_authored_history_to_a_later_connection()
    {
        var sender = await RegisterMemberAsync();
        await using var sendingConnection = CreateConnection(sender.AccessToken);
        var received = MessageReceivedBy(sendingConnection);
        await sendingConnection.StartAsync();

        var uniqueText = $"Persist this message {Guid.NewGuid():N}";
        await sendingConnection.InvokeAsync("SendMessage", uniqueText);
        var liveMessage = await received.WaitAsync(TimeSpan.FromSeconds(5));
        await sendingConnection.StopAsync();

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider
                .GetRequiredService<FeatureLabDbContext>();
            var stored = await dbContext.ChatMessages
                .AsNoTracking()
                .SingleAsync(message => message.Id == liveMessage.Id);

            Assert.Equal(sender.UserId, stored.AuthorId);
            Assert.Equal(liveMessage, stored.ToMessage());
        }

        var readerToken = await RegisterAndGetAccessTokenAsync();
        await using var readingConnection = CreateConnection(readerToken);
        await readingConnection.StartAsync();

        var history = await readingConnection
            .InvokeAsync<ChatMessage[]>("GetRecentMessages");
        var replayedMessage = Assert.Single(
            history,
            message => message.Id == liveMessage.Id);

        Assert.Equal(liveMessage, replayedMessage);
    }

    private HubConnection CreateConnection(string accessToken) =>
        new HubConnectionBuilder()
            .WithUrl(
                new Uri(_factory.Server.BaseAddress, ChatHub.Route),
                options =>
                {
                    options.Transports = HttpTransportType.LongPolling;
                    options.AccessTokenProvider =
                        () => Task.FromResult<string?>(accessToken);
                    options.HttpMessageHandlerFactory =
                        _ => _factory.Server.CreateHandler();
                })
            .Build();

    private static Task<ChatMessage> MessageReceivedBy(HubConnection connection)
    {
        var received = new TaskCompletionSource<ChatMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        connection.On<ChatMessage>(
            ChatHub.MessageReceived,
            message => received.TrySetResult(message));

        return received.Task;
    }

    private async Task<string> RegisterAndGetAccessTokenAsync()
    {
        var member = await RegisterMemberAsync();
        return member.AccessToken;
    }

    private async Task<RegisteredMember> RegisterMemberAsync()
    {
        using var client = _factory.CreateClient();
        var email = $"chat-learner-{Guid.NewGuid():N}@example.test";
        const string password = "FeatureLab!123";

        var registration = await client.PostAsJsonAsync("/account/register", new
        {
            email,
            password,
        });
        registration.EnsureSuccessStatusCode();

        var login = await client.PostAsJsonAsync("/account/login", new
        {
            email,
            password,
        });
        login.EnsureSuccessStatusCode();

        var tokens = await login.Content.ReadFromJsonAsync<LoginTokenResponse>();
        Assert.NotNull(tokens);
        Assert.False(string.IsNullOrWhiteSpace(tokens.AccessToken));

        await using var scope = _factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<FeatureLabDbContext>();
        var userId = await dbContext.Users
            .Where(user => user.Email == email)
            .Select(user => user.Id)
            .SingleAsync();

        return new RegisteredMember(tokens.AccessToken, userId);
    }

    private sealed record RegisteredMember(
        string AccessToken,
        string UserId);
}
