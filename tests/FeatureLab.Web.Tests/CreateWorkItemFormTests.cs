using Bunit;
using FeatureLab.Client.Features.WorkItems;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace FeatureLab.Web.Tests;

public sealed class CreateWorkItemFormTests : BunitContext
{
    [Fact]
    public async Task Submit_shows_the_created_work_item()
    {
        var api = new StubWorkItemApi(
            CreateWorkItemResult.Created(
                Guid.Parse("1dc18df2-3b2b-49da-8d09-9c3fd9fb05e4"),
                "Ship the Blazor form"));
        Services.AddSingleton<IWorkItemApi>(api);

        var form = Render<CreateWorkItemForm>();

        await form.Find("input[name=title]").ChangeAsync(
            new ChangeEventArgs { Value = "  Ship the Blazor form  " });
        await form.Find("form").SubmitAsync();

        form.WaitForAssertion(() =>
            Assert.Contains("Created “Ship the Blazor form”.", form.Markup));
        Assert.Equal("  Ship the Blazor form  ", api.SubmittedTitle);
    }

    [Fact]
    public async Task Submit_shows_the_title_error_returned_by_the_api()
    {
        var api = new StubWorkItemApi(
            CreateWorkItemResult.Validation(
                new Dictionary<string, string[]>
                {
                    ["Title"] = ["Title must contain 3 to 120 characters."],
                }));
        Services.AddSingleton<IWorkItemApi>(api);

        var form = Render<CreateWorkItemForm>();

        await form.Find("input[name=title]").ChangeAsync(
            new ChangeEventArgs { Value = "x" });
        await form.Find("form").SubmitAsync();

        form.WaitForAssertion(() =>
            Assert.Equal(
                "Title must contain 3 to 120 characters.",
                form.Find("[data-testid=title-error]").TextContent));
        Assert.Equal("true", form.Find("input[name=title]").GetAttribute("aria-invalid"));
    }

    [Fact]
    public async Task Submit_explains_when_the_user_is_forbidden()
    {
        var api = new StubWorkItemApi(CreateWorkItemResult.Forbidden());
        Services.AddSingleton<IWorkItemApi>(api);

        var form = Render<CreateWorkItemForm>();

        await form.Find("input[name=title]").ChangeAsync(
            new ChangeEventArgs { Value = "Create a protected item" });
        await form.Find("form").SubmitAsync();

        form.WaitForAssertion(() =>
            Assert.Equal(
                "You are signed in, but you do not have permission to create work items.",
                form.Find("[role=alert]").TextContent));
        Assert.Empty(form.FindAll("[data-testid=title-error]"));
    }

    [Fact]
    public async Task Submit_explains_when_the_user_must_sign_in()
    {
        var api = new StubWorkItemApi(CreateWorkItemResult.Unauthorized());
        Services.AddSingleton<IWorkItemApi>(api);

        var form = Render<CreateWorkItemForm>();

        await form.Find("input[name=title]").ChangeAsync(
            new ChangeEventArgs { Value = "Create after signing in" });
        await form.Find("form").SubmitAsync();

        form.WaitForAssertion(() =>
            Assert.Equal(
                "Sign in before creating a work item.",
                form.Find("[role=alert]").TextContent));
    }

    [Fact]
    public async Task Submit_shows_a_safe_message_when_the_request_fails()
    {
        var api = new StubWorkItemApi(CreateWorkItemResult.Failure());
        Services.AddSingleton<IWorkItemApi>(api);

        var form = Render<CreateWorkItemForm>();

        await form.Find("input[name=title]").ChangeAsync(
            new ChangeEventArgs { Value = "Keep this input" });
        await form.Find("form").SubmitAsync();

        form.WaitForAssertion(() =>
            Assert.Equal(
                "We could not create the work item. Try again.",
                form.Find("[role=alert]").TextContent));
        Assert.Equal(
            "Keep this input",
            form.Find("input[name=title]").GetAttribute("value"));
    }

    private sealed class StubWorkItemApi(CreateWorkItemResult result) : IWorkItemApi
    {
        public string? SubmittedTitle { get; private set; }

        public Task<CreateWorkItemResult> CreateAsync(
            string title,
            CancellationToken cancellationToken = default)
        {
            SubmittedTitle = title;
            return Task.FromResult(result);
        }
    }
}
