#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace MapSliceStreaming.Editor
{
    /// <summary>
    /// 실제 나무의 5단계 LOD 전환 기준을 조정합니다.
    /// 원본 메시와 각 LOD의 렌더러 구성은 유지하며,  프리팹 GUID, Terrain 나무 배치 및 셰이더는 변경하지 않습니다.
    /// 기존에 생성된 LOD 프리팹의 화면 상대 크기 기준만 조정합니다.
    /// </summary>
    public sealed class MapSliceLongerHighQualityLOD : EditorWindow
    {
        private const string PrefabFolder = "Assets/MapSliceStreaming/Generated/Showcase/Prefabs/";

        private static readonly string[] Species =
        {
            "JapaneseCypress",
            "SawtoothOak",
            "Ginkgo_wood"
        };

        private static readonly string[] Labels =
        {
            "고품질 - LOD0 유지 범위 약 3.2배",
            "초고품질 - LOD0 유지 범위 약 5배 (PC/스크린샷)",
            "기본값 복원 - LOD0 9%"
        };

        private static readonly float[][] Thresholds =
        {
            new[] { 0.028f, 0.014f, 0.007f, 0.0028f, 0.0007f },
            new[] { 0.018f, 0.009f, 0.0045f, 0.0018f, 0.00045f },
            new[] { 0.090f, 0.045f, 0.020f, 0.008f, 0.0025f }
        };

        private int selectedPreset;
        private string lastReport = "LOD 설정을 검사한 후 프로파일을 적용하세요.";

        [MenuItem("Tools/Map Streaming/나무 LOD 품질 설정")]
        private static void Open()
        {
            GetWindow<MapSliceLongerHighQualityLOD>("나무 LOD 품질");
        }

        private static string AssetPath(string species)
        {
            return PrefabFolder + "_" + species + "_LOD5.prefab";
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("나무 LOD 전환 거리 설정",EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("나무 프리팹 3종의 LOD 전환 기준만 변경합니다. 원본 메시, 프리팹 GUID, Terrain 나무 배치, 스트리밍 데이터 및 셰이더는 유지됩니다.", MessageType.Info);

            selectedPreset = EditorGUILayout.Popup("LOD 품질", selectedPreset, Labels);

            var heights = Thresholds[selectedPreset];

            EditorGUILayout.LabelField("LOD0~4 전환 기준: " + string.Join(" / ", Array.ConvertAll(heights, x => (x * 100f).ToString("0.###") + "%")));
            EditorGUILayout.HelpBox("기본 전환 기준은 9 / 4.5 / 2 / 0.8 / 0.25%입니다. 기준값이 낮을수록 고품질 LOD가 더 먼 거리까지 유지됩니다.\n 고품질 설정은 GPU 부하가 증가할 수 있으며, 초고품질은 PC 및 시각적 비교 용도로 권장합니다.", MessageType.None);
            EditorGUILayout.TextArea(lastReport, GUILayout.MinHeight(66));

            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                if (GUILayout.Button("1. LOD 설정 검사", GUILayout.Height(34)))
                    Audit();
                if (GUILayout.Button("2. 선택한 LOD 품질 적용", GUILayout.Height(40)))
                    Apply();
                if (GUILayout.Button("3. 기본 LOD 전환 기준 복원", GUILayout.Height(32)))
                    ApplyProfile(2);
            }

            EditorGUILayout.HelpBox("이 설정은 로드된 Terrain의 나무 LOD에 적용됩니다. 미로드 청크의 원거리 숲은 별도 렌더러로 처리됩니다.\n Terrain 빌보드 거리와 런타임 LOD 설정도 함께 확인하세요.", MessageType.Warning);
        }

        private static bool CheckPrefab(string path, out LODGroup group, out string issue)
        {
            group = null;
            issue = null;

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (!prefab)
            {
                issue = "프리팹 누락: " + path;
                return false;
            }

            group = prefab.GetComponent<LODGroup>();
            if (!group)
            {
                issue = "LODGroup 누락: " + path;
                return false;
            }

            LOD[] lods = group.GetLODs();
            if (lods == null || lods.Length != 5)
            {
                issue = "5단계 LOD 구성 오류: " + path;
                return false;
            }

            for (int i = 0; i < lods.Length; i++)
            {
                if (lods[i].renderers == null || lods[i].renderers.Length == 0)
                {
                    issue = $"LOD{i} 렌더러 누락: {path}";
                    return false;
                }

                foreach (var renderer in lods[i].renderers)
                {
                    if (!renderer || !renderer.GetComponent<MeshFilter>() || !renderer.GetComponent<MeshFilter>().sharedMesh)
                    {
                        issue = $"LOD{i} 메시 누락: {path}";
                        return false;
                    }
                }
            }

            return true;
        }

        private void Audit()
        {
            var report = new StringBuilder();
            int valid = 0;

            foreach (string species in Species)
            {
                LODGroup group;
                string issue;
                string path = AssetPath(species);

                if (!CheckPrefab(path, out group, out issue))
                {
                    report.AppendLine(species + ": " + issue);
                    continue;
                }

                valid++;

                LOD[] lods = group.GetLODs();

                report.Append(species + ": ");

                for (int i = 0; i < lods.Length; i++)
                {
                    if (i > 0)
                        report.Append(" / ");

                    report.Append((lods[i].screenRelativeTransitionHeight * 100f).ToString("0.###") + "%");
                }

                report.AppendLine();
            }

            float billboard = -1;

            foreach (Terrain terrain in Terrain.activeTerrains)
            {
                if (terrain && (billboard < 0 || terrain.treeBillboardDistance < billboard))
                    billboard = terrain.treeBillboardDistance;
            }

            if (billboard >= 0)
                report.AppendLine($"Terrain 최소 빌보드 시작 거리: {billboard:0}m");

            report.AppendLine($"LOD Bias: {QualitySettings.lodBias:0.###}");

            // 현재 씬에 로드되지 않은 청크의 TerrainData도 검사합니다.
            int generatedReferences = 0;
            int legacyReferences = 0;
            int treeAssets = 0;

            foreach (string guid in AssetDatabase.FindAssets("t:TerrainData"))
            {
                TerrainData data = AssetDatabase.LoadAssetAtPath<TerrainData>(AssetDatabase.GUIDToAssetPath(guid));
                if (!data || data.treePrototypes == null)
                    continue;

                treeAssets++;

                foreach (TreePrototype prototype in data.treePrototypes)
                {
                    if (!prototype.prefab)
                        continue;

                    string prefabPath = AssetDatabase.GetAssetPath(prototype.prefab);

                    if (prefabPath.StartsWith(PrefabFolder, StringComparison.Ordinal))
                        generatedReferences++;
                    else if (prefabPath.Contains("Showcase"))
                        legacyReferences++;
                }
            }

            report.AppendLine($"TerrainData 검사: {treeAssets}개");
            report.AppendLine($"LOD 프리팹 참조: {generatedReferences}슬롯");
            report.AppendLine($"기타 Showcase 프리팹 참조: {legacyReferences}슬롯");

            if (treeAssets > 0 && generatedReferences == 0)
                report.AppendLine("주의: 최적화된 LOD 프리팹을 참조하는 TerrainData가 없습니다. 나무 프리팹 연결 상태를 확인하세요.");

            lastReport = report.ToString();

            Debug.Log($"LOD 검사 완료: {valid}/{Species.Length}개");
        }

        private void Apply()
        {
            ApplyProfile(selectedPreset);
        }

        private void ApplyProfile(int preset)
        {
            // 일부 프리팹에만 설정되는 것을 방지하기 위해 모든 나무 프리팹을 먼저 검사합니다.
            foreach (string species in Species)
            {
                LODGroup group;
                string issue;

                if (!CheckPrefab(AssetPath(species), out group, out issue))
                {
                    lastReport = "LOD 적용 중단: " + issue;
                    Debug.LogError(lastReport);
                    return;
                }
            }

            string preview = Labels[preset];

            if (!EditorUtility.DisplayDialog("나무 LOD 품질 설정",
                $"선택한 설정: {preview}\n\n" +
                "나무 프리팹 3종의 LOD 전환 기준을 변경합니다.\n" +
                "메시, GUID 및 나무 배치는 유지됩니다.\n\n" +
                "기존 프리팹은 프로젝트 외부 백업 폴더에 저장합니다.",
                "적용", "취소"))
            {
                return;
            }

            string project = Path.GetDirectoryName(Application.dataPath);
            string backup = Path.Combine(project,  "_LOD_BACKUP", DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));

            try
            {
                AssetDatabase.SaveAssets();

                foreach (string species in Species)
                {
                    string path = AssetPath(species);
                    string disk = Path.Combine(project, path.Replace('/', Path.DirectorySeparatorChar));

                    if (!File.Exists(disk))
                        throw new IOException("프리팹 파일 누락: " + disk);

                    string target = Path.Combine(backup, path.Replace('/', Path.DirectorySeparatorChar));

                    Directory.CreateDirectory(Path.GetDirectoryName(target));

                    File.Copy(disk, target, false);

                    if (File.Exists(disk + ".meta"))
                        File.Copy(disk + ".meta", target + ".meta", false);
                }

                int changed = 0;

                foreach (string species in Species)
                {
                    string path = AssetPath(species);
                    string beforeGuid = AssetDatabase.AssetPathToGUID(path);
                    GameObject root = PrefabUtility.LoadPrefabContents(path);

                    try
                    {
                        LODGroup group = root.GetComponent<LODGroup>();
                        if (!group)
                            throw new InvalidOperationException("LODGroup 누락: " + species);

                        LOD[] lods = group.GetLODs();
                        if (lods.Length != 5)
                            throw new InvalidOperationException("LOD 단계 변경 감지: " + species);

                        for (int i = 0; i < lods.Length; i++)
                            lods[i].screenRelativeTransitionHeight = Thresholds[preset][i];

                        group.SetLODs(lods);

                        if (!PrefabUtility.SaveAsPrefabAsset(root, path))
                            throw new InvalidOperationException("프리팹 저장 실패: " + path);
                    }
                    finally
                    {
                        PrefabUtility.UnloadPrefabContents(root);
                    }

                    if (AssetDatabase.AssetPathToGUID(path) != beforeGuid)
                        throw new InvalidOperationException("프리팹 GUID 변경 감지: " + species);

                    changed++;
                }

                AssetDatabase.SaveAssets();

                foreach (var terrain in Terrain.activeTerrains)
                {
                    if (terrain)
                        terrain.Flush();
                }

                float lod0 = Thresholds[preset][0] * 100f;
                string successMessage = $"LOD 설정 적용 완료: {changed}/{Species.Length}개 (LOD0 {lod0:0.###}%)";
                lastReport = successMessage + "\n백업 경로: " + backup + "\n동일한 카메라 위치에서 품질을 비교하고 Android 성능을 확인하세요.";

                Debug.Log(successMessage);

                Audit();

                lastReport = successMessage + "\n" + lastReport;
            }
            catch (Exception ex)
            {
                lastReport = $"LOD 설정 적용 실패: {ex.Message}\n" +
                    $"백업 경로: {backup}";

                Debug.LogException(ex);
                Debug.LogError($"LOD 설정 적용 실패: {ex.Message}");
            }
        }
    }
}
#endif
