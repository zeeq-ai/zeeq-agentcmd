# Executable research archive

The original spikes, their test assemblies and Python orchestration are preserved here. The archive has its own frozen SDK pin and writes reports under `/tmp/opencode/llmcmd-research-*` so historical runs cannot overwrite current library evidence. They use their original namespaces and project relationships. The maintained library lives in `../src`; current tests live in `../tests` and current examples in `../examples`.

`Directory.Build.props`, `.targets` and `Directory.Packages.props` here intentionally isolate these historical projects from the library's current build policy. None is a dependency of a source library package.

From the repository root, `mise run verify:research` re-runs the original offline verification. It needs Python and the pinned SDK. `mise run spike:secrets`, `spike:observability` and `spike:propagation` target the archived AppHosts explicitly; the latter two need Docker. They are not dependencies of normal library tests. Paid API calls remain opt-in only in the maintained live-provider test project.

The original 35 execution-capture tests exercise an alternative executor and fake pricing implementation. They remain evidence, rather than being presented as tests of the promoted library. The richer compiler and precompile scripts are retained beside the sources they originally tested.

Precompile tools remain here only for historical reproduction. The maintained
library emits STJ metadata directly and has a package-only NativeAOT proof. See
[the current serialization boundary](../docs/architecture/serialization-and-aot.md)
and [the serializer decision archive](serialization-aot/README.md).
