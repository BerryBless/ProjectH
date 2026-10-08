#!/usr/bin/env python3
"""전체 검증 결과 모으기: 영역별 결과, 실제로 읽은 파일 범위, 세션·토큰, 지적 목록.

사용: summarize.py <workspace_dir>
출력: <workspace_dir>/02_summary.json, 표준 출력에 영역 표
종료 코드: 0 = 모든 영역 결과가 있고 모든 파일을 읽음, 1 = 실패 영역이나 읽지 않은 파일이 있음
"""
import csv
import json
import os
import sys


def norm(p: str) -> str:
    return p.replace("\\", "/").lstrip("./").strip()


def main() -> int:
    ws = sys.argv[1]
    areas = json.load(open(os.path.join(ws, "areas.json"), encoding="utf-8"))
    runs = {}
    if os.path.exists(os.path.join(ws, "runs.tsv")):
        with open(os.path.join(ws, "runs.tsv"), encoding="utf-8") as fh:
            for r in csv.DictReader(fh, delimiter="\t"):
                runs[r["label"]] = r  # 같은 영역을 다시 돌렸으면 마지막 실행이 남는다

    summary = {"areas": [], "findings": [], "missing_files": [], "failed_areas": [], "total_tokens": 0}
    print(f"{'area':5} {'files':>5} {'lines':>6} {'read':>5} {'find':>4} {'tokens':>8}  session")
    for a in areas:
        aid = a["id"]
        run = runs.get(f"area:{aid}", {})
        tokens = int(run["tokens"]) if run.get("tokens", "-").isdigit() else 0
        summary["total_tokens"] += tokens
        path = os.path.join(ws, f"area_{aid}_result.json")
        result = None
        if os.path.exists(path) and os.path.getsize(path) > 0:
            try:
                result = json.load(open(path, encoding="utf-8"))
            except json.JSONDecodeError:
                result = None
        listed = {norm(f) for f in a["files"]}
        if result is None:
            summary["failed_areas"].append(aid)
            read, missing = set(), sorted(listed)
        else:
            read = {norm(f) for f in result.get("files_read", [])} & listed
            missing = sorted(listed - read)
            for f in result.get("findings", []):
                summary["findings"].append({**f, "area": aid})
        summary["missing_files"] += [{"area": aid, "file": f} for f in missing]
        summary["areas"].append({"id": aid, "files": len(listed), "lines": a["lines"], "read": len(read),
                                 "findings": 0 if result is None else len(result.get("findings", [])),
                                 "exit": run.get("exit", "-"), "session": run.get("session", "-"), "tokens": tokens})
        status = "FAILED" if result is None else ""
        print(f"{aid:5} {len(listed):5d} {a['lines']:6d} {len(read):5d} "
              f"{summary['areas'][-1]['findings']:4d} {tokens:8d}  {run.get('session', '-')} {status}")

    with open(os.path.join(ws, "02_summary.json"), "w", encoding="utf-8") as fh:
        json.dump(summary, fh, ensure_ascii=False, indent=1)
    files_total = sum(x["files"] for x in summary["areas"])
    files_read = sum(x["read"] for x in summary["areas"])
    print(f"read {files_read}/{files_total} files, findings {len(summary['findings'])}, "
          f"tokens {summary['total_tokens']}, failed areas {summary['failed_areas'] or 'none'}")
    if summary["missing_files"]:
        print("not read:", ", ".join(f"{m['area']}:{m['file']}" for m in summary["missing_files"][:20]))
    return 1 if summary["failed_areas"] or summary["missing_files"] else 0


if __name__ == "__main__":
    sys.exit(main())
