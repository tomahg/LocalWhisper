using System.Net.Http;
using System.Text;
using System.Text.Json;
using LocalWhisperer.Models;

namespace LocalWhisperer.Services;

public record ModelInfo(string Id, string Name, bool Loaded);

/// <summary>
/// Thin HTTP client for the transcription server's REST API.
/// Derives the base URL from the WebSocket URL stored in AppSettings.
/// </summary>
public class ServerApiService
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    // Model loading can take several minutes for large models
    private readonly HttpClient _httpLong = new() { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>GET /models — returns available models with loaded flag.</summary>
    public async Task<List<ModelInfo>> GetModelsAsync(string serverUrl)
    {
        var baseUrl = WsToHttp(serverUrl);
        var json = await _http.GetStringAsync($"{baseUrl}/models");
        return JsonSerializer.Deserialize<List<ModelInfo>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
    }

    /// <summary>POST /models/switch — switches the active model.</summary>
    public async Task SwitchModelAsync(string serverUrl, string modelId)
    {
        var baseUrl = WsToHttp(serverUrl);
        var body = JsonSerializer.Serialize(new { model_id = modelId });
        var response = await _httpLong.PostAsync(
            $"{baseUrl}/models/switch",
            new StringContent(body, Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
    }

    /// <summary>GET /health — returns true if server is reachable.</summary>
    public async Task<bool> PingAsync(string serverUrl)
    {
        try
        {
            var baseUrl = WsToHttp(serverUrl);
            var response = await _http.GetAsync($"{baseUrl}/health");
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>POST /transcribe/file — uploads an audio file for transcription.</summary>
    public async Task<TranscriptionResult> TranscribeFileAsync(
        string serverUrl, string filePath, CancellationToken ct = default)
    {
        var baseUrl = WsToHttp(serverUrl);
        using var content = new MultipartFormDataContent();
        using var stream = File.OpenRead(filePath);
        var streamContent = new StreamContent(stream);
        content.Add(streamContent, "file", Path.GetFileName(filePath));

        var response = await _httpLong.PostAsync($"{baseUrl}/transcribe/file", content, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<TranscriptionResult>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? new TranscriptionResult();
    }

    // ws://host:port/ws/transcribe → http://host:port
    private static string WsToHttp(string wsUrl)
    {
        var uri = new Uri(wsUrl);
        var scheme = uri.Scheme == "wss" ? "https" : "http";
        return $"{scheme}://{uri.Host}:{uri.Port}";
    }
}
