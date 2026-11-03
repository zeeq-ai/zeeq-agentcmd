#!/usr/bin/env python3
"""Prove user-secrets -> Aspire parameter -> explicitly started TUnit process."""
import argparse
import hashlib
import json
import os
import pathlib
import secrets
import shutil
import subprocess
import xml.etree.ElementTree as ET


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--dotnet', default=os.environ.get('DOTNET_HOST_PATH', 'dotnet'))
    args = parser.parse_args()
    root = pathlib.Path(__file__).resolve().parents[1]
    host = root / 'spikes/SecretPropagation/apphost.cs'
    output = pathlib.Path('/tmp/opencode/llmcmd-research-secret-propagation')
    output.mkdir(parents=True, exist_ok=True)
    (output / 'proof.json').unlink(missing_ok=True)
    dotnet = pathlib.Path(shutil.which(args.dotnet) or args.dotnet).resolve()
    env = dict(os.environ, DOTNET_HOST_PATH=str(dotnet), DOTNET_ROOT=str(dotnet.parent),
               PATH=str(dotnet.parent) + os.pathsep + os.environ.get('PATH', ''),
               ASPIRE_ENVIRONMENT='Development', DOTNET_ENVIRONMENT='Development')
    # The secret must come from user-secrets, not from a pre-existing parent env.
    for key in list(env):
        if key.upper() in ('LLMCMD_SPIKE_SECRET', 'PARAMETERS__SECRET-PROPAGATION-PLACEHOLDER',
                           'PARAMETERS__SECRET_PROPAGATION_PLACEHOLDER'):
            env.pop(key)

    def run(name, command, *, stdin=None, save=True):
        result = subprocess.run(command, cwd=host.parent, env=env, input=stdin,
                                capture_output=True, text=True, timeout=180)
        if save:
            (output / (name + '.log')).write_text(result.stdout + result.stderr)
        if result.returncode:
            raise RuntimeError(f'{name} failed; inspect {output / (name + ".log")}')
        print(name + ': passed', flush=True)
        return result.stdout

    def description(name):
        # Describe may include process environments. Keep the raw response in
        # memory; store only resource status/exit-code evidence, never its values.
        model = json.loads(run(name, ['aspire', 'describe', '--apphost', str(host),
                                     '--format', 'Json', '--non-interactive'], save=False))
        resources = [{k: r[k] for k in ('displayName', 'state', 'exitCode') if k in r}
                     for r in model['resources'] if r.get('displayName') in names]
        (output / (name + '.json')).write_text(json.dumps(resources, indent=2))
        return {r['displayName']: r for r in resources}

    running = json.loads(run('ps-before', ['aspire', 'ps', '--format', 'Json']))
    if any(r['appHostPath'] == str(host) for r in running):
        raise RuntimeError('This spike is already running; its existing session was left intact')
    # The pinned SDK supports `set --file`, but not `init --file`. The
    # file-based AppHost declares its stable UserSecretsId property itself.
    placeholder = 'placeholder-only-' + secrets.token_urlsafe(32)
    run('secrets-set', [str(dotnet), 'user-secrets', 'set', '--file', str(host)],
        stdin=json.dumps({'Parameters:secret-propagation-placeholder': placeholder}))
    env['SpikeExpectedSha256'] = hashlib.sha256(placeholder.encode()).hexdigest()
    env['SpikeEvidenceDirectory'] = str(output)
    del placeholder
    names = ('control-tests', 'receiving-tests')
    for name in names:
        for suffix in ('.xml', '.gate'):
            (output / (name + suffix)).unlink(missing_ok=True)
    project = root / 'tests/LlmCmd.SecretPropagation.Tests/LlmCmd.SecretPropagation.Tests.csproj'
    run('tests-build', [str(dotnet), 'build', str(project), '--nologo'])
    started = False
    try:
        run('start', ['aspire', 'start', '--apphost', str(host), '--isolated',
                      '--non-interactive', '--format', 'Json'])
        started = True
        before = description('before-explicit-start')
        if len(before) != 2 or any(r['state'] != 'NotStarted' for r in before.values()):
            raise RuntimeError('Tests did not remain stopped until explicit start')
        if any((output / (name + '.xml')).exists() for name in names):
            raise RuntimeError('A test report existed before explicit start')
        for name in names:
            run(name + '-start', ['aspire', 'resource', name, 'start', '--apphost', str(host), '--non-interactive'])
            run(name + '-up', ['aspire', 'wait', name, '--status', 'up', '--timeout', '60',
                              '--apphost', str(host), '--non-interactive'])
            (output / (name + '.gate')).write_text('Aspire observed the test process running.\n')
            run(name + '-down', ['aspire', 'wait', name, '--status', 'down', '--timeout', '60',
                                '--apphost', str(host), '--non-interactive'])
            run(name + '-logs', ['aspire', 'logs', name, '--apphost', str(host), '--non-interactive'])
            report = ET.parse(output / (name + '.xml')).getroot()
            cases = list(report.iter('testcase'))
            if len(cases) != 2 or any(c.find('failure') is not None or c.find('error') is not None
                                      or c.find('skipped') is not None for c in cases):
                raise RuntimeError(name + ' did not pass both tests')
        after = description('after-tests')
        if any(r.get('exitCode') != 0 for r in after.values()):
            raise RuntimeError('A test process exited unsuccessfully')
        (output / 'proof.json').write_text(json.dumps({
            'secretSource': 'AppHost user-secrets', 'explicitStartVerified': True,
            'recipientMatchedExactPlaceholder': True, 'controlDidNotReceiveSecret': True,
            'secretAbsentFromProcessArguments': True, 'testsPassed': 4,
        }, indent=2))
    finally:
        if started:
            run('stop', ['aspire', 'stop', '--apphost', str(host), '--non-interactive'])
    print('4/4 TUnit checks passed; explicit start and scoped secret injection verified.', flush=True)


if __name__ == '__main__':
    main()
