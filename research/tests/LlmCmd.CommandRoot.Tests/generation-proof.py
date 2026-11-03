#!/usr/bin/env python3
"""Isolated generator identity and startup checks. No provider traffic."""
import os
import pathlib
import re
import subprocess
import tempfile
import xml.etree.ElementTree as ET

root = pathlib.Path(__file__).resolve().parents[2]
dotnet = os.environ.get('DOTNET_HOST_PATH', 'dotnet')
out = pathlib.Path('/tmp/opencode/llmcmd-research-commandroot-generation')
out.mkdir(parents=True, exist_ok=True)
suite = ET.Element('testsuite', name='CommandRoot.Generation')
failed = 0


def check(name, action):
    global failed
    case = ET.SubElement(suite, 'testcase', name=name)
    try:
        action()
        print(name + ' PASS', flush=True)
    except Exception as error:
        failed += 1
        ET.SubElement(case, 'failure', message=str(error)).text = str(error)
        print(name + ' FAIL: ' + str(error), flush=True)


with tempfile.TemporaryDirectory(dir=out.resolve()) as directory:
    project = pathlib.Path(directory)
    (project / 'ContractProof.csproj').write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><OutputType>Exe</OutputType><AssemblyName>ContractProof</AssemblyName><TargetFramework>net11.0</TargetFramework><LangVersion>15</LangVersion><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><TreatWarningsAsErrors>true</TreatWarningsAsErrors><JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault><EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles><CompilerGeneratedFilesOutputPath>obj/generated</CompilerGeneratedFilesOutputPath></PropertyGroup>
<ItemGroup><ProjectReference Include="{root / 'spikes/CommandRoot/Runtime/Runtime.csproj'}"/><ProjectReference Include="{root / 'spikes/CommandRoot.Generator/Generator.csproj'}" OutputItemType="Analyzer" ReferenceOutputAssembly="false"/></ItemGroup>
<Import Project="{root / 'spikes/CommandRoot/build/LlmCmd.Precompile.targets'}"/>
</Project>''')
    (project / 'Program.cs').write_text('internal static class Program { public static void Main() { } }')

    def source(*, extra=False, manual=False, camel=False, count=7, converter=False, profile=None):
        context = ''
        if manual:
            context = ('[JsonSourceGenerationOptions(PropertyNamingPolicy=JsonKnownNamingPolicy.CamelCase)]' if camel else '') + '[JsonSerializable(typeof(Input))][JsonSerializable(typeof(Result))]' + ('[JsonSerializable(typeof(Other))]' if extra else '') + 'internal partial class Context:JsonSerializerContext;'
        return f'''using CommandRoot;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace Proof;
public sealed record Input(string Document);
{'[JsonConverter(typeof(OpaqueConverter))]' if converter else ''}
public sealed record Result(string? Name,int Count={count});
{context}
{'[PromptCommand("proof.extract",JsonContext=typeof(Context))]' if manual else ''}
public sealed partial class Extract:PromptCommand<Input,Result> {{ const string Id="proof.extract"; const string UserTemplate="Read {{Document}}"; {('const string Profile="'+profile+'";') if profile else ''} }}
{('public sealed record Other(string Value); public sealed partial class Extra:PromptCommand<Input,Other> { const string Id="proof.other"; const string UserTemplate="Read {Document}"; }') if extra else ''}
{('public sealed class OpaqueConverter:JsonConverter<Result> { public override Result? Read(ref Utf8JsonReader reader,Type type,JsonSerializerOptions options)=>throw new NotSupportedException(); public override void Write(Utf8JsonWriter writer,Result value,JsonSerializerOptions options)=>throw new NotSupportedException(); }') if converter else ''}
'''

    def build(label, content):
        (project / 'Command.cs').write_text(content)
        result = subprocess.run([dotnet, 'build', str(project / 'ContractProof.csproj'), '--nologo', '-v:q'], capture_output=True, text=True)
        log = result.stdout + result.stderr
        (out / (label + '-build.log')).write_text(log)
        assert result.returncode == 0, label + ': build failed; see ' + str(out / (label + '-build.log'))
        files = list((project / 'obj/generated').rglob('Proof_Extract.g.cs'))
        assert len(files) == 1, 'Expected one generated Extract descriptor'
        generated = files[0].read_text()
        digest = re.search(r'"([a-f0-9]{64})"', generated)
        assert digest, 'Definition hash missing'
        return digest.group(1)

    def identity_default():
        original = build('default-before', source())
        added = build('default-after', source(extra=True))
        assert original == added, 'Unrelated command changed the first hash'

    def identity_explicit():
        original = build('explicit-before', source(manual=True))
        added = build('explicit-after', source(manual=True, extra=True))
        assert original == added, 'Unrelated manual-context root changed the first hash'
        policy = build('explicit-policy', source(manual=True, camel=True))
        assert original != policy, 'Serializer naming-policy change did not change the hash'
        default = build('explicit-default', source(manual=True, count=8))
        assert original != default, 'Constructor default change did not change the hash'

    check('default_context_unrelated_command_preserves_definition_hash', identity_default)
    check('explicit_context_hash_tracks_policy_and_defaults_not_root_list', identity_explicit)

    (project / 'Program.cs').write_text('''using CommandRoot;
