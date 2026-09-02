#!/usr/bin/env bash
set -euo pipefail

# Run the smallest Native AOT slice on a native Linux x64 host. The caller may
# provide a source path, output directory, and publish timeout in seconds.
source_path="${1:-samples/hello.rs}"
output_dir="${2:-artifacts/p0/linux-x64}"
timeout_seconds="${3:-300}"

status="started"
reason="script-started"
publish_exit=""
run_exit=""
dotnet_version=""
kernel=""
executable=""
file_description=""
actual_output=""
publish_started=""
output_ready=false
output_preexisting_nonempty=false
evidence_path=""
probe_temp=""
publish_log=""
run_log=""

json_escape() {
    printf '%s' "$1" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g' -e ':a' -e 'N' -e '$!ba' -e 's/\n/\\n/g'
}

json_string_or_null() {
    if [[ -n "$1" ]]; then
        printf '"%s"' "$(json_escape "$1")"
    else
        printf 'null'
    fi
}

write_evidence() {
    local final_exit="$1"
    [[ "$output_ready" == true && -n "$evidence_path" ]] || return 0
    # Never modify a directory that was non-empty on entry.
    [[ "$output_preexisting_nonempty" == true ]] && return 0

    cat >"$evidence_path" <<EOF
{
  "status": "$(json_escape "$status")",
  "reason": "$(json_escape "$reason")",
  "platform": "linux-x64",
  "publishStarted": $(json_string_or_null "$publish_started"),
  "dotnetVersion": $(json_string_or_null "$dotnet_version"),
  "kernel": $(json_string_or_null "$kernel"),
  "executable": $(json_string_or_null "$executable"),
  "fileDescription": $(json_string_or_null "$file_description"),
  "publishExitCode": ${publish_exit:-null},
  "runExitCode": ${run_exit:-null},
  "scriptExitCode": $final_exit,
  "stdout": $(json_string_or_null "$actual_output")
}
EOF
}

on_exit() {
    local exit_code="$?"
    if [[ "$status" == "started" ]]; then
        status="failed"
        reason="script-exited-before-validation"
    fi
    # Evidence must not hide the original exit status or turn cleanup into a
    # second failure. The output directory may not have been usable.
    write_evidence "$exit_code" || true
    if [[ -n "$probe_temp" && -d "$probe_temp" ]]; then
        # timeout is checked before probe_temp is created; keep cleanup bounded
        # even if a filesystem operation becomes unresponsive.
        timeout --signal=TERM --kill-after=1s 5s rm -rf -- "$probe_temp" || true
    fi
}
trap on_exit EXIT

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

# Prepare the evidence location before environment checks so skipped probes
# still leave a machine-readable reason whenever the path is writable.
mkdir -p "$output_dir"
output_dir="$(realpath -m "$output_dir")"
evidence_path="$output_dir/linux-aot-evidence.json"
if find "$output_dir" -mindepth 1 -maxdepth 1 -print -quit | grep -q .; then
    output_preexisting_nonempty=true
    status="failed"
    reason="output-directory-not-empty"
    printf 'output directory must be empty: %s\n' "$output_dir" >&2
    exit 2
fi
output_ready=true
kernel="$(uname -srvm 2>/dev/null || true)"

if [[ ! "$timeout_seconds" =~ ^[1-9][0-9]*$ ]] || (( timeout_seconds > 3600 )); then
    status="failed"
    reason="invalid-timeout"
    printf 'timeout must be an integer from 1 through 3600 seconds\n' >&2
    exit 2
fi

if [[ "$(uname -s)" != "Linux" || "$(uname -m)" != "x86_64" ]]; then
    status="skipped"
    reason="native-linux-x86_64-required"
    printf 'P0-10 requires a native Linux x86_64 runner (found %s/%s)\n' "$(uname -s)" "$(uname -m)" >&2
    exit 77
