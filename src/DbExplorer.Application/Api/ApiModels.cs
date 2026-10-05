namespace DbExplorer.Application.Api;

/// <summary>An imported (or hand-made) set of HTTP requests, independent of the tool it came from.</summary>
public sealed class ApiCollection
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>The format it was imported from, such as "Postman v2.1" or "Insomnia v4"; null when made in the app.</summary>
    public string? SourceFormat { get; set; }

    public DateTimeOffset ImportedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>Collection-level variables, used when no environment overrides them.</summary>
    public List<ApiVariable> Variables { get; set; } = [];

    /// <summary>Applies to every request that inherits its auth from the collection.</summary>
    public ApiAuth? Auth { get; set; }

    public List<ApiFolder> Folders { get; set; } = [];
    public List<ApiRequest> Requests { get; set; } = [];

    /// <summary>Every request in the collection, depth first, with the folders leading to it.</summary>
    public IEnumerable<(IReadOnlyList<ApiFolder> Path, ApiRequest Request)> AllRequests()
    {
        foreach (var r in Requests) yield return ([], r);
        foreach (var f in Folders)
            foreach (var item in f.AllRequests([]))
                yield return item;
    }

    /// <summary>
    /// The auth a request actually uses: its own, otherwise the nearest folder's, otherwise the collection's.
    /// Null means no auth.
    /// </summary>
    public ApiAuth? EffectiveAuth(ApiRequest request)
    {
        if (request.Auth is { Type: not ApiAuthType.Inherit }) return Normalize(request.Auth);

        var path = AllRequests().FirstOrDefault(x => ReferenceEquals(x.Request, request)).Path ?? [];
        for (var i = path.Count - 1; i >= 0; i--)
        {
            if (path[i].Auth is { Type: not ApiAuthType.Inherit } folderAuth) return Normalize(folderAuth);
        }
        return Auth is { Type: not ApiAuthType.Inherit } ? Normalize(Auth) : null;

        static ApiAuth? Normalize(ApiAuth auth) => auth.Type == ApiAuthType.None ? null : auth;
    }
}

public sealed class ApiFolder
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public ApiAuth? Auth { get; set; }
    public List<ApiFolder> Folders { get; set; } = [];
    public List<ApiRequest> Requests { get; set; } = [];

    internal IEnumerable<(IReadOnlyList<ApiFolder> Path, ApiRequest Request)> AllRequests(IReadOnlyList<ApiFolder> parents)
    {
        var path = parents.Append(this).ToList();
        foreach (var r in Requests) yield return (path, r);
        foreach (var f in Folders)
            foreach (var item in f.AllRequests(path))
                yield return item;
    }
}

public sealed class ApiRequest
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string Method { get; set; } = "GET";

    /// <summary>The URL as written, variables included (<c>{{baseUrl}}/users</c>); query parameters may be in it or in <see cref="QueryParams"/>.</summary>
    public string Url { get; set; } = "";

    /// <summary>Appended to <see cref="Url"/> when the request is sent (enabled ones only).</summary>
    public List<ApiKeyValue> QueryParams { get; set; } = [];

    public List<ApiKeyValue> Headers { get; set; } = [];
    public ApiBody Body { get; set; } = new();

    /// <summary>Null or <see cref="ApiAuthType.Inherit"/> takes the folder's or collection's auth.</summary>
    public ApiAuth? Auth { get; set; }
}

public sealed class ApiKeyValue
{
    public ApiKeyValue() { }

    public ApiKeyValue(string key, string value, bool enabled = true)
    {
        Key = key;
        Value = value;
        Enabled = enabled;
    }

    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public enum ApiBodyMode
{
    None,

    /// <summary>Text sent as is; <see cref="ApiBody.ContentType"/> says what it is (JSON, XML, plain text…).</summary>
    Raw,

    /// <summary><c>application/x-www-form-urlencoded</c> from <see cref="ApiBody.Fields"/>.</summary>
    UrlEncoded,

    /// <summary><c>multipart/form-data</c> from <see cref="ApiBody.Fields"/>; file fields are not imported.</summary>
    FormData,

    /// <summary>A GraphQL query and its variables, sent as a JSON body.</summary>
    GraphQL
}

public sealed class ApiBody
{
    public ApiBodyMode Mode { get; set; } = ApiBodyMode.None;

    /// <summary>The raw text, or the GraphQL query.</summary>
    public string? Raw { get; set; }

    /// <summary>The Content-Type of a raw body when no header sets one (for example <c>application/json</c>).</summary>
    public string? ContentType { get; set; }

    /// <summary>The JSON text of a GraphQL request's variables.</summary>
    public string? GraphQLVariables { get; set; }

    public List<ApiKeyValue> Fields { get; set; } = [];
}

public enum ApiAuthType
{
    Inherit,
    None,
    Bearer,
    Basic,
    ApiKey
}

public enum ApiKeyLocation
{
    Header,
    Query
}

public sealed class ApiAuth
{
    public ApiAuthType Type { get; set; } = ApiAuthType.Inherit;

    /// <summary>Bearer token.</summary>
    public string? Token { get; set; }

    public string? Username { get; set; }
    public string? Password { get; set; }

    /// <summary>API key name (header or query parameter) and value.</summary>
    public string? Key { get; set; }
    public string? Value { get; set; }
    public ApiKeyLocation In { get; set; } = ApiKeyLocation.Header;
}

public sealed class ApiVariable
{
    public ApiVariable() { }

    public ApiVariable(string key, string value, bool enabled = true)
    {
        Key = key;
        Value = value;
        Enabled = enabled;
    }

    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

/// <summary>A named set of variables (Postman / Insomnia environment) that overrides collection variables.</summary>
public sealed class ApiEnvironment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public List<ApiVariable> Variables { get; set; } = [];
}

/// <summary>What an importer read from one file.</summary>
public sealed class ApiImportResult
{
    public List<ApiCollection> Collections { get; } = [];
    public List<ApiEnvironment> Environments { get; } = [];

    /// <summary>Parts of the file that were skipped or simplified (scripts, file uploads, unsupported auth…).</summary>
    public List<string> Warnings { get; } = [];
}

/// <summary>The answer to a sent request.</summary>
public sealed class ApiResponse
{
    public int StatusCode { get; init; }
    public string? ReasonPhrase { get; init; }
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; } = [];
    public string Body { get; init; } = "";
    public string? ContentType { get; init; }
    public long SizeBytes { get; init; }
    public TimeSpan Elapsed { get; init; }
    public bool IsSuccess => StatusCode is >= 200 and < 300;
}

/// <summary>One sent request, kept for recall.</summary>
public sealed class ApiHistoryEntry
{
    public DateTimeOffset SentAt { get; set; }
    public string? CollectionId { get; set; }
    public string? RequestId { get; set; }
    public string Method { get; set; } = "";

    /// <summary>The URL after variables were resolved.</summary>
    public string Url { get; set; } = "";

    public int? StatusCode { get; set; }
    public long ElapsedMs { get; set; }
    public string? Error { get; set; }
}
