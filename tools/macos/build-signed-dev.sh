#!/usr/bin/env bash
set -euo pipefail
action="${1:-build}"
if [[ "$action" != "build" && "$action" != "test" ]]; then
    printf '%s\n' 'Supported actions: build, test.' >&2
    exit 1
fi
if [[ $# -gt 0 ]]; then shift; fi
for argument in "$@"; do
    if [[ "$action" != "test" || ! "$argument" =~ ^-(only|skip)-testing:NativeAppTests/[A-Za-z0-9_]+(/[A-Za-z0-9_]+)?$ ]]; then
        printf '%s\n' 'Additional arguments must be NativeAppTests only-testing or skip-testing selectors.' >&2
        exit 1
    fi
done

configuration=Debug
derived_data=build/DerivedData
# ANNOTATOR_TEST_ARCH=x86_64 builds and tests the Intel code; on Apple Silicon it runs under Rosetta.
arch="${ANNOTATOR_TEST_ARCH:-arm64}"
if [[ "$arch" != "arm64" && "$arch" != "x86_64" ]]; then
    printf '%s\n' 'ANNOTATOR_TEST_ARCH must be arm64 or x86_64.' >&2
    exit 1
fi
if [[ "$arch" == "x86_64" ]]; then derived_data=build/DerivedData-x86_64; fi
overrides=(CODE_SIGN_STYLE=Manual CODE_SIGN_IDENTITY="Apple Development")
stamp="$(date '+%Y-%m-%d %H:%M %z')"

team="${DEVELOPMENT_TEAM:-}"
if [[ -z "$team" ]]; then
    team="$(security find-certificate -c 'Apple Development' -p |
        openssl x509 -noout -subject -nameopt RFC2253 |
        sed -n 's/.*OU=\([^,]*\).*/\1/p')"
fi
if [[ ! "$team" =~ ^[A-Z0-9]{10}$ ]]; then
    printf '%s\n' 'A valid personal development team is required. Set DEVELOPMENT_TEAM explicitly.' >&2
    exit 1
fi

xcodebuild -project apps/macos/Annotator.xcodeproj -scheme Annotator \
    -configuration "$configuration" -destination "platform=macOS,arch=$arch" -derivedDataPath "$derived_data" \
    DEVELOPMENT_TEAM="$team" CODE_SIGNING_REQUIRED=YES \
    ANNOTATOR_BUILD_STAMP="$stamp" "${overrides[@]}" "$@" "$action"
