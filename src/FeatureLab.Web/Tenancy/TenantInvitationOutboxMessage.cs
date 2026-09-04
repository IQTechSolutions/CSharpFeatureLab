using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace FeatureLab.Tenancy;

public sealed class TenantInvitationOutboxMessage
{
    public const int MaximumProtectedPayloadLength = 4096;

    private TenantInvitationOutboxMessage()
    {
    }

    private TenantInvitationOutboxMessage(
        Guid invitationId,
        Guid tenantId,
        string protectedPayload,
        DateTimeOffset createdAt)
    {
        if (invitationId == Guid.Empty)
        {
            throw new ArgumentException(
                "A non-empty invitation identifier is required.",
                nameof(invitationId));
        }

        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException(
                "A non-empty tenant identifier is required.",
                nameof(tenantId));
        }

        if (string.IsNullOrWhiteSpace(protectedPayload)
            || protectedPayload.Length > MaximumProtectedPayloadLength)
        {
            throw new ArgumentException(
                "A protected delivery payload is required.",
                nameof(protectedPayload));
        }

        InvitationId = invitationId;
        TenantId = tenantId;
        ProtectedPayload = protectedPayload;
        CreatedAt = createdAt.UtcDateTime;
    }

    public Guid InvitationId { get; private set; }

    public Guid TenantId { get; private set; }

    public string ProtectedPayload { get; private set; } = string.Empty;

    public DateTime CreatedAt { get; private set; }

    public static TenantInvitationOutboxMessage Create(
        Guid invitationId,
        Guid tenantId,
        string protectedPayload,
        DateTimeOffset createdAt) =>
        new(invitationId, tenantId, protectedPayload, createdAt);
}

public sealed record TenantInvitationOutboxEnvelope(
    int Version,
    Guid InvitationId,
    Guid TenantId,
    string NormalizedRecipient,
    string Code,
    DateTimeOffset ExpiresAt)
{
    public const int CurrentVersion = 1;

    public override string ToString() =>
        $"{nameof(TenantInvitationOutboxEnvelope)} {{ Version = {Version}, "
        + $"InvitationId = {InvitationId}, TenantId = {TenantId}, "
        + "NormalizedRecipient = [REDACTED], Code = [REDACTED], "
        + $"ExpiresAt = {ExpiresAt:O} }}";
}

public interface ITenantInvitationOutboxProtector
{
    string Protect(TenantInvitationOutboxEnvelope envelope);

    bool TryUnprotect(
        string protectedPayload,
        out TenantInvitationOutboxEnvelope? envelope);
}

public sealed class TenantInvitationOutboxProtector :
    ITenantInvitationOutboxProtector
{
    public const string Purpose =
        "FeatureLab.TenantInvitationDelivery.OutboxEnvelope.v1";

    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web);

    private readonly ITimeLimitedDataProtector _protector;

    public TenantInvitationOutboxProtector(
        IDataProtectionProvider dataProtectionProvider)
    {
        _protector = dataProtectionProvider
            .CreateProtector(Purpose)
            .ToTimeLimitedDataProtector();
    }

    public string Protect(TenantInvitationOutboxEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ValidateForProtection(envelope);

        return _protector.Protect(
            JsonSerializer.Serialize(envelope, JsonOptions),
            envelope.ExpiresAt);
    }

    public bool TryUnprotect(
        string protectedPayload,
        out TenantInvitationOutboxEnvelope? envelope)
    {
        envelope = null;
        if (string.IsNullOrWhiteSpace(protectedPayload))
        {
            return false;
        }

        try
        {
            var json = _protector.Unprotect(
                protectedPayload,
                out var protectedUntil);
            var candidate = JsonSerializer.Deserialize<
                TenantInvitationOutboxEnvelope>(json, JsonOptions);
            if (candidate is null
                || candidate.Version
                    != TenantInvitationOutboxEnvelope.CurrentVersion
                || candidate.InvitationId == Guid.Empty
                || candidate.TenantId == Guid.Empty
                || string.IsNullOrWhiteSpace(candidate.NormalizedRecipient)
                || string.IsNullOrWhiteSpace(candidate.Code)
                || candidate.ExpiresAt != protectedUntil)
            {
                return false;
            }

            envelope = candidate;
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void ValidateForProtection(
        TenantInvitationOutboxEnvelope envelope)
    {
        if (envelope.Version
                != TenantInvitationOutboxEnvelope.CurrentVersion
            || envelope.InvitationId == Guid.Empty
            || envelope.TenantId == Guid.Empty
            || string.IsNullOrWhiteSpace(envelope.NormalizedRecipient)
            || string.IsNullOrWhiteSpace(envelope.Code))
        {
            throw new ArgumentException(
                "A complete current-version delivery envelope is required.",
                nameof(envelope));
        }
    }
}
