using System.Data;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using FeatureLab.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FeatureLab.Tenancy;

public sealed class IssuedTenantInvitation
{
    public IssuedTenantInvitation(
        Guid id,
        string recipientEmail,
        string code,
        Guid tenantId,
        DateTimeOffset expiresAt)
    {
        if (id == Guid.Empty
            || tenantId == Guid.Empty
            || string.IsNullOrWhiteSpace(recipientEmail)
            || string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException(
                "A complete issued invitation is required.");
        }

        Id = id;
        RecipientEmail = recipientEmail;
        Code = code;
        TenantId = tenantId;
        ExpiresAt = expiresAt;
    }

    public Guid Id { get; }

    public string RecipientEmail { get; }

    [JsonIgnore]
    public string Code { get; }

    public Guid TenantId { get; }

    public DateTimeOffset ExpiresAt { get; }

    public override string ToString() =>
        $"{nameof(IssuedTenantInvitation)} {{ Id = {Id}, "
        + "RecipientEmail = [REDACTED], Code = [REDACTED], "
        + $"TenantId = {TenantId}, ExpiresAt = {ExpiresAt:O} }}";
}

public sealed record PendingTenantInvitation(
    Guid Id,
    string Email,
    DateTimeOffset ExpiresAt);

public enum IssueTenantInvitationStatus
{
    Queued,
    InvalidRecipient,
    ActiveMember,
    StaleOwner,
    Conflict,
}

public sealed class IssueTenantInvitationResult
{
    private IssueTenantInvitationResult(
        IssueTenantInvitationStatus status,
        Guid? id = null,
        DateTimeOffset? expiresAt = null)
    {
        Status = status;
        Id = id;
        ExpiresAt = expiresAt;
    }

    public IssueTenantInvitationStatus Status { get; }

    public Guid? Id { get; }

    public DateTimeOffset? ExpiresAt { get; }

    public static IssueTenantInvitationResult Queued(
        Guid id,
        DateTimeOffset expiresAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException(
                "A queued invitation identifier is required.",
                nameof(id));
        }

        return new(
            IssueTenantInvitationStatus.Queued,
            id,
            expiresAt);
    }

    public static IssueTenantInvitationResult FromStatus(
        IssueTenantInvitationStatus status)
    {
        if (status == IssueTenantInvitationStatus.Queued)
        {
            throw new ArgumentException(
                "A queued result requires invitation metadata.",
                nameof(status));
        }

        return new(status);
    }

    public override string ToString() =>
        $"{nameof(IssueTenantInvitationResult)} {{ Status = {Status}, "
        + $"Id = {Id}, ExpiresAt = {ExpiresAt:O} }}";
}

public enum CancelTenantInvitationStatus
{
    Canceled,
    Unavailable,
    StaleOwner,
    Conflict,
}

public sealed record CancelTenantInvitationResult(
    CancelTenantInvitationStatus Status);

public interface ITenantInvitationStore
{
    Task<IssuedTenantInvitation> IssueAsync(
        Guid tenantId,
        string email,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default);

    Task<IssueTenantInvitationResult> IssueForOwnerAsync(
        string userId,
        string securityStamp,
        long membershipVersion,
        Guid tenantId,
        string email,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PendingTenantInvitation>?> ListPendingForOwnerAsync(
        string userId,
        string securityStamp,
        long membershipVersion,
        Guid tenantId,
        CancellationToken cancellationToken = default);

    Task<CancelTenantInvitationResult> CancelForOwnerAsync(
        string userId,
        string securityStamp,
        long membershipVersion,
        Guid tenantId,
        Guid invitationId,
        CancellationToken cancellationToken = default);

    Task<bool> AcceptAsync(
        string userId,
        string securityStamp,
        string code,
        CancellationToken cancellationToken = default);
}

public sealed class EfTenantInvitationStore(
    FeatureLabDbContext dbContext,
    ILookupNormalizer normalizer,
    TimeProvider timeProvider,
    ITenantInvitationOutboxProtector outboxProtector)
    : ITenantInvitationStore
{
    public static readonly TimeSpan InvitationLifetime = TimeSpan.FromHours(24);

    public const int MinimumCodeLength = 20;

    public const int MaximumCodeLength = 256;

    public async Task<IssuedTenantInvitation> IssueAsync(
        Guid tenantId,
        string email,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException(
                "A non-empty tenant identifier is required.",
                nameof(tenantId));
        }

        var normalizedEmail = NormalizeEmail(email);
        if (normalizedEmail is null)
        {
            throw new ArgumentException(
                "An email address is required.",
                nameof(email));
        }

        if (expiresAt <= timeProvider.GetUtcNow())
        {
            throw new ArgumentOutOfRangeException(
                nameof(expiresAt),
                "An invitation must expire in the future.");
        }

        var code = WebEncoders.Base64UrlEncode(
            RandomNumberGenerator.GetBytes(32));
        var invitation = TenantInvitation.Create(
            tenantId,
            normalizedEmail,
            Hash(code),
            expiresAt);

        dbContext.TenantInvitations.Add(invitation);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new IssuedTenantInvitation(
            invitation.Id,
            normalizedEmail,
            code,
            invitation.TenantId,
            invitation.ExpiresAt);
    }

