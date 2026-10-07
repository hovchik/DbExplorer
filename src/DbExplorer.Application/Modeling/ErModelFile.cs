using System.Text.Json;
using System.Text.Json.Serialization;
using DbExplorer.Application.Copy;
using DbExplorer.Application.Design;

namespace DbExplorer.Application.Modeling;

/// <summary>Saves a model as a .dbxmodel JSON file and reads it back. Only the model is stored: no connection details.</summary>
public static class ErModelFile
{
    public const string Extension = "dbxmodel";
    public const string TypeName = "DbExplorer ER model";
    public const int Version = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Write(ErModel model) => JsonSerializer.Serialize(new FileDto
    {
        Format = "dbexplorer-er-model",
        Version = Version,
        Name = model.Name,
        Provider = model.ProviderKey,
        Database = model.Database,
        Tables = model.Tables.Select(t => new TableDto { Id = t.Id, X = Math.Round(t.X, 1), Y = Math.Round(t.Y, 1), Design = ToDto(t.Design), Baseline = t.Baseline is null ? null : ToDto(t.Baseline) }).ToList()
    }, Options);

    /// <exception cref="FormatException">The text is not a model file, or one from a newer version.</exception>
    public static ErModel Read(string json)
    {
        FileDto? file;
        try
        {
            file = JsonSerializer.Deserialize<FileDto>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new FormatException("This is not an ER model file: " + ex.Message, ex);
        }
        if (file is null || file.Format != "dbexplorer-er-model") throw new FormatException("This is not an ER model file.");
        if (file.Version > Version) throw new FormatException($"This model was saved by a newer DbExplorer (format {file.Version}).");

        var ids = new HashSet<string>();
        return new ErModel
        {
            Name = file.Name ?? "Untitled model",
            ProviderKey = string.IsNullOrEmpty(file.Provider) ? SqlDialect.SqlServerKey : file.Provider,
            Database = file.Database ?? "",
            Tables = (file.Tables ?? []).Where(t => t.Design is not null).Select(t => new ModelTable
            {
                // Two tables with one id (a hand-edited file) would move together on the canvas.
                Id = !string.IsNullOrEmpty(t.Id) && ids.Add(t.Id) ? t.Id : Guid.NewGuid().ToString("N"),
                X = t.X,
                Y = t.Y,
                Design = FromDto(t.Design!, file.Database ?? ""),
                Baseline = t.Baseline is null ? null : FromDto(t.Baseline, file.Database ?? "")
            }).ToList()
        };
    }

    private static DesignDto ToDto(TableDesign d) => new()
    {
        Schema = d.Schema, Name = d.Name, PrimaryKeyName = NullIfEmpty(d.PrimaryKeyName),
        Columns = d.Columns.Select(c => new ColumnDto
        {
            Name = c.Name, OriginalName = c.OriginalName, Type = c.Type, Size = c.Size, Nullable = c.IsNullable,
            PrimaryKey = c.IsPrimaryKey, Identity = c.IsIdentity, Default = c.Default
        }).ToList(),
        ForeignKeys = d.ForeignKeys.Select(f => new ForeignKeyDto
        {
            Name = NullIfEmpty(f.Name), Column = f.Column, ReferencedSchema = f.ReferencedSchema, ReferencedTable = f.ReferencedTable,
            ReferencedColumn = f.ReferencedColumn
        }).ToList(),
        Indexes = d.Indexes.Select(i => new IndexDto { Name = NullIfEmpty(i.Name), Columns = i.Columns.ToList(), Unique = i.IsUnique }).ToList()
    };

    private static TableDesign FromDto(DesignDto d, string database) => new()
    {
        Database = database,
        Schema = d.Schema ?? "",
        Name = d.Name ?? "",
        PrimaryKeyName = d.PrimaryKeyName ?? "",
        Columns = (d.Columns ?? []).Select(c => new ColumnDesign
        {
            Name = c.Name ?? "", OriginalName = c.OriginalName, Type = c.Type ?? "", Size = c.Size, IsNullable = c.Nullable && !c.PrimaryKey,
            IsPrimaryKey = c.PrimaryKey, IsIdentity = c.Identity, Default = c.Default
        }).ToList(),
        ForeignKeys = (d.ForeignKeys ?? []).Select(f => new ForeignKeyDesign
        {
            Name = f.Name ?? "", Column = f.Column ?? "", ReferencedSchema = f.ReferencedSchema ?? "",
            ReferencedTable = f.ReferencedTable ?? "", ReferencedColumn = f.ReferencedColumn ?? ""
        }).ToList(),
        Indexes = (d.Indexes ?? []).Select(i => new IndexDesign { Name = i.Name ?? "", Columns = i.Columns ?? [], IsUnique = i.Unique }).ToList()
    };

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    private sealed class FileDto
    {
        public string? Format { get; set; }
        public int Version { get; set; }
        public string? Name { get; set; }
        public string? Provider { get; set; }
        public string? Database { get; set; }
        public List<TableDto>? Tables { get; set; }
    }

    private sealed class TableDto
    {
        public string? Id { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public DesignDto? Design { get; set; }
        public DesignDto? Baseline { get; set; }
    }

    private sealed class DesignDto
    {
        public string? Schema { get; set; }
        public string? Name { get; set; }
        public string? PrimaryKeyName { get; set; }
        public List<ColumnDto>? Columns { get; set; }
        public List<ForeignKeyDto>? ForeignKeys { get; set; }
        public List<IndexDto>? Indexes { get; set; }
    }

    private sealed class ColumnDto
    {
        public string? Name { get; set; }
        public string? OriginalName { get; set; }
        public string? Type { get; set; }
        public string? Size { get; set; }
        public bool Nullable { get; set; } = true;
        public bool PrimaryKey { get; set; }
        public bool Identity { get; set; }
        public string? Default { get; set; }
    }

    private sealed class ForeignKeyDto
    {
        public string? Name { get; set; }
        public string? Column { get; set; }
        public string? ReferencedSchema { get; set; }
        public string? ReferencedTable { get; set; }
        public string? ReferencedColumn { get; set; }
    }

    private sealed class IndexDto
    {
        public string? Name { get; set; }
        public List<string>? Columns { get; set; }
        public bool Unique { get; set; }
    }
}
