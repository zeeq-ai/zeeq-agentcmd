#!/usr/bin/env python3
"""Compile the generated surface against a narrow runtime contract in an isolated project."""
import os, pathlib, re, shutil, subprocess, tempfile, xml.etree.ElementTree as ET
root=pathlib.Path(__file__).resolve().parents[2]
out=pathlib.Path('/tmp/opencode/llmcmd-research-composition-compile'); out.mkdir(parents=True,exist_ok=True)
dotnet=os.environ.get('DOTNET_HOST_PATH','dotnet')
analyzer=out/'generator-proof/bin/Debug/netstandard2.0/Generator.dll'
suite=ET.Element('testsuite',name='CompositionCompile'); failed=0
generator=out/'generator-proof'; generator.mkdir(exist_ok=True)
for pattern in ('*.cs','*.md','*.csproj'):
 for source in (root/'spikes/CommandRoot.Generator').glob(pattern): shutil.copy2(source,generator/source.name)
build=subprocess.run([dotnet,'build',str(generator/'Generator.csproj'),'-v:q','--nologo'],text=True,capture_output=True)
(out/'generator-build.log').write_text(build.stdout+build.stderr)
if build.returncode: raise SystemExit('Isolated generator build failed; see generator-build.log')
stub='''using System; using System.Collections.Generic; using System.Text.Json.Serialization; using System.Text.Json.Serialization.Metadata;
namespace Microsoft.Extensions.DependencyInjection { public interface IServiceCollection {} }
namespace CommandRoot {
[AttributeUsage(AttributeTargets.Class)] public sealed class CompositeOutputAttribute(string id):Attribute { public int Revision {get;init;}=1; public Type? JsonContext {get;init;} }
public class CommandRuntime {} public class PromptCommand<TP,TR> { public Invocation<TP,TR> WithParameters(TP p)=>new(); } public sealed class Invocation<TP,TR> {}
public sealed class CommandJsonContract<TP,TR> { readonly Func<JsonSerializerContext> c; public CommandJsonContract(Func<JsonSerializerContext> c,Action<JsonTypeInfo> configure,params Type[] t) { this.c=c; } public JsonTypeInfo<TR> Result => (JsonTypeInfo<TR>)c().GetTypeInfo(typeof(TR))!; public void Validate() {} }
public sealed record SlotDescriptor(string Id,string Name,Type Type,bool Required);
public sealed class PartSlot<T,TPart> { public SlotDescriptor Descriptor=>null!; public static PartSlot<T,TPart> Declare(string id,string name,bool required)=>new(); }
public interface ICompositeDescriptor {} public sealed record CompositeDescriptor<T>(string Id,int Revision,string Hash,JsonTypeInfo<T> Whole,IReadOnlyList<SlotDescriptor> Slots,Func<T,string?>? Validate):ICompositeDescriptor;
public sealed class Pinned<T> {} public sealed class PartSource<T> {}
public sealed class StepRef<T> { public PartSource<TTarget> Then<TTarget,TP>(Func<Pinned<T>,Invocation<TP,TTarget>> f)=>new(); }
public sealed class StepCatalog<T> { public StepRef<P> Of<P>(PartSlot<T,P> slot)=>new(); }
public sealed class CompositionRoot<T> { public StepCatalog<T> Steps=>new(); public CompositionRoot<T> WithPartFrom<P,V>(PartSlot<T,V> slot,Invocation<P,V> call,string m,string f,int l)=>this; public CompositionRoot<T> WithPartFrom<V>(PartSlot<T,V> slot,PartSource<V> source,string m,string f,int l)=>this; public CompositionRoot<T> ReusePart<V>(PartSlot<T,V> slot,Pinned<V> v)=>this; }
public sealed record CommandRegistration(string Id,int Revision,Type C,Type P,Type R,string Profile); public sealed record CommandContractError(string Id,string Error);
public interface IGeneratedCommandCatalog { static abstract void Register(Microsoft.Extensions.DependencyInjection.IServiceCollection services); }
public static class CompositionServiceCollectionExtensions { public static void AddCompositeDescriptor<T>(Microsoft.Extensions.DependencyInjection.IServiceCollection s,CompositeDescriptor<T> descriptor) {} }
public static class CommandServices { public static Microsoft.Extensions.DependencyInjection.IServiceCollection AddLlmCommands<T>(Microsoft.Extensions.DependencyInjection.IServiceCollection s,CommandRuntime r) where T:IGeneratedCommandCatalog=>s; }
}
'''
model='''namespace Example { using CommandRoot; [CompositeOutput("doc")] public sealed partial record Document(A Header,A Backup,B Tail); public class Base {} public sealed class A:Base {} public sealed class B:Base {} }'''
cases=[
('valid_named_and_same_type_slots',model,'''using CommandRoot; using Example; class Use { void M(CompositionRoot<Document> r,Invocation<int,A> a,Invocation<string,B> b) { r.WithHeaderFrom(a); r.WithBackupFrom(a); r.WithTailFrom(b); var x=r.Steps.Header.Then(p=>b); r.WithTailFrom(x); r.WithPartFrom(DocumentParts.Header,a,"member","file",1); } }''',True,''),
('explicit_serializer_context', '''namespace Example { using CommandRoot; using System.Text.Json.Serialization; [CompositeOutput("doc",JsonContext=typeof(Manual))] public sealed partial record Document(string Header); [JsonSerializable(typeof(Document))] public partial class Manual:JsonSerializerContext {} }''', '', True, ''),
('distinct_namespace_hint_names', '''namespace A_B { using CommandRoot; [CompositeOutput("one")] public sealed partial record C(string Header); } namespace A { using CommandRoot; [CompositeOutput("two")] public sealed partial record B_C(string Header); }''', '', True, ''),
('wrong_result_rejected',model,'''using CommandRoot; using Example; class Use { void M(CompositionRoot<Document> r,Invocation<int,B> b) { r.WithHeaderFrom(b); } }''',False,'CS'),
('wrong_parameters_rejected',model,'''using CommandRoot; using Example; class Use { void M(CompositionRoot<Document> r,PromptCommand<int,A> a) { r.WithHeaderFrom(a,"wrong"); } }''',False,'CS'),
('wrong_then_result_rejected',model,'''using CommandRoot; using Example; class Use { void M(CompositionRoot<Document> r,Invocation<int,B> b) { r.WithHeaderFrom(r.Steps.Header.Then(p=>b)); } }''',False,'CS'),
('base_to_derived_rejected',model,'''using CommandRoot; using Example; class Use { void M(CompositionRoot<Document> r,Invocation<int,Base> b) { r.WithHeaderFrom(b); } }''',False,'CS'),
('derived_to_base_rejected','''namespace Example { using CommandRoot; [CompositeOutput("doc")] public sealed partial record Document(Base Header); public class Base {} public sealed class A:Base {} }''','''using CommandRoot; using Example; class Use { void M(CompositionRoot<Document> r,Invocation<int,A> a) { r.WithHeaderFrom(a); } }''',False,'CS'),
('generated_name_collision_rejected','''namespace Example { using CommandRoot; [CompositeOutput("doc")] public sealed partial record Document(string Header); public class DocumentParts {} }''','',False,'LLMCMD001'),
('ambiguous_json_names_rejected','''namespace Example { using CommandRoot; using System.Text.Json.Serialization; [CompositeOutput("doc")] public sealed partial record Document([property:JsonPropertyName("x")] string One,[property:JsonPropertyName("x")] string Two); }''','',False,'LLMCMD001'),
('optional_nonnullable_rejected','''namespace Example { using CommandRoot; [CompositeOutput("doc")] public sealed partial record Document(string Header="x"); }''','',False,'LLMCMD001'),
('ignored_member_rejected','''namespace Example { using CommandRoot; [CompositeOutput("doc")] public sealed partial record Document([property:System.Text.Json.Serialization.JsonIgnore] string Header); }''','',False,'LLMCMD001'),
('duplicate_id_rejected','''namespace Example { using CommandRoot; [CompositeOutput("doc")] public sealed partial record Document(string Header); [CompositeOutput("doc")] public sealed partial record Other(string Header); }''','',False,'LLMCMD001')]
with tempfile.TemporaryDirectory(prefix='compile with spaces ',dir=out) as d:
 p=pathlib.Path(d); project=p/'Probe.csproj'; (p/'Stub.cs').write_text(stub)
 project.write_text(f'''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net11.0</TargetFramework><LangVersion>15</LangVersion><Nullable>enable</Nullable><EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles><CompilerGeneratedFilesOutputPath>obj/generated</CompilerGeneratedFilesOutputPath><JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault></PropertyGroup><ItemGroup><Analyzer Include="{analyzer}" /></ItemGroup><Import Project="{root/'spikes/CommandRoot/build/LlmCmd.Precompile.targets'}" /></Project>''')
 for name,definition,consumer,should_pass,diagnostic in cases:
  node=ET.SubElement(suite,'testcase',name=name); (p/'Model.cs').write_text(definition); (p/'Use.cs').write_text(consumer)
  proc=subprocess.run([dotnet,'build',str(project),'-v:q','--nologo'],text=True,capture_output=True); log=proc.stdout+proc.stderr; (out/(name+'.log')).write_text(log)
  success=proc.returncode==0 if should_pass else proc.returncode!=0 and diagnostic in log and (diagnostic != 'CS' or 'Use.cs' in log)
  if not success: failed+=1; ET.SubElement(node,'failure',message='Unexpected compiler result').text=log
  print(name,'PASS' if success else 'FAIL',flush=True)
 node=ET.SubElement(suite,'testcase',name='hash_ignores_unrelated_composite')
 try:
  hashes=[]
  for suffix in ('', '\nnamespace Other { using CommandRoot; [CompositeOutput("other")] public sealed partial record Unrelated(string Added); }'):
   (p/'Model.cs').write_text(model+suffix); (p/'Use.cs').write_text('')
   proc=subprocess.run([dotnet,'build',str(project),'-v:q','--nologo'],text=True,capture_output=True)
   if proc.returncode: raise Exception(proc.stdout+proc.stderr)
   files=list((p/'obj/generated').rglob('Composite_*.g.cs'))
   hits=[match.group(1) for f in files if (match:=re.search(r'Descriptor = new\("doc",1,"([0-9a-f]{64})"',f.read_text()))]
   if len(hits)!=1: raise Exception('Expected one generated document descriptor')
   hashes.append(hits[0])
  if hashes[0]!=hashes[1]: raise Exception('Unrelated composite changed definition hash')
  print('hash_ignores_unrelated_composite PASS',flush=True)
 except Exception as e:
  failed+=1; ET.SubElement(node,'failure',message='Hash independence failed').text=str(e); print('hash_ignores_unrelated_composite FAIL',flush=True)
suite.set('tests',str(len(cases)+1)); suite.set('failures',str(failed)); ET.ElementTree(suite).write(out/'tests.xml',encoding='utf-8',xml_declaration=True)
raise SystemExit(bool(failed))
