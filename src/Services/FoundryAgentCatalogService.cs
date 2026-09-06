using Azure.AI.Projects;
using Azure.AI.Projects.Agents;
using Azure.Core;
using image_search_demo.Models;

namespace image_search_demo.Services;

public sealed class FoundryAgentCatalogService(TokenCredential credential)
{
    public async Task<IReadOnlyList<FoundryAgentDto>> GetAgentsAsync(
        ProjectSelection project,
        CancellationToken cancellationToken)
    {
        var client = new AIProjectClient(new Uri(project.ProjectEndpoint), credential);
        var agents = new List<FoundryAgentDto>();

        await foreach (var agent in client.AgentAdministrationClient.GetAgentsAsync(
                           limit: 100,
                           order: AgentListOrder.Ascending,
                           cancellationToken: cancellationToken))
        {
            var versions = new List<string>();
            string? description = null;
            await foreach (var version in client.AgentAdministrationClient.GetAgentVersionsAsync(
                               agent.Name,
                               limit: 100,
                               order: AgentListOrder.Descending,
                               cancellationToken: cancellationToken))
            {
                versions.Add(version.Version);
                description ??= version.Description;
            }

            agents.Add(new FoundryAgentDto(agent.Name, description, versions));
        }

        return agents;
    }
}
