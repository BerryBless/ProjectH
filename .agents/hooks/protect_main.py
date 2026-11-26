#!/usr/bin/env python
"""protect-main PreToolUse 훅. Claude Code 와 Codex 가 같은 스크립트를 쓴다.

에이전트가 main 에 직접 올리는 것을 막는다. 머지는 사용자만 GitHub 에서 한다.

차단하는 호출 (Bash / PowerShell):
  - 현재 브랜치가 main(또는 master) 인 상태의 `git commit`, `git push`, `git merge`
  - 브랜치와 무관하게 `git push --force / -f / --force-with-lease`, `git push ... +<ref>`
  - 브랜치와 무관하게 `git push ... main` / `master` (다른 브랜치에서 main 으로 밀어 넣기)
  - `gh pr merge` (PR 머지는 사용자 몫)

막지 못하는 것: 셸 밖에서 하는 git 조작, GitHub 웹에서의 머지. 그건 저장소 설정(브랜치 보호)이 맡는다.
검사기 자체가 실패하면 거부한다(fail closed). main 오염은 되돌리기 번거롭기 때문이다.

거부 출력은 두 도구가 같은 형식(hookSpecificOutput.permissionDecision = deny)을 쓴다.
"""
import json
import os
import re
import subprocess
import sys

for _stream in (sys.stdin, sys.stdout, sys.stderr):
    if hasattr(_stream, "reconfigure"):
        _stream.reconfigure(encoding="utf-8", errors="replace")

PROTECTED = {"main", "master"}
SHELL_TOOLS = {"Bash", "PowerShell"}

GIT_COMMIT_RE = re.compile(r"(?:^|[\s;&|(])git\s+(?:-C\s+\S+\s+)?commit\b")
GIT_PUSH_RE = re.compile(r"(?:^|[\s;&|(])git\s+(?:-C\s+\S+\s+)?push\b")
GIT_MERGE_RE = re.compile(r"(?:^|[\s;&|(])git\s+(?:-C\s+\S+\s+)?merge\b")
FORCE_RE = re.compile(r"\s(?:--force(?:-with-lease)?(?:=\S+)?|-f)(?:\s|$)|\s\+\S+")
PUSH_TO_MAIN_RE = re.compile(r"(?:^|[\s;&|(])git\s+(?:-C\s+\S+\s+)?push\b[^;&|]*?(?:\s|:)(?:main|master)(?:\s|$|:)")
PR_MERGE_RE = re.compile(r"(?:^|[\s;&|(])gh\s+pr\s+merge\b")


def _emit_deny(reason):
    print(json.dumps({"hookSpecificOutput": {
        "hookEventName": "PreToolUse",
        "permissionDecision": "deny",
        "permissionDecisionReason": reason,
    }}, ensure_ascii=False))


def current_branch(cwd):
    out = subprocess.run(
        ["git", "branch", "--show-current"],
        cwd=cwd, capture_output=True, text=True, timeout=10,
    )
    if out.returncode != 0:
        return None  # git 저장소가 아니면 검사 대상 아님
    return out.stdout.strip()


def main():
    try:
        payload = json.load(sys.stdin)
    except Exception as e:  # noqa: BLE001
        _emit_deny(f"[protect-main] 훅 입력을 읽지 못해 호출을 차단했다: {e}")
        return
    tool = payload.get("tool_name") or payload.get("tool") or ""
    if tool not in SHELL_TOOLS:
        return
    tool_input = payload.get("tool_input") or {}
    command = tool_input.get("command") or ""
    if not any(r.search(command) for r in (GIT_COMMIT_RE, GIT_PUSH_RE, GIT_MERGE_RE, PR_MERGE_RE)):
        return

    rule = "규칙: 작업 브랜치(agent/<작업명>)에서 커밋·push 하고 PR 을 연다. 머지는 사용자가 GitHub 에서 한다. 훅을 우회하거나 끄지 않는다."

    if PR_MERGE_RE.search(command):
        _emit_deny(f"[protect-main] `gh pr merge` 는 에이전트가 하지 않는다. PR 링크를 사용자에게 보고하라. {rule}")
        return
    if GIT_PUSH_RE.search(command) and FORCE_RE.search(command):
        _emit_deny(f"[protect-main] force push 를 차단했다. 원격 기록을 덮어쓰지 않는다. 원인만 보고하라. {rule}")
        return
    if PUSH_TO_MAIN_RE.search(command):
        _emit_deny(f"[protect-main] main/master 로의 push 를 차단했다. {rule}")
        return

    cwd = payload.get("cwd") or os.getcwd()
    try:
        branch = current_branch(cwd)
    except Exception as e:  # noqa: BLE001
        _emit_deny(f"[protect-main] 현재 브랜치를 확인하지 못해 호출을 차단했다: {e}")
        return
    if branch is None:
        return
    if branch == "":
        _emit_deny("[protect-main] detached HEAD 상태다. 브랜치를 만든 뒤 다시 시도하라. " + rule)
        return
    if branch in PROTECTED:
        _emit_deny(
            f"[protect-main] 현재 브랜치가 `{branch}` 이라 commit/push/merge 를 차단했다. "
            "먼저 `git switch -c agent/<작업명>` 으로 작업 브랜치를 만들고 거기서 커밋·push 한 뒤 PR 을 연다. " + rule
        )
        return


if __name__ == "__main__":
    main()
