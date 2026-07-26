using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace FeatureLab.Client.Features.WorkItems;

public sealed class HttpWorkItemApi(HttpClient httpClient) : IWorkItemApi
{
    public async Task<CreateWorkItemResult> CreateAsync(
        string title,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/work-items")
        {
            Content = JsonContent.Create(new CreateWorkItemRequest(title)),
        };
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return CreateWorkItemResult.Failure();
        }

        using (response)
        {
            try
            {
                return await MapResponseAsync(response, cancellationToken);
            }
            catch (Exception exception)
                when (exception is JsonException or NotSupportedException)
            {
                return CreateWorkItemResult.Failure();
            }
        }
    }

    private static async Task<CreateWorkItemResult> MapResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.Created)
        {
            var workItem = await response.Content
                .ReadFromJsonAsync<WorkItemResponse>(cancellationToken);

            return workItem is null
                ? CreateWorkItemResult.Failure()
                : CreateWorkItemResult.Created(workItem.Id, workItem.Title);
        }

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            var problem = await response.Content
                .ReadFromJsonAsync<ValidationProblemResponse>(cancellationToken);

            return problem?.Errors is null
                ? CreateWorkItemResult.Failure()
                : CreateWorkItemResult.Validation(problem.Errors);
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            return CreateWorkItemResult.Forbidden();
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return CreateWorkItemResult.Unauthorized();
        }

        return CreateWorkItemResult.Failure();
    }

    private sealed record CreateWorkItemRequest(string Title);

    private sealed record WorkItemResponse(
        Guid Id,
        string Title,
        bool IsCompleted,
        DateTime CreatedAtUtc);

    private sealed record ValidationProblemResponse(
        Dictionary<string, string[]> Errors);
}
