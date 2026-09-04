#!/usr/bin/env bash
set -euo pipefail

# Run the smallest Native AOT slice on a native Linux x64 host. The caller may
# provide a source path, output directory, and publish timeout in seconds.
source_path="${1:-samples/hello.rs}"
output_dir="${2:-artifacts/p0/linux-x64}"
timeout_seconds="${3:-300}"
timeout_seconds_json="null"

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
publish_log_capture_pid=""
publish_log_capture_parent_pid=""
publish_log_capture_started_epoch=""
publish_log_capture_elapsed_ms=""
publish_log_capture_exit_code=""
publish_log_capture_cleanup_incomplete=false
publish_log_capture_diagnostic=""
run_pid=""
run_parent_pid=""
run_command_line=""
run_started_epoch=""
run_elapsed_ms=""
run_termination="not-started"
run_cleanup_attempted=false
run_cleanup_incomplete=false
run_cleanup_diagnostic=""
run_log_capture_pid=""
run_log_capture_parent_pid=""
run_log_capture_started_epoch=""
run_log_capture_elapsed_ms=""
run_log_capture_exit_code=""
run_log_capture_cleanup_incomplete=false
run_log_capture_diagnostic=""
publish_output_truncated=false
run_output_truncated=false

# Keep captured diagnostics bounded even when a compiler process is noisy. The
# logger drains the pipe after retaining the first bytes, so the child cannot
# block on a full pipe and the temporary filesystem never grows without bound.
maximum_log_bytes=262144
capture_wait_timeout_seconds=10
capture_sequence=0
active_capture_pid=""
active_capture_start_ticks=""
active_capture_parent_pid=""
active_capture_command_line=""
active_capture_fifo=""
active_capture_marker=""
active_command_pid=""
active_command_process_group=""
active_command_session_id=""
active_command_start_ticks=""
active_command_parent_pid=""
active_command_identity_command_line=""
active_command_completion_path=""
evidence_temp_path=""
probe_cleanup_attempted=false
probe_cleanup_incomplete=false
probe_cleanup_diagnostic=""
OBSERVED_PROCESS_START_TICKS=""
OBSERVED_PROCESS_PARENT_PID=""
OBSERVED_PROCESS_GROUP_ID=""
OBSERVED_PROCESS_SESSION_ID=""
OBSERVED_PROCESS_ARGUMENT_ZERO=""
OBSERVED_PROCESS_EXECUTABLE=""
OBSERVED_PROCESS_COMMAND_LINE=""
PROCESS_IDENTITY_DIAGNOSTIC=""
LAST_WAIT_CLEANUP_INCOMPLETE=false
LAST_WAIT_CLEANUP_ATTEMPTED=false
LAST_WAIT_CLEANUP_DIAGNOSTIC=""
READ_COMPLETION_EXIT_CODE=""
READ_COMPLETION_DIAGNOSTIC=""
READ_FILE_CONTENT=""
READ_FILE_DIAGNOSTIC=""
EXACT_OUTPUT_DIAGNOSTIC=""
PROCESS_GROUP_OBSERVATION_DIAGNOSTIC=""
MONOTONIC_MICROSECONDS=""
BOUNDARY_DIAGNOSTIC=""
PID_OBSERVATION_DIAGNOSTIC=""
PID_SIGNAL_SENT_PROCESS_ID=""
PID_SIGNAL_SENT_START_TICKS=""
PID_SIGNAL_SENT_PARENT_PID=""
PID_SIGNAL_SENT_COMMAND_LINE=""
PID_SIGNAL_SENT_NAME=""
KILL_SENT_PROCESS_ID=""
KILL_SENT_PROCESS_GROUP_ID=""
KILL_SENT_SESSION_ID=""
KILL_SENT_START_TICKS=""
KILL_SENT_PARENT_PID=""
KILL_SENT_COMMAND_LINE=""
LAST_STOP_DIAGNOSTIC=""
LAST_PID=""
LAST_PARENT_PID=""
LAST_STARTED_EPOCH=""
LAST_COMMAND_LINE=""
LAST_ELAPSED_MS=""
LAST_EXIT_CODE=""
LAST_TERMINATION="not-started"
LAST_CLEANUP_ATTEMPTED=false
LAST_CLEANUP_INCOMPLETE=false
LAST_CLEANUP_DIAGNOSTIC=""
LAST_OUTPUT_TRUNCATED=false
LAST_LOG_CAPTURE_PID=""
LAST_LOG_CAPTURE_PARENT_PID=""
LAST_LOG_CAPTURE_STARTED_EPOCH=""
LAST_LOG_CAPTURE_ELAPSED_MS=""
LAST_LOG_CAPTURE_EXIT_CODE=""
LAST_LOG_CAPTURE_CLEANUP_INCOMPLETE=false
LAST_LOG_CAPTURE_DIAGNOSTIC=""

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

