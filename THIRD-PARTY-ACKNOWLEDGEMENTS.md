# Third-party acknowledgements

This file credits the projects whose published designs informed OpenTail Dolphin's design documents. It is kept current whenever a
design document cites a third-party project.

## What is credited here

**Ideas only.** The projects below informed design documents in `docs/design/`. No code, configuration or documentation text from them is
copied into this repository. Where a design document describes an idea from one of them, the description is our own paraphrase, with a
citation to the project's file that states the idea.

If any code, configuration or text is copied later, the copy must be recorded here before it is merged. Record the source, the licence,
the copyright notice and any NOTICE file. The MIT licence requires its copyright and permission notice to be kept with the copy. The
Apache-2.0 licence requires its licence text to be kept, and any NOTICE file to be kept too. The Elastic License 2.0 is not an
open-source licence: do not copy from a project under it without the owner's decision.

The packages Dolphin depends on are a separate matter: their licences travel with the packages and are not listed here.

## Projects

### Airbyte

- Source: https://github.com/airbytehq/airbyte
- Licence: Elastic License 2.0 in the repository's root `LICENSE`, as read on 2026-11-13. Only its documentation was read.
- Copyright, as stated in its `LICENSE_SHORT`: Copyright (c) 2026 Airbyte, Inc.
- **Rule: nobody working on Dolphin, person or AI agent, opens Airbyte's source code.** Only the ideas in its published
  documentation may be used, in our own words. No code, configuration, test or documentation text is copied or paraphrased from it.
  The full rule is at the top of `docs/design/ingestion-ideas-2026-11.md`.
- Used for: `docs/design/ingestion-ideas-2026-11.md`. The connection check as a separate operation, the three failure types, the
  record count carried with a state message, the early estimate, and the API budget corrected from response headers. Airbyte's protocol
  document itself credits the Singer specification as its starting point.

### unstructured

- Source: https://github.com/Unstructured-IO/unstructured
- Licence: Apache-2.0 (`LICENSE.md`).
- Used for: `docs/design/ingestion-ideas-2026-11.md`. Typed document elements, splitting a table with its header rows repeated and
  marked, a consolidation rule per metadata field, and file-type detection from content before the extension.

### LlamaIndex

- Source: https://github.com/run-llama/llama_index
- Licence: MIT (`LICENSE`).
- Copyright, as stated in its licence: Copyright (c) Jerry Liu.
- Used for: `docs/design/ingestion-ideas-2026-11.md`. Caching a transformation under the hash of its input and its configuration, and
  separate lists of metadata keys left out of the embedded text and of the text shown to a model.

## Published ideas

### Contextual retrieval (Anthropic, 2024)

- Source: https://platform.claude.com/cookbook/capabilities-contextual-embeddings-guide
- Used for: `docs/design/ingestion-ideas-2026-11.md`, D1. Putting a short generated context in front of each chunk before it is embedded
  and keyword-indexed.
