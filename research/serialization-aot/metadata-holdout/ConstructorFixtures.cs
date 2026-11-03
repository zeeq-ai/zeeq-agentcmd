using System.Text.Json.Serialization;
using SerializationSpikes;

/// <summary>An unmarked overload must not turn optional init members into constructor requirements.</summary>
[CompositeOutput]
public sealed class DefaultConstructorChoice
{
    public DefaultConstructorChoice()
    {
        Name = "public-default";
    }

    public DefaultConstructorChoice(string name, int count)
    {
        Name = name;
        Count = count;
    }

    public string Name { get; init; }
    public int Count { get; init; } = 7;
}

/// <summary>The author's explicit choice takes precedence over an available default constructor.</summary>
[CompositeOutput]
public sealed class MarkedConstructorWithDefault
{
    public MarkedConstructorWithDefault()
    {
        Name = "wrong-constructor";
    }

    [JsonConstructor]
    public MarkedConstructorWithDefault(string name)
    {
        Name = name;
    }

    public string Name { get; }
}
