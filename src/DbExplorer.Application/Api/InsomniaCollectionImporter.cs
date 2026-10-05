using System.Text.Json;
using System.Text.RegularExpressions;

namespace DbExplorer.Application.Api;

/// <summary>
/// Insomnia JSON exports (export format 4): every workspace becomes a collection, its base environment the
/// collection variables and its sub-environments <see cref="ApiEnvironment"/>s.
/// </summary>
public sealed partial class InsomniaCollectionImporter : IApiCollectionImporter
{
    public string FormatName => "Insomnia v4";

    public bool CanImport(string content)
    {
        using var doc = JsonReading.TryParse(content);
        return doc is not null && IsExport(doc.RootElement);
    }

    private static bool IsExport(JsonElement root)
        => root.ValueKind == JsonValueKind.Object
            && root.Str("_type") == "export"
            && root.Prop("resources") is { ValueKind: JsonValueKind.Array };

    public ApiImportResult Import(string content)
    {
        using var doc = JsonReading.Parse(content, "Insomnia");
        var root = doc.RootElement;
        if (!IsExport(root)) throw new InvalidDataException("The file is not an Insomnia export.");

        var version = root.Str("__export_format");
        if (version is not null && version != "4")
            throw new InvalidDataException($"Insomnia export format {version} is not supported; export again as \"Insomnia v4 (JSON)\".");

        var resources = root.Array("resources").Where(r => r.ValueKind == JsonValueKind.Object).ToList();
        var byParent = resources.ToLookup(r => r.Str("parentId") ?? "");
        var result = new ApiImportResult();

        foreach (var workspace in resources.Where(r => r.Str("_type") == "workspace"))
        {
            var id = workspace.Str("_id") ?? "";
            var collection = new ApiCollection
            {
                Name = workspace.Str("name") ?? "Imported workspace",
                Description = NullIfEmpty(workspace.Str("description")),
                SourceFormat = FormatName
            };

            foreach (var baseEnv in byParent[id].Where(r => r.Str("_type") == "environment"))
            {
                collection.Variables.AddRange(Flatten(baseEnv.Prop("data")));
                foreach (var subEnv in byParent[baseEnv.Str("_id") ?? ""].Where(r => r.Str("_type") == "environment"))
                {
                    result.Environments.Add(new ApiEnvironment
                    {
                        Name = $"{collection.Name} / {subEnv.Str("name") ?? "Environment"}",
                        Variables = Flatten(subEnv.Prop("data"))
                    });
                }
            }

            ReadChildren(id, byParent, collection.Folders, collection.Requests, "", result.Warnings);
            result.Collections.Add(collection);
        }

        if (result.Collections.Count == 0 && result.Environments.Count == 0)
            throw new InvalidDataException("The Insomnia export contains no workspace.");
        return result;
    }

    private static void ReadChildren(string parentId, ILookup<string, JsonElement> byParent, List<ApiFolder> folders,
        List<ApiRequest> requests, string parentPath, List<string> warnings)
    {
        // Insomnia orders siblings by metaSortKey (smaller first).
        foreach (var r in byParent[parentId].OrderBy(r => r.Prop("metaSortKey") is { ValueKind: JsonValueKind.Number } k ? k.GetDouble() : 0))
        {
            var name = r.Str("name") ?? "";
            var path = parentPath.Length == 0 ? name : $"{parentPath} / {name}";
            switch (r.Str("_type"))
            {
                case "request_group":
                    var folder = new ApiFolder
                    {
                        Name = name,
                        Description = NullIfEmpty(r.Str("description")),
                        Auth = ReadAuth(r.Prop("authentication"), $"folder \"{path}\"", warnings)
                    };
                    if (r.Prop("environment") is { ValueKind: JsonValueKind.Object } env && env.EnumerateObject().Any())
                        warnings.Add($"Folder \"{path}\" has its own environment, which was not imported.");
                    ReadChildren(r.Str("_id") ?? "", byParent, folder.Folders, folder.Requests, path, warnings);
                    folders.Add(folder);
                    break;
                case "request":
                    requests.Add(ReadRequest(r, path, warnings));
                    break;
                case "grpc_request" or "websocket_request":
                    warnings.Add($"\"{path}\" is a {r.Str("_type")!.Replace("_request", "")} request, which is not supported; skipped.");
                    break;
            }
        }
    }

    private static ApiRequest ReadRequest(JsonElement r, string path, List<string> warnings)
    {
        var request = new ApiRequest
        {
            Name = r.Str("name") ?? "",
            Description = NullIfEmpty(r.Str("description")),
            Method = (r.Str("method") ?? "GET").ToUpperInvariant(),
            Url = Template(r.Str("url") ?? "", $"request \"{path}\"", warnings),
            QueryParams = ReadPairs(r.Array("parameters"), path, warnings),
            Headers = ReadPairs(r.Array("headers"), path, warnings),
            Auth = ReadAuth(r.Prop("authentication"), $"request \"{path}\"", warnings)
        };

        if (r.Prop("body") is { ValueKind: JsonValueKind.Object } body)
            request.Body = ReadBody(body, path, warnings);
        return request;
    }

