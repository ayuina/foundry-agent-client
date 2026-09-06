using System.Security.Claims;
using System.Text.Json;
using image_search_demo.Models;
using image_search_demo.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace ImageSearchDemo.Tests;

public sealed class ProjectSelectionTests
{
    private readonly ProjectSelectionTokenService _tokens;
    private readonly ClaimsPrincipal _user = CreateUser("test-user");

    public ProjectSelectionTests()
    {
        var services = new ServiceCollection();
        services.AddDataProtection();
        _tokens = new ProjectSelectionTokenService(
            services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>());
    }

    [Fact]
    public void ProjectToken_RoundTripsForSameUser()
    {
        var selection = Selection();

        var token = _tokens.Protect(selection, _user);
        var resolved = _tokens.ResolveProject(token, _user);

        Assert.Equal(selection, resolved);
    }

    [Fact]
    public void ProjectToken_RejectsDifferentUser()
    {
        var token = _tokens.Protect(Selection(), _user);

        Assert.Throws<ArgumentException>(() =>
            _tokens.ResolveProject(token, CreateUser("different-user")));
    }

    [Fact]
    public void ProjectSelection_RejectsNonFoundryEndpoint()
    {
        var selection = Selection() with { ProjectEndpoint = "https://attacker.example/api/projects/demo" };

        Assert.Throws<ArgumentException>(() => ProjectSelectionTokenService.ValidateSelection(selection));
    }

    [Theory]
    [InlineData("https://example.com/image.png", true)]
    [InlineData("https://example.com/document.pdf", false)]
    [InlineData("http://example.com/image.png", false)]
    [InlineData("javascript:alert(1)", false)]
    public void ImageDetection_RequiresSafeHttpsImageUrl(string value, bool expected)
    {
        var safeUrl = Uri.TryCreate(value, UriKind.Absolute, out var uri)
            ? FoundryAgentGateway.TryGetSafeHttpsUrl(uri)
            : null;

        Assert.Equal(expected, FoundryAgentGateway.IsImageUrl(safeUrl));
    }

    [Fact]
    public void BlobCanonicalization_RemovesQueryAndRejectsOtherHosts()
    {
        Assert.True(BlobCitationService.TryCanonicalize(
            "https://storage123.blob.core.windows.net/docs/report.pdf?sv=secret",
            out var canonical));
        Assert.Equal("https://storage123.blob.core.windows.net/docs/report.pdf", canonical.AbsoluteUri);
        Assert.False(BlobCitationService.TryCanonicalize(
            "https://attacker.example/docs/report.pdf",
            out _));
    }

    [Fact]
    public void GeneratedSvg_IsServedAsDownload()
    {
        Assert.Equal("application/octet-stream", FoundryAgentGateway.GetContentType("untrusted.svg"));
    }

    [Fact]
    public void ResourceGraphProject_UsesProjectNameFromArmId()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "id": "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/demo-rg/providers/Microsoft.CognitiveServices/accounts/demo-foundry/projects/search-project",
              "name": "demo-foundry/search-project",
              "resourceGroup": "demo-rg",
              "subscriptionId": "11111111-1111-1111-1111-111111111111",
              "properties": {
                "endpoints": {
                  "AI Foundry API": "https://demo-foundry.services.ai.azure.com/api/projects/search-project"
                }
              }
            }
            """);

        var mapped = FoundryResourceDiscoveryService.TryMapProject(
            document.RootElement,
            new Dictionary<string, string>
            {
                ["11111111-1111-1111-1111-111111111111"] = "Demo Subscription"
            },
            out var selection);

        Assert.True(mapped);
        Assert.Equal("search-project", selection.ProjectName);
        Assert.Equal("demo-foundry", selection.ResourceName);
    }

    internal static ProjectSelection Selection() => new(
        "demo-foundry",
        "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/demo-rg/providers/Microsoft.CognitiveServices/accounts/demo-foundry",
        "search-project",
        "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/demo-rg/providers/Microsoft.CognitiveServices/accounts/demo-foundry/projects/search-project",
        "https://demo-foundry.services.ai.azure.com/api/projects/search-project",
        "11111111-1111-1111-1111-111111111111",
        "Demo Subscription",
        "demo-rg");

    internal static ClaimsPrincipal CreateUser(string id) => new(
        new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, id), new Claim(ClaimTypes.Name, "Test User")],
            "Test"));
}
