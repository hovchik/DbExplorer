using System.Diagnostics;
using System.Text;

namespace DbExplorer.Application.Api;

/// <summary>Sends requests and records them in the history.</summary>
public sealed class ApiRequestRunner(HttpClient http, ApiCollectionStore? store = null)
{
    /// <summary>Response bodies beyond this are cut, so a huge download cannot exhaust memory.</summary>
    public const int MaxBodyBytes = 10 * 1024 * 1024;

    /// <summary>Resolves the request's variables against <paramref name="collection"/> and <paramref name="environment"/> and sends it.</summary>
    public async Task<ApiResponse> SendAsync(ApiRequest request, ApiCollection? collection, ApiEnvironment? environment,
        CancellationToken ct = default)
    {
        var variables = ApiVariableResolver.For(collection, environment);
        using var message = ApiRequestBuilder.Build(request, collection, variables);

        var history = new ApiHistoryEntry
        {
            SentAt = DateTimeOffset.Now,
            CollectionId = collection?.Id,
            RequestId = request.Id,
            Method = message.Method.Method,
            Url = message.RequestUri?.ToString() ?? ""
        };

        var watch = Stopwatch.StartNew();
        try
        {
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
            var (bytes, truncated) = await ReadBodyAsync(response, ct);
            watch.Stop();

            var headers = response.Headers.Concat(response.Content.Headers)
                .SelectMany(h => h.Value.Select(v => new KeyValuePair<string, string>(h.Key, v)))
                .ToList();
            var result = new ApiResponse
            {
                StatusCode = (int)response.StatusCode,
                ReasonPhrase = response.ReasonPhrase,
                Headers = headers,
                ContentType = response.Content.Headers.ContentType?.ToString(),
                Body = Decode(bytes, response.Content.Headers.ContentType?.CharSet)
                    + (truncated ? $"\n\n… cut at {MaxBodyBytes / (1024 * 1024)} MB" : ""),
                SizeBytes = response.Content.Headers.ContentLength ?? bytes.Length,
                Elapsed = watch.Elapsed
            };
            history.StatusCode = result.StatusCode;
            history.ElapsedMs = (long)watch.Elapsed.TotalMilliseconds;
            await RecordAsync(history);
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            history.ElapsedMs = (long)watch.Elapsed.TotalMilliseconds;
            history.Error = ex is TaskCanceledException ? "The request timed out." : ex.Message;
            await RecordAsync(history);
            throw new HttpRequestException(history.Error, ex);
        }
    }

    private async Task RecordAsync(ApiHistoryEntry entry)
    {
        if (store is null) return;
        try
        {
            await store.AppendHistoryAsync(entry);
        }
        catch (IOException)
        {
            // History is a convenience; a locked or full disk must not fail the request itself.
        }
    }

    private static async Task<(byte[] Bytes, bool Truncated)> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            var room = MaxBodyBytes - (int)buffer.Length;
            if (read > room)
            {
                buffer.Write(chunk, 0, room);
                return (buffer.ToArray(), true);
            }
            buffer.Write(chunk, 0, read);
        }
        return (buffer.ToArray(), false);
    }

    private static string Decode(byte[] bytes, string? charset)
    {
        var encoding = Encoding.UTF8;
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                encoding = Encoding.GetEncoding(charset.Trim('"'));
            }
            catch (ArgumentException)
            {
                // Unknown charset: UTF-8 is the best guess.
            }
        }
        return encoding.GetString(bytes);
    }
}
