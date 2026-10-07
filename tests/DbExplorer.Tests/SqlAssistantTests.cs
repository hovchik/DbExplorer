using System.Net;
using System.Text;
using System.Text.Json;
using DbExplorer.Application;
using DbExplorer.Application.Assistant;
using DbExplorer.Application.Connections;

namespace DbExplorer.Tests;

/// <summary>The Query tab's AI assistant: what schema it describes, what it asks, and how the API call is made
/// (against a fake HTTP handler; nothing reaches the real API).</summary>
public class SqlAssistantTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbx-assistant-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // ----- Schema context -----

    [Fact]
    public void Schema_lists_tables_with_column_types_and_keys()
    {
        var schema = SchemaContextBuilder.Build(TestSnapshots.Shop(), database: null, focusText: null);

        Assert.Contains("table sales.Orders (OrderId int pk, CustomerId int, OrderDate datetime2(7))", schema);
        Assert.Contains("  fk (CustomerId) -> dbo.Customers (CustomerId)", schema);
        Assert.Contains("view sales.vOrderTotals", schema);
        Assert.Contains("procedure dbo.usp_GetCustomer", schema);
    }

    [Fact]
    public void Schema_never_includes_row_counts()
    {
        var schema = SchemaContextBuilder.Build(TestSnapshots.Shop(), database: null, focusText: null);

        Assert.DoesNotContain("1000", schema);
        Assert.DoesNotContain("5000", schema);
    }

    [Fact]
    public void Schema_of_a_picked_database_leaves_the_others_out()
    {
        var schema = SchemaContextBuilder.Build(TestSnapshots.TwoDatabases(), "Billing", focusText: null);

        Assert.Contains("table dbo.Invoices (InvoiceId int pk)", schema);
        Assert.Contains("table dbo.Transactions (InvoiceId int pk)", schema);
        Assert.DoesNotContain("Customers", schema);
        Assert.DoesNotContain("SaleId", schema);
    }

    [Fact]
    public void Tables_named_in_the_question_survive_when_the_schema_is_cut_short()
    {
        var schema = SchemaContextBuilder.Build(TestSnapshots.Shop(), database: null, focusText: "count the products", maxChars: 80);

        Assert.StartsWith("table dbo.Products (ProductId int pk, Title nvarchar(100))", schema);
        Assert.Contains("more objects not listed", schema);
        Assert.DoesNotContain("Customers", schema);
    }

    // ----- Prompts and answers -----

    [Fact]
    public async Task Write_sends_the_request_the_dialect_and_the_schema_and_returns_the_sql()
    {
        var model = new FakeModel("Here you go:\n```sql\nSELECT TOP (10) c.Name FROM dbo.Customers c;\n```\nThis lists ten customers.");
        var assistant = new SqlAssistant(model);

        var answer = await assistant.WriteAsync(new AssistantContext("SqlServer", TestSnapshots.Shop(), null),
            "ten customer names", currentSql: "SELECT 1", CancellationToken.None);

        Assert.Equal("SELECT TOP (10) c.Name FROM dbo.Customers c;", answer.Sql);
        Assert.Equal(AssistantTask.Write, answer.Task);
        Assert.Contains("Microsoft SQL Server (T-SQL)", model.System);
        Assert.Contains("table dbo.Customers (CustomerId int pk, Name nvarchar(100), Email nvarchar(200))", model.System);
        Assert.Contains("never run", model.System);
        Assert.Contains("ten customer names", model.Question);
        Assert.Contains("SELECT 1", model.Question);
    }

    [Fact]
    public async Task The_system_prompt_is_the_same_for_every_task_on_one_database_so_it_can_be_cached()
    {
        var model = new FakeModel("ok");
        var assistant = new SqlAssistant(model);
        var context = new AssistantContext("Postgres", TestSnapshots.Shop(), null);

        await assistant.ExplainAsync(context, "SELECT * FROM dbo.Audit", CancellationToken.None);
        var explain = model.System;
        await assistant.FixAsync(context, "SELECT * FROM dbo.Audit", "boom", CancellationToken.None);

        Assert.Contains("PostgreSQL", explain);
        Assert.Contains("boom", model.Question);
        // Ordering by mentioned names is the same here (both mention Audit), so the prompt is byte-identical.
        Assert.Equal(explain, model.System);
    }

    [Fact]
    public async Task Explain_without_sql_in_the_answer_returns_prose_only()
    {
        var assistant = new SqlAssistant(new FakeModel("It counts the orders per customer."));

        var answer = await assistant.ExplainAsync(new AssistantContext("Postgres", TestSnapshots.Shop(), null), "SELECT 1", CancellationToken.None);

        Assert.Null(answer.Sql);
        Assert.Equal("It counts the orders per customer.", answer.Text);
    }

    [Theory]
    [InlineData("```sql\nSELECT 1\n```", "SELECT 1")]
    [InlineData("text\n```SQL \r\nSELECT 2;\r\n```", "SELECT 2;")]
    [InlineData("```postgresql\nSELECT 3\n```", "SELECT 3")]
    [InlineData("```\nSELECT 4\n```", "SELECT 4")]
    [InlineData("first\n```sql\nSELECT 5\n```\nthen\n```sql\nSELECT 6\n```", "SELECT 5")]
    [InlineData("no code here", null)]
    [InlineData("```sql\n\n```", null)]
    public void Extracts_the_first_sql_block(string text, string? expected) =>
        Assert.Equal(expected, SqlAssistant.ExtractSql(text));

    // ----- Claude API call -----

    [Fact]
    public async Task Calls_the_messages_api_with_the_key_model_and_fallbacks()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, MessageJson("end_turn", "```sql\nSELECT 1\n```"));
        var model = new ClaudeAssistantModel(Settings("sk-ant-test-key"), new HttpClient(handler));

        var text = await model.AskAsync("system prompt", "the question", CancellationToken.None);

        Assert.Equal("```sql\nSELECT 1\n```", text);
        var request = Assert.Single(handler.Requests);
        Assert.EndsWith("/v1/messages", request.Uri.AbsolutePath);
        Assert.Equal("sk-ant-test-key", request.Headers["x-api-key"]);
        Assert.Contains("server-side-fallback-2026-07-01", request.Headers["anthropic-beta"]);

        using var body = JsonDocument.Parse(request.Body);
        var root = body.RootElement;
        Assert.Equal(AssistantSettings.DefaultModel, root.GetProperty("model").GetString());
        Assert.Equal("default", root.GetProperty("fallbacks").GetString());
        Assert.Equal("ephemeral", root.GetProperty("cache_control").GetProperty("type").GetString());
        Assert.Contains("system prompt", root.GetProperty("system").ToString());
        Assert.Contains("the question", root.GetProperty("messages").ToString());
        Assert.False(root.TryGetProperty("thinking", out _)); // thinking stays on (adaptive) by default
    }

    [Fact]
    public async Task A_refusal_is_reported_as_an_assistant_error()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, MessageJson("refusal", ""));
        var model = new ClaudeAssistantModel(Settings("sk-ant-test-key"), new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<AssistantException>(() => model.AskAsync("s", "q", CancellationToken.None));
        Assert.Contains("declined", ex.Message);
    }

    [Fact]
    public async Task A_rejected_key_says_to_check_the_settings()
    {
        var handler = new FakeHandler(HttpStatusCode.Unauthorized,
            """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""");
        var model = new ClaudeAssistantModel(Settings("sk-ant-wrong"), new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<AssistantException>(() => model.AskAsync("s", "q", CancellationToken.None));
        Assert.Contains("not accepted", ex.Message);
    }

    [Fact]
    public async Task Without_a_key_nothing_is_sent()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, MessageJson("end_turn", "x"));
        var model = new ClaudeAssistantModel(Settings(null), new HttpClient(handler));

        await Assert.ThrowsAsync<AssistantException>(() => model.AskAsync("s", "q", CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    // ----- Settings -----

    [Fact]
    public void The_key_is_stored_encrypted_and_read_back()
    {
        var protector = new FakeProtector();
        var settings = new AssistantSettings(new AppPaths(_root), protector);
        Assert.False(settings.IsConfigured);

        settings.SetApiKey("  sk-ant-secret-1234  ");

        Assert.True(settings.IsConfigured);
        Assert.DoesNotContain("sk-ant-secret-1234", File.ReadAllText(Path.Combine(_root, "assistant.json")));
        var reloaded = new AssistantSettings(new AppPaths(_root), protector);
        Assert.Equal("sk-ant-secret-1234", reloaded.ApiKey);
    }

    [Fact]
    public void Removing_the_key_hides_the_assistant_and_raises_changed()
    {
        var settings = new AssistantSettings(new AppPaths(_root), new FakeProtector());
        settings.SetApiKey("sk-ant-secret-1234");
        var changed = 0;
        settings.Changed += () => changed++;

        settings.SetApiKey("");

        Assert.False(settings.IsConfigured);
        Assert.Equal(1, changed);
        Assert.False(new AssistantSettings(new AppPaths(_root), new FakeProtector()).IsConfigured);
    }

    [Fact]
    public void Without_a_secret_store_the_key_lasts_only_for_the_session()
    {
        var settings = new AssistantSettings(new AppPaths(_root), new NoSecretProtector());

        settings.SetApiKey("sk-ant-secret-1234");

        Assert.True(settings.IsConfigured);
        Assert.False(settings.CanPersistKey);
        Assert.DoesNotContain("sk-ant", File.ReadAllText(Path.Combine(_root, "assistant.json")));
        Assert.False(new AssistantSettings(new AppPaths(_root), new NoSecretProtector()).IsConfigured);
    }

    [Theory]
    [InlineData("sk-ant-api03-abcdefghijklmnop", "sk-ant-…mnop")]
    [InlineData("short", "•••••")]
    [InlineData(null, "")]
    public void Masks_the_key(string? key, string expected) => Assert.Equal(expected, AssistantSettings.Mask(key));

    // ----- Helpers -----

    private AssistantSettings Settings(string? key)
    {
        var settings = new AssistantSettings(new AppPaths(_root), new FakeProtector());
        settings.SetApiKey(key);
        return settings;
    }

    private static string MessageJson(string stopReason, string text) => JsonSerializer.Serialize(new
    {
        id = "msg_test",
        type = "message",
        role = "assistant",
        model = AssistantSettings.DefaultModel,
        content = text.Length == 0 ? Array.Empty<object>() : new object[] { new { type = "text", text } },
        stop_reason = stopReason,
        stop_sequence = (string?)null,
        usage = new { input_tokens = 10, output_tokens = 5 }
    });

    private sealed class FakeModel(string answer) : IAssistantModel
    {
        public string System { get; private set; } = "";
        public string Question { get; private set; } = "";

        public Task<string> AskAsync(string system, string question, CancellationToken ct)
        {
            System = system;
            Question = question;
            return Task.FromResult(answer);
        }
    }

    private sealed record CapturedRequest(Uri Uri, Dictionary<string, string> Headers, string Body);

    private sealed class FakeHandler(HttpStatusCode status, string responseJson) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var headers = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add(new CapturedRequest(request.RequestUri!, headers, body));
            return new HttpResponseMessage(status) { Content = new StringContent(responseJson, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class FakeProtector : ISecretProtector
    {
        public bool IsSupported => true;
        public string Protect(string plainText) => "enc:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(new string(plainText.Reverse().ToArray())));

        public string? Unprotect(string protectedText) => protectedText.StartsWith("enc:")
            ? new string(Encoding.UTF8.GetString(Convert.FromBase64String(protectedText[4..])).Reverse().ToArray())
            : null;
    }
}
