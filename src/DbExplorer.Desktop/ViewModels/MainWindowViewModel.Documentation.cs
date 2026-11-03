using System.Diagnostics;
using System.Text;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Documentation;
using DbExplorer.Application.Metadata;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Abstractions;
using DbExplorer.Desktop.Services;
using DbExplorer.Desktop.Views;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>Export database documentation: one HTML or Markdown file with tables, columns, keys, comments and the diagram.</summary>
public partial class MainWindowViewModel
{
    [RelayCommand(CanExecute = nameof(CanUseSession))]
    private Task ExportDocumentationAsync() => ExportDocumentationAsync(null, null);

    /// <summary>Asks what to export (preselecting <paramref name="database"/> and <paramref name="schema"/>), where to
    /// save it, then reads and writes it with the usual progress line and Cancel button.</summary>
    internal async Task ExportDocumentationAsync(string? database, string? schema)
    {
        if (Session is not { } session || IsBusy) return;

        IReadOnlyList<string> databases;
        try
        {
            databases = await session.GetServerDatabasesAsync();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("documentation: list databases", ex);
            databases = session.Snapshot.Databases;
        }
        var preselect = database ?? (string.IsNullOrEmpty(session.Profile.Database) ? null : session.Profile.Database)
                        ?? Query.SelectedDocument?.CurrentDatabase;
        if (preselect is not null && !databases.Contains(preselect, StringComparer.OrdinalIgnoreCase))
            databases = [preselect, .. databases];

        var request = await _dialogs.PromptDocumentationAsync(new DocumentationPrompt(databases, preselect, schema, async db =>
        {
            var snapshot = await SnapshotFor(session, db);
            return snapshot.Objects
                .Where(o => string.IsNullOrEmpty(db) || string.IsNullOrEmpty(o.Database) || string.Equals(o.Database, db, StringComparison.OrdinalIgnoreCase))
                .Select(o => o.Schema).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase).ToList();
        }));
        if (request is null || !ReferenceEquals(session, Session)) return;

        var html = request.Format == DocumentationFormat.Html;
        var baseName = string.Join("_", ((request.Database ?? session.Profile.Name) + (request.Options.Schema is { } s ? "-" + s : ""))
            .Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        var path = await _dialogs.PickSaveFilePathAsync("Export database documentation", baseName + "-documentation",
            html ? "html" : "md", html ? "HTML page" : "Markdown");
        if (path is null || !ReferenceEquals(session, Session) || IsBusy) return;

        var ct = BeginLoad();
        var progress = LoadProgress(ct);
        IDatabaseProvider? ownProvider = null;
        try
        {
            progress.Report("Reading the catalog…");
            var snapshot = await SnapshotFor(session, request.Database);
            var provider = session.Provider;
            if (!UsesSessionProvider(session, request.Database))
            {
                // Another database on a single-database connection: read its comments and constraints over a
                // short-lived connection of its own, the same way its catalog was read.
                var profile = session.Profile.Clone();
                profile.Database = request.Database!;
                provider = ownProvider = session.Factory.Create(profile);
            }

            var doc = await Task.Run(() => DocumentationBuilder.LoadAsync(provider, snapshot, request.Database, request.Options, progress, ct), ct);
            doc = doc with
            {
                Connection = session.Profile.Name,
                Engine = session.Factory.DisplayName,
                ServerVersion = session.ServerVersion
            };
            var text = await Task.Run(() => html ? DocumentationWriter.ToHtml(doc) : DocumentationWriter.ToMarkdown(doc), ct);
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(false), ct);

            var summary = $"{doc.Tables.Count} table(s), {doc.Views.Count} view(s), {doc.Routines.Count} routine(s)";
            StatusText = $"Documentation of {doc.Title} saved to {Path.GetFileName(path)}: {summary}" +
                         (doc.Notes.Count > 0 ? $". {doc.Notes[^1]}" : "");
            if (request.OpenWhenDone) OpenFile(path);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Documentation export cancelled.";
        }
        catch (Exception ex)
        {
            ErrorLog.Write("documentation export", ex);
            ShowError("Could not export the documentation: " + ex.Message);
        }
        finally
        {
            if (ownProvider is not null) await ownProvider.DisposeAsync();
            EndLoad();
        }
    }

    /// <summary>The session's own provider can read the database: the connection's own one, or any database of a
    /// server-wide catalog.</summary>
    private static bool UsesSessionProvider(DatabaseSession session, string? database) =>
        string.IsNullOrEmpty(database) ||
        string.Equals(session.Profile.Database, database, StringComparison.OrdinalIgnoreCase) ||
        session.Snapshot.ContainsDatabase(database);

    private Task<MetadataSnapshot> SnapshotFor(DatabaseSession session, string? database) =>
        string.IsNullOrEmpty(database) ? Task.FromResult(session.Snapshot) : _sessions.GetDatabaseSnapshotAsync(session, database);

    private void OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ErrorLog.Write("documentation: open file", ex);
        }
    }
}
