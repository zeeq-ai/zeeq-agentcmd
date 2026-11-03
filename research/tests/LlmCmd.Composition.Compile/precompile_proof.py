#!/usr/bin/env python3
"""Isolated precompiler proof; it never builds the shared runtime project."""
import os, pathlib, subprocess, tempfile, xml.etree.ElementTree as ET
root=pathlib.Path(__file__).resolve().parents[2]
out=pathlib.Path('/tmp/opencode/llmcmd-research-composition-compile'); out.mkdir(parents=True,exist_ok=True)
dotnet=os.environ.get('DOTNET_HOST_PATH','dotnet')
suite=ET.Element('testsuite',name='CompositePrecompile'); failures=0
with tempfile.TemporaryDirectory(prefix='composite source ',dir=out) as tmp:
    p=pathlib.Path(tmp)
    (p/'Proof.csproj').write_text(f'''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net11.0</TargetFramework><OutputType>Exe</OutputType><LangVersion>15</LangVersion><Nullable>enable</Nullable><JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault></PropertyGroup><Import Project="{root / 'spikes/CommandRoot/build/LlmCmd.Precompile.targets'}" /></Project>''')
    (p/'Program.cs').write_text('''using System; using System.Text.Json; using LlmCmd.Generated;
namespace CommandRoot { [AttributeUsage(AttributeTargets.Class)] public sealed class CompositeOutputAttribute(string id):Attribute {} }
namespace App { [CommandRoot.CompositeOutput("document")] public sealed partial record Document(Part Header); public sealed record Part(string Name); }
public static class Program { public static void Main() { if(JsonSerializer.IsReflectionEnabledByDefault) throw new Exception("Reflection enabled"); var ti=LlmCmdJsonContext.Default.GetTypeInfo(typeof(App.Document))!; var json=JsonSerializer.Serialize(new App.Document(new("ok")),ti); var value=(App.Document)JsonSerializer.Deserialize(json,ti)!; if(value.Header.Name!="ok") throw new Exception("Incorrect roundtrip"); Console.WriteLine("composite context passed"); } }
''')
    node=ET.SubElement(suite,'testcase',name='aggregate_root_without_leaf_commands_reflection_disabled')
    proc=subprocess.run([dotnet,'run','--project',str(p/'Proof.csproj'),'--nologo'],capture_output=True,text=True)
    (out/'precompile.log').write_text(proc.stdout+proc.stderr)
    if proc.returncode or 'composite context passed' not in proc.stdout:
        failures+=1; ET.SubElement(node,'failure',message='Composite precompile proof failed').text=proc.stdout+proc.stderr
suite.set('tests','1'); suite.set('failures',str(failures)); ET.ElementTree(suite).write(out/'precompile-tests.xml',encoding='utf-8',xml_declaration=True)
raise SystemExit(bool(failures))
