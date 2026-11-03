#!/usr/bin/env python3
"""Run this workspace's RabbitMQ + two-process propagation proof and stop only its AppHost."""
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
output = pathlib.Path('/tmp/opencode/llmcmd-research-propagation')
(output / 'otel').mkdir(parents=True, exist_ok=True)
dotnet = pathlib.Path(shutil.which(args.dotnet) or args.dotnet).absolute()
env = dict(os.environ, DOTNET_HOST_PATH=str(dotnet), TUNIT_ENABLE_JUNIT_REPORTER='1',
           DOTNET_ROOT=str(dotnet.parent), PATH=str(dotnet.parent) + os.pathsep + os.environ.get('PATH', ''))
if args.docker_config:
    env['DOCKER_CONFIG'] = args.docker_config
    env['ASPIRE_ENABLE_CONTAINER_TUNNEL'] = 'false'
apphost = str(root / 'spikes/PropagationHost/apphost.cs')

def run(name, command, timeout=180):
    with (output / name).open('w') as log:
        result = subprocess.run(command, cwd=root, env=env, stdout=log, stderr=subprocess.STDOUT, timeout=timeout)
    if result.returncode:
        raise RuntimeError(f'{name}: exit {result.returncode}; inspect {output / name}')

run('ps-before.json', ['aspire', 'ps', '--format', 'Json'])
if any(r['appHostPath'] == apphost for r in json.loads((output / 'ps-before.json').read_text())):
    raise RuntimeError('The propagation AppHost is already running; leave the existing session intact')
project = 'tests/LlmCmd.PropagationHost.Tests/LlmCmd.PropagationHost.Tests.csproj'
run('host-tests-build.log', [str(dotnet), 'build', project, '--nologo'])
started = False
try:
    run('start.log', ['aspire', 'start', '--apphost', apphost, '--isolated', '--non-interactive', '--format', 'Json'])
    started = True
    for resource, status in [('queue', 'healthy'), ('collector', 'healthy'), ('edge', 'up'), ('worker', 'up')]:
        run(f'wait-{resource}.log', ['aspire', 'wait', resource, '--status', status, '--apphost', apphost, '--non-interactive'])
    run('describe.json', ['aspire', 'describe', '--apphost', apphost, '--format', 'Json'])
    description = json.loads((output / 'describe.json').read_text())
    for name in ['edge', 'worker']:
        env[f'LLMCMD_{name.upper()}_URL'] = next(u['url'] for r in description['resources']
                                              if r['displayName'] == name for u in r['urls'] if u['name'] == 'http')
    run('host-tests.log', [str(dotnet), 'run', '--no-build', '--project', project, '--',
                           '--junit-output-path', str(output / 'host-tests.xml')])
finally:
    if started:
        for resource in ['edge', 'worker', 'queue', 'collector']:
            try:
                run(f'logs-{resource}.txt', ['aspire', 'logs', resource, '--apphost', apphost, '--non-interactive'])
            except Exception:
                pass
        run('stop.log', ['aspire', 'stop', '--apphost', apphost, '--non-interactive'])
