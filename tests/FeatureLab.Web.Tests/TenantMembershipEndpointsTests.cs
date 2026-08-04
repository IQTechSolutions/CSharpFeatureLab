using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FeatureLab.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace FeatureLab.Web.Tests;

public sealed class TenantMembershipEndpointsTests(
    FeatureLabWebFactory factory)
    : IClassFixture<FeatureLabWebFactory>
{
    [Fact]
    public async Task Removing_membership_blocks_the_existing_access_token()
    {
        var member = await RegisterMemberAsync(factory);
        using var client = member.Client;

        var beforeRemoval = await client.GetAsync("/api/work-items");
        Assert.Equal(HttpStatusCode.OK, beforeRemoval.StatusCode);

        var removal = await client.DeleteAsync("/api/tenant-membership");
        Assert.Equal(HttpStatusCode.NoContent, removal.StatusCode);

        var afterRemoval = await client.GetAsync("/api/work-items");
        Assert.Equal(HttpStatusCode.Forbidden, afterRemoval.StatusCode);
    }

    [Fact]
    public async Task Anonymous_requests_cannot_remove_membership()
    {
        using var client = factory.CreateClient();

        var response = await client.DeleteAsync("/api/tenant-membership");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Rejoining_the_same_tenant_does_not_reactivate_the_old_token()
    {
        var member = await RegisterMemberAsync(factory);
        using var client = member.Client;
        var removal = await client.DeleteAsync("/api/tenant-membership");
        Assert.Equal(HttpStatusCode.NoContent, removal.StatusCode);

        await AssignTenantAsync(
            factory,
            member.Email,
            member.TenantId);

        var response = await client.GetAsync("/api/work-items");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using var freshClient = await SignInAsync(
            factory,
            member.Email);
        var freshResponse = await freshClient.GetAsync("/api/work-items");
        Assert.Equal(HttpStatusCode.OK, freshResponse.StatusCode);
    }

    private static async Task<RegisteredMember> RegisterMemberAsync(
        WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        var email = $"departing-member-{Guid.NewGuid():N}@example.test";
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

        var tokens = await login.Content
            .ReadFromJsonAsync<LoginTokenResponse>();
        Assert.NotNull(tokens);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        await using var scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider
            .GetRequiredService<UserManager<FeatureLabUser>>();
        var user = await userManager.FindByEmailAsync(email);
        Assert.NotNull(user);

        return new RegisteredMember(
            client,
            email,
            user.TenantId);
    }

    private static async Task<HttpClient> SignInAsync(
        WebApplicationFactory<Program> factory,
        string email)
    {
        var client = factory.CreateClient();
        const string password = "FeatureLab!123";
        var login = await client.PostAsJsonAsync("/account/login", new
        {
            email,
            password,
        });
        login.EnsureSuccessStatusCode();
        var tokens = await login.Content
            .ReadFromJsonAsync<LoginTokenResponse>();
        Assert.NotNull(tokens);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    private static async Task AssignTenantAsync(
        WebApplicationFactory<Program> factory,
        string email,
        Guid tenantId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider
            .GetRequiredService<UserManager<FeatureLabUser>>();
        var user = await userManager.FindByEmailAsync(email);
        Assert.NotNull(user);
        user.TenantId = tenantId;
        var update = await userManager.UpdateAsync(user);
        Assert.True(
            update.Succeeded,
            string.Join(
                "; ",
                update.Errors.Select(error => error.Description)));
    }

    private sealed record RegisteredMember(
        HttpClient Client,
        string Email,
        Guid TenantId);
}
