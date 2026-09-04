using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FeatureLab.Data;
using FeatureLab.Identity;
using FeatureLab.Tenancy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body);
        Assert.Equal(3, json.RootElement.EnumerateObject().Count());
        var invitationId = json.RootElement.GetProperty("id").GetGuid();
        var expiresAt = json.RootElement.GetProperty("expiresAt")
            .GetDateTimeOffset();
        Assert.Equal(
            "queued",
            json.RootElement.GetProperty("deliveryStatus").GetString());
        var delivered = await TakeDeliveryAsync(invitationId);
        var code = delivered.Code;
        Assert.DoesNotContain(code, body, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "code",
            body,
            StringComparison.OrdinalIgnoreCase);
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
        Assert.Equal(invitationId, invitation.Id);
        Assert.Equal(tenantId, invitation.TenantId);
        Assert.Equal(
            normalizer.NormalizeEmail(recipientEmail),
            invitation.NormalizedEmail);
        Assert.Equal(invitation.NormalizedEmail, delivered.RecipientEmail);
        Assert.Equal(Hash(code), invitation.CodeHash);
        Assert.DoesNotContain(code, invitation.CodeHash);
        Assert.Equal(expiresAt, invitation.ExpiresAt);
        Assert.Null(invitation.ClosedAt);
        Assert.Null(invitation.AcceptedAt);
    }

    [Fact]
    public async Task Recorder_capacity_failure_retains_queue_until_adapter_recovers()
    {
        using var capacityFactory = factory.WithWebHostBuilder(_ =>
        {
        });
        var tenantId = Guid.NewGuid();
        using var owner = await RegisterMemberAsync(
            capacityFactory,
            tenantId,
            TenantMembershipRole.Owner);
        var recipientEmail =
            $"capacity-failure-{Guid.NewGuid():N}@example.test";
        var recorder = capacityFactory.Services
            .GetRequiredService<RecordingTenantInvitationDelivery>();
        var fillerIds = new List<Guid>();
        while (recorder.Count < RecordingTenantInvitationDelivery.Capacity)
        {
            var fillerId = Guid.NewGuid();
            await recorder.DeliverAsync(
                fillerId,
                $"FILLER-{Guid.NewGuid():N}@EXAMPLE.TEST",
                $"filler-secret-{fillerId:N}",
                DateTimeOffset.UtcNow.AddHours(1),
                default);
            fillerIds.Add(fillerId);
        }

        var queuedInvitationId = Guid.Empty;
        try
        {
            var response = await owner.Client.PostAsJsonAsync(
                "/api/tenant-invitations",
                new { email = recipientEmail });

            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            using var queued = JsonDocument.Parse(body);
            queuedInvitationId = queued.RootElement
                .GetProperty("id")
                .GetGuid();
            Assert.Equal(
                "queued",
                queued.RootElement.GetProperty("deliveryStatus")
                    .GetString());
            Assert.DoesNotContain(
                recipientEmail,
                body,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "code",
                body,
                StringComparison.OrdinalIgnoreCase);

            await using var scope = capacityFactory.Services
                .CreateAsyncScope();
            var dbContext = scope.ServiceProvider
                .GetRequiredService<FeatureLabDbContext>();
            var normalizer = scope.ServiceProvider
                .GetRequiredService<ILookupNormalizer>();
            var normalizedEmail = normalizer.NormalizeEmail(recipientEmail);
            var invitation = await dbContext.TenantInvitations
                .SingleAsync(candidate =>
                    candidate.TenantId == tenantId
                    && candidate.NormalizedEmail == normalizedEmail);
            Assert.Null(invitation.ClosedAt);
            Assert.Equal(1, invitation.Version);
            Assert.True(await dbContext.TenantInvitationOutboxMessages
                .AnyAsync(message =>
                    message.InvitationId == invitation.Id));
            Assert.False(recorder.TryTake(invitation.Id, out _));
        }
        finally
        {
            foreach (var fillerId in fillerIds)
            {
                recorder.TryTake(fillerId, out _);
            }
        }

        var dispatcher = capacityFactory.Services
            .GetRequiredService<TenantInvitationOutboxDispatcher>();
        await dispatcher.ProcessBatchAsync();
        _ = await TakeDeliveryAsync(
            capacityFactory,
            queuedInvitationId);
    }

    [Fact]
    public async Task Concurrent_close_during_delivery_fail_closes_queue_safely()
    {
        var logs = new CapturingLoggerProvider();
        using var unknownFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureLogging(logging => logging.AddProvider(logs));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ITenantInvitationDelivery>();
                services.AddSingleton<CloseThenFailDelivery>();
                services.AddSingleton<ITenantInvitationDelivery>(provider =>
                    provider.GetRequiredService<CloseThenFailDelivery>());
            });
        });
        var tenantId = Guid.NewGuid();
        using var owner = await RegisterMemberAsync(
            unknownFactory,
            tenantId,
            TenantMembershipRole.Owner);
        var recipientEmail =
            $"unknown-delivery-{Guid.NewGuid():N}@example.test";

        var response = await owner.Client.PostAsJsonAsync(
            "/api/tenant-invitations",
            new { email = recipientEmail });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var queued = JsonDocument.Parse(body);
        Assert.Equal(
            "queued",
            queued.RootElement.GetProperty("deliveryStatus").GetString());
        var failingDelivery = unknownFactory.Services
            .GetRequiredService<CloseThenFailDelivery>();
        var dispatcher = unknownFactory.Services
            .GetRequiredService<TenantInvitationOutboxDispatcher>();
        await dispatcher.ProcessBatchAsync();
        Assert.NotNull(failingDelivery.Code);
        Assert.DoesNotContain(
            failingDelivery.Code,
            body,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            logs.Messages,
            message => message.Contains(
                failingDelivery.Code,
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            logs.Messages,
            message => message.Contains(
                recipientEmail,
                StringComparison.OrdinalIgnoreCase));

        await dispatcher.ProcessBatchAsync();
        await using var scope = unknownFactory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<FeatureLabDbContext>();
        var invitation = await dbContext.TenantInvitations.SingleAsync(
            candidate => candidate.Id == failingDelivery.InvitationId);
        Assert.NotNull(invitation.ClosedAt);
        Assert.Equal(2, invitation.Version);
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

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
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
            response => response.StatusCode == HttpStatusCode.Accepted);
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

        Assert.Equal(HttpStatusCode.Accepted, reissue.StatusCode);
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
        var delivered = await TakeDeliveryAsync(issued.Id);

        var acceptance = await recipient.Client.PostAsJsonAsync(
            "/api/tenant-invitations/accept",
            new { code = delivered.Code });
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
        var delivered = await TakeDeliveryAsync(issued.Id);
        using var unscopedSession = await SignInAsync(
            formerOwner.Email,
            formerOwner.Password);
        var acceptance = await unscopedSession.PostAsJsonAsync(
            "/api/tenant-invitations/accept",
            new { code = delivered.Code });
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

    [Fact]
    public async Task Owner_cancels_pending_invitation_without_sending_its_code()
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
        var delivered = await TakeDeliveryAsync(issued.Id);

        var cancellation = await owner.Client.DeleteAsync(
            $"/api/tenant-invitations/{issued.Id}");
        var rejectedAcceptance = await recipient.Client.PostAsJsonAsync(
            "/api/tenant-invitations/accept",
            new { code = delivered.Code });

        Assert.Equal(HttpStatusCode.NoContent, cancellation.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, rejectedAcceptance.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<FeatureLabDbContext>();
        var invitation = await dbContext.TenantInvitations.SingleAsync(
            invitation => invitation.Id == issued.Id);
        var recipientUser = await dbContext.Users.SingleAsync(
            user => user.Id == recipient.UserId);
        Assert.NotNull(invitation.ClosedAt);
        Assert.Null(invitation.AcceptedAt);
        Assert.Null(invitation.AcceptedByUserId);
        Assert.Equal(2, invitation.Version);
        Assert.Equal(owner.UserId, invitation.IssuedByUserId);
        Assert.Equal(Hash(delivered.Code), invitation.CodeHash);
        Assert.Equal(Guid.Empty, recipientUser.TenantId);
        Assert.DoesNotContain(
            dbContext.TenantMemberships,
            membership => membership.UserId == recipient.UserId
                && membership.TenantId == tenantId);
    }

    [Fact]
    public async Task Repeated_cancellation_is_an_idempotent_no_op()
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

        var first = await owner.Client.DeleteAsync(
            $"/api/tenant-invitations/{issued.Id}");
        var repeated = await owner.Client.DeleteAsync(
            $"/api/tenant-invitations/{issued.Id}");

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, repeated.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<FeatureLabDbContext>();
        var invitation = await dbContext.TenantInvitations.SingleAsync(
            invitation => invitation.Id == issued.Id);
        Assert.Equal(2, invitation.Version);
    }

    [Fact]
    public async Task Member_and_anonymous_callers_cannot_cancel_an_invitation()
    {
        var tenantId = Guid.NewGuid();
        using var owner = await RegisterMemberAsync(
            tenantId,
            TenantMembershipRole.Owner);
        using var member = await RegisterMemberAsync(
            tenantId,
            TenantMembershipRole.Member);
        using var recipient = await RegisterUnscopedAsync();
        var issue = await owner.Client.PostAsJsonAsync(
            "/api/tenant-invitations",
            new { email = recipient.Email });
        var issued = await issue.Content
            .ReadFromJsonAsync<IssueInvitationResponse>();
        Assert.NotNull(issued);
        using var anonymous = factory.CreateClient();

        var memberAttempt = await member.Client.DeleteAsync(
            $"/api/tenant-invitations/{issued.Id}");
        var anonymousAttempt = await anonymous.DeleteAsync(
            $"/api/tenant-invitations/{issued.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, memberAttempt.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousAttempt.StatusCode);
        Assert.Equal(1, await PendingInvitationCountAsync(
            tenantId,
            recipient.Email));
    }

    [Fact]
    public async Task Another_workspace_owner_gets_the_same_no_op_as_a_missing_id()
    {
        var sourceTenantId = Guid.NewGuid();
        using var sourceOwner = await RegisterMemberAsync(
            sourceTenantId,
            TenantMembershipRole.Owner);
        var otherTenantId = Guid.NewGuid();
        using var otherOwner = await RegisterMemberAsync(
            otherTenantId,
            TenantMembershipRole.Owner);
        using var recipient = await RegisterUnscopedAsync();
        var issue = await sourceOwner.Client.PostAsJsonAsync(
            "/api/tenant-invitations",
            new { email = recipient.Email });
        var issued = await issue.Content
            .ReadFromJsonAsync<IssueInvitationResponse>();
        Assert.NotNull(issued);

        var crossTenant = await otherOwner.Client.DeleteAsync(
            $"/api/tenant-invitations/{issued.Id}");
        var missing = await otherOwner.Client.DeleteAsync(
            $"/api/tenant-invitations/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NoContent, crossTenant.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, missing.StatusCode);
        Assert.Equal(1, await PendingInvitationCountAsync(
            sourceTenantId,
            recipient.Email));
    }

    [Fact]
    public async Task Accepted_and_missing_invitations_are_indistinguishable_to_cancel()
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
        var delivered = await TakeDeliveryAsync(issued.Id);
        var acceptance = await recipient.Client.PostAsJsonAsync(
            "/api/tenant-invitations/accept",
            new { code = delivered.Code });
        Assert.Equal(HttpStatusCode.NoContent, acceptance.StatusCode);

        var accepted = await owner.Client.DeleteAsync(
            $"/api/tenant-invitations/{issued.Id}");
        var missing = await owner.Client.DeleteAsync(
            $"/api/tenant-invitations/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, missing.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<FeatureLabDbContext>();
        var invitation = await dbContext.TenantInvitations.SingleAsync(
            invitation => invitation.Id == issued.Id);
        var membership = await dbContext.TenantMemberships.SingleAsync(
            membership => membership.UserId == recipient.UserId
                && membership.TenantId == tenantId);
        var user = await dbContext.Users.SingleAsync(
            user => user.Id == recipient.UserId);
        Assert.NotNull(invitation.AcceptedAt);
        Assert.Equal(recipient.UserId, invitation.AcceptedByUserId);
        Assert.Equal(2, invitation.Version);
        Assert.True(membership.IsActive);
        Assert.Equal(tenantId, user.TenantId);
    }

    [Fact]
    public async Task Stale_owner_session_cannot_cancel_a_pending_invitation()
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
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider
                .GetRequiredService<FeatureLabDbContext>();
            var membership = await dbContext.TenantMemberships.SingleAsync(
                membership => membership.UserId == owner.UserId
                    && membership.TenantId == tenantId);
            membership.Remove(DateTimeOffset.UtcNow);
            membership.Reactivate(TenantMembershipRole.Owner);
            await dbContext.SaveChangesAsync();
        }

        var response = await owner.Client.DeleteAsync(
            $"/api/tenant-invitations/{issued.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(1, await PendingInvitationCountAsync(
            tenantId,
            recipient.Email));
    }

    [Fact]
    public async Task Stale_identity_stamp_cannot_cancel_a_pending_invitation()
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
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider
                .GetRequiredService<FeatureLabDbContext>();
            var user = await dbContext.Users.SingleAsync(
                user => user.Id == owner.UserId);
            user.SecurityStamp = Guid.NewGuid().ToString("N");
            await dbContext.SaveChangesAsync();
        }

        var response = await owner.Client.DeleteAsync(
            $"/api/tenant-invitations/{issued.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(1, await PendingInvitationCountAsync(
            tenantId,
            recipient.Email));
    }

    [Fact]
    public async Task Another_current_owner_can_cancel_without_being_the_issuer()
    {
        var tenantId = Guid.NewGuid();
        using var issuer = await RegisterMemberAsync(
            tenantId,
            TenantMembershipRole.Owner);
        using var otherOwner = await RegisterMemberAsync(
            tenantId,
            TenantMembershipRole.Owner);
        using var recipient = await RegisterUnscopedAsync();
        var issue = await issuer.Client.PostAsJsonAsync(
            "/api/tenant-invitations",
            new { email = recipient.Email });
        var issued = await issue.Content
            .ReadFromJsonAsync<IssueInvitationResponse>();
        Assert.NotNull(issued);

        var response = await otherOwner.Client.DeleteAsync(
            $"/api/tenant-invitations/{issued.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<FeatureLabDbContext>();
        var invitation = await dbContext.TenantInvitations.SingleAsync(
            invitation => invitation.Id == issued.Id);
        Assert.NotNull(invitation.ClosedAt);
        Assert.Equal(issuer.UserId, invitation.IssuedByUserId);
        Assert.NotEqual(otherOwner.UserId, invitation.IssuedByUserId);
    }

    [Fact]
    public async Task Cancellation_releases_the_recipient_slot_for_a_new_code()
    {
        var tenantId = Guid.NewGuid();
        using var owner = await RegisterMemberAsync(
            tenantId,
            TenantMembershipRole.Owner);
        using var recipient = await RegisterUnscopedAsync();
        var firstIssue = await owner.Client.PostAsJsonAsync(
            "/api/tenant-invitations",
            new { email = recipient.Email });
        var first = await firstIssue.Content
            .ReadFromJsonAsync<IssueInvitationResponse>();
        Assert.NotNull(first);
        var firstDelivery = await TakeDeliveryAsync(first.Id);
        var cancellation = await owner.Client.DeleteAsync(
            $"/api/tenant-invitations/{first.Id}");
        Assert.Equal(HttpStatusCode.NoContent, cancellation.StatusCode);

        var secondIssue = await owner.Client.PostAsJsonAsync(
            "/api/tenant-invitations",
            new { email = recipient.Email });
        var second = await secondIssue.Content
            .ReadFromJsonAsync<IssueInvitationResponse>();

        Assert.Equal(HttpStatusCode.Accepted, secondIssue.StatusCode);
        Assert.NotNull(second);
        var secondDelivery = await TakeDeliveryAsync(second.Id);
        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(firstDelivery.Code, secondDelivery.Code);
        Assert.Equal(1, await PendingInvitationCountAsync(
            tenantId,
            recipient.Email));
    }

    [Fact]
    public async Task Concurrent_acceptance_and_cancellation_leave_one_terminal_state()
    {
        var tenantId = Guid.NewGuid();
        using var owner = await RegisterMemberAsync(
            tenantId,
            TenantMembershipRole.Owner);
        using var recipient = await RegisterUnscopedAsync();
        using var secondRecipientSession = await SignInAsync(
            recipient.Email,
            recipient.Password);
        var issue = await owner.Client.PostAsJsonAsync(
            "/api/tenant-invitations",
            new { email = recipient.Email });
        var issued = await issue.Content
            .ReadFromJsonAsync<IssueInvitationResponse>();
        Assert.NotNull(issued);
        var delivered = await TakeDeliveryAsync(issued.Id);

        var attempts = await Task.WhenAll(
            owner.Client.DeleteAsync(
                $"/api/tenant-invitations/{issued.Id}"),
            secondRecipientSession.PostAsJsonAsync(
                "/api/tenant-invitations/accept",
                new { code = delivered.Code }));

        Assert.Contains(
            attempts[0].StatusCode,
            new[] { HttpStatusCode.NoContent, HttpStatusCode.Conflict });
        Assert.Contains(
            attempts[1].StatusCode,
            new[] { HttpStatusCode.NoContent, HttpStatusCode.BadRequest });
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<FeatureLabDbContext>();
        var invitation = await dbContext.TenantInvitations.SingleAsync(
            invitation => invitation.Id == issued.Id);
        var memberships = await dbContext.TenantMemberships
            .Where(membership => membership.UserId == recipient.UserId
                && membership.TenantId == tenantId)
            .ToArrayAsync();
        Assert.NotNull(invitation.ClosedAt);
        Assert.Equal(2, invitation.Version);
        if (invitation.AcceptedAt is null)
        {
            Assert.Equal(HttpStatusCode.NoContent, attempts[0].StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, attempts[1].StatusCode);
            Assert.Empty(memberships);
        }
        else
        {
            Assert.Equal(HttpStatusCode.NoContent, attempts[1].StatusCode);
            Assert.Equal(recipient.UserId, invitation.AcceptedByUserId);
            Assert.Single(memberships);
        }
    }

    [Fact]
    public async Task Stale_terminal_write_is_rejected_by_invitation_version()
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
        await using var cancelScope = factory.Services.CreateAsyncScope();
        await using var acceptScope = factory.Services.CreateAsyncScope();
        var cancelDbContext = cancelScope.ServiceProvider
            .GetRequiredService<FeatureLabDbContext>();
        var acceptDbContext = acceptScope.ServiceProvider
            .GetRequiredService<FeatureLabDbContext>();
        var cancelCopy = await cancelDbContext.TenantInvitations.SingleAsync(
            invitation => invitation.Id == issued.Id);
        var acceptCopy = await acceptDbContext.TenantInvitations.SingleAsync(
            invitation => invitation.Id == issued.Id);
        var now = DateTimeOffset.UtcNow;

        cancelCopy.Close(now);
        await cancelDbContext.SaveChangesAsync();
        acceptCopy.Accept(recipient.UserId, now.AddMilliseconds(1));

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => acceptDbContext.SaveChangesAsync());
        await using var verificationScope =
            factory.Services.CreateAsyncScope();
        var verificationDbContext = verificationScope.ServiceProvider
            .GetRequiredService<FeatureLabDbContext>();
        var stored = await verificationDbContext.TenantInvitations
            .SingleAsync(invitation => invitation.Id == issued.Id);
        Assert.NotNull(stored.ClosedAt);
        Assert.Null(stored.AcceptedAt);
        Assert.Null(stored.AcceptedByUserId);
        Assert.Equal(2, stored.Version);
    }

    private async Task<RegisteredMember> RegisterMemberAsync(
        Guid tenantId,
        TenantMembershipRole role) =>
        await RegisterMemberAsync(factory, tenantId, role);

    private async Task<RegisteredMember> RegisterMemberAsync(
        WebApplicationFactory<Program> host,
        Guid tenantId,
        TenantMembershipRole role)
    {
        var account = await RegisterAccountAsync(host);
        await TenantTestData.ProvisionAsync(
            host.Services,
            account.Email,
            tenantId,
            role: role);
        var client = await SignInAsync(
            host,
            account.Email,
            account.Password);
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

    private Task<RegisteredAccount> RegisterAccountAsync() =>
        RegisterAccountAsync(factory);

    private static async Task<RegisteredAccount> RegisterAccountAsync(
        WebApplicationFactory<Program> host)
    {
        using var client = host.CreateClient();
        var email = $"issuance-member-{Guid.NewGuid():N}@example.test";
        const string password = "FeatureLab!123";
        var registration = await client.PostAsJsonAsync(
            "/account/register",
            new { email, password });
        registration.EnsureSuccessStatusCode();

        await using var scope = host.Services.CreateAsyncScope();
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
        string password) =>
        await SignInAsync(factory, email, password);

    private static async Task<HttpClient> SignInAsync(
        WebApplicationFactory<Program> host,
        string email,
        string password)
    {
        var client = host.CreateClient();
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

    private Task<RecordedTenantInvitationDelivery> TakeDeliveryAsync(
        Guid invitationId) =>
        TakeDeliveryAsync(factory, invitationId);

    private static async Task<RecordedTenantInvitationDelivery>
        TakeDeliveryAsync(
            WebApplicationFactory<Program> host,
            Guid invitationId)
    {
        var recorder = host.Services
            .GetRequiredService<RecordingTenantInvitationDelivery>();
        var dispatcher = host.Services
            .GetRequiredService<TenantInvitationOutboxDispatcher>();
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await dispatcher.ProcessBatchAsync();
            if (recorder.TryTake(invitationId, out var delivery))
            {
                return delivery;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        throw new InvalidOperationException(
            $"Invitation {invitationId} was not delivered in time.");
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
        DateTimeOffset ExpiresAt,
        string DeliveryStatus);

    private sealed class CloseThenFailDelivery(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider) : ITenantInvitationDelivery
    {
        public Guid InvitationId { get; private set; }

        public string? Code { get; private set; }

        public async Task DeliverAsync(
            Guid invitationId,
            string recipientEmail,
            string code,
            DateTimeOffset expiresAt,
            CancellationToken cancellationToken)
        {
            InvitationId = invitationId;
            Code = code;
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider
                .GetRequiredService<FeatureLabDbContext>();
            var invitation = await dbContext.TenantInvitations.SingleAsync(
                candidate => candidate.Id == invitationId,
                CancellationToken.None);
            invitation.Close(timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(CancellationToken.None);

            throw new InvalidOperationException(
                $"Provider failure included {recipientEmail} and {code}.");
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyCollection<string> Messages => _messages.ToArray();

        public ILogger CreateLogger(string categoryName) =>
            new CapturingLogger(_messages);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(
            ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull =>
                null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                messages.Enqueue(formatter(state, exception));
                if (exception is not null)
                {
                    messages.Enqueue(exception.ToString());
                }
            }
        }
    }
}
