using HRCompanion.Core.Abstractions;

namespace HRCompanion.Core.Models;

public enum LiveMeetingHealthState
{
    Manual,
    FullListening,
    HrReconnecting,
    UserReconnecting,
    TranscriptionDegraded,
    TranscriptionGap,
    AssistantDegraded
}

public sealed record LiveMeetingHealth(
    LiveMeetingHealthState State,
    TranscriberConnectionState HrTranscriber,
    TranscriberConnectionState UserTranscriber,
    bool HasTranscriptionGap,
    bool AssistantDegraded,
    TranscriberDiagnostics HrDiagnostics,
    TranscriberDiagnostics UserDiagnostics);
