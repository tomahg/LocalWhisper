using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LocalWhisper.Models;

namespace LocalWhisper.Services;

public class LlmService
{
    private readonly HttpClient _http = new();

    /// <summary>
    /// Sends <paramref name="text"/> to the configured LLM backend and returns the processed result.
    /// Returns the original <paramref name="text"/> on any error (graceful fallback).
    /// Returns the original text immediately when LlmMode is Off.
    /// </summary>
    public async Task<string> PostProcessAsync(
        string text,
        AppSettings settings,
        CancellationToken ct = default)
    {
        if (!settings.LlmEnabled || string.IsNullOrWhiteSpace(text))
            return text;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(settings.LlmTimeoutSec));

        try
        {
            return await RunAsync(text, settings.LlmPrompt, settings, timeoutCts.Token);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LlmService] Post-processing failed: {ex.Message}");
            return text;
        }
    }

    private Task<string> RunAsync(string text, string prompt, AppSettings settings, CancellationToken ct) =>
        settings.LlmBackend == LlmBackend.Claude
            ? CallClaudeAsync(text, prompt, settings, ct)
            : CallOpenAiCompatibleAsync(text, prompt, settings, ct);

    private async Task<string> CallOpenAiCompatibleAsync(
        string userText, string systemPrompt, AppSettings settings, CancellationToken ct)
    {
        var url = BuildOpenAiUrl(settings);

        var taggedText = $"<tekst>{userText}</tekst>";
        // Some models (e.g. Mistral-7B-Instruct) only support user/assistant roles.
        // When LlmUseSystemRole is off, the system prompt is prepended to the user message instead.
        object[] messages = settings.LlmUseSystemRole
            ? [new { role = "system", content = systemPrompt }, new { role = "user", content = taggedText }]
            : [new { role = "user", content = $"{systemPrompt}\n\n{taggedText}" }];

        var body = new
        {
            model       = settings.LlmModel,
            messages,
            temperature = 0.3,
            max_tokens  = 2048,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Content = new StringContent(
            JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        if (!string.IsNullOrEmpty(settings.LlmApiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.LlmApiKey);

        var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return (doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? userText).Trim();
    }

    private async Task<string> CallClaudeAsync(
        string userText, string systemPrompt, AppSettings settings, CancellationToken ct)
    {
        const string url = "https://api.anthropic.com/v1/messages";
        var body = new
        {
            model      = settings.LlmModel,
            max_tokens = 2048,
            system     = systemPrompt,
            messages   = new[] { new { role = "user", content = $"<tekst>{userText}</tekst>" } },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Content = new StringContent(
            JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        req.Headers.Add("x-api-key", settings.LlmApiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");

        var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return (doc.RootElement
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString() ?? userText).Trim();
    }

    private static string BuildOpenAiUrl(AppSettings settings) => settings.LlmBackend switch
    {
        LlmBackend.OpenAI      => "https://api.openai.com/v1/chat/completions",
        LlmBackend.AzureOpenAI => settings.LlmBaseUrl,   // user provides full Azure deployment URL
        _                      => $"{settings.LlmBaseUrl.TrimEnd('/')}/v1/chat/completions",
    };
}
