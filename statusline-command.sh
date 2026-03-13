#!/usr/bin/env bash
# Claude Code status line: model name, context bar, 5h window bar

input=$(cat)

model=$(echo "$input" | jq -r '.model.display_name // "Unknown"')
used_pct=$(echo "$input" | jq -r '.context_window.used_percentage // empty')

# Session timing: derive start time from transcript mtime
transcript=$(echo "$input" | jq -r '.transcript_path // empty')
now_epoch=$(date +%s)
session_epoch=""
if [ -n "$transcript" ] && [ -f "$transcript" ]; then
  session_epoch=$(stat -c %Y "$transcript" 2>/dev/null || stat -f %m "$transcript" 2>/dev/null || echo "")
fi
[ -z "$session_epoch" ] && session_epoch="$now_epoch"

elapsed_sec=$(( now_epoch - session_epoch ))
[ "$elapsed_sec" -lt 0 ] && elapsed_sec=0
window_sec=$(( 5 * 3600 ))
[ "$elapsed_sec" -gt "$window_sec" ] && elapsed_sec=$window_sec
time_pct=$(( elapsed_sec * 100 / window_sec ))

# Build a block-character progress bar
make_bar() {
  local pct=$1
  local width=$2
  local filled=$(( pct * width / 100 ))
  local empty=$(( width - filled ))
  local bar=""
  local i
  for (( i=0; i<filled; i++ )); do bar="${bar}█"; done
  for (( i=0; i<empty;  i++ )); do bar="${bar}░"; done
  printf "%s" "$bar"
}

BAR_WIDTH=10

# Context bar
if [ -n "$used_pct" ]; then
  used_int=$(printf "%.0f" "$used_pct")
  ctx_bar=$(make_bar "$used_int" "$BAR_WIDTH")
  if   [ "$used_int" -ge 85 ]; then ctx_color="\033[31m"
  elif [ "$used_int" -ge 60 ]; then ctx_color="\033[33m"
  else ctx_color="\033[32m"; fi
  ctx_str=$(printf "${ctx_color}ctx [%s] %3d%%\033[0m" "$ctx_bar" "$used_int")
else
  ctx_str="ctx [░░░░░░░░░░]  --%"
fi

# 5-hour window bar
time_bar=$(make_bar "$time_pct" "$BAR_WIDTH")
elapsed_h=$(( elapsed_sec / 3600 ))
elapsed_m=$(( (elapsed_sec % 3600) / 60 ))
if   [ "$time_pct" -ge 85 ]; then time_color="\033[31m"
elif [ "$time_pct" -ge 60 ]; then time_color="\033[33m"
else time_color="\033[32m"; fi
time_str=$(printf "${time_color}5h  [%s] %dh%02dm\033[0m" "$time_bar" "$elapsed_h" "$elapsed_m")

printf "%s  |  %b  |  %b\n" "$model" "$ctx_str" "$time_str"
