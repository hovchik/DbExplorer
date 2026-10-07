using DbExplorer.Application.Connections;
using DbExplorer.Core.Connections;

namespace DbExplorer.Tests;

public class ConnectionTransferTests
{
    private static ConnectionProfile Profile(string name, string? password = null) => new()
    {
        Name = name, ProviderKey = "postgres", Host = "db.local", Port = 5432, Database = "app",
        UserName = "admin", Password = password, SavePassword = password is not null,
        Environment = ConnectionEnvironment.Production
    };

    [Fact]
    public void Export_without_passwords_leaves_them_out()
    {
        var json = ConnectionTransfer.Export([Profile("main", "s3cret!")], exportPassword: null);

        Assert.DoesNotContain("s3cret!", json);
        var file = ConnectionTransfer.Read(json);
        Assert.False(file.HasPasswords);
        var loaded = Assert.Single(ConnectionTransfer.Profiles(file));
        Assert.Equal("main", loaded.Name);
        Assert.Equal("db.local", loaded.Host);
        Assert.Equal(5432, loaded.Port);
        Assert.Equal("admin", loaded.UserName);
        Assert.Equal(ConnectionEnvironment.Production, loaded.Environment);
        Assert.Null(loaded.Password);
    }

    [Fact]
    public void Export_with_passwords_encrypts_them_and_the_right_password_restores_them()
    {
        var json = ConnectionTransfer.Export([Profile("main", "s3cret!"), Profile("other")], "export pass");

        Assert.DoesNotContain("s3cret!", json);
        var file = ConnectionTransfer.Read(json);
        Assert.True(file.HasPasswords);
        var loaded = ConnectionTransfer.Profiles(file, "export pass");
        Assert.Equal("s3cret!", loaded[0].Password);
        Assert.True(loaded[0].SavePassword);
        Assert.Null(loaded[1].Password);
    }

    [Fact]
    public void A_wrong_export_password_is_reported_even_when_no_connection_has_a_password()
    {
        var withPassword = ConnectionTransfer.Read(ConnectionTransfer.Export([Profile("main", "s3cret!")], "right"));
        var withoutAny = ConnectionTransfer.Read(ConnectionTransfer.Export([Profile("main")], "right"));

        Assert.Throws<WrongExportPasswordException>(() => ConnectionTransfer.Profiles(withPassword, "wrong"));
        Assert.Throws<WrongExportPasswordException>(() => ConnectionTransfer.Profiles(withoutAny, "wrong"));
    }

    [Fact]
    public void A_file_with_passwords_can_be_imported_without_them()
    {
        var file = ConnectionTransfer.Read(ConnectionTransfer.Export([Profile("main", "s3cret!")], "right"));
        Assert.Null(Assert.Single(ConnectionTransfer.Profiles(file)).Password);
    }

    [Fact]
    public void A_tampered_password_is_rejected()
    {
        var json = ConnectionTransfer.Export([Profile("main", "s3cret!")], "right");
        var doc = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        var cipher = Convert.FromBase64String(doc["connections"]![0]!["password"]!.GetValue<string>());
        cipher[^1] ^= 1;
        doc["connections"]![0]!["password"] = Convert.ToBase64String(cipher);

        var file = ConnectionTransfer.Read(doc.ToJsonString());
        Assert.Throws<InvalidDataException>(() => ConnectionTransfer.Profiles(file, "right"));
    }

    [Theory]
    [InlineData(2147483647)]
    [InlineData(10_000_001)]
    [InlineData(999)]
    public void A_file_asking_for_an_unreasonable_key_work_factor_is_refused(int iterations)
    {
        var doc = System.Text.Json.Nodes.JsonNode.Parse(ConnectionTransfer.Export([Profile("main", "s3cret!")], "right"))!;
        doc["passwords"]!["iterations"] = iterations;

        var ex = Assert.Throws<InvalidDataException>(() => ConnectionTransfer.Read(doc.ToJsonString()));
        Assert.Contains("iterations", ex.Message);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"format":"Something.Else","version":1,"connections":[]}""")]
    [InlineData("""{"format":"DbExplorer.Connections","version":99,"connections":[]}""")]
    public void Other_files_are_refused(string json) =>
        Assert.Throws<InvalidDataException>(() => ConnectionTransfer.Read(json));

    [Fact]
    public void Conflicts_match_names_ignoring_case()
    {
        var conflicts = ConnectionTransfer.Conflicts([Profile("Main"), Profile("dev")], [Profile("main"), Profile("new")]);
        Assert.Equal(["main"], conflicts);
    }

    [Fact]
    public void Merge_skip_keeps_the_existing_connection()
    {
        var existing = Profile("main");
        var result = ConnectionTransfer.Merge([existing], [Profile("MAIN"), Profile("new")], ImportConflictChoice.Skip);

        Assert.Equal(["main", "new"], result.Profiles.Select(p => p.Name));
        Assert.Same(existing, result.Profiles[0]);
        Assert.Equal((1, 0, 1), (result.Added, result.Replaced, result.Skipped));
    }

    [Fact]
    public void Merge_overwrite_replaces_in_place_and_keeps_the_id()
    {
        var existing = Profile("main");
        var incoming = Profile("main");
        incoming.Host = "elsewhere";
        var result = ConnectionTransfer.Merge([existing, Profile("b")], [incoming], ImportConflictChoice.Overwrite);

        Assert.Equal(2, result.Profiles.Count);
        Assert.Equal("elsewhere", result.Profiles[0].Host);
        Assert.Equal(existing.Id, result.Profiles[0].Id);
        Assert.Equal(1, result.Replaced);
    }

    [Fact]
    public void Merge_keep_both_renames_and_gives_a_new_id()
    {
        var existing = Profile("main");
        var dupe = Profile("main");
        dupe.Id = existing.Id;
        var result = ConnectionTransfer.Merge([existing, Profile("main (2)")], [dupe], ImportConflictChoice.KeepBoth);

        Assert.Equal(["main", "main (2)", "main (3)"], result.Profiles.Select(p => p.Name));
        Assert.NotEqual(existing.Id, result.Profiles[2].Id);
        Assert.Equal(1, result.Added);
    }
}
