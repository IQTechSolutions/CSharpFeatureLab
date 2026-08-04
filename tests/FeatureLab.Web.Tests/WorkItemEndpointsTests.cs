using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using FeatureLab.Features.BackgroundJobs;
using FeatureLab.Features.WorkItems;
using FeatureLab.Identity;
using FeatureLab.Tenancy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
    public async Task List_forbids_an_authenticated_user_without_tenant_membership()
    {
        using var client = await CreateAuthenticatedClientAsync(hasTenantMembership: false);

        var response = await client.GetAsync("/api/work-items");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Registration_provisions_a_server_owned_tenant_membership()
    {
        using var client = _factory.CreateClient();
        var email = $"new-member-{Guid.NewGuid():N}@example.test";
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
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.GetAsync("/api/work-items");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task List_forbids_an_ambiguous_tenant_principal()
    {
        var email = $"ambiguous-member-{Guid.NewGuid():N}@example.test";
        const string password = "FeatureLab!123";
        using var registrationClient = _factory.CreateClient();
        var registration = await registrationClient.PostAsJsonAsync("/account/register", new
        {
            email,
            password,
        });
        registration.EnsureSuccessStatusCode();
        await GrantClaimAsync(
            email,
            new Claim(
                TenantMembership.ClaimType,
                Guid.NewGuid().ToString()));
        using var client = await SignInAsync(email, password);

        var response = await client.GetAsync("/api/work-items");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
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
    public async Task List_hides_work_items_from_another_tenant()
    {
        var clients = await CreateClientsInDifferentTenantsAsync();
        using var firstTenant = clients.FirstTenant;
        using var secondTenant = clients.SecondTenant;
        var title = $"Tenant A item {Guid.NewGuid():N}";

        var createResponse = await firstTenant.PostAsJsonAsync("/api/work-items", new
        {
            title,
            tenantId = clients.SecondTenantId,
        });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var secondTenantItems =
            await secondTenant.GetFromJsonAsync<WorkItemResponse[]>("/api/work-items");

        Assert.NotNull(secondTenantItems);
        Assert.DoesNotContain(secondTenantItems, item => item.Title == title);
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
    public async Task Update_hides_a_work_item_from_another_tenant()
    {
        var clients = await CreateClientsInDifferentTenantsAsync();
        using var firstTenant = clients.FirstTenant;
        using var secondTenant = clients.SecondTenant;
        var created = await CreateWorkItemAsync(
            firstTenant,
            "Tenant A edit boundary");

        var response = await secondTenant.PutAsJsonAsync(
            $"/api/work-items/{created.Id}/title",
            new
            {
                title = "Tenant B edit",
                version = created.Version,
            });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Reports_hide_resources_from_another_tenant()
    {
        var clients = await CreateClientsInDifferentTenantsAsync();
        using var firstTenant = clients.FirstTenant;
        using var secondTenant = clients.SecondTenant;
        var created = await CreateWorkItemAsync(
            firstTenant,
            "Tenant A report boundary");
        var reportRequest = await firstTenant.PostAsync(
            $"/api/work-items/{created.Id}/reports",
            content: null);
        reportRequest.EnsureSuccessStatusCode();
        var accepted =
            await reportRequest.Content.ReadFromJsonAsync<WorkItemReportAcceptedResponse>();
        Assert.NotNull(accepted);

        var requestFromAnotherTenant = await secondTenant.PostAsync(
            $"/api/work-items/{created.Id}/reports",
            content: null);
        var statusFromAnotherTenant = await secondTenant.GetAsync(
            $"/api/work-items/reports/{accepted.Report.Id}");

        Assert.Equal(HttpStatusCode.NotFound, requestFromAnotherTenant.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, statusFromAnotherTenant.StatusCode);
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

    private async Task<HttpClient> CreateAuthenticatedClientAsync(
        bool canCreateWorkItems = false,
        bool hasTenantMembership = true)
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

        if (!hasTenantMembership)
        {
            await AssignTenantAsync(email, Guid.Empty);
        }

        if (canCreateWorkItems)
        {
            await GrantClaimAsync(
                email,
                new Claim(
                    WorkItemAuthorization.PermissionClaimType,
                    WorkItemAuthorization.CreatePermission));
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

    private async Task<TenantClients> CreateClientsInDifferentTenantsAsync()
    {
        var firstEmail = $"tenant-a-{Guid.NewGuid():N}@example.test";
        var secondEmail = $"tenant-b-{Guid.NewGuid():N}@example.test";
        const string password = "FeatureLab!123";
        using var registrationClient = _factory.CreateClient();
        var firstRegistration = await registrationClient.PostAsJsonAsync("/account/register", new
        {
            email = firstEmail,
            password,
        });
        firstRegistration.EnsureSuccessStatusCode();
        var secondRegistration = await registrationClient.PostAsJsonAsync("/account/register", new
        {
            email = secondEmail,
            password,
        });
        secondRegistration.EnsureSuccessStatusCode();

        var firstTenantId = Guid.NewGuid();
        await AssignTenantAsync(firstEmail, firstTenantId);
        await GrantClaimAsync(
            firstEmail,
            new Claim(
                WorkItemAuthorization.PermissionClaimType,
                WorkItemAuthorization.CreatePermission));
        var firstTenant = await SignInAsync(firstEmail, password);

        var secondTenantId = Guid.NewGuid();
        await AssignTenantAsync(secondEmail, secondTenantId);
        var secondTenant = await SignInAsync(secondEmail, password);

        return new TenantClients(
            firstTenant,
            secondTenant,
            secondTenantId);
    }

    private async Task<HttpClient> SignInAsync(string email, string password)
    {
        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/account/login", new
        {
            email,
            password,
        });
        login.EnsureSuccessStatusCode();

        var tokens = await login.Content.ReadFromJsonAsync<LoginTokenResponse>();
        Assert.NotNull(tokens);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    private async Task AssignTenantAsync(
        string email,
        Guid tenantId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<FeatureLabUser>>();
        var user = await userManager.FindByEmailAsync(email);
        Assert.NotNull(user);

        user.TenantId = tenantId;
        var update = await userManager.UpdateAsync(user);

        Assert.True(
            update.Succeeded,
            string.Join("; ", update.Errors.Select(error => error.Description)));
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

    private async Task GrantClaimAsync(string email, Claim claim)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<FeatureLabUser>>();
        var user = await userManager.FindByEmailAsync(email);

        Assert.NotNull(user);

        var result = await userManager.AddClaimAsync(user, claim);

        Assert.True(
            result.Succeeded,
            string.Join("; ", result.Errors.Select(error => error.Description)));
    }

    private sealed record TenantClients(
        HttpClient FirstTenant,
        HttpClient SecondTenant,
        Guid SecondTenantId);
}

public sealed record LoginTokenResponse(string AccessToken);

public sealed class FeatureLabWebFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"feature-lab-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:FeatureLab", $"Data Source={_databasePath};Pooling=False");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IWorkItemReportScheduler>();
            services.AddSingleton<RecordingWorkItemReportScheduler>();
            services.AddSingleton<IWorkItemReportScheduler>(
                provider => provider.GetRequiredService<RecordingWorkItemReportScheduler>());
        });
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
