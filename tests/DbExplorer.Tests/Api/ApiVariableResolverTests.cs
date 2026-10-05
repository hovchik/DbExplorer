using DbExplorer.Application.Api;

namespace DbExplorer.Tests.Api;

public class ApiVariableResolverTests
{
    private static ApiVariableResolver Resolver(params ApiVariable[] variables) => new(variables);

    [Fact]
    public void Later_scopes_override_earlier_ones_and_disabled_variables_are_ignored()
    {
        var collection = new ApiCollection { Variables = [new("host", "collection"), new("port", "80")] };
        var environment = new ApiEnvironment { Variables = [new("host", "environment"), new("port", "8080", enabled: false)] };
        var resolver = ApiVariableResolver.For(collection, environment, [new ApiVariable("user", "run")]);

        Assert.Equal("environment:80 run", resolver.Resolve("{{host}}:{{port}} {{user}}"));
    }

    [Fact]
    public void Nested_references_and_whitespace_inside_braces_are_resolved()
    {
        var resolver = Resolver(new ApiVariable("scheme", "https"), new ApiVariable("host", "api.test"), new ApiVariable("baseUrl", "{{scheme}}://{{ host }}"));

        Assert.Equal("https://api.test/v1", resolver.Resolve("{{baseUrl}}/v1"));
        Assert.Empty(resolver.Unresolved);
    }

    [Fact]
    public void Unknown_names_stay_as_written_and_are_reported()
    {
        var resolver = Resolver(new ApiVariable("a", "1"));

        Assert.Equal("1-{{missing}}", resolver.Resolve("{{a}}-{{missing}}"));
        Assert.Equal(["missing"], resolver.Unresolved);
    }

    [Fact]
    public void A_self_referencing_variable_does_not_loop_forever()
    {
        var resolver = Resolver(new ApiVariable("a", "x{{a}}"));

        var value = resolver.Resolve("{{a}}");

        Assert.StartsWith("xxxx", value);
        Assert.Contains("a", resolver.Unresolved);
    }

    [Fact]
    public void Dynamic_variables_are_generated()
    {
        var now = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var resolver = new ApiVariableResolver([], () => now, new Random(1));

        Assert.True(Guid.TryParse(resolver.Resolve("{{$guid}}"), out _));
        Assert.Equal(now.ToUnixTimeSeconds().ToString(), resolver.Resolve("{{$timestamp}}"));
        Assert.Equal("2026-01-02T03:04:05.000Z", resolver.Resolve("{{$isoTimestamp}}"));
        Assert.InRange(int.Parse(resolver.Resolve("{{$randomInt}}")), 0, 1000);
        Assert.Equal("{{$unknownDynamic}}", resolver.Resolve("{{$unknownDynamic}}"));
    }

    [Fact]
    public void Text_without_placeholders_and_null_pass_through()
    {
        var resolver = new ApiVariableResolver();

        Assert.Equal("plain { text }", resolver.Resolve("plain { text }"));
        Assert.Equal("", resolver.Resolve(null));
    }
}
