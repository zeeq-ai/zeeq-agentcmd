#!/usr/bin/env bash
set -euo pipefail

# This is an explicit research command, outside the production CI graph. It
# compiles local fixtures only; no model keys, containers, or provider calls.
repository_root="$(cd "$(dirname "$0")/../.." && pwd -P)"
cd "$repository_root"
report_directory="${1:-$repository_root/artifacts/serialization-aot}"
mkdir -p "$report_directory"
report_directory="$(cd "$report_directory" && pwd -P)"

spike=research/serialization-aot
mise exec -- csharpier check "$spike" --ignore-path "$spike/.csharpierignore" > "$report_directory/format.log" 2>&1

test_project() {
    local project="$1"
    local name="$2"
    mise exec -- dotnet run --project "$spike/$project" -- \
        --report-trx --report-trx-filename "$report_directory/$name.trx" \
        > "$report_directory/$name.log" 2>&1
    test -s "$report_directory/$name.trx"
}

test_project oracle-tests/OracleTests.csproj oracle-tests
test_project metadata/Tests/Tests.csproj metadata-tests
test_project metadata-holdout-tests/MetadataHoldoutTests.csproj metadata-holdout-tests
test_project codec/Tests/Tests.csproj codec-tests
test_project codec/CommonTests/CommonTests.csproj codec-common-tests
test_project meai-metadata-tests/MetadataMeaiTests.csproj meai-metadata-tests
test_project meai-codec-tests/CodecMeaiTests.csproj meai-codec-tests

publish_and_run() {
    local project="$1"
    local name="$2"
    local binary="$3"
    shift 3
    mise exec -- dotnet publish "$spike/$project" -c Release -r osx-arm64 \
        -p:TreatWarningsAsErrors=true -p:ILLinkTreatWarningsAsErrors=true \
        -p:TrimmerSingleWarn=false "$@" -o "$report_directory/$name-native" \
        > "$report_directory/$name-publish.log" 2>&1
    "$report_directory/$name-native/$binary" --report-dir="$report_directory/$name-conformance" \
        > "$report_directory/$name-native.log" 2>&1
}

publish_and_run oracle/Oracle.csproj oracle Oracle -p:PublishAot=true
publish_and_run metadata/App/App.csproj metadata App -p:PublishAot=true
publish_and_run metadata-holdout/MetadataHoldout.csproj metadata-holdout MetadataHoldout -p:PublishAot=true
# Codec projects set PublishAot on the executable. Making it a global property
# would incorrectly ask the netstandard build-time analyzer to publish as AOT.
publish_and_run codec/Common/Common.csproj codec Common
publish_and_run meai-metadata/MetadataMeai.csproj meai-metadata MetadataMeai -p:PublishAot=true
publish_and_run meai-codec/CodecMeai.csproj meai-codec CodecMeai

# These once-failing cases are now regression gates. A failure must stop the
# reproduction instead of being accepted as the historical known result.
mise exec -- dotnet run --project "$spike/metadata-holdout/MetadataHoldout.csproj" \
    > "$report_directory/metadata-holdout.log" 2>&1
echo "Metadata holdout passed in managed and native execution."

# The same held-out contracts must be offered to the codec as well. It currently
# rejects ConstructorChoice at compile time; record rejection rather than count
# unexecuted runtime checks as passes or failures.
if mise exec -- dotnet run --project "$spike/codec-holdout/CodecHoldout.csproj" > "$report_directory/codec-holdout.log" 2>&1; then
    echo "Codec holdout is now green; update the recorded migration review."
else
    if ! rg -q 'error CODEC001' "$report_directory/codec-holdout.log"; then
        echo "Unexpected codec holdout failure; inspect codec-holdout.log." >&2
        exit 1
    fi
    echo "Codec rejects the held-out constructor shape with CODEC001. Runtime holdout checks did not run."
fi

echo "Research checks complete. Evidence: $report_directory"
