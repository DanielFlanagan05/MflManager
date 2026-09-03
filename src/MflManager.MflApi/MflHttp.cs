using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using MflManager.Application;
using MflManager.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MflManager.MflApi;

public sealed class MflSessionCookieJar
{
    private readonly CookieContainer _cookies = new();
    private readonly object _gate = new();

    public void Add(Uri uri, Cookie cookie) { lock (_gate) _cookies.Add(uri, cookie); }
    public string Header(Uri uri) { lock (_gate) return _cookies.GetCookieHeader(uri); }
    public Cookie? FindUserCookie(Uri uri)
    {
        lock (_gate) return _cookies.GetCookies(uri).Cast<Cookie>().FirstOrDefault(x => x.Name == "MFL_USER_ID");
    }
    public void Clear() { lock (_gate) _cookies.SetCookies(new Uri("https://api.myfantasyleague.com"), "MFL_USER_ID=; expires=Thu, 01 Jan 1970 00:00:00 GMT"); }
}

public sealed class CorrelationHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.TryAddWithoutValidation("X-Correlation-ID", Guid.NewGuid().ToString("N"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("MflManager", "1.0"));
        return base.SendAsync(request, cancellationToken);
    }
}

public sealed class CookieHandler(MflSessionCookieJar cookieJar, IMflCookieStore cookieStore) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var cookie = cookieJar.Header(request.RequestUri!);
        if (string.IsNullOrWhiteSpace(cookie))
        {
            var stored = await cookieStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(stored)) cookieJar.Add(request.RequestUri!, new Cookie("MFL_USER_ID", stored, "/", request.RequestUri!.Host));
            cookie = cookieJar.Header(request.RequestUri!);
        }
        if (!string.IsNullOrEmpty(cookie)) request.Headers.TryAddWithoutValidation("Cookie", cookie);
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            foreach (var value in values)
            {
                var first = value.Split(';', 2)[0].Split('=', 2);
                if (first.Length == 2 && first[0] == "MFL_USER_ID")
                {
                    cookieJar.Add(request.RequestUri!, new Cookie(first[0], first[1], "/", request.RequestUri!.Host));
                    await cookieStore.SaveAsync(first[1], cancellationToken).ConfigureAwait(false);
                }
            }
        }
        return response;
    }
}

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMflApi(this IServiceCollection services)
    {
        services.AddSingleton<MflSessionCookieJar>();
        services.AddTransient<CorrelationHandler>();
        services.AddTransient<CookieHandler>();
        services.AddHttpClient("mfl", client => client.Timeout = TimeSpan.FromSeconds(30))
            .AddHttpMessageHandler<CorrelationHandler>()
            .AddHttpMessageHandler<CookieHandler>();
        services.AddHttpClient("fantasypros", client => client.Timeout = TimeSpan.FromSeconds(20));
        services.AddSingleton<IMflAuthenticationClient, MflAuthenticationClient>();
        services.AddSingleton<IMflLeagueDiscoveryClient, MflLeagueDiscoveryClient>();
        services.AddSingleton<IMflExportClient, MflExportClient>();
        services.AddSingleton<IMflImportClient, MflImportClient>();
        services.AddSingleton<IMflDraftClient, MflDraftClient>();
        services.AddSingleton<IFantasyProsRankingClient, FantasyProsRankingClient>();
        services.AddSingleton<IMflCapabilityService, MflCapabilityService>();
        services.AddSingleton<IMflRefreshService, MflRefreshService>();
        services.AddSingleton<IMflMutationCoordinator, MflMutationCoordinator>();
        return services;
    }
}

internal static class MflHttpResult
{
    public static string CorrelationId(HttpRequestMessage request) =>
        request.Headers.TryGetValues("X-Correlation-ID", out var values) ? values.First() : Guid.NewGuid().ToString("N");

