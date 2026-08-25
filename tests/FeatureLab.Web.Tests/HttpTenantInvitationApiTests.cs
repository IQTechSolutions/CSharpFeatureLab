using System.Net;
using System.Text;
using FeatureLab.Client.Features.Tenancy;

namespace FeatureLab.Web.Tests;

public sealed class HttpTenantInvitationApiTests
{
    [Fact]
    public async Task List_sends_a_scope_free_get_and_maps_pending_invitations()
    {
        var invitationId = Guid.Parse(
            "1465d09d-1430-4a7c-bc7b-205f7396f128");
        var recipient = Recipient("owner-candidate");
        var handler = new RecordingHttpMessageHandler(
            HttpStatusCode.OK,
            $$"""
            [
              {
                "id": "{{invitationId}}",
                "email": "{{recipient}}",
                "expiresAt": "2026-08-26T16:30:00+00:00"
              }
            ]
            """);
        var api = new HttpTenantInvitationApi(CreateClient(handler));

        var result = await api.ListPendingAsync();

        var loaded = Assert.IsType<
            LoadPendingInvitationsResult.LoadedResult>(result);
        var invitation = Assert.Single(loaded.Invitations);
        Assert.Equal(invitationId, invitation.Id);
        Assert.Equal(recipient, invitation.Email);
        Assert.Equal(
            DateTimeOffset.Parse("2026-08-26T16:30:00+00:00"),
            invitation.ExpiresAt);
        Assert.Equal(HttpMethod.Get, handler.RequestMethod);
        Assert.Equal(
            "/api/tenant-invitations",
            handler.RequestPathAndQuery);
        Assert.Null(handler.RequestBody);
    }

    [Theory]
    [InlineData((int)HttpStatusCode.Unauthorized)]
    [InlineData((int)HttpStatusCode.Forbidden)]
    public async Task List_preserves_access_failures(int statusCode)
    {
        var handler = new RecordingHttpMessageHandler(
            (HttpStatusCode)statusCode,
            string.Empty);
        var api = new HttpTenantInvitationApi(CreateClient(handler));

        var result = await api.ListPendingAsync();

        if ((HttpStatusCode)statusCode == HttpStatusCode.Unauthorized)
        {
            Assert.IsType<LoadPendingInvitationsResult.UnauthorizedResult>(
                result);
        }
        else
        {
            Assert.IsType<LoadPendingInvitationsResult.ForbiddenResult>(result);
        }
    }

    [Fact]
    public async Task List_maps_invalid_json_to_a_safe_failure()
    {
        var handler = new RecordingHttpMessageHandler(
            HttpStatusCode.OK,
            "[{\"email\":\"unterminated");
        var api = new HttpTenantInvitationApi(CreateClient(handler));

        var result = await api.ListPendingAsync();

        Assert.IsType<LoadPendingInvitationsResult.FailureResult>(result);
    }

    [Fact]
    public async Task List_maps_a_network_error_to_a_safe_failure()
    {
        var api = new HttpTenantInvitationApi(
            CreateClient(new ThrowingHttpMessageHandler()));

        var result = await api.ListPendingAsync();

        Assert.IsType<LoadPendingInvitationsResult.FailureResult>(result);
    }

    [Fact]
    public async Task Cancel_sends_only_the_management_identifier()
    {
        var invitationId = Guid.Parse(
            "1465d09d-1430-4a7c-bc7b-205f7396f128");
        var handler = new RecordingHttpMessageHandler(
            HttpStatusCode.NoContent,
            null);
        var api = new HttpTenantInvitationApi(CreateClient(handler));

        var result = await api.CancelAsync(invitationId);

        Assert.IsType<CancelPendingInvitationResult.NoLongerPendingResult>(
            result);
        Assert.Equal(HttpMethod.Delete, handler.RequestMethod);
        Assert.Equal(
            $"/api/tenant-invitations/{invitationId:D}",
            handler.RequestPathAndQuery);
        Assert.Null(handler.RequestBody);
        Assert.DoesNotContain(
            "tenantId",
            handler.RequestPathAndQuery,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "code",
            handler.RequestPathAndQuery,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData((int)HttpStatusCode.Unauthorized)]
    [InlineData((int)HttpStatusCode.Forbidden)]
    [InlineData((int)HttpStatusCode.Conflict)]
    [InlineData((int)HttpStatusCode.InternalServerError)]
    public async Task Cancel_maps_each_safe_outcome(int statusCode)
    {
        var handler = new RecordingHttpMessageHandler(
            (HttpStatusCode)statusCode,
            """{"detail":"Do not expose this server detail."}""");
        var api = new HttpTenantInvitationApi(CreateClient(handler));

        var result = await api.CancelAsync(Guid.NewGuid());

        switch ((HttpStatusCode)statusCode)
        {
            case HttpStatusCode.Unauthorized:
                Assert.IsType<CancelPendingInvitationResult.UnauthorizedResult>(
                    result);
                break;
            case HttpStatusCode.Forbidden:
                Assert.IsType<CancelPendingInvitationResult.ForbiddenResult>(
                    result);
                break;
            case HttpStatusCode.Conflict:
                Assert.IsType<CancelPendingInvitationResult.ConflictResult>(
                    result);
                break;
            default:
                Assert.IsType<CancelPendingInvitationResult.FailureResult>(
                    result);
                break;
        }
    }

    [Fact]
    public async Task Cancel_maps_a_network_error_to_a_safe_failure()
    {
        var api = new HttpTenantInvitationApi(
            CreateClient(new ThrowingHttpMessageHandler()));

        var result = await api.CancelAsync(Guid.NewGuid());

        Assert.IsType<CancelPendingInvitationResult.FailureResult>(result);
    }

    private static HttpClient CreateClient(HttpMessageHandler handler) =>
        new(handler)
        {
            BaseAddress = new UriBuilder(
                Uri.UriSchemeHttps,
                "feature-lab.example").Uri,
        };

    private static string Recipient(string localPart) =>
        $"{localPart}@example.test";

    private sealed class RecordingHttpMessageHandler(
        HttpStatusCode statusCode,
        string? responseBody) : HttpMessageHandler
    {
        public HttpMethod? RequestMethod { get; private set; }

        public string? RequestPathAndQuery { get; private set; }

        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestMethod = request.Method;
            RequestPathAndQuery = request.RequestUri?.PathAndQuery;
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            var response = new HttpResponseMessage(statusCode);
            if (responseBody is not null)
            {
                response.Content = new StringContent(
                    responseBody,
                    Encoding.UTF8,
                    "application/json");
            }

            return response;
        }
    }

    private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("Synthetic network failure.");
    }
}
