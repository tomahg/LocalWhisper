using System.Text.Json.Serialization;

namespace LocalWhisperer.Models;

public class TranscriptionResult
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("segment_id")]
    public int SegmentId { get; set; }

    [JsonPropertyName("processing_time_ms")]
    public int ProcessingTimeMs { get; set; }

    public bool IsFinal => Type == "final";
}
