#!/usr/bin/env python3
"""Compile generated calls against a copied real runtime; no shared bin/obj writes."""
import os, pathlib, shutil, subprocess, tempfile, xml.etree.ElementTree as ET
root=pathlib.Path(__file__).resolve().parents[2]
out=pathlib.Path('/tmp/opencode/llmcmd-research-composition-compile'); out.mkdir(parents=True,exist_ok=True)
dotnet=os.environ.get('DOTNET_HOST_PATH','dotnet')
suite=ET.Element('testsuite',name='RealCompositionCompile'); failures=0
header='''using CommandRoot; using CompositionDemo; namespace CompositionDemo; '''
cases=[
 ('valid_demo_and_same_type_slots', '''[CompositeOutput("demo.twins")] public sealed partial record Twins(Header Left,Header Right); class Probe { void Check(CompositionRoot<Twins> root,Invocation<SourceText,Header> call) { root.WithLeftFrom(call).WithRightFrom(call); } }''', True),
 ('real_wrong_result', '''class Probe { void Check(CompositionRoot<DocumentExtraction> root,Invocation<SourceText,Parties> call) { root.WithHeaderFrom(call); } }''', False),
 ('real_wrong_parameters', '''class Probe { void Check(CompositionRoot<DocumentExtraction> root,ExtractHeader cmd) { root.WithHeaderFrom(cmd, "bad"); } }''', False),
 ('real_wrong_then_result', '''class Probe { void Check(CompositionRoot<DocumentExtraction> root,ExtractParties cmd) { root.WithHeaderFrom(root.Steps.LineItems.Then(p=>cmd.WithParameters(new("x")))); } }''', False),
 ('real_derived_to_base', '''public record Animal(string Name); public sealed record Dog(string Name):Animal(Name); [CompositeOutput("demo.animal")] public sealed partial record Whole(Animal Pet); class Probe { void Check(CompositionRoot<Whole> root,Invocation<SourceText,Dog> call) { root.WithPetFrom(call); } }''', False),
 ('real_base_to_derived', '''public record Animal(string Name); public sealed record Dog(string Name):Animal(Name); [CompositeOutput("demo.dog")] public sealed partial record Whole(Dog Pet); class Probe { void Check(CompositionRoot<Whole> root,Invocation<SourceText,Animal> call) { root.WithPetFrom(call); } }''', False)]
with tempfile.TemporaryDirectory(prefix='actual runtime ',dir=out.resolve()) as tmp:
 p=pathlib.Path(tmp).resolve(); (p/'spikes').mkdir(); shutil.copy2(root/'global.json',p/'global.json')
 for name in ['HumanGrading','ProfileRouting','ContextPropagation','CommandRoot','CommandRoot.Generator','CommandRoot.Precompile','Composition.Demo']:
  shutil.copytree(root/'spikes'/name,p/'spikes'/name,ignore=shutil.ignore_patterns('bin','obj'))
 project=p/'spikes/Composition.Demo/Composition.Demo.csproj'
 for name,source,should_pass in cases:
  node=ET.SubElement(suite,'testcase',name=name); (project.parent/'CompileProbe.cs').write_text(header+source)
  proc=subprocess.run([dotnet,'build',str(project),'-v:q','--nologo'],capture_output=True,text=True); log=proc.stdout+proc.stderr
  (out/(name+'.log')).write_text(log)
  ok=proc.returncode==0 if should_pass else proc.returncode!=0 and 'error CS' in log and 'CompileProbe.cs' in log
  if not ok: failures+=1; ET.SubElement(node,'failure',message='Unexpected compiler result').text=log
  print(name,'PASS' if ok else 'FAIL',flush=True)
suite.set('tests',str(len(cases))); suite.set('failures',str(failures)); ET.ElementTree(suite).write(out/'real-tests.xml',encoding='utf-8',xml_declaration=True)
raise SystemExit(bool(failures))
