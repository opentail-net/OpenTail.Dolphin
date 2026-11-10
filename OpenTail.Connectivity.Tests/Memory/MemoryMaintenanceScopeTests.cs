using OpenTail.Connectivity.Memory;

namespace OpenTail.Connectivity.Tests.Memory;

/// <summary>Maintenance is confined to the workspace and project the request asks for, and never groups records from two workspaces.</summary>
public sealed class MemoryMaintenanceScopeTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-15T12:00:00Z");
    private static readonly MemoryPolicy Durable = new(MemorySensitivity.Internal, MemoryTrustLevel.High, MemoryRetentionClass.Durable);

    private static MemoryRecord Record(string id, string workspace, string project, string content, string? dedupeKey = null) => new(
        Id: id,
        Kind: MemoryRecordKind.DecisionLog,
        Source: new MemorySource(MemorySourceType.GitHub, "github", ExternalId: id, Workspace: workspace, ProjectScope: project, Actor: "casey"),
        Policy: Durable,
        Content: content,
        CreatedAtUtc: Now.AddDays(-1),
        ObservedAtUtc: Now.AddDays(-1),
        DedupeKey: dedupeKey,
        BaseImportance: 0.5);

    private static async Task<MemoryMaintenanceReport> RunAsync(IEnumerable<MemoryRecord> records, string? workspace = null, string? project = null) =>
        await new DefaultMemoryMaintenanceService().ExecuteAsync(new MemoryMaintenanceRequest(
            Records: records.ToList(),
            Links: [],
            AsOfUtc: Now,
            Workspace: workspace,
            ProjectScope: project));

    [Fact]
    public async Task A_duplicate_key_in_two_workspaces_is_not_reported_as_a_duplicate()
    {
        var report = await RunAsync([
            Record("a1", "ws-a", "Core", "Decision approved.", dedupeKey: "shared-key"),
            Record("b1", "ws-b", "Core", "Decision approved.", dedupeKey: "shared-key"),
        ]);

        Assert.DoesNotContain(report.Actions, action => action.Kind == MemoryMaintenanceActionKind.Deduplicate);
    }

    [Fact]
    public async Task Two_workspaces_with_a_project_of_the_same_name_get_separate_groups()
    {
        var report = await RunAsync([
            Record("a1", "ws-a", "Core", "Open todo for the release."),
            Record("a2", "ws-a", "Core", "Blocked on review."),
            Record("b1", "ws-b", "Core", "Open todo for the release."),
            Record("b2", "ws-b", "Core", "Blocked on review."),
        ]);

        Assert.Equal(2, report.Actions.Count(action => action.Kind == MemoryMaintenanceActionKind.UnresolvedTopicView));
    }

    [Fact]
    public async Task A_workspace_scoped_request_ignores_the_records_of_other_workspaces()
    {
        var report = await RunAsync([
            Record("a1", "ws-a", "Core", "Decision approved.", dedupeKey: "shared-key"),
            Record("b1", "ws-b", "Core", "Decision approved.", dedupeKey: "shared-key"),
        ], workspace: "ws-a");

        Assert.DoesNotContain(report.Actions, action => action.Kind == MemoryMaintenanceActionKind.Deduplicate);
    }

    [Fact]
    public async Task A_project_scoped_request_leaves_other_projects_alone()
    {
        var report = await RunAsync([
            Record("c1", "ws-a", "Core", "Open todo for the release."),
            Record("c2", "ws-a", "Core", "Blocked on review."),
            Record("o1", "ws-a", "Other", "Open todo for the release."),
            Record("o2", "ws-a", "Other", "Blocked on review."),
        ], project: "Core");

        Assert.Single(report.Actions, action => action.Kind == MemoryMaintenanceActionKind.UnresolvedTopicView);
    }
}
