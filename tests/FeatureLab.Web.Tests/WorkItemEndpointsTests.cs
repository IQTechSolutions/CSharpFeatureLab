using System.Net;
using System.Net.Http.Json;
using FeatureLab.Features.WorkItems;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FeatureLab.Web.Tests;

public sealed class WorkItemEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public WorkItemEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Create_returns_the_new_work_item()
    {
        var response = await _client.PostAsJsonAsync("/api/work-items", new
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
        var response = await _client.PostAsJsonAsync("/api/work-items", new
        {
            title = "  ",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_contains_a_created_work_item()
    {
        await _client.PostAsJsonAsync("/api/work-items", new
        {
            title = "Prove the vertical slice",
        });

        var items = await _client.GetFromJsonAsync<WorkItemResponse[]>("/api/work-items");

        Assert.NotNull(items);
        Assert.Contains(items, item => item.Title == "Prove the vertical slice");
    }
}

