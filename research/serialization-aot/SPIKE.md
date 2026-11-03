# Zero-registration serializer spikes

Historical experiment brief. Generated STJ metadata is now implemented in the
maintained library, and its precompiler has been removed. See
[the current serialization and NativeAOT boundary](../../docs/architecture/serialization-and-aot.md).
The instructions below describe the isolated experiments.

This experiment compares two ways to remove the precompile tool while keeping
plain DTOs, fluent commands, and NativeAOT. Production code is not changed.

## Ownership

- Lead: shared fixtures, independent oracle/conformance checks, final judgment.
- Metadata principal: `metadata/` only. Emit STJ metadata from semantic symbols.
- Codec principal: `codec/` only. Emit readers, writers, and schema from one model.
- DMTS: independent advice and evidence review. No implementation.

Use the SDK pinned in `../../global.json`. Keep each build graph independent.
Do not build or change the running Aspire host. Do not read secrets or call APIs.

## Shared boundary

`shared/Contracts.cs` contains the same authored types for both candidates. Discover
roots through `PromptCommand<TParameters,TResult>` and `[CompositeOutput]`, not a
list of hardcoded DTO names. The stubs stand in for the existing library discovery
boundary; implementing the whole command runtime is outside this experiment.

Each executable implements `ISpikeSerializer` from `shared/ISpikeSerializer.cs` and
invokes `Conformance.Run` from the shared harness. The lead supplies the harness.
It compares against an STJ source-generated oracle in `shared/OracleContext.cs`.
Only the oracle may use `[JsonSerializable]`. Candidate resolver/codecs must not
fall back to it or to reflection.

Use options: camelCase names, case-sensitive properties, required constructor
parameters, nullable annotations, unmapped-member rejection, explicit nulls,
and last-write-wins duplicate properties (STJ baseline). An unknown polymorphic
discriminator must fail. Optional constructor defaults must survive an absent key.
Apply generated nullability to closed generic properties where STJ erases it.
Collection-element nullable enforcement is a separate, reported test; do not claim
STJ enforces it automatically.

## Required evidence

1. Real incremental generator emits implementation from symbols. Handwritten
   output can be an initial feasibility check, not final generation proof.
2. Ordinary build and a fresh output directory succeed without precompile targets.
3. `PublishAot=true`, `JsonSerializerIsReflectionEnabledByDefault=false`,
   `TreatWarningsAsErrors=true`, `ILLinkTreatWarningsAsErrors=true`, and
   `TrimmerSingleWarn=false`. Publish for `osx-arm64`; execute the native binary.
   Report any suppressed warning. Do not suppress AOT warnings to pass.
4. The common conformance harness covers serialization, roundtrip, schema, defaults,
   required/init members, JSON naming/ignore, nested/collection/dictionary types,
   nullable generics, a custom converter, approved polymorphism, invalid inputs,
   and exact stored-whole reconstruction/evaluation.
5. Unsupported shapes must produce a useful compile diagnostic (at least one
   negative compiler test). Changing an authored DTO must change generated output.
6. Save publish/build/run logs and JUnit or JSON results under ignored `artifacts/`
   or `/tmp/opencode/llmcmd-serialization-aot/`. Record generated source, lines of
   implementation, dependencies, and unresolved gaps. Timings are observations,
   not benchmarks unless repeated under comparable conditions.

The native runner is an executable smoke check. Authored automated tests use
TUnit. Keep this work outside the production solution and normal CI targets.
Format owned source with CSharpier through mise; re-read after formatting.

## Decision rule

Native execution, contract correctness, and no candidate registration markup are
hard gates. Compare extension cost and semantic ownership after those gates pass.
A small native serializer proof does not prove the complete library is AOT-safe.
Do not remove the existing precompiler based on the spike alone.
