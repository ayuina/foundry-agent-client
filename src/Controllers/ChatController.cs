using System.ClientModel;
using System.Security.Cryptography;
using System.Text.Json;
using image_search_demo.Models;
using image_search_demo.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;

namespace image_search_demo.Controllers;

[Authorize]
[ApiController]
[Route("api/chat")]
public sealed class ChatController(
    IFoundryAgentGateway gateway,
    IFoundryResourceDiscoveryService resourceDiscovery,
    ProjectSelectionTokenService projectTokens,
    BlobCitationService blobCitations,
    IDataProtectionProvider dataProtectionProvider,
    ILogger<ChatController> logger) : ControllerBase
{
    private const int MaxInlineImageBytes = 8 * 1024 * 1024;
    private const int MaxDownloadBytes = 50 * 1024 * 1024;
    private readonly ITimeLimitedDataProtector _conversationProtector = dataProtectionProvider
        .CreateProtector("FoundryChat.Conversation.v2")
        .ToTimeLimitedDataProtector();
    private readonly ITimeLimitedDataProtector _assetProtector = dataProtectionProvider
        .CreateProtector("FoundryChat.Asset.v2")
        .ToTimeLimitedDataProtector();

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<ActionResult<ChatResponse>> Send(
        ChatRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        try
        {
            var connection = projectTokens.ResolveConnection(request, User);
            if (!await resourceDiscovery.CanAccessProjectAsync(connection.Project, User, cancellationToken))
            {
                return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
                {
                    Title = "この Foundry Project へのアクセス権がありません。",
                    Detail = "接続先を再読み込みして、アクセス可能な Project を選択してください。"
                });
            }

            var userId = UserIdentity.GetId(User);
            var connectionKey = ProjectSelectionTokenService.GetConnectionKey(connection);
            string? conversationId = null;

            if (!string.IsNullOrWhiteSpace(request.ConversationToken))
            {
                var state = Unprotect<ConversationState>(_conversationProtector, request.ConversationToken);
                if (state is null || state.UserId != userId || state.ConnectionKey != connectionKey)
                {
                    return ExpiredConversation();
                }

                conversationId = state.ConversationId;
            }

            var reply = await gateway.SendAsync(connection, request.Message.Trim(), conversationId, cancellationToken);
            var token = Protect(
                _conversationProtector,
                new ConversationState(userId, connectionKey, reply.ConversationId, connection),
                TimeSpan.FromHours(2));
            var citations = reply.Citations
                .Select(citation => blobCitations.CreateCitation(
                    citation,
                    User,
                    blobToken => Url.Action(nameof(GetBlobCitation), "Chat", new { token = blobToken })!))
                .ToArray();
            var assets = reply.Assets.Select(asset => CreateAssetDto(userId, connection, asset)).ToArray();

            return Ok(new ChatResponse(reply.Text, token, citations, assets));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new ProblemDetails { Title = "接続設定が正しくありません。", Detail = exception.Message });
        }
        catch (Azure.RequestFailedException exception)
        {
            return FoundryFailure(exception, exception.Status);
        }
        catch (ClientResultException exception)
        {
            return FoundryFailure(exception, exception.Status);
        }
        catch (FoundryAgentException exception)
        {
            logger.LogWarning(exception, "Foundry response did not complete.");
            return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
            {
                Title = "Foundry Agent が回答を完了できませんでした。",
                Detail = "エージェントの状態を確認して、もう一度お試しください。"
            });
        }
    }

    [HttpPost("clear")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Clear(
        ClearConversationRequest request,
        CancellationToken cancellationToken)
    {
        ResolvedAgentConnection connection;
        try
        {
            connection = projectTokens.ResolveConnection(request, User);
        }
        catch (ArgumentException)
        {
            return NoContent();
        }

        var state = Unprotect<ConversationState>(_conversationProtector, request.ConversationToken);
        if (state is null ||
            state.UserId != UserIdentity.GetId(User) ||
            state.ConnectionKey != ProjectSelectionTokenService.GetConnectionKey(connection))
        {
            return NoContent();
        }

        await gateway.DeleteConversationAsync(state.Connection, state.ConversationId, cancellationToken);
        return NoContent();
    }

    [HttpGet("assets/{token}")]
    public async Task<IActionResult> GetAsset(string token, CancellationToken cancellationToken)
    {
        var state = Unprotect<AssetState>(_assetProtector, token);
        if (state is null || state.UserId != UserIdentity.GetId(User))
        {
            return NotFound();
        }

        var content = await gateway.DownloadContainerFileAsync(
            state.Connection,
            state.ContainerId,
            state.FileId,
            cancellationToken);
        if (content.Length > MaxDownloadBytes)
        {
            logger.LogWarning("Rejected generated file {FileName} because it exceeds the download limit.", state.Name);
            return StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        Response.Headers.XContentTypeOptions = "nosniff";
        return state.ContentType.StartsWith("image/", StringComparison.Ordinal)
            ? File(content, state.ContentType)
            : File(content, state.ContentType, state.Name);
    }

    [HttpGet("citations/{token}")]
    public async Task<IActionResult> GetBlobCitation(string token, CancellationToken cancellationToken)
    {
        try
        {
            var download = await blobCitations.DownloadAsync(token, User, cancellationToken);
            if (download is null)
            {
                return NotFound();
            }

            Response.Headers.XContentTypeOptions = "nosniff";
            Response.Headers.ContentSecurityPolicy = "sandbox";
            return download.Inline
                ? File(download.Content, download.ContentType, enableRangeProcessing: true)
                : File(download.Content, download.ContentType, download.FileName);
        }
        catch (BlobCitationTooLargeException)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge);
        }
        catch (Azure.RequestFailedException exception) when (exception.Status == StatusCodes.Status404NotFound)
        {
            return NotFound();
        }
        catch (Azure.RequestFailedException exception)
        {
            logger.LogWarning(exception, "Unable to retrieve Blob citation. Status: {Status}", exception.Status);
            return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
            {
                Title = "引用ドキュメントを取得できませんでした。",
                Detail = "Web アプリの Identity に Blob の読み取り権限があることを確認してください。"
            });
        }
    }

    private AssetDto CreateAssetDto(
        string userId,
        ResolvedAgentConnection connection,
        GeneratedAsset asset)
    {
        if (asset.Content is not null)
        {
            if (asset.Content.Length > MaxInlineImageBytes)
            {
                throw new FoundryAgentException("Generated image exceeds the inline response limit.");
            }

            var dataUrl = $"data:{asset.ContentType};base64,{Convert.ToBase64String(asset.Content)}";
            return new AssetDto(asset.Name, dataUrl, true, asset.ContentType);
        }

        if (asset.ContainerId is null || asset.FileId is null)
        {
            throw new FoundryAgentException("Generated asset does not contain a retrievable file reference.");
        }

        var token = Protect(
            _assetProtector,
            new AssetState(
                userId,
                connection,
                asset.Name,
                asset.ContentType,
                asset.ContainerId,
                asset.FileId),
            TimeSpan.FromMinutes(30));
        return new AssetDto(
            asset.Name,
            Url.Action(nameof(GetAsset), "Chat", new { token })!,
            asset.ContentType.StartsWith("image/", StringComparison.Ordinal),
            asset.ContentType);
    }

    private ObjectResult FoundryFailure(Exception exception, int status)
    {
        logger.LogWarning(exception, "Foundry request failed with status {Status}.", status);
        return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
        {
            Title = "Foundry Agent への接続に失敗しました。",
            Detail = "接続設定、権限、エージェントの状態を確認してください。"
        });
    }

    private ConflictObjectResult ExpiredConversation() =>
        Conflict(new ProblemDetails
        {
            Title = "会話の有効期限が切れました。",
            Detail = "新しい会話としてもう一度送信してください。"
        });

    private static string Protect<T>(
        ITimeLimitedDataProtector protector,
        T value,
        TimeSpan lifetime) =>
        protector.Protect(JsonSerializer.Serialize(value), lifetime);

    private static T? Unprotect<T>(ITimeLimitedDataProtector protector, string token)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(protector.Unprotect(token));
        }
        catch (CryptographicException)
        {
            return default;
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private sealed record ConversationState(
        string UserId,
        string ConnectionKey,
        string ConversationId,
        ResolvedAgentConnection Connection);

    private sealed record AssetState(
        string UserId,
        ResolvedAgentConnection Connection,
        string Name,
        string ContentType,
        string ContainerId,
        string FileId);
}
