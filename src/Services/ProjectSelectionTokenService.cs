using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using image_search_demo.Models;
using Microsoft.AspNetCore.DataProtection;

namespace image_search_demo.Services;

public sealed partial class ProjectSelectionTokenService(IDataProtectionProvider dataProtectionProvider)
{
    private readonly ITimeLimitedDataProtector _protector = dataProtectionProvider
        .CreateProtector("FoundryChat.ProjectSelection.v1")
        .ToTimeLimitedDataProtector();

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9._-]{0,126}[A-Za-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(
        @"^/subscriptions/[0-9a-f-]+/resourceGroups/[^/]+/providers/Microsoft\.CognitiveServices/accounts/[^/]+/projects/[^/]+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProjectResourceIdPattern();

    public string Protect(ProjectSelection selection, ClaimsPrincipal user)
    {
        ValidateSelection(selection);
        var state = new ProjectSelectionState(UserIdentity.GetId(user), selection);
        return _protector.Protect(JsonSerializer.Serialize(state), TimeSpan.FromHours(8));
    }

    public ProjectSelection ResolveProject(string token, ClaimsPrincipal user)
    {
        ProjectSelectionState? state;
        try
        {
            state = JsonSerializer.Deserialize<ProjectSelectionState>(_protector.Unprotect(token));
        }
        catch (CryptographicException)
        {
            throw new ArgumentException("Project の選択情報が無効または期限切れです。", nameof(token));
        }
        catch (JsonException)
        {
            throw new ArgumentException("Project の選択情報が無効です。", nameof(token));
        }

        if (state is null || state.UserId != UserIdentity.GetId(user))
        {
            throw new ArgumentException("Project の選択情報が現在のユーザーと一致しません。", nameof(token));
        }

        ValidateSelection(state.Project);
        return state.Project;
    }

    public ResolvedAgentConnection ResolveConnection(AgentConnectionRequest request, ClaimsPrincipal user)
    {
        var project = ResolveProject(request.ProjectToken, user);
        var agentName = request.AgentName.Trim();
        ValidateIdentifier(agentName, nameof(request.AgentName), 128);

        var version = request.AgentVersion?.Trim();
        if (!string.IsNullOrWhiteSpace(version))
        {
            ValidateIdentifier(version, nameof(request.AgentVersion), 64);
        }

        return new ResolvedAgentConnection(project, agentName, version);
    }

    public static string GetConnectionKey(ResolvedAgentConnection connection) =>
        string.Join(
            '\n',
            connection.Project.ProjectEndpoint,
            connection.AgentName,
            connection.AgentVersion ?? "");

    internal static void ValidateSelection(ProjectSelection selection)
    {
        if (!Uri.TryCreate(selection.ProjectEndpoint, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps ||
            endpoint.Port != 443 ||
            !endpoint.Host.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase) ||
            !endpoint.AbsolutePath.StartsWith("/api/projects/", StringComparison.OrdinalIgnoreCase) ||
            endpoint.AbsolutePath.Length <= "/api/projects/".Length ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new ArgumentException("Foundry Project Endpoint の形式が正しくありません。");
        }

        ValidateIdentifier(selection.ResourceName, nameof(selection.ResourceName), 128);
        ValidateIdentifier(selection.ProjectName, nameof(selection.ProjectName), 128);
        if (!ProjectResourceIdPattern().IsMatch(selection.ProjectResourceId) ||
            !selection.ProjectResourceId.StartsWith(selection.ResourceId + "/projects/", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Foundry Project Resource ID の形式が正しくありません。");
        }
    }

    private static void ValidateIdentifier(string value, string parameterName, int maximumLength)
    {
        if (value.Length is 0 || value.Length > maximumLength || !IdentifierPattern().IsMatch(value))
        {
            throw new ArgumentException("接続設定の形式が正しくありません。", parameterName);
        }
    }

    private sealed record ProjectSelectionState(string UserId, ProjectSelection Project);
}

public static class UserIdentity
{
    public static string GetId(ClaimsPrincipal user) =>
        user.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier") ??
        user.FindFirstValue(ClaimTypes.NameIdentifier) ??
        throw new InvalidOperationException("Authenticated user identifier is unavailable.");
}
