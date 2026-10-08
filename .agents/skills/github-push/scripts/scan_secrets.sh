#!/usr/bin/env bash
# Scan commit candidates for secrets before commit/push.
#
# Usage:
#   scan_secrets.sh <file>...   tracked files: added lines vs HEAD; untracked files: full content
#   scan_secrets.sh --staged    added lines in the staged diff (final gate before commit)
#
# Exit: 0 = nothing found, 1 = findings, 2 = usage/runtime error.
# Findings print file:line and a category only. Matched values are never printed,
# because the report itself must not leak the secret.

set -u
# A failing git/awk stage must fail the scan (exit 2), not silently pass as "nothing found".
set -o pipefail

git rev-parse --is-inside-work-tree >/dev/null 2>&1 || { echo "scan_secrets: not a git repository" >&2; exit 2; }
[ $# -ge 1 ] || { echo "usage: scan_secrets.sh --staged | <file>..." >&2; exit 2; }

# category<TAB>extended regex. Case-insensitive match.
PATTERNS=$(cat <<'EOF'
private-key	-----BEGIN ([A-Z]+ )?PRIVATE KEY-----
aws-access-key	(AKIA|ASIA)[0-9A-Z]{16}
aws-secret	aws_?secret_?access_?key[^A-Za-z0-9]{0,5}[:=][^A-Za-z0-9]{0,5}[A-Za-z0-9/+=]{30,}
github-token	(ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}
slack-token	xox[abprs]-[A-Za-z0-9-]{10,}
api-key-sk	(^|[^A-Za-z0-9])sk-[A-Za-z0-9_-]{20,}
google-api-key	AIza[0-9A-Za-z_-]{35}
jwt	eyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}
connection-string-password	(password|pwd)[[:space:]]*=[[:space:]]*[^;"'[:space:]<>{}$]{3,}
secret-assignment	(password|passwd|secret|api[_-]?key|access[_-]?token|auth[_-]?token|client[_-]?secret|jwt[_-]?secret)["']?[[:space:]]*[:=][[:space:]]*["'][^"'<>{}$[:space:]]{6,}["']
EOF
)

# Files that usually hold secrets. Reported even if the content looks clean.
SENSITIVE_NAMES='(^|/)(\.env(\..+)?|secrets\.json|credentials(\..+)?|id_rsa|id_ed25519|id_ecdsa|.+\.pem|.+\.key|.+\.pfx|.+\.p12)$'

# Findings go to stdout. The exit code is decided from the collected output at the end,
# because scan_stream runs inside pipelines (subshells) and cannot set a parent variable.
report() { # file line category
  printf '%s:%s\t%s\n' "$1" "$2" "$3"
}

# Reads "file<TAB>line<TAB>text" from stdin.
# One awk pass per pattern over the whole stream. Spawning grep per line was
# ~190k processes for a 9.5k-line Unity project and took minutes on Windows.
# Case-insensitive via tolower() on both sides, which works in any POSIX awk.
scan_stream() {
  local buf
  buf=$(cat) || return 2
  [ -n "$buf" ] || return 0
  while IFS=$'\t' read -r cat re; do
    [ -n "$cat" ] || continue
    # Pass the regex via ENVIRON: awk -v would unescape "\." into "." and loosen the pattern.
    printf '%s\n' "$buf" | SCAN_RE="$re" awk -F'\t' -v cat="$cat" '
      BEGIN { re = tolower(ENVIRON["SCAN_RE"]) }
      { text = $0; sub(/^[^\t]*\t[^\t]*\t/, "", text)
        if (tolower(text) ~ re) print $1 ":" $2 "\t" cat }' || return 2
  done <<< "$PATTERNS"
}

# Convert a unified diff (-U0) into "file<TAB>line<TAB>added text".
diff_to_lines() {
  awk '
    /^\+\+\+ / { f = substr($0, 5); sub(/^b\//, "", f); next }
    /^@@ / { split($3, a, ","); ln = substr(a[1], 2) + 0; next }
    /^\+/ && f != "/dev/null" { print f "\t" ln "\t" substr($0, 2); ln++ }
  '
}

check_name() {
  if printf '%s\n' "$1" | grep -Eiq -- "$SENSITIVE_NAMES"; then
    report "$1" "-" "sensitive-filename"
  fi
}

main() {
if [ "$1" = "--staged" ]; then
  while IFS= read -r f; do check_name "$f"; done < <(git diff --cached --name-only --diff-filter=ACMR)
  git diff --cached -U0 --no-color --diff-filter=ACMR | diff_to_lines | scan_stream || return 2
else
  for f in "$@"; do
    [ -e "$f" ] || { echo "scan_secrets: skip missing $f" >&2; continue; }
    check_name "$f"
    if git ls-files --error-unmatch -- "$f" >/dev/null 2>&1; then
      git diff HEAD -U0 --no-color -- "$f" | diff_to_lines | scan_stream || return 2
    else
      # Untracked: every line is new. Skip binary files.
      grep -Iq . -- "$f" 2>/dev/null || continue
      awk -v f="$f" '{ print f "\t" NR "\t" $0 }' "$f" | scan_stream || return 2
    fi
  done
fi
return 0
}

out=$(main "$@") || { echo "scan_secrets: scan failed" >&2; exit 2; }
[ -z "$out" ] && exit 0
printf '%s\n' "$out" | sort -u
exit 1
