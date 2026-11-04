# Library foundation decisions

The library starts from the contracts exercised by the research suites. This document records the reasons for the first promotion; it does not grant production guarantees that the experiments did not establish.

## One coherent runtime package

Commands, composition, actor/run checks, immutable output references, context and routing form one runtime assembly in `Zeeq.AgentCmd`. They were already mutually required by execution. Keeping their caller types in one namespace makes the fluent API readable and avoids exposing experiment names as package boundaries.

The source generators and analyzers ship in that same NuGet package. Generated descriptors call concrete runtime contracts, so separately versioning them would invite a compile-time version mismatch. A clean external package-consumer test is the distribution gate. No source project in `src` refers to the archived experiments. Optional provider mapping and OpenTelemetry packages stay separate so a basic application does not inherit exporter or provider SDK dependencies.

## Author intent; generate plumbing

A source-authored `sealed partial` subclass, stable `const Id`, templates and named input/result records are enough to author a command. Base-type discovery avoids a repetitive marker attribute. The ID is independent of CLR names and traces. Optional hooks customize behavior; generated constructors, renderers, descriptors, validation, schema and DI remain generated.

Ordinary source generators do not consume each other's output in the same pass. A dedicated metadata generator therefore discovers both leaf and whole roots itself and emits an STJ metadata resolver directly. Command and composition generators reference its known name; they do not inspect its generated output. STJ retains ownership of reading, writing and schema export. The precompiler and serializer build targets have been removed. An explicitly selected authored STJ context remains the escape hatch for exceptional shapes.

The same configured JSON contracts drive schema, deserialization, required presence and JSON-name mapping. Presence is separate from nullability. Validation distinguishes closed generic nullable shapes and dispatches approved derived types. Unknown or incompatible contracts fail rather than silently enabling reflection. Contract hashes exclude unrelated serializer roots.

The three runtime libraries enable AOT and trimming analysis with warnings as errors. Native verification uses a fresh package-only consumer with reflection disabled and exercises actual execution, composition, stored-whole evaluation and optional platform adapters. See [the serialization boundary](serialization-and-aot.md); successful native execution does not establish compatibility of arbitrary external SDKs.

## Values, execution and stored facts differ

An immutable invocation retains its type parameters, caller configuration and explicit context. Fluent callbacks freeze their settings or retain a terminal configuration failure. `ExecuteAsync` captures context before awaiting and emits it even when a snapshot does not exist. AsyncLocal is a convenience default, not a security grant, transaction or durable carrier.

A successful typed model result and successful output delivery are independent. `Outcome<T>` exposes `Success` or `Failed`; its non-generic closed failure preserves the original execution context through functional combinators. Pure Map/Bind callbacks may throw programmer exceptions. Deferred composition callbacks are caught by the composition runner.

A provider attempt is distinct from one logical execution. SDK retries can conceal extra network requests, so live smoke tests disable them. Missing accounting dimensions stay unknown. Cached and reasoning counters remain subsets rather than extra totals. Normalized and native evidence can conflict; neither silently overwrites the other.

## The root registers deferred work

`WithHeaderFrom` expresses that the header will be produced by a source invocation. It groups methods in autocomplete while making deferred work explicit. The mutable construction handle returns itself, so ordinary statements and if/else branches are safe. Leaf configuration remains immutable. Registration after freeze throws; duplicate registration before freeze becomes a closed configuration failure.

The plan validates required slots and dependencies, then claims each step before model work. Typed slots establish result compatibility, not the identity or authorization of a particular draft. `Then` consumes one exact stored `Pinned<T>` reference; it does not serialize arbitrary delegates. Failed dependencies retain the original cause, and skipped steps do not receive invented execution IDs.

Attachment, memoization, fingerprints and replay checks preserve the intended work within the in-memory implementation. Route fingerprints include resolved settings so profile remapping cannot silently reuse a different trial. Reuse has explicit provenance and no invented model execution.

The aggregate is assembled at contract JSON names, checked through the whole contract and stored with `ProducerRef.Composition(DraftId)`. It never borrows a part's execution ID. Grades and corrections address exact immutable versions and JSON pointers, with expected-value checks and replayable caller event IDs.

## Honest boundaries and next work

The archived execution-capture experiment also explored external runtime snapshots, override authorization, pricing and a different executor. That executor and its fake pricing types were not promoted as a second public execution system. Its 35 tests remain under `research/tests/AgentCmd.Tests` rather than being relabeled as tests of the new library.

The first library has no durable database store, lease/fencing implementation, restart reconciler, general workflow engine, automatic serialization of callbacks or exactly-once billing guarantee. Runtime snapshot-provider integration and invoice reconciliation require further work. Native stream extraction and cloud collector acceptance need further integration evidence. These are explicit follow-on items, not implicit promises of the alpha.

The provider package uses a shared accounting engine with immutable platform/model rate cards and first-party provenance. Separate MEAI request policies validate model constraints; they do not change caller options. One explicit target binding is shared by validation and pricing. The [model policy design](provider-model-policies.md) explains those seams and the live model matrix. Rates require explicit billing scope and preserve unknown or conflicting usage. Real API and generated-runtime tests retain sanitized accounting snapshots; a separate, explicitly started local Langfuse proof exports and reads back those snapshots without another model call. This proves the current collector adapter and emitter path, while keeping hosted credentials and Docker outside ordinary tests.

The Architect and DMTS reviewed the package and API boundary. Their central gates were coherent versioning, a namespace-aware generator migration, no source-build dependency in NuGet, preserved contract tests, and an external consumer proof with reflection disabled.