    private static ApiBody ReadBody(JsonElement body, string path, List<string> warnings)
    {
        var mime = body.Str("mimeType");
        switch (mime)
        {
            case null or "" when body.Str("text") is null:
                return new ApiBody();
            case "application/x-www-form-urlencoded":
                return new ApiBody { Mode = ApiBodyMode.UrlEncoded, Fields = ReadPairs(body.Array("params"), path, warnings) };
            case "multipart/form-data":
                var fields = new List<ApiKeyValue>();
                foreach (var p in body.Array("params"))
                {
                    if (p.Str("type") == "file")
                    {
                        warnings.Add($"Request \"{path}\": file field \"{p.Str("name")}\" was skipped; attach the file again after import.");
                        continue;
                    }
                    fields.Add(new ApiKeyValue(p.Str("name") ?? "", Template(p.Str("value") ?? "", $"request \"{path}\"", warnings), !p.Bool("disabled")));
                }
                return new ApiBody { Mode = ApiBodyMode.FormData, Fields = fields };
            case "application/graphql":
                // Insomnia stores {"query": "...", "variables": {...}} as the body text.
                using (var gql = JsonReading.TryParse(body.Str("text") ?? ""))
                {
                    var root = gql?.RootElement ?? default;
                    return new ApiBody
                    {
                        Mode = ApiBodyMode.GraphQL,
                        Raw = Template(root.Str("query") ?? "", $"request \"{path}\"", warnings),
                        GraphQLVariables = root.Prop("variables") is { } v ? Template(v.GetRawText(), $"request \"{path}\"", warnings) : null
                    };
                }
            case "application/octet-stream":
                warnings.Add($"Request \"{path}\": the binary file body was skipped.");
                return new ApiBody();
            default:
                return new ApiBody { Mode = ApiBodyMode.Raw, Raw = Template(body.Str("text") ?? "", $"request \"{path}\"", warnings), ContentType = mime };
        }
    }

    private static ApiAuth? ReadAuth(JsonElement? auth, string owner, List<string> warnings)
    {
        if (auth is not { ValueKind: JsonValueKind.Object } a || !a.EnumerateObject().Any()) return null;
        if (a.Bool("disabled")) return new ApiAuth { Type = ApiAuthType.None };

        switch (a.Str("type"))
        {
            case null or "" or "none":
                return new ApiAuth { Type = ApiAuthType.None };
            case "bearer":
                var prefix = a.Str("prefix");
                if (!string.IsNullOrEmpty(prefix) && !prefix.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
                    warnings.Add($"The bearer prefix \"{prefix}\" on {owner} was replaced by \"Bearer\".");
                return new ApiAuth { Type = ApiAuthType.Bearer, Token = Template(a.Str("token") ?? "", owner, warnings) };
            case "basic":
                return new ApiAuth
                {
                    Type = ApiAuthType.Basic,
                    Username = Template(a.Str("username") ?? "", owner, warnings),
                    Password = Template(a.Str("password") ?? "", owner, warnings)
                };
            case "apikey":
                return new ApiAuth
                {
                    Type = ApiAuthType.ApiKey,
                    Key = Template(a.Str("key") ?? "", owner, warnings),
                    Value = Template(a.Str("value") ?? "", owner, warnings),
                    In = a.Str("addTo") == "queryParams" ? ApiKeyLocation.Query : ApiKeyLocation.Header
                };
            default:
                warnings.Add($"Auth type \"{a.Str("type")}\" on {owner} is not supported yet; requests are sent without it.");
                return new ApiAuth { Type = ApiAuthType.None };
        }
    }

    private static List<ApiKeyValue> ReadPairs(IEnumerable<JsonElement> items, string path, List<string> warnings)
        => items.Where(i => i.ValueKind == JsonValueKind.Object && !string.IsNullOrEmpty(i.Str("name")))
            .Select(i => new ApiKeyValue(
                Template(i.Str("name")!, $"request \"{path}\"", warnings),
                Template(i.Str("value") ?? "", $"request \"{path}\"", warnings),
                !i.Bool("disabled")))
            .ToList();

    /// <summary>Environment data may nest objects; <c>{"auth": {"token": "x"}}</c> becomes the variable <c>auth.token</c>.</summary>
    private static List<ApiVariable> Flatten(JsonElement? data)
    {
        var result = new List<ApiVariable>();
        if (data is { ValueKind: JsonValueKind.Object } d) Walk(d, "");
        return result;

        void Walk(JsonElement obj, string prefix)
        {
            foreach (var p in obj.EnumerateObject())
            {
                var key = prefix + p.Name;
                if (p.Value.ValueKind == JsonValueKind.Object) Walk(p.Value, key + ".");
                else result.Add(new ApiVariable(key, JsonReading.AsText(p.Value) ?? ""));
            }
        }
    }

    /// <summary>
    /// Insomnia writes variables as <c>{{ _.name }}</c> (or <c>{{ name }}</c>); both become <c>{{name}}</c>.
    /// Template tags such as <c>{% response %}</c> cannot be evaluated outside Insomnia and stay as they are.
    /// </summary>
    internal static string Template(string text, string owner, List<string> warnings)
    {
        if (text.Length == 0 || !text.Contains('{')) return text;
        if (TagRegex().IsMatch(text))
        {
            var warning = $"{char.ToUpperInvariant(owner[0])}{owner[1..]} uses Insomnia template tags ({{% … %}}), which are kept as text.";
            if (!warnings.Contains(warning)) warnings.Add(warning);
        }
        return VariableRegex().Replace(text, m => "{{" + m.Groups["name"].Value + "}}");
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    [GeneratedRegex(@"\{\{\s*(?:_\.)?(?<name>[^{}\s]+)\s*\}\}")]
    private static partial Regex VariableRegex();

    [GeneratedRegex(@"\{%.*?%\}", RegexOptions.Singleline)]
    private static partial Regex TagRegex();
}
