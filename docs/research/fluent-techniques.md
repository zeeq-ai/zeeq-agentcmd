# Strongly typed fluent techniques for LlmCmd commands

Owner: System Architect. Status: research, no implementation. Date: 2026-11-01.

Legend:
- **[V]** = verified by a compile run or by reading a primary source at the stated commit.
- **[S]** = stated in official docs (quoted or paraphrased, with a link).
- **[K]** = from my knowledge, not re-verified here.
- **[P]** = proposal.

## 1. Summary

1. **[P] Required parameters: use generic typestate.** Use one invariant sealed class `Draft<T1..TN>` with sealed, uninstantiable `Missing`/`Present` markers. Each setter is a C# 14 extension block that is applicable only while its own slot is `Missing`. This gives any order, compile errors for missing or duplicate parameters, and N type arguments instead of 2^N classes. **[V]** The compile cost is flat up to N=32 (section 4).
2. **[P] Readable errors.** Add a generic `[Obsolete(error: true)]` `Build<T1..TN>` guard overload. An incomplete chain then reports CS0619 with our message, not an opaque CS1929. **[V]**
3. **[P] Settings must not erase the command type.** Fluency and execution typing need separate interfaces: `ISettable<TSelf>` and `ICommand<TSelf,TResponse>`. The executor takes `ICommand<TCmd,TResponse>` as its parameter type, not `TCmd` with a constraint. **[V]** The constraint-only form gives CS0411.
4. **[P] Honest limits.** The type system guarantees the chain *shape* only. It cannot follow statement-level control flow (`if` reassignment, ternary branches). `default`/`null!`, discarded results and reflection bypass it. Keep run-time validation in `Build`/executor. Analyzers add heuristics, not guarantees (section 7).

## 2. Primary-source precedents

### LINQ: a state change through the return type [V]
dotnet/runtime @ `28978b0662e7652dc40e7a4146ad6d0629c94ecc`, `src/libraries/System.Linq/src/System/Linq/OrderBy.cs`:
- L50 `public static IOrderedEnumerable<TSource> OrderBy<TSource, TKey>(this IEnumerable<TSource> source, Func<TSource, TKey> keySelector)`
- L101 `public static IOrderedEnumerable<TSource> ThenBy<TSource, TKey>(this IOrderedEnumerable<TSource> source, Func<TSource, TKey> keySelector)`

Lesson: `ThenBy` is reachable only after `OrderBy`, because the receiver type encodes the state. This is one bit of state with a named interface. It does not scale to N independent bits.

### EF Core: a covariant include state with an overload split by property shape [V]
dotnet/efcore @ `d223635279d624518718f37e3221ade8943ff4eb`:
- `src/EFCore/Query/IIncludableQueryable.cs` L16: `public interface IIncludableQueryable<out TEntity, out TProperty> : IQueryable<TEntity>;`
- `src/EFCore/Extensions/EntityFrameworkQueryableExtensions.cs`:
  - L2731 `Include<TEntity, TProperty>(this IQueryable<TEntity> source, Expression<Func<TEntity, TProperty>> navigationPropertyPath) where TEntity : class`
  - L2778 `ThenInclude<TEntity, TPreviousProperty, TProperty>(this IIncludableQueryable<TEntity, IEnumerable<TPreviousProperty>> source, …)`
  - L2809 `ThenInclude<TEntity, TPreviousProperty, TProperty>(this IIncludableQueryable<TEntity, TPreviousProperty> source, …)`

Lessons:
- The *previous* step's type flows as a generic argument (`TPreviousProperty`). Overloads specialise on its shape (a collection or a reference).
- Covariance (`out`) is deliberate: an includable query is still an `IQueryable<TEntity>`.
- For required-parameter typestate, covariance is a hazard (section 5.4).