fi
if ! command -v dotnet >/dev/null 2>&1; then
    status="skipped"
    reason="dotnet-not-found"
    printf 'dotnet was not found on PATH\n' >&2
    exit 77
fi
if ! command -v timeout >/dev/null 2>&1; then
    status="skipped"
    reason="timeout-not-found"
    printf 'timeout was not found on PATH\n' >&2
    exit 77
fi
if ! command -v file >/dev/null 2>&1; then
    status="skipped"
    reason="file-not-found"
    printf 'file was not found on PATH\n' >&2
    exit 77
fi

set +e
dotnet_version="$(dotnet --version 2>&1)"
dotnet_status=$?
set -e
if (( dotnet_status != 0 )); then
    status="skipped"
    reason="required-dotnet-sdk-unavailable"
    printf 'required .NET SDK is unavailable (dotnet status=%d): %s\n' "$dotnet_status" "$dotnet_version" >&2
    exit 77
fi

if ! source_path="$(realpath -e "$source_path")"; then
    status="failed"
    reason="source-not-found"
    printf 'source path does not exist: %s\n' "$source_path" >&2
    exit 2
fi

probe_temp="$(mktemp -d "${TMPDIR:-/tmp}/rustsharp-linux-aot.XXXXXX")"
publish_log="$probe_temp/linux-aot-publish.log"
run_log="$probe_temp/linux-aot-run.log"
publish_started="$(date --iso-8601=seconds)"
set +e
timeout --signal=TERM --kill-after=5s "${timeout_seconds}s" \
    dotnet run --project src/RustSharp.Cli --configuration Release --no-restore -- \
    publish "$source_path" --runtime linux-x64 --output "$output_dir" --timeout "$timeout_seconds" \
    >"$publish_log" 2>&1
publish_exit=$?
set -e
if (( publish_exit != 0 )); then
    status="failed"
    reason="native-aot-publish-failed"
    cp "$publish_log" "$output_dir/linux-aot-publish.log" 2>/dev/null || true
    printf 'Native AOT publish failed with exit code %d\n' "$publish_exit" >&2
    cat "$publish_log" >&2
    exit "$publish_exit"
fi
if ! cp "$publish_log" "$output_dir/linux-aot-publish.log"; then
    status="failed"
    reason="native-aot-publish-log-copy-failed"
    printf 'could not copy the publish log into the evidence directory\n' >&2
    exit 1
fi

mapfile -t executables < <(find "$output_dir" -mindepth 1 -maxdepth 1 -type f -perm -111 -name '*.NativeAotHost' -print)
if (( ${#executables[@]} != 1 )); then
    status="failed"
    reason="native-aot-executable-count-mismatch"
    printf 'expected exactly one executable named *.NativeAotHost, found %d\n' "${#executables[@]}" >&2
    exit 1
fi
executable="${executables[0]}"
file_description="$(file -b "$executable")"
if [[ "$file_description" != *"ELF 64-bit"* || "$file_description" != *"x86-64"* ]]; then
    status="failed"
    reason="native-aot-artifact-not-linux-x64-elf"
    printf 'published artifact is not an x86-64 ELF: %s\n' "$file_description" >&2
    exit 1
fi

set +e
timeout --signal=TERM --kill-after=5s 30s "$executable" >"$run_log" 2>&1
run_exit=$?
set -e
cp "$run_log" "$output_dir/linux-aot-run.log" 2>/dev/null || true
expected_output=$'Hello from Rust#\n'
actual_output="$(cat "$run_log"; printf x)"
actual_output="${actual_output%x}"
if (( run_exit != 0 )) || [[ "$actual_output" != "$expected_output" ]]; then
    status="failed"
    reason="native-aot-runtime-output-mismatch"
    printf 'native executable failed: exit=%d output=%q\n' "$run_exit" "$actual_output" >&2
    exit 1
fi

status="passed"
reason="native-aot-output-verified"
printf 'Linux x64 Native AOT passed: %s\nEvidence: %s\n' "$executable" "$evidence_path"
