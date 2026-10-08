---
name: github-push
description: "현재 작업을 검증한 뒤 Git Commit을 만들고 현재 GitHub Remote Branch로 Push한다. 사용자가 '푸시', 'github push', '깃허브 푸시'를 명시적으로 입력했을 때만 사용한다. 구현을 마쳤다거나 저장해 달라는 요청, 'commit 메시지 추천해줘', 'git 상태 보여줘' 같은 요청에는 사용하지 않으며 스스로 판단해 Commit·Push하지 않는다."
---

# GitHub Push

사용자가 "푸시", "github push", "깃허브 푸시"를 입력했을 때만 실행한다. 작업이 끝났다고 스스로 Commit이나 Push를 시작하지 않는다. Push는 원격 저장소에 남아 되돌리기 어렵기 때문이다.

순서는 바꾸지 않는다.

```text
Repository 확인 → Remote 확인 → Branch 확인 → 변경사항 확인
→ 민감정보 검사 → 불필요한 파일 확인 → 빌드/테스트 → 변경사항 분석
→ Stage → Commit → Push → 결과 확인
```

한 단계라도 실패하면 뒤 단계로 가지 않고 **실패 보고**(마지막 절) 형식으로 멈춘다. 실패 원인만 정리하고, 검증을 통과시키려고 코드·테스트·설정을 고치지 않는다.

## 절대 규칙

1. 사용자가 "푸시"를 호출했을 때만 Commit/Push한다.
2. 기존 사용자 변경사항을 삭제하지 않는다.
3. Push 전에 diff를 직접 확인한다.
4. 민감정보가 있으면 Push하지 않는다.
5. 가능한 경우 Build/Test 후 Push한다.
6. Test를 통과시키기 위해 Test를 삭제하거나 비활성화하지 않는다.
7. Force Push하지 않는다.
8. Remote URL을 임의로 변경하지 않는다.
9. 현재 Branch를 확인하지 않고 Push하지 않는다.
10. 의미 없는 Commit Message를 만들지 않는다.

다음 명령은 사용자가 따로 요청하지 않는 한 쓰지 않는다. 사용자의 커밋하지 않은 작업이나 원격 기록을 되돌릴 수 없게 지우기 때문이다.

```text
git push --force / -f / --force-with-lease
git reset --hard
git checkout -- .
git restore .      (작업 트리 복원)
git clean -fd
git rebase --onto
git stash          (임의 stash)
git remote add / git remote set-url
git init
```

## 1. Repository 확인

```bash
git rev-parse --is-inside-work-tree
```

Git Repository가 아니면 중단한다. 새 Repository를 만들지 않는다.

진행 중인 merge·rebase·cherry-pick이 있거나 충돌 파일이 있으면 "Repository 상태 이상"으로 중단한다. `git status`에 `Unmerged paths`, `rebase in progress`, `You have unmerged paths`가 보이면 해당한다.

## 2. Remote 확인

```bash
git remote -v
```

Remote가 없으면 중단한다. 기존에 설정된 remote만 사용한다. Remote가 여러 개이고 현재 Branch의 upstream도 없어 Push 대상을 정할 수 없으면 "Push 대상 Branch 불명확"으로 중단하고 사용자에게 묻는다.

## 3. Branch 확인

```bash
git branch --show-current
git branch -vv
```

- 출력이 비어 있으면 Detached HEAD다. 자동 Push하지 않는다.
- `main`, `master`, `production`, `release`는 보호 Branch로 취급한다. 이번 대화에서 사용자가 해당 Branch에 작업·Push하라고 명시했으면 진행한다. 그렇지 않으면 이 Branch로 Push해도 되는지 사용자에게 확인한다. 새 Branch를 만들거나 Branch 전략을 바꾸는 일은 사용자가 요청할 때만 한다.

## 4. 변경사항 확인

```bash
git status --short
git diff
git diff --cached
git log --oneline @{u}..HEAD   # upstream이 있을 때: Push되지 않은 기존 Commit
```

- 변경사항이 없고 Push되지 않은 Commit도 없으면 "변경사항 없음"으로 끝낸다. Commit을 만들려고 파일을 고치지 않는다.
- 변경사항이 없지만 Push되지 않은 Commit이 있으면 그 Commit 목록을 보여 주고 Push할지 사용자에게 확인한다. 확인받으면 5·7·9단계 검사를 해당 Commit 범위(`git diff @{u}..HEAD`)에 적용한 뒤 10단계로 간다.
- Commit 후보 파일을 정한다. 이번 작업과 관계없어 보이는 변경(사용자가 따로 고치던 파일)은 후보에서 빼고 보고에 "제외한 변경"으로 적는다. 판단이 어려우면 사용자에게 묻는다.

