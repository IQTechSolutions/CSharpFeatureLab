using System.Net;
using System.Net.Http.Json;
using FeatureLab.Features.Chat;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

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

        return tokens.AccessToken;
    }
}
