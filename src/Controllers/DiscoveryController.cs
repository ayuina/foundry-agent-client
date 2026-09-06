using System.ClientModel;
using image_search_demo.Models;
using image_search_demo.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web;

namespace image_search_demo.Controllers;

[Authorize]
[ApiController]
[Route("api/discovery")]
public sealed class DiscoveryController(
    IFoundryResourceDiscoveryService resourceDiscovery,
    FoundryAgentCatalogService agentCatalog,
    ProjectSelectionTokenService projectTokens,
    ILogger<DiscoveryController> logger) : ControllerBase
{
    [HttpGet("projects")]
    [AuthorizeForScopes(Scopes = ["https://management.azure.com/user_impersonation"])]
    public async Task<ActionResult<IReadOnlyList<FoundryResourceDto>>> GetProjects(
        CancellationToken cancellationToken)
    {
        var projects = await resourceDiscovery.GetProjectsAsync(User, cancellationToken);
        var resources = projects
            .GroupBy(
                project => new
                {
                    project.ResourceId,
                    project.ResourceName,
                    project.SubscriptionId,
                    project.SubscriptionName,
                    project.ResourceGroupName
                })
            .Select(group => new FoundryResourceDto(
                group.Key.ResourceId,
                group.Key.ResourceName,
                group.Key.SubscriptionId,
                group.Key.SubscriptionName,
                group.Key.ResourceGroupName,
                group.Select(project => new FoundryProjectDto(
                        project.ProjectResourceId,
                        project.ProjectName,
                        project.ProjectName,
                        projectTokens.Protect(project, User)))
                    .ToArray()))
            .ToArray();
        return Ok(resources);
    }

    [HttpGet("agents")]
    public async Task<ActionResult<IReadOnlyList<FoundryAgentDto>>> GetAgents(
        [FromQuery] string projectToken,
        CancellationToken cancellationToken)
    {
        try
        {
            var project = projectTokens.ResolveProject(projectToken, User);
            if (!await resourceDiscovery.CanAccessProjectAsync(project, User, cancellationToken))
            {
                return Forbid();
            }

            return Ok(await agentCatalog.GetAgentsAsync(project, cancellationToken));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new ProblemDetails { Title = "Project の選択が無効です。", Detail = exception.Message });
        }
        catch (Azure.RequestFailedException exception)
        {
            logger.LogWarning(exception, "Unable to list Foundry agents. Status: {Status}", exception.Status);
            return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
            {
                Title = "Agent の一覧を取得できませんでした。",
                Detail = "Web アプリの Identity に対象 Project の Foundry User 権限があることを確認してください。"
            });
        }
        catch (ClientResultException exception)
        {
            logger.LogWarning(exception, "Unable to list Foundry agents. Status: {Status}", exception.Status);
            return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
            {
                Title = "Agent の一覧を取得できませんでした。",
                Detail = "Web アプリの Identity に対象 Project の Foundry User 権限があることを確認してください。"
            });
        }
    }
}
