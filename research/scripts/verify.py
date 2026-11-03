#!/usr/bin/env python3
"""Run TUnit contracts and compiler experiments with the pinned SDK."""
import argparse
import json
import os
import pathlib
import subprocess
import sys
import xml.etree.ElementTree as ET

parser = argparse.ArgumentParser()
parser.add_argument('--dotnet', default=os.environ.get('DOTNET_HOST_PATH', 'dotnet'))
parser.add_argument('--with-observability', action='store_true')
parser.add_argument('--with-propagation', action='store_true')
parser.add_argument('--docker-config')
args = parser.parse_args()
root = pathlib.Path(__file__).resolve().parents[1]
output = pathlib.Path('/tmp/opencode/llmcmd-research-verification')
output.mkdir(parents=True, exist_ok=True)
env = dict(os.environ, DOTNET_HOST_PATH=args.dotnet, TUNIT_ENABLE_JUNIT_REPORTER='1')
commands = [
    ('typed-build', [args.dotnet, 'build', 'spikes/TypedCommands/Sample/Sample.csproj', '--nologo']),
    ('typed-sample', [args.dotnet, 'run', '--no-build', '--project', 'spikes/TypedCommands/Sample/Sample.csproj']),
    ('typed-compile', [sys.executable, 'spikes/TypedCommands/Tests/run.py']),
    ('order-compile', [sys.executable, 'spikes/ArbitraryOrder/Tests/run.py']),
]
reports = [('TypedCommands compiler', '/tmp/opencode/llmcmd-research-typed/tests.xml'),
           ('ArbitraryOrder compiler', '/tmp/opencode/llmcmd-research-order/tests.xml')]
projects = [
    ('core', 'LlmCmd.Tests', '/tmp/opencode/llmcmd-research-tunit.xml'),
    ('collector-adapters', 'LlmCmd.CollectorAdapters.Tests', '/tmp/opencode/llmcmd-research-adapters/tests.xml'),
    ('human-grading', 'LlmCmd.HumanGrading.Tests', '/tmp/opencode/llmcmd-research-human/tests.xml'),
    ('command-root', 'LlmCmd.CommandRoot.Tests', '/tmp/opencode/llmcmd-research-commandroot/tests.xml'),
    ('provider-adapters', 'LlmCmd.ProviderAdapters.Tests', '/tmp/opencode/llmcmd-research-providers/tests.xml'),
    ('provider-sdk-offline', 'LlmCmd.LiveProviders.Tests', '/tmp/opencode/llmcmd-research-live-providers/offline.xml'),
    ('context-propagation', 'LlmCmd.ContextPropagation.Tests', '/tmp/opencode/llmcmd-research-propagation/tests.xml'),
    ('composition', 'LlmCmd.Composition.Tests', '/tmp/opencode/llmcmd-research-composition/tests.xml'),
]
for label, project, report in projects:
    path = f'tests/{project}/{project}.csproj'
    commands.extend([(label + '-build', [args.dotnet, 'build', path, '--nologo']),
                     (label + '-tests', [args.dotnet, 'run', '--no-build', '--project', path, '--', '--junit-output-path', report])])
    reports.append((label + ' TUnit', report))
commands.append(('command-root-compile', [sys.executable, 'tests/LlmCmd.CommandRoot.Tests/compile-negative.py']))
reports.append(('CommandRoot compiler', '/tmp/opencode/llmcmd-research-commandroot/compile.xml'))
commands.append(('command-generation-proof', [sys.executable, 'tests/LlmCmd.CommandRoot.Tests/generation-proof.py']))
reports.append(('Command generation', '/tmp/opencode/llmcmd-research-commandroot-generation/tests.xml'))
commands.append(('precompile-proof', [sys.executable, 'tests/PrecompileProof/run.py']))
reports.append(('Serializer precompile', '/tmp/opencode/llmcmd-research-precompile/tests.xml'))
commands.extend([
    ('composition-compile', [sys.executable, 'tests/LlmCmd.Composition.Compile/run.py']),
    ('composition-real-compile', [sys.executable, 'tests/LlmCmd.Composition.Compile/real_compile.py']),
    ('composition-precompile', [sys.executable, 'tests/LlmCmd.Composition.Compile/precompile_proof.py']),
])
reports.extend([
    ('Composition compiler', '/tmp/opencode/llmcmd-research-composition-compile/tests.xml'),
    ('Composition real-runtime compiler', '/tmp/opencode/llmcmd-research-composition-compile/real-tests.xml'),
    ('Composite serializer precompile', '/tmp/opencode/llmcmd-research-composition-compile/precompile-tests.xml'),
])
commands.extend([
    ('command-demo-build', [args.dotnet, 'build', 'spikes/CommandRoot.Demo/CommandRoot.Demo.csproj', '--nologo']),
    ('command-demo-run', [args.dotnet, 'run', '--no-build', '--project', 'spikes/CommandRoot.Demo/CommandRoot.Demo.csproj']),
    ('composition-demo-build', [args.dotnet, 'build', 'spikes/Composition.Demo/Composition.Demo.csproj', '--nologo']),
    ('composition-demo-run', [args.dotnet, 'run', '--no-build', '--project', 'spikes/Composition.Demo/Composition.Demo.csproj']),
    ('composition-demo-quick', [args.dotnet, 'run', '--no-build', '--project', 'spikes/Composition.Demo/Composition.Demo.csproj', '--', '--quick']),
])
if args.with_observability:
    command = [sys.executable, 'scripts/verify_observability.py', '--dotnet', args.dotnet]
    if args.docker_config:
        command.extend(['--docker-config', args.docker_config])
    commands.append(('aspire-collector', command))
    reports.append(('Aspire receiver TUnit', '/tmp/opencode/llmcmd-research-otel/tests.xml'))
if args.with_propagation:
    command = [sys.executable, 'scripts/verify_propagation.py', '--dotnet', args.dotnet]
    if args.docker_config:
        command.extend(['--docker-config', args.docker_config])
    commands.append(('aspire-propagation', command))
    reports.append(('RabbitMQ propagation TUnit', '/tmp/opencode/llmcmd-research-propagation/host-tests.xml'))

results = []
for name, command in commands:
    with (output / f'{name}.log').open('w') as log:
        run = subprocess.run(command, cwd=root, env=env, stdout=log, stderr=subprocess.STDOUT)
    results.append(dict(name=name, command=' '.join(command), exit=run.returncode, log=str(output / f'{name}.log')))
    print(f'{name}: exit {run.returncode}', flush=True)
    if run.returncode:
        break
(output / 'commands.json').write_text(json.dumps(results, indent=2))
tests = []
if len(results) == len(commands) and all(r['exit'] == 0 for r in results):
    for suite, path in reports:
        for case in ET.parse(path).iter('testcase'):
            failure = case.find('failure')
            error = case.find('error')
            problem = failure if failure is not None else error
            skipped = case.find('skipped') is not None
            tests.append(dict(name=f'{suite}: {case.attrib.get("name", "unnamed")}',
                              status='failed' if problem is not None else 'skipped' if skipped else 'passed',
                              message=(problem.text or problem.attrib.get('message', '')) if problem is not None else ''))
else:
    tests.append(dict(name='Spike verification', status='failed', message='A command failed; inspect commands.json and the referenced log.'))
(output / 'tests.json').write_text(json.dumps(tests, indent=2))
print(f'{sum(t["status"] == "passed" for t in tests)}/{len(tests)} checks passed', flush=True)
raise SystemExit(any(r['exit'] for r in results) or any(t['status'] == 'failed' for t in tests))
