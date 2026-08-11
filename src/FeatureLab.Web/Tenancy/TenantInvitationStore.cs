using System.Security.Cryptography;
using System.Text;
using FeatureLab.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FeatureLab.Tenancy;

public sealed record IssuedTenantInvitation(
    string Code,
    Guid TenantId,
    DateTimeOffset ExpiresAt);

public interface ITenantInvitationStore
{
    Task<IssuedTenantInvitation> IssueAsync(
        Guid tenantId,
        string email,
        DateTimeOffset expiresAt,
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
    TimeProvider timeProvider) : ITenantInvitationStore
{
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

        var normalizedEmail = normalizer.NormalizeEmail(email?.Trim());
        if (string.IsNullOrWhiteSpace(normalizedEmail))
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
            code,
            invitation.TenantId,
            invitation.ExpiresAt);
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
            return false;
        }

        invitation.Accept(user.Id, now);
        if (membership is null)
        {
            dbContext.TenantMemberships.Add(
                TenantMembershipRecord.Create(
                    user.Id,
                    invitation.TenantId,
                    now));
        }
        else
        {
            membership.Reactivate();
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
    }

    private static string Hash(string code) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(code)));

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
