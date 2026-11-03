using System;
namespace Demo;
[Prompt("support.reply", "For {customer}, answer {question}. Literal {{braces}}.")]
public static partial class SupportReply
{
    static partial void Define(string customer, string question);
}
public enum Language { English = 1, French = 2 }
[Prompt("sample.optional", "For {customer}: {count} {tone} {locale}")]
public static partial class OptionalExample
{
    static partial void Define(string? customer, int count = 3, string? tone = null, Language locale = Language.English);
}
public static class Program
{
    public static void Main()
    {
        var atomic = SupportReply.Bind("Ada", "Where is my order?").WithModel("example-model");
        var chained = SupportReply.Start().WithCustomer("Ada").WithQuestion("Where is my order?").Build();
        var optional = OptionalExample.Start().WithCustomer(null).Build().WithCount(5).WithTone("polite");
        Console.WriteLine($"{SupportReply.Id}: {atomic.customer}, {chained.question}");
    }
}
