namespace HRCompanion.Infrastructure.OpenAI;

public sealed class OpenAiOptions
{
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string AnswerModel { get; set; } = "gpt-5.6-sol";
    public string FastModel { get; set; } = "gpt-5.6-luna";
    public string TranscriptionModel { get; set; } = "gpt-live-transcribe";
    public string TranscriptionLanguage { get; set; } = "en";
    public string TranscriptionDelay { get; set; } = "low";
    public int AnalysisTimeoutMilliseconds { get; set; } = 2500;
    public int AnswerTimeoutMilliseconds { get; set; } = 6000;
    // API service_tier supports auto/default/flex/priority. Keep auto until real latency/cost benchmarking justifies priority.
    public string ServiceTier { get; set; } = "auto";
}
