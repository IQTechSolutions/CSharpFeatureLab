using System.Net;
using System.Text;
using FeatureLab.Client.Features.WorkItems;

namespace FeatureLab.Web.Tests;

public sealed class HttpWorkItemApiTests
{
    [Fact]
    public async Task Create_maps_a_created_response()
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.Created,
            """
            {
              "id": "1dc18df2-3b2b-49da-8d09-9c3fd9fb05e4",
              "title": "Ship the Blazor form",
              "isCompleted": false,
              "createdAtUtc": "2026-07-26T18:00:00Z"
            }
            """);
        var client = CreateClient(handler);
        var api = new HttpWorkItemApi(client);

        var result = await api.CreateAsync("  Ship the Blazor form  ");

        var created = Assert.IsType<CreateWorkItemResult.CreatedResult>(result);
        Assert.Equal("Ship the Blazor form", created.Title);
        Assert.Contains(
            "\"title\":\"  Ship the Blazor form  \"",
            handler.RequestBody,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_maps_a_validation_problem()
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.BadRequest,
            """
            {
              "title": "One or more validation errors occurred.",
              "status": 400,
              "errors": {
                "Title": ["Title must contain 3 to 120 characters."]
              }
            }
            """);
        var api = new HttpWorkItemApi(CreateClient(handler));

        var result = await api.CreateAsync("x");

        var validation = Assert.IsType<CreateWorkItemResult.ValidationResult>(result);
        Assert.Equal(
            "Title must contain 3 to 120 characters.",
            Assert.Single(validation.Errors["Title"]));
    }

    [Fact]
    public async Task Create_maps_a_forbidden_response()
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.Forbidden,
            string.Empty);
        var api = new HttpWorkItemApi(CreateClient(handler));

        var result = await api.CreateAsync("Protected work item");

        Assert.IsType<CreateWorkItemResult.ForbiddenResult>(result);
    }

    [Fact]
    public async Task Create_maps_an_unauthorized_response()
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.Unauthorized,
            string.Empty);
        var api = new HttpWorkItemApi(CreateClient(handler));

        var result = await api.CreateAsync("Sign-in required");

        Assert.IsType<CreateWorkItemResult.UnauthorizedResult>(result);
    }

    [Fact]
    public async Task Create_maps_an_unexpected_response_to_a_safe_failure()
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.InternalServerError,
            """{"detail":"Sensitive server detail"}""");
        var api = new HttpWorkItemApi(CreateClient(handler));

        var result = await api.CreateAsync("Do not expose details");

        Assert.IsType<CreateWorkItemResult.FailureResult>(result);
    }

    [Fact]
    public async Task Create_maps_a_network_error_to_a_safe_failure()
    {
        var api = new HttpWorkItemApi(CreateClient(new ThrowingHttpMessageHandler()));

        var result = await api.CreateAsync("Network failure");

        Assert.IsType<CreateWorkItemResult.FailureResult>(result);
    }

    [Fact]
    public async Task Create_maps_an_invalid_json_response_to_a_safe_failure()
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.Created,
            """{"title":""");
        var api = new HttpWorkItemApi(CreateClient(handler));

        var result = await api.CreateAsync("Invalid response");

        Assert.IsType<CreateWorkItemResult.FailureResult>(result);
    }

    [Fact]
    public async Task Create_maps_an_empty_created_response_to_a_safe_failure()
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.Created,
            "null");
        var api = new HttpWorkItemApi(CreateClient(handler));

        var result = await api.CreateAsync("Empty response");

        Assert.IsType<CreateWorkItemResult.FailureResult>(result);
    }

    private sealed class StubHttpMessageHandler(
        HttpStatusCode statusCode,
        string responseBody) : HttpMessageHandler
    {
        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(
                    responseBody,
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    private static HttpClient CreateClient(HttpMessageHandler handler) =>
        new(handler)
        {
            BaseAddress = new UriBuilder(
                Uri.UriSchemeHttps,
                "feature-lab.example").Uri,
        };

    private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("Synthetic network failure.");
    }
}
