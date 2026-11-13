# Ideas from three ingestion projects, and from a short search (November 2026)

**Status:** record and proposal, written 2026-11-13. Nothing here is built, and no existing contract is changed by this document.
Each idea says what Dolphin has today, what would be added, and what must be checked before it is designed.

**Sources.** Local copies of three projects, read on 2026-11-13, and a web search the same day (section 5). Credits are in
[`THIRD-PARTY-ACKNOWLEDGEMENTS.md`](../../THIRD-PARTY-ACKNOWLEDGEMENTS.md). Ideas only: no code, configuration or text was copied.

| Project | Read at | Licence | What was read |
|---|---|---|---|
| Airbyte | `7e705c7b2d0` | Elastic License 2.0 (the repository's root `LICENSE`) | Documentation only: `docs/platform/understanding-airbyte/airbyte-protocol.md` and `docs/platform/connector-development/config-based/understanding-the-yaml-file/rate-limit-api-budget.md` |
| unstructured | `7d81b40e` | Apache-2.0 | `unstructured/documents/elements.py`, and the option and metadata names in `unstructured/chunking/base.py`, `chunking/title.py` and `file_utils/filetype.py` |
| LlamaIndex | `e3c425ccc` | MIT | `llama-index-core/llama_index/core/ingestion/pipeline.py` and `schema.py` |

**Limits.** I read the files named, not the projects. I ran nothing. The three are Python or JVM projects, so everything here is a
design idea to rebuild in .NET, not something to port.

## 1. What was read in Dolphin

- `docs/architecture.md` in full: the pipeline, validation codes, transformers, incremental runs, deletions, version skipping.
- `ISourceConnector` (`DiscoverAsync`, `FetchAsync`) and `ITextExtractor` (`Extract(fileName, content)` returning
  `TextExtractionResult` with `Success`, `Text`, `ErrorMessage`, `Metadata`, `NotText`).
- `SourceConnectorConformance`: the checks it runs (ids, stability across runs, cancellation, read-only names, empty source, the
  incremental no-change check).
- `MemoryChunk` (`Id`, `ParentRecordId`, `ChunkOrdinal`, `Kind`, `Content`, `WindowText`, `Summary`, `Embedding`, `Tags`) and the
  names of the two chunkers (`HierarchicalChunker`, `SentenceWindowChunker`). Their bodies were searched for "table" and "header"
  (no match), not read.
- `docs/design/shared-groundwork.md` section 5 (the REST helper, not built), `summary-cards.md` "Re-summarising",
  `source-permissions-and-isolation.md` (headings and the lines on staleness), `memory.md` "What others do".

**Not read:** the body of `ConnectivityPipeline`, the extraction library behind `TransformationsTextExtractor`, the memory
ingestion pipeline and retriever. Ideas that depend on them are marked *check first*.

## 2. Summary: what is worth taking

| # | Idea | From | Fits | Size | Worth |
|---|---|---|---|---|---|
| A1 | A connection check every connector can answer | Airbyte | `Files` contracts, conformance suite, Orca `--lint` | Small | **High** |
| A2 | Three failure kinds: configuration, transient, system | Airbyte | Pipeline, `IngestionReport` | Small to medium | **High** |
| A3 | A call budget per API, corrected from response headers | Airbyte | The REST helper (shared groundwork 5) | Medium | **High**, with the helper |
| D1 | Context for each chunk before it is indexed | Search | `Memory` chunking and hooks | Medium | **High** for retrieval quality |
| B2 | Tables split with their header repeated | unstructured | `Memory` chunkers | Small to medium | Medium |
| B4 | File type by content, not only by extension | unstructured | Extraction, attachments | Small | Medium (a safety check) |
| C3 | Say which metadata is embedded and which the model sees | LlamaIndex | `Memory` records | Small | Medium |
| B1 | Typed elements beside the flat body | unstructured | `ITextExtractor` | Large | Medium; design first |
| A4 | A change count with each checkpoint | Airbyte | Incremental runs | Small | Low to medium |
| C2 | One cache rule for every costly transformer | LlamaIndex | Transformers, embeddings | Small | Low: partly there |
| A5 | An early size estimate | Airbyte | `ISourceConnector` | Small | Low |
| D2 | Two more permission tests | Search | Source permissions plan | Small | Medium; added to that plan |

## 3. The ideas

### A1. A connection check (Airbyte)

**There:** every connector answers `check(Config) -> AirbyteConnectionStatus`: succeeded or failed, with a message. It is a
separate operation from reading.

**Here:** nothing. No type in the repository has a check, probe or test-connection method (searched by name). A wrong credential
or an unreachable host shows up as the first failed item of a run.

**Proposal:** an optional `IConnectionCheck` beside `ISourceConnector`: `Task<ConnectionCheckResult> CheckAsync(ct)`, returning
succeeded or failed, a failure kind (A2) and a message passed through `CredentialRedactor`. It makes the cheapest authenticated
call the source has (list one item). It must stay read-only. A conformance check runs it against the fixture. Orca can call it
from `--lint` and at startup, so an operator learns of a bad secret before an agent does.

### A2. Three failure kinds (Airbyte)

**There:** an error trace carries `failure_type`: `system_error`, `config_error`, or `transient_error` (added in protocol
v0.8.0). The orchestrator treats them differently: a configuration error is the user's to fix and is not retried.

**Here:** two outcomes are documented. A failed fetch goes on the retry list, for up to `MaxItemAttempts` (5) runs. A rejected
item (extraction or validation) is not retried. There is no third case for "the configuration is wrong".

**Proposal:** a `SourceConfigurationException` (expired or revoked credential, missing scope, unknown site or mailbox). The
pipeline stops the run on it, saves nothing past the last commit, puts nothing on the retry list, and reports it as a
configuration failure in `IngestionReport`. Without it, a revoked token can fill the retry list (100 by default) with healthy
items and then hold the cursor.

*Check first:* how `ConnectivityPipeline` treats a 401 or 403 from `FetchAsync` today. The body was not read.

### A3. A call budget, corrected from headers (Airbyte)

**There:** an `HTTPAPIBudget` holds one or more rate policies (fixed window, moving window, unlimited), matched to requests. It
updates itself from response headers (defaults `ratelimit-remaining` and `ratelimit-reset`) and treats the listed status codes
(default 429) as "budget spent". The connector slows down before the API refuses it.

**Here:** each connector has its own delay and retry code. The REST helper in shared groundwork (section 5) would unify retries
and `Retry-After`, and is not built. It has no budget: it reacts after a 429.

**Proposal:** add a budget to that helper's design. One budget per API and credential, shared by every connector instance that
uses the credential (`PerSecret<T>` already gives one object per secret). Policies are declared by the connector; the helper
corrects the remaining count from headers when the API sends them. Two runs against one tenant then share the limit, where
today each would discover it alone.

### A4. A change count with each checkpoint (Airbyte)

**There:** a state message carries `sourceStats.recordCount`. The destination commits the state only after it has received that
many records.

**Here:** the pipeline flushes every exporter and then saves the cursor. Nothing compares how many changes the connector sent
since the last checkpoint with how many the pipeline handled.

**Proposal:** `SourceChange.Checkpoint` may carry the number of changes since the previous checkpoint. When present, the pipeline
checks that exported, deleted, skipped, failed and retry-listed changes add up to it before saving, and refuses the checkpoint
otherwise. A cheap guard against a connector or pipeline bug that drops an item silently. Add it to conformance check 8.

### A5. An early estimate (Airbyte)

**There:** `AirbyteEstimateTraceMessage` gives a row and byte estimate early in a sync, for progress and time remaining.

**Proposal:** an optional `IEstimatesWork` on a connector (`EstimateAsync` returning item and byte counts, or null). Useful for
summary cards, where the cost of a run is a budget question. Low priority.

### B1. Typed elements beside the flat body (unstructured)

**There:** extraction returns a list of typed elements: `Title`, `NarrativeText`, `ListItem`, `Table`, `Header`, `Footer`,
`CodeSnippet`, `FigureCaption`, `PageBreak` and others. Each has metadata such as `page_number`, `parent_id`, `category_depth`
and, for a table, `text_as_html`.

**Here:** `TextExtractionResult.Text` is one string, and `NormalisedDocument.Body` is that string trimmed. `ContentHash` is over
the body. Structure is lost before the chunkers see it.

**Proposal, to design before building:** keep `Body` and `ContentHash` exactly as they are. Add an optional list of elements to
`TextExtractionResult` (kind, text, heading depth, page, and the offset of the text in `Body`). Chunkers that receive it split on
headings and keep a table whole; consumers that ignore it see no change. Headers and footers could then be left out of the body,
which is a separate decision because it changes hashes.

*Check first:* what the extraction library already knows about structure. If it can give headings and tables, this is mostly
plumbing. If not, this is a large piece of work and should wait for a customer who needs it.

### B2. Tables split with their header repeated (unstructured)

**There:** a table too large for one chunk is split into `TableChunk`s. Options and metadata: `repeat_table_headers`,
`num_carried_over_header_rows`, `is_continuation`, `table_id`, `chunk_index`. A later chunk repeats the header rows, says how many
it repeated (so they can be removed when the table is put back together), and says it is a continuation. The by-title chunker
also has `combine_text_under_n_chars` (merge small sections), a soft and a hard maximum, and `multipage_sections`.

**Here:** neither chunker mentions tables or headers. A long CSV-like or Markdown table is presumably cut by size, and a later
piece has no column names.

**Proposal:** in `HierarchicalChunker`, detect a Markdown or delimited table, split it on row boundaries, repeat the header row in
each piece, and tag the pieces (`table:<id>`, `continuation`) through `MemoryChunk.Tags`. No contract change. A row cut from its
header is a common cause of wrong answers about tabular data.

### B3. A rule per metadata key when parts are merged (unstructured)

**There:** each metadata field has a `ConsolidationStrategy` for when elements are combined into a chunk: drop, first, concatenate
strings, concatenate lists, unique list.

**Here:** relevant only if B1 is built, or where a chunk's tags come from several parts. Record it with B1; nothing to do alone.

### B4. File type by content (unstructured)

**There:** `detect_filetype` uses, in order: a known binary type from content, an explicit content type, a guess from the file's
first bytes, and the extension last. It tells zip-based formats apart by looking inside, and JSON from NDJSON by parsing.

**Here:** `Extract(fileName, content)` receives the name, and the pipeline records `source.extension`. Dolphin itself has no
content sniffing (searched). The extraction library may; it was not read.

**Proposal:** a small check of the first bytes against the extension for the handful of types that matter: zip, PDF, OLE, PE and
ELF executables, images. On a mismatch, add a warning (`EXTENSION_MISMATCH`) and prefer the content. An email attachment named
`invoice.txt` that is an executable or an archive should not be read as text, and the attachment work in the plan (section 32)
is where this matters most.

*Check first:* what `TransformationsTextExtractor` does with a mismatch today.

### C2. One cache rule for costly transformers (LlamaIndex)

**There:** the ingestion pipeline caches each transformation under a hash of the input content plus the transformation's
configuration, so an unchanged input with an unchanged transformer is not processed again.

**Here:** summary cards already follow this rule (content hash, plus the summariser's `Version`), and version skipping avoids the
fetch. The rule is not stated once for every costly step.

**Proposal:** state it as a contract for anything that calls a model: key = `ContentHash` + the step's id + the step's version.
Embeddings (`MemoryEmbeddingModel` has `Revision`) and D1 below would use the same key. Mostly a documentation and naming task.

### C3. Which metadata is embedded, and which the model sees (LlamaIndex)

**There:** a node has `excluded_embed_metadata_keys` and `excluded_llm_metadata_keys`. The text built for the embedding model and
the text shown to the language model each leave out their own list (`MetadataMode.EMBED`, `LLM`, `ALL`, `NONE`).

**Here:** not verified in the memory code. Dolphin's metadata includes keys that should never be either (`source.sizeBytes`, ACL
principals) and keys that help retrieval but not reading (a path), and the reverse.

**Proposal:** one table in the memory design that names, for each metadata prefix, whether it is embedded, shown to the model, both
or neither, with "neither" as the default for a new key. ACL and permission keys are never shown to a model. *Check first* what
the ingestion pipeline does today.

### Looked at and not taken

- **LlamaIndex's document-store strategies** (`UPSERTS`, `DUPLICATES_ONLY`, `UPSERTS_AND_DELETE`). Dolphin has the first two
  through stable ids and `ContentHash`. The third deletes whatever a run did not see. Dolphin forbids that on purpose except after
  a full, completed, untruncated run (the deletion rule in `architecture.md`), and should keep its rule.
- **unstructured's element id** (a hash of file name, text, page and position). Dolphin's document id does not depend on the
  text, which is what lets a store update in place.
- **unstructured's `DataSourceMetadata`** (url, version, record locator, dates, permissions). Dolphin has the equivalents.
- **Airbyte's acceptance tests.** Dolphin has its own conformance suites for connectors, memory backends and queues.
- **LlamaIndex's node relationships** (previous, next, parent, source). `MemoryChunk` has the parent and an ordinal, which gives
  previous and next.
- **Airbyte's declarative connector manifests.** The closest thing we have is Orca's integration profiles, which solve a
  different problem (curated tools over an OpenAPI description). Nothing to take for Dolphin.

## 4. For Orca

Less applies. Two notes, neither of them a plan:

- A3's header-corrected budget would also suit Orca's integration profiles, where a profile could declare its API's limit. Orca
  has a rate limit per tool at the claim; whether a profile-level budget adds anything should be judged against that code first.
- A1's check is useful to Orca only through Dolphin: `--lint` and startup can call it for each configured connector.

The July 2026 MCP revision was looked at in the search. Orca's `docs/design/mcp-tasks.md` already tracks it (the Tasks extension,
no `tasks/list`, the SDK 2.x upgrade), so nothing is added there.

