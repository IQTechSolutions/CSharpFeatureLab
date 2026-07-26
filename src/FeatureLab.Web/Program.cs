using FeatureLab.Data;
using FeatureLab.Identity;
using FeatureLab.Features.WorkItems;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseStaticWebAssets();

var connectionString = builder.Configuration.GetConnectionString("FeatureLab")
    ?? "Data Source=app.db";

builder.Services.AddDbContext<FeatureLabDbContext>(options =>
    options.UseSqlite(connectionString));
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(
        WorkItemAuthorization.CreatePolicy,
        policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim(
                WorkItemAuthorization.PermissionClaimType,
                WorkItemAuthorization.CreatePermission));
builder.Services.AddIdentityApiEndpoints<FeatureLabUser>()
    .AddEntityFrameworkStores<FeatureLabDbContext>();
builder.Services.AddScoped<IWorkItemStore, EfWorkItemStore>();
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/api/about", () => Results.Ok(new
{
    application = "C# Feature Lab",
    lesson = "Build a Blazor form that reports useful errors",
}));
app.MapGroup("/account").MapIdentityApi<FeatureLabUser>();
app.MapWorkItemEndpoints();
app.MapFallbackToFile("index.html");

if (app.Environment.IsEnvironment("Testing"))
{
    await using var scope = app.Services.CreateAsyncScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<FeatureLabDbContext>();
    await dbContext.Database.EnsureCreatedAsync();
}

app.Run();

public partial class Program;
