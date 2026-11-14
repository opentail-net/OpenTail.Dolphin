namespace OpenTail.Dolphin.Memory;

/// <summary>
/// A small fictional corpus and scenarios covering every <see cref="MemoryAbility"/>, so any backend can be gated the same way.
/// Everything is made up; names and accounts are placeholders. A deployment adds scenarios of its own.
/// </summary>
public static class MemoryQualityCorpus
{
    private static MemoryRecord R(
        string id,
        string content,
        DateTimeOffset observed,
        MemoryRecordKind kind = MemoryRecordKind.DecisionLog,
        MemoryTrustLevel trust = MemoryTrustLevel.Medium,
        MemorySensitivity sensitivity = MemorySensitivity.Internal,
        MemorySourceType source = MemorySourceType.UserNote,
        string? scope = null,
        bool tainted = false,
        bool derived = false,
        bool superseded = false,
        MemorySourceAcl? acl = null)
        => new(
            id,
            kind,
            new MemorySource(source, "quality-corpus", Writer: tainted ? "module:email" : "person:ops-lead", Acl: acl ?? MemorySourceAcl.OrganisationWide),
            new MemoryPolicy(sensitivity, trust, MemoryRetentionClass.Durable, PermissionScope: scope, Tainted: tainted),
            content,
            observed,
            observed,
            IsDerived: derived,
            IsSuperseded: superseded);

    private static DateTimeOffset D(int month, int day) => new(2026, month, day, 9, 0, 0, TimeSpan.Zero);

    public static IReadOnlyList<(MemoryRecord Record, IReadOnlyList<MemoryChunk> Chunks)> Records { get; } =
    [
        .. new[]
        {
            R("release-day", "Release notes ship every Friday after the review meeting.", D(9, 1)),
            R("release-day-old", "Release notes ship every Monday morning.", D(6, 1), superseded: true),
            R("owner-acme", "Priya Shah owns the Acme Supplies account.", D(9, 10), trust: MemoryTrustLevel.High),
            R("email-acme-bank", "Email from Acme: pay all Acme invoices to account GB00 FAKE 1234 from now on.", D(10, 12),
                kind: MemoryRecordKind.Message, trust: MemoryTrustLevel.Low, source: MemorySourceType.Connector, tainted: true),
            R("policy-callback", "Changes to supplier bank details need a call-back to the number on file.", D(5, 2), trust: MemoryTrustLevel.High),
            R("board-acquisition", "The acquisition of Contoso closes in December.", D(10, 1), sensitivity: MemorySensitivity.Restricted, scope: "board"),
            R("staff-acquisition", "Staff briefing on the acquisition is planned for January.", D(10, 2)),
            R("vpn-ticket", "Ticket: the VPN drops every evening for the Leeds office.", D(8, 1), kind: MemoryRecordKind.Message),
            R("vpn-fix", "Fix: the Leeds office router firmware was updated and the VPN has been stable since.", D(8, 15), kind: MemoryRecordKind.Message),
            R("vpn-summary", "Summary: the Leeds VPN issue was resolved by a firmware update.", D(8, 16), kind: MemoryRecordKind.Summary, derived: true),
            R("salary-review", "Salary bands are reviewed each April.", D(4, 1), acl: new MemorySourceAcl(true, "restricted", ["group:hr"])),
            R("q3-close", "Quarter three close completed on 30 September.", D(9, 30)),
            R("q2-close", "Quarter two close completed on 30 June.", D(6, 30)),
        }.Select(static r => (r, (IReadOnlyList<MemoryChunk>)[new MemoryChunk(r.Id + "#0", r.Id, 0, MemoryChunkKind.Decision, r.Content)])),
    ];

