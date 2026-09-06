using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using image_search_demo.Models;
using Microsoft.Identity.Web;

namespace image_search_demo.Services;

public interface IFoundryResourceDiscoveryService
{
    Task<IReadOnlyList<ProjectSelection>> GetProjectsAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken);

    Task<bool> CanAccessProjectAsync(
        ProjectSelection project,
        ClaimsPrincipal user,
        CancellationToken cancellationToken);
}

public sealed class FoundryResourceDiscoveryService(
    HttpClient httpClient,
    ITokenAcquisition tokenAcquisition) : IFoundryResourceDiscoveryService
{
    private const string ArmScope = "https://management.azure.com/user_impersonation";

    public async Task<IReadOnlyList<ProjectSelection>> GetProjectsAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var accessToken = await tokenAcquisition.GetAccessTokenForUserAsync(
            [ArmScope],
            user: user);
        var subscriptions = await GetSubscriptionsAsync(accessToken, cancellationToken);

        if (subscriptions.Count == 0)
        {
            return [];
        }

        var subscriptionNames = subscriptions.ToDictionary(
            subscription => subscription.Id,
            subscription => subscription.Name,
            StringComparer.OrdinalIgnoreCase);
        var results = new List<ProjectSelection>();
        foreach (var subscriptionBatch in subscriptions.Chunk(1000))
        {
            string? skipToken = null;
            do
            {
                using var graphRequest = CreateRequest(
                    HttpMethod.Post,
                    "providers/Microsoft.ResourceGraph/resources?api-version=2024-04-01",
                    accessToken);
                graphRequest.Content = JsonContent.Create(new
                {
                    subscriptions = subscriptionBatch.Select(subscription => subscription.Id),
                    query = """
                    Resources
                    | where type =~ 'microsoft.cognitiveservices/accounts/projects'
                    | project id, name, resourceGroup, subscriptionId, properties
                    """,
                    options = new
                    {
                        resultFormat = "objectArray",
                        skipToken
                    }
                });

                using var graphResponse = await httpClient.SendAsync(graphRequest, cancellationToken);
                graphResponse.EnsureSuccessStatusCode();
                using var graphDocument = JsonDocument.Parse(
                    await graphResponse.Content.ReadAsStreamAsync(cancellationToken));
                var root = graphDocument.RootElement;

                foreach (var project in root.GetProperty("data").EnumerateArray())
                {
                    if (TryMapProject(project, subscriptionNames, out var selection))
                    {
                        results.Add(selection);
                    }
                }

                skipToken = root.TryGetProperty("$skipToken", out var tokenElement)
                    ? tokenElement.GetString()
                    : null;
            }
            while (!string.IsNullOrWhiteSpace(skipToken));
        }

        return results
            .DistinctBy(project => project.ProjectEndpoint, StringComparer.OrdinalIgnoreCase)
            .OrderBy(project => project.SubscriptionName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(project => project.ResourceName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(project => project.ProjectName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public async Task<bool> CanAccessProjectAsync(
        ProjectSelection project,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        ProjectSelectionTokenService.ValidateSelection(project);
        var accessToken = await tokenAcquisition.GetAccessTokenForUserAsync([ArmScope], user: user);
        using var request = CreateRequest(
            HttpMethod.Get,
            $"{project.ProjectResourceId.TrimStart('/')}?api-version=2026-07-01",
            accessToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    private async Task<IReadOnlyList<SubscriptionInfo>> GetSubscriptionsAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        var subscriptions = new List<SubscriptionInfo>();
        string? nextLink = "subscriptions?api-version=2022-12-01";
        while (!string.IsNullOrWhiteSpace(nextLink))
        {
            if (Uri.TryCreate(nextLink, UriKind.Absolute, out var absolute) &&
                (absolute.Scheme != Uri.UriSchemeHttps ||
                 !absolute.Host.Equals("management.azure.com", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("ARM returned an invalid subscriptions continuation URL.");
            }

            using var request = CreateRequest(HttpMethod.Get, nextLink, accessToken);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStreamAsync(cancellationToken));
            var root = document.RootElement;
            subscriptions.AddRange(root.GetProperty("value")
                .EnumerateArray()
                .Where(item => !item.TryGetProperty("state", out var state) ||
                               state.GetString() == "Enabled")
                .Select(item => new SubscriptionInfo(
                    item.GetProperty("subscriptionId").GetString()!,
                    item.GetProperty("displayName").GetString() ??
                    item.GetProperty("subscriptionId").GetString()!)));
            nextLink = root.TryGetProperty("nextLink", out var nextLinkElement)
                ? nextLinkElement.GetString()
                : null;
        }

        return subscriptions;
    }

    internal static bool TryMapProject(
        JsonElement project,
        IReadOnlyDictionary<string, string> subscriptionNames,
        out ProjectSelection selection)
    {
        selection = default!;
        var id = project.GetProperty("id").GetString();
        var subscriptionId = project.GetProperty("subscriptionId").GetString();
        var resourceGroup = project.GetProperty("resourceGroup").GetString();
        if (string.IsNullOrWhiteSpace(id) ||
            string.IsNullOrWhiteSpace(subscriptionId) ||
            string.IsNullOrWhiteSpace(resourceGroup))
        {
            return false;
        }

        var segments = id.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var accountIndex = Array.FindIndex(
            segments,
            segment => segment.Equals("accounts", StringComparison.OrdinalIgnoreCase));
        if (accountIndex < 0 ||
            accountIndex + 3 >= segments.Length ||
            !segments[accountIndex + 2].Equals("projects", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var projectName = segments[accountIndex + 3];
        var properties = project.GetProperty("properties");
        if (!properties.TryGetProperty("endpoints", out var endpoints) ||
            !endpoints.TryGetProperty("AI Foundry API", out var endpointElement))
        {
            return false;
        }

        var endpoint = endpointElement.GetString();
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return false;
        }

        var resourceId = string.Join('/', segments.Take(accountIndex + 2).Prepend(""));
        var candidate = new ProjectSelection(
            segments[accountIndex + 1],
            resourceId,
            projectName,
            id,
            endpoint,
            subscriptionId,
            subscriptionNames.GetValueOrDefault(subscriptionId, subscriptionId),
            resourceGroup);

        try
        {
            ProjectSelectionTokenService.ValidateSelection(candidate);
            selection = candidate;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string uri, string accessToken)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private sealed record SubscriptionInfo(string Id, string Name);
}
