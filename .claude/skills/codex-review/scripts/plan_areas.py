#!/usr/bin/env python3
"""전체 검증용 영역 나누기.

사용: plan_areas.py <target_repo> <workspace_dir> <scope> [max_lines]
  scope: server | client | shared | all | <경로 접두어,경로 접두어,...>
  max_lines: 영역 하나의 최대 줄 수(기본 2500). 파일 하나가 이보다 크면 그 파일만으로 한 영역이 된다.

git이 추적하는 .cs 파일만 대상으로 한다(bin/obj/Library 같은 생성물은 추적하지 않으므로 자연히 빠진다).
같은 디렉터리의 파일을 같은 영역에 모아 Codex가 맥락을 이어서 읽게 한다.

출력:
  <workspace_dir>/areas.json            [{"id": "a01", "lines": N, "files": [...]}, ...]
  <workspace_dir>/area_<id>_files.txt   영역별 파일 목록(한 줄에 하나)
"""
import json
import os
import subprocess
import sys
from collections import OrderedDict

SCOPES = {
    "server": ["Server/src/", "Shared/Runtime/"],
    "client": ["Client/Assets/Scripts/", "Shared/Runtime/"],
    "shared": ["Shared/Runtime/"],
    "all": ["Server/src/", "Client/Assets/Scripts/", "Shared/Runtime/"],
}


def main() -> int:
    if len(sys.argv) < 4:
        print(__doc__, file=sys.stderr)
        return 2
    repo, ws, scope = sys.argv[1], sys.argv[2], sys.argv[3]
    max_lines = int(sys.argv[4]) if len(sys.argv) > 4 else 2500
    prefixes = SCOPES.get(scope) or [p.strip().rstrip("/") + "/" for p in scope.split(",") if p.strip()]

    tracked = subprocess.run(["git", "-C", repo, "ls-files", "--", *prefixes],
                             capture_output=True, text=True, check=True).stdout.split("\n")
    files = sorted(f for f in tracked if f.endswith(".cs"))
    if not files:
        print(f"no tracked .cs files under {prefixes}", file=sys.stderr)
        return 1

    by_dir: "OrderedDict[str, list]" = OrderedDict()
    for f in files:
        with open(os.path.join(repo, f), encoding="utf-8", errors="replace") as fh:
            n = sum(1 for _ in fh)
        by_dir.setdefault(os.path.dirname(f), []).append((f, n))

    areas, cur, cur_lines = [], [], 0

    def flush():
        nonlocal cur, cur_lines
        if cur:
            areas.append({"id": f"a{len(areas) + 1:02d}", "lines": cur_lines, "files": [f for f, _ in cur]})
        cur, cur_lines = [], 0

    for _, entries in by_dir.items():
        dir_lines = sum(n for _, n in entries)
        # 디렉터리 하나가 통째로 들어가면 같이 넣는다. 아니면 지금 영역을 닫고 파일 단위로 채운다.
        if cur_lines + dir_lines <= max_lines:
            cur += entries
            cur_lines += dir_lines
            continue
        if cur_lines > 0 and dir_lines <= max_lines:
            flush()
            cur, cur_lines = list(entries), dir_lines
            continue
        for f, n in entries:
            if cur_lines + n > max_lines:
                flush()
            cur.append((f, n))
            cur_lines += n
    flush()

    os.makedirs(ws, exist_ok=True)
    with open(os.path.join(ws, "areas.json"), "w", encoding="utf-8") as fh:
        json.dump(areas, fh, ensure_ascii=False, indent=1)
    for a in areas:
        with open(os.path.join(ws, f"area_{a['id']}_files.txt"), "w", encoding="utf-8") as fh:
            fh.write("\n".join(a["files"]) + "\n")
    total = sum(a["lines"] for a in areas)
    print(f"{len(files)} files, {total} lines -> {len(areas)} areas (max {max_lines} lines)")
    for a in areas:
        print(f"  {a['id']}: {len(a['files']):3d} files {a['lines']:6d} lines  {a['files'][0]} ...")
    return 0


if __name__ == "__main__":
    sys.exit(main())