### FluentValidation: rule availability by property type [V]
FluentValidation @ `4984ff538e26b03c296f1fbc41085e9d6d687206`:
- `src/FluentValidation/AbstractValidator.cs` L210 `public IRuleBuilderInitial<T, TProperty> RuleFor<TProperty>(Expression<Func<T, TProperty>> expression)`
- `src/FluentValidation/Syntax.cs`:
  - L29 `IRuleBuilderInitial<T, out TProperty> : IRuleBuilder<T,TProperty>`
  - L37 `IRuleBuilder<T, out TProperty>`
  - L82 `IRuleBuilderOptions`
  - L94 `IRuleBuilderOptionsConditions`
  - L106 `IRuleBuilderInitialCollection`
- `src/FluentValidation/DefaultValidatorExtensions.cs`:
  - L75 `EmailAddress<T>(this IRuleBuilder<T, string> …)`
  - L113 `NotEmpty<T, TProperty>(this IRuleBuilder<T, TProperty> …)`
  - L136 `Length<T>(this IRuleBuilder<T, string> ruleBuilder, int min, int max)`

Lessons:
- Close a type argument in the extension receiver (`string`) to gate an operation on the type.
- Use a phase-specific interface (`Initial` → `Options` → `OptionsConditions`) to gate an operation on the phase.
- This is the same mechanism as the `Missing`/`Present` slots, applied per parameter.

## 3. Language features in scope

- **[S] C# 14 extension blocks.** `extension<T>(Receiver<T> r) { … }` inside a top-level non-generic static class. Allowed members: methods, properties, indexers and operators.
  - Type parameters used in the receiver go on the `extension` declaration. Other type parameters go on the member. The same parameter cannot be in both.
  - The block form and the `this` form "generate the same intermediate language (IL)" and are "binary and source compatible".
  - C# 15 adds extension indexers.
  - Source: <https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/extension> (dotnet/docs @ `433547d0bf6a23201359df8263191c227f002fd3`, ms.date 2026-10-19).
  - Consequence: the typestate design does not *need* C# 14. Classic `this` extension methods emit the same IL (the root agent's two-parameter proof `spikes/ArbitraryOrder` uses them). Blocks only group the setters and allow extension *properties*. These are not useful for setters that take arguments.
- **[V] Extension-block type parameters must be inferable from the receiver.** `extension<TCmd, TResponse>(TCmd c) where TCmd : ICommand<TCmd, TResponse>` compiles, but every call gives CS0411. `TResponse` appears only in the constraint, and inference ignores constraints.
- **[S] Static abstract interface members.** Calls resolve at compile time from the type argument. "Method dispatch … is resolved by using the compile-time type of an expression."
  - In C# 15, *non-virtual* static interface members no longer need runtime default-interface-member support. `static abstract`/`static virtual` members keep their runtime requirements.
  - Source: <https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/interface> (dotnet/docs @ `433547d0…`).
  - Consequence: `TCmd.PromptId` is a compile-time-bound lookup. A command that is passed as a base type or interface reads the static members of the *static* type.

## 4. Compile verification

Environment:
- SDK 10.0.302, `net10.0`, `LangVersion 14`, `Nullable enable`, `TreatWarningsAsErrors true`.
- Scratch in `/tmp/fluent-lab` (deleted after this doc).
- One `Lib.cs` (96 lines) plus 20 single-file `Program.cs` cases. Each case does a full rebuild, and the cases that should compile are run.

**Result: 20/20 cases behave as expected.**

