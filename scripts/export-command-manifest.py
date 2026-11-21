#!/usr/bin/env python3
"""Export generator-owned manifest data; never import, load, or execute the application.

Build with EmitCompilerGeneratedFiles=true, then supply the single generated
AgentCmd.Manifest.g.cs file. Baseline prompt/profile disclosure is opt-in.
The .sha256 sidecar describes the exact JSON bytes (no BOM or trailing newline).
"""

import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import re
import tempfile


def atomic_write(path: Path, data: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary = tempfile.mkstemp(prefix=path.name + ".", dir=path.parent)
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(data)
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--generated-file", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--include-baseline-templates", action="store_true")
    args = parser.parse_args()
    policy = "baseline-templates" if args.include_baseline_templates else "structural"
    try:
        source = args.generated_file.read_text(encoding="utf-8-sig")
        matches = re.findall(
            rf"^// agentcmd-manifest:{policy}:([0-9a-f]{{64}}):([A-Za-z0-9+/=]+)$",
            source,
            re.MULTILINE,
        )
        if len(matches) != 1:
            raise ValueError("Expected exactly one generated manifest for the selected disclosure policy.")
        expected_digest, encoded = matches[0]
        data = base64.b64decode(encoded, validate=True)
        digest = hashlib.sha256(data).hexdigest()
        if digest != expected_digest:
            raise ValueError("Generated manifest checksum mismatch.")
        manifest = json.loads(data.decode("utf-8"))
        if (
            manifest.get("formatVersion") != 1
            or not isinstance(manifest.get("catalogId"), str)
            or not manifest["catalogId"].strip()
            or not isinstance(manifest.get("commands"), list)
            or manifest.get("baselineTemplatesIncluded") is not args.include_baseline_templates
        ):
            raise ValueError("Invalid generated manifest envelope or disclosure policy.")
        if args.generated_file.resolve() in (args.output.resolve(), Path(str(args.output) + ".sha256").resolve()):
            raise ValueError("Output must not overwrite the generated source.")
        atomic_write(args.output, data)
        atomic_write(Path(str(args.output) + ".sha256"), (digest + "\n").encode("ascii"))
    except (OSError, ValueError, TypeError, AttributeError) as error:
        # Do not print raw manifest/template content when extraction fails.
        parser.exit(1, f"Manifest export failed: {type(error).__name__}.\n")
    print(f"Manifest exported: {args.output} (sha256 {digest})")


if __name__ == "__main__":
    main()
