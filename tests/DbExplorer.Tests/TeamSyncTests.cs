using DbExplorer.Application;
using DbExplorer.Application.Connections;
using DbExplorer.Application.Team;
using DbExplorer.Core.Connections;

namespace DbExplorer.Tests;

/// <summary>Two teammates (alice and bob), each with their own app folder, sharing one team folder.</summary>
public sealed class TeamSyncTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "dbx-team-" + Guid.NewGuid().ToString("N"));
    private string TeamRoot => Path.Combine(_temp, "team");

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); }
        catch (IOException) { }
    }

    private TeamSync Member(string name, ISecretProtector? protector = null)
    {
        var sync = new TeamSync(new TeamSyncStore(new AppPaths(Path.Combine(_temp, name)), protector ?? new NoSecretProtector()));
        sync.SetFolder(TeamRoot);
        sync.MemberName = name;
        return sync;
    }

    private static ConnectionProfile Profile(string name, string? password = null) => new()
    {
        Name = name, ProviderKey = "postgres", Host = "db.local", Port = 5432, Database = "app",
        UserName = "admin", Password = password, SavePassword = password is not null
    };

    private static TeamItem Item(TeamSync sync, string name) => sync.Scan().Items.Single(i => i.Name == name);

    [Fact]
    public void Shared_query_shows_as_new_to_a_teammate_until_seen()
    {
        var alice = Member("alice");
        var bob = Member("bob");

        var written = alice.ShareText(TeamItemKind.Query, "Reports/Monthly sales", "select 1;");

        Assert.Equal("queries/Reports/Monthly sales.sql", written.RelativePath);
        Assert.False(written.Conflict);
        Assert.Equal(0, alice.Scan().ChangedCount);
        var item = Assert.Single(bob.Scan().Items);
        Assert.Equal(TeamItemStatus.New, item.Status);
        Assert.Equal("Reports/Monthly sales", item.Name);
        Assert.Equal("select 1;", bob.ReadText(item));

        bob.MarkSeen([item]);
        Assert.Equal(0, bob.Scan().ChangedCount);

        alice.ShareText(TeamItemKind.Query, "Reports/Monthly sales", "select 2;");
        Assert.Equal(TeamItemStatus.Updated, Item(bob, "Reports/Monthly sales").Status);
    }

    [Fact]
    public void Saving_over_a_teammates_newer_version_keeps_both()
    {
        var alice = Member("alice");
        var bob = Member("bob");
        alice.ShareText(TeamItemKind.Query, "Orders", "select 1;");
        bob.MarkSeen([Item(bob, "Orders")]);

        alice.ShareText(TeamItemKind.Query, "Orders", "select 2;");      // bob has not seen this one
        var result = bob.ShareText(TeamItemKind.Query, "Orders", "select 3;");

        Assert.True(result.Conflict);
        Assert.Equal("queries/Orders (bob).sql", result.RelativePath);
        Assert.Equal("select 2;", File.ReadAllText(Path.Combine(TeamRoot, "queries", "Orders.sql")));
        Assert.Equal("select 3;", File.ReadAllText(Path.Combine(TeamRoot, "queries", "Orders (bob).sql")));
    }

    [Fact]
    public void Saving_over_the_version_you_last_saw_replaces_it()
    {
        var alice = Member("alice");
        var bob = Member("bob");
        alice.ShareText(TeamItemKind.Snippet, "Active customers", "select * from customers where active;");
        bob.MarkSeen([Item(bob, "Active customers")]);

        var result = bob.ShareText(TeamItemKind.Snippet, "Active customers", "select * from customers where active = true;");

        Assert.False(result.Conflict);
        Assert.Equal("snippets/Active customers.sql", result.RelativePath);
        alice.Scan();
        var snippet = Assert.Single(alice.Snippets);
        Assert.Equal("select * from customers where active = true;", snippet.Sql);
    }

    [Fact]
    public void A_name_used_by_a_file_you_never_saw_is_not_overwritten()
    {
        var alice = Member("alice");
        var bob = Member("bob");
        alice.ShareText(TeamItemKind.Snippet, "Top", "select top 10 *");

        var result = bob.ShareText(TeamItemKind.Snippet, "Top", "select * limit 10");

        Assert.True(result.Conflict);
        Assert.Equal(2, alice.Scan().Items.Count);
    }

    [Fact]
    public void Shared_connection_without_passwords_never_contains_them()
    {
        var alice = Member("alice");
        alice.ShareConnection(Profile("Shop", "s3cret!"), includePasswords: false);

        var text = File.ReadAllText(Path.Combine(TeamRoot, "connections", "Shop.dbxconnections"));
        Assert.DoesNotContain("s3cret!", text);
        Assert.False(ConnectionTransfer.Read(text).HasPasswords);
    }

    [Fact]
    public void Sharing_passwords_needs_a_team_password_and_encrypts_them()
    {
        var alice = Member("alice");
        Assert.Throws<InvalidOperationException>(() => alice.ShareConnection(Profile("Shop", "s3cret!"), includePasswords: true));

        alice.SetTeamPassword("team pass", remember: false);
        alice.ShareConnection(Profile("Shop", "s3cret!"), includePasswords: true);

        var text = File.ReadAllText(Path.Combine(TeamRoot, "connections", "Shop.dbxconnections"));
        Assert.DoesNotContain("s3cret!", text);

        var bob = Member("bob");
        var withoutPassword = bob.PullConnections([Item(bob, "Shop")], []);
        Assert.True(withoutPassword.PasswordsLeftOut);
        Assert.Null(Assert.Single(withoutPassword.Profiles).Password);

        var carol = Member("carol");
        carol.SetTeamPassword("team pass", remember: false);
        var withPassword = carol.PullConnections([Item(carol, "Shop")], []);
        Assert.False(withPassword.PasswordsLeftOut);
        Assert.Equal("s3cret!", Assert.Single(withPassword.Profiles).Password);
    }

    [Fact]
    public void Wrong_team_password_pulls_without_passwords()
    {
        var alice = Member("alice");
        alice.SetTeamPassword("right", remember: false);
        alice.ShareConnection(Profile("Shop", "s3cret!"), includePasswords: true);
        var bob = Member("bob");
        bob.SetTeamPassword("wrong", remember: false);

        var result = bob.PullConnections([Item(bob, "Shop")], []);

        Assert.True(result.PasswordsLeftOut);
        Assert.Null(Assert.Single(result.Profiles).Password);
    }

    [Fact]
    public void Pulled_connection_is_updated_in_place_and_keeps_the_local_password()
    {
        var alice = Member("alice");
        var bob = Member("bob");
        var shop = Profile("Shop");
        alice.ShareConnection(shop, includePasswords: false);

        var first = bob.PullConnections([Item(bob, "Shop")], []);
        var mine = Assert.Single(first.Profiles);
        Assert.Equal(["Shop"], first.Added);
        Assert.Equal(shop.Id, mine.Id);
        mine.Password = "bobs own";        // typed on first connect: not an edit
        mine.SavePassword = true;

        shop.Database = "app2";
        alice.ShareConnection(shop, includePasswords: false);
        Assert.Equal(TeamItemStatus.Updated, Item(bob, "Shop").Status);
        var second = bob.PullConnections([Item(bob, "Shop")], [mine]);

        var updated = Assert.Single(second.Profiles);
        Assert.Equal(["Shop"], second.Updated);
        Assert.Equal("app2", updated.Database);
        Assert.Equal("bobs own", updated.Password);
        Assert.True(updated.SavePassword);
        Assert.Equal(0, bob.Scan().ChangedCount);
    }

    [Theory]
    [InlineData("host")]
    [InlineData("port")]
    [InlineData("user")]
    [InlineData("ssh host")]
    [InlineData("ssh user")]
    public void Shared_update_pointing_elsewhere_drops_the_local_passwords(string change)
    {
        var alice = Member("alice");
        var bob = Member("bob");
        var shop = Profile("Shop");
        shop.Ssh.Enabled = true;
        shop.Ssh.Host = "bastion.local";
        shop.Ssh.UserName = "tunnel";
        alice.ShareConnection(shop, includePasswords: false);
        var mine = Assert.Single(bob.PullConnections([Item(bob, "Shop")], []).Profiles);
        mine.Password = "bobs own";
        mine.SavePassword = true;
        mine.Ssh.Password = "ssh secret";
        mine.Ssh.Passphrase = "key secret";

        switch (change)
        {
            case "host": shop.Host = "evil.example"; break;
            case "port": shop.Port = 15432; break;
            case "user": shop.UserName = "someone"; break;
            case "ssh host": shop.Ssh.Host = "evil.example"; break;
            case "ssh user": shop.Ssh.UserName = "someone"; break;
        }
        alice.ShareConnection(shop, includePasswords: false);
        var updated = Assert.Single(bob.PullConnections([Item(bob, "Shop")], [mine]).Profiles);

        Assert.Null(updated.Password);
        Assert.Null(updated.Ssh.Password);
        Assert.Null(updated.Ssh.Passphrase);
    }

    [Fact]
    public void Shared_update_to_the_same_server_keeps_the_local_ssh_passwords()
    {
        var alice = Member("alice");
        var bob = Member("bob");
        var shop = Profile("Shop");
        shop.Ssh.Enabled = true;
        shop.Ssh.Host = "bastion.local";
        shop.Ssh.UserName = "tunnel";
        alice.ShareConnection(shop, includePasswords: false);
        var mine = Assert.Single(bob.PullConnections([Item(bob, "Shop")], []).Profiles);
        mine.Password = "bobs own";
        mine.Ssh.Password = "ssh secret";

        shop.Database = "other";
        alice.ShareConnection(shop, includePasswords: false);
        var updated = Assert.Single(bob.PullConnections([Item(bob, "Shop")], [mine]).Profiles);

        Assert.Equal("other", updated.Database);
        Assert.Equal("bobs own", updated.Password);
        Assert.Equal("ssh secret", updated.Ssh.Password);
    }

    [Fact]
    public void A_file_that_cannot_be_read_is_skipped_and_stays_new()
    {
        var alice = Member("alice");
        var bob = Member("bob");
        alice.ShareConnection(Profile("Archive"), includePasswords: false);
        alice.ShareConnection(Profile("Shop"), includePasswords: false);
        var archive = Path.Combine(TeamRoot, "connections", "Archive.dbxconnections");
        var good = File.ReadAllText(archive);
        File.WriteAllText(archive, "{ not json");

        var result = bob.PullConnections(bob.Scan().Items, []);

        Assert.Equal(["Shop"], result.Added);
        Assert.Equal("Shop", Assert.Single(result.Profiles).Name);
        Assert.StartsWith("Archive", Assert.Single(result.Failed));
        Assert.Equal(TeamItemStatus.New, Item(bob, "Archive").Status);
        Assert.Equal(TeamItemStatus.Seen, Item(bob, "Shop").Status);

        File.WriteAllText(archive, good);      // the sync tool caught up
        Assert.Contains("Archive", bob.PullConnections([Item(bob, "Archive")], result.Profiles).Added);
    }

    [Fact]
    public void Connection_edited_on_both_sides_keeps_both()
    {
        var alice = Member("alice");
        var bob = Member("bob");
        var shop = Profile("Shop");
        alice.ShareConnection(shop, includePasswords: false);
        var mine = Assert.Single(bob.PullConnections([Item(bob, "Shop")], []).Profiles);

        mine.Database = "bobs_db";          // edited locally
        shop.Database = "alices_db";        // and by the team
        alice.ShareConnection(shop, includePasswords: false);
        var result = bob.PullConnections([Item(bob, "Shop")], [mine]);

        Assert.Equal(2, result.Profiles.Count);
        Assert.Equal(["Shop (team)"], result.KeptBoth);
        var local = result.Profiles.Single(p => p.Name == "Shop");
        var team = result.Profiles.Single(p => p.Name == "Shop (team)");
        Assert.Equal("bobs_db", local.Database);
        Assert.NotEqual(shop.Id, local.Id);
        Assert.Equal("alices_db", team.Database);
        Assert.Equal(shop.Id, team.Id);
    }

    [Fact]
    public void New_shared_connection_with_a_taken_name_is_added_next_to_it()
    {
        var alice = Member("alice");
        var bob = Member("bob");
        alice.ShareConnection(Profile("Shop"), includePasswords: false);
        var bobsOwn = Profile("Shop");

        var result = bob.PullConnections([Item(bob, "Shop")], [bobsOwn]);

        Assert.Equal(2, result.Profiles.Count);
        Assert.Equal(["Shop (2)"], result.KeptBoth);
        Assert.Same(bobsOwn, result.Profiles[0]);
    }

    [Fact]
    public void Sharing_a_connection_a_teammate_changed_keeps_both_as_separate_connections()
    {
        var alice = Member("alice");
        var bob = Member("bob");
        var shop = Profile("Shop");
        alice.ShareConnection(shop, includePasswords: false);
        var mine = Assert.Single(bob.PullConnections([Item(bob, "Shop")], []).Profiles);

        shop.Port = 6543;
        alice.ShareConnection(shop, includePasswords: false);
        mine.Port = 7000;
        var result = bob.ShareConnection(mine, includePasswords: false);

        Assert.True(result.Conflict);
        Assert.Equal("connections/Shop (bob).dbxconnections", result.RelativePath);
        var copy = Assert.Single(ConnectionTransfer.Profiles(ConnectionTransfer.Read(File.ReadAllText(Path.Combine(TeamRoot, result.RelativePath)))));
        Assert.Equal("Shop (bob)", copy.Name);
        Assert.NotEqual(shop.Id, copy.Id);
        Assert.Equal(7000, copy.Port);
    }

    [Fact]
    public void A_different_connection_with_the_same_name_is_not_overwritten()
    {
        var alice = Member("alice");
        var bob = Member("bob");
        alice.ShareConnection(Profile("Shop"), includePasswords: false);
        bob.MarkSeen(bob.Scan().Items);

        var result = bob.ShareConnection(Profile("Shop"), includePasswords: false);

        Assert.True(result.Conflict);
    }

    [Fact]
    public void Sharing_an_unchanged_connection_again_writes_nothing()
    {
        var alice = Member("alice");
        var bob = Member("bob");
        var shop = Profile("Shop");
        alice.ShareConnection(shop, includePasswords: false);
        bob.MarkSeen(bob.Scan().Items);

        var again = alice.ShareConnection(shop, includePasswords: false);

        Assert.True(again.Unchanged);
        Assert.Equal(0, bob.Scan().ChangedCount);
    }

    [Fact]
    public void Renamed_connection_replaces_its_old_file()
    {
        var alice = Member("alice");
        var shop = Profile("Shop");
        alice.ShareConnection(shop, includePasswords: false);

        shop.Name = "Shop (EU)";
        alice.ShareConnection(shop, includePasswords: false);

        var item = Assert.Single(alice.Scan().Items);
        Assert.Equal("Shop (EU)", item.Name);
    }

    [Fact]
    public void Remove_deletes_only_the_version_you_saw()
    {
        var alice = Member("alice");
        var bob = Member("bob");
        alice.ShareText(TeamItemKind.Query, "Orders", "select 1;");
        var seen = Item(bob, "Orders");
        alice.ShareText(TeamItemKind.Query, "Orders", "select 2;");

        Assert.False(bob.Remove(seen));
        Assert.True(bob.Remove(Item(bob, "Orders")));
        Assert.Empty(alice.Scan().Items);
        Assert.Equal(["queries/Orders.sql"], alice.Scan().Removed);
    }

    [Fact]
    public void Temporary_hidden_and_unrelated_files_are_ignored()
    {
        var alice = Member("alice");
        Directory.CreateDirectory(Path.Combine(TeamRoot, "queries", ".git"));
        Directory.CreateDirectory(Path.Combine(TeamRoot, "snippets"));
        File.WriteAllText(Path.Combine(TeamRoot, "queries", ".git", "x.sql"), "x");
        File.WriteAllText(Path.Combine(TeamRoot, "queries", "~lock.sql"), "x");
        File.WriteAllText(Path.Combine(TeamRoot, "queries", "notes.txt"), "x");
        File.WriteAllText(Path.Combine(TeamRoot, "readme.sql"), "x");
        File.WriteAllText(Path.Combine(TeamRoot, "snippets", "ok.sql"), "select 1");

        var item = Assert.Single(alice.Scan().Items);
        Assert.Equal(TeamItemKind.Snippet, item.Kind);
    }

    [Fact]
    public void Missing_folder_is_reported_not_thrown()
    {
        var alice = Member("alice");
        alice.SetFolder(Path.Combine(_temp, "nowhere"));

        Assert.True(alice.Scan().FolderMissing);
    }

    [Fact]
    public void Changing_folder_forgets_what_was_seen()
    {
        var alice = Member("alice");
        alice.ShareText(TeamItemKind.Query, "Orders", "select 1;");
        alice.SetFolder(Path.Combine(_temp, "other"));
        alice.SetFolder(TeamRoot);

        Assert.Equal(TeamItemStatus.New, Assert.Single(alice.Scan().Items).Status);
    }

    [Fact]
    public void State_and_remembered_password_survive_a_restart()
    {
        var protector = new ReversibleProtector();
        var alice = Member("alice", protector);
        alice.SetTeamPassword("team pass", remember: true);
        alice.ShareText(TeamItemKind.Query, "Orders", "select 1;");

        var restarted = new TeamSync(new TeamSyncStore(new AppPaths(Path.Combine(_temp, "alice")), protector));

        Assert.Equal(Path.GetFullPath(TeamRoot), restarted.FolderPath);
        Assert.Equal("team pass", restarted.TeamPassword);
        Assert.Equal(0, restarted.Scan().ChangedCount);
        Assert.DoesNotContain("team pass", File.ReadAllText(Path.Combine(_temp, "alice", "team-sync.json")));
    }

    [Theory]
    [InlineData("Orders: open?", "Orders_ open_")]
    [InlineData("CON", "_CON")]
    [InlineData("  .hidden. ", "hidden")]
    [InlineData("a/b", "a_b")]
    public void File_names_are_safe_on_every_os(string name, string expected) =>
        Assert.Equal(expected, TeamFolder.SafeRelativeName(name, allowFolders: false));

    [Fact]
    public void Query_names_keep_their_folders_but_cannot_leave_the_team_folder()
    {
        Assert.Equal("Reports/Monthly", TeamFolder.SafeRelativeName(@"Reports\ Monthly", allowFolders: true));
        Assert.Equal("Reports/etc", TeamFolder.SafeRelativeName("../Reports/../~etc", allowFolders: true));
        Assert.Throws<InvalidOperationException>(() => new TeamFolder(TeamRoot).FullPath("../outside.sql"));
    }

    [Fact]
    public async Task Watcher_notices_a_new_file()
    {
        Directory.CreateDirectory(Path.Combine(TeamRoot, "queries"));
        using var watcher = new TeamFolderWatcher(TeamRoot, pollInterval: TimeSpan.FromMilliseconds(200), debounce: TimeSpan.FromMilliseconds(50));
        var changed = new TaskCompletionSource();
        watcher.Changed += () => changed.TrySetResult();

        await File.WriteAllTextAsync(Path.Combine(TeamRoot, "queries", "new.sql"), "select 1;");

        Assert.Same(changed.Task, await Task.WhenAny(changed.Task, Task.Delay(TimeSpan.FromSeconds(10))));
    }

    private sealed class ReversibleProtector : ISecretProtector
    {
        public bool IsSupported => true;
        public string Protect(string plainText) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plainText).Reverse().ToArray());
        public string? Unprotect(string protectedText) => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedText).Reverse().ToArray());
    }
}