## 5. 민감정보 검사

Commit 후보 파일을 검사 스크립트에 넘긴다. 추적 중인 파일은 추가된 줄만, 새 파일은 전체 내용을 검사한다.

```bash
bash .Codex/skills/github-push/scripts/scan_secrets.sh <후보 파일...>
```

- 종료 코드 0: 발견 없음. 1: 발견. 2: 실행 오류.
- 1이면 Commit과 Push를 중단하고 스크립트가 출력한 **파일:줄과 유형만** 보고한다. 값은 옮겨 적지 않는다. 보고서에 비밀값을 적으면 그 자체가 유출이 되기 때문이다.
- 민감정보를 자동으로 지우거나 다른 값으로 바꾸지 않는다.
- 스크립트는 정규식 검사라 놓치는 경우가 있다. `appsettings.*.json`, `*.config`, Connection String이 있는 파일은 diff를 직접 읽어 비밀번호·토큰이 평문으로 들어 있지 않은지 한 번 더 확인한다.
- 스크립트가 잡은 항목이 명백한 예시·더미 값(`password = "<your-password>"`)이라고 판단되면 중단하지 않을 수 있다. 이때도 해당 파일:줄과 판단 근거를 보고에 적는다.

## 6. 불필요한 파일 확인

Commit 후보에 다음 생성물이 들어 있지 않은지 확인한다.

```text
Unity: Library/ Temp/ Obj/ obj/ Logs/ UserSettings/ Build/ Builds/
.NET:  bin/ obj/ TestResults/
IDE:   .idea/ .vs/
```

- 들어 있으면 후보에서 뺀다. 파일을 지우거나 `.gitignore`를 고치지 않는다.
- 이미 추적 중인 파일(`git ls-files`에 있는 파일)은 프로젝트가 의도한 것일 수 있으므로 임의로 추적을 해제하지 않고 보고만 한다.
- `.gitignore`가 있으면 그 규칙을 우선한다.
- Unity 에셋을 Commit할 때는 짝이 되는 `.meta` 파일이 함께 후보에 있는지 확인한다. `.meta`가 빠지면 다른 PC에서 GUID가 새로 생겨 참조가 끊긴다. 반대로 에셋 없이 `.meta`만 남은 경우도 보고한다.

## 7. 빌드와 테스트

현재 변경과 관련된 범위만 실행한다.

**Server** (`.sln`, `.slnx`, `.csproj` 아래 파일이 바뀐 경우)

```bash
dotnet build <변경과 관련된 솔루션 또는 프로젝트>
dotnet test  <테스트 프로젝트가 있을 때>
```

실패하면 중단한다. 실패한 테스트를 삭제·비활성화하지 않는다.

**Unity Client** (`Client/` 아래 `.cs`, `.asmdef`가 바뀐 경우)

CLI 빌드 환경이 이미 구성되어 있지 않다면 새 CI나 Build Script를 만들지 않는다. 기존 Unity batchmode 검증 스크립트가 있으면 그것을 쓴다. 없으면 변경된 파일을 직접 읽고 다음을 확인한다.

```text
명백한 C# syntax 오류
누락된 using / namespace
삭제·이름 변경된 타입을 아직 참조하는 코드 (Grep으로 확인)
asmdef 참조 누락
코드에 적은 Asset 경로가 실제로 존재하는지
```

이 경우 결과는 PASS가 아니라 `STATIC CHECK`로 보고한다. 실제 컴파일을 하지 않았기 때문이다. 명백한 오류를 찾으면 Compile Error로 보고 중단한다.

관련 코드가 없거나 실행할 수 없으면 `SKIPPED`와 이유를 적는다.

## 8. 변경사항 분석

실제 diff를 읽고 다음을 정리한다. 파일 이름만 보고 판단하지 않는다.

- 무엇을 바꿨는가
- 왜 바꿨는가 (대화 맥락과 diff로 알 수 있는 범위)
- 주요 기능은 무엇인가
- feat / fix / refactor / perf / test / docs / build / chore 중 무엇인가

## 9. Stage

필요한 파일만 경로를 지정해 Stage한다.

