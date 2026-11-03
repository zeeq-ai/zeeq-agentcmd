# Arbitrary-order typestate experiment

This hand-written API proves a shape that a source generator could emit. It is separate from the actual incremental generator spike. It compiles under C# 15 on .NET 11 RC3.

`Draft<TName, TCount>` tracks which values were supplied. Each extension method accepts only a draft with its own parameter missing and preserves the other parameter's state. `Build` accepts only `Draft<Present, Present>`. Both setter orders compile. Missing values, repeated setters, and wrong argument types fail compilation. Builders are immutable; branching from a partial builder is safe.

For N parameters, a generic draft needs N state parameters and N setters whose signatures preserve the other N-1 states. This avoids emitting a named class for all 2^N subsets. It still exposes large generic signatures; the number of possible constructed types is exponential and value-type state markers can affect generic code size. This spike uses reference-type markers. No size or performance measurements were made. IntelliSense quality and generated code size at realistic N remain design gates.

Only *presence and static types* are enforced. `null!`, reflection, explicit generic construction through internal access, and malformed deserialization still require runtime validation. The consuming assembly contains generated code, so `internal` constructors are not a security boundary. Hiding completion as an extension method causes a compiler error for incomplete receivers, although tooling may show candidate extensions. This proof permits no duplicate assignment; replacement after binding should use the complete immutable command's API.

Run `python3 spikes/ArbitraryOrder/Tests/run.py`. Compiler and runtime results are saved as `/tmp/opencode/llmcmd-order/tests.xml`; each case's build/run log is saved beside it.