## 5. From the search

The pages found are mostly secondary write-ups. The numbers below are as reported and were not reproduced.

### D1. Context for each chunk before it is indexed

**The idea (Anthropic, "contextual retrieval", 2024).** A chunk cut from a document often cannot be found, because it no longer
says what it is about ("revenue grew 3%": whose, when?). Before indexing, a model writes a short context for each chunk (about 50
to 100 tokens) from the whole document, and that context is put in front of the chunk for both the embedding and the keyword
index. Reported effect on the rate of failed top-20 retrievals: 35% lower with contextual embeddings, 49% lower with contextual
keyword search added, 67% lower with a reranker as well. The cost is one model call per chunk, which the write-ups say is only
affordable with prompt caching. Anthropic's own post was not read; the figures come from its cookbook and from summaries that
agree with each other.

**Here:** `MemoryChunk` has `Content`, `WindowText` and `Summary`. The model hooks are `IQueryCondenser`, `IReranker` and
`IFactExtractor`, all optional, all implemented outside the library.

**Proposal:**
- A free, deterministic first step: put the document title and the heading path in front of the text that is embedded and
  keyword-indexed. No model, no cost. It needs headings, which `HierarchicalChunker` presumably has (*check first*).
- A fourth optional hook, `IChunkContextualizer`, implemented outside like the others. Its output is stored apart from `Content`
  (the stored text of a record is never rewritten), is used only for indexing, is cached under the C2 key, and inherits the
  record's policy, sensitivity and taint. It is text a model wrote from untrusted content, so it is never shown as a fact and
  never quoted.