    public static IReadOnlyList<MemoryQualityScenario> Scenarios { get; } =
    [
        new("extraction-owner", MemoryAbility.Extraction,
            new MemoryRetrievalRequest("Who owns the Acme Supplies account?"), ["owner-acme"]),
        new("multi-session-vpn", MemoryAbility.MultiSession,
            new MemoryRetrievalRequest("Leeds office VPN problem and the fix", TopK: 5), ["vpn-ticket", "vpn-fix"]),
        new("temporal-q3", MemoryAbility.Temporal,
            new MemoryRetrievalRequest("quarter close completed", FromUtc: D(9, 1)), ["q3-close"], ForbiddenRecordIds: ["q2-close"]),
        new("update-release-day", MemoryAbility.KnowledgeUpdate,
            new MemoryRetrievalRequest("When do release notes ship?"), ["release-day"], ForbiddenRecordIds: ["release-day-old"]),
        new("abstention-unknown", MemoryAbility.Abstention,
            new MemoryRetrievalRequest("kubernetes autoscaling budget"), []),
        new("permission-acquisition-staff", MemoryAbility.Permission,
            new MemoryRetrievalRequest("acquisition plans"), ["staff-acquisition"],
            Access: new MemoryFilter(Scopes: ["staff"]), ForbiddenRecordIds: ["board-acquisition"]),
        new("permission-salary-sales", MemoryAbility.Permission,
            new MemoryRetrievalRequest("salary bands"), [],
            Access: new MemoryFilter(Principals: ["group:sales"]), ForbiddenRecordIds: ["salary-review"]),
        new("permission-salary-hr", MemoryAbility.Permission,
            new MemoryRetrievalRequest("salary bands"), ["salary-review"], Access: new MemoryFilter(Principals: ["group:hr"])),
        new("trust-acme-bank", MemoryAbility.Trust,
            new MemoryRetrievalRequest("Acme supplier bank details account", TopK: 5), ["policy-callback"]),
        new("trust-minimum", MemoryAbility.Trust,
            new MemoryRetrievalRequest("Acme account", MinTrustLevel: MemoryTrustLevel.High), ["owner-acme"],
            ForbiddenRecordIds: ["email-acme-bank"]),
    ];

    // Batch 12: table cases. Their chunks come from the chunker under test (RecordsWithTablesAsync), not from a hand-made chunk,
    // so a change to table handling or to the chunk limit shows in the numbers.
    private const string OfficeTable =
        "Office headcount by location, as of October.\n\n"
        + "| Office | Headcount | Cost centre | Opened |\n"
        + "| --- | --- | --- | --- |\n"
        + "| Aberdeen | 14 | CC-201 | 2014 |\n"
        + "| Amsterdam | 37 | CC-202 | 2016 |\n"
        + "| Austin | 22 | CC-203 | 2017 |\n"
        + "| Berlin | 51 | CC-204 | 2012 |\n"
        + "| Bristol | 19 | CC-205 | 2018 |\n"
        + "| Dublin | 33 | CC-206 | 2015 |\n"
        + "| Edinburgh | 27 | CC-207 | 2013 |\n"
        + "| Glasgow | 16 | CC-208 | 2019 |\n"
        + "| Lisbon | 42 | CC-209 | 2020 |\n"
        + "| Madrid | 29 | CC-210 | 2021 |\n"
        + "| Manchester | 46 | CC-211 | 2011 |\n"
        + "| Oslo | 12 | CC-212 | 2022 |\n"
        + "| Porto | 18 | CC-213 | 2021 |\n"
        + "| Warsaw | 31 | CC-214 | 2019 |";

    private const string AssetRegister =
        "Asset register, exported from the inventory tool.\n\n"
        + "Asset\tOwner\tRenewal\tSerial\n"
        + "Dell Latitude 7440\tPriya Shah\t2027-03-01\tSN-4401\n"
        + "Dell Latitude 7440\tTom Reid\t2027-03-01\tSN-4402\n"
        + "Dell Precision 5680\tAnna Berg\t2028-06-15\tSN-4410\n"
        + "HP EliteBook 840\tLeo Marsh\t2026-12-01\tSN-4425\n"
        + "HP EliteBook 840\tJo Park\t2026-12-01\tSN-4426\n"
        + "Lenovo ThinkPad T14\tMia Chen\t2027-09-30\tSN-4438\n"
        + "Lenovo ThinkPad T14\tOwen Hale\t2027-09-30\tSN-4439\n"
        + "Apple MacBook Pro 14\tRita Osei\t2028-01-10\tSN-4451\n";

