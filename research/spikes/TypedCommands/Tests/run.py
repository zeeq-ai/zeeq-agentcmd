#!/usr/bin/env python3
import os, pathlib, subprocess, tempfile, xml.etree.ElementTree as ET
root = pathlib.Path(__file__).resolve().parents[1]
dotnet = os.environ.get('DOTNET_HOST_PATH', 'dotnet')
out = pathlib.Path('/tmp/opencode/llmcmd-research-typed'); out.mkdir(parents=True, exist_ok=True)
cases = [
 ('atomic', '"Hi {name} {count} {{ok}}"', 'C.Bind("A", 3).WithModel("m")', None),
 ('chain', '"Hi {name} {count}"', 'C.Start().WithName("A").WithCount(3).Build()', None),
 ('missing_atomic', '"Hi {name} {count}"', 'C.Bind("A")', 'CS7036'),
 ('wrong_type', '"Hi {name} {count}"', 'C.Bind("A", "three")', 'CS1503'),
 ('early_build', '"Hi {name} {count}"', 'C.Start().WithName("A").Build()', 'CS1061'),
 ('out_of_order', '"Hi {name} {count}"', 'C.Start().WithCount(3)', 'CS1061'),
 ('repeated', '"Hi {name} {name} {count}"', 'C.Bind("A", 3)', None),
 ('unresolved', '"Hi {name} {count} {other}"', 'C.Bind("A", 3)', 'PROMPT001'),
 ('unclosed', '"Hi {name} {count"', 'C.Bind("A", 3)', 'PROMPT001'),
 ('unused', '"Hi {name}"', 'C.Bind("A", 3)', 'PROMPT001'),
]
extra = [
 ('default_id', '[Prompt("Hi {name} {count}")]', 'static partial void Define(string name, int count);', None, 'Test.C'),
 ('explicit_id', '[Prompt("stable", "Hi {name} {count}")]', 'static partial void Define(string name, int count);', None, 'stable'),
 ('multiple_define', '[Prompt("Hi {name} {count}")]', 'static partial void Define(string name, int count); static partial void Define(string name);', 'PROMPT001', None),
 ('multiple_attributes', '[Prompt("Hi {name} {count}")][Prompt("Hi {name} {count}")]', 'static partial void Define(string name, int count);', 'PROMPT001', None),
 ('null_template', '[Prompt(null)]', 'static partial void Define(string name, int count);', 'PROMPT001', None),
 ('null_id', '[Prompt(null, "Hi {name} {count}")]', 'static partial void Define(string name, int count);', 'PROMPT001', None),
 ('bad_grammar', '[Prompt("Hi {name} {count} }")]', 'static partial void Define(string name, int count);', 'PROMPT001', None),
]
compile_cases = [
 ('nullable_optional', 'namespace Test; [Prompt("id", "{name} {count} {tone}")] public static partial class C { static partial void Define(string? name, int count = 3, string? tone = null); } public class Use { object a = C.Bind(null); object b = C.Start().WithName(null).Build().WithCount(5).WithTone(null); }', None),
 ('optional_required_missing', 'namespace Test; [Prompt("id", "{name} {count}")] public static partial class C { static partial void Define(string? name, int count = 3); } public class Use { object a = C.Bind(); object b = C.Start().Build(); }', 'CS7036'),
 ('nonnull_rejects_null', 'namespace Test; [Prompt("id", "{name}")] public static partial class C { static partial void Define(string name); } public class Use { object a = C.Bind(null); }', 'CS8625'),
 ('attribute_optional_without_default', 'namespace Test; [Prompt("id", "{name}")] public static partial class C { static partial void Define([System.Runtime.InteropServices.Optional] string name); }', 'PROMPT001'),
 ('float_special_default', 'namespace Test; [Prompt("id", "{name} {ratio}")] public static partial class C { static partial void Define(string name, float ratio = float.NaN); } public class Use { object a = C.Bind("x"); }', None),
 ('enum_and_literal_defaults', 'namespace Test; public enum Choice { One = 1 } [Prompt("id", "{name} {flag} {choice} {ratio}")] public static partial class C { static partial void Define(string name, bool flag = true, Choice choice = Choice.One, float ratio = 1.5f); } public class Use { object a = C.Bind("x"); }', None),
]
suite = ET.Element('testsuite', name='TypedCommands', tests=str(len(cases) + len(extra) + len(compile_cases)))
failed = 0
with tempfile.TemporaryDirectory() as d:
    d = pathlib.Path(d)
    (d/'Test.csproj').write_text(f'''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net11.0</TargetFramework><LangVersion>15</LangVersion><Nullable>enable</Nullable><WarningsAsErrors>CS8625;CS8604</WarningsAsErrors><EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles><CompilerGeneratedFilesOutputPath>$(BaseIntermediateOutputPath)generated</CompilerGeneratedFilesOutputPath></PropertyGroup><ItemGroup><ProjectReference Include="{root / 'Generator/Generator.csproj'}" OutputItemType="Analyzer" ReferenceOutputAssembly="false" /></ItemGroup></Project>''')
    for name, template, call, expected in cases:
        (d/'Test.cs').write_text(f'namespace Test; [Prompt("id", {template})] public static partial class C {{ static partial void Define(string name, int count); }} public class Use {{ object x = {call}; }}')
        p = subprocess.run([dotnet,'build',str(d/'Test.csproj'),'--nologo','-v:q','-t:Rebuild'],capture_output=True,text=True)
        log = p.stdout + p.stderr; (out/f'{name}.log').write_text(log)
        ok = (p.returncode == 0) if expected is None else (p.returncode != 0 and expected in log)
        case = ET.SubElement(suite,'testcase',name=name)
        if not ok:
            failed += 1; ET.SubElement(case,'failure',message=f'Expected {expected or "success"}').text = log
        print(f'{name}: {"PASS" if ok else "FAIL"}')
    for name, attribute, definition, expected, expected_id in extra:
        (d/'Test.cs').write_text(f'namespace Test; {attribute} public static partial class C {{ {definition} }}')
        p = subprocess.run([dotnet,'build',str(d/'Test.csproj'),'--nologo','-v:q','-t:Rebuild'],capture_output=True,text=True)
        log = p.stdout + p.stderr; (out/f'{name}.log').write_text(log)
        generated = list((d/'obj/generated').rglob('Test_C.g.cs'))
        source = generated[0].read_text() if generated else ''
        ok = (p.returncode == 0 and f'Id = "{expected_id}";' in source) if expected is None else (p.returncode != 0 and expected in log and 'CS8785' not in log)
        case = ET.SubElement(suite,'testcase',name=name)
        if not ok:
            failed += 1; ET.SubElement(case,'failure',message=f'Expected {expected or expected_id}').text = log + source
        print(f'{name}: {"PASS" if ok else "FAIL"}')
    for name, source, expected in compile_cases:
        (d/'Test.cs').write_text(source)
        p = subprocess.run([dotnet,'build',str(d/'Test.csproj'),'--nologo','-v:q','-t:Rebuild'],capture_output=True,text=True)
        log = p.stdout + p.stderr; (out/f'{name}.log').write_text(log)
        ok = (p.returncode == 0) if expected is None else (p.returncode != 0 and expected in log and 'CS8785' not in log)
        case = ET.SubElement(suite,'testcase',name=name)
        if not ok:
            failed += 1; ET.SubElement(case,'failure',message=f'Expected {expected or "success"}').text = log
        print(f'{name}: {"PASS" if ok else "FAIL"}')
suite.set('failures',str(failed)); ET.ElementTree(suite).write(out/'tests.xml',encoding='utf-8',xml_declaration=True)
raise SystemExit(bool(failed))
