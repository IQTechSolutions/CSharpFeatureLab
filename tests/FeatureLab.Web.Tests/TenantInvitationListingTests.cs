using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FeatureLab.Data;
using FeatureLab.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FeatureLab.Web.Tests;

public sealed class TenantInvitationListingTests(
    FeatureLabWebFactory factory)
    : IClassFixture<FeatureLabWebFactory>
{
    [Fact]
    public async Task Owner_lists_only_safe_current_invitations_from_selected_tenant()
    {
        var tenantId = Guid.NewGuid();
        using var owner = await RegisterMemberAsync(
            tenantId,
            TenantMembershipRole.Owner);
        var foreignTenantId = Guid.NewGuid();
        using var foreignOwner = await RegisterMemberAsync(
            foreignTenantId,
            TenantMembershipRole.Owner);
        var issued = await IssueAsync(
            owner.Client,
            $"issued-{Guid.NewGuid():N}@example.test");
        var now = DateTimeOffset.UtcNow;
        var earlierEmail = $"earlier-{Guid.NewGuid():N}@example.test";
        var expiredEmail = $"expired-{Guid.NewGuid():N}@example.test";
        var closedEmail = $"closed-{Guid.NewGuid():N}@example.test";
        var acceptedEmail = $"accepted-{Guid.NewGuid():N}@example.test";
        var foreignEmail = $"foreign-{Guid.NewGuid():N}@example.test";
        var seeded = await SeedLifecycleRowsAsync(
            tenantId,
            foreignTenantId,
            owner.UserId,
            earlierEmail,
            expiredEmail,
            closedEmail,
            acceptedEmail,
            foreignEmail,
            now);

        var response = await owner.Client.GetAsync(
            $"/api/tenant-invitations?tenantId={foreignTenantId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        var invitations = json.RootElement.EnumerateArray().ToArray();
        Assert.Equal(2, invitations.Length);
        Assert.Equal(seeded.EarlierId, invitations[0].GetProperty("id").GetGuid());
        Assert.Equal(
            seeded.EarlierNormalizedEmail,
            invitations[0].GetProperty("email").GetString());
        Assert.Equal(issued.Id, invitations[1].GetProperty("id").GetGuid());
        Assert.All(
            invitations,
            invitation => Assert.Equal(
                new[] { "id", "email", "expiresAt" },
                invitation.EnumerateObject()
                    .Select(property => property.Name)
                    .ToArray()));
        Assert.DoesNotContain(issued.Code, body, StringComparison.Ordinal);
        Assert.DoesNotContain(
            Hash(issued.Code),
            body,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tenantId", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("codeHash", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("issuedBy", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("closedAt", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("acceptedAt", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("version", body, StringComparison.OrdinalIgnoreCase);

        await AssertLifecycleRowsUnchangedAsync(seeded);
        GC.KeepAlive(foreignOwner);
    }

    [Fact]
    public async Task Another_current_owner_lists_workspace_invitations()
    {
        var tenantId = Guid.NewGuid();
        using var issuer = await RegisterMemberAsync(
            tenantId,
            TenantMembershipRole.Owner);
        using var otherOwner = await RegisterMemberAsync(
            tenantId,
            TenantMembershipRole.Owner);
        var issued = await IssueAsync(
            issuer.Client,
            $"co-owner-{Guid.NewGuid():N}@example.test");

        var response = await otherOwner.Client.GetFromJsonAsync<
            PendingInvitationResponse[]>("/api/tenant-invitations");

        var invitation = Assert.Single(Assert.IsType<
            PendingInvitationResponse[]>(response));
        Assert.Equal(issued.Id, invitation.Id);
    }

    [Fact]
    public async Task Empty_selected_workspace_cannot_be_redirected_by_query_input()
    {
        var emptyTenantId = Guid.NewGuid();
        using var emptyOwner = await RegisterMemberAsync(
            emptyTenantId,
            TenantMembershipRole.Owner);
        var populatedTenantId = Guid.NewGuid();
        using var populatedOwner = await RegisterMemberAsync(
            populatedTenantId,
            TenantMembershipRole.Owner);
        await IssueAsync(
            populatedOwner.Client,
            $"foreign-only-{Guid.NewGuid():N}@example.test");

        var response = await emptyOwner.Client.GetAsync(
            $"/api/tenant-invitations?tenantId={populatedTenantId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var invitations = await response.Content
            .ReadFromJsonAsync<PendingInvitationResponse[]>();
        Assert.Empty(Assert.IsType<PendingInvitationResponse[]>(invitations));
    }

    [Fact]
    public async Task Anonymous_member_and_stale_owner_sessions_are_denied()
    {
        using var anonymous = factory.CreateClient();
        var anonymousResponse = await anonymous.GetAsync(
            "/api/tenant-invitations");

        var memberTenantId = Guid.NewGuid();
        using var member = await RegisterMemberAsync(
            memberTenantId,
            TenantMembershipRole.Member);
        var memberResponse = await member.Client.GetAsync(
            "/api/tenant-invitations");

        var versionTenantId = Guid.NewGuid();
        using var versionOwner = await RegisterMemberAsync(
            versionTenantId,
            TenantMembershipRole.Owner);
        var stampTenantId = Guid.NewGuid();
        using var stampOwner = await RegisterMemberAsync(
            stampTenantId,
            TenantMembershipRole.Owner);
        await MakeSessionsStaleAsync(
            versionOwner,
            versionTenantId,
            stampOwner);
        var versionResponse = await versionOwner.Client.GetAsync(
            "/api/tenant-invitations");
        var stampResponse = await stampOwner.Client.GetAsync(
            "/api/tenant-invitations");

        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, memberResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, versionResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, stampResponse.StatusCode);
    }

    [Fact]
    public async Task Store_returns_null_when_live_owner_coordinates_are_stale()
    {
        var tenantId = Guid.NewGuid();
        using var owner = await RegisterMemberAsync(
            tenantId,
            TenantMembershipRole.Owner);
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<FeatureLabDbContext>();
        var user = await dbContext.Users.SingleAsync(
            user => user.Id == owner.UserId);
        var membership = await dbContext.TenantMemberships.SingleAsync(
            membership => membership.UserId == owner.UserId
                && membership.TenantId == tenantId);
        var store = scope.ServiceProvider
            .GetRequiredService<ITenantInvitationStore>();

        var result = await store.ListPendingForOwnerAsync(
            owner.UserId,
            Assert.IsType<string>(user.SecurityStamp),
            membership.Version + 1,
            tenantId);

        Assert.Null(result);
    }

    private async Task<SeededLifecycleRows> SeedLifecycleRowsAsync(
        Guid tenantId,
        Guid foreignTenantId,
        string acceptedByUserId,
        string earlierEmail,
        string expiredEmail,
        string closedEmail,
        string acceptedEmail,
        string foreignEmail,
        DateTimeOffset now)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<FeatureLabDbContext>();
        var normalizer = scope.ServiceProvider
            .GetRequiredService<ILookupNormalizer>();
        var earlier = NewInvitation(
            tenantId,
            earlierEmail,
            now.AddHours(1),
            normalizer);
        var expired = NewInvitation(
            tenantId,
            expiredEmail,
            now.AddMinutes(-1),
            normalizer);
        var closed = NewInvitation(
            tenantId,
            closedEmail,
            now.AddHours(2),
            normalizer);
        closed.Close(now);
        var accepted = NewInvitation(
            tenantId,
            acceptedEmail,
            now.AddHours(3),
            normalizer);
        accepted.Accept(acceptedByUserId, now);
        var foreign = NewInvitation(
            foreignTenantId,
            foreignEmail,
            now.AddMinutes(30),
            normalizer);
        dbContext.TenantInvitations.AddRange(
            earlier,
            expired,
            closed,
            accepted,
            foreign);
        await dbContext.SaveChangesAsync();
        return new SeededLifecycleRows(
            earlier.Id,
            earlier.NormalizedEmail,
            expired.Id,
            closed.Id,
            accepted.Id,
            foreign.Id);
    }

    private async Task AssertLifecycleRowsUnchangedAsync(
        SeededLifecycleRows seeded)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<FeatureLabDbContext>();
        var invitations = await dbContext.TenantInvitations
            .Where(invitation => new[]
                {
                    seeded.ExpiredId,
                    seeded.ClosedId,
                    seeded.AcceptedId,
                    seeded.ForeignId,
                }
                .Contains(invitation.Id))
            .ToDictionaryAsync(invitation => invitation.Id);
        Assert.Null(invitations[seeded.ExpiredId].ClosedAt);
        Assert.Equal(1, invitations[seeded.ExpiredId].Version);
        Assert.NotNull(invitations[seeded.ClosedId].ClosedAt);
        Assert.Equal(2, invitations[seeded.ClosedId].Version);
        Assert.NotNull(invitations[seeded.AcceptedId].AcceptedAt);
        Assert.Equal(2, invitations[seeded.AcceptedId].Version);
        Assert.Null(invitations[seeded.ForeignId].ClosedAt);
        Assert.Equal(1, invitations[seeded.ForeignId].Version);
    }

    private static TenantInvitation NewInvitation(
        Guid tenantId,
        string email,
        DateTimeOffset expiresAt,
        ILookupNormalizer normalizer) =>
        TenantInvitation.Create(
            tenantId,
            Assert.IsType<string>(normalizer.NormalizeEmail(email)),
            Hash($"test-code-{Guid.NewGuid():N}"),
            expiresAt);

    private async Task MakeSessionsStaleAsync(
        RegisteredMember versionOwner,
        Guid versionTenantId,
        RegisteredMember stampOwner)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<FeatureLabDbContext>();
        var membership = await dbContext.TenantMemberships.SingleAsync(
            membership => membership.UserId == versionOwner.UserId
                && membership.TenantId == versionTenantId);
        membership.Remove(DateTimeOffset.UtcNow);
        membership.Reactivate(TenantMembershipRole.Owner);
        var user = await dbContext.Users.SingleAsync(
            user => user.Id == stampOwner.UserId);
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        await dbContext.SaveChangesAsync();
    }

    private async Task<IssueInvitationResponse> IssueAsync(
        HttpClient client,
        string email)
    {
        var response = await client.PostAsJsonAsync(
            "/api/tenant-invitations",
            new { email });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var queued = Assert.IsType<QueuedInvitationResponse>(
            await response.Content
                .ReadFromJsonAsync<QueuedInvitationResponse>());
        Assert.Equal("queued", queued.DeliveryStatus);
        var recorder = factory.Services
            .GetRequiredService<RecordingTenantInvitationDelivery>();
        var dispatcher = factory.Services
            .GetRequiredService<TenantInvitationOutboxDispatcher>();
        RecordedTenantInvitationDelivery? delivery = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await dispatcher.ProcessBatchAsync();
            if (recorder.TryTake(queued.Id, out delivery))
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        Assert.NotNull(delivery);
        return new IssueInvitationResponse(
            queued.Id,
            delivery.Code,
            queued.ExpiresAt);
    }

    private async Task<RegisteredMember> RegisterMemberAsync(
        Guid tenantId,
        TenantMembershipRole role)
    {
        var account = await RegisterAccountAsync();
        await TenantTestData.ProvisionAsync(
            factory.Services,
            account.Email,
            tenantId,
            role: role);
        var client = await SignInAsync(account.Email, account.Password);
        return new RegisteredMember(
            client,
            account.UserId,
            account.Email,
            account.Password);
    }

    private async Task<RegisteredAccount> RegisterAccountAsync()
    {
        using var client = factory.CreateClient();
        var email = $"listing-member-{Guid.NewGuid():N}@example.test";
        const string password = "FeatureLab!123";
        var registration = await client.PostAsJsonAsync(
            "/account/register",
            new { email, password });
        registration.EnsureSuccessStatusCode();

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<FeatureLabDbContext>();
        var userId = await dbContext.Users
            .Where(user => user.Email == email)
            .Select(user => user.Id)
            .SingleAsync();
        return new RegisteredAccount(userId, email, password);
    }

    private async Task<HttpClient> SignInAsync(
        string email,
        string password)
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync(
            "/account/login",
            new { email, password });
        login.EnsureSuccessStatusCode();
        var tokens = await login.Content
            .ReadFromJsonAsync<LoginTokenResponse>();
        Assert.NotNull(tokens);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    private static string Hash(string code) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(code)));

    private sealed record RegisteredAccount(
        string UserId,
        string Email,
        string Password);

    private sealed record RegisteredMember(
        HttpClient Client,
        string UserId,
        string Email,
        string Password) : IDisposable
    {
        public void Dispose() => Client.Dispose();
    }

    private sealed record IssueInvitationResponse(
        Guid Id,
        string Code,
        DateTimeOffset ExpiresAt);

    private sealed record QueuedInvitationResponse(
        Guid Id,
        DateTimeOffset ExpiresAt,
        string DeliveryStatus);

    private sealed record PendingInvitationResponse(
        Guid Id,
        string Email,
        DateTimeOffset ExpiresAt);

    private sealed record SeededLifecycleRows(
        Guid EarlierId,
        string EarlierNormalizedEmail,
        Guid ExpiredId,
        Guid ClosedId,
        Guid AcceptedId,
        Guid ForeignId);
}
