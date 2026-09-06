using Azure;
using Azure.AI.Extensions.OpenAI;
using Azure.AI.Projects;
using Azure.Core;
using image_search_demo.Models;
using OpenAI.Responses;
using System.ClientModel.Primitives;

namespace image_search_demo.Services;

public sealed class FoundryAgentGateway(
    TokenCredential credential,
    ILogger<FoundryAgentGateway> logger) : IFoundryAgentGateway
{
    public async Task<AgentReply> SendAsync(
        ResolvedAgentConnection connection,
        string message,
        string? conversationId,
        CancellationToken cancellationToken)
    {
        var client = GetClient(connection);
        var openAiClient = client.ProjectOpenAIClient;

        if (string.IsNullOrWhiteSpace(conversationId))
        {
            var conversation = await openAiClient
                .GetProjectConversationsClient()
                .CreateProjectConversationAsync(cancellationToken: cancellationToken);
            conversationId = conversation.Value.Id;
        }

        var agentReference = string.IsNullOrWhiteSpace(connection.AgentVersion)
            ? new AgentReference(connection.AgentName)
            : new AgentReference(connection.AgentName, connection.AgentVersion);

        var responseClient = openAiClient.GetProjectResponsesClientForAgent(agentReference);
        var options = new CreateResponseOptions
        {
            AgentConversationId = conversationId,
            InputItems = { ResponseItem.CreateUserMessageItem(message) }
        };

        var responseResult = await responseClient.CreateResponseAsync(options, cancellationToken);
        var response = responseResult.Value;
        if (response.Status != ResponseStatus.Completed)
        {
            throw new FoundryAgentException($"Foundry response ended with status '{response.Status}'.");
        }

        return MapResponse(response, conversationId);
    }

    public async Task DeleteConversationAsync(
        ResolvedAgentConnection connection,
        string conversationId,
        CancellationToken cancellationToken)
    {
        var openAiClient = GetClient(connection).ProjectOpenAIClient;
        await openAiClient.GetConversationClient().DeleteConversationAsync(
            conversationId,
            new RequestOptions { CancellationToken = cancellationToken });
    }

    public async Task<byte[]> DownloadContainerFileAsync(
        ResolvedAgentConnection connection,
        string containerId,
        string fileId,
        CancellationToken cancellationToken)
    {
        var data = await GetClient(connection).ProjectOpenAIClient
            .GetContainerClient()
            .DownloadContainerFileAsync(containerId, fileId, cancellationToken);
        return data.Value.ToArray();
    }

    private AIProjectClient GetClient(ResolvedAgentConnection connection)
    {
        return new AIProjectClient(new Uri(connection.Project.ProjectEndpoint), credential);
    }

    private AgentReply MapResponse(ResponseResult response, string conversationId)
    {
        var citations = new List<CitationDto>();
        var assets = new List<GeneratedAsset>();

        foreach (var item in response.OutputItems)
        {
            if (item is MessageResponseItem messageItem)
            {
                MapMessageContent(messageItem, citations, assets);
            }

            if (item is ImageGenerationCallResponseItem imageItem)
            {
                assets.Add(new GeneratedAsset(
                    $"generated-{assets.Count + 1}.png",
                    "image/png",
                    imageItem.ImageResultBytes.ToArray()));
            }
        }

        var distinctCitations = citations
            .DistinctBy(citation => $"{citation.Kind}\n{citation.Title}\n{citation.OriginalUrl}", StringComparer.OrdinalIgnoreCase)
            .ToArray();

        logger.LogInformation(
            "Foundry response {ResponseId} completed with {CitationCount} citations and {AssetCount} assets.",
            response.Id,
            distinctCitations.Length,
            assets.Count);

        return new AgentReply(response.GetOutputText(), conversationId, distinctCitations, assets);
    }

    private static void MapMessageContent(
        MessageResponseItem messageItem,
        ICollection<CitationDto> citations,
        ICollection<GeneratedAsset> assets)
    {
        foreach (var content in messageItem.Content)
        {
            foreach (var annotation in content.OutputTextAnnotations)
            {
                switch (annotation)
                {
                    case UriCitationMessageAnnotation uri:
                        var safeUrl = TryGetSafeHttpsUrl(uri.Uri);
                        citations.Add(new CitationDto(
                            string.IsNullOrWhiteSpace(uri.Title) ? safeUrl ?? "参照リンク" : uri.Title,
                            safeUrl,
                            safeUrl,
                            IsImageUrl(safeUrl) ? "image" : "web"));
                        break;
                    case FileCitationMessageAnnotation file:
                        citations.Add(new CitationDto(
                            string.IsNullOrWhiteSpace(file.Filename) ? "引用ドキュメント" : file.Filename,
                            null,
                            null,
                            "file"));
                        break;
                    case ContainerFileCitationMessageAnnotation container:
                        var name = Path.GetFileName(container.Filename);
                        var contentType = GetContentType(name);
                        assets.Add(new GeneratedAsset(
                            name,
                            contentType,
                            ContainerId: container.ContainerId,
                            FileId: container.FileId));
                        break;
                }
            }
        }
    }

    internal static string? TryGetSafeHttpsUrl(Uri? value) =>
        value is { Scheme: "https", IsAbsoluteUri: true } ? value.AbsoluteUri : null;

    internal static bool IsImageUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return Path.GetExtension(uri.AbsolutePath).ToLowerInvariant() is
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".svg";
    }

    internal static string GetContentType(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".pdf" => "application/pdf",
            ".csv" => "text/csv",
            ".txt" => "text/plain",
            ".json" => "application/json",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            _ => "application/octet-stream"
        };
}
