using DbExplorer.Application.Api;

namespace DbExplorer.Tests.Api;

public class InsomniaCollectionImporterTests
{
    private readonly InsomniaCollectionImporter _importer = new();

    [Fact]
    public void Recognises_Insomnia_exports_only()
    {
        Assert.True(_importer.CanImport(Samples.Read("insomnia-v4.json")));
        Assert.False(_importer.CanImport(Samples.Read("postman-v21.json")));
        Assert.False(_importer.CanImport("{}"));
    }

    [Fact]
    public void Workspace_becomes_a_collection_with_sorted_folders_and_requests()
    {
        var result = _importer.Import(Samples.Read("insomnia-v4.json"));
        var c = Assert.Single(result.Collections);

        Assert.Equal("Orders API", c.Name);
        Assert.Equal("Insomnia v4", c.SourceFormat);
        Assert.Equal("Token", Assert.Single(c.Requests).Name);
        var orders = Assert.Single(c.Folders);
        Assert.Equal(["List orders", "Create order"], orders.Requests.Select(r => r.Name));
    }

    [Fact]
    public void Base_environment_becomes_collection_variables_and_sub_environments_are_kept()
    {
        var result = _importer.Import(Samples.Read("insomnia-v4.json"));
        var c = result.Collections[0];

        Assert.Equal("https://orders.example.com", c.Variables.Single(v => v.Key == "baseUrl").Value);
        Assert.Equal("base-token", c.Variables.Single(v => v.Key == "auth.token").Value);
        var prod = Assert.Single(result.Environments);
        Assert.Equal("Orders API / Production", prod.Name);
        Assert.Equal("https://orders.prod.example.com", Assert.Single(prod.Variables).Value);
    }

    [Fact]
    public void Insomnia_variable_syntax_is_converted()
    {
        var c = _importer.Import(Samples.Read("insomnia-v4.json")).Collections[0];
        var list = c.Folders[0].Requests[0];
        var create = c.Folders[0].Requests[1];

        Assert.Equal("{{baseUrl}}/orders", list.Url);
        Assert.Equal([("status", true), ("page", false)], list.QueryParams.Select(p => (p.Key, p.Enabled)));
        Assert.Equal("{\"sku\": \"{{sku}}\"}", create.Body.Raw);
        Assert.Equal("application/json", create.Body.ContentType);
        Assert.Equal("{{auth.token}}", c.EffectiveAuth(create)?.Token);
    }

    [Fact]
    public void Reads_auth_and_form_bodies_and_warns_about_what_it_skips()
    {
        var result = _importer.Import(Samples.Read("insomnia-v4.json"));
        var token = result.Collections[0].Requests[0];

        Assert.Equal(ApiAuthType.Basic, token.Auth?.Type);
        Assert.Equal(("svc", "pw"), (token.Auth!.Username, token.Auth.Password));
        Assert.Equal(ApiBodyMode.UrlEncoded, token.Body.Mode);
        Assert.Contains(result.Warnings, w => w.Contains("template tags") && w.Contains("\"Token\""));
        Assert.Contains(result.Warnings, w => w.Contains("\"Stream\" is a grpc request"));
    }

    [Fact]
    public void Rejects_other_export_versions()
    {
        var v3 = """{"_type": "export", "__export_format": 3, "resources": []}""";
        Assert.Throws<InvalidDataException>(() => _importer.Import(v3));
    }

    [Fact]
    public void The_dispatcher_picks_the_matching_importer()
    {
        var importer = new ApiCollectionImporter();

        Assert.IsType<InsomniaCollectionImporter>(importer.Detect(Samples.Read("insomnia-v4.json")));
        Assert.IsType<PostmanCollectionImporter>(importer.Detect(Samples.Read("postman-v21.json")));
        Assert.Contains("Supported formats", Assert.Throws<InvalidDataException>(() => importer.Import("""{"a": 1}""")).Message);
    }
}
