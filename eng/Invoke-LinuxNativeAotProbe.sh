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
dotnet_path=""
output_ready=false
output_preexisting_nonempty=false
evidence_path=""
probe_temp=""
publish_log=""
run_log=""
publish_pid=""
publish_parent_pid=""
publish_command_line=""
publish_started_epoch=""
publish_elapsed_ms=""
publish_termination="not-started"
publish_cleanup_attempted=false
publish_cleanup_incomplete=false
publish_cleanup_diagnostic=""
run_pid=""
run_parent_pid=""
run_command_line=""
run_started_epoch=""
run_elapsed_ms=""
run_termination="not-started"
run_cleanup_attempted=false
run_cleanup_incomplete=false
run_cleanup_diagnostic=""

# The timeout process is launched in the background so its PID and bounded
# elapsed time can be recorded without allowing a hung child to block the
# probe indefinitely. GNU timeout also forwards TERM/KILL to the command on a
# deadline; the RustSharp bounded runner performs the owned-tree cleanup
# inside the dotnet process itself.
epoch_milliseconds() {
    local value
    value="$(date +%s%3N 2>/dev/null || true)"
    if [[ "$value" =~ ^[0-9]+$ ]]; then
        printf '%s' "$value"
    else
        value="$(date +%s 2>/dev/null || true)"
        printf '%s000' "${value:-0}"
    fi
}

format_command_line() {
    local argument quoted line=""
    for argument in "$@"; do
        printf -v quoted '%q' "$argument"
        if [[ -n "$line" ]]; then
            line+=" "
        fi
        line+="$quoted"
    done
    printf '%s' "$line"
}

run_bounded_capture() {
    local timeout_value="$1"
    local log_path="$2"
    shift 2

    local started_epoch ended_epoch process_id command_line exit_code
    started_epoch="$(epoch_milliseconds)"
    command_line="$(format_command_line "$@")"

    set +e
    timeout --signal=TERM --kill-after=5s "${timeout_value}s" "$@" >"$log_path" 2>&1 &
    process_id=$!
    wait "$process_id"
    exit_code=$?
    set -e

    ended_epoch="$(epoch_milliseconds)"
    LAST_PID="$process_id"
    LAST_PARENT_PID="$$"
    LAST_STARTED_EPOCH="$started_epoch"
    LAST_COMMAND_LINE="$command_line"
    LAST_ELAPSED_MS=$((ended_epoch - started_epoch))
    LAST_EXIT_CODE="$exit_code"
    LAST_TERMINATION="exited"
    LAST_CLEANUP_ATTEMPTED=false
    LAST_CLEANUP_INCOMPLETE=false
    LAST_CLEANUP_DIAGNOSTIC=""

    if (( exit_code == 124 || exit_code == 137 )); then
        LAST_TERMINATION="timed-out"
        LAST_CLEANUP_ATTEMPTED=true
        LAST_CLEANUP_DIAGNOSTIC="timeout sent TERM/KILL to the bounded command"
    elif (( exit_code >= 128 )); then
        LAST_TERMINATION="signaled"
        LAST_CLEANUP_ATTEMPTED=true
        LAST_CLEANUP_DIAGNOSTIC="bounded command terminated by signal $((exit_code - 128))"
    fi

    # A completed timeout process is no longer killable. Keep this check as a
    # defensive guard for unusual timeout implementations and expose any
    # surviving wrapper as incomplete cleanup rather than hiding it.
    if kill -0 "$process_id" 2>/dev/null; then
        LAST_CLEANUP_ATTEMPTED=true
        LAST_CLEANUP_INCOMPLETE=true
        LAST_CLEANUP_DIAGNOSTIC="timeout wrapper remained alive after wait"
    fi
}

json_escape() {
    printf '%s' "$1" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g' -e 's/\r/\\r/g' -e 's/\t/\\t/g' -e ':a' -e 'N' -e '$!ba' -e 's/\n/\\n/g'
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
  "schemaVersion": 1,
  "platform": "linux-x64",
  "expectedExitCode": 0,
  "expectedStdout": "Hello from Rust#\n",
  "timeoutSeconds": ${timeout_seconds:-null},
  "publishStarted": $(json_string_or_null "$publish_started"),
  "dotnetPath": $(json_string_or_null "$dotnet_path"),
  "dotnetVersion": $(json_string_or_null "$dotnet_version"),
  "kernel": $(json_string_or_null "$kernel"),
  "executable": $(json_string_or_null "$executable"),
  "fileDescription": $(json_string_or_null "$file_description"),
  "publishExitCode": ${publish_exit:-null},
  "runExitCode": ${run_exit:-null},
  "scriptExitCode": $final_exit,
  "stdout": $(json_string_or_null "$actual_output"),
  "publishProcess": {
    "pid": ${publish_pid:-null},
    "parentPid": ${publish_parent_pid:-null},
    "startedAtEpochMilliseconds": ${publish_started_epoch:-null},
    "commandLine": $(json_string_or_null "$publish_command_line"),
    "elapsedMilliseconds": ${publish_elapsed_ms:-null},
    "termination": $(json_string_or_null "$publish_termination"),
    "cleanupAttempted": $publish_cleanup_attempted,
    "cleanupIncomplete": $publish_cleanup_incomplete,
    "cleanupDiagnostic": $(json_string_or_null "$publish_cleanup_diagnostic")
  },
  "runProcess": {
    "pid": ${run_pid:-null},
    "parentPid": ${run_parent_pid:-null},
    "startedAtEpochMilliseconds": ${run_started_epoch:-null},
    "commandLine": $(json_string_or_null "$run_command_line"),
    "elapsedMilliseconds": ${run_elapsed_ms:-null},
    "termination": $(json_string_or_null "$run_termination"),
    "cleanupAttempted": $run_cleanup_attempted,
    "cleanupIncomplete": $run_cleanup_incomplete,
    "cleanupDiagnostic": $(json_string_or_null "$run_cleanup_diagnostic")
  }
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
dotnet_path="$(command -v dotnet)"
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
run_bounded_capture "$timeout_seconds" "$publish_log" \
    dotnet run --project src/RustSharp.Cli --configuration Release --no-restore -- \
    publish "$source_path" --runtime linux-x64 --output "$output_dir" --timeout "$timeout_seconds"
publish_exit="$LAST_EXIT_CODE"
publish_pid="$LAST_PID"
publish_parent_pid="$LAST_PARENT_PID"
publish_started_epoch="$LAST_STARTED_EPOCH"
publish_command_line="$LAST_COMMAND_LINE"
publish_elapsed_ms="$LAST_ELAPSED_MS"
publish_termination="$LAST_TERMINATION"
publish_cleanup_attempted="$LAST_CLEANUP_ATTEMPTED"
publish_cleanup_incomplete="$LAST_CLEANUP_INCOMPLETE"
publish_cleanup_diagnostic="$LAST_CLEANUP_DIAGNOSTIC"
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

run_bounded_capture 30 "$run_log" "$executable"
run_exit="$LAST_EXIT_CODE"
run_pid="$LAST_PID"
run_parent_pid="$LAST_PARENT_PID"
run_started_epoch="$LAST_STARTED_EPOCH"
run_command_line="$LAST_COMMAND_LINE"
run_elapsed_ms="$LAST_ELAPSED_MS"
run_termination="$LAST_TERMINATION"
run_cleanup_attempted="$LAST_CLEANUP_ATTEMPTED"
run_cleanup_incomplete="$LAST_CLEANUP_INCOMPLETE"
run_cleanup_diagnostic="$LAST_CLEANUP_DIAGNOSTIC"
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