| # | Case | Expected | Observed |
|---|---|---|---|
| 1 | `Start().WithName.WithCount.WithTone.Build()` | compile + run | ok |
| 2 | reverse order `WithTone.WithCount.WithName` | compile + run | ok |
| 3 | missing `WithTone`, then `Build()` | guard message | CS0619 "ReplyCommand requires name, count and tone…" |
| 4 | `WithName` twice | error | CS1929 (no applicable receiver) |
| 5 | branch from a shared prefix `a` into two chains | independent results | ok |
| 6 | `flag ? Start().WithName("a") : Start()` | error | CS0173 |
| 7 | generic helper `Draft<Present,TC,TT> Named<TC,TT>(Draft<Missing,TC,TT>)` | state preserved | ok |
| 8 | non-generic helper through `object` + cast | compiles (erased) | ok: a wrong cast fails only at run time |
| 9 | `new ReplyCommand(..).WithModel().WithTemperature()` + `Executor.Run(cmd)` | `ReplyCommand` kept, `ReplyAnswer` inferred, `TCmd.PromptId` read | ok |
| 10 | `RunConstrained<TCmd,TResponse>(TCmd) where TCmd : ICommand<TCmd,TResponse>` | inference fails | CS0411 |
| 11 | base-record `WithModel` assigned to the derived type | error | CS0266 |
| 12 | extension block with a constraint-only type parameter | inference fails | CS0411 at the call |
| 13 | generic `Tagged()` between setters | state preserved | ok |
| 14 | `default(SDraft<Present>).Build()` (struct draft) | compiles: **bypass** | ok, null fields |
| 15 | `default(Draft<P,P,P>).Build()` (class draft) | error | CS8625 (a nullable *warning*, an error only under TreatWarningsAsErrors) |
| 16 | `default(Draft<P,P,P>)!.Build()` | compiles: **bypass** | NRE at run time |
| 17 | covariant `ICovDraft<out T>` + marker inheritance | compiles: **re-opens a filled slot** | ok |
| 18 | `if (x) d = d.WithName("a");` | error | CS0029 |
| 19 | `d.WithName("lost");` (result discarded) | compiles silently | ok |
| 20 | `new Present()` | error | CS0122 |

**Scale [V].** A generator script emitted `Draft<T0..TN-1>` with one extension block per slot, a complete `Build` and the generic guard, for N = 3, 8, 16 and 32. For each N:
- a full chain in reverse order compiled and ran;
- dropping the last setter gave CS0619.

The rebuild wall time was 1.0–1.2 s at every N, so it was dominated by fixed overhead. Generated source grows O(N²), because each setter names N type arguments: 1.0 KB at N=3 and 16.6 KB at N=32. IDE hover text and error messages also grow with N. The guard overload keeps the *diagnostic* short.

**C# 15 / .NET 11 RC re-run: BLOCKED.** The local RC muxer (`~/.local/share/mise/installs/dotnet/10.0.402/dotnet`, which has the 11.0.100-rc.3.26522.148 SDK) is SIGKILLed on start (exit 137). It was modified on 2026-11-01 12:01, probably by an install in progress. **[K]** C# 15 does not change extension resolution, overload tie-breaking or type inference for these cases. The root agent should run this suite on the pinned SDK (`global.json` 11.0.100-rc.3) before relying on it.

## 5. Technique comparison

### 5.1 Typestate encodings

