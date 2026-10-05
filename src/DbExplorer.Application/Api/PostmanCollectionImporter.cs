using System.Text.Json;

namespace DbExplorer.Application.Api;

/// <summary>
/// Postman collection exports (schema v2.1, and v2.0 which differs only in how auth is written)
/// and Postman environment / globals exports.
/// </summary>
public sealed class PostmanCollectionImporter : IApiCollectionImporter
{
    public string FormatName => "Postman v2.1";

    public bool CanImport(string content)
    {
        using var doc = JsonReading.TryParse(content);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object) return false;
        var root = doc.RootElement;
        return IsCollection(root) || IsEnvironment(root);
    }

    private static bool IsCollection(JsonElement root)
        => root.Prop("info") is { } info && (info.Str("schema") ?? "").Contains("getpostman.com", StringComparison.OrdinalIgnoreCase)
            || root.Prop("info") is not null && root.Prop("item") is { ValueKind: JsonValueKind.Array };

    private static bool IsEnvironment(JsonElement root)
        => root.Str("_postman_variable_scope") is "environment" or "globals"
            || root.Prop("values") is { ValueKind: JsonValueKind.Array } && root.Str("name") is not null && root.Prop("item") is null;

    public ApiImportResult Import(string content)
    {
        using var doc = JsonReading.Parse(content, "Postman");
        var root = doc.RootElement;
        var result = new ApiImportResult();

        if (IsCollection(root))
        {
            var schema = root.Prop("info")?.Str("schema") ?? "";
            if (schema.Contains("v1.", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Postman v1 collections are not supported; export the collection again as v2.1 from Postman.");
            result.Collections.Add(ReadCollection(root, result.Warnings));
        }
        else if (IsEnvironment(root))
        {
            result.Environments.Add(ReadEnvironment(root));
        }
        else
        {
            throw new InvalidDataException("The file is neither a Postman collection nor a Postman environment.");
        }
        return result;
    }

    private static ApiCollection ReadCollection(JsonElement root, List<string> warnings)
    {
        var info = root.Prop("info") ?? default;
        var collection = new ApiCollection
        {
            Name = info.Str("name") ?? "Imported collection",
            Description = Description(info),
            SourceFormat = (info.Str("schema") ?? "").Contains("v2.0", StringComparison.OrdinalIgnoreCase) ? "Postman v2.0" : "Postman v2.1",
            Variables = ReadVariables(root.Array("variable")),
            Auth = ReadAuth(root.Prop("auth"), "the collection", warnings)
        };
        WarnAboutScripts(root, $"collection \"{collection.Name}\"", warnings);
        ReadItems(root.Array("item"), collection.Folders, collection.Requests, "", warnings);
        return collection;
    }

    private static void ReadItems(IEnumerable<JsonElement> items, List<ApiFolder> folders, List<ApiRequest> requests,
        string parentPath, List<string> warnings)
    {
        foreach (var item in items)
        {
            var name = item.Str("name") ?? "";
            var path = parentPath.Length == 0 ? name : $"{parentPath} / {name}";

            if (item.Prop("item") is { ValueKind: JsonValueKind.Array } children)
            {
                var folder = new ApiFolder
                {
                    Name = name,
                    Description = Description(item),
                    Auth = ReadAuth(item.Prop("auth"), $"folder \"{path}\"", warnings)
                };
                WarnAboutScripts(item, $"folder \"{path}\"", warnings);
                ReadItems(children.EnumerateArray(), folder.Folders, folder.Requests, path, warnings);
                folders.Add(folder);
            }
            else if (item.Prop("request") is { } request)
            {
                WarnAboutScripts(item, $"request \"{path}\"", warnings);
                requests.Add(ReadRequest(name, request, path, warnings));
            }
        }
    }

    private static ApiRequest ReadRequest(string name, JsonElement request, string path, List<string> warnings)
    {
        // A request may be written as just its URL.
        if (request.ValueKind == JsonValueKind.String)
            return new ApiRequest { Name = name, Url = request.GetString() ?? "" };

        var result = new ApiRequest
        {
            Name = name,
            Description = Description(request),
            Method = (request.Str("method") ?? "GET").ToUpperInvariant(),
            Headers = ReadKeyValues(request.Prop("header") is { ValueKind: JsonValueKind.Array } h ? h.EnumerateArray() : []),
            Auth = ReadAuth(request.Prop("auth"), $"request \"{path}\"", warnings),
            Body = ReadBody(request.Prop("body"), path, warnings)
        };
        ReadUrl(request.Prop("url"), result);
        return result;
    }

    private static void ReadUrl(JsonElement? url, ApiRequest request)
    {
        if (url is not { } u) return;
        if (u.ValueKind == JsonValueKind.String)
        {
            request.Url = u.GetString() ?? "";
            return;
        }

        var raw = u.Str("raw") ?? BuildRaw(u);
        var query = u.Array("query").ToList();
        if (query.Count > 0)
        {
            // The query list also holds disabled parameters, which raw leaves out: keep them as separate entries.
            var q = raw.IndexOf('?');
            if (q >= 0) raw = raw[..q];
            request.QueryParams = ReadKeyValues(query);
        }

        // Path variables (/users/:id) are stored next to the URL; put their value in place.
        foreach (var v in u.Array("variable"))
        {
            var key = v.Str("key");
            if (string.IsNullOrEmpty(key)) continue;
            raw = ReplacePathVariable(raw, key, v.Str("value") ?? "");
        }
        request.Url = raw;
    }

    private static string ReplacePathVariable(string url, string key, string value)
    {
        var token = ":" + key;
        var i = 0;
        while ((i = url.IndexOf(token, i, StringComparison.Ordinal)) >= 0)
        {
            var end = i + token.Length;
            var startsSegment = i > 0 && url[i - 1] == '/';
            var endsSegment = end == url.Length || url[end] is '/' or '?' or '#';
            if (startsSegment && endsSegment)
            {
                url = url[..i] + value + url[end..];
                i += value.Length;
            }
            else
            {
                i = end;
            }
        }
        return url;
    }

    private static string BuildRaw(JsonElement u)
    {
        var protocol = u.Str("protocol");
        var host = u.Prop("host") is { ValueKind: JsonValueKind.Array } h
            ? string.Join('.', h.EnumerateArray().Select(JsonReading.AsText))
            : u.Str("host") ?? "";
        var port = u.Str("port");
        var path = u.Prop("path") is { ValueKind: JsonValueKind.Array } p
            ? string.Join('/', p.EnumerateArray().Select(s => s.ValueKind == JsonValueKind.Object ? s.Str("value") : JsonReading.AsText(s)))
            : u.Str("path") ?? "";

        var raw = (protocol is null ? "" : protocol + "://") + host + (port is null ? "" : ":" + port);
        if (path.Length > 0) raw += "/" + path.TrimStart('/');
        return raw;
    }

    private static ApiBody ReadBody(JsonElement? body, string path, List<string> warnings)
    {
        if (body is not { } b || b.Bool("disabled")) return new ApiBody();

        switch (b.Str("mode"))
        {
            case "raw":
                var language = b.Prop("options")?.Prop("raw")?.Str("language");
                return new ApiBody { Mode = ApiBodyMode.Raw, Raw = b.Str("raw") ?? "", ContentType = ContentTypeFor(language) };
            case "urlencoded":
                return new ApiBody { Mode = ApiBodyMode.UrlEncoded, Fields = ReadKeyValues(b.Array("urlencoded")) };
            case "formdata":
                var fields = new List<ApiKeyValue>();
                foreach (var f in b.Array("formdata"))
                {
                    if (f.Str("type") == "file")
                    {
                        warnings.Add($"Request \"{path}\": file field \"{f.Str("key")}\" was skipped; attach the file again after import.");
                        continue;
                    }
                    fields.Add(new ApiKeyValue(f.Str("key") ?? "", f.Str("value") ?? "", !f.Bool("disabled")));
                }
                return new ApiBody { Mode = ApiBodyMode.FormData, Fields = fields };
            case "graphql":
                var gql = b.Prop("graphql") ?? default;
                return new ApiBody { Mode = ApiBodyMode.GraphQL, Raw = gql.Str("query") ?? "", GraphQLVariables = gql.Str("variables") };
            case "file":
                warnings.Add($"Request \"{path}\": the binary file body was skipped.");
                return new ApiBody();
            default:
                return new ApiBody();
        }
    }

    private static string? ContentTypeFor(string? language) => language?.ToLowerInvariant() switch
    {
        "json" => "application/json",
        "xml" => "application/xml",
        "html" => "text/html",
        "javascript" => "application/javascript",
        "text" => "text/plain",
        _ => null
    };

    /// <summary>v2.1 writes auth parameters as a list of key/value; v2.0 as an object.</summary>
    private static ApiAuth? ReadAuth(JsonElement? auth, string owner, List<string> warnings)
    {
        if (auth is not { ValueKind: JsonValueKind.Object } a) return null;
        var type = a.Str("type") ?? "noauth";

        string? Param(string key)
        {
            if (a.Prop(type) is not { } p) return null;
            if (p.ValueKind == JsonValueKind.Object) return p.Str(key);
            foreach (var kv in p.EnumerateArray())
                if (kv.Str("key") == key) return kv.Str("value");
            return null;
        }

        switch (type)
        {
            case "noauth":
                return new ApiAuth { Type = ApiAuthType.None };
            case "inherit":
                return new ApiAuth { Type = ApiAuthType.Inherit };
            case "bearer":
                return new ApiAuth { Type = ApiAuthType.Bearer, Token = Param("token") ?? "" };
            case "basic":
                return new ApiAuth { Type = ApiAuthType.Basic, Username = Param("username") ?? "", Password = Param("password") ?? "" };
            case "apikey":
                return new ApiAuth
                {
                    Type = ApiAuthType.ApiKey,
                    Key = Param("key") ?? "",
                    Value = Param("value") ?? "",
                    In = Param("in") == "query" ? ApiKeyLocation.Query : ApiKeyLocation.Header
                };
            default:
                warnings.Add($"Auth type \"{type}\" on {owner} is not supported yet; requests are sent without it.");
                return new ApiAuth { Type = ApiAuthType.None };
        }
    }

    private static void WarnAboutScripts(JsonElement item, string owner, List<string> warnings)
    {
        foreach (var e in item.Array("event"))
        {
            var exec = e.Prop("script")?.Prop("exec");
            var hasCode = exec switch
            {
                { ValueKind: JsonValueKind.Array } arr => arr.EnumerateArray().Any(l => !string.IsNullOrWhiteSpace(JsonReading.AsText(l))),
                { ValueKind: JsonValueKind.String } s => !string.IsNullOrWhiteSpace(s.GetString()),
                _ => false
            };
            if (hasCode)
                warnings.Add($"The {e.Str("listen") ?? "event"} script on {owner} was not imported; scripts are not run.");
        }
    }

    private static ApiEnvironment ReadEnvironment(JsonElement root) => new()
    {
        Name = root.Str("name") ?? (root.Str("_postman_variable_scope") == "globals" ? "Globals" : "Imported environment"),
        Variables = root.Array("values")
            .Where(v => !string.IsNullOrEmpty(v.Str("key")))
            .Select(v => new ApiVariable(v.Str("key")!, v.Str("value") ?? "", v.Prop("enabled") is null || v.Bool("enabled")))
            .ToList()
    };

    private static List<ApiVariable> ReadVariables(IEnumerable<JsonElement> vars)
        => vars.Where(v => !string.IsNullOrEmpty(v.Str("key")))
            .Select(v => new ApiVariable(v.Str("key")!, v.Str("value") ?? "", !v.Bool("disabled")))
            .ToList();

    private static List<ApiKeyValue> ReadKeyValues(IEnumerable<JsonElement> items)
        => items.Where(i => i.ValueKind == JsonValueKind.Object && !string.IsNullOrEmpty(i.Str("key")))
            .Select(i => new ApiKeyValue(i.Str("key")!, i.Str("value") ?? "", !i.Bool("disabled")))
            .ToList();

    private static string? Description(JsonElement e) => e.Prop("description") switch
    {
        { ValueKind: JsonValueKind.String } s => s.GetString(),
        { ValueKind: JsonValueKind.Object } o => o.Str("content"),
        _ => null
    };
}
