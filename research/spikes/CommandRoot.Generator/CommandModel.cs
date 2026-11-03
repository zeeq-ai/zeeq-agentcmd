using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal sealed class PropertyModel
{
    public IPropertySymbol Symbol = null!;
    public string? JsonName;
    public bool Required;
    public bool Ignored;
}
internal sealed class ContractTypeModel
{
    public INamedTypeSymbol Type = null!;
    public List<PropertyModel> Properties = new List<PropertyModel>();
}
internal sealed class CommandModel
{
    public INamedTypeSymbol Type = null!, Context = null!;
    public ITypeSymbol Parameters = null!, Result = null!;
    public ClassDeclarationSyntax Syntax = null!;
    public string Id = "", Profile = "", System = "", User = "";
    public int Revision;
    public List<PropertyModel> ParametersShape = new List<PropertyModel>();
    public List<ContractTypeModel> Contracts = new List<ContractTypeModel>();
    public string ParameterAlias = "", ResultAlias = "";
    public string? InputValidator, ResultValidator;
}
internal static class CommandModels
{
    internal static readonly SymbolDisplayFormat NullableTypeFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithMiscellaneousOptions(SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);
    internal static string Type(ITypeSymbol type) => type.ToDisplayString(NullableTypeFormat);
    internal static string RuntimeType(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    internal static bool IsCommand(INamedTypeSymbol type) => type.BaseType?.OriginalDefinition.ToDisplayString() == "CommandRoot.PromptCommand<TP, TR>";
    internal static string? Constant(INamedTypeSymbol type,string name) => type.GetMembers(name).OfType<IFieldSymbol>().FirstOrDefault(f=>f.IsConst)?.ConstantValue as string;
    internal static int? Integer(INamedTypeSymbol type,string name) => type.GetMembers(name).OfType<IFieldSymbol>().FirstOrDefault(f=>f.IsConst)?.ConstantValue as int?;
    internal static IEnumerable<IPropertySymbol> Properties(ITypeSymbol type)
    {
        var seen=new HashSet<string>(StringComparer.Ordinal);
        for(var current=type as INamedTypeSymbol;current is not null;current=current.BaseType)
            foreach(var p in current.GetMembers().OfType<IPropertySymbol>())
                if(!p.IsStatic && !p.IsIndexer && p.DeclaredAccessibility==Accessibility.Public
                    && p.GetMethod?.DeclaredAccessibility==Accessibility.Public && seen.Add(p.Name)) yield return p;
    }
    internal static bool Scalar(ITypeSymbol type)
    {
        if(type is INamedTypeSymbol n && n.OriginalDefinition.SpecialType==SpecialType.System_Nullable_T) type=n.TypeArguments[0];
        return type.TypeKind==TypeKind.Enum || type.SpecialType is SpecialType.System_String or SpecialType.System_Char or SpecialType.System_Boolean
            or SpecialType.System_Byte or SpecialType.System_SByte or SpecialType.System_Int16 or SpecialType.System_UInt16
            or SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64
            or SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal;
    }
    internal static List<ContractTypeModel> ContractGraph(ITypeSymbol parameters,ITypeSymbol result,Action<string> error)
    {
        var models=new List<ContractTypeModel>(); var seen=new HashSet<ITypeSymbol>(SymbolEqualityComparer.IncludeNullability);
        var runtimeShapes=new Dictionary<string,ITypeSymbol>(StringComparer.Ordinal);
        Visit(parameters); Visit(result); return models;
        void Visit(ITypeSymbol type)
        {
            if(type is IArrayTypeSymbol array) { Visit(array.ElementType); return; }
            if(type is not INamedTypeSymbol named) return;
            named=(INamedTypeSymbol)named.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
            if(!seen.Add(named)) return;
            if(named.IsGenericType) foreach(var argument in named.TypeArguments) Visit(argument);
            var ns=named.ContainingNamespace.ToDisplayString();
            if(Scalar(named) || ns=="System" || ns.StartsWith("System.",StringComparison.Ordinal)) return;
            var key=RuntimeType(named);
            if(runtimeShapes.TryGetValue(key,out var existing) && !SymbolEqualityComparer.IncludeNullability.Equals(existing,named))
            { error("Conflicting nullable generic contracts for "+key+". Use distinct DTO types so one JSON contract has one meaning."); return; }
            runtimeShapes[key]=named;
            var constructors=named.InstanceConstructors.Where(c=>c.DeclaredAccessibility==Accessibility.Public
                && !(named.IsRecord && c.Parameters.Length==1 && SymbolEqualityComparer.Default.Equals(c.Parameters[0].Type,named))).ToArray();
            var selected=constructors.Where(c=>c.GetAttributes().Any(a=>a.AttributeClass?.ToDisplayString()=="System.Text.Json.Serialization.JsonConstructorAttribute")).ToArray();
            IMethodSymbol? constructor=selected.Length==1 ? selected[0] : constructors.Length==1 ? constructors[0]
                : constructors.FirstOrDefault(c=>c.Parameters.Length==0);
            if(selected.Length>1 || constructor is null && constructors.Length>1)
            { error("Multiple constructors require an explicit [JsonConstructor] for "+RuntimeType(named)); return; }
            var model=new ContractTypeModel { Type=named };
            foreach(var attribute in named.GetAttributes().Where(a=>a.AttributeClass?.ToDisplayString()=="System.Text.Json.Serialization.JsonDerivedTypeAttribute"))
                if(attribute.ConstructorArguments.FirstOrDefault().Value is ITypeSymbol derived) Visit(derived);
            foreach(var p in Properties(named))
            {
                var attributes=p.GetAttributes();
                var ignore=attributes.FirstOrDefault(a=>a.AttributeClass?.ToDisplayString()=="System.Text.Json.Serialization.JsonIgnoreAttribute");
                var ignored=ignore is not null && (!ignore.NamedArguments.Any(x=>x.Key=="Condition")
                    || ignore.NamedArguments.Any(x=>x.Key=="Condition" && Convert.ToInt32(x.Value.Value)==1));
                var parameter=constructor?.Parameters.FirstOrDefault(v=>string.Equals(v.Name,p.Name,StringComparison.OrdinalIgnoreCase)
                    && SymbolEqualityComparer.Default.Equals(v.Type,p.Type));
                var member=new PropertyModel { Symbol=p,Ignored=ignored,
                    JsonName=attributes.FirstOrDefault(a=>a.AttributeClass?.ToDisplayString()=="System.Text.Json.Serialization.JsonPropertyNameAttribute")?.ConstructorArguments.FirstOrDefault().Value as string,
                    Required=!ignored && (p.IsRequired || attributes.Any(a=>a.AttributeClass?.ToDisplayString()=="System.Text.Json.Serialization.JsonRequiredAttribute")
                        || parameter is not null && !parameter.IsOptional
                        || parameter is null && p.SetMethod is not null && p.Type.IsReferenceType && p.Type.NullableAnnotation==NullableAnnotation.NotAnnotated) };
                model.Properties.Add(member);
                if(!ignored) Visit(p.Type);
            }
            models.Add(model);
        }
    }
}
