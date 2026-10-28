#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace MapSliceStreaming.Editor
{
    /// <summary>
    /// 실제 자연 에셋의 LOD 및 캐릭터 가림 설정을 검사하고 복구합니다.
    /// 기존 Terrain의 나무 배치, 스트리밍 카탈로그, Addressables 및 지형 높이 데이터는 변경하지 않습니다.
    /// 프리팹은 기존 에셋 경로에서 다시 생성하여 GUID와 참조 관계를 유지하도록 구성했습니다.
    /// </summary>
    public sealed class MapSliceLODAndShaderRepair : EditorWindow
    {
        const string Src = "Assets/Polyeler/EssentialNaturePack/Prefabs/";
        const string Gen = "Assets/MapSliceStreaming/Generated/Showcase";
        const string Prefabs = Gen + "/Prefabs/";

        static readonly string[] Trees =
        {
            "JapaneseCypress", "SawtoothOak", "Ginkgo_wood"
        };

        static readonly string[] Props =
        {
            "Mugwort", "ViolaGrypoceras", "Decayed_wood_E",
            "FallenLeaves", "Rock_A", "Rock_B", "Rock_C", "Rock_D"
        };

        static readonly string[] All = Trees.Concat(Props).ToArray();

        [SerializeField] MapSliceCatalog catalog;
        [SerializeField] MapSliceStreamer streamer;
        [SerializeField] Transform player;
        [SerializeField] Camera gameCamera;
        [SerializeField] bool softFade = true;

        string status = "검사 전";

        static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;
        static string BackupRoot => Path.Combine(ProjectRoot, "_SAFE_BACKUP");

        [MenuItem("Tools/Map Streaming/LOD 및 캐릭터 가림 관리")]
        static void Open()
        {
            GetWindow<MapSliceLODAndShaderRepair>("LOD 및 캐릭터 가림");
        }

        void OnEnable()
        {
            if (!streamer)
            {
                foreach (var x in Resources.FindObjectsOfTypeAll<MapSliceStreamer>())
                {
                    if (x && !EditorUtility.IsPersistent(x) && x.gameObject.scene.IsValid())
                    {
                        streamer = x;
                        break;
                    }
                }
            }

            if (!player)
            {
                foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
                {
                    if (t && !EditorUtility.IsPersistent(t) && t.gameObject.scene.IsValid() && (t.name == "Explorer" || t.name == "Player"))
                    {
                        player = t;
                        break;
                    }
                }
            }

            if (!gameCamera)
                gameCamera = Camera.main;
        }

        void OnGUI()
        {
            EditorGUILayout.LabelField("자연 에셋 LOD 및 캐릭터 가림", EditorStyles.boldLabel);

            EditorGUILayout.HelpBox("기존 경로와 GUID를 유지하면서 LOD 프리팹을 다시 생성합니다. Terrain 나무 배치와 Addressables는 유지됩니다. 작업 전 프로젝트를 백업하세요.",
                MessageType.Info);

            catalog = (MapSliceCatalog)EditorGUILayout.ObjectField("전체 청크 카탈로그", catalog, typeof(MapSliceCatalog), false);
            streamer = (MapSliceStreamer)EditorGUILayout.ObjectField("씬 스트리머", streamer, typeof(MapSliceStreamer), true);
            player = (Transform)EditorGUILayout.ObjectField("캐릭터", player, typeof(Transform), true);
            gameCamera = (Camera)EditorGUILayout.ObjectField("게임 카메라", gameCamera, typeof(Camera), true);
            softFade = EditorGUILayout.Toggle("나무 반투명 처리", softFade);
            EditorGUILayout.HelpBox(status, MessageType.None);

            if (GUILayout.Button("1. 에셋 및 5단계 LOD 검사", GUILayout.Height(34)))
                Audit();

            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                if (GUILayout.Button("2. LOD 프리팹 11개 다시 생성", GUILayout.Height(42)))
                    Build();

                if (GUILayout.Button("3. Terrain 나무 프리팹 연결 복구", GUILayout.Height(33)))
                    Relink();

                if (GUILayout.Button("4. 캐릭터 가림 셰이더 적용", GUILayout.Height(36)))
                    ApplyMode(softFade);

                if (GUILayout.Button("5. Android용 컷아웃 적용", GUILayout.Height(30)))
                    ApplyMode(false);

                if (GUILayout.Button("6. 백업 복원", GUILayout.Height(30)))
                    Restore();
            }

            EditorGUILayout.HelpBox("반투명 모드는 부드럽지만 오버드로우 비용이 증가합니다. 컷아웃 모드는 캐릭터를 가리는 영역만 제거하며 Android에 유리합니다.",MessageType.Warning);
        }

        static string OptPath(string name) => Prefabs + "_" + name + "_LOD5.prefab";
        static GameObject Opt(string name) => AssetDatabase.LoadAssetAtPath<GameObject>(OptPath(name));
        static string PathToPhysical(string relative) => Path.Combine(ProjectRoot, relative.Replace('/', Path.DirectorySeparatorChar));

        static void BackUp(string path)
        {
            string original = PathToPhysical(path);
            if (!File.Exists(original))
                return;

            string target = Path.Combine(BackupRoot, path.Replace('/', Path.DirectorySeparatorChar));

            Directory.CreateDirectory(Path.GetDirectoryName(target));

            if (!File.Exists(target))
                File.Copy(original, target);

            if (File.Exists(original + ".meta") && !File.Exists(target + ".meta"))
                File.Copy(original + ".meta", target + ".meta");
        }

        static long Triangles(LOD lod)
        {
            long result = 0;

            foreach (var r in lod.renderers)
            {
                if (!r)
                    continue;

                var filter = r.GetComponent<MeshFilter>();
                if (!filter || !filter.sharedMesh)
                    continue;

                var mesh = filter.sharedMesh;

                for (int k = 0; k < mesh.subMeshCount; k++)
                {
                    if (mesh.GetTopology(k) == MeshTopology.Triangles)
                        result += (long)mesh.GetIndexCount(k) / 3;
                }
            }

            return result;
        }

        void Audit()
        {
            var text = new StringBuilder("Name,Source,Generated,Stages,TrianglesLOD0,TrianglesLOD1,TrianglesLOD2,TrianglesLOD3,TrianglesLOD4,PrefabGUID\n");

            int missing = 0;
            int five = 0;

            foreach (string name in All)
            {
                var src = AssetDatabase.LoadAssetAtPath<GameObject>(Src + name + ".prefab");
                var opt = Opt(name);

                LOD[] lods = opt && opt.GetComponent<LODGroup>() ? opt.GetComponent<LODGroup>().GetLODs() : new LOD[0];

                if (!src || !opt)
                    missing++;

                if (lods.Length == 5)
                    five++;

                var values = new List<string>
                {
                    name,
                    src ? "YES" : "MISSING",
                    opt ? "YES" : "MISSING",
                    lods.Length.ToString()
                };

                for (int i = 0; i < 5; i++)
                    values.Add(i < lods.Length ? Triangles(lods[i]).ToString() : "-");

                values.Add(AssetDatabase.AssetPathToGUID(OptPath(name)));
                text.AppendLine(string.Join(",", values));
            }

            string dir = PathToPhysical(Gen + "/Reports");
            Directory.CreateDirectory(dir);

            string csv = Path.Combine(dir, "_LOD_Audit.csv");
            File.WriteAllText(csv, text.ToString(), Encoding.UTF8);

            AssetDatabase.Refresh();

            status = $"LOD 검사 완료: 정상 {five}/11개, 누락 {missing}개";
            Debug.Log(status);
        }

        void Build()
        {
            if (!EditorUtility.DisplayDialog("LOD 프리팹 생성",
                "프리팹 11개를 기존 경로에서 다시 생성합니다. 나무 배치는 유지되며, 기존 파일은 _SAFE_BACKUP에 백업합니다.",
                "생성", "취소"))
                return;

            try
            {
                if (!Shader.Find("MapSlice/NatureOcclusion"))
                    throw new InvalidOperationException("캐릭터 가림 셰이더를 찾을 수 없습니다.");

                foreach (string name in All)
                {
                    if (!AssetDatabase.LoadAssetAtPath<GameObject>(Src + name + ".prefab"))
                        throw new InvalidOperationException($"원본 프리팹 누락: {name}");
                }

                AssetDatabase.SaveAssets();

                foreach (string name in All)
                    BackUp(OptPath(name));

                foreach (string guid in AssetDatabase.FindAssets("t:Material", new[] { Gen + "/Materials" }))
                {
                    BackUp(AssetDatabase.GUIDToAssetPath(guid));
                }

                MapSliceAssetOptimizer.EnableSourceReadability();
                MapSliceAssetOptimizer.ValidateSources();

                var report = new StringBuilder("Prefab,LOD0Tris,LOD1Tris,LOD2Tris,LOD3Tris,LOD4Tris,GUID\n");

                foreach (string name in All)
                {
                    bool isTree = Trees.Contains(name);
                    var source = AssetDatabase.LoadAssetAtPath<GameObject>(Src + name + ".prefab");

                    string before = AssetDatabase.AssetPathToGUID(OptPath(name));
                    var built = MapSliceAssetOptimizer.BuildOptimizedPrefab(source, name, isTree, true);

                    if (!built)
                        throw new InvalidOperationException($"LOD 프리팹 생성 실패: {name}");

                    string after = AssetDatabase.AssetPathToGUID(OptPath(name));

                    if (!String.IsNullOrEmpty(before) && before != after)
                        throw new InvalidOperationException($"프리팹 GUID 변경 감지: {name}");

                    var group = built.GetComponent<LODGroup>();
                    if (!group || group.GetLODs().Length != 5)
                        throw new InvalidOperationException($"5단계 LOD 구성 실패: {name}");

                    var lods = group.GetLODs();
                    var counts = lods.Select(Triangles).ToArray();

                    if (counts.Any(x => x <= 0))
                        throw new InvalidOperationException($"빈 LOD 메시 발견: {name}");

                    if (isTree && counts[0] < counts[4])
                        Debug.LogWarning($"LOD 삼각형 수 확인 필요: {name}");

                    report.AppendLine(name + "," + string.Join(",", counts.Select(x => x.ToString())) + "," + after);
                }

                var reportDir = PathToPhysical(Gen + "/Reports");
                Directory.CreateDirectory(reportDir);

                File.WriteAllText(Path.Combine(reportDir, "_Rebuild_LODs.csv"),report.ToString(), Encoding.UTF8);

                AssetDatabase.SaveAssets();
                SetMaterials(softFade);

                foreach (var t in Terrain.activeTerrains)
                {
                    if (!t)
                        continue;

                    t.Flush();

                    if (t.terrainData)
                        EditorUtility.SetDirty(t.terrainData);
                }

                AssetDatabase.SaveAssets();
                EditorSceneManager.MarkAllScenesDirty();

                status = "LOD 프리팹 생성 완료: 11개, 각 5단계 (GUID 유지)";
                Debug.Log(status);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                status = $"LOD 프리팹 생성 실패: {ex.Message}";
            }
            finally
            {
                MapSliceAssetOptimizer.DisableSourceReadability();
                AssetDatabase.Refresh();
            }
        }

        static int Species(GameObject prefab)
        {
            if (!prefab)
                return -1;

            string name = (prefab.name + " " + AssetDatabase.GetAssetPath(prefab)).ToLowerInvariant();

            if (name.Contains("cypress") || name.Contains("pine") || name.Contains("spruce") || name.Contains("conifer"))
                return 0;

            if (name.Contains("ginkgo") || name.Contains("birch"))
                return 2;

            if (name.Contains("oak") || name.Contains("broadleaf") || name.Contains("deciduous"))
                return 1;

            return -1;
        }

        IEnumerable<TerrainData> TerrainDataInProject()
        {
            var list = new HashSet<TerrainData>();

            if (catalog)
            {
                foreach (var slice in catalog.Slices)
                {
                    if (slice == null || slice.Prefab == null)
                        continue;

                    string path = AssetDatabase.GUIDToAssetPath(slice.Prefab.AssetGUID);
                    var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);

                    if (!root)
                        continue;

                    foreach (var terrain in root.GetComponentsInChildren<Terrain>(true))
                    {
                        if (terrain && terrain.terrainData)
                            list.Add(terrain.terrainData);
                    }
                }
            }

            foreach (var t in Resources.FindObjectsOfTypeAll<Terrain>())
            {
                if (t && !EditorUtility.IsPersistent(t) && t.gameObject.scene.IsValid() && t.terrainData)
                    list.Add(t.terrainData);
            }

            return list;
        }

        void Relink()
        {
            if (!catalog)
            {
                Debug.LogError("나무 연결 실패: 전체 청크 카탈로그 미지정");
                return;
            }

            var prefabs = Trees.Select(Opt).ToArray();
            if (prefabs.Any(x => !x))
            {
                Debug.LogError("나무 연결 실패: LOD 프리팹 누락");
                return;
            }

            var planned = new Dictionary<TerrainData, TreePrototype[]>();
            int slots = 0;
            int unknown = 0;

            foreach (var td in TerrainDataInProject())
            {
                var old = td.treePrototypes;
                var copies = old.Select(p => new TreePrototype(p)).ToArray();
                var counts = new int[old.Length];

                foreach (var tree in td.treeInstances)
                {
                    if (tree.prototypeIndex >= 0 && tree.prototypeIndex < counts.Length)
                        counts[tree.prototypeIndex]++;
                }

                bool changed = false;

                for (int i = 0; i < old.Length; i++)
                {
                    // 수종을 확인할 수 없는 나무는 임의로 연결하지 않습니다.
                    if (counts[i] == 0 || old[i].prefab == null)
                        continue;

                    int species = Species(old[i].prefab);
                    if (species < 0)
                    {
                        unknown += counts[i];
                        continue;
                    }

                    if (old[i].prefab == prefabs[species])
                        continue;

                    copies[i].prefab = prefabs[species];
                    slots++;
                    changed = true;
                }

                if (changed)
                    planned.Add(td, copies);
            }

            if (planned.Count == 0)
            {
                status = $"나무 연결 변경 없음 (미분류 {unknown}개)";
                Debug.Log(status);
                return;
            }

            if (!EditorUtility.DisplayDialog("Terrain 나무 연결 복구",
                $"{planned.Count}개 TerrainData의 나무 슬롯 {slots}개를 변경합니다. 기존 나무 배치는 유지하고 수정 대상은 백업합니다.",
                "연결", "취소"))
                return;

            try
            {
                AssetDatabase.SaveAssets();

                foreach (var td in planned.Keys)
                {
                    var path = AssetDatabase.GetAssetPath(td);

                    if (String.IsNullOrEmpty(path) ||!path.StartsWith("Assets/", StringComparison.Ordinal))
                        throw new InvalidOperationException($"TerrainData 경로 오류: {td.name}");

                    BackUp(path);
                }

                int count = 0;

                foreach (var pair in planned)
                {
                    count += pair.Key.treeInstanceCount;

                    Undo.RegisterCompleteObjectUndo(pair.Key, "Terrain 나무 프리팹 연결 복구");

                    pair.Key.treePrototypes = pair.Value;
                    EditorUtility.SetDirty(pair.Key);
                }

                foreach (var terrain in Terrain.activeTerrains)
                {
                    if (terrain)
                        terrain.Flush();
                }

                AssetDatabase.SaveAssets();
                EditorSceneManager.MarkAllScenesDirty();

                status = $"나무 연결 완료: {slots}슬롯, {planned.Count} TerrainData " +
                         $"(배치 {count}개 유지, 미분류 {unknown}개)";

                Debug.Log(status);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                status = $"나무 연결 실패: {ex.Message}";
            }
        }

        static IEnumerable<Material> MaterialsOn(GameObject prefab)
        {
            if (!prefab)
                yield break;

            foreach (var renderer in prefab.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (!renderer)
                    continue;

                foreach (var material in renderer.sharedMaterials)
                {
                    if (material)
                        yield return material;
                }
            }
        }

        static void Configure(Material mat, bool soften)
        {
            string path = AssetDatabase.GetAssetPath(mat);

            // 생성된 머티리얼만 수정하며 에셋스토어 원본은 유지합니다.
            if (!path.StartsWith(Gen + "/Materials/", StringComparison.Ordinal))
                return;

            BackUp(path);

            Texture diffuse = mat.HasProperty("_BaseMap") ? mat.GetTexture("_BaseMap") : null;
            Color color = mat.HasProperty("_BaseColor") ? mat.GetColor("_BaseColor") : Color.white;
            float alphaClip = mat.HasProperty("_AlphaClip") ? mat.GetFloat("_AlphaClip") : 0f;
            float cutoff = mat.HasProperty("_Cutoff") ? mat.GetFloat("_Cutoff") : .38f;
            float cull = mat.HasProperty("_Cull") ? mat.GetFloat("_Cull") : 2f;
            Shader target = Shader.Find("MapSlice/NatureOcclusion");

            if (!target)
                throw new InvalidOperationException("캐릭터 가림 셰이더 누락");

            // 생성 머티리얼 이름에 저장된 원본 GUID로 노멀맵을 다시 연결합니다. 원본 머티리얼은 수정하지 않습니다.
            Texture normal = null;
            float normalStrength = 1f;

            string key = Path.GetFileNameWithoutExtension(path);
            if (key.Length >= 32)
            {
                string guid = key.Substring(key.Length - 32);
                string sourcePath = AssetDatabase.GUIDToAssetPath(guid);

                Material original = AssetDatabase.LoadAssetAtPath<Material>(sourcePath);

                if (original && original.HasProperty("_BumpMap"))
                {
                    normal = original.GetTexture("_BumpMap");

                    if (original.HasProperty("_BumpScale"))
                        normalStrength = original.GetFloat("_BumpScale");
                }
            }

            mat.shader = target;

            if (diffuse)
                mat.SetTexture("_BaseMap", diffuse);

            mat.SetColor("_BaseColor", color);
            mat.SetFloat("_AlphaClip", alphaClip);
            mat.SetFloat("_Cutoff", cutoff);
            mat.SetFloat("_Cull", cull);

            if (normal)
                mat.SetTexture("_BumpMap", normal);

            mat.SetFloat("_BumpScale", normalStrength);
            mat.SetFloat("_UseNormalMap", normal ? 1f : 0f);
            mat.SetFloat("_SoftFade", soften ? 1f : 0f);
            mat.SetFloat("_Occlude", 1f);
            mat.SetFloat("_SrcBlend", (float)(soften ? BlendMode.SrcAlpha : BlendMode.One));
            mat.SetFloat("_DstBlend", (float)(soften ? BlendMode.OneMinusSrcAlpha : BlendMode.Zero));
            mat.SetFloat("_ZWrite", soften ? 0f : 1f);
            mat.SetOverrideTag("RenderType", soften ? "Transparent" : "TransparentCutout");
            mat.renderQueue = soften ? (int)RenderQueue.Transparent : (int)RenderQueue.AlphaTest;
            mat.enableInstancing = true;
            EditorUtility.SetDirty(mat);
        }

        void SetMaterials(bool softenTrees)
        {
            var trees = new HashSet<Material>();
            var grounds = new HashSet<Material>();

            foreach (string name in Trees)
                foreach (var mat in MaterialsOn(Opt(name)))
                    trees.Add(mat);

            foreach (string name in Props)
                foreach (var mat in MaterialsOn(Opt(name)))
                    grounds.Add(mat);

            if (trees.Count == 0)
                throw new InvalidOperationException("LOD 나무 머티리얼 누락");

            // 지면 식생과 공유하는 머티리얼은 컷아웃을 유지합니다.
            foreach (var material in trees)
                Configure(material, softenTrees && !grounds.Contains(material));

            foreach (var material in grounds)
                Configure(material, false);

            Debug.Log($"가림 머티리얼 설정 완료: 나무 {trees.Count}개, 식생 {grounds.Count}개");
        }

        void ApplyMode(bool mode)
        {
            if (!Shader.Find("MapSlice/NatureOcclusion"))
            {
                Debug.LogError("캐릭터 가림 설정 실패: 셰이더 누락");
                return;
            }

            try
            {
                SetMaterials(mode);

                if (!streamer)
                {
                    foreach (var s in Resources.FindObjectsOfTypeAll<MapSliceStreamer>())
                    {
                        if (s && !EditorUtility.IsPersistent(s) && s.gameObject.scene.IsValid())
                        {
                            streamer = s;
                            break;
                        }
                    }
                }

                if (!streamer || !player || !gameCamera)
                {
                    status = "머티리얼 적용 완료 (스트리머·캐릭터·카메라 설정 필요)";
                    Debug.LogWarning(status);
                }
                else
                {
                    var oc = streamer.GetComponent<MapSliceCharacterOcclusion>();

                    if (!oc)
                        oc = Undo.AddComponent<MapSliceCharacterOcclusion>(streamer.gameObject);

                    Undo.RecordObject(oc, "캐릭터 가림 설정");

                    oc.character = player;
                    oc.viewCamera = gameCamera;
                    oc.enableOcclusion = true;
                    oc.enabled = true;
                    oc.transitionPixels = 3;
                    oc.extraPaddingPixels = 17;
                    oc.softFadeInnerAlpha = .08f;

                    EditorUtility.SetDirty(oc);

                    // 이전 가림 시스템과 카메라 회피 기능의 중복 실행을 방지합니다.
                    foreach (var behaviour in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
                    {
                        if (!behaviour || EditorUtility.IsPersistent(behaviour) || !behaviour.gameObject.scene.IsValid())
                            continue;

                        string type = behaviour.GetType().Name;

                        if (type == "MapSliceCameraTreeAvoidance" ||type == "MapSliceCleanOcclusionController")
                        {
                            Undo.RecordObject(behaviour, "중복 가림 시스템 비활성화");
                            behaviour.enabled = false;
                            EditorUtility.SetDirty(behaviour);
                        }
                    }

                    EditorSceneManager.MarkAllScenesDirty();

                    status = mode ? "캐릭터 가림 설정 완료: 반투명" : "캐릭터 가림 설정 완료: 컷아웃";
                    Debug.Log(status);
                }

                AssetDatabase.SaveAssets();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                status = $"캐릭터 가림 설정 실패: {ex.Message}";
            }
        }

        void Restore()
        {
            if (!Directory.Exists(BackupRoot))
            {
                Debug.LogError("백업 복원 실패: 백업 폴더 없음");
                return;
            }

            if (!EditorUtility.DisplayDialog("LOD 및 머티리얼 복원",
                "백업된 프리팹과 머티리얼을 복원합니다. 플레이 모드를 종료한 상태에서 진행하세요.",
                "복원", "취소"))
                return;

            try
            {
                int count = 0;

                foreach (string backup in Directory.GetFiles(BackupRoot, "*", SearchOption.AllDirectories))
                {
                    string relative = backup.Substring(BackupRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    string destination = Path.Combine(ProjectRoot, relative);

                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    File.Copy(backup, destination, true);

                    count++;
                }

                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);

                status = $"백업 복원 완료: {count}개 파일";
                Debug.Log(status);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                status = $"백업 복원 실패: {ex.Message}";
            }
        }
    }
}
#endif
