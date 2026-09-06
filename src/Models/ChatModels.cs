using System.ComponentModel.DataAnnotations;

namespace image_search_demo.Models;

public record AgentConnectionRequest
{
    [Required, StringLength(8192)]
    public string ProjectToken { get; init; } = "";

    [Required, StringLength(128)]
    public string AgentName { get; init; } = "";

    [StringLength(64)]
    public string? AgentVersion { get; init; }
}

public sealed record ChatRequest : AgentConnectionRequest
{
    [Required, StringLength(8000, MinimumLength = 1)]
    public string Message { get; init; } = "";

    [StringLength(4096)]
    public string? ConversationToken { get; init; }
}

public sealed record ClearConversationRequest : AgentConnectionRequest
{
    [Required, StringLength(4096)]
    public string ConversationToken { get; init; } = "";
}

public sealed record ChatResponse(
    string Text,
    string ConversationToken,
    IReadOnlyList<CitationDto> Citations,
    IReadOnlyList<AssetDto> Assets);

public sealed record CitationDto(string Title, string? OriginalUrl, string? Href, string Kind);

public sealed record AssetDto(string Name, string Url, bool IsImage, string? ContentType);

public sealed record AgentReply(
    string Text,
    string ConversationId,
    IReadOnlyList<CitationDto> Citations,
    IReadOnlyList<GeneratedAsset> Assets);

public sealed record GeneratedAsset(
    string Name,
    string ContentType,
    byte[]? Content = null,
    string? ContainerId = null,
    string? FileId = null);

public sealed record ProjectSelection(
    string ResourceName,
    string ResourceId,
    string ProjectName,
    string ProjectResourceId,
    string ProjectEndpoint,
    string SubscriptionId,
    string SubscriptionName,
    string ResourceGroupName);

public sealed record ResolvedAgentConnection(
    ProjectSelection Project,
    string AgentName,
    string? AgentVersion);

public sealed record FoundryResourceDto(
    string Id,
    string Name,
    string SubscriptionId,
    string SubscriptionName,
    string ResourceGroupName,
    IReadOnlyList<FoundryProjectDto> Projects);

public sealed record FoundryProjectDto(
    string Id,
    string Name,
    string DisplayName,
    string ProjectToken);

public sealed record FoundryAgentDto(
    string Name,
    string? Description,
    IReadOnlyList<string> Versions);

public sealed record BlobProxyResult(
    Stream Content,
    string ContentType,
    string FileName,
    bool Inline);