    public async Task<IssueTenantInvitationResult> IssueForOwnerAsync(
        string userId,
        string securityStamp,
        long membershipVersion,
        Guid tenantId,
        string email,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId)
            || string.IsNullOrWhiteSpace(securityStamp)
            || membershipVersion <= 0
            || tenantId == Guid.Empty)
        {
            return IssueTenantInvitationResult.FromStatus(
                IssueTenantInvitationStatus.StaleOwner);
        }

        var normalizedEmail = NormalizeEmail(email);
        if (normalizedEmail is null)
        {
            return IssueTenantInvitationResult.FromStatus(
                IssueTenantInvitationStatus.InvalidRecipient);
        }

        try
        {
            await using var transaction =
                await dbContext.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    cancellationToken);
            var isCurrentOwner = await IsCurrentOwnerAsync(
                userId,
                securityStamp,
                membershipVersion,
                tenantId,
                cancellationToken);
            if (!isCurrentOwner)
            {
                return IssueTenantInvitationResult.FromStatus(
                    IssueTenantInvitationStatus.StaleOwner);
            }

            var targetUserId = await dbContext.Users
                .AsNoTracking()
                .Where(user => user.NormalizedEmail == normalizedEmail)
                .Select(user => user.Id)
                .SingleOrDefaultAsync(cancellationToken);
            if (targetUserId is not null
                && await dbContext.TenantMemberships
                    .AsNoTracking()
                    .AnyAsync(
                        membership => membership.UserId == targetUserId
                            && membership.TenantId == tenantId
                            && membership.IsActive,
                        cancellationToken))
            {
                return IssueTenantInvitationResult.FromStatus(
                    IssueTenantInvitationStatus.ActiveMember);
            }

            var now = timeProvider.GetUtcNow();
            var pending = await dbContext.TenantInvitations
                .SingleOrDefaultAsync(
                    invitation => invitation.TenantId == tenantId
                        && invitation.NormalizedEmail == normalizedEmail
                        && invitation.ClosedAt == null,
                    cancellationToken);
            if (pending is not null)
            {
                if (pending.ExpiresAt > now)
                {
                    return IssueTenantInvitationResult.FromStatus(
                        IssueTenantInvitationStatus.Conflict);
                }

                pending.Close(now);
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            var code = WebEncoders.Base64UrlEncode(
                RandomNumberGenerator.GetBytes(32));
            var invitation = TenantInvitation.Create(
                tenantId,
                normalizedEmail,
                Hash(code),
                now.Add(InvitationLifetime),
                userId);
            var protectedPayload = outboxProtector.Protect(
                new TenantInvitationOutboxEnvelope(
                    TenantInvitationOutboxEnvelope.CurrentVersion,
                    invitation.Id,
                    invitation.TenantId,
                    invitation.NormalizedEmail,
                    code,
                    invitation.ExpiresAt));
            dbContext.TenantInvitations.Add(invitation);
            dbContext.TenantInvitationOutboxMessages.Add(
                TenantInvitationOutboxMessage.Create(
                    invitation.Id,
                    invitation.TenantId,
                    protectedPayload,
                    now));
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return IssueTenantInvitationResult.Queued(
                invitation.Id,
                invitation.ExpiresAt);
        }
        catch (DbUpdateConcurrencyException)
        {
            return IssueTenantInvitationResult.FromStatus(
                IssueTenantInvitationStatus.Conflict);
        }
        catch (DbUpdateException exception)
            when (IsInvitationConflict(exception))
        {
            return IssueTenantInvitationResult.FromStatus(
                IssueTenantInvitationStatus.Conflict);
        }
        catch (SqliteException exception)
            when (exception.SqliteErrorCode is 5 or 6 or 19)
        {
            return IssueTenantInvitationResult.FromStatus(
                IssueTenantInvitationStatus.Conflict);
        }
    }

    public async Task<CancelTenantInvitationResult> CancelForOwnerAsync(
        string userId,
        string securityStamp,
        long membershipVersion,
        Guid tenantId,
        Guid invitationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId)
            || string.IsNullOrWhiteSpace(securityStamp)
            || membershipVersion <= 0
            || tenantId == Guid.Empty)
        {
            return new(CancelTenantInvitationStatus.StaleOwner);
        }

        if (invitationId == Guid.Empty)
        {
            return new(CancelTenantInvitationStatus.Unavailable);
        }

        try
        {
            await using var transaction =
                await dbContext.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    cancellationToken);
            var isCurrentOwner = await IsCurrentOwnerAsync(
                userId,
                securityStamp,
                membershipVersion,
                tenantId,
                cancellationToken);
            if (!isCurrentOwner)
            {
                return new(CancelTenantInvitationStatus.StaleOwner);
            }

            var invitation = await dbContext.TenantInvitations
                .SingleOrDefaultAsync(
                    invitation => invitation.Id == invitationId
                        && invitation.TenantId == tenantId
                        && invitation.ClosedAt == null,
                    cancellationToken);
            if (invitation is null)
            {
                return new(CancelTenantInvitationStatus.Unavailable);
            }

            invitation.Close(timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new(CancelTenantInvitationStatus.Canceled);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new(CancelTenantInvitationStatus.Unavailable);
        }
        catch (DbUpdateException exception)
            when (IsSqliteBusyOrLocked(exception))
        {
            return new(CancelTenantInvitationStatus.Conflict);
        }
        catch (SqliteException exception)
            when (exception.SqliteErrorCode is 5 or 6)
        {
            return new(CancelTenantInvitationStatus.Conflict);
        }
    }

    public async Task<IReadOnlyList<PendingTenantInvitation>?>
        ListPendingForOwnerAsync(
            string userId,
            string securityStamp,
            long membershipVersion,
            Guid tenantId,
            CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId)
            || string.IsNullOrWhiteSpace(securityStamp)
            || membershipVersion <= 0
            || tenantId == Guid.Empty)
        {
            return null;
        }

        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
        var isCurrentOwner = await IsCurrentOwnerAsync(
            userId,
            securityStamp,
            membershipVersion,
            tenantId,
            cancellationToken);
        if (!isCurrentOwner)
        {
            return null;
        }

        // Tenant scope and the safe response shape stay in SQL. SQLite cannot
        // reliably order DateTimeOffset values, so only the already-safe
        // projection is filtered and ordered in memory.
        var openInvitations = await dbContext.TenantInvitations
            .AsNoTracking()
            .Where(invitation => invitation.TenantId == tenantId
                && invitation.ClosedAt == null)
            .Select(invitation => new PendingTenantInvitation(
                invitation.Id,
                invitation.NormalizedEmail,
                invitation.ExpiresAt))
            .ToListAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var pendingInvitations = openInvitations
            .Where(invitation => invitation.ExpiresAt > now)
            .OrderBy(invitation => invitation.ExpiresAt)
            .ThenBy(invitation => invitation.Email, StringComparer.Ordinal)
            .ThenBy(invitation => invitation.Id)
            .ToArray();

        await transaction.CommitAsync(cancellationToken);
        return pendingInvitations;
    }

    public async Task<bool> AcceptAsync(
        string userId,
        string securityStamp,
        string code,
        CancellationToken cancellationToken = default)
    {
        var normalizedCode = code?.Trim();
        if (string.IsNullOrWhiteSpace(userId)
            || string.IsNullOrWhiteSpace(securityStamp)
            || string.IsNullOrWhiteSpace(normalizedCode)
            || normalizedCode.Length < MinimumCodeLength
            || normalizedCode.Length > MaximumCodeLength)
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();
        var user = await dbContext.Users.SingleOrDefaultAsync(
            user => user.Id == userId,
            cancellationToken);
        if (user is null
            || string.IsNullOrWhiteSpace(user.NormalizedEmail)
            || !string.Equals(
                user.SecurityStamp,
                securityStamp,
                StringComparison.Ordinal))
        {
            return false;
        }

        var codeHash = Hash(normalizedCode);
        var invitation = await dbContext.TenantInvitations
            .SingleOrDefaultAsync(
                invitation => invitation.CodeHash == codeHash,
                cancellationToken);
        if (invitation is null
            || invitation.AcceptedAt is not null
            || invitation.ClosedAt is not null
            || invitation.ExpiresAt <= now
            || !string.Equals(
                invitation.NormalizedEmail,
                user.NormalizedEmail,
                StringComparison.Ordinal))
        {
            return false;
        }

        var membership = await dbContext.TenantMemberships
            .SingleOrDefaultAsync(
                membership => membership.UserId == user.Id
                    && membership.TenantId == invitation.TenantId,
                cancellationToken);
        if (membership is { IsActive: true })
        {
            invitation.Close(now);
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
            }

            return false;
        }

        invitation.Accept(user.Id, now);
        if (membership is null)
        {
            dbContext.TenantMemberships.Add(
                TenantMembershipRecord.Create(
                    user.Id,
                    invitation.TenantId,
                    TenantMembershipRole.Member,
                    now));
        }
        else
        {
            membership.Reactivate(TenantMembershipRole.Member);
        }

        user.TenantId = invitation.TenantId;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        user.ConcurrencyStamp = Guid.NewGuid().ToString("N");

        try
        {
            // SaveChanges uses one transaction. Both Version properties are
            // concurrency tokens, so only one request can consume this code.
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
        catch (DbUpdateException exception)
            when (IsConcurrentMembershipInsert(exception))
        {
            return false;
        }
        catch (SqliteException exception)
            when (exception.SqliteErrorCode is 5 or 6)
        {
            return false;
        }
    }

    private static string Hash(string code) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(code)));

    private string? NormalizeEmail(string? email)
    {
        var trimmed = email?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)
            || trimmed.Length > 256
            || !MailAddress.TryCreate(trimmed, out var parsed)
            || !string.Equals(
                parsed.Address,
                trimmed,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var normalized = normalizer.NormalizeEmail(trimmed);
        return string.IsNullOrWhiteSpace(normalized)
            || normalized.Length > 256
            ? null
            : normalized;
    }

    private Task<bool> IsCurrentOwnerAsync(
        string userId,
        string securityStamp,
        long membershipVersion,
        Guid tenantId,
        CancellationToken cancellationToken) =>
        (from membership in dbContext.TenantMemberships.AsNoTracking()
         join user in dbContext.Users.AsNoTracking()
             on membership.UserId equals user.Id
         where membership.UserId == userId
             && membership.TenantId == tenantId
             && membership.IsActive
             && membership.Role == TenantMembershipRole.Owner
             && membership.Version == membershipVersion
             && user.TenantId == tenantId
             && user.SecurityStamp == securityStamp
         select membership)
        .AnyAsync(cancellationToken);

    private static bool IsInvitationConflict(
        DbUpdateException exception) =>
        exception.InnerException is SqliteException
        {
            SqliteErrorCode: 19,
        } sqliteException
        && sqliteException.Message.Contains(
            "TenantInvitations.TenantId",
            StringComparison.Ordinal)
        && sqliteException.Message.Contains(
            "TenantInvitations.NormalizedEmail",
            StringComparison.Ordinal);

    private static bool IsSqliteBusyOrLocked(
        DbUpdateException exception) =>
        exception.InnerException is SqliteException
        {
            SqliteErrorCode: 5 or 6,
        };

    private static bool IsConcurrentMembershipInsert(
        DbUpdateException exception) =>
        exception.InnerException is SqliteException
        {
            SqliteErrorCode: 19,
        } sqliteException
        && sqliteException.Message.Contains(
            "TenantMemberships.UserId",
            StringComparison.Ordinal)
        && sqliteException.Message.Contains(
            "TenantMemberships.TenantId",
            StringComparison.Ordinal);
}
