using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Api;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>A collection, folder or request in the API tree.</summary>
public sealed class ApiTreeNode
{
    public required ApiCollection Collection { get; init; }
    public ApiRequest? Request { get; init; }
    public required string Name { get; init; }
    public ObservableCollection<ApiTreeNode> Children { get; } = [];

    /// <summary>True for a collection (the top level of the tree).</summary>
    public bool IsRoot { get; init; }

    /// <summary>"GET", "POST"… for a request; empty for a folder or collection.</summary>
    public string Badge => Request?.Method ?? "";
}

/// <summary>The API tab: imported Postman / Insomnia collections, environments, and sending requests.</summary>
public partial class ApiViewModel : ViewModelBase
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly ApiCollectionImporter _importer;
    private readonly ApiCollectionStore _store;
    private readonly ApiRequestRunner _runner;
    private readonly List<ApiCollection> _collections = [];
    private CancellationTokenSource? _sendCts;

    public ApiViewModel(ApiCollectionImporter importer, ApiCollectionStore store, ApiRequestRunner runner)
    {
        _importer = importer;
        _store = store;
        _runner = runner;
        _ = LoadAsync();
    }

    public static IReadOnlyList<string> Methods { get; } = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];

    public static IReadOnlyList<ApiBodyMode> BodyModes { get; } = Enum.GetValues<ApiBodyMode>();

    /// <summary>The file types offered by the import dialog.</summary>
    public string SupportedFormats => string.Join(", ", _importer.Importers.Select(i => i.FormatName));

    public ObservableCollection<ApiTreeNode> Tree { get; } = [];

    /// <summary>The first entry is null: no environment, collection variables only.</summary>
    public ObservableCollection<ApiEnvironment?> Environments { get; } = [null];

    /// <summary>The selected request's headers and query parameters; rows added here are written back before sending or saving.</summary>
    public ObservableCollection<ApiKeyValue> Headers { get; } = [];
    public ObservableCollection<ApiKeyValue> QueryParams { get; } = [];

    public ObservableCollection<ApiKeyValue> ResponseHeaders { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];

    [ObservableProperty] private ApiTreeNode? _selectedNode;
    [ObservableProperty] private ApiEnvironment? _selectedEnvironment;
    [ObservableProperty] private ApiRequest? _selectedRequest;
    [ObservableProperty] private string _resolvedUrl = "";
    [ObservableProperty] private string _status = "Import a Postman (v2.0 / v2.1) or Insomnia (v4) export to start.";
    [ObservableProperty] private string _responseStatus = "";
    [ObservableProperty] private string _responseBody = "";
    [ObservableProperty] private bool _isSending;
    [ObservableProperty] private bool _hasUnsavedChanges;

    public bool HasRequest => SelectedRequest is not null;
    public bool HasWarnings => Warnings.Count > 0;

    partial void OnSelectedNodeChanged(ApiTreeNode? value)
    {
        SyncEdits();
        SelectedRequest = value?.Request;
        Headers.Clear();
        QueryParams.Clear();
        foreach (var h in SelectedRequest?.Headers ?? []) Headers.Add(h);
        foreach (var q in SelectedRequest?.QueryParams ?? []) QueryParams.Add(q);
        OnPropertyChanged(nameof(HasRequest));
        RefreshPreview();
        DeleteCollectionCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedRequestChanged(ApiRequest? value) => SendCommand.NotifyCanExecuteChanged();

    partial void OnSelectedEnvironmentChanged(ApiEnvironment? value) => RefreshPreview();

    /// <summary>Called by the view when the URL, a header or the body is edited.</summary>
    public void MarkEdited()
    {
        HasUnsavedChanges = true;
        RefreshPreview();
    }

    /// <summary>Shows the URL as it will be sent, and which variables have no value.</summary>
    public void RefreshPreview()
    {
        SyncEdits();
        if (SelectedNode is not { Request: { } request } node)
        {
            ResolvedUrl = "";
            return;
        }
        var variables = ApiVariableResolver.For(node.Collection, SelectedEnvironment);
        var url = variables.Resolve(request.Url);
        foreach (var h in request.Headers.Where(h => h.Enabled)) variables.Resolve(h.Value);
        variables.Resolve(request.Body.Raw);
        ResolvedUrl = variables.Unresolved.Count == 0
            ? url
            : $"{url}    (no value for: {string.Join(", ", variables.Unresolved)})";
    }

    /// <summary>Copies rows added in the grids back to the request.</summary>
    private void SyncEdits()
    {
        if (SelectedRequest is not { } request) return;
        request.Headers = Headers.Where(h => !string.IsNullOrWhiteSpace(h.Key) || !string.IsNullOrEmpty(h.Value)).ToList();
        request.QueryParams = QueryParams.Where(q => !string.IsNullOrWhiteSpace(q.Key) || !string.IsNullOrEmpty(q.Value)).ToList();
    }

    private async Task LoadAsync()
    {
        try
        {
            _collections.AddRange(await _store.LoadCollectionsAsync());
            foreach (var e in await _store.LoadEnvironmentsAsync()) Environments.Add(e);
            RebuildTree();
            if (_collections.Count > 0) Status = $"{_collections.Count} collection(s), {Environments.Count - 1} environment(s).";
        }
        catch (Exception ex)
        {
            Status = $"Could not load saved collections: {ex.Message}";
        }
    }

    public async Task ImportFileAsync(string path)
    {
        Warnings.Clear();
        ApiImportResult result;
        try
        {
            result = await _importer.ImportFileAsync(path);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Status = $"Could not import {Path.GetFileName(path)}: {ex.Message}";
            OnPropertyChanged(nameof(HasWarnings));
            return;
        }

        await _store.SaveImportAsync(result);
        foreach (var c in result.Collections)
        {
            var i = _collections.FindIndex(x => x.Id == c.Id);
            if (i >= 0) _collections[i] = c;
            else _collections.Add(c);
        }
        foreach (var e in result.Environments)
        {
            var existing = Environments.FirstOrDefault(x => x?.Id == e.Id);
            if (existing is not null) Environments[Environments.IndexOf(existing)] = e;
            else Environments.Add(e);
        }
        RebuildTree();

        foreach (var w in result.Warnings) Warnings.Add(w);
        OnPropertyChanged(nameof(HasWarnings));
        var requests = result.Collections.Sum(c => c.AllRequests().Count());
        Status = $"Imported {Path.GetFileName(path)}: {result.Collections.Count} collection(s), {requests} request(s), "
            + $"{result.Environments.Count} environment(s)" + (result.Warnings.Count > 0 ? $", {result.Warnings.Count} note(s) below." : ".");
        if (result.Environments.Count > 0 && SelectedEnvironment is null) SelectedEnvironment = result.Environments[0];
    }

    private void RebuildTree()
    {
        Tree.Clear();
        foreach (var c in _collections.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var root = new ApiTreeNode { Collection = c, Name = c.Name, IsRoot = true };
            AddChildren(root, c, c.Folders, c.Requests);
            Tree.Add(root);
        }
    }

    private static void AddChildren(ApiTreeNode parent, ApiCollection c, List<ApiFolder> folders, List<ApiRequest> requests)
    {
        foreach (var f in folders)
        {
            var node = new ApiTreeNode { Collection = c, Name = f.Name };
            AddChildren(node, c, f.Folders, f.Requests);
            parent.Children.Add(node);
        }
        foreach (var r in requests)
            parent.Children.Add(new ApiTreeNode { Collection = c, Request = r, Name = r.Name });
    }

    private bool CanSend() => SelectedRequest is not null && !IsSending;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        if (SelectedNode is not { Request: { } request } node) return;
        SyncEdits();
        IsSending = true;
        SendCommand.NotifyCanExecuteChanged();
        _sendCts = new CancellationTokenSource();
        ResponseHeaders.Clear();
        ResponseBody = "";
        ResponseStatus = "Sending…";
        try
        {
            var response = await _runner.SendAsync(request, node.Collection, SelectedEnvironment, _sendCts.Token);
            ResponseStatus = $"{response.StatusCode} {response.ReasonPhrase} · {response.Elapsed.TotalMilliseconds:N0} ms · {FormatSize(response.SizeBytes)}";
            foreach (var h in response.Headers) ResponseHeaders.Add(new ApiKeyValue(h.Key, h.Value));
            ResponseBody = Pretty(response.Body, response.ContentType);
        }
        catch (OperationCanceledException) when (_sendCts.IsCancellationRequested)
        {
            ResponseStatus = "Cancelled.";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or UriFormatException)
        {
            ResponseStatus = $"Failed: {ex.Message}";
        }
        finally
        {
            IsSending = false;
            SendCommand.NotifyCanExecuteChanged();
            _sendCts.Dispose();
            _sendCts = null;
        }
    }

    [RelayCommand]
    private void Cancel() => _sendCts?.Cancel();

    [RelayCommand]
    private async Task SaveAsync()
    {
        SyncEdits();
        try
        {
            await _store.SaveCollectionsAsync(_collections);
            HasUnsavedChanges = false;
            Status = "Saved.";
        }
        catch (IOException ex)
        {
            Status = $"Could not save: {ex.Message}";
        }
    }

    private bool CanDeleteCollection() => SelectedNode is { IsRoot: true };

    [RelayCommand(CanExecute = nameof(CanDeleteCollection))]
    private async Task DeleteCollectionAsync()
    {
        if (SelectedNode is not { IsRoot: true } node) return;
        _collections.Remove(node.Collection);
        Tree.Remove(node);
        SelectedNode = null;
        await _store.SaveCollectionsAsync(_collections);
        Status = $"Removed collection \"{node.Collection.Name}\".";
    }

    [RelayCommand]
    private void AddHeader()
    {
        if (SelectedRequest is null) return;
        Headers.Add(new ApiKeyValue());
        HasUnsavedChanges = true;
    }

    [RelayCommand]
    private void AddQueryParam()
    {
        if (SelectedRequest is null) return;
        QueryParams.Add(new ApiKeyValue());
        HasUnsavedChanges = true;
    }

    private static string Pretty(string body, string? contentType)
    {
        if (contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true || string.IsNullOrWhiteSpace(body)) return body;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return JsonSerializer.Serialize(doc.RootElement, Indented);
        }
        catch (JsonException)
        {
            return body;
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:N1} KB",
        _ => $"{bytes / (1024.0 * 1024):N1} MB"
    };
}
