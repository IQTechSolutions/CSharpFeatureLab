using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace FeatureLab.Tenancy;

public static class TenantInvitationEndpoints
{
    public static IEndpointRouteBuilder MapTenantInvitationEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
                "/api/tenant-invitations/accept",
                async (
                    AcceptTenantInvitationRequest request,
                    ClaimsPrincipal principal,
                    ITenantInvitationStore invitations,
                    IOptions<IdentityOptions> identityOptions,
                    CancellationToken cancellationToken) =>
                {
                    var userId = principal.FindFirstValue(
                        ClaimTypes.NameIdentifier);
                    if (string.IsNullOrWhiteSpace(userId))
                    {
                        return Results.Unauthorized();
                    }

                    var securityStamps = principal.FindAll(
                            identityOptions.Value.ClaimsIdentity
                                .SecurityStampClaimType)
                        .Select(claim => claim.Value)
                        .ToArray();
                    if (securityStamps.Length != 1
                        || string.IsNullOrWhiteSpace(securityStamps[0]))
                    {
                        return CannotAccept();
                    }

                    var accepted = await invitations.AcceptAsync(
                        userId,
                        securityStamps[0],
                        request.Code ?? string.Empty,
                        cancellationToken);

                    return accepted
                        ? Results.NoContent()
                        : CannotAccept();
                })
            .RequireAuthorization();

        return endpoints;
    }

    public sealed record AcceptTenantInvitationRequest(string? Code);

    private static IResult CannotAccept() =>
        Results.BadRequest(new
        {
            error = "The invitation cannot be accepted.",
        });
}
