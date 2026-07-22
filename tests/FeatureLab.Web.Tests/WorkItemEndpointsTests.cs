using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FeatureLab.Features.WorkItems;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;

namespace FeatureLab.Web.Tests;

public sealed class WorkItemEndpointsTests : IClassFixture<FeatureLabWebFactory>
{
    private readonly FeatureLabWebFactory _factory;

    public WorkItemEndpointsTests(FeatureLabWebFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Create_returns_the_new_work_item()
    {
        using var client = await CreateAuthenticatedClientAsync();
        var response = await client.PostAsJsonAsync("/api/work-items", new
        {
            title = "  Ship the first feature  ",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<WorkItemResponse>();
        Assert.NotNull(created);
        Assert.Equal("Ship the first feature", created.Title);
        Assert.NotEqual(Guid.Empty, created.Id);
    }

    [Fact]
    public async Task Create_rejects_an_empty_title()
    {
        using var client = await CreateAuthenticatedClientAsync();
        var response = await client.PostAsJsonAsync("/api/work-items", new
        {
            title = "  ",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_contains_a_created_work_item()
    {
        using var client = await CreateAuthenticatedClientAsync();
        await client.PostAsJsonAsync("/api/work-items", new
        {
            title = "Prove the vertical slice",
        });

        var items = await client.GetFromJsonAsync<WorkItemResponse[]>("/api/work-items");

        Assert.NotNull(items);
        Assert.Contains(items, item => item.Title == "Prove the vertical slice");
    }

    [Fact]
    public async Task Create_rejects_an_anonymous_request()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/work-items", new
        {
            title = "This must not be accepted",
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task List_only_returns_the_authenticated_users_work_items()
    {
        using var firstUser = await CreateAuthenticatedClientAsync();
        using var secondUser = await CreateAuthenticatedClientAsync();

        await firstUser.PostAsJsonAsync("/api/work-items", new
        {
            title = "First user's private work item",
        });

        var secondUsersItems = await secondUser.GetFromJsonAsync<WorkItemResponse[]>("/api/work-items");

        Assert.NotNull(secondUsersItems);
        Assert.DoesNotContain(secondUsersItems, item => item.Title == "First user's private work item");
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync()
    {
        var client = _factory.CreateClient();
        var email = $"learner-{Guid.NewGuid():N}@example.test";
        const string password = "FeatureLab!123";

        var registration = await client.PostAsJsonAsync("/account/register", new
        {
            email,
            password,
        });
        registration.EnsureSuccessStatusCode();

        var login = await client.PostAsJsonAsync("/account/login", new
        {
            email,
            password,
        });
        login.EnsureSuccessStatusCode();

        var tokens = await login.Content.ReadFromJsonAsync<LoginTokenResponse>();
        Assert.NotNull(tokens);
        Assert.False(string.IsNullOrWhiteSpace(tokens.AccessToken));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }
}

public sealed record LoginTokenResponse(string AccessToken);

public sealed class FeatureLabWebFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"feature-lab-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:FeatureLab", $"Data Source={_databasePath};Pooling=False");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing && File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }
}
