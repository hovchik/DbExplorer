using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;

namespace DbExplorer.Application.Assistant;

/// <summary>Sends one question to a language model and returns its text answer.</summary>
public interface IAssistantModel
{
    Task<string> AskAsync(string system, string question, CancellationToken ct);
}

/// <summary>A failure the user should read as is (bad key, rate limit, refusal).</summary>
public sealed class AssistantException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Asks Claude through the Anthropic Messages API with the user's own key.</summary>
public sealed class ClaudeAssistantModel(AssistantSettings settings, HttpClient? httpClient = null) : IAssistantModel
{
    private AnthropicClient? _client;
    private string? _clientKey;

    public async Task<string> AskAsync(string system, string question, CancellationToken ct)
    {
        if (settings.ApiKey is not { Length: > 0 } key)
            throw new AssistantException("Set your Anthropic API key in AI ▾ › Settings first.");

        // One client per key, so its connections are reused between questions.
        if (_client is null || _clientKey != key)
        {
            _client = httpClient is null
                ? new AnthropicClient { ApiKey = key }
                : new AnthropicClient { ApiKey = key, HttpClient = httpClient };
            _clientKey = key;
        }
        var client = _client;

        BetaMessage response;
        try
        {
            response = await client.Beta.Messages.Create(new MessageCreateParams
            {
                Model = settings.Model,
                MaxTokens = 16000,
                // The system prompt (instructions + schema) stays the same between questions on one database: cache it.
                CacheControl = new BetaCacheControlEphemeral(),
                OutputConfig = new BetaOutputConfig { Effort = Effort.Medium },
                // If the model declines for policy reasons, let the API retry on its default fallback model.
                Betas = ["server-side-fallback-2026-07-01"],
                Fallbacks = new Default(),
                System = system,
                Messages = [new() { Role = Role.User, Content = question }]
            }, ct);
        }
        catch (AnthropicUnauthorizedException ex)
        {
            throw new AssistantException("The Anthropic API key was not accepted. Check it in AI ▾ › Settings.", ex);
        }
        catch (AnthropicRateLimitException ex)
        {
            throw new AssistantException("The Anthropic API is rate limiting this key. Try again in a moment.", ex);
        }
        catch (AnthropicApiException ex)
        {
            throw new AssistantException("The Anthropic API returned an error: " + ex.Message, ex);
        }
        catch (HttpRequestException ex)
        {
            throw new AssistantException("Could not reach the Anthropic API: " + ex.Message, ex);
        }

        if (response.StopReason == BetaStopReason.Refusal)
            throw new AssistantException("Claude declined to answer this request.");

        var text = string.Concat(response.Content.Select(b => b.Value).OfType<BetaTextBlock>().Select(t => t.Text));
        if (string.IsNullOrWhiteSpace(text)) throw new AssistantException("Claude returned an empty answer.");
        return text;
    }
}
