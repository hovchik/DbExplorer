using DbExplorer.Application.Api;

namespace DbExplorer.Tests.Api;

public class PostmanCollectionImporterTests
{
    private readonly PostmanCollectionImporter _importer = new();

    private ApiCollection ImportSample() => Assert.Single(_importer.Import(Samples.Read("postman-v21.json")).Collections);

    private static ApiRequest Request(ApiCollection c, string name) => c.AllRequests().Single(r => r.Request.Name == name).Request;

    [Fact]
    public void Recognises_Postman_collections_and_environments_only()
    {
        Assert.True(_importer.CanImport(Samples.Read("postman-v21.json")));
        Assert.True(_importer.CanImport(Samples.Read("postman-v20.json")));
        Assert.True(_importer.CanImport(Samples.Read("postman-environment.json")));
        Assert.False(_importer.CanImport(Samples.Read("insomnia-v4.json")));
        Assert.False(_importer.CanImport("not json"));
        Assert.False(_importer.CanImport("[]"));
    }

    [Fact]
    public void Reads_name_variables_and_folder_tree()
    {
        var c = ImportSample();

        Assert.Equal("Pet Store", c.Name);
        Assert.Equal("Postman v2.1", c.SourceFormat);
        Assert.Equal(["baseUrl", "token", "unused"], c.Variables.Select(v => v.Key));
        Assert.False(c.Variables[2].Enabled);

        Assert.Equal(["Health", "Login", "Query pets"], c.Requests.Select(r => r.Name));
        var pets = Assert.Single(c.Folders);
        Assert.Equal("Pets", pets.Name);
        Assert.Equal(["List pets", "Get pet", "Create pet", "Upload photo"], pets.Requests.Select(r => r.Name));
        Assert.Equal(7, c.AllRequests().Count());
    }

    [Fact]
    public void Url_query_parameters_keep_disabled_entries_apart_from_the_url()
    {
        var list = Request(ImportSample(), "List pets");

        Assert.Equal("{{baseUrl}}/pets", list.Url);
        Assert.Collection(list.QueryParams,
            p => Assert.Equal(("limit", "10", true), (p.Key, p.Value, p.Enabled)),
            p => Assert.Equal(("offset", "0", false), (p.Key, p.Value, p.Enabled)));
        Assert.Collection(list.Headers,
            h => Assert.Equal(("Accept", true), (h.Key, h.Enabled)),
            h => Assert.Equal(("X-Debug", false), (h.Key, h.Enabled)));
    }

    [Fact]
    public void Path_variables_are_put_in_place_and_method_is_upper_cased()
    {
        var c = ImportSample();

        Assert.Equal("{{baseUrl}}/pets/{{petId}}", Request(c, "Get pet").Url);
        Assert.Equal("GET", Request(c, "Health").Method);
    }

    [Fact]
    public void Reads_every_body_mode()
    {
        var c = ImportSample();

        var raw = Request(c, "Create pet").Body;
        Assert.Equal(ApiBodyMode.Raw, raw.Mode);
        Assert.Equal("application/json", raw.ContentType);
        Assert.Contains("{{petName}}", raw.Raw);

        var form = Request(c, "Upload photo").Body;
        Assert.Equal(ApiBodyMode.FormData, form.Mode);
        Assert.Equal("caption", Assert.Single(form.Fields).Key);

        var encoded = Request(c, "Login").Body;
        Assert.Equal(ApiBodyMode.UrlEncoded, encoded.Mode);
        Assert.Equal([true, false], encoded.Fields.Select(f => f.Enabled));

        var gql = Request(c, "Query pets").Body;
        Assert.Equal(ApiBodyMode.GraphQL, gql.Mode);
        Assert.Equal("{ pets { id } }", gql.Raw);
        Assert.Equal("{\"first\": 5}", gql.GraphQLVariables);
    }

    [Fact]
    public void Auth_is_inherited_from_folder_then_collection_unless_overridden()
    {
        var c = ImportSample();

        var collectionAuth = c.EffectiveAuth(Request(c, "Health"));
        Assert.Equal(ApiAuthType.Bearer, collectionAuth?.Type);
        Assert.Equal("{{token}}", collectionAuth?.Token);

        var folderAuth = c.EffectiveAuth(Request(c, "List pets"));
        Assert.Equal(ApiAuthType.ApiKey, folderAuth?.Type);
        Assert.Equal(("X-Api-Key", "{{apiKey}}", ApiKeyLocation.Header), (folderAuth!.Key, folderAuth.Value, folderAuth.In));

        Assert.Null(c.EffectiveAuth(Request(c, "Get pet")));

        var basic = c.EffectiveAuth(Request(c, "Login"));
        Assert.Equal(("admin", "{{password}}"), (basic!.Username, basic.Password));
    }

    [Fact]
    public void Warns_about_scripts_and_skipped_file_fields()
    {
        var result = _importer.Import(Samples.Read("postman-v21.json"));

        Assert.Contains(result.Warnings, w => w.Contains("prerequest script on collection \"Pet Store\""));
        Assert.Contains(result.Warnings, w => w.Contains("test script on request \"Pets / Create pet\""));
        Assert.Contains(result.Warnings, w => w.Contains("file field \"photo\""));
    }

    [Fact]
    public void Reads_v20_auth_objects_and_requests_written_as_a_url()
    {
        var c = Assert.Single(_importer.Import(Samples.Read("postman-v20.json")).Collections);

        Assert.Equal("Postman v2.0", c.SourceFormat);
        Assert.Equal("v20-token", c.Auth?.Token);
        Assert.Equal("https://legacy.example.com/ping", Assert.Single(c.Requests).Url);
    }

    [Fact]
    public void Reads_an_environment_file()
    {
        var result = _importer.Import(Samples.Read("postman-environment.json"));

        Assert.Empty(result.Collections);
        var env = Assert.Single(result.Environments);
        Assert.Equal("Staging", env.Name);
        Assert.Equal(4, env.Variables.Count);
        Assert.False(env.Variables.Single(v => v.Key == "old").Enabled);
    }

    [Fact]
    public void Rejects_v1_collections_and_broken_json()
    {
        var v1 = """{"info": {"name": "x", "schema": "https://schema.getpostman.com/json/collection/v1.0.0/collection.json"}, "item": []}""";
        Assert.Contains("v1", Assert.Throws<InvalidDataException>(() => _importer.Import(v1)).Message);
        Assert.Throws<InvalidDataException>(() => _importer.Import("{ broken"));
    }
}