read_monotonic_microseconds() {
    local uptime_value whole_seconds fractional_seconds

    MONOTONIC_MICROSECONDS=""
    BOUNDARY_DIAGNOSTIC=""
    if ! IFS=' ' read -r uptime_value _ 2>/dev/null </proc/uptime; then
        BOUNDARY_DIAGNOSTIC="could not read the monotonic /proc/uptime clock"
        return 1
    fi
    if [[ ! "$uptime_value" =~ ^([0-9]+)\.([0-9]+)$ ]]; then
        BOUNDARY_DIAGNOSTIC="the monotonic /proc/uptime clock had an invalid shape"
        return 1
    fi
    whole_seconds="${BASH_REMATCH[1]}"
    fractional_seconds="${BASH_REMATCH[2]}000000"
    fractional_seconds="${fractional_seconds:0:6}"
    MONOTONIC_MICROSECONDS=$((10#$whole_seconds * 1000000 + 10#$fractional_seconds))
}

sleep_until_deadline() {
    local deadline_microseconds="$1"
    local remaining_microseconds sleep_microseconds sleep_value

    if ! read_monotonic_microseconds; then
        return 2
    fi
    remaining_microseconds=$((deadline_microseconds - MONOTONIC_MICROSECONDS))
    if (( remaining_microseconds <= 0 )); then
        return 1
    fi
    sleep_microseconds="$remaining_microseconds"
    if (( sleep_microseconds > 50000 )); then
        sleep_microseconds=50000
    fi
    printf -v sleep_value '0.%06d' "$sleep_microseconds"
    if ! sleep "$sleep_value"; then
        BOUNDARY_DIAGNOSTIC="bounded observation sleep failed"
        return 2
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

read_file_preserving_newlines() {
    local path="$1"
    local value

    READ_FILE_CONTENT=""
    READ_FILE_DIAGNOSTIC=""
    if value="$(
        set +e
        cat -- "$path"
        file_exit=$?
        printf x
        sentinel_exit=$?
        if (( file_exit != 0 )); then
            exit "$file_exit"
        fi
        exit "$sentinel_exit"
    )"; then
        :
    else
        READ_FILE_DIAGNOSTIC="could not read output file $path"
        return 1
    fi
    if [[ "$value" != *x ]]; then
        READ_FILE_DIAGNOSTIC="output file sentinel was not retained for $path"
        return 1
    fi
    READ_FILE_CONTENT="${value%x}"
}

verify_exact_runtime_output() {
    local actual_path="$1"
    local expected_path="$2"
    local comparison_exit

    EXACT_OUTPUT_DIAGNOSTIC=""
    if ! printf 'Hello from Rust#\n' >"$expected_path"; then
        EXACT_OUTPUT_DIAGNOSTIC="could not write the task-owned expected output bytes"
        return 2
    fi
    if cmp -s -- "$expected_path" "$actual_path"; then
        return 0
    else
        comparison_exit=$?
    fi
    if (( comparison_exit == 1 )); then
        EXACT_OUTPUT_DIAGNOSTIC="runtime output bytes do not exactly match the expected output"
        return 1
    fi
    EXACT_OUTPUT_DIAGNOSTIC="runtime output byte comparison failed with code $comparison_exit"
    return 2
}

read_process_identity() {
    local process_id="$1"
    local stat_line stat_tail verification_stat_line verification_stat_tail executable_path
    local -a stat_fields=()
    local -a verification_stat_fields=()
    local -a command_arguments=()

    OBSERVED_PROCESS_START_TICKS=""
    OBSERVED_PROCESS_PARENT_PID=""
    OBSERVED_PROCESS_GROUP_ID=""
    OBSERVED_PROCESS_SESSION_ID=""
    OBSERVED_PROCESS_ARGUMENT_ZERO=""
    OBSERVED_PROCESS_EXECUTABLE=""
    OBSERVED_PROCESS_COMMAND_LINE=""
    [[ "$process_id" =~ ^[1-9][0-9]*$ ]] || return 1
    [[ -r "/proc/$process_id/stat" && -r "/proc/$process_id/cmdline" && -e "/proc/$process_id/exe" ]] || return 1
    IFS= read -r stat_line <"/proc/$process_id/stat" || return 1
    [[ "$stat_line" == *") "* ]] || return 1
    stat_tail="${stat_line##*) }"
    read -r -a stat_fields <<<"$stat_tail"
    (( ${#stat_fields[@]} >= 20 )) || return 1
    mapfile -d '' -t command_arguments <"/proc/$process_id/cmdline" || return 1
    (( ${#command_arguments[@]} > 0 )) || return 1
    executable_path="$(readlink -e -- "/proc/$process_id/exe" 2>/dev/null)" || return 1
    [[ -n "$executable_path" ]] || return 1
    IFS= read -r verification_stat_line <"/proc/$process_id/stat" || return 1
    [[ "$verification_stat_line" == *") "* ]] || return 1
    verification_stat_tail="${verification_stat_line##*) }"
    read -r -a verification_stat_fields <<<"$verification_stat_tail"
    (( ${#verification_stat_fields[@]} >= 20 )) || return 1
    if [[ "${verification_stat_fields[1]}" != "${stat_fields[1]}" ||
          "${verification_stat_fields[2]}" != "${stat_fields[2]}" ||
          "${verification_stat_fields[3]}" != "${stat_fields[3]}" ||
          "${verification_stat_fields[19]}" != "${stat_fields[19]}" ]]; then
        return 1
    fi

    OBSERVED_PROCESS_PARENT_PID="${stat_fields[1]}"
    OBSERVED_PROCESS_GROUP_ID="${stat_fields[2]}"
    OBSERVED_PROCESS_SESSION_ID="${stat_fields[3]}"
    OBSERVED_PROCESS_START_TICKS="${stat_fields[19]}"
    OBSERVED_PROCESS_ARGUMENT_ZERO="${command_arguments[0]}"
    OBSERVED_PROCESS_EXECUTABLE="$executable_path"
    OBSERVED_PROCESS_COMMAND_LINE="$(format_command_line "${command_arguments[@]}")"
    [[ "$OBSERVED_PROCESS_PARENT_PID" =~ ^[0-9]+$ &&
       "$OBSERVED_PROCESS_GROUP_ID" =~ ^[1-9][0-9]*$ &&
       "$OBSERVED_PROCESS_SESSION_ID" =~ ^[1-9][0-9]*$ &&
       "$OBSERVED_PROCESS_START_TICKS" =~ ^[0-9]+$ ]]
}

read_process_identity_stabilized() {
    local process_id="$1"
    local expected_basename="$2"
    local expected_process_group="${3:-}"
    local expected_session_id="${4:-}"
    local maximum_attempts=5
    local attempt argument_zero_basename executable_basename

    for ((attempt = 1; attempt <= maximum_attempts; attempt++)); do
        if read_process_identity "$process_id"; then
            argument_zero_basename="${OBSERVED_PROCESS_ARGUMENT_ZERO##*/}"
            executable_basename="${OBSERVED_PROCESS_EXECUTABLE##*/}"
            if [[ "$argument_zero_basename" == "$expected_basename" &&
                  "$executable_basename" == "$expected_basename" &&
                  ( -z "$expected_process_group" || "$OBSERVED_PROCESS_GROUP_ID" == "$expected_process_group" ) &&
                  ( -z "$expected_session_id" || "$OBSERVED_PROCESS_SESSION_ID" == "$expected_session_id" ) ]]; then
                return 0
            fi
        fi
        if (( attempt < maximum_attempts )); then
            sleep 0.02
        fi
    done
    return 1
}

process_identity_matches() {
    local process_id="$1"
    local expected_start_ticks="$2"
    local expected_parent_pid="$3"
    local expected_command_line="$4"

    PROCESS_IDENTITY_DIAGNOSTIC=""
    if [[ -z "$expected_start_ticks" || -z "$expected_parent_pid" || -z "$expected_command_line" ]]; then
        PROCESS_IDENTITY_DIAGNOSTIC="refused to signal PID $process_id because its recorded identity is incomplete"
        return 1
    fi
    if ! read_process_identity "$process_id"; then
        PROCESS_IDENTITY_DIAGNOSTIC="refused to signal PID $process_id because its current identity is unavailable"
        return 1
    fi
    if [[ "$OBSERVED_PROCESS_START_TICKS" != "$expected_start_ticks" ||
          "$OBSERVED_PROCESS_PARENT_PID" != "$expected_parent_pid" ||
          "$OBSERVED_PROCESS_COMMAND_LINE" != "$expected_command_line" ]]; then
        PROCESS_IDENTITY_DIAGNOSTIC="refused to signal PID $process_id because its start time, parent PID, or command line no longer matches the recorded identity"
        return 1
    fi
}

signal_owned_pid() {
    local process_id="$1"
    local expected_start_ticks="$2"
    local expected_parent_pid="$3"
    local expected_command_line="$4"
    local signal_name="$5"

    process_identity_matches "$process_id" "$expected_start_ticks" "$expected_parent_pid" "$expected_command_line" || return 1
    kill "-$signal_name" -- "$process_id" 2>/dev/null
}

pid_is_running() {
    local process_id="$1"
    local stat_line stat_tail
    local -a stat_fields=()

    if ! kill -0 "$process_id" 2>/dev/null; then
        return 1
    fi

    # kill -0 also succeeds for a zombie until its parent reaps it. Treat a
    # confirmed Z/X state as terminal. Any unreadable or malformed state is
    # conservatively live so an observation failure can never enter wait.
    [[ -r "/proc/$process_id/stat" ]] || return 0
    IFS= read -r stat_line 2>/dev/null <"/proc/$process_id/stat" || return 0
    [[ "$stat_line" == *") "* ]] || return 0
    stat_tail="${stat_line##*) }"
    read -r -a stat_fields <<<"$stat_tail"
    (( ${#stat_fields[@]} >= 1 )) || return 0
    [[ "${stat_fields[0]}" != Z && "${stat_fields[0]}" != X ]]
}

wait_for_pid_bounded() {
    local process_id="$1"
    local timeout_seconds_value="$2"
    local process_group_id="${3:-}"
    local expected_start_ticks="${4:-}"
    local expected_parent_pid="${5:-}"
    local expected_command_line="${6:-}"
    local expected_session_id="${7:-}"
    local poll_milliseconds_value=250
    local maximum_polls=$((timeout_seconds_value * 1000 / poll_milliseconds_value + 1))
    local poll

    LAST_WAIT_CLEANUP_INCOMPLETE=false
    LAST_WAIT_CLEANUP_ATTEMPTED=false
    LAST_WAIT_CLEANUP_DIAGNOSTIC=""
    for ((poll = 0; poll < maximum_polls; poll++)); do
        if ! pid_is_running "$process_id"; then
            wait "$process_id" 2>/dev/null
            return "$?"
        fi
        sleep 0.25
    done

    # Revalidate the recorded identity before signaling. If the PID or PGID
    # was reused, preserve the unrelated process and expose incomplete cleanup.
    if ! stop_recorded_process_group \
        "$process_id" "$process_group_id" \
        "$expected_start_ticks" "$expected_parent_pid" "$expected_command_line" "$expected_session_id"; then
        LAST_WAIT_CLEANUP_INCOMPLETE=true
        LAST_WAIT_CLEANUP_DIAGNOSTIC="${LAST_STOP_DIAGNOSTIC:-bounded process ownership could not be reconfirmed}"
    fi
    LAST_WAIT_CLEANUP_ATTEMPTED=true
    return 124
}

read_completion_exit_code() {
    local completion_path="$1"
    local -a completion_lines=()

    READ_COMPLETION_EXIT_CODE=""
    READ_COMPLETION_DIAGNOSTIC=""
    [[ -e "$completion_path" ]] || return 1
    if [[ ! -f "$completion_path" || ! -r "$completion_path" ]]; then
        READ_COMPLETION_DIAGNOSTIC="command completion marker is not a readable regular file"
        return 2
    fi
    if ! mapfile -n 2 -t completion_lines <"$completion_path"; then
        READ_COMPLETION_DIAGNOSTIC="command completion marker could not be read"
        return 2
    fi
    if (( ${#completion_lines[@]} != 1 )) || [[ ! "${completion_lines[0]}" =~ ^(0|[1-9][0-9]{0,2})$ ]]; then
        READ_COMPLETION_DIAGNOSTIC="command completion marker has an invalid shape"
        return 2
    fi
    READ_COMPLETION_EXIT_CODE="$((10#${completion_lines[0]}))"
    if (( READ_COMPLETION_EXIT_CODE > 255 )); then
        READ_COMPLETION_EXIT_CODE=""
        READ_COMPLETION_DIAGNOSTIC="command completion marker exit code is outside 0 through 255"
        return 2
    fi
}

wait_for_supervised_process_bounded() {
    local process_id="$1"
    local timeout_seconds_value="$2"
    local process_group_id="$3"
    local expected_session_id="$4"
    local expected_start_ticks="$5"
    local expected_parent_pid="$6"
    local expected_command_line="$7"
    local completion_path="$8"
    local poll_milliseconds_value=250
    local maximum_polls=$((timeout_seconds_value * 1000 / poll_milliseconds_value + 1))
    local poll completion_status completion_exit=125

    LAST_WAIT_CLEANUP_INCOMPLETE=false
    LAST_WAIT_CLEANUP_ATTEMPTED=false
    LAST_WAIT_CLEANUP_DIAGNOSTIC=""
    for ((poll = 0; poll < maximum_polls; poll++)); do
        if read_completion_exit_code "$completion_path"; then
            completion_status=0
            completion_exit="$READ_COMPLETION_EXIT_CODE"
        else
            completion_status=$?
        fi
        if (( completion_status == 0 || completion_status == 2 )); then
            LAST_WAIT_CLEANUP_ATTEMPTED=true
            if ! stop_recorded_process_group \
                "$process_id" "$process_group_id" \
                "$expected_start_ticks" "$expected_parent_pid" "$expected_command_line" "$expected_session_id"; then
                LAST_WAIT_CLEANUP_INCOMPLETE=true
                LAST_WAIT_CLEANUP_DIAGNOSTIC="${LAST_STOP_DIAGNOSTIC:-supervisor ownership could not be reconfirmed}"
            fi
            if ! rm -f -- "$completion_path"; then
                LAST_WAIT_CLEANUP_INCOMPLETE=true
                if [[ -n "$LAST_WAIT_CLEANUP_DIAGNOSTIC" ]]; then
                    LAST_WAIT_CLEANUP_DIAGNOSTIC+="; "
                fi
                LAST_WAIT_CLEANUP_DIAGNOSTIC+="command completion marker could not be removed"
            fi
            if (( completion_status == 2 )); then
                LAST_WAIT_CLEANUP_INCOMPLETE=true
                if [[ -n "$LAST_WAIT_CLEANUP_DIAGNOSTIC" ]]; then
                    LAST_WAIT_CLEANUP_DIAGNOSTIC+="; "
                fi
                LAST_WAIT_CLEANUP_DIAGNOSTIC+="$READ_COMPLETION_DIAGNOSTIC"
                return 125
            fi
            return "$completion_exit"
        fi
        if ! pid_is_running "$process_id"; then
            LAST_WAIT_CLEANUP_ATTEMPTED=true
            LAST_WAIT_CLEANUP_INCOMPLETE=true
            LAST_WAIT_CLEANUP_DIAGNOSTIC="supervisor exited before publishing a valid completion marker"
            wait "$process_id" 2>/dev/null || true
            return 125
        fi
        sleep 0.25
    done

    LAST_WAIT_CLEANUP_ATTEMPTED=true
    if ! stop_recorded_process_group \
        "$process_id" "$process_group_id" \
        "$expected_start_ticks" "$expected_parent_pid" "$expected_command_line" "$expected_session_id"; then
        LAST_WAIT_CLEANUP_INCOMPLETE=true
        LAST_WAIT_CLEANUP_DIAGNOSTIC="${LAST_STOP_DIAGNOSTIC:-timed-out supervisor ownership could not be reconfirmed}"
    fi
    return 124
}

process_group_is_running() {
    local process_group_id="$1"
    [[ "$process_group_id" =~ ^[1-9][0-9]*$ ]] || return 1
    kill -0 -- "-$process_group_id" 2>/dev/null
}

owned_process_group_has_live_members() {
    local process_group_id="$1"
    local expected_session_id="$2"
    local deadline_microseconds="$3"
    local maximum_entries=32768
    local entries=0 stat_path stat_line stat_tail current_microseconds
    local -a stat_fields=()

    PROCESS_GROUP_OBSERVATION_DIAGNOSTIC=""
    if [[ ! "$process_group_id" =~ ^[1-9][0-9]*$ ||
          ! "$expected_session_id" =~ ^[1-9][0-9]*$ ||
          ! "$deadline_microseconds" =~ ^[1-9][0-9]*$ ]]; then
        PROCESS_GROUP_OBSERVATION_DIAGNOSTIC="process-group observation received an invalid PGID, SID, or deadline"
        return 2
    fi
    for stat_path in /proc/[1-9]*/stat; do
        entries=$((entries + 1))
        if ! read_monotonic_microseconds; then
            PROCESS_GROUP_OBSERVATION_DIAGNOSTIC="$BOUNDARY_DIAGNOSTIC"
            return 2
        fi
        current_microseconds="$MONOTONIC_MICROSECONDS"
        if (( current_microseconds >= deadline_microseconds )); then
            PROCESS_GROUP_OBSERVATION_DIAGNOSTIC="process-group observation reached its shared one-second deadline"
            return 3
        fi
        if (( entries > maximum_entries )); then
            PROCESS_GROUP_OBSERVATION_DIAGNOSTIC="process-group observation exceeded its item bound"
            return 2
        fi
        if ! IFS= read -r stat_line 2>/dev/null <"$stat_path"; then
            if [[ -e "$stat_path" ]]; then
                PROCESS_GROUP_OBSERVATION_DIAGNOSTIC="process-group observation could not read $stat_path"
                return 2
            fi
            continue
        fi
        if [[ "$stat_line" != *") "* ]]; then
            if [[ -e "$stat_path" ]]; then
                PROCESS_GROUP_OBSERVATION_DIAGNOSTIC="process-group observation found malformed state in $stat_path"
                return 2
            fi
            continue
        fi
        stat_tail="${stat_line##*) }"
        read -r -a stat_fields <<<"$stat_tail"
        if (( ${#stat_fields[@]} < 4 )); then
            if [[ -e "$stat_path" ]]; then
                PROCESS_GROUP_OBSERVATION_DIAGNOSTIC="process-group observation found incomplete state in $stat_path"
                return 2
            fi
            continue
        fi
        if [[ "${stat_fields[2]}" == "$process_group_id" &&
              "${stat_fields[3]}" == "$expected_session_id" &&
              "${stat_fields[0]}" != Z && "${stat_fields[0]}" != X ]]; then
            return 0
        fi
    done
    return 1
}

signal_owned_process_group() {
    local process_id="$1"
    local process_group_id="$2"
    local expected_start_ticks="$3"
    local expected_parent_pid="$4"
    local expected_command_line="$5"
    local expected_session_id="$6"
    local signal_name="$7"
    local shell_process_group

    # The stable supervisor is both process-group and session leader. Requiring
    # all three IDs plus its full /proc identity prevents a stale or corrupted
    # record from signaling an unrelated process group.
    PROCESS_IDENTITY_DIAGNOSTIC=""
    if [[ ! "$process_id" =~ ^[1-9][0-9]*$ ||
          "$process_group_id" != "$process_id" ||
          "$expected_session_id" != "$process_id" ]]; then
        PROCESS_IDENTITY_DIAGNOSTIC="refused to signal process group $process_group_id because its ownership record is invalid"
        return 1
    fi
    shell_process_group="$(ps -o pgid= -p "$$" 2>/dev/null | tr -d '[:space:]' || true)"
    if [[ -z "$shell_process_group" || "$process_group_id" == "$shell_process_group" ]]; then
        PROCESS_IDENTITY_DIAGNOSTIC="refused to signal process group $process_group_id because it is not isolated from the probe shell"
        return 1
    fi
    process_identity_matches "$process_id" "$expected_start_ticks" "$expected_parent_pid" "$expected_command_line" || return 1
    if [[ "$OBSERVED_PROCESS_GROUP_ID" != "$process_group_id" ]]; then
        PROCESS_IDENTITY_DIAGNOSTIC="refused to signal process group $process_group_id because the recorded process is no longer its leader"
        return 1
    fi
    if [[ "$OBSERVED_PROCESS_SESSION_ID" != "$expected_session_id" ]]; then
        PROCESS_IDENTITY_DIAGNOSTIC="refused to signal process group $process_group_id because the recorded process is no longer its session leader"
        return 1
    fi
    kill "-$signal_name" -- "-$process_group_id" 2>/dev/null
}

observe_recorded_pid_state() {
    local process_id="$1"
    local expected_start_ticks="$2"
    local stat_line stat_tail
    local -a stat_fields=()

    PID_OBSERVATION_DIAGNOSTIC=""
    if [[ ! "$process_id" =~ ^[1-9][0-9]*$ || ! "$expected_start_ticks" =~ ^[0-9]+$ ]]; then
        PID_OBSERVATION_DIAGNOSTIC="PID observation received an invalid identity"
        return 2
    fi
    if [[ ! -e "/proc/$process_id/stat" ]]; then
        return 1
    fi
    if ! IFS= read -r stat_line 2>/dev/null <"/proc/$process_id/stat"; then
        if [[ ! -e "/proc/$process_id/stat" ]]; then
            return 1
        fi
        PID_OBSERVATION_DIAGNOSTIC="could not read state for recorded PID $process_id"
        return 2
    fi
    if [[ "$stat_line" != *") "* ]]; then
        PID_OBSERVATION_DIAGNOSTIC="recorded PID $process_id exposed malformed process state"
        return 2
    fi
    stat_tail="${stat_line##*) }"
    read -r -a stat_fields <<<"$stat_tail"
    if (( ${#stat_fields[@]} < 20 )); then
        PID_OBSERVATION_DIAGNOSTIC="recorded PID $process_id exposed incomplete process state"
        return 2
    fi
    if [[ "${stat_fields[19]}" != "$expected_start_ticks" ]]; then
        PID_OBSERVATION_DIAGNOSTIC="recorded PID $process_id no longer has the expected start time"
        return 2
    fi
    if [[ "${stat_fields[0]}" == Z || "${stat_fields[0]}" == X ]]; then
        return 1
    fi
    return 0
}

wait_for_recorded_pid_terminal_bounded() {
    local process_id="$1"
    local expected_start_ticks="$2"
    local maximum_observations=20
    local observation deadline_microseconds observation_status sleep_status

    PID_OBSERVATION_DIAGNOSTIC=""
    if ! read_monotonic_microseconds; then
        PID_OBSERVATION_DIAGNOSTIC="$BOUNDARY_DIAGNOSTIC"
        return 2
    fi
    deadline_microseconds=$((MONOTONIC_MICROSECONDS + 1000000))
    for ((observation = 0; observation < maximum_observations; observation++)); do
        if observe_recorded_pid_state "$process_id" "$expected_start_ticks"; then
            observation_status=0
        else
            observation_status=$?
            if (( observation_status == 1 )); then
                reap_recorded_pid_if_terminal "$process_id" || true
                return 0
            fi
            return 2
        fi
        if (( observation < maximum_observations - 1 )); then
            if sleep_until_deadline "$deadline_microseconds"; then
                :
            else
                sleep_status=$?
                if (( sleep_status == 2 )); then
                    PID_OBSERVATION_DIAGNOSTIC="$BOUNDARY_DIAGNOSTIC"
                    return 2
                fi
                break
            fi
        fi
    done
    PID_OBSERVATION_DIAGNOSTIC="recorded PID $process_id remained live through its bounded one-second observation"
    return 1
}

stop_recorded_pid() {
    local process_id="$1"
    local expected_start_ticks="${2:-}"
    local expected_parent_pid="${3:-}"
    local expected_command_line="${4:-}"
    local observation_status signal_latched=false

    [[ -n "$process_id" ]] || return 0
    LAST_STOP_DIAGNOSTIC=""
    if [[ -n "$PID_SIGNAL_SENT_PROCESS_ID" ]]; then
        if [[ "$PID_SIGNAL_SENT_PROCESS_ID" != "$process_id" ||
              "$PID_SIGNAL_SENT_START_TICKS" != "$expected_start_ticks" ||
              "$PID_SIGNAL_SENT_PARENT_PID" != "$expected_parent_pid" ||
              "$PID_SIGNAL_SENT_COMMAND_LINE" != "$expected_command_line" ]]; then
            LAST_STOP_DIAGNOSTIC="refused to clean recorded PID $process_id because an unresolved signal latch belongs to a different process identity"
            return 1
        fi
        signal_latched=true
    fi

    # A failed cleanup remains latched across EXIT-trap re-entry. Re-observe
    # that exact identity once under the normal bound, but never signal it a
    # second time merely because the caller is retrying cleanup.
    if [[ "$signal_latched" == true ]]; then
        if wait_for_recorded_pid_terminal_bounded "$process_id" "$expected_start_ticks"; then
            PID_SIGNAL_SENT_PROCESS_ID=""
            PID_SIGNAL_SENT_START_TICKS=""
            PID_SIGNAL_SENT_PARENT_PID=""
            PID_SIGNAL_SENT_COMMAND_LINE=""
            PID_SIGNAL_SENT_NAME=""
            return 0
        fi
        LAST_STOP_DIAGNOSTIC="${PID_OBSERVATION_DIAGNOSTIC:-recorded PID $process_id could not be observed safely}; refused to send another signal after the recorded $PID_SIGNAL_SENT_NAME"
        return 1
    fi

    if observe_recorded_pid_state "$process_id" "$expected_start_ticks"; then
        observation_status=0
    else
        observation_status=$?
        if (( observation_status == 1 )); then
            reap_recorded_pid_if_terminal "$process_id" || true
            return 0
        fi
        LAST_STOP_DIAGNOSTIC="$PID_OBSERVATION_DIAGNOSTIC"
        return 1
    fi

    if ! signal_owned_pid "$process_id" "$expected_start_ticks" "$expected_parent_pid" "$expected_command_line" TERM; then
        LAST_STOP_DIAGNOSTIC="${PROCESS_IDENTITY_DIAGNOSTIC:-could not send TERM to recorded PID $process_id}"
        return 1
    fi
    PID_SIGNAL_SENT_PROCESS_ID="$process_id"
    PID_SIGNAL_SENT_START_TICKS="$expected_start_ticks"
    PID_SIGNAL_SENT_PARENT_PID="$expected_parent_pid"
    PID_SIGNAL_SENT_COMMAND_LINE="$expected_command_line"
    PID_SIGNAL_SENT_NAME=TERM
    if wait_for_recorded_pid_terminal_bounded "$process_id" "$expected_start_ticks"; then
        PID_SIGNAL_SENT_PROCESS_ID=""
        PID_SIGNAL_SENT_START_TICKS=""
        PID_SIGNAL_SENT_PARENT_PID=""
        PID_SIGNAL_SENT_COMMAND_LINE=""
        PID_SIGNAL_SENT_NAME=""
        return 0
    else
        observation_status=$?
        if (( observation_status == 2 )); then
            LAST_STOP_DIAGNOSTIC="$PID_OBSERVATION_DIAGNOSTIC"
            return 1
        fi
    fi

    if ! signal_owned_pid "$process_id" "$expected_start_ticks" "$expected_parent_pid" "$expected_command_line" KILL; then
        LAST_STOP_DIAGNOSTIC="${PROCESS_IDENTITY_DIAGNOSTIC:-could not send KILL to recorded PID $process_id}"
        return 1
    fi
    PID_SIGNAL_SENT_NAME=KILL
    if ! wait_for_recorded_pid_terminal_bounded "$process_id" "$expected_start_ticks"; then
        LAST_STOP_DIAGNOSTIC="$PID_OBSERVATION_DIAGNOSTIC"
        return 1
    fi
    PID_SIGNAL_SENT_PROCESS_ID=""
    PID_SIGNAL_SENT_START_TICKS=""
    PID_SIGNAL_SENT_PARENT_PID=""
    PID_SIGNAL_SENT_COMMAND_LINE=""
    PID_SIGNAL_SENT_NAME=""
}

reap_recorded_pid_if_terminal() {
    local process_id="$1"
    local stat_line stat_tail
    local -a stat_fields=()

    [[ "$process_id" =~ ^[1-9][0-9]*$ ]] || return 1
    if [[ ! -e "/proc/$process_id/stat" ]]; then
        wait "$process_id" 2>/dev/null || true
        return 0
    fi
    if ! IFS= read -r stat_line <"/proc/$process_id/stat"; then
        return 0
    fi
    [[ "$stat_line" == *") "* ]] || return 0
    stat_tail="${stat_line##*) }"
    read -r -a stat_fields <<<"$stat_tail"
    if (( ${#stat_fields[@]} >= 1 )) && [[ "${stat_fields[0]}" == Z || "${stat_fields[0]}" == X ]]; then
        wait "$process_id" 2>/dev/null || true
    fi
}

stop_recorded_process_group() {
    local process_id="$1"
    local process_group_id="$2"
    local expected_start_ticks="${3:-}"
    local expected_parent_pid="${4:-}"
    local expected_command_line="${5:-}"
    local expected_session_id="${6:-}"
    local poll observation_status group_cleared=false kill_already_sent=false
    local current_microseconds deadline_microseconds

    [[ -n "$process_id" ]] || return 0
    LAST_STOP_DIAGNOSTIC=""
    if [[ -z "$process_group_id" && -z "$expected_session_id" ]]; then
        stop_recorded_pid "$process_id" "$expected_start_ticks" "$expected_parent_pid" "$expected_command_line"
        return "$?"
    fi
    if [[ -z "$process_group_id" || -z "$expected_session_id" ||
          "$process_group_id" != "$process_id" || "$expected_session_id" != "$process_id" ]]; then
        LAST_STOP_DIAGNOSTIC="refused to clean a process group because its supervisor PID, PGID, or SID record is incomplete"
        return 1
    fi
    if [[ "$KILL_SENT_PROCESS_ID" == "$process_id" &&
          "$KILL_SENT_PROCESS_GROUP_ID" == "$process_group_id" &&
          "$KILL_SENT_SESSION_ID" == "$expected_session_id" &&
          "$KILL_SENT_START_TICKS" == "$expected_start_ticks" &&
          "$KILL_SENT_PARENT_PID" == "$expected_parent_pid" &&
          "$KILL_SENT_COMMAND_LINE" == "$expected_command_line" ]]; then
        kill_already_sent=true
    elif process_group_is_running "$process_group_id"; then
        # One identity-checked KILL avoids a leader-exit race between TERM and
        # KILL. GNU timeout already provides the graceful TERM/KILL sequence for
        # its command; this path is the final task-owned containment cleanup.
        if ! signal_owned_process_group \
            "$process_id" "$process_group_id" \
            "$expected_start_ticks" "$expected_parent_pid" "$expected_command_line" "$expected_session_id" KILL; then
            LAST_STOP_DIAGNOSTIC="${PROCESS_IDENTITY_DIAGNOSTIC:-could not send KILL to recorded process group $process_group_id}"
            return 1
        fi
        KILL_SENT_PROCESS_ID="$process_id"
        KILL_SENT_PROCESS_GROUP_ID="$process_group_id"
        KILL_SENT_SESSION_ID="$expected_session_id"
        KILL_SENT_START_TICKS="$expected_start_ticks"
        KILL_SENT_PARENT_PID="$expected_parent_pid"
        KILL_SENT_COMMAND_LINE="$expected_command_line"
        kill_already_sent=true
    else
        group_cleared=true
    fi

    if [[ "$kill_already_sent" == true && "$group_cleared" != true ]]; then
        if ! read_monotonic_microseconds; then
            LAST_STOP_DIAGNOSTIC="$BOUNDARY_DIAGNOSTIC"
            return 1
        fi
        current_microseconds="$MONOTONIC_MICROSECONDS"
        deadline_microseconds=$((current_microseconds + 1000000))
        # KILL was sent at most once. Observe for at most 20 * 50 ms so a
        # zombie-only group is not mistaken for a live cleanup failure.
        for ((poll = 0; poll < 20; poll++)); do
            if ! process_group_is_running "$process_group_id"; then
                group_cleared=true
                break
            fi
            if owned_process_group_has_live_members \
                "$process_group_id" "$expected_session_id" "$deadline_microseconds"; then
                observation_status=0
            else
                observation_status=$?
                if (( observation_status == 1 )); then
                    group_cleared=true
                    break
                fi
                if (( observation_status == 2 )); then
                    LAST_STOP_DIAGNOSTIC="${PROCESS_GROUP_OBSERVATION_DIAGNOSTIC:-process-group state could not be observed safely}"
                    return 1
                fi
                break
            fi
            if (( poll < 19 )); then
                if sleep_until_deadline "$deadline_microseconds"; then
                    :
                else
                    observation_status=$?
                    if (( observation_status == 2 )); then
                        LAST_STOP_DIAGNOSTIC="$BOUNDARY_DIAGNOSTIC"
                        return 1
                    fi
                    break
                fi
            fi
        done
    fi
    reap_recorded_pid_if_terminal "$process_id" || true
    if [[ "$group_cleared" != true ]]; then
        LAST_STOP_DIAGNOSTIC="recorded process group $process_group_id retained a live member or could not be observed before the shared deadline"
        return 1
    fi
}

run_bounded_capture() {
    local timeout_value="$1"
    local log_path="$2"
    shift 2

    local started_epoch ended_epoch process_id process_group_id process_session_id command_line exit_code
    local process_start_ticks="" process_parent_pid="" process_identity_command_line=""
    local command_wait_cleanup_attempted=false command_wait_cleanup_incomplete=false command_wait_cleanup_diagnostic=""
    local completion_path supervisor_hold_seconds supervisor_script timeout_path
    local logger_pid logger_exit marker_path marker_value marker_read_exit fifo_path logger_started_epoch logger_ended_epoch
    local logger_start_ticks="" logger_parent_pid="" logger_command_line=""
    local logger_wait_cleanup_incomplete=false logger_wait_cleanup_diagnostic=""
    started_epoch="$(epoch_milliseconds)"
    command_line="$(format_command_line "$@")"

    if [[ -z "$probe_temp" || ! -d "$probe_temp" ]]; then
        printf 'bounded capture requires an initialized probe temporary directory\n' >&2
        LAST_EXIT_CODE=125
        LAST_TERMINATION="failed-to-start"
        LAST_CLEANUP_ATTEMPTED=false
        LAST_CLEANUP_INCOMPLETE=true
        LAST_CLEANUP_DIAGNOSTIC="probe temporary directory is unavailable"
        return 125
    fi

    capture_sequence=$((capture_sequence + 1))
    fifo_path="$probe_temp/capture-$capture_sequence.fifo"
    marker_path="$probe_temp/capture-$capture_sequence.truncated"
    completion_path="$probe_temp/capture-$capture_sequence.command-status"
    active_capture_fifo="$fifo_path"
    active_capture_marker="$marker_path"
    if ! : >"$marker_path"; then
        active_capture_marker=""
        LAST_EXIT_CODE=125
        LAST_TERMINATION="failed-to-start"
        LAST_CLEANUP_ATTEMPTED=false
        LAST_CLEANUP_INCOMPLETE=true
        LAST_CLEANUP_DIAGNOSTIC="could not create bounded log capture marker"
        return 125
    fi
    if ! mkfifo -- "$fifo_path"; then
        active_capture_fifo=""
        active_capture_marker=""
        LAST_EXIT_CODE=125
        LAST_TERMINATION="failed-to-start"
        LAST_CLEANUP_ATTEMPTED=false
        LAST_CLEANUP_INCOMPLETE=true
        LAST_CLEANUP_DIAGNOSTIC="could not create bounded log capture FIFO"
        return 125
    fi

    # The logger has its own process and drains all remaining bytes after the
    # retained prefix. This isolates the byte limit from the publish process,
    # whose generated artifacts must not inherit a file-size resource limit.
    logger_started_epoch="$(epoch_milliseconds)"
    (
        set +e
        export LC_ALL=C
        head -c "$maximum_log_bytes" >"$log_path"
        head_exit=$?
        extra=""
        IFS= read -r -N 1 extra
        read_exit=$?
        if (( read_exit == 0 )); then
            marker_value=true
        elif (( read_exit == 1 )) && [[ -z "$extra" ]]; then
            marker_value=false
        else
            marker_value=error
        fi
        marker_exit=0
        printf '%s\n' "$marker_value" >"$marker_path" || marker_exit=$?
        cat_exit=0
        cat >/dev/null || cat_exit=$?
        if (( head_exit != 0 )); then
            exit "$head_exit"
        fi
        if [[ "$marker_value" == error ]]; then
            exit 121
        fi
        if (( marker_exit != 0 )); then
            exit 122
        fi
        if (( cat_exit != 0 )); then
            exit 123
        fi
        exit 0
    ) <"$fifo_path" &
    logger_pid=$!
    active_capture_pid="$logger_pid"
    if read_process_identity "$logger_pid"; then
        logger_start_ticks="$OBSERVED_PROCESS_START_TICKS"
        logger_parent_pid="$OBSERVED_PROCESS_PARENT_PID"
        logger_command_line="$OBSERVED_PROCESS_COMMAND_LINE"
    fi
    active_capture_start_ticks="$logger_start_ticks"
    active_capture_parent_pid="$logger_parent_pid"
    active_capture_command_line="$logger_command_line"

    timeout_path="$(command -v timeout 2>/dev/null || true)"
    supervisor_hold_seconds=$((timeout_value + capture_wait_timeout_seconds + 5))
    supervisor_script='
completion_path="$1"
hold_seconds="$2"
timeout_path="$3"
timeout_value="$4"
shift 4
set +e
"$timeout_path" --foreground --signal=TERM --kill-after=5s "${timeout_value}s" "$@"
command_exit=$?
completion_temp="${completion_path}.${BASHPID}.tmp"
completion_written=false
if printf "%s\n" "$command_exit" >"$completion_temp"; then
    if mv -f -- "$completion_temp" "$completion_path"; then
        completion_written=true
    fi
fi
if [[ "$completion_written" != true ]]; then
    rm -f -- "$completion_temp" 2>/dev/null || true
    printf "supervisor could not publish the command completion marker\n" >&2
fi
# The owner terminates this stable PID=PGID=SID anchor after consuming the
# marker. This bounded hold keeps group ownership provable even when the
# timeout child exits while another member of its group remains alive.
sleep "$hold_seconds"
exit 125
'

    set +e
    setsid bash -c "$supervisor_script" bash \
        "$completion_path" "$supervisor_hold_seconds" "$timeout_path" "$timeout_value" "$@" \
        >"$fifo_path" 2>&1 &
    process_id=$!
    active_command_pid="$process_id"
    active_command_process_group=""
    active_command_session_id=""
    active_command_start_ticks=""
    active_command_parent_pid=""
    active_command_identity_command_line=""
    active_command_completion_path="$completion_path"
    # setsid makes the stable Bash supervisor both group and session leader;
    # timeout --foreground and the target stay inside that owned containment.
    process_group_id=""
    process_session_id=""
    if read_process_identity_stabilized "$process_id" bash "$process_id" "$process_id"; then
        process_start_ticks="$OBSERVED_PROCESS_START_TICKS"
        process_parent_pid="$OBSERVED_PROCESS_PARENT_PID"
        process_group_id="$OBSERVED_PROCESS_GROUP_ID"
        process_session_id="$OBSERVED_PROCESS_SESSION_ID"
        process_identity_command_line="$OBSERVED_PROCESS_COMMAND_LINE"
    fi
    active_command_pid="$process_id"
    active_command_process_group="$process_group_id"
    active_command_session_id="$process_session_id"
    active_command_start_ticks="$process_start_ticks"
    active_command_parent_pid="$process_parent_pid"
    active_command_identity_command_line="$process_identity_command_line"
    wait_for_supervised_process_bounded \
        "$process_id" "$((timeout_value + capture_wait_timeout_seconds))" \
        "$process_group_id" "$process_session_id" \
        "$process_start_ticks" "$process_parent_pid" "$process_identity_command_line" \
        "$completion_path"
    exit_code=$?
    command_wait_cleanup_attempted="$LAST_WAIT_CLEANUP_ATTEMPTED"
    command_wait_cleanup_incomplete="$LAST_WAIT_CLEANUP_INCOMPLETE"
    command_wait_cleanup_diagnostic="$LAST_WAIT_CLEANUP_DIAGNOSTIC"
    set -e

    logger_exit=0
    wait_for_pid_bounded \
        "$logger_pid" "$capture_wait_timeout_seconds" "" \
        "$logger_start_ticks" "$logger_parent_pid" "$logger_command_line" || logger_exit=$?
    logger_wait_cleanup_incomplete="$LAST_WAIT_CLEANUP_INCOMPLETE"
    logger_wait_cleanup_diagnostic="$LAST_WAIT_CLEANUP_DIAGNOSTIC"
    logger_ended_epoch="$(epoch_milliseconds)"
    if ! pid_is_running "$logger_pid"; then
        active_capture_pid=""
        active_capture_start_ticks=""
        active_capture_parent_pid=""
        active_capture_command_line=""
    fi
    if rm -f -- "$fifo_path"; then
        active_capture_fifo=""
    else
        logger_wait_cleanup_incomplete=true
        if [[ -n "$logger_wait_cleanup_diagnostic" ]]; then
            logger_wait_cleanup_diagnostic+="; "
        fi
        logger_wait_cleanup_diagnostic+="bounded log capture FIFO could not be removed"
    fi

    ended_epoch="$(epoch_milliseconds)"
    LAST_PID="$process_id"
    LAST_PARENT_PID="$$"
    LAST_STARTED_EPOCH="$started_epoch"
    LAST_COMMAND_LINE="$command_line"
    LAST_ELAPSED_MS=$((ended_epoch - started_epoch))
    LAST_EXIT_CODE="$exit_code"
    LAST_TERMINATION="exited"
    LAST_CLEANUP_ATTEMPTED="$command_wait_cleanup_attempted"
    LAST_CLEANUP_INCOMPLETE="$command_wait_cleanup_incomplete"
    LAST_CLEANUP_DIAGNOSTIC="$command_wait_cleanup_diagnostic"
    LAST_OUTPUT_TRUNCATED=false
    LAST_LOG_CAPTURE_PID="$logger_pid"
    LAST_LOG_CAPTURE_PARENT_PID="$$"
    LAST_LOG_CAPTURE_STARTED_EPOCH="$logger_started_epoch"
    LAST_LOG_CAPTURE_ELAPSED_MS=$((logger_ended_epoch - logger_started_epoch))
    LAST_LOG_CAPTURE_EXIT_CODE="$logger_exit"
    LAST_LOG_CAPTURE_CLEANUP_INCOMPLETE="$logger_wait_cleanup_incomplete"
    LAST_LOG_CAPTURE_DIAGNOSTIC="$logger_wait_cleanup_diagnostic"

    marker_value=""
    marker_read_exit=0
    if IFS= read -r marker_value <"$marker_path"; then
        :
    else
        marker_read_exit=$?
    fi
    if (( marker_read_exit != 0 )); then
        LAST_LOG_CAPTURE_CLEANUP_INCOMPLETE=true
        if [[ -n "$LAST_LOG_CAPTURE_DIAGNOSTIC" ]]; then
            LAST_LOG_CAPTURE_DIAGNOSTIC+="; "
        fi
        LAST_LOG_CAPTURE_DIAGNOSTIC+="bounded log capture marker could not be read"
    elif [[ "$marker_value" == true ]]; then
        LAST_OUTPUT_TRUNCATED=true
        if [[ -n "$LAST_LOG_CAPTURE_DIAGNOSTIC" ]]; then
            LAST_LOG_CAPTURE_DIAGNOSTIC+="; "
        fi
        LAST_LOG_CAPTURE_DIAGNOSTIC+="captured output exceeded ${maximum_log_bytes} bytes"
    elif [[ "$marker_value" != false ]]; then
        LAST_LOG_CAPTURE_CLEANUP_INCOMPLETE=true
        if [[ -n "$LAST_LOG_CAPTURE_DIAGNOSTIC" ]]; then
            LAST_LOG_CAPTURE_DIAGNOSTIC+="; "
        fi
        LAST_LOG_CAPTURE_DIAGNOSTIC+="bounded log capture marker has an invalid terminal state"
    fi
    if (( logger_exit != 0 )); then
        LAST_LOG_CAPTURE_CLEANUP_INCOMPLETE=true
        if [[ -n "$LAST_LOG_CAPTURE_DIAGNOSTIC" ]]; then
            LAST_LOG_CAPTURE_DIAGNOSTIC+="; "
        fi
        LAST_LOG_CAPTURE_DIAGNOSTIC+="bounded log capture exited with code $logger_exit"
    fi
    if (( logger_exit == 124 )); then
        LAST_LOG_CAPTURE_CLEANUP_INCOMPLETE=true
        if [[ -n "$LAST_LOG_CAPTURE_DIAGNOSTIC" ]]; then
            LAST_LOG_CAPTURE_DIAGNOSTIC+="; "
        fi
        LAST_LOG_CAPTURE_DIAGNOSTIC+="bounded log capture did not exit within ${capture_wait_timeout_seconds}s"
    fi
    if rm -f -- "$marker_path"; then
        active_capture_marker=""
    else
        LAST_LOG_CAPTURE_CLEANUP_INCOMPLETE=true
        if [[ -n "$LAST_LOG_CAPTURE_DIAGNOSTIC" ]]; then
            LAST_LOG_CAPTURE_DIAGNOSTIC+="; "
        fi
        LAST_LOG_CAPTURE_DIAGNOSTIC+="bounded log capture marker could not be removed"
    fi

    if (( exit_code == 124 || exit_code == 137 )); then
        LAST_TERMINATION="timed-out"
        LAST_CLEANUP_ATTEMPTED=true
        if [[ -n "$LAST_CLEANUP_DIAGNOSTIC" ]]; then
            LAST_CLEANUP_DIAGNOSTIC+="; "
        fi
        LAST_CLEANUP_DIAGNOSTIC+="bounded command returned a timeout termination code"
    elif (( exit_code >= 128 )); then
        LAST_TERMINATION="signaled"
        LAST_CLEANUP_ATTEMPTED=true
        if [[ -n "$LAST_CLEANUP_DIAGNOSTIC" ]]; then
            LAST_CLEANUP_DIAGNOSTIC+="; "
        fi
        LAST_CLEANUP_DIAGNOSTIC+="bounded command terminated by signal $((exit_code - 128))"
    fi

    if ! pid_is_running "$process_id" && ! process_group_is_running "$process_group_id"; then
        active_command_pid=""
        active_command_process_group=""
        active_command_session_id=""
        active_command_start_ticks=""
        active_command_parent_pid=""
        active_command_identity_command_line=""
        active_command_completion_path=""
    fi

    if (( logger_exit != 0 )); then
        LAST_CLEANUP_ATTEMPTED=true
        LAST_CLEANUP_INCOMPLETE=true
        if [[ -n "$LAST_CLEANUP_DIAGNOSTIC" ]]; then
            LAST_CLEANUP_DIAGNOSTIC+="; "
        fi
        LAST_CLEANUP_DIAGNOSTIC+="$LAST_LOG_CAPTURE_DIAGNOSTIC"
    fi
}

json_escape() {
    local LC_ALL=C
    local value="$1"
    local escaped=""
    local character
    local code
    local index
    local length=${#value}
    local maximum_json_escape_bytes=1048576

    # Keep evidence generation bounded even if a diagnostic escapes the
    # bounded process-log capture. All expected fields are far below this
    # limit; the marker only applies to an unexpected oversized value.
    if (( length > maximum_json_escape_bytes )); then
        length=$maximum_json_escape_bytes
    fi
    for ((index = 0; index < length; index++)); do
        character="${value:index:1}"
        case "$character" in
            \\) escaped+='\\' ;;
            '"') escaped+='\"' ;;
            $'\b') escaped+='\b' ;;
            $'\f') escaped+='\f' ;;
            $'\n') escaped+='\n' ;;
            $'\r') escaped+='\r' ;;
            $'\t') escaped+='\t' ;;
            *)
                if printf -v code '%d' "'$character" && [[ "$code" =~ ^[0-9]+$ ]] && (( code < 0x20 )); then
                    printf -v character '\\u%04x' "$code"
                fi
                escaped+="$character"
                ;;
        esac
    done
    if (( length < ${#value} )); then
        escaped+='\u2026'
    fi
    printf '%s' "$escaped"
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
    local temporary_path
    local write_status=0
    [[ "$output_ready" == true && -n "$evidence_path" ]] || return 0
    # Never modify a directory that was non-empty on entry.
    [[ "$output_preexisting_nonempty" == true ]] && return 0

    temporary_path="${evidence_path}.${BASHPID}.tmp"
    evidence_temp_path="$temporary_path"
    if cat >"$temporary_path" <<EOF
{
  "status": "$(json_escape "$status")",
  "reason": "$(json_escape "$reason")",
  "schemaVersion": 1,
  "platform": "linux-x64",
  "expectedExitCode": 0,
  "expectedStdout": "Hello from Rust#\n",
  "timeoutSeconds": ${timeout_seconds_json},
  "maximumLogBytes": ${maximum_log_bytes},
  "publishOutputTruncated": $publish_output_truncated,
  "runOutputTruncated": $run_output_truncated,
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
    "cleanupDiagnostic": $(json_string_or_null "$publish_cleanup_diagnostic"),
    "logCapture": {
      "pid": ${publish_log_capture_pid:-null},
      "parentPid": ${publish_log_capture_parent_pid:-null},
      "startedAtEpochMilliseconds": ${publish_log_capture_started_epoch:-null},
      "elapsedMilliseconds": ${publish_log_capture_elapsed_ms:-null},
      "exitCode": ${publish_log_capture_exit_code:-null},
      "cleanupIncomplete": ${publish_log_capture_cleanup_incomplete:-false},
      "diagnostic": $(json_string_or_null "${publish_log_capture_diagnostic:-}")
    }
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
    "cleanupDiagnostic": $(json_string_or_null "$run_cleanup_diagnostic"),
    "logCapture": {
      "pid": ${run_log_capture_pid:-null},
      "parentPid": ${run_log_capture_parent_pid:-null},
      "startedAtEpochMilliseconds": ${run_log_capture_started_epoch:-null},
      "elapsedMilliseconds": ${run_log_capture_elapsed_ms:-null},
      "exitCode": ${run_log_capture_exit_code:-null},
      "cleanupIncomplete": ${run_log_capture_cleanup_incomplete:-false},
      "diagnostic": $(json_string_or_null "${run_log_capture_diagnostic:-}")
    }
  },
  "cleanup": {
    "temporaryDirectory": $(json_string_or_null "$probe_temp"),
    "attempted": $probe_cleanup_attempted,
    "incomplete": $probe_cleanup_incomplete,
    "diagnostic": $(json_string_or_null "$probe_cleanup_diagnostic")
  }
}
EOF
    then
        # Refuse to replace a path that appeared after the entry-time empty
        # directory check (for example, from a concurrent probe invocation).
        if mv -T -n -- "$temporary_path" "$evidence_path" && [[ ! -e "$temporary_path" ]]; then
            :
        else
            write_status=$?
            if (( write_status == 0 )); then
                write_status=1
            fi
        fi
    else
        write_status=$?
    fi
    rm -f -- "$temporary_path" 2>/dev/null || true
    evidence_temp_path=""
    return "$write_status"
}

append_probe_cleanup_diagnostic() {
    local diagnostic="$1"
    [[ -n "$diagnostic" ]] || return 0
    if [[ -n "$probe_cleanup_diagnostic" ]]; then
        probe_cleanup_diagnostic+="; "
    fi
    probe_cleanup_diagnostic+="$diagnostic"
}

on_exit() {
    local exit_code="$?"
    local cleanup_exit=0
    if [[ "$status" == "started" ]]; then
        status="failed"
        reason="script-exited-before-validation"
    fi
    # If an interrupt arrives while a capture is active, terminate only the
    # recorded identities owned by this invocation. Refuse to signal a reused
    # PID or process group, and preserve that refusal in the final evidence.
    if [[ -n "$active_command_pid" ]]; then
        probe_cleanup_attempted=true
        if ! stop_recorded_process_group \
            "$active_command_pid" "$active_command_process_group" \
            "$active_command_start_ticks" "$active_command_parent_pid" "$active_command_identity_command_line" \
            "$active_command_session_id"; then
            probe_cleanup_incomplete=true
            append_probe_cleanup_diagnostic "${LAST_STOP_DIAGNOSTIC:-active command cleanup could not confirm process ownership}"
        fi
    fi
    if [[ -n "$active_capture_pid" ]]; then
        probe_cleanup_attempted=true
        if ! stop_recorded_pid \
            "$active_capture_pid" \
            "$active_capture_start_ticks" "$active_capture_parent_pid" "$active_capture_command_line"; then
            probe_cleanup_incomplete=true
            append_probe_cleanup_diagnostic "${LAST_STOP_DIAGNOSTIC:-active log capture cleanup could not confirm process ownership}"
        fi
    fi

    # Determine temporary-directory cleanup before writing evidence so a
    # successful probe cannot hide leaked task-owned resources.
    if [[ -n "$probe_temp" && -d "$probe_temp" ]]; then
        probe_cleanup_attempted=true
        if [[ "$probe_temp" != "/" && "$probe_temp" == */rustsharp-linux-aot.?????? ]]; then
            if timeout --signal=TERM --kill-after=1s 5s rm -rf -- "$probe_temp"; then
                cleanup_exit=0
            else
                cleanup_exit=$?
                probe_cleanup_incomplete=true
                append_probe_cleanup_diagnostic "temporary directory cleanup exited with code $cleanup_exit"
            fi
            if [[ -d "$probe_temp" ]]; then
                probe_cleanup_incomplete=true
                append_probe_cleanup_diagnostic "temporary directory still exists after bounded cleanup"
            fi
        else
            probe_cleanup_incomplete=true
            append_probe_cleanup_diagnostic "refused to remove temporary directory because its recorded path failed the ownership check"
        fi
    fi

    if [[ "$probe_cleanup_incomplete" == true ]]; then
        if (( exit_code == 0 )); then
            exit_code=1
        fi
        if [[ "$status" == "passed" ]]; then
            status="failed"
            reason="probe-cleanup-failed"
        fi
    fi

    local evidence_failed=false
    if ! write_evidence "$exit_code"; then
        evidence_failed=true
        printf 'could not write Linux Native AOT evidence to %s\n' "$evidence_path" >&2
    fi
    # A successful probe without evidence is not a valid gate result. Preserve
    # an existing non-zero status for failed/blocked probes, but turn a green
    # result into a bounded failure when archival itself fails.
    if [[ "$evidence_failed" == true && "$exit_code" -eq 0 ]]; then
        exit_code=1
    fi
    if [[ -n "$evidence_temp_path" ]]; then
        rm -f -- "$evidence_temp_path" 2>/dev/null || true
    fi
    trap - EXIT
    exit "$exit_code"
}
trap on_exit EXIT

# Classify the host before resolving caller-provided paths. WSL and MSYS shells
# need an explicit translation for Windows absolute paths; treating such a path
# as POSIX text can create a mangled relative directory inside the repository.
host_system="$(uname -s 2>/dev/null || true)"
host_machine="$(uname -m 2>/dev/null || true)"
kernel="$(uname -srvm 2>/dev/null || true)"
kernel_lower="${kernel,,}"
is_wsl_host=false
if [[ -n "${WSL_INTEROP:-}" || -n "${WSL_DISTRO_NAME:-}" || "$kernel_lower" == *microsoft* || "$kernel_lower" == *wsl* ]]; then
    is_wsl_host=true
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

# Prepare the evidence location before environment checks so skipped probes
# still leave a machine-readable reason whenever the path is writable.
if [[ "$output_dir" =~ ^[[:alpha:]]: ]] &&
   [[ ! "$output_dir" =~ ^[[:alpha:]]:[\\/].* ]]; then
    status="failed"
    reason="ambiguous-windows-output-path"
    printf 'Windows output path lost its directory separators before reaching the probe: %s\n' "$output_dir" >&2
    exit 64
fi
if [[ "$output_dir" =~ ^[[:alpha:]]:[\\/].* ]]; then
    translated_output_dir=""
    if [[ "$is_wsl_host" == true ]]; then
        if ! command -v wslpath >/dev/null 2>&1 ||
           ! translated_output_dir="$(wslpath -u "$output_dir" 2>/dev/null)" ||
           [[ -z "$translated_output_dir" ]]; then
            status="failed"
            reason="output-path-translation-failed"
            printf 'could not translate Windows output path for WSL: %s\n' "$output_dir" >&2
            exit 64
        fi
    elif [[ "$host_system" == MINGW* || "$host_system" == MSYS* || "$host_system" == CYGWIN* ]]; then
        if ! command -v cygpath >/dev/null 2>&1 ||
           ! translated_output_dir="$(cygpath -u "$output_dir" 2>/dev/null)" ||
           [[ -z "$translated_output_dir" ]]; then
            status="failed"
            reason="output-path-translation-failed"
            printf 'could not translate Windows output path for MSYS/Cygwin: %s\n' "$output_dir" >&2
            exit 64
        fi
    else
        status="failed"
        reason="windows-output-path-on-posix-host"
        printf 'Windows output path is invalid on this POSIX host: %s\n' "$output_dir" >&2
        exit 64
    fi
    output_dir="$translated_output_dir"
fi
mkdir -p "$output_dir"
output_dir="$(realpath -m "$output_dir")"
evidence_path="$output_dir/linux-aot-evidence.json"
first_output_entry=""
if ! first_output_entry="$(find "$output_dir" -mindepth 1 -maxdepth 1 -print -quit)"; then
    status="failed"
    reason="output-directory-inspection-failed"
    printf 'could not inspect output directory: %s\n' "$output_dir" >&2
    exit 2
fi
if [[ -n "$first_output_entry" ]]; then
    output_preexisting_nonempty=true
    status="failed"
    reason="output-directory-not-empty"
    printf 'output directory must be empty: %s\n' "$output_dir" >&2
    exit 2
fi
output_ready=true

if [[ "$host_system" != "Linux" || "$host_machine" != "x86_64" ]]; then
    status="skipped"
    reason="native-linux-x86_64-required"
    printf 'P0-10 requires a native Linux x86_64 runner (found %s/%s)\n' "$host_system" "$host_machine" >&2
    exit 77
fi
if [[ "$is_wsl_host" == true ]]; then
    status="skipped"
    reason="native-linux-x86_64-required"
    printf 'P0-10 requires native Linux x86_64; WSL is not accepted as native evidence\n' >&2
    exit 77
fi

if [[ ! "$timeout_seconds" =~ ^[1-9][0-9]{0,3}$ ]]; then
    status="failed"
    reason="invalid-timeout"
    printf 'timeout must be an integer from 1 through 3600 seconds\n' >&2
    exit 64
fi
timeout_seconds="$((10#$timeout_seconds))"
if (( timeout_seconds > 3600 )); then
    status="failed"
    reason="invalid-timeout"
    printf 'timeout must be an integer from 1 through 3600 seconds\n' >&2
    exit 64
fi
timeout_seconds_json="$timeout_seconds"

if ! command -v dotnet >/dev/null 2>&1; then
    status="skipped"
    reason="dotnet-not-found"
    printf 'dotnet was not found on PATH\n' >&2
    exit 77
fi
dotnet_path="$(command -v dotnet)"
required_probe_commands=(timeout setsid file mktemp mkfifo head cat cmp ps tr readlink)
for required_command in "${required_probe_commands[@]}"; do
    if ! command -v "$required_command" >/dev/null 2>&1; then
        status="skipped"
        reason="${required_command}-not-found"
        printf '%s was not found on PATH\n' "$required_command" >&2
        exit 77
    fi
done

probe_temp="$(mktemp -d "${TMPDIR:-/tmp}/rustsharp-linux-aot.XXXXXX")"
dotnet_version_log="$probe_temp/dotnet-version.log"
run_bounded_capture 15 "$dotnet_version_log" dotnet --version
dotnet_status="$LAST_EXIT_CODE"
if (( dotnet_status != 0 )) || [[ "$LAST_OUTPUT_TRUNCATED" == true || "$LAST_CLEANUP_INCOMPLETE" == true || "$LAST_LOG_CAPTURE_CLEANUP_INCOMPLETE" == true ]]; then
    status="skipped"
    reason="required-dotnet-sdk-unavailable"
    printf 'required .NET SDK is unavailable (dotnet status=%d): %s\n' "$dotnet_status" "$dotnet_version" >&2
    exit 77
fi
if ! read_file_preserving_newlines "$dotnet_version_log"; then
    status="skipped"
    reason="dotnet-version-log-read-failed"
    printf '%s\n' "$READ_FILE_DIAGNOSTIC" >&2
    exit 77
fi
dotnet_version="$READ_FILE_CONTENT"

if ! source_path="$(realpath -e "$source_path")"; then
    status="failed"
    reason="source-not-found"
    printf 'source path does not exist: %s\n' "$source_path" >&2
    exit 2
fi

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
publish_output_truncated="$LAST_OUTPUT_TRUNCATED"
publish_log_capture_pid="$LAST_LOG_CAPTURE_PID"
publish_log_capture_parent_pid="$LAST_LOG_CAPTURE_PARENT_PID"
publish_log_capture_started_epoch="$LAST_LOG_CAPTURE_STARTED_EPOCH"
publish_log_capture_elapsed_ms="$LAST_LOG_CAPTURE_ELAPSED_MS"
publish_log_capture_exit_code="$LAST_LOG_CAPTURE_EXIT_CODE"
publish_log_capture_cleanup_incomplete="$LAST_LOG_CAPTURE_CLEANUP_INCOMPLETE"
publish_log_capture_diagnostic="$LAST_LOG_CAPTURE_DIAGNOSTIC"
if [[ "$publish_output_truncated" == true || "$publish_cleanup_incomplete" == true || "$publish_log_capture_cleanup_incomplete" == true ]]; then
    status="failed"
    reason="native-aot-publish-log-capture-failed"
    printf 'bounded publish process/log capture failed or truncated output\n' >&2
    exit 1
fi
if ! cp "$publish_log" "$output_dir/linux-aot-publish.log"; then
    status="failed"
    reason="native-aot-publish-log-copy-failed"
    printf 'could not copy the publish log into the evidence directory\n' >&2
    exit 1
fi
if (( publish_exit != 0 )); then
    status="failed"
    reason="native-aot-publish-failed"
    if ! read_file_preserving_newlines "$publish_log"; then
        reason="native-aot-publish-log-read-failed"
        printf '%s\n' "$READ_FILE_DIAGNOSTIC" >&2
        exit 1
    fi
    printf 'Native AOT publish failed with exit code %d\n%s' "$publish_exit" "$READ_FILE_CONTENT" >&2
    exit "$publish_exit"
fi

mapfile -t executables < <(find "$output_dir" -mindepth 1 -maxdepth 1 -type f -perm -111 -name '*.NativeAotHost' -print)
if (( ${#executables[@]} != 1 )); then
    status="failed"
    reason="native-aot-executable-count-mismatch"
    printf 'expected exactly one executable named *.NativeAotHost, found %d\n' "${#executables[@]}" >&2
    exit 1
fi
executable="${executables[0]}"
file_description_log="$probe_temp/file-description.log"
run_bounded_capture 15 "$file_description_log" file -b -- "$executable"
file_status="$LAST_EXIT_CODE"
if (( file_status != 0 )) || [[ "$LAST_OUTPUT_TRUNCATED" == true || "$LAST_CLEANUP_INCOMPLETE" == true || "$LAST_LOG_CAPTURE_CLEANUP_INCOMPLETE" == true ]]; then
    status="failed"
    reason="native-aot-artifact-inspection-failed"
    printf 'could not inspect the published artifact (file status=%d): %s\n' "$file_status" "$file_description" >&2
    exit 1
fi
if ! read_file_preserving_newlines "$file_description_log"; then
    status="failed"
    reason="native-aot-artifact-inspection-log-read-failed"
    printf '%s\n' "$READ_FILE_DIAGNOSTIC" >&2
    exit 1
fi
file_description="$READ_FILE_CONTENT"
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
run_output_truncated="$LAST_OUTPUT_TRUNCATED"
run_log_capture_pid="$LAST_LOG_CAPTURE_PID"
run_log_capture_parent_pid="$LAST_LOG_CAPTURE_PARENT_PID"
run_log_capture_started_epoch="$LAST_LOG_CAPTURE_STARTED_EPOCH"
run_log_capture_elapsed_ms="$LAST_LOG_CAPTURE_ELAPSED_MS"
run_log_capture_exit_code="$LAST_LOG_CAPTURE_EXIT_CODE"
run_log_capture_cleanup_incomplete="$LAST_LOG_CAPTURE_CLEANUP_INCOMPLETE"
run_log_capture_diagnostic="$LAST_LOG_CAPTURE_DIAGNOSTIC"
if [[ "$run_output_truncated" == true || "$run_cleanup_incomplete" == true || "$run_log_capture_cleanup_incomplete" == true ]]; then
    status="failed"
    reason="native-aot-runtime-log-capture-failed"
    printf 'bounded runtime process/log capture failed or truncated output\n' >&2
    exit 1
fi
if ! cp "$run_log" "$output_dir/linux-aot-run.log"; then
    status="failed"
    reason="native-aot-runtime-log-copy-failed"
    printf 'could not copy the runtime log into the evidence directory\n' >&2
    exit 1
fi
expected_output_path="$probe_temp/expected-runtime-output.bin"
exact_output_status=0
verify_exact_runtime_output "$run_log" "$expected_output_path" || exact_output_status=$?
if (( exact_output_status == 2 )); then
    status="failed"
    reason="native-aot-runtime-output-comparison-failed"
    printf '%s\n' "$EXACT_OUTPUT_DIAGNOSTIC" >&2
    exit 1
fi
if ! read_file_preserving_newlines "$run_log"; then
    status="failed"
    reason="native-aot-runtime-log-read-failed"
    printf '%s\n' "$READ_FILE_DIAGNOSTIC" >&2
    exit 1
fi
actual_output="$READ_FILE_CONTENT"
if (( run_exit != 0 || exact_output_status != 0 )); then
    status="failed"
    reason="native-aot-runtime-output-mismatch"
    printf 'native executable failed: exit=%d output=%q (%s)\n' \
        "$run_exit" "$actual_output" "$EXACT_OUTPUT_DIAGNOSTIC" >&2
    exit 1
fi

status="passed"
reason="native-aot-output-verified"
printf 'Linux x64 Native AOT passed: %s\nEvidence: %s\n' "$executable" "$evidence_path"
