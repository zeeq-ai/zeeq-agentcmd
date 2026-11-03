namespace ArbitraryOrder;

// Phantom types encode presence. No marker instances are constructed.
public sealed class Missing { private Missing() { } }
public sealed class Present { private Present() { } }

// This is the shape a generator can emit, hand-written here to test C# 15.
// Each setter changes only its own state and preserves all other states.
public sealed class Draft<TName, TCount>
{
    internal string? Name { get; }
    internal int Count { get; }
    internal Draft(string? name, int count) => (Name, Count) = (name, count);
}

public sealed record BoundCommand(string Name, int Count)
{
    public string? Model { get; init; }
    public BoundCommand WithModel(string model) => this with { Model = model };
}

public static class Command
{
    public static Draft<Missing, Missing> Start() => new(null, 0);
    public static BoundCommand Bind(string name, int count) => new(name, count);

    public static Draft<Present, TCount> WithName<TCount>(
        this Draft<Missing, TCount> draft, string name) => new(name, draft.Count);

    public static Draft<TName, Present> WithCount<TName>(
        this Draft<TName, Missing> draft, int count) => new(draft.Name, count);

    public static BoundCommand Build(this Draft<Present, Present> draft)
        => new(draft.Name!, draft.Count);
}
