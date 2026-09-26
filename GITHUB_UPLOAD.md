# GitHub에 업로드하기

소스 저장소를 올리는 절차입니다. 프로그램 실행이나 실거래 시작은 포함하지 않습니다.

## 포함되는 파일

- `.gitignore`, `.gitattributes`, `README.md`, 이 안내 문서
- `app/`, `trading/`, `tests/`
- `build.ps1`, `run.ps1`, `strategy.json`
- `data/`의 원본 시세·거래 규칙 자료 14개. 차트와 데이터 출처 검증에 필요하므로 함께 올립니다.

`.venv/`, `private_state/`, `records/`, `reports/`, `.ui-update/`, `.env`, 실행 파일, 데이터베이스, 엑셀, 로그, 백업과 ZIP은 `.gitignore`에서 제외합니다. 제외한 파일은 로컬에 계속 보관됩니다. API 키는 프로그램의 **API 설정**에 입력하며 저장소에 넣지 않습니다.

## 첫 업로드

1. GitHub에서 빈 **Private** 저장소를 만듭니다. 로컬 파일로 첫 커밋을 만들기 위해 생성 화면의 README·라이선스·gitignore 추가는 선택하지 않습니다.
2. 아래 명령은 Git이 설치된 PowerShell에서 프로젝트 폴더를 열고 실행합니다.

```powershell
# .git이 없는 소스 ZIP으로 시작한 경우에도 사용할 수 있습니다.
git init
git add .
git diff --cached --name-only
```

표시된 목록에 개인 자료나 생성 파일이 없는지 확인한 후 커밋합니다. `git add -f`로 제외 규칙을 우회하지 않습니다. GitHub 웹 업로드는 `.gitignore`를 적용하지 않으므로 원래 실행 폴더 전체를 드래그하지 않습니다.

```powershell
git commit -m "Prepare Trading source repository"
git branch -M main
```

Git이 커밋 작성자 설정을 요청하면 저장소 안에서 본인이 사용할 이름과 이메일을 설정한 뒤 커밋 명령을 다시 실행합니다. 공개할 이메일을 직접 선택합니다.

```powershell
git config user.name "YOUR_NAME"
git config user.email "YOUR_COMMIT_EMAIL"
```

다음 URL의 `YOUR_ACCOUNT`와 `YOUR_REPOSITORY`를 만든 저장소 정보로 바꿉니다. `origin`이 이미 있으면 `git remote -v`로 확인하고 아래 `remote add`는 생략합니다.

```powershell
git remote add origin https://github.com/YOUR_ACCOUNT/YOUR_REPOSITORY.git
git push -u origin main
```

GitHub 인증이 요청되면 Git의 인증 창에서 진행합니다. 인증 토큰을 소스나 문서에 적지 않습니다.

## 소스 ZIP으로 시작하는 경우

이 준비 작업에서 생성한 `reports/github-upload/Trading-source.zip`은 위 소스 파일만 담은 시점별 복사본입니다. ZIP 안의 `Trading` 폴더를 **Documents 아래 새 폴더**에 풀고 첫 업로드 절차를 진행하면 됩니다. ZIP 자체를 저장소에 올리는 대신 압축을 푼 소스를 Git으로 커밋합니다. 이후 코드를 수정했다면 원래 프로젝트에서 Git으로 업로드하여 최신 변경 사항을 포함합니다.

## 이후 변경 사항 업로드

```powershell
git status --short
git add .
git diff --cached --stat
git diff --cached
git commit -m "Describe the change"
git push
```

새 PC에서 실행할 때는 [README.md](README.md)의 설치 과정을 따릅니다. API 암호문은 Windows 사용자 계정에 연결되므로 각 PC에서 API 설정을 다시 입력합니다. `private_state/`와 `records/`의 로컬 보관은 소스 버전 관리와 별개입니다.
