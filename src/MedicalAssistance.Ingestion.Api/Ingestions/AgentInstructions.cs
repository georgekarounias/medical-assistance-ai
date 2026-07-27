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
/// The agent names — the lookup keys into the <c>agent_instructions</c> table.
///
/// The instruction TEXT lives only in the database (ADR-0008), bootstrapped by the
/// SeedAgentInstructions migration; code holds no default copy of it. These are
/// just the keys a strategy uses to fetch its instructions from the singleton
/// provider, so a prompt can be edited in the database (and reloaded on restart)
/// without a code change.
/// </summary>
public static class AgentNames
{
    /// <summary>The transcript chunking agent.</summary>
    public const string TranscriptChunker = "TranscriptChunker";

    /// <summary>The doctor-note chunking agent.</summary>
    public const string DoctorNoteChunker = "DoctorNoteChunker";

    /// <summary>The LabReport Tier 2 analyte-mapping agent (classifies columns + canonical names only).</summary>
    public const string LabAnalyteMapper = "LabAnalyteMapper";

    /// <summary>The imaging-report chunking agent (chunks a radiologist's extracted findings).</summary>
    public const string ImagingReportChunker = "ImagingReportChunker";

    /// <summary>The patient-summarizer agent: folds a patient's per-document summaries into one rolling overview.</summary>
    public const string PatientSummarizer = "PatientSummarizer";

    /// <summary>The lab-report summarizer: writes the per-document summary a LabReport has no chunking agent to produce.</summary>
    public const string LabReportSummarizer = "LabReportSummarizer";
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
