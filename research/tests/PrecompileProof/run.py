#!/usr/bin/env python3
import hashlib, json, os, pathlib, subprocess, tempfile, xml.etree.ElementTree as ET
root=pathlib.Path(__file__).resolve().parents[2]
out=pathlib.Path('/tmp/opencode/llmcmd-research-precompile'); out.mkdir(parents=True,exist_ok=True)
dotnet=os.environ.get('DOTNET_HOST_PATH','dotnet')
suite=ET.Element('testsuite',name='PrecompileProof'); failures=0; count=0
def check(name,action):
    global failures,count
    count+=1; node=ET.SubElement(suite,'testcase',name=name)
    try: action(); print(name,'PASS')
    except Exception as e: failures+=1; ET.SubElement(node,'failure',message=str(e)).text=str(e); print(name,'FAIL',str(e))
def run(project,*args):
    p=subprocess.run([dotnet,*args,str(project),'--nologo','-v:q'],text=True,capture_output=True)
    return p.returncode,p.stdout+p.stderr
def expect(ok,message):
    if not ok: raise AssertionError(message)
def alias(name): return 'Type_'+hashlib.sha256(('global::'+name).encode()).hexdigest()
with tempfile.TemporaryDirectory(prefix='LlmCmd proof space ',dir=out.resolve()) as d:
    path=pathlib.Path(d); proj=path/'Proof.csproj'
    proj.write_text(f'''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net11.0</TargetFramework><LangVersion>15</LangVersion><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault><DefineConstants>$(DefineConstants);LLMCMD_PRECOMP_TEST</DefineConstants></PropertyGroup><ItemGroup><ProjectReference Include="{root / 'spikes/CommandRoot/Runtime/Runtime.csproj'}" /></ItemGroup><Import Project="{root / 'spikes/CommandRoot/build/LlmCmd.Precompile.targets'}" /></Project>''')
    (path/'One.cs').write_text('namespace One; public sealed record Detail(string Value); public sealed record Input(string Name, Detail Nested); public sealed record Output(int Count); public sealed class Command : global::CommandRoot.PromptCommand<Input,Output> { public Command(global::CommandRoot.CommandRuntime r) : base(r,null!) {} }')
    (path/'Two.cs').write_text('namespace Two; public sealed record Input(int Value); public sealed record Output(string Text); public sealed class Command : global::CommandRoot.PromptCommand<Input,Output> { public Command(global::CommandRoot.CommandRuntime r) : base(r,null!) {} }')
    (path/'Conditional.cs').write_text('#if LLMCMD_PRECOMP_TEST\nnamespace Three; public sealed record Input(bool Enabled); public sealed class Command : global::CommandRoot.PromptCommand<Input,One.Output> { public Command(global::CommandRoot.CommandRuntime r) : base(r,null!) {} }\n#endif')
    (path/'Abstract.cs').write_text('namespace One; public abstract class Base<T> : global::CommandRoot.PromptCommand<T,Output> { protected Base(global::CommandRoot.CommandRuntime r) : base(r,null!) {} }')
    def program(two):
        b='''using System; using System.Text.Json; using LlmCmd.Generated; public static class Program { public static void Main() { if (JsonSerializer.IsReflectionEnabledByDefault) throw new Exception("reflection enabled");'''
        b+=f' var a=new One.Input("hello",new One.Detail("nested")); var ja=JsonSerializer.Serialize(a,LlmCmdJsonContext.Default.{alias("One.Input")}); var da=JsonSerializer.Deserialize(ja,LlmCmdJsonContext.Default.{alias("One.Input")}); if(da?.Name!="hello" || da.Nested.Value!="nested") throw new Exception("one mismatch");'
        if two: b+=f' var b=new Two.Input(42); var jb=JsonSerializer.Serialize(b,LlmCmdJsonContext.Default.{alias("Two.Input")}); var db=JsonSerializer.Deserialize(jb,LlmCmdJsonContext.Default.{alias("Two.Input")}); if(db?.Value!=42) throw new Exception("two mismatch");'
        b+=f' try {{ JsonSerializer.Deserialize("""{{"Name":null,"Nested":{{"Value":"x"}}}}""",LlmCmdJsonContext.Default.{alias("One.Input")}); throw new Exception("nullable validation missing"); }} catch (JsonException) {{ }}'
        return b+' Console.WriteLine("metadata roundtrip passed"); } }'
    (path/'Program.cs').write_text(program(True))
    def design_time(expected):
        generated=path/'obj/Debug/net11.0/llmcmd-precompile/LlmCmdJsonContext.g.cs'
        before=generated.stat().st_mtime_ns if generated.exists() else None
        p=subprocess.run([dotnet,'msbuild',str(proj),'-nologo','-t:LlmCmdIncludeExistingSerializerContext','-p:DesignTimeBuild=true','-getItem:Compile'],text=True,capture_output=True)
        expect(p.returncode==0,p.stdout+p.stderr)
        items=json.loads(p.stdout)['Items']['Compile']
        matches=[item for item in items if pathlib.Path(item['FullPath'])==generated]
        expect(len(matches)==int(expected),f'expected {int(expected)} generated item, got {len(matches)}')
        expect((generated.stat().st_mtime_ns if generated.exists() else None)==before,'design-time target changed generated output')
    check('design_time_without_existing_output_does_not_generate',lambda: design_time(False))
    def build(name):
        code,log=run(proj,'build'); (out/(name+'.log')).write_text(log); expect(code==0,log[-4500:])
    def fresh():
        build('fresh'); code,log=run(proj,'run','--no-build','--project'); (out/'run.log').write_text(log); expect(code==0 and 'metadata roundtrip passed' in log,log)
        generated=path/'obj/Debug/net11.0/llmcmd-precompile/LlmCmdJsonContext.g.cs'; data=generated.read_text();
        for name in ['One.Input','One.Output','Two.Input','Two.Output','Three.Input']: expect(alias(name) in data,'missing alias '+name)
        expect(len(set(alias(n) for n in ['One.Input','One.Output','Two.Input','Two.Output']))==4,'alias collision')
    check('fresh_build_reflection_disabled_and_same_names_in_space_path',fresh)
    check('design_time_includes_existing_output_once_without_rewrite',lambda: design_time(True))
    def stable():
        generated=path/'obj/Debug/net11.0/llmcmd-precompile/LlmCmdJsonContext.g.cs'; before=generated.stat().st_mtime_ns; build('incremental'); expect(generated.stat().st_mtime_ns==before,'unchanged output was rewritten')
    check('unchanged_context_not_rewritten',stable)
    def removal():
        (path/'Two.cs').unlink(); (path/'Program.cs').write_text(program(False)); build('removed'); data=(path/'obj/Debug/net11.0/llmcmd-precompile/LlmCmdJsonContext.g.cs').read_text(); expect(alias('Two.Input') not in data and alias('One.Input') in data,'removed types persisted')
        code,log=run(proj,'run','--no-build','--project'); expect(code==0,log)
    check('source_removal_updates_context',removal)
    def clean():
        code,log=run(proj,'clean'); (out/'clean.log').write_text(log); expect(code==0,log); expect(not (path/'obj/Debug/net11.0/llmcmd-precompile/LlmCmdJsonContext.g.cs').exists(),'clean retained generated context'); build('clean-rebuild')
        code,log=run(proj,'run','--no-build','--project'); expect(code==0,log)
    check('clean_and_rebuild',clean)
    def invalid():
        (path/'Broken.cs').write_text('namespace Bad; public sealed class Broken : global::CommandRoot.PromptCommand<MissingType,One.Output> { public Broken(global::CommandRoot.CommandRuntime r) : base(r,null!) {} }')
        code,log=run(proj,'build'); (out/'unresolved.log').write_text(log); expect(code!=0 and 'LLMCMDPRE001' in log,'expected explicit unresolved-type diagnostic: '+log)
    check('unresolved_type_fails_clearly',invalid)
suite.set('tests',str(count)); suite.set('failures',str(failures)); ET.ElementTree(suite).write(out/'tests.xml',encoding='utf-8',xml_declaration=True)
raise SystemExit(bool(failures))