- Measure it with `MemoryQualityGate` before turning it on anywhere: the gate exists for exactly this.

### D2. Two more tests for the permissions plan

The search on permission-aware retrieval agrees with the plan in
[`source-permissions-and-isolation.md`](source-permissions-and-isolation.md): filter inside the search call, treat unknown as
deny, bound the age of a captured ACL. Two test ideas were not in it and have been added there: a paired-persona probe, and the
rule that a cached answer must not outlive a revoked permission. A third point is a warning for connector authors: a re-sync must
never drop or flatten an ACL, because that turns a restricted document open.

## 6. Suggested order

1. **A2, then A1.** Small, and they change what an operator sees when a credential breaks. A1 uses A2's kinds.
2. **A3 with the REST helper**, when the helper is built.
3. **D1's deterministic step and B2**, measured with the quality gate.
4. **B4**, with the attachment work.
5. **C3** as a table in the memory design.
6. **B1** only after checking what the extraction library provides, and only with a need.

## 7. Open questions for the owner

- Is A1 wanted on every connector, or only on those with credentials?
- D1's model hook sends document text to a model at indexing time. Is that acceptable by default, or opt-in per source?
- B1: is structure (headings, tables, pages) something the first customers ask for?

## 8. Sources for section 5

- Anthropic cookbook, contextual embeddings: https://platform.claude.com/cookbook/capabilities-contextual-embeddings-guide
- InfoQ on contextual retrieval: https://www.infoq.com/news/2024/09/anthropic-contextual-retrieval/
- Microsoft ISE, SharePoint document-level access (April 2026): https://devblogs.microsoft.com/ise/sharepoint-doc-level-access/
- WorkOS, RAG access control: https://workos.com/blog/rag-access-control-document-permissions
- Particula, stale ACLs in RAG: https://particula.tech/blog/rag-document-acl-sync-stale-permissions
- MCP blog, the 2026-07-28 release candidate: https://blog.modelcontextprotocol.io/posts/2026-07-28-release-candidate/
