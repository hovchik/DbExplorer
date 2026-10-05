namespace DbExplorer.Application.Api;

/// <summary>Reads one export format (Postman, Insomnia…) into <see cref="ApiCollection"/>s and <see cref="ApiEnvironment"/>s.</summary>
public interface IApiCollectionImporter
{
    /// <summary>Shown to the user, for example "Postman v2.1".</summary>
    string FormatName { get; }

    /// <summary>True when <paramref name="content"/> looks like this format; cheap, never throws.</summary>
    bool CanImport(string content);

    /// <summary>Reads the file. Throws <see cref="InvalidDataException"/> when it is not valid for this format.</summary>
    ApiImportResult Import(string content);
}

/// <summary>Picks the importer that recognises a file and runs it.</summary>
public sealed class ApiCollectionImporter(IEnumerable<IApiCollectionImporter> importers)
{
    public ApiCollectionImporter()
        : this([new PostmanCollectionImporter(), new InsomniaCollectionImporter()])
    {
    }

    public IReadOnlyList<IApiCollectionImporter> Importers { get; } = importers.ToList();

    public IApiCollectionImporter? Detect(string content) => Importers.FirstOrDefault(i => i.CanImport(content));

    public ApiImportResult Import(string content)
    {
        var importer = Detect(content)
            ?? throw new InvalidDataException(
                $"The file is not a supported collection. Supported formats: {string.Join(", ", Importers.Select(i => i.FormatName))}.");
        return importer.Import(content);
    }

    public async Task<ApiImportResult> ImportFileAsync(string path, CancellationToken ct = default)
        => Import(await File.ReadAllTextAsync(path, ct));
}
