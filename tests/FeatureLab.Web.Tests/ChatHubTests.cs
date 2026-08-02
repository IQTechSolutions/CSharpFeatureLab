using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FeatureLab.Data;
using FeatureLab.Features.Chat;
using FeatureLab.Identity;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Identity;
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
    public async Task Connections_without_tenant_membership_are_forbidden()
    {
        var member = await RegisterMemberAsync(Guid.Empty);
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", member.AccessToken);

        var response = await client.PostAsync(
            $"{ChatHub.Route}/negotiate?negotiateVersion=1",
            content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SendMessage_broadcasts_server_authored_message_inside_the_tenant()
    {
        var tenantId = Guid.NewGuid();
        var senderMember = await RegisterMemberAsync(tenantId);
        var observerMember = await RegisterMemberAsync(tenantId);
        await using var sender = CreateConnection(senderMember.AccessToken);
        await using var observer = CreateConnection(observerMember.AccessToken);
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
    public async Task SendMessage_does_not_broadcast_to_another_tenant_for_the_same_user()
    {
        var members = await RegisterSameUserInDifferentTenantsAsync();
        await using var firstTenant = CreateConnection(
            members.FirstTenant.AccessToken);
        await using var secondTenant = CreateConnection(
            members.SecondTenant.AccessToken);
        var firstTenantReceived = MessageReceivedBy(firstTenant);
        var secondTenantReceived = MessageReceivedBy(secondTenant);

        await firstTenant.StartAsync();
        await secondTenant.StartAsync();
        await firstTenant.InvokeAsync(
            "SendMessage",
            "Tenant A only");

        var message = await firstTenantReceived.WaitAsync(
            TimeSpan.FromSeconds(5));
        Assert.Equal("Tenant A only", message.Text);
        await Assert.ThrowsAsync<TimeoutException>(
            () => secondTenantReceived.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Reconnected_member_rejoins_only_its_tenant_group()
    {
        var members = await RegisterSameUserInDifferentTenantsAsync();
        await using var firstTenant = CreateConnection(
            members.FirstTenant.AccessToken);
        await using var secondTenant = CreateConnection(
            members.SecondTenant.AccessToken);
        var firstTenantReceived = MessageReceivedBy(firstTenant);
        var secondTenantReceived = MessageReceivedBy(secondTenant);

        await firstTenant.StartAsync();
        var originalConnectionId = firstTenant.ConnectionId;
        await firstTenant.StopAsync();
        await firstTenant.StartAsync();
        await secondTenant.StartAsync();

        Assert.NotEqual(originalConnectionId, firstTenant.ConnectionId);
        await firstTenant.InvokeAsync(
            "SendMessage",
            "Tenant A after reconnect");
        var message = await firstTenantReceived.WaitAsync(
            TimeSpan.FromSeconds(5));

        Assert.Equal("Tenant A after reconnect", message.Text);
        await Assert.ThrowsAsync<TimeoutException>(
            () => secondTenantReceived.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task SendMessage_rejects_invalid_text_with_a_safe_error()
    {
        var member = await RegisterMemberAsync();
        await using var connection = CreateConnection(member.AccessToken);
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
                .IgnoreQueryFilters()
                .SingleAsync(message => message.Id == liveMessage.Id);

            Assert.Equal(sender.UserId, stored.AuthorId);
            Assert.Equal(sender.TenantId, stored.TenantId);
            Assert.Equal(liveMessage, stored.ToMessage());
        }

        var reader = await RegisterMemberAsync(sender.TenantId);
        await using var readingConnection = CreateConnection(reader.AccessToken);
        await readingConnection.StartAsync();

        var history = await readingConnection
            .InvokeAsync<ChatMessage[]>("GetRecentMessages");
        var replayedMessage = Assert.Single(
            history,
            message => message.Id == liveMessage.Id);

        Assert.Equal(liveMessage, replayedMessage);
    }

    [Fact]
    public async Task RecentMessages_hides_another_tenants_history_from_the_same_user()
    {
        var members = await RegisterSameUserInDifferentTenantsAsync();
        await using var firstTenant = CreateConnection(
            members.FirstTenant.AccessToken);
        await using var secondTenant = CreateConnection(
            members.SecondTenant.AccessToken);
        var firstTenantReceived = MessageReceivedBy(firstTenant);
        var secondTenantReceived = MessageReceivedBy(secondTenant);
        await firstTenant.StartAsync();
        await secondTenant.StartAsync();
        await firstTenant.InvokeAsync(
            "SendMessage",
            $"Tenant A history {Guid.NewGuid():N}");
        var firstTenantMessage = await firstTenantReceived.WaitAsync(
            TimeSpan.FromSeconds(5));
        await secondTenant.InvokeAsync(
            "SendMessage",
            $"Tenant B history {Guid.NewGuid():N}");
        var secondTenantMessage = await secondTenantReceived.WaitAsync(
            TimeSpan.FromSeconds(5));

        var firstTenantHistory = await firstTenant
            .InvokeAsync<ChatMessage[]>("GetRecentMessages");
        var secondTenantHistory = await secondTenant
            .InvokeAsync<ChatMessage[]>("GetRecentMessages");

        Assert.Contains(
            firstTenantHistory,
            message => message.Id == firstTenantMessage.Id);
        Assert.DoesNotContain(
            firstTenantHistory,
            message => message.Id == secondTenantMessage.Id);
        Assert.Contains(
            secondTenantHistory,
            message => message.Id == secondTenantMessage.Id);
        Assert.DoesNotContain(
            secondTenantHistory,
            message => message.Id == firstTenantMessage.Id);
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

    private async Task<RegisteredMember> RegisterMemberAsync(
        Guid? tenantId = null)
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

        if (tenantId is { } assignedTenantId)
        {
            await AssignTenantAsync(email, assignedTenantId);
        }

        var accessToken = await SignInAsync(email, password);

        await using var scope = _factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<FeatureLabDbContext>();
        var user = await dbContext.Users
            .Where(user => user.Email == email)
            .Select(user => new
            {
                user.Id,
                user.TenantId,
            })
            .SingleAsync();

        return new RegisteredMember(
            accessToken,
            user.Id,
            user.TenantId);
    }

    private async Task<TenantMembers> RegisterSameUserInDifferentTenantsAsync()
    {
        using var client = _factory.CreateClient();
        var email = $"chat-switcher-{Guid.NewGuid():N}@example.test";
        const string password = "FeatureLab!123";
        var registration = await client.PostAsJsonAsync("/account/register", new
        {
            email,
            password,
        });
        registration.EnsureSuccessStatusCode();

        var firstTenantId = Guid.NewGuid();
        await AssignTenantAsync(email, firstTenantId);
        var firstToken = await SignInAsync(email, password);

        var secondTenantId = Guid.NewGuid();
        await AssignTenantAsync(email, secondTenantId);
        var secondToken = await SignInAsync(email, password);

        await using var scope = _factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<FeatureLabDbContext>();
        var userId = await dbContext.Users
            .Where(user => user.Email == email)
            .Select(user => user.Id)
            .SingleAsync();

        return new TenantMembers(
            new RegisteredMember(firstToken, userId, firstTenantId),
            new RegisteredMember(secondToken, userId, secondTenantId));
    }

    private async Task AssignTenantAsync(string email, Guid tenantId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider
            .GetRequiredService<UserManager<FeatureLabUser>>();
        var user = await userManager.FindByEmailAsync(email);
        Assert.NotNull(user);
        user.TenantId = tenantId;
        var update = await userManager.UpdateAsync(user);
        Assert.True(
            update.Succeeded,
            string.Join(
                "; ",
                update.Errors.Select(error => error.Description)));
    }

    private async Task<string> SignInAsync(string email, string password)
    {
        using var client = _factory.CreateClient();
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

    private sealed record RegisteredMember(
        string AccessToken,
        string UserId,
        Guid TenantId);

    private sealed record TenantMembers(
        RegisteredMember FirstTenant,
        RegisteredMember SecondTenant);
}