```bash
git add <path1> <path2> ...
```

`git add .`나 `git add -A`는 저장소의 모든 변경이 이번 작업에서 나왔고 모두 Commit 대상임이 확실할 때만 쓴다.

Stage 후 최종 검사를 한다. Stage 내용이 실제로 Commit될 내용이므로 여기서 한 번 더 확인한다.

```bash
git diff --cached --stat
git diff --cached
bash .Codex/skills/github-push/scripts/scan_secrets.sh --staged
```

문제가 있으면 Commit하지 않는다. 이미 Stage한 파일은 `git restore --staged <path>`로 Stage만 해제할 수 있다. 이 명령은 작업 트리 내용을 바꾸지 않는다.

## 10. Commit

형식은 `<type>: <summary>`다. 짧고 구체적으로 쓴다.

```text
feat: add player session management
fix: prevent duplicate packet processing
perf: reduce allocations in packet parser
```

`update`, `changes`, `fix`, `work`, `test`처럼 내용을 알 수 없는 메시지는 쓰지 않는다. 실행 환경이 Commit attribution(예: `Co-Authored-By` 줄)을 지정했다면 메시지 끝에 붙인다.

```bash
git commit -m "<type>: <summary>" [-m "<본문/attribution>"]
git status
git log -1 --oneline
git branch -vv
```

Commit hook이 실패하면 `--no-verify`로 우회하지 않는다. 원인을 보고하고 중단한다.

## 11. Push

- upstream이 있으면 `git push`
- upstream이 없고 remote와 Branch가 명확하면 `git push -u <remote> <current-branch>`. Remote가 `origin` 하나뿐이면 `origin`을 쓴다.
- 기존 upstream 설정을 바꾸지 않는다.

Push가 실패하면 억지로 진행하지 않는다.

| 원인 | 대응 |
|------|------|
| non-fast-forward, Remote에 새 Commit | Force Push하지 않는다. `git fetch` → `git status` → `git log --oneline --graph --decorate --all -20`으로 상황만 확인하고 보고한다. 프로젝트의 merge/rebase 정책이 명확하지 않으면 병합하지 않는다. |
| Authentication 실패, Permission 부족 | 자격 증명을 만들거나 URL을 바꾸지 않는다. 보고한다. |
| Branch Protection | 보고한다. 다른 Branch로 우회 Push하지 않는다. |
| Network 오류 | 한 번 다시 시도하고, 또 실패하면 보고한다. |

## 12. 결과 확인

```bash
git status
git log -1 --oneline
git rev-parse HEAD @{u}
```

Push 명령의 종료 코드만 보지 말고 출력의 `rejected`, `error`, `fatal`을 확인한다. `HEAD`와 `@{u}`가 같은 커밋을 가리켜야 완료로 판단한다.

## 결과 보고

성공:

```text
GitHub Push 완료

Branch:
<현재 branch>

Commit:
<commit hash> <commit message>

Changed:
<주요 변경사항>

Validation:
- Build: PASS / SKIPPED (이유)
- Test: PASS / SKIPPED (이유)
- Unity: STATIC CHECK / SKIPPED (이유)
- Secret Scan: PASS

Push:
<remote>/<branch>
```

제외한 변경이나 주의 사항이 있으면 한두 줄로 덧붙인다.

실패:

```text
GitHub Push 중단

Stage:
Repository / Remote / Branch / Changes / Secret / Build / Test / Commit / Push

Reason:
실패 원인 (민감정보라면 파일:줄과 유형만)

Repository 변경:
Commit 생성 여부 (생성했다면 hash)
Push 여부
```

불필요하게 긴 설명은 하지 않는다.

## 테스트 시나리오

**정상 흐름:** feature Branch, upstream 있음, `Server/` 파일 2개 수정 → 검사 통과 → `dotnet build` PASS → 두 파일만 Stage → `feat: ...` Commit → `git push` → `HEAD == @{u}` 확인 → 완료 보고.

**오류 흐름 1:** 새로 추가한 `appsettings.Development.json`에 `Password=<실제 비밀번호>` 형태의 평문 값이 있다 → 스크립트 종료 코드 1 → 파일:줄과 유형만 보고, Commit 없음, Push 없음.

**오류 흐름 2:** Push에서 non-fast-forward → fetch와 로그로 상황만 확인 → Commit은 로컬에 있고 Push되지 않았다고 보고. Force Push나 자동 rebase를 하지 않는다.
