using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using FeatureLab.Features.WorkItems;
using FeatureLab.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

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
        using var client = await CreateAuthenticatedClientAsync(canCreateWorkItems: true);
        var response = await client.PostAsJsonAsync("/api/work-items", new
        {
            title = "  Ship the first feature  ",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<WorkItemResponse>();
        Assert.NotNull(created);
        Assert.Equal("Ship the first feature", created.Title);
        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal(1, created.Version);
    }

    [Fact]
    public async Task Create_rejects_an_empty_title()
    {
        using var client = await CreateAuthenticatedClientAsync(canCreateWorkItems: true);
        var response = await client.PostAsJsonAsync("/api/work-items", new
        {
            title = "  ",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_contains_a_created_work_item()
    {
        using var client = await CreateAuthenticatedClientAsync(canCreateWorkItems: true);
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
    public async Task Create_forbids_an_authenticated_user_without_permission()
    {
        using var client = await CreateAuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync("/api/work-items", new
        {
            title = "This user is signed in but cannot create",
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task List_allows_an_authenticated_user_without_create_permission()
    {
        using var client = await CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/work-items");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task List_only_returns_the_authenticated_users_work_items()
    {
        using var firstUser = await CreateAuthenticatedClientAsync(canCreateWorkItems: true);
        using var secondUser = await CreateAuthenticatedClientAsync(canCreateWorkItems: true);

        var createResponse = await firstUser.PostAsJsonAsync("/api/work-items", new
        {
            title = "First user's private work item",
        });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var secondUsersItems = await secondUser.GetFromJsonAsync<WorkItemResponse[]>("/api/work-items");

        Assert.NotNull(secondUsersItems);
        Assert.DoesNotContain(secondUsersItems, item => item.Title == "First user's private work item");
    }

    [Fact]
    public async Task Update_renames_the_work_item_and_advances_its_version()
    {
        using var client = await CreateAuthenticatedClientAsync(canCreateWorkItems: true);
        var created = await CreateWorkItemAsync(client, "Original title");

        var response = await client.PutAsJsonAsync(
            $"/api/work-items/{created.Id}/title",
            new
            {
                title = "  Updated title  ",
                version = created.Version,
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var updated = await response.Content.ReadFromJsonAsync<WorkItemResponse>();
        Assert.NotNull(updated);
        Assert.Equal("Updated title", updated.Title);
        Assert.Equal(created.Version + 1, updated.Version);
    }

    [Fact]
    public async Task Update_rejects_a_stale_version_without_losing_the_winning_change()
    {
        using var client = await CreateAuthenticatedClientAsync(canCreateWorkItems: true);
        var created = await CreateWorkItemAsync(client, "Original title");

        var winningResponse = await client.PutAsJsonAsync(
            $"/api/work-items/{created.Id}/title",
            new
            {
                title = "First editor wins",
                version = created.Version,
            });
        Assert.Equal(HttpStatusCode.OK, winningResponse.StatusCode);

        var staleResponse = await client.PutAsJsonAsync(
            $"/api/work-items/{created.Id}/title",
            new
            {
                title = "Stale editor overwrites",
                version = created.Version,
            });

        Assert.Equal(HttpStatusCode.Conflict, staleResponse.StatusCode);
        Assert.Contains(
            "Reload the work item",
            await staleResponse.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        var items = await client.GetFromJsonAsync<WorkItemResponse[]>("/api/work-items");
        var saved = Assert.Single(items!, item => item.Id == created.Id);
        Assert.Equal("First editor wins", saved.Title);
        Assert.Equal(created.Version + 1, saved.Version);
    }

    [Fact]
    public async Task Update_hides_another_users_work_item()
    {
        using var owner = await CreateAuthenticatedClientAsync(canCreateWorkItems: true);
        using var otherUser = await CreateAuthenticatedClientAsync();
        var created = await CreateWorkItemAsync(owner, "Owner-only edit");

        var response = await otherUser.PutAsJsonAsync(
            $"/api/work-items/{created.Id}/title",
            new
            {
                title = "Another user's edit",
                version = created.Version,
            });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Update_rejects_an_invalid_version()
    {
        using var client = await CreateAuthenticatedClientAsync(canCreateWorkItems: true);
        var created = await CreateWorkItemAsync(client, "Versioned item");

        var response = await client.PutAsJsonAsync(
            $"/api/work-items/{created.Id}/title",
            new
            {
                title = "Invalid version",
                version = 0,
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Update_rejects_an_invalid_title_without_changing_the_work_item()
    {
        using var client = await CreateAuthenticatedClientAsync(canCreateWorkItems: true);
        var created = await CreateWorkItemAsync(client, "Original valid title");

        var response = await client.PutAsJsonAsync(
            $"/api/work-items/{created.Id}/title",
            new
            {
                title = "x",
                version = created.Version,
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var items = await client.GetFromJsonAsync<WorkItemResponse[]>("/api/work-items");
        var saved = Assert.Single(items!, item => item.Id == created.Id);
        Assert.Equal("Original valid title", saved.Title);
        Assert.Equal(created.Version, saved.Version);
    }

    [Fact]
    public async Task Blazor_host_serves_the_create_route()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/work-items/new");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(
            "_framework/blazor.webassembly.js",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync(bool canCreateWorkItems = false)
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

        if (canCreateWorkItems)
        {
            await GrantCreatePermissionAsync(email);
        }

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

    private static async Task<WorkItemResponse> CreateWorkItemAsync(
        HttpClient client,
        string title)
    {
        var response = await client.PostAsJsonAsync("/api/work-items", new
        {
            title,
        });
        response.EnsureSuccessStatusCode();

        var created = await response.Content.ReadFromJsonAsync<WorkItemResponse>();
        return Assert.IsType<WorkItemResponse>(created);
    }

    private async Task GrantCreatePermissionAsync(string email)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<FeatureLabUser>>();
        var user = await userManager.FindByEmailAsync(email);

        Assert.NotNull(user);

        var result = await userManager.AddClaimAsync(
            user,
            new Claim(
                WorkItemAuthorization.PermissionClaimType,
                WorkItemAuthorization.CreatePermission));

        Assert.True(
            result.Succeeded,
            string.Join("; ", result.Errors.Select(error => error.Description)));
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
