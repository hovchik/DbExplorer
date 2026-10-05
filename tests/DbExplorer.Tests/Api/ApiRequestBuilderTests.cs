using System.Net;
using System.Text;
using DbExplorer.Application;
using DbExplorer.Application.Api;

namespace DbExplorer.Tests.Api;

public class ApiRequestBuilderTests
{
    private static readonly ApiCollection Sample = new PostmanCollectionImporter().Import(Samples.Read("postman-v21.json")).Collections[0];

    private static readonly ApiEnvironment Staging = new PostmanCollectionImporter().Import(Samples.Read("postman-environment.json")).Environments[0];

    private static ApiRequest Request(string name) => Sample.AllRequests().Single(r => r.Request.Name == name).Request;

    private static HttpRequestMessage Build(string name, params ApiVariable[] extra)
        => ApiRequestBuilder.Build(Request(name), Sample, ApiVariableResolver.For(Sample, Staging, extra));

    [Fact]
    public void Resolves_url_and_query_from_the_environment_and_applies_folder_api_key()
    {
        using var message = Build("List pets", new ApiVariable("apiKey", "k-123"));

        Assert.Equal(HttpMethod.Get, message.Method);
        Assert.Equal("https://staging.example.com/pets?limit=10", message.RequestUri!.ToString());
        Assert.Equal("k-123", message.Headers.GetValues("X-Api-Key").Single());
        Assert.Equal("application/json", message.Headers.Accept.Single().MediaType);
        Assert.False(message.Headers.Contains("X-Debug"));
        Assert.False(message.Headers.Contains("Authorization"));
    }

    [Fact]
    public void Collection_bearer_token_comes_from_the_environment()
    {
        using var message = Build("Health");

        Assert.Equal("Bearer staging-token", message.Headers.GetValues("Authorization").Single());
        Assert.Null(message.Content);
    }

    [Fact]
    public void No_auth_request_sends_no_authorization()
    {
        using var message = Build("Get pet");

        Assert.Equal("https://staging.example.com/pets/42", message.RequestUri!.ToString());
        Assert.False(message.Headers.Contains("Authorization"));
    }

    [Fact]
    public async Task Raw_json_body_is_resolved_with_its_content_type()
    {
        using var message = Build("Create pet", new ApiVariable("petName", "Rex"));

        Assert.Equal("application/json", message.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("{ \"name\": \"Rex\" }", await message.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Basic_auth_and_url_encoded_form()
    {
        using var message = Build("Login", new ApiVariable("password", "s3cret"));

        Assert.Equal("Basic", message.Headers.Authorization!.Scheme);
        Assert.Equal("admin:s3cret", Encoding.UTF8.GetString(Convert.FromBase64String(message.Headers.Authorization.Parameter!)));
        Assert.Equal("grant_type=client_credentials", await message.Content!.ReadAsStringAsync());
    }

    [Fact]
    public async Task GraphQL_is_sent_as_json_with_parsed_variables()
    {
        using var message = Build("Query pets");

        Assert.Equal("application/json", message.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("""{"query":"{ pets { id } }","variables":{"first":5}}""", await message.Content.ReadAsStringAsync());
    }

    [Fact]
    public void A_header_written_on_the_request_wins_over_auth_and_content_headers_go_on_the_body()
    {
        var request = new ApiRequest
        {
            Method = "post",
            Url = "example.com/x?a=1",
            Headers = [new("Authorization", "Custom abc"), new("Content-Type", "text/csv"), new("Content-Length", "999")],
            Body = new ApiBody { Mode = ApiBodyMode.Raw, Raw = "a,b", ContentType = "application/json" },
            QueryParams = [new("q", "x y&z")],
            Auth = new ApiAuth { Type = ApiAuthType.Bearer, Token = "ignored" }
        };

        using var message = ApiRequestBuilder.Build(request, null, new ApiVariableResolver());

        Assert.Equal("http://example.com/x?a=1&q=x%20y%26z", message.RequestUri!.AbsoluteUri);
        Assert.Equal("Custom abc", message.Headers.GetValues("Authorization").Single());
        Assert.Equal("text/csv", message.Content!.Headers.ContentType!.MediaType);
        Assert.Equal(3, message.Content.Headers.ContentLength);
    }

    [Fact]
    public void An_unresolved_host_gives_a_readable_error()
    {
        var request = new ApiRequest { Url = "{{nowhere}}/x" };

        var ex = Assert.Throws<InvalidOperationException>(() => ApiRequestBuilder.Build(request, null, new ApiVariableResolver()));
        Assert.Contains("variable", ex.Message);
    }

    [Fact]
    public async Task Runner_returns_the_response_and_records_history()
    {
        var root = Path.Combine(Path.GetTempPath(), "dbx-api-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ApiCollectionStore(new AppPaths(root));
            HttpRequestMessage? seen = null;
            var handler = new StubHandler(req =>
            {
                seen = req;
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent("""{"id": 7}""", Encoding.UTF8, "application/json")
                };
            });
            var runner = new ApiRequestRunner(new HttpClient(handler), store);

            var response = await runner.SendAsync(Request("Health"), Sample, Staging);

            Assert.Equal(201, response.StatusCode);
            Assert.True(response.IsSuccess);
            Assert.Equal("""{"id": 7}""", response.Body);
            Assert.StartsWith("application/json", response.ContentType);
            Assert.Equal("https://staging.example.com/health", seen!.RequestUri!.ToString());

            var entry = Assert.Single(await store.LoadHistoryAsync());
            Assert.Equal((201, "GET", Sample.Id), (entry.StatusCode, entry.Method, entry.CollectionId));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Runner_records_failures()
    {
        var root = Path.Combine(Path.GetTempPath(), "dbx-api-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ApiCollectionStore(new AppPaths(root));
            var runner = new ApiRequestRunner(new HttpClient(new StubHandler(_ => throw new HttpRequestException("refused"))), store);

            await Assert.ThrowsAsync<HttpRequestException>(() => runner.SendAsync(Request("Health"), Sample, Staging));

            Assert.Equal("refused", Assert.Single(await store.LoadHistoryAsync()).Error);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
