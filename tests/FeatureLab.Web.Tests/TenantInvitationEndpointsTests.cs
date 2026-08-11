using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using FeatureLab.Data;
using FeatureLab.Features.WorkItems;
using FeatureLab.Identity;
using FeatureLab.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FeatureLab.Web.Tests;

public sealed class TenantInvitationEndpointsTests(
    FeatureLabWebFactory factory)
    : IClassFixture<FeatureLabWebFactory>
{
    [Fact]
    public async Task Accepting_an_invitation_uses_its_server_owned_tenant()
    {
        var initialTenantId = Guid.NewGuid();
        using var member = await RegisterAsync(initialTenantId);
        var initialTitle = $"Initial workspace {Guid.NewGuid():N}";
        await SeedWorkItemAsync(
            member.UserId,
            initialTenantId,
            initialTitle);
        var targetTenantId = Guid.NewGuid();
        var invitation = await IssueAsync(
            targetTenantId,
            member.Email);
        var beforeAcceptance = await member.Client.GetAsync(
            "/api/work-items");
        Assert.Equal(HttpStatusCode.OK, beforeAcceptance.StatusCode);
        var initialItems = await beforeAcceptance.Content
            .ReadFromJsonAsync<WorkItemResponse[]>();
        Assert.NotNull(initialItems);
        Assert.Contains(
            initialItems,
            item => item.Title == initialTitle);

        var response = await member.Client.PostAsJsonAsync(
            "/api/tenant-invitations/accept",
            new
            {
                code = invitation.Code,
                tenantId = Guid.NewGuid(),
            });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var staleTokenResponse = await member.Client.GetAsync(
            "/api/work-items");
        Assert.Equal(HttpStatusCode.Forbidden, staleTokenResponse.StatusCode);

        using var freshClient = await SignInAsync(
            member.Email,
            member.Password);
        var freshResponse = await freshClient.GetAsync("/api/work-items");
        Assert.Equal(HttpStatusCode.OK, freshResponse.StatusCode);
        var targetItems = await freshResponse.Content
            .ReadFromJsonAsync<WorkItemResponse[]>();
        Assert.NotNull(targetItems);
        Assert.DoesNotContain(
            targetItems,
            item => item.Title == initialTitle);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<FeatureLabDbContext>();
        var user = await dbContext.Users.SingleAsync(
            user => user.Email == member.Email);
        var memberships = await dbContext.TenantMemberships
            .Where(membership => membership.UserId == user.Id)
            .OrderBy(membership => membership.TenantId)
            .ToArrayAsync();
        var membership = Assert.Single(
            memberships,
            membership => membership.TenantId == targetTenantId);
        var storedInvitation = await dbContext.TenantInvitations.SingleAsync(
            stored => stored.TenantId == targetTenantId
                && stored.NormalizedEmail == user.NormalizedEmail);

        Assert.Equal(targetTenantId, user.TenantId);
        Assert.NotEqual(member.SecurityStamp, user.SecurityStamp);
        Assert.NotEqual(member.ConcurrencyStamp, user.ConcurrencyStamp);
        Assert.Equal(targetTenantId, membership.TenantId);
        Assert.True(membership.IsActive);
        Assert.Equal(1, membership.Version);
        Assert.Equal(2, memberships.Length);
        Assert.Contains(
            memberships,
            existing => existing.TenantId == initialTenantId
                && existing.IsActive);
        Assert.Equal(user.Id, storedInvitation.AcceptedByUserId);
        Assert.NotNull(storedInvitation.AcceptedAt);
        Assert.Equal(2, storedInvitation.Version);
        Assert.NotEqual(invitation.Code, storedInvitation.CodeHash);
        Assert.Equal(64, storedInvitation.CodeHash.Length);
    }

    [Fact]
    public async Task Invitation_for_another_email_is_not_consumed()
    {
        using var intendedMember = await RegisterAsync();
        using var otherMember = await RegisterAsync();
        var invitation = await IssueAsync(
            Guid.NewGuid(),
            intendedMember.Email);

        var rejected = await otherMember.Client.PostAsJsonAsync(
            "/api/tenant-invitations/accept",
            new { code = invitation.Code });

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var accepted = await intendedMember.Client.PostAsJsonAsync(
            "/api/tenant-invitations/accept",
            new { code = invitation.Code });
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
    }

    [Fact]
    public async Task Stale_identity_stamp_cannot_accept_another_invitation()
    {
        using var member = await RegisterAsync();
        var firstInvitation = await IssueAsync(
            Guid.NewGuid(),
            member.Email);
        var secondInvitation = await IssueAsync(
            Guid.NewGuid(),
            member.Email);
        var firstAcceptance = await member.Client.PostAsJsonAsync(
            "/api/tenant-invitations/accept",
            new { code = firstInvitation.Code });
        Assert.Equal(HttpStatusCode.NoContent, firstAcceptance.StatusCode);

        var staleAcceptance = await member.Client.PostAsJsonAsync(
            "/api/tenant-invitations/accept",
            new { code = secondInvitation.Code });

        Assert.Equal(HttpStatusCode.BadRequest, staleAcceptance.StatusCode);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider
                .GetRequiredService<FeatureLabDbContext>();
            var storedInvitation = await dbContext.TenantInvitations
                .SingleAsync(invitation =>
                    invitation.TenantId == secondInvitation.TenantId);
            Assert.Null(storedInvitation.AcceptedAt);
            Assert.Null(storedInvitation.AcceptedByUserId);
        }

        using var freshClient = await SignInAsync(
            member.Email,
            member.Password);
        var freshAcceptance = await freshClient.PostAsJsonAsync(
            "/api/tenant-invitations/accept",
            new { code = secondInvitation.Code });
        Assert.Equal(HttpStatusCode.NoContent, freshAcceptance.StatusCode);
    }

    [Fact]
    public async Task Duplicate_normalized_email_is_rejected()
    {
        var email = $"unique-member-{Guid.NewGuid():N}@example.test";
        await using var scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider
            .GetRequiredService<UserManager<FeatureLabUser>>();
        var first = new FeatureLabUser
        {
            UserName = $"first-{Guid.NewGuid():N}",
            Email = email,
        };
        var second = new FeatureLabUser
        {
            UserName = $"second-{Guid.NewGuid():N}",
            Email = email.ToUpperInvariant(),
        };

        var firstResult = await userManager.CreateAsync(
            first,
            "FeatureLab!123");
        var secondResult = await userManager.CreateAsync(
            second,
            "FeatureLab!123");

        Assert.True(firstResult.Succeeded);
        Assert.False(secondResult.Succeeded);
        Assert.Contains(
            secondResult.Errors,
            error => error.Code == "DuplicateEmail");
    }

    [Fact]
    public async Task Concurrent_acceptance_and_replay_have_one_winner()
    {
        using var member = await RegisterAsync();
        var invitation = await IssueAsync(
            Guid.NewGuid(),
            member.Email);
        using var otherSession = await SignInAsync(
            member.Email,
            member.Password);

        var attempts = await Task.WhenAll(
            member.Client.PostAsJsonAsync(
                "/api/tenant-invitations/accept",
                new { code = invitation.Code }),
            otherSession.PostAsJsonAsync(
                "/api/tenant-invitations/accept",
                new { code = invitation.Code }));

        Assert.Single(
            attempts,
            response => response.StatusCode == HttpStatusCode.NoContent);
        Assert.Single(
            attempts,
            response => response.StatusCode == HttpStatusCode.BadRequest);

        var replay = await member.Client.PostAsJsonAsync(
            "/api/tenant-invitations/accept",
            new { code = invitation.Code });

        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<FeatureLabDbContext>();
        Assert.Single(
            dbContext.TenantMemberships,
            membership => membership.UserId == member.UserId);
    }

    [Fact]
    public async Task Concurrent_different_invitations_commit_one_selector()
    {
        using var member = await RegisterAsync();
        var firstInvitation = await IssueAsync(
            Guid.NewGuid(),
            member.Email);
        var secondInvitation = await IssueAsync(
            Guid.NewGuid(),
            member.Email);

        var attempts = await Task.WhenAll(
            member.Client.PostAsJsonAsync(
                "/api/tenant-invitations/accept",
                new { code = firstInvitation.Code }),
            member.Client.PostAsJsonAsync(
                "/api/tenant-invitations/accept",
                new { code = secondInvitation.Code }));

        Assert.Single(
            attempts,
            response => response.StatusCode == HttpStatusCode.NoContent);
        Assert.Single(
            attempts,
            response => response.StatusCode == HttpStatusCode.BadRequest);

        Guid winningTenantId;
        Guid losingTenantId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider
                .GetRequiredService<FeatureLabDbContext>();
            var user = await dbContext.Users.SingleAsync(
                user => user.Id == member.UserId);
            var invitations = await dbContext.TenantInvitations
                .Where(invitation =>
                    invitation.TenantId == firstInvitation.TenantId
                    || invitation.TenantId == secondInvitation.TenantId)
                .ToArrayAsync();
            var consumed = Assert.Single(
                invitations,
                invitation => invitation.AcceptedAt is not null);
            var unconsumed = Assert.Single(
                invitations,
                invitation => invitation.AcceptedAt is null);
            var membership = await dbContext.TenantMemberships.SingleAsync(
                membership => membership.UserId == member.UserId);

            winningTenantId = consumed.TenantId;
            losingTenantId = unconsumed.TenantId;
            Assert.Equal(member.UserId, consumed.AcceptedByUserId);
            Assert.Null(unconsumed.AcceptedByUserId);
            Assert.Equal(winningTenantId, user.TenantId);
            Assert.Equal(winningTenantId, membership.TenantId);
            Assert.True(membership.IsActive);
            Assert.NotEqual(member.SecurityStamp, user.SecurityStamp);
            Assert.NotEqual(member.ConcurrencyStamp, user.ConcurrencyStamp);
        }

        var winningTitle = $"Winning workspace {Guid.NewGuid():N}";
        var losingTitle = $"Losing workspace {Guid.NewGuid():N}";
        await SeedWorkItemAsync(
            member.UserId,
            winningTenantId,
            winningTitle);
        await SeedWorkItemAsync(
            member.UserId,
            losingTenantId,
            losingTitle);
        using var freshClient = await SignInAsync(
            member.Email,
            member.Password);

        var visibleItems = await freshClient
            .GetFromJsonAsync<WorkItemResponse[]>("/api/work-items");

        Assert.NotNull(visibleItems);
        Assert.Contains(
            visibleItems,
            item => item.Title == winningTitle);
        Assert.DoesNotContain(
            visibleItems,
            item => item.Title == losingTitle);
    }

    [Fact]
    public async Task Expired_invitation_does_not_create_a_membership()
    {
        using var member = await RegisterAsync();
        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider
                .GetRequiredService<FeatureLabDbContext>();
            var user = await dbContext.Users.SingleAsync(
                user => user.Email == member.Email);
            dbContext.TenantInvitations.Add(
                TenantInvitation.Create(
                    Guid.NewGuid(),
                    Assert.IsType<string>(user.NormalizedEmail),
                    Hash(code),
                    DateTimeOffset.UtcNow.AddMinutes(-1)));
            await dbContext.SaveChangesAsync();
        }

        var response = await member.Client.PostAsJsonAsync(
            "/api/tenant-invitations/accept",
            new { code });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var verificationScope = factory.Services.CreateAsyncScope();
        var verificationDbContext = verificationScope.ServiceProvider
            .GetRequiredService<FeatureLabDbContext>();
        Assert.DoesNotContain(
            verificationDbContext.TenantMemberships,
            membership => membership.UserId == member.UserId);
    }

    [Fact]
    public async Task Out_of_range_codes_are_rejected_before_lookup()
    {
        using var member = await RegisterAsync();
        var invalidCodes = new[]
        {
            "too-short",
            new string('x', EfTenantInvitationStore.MaximumCodeLength + 1),
        };

        foreach (var code in invalidCodes)
        {
            var response = await member.Client.PostAsJsonAsync(
                "/api/tenant-invitations/accept",
                new { code });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task Anonymous_user_cannot_accept_an_invitation()
    {
        using var member = await RegisterAsync();
        var invitation = await IssueAsync(
            Guid.NewGuid(),
            member.Email);
        using var anonymousClient = factory.CreateClient();

        var response = await anonymousClient.PostAsJsonAsync(
            "/api/tenant-invitations/accept",
            new { code = invitation.Code });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<RegisteredUser> RegisterAsync(
        Guid? initialTenantId = null)
    {
        var client = factory.CreateClient();
        var email = $"invited-member-{Guid.NewGuid():N}@example.test";
        const string password = "FeatureLab!123";
        var registration = await client.PostAsJsonAsync(
            "/account/register",
            new
            {
                email,
                password,
            });
        registration.EnsureSuccessStatusCode();

        if (initialTenantId is { } tenantId)
        {
            await TenantTestData.ProvisionAsync(
                factory.Services,
                email,
                tenantId);
        }

        var authenticatedClient = await SignInAsync(email, password);
        client.Dispose();

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<FeatureLabDbContext>();
        var user = await dbContext.Users
            .Where(user => user.Email == email)
            .Select(user => new
            {
                user.Id,
                user.SecurityStamp,
                user.ConcurrencyStamp,
            })
            .SingleAsync();

        return new RegisteredUser(
            authenticatedClient,
            user.Id,
            email,
            password,
            Assert.IsType<string>(user.SecurityStamp),
            Assert.IsType<string>(user.ConcurrencyStamp));
    }

    private async Task<HttpClient> SignInAsync(
        string email,
        string password)
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync(
            "/account/login",
            new
            {
                email,
                password,
            });
        login.EnsureSuccessStatusCode();
        var tokens = await login.Content
            .ReadFromJsonAsync<LoginTokenResponse>();
        Assert.NotNull(tokens);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    private async Task<IssuedTenantInvitation> IssueAsync(
        Guid tenantId,
        string email)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var invitations = scope.ServiceProvider
            .GetRequiredService<ITenantInvitationStore>();
        return await invitations.IssueAsync(
            tenantId,
            email,
            DateTimeOffset.UtcNow.AddHours(1));
    }

    private async Task SeedWorkItemAsync(
        string userId,
        Guid tenantId,
        string title)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<FeatureLabDbContext>();
        dbContext.WorkItems.Add(
            WorkItem.Create(
                title,
                userId,
                tenantId,
                TimeProvider.System));
        await dbContext.SaveChangesAsync();
    }

    private static string Hash(string code) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(code)));

    private sealed record RegisteredUser(
        HttpClient Client,
        string UserId,
        string Email,
        string Password,
        string SecurityStamp,
        string ConcurrencyStamp) : IDisposable
    {
        public void Dispose() => Client.Dispose();
    }
}