| Encoding | Types emitted | Order | Missing-parameter error | Notes |
|---|---|---|---|---|
| Ordered stage interfaces (`Stage0..N`, current `spikes/TypedCommands`) | N | fixed | CS1061 "no WithX on StageK" | simple; order is part of the API |
| Named class per subset | 2^N | any | CS1061 | impractical above N≈4 |
| **Generic slots `Draft<T1..TN>`** (root's `spikes/ArbitraryOrder`, extended here) | 1 class + 2 markers + N setters | any | CS1929, or **CS0619 with our text** via the guard | **[P] recommended** |
| `required init` members + object initializer | 0 | any | CS9035 | not fluent; bypassed by `[SetsRequiredMembers]` and reflection |
| Constructor / `Bind(a, b, c)` | 0 | positional | CS7036 | already generated; keep it as the non-fluent path |

[P] Generate both `Bind(...)` (positional, best for many parameters) and the generic-slot fluent chain from the same `Define` signature. Above about 12 required parameters, document `Bind` as the preferred form. The type system scales, but tooltips get noisy.

### 5.2 Extension methods vs generic constraints
- Gate the state through the **receiver type** (an extension on `Draft<Missing, TC, TT>`). The compiler then *selects* members by state, and absent members do not appear in IntelliSense.
- Gating through **constraints** (`where TName : Missing`) does not work for overload selection: constraints are not part of the signature for inference, which gives CS0411 (cases 10 and 12). It also cannot express "not yet set" without marker inheritance (5.4).
- Rule: every state type parameter must appear in the receiver. Constraints are for capability only (e.g. `TCmd : ISettable<TCmd>`).

### 5.3 Immutable chain branching
- [V] The draft is an immutable sealed class. Every setter returns a new instance, so a shared prefix is reusable (case 5).
- [P] Keep drafts immutable. A mutable builder that returns `this` with a cast would make case 5 alias silently.
- The allocation cost is one small object per step. This is negligible next to an LLM call.

### 5.4 Variance and marker hygiene
- [V] Case 17: with a covariant interface and `PresentSub : MissingBase`, a filled draft converts to the "missing" receiver, and its setter re-appears.
- [P] Rules:
  - the draft is an **invariant sealed class** (not an interface, not a struct);
  - the markers are **sealed, unrelated** classes with private constructors;
  - the draft constructor is `internal`, and generated code is the only producer.
- EF and FluentValidation use `out` on purpose. Do not copy that.

### 5.5 Return types through WithSettings / WithModel
- [V] A fluent method on a base record returns the base. The `with` expression keeps the run-time type, but the static type is lost (case 11, CS0266).
- [V] Self-typed `ISettable<TSelf>` + `extension<TCmd>(TCmd cmd) where TCmd : ISettable<TCmd>` keeps the concrete command type through any number of settings calls (case 9). Here the constraint is fine, because `TCmd` *is* the receiver.
- [V] For drafts, one block `extension<A,B,C>(Draft<A,B,C> d)` that returns `Draft<A,B,C>` lets settings appear anywhere in the chain without changing the state (case 13). [P] The generator emits it once per draft arity.
- [V] Response typing: write the executor as `Run<TCmd,TResponse>(ICommand<TCmd,TResponse> cmd)`. Inference then binds both from the implemented interface, and `TCmd.PromptId` stays available (case 9).
- [P] Keep `ISettable<TSelf>` (fluency) separate from `ICommand<TSelf,TResponse>` (execution). Settings extensions then never need an uninferable `TResponse`.

### 5.6 Control flow and helpers
- [V] Statement-level branching cannot change a variable's state type (cases 6 and 18). [P] Patterns that work:
  - a complete chain per branch, then a common supertype after `Build()`;
  - generated conditional setters for optional values (`WithTone(string? tone)` on the *optional* slot only);
  - for a value that is required but sometimes defaulted, have the caller compute the value first and set it once.
- [V] Generic helpers preserve state when they are generic in the untouched slots (case 7). [P] Document this as the supported way to factor out chain fragments.
- [V] Erasure through `object`, `dynamic` or reflection loses the guarantee (case 8). A bad cast fails at run time with `InvalidCastException` **[K]**. It does not silently produce wrong data.

## 6. What the type system does and does not guarantee

| Property | Guarantee | Bypass |
|---|---|---|
| Each required parameter set exactly once in a fluent chain | **compile-time** | `default`/`null!` draft (cases 15–16); struct drafts (case 14) |
| Parameter value types | compile-time | none (beyond `dynamic`) |
| Response type flows to the executor result | compile-time | casting the command to a base type |
| A chain result is used | **none** | discarded results compile (case 19) |
| Override/Redis values match the schema | none | run time only |
| Deserialized or reflected commands are complete | none | run time only |

[P] Consequence: `Build()` and the executor still validate. Expected failures return a closed `ParameterMissing` / `ParameterInvalid` result, not an exception, as agreed earlier for ExecutionResult.

## 7. Analyzers: an honest characterization

Mechanisms:
- [S] Roslyn exposes an intra-procedural `ControlFlowGraph`, built from an operation body, e.g. `ControlFlowGraph.Create(IBlockOperation body, …)` (dotnet/roslyn @ `c0c418248c9760d912747320a10392d0aab49cd2`, `src/Compilers/Core/Portable/Operations/ControlFlowGraph.cs` L24, L105).
- [S] The roslyn-analyzers dataflow framework (used by CA1062, CA1303, CA1508, CA2000, CA2100, CA2213 and the taint rules) is configurable through `.editorconfig`:
  - `interprocedural_analysis_kind` = `None | NonContextSensitive | ContextSensitive`; the default is rule-specific;
  - `max_interprocedural_method_call_chain`, default 3;
  - `max_interprocedural_lambda_or_local_function_call_chain`, default 3;
  - `points_to_analysis_kind` = `None | PartialWithoutTrackingFieldsAndProperties | Complete`.
  - Source: <https://github.com/dotnet/roslyn-analyzers/blob/main/docs/Analyzer%20Configuration.md> (repo head `4e301e92afe5e027796b14c36d5049c855b2e9a7`; exact line numbers not captured).
  - Interprocedural analysis applies to *source* method invocations only, within bounded chain depths.

What an LlmCmd analyzer could do, and where it fails:

| Scenario | Typestate (compiler) | Custom analyzer |
|---|---|---|
| Straight-line chain | exact | exact |
| Local alias (`var a = d; a.With…`) | exact (by type) | needs points-to/copy analysis; misses are possible |
| Helper in the same assembly | exact if generic (case 7) | only to depth ≈3 and only if interprocedural analysis is enabled; otherwise a false negative |
| Helper in another assembly / NuGet | exact (by type) | **no body to analyze**: false negative, or a forced annotation |
| Branches and loops | rejected unless expression-shaped | CFG merge: "maybe set" means warn or stay silent; heuristic either way |
| Lambdas, async, iterators | exact (by type) | limited by the lambda chain depth |
| Reflection, `dynamic`, deserialization | bypassed | invisible |
| Suppression | `!`/`default` only | `#pragma`, `[SuppressMessage]`, `.editorconfig` severity = none |

Conclusions:
- [P] Do not use an analyzer for required-parameter enforcement. Typestate is a type-system guarantee across aliases, helpers and assemblies. An analyzer is a heuristic with both false negatives and false positives.
- [P] One narrow analyzer is worth having: **"fluent result discarded"** (case 19, an `ExpressionStatement` whose value is a `Draft<…>` or command). It is intra-procedural and syntactic, so it has no false negatives in that shape. [K] BCL `CA1806` covers only fixed APIs and does not know our types.
- [P] Optional second analyzer: flag `default(Draft<…>)`, `null!` and `Unsafe.As` targeting draft types (cases 14–16) as a defence in depth. It is syntactic and easy to bypass, so treat it as a lint, not a proof.

## 8. Recommendations for the generator [P]

1. Emit `Draft<T1..TN>` as a sealed invariant class with an internal constructor, plus shared `Missing`/`Present` markers in the runtime package.
2. Emit one extension block per required slot, one generic settings block per arity, and one complete `Build` that returns the command record.
3. Emit a generic `[Obsolete(error: true)]` guard `Build<T1..TN>` in a *separate* static class. Its message lists the required parameter names in declaration order.
4. Keep `Bind(...)` as the positional alternative. Both paths produce the same record, so tests can compare them.
5. Command records implement `ISettable<TSelf>` and `ICommand<TSelf,TResponse>` (with `static abstract PromptId`, revision and template). Executor entry points take `ICommand<TCmd,TResponse>`.
6. Validate in `Build()` and in the executor anyway (section 6).
7. Optional analyzer: "discarded fluent result" only.
8. Tests (root-owned, TUnit): port the 20 cases above as compile-fail and compile-pass fixtures, plus the N=32 scale case. Run them on the pinned .NET 11 RC SDK.

## 9. Open items
- Re-run section 4 on SDK 11.0.100-rc.3 / C# 15 (blocked locally; see section 4).
- Measure IDE responsiveness (completion and hover) at N≥16. Wall-clock build time is not a proxy.
- Decide the maximum supported required-parameter count before recommending `Bind`.