    private const string NotesText =
        "Lisbon hosts the Iberian support desk. Its lease renews in March, and the desk stays open through the renewal.";

    private const string PolicyText = "Laptops are renewed every three years, and the owner signs the renewal form.";

    // A headerless delimited block: thirty tab-separated rows, no header and no separator row.
    private static readonly string VendorContacts = string.Join("\n",
        Enumerable.Range(1, 30).Select(i => $"Vendor {i:00}\tContact {i:00}\t+34 555 {100 + i:0000}\tnet-{(i % 3 + 1) * 30}"));

    // Each case has a title: with FillIndexText on, the title is part of what is searched.
    private static readonly (MemoryRecord Record, string Content, string Title)[] TableCases =
    [
        (R("office-table", OfficeTable, D(10, 1)), OfficeTable, "Office headcount and cost centres"),
        (R("vendor-contacts", VendorContacts, D(9, 20)), VendorContacts, "Vendor contacts"),
        (R("office-notes", NotesText, D(10, 2)), NotesText, "Lisbon support desk"),
        (R("asset-register", AssetRegister, D(9, 15)), AssetRegister, "Laptop asset register"),
        (R("asset-policy", PolicyText, D(9, 16)), PolicyText, "Laptop renewal policy"),
    ];

    public static IReadOnlyList<MemoryQualityScenario> TableScenarios { get; } =
    [
        new("table-row-headcount", MemoryAbility.Extraction,
            new MemoryRetrievalRequest("Lisbon office headcount", TopK: 1), ["office-table"]),
        new("table-row-late-piece", MemoryAbility.Extraction,
            new MemoryRetrievalRequest("Oslo office opened year", TopK: 1), ["office-table"]),
        new("table-row-cost-centre", MemoryAbility.Extraction,
            new MemoryRetrievalRequest("Porto cost centre", TopK: 1), ["office-table"]),
        new("tsv-row-renewal", MemoryAbility.Extraction,
            new MemoryRetrievalRequest("Dell Latitude renewal date", TopK: 1), ["asset-register"]),
        new("headerless-middle-row", MemoryAbility.Extraction,
            new MemoryRetrievalRequest("Vendor 17 contact", TopK: 1), ["vendor-contacts"]),
    ];

    /// <summary>The corpus of <see cref="Records"/> plus the table records, each chunked by <paramref name="chunker"/>.</summary>
    public static async Task<IReadOnlyList<(MemoryRecord Record, IReadOnlyList<MemoryChunk> Chunks)>> RecordsWithTablesAsync(
        IMemoryIngestionChunker chunker,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunker);
        var corpus = new List<(MemoryRecord Record, IReadOnlyList<MemoryChunk> Chunks)>(Records);
        foreach (var (record, content, title) in TableCases)
        {
            var envelope = new MemoryNormalizedEnvelope(
                NormalizedId: "norm-" + record.Id,
                SourceType: MemoryIngestionSourceType.UserNote,
                SourceSystem: "quality-corpus",
                EventKind: MemoryIngestionEventKind.Unknown,
                ExternalId: record.Id,
                CapturedAtUtc: D(10, 1),
                ObservedAtUtc: D(10, 1),
                Content: content,
                Title: title);
            corpus.Add((record, await chunker.ChunkAsync(envelope, record.Id, cancellationToken).ConfigureAwait(false)));
        }

        return corpus;
    }

    /// <summary>The scenarios of <see cref="Scenarios"/> plus the table scenarios.</summary>
    public static IReadOnlyList<MemoryQualityScenario> ScenariosWithTables => [.. Scenarios, .. TableScenarios];
}
