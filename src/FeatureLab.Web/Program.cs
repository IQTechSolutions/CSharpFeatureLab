using FeatureLab.Features.WorkItems;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IWorkItemStore, InMemoryWorkItemStore>();
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
app.MapGet("/", () => Results.Ok(new
{
    application = "C# Feature Lab",
    lesson = "Build a complete vertical slice",
}));
app.MapWorkItemEndpoints();

app.Run();

public partial class Program;

