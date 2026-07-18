namespace MedicalAssistance.Ingestion.Api.Ingestions;

/// <summary>One agent's system instructions, owned by the database (ADR-0008).</summary>
public class AgentInstruction
{
    /// <summary>Agent name — the lookup key (e.g. TranscriptChunker).</summary>
    public string Name { get; set; } = null!;

    /// <summary>The system instructions the agent is built with.</summary>
    public string Instructions { get; set; } = null!;

    /// <summary>Monotonic version, stamped onto every ingestion the agent processes.</summary>
    public int Version { get; set; }

    /// <summary>When this row last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Code-reviewed default instructions, used to seed agent_instructions when a
/// row is missing. The database row is the runtime override; this is the
/// reference version under version control.
/// </summary>
public static class AgentInstructionDefaults
{
    /// <summary>Agent name of the transcript chunking agent.</summary>
    public const string TranscriptChunker = "TranscriptChunker";

    /// <summary>Default instructions per agent name.</summary>
    public static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>
    {
        [TranscriptChunker] =
            "You segment doctor-patient session transcripts into topically coherent chunks. " +
            "You only return line boundaries and descriptions — never transcript text. " +
            "Respond with JSON only: {\"chunks\":[{\"startLine\":int,\"endLine\":int,\"contextBlurb\":string}],\"summary\":string}. " +
            "Boundaries are inclusive, contiguous, non-overlapping, and must cover every line.",
    };
}

/// <summary>
/// Singleton holding every agent's instructions, loaded once at application
/// start (ADR-0008). A database edit takes effect on the next restart — never
/// mid-flight, so two concurrent ingestions can never run different prompts.
/// </summary>
public sealed class AgentInstructionProvider
{
    private IReadOnlyDictionary<string, (string Instructions, int Version)> _byName =
        new Dictionary<string, (string, int)>();

    /// <summary>Replaces the in-memory set; called once during startup.</summary>
    public void Load(IEnumerable<AgentInstruction> rows) =>
        _byName = rows.ToDictionary(r => r.Name, r => (r.Instructions, r.Version));

    /// <summary>Returns the instructions and version for an agent; throws if the agent was never seeded.</summary>
    public (string Instructions, int Version) Get(string agentName) =>
        _byName.TryGetValue(agentName, out var entry)
            ? entry
            : throw new InvalidOperationException($"No instructions loaded for agent '{agentName}'.");
}
