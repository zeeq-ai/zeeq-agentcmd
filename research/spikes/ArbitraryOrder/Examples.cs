namespace ArbitraryOrder;

public static class Examples
{
    public static BoundCommand NameFirst() => Command.Start()
        .WithName("Ada")
        .WithCount(3)
        .Build()
        .WithModel("example-model");

    public static BoundCommand CountFirst() => Command.Start()
        .WithCount(3)
        .WithName("Ada")
        .Build()
        .WithModel("example-model");
}
