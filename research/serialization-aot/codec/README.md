# Generated codec experiment

This is an isolated research candidate. It does not establish that LlmCmd is
NativeAOT-compatible. The candidate has no `JsonSerializable` registrations
and does not use the oracle's resolver. The common app links the oracle only for
comparison. The generator discovers command and composite roots with Roslyn,
then emits readers, writers and schema from its symbol graph.

The common harness passed 52 of 52 checks in a native `osx-arm64` binary with
reflection disabled. The separate TUnit suite passed eight tests and the TUnit
wrapper for the shared harness passed one test. Their results are recorded in
`/tmp/opencode/llmcmd-serialization-aot/codec/tests.log` and
`/tmp/opencode/llmcmd-serialization-aot/codec/common-tests.log`. The native
shared harness wrote `common-native-report/tests.xml` under that directory.
The TUnit runs produced HTML reports, not the requested JUnit files.

```sh
mise exec -- dotnet publish research/serialization-aot/codec/Common/Common.csproj -c Release -r osx-arm64 -p:TreatWarningsAsErrors=true -p:ILLinkTreatWarningsAsErrors=true -p:TrimmerSingleWarn=false -o /tmp/opencode/llmcmd-serialization-aot/codec/common-native
/tmp/opencode/llmcmd-serialization-aot/codec/common-native/Common --report-dir=/tmp/opencode/llmcmd-serialization-aot/codec/common-native-report
mise exec -- dotnet run --project research/serialization-aot/codec/Tests -- --junit-output-path /tmp/opencode/llmcmd-serialization-aot/codec/tests.xml
mise exec -- dotnet run --project research/serialization-aot/codec/CommonTests -- --junit-output-path /tmp/opencode/llmcmd-serialization-aot/codec/common-tests.xml
mise exec -- dotnet build research/serialization-aot/codec/Negative/Negative.csproj
```

The last command intentionally fails with `CODEC001` for `double`. A separate
copy under `/private/tmp/opencode/llmcmd-serialization-aot/codec/mutation/`
adds a Boolean constructor parameter to the smoke DTO. Its generated reader,
writer and schema include that field; see `mutation-proof.log`. The generator
has 1,184 authored lines. The common fixture emitted 297 lines of generated
codec code. Dependencies added for the spike are Microsoft.CodeAnalysis.CSharp
4.14.0 and TUnit 1.78.0.

The supported sample covers positional records, a narrow init-only class,
closed generic members, arrays, read-only lists, string-keyed dictionaries,
immutable arrays, string/int/decimal/bool, DateOnly and Guid, concrete
`JsonConverter<T>`, string enums, and attributed string-discriminator
polymorphism. Nullable annotations are carried for the tested closed generic.
The schema for an opaque converter is JSON `true`, matching this SDK's oracle;
it cannot express converter validation. The common test also demonstrates that
collection null elements are accepted, as with the STJ baseline.

The following boundaries need work before any production proposal:

- The codec fixes its JSON policy and constructs new converter options. It has
  no integration with arbitrary host `JsonSerializerOptions` or custom naming,
  converters and settings supplied through those options.
- The generator chooses the first public parameterized record constructor. It
  does not implement the full STJ constructor selection, attribute, naming,
  ordering or collision rules. Init classes accept only scalar init properties
  and literal defaults. Some unsupported forms produce diagnostics, but not all
  unsupported variations are diagnosed. For example conditional `JsonIgnore`
  and enum aliases require further handling.
- Nullable closed generic arguments share a CLR runtime identity. A root set
  containing both nullable and non-nullable instantiations needs an explicit
  contract identity policy; generic and cross-assembly cases are not covered
  broadly. Source syntax is used for init defaults, so external DTOs need
  further investigation.
- Path reporting handles the tested nested property and array paths. Escaped
  property names, dictionary keys, converter failures and broader exception
  cases need parity tests. Serialization also needs a full nullable-value audit.
- The polymorphic reader and writer buffer through JSON documents and byte
  arrays. They handle the tested string-discriminator form, not all STJ
  polymorphism options. Recursive graphs are rejected. The codec does not
  provide a streaming or `ReadOnlySequence<byte>` interface.
- The incremental generator collects all discovered type declarations before
  building the graph. Incremental invalidation, deterministic ordering, semantic
  identity of the command marker, diagnostic coverage and build-time costs need
  more work. The generated schema matches this fixture, not the full STJ schema
  surface.

The narrow fallback is the existing generated STJ metadata/precompile contract
for shapes the codec cannot safely own. Production use of this codec requires
explicit diagnostics or a safe dispatch boundary for every unsupported shape;
silently assuming coverage is not safe.
