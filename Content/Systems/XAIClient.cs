// TerrarAI replacement implementation, 2026-10-20. See docs/source-replacement.md.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TerrarAI.Content.Companion;
namespace TerrarAI.Content.Systems;

// Legacy type name is a compatibility facade. All transport lives in LocalModelClient.
public sealed class XAIClient : IDisposable
{
    private readonly LocalModelClient _transport;
    public XAIClient(HttpClient? httpClient = null) { _transport = new(httpClient); }
    public void Dispose() => _transport.Dispose();
    private static ModelOptions Options(float? temperature = null, int? tokens = null)
    {
        var settings = TerrarAI_Config.Get();
        return new(settings.BaseEndpoint, settings.Model, settings.GetEffectiveApiKey(), temperature ?? settings.Temperature,
            tokens ?? settings.MaxOutputTokens, settings.RequestTimeoutSeconds);
    }
    public Task<string> SendChatCompletionAsync(string systemPrompt, string userPrompt,
        List<(string role, string content)>? conversationHistory = null, CancellationToken cancellationToken = default) =>
        _transport.SendAsync(Options(), systemPrompt, userPrompt, cancellationToken);
    public Task<string> AskAsync(string state, CancellationToken cancellationToken) =>
        _transport.SendStructuredAsync(Options(.2f, 384), InquiryProtocol.SystemPrompt, state, InquiryProtocol.Schema, "companion_inquiry", cancellationToken);
    public Task<string> PlanAgentStepAsync(string state, CancellationToken cancellationToken, JsonElement? schema = null) =>
        _transport.SendStructuredAsync(Options(.2f, 900), AgentProtocol.SystemPrompt, state, schema ?? AgentProtocol.Schema, "companion_agent_step", cancellationToken);
    public Task<string> AnalyzeDialogueAsync(string state, string source, CancellationToken cancellationToken) =>
        _transport.SendStructuredAsync(Options(0, 256), PreferenceProtocol.SystemPrompt, state, PreferenceProtocol.SchemaForSource(source), "companion_dialogue", cancellationToken);
    public Task<string> AskWikiAsync(string path, string version, string question, object world, CancellationToken cancellationToken, string? brainState = null)
    {
        ModelOptions options = Options(0, 384);
        return Retrieve();
        async Task<string> Retrieve()
        {
            if (brainState != null && Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint))
            {
                string payload = "{\"question\":" + JsonSerializer.Serialize(question) + ",\"version\":" + JsonSerializer.Serialize(version) + ",\"state\":" + brainState + "}";
                string? answer = await _transport.PostBrainAsync(new Uri(endpoint, "/terrarai/advise").ToString(), payload,
                    Math.Max(120, options.TimeoutSeconds), cancellationToken).ConfigureAwait(false);
                if (answer != null) return JsonSerializer.Serialize(BrainInquiryProtocol.Parse(answer, version));
            }
            var evidence = await WikiKnowledge.SearchAsync(path, version, question, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            string state = JsonSerializer.Serialize(new { question, world, wiki = new { game_version = evidence.GameVersion,
                status = evidence.Status, sources = evidence.Sources.Select(s => new { id = s.Id, title = s.Title, excerpt = s.Excerpt }) } });
            string result = await _transport.SendStructuredAsync(options, WikiInquiryProtocol.SystemPrompt, state,
                WikiInquiryProtocol.SchemaFor(evidence), "companion_wiki_inquiry", cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(new WikiInquiryResult(evidence, result));
        }
    }
}
