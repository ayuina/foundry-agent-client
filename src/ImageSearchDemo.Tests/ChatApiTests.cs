using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using image_search_demo.Models;
using image_search_demo.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ImageSearchDemo.Tests;

public sealed partial class ChatApiTests : IClassFixture<ChatApiTests.ChatApplicationFactory>
{
    private readonly ChatApplicationFactory _factory;

    public ChatApiTests(ChatApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Send_ReturnsTextCitationsAssetsAndConversationToken()
    {
        using var client = _factory.CreateClient();
        var antiForgeryToken = await GetAntiForgeryTokenAsync(client);
        var request = CreateValidRequest();
        using var message = CreatePost("/api/chat", request, antiForgeryToken);

        var response = await client.SendAsync(message);
        var result = await response.Content.ReadFromJsonAsync<ChatResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(result);
        Assert.Equal("Grounded answer", result.Text);
        Assert.Single(result.Citations);
        Assert.Equal(
            "https://storage123.blob.core.windows.net/docs/source.pdf",
            result.Citations[0].OriginalUrl);
        Assert.StartsWith("/api/chat/citations/", result.Citations[0].Href);
        Assert.Single(result.Assets);
        Assert.False(string.IsNullOrWhiteSpace(result.ConversationToken));
    }

    [Fact]
    public async Task Send_RejectsConversationTokenForDifferentAgent()
    {
        using var client = _factory.CreateClient();
        var antiForgeryToken = await GetAntiForgeryTokenAsync(client);
        using var firstMessage = CreatePost("/api/chat", CreateValidRequest(), antiForgeryToken);
        var firstResponse = await client.SendAsync(firstMessage);
        var first = await firstResponse.Content.ReadFromJsonAsync<ChatResponse>();
        Assert.NotNull(first);

        var changedAgent = CreateValidRequest() with
        {
            AgentName = "another-agent",
            ConversationToken = first.ConversationToken
        };
        using var secondMessage = CreatePost("/api/chat", changedAgent, antiForgeryToken);

        var secondResponse = await client.SendAsync(secondMessage);

        Assert.Equal(HttpStatusCode.Conflict, secondResponse.StatusCode);
    }

    [Fact]
    public async Task Send_RejectsInvalidProjectToken()
    {
        using var client = _factory.CreateClient();
        var antiForgeryToken = await GetAntiForgeryTokenAsync(client);
        var request = CreateValidRequest() with { ProjectToken = "invalid" };
        using var message = CreatePost("/api/chat", request, antiForgeryToken);

        var response = await client.SendAsync(message);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private ChatRequest CreateValidRequest()
    {
        var tokenService = _factory.Services.GetRequiredService<ProjectSelectionTokenService>();
        var projectToken = tokenService.Protect(
            ProjectSelectionTests.Selection(),
            ProjectSelectionTests.CreateUser("test-user"));
        return new ChatRequest
        {
            ProjectToken = projectToken,
            AgentName = "document-agent",
            AgentVersion = "1",
            Message = "Question"
        };
    }

    private static HttpRequestMessage CreatePost(string uri, object body, string antiForgeryToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("RequestVerificationToken", antiForgeryToken);
        return request;
    }

    private static async Task<string> GetAntiForgeryTokenAsync(HttpClient client)
    {
        var html = await client.GetStringAsync("/");
        var match = AntiForgeryTokenPattern().Match(html);
        Assert.True(match.Success);
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex AntiForgeryTokenPattern();

    public sealed class ChatApplicationFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IFoundryAgentGateway>();
                services.RemoveAll<IFoundryResourceDiscoveryService>();
                services.AddSingleton<IFoundryAgentGateway, FakeFoundryAgentGateway>();
                services.AddSingleton<IFoundryResourceDiscoveryService, FakeFoundryResourceDiscoveryService>();
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = TestAuthenticationHandler.AuthenticationSchemeName;
                        options.DefaultChallengeScheme = TestAuthenticationHandler.AuthenticationSchemeName;
                    })
                    .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                        TestAuthenticationHandler.AuthenticationSchemeName,
                        _ => { });
            });
        }
    }

    private sealed class FakeFoundryAgentGateway : IFoundryAgentGateway
    {
        public Task<AgentReply> SendAsync(
            ResolvedAgentConnection connection,
            string message,
            string? conversationId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new AgentReply(
                "Grounded answer",
                conversationId ?? "conversation-1",
                [new CitationDto(
                    "Source",
                    "https://storage123.blob.core.windows.net/docs/source.pdf?sv=secret",
                    "https://storage123.blob.core.windows.net/docs/source.pdf?sv=secret",
                    "web")],
                [new GeneratedAsset("chart.png", "image/png", [1, 2, 3])]));

        public Task DeleteConversationAsync(
            ResolvedAgentConnection connection,
            string conversationId,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<byte[]> DownloadContainerFileAsync(
            ResolvedAgentConnection connection,
            string containerId,
            string fileId,
            CancellationToken cancellationToken) => Task.FromResult<byte[]>([1, 2, 3]);
    }

    private sealed class FakeFoundryResourceDiscoveryService : IFoundryResourceDiscoveryService
    {
        public Task<IReadOnlyList<ProjectSelection>> GetProjectsAsync(
            ClaimsPrincipal user,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProjectSelection>>([ProjectSelectionTests.Selection()]);

        public Task<bool> CanAccessProjectAsync(
            ProjectSelection project,
            ClaimsPrincipal user,
            CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string AuthenticationSchemeName = "Test";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "test-user"),
                    new Claim(ClaimTypes.Name, "Test User")
                ],
                AuthenticationSchemeName);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), AuthenticationSchemeName)));
        }
    }
}
