using image_search_demo.Models;

namespace image_search_demo.Services;

public interface IFoundryAgentGateway
{
    Task<AgentReply> SendAsync(
        ResolvedAgentConnection connection,
        string message,
        string? conversationId,
        CancellationToken cancellationToken);

    Task DeleteConversationAsync(
        ResolvedAgentConnection connection,
        string conversationId,
        CancellationToken cancellationToken);

    Task<byte[]> DownloadContainerFileAsync(
        ResolvedAgentConnection connection,
        string containerId,
        string fileId,
        CancellationToken cancellationToken);
}
