#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."

mode="${1:-check}"
case "$mode" in
    format|check) ;;
    *) echo "Use format or check." >&2; exit 2 ;;
esac

if [[ "$mode" == "format" ]]; then
    # Apply the one build-enforced style rule before CSharpier. The SDK parser
    # supports preview syntax and adds braces without rewriting control flow.
    dotnet format style Zeeq.AgentCmd.slnx --no-restore --diagnostics IDE0011 --severity warn
else
    dotnet format style Zeeq.AgentCmd.slnx --no-restore --diagnostics IDE0011 --severity warn --verify-no-changes
fi

csharpier "$mode" .

# One exact exception list feeds both tools. New unsupported syntax fails the
# CSharpier check until a maintainer reviews and adds its source file here.
preview_files=()
while IFS= read -r line; do
    case "$line" in
        src/*.cs)
            [[ -f "$line" ]] || { echo "Missing preview-syntax source: $line" >&2; exit 1; }
            preview_files+=("$line")
            ;;
    esac
done < .csharpierignore

handled=0
# Load only the owning libraries. Formatting them does not need the AppHost's
# design-time workspace or the test projects' generated fixture catalogs.
for library in Zeeq.AgentCmd Zeeq.AgentCmd.Providers Zeeq.AgentCmd.OpenTelemetry; do
    library_files=()
    for source in "${preview_files[@]}"; do
        case "$source" in
            "src/$library/"*) library_files+=("$source"); handled=$((handled + 1)) ;;
        esac
    done
    [[ ${#library_files[@]} -gt 0 ]] || continue
    format_command=(dotnet format whitespace "src/$library/$library.csproj" --no-restore --include "${library_files[@]}")
    if [[ "$mode" == "check" ]]; then
        "${format_command[@]}" --verify-no-changes
    else
        # A compact declaration can expose one more indentation change. Keep
        # convergence bounded and verify, rather than silently accepting drift.
        "${format_command[@]}"
        "${format_command[@]}"
        "${format_command[@]}" --verify-no-changes
    fi
done
if [[ "$handled" -ne ${#preview_files[@]} ]]; then
    echo "A preview-syntax exemption has no owning formatter project." >&2
    exit 1
fi
