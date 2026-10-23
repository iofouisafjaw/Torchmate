using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TerrarAI.Content.Companion;

public sealed record ModelOptions(string Endpoint, string Model, string ApiKey, float Temperature,
    int MaxTokens, int TimeoutSeconds);

public sealed class LocalModelClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public LocalModelClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    public Task<string> SendAsync(ModelOptions options, string system, string state, CancellationToken token) =>
        SendStructuredAsync(options, system, state, CompanionProtocol.Schema, "companion_action", token);

    public async Task<string> SendStructuredAsync(ModelOptions options, string system, string state,
        JsonElement schema, string schemaName, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        bool acquired = false;
        try
        {
            await _gate.WaitAsync(deadline.Token).ConfigureAwait(false);
            acquired = true;
            var payload = new
            {
                model = options.Model, temperature = options.Temperature,
                max_tokens = options.MaxTokens, stream = false, cache_prompt = true,
                chat_template_kwargs = new { enable_thinking = false },
                messages = new[] { new { role = "system", content = system }, new { role = "user", content = state } },
                response_format = new { type = "json_schema", json_schema = new { name = schemaName, schema } }
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, options.Endpoint)
            { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") };
            if (!string.IsNullOrWhiteSpace(options.ApiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
            using var response = await _http.SendAsync(request, deadline.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: deadline.Token).ConfigureAwait(false);
            var choice = document.RootElement.GetProperty("choices")[0];
            if (choice.TryGetProperty("finish_reason", out var finish) && finish.GetString() == "length")
                throw new FormatException("Model output was truncated; increase MaxOutputTokens.");
            var content = choice.GetProperty("message").GetProperty("content").GetString();
            if (string.IsNullOrWhiteSpace(content)) throw new FormatException("Model returned no action.");
            return content;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("Local model request timed out."); }
        finally { if (acquired) _gate.Release(); }
    }

    // POST to the TerrarAI brain sitting in front of the model. Returns null when no brain is
    // listening (plain llama-server answers 404) so callers can fall back to the direct path.
    public async Task<string?> PostBrainAsync(string url, string json, int timeoutSeconds, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        bool acquired = false;
        try
        {
            await _gate.WaitAsync(deadline.Token).ConfigureAwait(false);
            acquired = true;
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            using var response = await _http.SendAsync(request, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            string body = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Brain error {(int)response.StatusCode}: {body}");
            return body;
        }
        catch (HttpRequestException ex) when (ex.InnerException is System.Net.Sockets.SocketException) { return null; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("Advice request timed out."); }
        finally { if (acquired) _gate.Release(); }
    }

    public void Dispose() => _http.Dispose();
}
