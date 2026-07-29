using FeatureLab.Data;
using FeatureLab.Features.BackgroundJobs;
using FeatureLab.Features.Chat;
using FeatureLab.Identity;
using FeatureLab.Features.WorkItems;
using Hangfire;
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
builder.Services.AddHangfire(configuration =>
{
    configuration
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings();

    if (builder.Environment.IsDevelopment()
        || builder.Environment.IsEnvironment("Testing"))
    {
        configuration.UseInMemoryStorage();
        return;
    }

    var backgroundJobsConnectionString =
        builder.Configuration.GetConnectionString("BackgroundJobs")
        ?? throw new InvalidOperationException(
            "A SQL Server BackgroundJobs connection string is required outside Development and Testing.");

    configuration.UseSqlServerStorage(backgroundJobsConnectionString);
});
if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddHangfireServer();
}
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IWorkItemReportService, EfWorkItemReportService>();
builder.Services.AddScoped<WorkItemReportJob>();
builder.Services.AddSingleton<IWorkItemReportScheduler, HangfireWorkItemReportScheduler>();
builder.Services.AddScoped<IChatMessageStore, EfChatMessageStore>();
builder.Services.AddScoped<IWorkItemStore, EfWorkItemStore>();
builder.Services.AddProblemDetails();
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 4 * 1024;
    options.MaximumParallelInvocationsPerClient = 1;
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/api/about", () => Results.Ok(new
{
    application = "C# Feature Lab",
    lesson = "Move slow work into a reliable background job",
}));
app.MapGroup("/account").MapIdentityApi<FeatureLabUser>();
app.MapWorkItemEndpoints();
app.MapHub<ChatHub>(ChatHub.Route);
app.MapFallbackToFile("index.html");

if (app.Environment.IsEnvironment("Testing"))
{
    await using var scope = app.Services.CreateAsyncScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<FeatureLabDbContext>();
    await dbContext.Database.EnsureCreatedAsync();
}

app.Run();

public partial class Program;
