using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace FeatureLab.Client.Features.Tenancy;

public sealed class HttpTenantInvitationApi(HttpClient httpClient)
    : ITenantInvitationApi
{
    private const string InvitationsPath = "api/tenant-invitations";

    public async Task<IssuePendingInvitationResult> IssueAsync(
        string recipientEmail,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(recipientEmail))
        {
            return IssuePendingInvitationResult.InvalidRecipient();
        }

        using var request = CreateRequest(HttpMethod.Post, InvitationsPath);
        request.Content = JsonContent.Create(
            new IssueInvitationRequest(recipientEmail.Trim()));

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return IssuePendingInvitationResult.Failure();
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                return IssuePendingInvitationResult.InvalidRecipient();
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return IssuePendingInvitationResult.Unauthorized();
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                return IssuePendingInvitationResult.Forbidden();
            }

            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                return IssuePendingInvitationResult.Conflict();
            }

            if (response.StatusCode != HttpStatusCode.Accepted)
            {
                return IssuePendingInvitationResult.Failure();
            }

            try
            {
                var invitation = await response.Content
                    .ReadFromJsonAsync<QueuedInvitationResponse>(
                        cancellationToken);
                if (invitation is null
                    || invitation.Id == Guid.Empty
                    || invitation.ExpiresAt == default
                    || !string.Equals(
                        invitation.DeliveryStatus,
                        "queued",
                        StringComparison.Ordinal))
                {
                    return IssuePendingInvitationResult.Failure();
                }

                return IssuePendingInvitationResult.Queued(
                    invitation.Id,
                    invitation.ExpiresAt);
            }
            catch (Exception exception)
                when (exception is HttpRequestException
                    or JsonException
                    or NotSupportedException)
            {
                return IssuePendingInvitationResult.Failure();
            }
        }
    }

    public async Task<LoadPendingInvitationsResult> ListPendingAsync(
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, InvitationsPath);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return LoadPendingInvitationsResult.Failure();
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return LoadPendingInvitationsResult.Unauthorized();
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                return LoadPendingInvitationsResult.Forbidden();
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                return LoadPendingInvitationsResult.Failure();
            }

            try
            {
                var invitations = await response.Content
                    .ReadFromJsonAsync<PendingInvitationResponse[]>(
                        cancellationToken);
                if (invitations is null
                    || invitations.Any(invitation =>
                        invitation.Id == Guid.Empty
                        || string.IsNullOrWhiteSpace(invitation.Email)
                        || invitation.ExpiresAt == default))
                {
                    return LoadPendingInvitationsResult.Failure();
                }

                return LoadPendingInvitationsResult.Loaded(
                    invitations
                        .Select(invitation => new PendingInvitationSummary(
                            invitation.Id,
                            invitation.Email,
                            invitation.ExpiresAt))
                        .ToArray());
            }
            catch (Exception exception)
                when (exception is HttpRequestException
                    or JsonException
                    or NotSupportedException)
            {
                return LoadPendingInvitationsResult.Failure();
            }
        }
    }

    public async Task<CancelPendingInvitationResult> CancelAsync(
        Guid invitationId,
        CancellationToken cancellationToken = default)
    {
        if (invitationId == Guid.Empty)
        {
            return CancelPendingInvitationResult.Failure();
        }

        using var request = CreateRequest(
            HttpMethod.Delete,
            $"{InvitationsPath}/{invitationId:D}");

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return CancelPendingInvitationResult.Failure();
        }

        using (response)
        {
            return response.StatusCode switch
            {
                HttpStatusCode.NoContent
                    => CancelPendingInvitationResult.NoLongerPending(),
                HttpStatusCode.Unauthorized
                    => CancelPendingInvitationResult.Unauthorized(),
                HttpStatusCode.Forbidden
                    => CancelPendingInvitationResult.Forbidden(),
                HttpStatusCode.Conflict
                    => CancelPendingInvitationResult.Conflict(),
                _ => CancelPendingInvitationResult.Failure(),
            };
        }
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string requestUri)
    {
        var request = new HttpRequestMessage(method, requestUri);
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        return request;
    }

    private sealed record PendingInvitationResponse(
        Guid Id,
        string Email,
        DateTimeOffset ExpiresAt);

    private sealed record IssueInvitationRequest(string Email);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record QueuedInvitationResponse(
        Guid Id,
        DateTimeOffset ExpiresAt,
        string DeliveryStatus);
}
