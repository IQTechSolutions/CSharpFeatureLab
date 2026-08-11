using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FeatureLab.Data;
using FeatureLab.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
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

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider
                .GetRequiredService<FeatureLabDbContext>();
            var removedUser = await dbContext.Users
                .AsNoTracking()
                .SingleAsync(user => user.Id == member.UserId);
            Assert.NotEqual(member.SecurityStamp, removedUser.SecurityStamp);
            Assert.NotEqual(
                member.ConcurrencyStamp,
                removedUser.ConcurrencyStamp);
        }

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
    public async Task Concurrent_removal_has_one_winner()
    {
        var member = await RegisterMemberAsync(factory);
        using var firstClient = member.Client;
        using var secondClient = await SignInAsync(
            factory,
            member.Email);

        var attempts = await Task.WhenAll(
            firstClient.DeleteAsync("/api/tenant-membership"),
            secondClient.DeleteAsync("/api/tenant-membership"));

        Assert.Single(
            attempts,
            response => response.StatusCode == HttpStatusCode.NoContent);
        Assert.Single(
            attempts,
            response => response.StatusCode is HttpStatusCode.NotFound
                or HttpStatusCode.Forbidden);
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

        var tenantId = Guid.NewGuid();
        await TenantTestData.ProvisionAsync(
            factory.Services,
            email,
            tenantId);

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
            user.Id,
            email,
            tenantId,
            Assert.IsType<string>(user.SecurityStamp),
            Assert.IsType<string>(user.ConcurrencyStamp));
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
        await TenantTestData.ProvisionAsync(
            factory.Services,
            email,
            tenantId);
    }

    private sealed record RegisteredMember(
        HttpClient Client,
        string UserId,
        string Email,
        Guid TenantId,
        string SecurityStamp,
        string ConcurrencyStamp);
}
