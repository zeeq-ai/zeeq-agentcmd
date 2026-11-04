# Serialization and NativeAOT

The normal compiler generator emits serializer metadata for command parameters,
results and composition wholes. User DTOs need no serialization root attributes.
There is no precompiler executable, consumer build target or packaged Roslyn
runtime. The same core package carries the generator and runtime.

## One contract

An assembly-local resolver supplies STJ `JsonTypeInfo` objects. Generated
requiredness and nullability modifiers are applied before options become
read-only. Schema export, input accounting, result reads and whole assembly use
these same contracts. Typed serializer overloads keep the path independent of
reflection fallback. STJ owns JSON parsing, streaming and error behavior.

The generator discovers leaf and whole roots in the authored compilation. It
does not require another generator's output to become its input. DTO mutations
therefore participate in ordinary compilation and IDE generation.

Attribute-declared concrete converters retain separate instances per metadata
scope. Generated declaration provenance allows the same contract to compose
across assemblies without sharing mutable converter state. Options-level and
authored-context converter instances retain conservative compatibility checks;
some nullable converter wrappers can therefore be rejected during preflight.

## Explicit contexts

An exceptional shape can select an authored `JsonSerializerContext` with
`PromptCommand.JsonContext` or `CompositeOutput.JsonContext`. That context must
declare the applicable roots. Its options retain authority over naming,
converters and unmapped members. Generated validation still applies.

Unsupported framework shapes, attributes or constructor arrangements produce
`AGENTMETA001` rather than silently changing their meaning or enabling reflection.
The supported boundary is deliberate; supplying an explicit context is the
extension seam for contracts outside it.

Automatic metadata covers ordinary accessible record/class DTOs, selected public
constructors, required/init members, common scalar types, `Guid`, `DateOnly`,
enums, nullable values, one-dimensional arrays, lists, string-key dictionaries
and immutable arrays. `byte[]` retains STJ's base64 wire format. Supported JSON
attributes include property names, requiredness, ignore conditions, named derived
types and concrete parameterless type-level converters. Other framework shapes,
property converter factories, extension data and advanced JSON policies require
an explicit context. The build diagnostic identifies the unsupported shape.
The supported ignore conditions are `Always`, `Never`, `WhenWritingNull` and
`WhenWritingDefault`; read-only/write-only ignore policies require an explicit
context.

## Native proof

`mise run test:package` creates a fresh consumer outside the repository and
references only the packed NuGet packages. On macOS ARM64 it also publishes and
runs a native executable with JSON reflection disabled and trimming/AOT warnings
treated as errors. The fixture covers a generated command, MEAI schema and typed
helpers, composition, exact ledger readback, whole-only grading and replay,
explicit context override, JSON edge cases, extraction, propagation and telemetry.

The package check also rejects precompiler tools and build targets in the package.
Native compilation requires its SDK platform toolchain. Other runtime IDs need
their own executed native proofs before claiming equivalent support.

Core, Providers and OpenTelemetry enable `IsAotCompatible` so unsafe library calls
fail normal builds. The native fixture uses a synthetic MEAI client and no vendor
SDKs, API keys, network model calls or containers. External SDK bridges retain
their own NativeAOT compatibility requirements.

The pinned macOS native toolchain can emit SDK debug-map and Swift/Foundation
debug-artifact warnings. Those are reported separately from IL/trimming warnings;
no warning is suppressed. Generator output is checked after unrelated edits, but
incremental step caching and IDE performance have not been established.
