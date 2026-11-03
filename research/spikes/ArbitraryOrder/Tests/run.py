#!/usr/bin/env python3
"""Compile against a separate consumer assembly; execute valid chains too."""
import pathlib
import os
import subprocess
import tempfile
import xml.etree.ElementTree as ET

root = pathlib.Path(__file__).resolve().parents[1]
dotnet = os.environ.get('DOTNET_HOST_PATH', 'dotnet')
out = pathlib.Path('/tmp/opencode/llmcmd-research-order')
out.mkdir(parents=True, exist_ok=True)
cases = [
    ('name_first', 'Command.Start().WithName("Ada").WithCount(3).Build()', None),
    ('count_first', 'Command.Start().WithCount(3).WithName("Ada").Build()', None),
    ('atomic', 'Command.Bind("Ada", 3)', None),
    ('missing_count', 'Command.Start().WithName("Ada").Build()', 'CS1929'),
    ('missing_name', 'Command.Start().WithCount(3).Build()', 'CS1929'),
    ('empty_build', 'Command.Start().Build()', 'CS1929'),
    ('duplicate_name', 'Command.Start().WithName("Ada").WithName("Other").WithCount(3).Build()', 'CS1929'),
    ('duplicate_count', 'Command.Start().WithCount(3).WithCount(4).WithName("Ada").Build()', 'CS1929'),
    ('wrong_type', 'Command.Start().WithName("Ada").WithCount("three").Build()', 'CS1503'),
    ('no_public_constructor', 'new Draft<Present, Present>("Ada", 3).Build()', 'CS1729'),
]
suite = ET.Element('testsuite', name='ArbitraryOrder', tests=str(len(cases)))
failures = 0
with tempfile.TemporaryDirectory() as work:
    work = pathlib.Path(work)
    project = work / 'Consumer.csproj'
    project.write_text(f'''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net11.0</TargetFramework><LangVersion>15</LangVersion><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup><ItemGroup><ProjectReference Include="{root / 'ArbitraryOrder.csproj'}" /></ItemGroup></Project>''')
    for name, expression, expected in cases:
        (work / 'Program.cs').write_text(f'''using ArbitraryOrder;
var result = {expression};
return result.Name == "Ada" && result.Count == 3 ? 0 : 1;
''')
        built = subprocess.run([dotnet, 'build', str(project), '--nologo', '-v:q', '-t:Rebuild'], capture_output=True, text=True)
        log = built.stdout + built.stderr
        ok = built.returncode == 0 if expected is None else built.returncode != 0 and expected in log
        if ok and expected is None:
            ran = subprocess.run([dotnet, 'run', '--no-build', '--project', str(project)], capture_output=True, text=True)
            log += ran.stdout + ran.stderr
            ok = ran.returncode == 0
        (out / f'{name}.log').write_text(log)
        case = ET.SubElement(suite, 'testcase', name=name)
        if not ok:
            failures += 1
            ET.SubElement(case, 'failure', message=f'Expected {expected or "compile and execute"}').text = log
suite.set('failures', str(failures))
ET.ElementTree(suite).write(out / 'tests.xml', encoding='utf-8', xml_declaration=True)
print(f'{len(cases) - failures}/{len(cases)} cases passed')
raise SystemExit(bool(failures))
