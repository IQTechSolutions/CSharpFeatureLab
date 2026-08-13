using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FeatureLab.Data;
using FeatureLab.Identity;
using FeatureLab.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FeatureLab.Web.Tests;

public sealed class TenantInvitationIssuanceTests(
    FeatureLabWebFactory factory)
    : IClassFixture<FeatureLabWebFactory>
{
    [Fact]
    public async Task Owner_issues_one_time_code_from_server_owned_scope()
    {
        var tenantId = Guid.NewGuid();
        using var owner = await RegisterMemberAsync(
            tenantId,
            TenantMembershipRole.Owner);
        var recipientEmail =
            $"owner-issued-{Guid.NewGuid():N}@example.test";
        var before = DateTimeOffset.UtcNow;

        var response = await owner.Client.PostAsJsonAsync(
            "/api/tenant-invitations",
            new
            {
                email = $"  {recipientEmail.ToUpperInvariant()}  ",
                tenantId = Guid.NewGuid(),
                userId = "forged-user",
                role = "Owner",
                expiresAt = before.AddYears(1),
                code = "chosen-by-client",
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        Assert.Equal(2, json.RootElement.EnumerateObject().Count());
        var code = json.RootElement.GetProperty("code").GetString();
        var expiresAt = json.RootElement.GetProperty("expiresAt")
            .GetDateTimeOffset();
        Assert.NotNull(code);
        Assert.InRange(
            expiresAt,
            before.Add(EfTenantInvitationStore.InvitationLifetime)
                .AddSeconds(-2),
            before.Add(EfTenantInvitationStore.InvitationLifetime)
                .AddSeconds(10));

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<FeatureLabDbContext>();
        var normalizer = scope.ServiceProvider
            .GetRequiredService<ILookupNormalizer>();
        var invitation = await dbContext.TenantInvitations.SingleAsync(
            invitation => invitation.IssuedByUserId == owner.UserId);
        Assert.Equal(tenantId, invitation.TenantId);
        Assert.Equal(
            normalizer.NormalizeEmail(recipientEmail),
            invitation.NormalizedEmail);
        Assert.Equal(Hash(code), invitation.CodeHash);
        Assert.DoesNotContain(code, invitation.CodeHash);
        Assert.Equal(expiresAt, invitation.ExpiresAt);
        Assert.Null(invitation.ClosedAt);
        Assert.Null(invitation.AcceptedAt);
    }

    [Fact]
    public async Task Ordinary_member_cannot_issue_an_invitation()
    {
        var tenantId = Guid.NewGuid();
        using var member = await RegisterMemberAsync(
            tenantId,
            TenantMembershipRole.Member);
        var recipientEmail = $"member-denied-{Guid.NewGuid():N}@example.test";

        var response = await member.Client.PostAsJsonAsync(
            "/api/tenant-invitations",
            new { email = recipientEmail });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(await InvitationExistsAsync(tenantId, recipientEmail));
    }

    [Fact]
    public async Task Owner_role_in_another_workspace_does_not_authorise_issuance()
    {
        var ownerTenantId = Guid.NewGuid();
        using var account = await RegisterMemberAsync(
            ownerTenantId,
            TenantMembershipRole.Owner);
        var selectedMemberTenantId = Guid.NewGuid();
        await TenantTestData.ProvisionAsync(
            factory.Services,
            account.Email,
            selectedMemberTenantId,
            role: TenantMembershipRole.Member);
        using var selectedSession = await SignInAsync(
            account.Email,
            account.Password);
        var recipientEmail =
            $"cross-scope-{Guid.NewGuid():N}@example.test";

        var response = await selectedSession.PostAsJsonAsync(
            "/api/tenant-invitations",
            new { email = recipientEmail });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(await InvitationExistsAsync(
            selectedMemberTenantId,
            recipientEmail));
    }

    [Fact]
    public async Task Anonymous_and_stale_owner_sessions_create_no_invitation()
    {
        var tenantId = Guid.NewGuid();
        using var owner = await RegisterMemberAsync(
            tenantId,
            TenantMembershipRole.Owner);
        var anonymousEmail =
            $"anonymous-denied-{Guid.NewGuid():N}@example.test";
        using var anonymous = factory.CreateClient();
        var anonymousResponse = await anonymous.PostAsJsonAsync(
            "/api/tenant-invitations",
            new { email = anonymousEmail });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider
                .GetRequiredService<FeatureLabDbContext>();
            var user = await dbContext.Users.SingleAsync(
                user => user.Id == owner.UserId);
            user.SecurityStamp = Guid.NewGuid().ToString("N");
            await dbContext.SaveChangesAsync();
        }

        var staleEmail = $"stale-denied-{Guid.NewGuid():N}@example.test";
        var staleResponse = await owner.Client.PostAsJsonAsync(
            "/api/tenant-invitations",
            new { email = staleEmail });
        Assert.Equal(HttpStatusCode.Forbidden, staleResponse.StatusCode);
        Assert.False(await InvitationExistsAsync(tenantId, anonymousEmail));
        Assert.False(await InvitationExistsAsync(tenantId, staleEmail));
    }

    [Fact]
    public async Task Stale_membership_version_and_inactive_owner_create_no_invitation()
    {
        var versionTenantId = Guid.NewGuid();
        using var versionOwner = await RegisterMemberAsync(
            versionTenantId,
            TenantMembershipRole.Owner);
        var inactiveTenantId = Guid.NewGuid();
        using var inactiveOwner = await RegisterMemberAsync(
            inactiveTenantId,
            TenantMembershipRole.Owner);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider
                .GetRequiredService<FeatureLabDbContext>();
            var versionMembership = await dbContext.TenantMemberships
                .SingleAsync(membership =>
                    membership.UserId == versionOwner.UserId
                    && membership.TenantId == versionTenantId);
            versionMembership.Remove(DateTimeOffset.UtcNow);
            versionMembership.Reactivate(TenantMembershipRole.Owner);

            var inactiveMembership = await dbContext.TenantMemberships
                .SingleAsync(membership =>
                    membership.UserId == inactiveOwner.UserId
                    && membership.TenantId == inactiveTenantId);
            inactiveMembership.Remove(DateTimeOffset.UtcNow);
            await dbContext.SaveChangesAsync();
        }

        var versionEmail =
            $"version-denied-{Guid.NewGuid():N}@example.test";
        var versionResponse = await versionOwner.Client.PostAsJsonAsync(
            "/api/tenant-invitations",
            new { email = versionEmail });
        var inactiveEmail =
            $"inactive-denied-{Guid.NewGuid():N}@example.test";
        var inactiveResponse = await inactiveOwner.Client.PostAsJsonAsync(
            "/api/tenant-invitations",
            new { email = inactiveEmail });

        Assert.Equal(HttpStatusCode.Forbidden, versionResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, inactiveResponse.StatusCode);
        Assert.False(await InvitationExistsAsync(versionTenantId, versionEmail));
        Assert.False(await InvitationExistsAsync(inactiveTenantId, inactiveEmail));
    }

    [Fact]
    public async Task Invalid_recipient_addresses_create_no_invitation()
    {
        var tenantId = Guid.NewGuid();
        using var owner = await RegisterMemberAsync(
            tenantId,
            TenantMembershipRole.Owner);
        var invalidAddresses = new[]
        {
            string.Empty,
            "   ",
            "not-an-email",
            $"Display Name <member-{Guid.NewGuid():N}@example.test>",
            $"{new string('a', 250)}@example.test",
        };

        foreach (var email in invalidAddresses)
        {
            var response = await owner.Client.PostAsJsonAsync(
                "/api/tenant-invitations",
                new { email });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<FeatureLabDbContext>();
        Assert.DoesNotContain(
            dbContext.TenantInvitations,
            invitation => invitation.TenantId == tenantId);
    }

    [Fact]
    public async Task Active_member_cannot_receive_a_dormant_rejoin_code()
    {
        var tenantId = Guid.NewGuid();
        using var owner = await RegisterMemberAsync(
            tenantId,
            TenantMembershipRole.Owner);
        using var member = await RegisterMemberAsync(
            tenantId,
            TenantMembershipRole.Member);

        var response = await owner.Client.PostAsJsonAsync(
            "/api/tenant-invitations",
            new { email = member.Email });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.False(await InvitationExistsAsync(tenantId, member.Email));
    }

    [Fact]
    public async Task Normalized_duplicate_issuance_leaves_one_pending_code()
    {
        var tenantId = Guid.NewGuid();
        using var owner = await RegisterMemberAsync(
            tenantId,
            TenantMembershipRole.Owner);
        var email = $"one-pending-{Guid.NewGuid():N}@example.test";

        var first = await owner.Client.PostAsJsonAsync(
            "/api/tenant-invitations",
            new { email });
        var duplicate = await owner.Client.PostAsJsonAsync(
            "/api/tenant-invitations",
            new { email = $"  {email.ToUpperInvariant()}  " });

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(1, await PendingInvitationCountAsync(tenantId, email));
    }

    [Fact]
    public async Task Concurrent_duplicate_issuance_has_one_winner()
    {
        var tenantId = Guid.NewGuid();
        using var owner = await RegisterMemberAsync(
            tenantId,
            TenantMembershipRole.Owner);
        using var secondSession = await SignInAsync(
            owner.Email,
            owner.Password);
        var email = $"concurrent-{Guid.NewGuid():N}@example.test";

        var attempts = await Task.WhenAll(
            owner.Client.PostAsJsonAsync(
                "/api/tenant-invitations",
                new { email }),
            secondSession.PostAsJsonAsync(
                "/api/tenant-invitations",
                new { email = email.ToUpperInvariant() }));

        Assert.Single(
            attempts,
            response => response.StatusCode == HttpStatusCode.Created);
        Assert.Single(
            attempts,
            response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(1, await PendingInvitationCountAsync(tenantId, email));
    }

    [Fact]
    public async Task Expired_pending_code_is_closed_before_reissue()
    {
        var tenantId = Guid.NewGuid();
        using var owner = await RegisterMemberAsync(
            tenantId,
            TenantMembershipRole.Owner);
        using var recipient = await RegisterUnscopedAsync();
        var oldCode = Convert.ToHexString(
            RandomNumberGenerator.GetBytes(24));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider
                .GetRequiredService<FeatureLabDbContext>();
            var normalizer = scope.ServiceProvider
                .GetRequiredService<ILookupNormalizer>();
            dbContext.TenantInvitations.Add(
                TenantInvitation.Create(
                    tenantId,
                    Assert.IsType<string>(
                        normalizer.NormalizeEmail(recipient.Email)),
                    Hash(oldCode),
                    DateTimeOffset.UtcNow.AddMinutes(-1),
                    owner.UserId));
            await dbContext.SaveChangesAsync();
        }

        var reissue = await owner.Client.PostAsJsonAsync(
            "/api/tenant-invitations",
            new { email = recipient.Email });
        var oldAcceptance = await recipient.Client.PostAsJsonAsync(
            "/api/tenant-invitations/accept",
            new { code = oldCode });

        Assert.Equal(HttpStatusCode.Created, reissue.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, oldAcceptance.StatusCode);
        await using var verificationScope = factory.Services.CreateAsyncScope();
        var verificationDbContext = verificationScope.ServiceProvider
            .GetRequiredService<FeatureLabDbContext>();
        var invitations = await verificationDbContext.TenantInvitations
            .Where(invitation => invitation.TenantId == tenantId)
            .ToArrayAsync();
        Assert.Equal(2, invitations.Length);
        Assert.Single(invitations, invitation => invitation.ClosedAt is not null);
        Assert.Single(invitations, invitation => invitation.ClosedAt is null);
    }

    [Fact]
    public async Task Invited_recipient_joins_as_member_and_cannot_reinvite()
    {
        var tenantId = Guid.NewGuid();
        using var owner = await RegisterMemberAsync(
            tenantId,
            TenantMembershipRole.Owner);
        using var recipient = await RegisterUnscopedAsync();
        var issue = await owner.Client.PostAsJsonAsync(
            "/api/tenant-invitations",
            new { email = recipient.Email });
        var issued = await issue.Content
            .ReadFromJsonAsync<IssueInvitationResponse>();
        Assert.NotNull(issued);

        var acceptance = await recipient.Client.PostAsJsonAsync(
            "/api/tenant-invitations/accept",
            new { code = issued.Code });
        Assert.Equal(HttpStatusCode.NoContent, acceptance.StatusCode);

        using var freshRecipient = await SignInAsync(
            recipient.Email,
            recipient.Password);
        var onward = await freshRecipient.PostAsJsonAsync(
            "/api/tenant-invitations",
            new
            {
                email = $"onward-denied-{Guid.NewGuid():N}@example.test",
            });

        Assert.Equal(HttpStatusCode.Forbidden, onward.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<FeatureLabDbContext>();
        var membership = await dbContext.TenantMemberships.SingleAsync(
            membership => membership.UserId == recipient.UserId
                && membership.TenantId == tenantId);
        Assert.Equal(TenantMembershipRole.Member, membership.Role);
    }

    [Fact]
    public async Task Removed_former_owner_rejoins_only_as_member()
    {
        var tenantId = Guid.NewGuid();
        using var issuer = await RegisterMemberAsync(
            tenantId,
            TenantMembershipRole.Owner);
        using var formerOwner = await RegisterMemberAsync(
            tenantId,
            TenantMembershipRole.Owner);
        var removal = await formerOwner.Client.DeleteAsync(
            "/api/tenant-membership");
        Assert.Equal(HttpStatusCode.NoContent, removal.StatusCode);

        var issue = await issuer.Client.PostAsJsonAsync(
            "/api/tenant-invitations",
            new { email = formerOwner.Email });
        var issued = await issue.Content
            .ReadFromJsonAsync<IssueInvitationResponse>();
        Assert.NotNull(issued);
        using var unscopedSession = await SignInAsync(
            formerOwner.Email,
            formerOwner.Password);
        var acceptance = await unscopedSession.PostAsJsonAsync(
            "/api/tenant-invitations/accept",
            new { code = issued.Code });
        Assert.Equal(HttpStatusCode.NoContent, acceptance.StatusCode);

        using var freshSession = await SignInAsync(
            formerOwner.Email,
            formerOwner.Password);
        var onward = await freshSession.PostAsJsonAsync(
            "/api/tenant-invitations",
            new
            {
                email = $"former-owner-denied-{Guid.NewGuid():N}@example.test",
            });

        Assert.Equal(HttpStatusCode.Forbidden, onward.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<FeatureLabDbContext>();
        var membership = await dbContext.TenantMemberships.SingleAsync(
            membership => membership.UserId == formerOwner.UserId
                && membership.TenantId == tenantId);
        Assert.True(membership.IsActive);
        Assert.Equal(TenantMembershipRole.Member, membership.Role);
        Assert.Equal(3, membership.Version);
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

    private async Task<RegisteredMember> RegisterUnscopedAsync()
    {
        var account = await RegisterAccountAsync();
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
        var email = $"issuance-member-{Guid.NewGuid():N}@example.test";
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

    private async Task<bool> InvitationExistsAsync(
        Guid tenantId,
        string email)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<FeatureLabDbContext>();
        var normalizer = scope.ServiceProvider
            .GetRequiredService<ILookupNormalizer>();
        var normalizedEmail = normalizer.NormalizeEmail(email.Trim());
        return await dbContext.TenantInvitations.AnyAsync(
            invitation => invitation.TenantId == tenantId
                && invitation.NormalizedEmail == normalizedEmail);
    }

    private async Task<int> PendingInvitationCountAsync(
        Guid tenantId,
        string email)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<FeatureLabDbContext>();
        var normalizer = scope.ServiceProvider
            .GetRequiredService<ILookupNormalizer>();
        var normalizedEmail = normalizer.NormalizeEmail(email.Trim());
        return await dbContext.TenantInvitations.CountAsync(
            invitation => invitation.TenantId == tenantId
                && invitation.NormalizedEmail == normalizedEmail
                && invitation.ClosedAt == null);
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
        string Code,
        DateTimeOffset ExpiresAt);
}