using HumanGrading;
using LlmCmd.Generated;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Proof;
internal static class Program
{
    public static async Task Main()
    {
        if(System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault) throw new Exception("Reflection must be disabled");
        var errors=ContractProofCommands.ValidateContracts();
        if(errors.Count!=1 || errors[0].CommandId!="proof.extract" || errors[0].Code!="json_contract_invalid") throw new Exception("Invalid generated contract was not diagnosed at startup");
        try { new ServiceCollection().AddLlmCommands<ContractProofCommands>(); throw new Exception("Registration accepted invalid contract"); }
        catch(InvalidOperationException e) when(e.Message.Contains("proof.extract")) { }
        var client=new NeverClient();
        using var runtime=CommandRuntime.Create(()=>client,new Dictionary<string,Profile> { ["only"]=new() },new HumanLedger(),()=>new Actor("test","worker"));
        var result=await new Extract(runtime).WithParameters(new("doc")).ExecuteAsync();
        if(result is not Outcome<Result>.Failed { Failure:ExecutionFailure.ConfigurationFailure { Reason:"JSON contract invalid" } } || client.Calls!=0) throw new Exception("Contract failure escaped its boundary or called provider");
    }
}
internal sealed class NeverClient:IChatClient
{
    public int Calls;
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,ChatOptions? options=null,CancellationToken ct=default) { Calls++; throw new Exception("Provider must not run"); }
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,ChatOptions? options=null,CancellationToken ct=default)=>throw new NotSupportedException();
    public object? GetService(Type type,object? key=null)=>null;
    public void Dispose() { }
}
''')

    def contract_startup():
        build('invalid-contract', source(converter=True))
        result = subprocess.run([dotnet, 'run', '--no-build', '--project', str(project / 'ContractProof.csproj')], capture_output=True, text=True)
        (out / 'invalid-contract-run.log').write_text(result.stdout + result.stderr)
        assert result.returncode == 0, 'Invalid contract startup/runtime check failed; see invalid-contract-run.log'

    check('generated_registration_rejects_invalid_contract_before_provider_work', contract_startup)

    def profile_startup(missing):
        driver = '''using CommandRoot;
using HumanGrading;
using LlmCmd.Generated;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Proof;
internal static class Program
{
    public static void Main()
    {
        var client=new NeverClient();
        using var runtime=CommandRuntime.Create(()=>client,new Dictionary<string,Profile> { ["only"]=new() },new HumanLedger(),()=>new Actor("test","worker"));
        BODY
        if(client.Calls!=0) throw new Exception("Startup made a provider request");
    }
}
internal sealed class NeverClient:IChatClient
{
    public int Calls;
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,ChatOptions? options=null,CancellationToken ct=default) { Calls++; throw new Exception("Provider must not run"); }
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,ChatOptions? options=null,CancellationToken ct=default)=>throw new NotSupportedException();
    public object? GetService(Type type,object? key=null)=>null;
    public void Dispose() { }
}
'''
        body = ('try { new ServiceCollection().AddLlmCommandsFromContractProof(runtime); throw new Exception("Missing profile was accepted"); } catch(InvalidOperationException e) when(e.Message.Contains("proof.extract")) { }' if missing else
                'var services=new ServiceCollection().AddLlmCommandsFromContractProof(runtime); if(!services.Any(s=>s.ServiceType==typeof(Extract)) || !services.Any(s=>ReferenceEquals(s.ImplementationInstance,runtime))) throw new Exception("Generated registration missing");')
        (project / 'Program.cs').write_text(driver.replace('BODY', body))
        label = 'missing-profile' if missing else 'valid-registration'
        build(label, source(profile='not.configured' if missing else None))
        result = subprocess.run([dotnet, 'run', '--no-build', '--project', str(project / 'ContractProof.csproj')], capture_output=True, text=True)
        (out / (label + '-run.log')).write_text(result.stdout + result.stderr)
        assert result.returncode == 0, label + ' failed; inspect its run log'

    check('generated_registration_includes_runtime_and_commands', lambda: profile_startup(False))
    check('generated_registration_rejects_unknown_default_profile_at_setup', lambda: profile_startup(True))

suite.set('tests', str(len(suite)))
suite.set('failures', str(failed))
ET.ElementTree(suite).write(out / 'tests.xml', encoding='utf-8', xml_declaration=True)
raise SystemExit(bool(failed))
