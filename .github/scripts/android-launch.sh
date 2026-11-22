#!/usr/bin/env bash
set -euo pipefail

package=org.stalsky.vapemeter
if [[ -f "$RUNNER_TEMP/previous.apk" ]]; then
  adb install "$RUNNER_TEMP/previous.apk"
fi
adb install -r "$RUNNER_TEMP/candidate.apk"
activity="$(adb shell cmd package resolve-activity --brief -a android.intent.action.MAIN -c android.intent.category.LAUNCHER "$package" | tail -n 1 | tr -d '\r')"
[[ "$activity" == "$package/"* ]]
adb logcat -b crash -c
adb shell am start -W -n "$activity"

for attempt in {1..15}; do
  activities="$(adb shell dumpsys activity activities)"
  if grep -F topResumedActivity <<<"$activities" | grep -F "$package/"; then
    break
  fi
  sleep 1
done
sleep 5
activities="$(adb shell dumpsys activity activities)"
crashes="$(adb logcat -d -b crash)"
if [[ "$crashes" == *"$package"* ]]; then
  printf '%s\n' "$crashes"
  exit 1
fi
grep -F topResumedActivity <<<"$activities" | grep -F "$package/"
adb shell pidof "$package"
adb shell am force-stop "$package"
