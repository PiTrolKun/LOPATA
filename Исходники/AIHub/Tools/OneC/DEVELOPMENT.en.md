# Extending the 1C scenario

Source: Исходники/AIHub/{Models,Services,Controls}/OneC. Tests: Исходники/AIHub.Tests/OneC*.cs. Offline documents: Tools/OneC. The published repository uses the same paths: https://github.com/PiTrolKun/LOPATA .

## Component map

- OneCProject: schema 1, tasks, source ranges, proposals, user-error code snapshots, history and Pending work.
- OneCProjectStore, Files and Validation: the sole persistence and ownership boundary. The .ownership registry is separate from the mutable project manifest. New immutable revisions never overwrite previous bytes.
- OneCProtocol: immutable request, strict response and source/task fingerprints. Model output grants no filesystem permissions.
- OneCWork: model → durable proposal → checker → durable diagnostics → bounded corrections. The shared BackgroundOperationController owns pause/resume. Completing a stage never applies a file automatically.
- OneCLocalModelSession: shared ReaderTranslationRuntime, model library, hardware selection, licenses and ComponentAudit. GGUF receives JSON schema; chatllm output is parsed strictly.
- OneCBslRuntime and OneCBslChecker: separately owned process, pinned version, limits and report/source validation. User code is not executed.
- OneCControl partials: UI integration. OneCCodeEditor is reused for files, editable proposals, diagnostic snapshots and reference code.
- OneCAdapters: source/checker registration. OneCLanguageReference provides a small versioned syntax reference without invented customer metadata.

## Add a source adapter

1. Implement IOneCProjectAdapter: Id, Supports(path), ListAsync(path, token). Return real paths and relative labels. Do not infer unsupported configuration metadata. The production OneCTextProjectAdapter is a working example.
2. Retain read-only imports: Store.Import creates a material snapshot and grants no write permission. XML disallows DTD and external resolvers, and limits size and depth. Folder enumeration limits depth, file count and visited directories; junctions and symbolic links are rejected.
3. Register the instance in OneCAdapters.Sources. Neither the UI nor OneCWork needs modification. Put specialized Supports predicates before the generic folder adapter.
4. Run OneCAdapterTests and add format-specific cases. Verify actual files are read, sources remain unchanged, hostile XML is rejected, and unsupported versions/capabilities fail explicitly.

## Add a checker

Implement IOneCCodeChecker (Id, Version, CheckAsync) and register its factory in OneCAdapters.Checkers. Capabilities must be explicitly listed. Return one-based diagnostic coordinates, the exact input CodeHash and an honest State. Process failure, missing reports and mismatched source paths are not success. OneCBslChecker is the real implementation example; platform checks must use a distinct tool identity and capability.

Use shared licenses and ComponentAudit. First use of a new revision and reinstallation always require a full audit; repeat OFF checks presence. Processes must use ownership, cancellation and limits. Integrate shared background work and application shutdown. Adapters never gain permission to edit foreign files; all writes remain in the project store.

## Contracts

The contracts folder contains a capability manifest, request/response examples and JSON schemas. ProtocolVersion is 1. There is no run capability. Unknown versions return unsupported-version; unknown capabilities return unsupported. Current adapters are typed in-process implementations. External stdio/MCP transport is not connected: implementing it requires a separate process, requestId, frame/response limits, cancellation, timeout and version checks. Model output and BSL/XML contents are data, not commands.

## Verification

Run from the repository root:

```powershell
dotnet build .\Исходники\AIHub.Tests\AIHub.Tests.csproj -p:BookOfficeArchive=true
.\Исходники\AIHub.Tests\bin\Debug\net10.0-windows\AIHub.Tests.exe --filter 'FullyQualifiedName~AIHub.Tests.OneC'
```

Actual integration tests require LOPATA_ONEC_MODEL (installed model ID) and LOPATA_ONEC_BSL_EXE (official BSL LS launcher). WPF tests use LOPATA_ONEC_UI=1 and optionally LOPATA_ONEC_UI_OUTPUT for images. Fixture tests establish state/persistence behavior, not real model execution. If a worktree uses a Runtime junction, stage the native test host beneath the real repository root or use an ordinary checkout; never disable junction protection for a test.

## Development status

Implemented: copy/paste, local projects and file ownership, editor/diff, local model and actual BSL LS integration, text-folder adapter, learning examples, version-linked error feedback and resume.

Contracts ready for extension: more sources, checkers and local knowledge providers.

Requires separate implementation and a real stand: Configurator/EDT metadata interpretation, isolated OneScript, genuine execution in a test database, CF/CFE/EPF/ERF builders, external stdio/MCP connectors and portable project packages. Success on one configuration never establishes support for every database.
