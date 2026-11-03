# Serializer decision

**Status: historical research; generated STJ metadata is implemented in the
maintained library.** The current package no longer contains a precompiler,
serializer build targets or a Roslyn runtime payload. See the
[current serialization boundary and NativeAOT proof](../../docs/architecture/serialization-and-aot.md).

The original decision was to choose generated STJ metadata. Both approaches
worked without serializer root-registration attributes or a precompile tool for
the tested shapes. The library integration hardened the metadata implementation;
neither prototype was promoted unchanged. The evidence below describes the
experiments, rather than the maintained package.

Two principal engineers implemented independent candidates. The lead supplied
the shared oracle, conformance corpus, MEAI probes, holdout and reproduction
script. DMTS reviewed the alternatives and the resulting evidence without coding.

## What passed

- Both real generators discovered command and composite roots from symbols.
  Their candidate paths contain no `JsonSerializable` registrations and do not
  resolve through the oracle or a reflection resolver.
- Both native `osx-arm64` executables passed the same **52/52** checks with JSON
  reflection disabled. Cases include constructors/defaults, required init,
  nested records, closed generic nullability, collections/dictionaries,
  converter delegation, approved polymorphism, schema parity, stored bytes,
  invalid input and two specific error-path comparisons.
- Metadata also passed MEAI's real `AIJsonUtilities.CreateJsonSchema` and
  `GetResponseAsync<DocumentWhole>` with its generated metadata in a native
  executable. Host naming, explicit JSON names, options and cancellation were
  checked. Options-level converters are explicitly rejected by this prototype.
- Codec also passed a native MEAI request through `ForJsonSchema`, standard
  `IChatClient` and host `DelegatingChatClient`, followed by its generated typed
  reader. This is a valid command-layer integration boundary. It does not
  implement the generic MEAI response-helper path or arbitrary host JSON options.
- TUnit checks passed for both generators, mutation/unsupported-shape cases,
  the shared corpus, oracle and MEAI probes. The final reproduction records
  their actual TRX reports; native conformance writes JUnit and JSON itself.

These are serializer proofs. The stored-whole check used a separate byte store
as a ledger stand-in, not the full library composition engine. These experiments
did not establish native execution of the complete library, a packed consumer
or a real provider SDK. The later maintained package proof covers actual library
execution and stored-whole grading; external SDK compatibility remains separate.
No secrets, paid calls or container actions were needed for these experiments.

## Why metadata wins

The runtime descriptors are still necessary. Our generator can emit them directly
from the command/DTO graph, so authors do not need to register the same roots a
second time for STJ's generator. That removes the reason for a separate bootstrap
compilation without adding a new JSON engine.

Metadata keeps STJ's reader, writer, schema exporter and error behavior. It also
fits the current `CommandJsonContract` and MEAI typed-helper boundary. The codec
owns constructor rules, JSON policies, schema generation and error paths in
addition to shape discovery. Its polymorphism implementation buffers through
documents and byte arrays. At the initial comparison the generators contained
477 versus 1,184 lines respectively; metadata has since gained the holdout fixes.
This is a scope observation, not a performance measurement.

The codec remains a viable alternative if a future requirement justifies owning
provider-specific codecs, schema dialects or repair diagnostics. Those benefits
do not currently justify replacing STJ's semantics. Neither throughput, build
performance nor binary-size superiority was benchmarked.

## What the holdout found

The exact same extra contracts were offered to both candidates after their main
corpus passed. An independent STJ source-generated oracle confirms the expected
behavior. See [the cases](metadata-holdout/Program.cs).

- **Metadata: both original failures are fixed.** The initial run was 0/2.
  Member-level ignore conditions now flow to STJ metadata, so `Never` preserves
  the member and overrides global ignore defaults. One constructor selector is
  shared by validation and emission: a public `[JsonConstructor]` wins, then a
  public parameterless constructor, then the sole public constructor. Ambiguous
  choices, multiple annotations and inaccessible marked constructors produce
  `META001` rather than silently selecting an overload. See the independent
  oracle tests in [metadata-holdout-tests](metadata-holdout-tests/HoldoutParityTests.cs).
