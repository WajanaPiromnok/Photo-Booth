#!/usr/bin/env bash

set -u

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

UNITY_PATH="${UNITY_PATH:-/Applications/Unity/Hub/Editor/6000.3.12f1/Unity.app/Contents/MacOS/Unity}"
PROJECT_PATH="${PROJECT_PATH:-${PROJECT_ROOT}}"
TEST_PLATFORM="${TEST_PLATFORM:-EditMode}"
RESULTS_PATH="${RESULTS_PATH:-/tmp/photo-booth-editmode.xml}"
LOG_PATH="${LOG_PATH:-/tmp/unity-editmode.log}"
EXTRA_ARGS="${EXTRA_ARGS:-}"

run_unity() {
  "${UNITY_PATH}" \
    -batchmode \
    -nographics \
    -projectPath "${PROJECT_PATH}" \
    -runTests \
    -testPlatform "${TEST_PLATFORM}" \
    -testResults "${RESULTS_PATH}" \
    -logFile "${LOG_PATH}" \
    -quit \
    ${EXTRA_ARGS}
}

extract_attr() {
  local line="$1"
  local key="$2"
  printf '%s\n' "${line}" | sed -n "s/.*${key}=\"\\([^\"]*\\)\".*/\\1/p"
}

print_env_hint_if_needed() {
  local log_path="$1"

  if rg -q "another Unity instance is running with this project open|Multiple Unity instances cannot open the same project" "${log_path}"; then
    echo "Environment hint: This project is already open in another Unity instance. Close the Editor for this project, then rerun the script."
  fi

  if rg -q "Failed to start the Unity Package Manager local server process|listen EPERM|Could not connect to IPC stream" "${log_path}"; then
    echo "Environment hint: Unity Package Manager IPC failed to start. This is common in restricted sandboxes or when local IPC sockets are blocked."
  fi

  if rg -q "Licensing initialization failed|Timed-out after 60.00s, waiting for channel|connection with the Unity Licensing Client has been lost" "${log_path}"; then
    echo "Environment hint: Unity licensing did not initialize cleanly. Try opening the project from Unity Hub first, then rerun this script from a normal Terminal session."
  fi
}

rm -f "${RESULTS_PATH}" "${LOG_PATH}"

echo "Running Unity ${TEST_PLATFORM} tests..."
echo "Unity:   ${UNITY_PATH}"
echo "Project: ${PROJECT_PATH}"
echo "Log:     ${LOG_PATH}"
echo "Results: ${RESULTS_PATH}"

unity_exit_code=0
run_unity
unity_exit_code=$?

if [[ ! -f "${RESULTS_PATH}" ]]; then
  echo "Unity did not produce a test result XML."
  echo "Unity exit code: ${unity_exit_code}"
  print_env_hint_if_needed "${LOG_PATH}"
  echo
  echo "Last log lines:"
  tail -n 80 "${LOG_PATH}" 2>/dev/null || true
  exit 1
fi

test_run_line="$(rg -m1 "<test-run " "${RESULTS_PATH}" || true)"
if [[ -z "${test_run_line}" ]]; then
  echo "Test result XML exists but does not contain a <test-run> root."
  echo "Unity exit code: ${unity_exit_code}"
  exit 1
fi

result="$(extract_attr "${test_run_line}" "result")"
total="$(extract_attr "${test_run_line}" "total")"
passed="$(extract_attr "${test_run_line}" "passed")"
failed="$(extract_attr "${test_run_line}" "failed")"
skipped="$(extract_attr "${test_run_line}" "skipped")"
duration="$(extract_attr "${test_run_line}" "duration")"

echo
echo "Test summary:"
echo "  Result:   ${result:-unknown}"
echo "  Total:    ${total:-unknown}"
echo "  Passed:   ${passed:-unknown}"
echo "  Failed:   ${failed:-unknown}"
echo "  Skipped:  ${skipped:-unknown}"
echo "  Duration: ${duration:-unknown}s"
echo "  Unity exit code: ${unity_exit_code}"

if [[ "${result}" == "Passed" && "${failed:-1}" == "0" ]]; then
  if [[ "${unity_exit_code}" != "0" ]]; then
    echo
    echo "Tests passed according to XML even though Unity returned a non-zero exit code."
    print_env_hint_if_needed "${LOG_PATH}"
  fi

  exit 0
fi

echo
echo "Tests did not pass."
print_env_hint_if_needed "${LOG_PATH}"
echo
echo "Failing test snippets:"
rg -n "<test-case .*result=\"Failed\"|<failure>|<message>|<stack-trace>" "${RESULTS_PATH}" || true

exit 1