    public static (bool Success, string? Code, string? Message) ParseMutation(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return (false, "EMPTY_RESPONSE", "MFL returned an empty response.");
        try
        {
            if (body.TrimStart().StartsWith('{'))
            {
                var node = JsonNode.Parse(body);
                var jsonError = FindValue(node, "error") ?? FindValue(node, "message");
                var jsonStatus = FindValue(node, "status") ?? FindValue(node, "result");
                if (!string.IsNullOrWhiteSpace(jsonError)) return (false, "MFL_REJECTED", jsonError);
                if (jsonStatus?.Contains("error", StringComparison.OrdinalIgnoreCase) == true) return (false, "MFL_REJECTED", jsonStatus);
                return (true, null, jsonStatus ?? "Accepted by MFL.");
            }
            var xml = XDocument.Parse(body);
            var errorElement = xml.Descendants().FirstOrDefault(x => x.Name.LocalName.Equals("error", StringComparison.OrdinalIgnoreCase));
            var statusElement = xml.Descendants().FirstOrDefault(x => x.Name.LocalName.Equals("status", StringComparison.OrdinalIgnoreCase));
            var error = errorElement?.Value ?? errorElement?.Attribute("message")?.Value;
            if (!string.IsNullOrWhiteSpace(error)) return (false, errorElement?.Attribute("code")?.Value ?? "MFL_REJECTED", error);
            var status = statusElement?.Value ?? statusElement?.Attribute("message")?.Value;
            if (status?.Contains("error", StringComparison.OrdinalIgnoreCase) == true) return (false, "MFL_REJECTED", status);
            return (true, null, status ?? "Accepted by MFL.");
        }
        catch
        {
            if (body.Contains("error", StringComparison.OrdinalIgnoreCase)) return (false, "MFL_REJECTED", "MFL rejected the request.");
            return (true, null, "Accepted by MFL.");
        }
    }

    private static string? FindValue(JsonNode? node, string name)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj)
            {
                if (property.Key.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value?.ToString();
                var found = FindValue(property.Value, name);
                if (found is not null) return found;
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array) { var found = FindValue(item, name); if (found is not null) return found; }
        }
        return null;
    }
}

public sealed class MflAuthenticationClient(IHttpClientFactory clients, MflSessionCookieJar cookieJar, IMflCookieStore cookieStore, ILogger<MflAuthenticationClient> logger)
    : IMflAuthenticationClient
{
    public async Task<MflResult<AuthenticatedSession>> LoginAsync(SeasonContext season, MflCredentials credentials, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.myfantasyleague.com/{season.Year}/login")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["USERNAME"] = credentials.Username,
                ["PASSWORD"] = credentials.Password,
                ["XML"] = "1"
            })
        };
        var correlationId = Guid.NewGuid().ToString("N");
        request.Headers.TryAddWithoutValidation("X-Correlation-ID", correlationId);
        try
        {
            using var response = await clients.CreateClient("mfl").SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return MflResult<AuthenticatedSession>.Failure("Unable to sign in to MFL.", correlationId, status: response.StatusCode);
            var parsed = MflHttpResult.ParseMutation(body);
            var cookie = cookieJar.FindUserCookie(request.RequestUri!);
            if (!parsed.Success || cookie is null || string.IsNullOrWhiteSpace(cookie.Value))
                return MflResult<AuthenticatedSession>.Failure(parsed.Message ?? "MFL did not issue an authenticated session.", correlationId, parsed.Code, response.StatusCode);
            return MflResult<AuthenticatedSession>.Success(new(credentials.Username, "stored-in-cookie-jar", DateTimeOffset.UtcNow), correlationId, response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("MFL login failed for correlation {CorrelationId}: {ExceptionType}", correlationId, ex.GetType().Name);
            return MflResult<AuthenticatedSession>.Failure("MFL could not be reached. Check the connection and try again.", correlationId);
        }
    }

    public async Task SignOutAsync(CancellationToken cancellationToken) { cookieJar.Clear(); await cookieStore.ForgetAsync(cancellationToken).ConfigureAwait(false); }
}

internal static class FormHelpers
{
    public static Uri WithQuery(Uri uri, IReadOnlyDictionary<string, string> parameters)
    {
        var query = string.Join('&', parameters.Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value)}"));
        return new UriBuilder(uri) { Query = query }.Uri;
    }
}
