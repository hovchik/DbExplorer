using DbExplorer.Application.Export;
using DbExplorer.Application.Query;

namespace DbExplorer.Tests;

public class RowJsonAndImageTests
{
    [Fact]
    public void A_row_becomes_one_json_object_with_typed_values()
    {
        var json = ResultExporter.ToJsonObject(["Id", "Name", "Note", "Id"], [7, "Ann", null, 8]);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(System.Text.Json.JsonValueKind.Object, root.ValueKind);
        Assert.Equal(7, root.GetProperty("Id").GetInt32());
        Assert.Equal("Ann", root.GetProperty("Name").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, root.GetProperty("Note").ValueKind);
        Assert.Equal(4, root.EnumerateObject().Count()); // the repeated column keeps its own key
    }

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0 }, "PNG")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "JPEG")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, "GIF")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4, 0x57, 0x45, 0x42, 0x50 }, "WEBP")]
    [InlineData(new byte[] { 0x01, 0x02, 0x03 }, null)]
    [InlineData(new byte[] { 0x42, 0x4D }, null)] // "BM" alone is too short to be a bitmap
    public void Pictures_are_recognized_from_their_first_bytes(byte[] bytes, string? expected) =>
        Assert.Equal(expected, ImageSniffer.Detect(bytes));
}
