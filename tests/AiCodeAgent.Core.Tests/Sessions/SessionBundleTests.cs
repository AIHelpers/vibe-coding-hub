using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Diffing;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Core.Sessions;

namespace AiCodeAgent.Core.Tests.Sessions;

public class SessionBundleTests
{
    [Fact]
    public void ToJson_RoundTrips_FullBundle()
    {
        var bundle = CreateSampleBundle();

        var json = SessionExporter.ToJson(bundle);
        var loaded = SessionExporter.FromJson(json);

        Assert.Equal(bundle.SchemaVersion, loaded.SchemaVersion);
        Assert.Equal(bundle.SessionId, loaded.SessionId);
        Assert.Equal(bundle.Conversation.Count, loaded.Conversation.Count);
        Assert.Equal(bundle.ToolCallLog.Count, loaded.ToolCallLog.Count);
        Assert.Equal(bundle.RolesUsed, loaded.RolesUsed);
        Assert.NotNull(loaded.Changeset);
        Assert.Equal(bundle.Changeset!.Hunks.Count, loaded.Changeset!.Hunks.Count);
        Assert.Equal(bundle.CheckpointRefs.Count, loaded.CheckpointRefs.Count);
        Assert.Equal(bundle.CheckpointSnapshots.Count, loaded.CheckpointSnapshots.Count);
    }

    [Fact]
    public void FromJson_UnsupportedSchema_Throws()
    {
        var bundle = CreateSampleBundle();
        var json = SessionExporter.ToJson(bundle).Replace(
            "\"schemaVersion\": 1",
            "\"schemaVersion\": 99");

        var ex = Assert.Throws<InvalidDataException>(() => SessionExporter.FromJson(json));
        Assert.Contains("99", ex.Message);
    }

    [Fact]
    public void ValidateSchema_AcceptsCurrentVersion()
    {
        var bundle = CreateSampleBundle();
        SessionExporter.ValidateSchema(bundle); // Should not throw
    }

    [Fact]
    public void ValidateSchema_SchemaMissing_DefaultsToCurrentVersion()
    {
        // A missing schemaVersion defaults to the current schema (backward-compatible).
        var json = """{ "sessionId": "abc" }""";
        var bundle = System.Text.Json.JsonSerializer.Deserialize<SessionBundle>(
            json, JsonOptions.Default)!;

        Assert.Equal(SessionBundle.CurrentSchemaVersion, bundle.SchemaVersion);
        SessionExporter.ValidateSchema(bundle); // Should not throw
    }

    [Fact]
    public void PromptPack_RoundTrips()
    {
        var pack = new PromptPack
        {
            Name = "team-pack",
            Description = "Team shared prompts",
            Roles = new List<AgentRolePreset>
            {
                new() { Role = "planner", SystemPrompt = "Plan things" }
            },
            Templates = new List<PromptTemplate>
            {
                new() { Name = "review", Template = "Review {{file}}", Variables = new List<string> { "file" } }
            }
        };

        var json = PromptPackService.ToJson(pack);
        var loaded = PromptPackService.FromJson(json);

        Assert.Equal(pack.Name, loaded.Name);
        Assert.Single(loaded.Roles);
        Assert.Single(loaded.Templates);
        Assert.Equal("review", loaded.Templates[0].Name);
    }

    internal static SessionBundle CreateSampleBundle()
    {
        return new SessionBundle
        {
            SchemaVersion = SessionBundle.CurrentSchemaVersion,
            SessionId = "session-123",
            CreatedAt = DateTime.UtcNow,
            Conversation = new List<Message>
            {
                new() { Role = MessageRole.User, Content = "Hello" },
                new() { Role = MessageRole.Assistant, Content = "Hi!" }
            },
            ToolCallLog = new List<ToolCallLogEntryDto>
            {
                new()
                {
                    ToolCallId = "call-1",
                    ToolName = "ReadFile",
                    Arguments = new Dictionary<string, object?> { ["path"] = "test.cs" },
                    Output = "file contents",
                    Duration = TimeSpan.FromMilliseconds(50),
                    AgentId = "agent-1",
                    Role = "implementer"
                }
            },
            Changeset = new ChangesetBundleDto
            {
                Entries = new List<DiffEntry>
                {
                    new() { FilePath = "test.cs", AgentId = "agent-1", DiffText = "-old\n+new" }
                },
                Hunks = new List<DiffHunk>
                {
                    new(
                        "test.cs-h0",
                        "test.cs",
                        1, 1, 1, 1,
                        new[] { new DiffLine(DiffLineKind.Removed, "old"), new DiffLine(DiffLineKind.Added, "new") },
                        "agent-1",
                        HunkStatus.Pending)
                }
            },
            CheckpointRefs = new List<string> { "cp-1", "cp-2" },
            CheckpointSnapshots = new List<CheckpointSnapshotDto>
            {
                new()
                {
                    CheckpointId = "cp-1",
                    FilePath = "test.cs",
                    OriginalContent = "original",
                    TurnId = "turn-1",
                    SessionId = "session-123"
                }
            },
            RolesUsed = new List<string> { "planner", "implementer", "reviewer" }
        };
    }
}