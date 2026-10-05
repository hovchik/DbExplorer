using DbExplorer.Application;
using DbExplorer.Application.Api;

namespace DbExplorer.Tests.Api;

public class ApiCollectionStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbx-api-store-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task An_imported_collection_round_trips_with_its_tree_and_auth()
    {
        var store = new ApiCollectionStore(new AppPaths(_root));
        var import = new PostmanCollectionImporter().Import(Samples.Read("postman-v21.json"));

        await store.SaveImportAsync(import);
        var loaded = Assert.Single(await store.LoadCollectionsAsync());

        Assert.Equal("Pet Store", loaded.Name);
        Assert.Equal(7, loaded.AllRequests().Count());
        Assert.Equal(ApiAuthType.ApiKey, loaded.Folders[0].Auth?.Type);
        Assert.Contains("\"Bearer\"", await File.ReadAllTextAsync(store.CollectionsFile));
    }

    [Fact]
    public async Task Re_importing_replaces_the_collection_with_the_same_name_and_keeps_its_id()
    {
        var store = new ApiCollectionStore(new AppPaths(_root));
        var importer = new PostmanCollectionImporter();
        await store.SaveImportAsync(importer.Import(Samples.Read("postman-v21.json")));
        var firstId = (await store.LoadCollectionsAsync())[0].Id;

        await store.SaveImportAsync(importer.Import(Samples.Read("postman-v21.json")));
        await store.SaveImportAsync(importer.Import(Samples.Read("postman-environment.json")));

        var collection = Assert.Single(await store.LoadCollectionsAsync());
        Assert.Equal(firstId, collection.Id);
        Assert.Equal("Staging", Assert.Single(await store.LoadEnvironmentsAsync()).Name);
    }

    [Fact]
    public async Task History_keeps_the_newest_entries_first()
    {
        var store = new ApiCollectionStore(new AppPaths(_root));

        for (var i = 0; i < 205; i++)
            await store.AppendHistoryAsync(new ApiHistoryEntry { Url = $"http://x/{i}" });

        var history = await store.LoadHistoryAsync();
        Assert.Equal(200, history.Count);
        Assert.Equal("http://x/204", history[0].Url);
    }

    [Fact]
    public async Task A_damaged_file_is_kept_aside()
    {
        var store = new ApiCollectionStore(new AppPaths(_root));
        await File.WriteAllTextAsync(store.CollectionsFile, "[{ broken");

        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadCollectionsAsync());
        Assert.False(File.Exists(store.CollectionsFile));
        Assert.Single(Directory.GetFiles(_root, "api-collections.json.corrupt-*"));
    }
}
