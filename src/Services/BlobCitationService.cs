using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using image_search_demo.Models;
using Microsoft.AspNetCore.DataProtection;

namespace image_search_demo.Services;

public sealed partial class BlobCitationService(
    TokenCredential credential,
    IDataProtectionProvider dataProtectionProvider)
{
    private const long MaxBlobBytes = 50L * 1024 * 1024;
    private readonly ITimeLimitedDataProtector _protector = dataProtectionProvider
        .CreateProtector("FoundryChat.BlobCitation.v1")
        .ToTimeLimitedDataProtector();

    [GeneratedRegex(@"^[a-z0-9]{3,24}\.blob\.core\.windows\.net$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BlobHostPattern();

    public CitationDto CreateCitation(CitationDto citation, ClaimsPrincipal user, Func<string, string> linkFactory)
    {
        if (!TryCanonicalize(citation.OriginalUrl, out var blobUri))
        {
            return citation;
        }

        var state = new BlobCitationState(UserIdentity.GetId(user), blobUri.AbsoluteUri);
        var token = _protector.Protect(JsonSerializer.Serialize(state), TimeSpan.FromDays(7));
        return citation with
        {
            OriginalUrl = blobUri.AbsoluteUri,
            Href = linkFactory(token)
        };
    }

    public async Task<BlobProxyResult?> DownloadAsync(
        string token,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        BlobCitationState? state;
        try
        {
            state = JsonSerializer.Deserialize<BlobCitationState>(_protector.Unprotect(token));
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }

        if (state is null ||
            state.UserId != UserIdentity.GetId(user) ||
            !TryCanonicalize(state.BlobUrl, out var blobUri))
        {
            return null;
        }

        var blobClient = new BlobClient(blobUri, credential);
        var properties = await blobClient.GetPropertiesAsync(cancellationToken: cancellationToken);
        if (properties.Value.ContentLength > MaxBlobBytes)
        {
            throw new BlobCitationTooLargeException();
        }

        var download = await blobClient.DownloadStreamingAsync(
            new BlobDownloadOptions
            {
                Conditions = new BlobRequestConditions { IfMatch = properties.Value.ETag }
            },
            cancellationToken);
        var contentType = NormalizeContentType(properties.Value.ContentType);
        var fileName = Path.GetFileName(Uri.UnescapeDataString(blobUri.AbsolutePath));
        return new BlobProxyResult(
            download.Value.Content,
            contentType,
            string.IsNullOrWhiteSpace(fileName) ? "document" : fileName,
            IsSafeInlineContentType(contentType));
    }

    internal static bool TryCanonicalize(string? value, out Uri uri)
    {
        uri = default!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var candidate) ||
            candidate.Scheme != Uri.UriSchemeHttps ||
            candidate.Port != 443 ||
            !BlobHostPattern().IsMatch(candidate.Host) ||
            candidate.AbsolutePath.Count(character => character == '/') < 2)
        {
            return false;
        }

        uri = new UriBuilder(candidate) { Query = "", Fragment = "" }.Uri;
        return true;
    }

    private static string NormalizeContentType(string? contentType)
    {
        var normalized = contentType?.Split(';', 2)[0].Trim().ToLowerInvariant();
        return normalized is
            "application/pdf" or
            "image/png" or
            "image/jpeg" or
            "image/gif" or
            "image/webp" or
            "text/plain" or
            "text/csv" or
            "application/json" or
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document" or
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
                ? normalized
                : "application/octet-stream";
    }

    private static bool IsSafeInlineContentType(string contentType) =>
        contentType is "application/pdf" or "image/png" or "image/jpeg" or "image/gif" or "image/webp" or "text/plain";

    private sealed record BlobCitationState(string UserId, string BlobUrl);
}

public sealed class BlobCitationTooLargeException : Exception;
