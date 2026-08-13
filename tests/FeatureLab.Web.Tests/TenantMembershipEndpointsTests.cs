using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FeatureLab.Data;
using FeatureLab.Features.WorkItems;
using FeatureLab.Identity;
using FeatureLab.Tenancy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FeatureLab.Web.Tests;

public sealed class TenantMembershipEndpointsTests(
    FeatureLabWebFactory factory)
    : IClassFixture<FeatureLabWebFactory>
{
    [Fact]
    public async Task Listing_and_selecting_an_existing_membership_switches_scope()
    {
        var initialTenantId = Guid.NewGuid();
        var targetTenantId = Guid.NewGuid();
        var member = await RegisterMemberAsync(factory, initialTenantId);
        using var client = member.Client;
        await ProvisionMembershipAsync(member.Email, targetTenantId);
        var initialTitle = $"Initial workspace {Guid.NewGuid():N}";
        var targetTitle = $"Target workspace {Guid.NewGuid():N}";
        await SeedWorkItemAsync(member.UserId, initialTenantId, initialTitle);
        await SeedWorkItemAsync(member.UserId, targetTenantId, targetTitle);

        var before = await client.GetFromJsonAsync<TenantMembershipOption[]>(
            "/api/tenant-memberships");

        Assert.NotNull(before);
        Assert.Equal(2, before.Length);
        Assert.Contains(
            before,
            membership => membership.TenantId == initialTenantId
                && membership.IsSelected);
        Assert.Contains(
            before,
            membership => membership.TenantId == targetTenantId
                && !membership.IsSelected);

        var switched = await client.PutAsJsonAsync(
            "/api/tenant-membership",
            new
            {
                tenantId = targetTenantId,
                userId = "forged-user",
                isActive = false,
                version = long.MaxValue,
            });

        Assert.Equal(HttpStatusCode.NoContent, switched.StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.GetAsync("/api/work-items")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.GetAsync("/api/tenant-memberships")).StatusCode);

        using var freshClient = await SignInAsync(factory, member.Email);
        var selected = await freshClient
            .GetFromJsonAsync<TenantMembershipOption[]>(
                "/api/tenant-memberships");
        Assert.NotNull(selected);
        Assert.Contains(
            selected,
            membership => membership.TenantId == targetTenantId
                && membership.IsSelected);

        var visibleItems = await freshClient
            .GetFromJsonAsync<WorkItemResponse[]>("/api/work-items");
        Assert.NotNull(visibleItems);
        Assert.Contains(visibleItems, item => item.Title == targetTitle);
        Assert.DoesNotContain(visibleItems, item => item.Title == initialTitle);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<FeatureLabDbContext>();
        var storedUser = await dbContext.Users
            .AsNoTracking()
            .SingleAsync(user => user.Id == member.UserId);
        var storedMemberships = await dbContext.TenantMemberships
            .AsNoTracking()
            .Where(membership => membership.UserId == member.UserId)
            .ToArrayAsync();
        Assert.Equal(targetTenantId, storedUser.TenantId);
        Assert.NotEqual(member.SecurityStamp, storedUser.SecurityStamp);
        Assert.NotEqual(member.ConcurrencyStamp, storedUser.ConcurrencyStamp);
        Assert.Equal(2, storedMemberships.Length);
        Assert.All(storedMemberships, membership =>
        {
            Assert.True(membership.IsActive);
            Assert.Equal(1, membership.Version);
        });
        Assert.Empty(dbContext.TenantInvitations);
    }

    [Fact]
    public async Task Listing_returns_only_the_callers_active_memberships()
    {
        var selectedTenantId = Guid.NewGuid();
        var activeTenantId = Guid.NewGuid();
        var inactiveTenantId = Guid.NewGuid();
        var member = await RegisterMemberAsync(factory, selectedTenantId);
        using var client = member.Client;
        await ProvisionMembershipAsync(member.Email, activeTenantId);
        await ProvisionMembershipAsync(member.Email, inactiveTenantId);
        await DeactivateMembershipAsync(member.UserId, inactiveTenantId);
        var other = await RegisterMemberAsync(factory, Guid.NewGuid());
        using var otherClient = other.Client;

        var memberships = await client
            .GetFromJsonAsync<TenantMembershipOption[]>(
                "/api/tenant-memberships");

        Assert.NotNull(memberships);
        Assert.Equal(2, memberships.Length);
        Assert.Equal(
            new[] { activeTenantId, selectedTenantId }.Order(),
            memberships.Select(membership => membership.TenantId));
        Assert.Single(
            memberships,
            membership => membership.TenantId == selectedTenantId
                && membership.IsSelected);
        Assert.DoesNotContain(
            memberships,
            membership => membership.TenantId == inactiveTenantId
                || membership.TenantId == other.TenantId);
    }

    [Fact]
    public async Task Selecting_an_unheld_or_inactive_membership_is_rejected()
    {
        var member = await RegisterMemberAsync(factory);
        using var client = member.Client;
        var inactiveTenantId = Guid.NewGuid();
        await ProvisionMembershipAsync(member.Email, inactiveTenantId);
        await DeactivateMembershipAsync(member.UserId, inactiveTenantId);
        var other = await RegisterMemberAsync(factory, Guid.NewGuid());
        using var otherClient = other.Client;
        var rejectedTenantIds = new[]
        {
            Guid.NewGuid(),
            inactiveTenantId,
            other.TenantId,
        };

        foreach (var tenantId in rejectedTenantIds)
        {
            var response = await client.PutAsJsonAsync(
                "/api/tenant-membership",
                new { tenantId });
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        var stillAuthorized = await client.GetAsync("/api/work-items");
        Assert.Equal(HttpStatusCode.OK, stillAuthorized.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<FeatureLabDbContext>();
        var storedUser = await dbContext.Users
            .AsNoTracking()
            .SingleAsync(user => user.Id == member.UserId);
        Assert.Equal(member.TenantId, storedUser.TenantId);
        Assert.Equal(member.SecurityStamp, storedUser.SecurityStamp);
        Assert.Equal(member.ConcurrencyStamp, storedUser.ConcurrencyStamp);
    }

    [Fact]
    public async Task Selecting_the_current_membership_is_idempotent()
    {
        var member = await RegisterMemberAsync(factory);
        using var client = member.Client;

        var response = await client.PutAsJsonAsync(
            "/api/tenant-membership",
            new { tenantId = member.TenantId });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.GetAsync("/api/work-items")).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<FeatureLabDbContext>();
        var storedUser = await dbContext.Users
            .AsNoTracking()
            .SingleAsync(user => user.Id == member.UserId);
        Assert.Equal(member.SecurityStamp, storedUser.SecurityStamp);
        Assert.Equal(member.ConcurrencyStamp, storedUser.ConcurrencyStamp);
    }

    [Fact]
    public async Task Empty_membership_selection_is_rejected()
    {
        var member = await RegisterMemberAsync(factory);
        using var client = member.Client;

        var response = await client.PutAsJsonAsync(
            "/api/tenant-membership",
            new { tenantId = Guid.Empty });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.GetAsync("/api/work-items")).StatusCode);
    }

    [Fact]
    public async Task Unscoped_identity_can_select_a_remaining_membership()
    {
        var member = await RegisterMemberAsync(factory);
        using var selectedClient = member.Client;
        var remainingTenantId = Guid.NewGuid();
        await ProvisionMembershipAsync(member.Email, remainingTenantId);
        var removed = await selectedClient.DeleteAsync(
            "/api/tenant-membership");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        using var unscopedClient = await SignInAsync(factory, member.Email);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await unscopedClient.GetAsync("/api/work-items")).StatusCode);

        var memberships = await unscopedClient
            .GetFromJsonAsync<TenantMembershipOption[]>(
                "/api/tenant-memberships");
        Assert.NotNull(memberships);
        var remaining = Assert.Single(memberships);
        Assert.Equal(remainingTenantId, remaining.TenantId);
        Assert.False(remaining.IsSelected);

        var switched = await unscopedClient.PutAsJsonAsync(
            "/api/tenant-membership",
            new { tenantId = remainingTenantId });
        Assert.Equal(HttpStatusCode.NoContent, switched.StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await unscopedClient.GetAsync("/api/tenant-memberships"))
                .StatusCode);

        using var freshClient = await SignInAsync(factory, member.Email);
        Assert.Equal(
            HttpStatusCode.OK,
            (await freshClient.GetAsync("/api/work-items")).StatusCode);
    }

    [Fact]
    public async Task Unscoped_identity_without_memberships_lists_empty()
    {
        using var client = factory.CreateClient();
        var email = $"unscoped-member-{Guid.NewGuid():N}@example.test";
        const string password = "FeatureLab!123";
        var registration = await client.PostAsJsonAsync(
            "/account/register",
            new { email, password });
        registration.EnsureSuccessStatusCode();
        var login = await client.PostAsJsonAsync(
            "/account/login",
            new { email, password });
        login.EnsureSuccessStatusCode();
        var tokens = await login.Content
            .ReadFromJsonAsync<LoginTokenResponse>();
        Assert.NotNull(tokens);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var memberships = await client
            .GetFromJsonAsync<TenantMembershipOption[]>(
                "/api/tenant-memberships");

        Assert.NotNull(memberships);
        Assert.Empty(memberships);
    }

    [Fact]
    public async Task A_stale_token_cannot_switch_again()
    {
        var member = await RegisterMemberAsync(factory);
        using var client = member.Client;
        var secondTenantId = Guid.NewGuid();
        var thirdTenantId = Guid.NewGuid();
        await ProvisionMembershipAsync(member.Email, secondTenantId);
        await ProvisionMembershipAsync(member.Email, thirdTenantId);
        var firstSwitch = await client.PutAsJsonAsync(
            "/api/tenant-membership",
            new { tenantId = secondTenantId });
        Assert.Equal(HttpStatusCode.NoContent, firstSwitch.StatusCode);

        var staleSwitch = await client.PutAsJsonAsync(
            "/api/tenant-membership",
            new { tenantId = thirdTenantId });

        Assert.Equal(HttpStatusCode.Forbidden, staleSwitch.StatusCode);
        using var freshClient = await SignInAsync(factory, member.Email);
        var memberships = await freshClient
            .GetFromJsonAsync<TenantMembershipOption[]>(
                "/api/tenant-memberships");
        Assert.NotNull(memberships);
        Assert.Contains(
            memberships,
            membership => membership.TenantId == secondTenantId
                && membership.IsSelected);
    }

    [Fact]
    public async Task Concurrent_switches_commit_one_selector()
    {
        var member = await RegisterMemberAsync(factory);
        using var firstClient = member.Client;
        using var secondClient = await SignInAsync(factory, member.Email);
        var secondTenantId = Guid.NewGuid();
        var thirdTenantId = Guid.NewGuid();
        await ProvisionMembershipAsync(member.Email, secondTenantId);
        await ProvisionMembershipAsync(member.Email, thirdTenantId);

        var attempts = await Task.WhenAll(
            firstClient.PutAsJsonAsync(
                "/api/tenant-membership",
                new { tenantId = secondTenantId }),
            secondClient.PutAsJsonAsync(
                "/api/tenant-membership",
                new { tenantId = thirdTenantId }));

        Assert.Single(
            attempts,
            response => response.StatusCode == HttpStatusCode.NoContent);
        Assert.Single(
            attempts,
            response => response.StatusCode is HttpStatusCode.Forbidden
                or HttpStatusCode.Conflict);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<FeatureLabDbContext>();
        var selectedTenantId = await dbContext.Users
            .Where(user => user.Id == member.UserId)
            .Select(user => user.TenantId)
            .SingleAsync();
        Assert.Contains(selectedTenantId, new[]
        {
            secondTenantId,
            thirdTenantId,
        });
        var memberships = await dbContext.TenantMemberships
            .Where(membership => membership.UserId == member.UserId)
            .ToArrayAsync();
        Assert.Equal(3, memberships.Length);
        Assert.All(memberships, membership =>
        {
            Assert.True(membership.IsActive);
            Assert.Equal(1, membership.Version);
        });

        using var freshClient = await SignInAsync(factory, member.Email);
        var freshMemberships = await freshClient
            .GetFromJsonAsync<TenantMembershipOption[]>(
                "/api/tenant-memberships");
        Assert.NotNull(freshMemberships);
        Assert.Single(
            freshMemberships,
            membership => membership.TenantId == selectedTenantId
                && membership.IsSelected);
    }

    [Fact]
    public async Task Concurrent_store_selections_report_one_conflict()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"feature-lab-selection-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Pooling=False";
        var userId = Guid.NewGuid().ToString("N");
        var initialTenantId = Guid.NewGuid();
        var secondTenantId = Guid.NewGuid();
        var thirdTenantId = Guid.NewGuid();
        var securityStamp = Guid.NewGuid().ToString("N");

        try
        {
            var seedOptions = new DbContextOptionsBuilder<FeatureLabDbContext>()
                .UseSqlite(connectionString)
                .Options;
            await using (var seed = CreateDbContext(
                seedOptions,
                initialTenantId))
            {
                await seed.Database.EnsureCreatedAsync();
                seed.Users.Add(new FeatureLabUser
                {
                    Id = userId,
                    UserName = $"member-{Guid.NewGuid():N}",
                    NormalizedUserName = $"MEMBER-{Guid.NewGuid():N}",
                    Email = $"member-{Guid.NewGuid():N}@example.test",
                    NormalizedEmail = $"MEMBER-{Guid.NewGuid():N}@EXAMPLE.TEST",
                    TenantId = initialTenantId,
                    SecurityStamp = securityStamp,
                    ConcurrencyStamp = Guid.NewGuid().ToString("N"),
                });
                seed.TenantMemberships.AddRange(
                    TenantMembershipRecord.Create(
                        userId,
                        initialTenantId,
                        TenantMembershipRole.Owner,
                        DateTimeOffset.UtcNow),
                    TenantMembershipRecord.Create(
                        userId,
                        secondTenantId,
                        TenantMembershipRole.Owner,
                        DateTimeOffset.UtcNow),
                    TenantMembershipRecord.Create(
                        userId,
                        thirdTenantId,
                        TenantMembershipRole.Owner,
                        DateTimeOffset.UtcNow));
                await seed.SaveChangesAsync();
            }

            var barrier = new SelectionSaveBarrier();
            var racingOptions = new DbContextOptionsBuilder<FeatureLabDbContext>()
                .UseSqlite(connectionString)
                .AddInterceptors(barrier)
                .Options;
            await using var firstContext = CreateDbContext(
                racingOptions,
                initialTenantId);
            await using var secondContext = CreateDbContext(
                racingOptions,
                initialTenantId);
            var firstStore = new EfTenantMembershipStore(
                firstContext,
                TimeProvider.System);
            var secondStore = new EfTenantMembershipStore(
                secondContext,
                TimeProvider.System);

            var results = await Task.WhenAll(
                firstStore.SelectAsync(
                    userId,
                    securityStamp,
                    secondTenantId),
                secondStore.SelectAsync(
                    userId,
                    securityStamp,
                    thirdTenantId));

            Assert.Single(
                results,
                result => result == SelectTenantMembershipResult.Selected);
            Assert.Single(
                results,
                result => result == SelectTenantMembershipResult.Conflict);
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    [Fact]
    public async Task Store_conflict_is_mapped_to_http_conflict()
    {
        using var conflictFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ITenantMembershipStore>();
                services.AddScoped<ITenantMembershipStore,
                    ConflictTenantMembershipStore>();
            }));
        using var client = conflictFactory.CreateClient();
        var email = $"conflict-member-{Guid.NewGuid():N}@example.test";
        const string password = "FeatureLab!123";
        var registration = await client.PostAsJsonAsync(
            "/account/register",
            new { email, password });
        registration.EnsureSuccessStatusCode();
        var login = await client.PostAsJsonAsync(
            "/account/login",
            new { email, password });
        login.EnsureSuccessStatusCode();
        var tokens = await login.Content
            .ReadFromJsonAsync<LoginTokenResponse>();
        Assert.NotNull(tokens);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.PutAsJsonAsync(
            "/api/tenant-membership",
            new { tenantId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_requests_cannot_list_or_select_memberships()
    {
        using var client = factory.CreateClient();

        var list = await client.GetAsync("/api/tenant-memberships");
        var select = await client.PutAsJsonAsync(
            "/api/tenant-membership",
            new { tenantId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, select.StatusCode);
    }

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
        WebApplicationFactory<Program> factory,
        Guid? tenantId = null)
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

        var assignedTenantId = tenantId ?? Guid.NewGuid();
        await TenantTestData.ProvisionAsync(
            factory.Services,
            email,
            assignedTenantId);

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
            assignedTenantId,
            Assert.IsType<string>(user.SecurityStamp),
            Assert.IsType<string>(user.ConcurrencyStamp));
    }

    private async Task ProvisionMembershipAsync(
        string email,
        Guid tenantId) =>
        await TenantTestData.ProvisionAsync(
            factory.Services,
            email,
            tenantId,
            select: false);

    private async Task DeactivateMembershipAsync(
        string userId,
        Guid tenantId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<FeatureLabDbContext>();
        var membership = await dbContext.TenantMemberships.SingleAsync(
            membership => membership.UserId == userId
                && membership.TenantId == tenantId);
        membership.Remove(DateTimeOffset.UtcNow);
        await dbContext.SaveChangesAsync();
    }

    private async Task SeedWorkItemAsync(
        string userId,
        Guid tenantId,
        string title)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<FeatureLabDbContext>();
        dbContext.WorkItems.Add(
            WorkItem.Create(
                title,
                userId,
                tenantId,
                TimeProvider.System));
        await dbContext.SaveChangesAsync();
    }

    private static FeatureLabDbContext CreateDbContext(
        DbContextOptions<FeatureLabDbContext> options,
        Guid tenantId)
    {
        var tenant = new TenantContext();
        tenant.Set(tenantId);
        return new FeatureLabDbContext(options, tenant);
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

    private sealed class SelectionSaveBarrier : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;

        public override async ValueTask<InterceptionResult<int>>
            SavingChangesAsync(
                DbContextEventData eventData,
                InterceptionResult<int> result,
                CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _arrivals) == 2)
            {
                _release.TrySetResult();
            }

            await _release.Task.WaitAsync(
                TimeSpan.FromSeconds(10),
                cancellationToken);
            return result;
        }
    }

    private sealed class ConflictTenantMembershipStore
        : ITenantMembershipStore
    {
        public Task<IReadOnlyList<TenantMembershipOption>?> ListActiveAsync(
            string userId,
            string securityStamp,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TenantMembershipOption>?>([]);

        public Task<bool> IsActiveAsync(
            string userId,
            Guid tenantId,
            string securityStamp,
            long membershipVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> IsActiveOwnerAsync(
            string userId,
            Guid tenantId,
            string securityStamp,
            long membershipVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<long?> GetActiveVersionAsync(
            string userId,
            Guid tenantId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<long?>(null);

        public Task<bool> RemoveAsync(
            string userId,
            Guid tenantId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<SelectTenantMembershipResult> SelectAsync(
            string userId,
            string securityStamp,
            Guid tenantId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(SelectTenantMembershipResult.Conflict);
    }
}
