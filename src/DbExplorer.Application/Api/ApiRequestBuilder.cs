using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DbExplorer.Application.Api;

/// <summary>Turns an <see cref="ApiRequest"/> into an <see cref="HttpRequestMessage"/>, with variables resolved and auth applied.</summary>
public static class ApiRequestBuilder
{
    /// <param name="collection">Supplies inherited auth; null for a request on its own.</param>
    public static HttpRequestMessage Build(ApiRequest request, ApiCollection? collection, ApiVariableResolver variables)
    {
        var auth = collection?.EffectiveAuth(request)
            ?? (request.Auth is { Type: not (ApiAuthType.Inherit or ApiAuthType.None) } own ? own : null);

        var query = request.QueryParams.Where(p => p.Enabled)
            .Select(p => (variables.Resolve(p.Key), variables.Resolve(p.Value)))
            .ToList();
        if (auth is { Type: ApiAuthType.ApiKey, In: ApiKeyLocation.Query })
            query.Add((variables.Resolve(auth.Key), variables.Resolve(auth.Value)));

        var message = new HttpRequestMessage(new HttpMethod(request.Method.Trim().ToUpperInvariant()), BuildUri(variables.Resolve(request.Url), query));

        var contentHeaders = new List<(string Key, string Value)>();
        foreach (var h in request.Headers.Where(h => h.Enabled && !string.IsNullOrWhiteSpace(h.Key)))
        {
            var key = variables.Resolve(h.Key).Trim();
            var value = variables.Resolve(h.Value);
            // Exports often carry a stale Content-Length; the real one is computed from the body.
            if (key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            if (IsContentHeader(key)) contentHeaders.Add((key, value));
            else message.Headers.TryAddWithoutValidation(key, value);
        }

        ApplyAuth(message, auth, variables);

        message.Content = BuildContent(request.Body, variables, contentHeaders.Any(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)));
        if (message.Content is null && contentHeaders.Count > 0)
            message.Content = new ByteArrayContent([]);
        if (message.Content is not null)
        {
            foreach (var (key, value) in contentHeaders)
            {
                message.Content.Headers.Remove(key);
                message.Content.Headers.TryAddWithoutValidation(key, value);
            }
        }
        return message;
    }

    /// <summary>
    /// Postman sends <c>example.com/x</c> as <c>http://example.com/x</c>; so does this. Query parameters are
    /// appended to any already in the URL, encoded.
    /// </summary>
    public static Uri BuildUri(string url, IReadOnlyCollection<(string Key, string Value)> query)
    {
        url = url.Trim();
        if (url.Length == 0) throw new InvalidOperationException("The request has no URL.");
        if (!url.Contains("://", StringComparison.Ordinal)) url = "http://" + url;

        if (query.Count > 0)
        {
            var fragment = "";
            var hash = url.IndexOf('#');
            if (hash >= 0)
            {
                fragment = url[hash..];
                url = url[..hash];
            }
            var sb = new StringBuilder(url);
            var separator = url.Contains('?') ? (url.EndsWith('?') || url.EndsWith('&') ? "" : "&") : "?";
            foreach (var (key, value) in query)
            {
                sb.Append(separator).Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
                separator = "&";
            }
            url = sb + fragment;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException($"\"{url}\" is not a valid http(s) URL. Check that every variable in it has a value.");
        return uri;
    }

    private static void ApplyAuth(HttpRequestMessage message, ApiAuth? auth, ApiVariableResolver variables)
    {
        if (auth is null) return;
        // A header written on the request wins over the auth settings, as in Postman.
        var hasAuthorization = message.Headers.Contains("Authorization");
        switch (auth.Type)
        {
            case ApiAuthType.Bearer when !hasAuthorization:
                message.Headers.TryAddWithoutValidation("Authorization", "Bearer " + variables.Resolve(auth.Token));
                break;
            case ApiAuthType.Basic when !hasAuthorization:
                var raw = $"{variables.Resolve(auth.Username)}:{variables.Resolve(auth.Password)}";
                message.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)));
                break;
            case ApiAuthType.ApiKey when auth.In == ApiKeyLocation.Header:
                var key = variables.Resolve(auth.Key).Trim();
                if (key.Length > 0 && !message.Headers.Contains(key))
                    message.Headers.TryAddWithoutValidation(key, variables.Resolve(auth.Value));
                break;
        }
    }

    private static HttpContent? BuildContent(ApiBody body, ApiVariableResolver variables, bool hasContentTypeHeader)
    {
        switch (body.Mode)
        {
            case ApiBodyMode.Raw:
                var content = new StringContent(variables.Resolve(body.Raw), Encoding.UTF8);
                if (!hasContentTypeHeader)
                    content.Headers.ContentType = MediaTypeHeaderValue.TryParse(body.ContentType ?? "text/plain", out var type)
                        ? type
                        : new MediaTypeHeaderValue("text/plain");
                if (content.Headers.ContentType is { } ct && ct.CharSet is null && ct.MediaType?.StartsWith("text/") == true)
                    ct.CharSet = "utf-8";
                return content;

            case ApiBodyMode.UrlEncoded:
                return new FormUrlEncodedContent(body.Fields.Where(f => f.Enabled)
                    .Select(f => new KeyValuePair<string, string>(variables.Resolve(f.Key), variables.Resolve(f.Value))));

            case ApiBodyMode.FormData:
                var form = new MultipartFormDataContent();
                foreach (var f in body.Fields.Where(f => f.Enabled))
                    form.Add(new StringContent(variables.Resolve(f.Value), Encoding.UTF8), variables.Resolve(f.Key));
                return form;

            case ApiBodyMode.GraphQL:
                var payload = new JsonObject { ["query"] = variables.Resolve(body.Raw) };
                var vars = variables.Resolve(body.GraphQLVariables);
                if (!string.IsNullOrWhiteSpace(vars))
                {
                    try
                    {
                        payload["variables"] = JsonNode.Parse(vars);
                    }
                    catch (JsonException ex)
                    {
                        throw new InvalidOperationException($"The GraphQL variables are not valid JSON: {ex.Message}", ex);
                    }
                }
                return new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

            default:
                return null;
        }
    }

    private static bool IsContentHeader(string name) => name.ToLowerInvariant() is
        "content-type" or "content-length" or "content-encoding" or "content-language" or "content-location"
        or "content-md5" or "content-range" or "content-disposition" or "expires" or "last-modified" or "allow";
}
