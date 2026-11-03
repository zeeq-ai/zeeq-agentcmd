public abstract class PromptCommand<TParameters, TResult>;

public sealed class Command : PromptCommand<Unsupported, Unsupported>;

public sealed record Unsupported(double Value);