- **Codec: compile-time rejection.** It emits `CODEC001` for the constructor
  class. Its runtime holdout checks therefore do not run. This is an explicit
  coverage limit, not two runtime passes or two runtime failures.

The initial failing evidence is retained separately. These regressions are now
required to pass in managed and native runs; the reproduction no longer accepts
the old 0/2 result. Fixing them does not establish complete attribute parity.

At comparison time, both candidates still needed work on conflicting nullable
generic instantiations,
cross-assembly accessibility, deterministic ordering and incremental invalidation.
Their pipelines currently collect type symbols before building a whole graph.
The corpus does not prove streaming/segmented input, every JSON error path,
invalid UTF-8 handling, arbitrary converters or complete STJ compatibility.

The custom converter's schema is opaque (`true` on this SDK). It does not express
the converter's `DOC-` validation rule. Collection element nullability still needs
the library's generated recursive validator; STJ metadata alone accepts null
elements. Preserve these separate validation responsibilities during integration.

Native publications produced no IL2026/IL3050 findings or suppressed AOT warnings.
The SDK's native runtime archive produced duplicate debug-map warnings in the
native toolchain. MSBuild's warning settings did not promote those warnings to
errors. A claim that every native linker warning is treated as an error is not
established by these runs. The MEAI probe exempts its experimental `MEAI001`
diagnostic; this is separate from trimming/AOT diagnostics.

## Original migration criteria

These criteria were written before library integration and are retained as the
decision record. The precompiler has since been removed. The maintained
[serialization documentation](../../docs/architecture/serialization-and-aot.md)
records the implemented boundary, package-only native test and remaining limits,
including unverified incremental step caching. Backward compatibility was not
required for the first library implementation.

1. Reuse the production semantic graph, requiredness, recursive validation and
   fingerprint rules. Every supported attribute/argument must have a differential
   test; every unsupported variation must have a useful compile error. Keep the
   repaired holdout green and add another independent holdout. Keep an explicit
   STJ context as a real override/fallback for exceptional shapes; no silent reflection.
2. Publish and run a fresh **NuGet-only** consumer natively. Prove leaf execution,
   MEAI middleware/options, aggregate assembly/readback/evaluation and the explicit
   context override. Include hostile JSON, streaming/segmented reads and host
   resolver/converter behavior. Remove the packaged precompile tool/targets only
   after that consumer works without them.
3. Prove deterministic output and unchanged semantic fingerprints. Test DTOs from
   a referenced assembly, multiple consumers, unrelated edits, source-order
   changes and measured generator invalidation. Keep the supported shape boundary
   explicit and reject unknown behavior before provider spend.

## Reproduce

The script is an explicit research command, outside the solution and normal CI.
It uses local fixtures and fake MEAI clients. It needs the pinned SDK and an
`osx-arm64` NativeAOT toolchain. It requires the metadata holdout to stay green and
records the codec's explicit rejection separately.

```sh
# Run offline TUnit checks, six native publications/runs, and both holdouts.
mise exec -- bash research/serialization-aot/run.sh

# Optional report directory, kept outside production source.
mise exec -- bash research/serialization-aot/run.sh /private/tmp/llmcmd-aot-proof

# Research is excluded from the root formatter; use its exact local ignore file.
mise exec -- csharpier check research/serialization-aot \
  --ignore-path research/serialization-aot/.csharpierignore
```

The default report directory is ignored `artifacts/serialization-aot`. The initial
comparison is preserved at `/tmp/opencode/llmcmd-serialization-aot/final/`.
Verification after fixing the two high findings is at
`/tmp/opencode/llmcmd-serialization-aot/metadata-fixes/verification/`: 27 TUnit
checks passed, the native metadata corpus passed 52/52, and the repaired native
holdout passed 2/2. That follow-up did not complete library integration at the time.
Native `*-conformance/tests.xml` files contain the individual shared checks.
TUnit reports are `*-tests.trx`; command logs are adjacent. Holdout logs remain
separate so the main corpus cannot conceal a production migration blocker.

See [experiment boundary](SPIKE.md), [metadata generator](metadata/Generator/MetadataGenerator.cs),
[codec generator](codec/Generator/CodecGenerator.cs) and [codec limitations](codec/README.md).
