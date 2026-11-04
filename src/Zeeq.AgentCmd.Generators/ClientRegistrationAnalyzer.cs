using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Zeeq.AgentCmd.Generators;

/// <summary>Checks fully understood, local named-client registrations before runtime validation.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ClientRegistrationAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Missing = new DiagnosticDescriptor(
        "AGENTCLIENT001",
        "Client is not registered",
        "Client '{0}' is not registered in this AddAgentCmd configuration; registered clients: {1}",
        "AgentCmd",
        DiagnosticSeverity.Error,
        true
    );
    private static readonly DiagnosticDescriptor Duplicate = new DiagnosticDescriptor(
        "AGENTCLIENT002",
        "Client is registered twice",
        "Client '{0}' is registered more than once",
        "AgentCmd",
        DiagnosticSeverity.Error,
        true
    );
    private static readonly DiagnosticDescriptor ModelMismatch = new DiagnosticDescriptor(
        "AGENTCLIENT003",
        "Model conflicts with client binding",
        "Model '{0}' conflicts with client '{1}' fixed model '{2}'",
        "AgentCmd",
        DiagnosticSeverity.Error,
        true
    );

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Missing, Duplicate, ModelMismatch);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var builder = start.Compilation.GetTypeByMetadataName("Zeeq.AgentCmd.AgentCmdBuilder");
            var extensions = start.Compilation.GetTypeByMetadataName(
                "Zeeq.AgentCmd.AgentCmdServiceCollectionExtensions"
            );
            if (builder is null || extensions is null)
            {
                return;
            }

            start.RegisterSyntaxNodeAction(
                node => Analyze(node, builder, extensions),
                SyntaxKind.InvocationExpression
            );
        });
    }

    private static void Analyze(
        SyntaxNodeAnalysisContext context,
        INamedTypeSymbol builder,
        INamedTypeSymbol extensions
    )
    {
        var syntax = (InvocationExpressionSyntax)context.Node;
        var name = syntax.Expression switch
        {
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            _ => null,
        };
        if (name != "AddAgentCmd")
        {
            return;
        }

        if (
            context.SemanticModel.GetOperation(syntax, context.CancellationToken)
            is not IInvocationOperation invocation
        )
        {
            return;
        }

        var method = invocation.TargetMethod.ReducedFrom ?? invocation.TargetMethod;
        if (
            method.Name != "AddAgentCmd"
            || !SymbolEqualityComparer.Default.Equals(method.ContainingType, extensions)
            || method.IsGenericMethod
            || !method.IsStatic
            || method.Parameters.Any(p => p.RefKind != RefKind.None)
            || TypeSignature(method.ReturnType)
                != "Microsoft.Extensions.DependencyInjection.IServiceCollection"
            || string.Join("|", method.Parameters.Select(p => p.Name + ":" + TypeSignature(p.Type)))
                != "services:Microsoft.Extensions.DependencyInjection.IServiceCollection|configure:System.Action`1<Zeeq.AgentCmd.AgentCmdBuilder>"
        )
        {
            return;
        }

        var configure = invocation.Arguments.FirstOrDefault(argument =>
            argument.Parameter?.Name == "configure"
        );
        if (
            configure?.Syntax is not ArgumentSyntax argumentSyntax
            || argumentSyntax.Expression is not LambdaExpressionSyntax lambda
        )
        {
            return;
        }

        ParameterSyntax? parameter = lambda switch
        {
            SimpleLambdaExpressionSyntax simple => simple.Parameter,
            ParenthesizedLambdaExpressionSyntax parenthesized
                when parenthesized.ParameterList.Parameters.Count == 1 => parenthesized
                .ParameterList
                .Parameters[0],
            _ => null,
        };
        if (
            parameter is null
            || context.SemanticModel.GetDeclaredSymbol(parameter, context.CancellationToken)
                is not IParameterSymbol receiver
        )
        {
            return;
        }

        var expressions = new List<ExpressionSyntax>();
        if (lambda.Body is ExpressionSyntax expression)
        {
            expressions.Add(expression);
        }
        else if (lambda.Body is BlockSyntax block)
        {
            foreach (var statement in block.Statements)
            {
                if (statement is not ExpressionStatementSyntax statementExpression)
                {
                    return;
                }

                expressions.Add(statementExpression.Expression);
            }
        }
        else
        {
            return;
        }

        var clients = new Dictionary<string, Client>(StringComparer.Ordinal);
        var routes = new List<Route>();
        var duplicates = new List<Client>();
        foreach (var item in expressions)
        {
            if (
                context.SemanticModel.GetOperation(item, context.CancellationToken)
                    is not IInvocationOperation call
                || !ReadChain(
                    call,
                    receiver,
                    builder,
                    context.SemanticModel,
                    context.CancellationToken,
                    clients,
                    routes,
                    duplicates
                )
            )
            {
                // An unknown statement may alter the registration set or prevent later calls.
                return;
            }
        }

        if (duplicates.Count > 0)
        {
            // The first duplicate stops registration, so later route validation is unreachable.
            var duplicate = duplicates[0];
            context.ReportDiagnostic(
                Diagnostic.Create(Duplicate, duplicate.Location, duplicate.Name)
            );
            return;
        }

        foreach (var route in routes)
        {
            if (!clients.TryGetValue(route.Client, out var client))
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        Missing,
                        route.ClientLocation,
                        route.Client,
                        clients.Count == 0
                            ? "(none)"
                            : string.Join(
                                ", ",
                                clients.Keys.OrderBy(key => key, StringComparer.Ordinal)
                            )
                    )
                );
            }
            else if (
                client.FixedModel is not null
                && route.Model is not null
                && !string.Equals(client.FixedModel, route.Model, StringComparison.Ordinal)
            )
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        ModelMismatch,
                        route.ModelLocation,
                        route.Model,
                        route.Client,
                        client.FixedModel
                    )
                );
            }
        }
    }

    private static bool ReadChain(
        IInvocationOperation call,
        IParameterSymbol receiver,
        INamedTypeSymbol builder,
        SemanticModel model,
        CancellationToken cancellationToken,
        Dictionary<string, Client> clients,
        List<Route> routes,
        List<Client> duplicates
    )
    {
        if (
            !SymbolEqualityComparer.Default.Equals(call.TargetMethod.ContainingType, builder)
            || !KnownBuilderOverload(call.TargetMethod, builder)
        )
        {
            return false;
        }

        if (call.Instance is IInvocationOperation previous)
        {
            if (
                !ReadChain(
                    previous,
                    receiver,
                    builder,
                    model,
                    cancellationToken,
                    clients,
                    routes,
                    duplicates
                )
            )
            {
                return false;
            }
        }
        else if (
            call.Instance is not IParameterReferenceOperation parameter
            || !SymbolEqualityComparer.Default.Equals(parameter.Parameter, receiver)
        )
        {
            return false;
        }

        foreach (var argument in call.Arguments.Where(a => !a.IsImplicit))
        {
            // Constant expressions, including nameof(builder), cannot leak the builder.
            if (argument.Value.ConstantValue.HasValue)
            {
                continue;
            }

            if (argument.Syntax.DescendantNodesAndSelf().OfType<ThrowExpressionSyntax>().Any())
            {
                return false;
            }

            // Diagnostics describe topology if setup arguments complete; helper calls do not receive the builder unless it is referenced below.
            if (
                argument
                    .Syntax.DescendantNodesAndSelf()
                    .OfType<IdentifierNameSyntax>()
                    .Any(identifier =>
                        SymbolEqualityComparer.Default.Equals(
                            model.GetSymbolInfo(identifier, cancellationToken).Symbol,
                            receiver
                        )
                    )
            )
            {
                return false;
            }
        }

        if (call.TargetMethod.Name == "AddChatClient")
        {
            var name = Argument(call, "name");
            var fixedModel = Argument(call, "fixedModel");
            if (!TryString(name, out var id) || id is null || string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            _ = TryString(fixedModel, out var fixedValue);
            if (fixedValue is not null && string.IsNullOrWhiteSpace(fixedValue))
            {
                return false;
            }

            var client = new Client(id, fixedValue, name!.Value.Syntax.GetLocation());
            if (clients.ContainsKey(id))
            {
                duplicates.Add(client);
            }
            else
            {
                clients.Add(id, client);
            }
        }
        else if (call.TargetMethod.Name == "AddProfile")
        {
            var client = Argument(call, "client");
            if (client is null)
            {
                // Legacy single-client profile overload has no client parameter.
                return true;
            }

            var modelArg = Argument(call, "model");
            if (!TryString(client, out var id))
            {
                // This route is unknown; it does not change the client registration set.
                return true;
            }

            if (id is null || string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            _ = TryString(modelArg, out var modelName);
            if (modelName is not null && string.IsNullOrWhiteSpace(modelName))
            {
                return false;
            }
            routes.Add(
                new Route(
                    id,
                    modelName,
                    client.Value.Syntax.GetLocation(),
                    modelArg!.Value.Syntax.GetLocation()
                )
            );
        }
        else if (
            call.TargetMethod.Name
            is not (
                "UseActor"
                or "UseBudget"
                or "UseDefaultProfile"
                or "UseChatClient"
                or "UseExperiments"
            )
        )
        {
            return false;
        }

        return true;
    }

    // Pin the supported surface so later overloads cannot silently acquire these semantics.
    private static bool KnownBuilderOverload(IMethodSymbol method, INamedTypeSymbol builder)
    {
        if (
            !SymbolEqualityComparer.Default.Equals(method.ReturnType, builder)
            || method.IsStatic
            || method.IsGenericMethod
            || method.Parameters.Any(p => p.RefKind != RefKind.None)
        )
        {
            return false;
        }

        var signature = string.Join(
            "|",
            method.Parameters.Select(p => p.Name + ":" + TypeSignature(p.Type))
        );
        const string factory =
            "factory:System.Func`2<System.IServiceProvider,Microsoft.Extensions.AI.IChatClient>";
        const string classify =
            "classify:System.Func`2<System.Exception,Zeeq.AgentCmd.FailureKind>";
        const string stringProfile = "name:System.String";
        const string typedProfile = "key:Zeeq.AgentCmd.ProfileKey";
        const string legacy = "|model:System.String|temperature:System.Nullable`1<System.Single>";
        const string route =
            "|client:System.String|model:System.String|defaults:Zeeq.AgentCmd.ProfileDefaults|retry:Zeeq.AgentCmd.RetryPolicy|fallback:Zeeq.AgentCmd.ProfileKey|attemptTimeout:System.Nullable`1<System.TimeSpan>";
        return method.Name switch
        {
            "AddChatClient" => signature
                == "name:System.String|" + factory + "|fixedModel:System.String|" + classify
                || signature
                    == "name:System.String|serviceKey:System.Object|fixedModel:System.String|"
                        + classify,
            "AddProfile" => signature == stringProfile + legacy
                || signature == typedProfile + legacy
                || signature == stringProfile + route
                || signature == typedProfile + route,
            "UseChatClient" => signature == factory,
            "UseActor" => signature
                == "factory:System.Func`2<System.IServiceProvider,Zeeq.AgentCmd.Actor>",
            "UseBudget" => signature == "value:Zeeq.AgentCmd.ExecutionBudget",
            "UseDefaultProfile" => signature == stringProfile || signature == typedProfile,
            "UseExperiments" => signature == "options:Zeeq.AgentCmd.ExperimentRuntimeOptions"
                || signature
                    == "factory:System.Func`2<System.IServiceProvider,Zeeq.AgentCmd.ExperimentRuntimeOptions>",
            _ => false,
        };
    }

    private static string TypeSignature(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named)
        {
            return "";
        }

        var definition = named.OriginalDefinition;
        var name = definition.ContainingNamespace.ToDisplayString() + "." + definition.MetadataName;
        return named.IsGenericType
            ? name + "<" + string.Join(",", named.TypeArguments.Select(TypeSignature)) + ">"
            : name;
    }

    private static IArgumentOperation? Argument(IInvocationOperation invocation, string name) =>
        invocation.Arguments.FirstOrDefault(argument => argument.Parameter?.Name == name);

    private static bool TryString(IArgumentOperation? argument, out string? value)
    {
        value = null;
        if (argument is null || !argument.Value.ConstantValue.HasValue)
        {
            return false;
        }

        var constant = argument.Value.ConstantValue.Value;
        if (constant is null || constant is string)
        {
            value = (string?)constant;
            return true;
        }

        return false;
    }

    private sealed class Client
    {
        public Client(string name, string? fixedModel, Location location)
        {
            Name = name;
            FixedModel = fixedModel;
            Location = location;
        }

        public string Name { get; }
        public string? FixedModel { get; }
        public Location Location { get; }
    }

    private sealed class Route
    {
        public Route(string client, string? model, Location clientLocation, Location modelLocation)
        {
            Client = client;
            Model = model;
            ClientLocation = clientLocation;
            ModelLocation = modelLocation;
        }

        public string Client { get; }
        public string? Model { get; }
        public Location ClientLocation { get; }
        public Location ModelLocation { get; }
    }
}
