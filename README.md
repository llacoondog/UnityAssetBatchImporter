# Asset Batch Importer

Unity Asset Store의 **My Assets**에서 사용자가 부여한 라벨을 읽어, 선택한 에셋을 현재 Unity 프로젝트에 일괄 설치하는 에디터 도구입니다.

## 설치

기존 Asset Batch Importer가 설치되어 있으면 먼저 제거합니다. 설치가 끝나면 `Tools > Asset Batch Importer`를 엽니다.

### Package Manager에서 Git URL로 설치

1. Git이 설치된 환경에서 Unity의 **Package Manager**를 엽니다.
2. `+ > Install package from git URL…`을 선택합니다.
3. 아래 주소를 붙여넣고 **Install**을 누릅니다.

```text
https://github.com/llacoondog/UnityAssetBatchImporter.git#v0.3.3
```

### .unitypackage로 설치

1. [v0.3.3 Release](https://github.com/llacoondog/UnityAssetBatchImporter/releases/tag/v0.3.3)에서 `UnityAssetBatchImporter_v0.3.3.unitypackage`를 다운로드합니다.
2. Unity에서 `Assets > Import Package > Custom Package…`를 열고 다운로드한 파일을 선택합니다.
3. **Import**를 누르면 `Assets/Plugins/llacoondog/AssetBatchImporter`에 설치됩니다.

## 라벨로 일괄 설치

1. Unity Asset Store의 **My Assets**에서 함께 설치할 에셋에 같은 라벨을 붙입니다.
2. Unity 에디터에서 같은 Unity 계정에 로그인한 뒤 **My Assets 새로고침**을 누릅니다.
3. **Label** 드롭다운에서 라벨을 선택하고 설치할 에셋에 체크합니다.
4. **Download & Import Selected**를 누릅니다. 다운로드가 필요한 에셋을 받은 뒤 선택한 에셋을 차례로 임포트합니다.

이미 다운로드한 에셋만 설치하려면 **캐시된 것만 Import**를 누릅니다. 현재 프로젝트에 이미 임포트된 에셋은 건너뜁니다.
