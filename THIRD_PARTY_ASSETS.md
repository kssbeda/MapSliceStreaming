# MapSliceStreaming — Third-Party Assets

## 1. 개요

MapSliceStreaming은 개인이 제작한 Unity 기술 포트폴리오입니다.

월드 스트리밍, LOD 관리, 캐릭터 가림 셰이더, 지도 시스템 등의 구현 코드와 외부에서 제공된 자연환경 에셋을 함께 사용합니다.

## 2. 외부 자연환경 에셋

프로젝트에서 사용한 주요 모델에는 다음과 같은 자연환경 리소스가 있습니다.

| 에셋              | 역할         |
| --------------- | ---------- |
| JapaneseCypress | 침엽수 계열 나무  |
| SawtoothOak     | 활엽수 계열 나무  |
| Ginkgo_wood     | 은행나무       |
| Mugwort         | 지면 식생      |
| ViolaGrypoceras | 지면 식생 및 꽃  |
| Decayed_wood_E  | 고목 및 나무 소품 |
| FallenLeaves    | 낙엽         |
| Rock_A–D        | 바위 소품      |

개발 과정에서는 Unity Asset Store에서 제공된 자연환경 에셋 패키지를 사용했습니다.

## 3. 직접 구현한 기술

프로젝트의 주요 기술 구현은 다음과 같습니다.

- 월드 청크 동적 로딩 및 언로딩
- Addressables 기반 리소스 관리
- 실제 자연 에셋의 5단계 LOD 구성
- 미로드 청크의 원거리 숲 렌더링
- 캐릭터 가림 셰이더와 제어 코드
- 미니맵 및 전체 지도
- 목적지 설정 및 경로 안내
- Unity Editor 자동화 및 검사 도구

## 4. GitHub 공개 범위

### 공개 대상

- 직접 작성한 C# 핵심 구현 코드
- 직접 작성한 HLSL / ShaderLab 코드
- 아키텍처 및 최적화 설명 문서
- 실제 게임 실행 화면
- 공개 조건을 충족하는 시연 영상

### 공개 제외 대상

- Unity Asset Store 원본 FBX 및 기타 모델
- 원본 텍스처와 머티리얼
- 원본 프리팹 및 UnityPackage
- 개별 라이선스로 재배포가 제한된 에셋
- 비공개 프로젝트 설정과 인증 정보

## 5. 영감을 받은 작품과의 관계

프로젝트는 《더 위쳐 3: 와일드 헌트》의 리마스터 버전을 플레이하면서 오픈 월드 탐험 및 자연환경 표현에서 영감을 받아 시작했습니다.

다만 원작 게임의 소스 코드, 캐릭터, 모델, 텍스처 및 게임 고유 자산을 사용하거나 복제한 프로젝트는 아닙니다.

독립적인 Unity 기반 기술 데모로 개발했습니다.

## 6. 참고 자료

- Unity Asset Store 이용 약관: [https://unity.com/legal/as-terms](https://unity.com/legal/as-terms)
- Unity Asset Store EULA FAQ: [https://assetstore.unity.com/browse/eula-faq](https://assetstore.unity.com/browse/eula-faq)
- Unity 공식 문서: [https://docs.unity3d.com/](https://docs.unity3d.com/)
