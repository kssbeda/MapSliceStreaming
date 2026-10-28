#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MapSliceStreaming.Editor
{
    // 저작용 씬을 기준으로 개별 베이크 단계를 순서대로 실행
    public sealed class MapSlicePipelineWindow : EditorWindow
    {
        private const string ResultKey = "MapSlice.Pipeline.LastResult";
        private const string BusyKey = "MapSlice.Pipeline.IsRunning";

        private Transform source;
        private string mapId = "MainField";
        private bool landscape = true;
        private bool seam = true;
        private bool slices = true;
        private bool hlod = true;
        private bool nav = true;
        private bool world = true;
        private bool addressables = true;

        private sealed class RunOptions
        {
            public string ScenePath;
            public string RootName;
            public string MapId;
            public bool Landscape;
            public bool Seam;
            public bool Slices;
            public bool Hlod;
            public bool Nav;
            public bool World;
            public bool Addressables;
        }

        [InitializeOnLoadMethod]
        private static void RecoverInterruptedRun()
        {
            // 스크립트 재컴파일로 실행이 끊긴 경우 잠금 상태만 복구
            if (!SessionState.GetBool(BusyKey, false))
                return;

            SessionState.SetBool(BusyKey, false);
            SessionState.SetString(ResultKey, "스크립트 재로드로 파이프라인이 중단되었습니다. 완료된 단계는 체크 해제하고 재실행하세요.");
        }

        [MenuItem("Tools/Map Streaming/14. V5.2 Pipeline Tool")]
        private static void Open() => GetWindow<MapSlicePipelineWindow>("Map Pipeline");

        private void OnGUI()
        {
            EditorGUILayout.LabelField("V5.3 Safe Pipeline — Save & Continue 수정", EditorStyles.boldLabel);
            source = (Transform)EditorGUILayout.ObjectField("MapSource_1km", source, typeof(Transform), true);
            mapId = EditorGUILayout.TextField("Map ID", mapId);
            landscape = EditorGUILayout.ToggleLeft("1. 산악 + 해안 지형 갱신", landscape);
            seam = EditorGUILayout.ToggleLeft("2. 높이 + 텍스처 경계 보정", seam);
            slices = EditorGUILayout.ToggleLeft("3. Slice Bake", slices);
            hlod = EditorGUILayout.ToggleLeft("4. HLOD Bake", hlod);
            nav = EditorGUILayout.ToggleLeft("5. NavMesh Bake", nav);
            world = EditorGUILayout.ToggleLeft("6. World Map Bake", world);
            addressables = EditorGUILayout.ToggleLeft("7. Addressables Build", addressables);

            EditorGUILayout.HelpBox("Sample_Authoring 씬에서 실행합니다. 기존 1~11번 개별 메뉴는 유지됩니다. Bake 중 씬 참조가 변경되면 안전하게 중단하고 로그를 남깁니다.", MessageType.Info);

            bool busy = SessionState.GetBool(BusyKey, false);
            using (new EditorGUI.DisabledScope(source == null || string.IsNullOrWhiteSpace(mapId) || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || busy))
            {
                if (GUILayout.Button("선택한 단계 일괄 실행", GUILayout.Height(36)))
                    ScheduleRun();
            }

            EditorGUILayout.LabelField(SessionState.GetString(ResultKey, "대기 중"), EditorStyles.wordWrappedLabel);
            if (busy)
                EditorGUILayout.HelpBox("파이프라인 실행 중입니다. Console에서 진행 단계를 확인하세요.", MessageType.Info);
        }

        private void ScheduleRun()
        {
            // GUI 이벤트 종료 후 실행하므로 에셋 재임포트와 GUI 처리가 충돌하지 않음
            if (source == null)
                return;

            Scene scene = source.gameObject.scene;
            if (!scene.IsValid() || !scene.isLoaded || string.IsNullOrEmpty(scene.path))
            {
                Debug.LogError("[MapSlice] 저작용 씬을 먼저 저장하세요.");
                return;
            }
            if (SceneManager.GetActiveScene().path != scene.path)
            {
                Debug.LogError("[MapSlice] 저작용 씬을 활성 씬으로 선택한 다음 실행하세요.");
                return;
            }
            if (!EditorUtility.DisplayDialog("Map Slice Pipeline", "선택한 단계의 기존 데이터를 갱신합니다. " +
                "프로젝트를 백업한 상태라면 계속하세요.", "실행", "취소")) return;

            RunOptions options = new RunOptions
            {
                ScenePath = scene.path,
                RootName = source.name,
                MapId = mapId.Trim(),
                Landscape = landscape,
                Seam = seam,
                Slices = slices,
                Hlod = hlod,
                Nav = nav,
                World = world,
                Addressables = addressables
            };

            SessionState.SetBool(BusyKey, true);
            Report("시작 준비");
            EditorApplication.delayCall += () => Execute(options);
        }

        private static void Execute(RunOptions options)
        {
            try
            {
                // 저장 창이 뜨더라도 기존 Transform을 재사용하지 않음
                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                {
                    Report("사용자가 씬 저장을 취소해 실행하지 않았습니다.");
                    return;
                }

                string folder = Path.GetDirectoryName(options.ScenePath)?.Replace('\\', '/');
                if (string.IsNullOrEmpty(folder))
                    throw new InvalidOperationException("저작용 씬 폴더를 찾을 수 없습니다.");

                string catalogPath = "Assets/MapSlices/" + options.MapId + "/" + options.MapId + "_Catalog.asset";

                // 제작용 씬 작업이 있을 때에만 바다 미리보기 생성
                if (options.Landscape || options.Seam || options.Slices || options.World)
                {
                    RequireSource(options);
                    Report("바다 미리보기 준비");
                    MapSliceSampleCoastSetup.SetupAuthoringOcean();
                }

                if (options.Landscape)
                {
                    Report("1/7 산악 지형 갱신");
                    MapSliceSampleLandscapeUpgrade.ApplyToRoot(RequireSource(options), folder);
                }
                if (options.Seam)
                {
                    Report("2/7 Terrain 경계 보정");
                    MapSliceSeamRepairWindow.Repair(RequireSource(options));
                }
                if (options.Slices)
                {
                    Report("3/7 Slice Bake");
                    MapSliceBakerWindow.RunAutomated(RequireSource(options), options.MapId);
                }

                MapSliceCatalog catalog = AssetDatabase.LoadAssetAtPath<MapSliceCatalog>(catalogPath);
                if (catalog == null || catalog.Slices == null || catalog.Slices.Count == 0)
                    throw new InvalidOperationException("Catalog가 없거나 비어 있습니다: " + catalogPath);

                if (options.Hlod)
                {
                    Report("4/7 HLOD Bake");
                    MapSliceHlodBakerWindow.RunAutomated(catalog);
                    catalog = RequireCatalog(catalogPath);
                    int proxies = 0;
                    foreach (MapSliceEntry entry in catalog.Slices)
                        if (entry.HasProxy)
                            ++proxies;
                    if (proxies == 0)
                        throw new InvalidOperationException("HLOD 프록시가 생성되지 않았습니다.");
                }
                if (options.Nav)
                {
                    Report("5/7 NavMesh Bake");
                    MapSliceNavMeshBakerWindow.RunAutomated(RequireCatalog(catalogPath));
                    catalog = RequireCatalog(catalogPath);
                    int navCount = 0;
                    foreach (MapSliceEntry entry in catalog.Slices)
                        if (entry.HasNavMesh)
                            ++navCount;
                    if (navCount == 0)
                        throw new InvalidOperationException("NavMesh가 생성되지 않았습니다.");
                }
                if (options.World)
                {
                    Report("6/7 World Map Bake");
                    MapSliceWorldMapBakerWindow.RunAutomated(RequireCatalog(catalogPath), RequireSource(options), options.MapId);
                }

                AssetDatabase.SaveAssets();
                SaveAuthoringScene(options.ScenePath);

                if (options.Addressables)
                {
                    // 빌드 이후 씬의 Transform/Component 참조는 사용하지 않음
                    Report("7/7 Addressables Build");
                    AddressableAssetSettings.BuildPlayerContent(out var result);
                    if (result == null || !string.IsNullOrEmpty(result.Error))
                        throw new InvalidOperationException("Addressables 빌드 실패: " + (result == null ? "결과 없음" : result.Error));
                }

                Report("완료: 선택한 단계의 작업이 끝났습니다.");
                Debug.Log("[MapSlice] 파이프라인 완료. Runtime 씬에서 로딩을 확인하세요.");
            }
            catch (Exception exception)
            {
                string failedStep = SessionState.GetString(ResultKey, "알 수 없음");
                Report("실패: " + failedStep + " / " + exception.Message);
                Debug.LogException(exception);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                SessionState.SetBool(BusyKey, false);
            }
        }

        private static Transform RequireSource(RunOptions options)
        {
            // Save & Continue 후에도 저장한 씬 경로에서 루트를 새로 찾음
            Scene scene = SceneManager.GetSceneByPath(options.ScenePath);
            if (!scene.IsValid() || !scene.isLoaded)
                throw new InvalidOperationException("저작용 씬이 언로드되었습니다: " + options.ScenePath + ". 씬을 다시 열고 완료된 단계는 체크 해제한 뒤 진행하세요.");

            Transform found = null;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root.name != options.RootName)
                    continue;
                if (found != null)
                    throw new InvalidOperationException("같은 이름의 Source Root가 여러 개 있습니다: " + options.RootName);
                found = root.transform;
            }
            if (found == null)
                throw new InvalidOperationException("Source Root를 찾지 못했습니다: " + options.RootName);
            if (!found.gameObject.activeSelf)
                found.gameObject.SetActive(true);
            return found;
        }

        private static MapSliceCatalog RequireCatalog(string catalogPath)
        {
            MapSliceCatalog catalog = AssetDatabase.LoadAssetAtPath<MapSliceCatalog>(catalogPath);
            if (catalog == null || catalog.Slices == null || catalog.Slices.Count == 0)
                throw new InvalidOperationException("Catalog 참조가 유효하지 않습니다: " + catalogPath);
            return catalog;
        }

        private static void SaveAuthoringScene(string scenePath)
        {
            Scene scene = SceneManager.GetSceneByPath(scenePath);
            if (!scene.IsValid() || !scene.isLoaded)
                throw new InvalidOperationException("씬 저장 전에 저작용 씬이 언로드되었습니다: " + scenePath);
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene))
                throw new InvalidOperationException("저작용 씬 저장 실패: " + scenePath);
        }

        private static void Report(string message)
        {
            SessionState.SetString(ResultKey, message);
            Debug.Log("[MapSlice Pipeline] " + message);
        }
    }
}
#endif