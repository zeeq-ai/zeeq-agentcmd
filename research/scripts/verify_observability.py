#!/usr/bin/env python3
"""Run only this workspace's collector stack; inspect via Aspire, test, stop."""
import argparse
import json
import os
import pathlib
import shutil
import subprocess

parser = argparse.ArgumentParser()
parser.add_argument('--dotnet', default=os.environ.get('DOTNET_HOST_PATH', 'dotnet'))
parser.add_argument('--docker-config')
args = parser.parse_args()
root = pathlib.Path(__file__).resolve().parents[1]
output = pathlib.Path('/tmp/opencode/llmcmd-research-otel')
output.mkdir(parents=True, exist_ok=True)
dotnet = pathlib.Path(shutil.which(args.dotnet) or args.dotnet).absolute()
env = dict(os.environ, DOTNET_HOST_PATH=str(dotnet), TUNIT_ENABLE_JUNIT_REPORTER='1')
if args.dotnet != 'dotnet':
    env['PATH'] = str(dotnet.parent) + os.pathsep + env.get('PATH', '')
    env['DOTNET_ROOT'] = str(dotnet.parent)
if args.docker_config:
    pathlib.Path(args.docker_config).mkdir(parents=True, exist_ok=True)
    env['DOCKER_CONFIG'] = args.docker_config
    env['ASPIRE_ENABLE_CONTAINER_TUNNEL'] = 'false'
apphost = str(root / 'spikes/Observability/apphost.cs')

def run(name, command):
    with (output / name).open('w') as log:
        result = subprocess.run(command, cwd=root, env=env, stdout=log, stderr=subprocess.STDOUT)
    if result.returncode:
        raise RuntimeError(f'{name}: exit {result.returncode}; inspect {output / name}')

started = False
try:
    run('verify-start.log', ['aspire', 'start', '--apphost', apphost, '--non-interactive', '--format', 'Json'])
    started = True
    run('verify-wait-collector.log', ['aspire', 'wait', 'collector', '--status', 'healthy', '--apphost', apphost, '--non-interactive'])
    run('verify-wait-producer.log', ['aspire', 'wait', 'producer', '--status', 'up', '--apphost', apphost, '--non-interactive'])
    run('describe.json', ['aspire', 'describe', '--apphost', apphost, '--format', 'Json'])
    description = json.loads((output / 'describe.json').read_text())
    env['LLMCMD_PRODUCER_URL'] = next(url['url'] for resource in description['resources']
                                    if resource['displayName'] == 'producer'
                                    for url in resource['urls'] if url['name'] == 'http')
    project = 'tests/LlmCmd.Observability.Tests/LlmCmd.Observability.Tests.csproj'
    run('verify-build.log', [str(dotnet), 'build', project, '--nologo'])
    run('verify-tests.log', [str(dotnet), 'run', '--no-build', '--project', project, '--', '--junit-output-path', str(output / 'tests.xml')])
finally:
    if started:
        run('verify-stop.log', ['aspire', 'stop', '--apphost', apphost, '--non-interactive'])
