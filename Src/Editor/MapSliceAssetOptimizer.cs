#if !UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

using Object = UnityEngine.Object;

namespace MapSliceStreaming.Editor
{
    /// <summary>
    /// LOD 최적화에 필요한 메시와 프리팹을 별도로 생성합니다.
    /// 에셋스토어에서 제공된 원본 모델과 리소스는 수정하지 않으며, 생성된 리소스를 별도로 관리하도록 구성했습니다.
    /// </summary>
    public static class MapSliceAssetOptimizer
    {
        public const string Folder = "Assets/MapSliceStreaming/Generated/Showcase";

        const string Prefabs = Folder + "/Prefabs";
        const string Meshes = Folder + "/Meshes";
        const string Materials = Folder + "/Materials";
        const string Source = "Assets/Polyeler/EssentialNaturePack/";

        static readonly string[] Names = {"JapaneseCypress", "SawtoothOak", "Ginkgo_wood", "Mugwort", "ViolaGrypoceras", "Decayed_wood_E", "FallenLeaves", "Rock_A", "Rock_B", "Rock_C", "Rock_D"};

        static readonly float[] Heights = {0.090f, 0.045f, 0.020f, 0.008f, 0.0025f};
        static readonly float[] TreeHeights = {0.028f, 0.014f, 0.007f, 0.0028f, 0.0007f};
        static readonly float[] Proportions = {1f, 1f, 0.82f, 0.58f, 0.36f};

        public static string[] AssetNames { get { return Names; } }

        static void Ensure(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder))
                return;

            string par = Path.GetDirectoryName(folder).Replace('\\','/');

            if (!AssetDatabase.IsValidFolder(par))
                Ensure(par);

            AssetDatabase.CreateFolder(par, Path.GetFileName(folder));
        }

        static string Safe(string s)
        {
            char[] arr = s.ToCharArray();
            for (int i = 0; i < arr.Length; i++)
            {
                if (!Char.IsLetterOrDigit(arr[i]) && arr[i] != '_')
                    arr[i] = '_';
            }
            return new string(arr);
        }

        public static GameObject SourcePrefab(string shortName) => AssetDatabase.LoadAssetAtPath<GameObject>(Source + "Prefabs/" + shortName + ".prefab");

        public static void ValidateSources()
        {
            foreach (var n in Names)
            {
                GameObject p = SourcePrefab(n);
                if (!p || p.GetComponentInChildren<MeshFilter>(true) == null)
                    throw new InvalidOperationException($"원본 prefab 또는 mesh 누락: {n}");

                foreach (var mf in p.GetComponentsInChildren<MeshFilter>(true))
                    if (mf && mf.sharedMesh && !mf.sharedMesh.isReadable)
                        throw new InvalidOperationException($"mesh Read/Write 비활성화: {mf.sharedMesh.name}. Nature.fbx > Model > Read/Write Enabled 활성화 후 Apply하세요."
            }

            if (!Shader.Find("MapSlice/OccludingNature"))
                throw new InvalidOperationException("shader import 실패: Unity asset import 완료 후 다시 시도하세요.");
        }

        // LOD 메시 생성에 필요한 경우 원본 FBX의 Read/Write 옵션을 임시로 활성화할 수 있도록 설정합니다.
        // 생성된 메시를 별도 에셋으로 저장한 후에는 원본 FBX의 Read/Write 옵션을 다시 비활성화할 수 있습니다.
        // 이를 통해 Android 빌드에서 불필요한 CPU 측 메시 데이터가 메모리에 유지되는 것을 줄이도록 구성했습니다.
        [MenuItem("Tools/Map Streaming/Nature FBX Read/Write 활성화(LOD 생성용)")]
        public static void EnableSourceReadability()
        {
            const string path = Source+"Meshes/Nature.fbx";
            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (!importer)
            {
                Debug.LogError("Nature.fbx missing at " + path);
                return;
            }

            if (!importer.isReadable)
            {
                importer.isReadable = true;
                importer.SaveAndReimport();
            }

            Debug.Log("Nature.fbx의 Read/Write 옵션을 활성화했습니다. LOD mesh 생성 및 적용을 완료한 후, Android 빌드 전에 Read/Write 옵션을 다시 비활성화하세요.");
        }

        [MenuItem("Tools/Map Streaming/Nature FBX Read/Write 비활성화(LOD 생성 후)")]
        public static void DisableSourceReadability()
        {
            const string path = Source + "Meshes/Nature.fbx";
            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (!importer)
            {
                Debug.LogError("Nature.fbx missing");
                return;
            }

            if (importer.isReadable)
            {
                importer.isReadable = false;
                importer.SaveAndReimport();
            }

            Debug.Log("FBX Read/Write 비활성화 완료 (LOD mesh 유지)");
        }

        static bool IsLeaf(Material m)
        {
            if (!m)
                return true;

            string n = m.name.ToLowerInvariant();

            return !(n.Contains("wood") || n.Contains("bark") || n.Contains("trunk") || n.Contains("rock") || n.Contains("decayed"))
        }

        static Material Adapt(Material src, string kind)
        {
            if (!src)
                throw new InvalidOperationException("Missing renderer material for " + kind);

            if (src.shader && src.shader.name == "MapSlice/OccludingNature")
                return src;

            Ensure(Materials);

            string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(src));
            string path = Materials + "/" + guid + ".mat";

            Material saved = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (saved)
                return saved;

            Shader shader = Shader.Find("MapSlice/OccludingNature");
            if (!shader)
                throw new InvalidOperationException("Occlusion shader를 찾지 못함");

            var mat = new Material(shader);
            mat.name = src.name;
            Texture tex = src.HasProperty("_BaseMap") ? src.GetTexture("_BaseMap") : null;
            if (!tex && src.HasProperty("_MainTex"))
                tex = src.GetTexture("_MainTex");

            if (tex)
                mat.SetTexture("_BaseMap", tex);

            Color tint = Color.white;
            if (src.HasProperty("_BaseColor"))
                tint = src.GetColor("_BaseColor");
            else if (src.HasProperty("_Color"))
                tint = src.GetColor("_Color");

            mat.SetColor("_BaseColor", tint);

            bool leaf = IsLeaf(src);
            mat.SetFloat("_AlphaClip", leaf ? 1f : 0f);
            mat.SetFloat("_Cutoff", leaf ? 0.42f : 0.0f);
            mat.SetFloat("_Cull", leaf ? 0f : 2f);
            mat.SetFloat("_Occlude", 1f);
            mat.enableInstancing = true;
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        /// <summary>
        /// 나뭇잎 메시를 단순히 삼각형 단위로 제거하면 하나의 잎을 구성하는 사각형 메시가 부분적으로 손실되어 중거리에서도 나뭇잎에 큰 구멍이 발생하는 문제가 있었습니다.
        /// 이를 개선하기 위해 공통 모서리를 공유하는 두 삼각형을 하나의 나뭇잎 카드로 판단하고, 카드 단위로 유지하거나 제거합니다.
        /// 쌍을 이루지 않는 일반 삼각형은 그대로 유지하여 나무줄기, 껍질, 바위 등의 메시가 불규칙하게 손상되지 않도록 처리합니다.
        /// 메시 최적화 과정에서도 원본의 노멀, UV, 탄젠트, 버텍스 컬러 및 바운드 정보를 유지하도록 구성했습니다.
        /// </summary>
        static Mesh SparseMesh(Mesh src, bool[] leafSubmesh, float keep, bool rockProxy, string id)
        {
            if (!src || !src.isReadable)
                throw new InvalidOperationException($"원본 mesh 읽기 불가: {(src ? src.name : "mesh 없음")}");
            if (rockProxy && keep < .60f)
                return RockProxy(src.bounds, id);

            Vector3[] v = src.vertices, n=src.normals;
            Vector4[] tangent = src.tangents;
            Vector2[] uv = src.uv, uv2 = src.uv2;
            Color[] colors = src.colors;

            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var tangents = new List<Vector4>();
            var uv0 = new List<Vector2>();
            var uv1 = new List<Vector2>();
            var colorOut = new List<Color>();
            var indexMap = new Dictionary<int,int>();
            var subTriangles = new List<int>[src.subMeshCount];

            for (int sub = 0; sub < src.subMeshCount; sub++)
            {
                int[] tris = src.GetTriangles(sub);
                var selected = new List<int>(tris.Length);
                bool isLeaf = leafSubmesh != null && sub<leafSubmesh.Length && leafSubmesh[sub];
                int triCount = tris.Length / 3;

                // 실행할 때마다 결과가 달라지는 난수 대신 정수 해시를 사용합니다.
                // 동일한 입력 데이터에서는 항상 같은 삼각형 선택 결과가 나오도록 하여
                // 에디터에서 LOD 메시를 다시 생성해도 일관된 결과를 유지합니다.
                for (int t = 0; t < triCount;)
                {
                    int pairSize = 1;
                    if (isLeaf && t + 1 < triCount)
                    {
                        int a = t * 3, b = a + 3, shared = 0;
                        for (int u = 0; u < 3; u++)
                        {
                            for (int w = 0; w < 3; w++)
                            {
                                if (tris[a + u] == tris[b + w])
                                {
                                    shared++;
                                    break;
                                }
                            }
                        }

                        if (shared >= 2)
                            pairSize = 2;
                    }

                    // 공통 모서리로 연결된 두 삼각형이 하나의 나뭇잎 카드를 구성하는 경우에만 카드 단위로 제거합니다.
                    // 카드의 삼각형 하나만 제거하거나 다른 연결 구조를 가진 메시를 임의로 손상시키지 않습니다.
                    uint h = unchecked((uint)((t+1)*1103515245 + (sub+17)*12345));
                    h ^= h >> 16;

                    bool retain = !isLeaf || pairSize==1 || ((h & 65535u)/65535f < keep);
                    if (retain)
                    {
                        for (int z = 0; z < pairSize * 3; z++)
                        {
                            int original=tris[t*3+z],mapped;
                            if (!indexMap.TryGetValue(original, out mapped))
                            {
                                mapped = vertices.Count; 
                                indexMap.Add(original, mapped);
                                vertices.Add(v[original]);

                                if (n.Length == v.Length)
                                    normals.Add(n[original]);
                                if (tangent.Length == v.Length)
                                    tangents.Add(tangent[original]);
                                if (uv.Length == v.Length) 
                                    uv0.Add(uv[original]);
                                if (uv2.Length == v.Length) 
                                    uv1.Add(uv2[original]);
                                if (colors.Length == v.Length)
                                    colorOut.Add(colors[original]);
                            }

                            selected.Add(mapped);
                        }
                    }

                    t += pairSize;
                }

                // triangle 수가 적은 나뭇잎 메시에서 최적화 결과가 0개의 triangle으로 줄어드는 경우 원본 서브메시를 유지합니다.
                // 작은 잎이나 식생이 완전히 사라지는 현상을 방지하여 낮은 LOD에서도 최소한의 외형이 유지되도록 처리합니다.
                if (selected.Count == 0 && tris.Length > 0)
                {
                    for (int j = 0; j < tris.Length; j++)
                    {
                        int original=tris[j], mapped;
                        if (!indexMap.TryGetValue(original, out mapped))
                        {
                            mapped = vertices.Count; 
                            indexMap.Add(original, mapped);
                            vertices.Add(v[original]);

                            if (n.Length == v.Length)
                                normals.Add(n[original]);
                            if (tangent.Length == v.Length) 
                                tangents.Add(tangent[original]);
                            if (uv.Length == v.Length)
                                uv0.Add(uv[original]);
                            if (uv2.Length == v.Length)
                                uv1.Add(uv2[original]);
                            if (colors.Length == v.Length) 
                                colorOut.Add(colors[original]);
                        }
                        selected.Add(mapped);
                    }
                }

                subTriangles[sub] = selected;
            }

            if (vertices.Count == 0)
                throw new InvalidOperationException($"triangle 데이터 누락: {id}");

            Mesh mesh=new Mesh {name=id};
            if (vertices.Count > 65535)
                mesh.indexFormat = IndexFormat.UInt32;

            mesh.SetVertices(vertices);

            if (normals.Count == vertices.Count) 
                mesh.SetNormals(normals);
            if (tangents.Count == vertices.Count)
                mesh.SetTangents(tangents);
            if (uv0.Count == vertices.Count)
                mesh.SetUVs(0, uv0);
            if (uv1.Count == vertices.Count)
                mesh.SetUVs(1, uv1);
            if (colorOut.Count == vertices.Count)
                mesh.SetColors(colorOut);

            mesh.subMeshCount = src.subMeshCount;

            for (int k = 0; k < subTriangles.Length; k++)
                mesh.SetTriangles(subTriangles[k], k, false);

            mesh.bounds = src.bounds;
            return mesh;
        }

        static Mesh RockProxy(Bounds b, string name)
        {
            Vector3 c = b.center, s = b.extents;
            s.x = Mathf.Max(.05f, s.x);
            s.y = Mathf.Max(.05f, s.y);
            s.z = Mathf.Max(.05f, s.z);

            Vector3[] v = {c + new Vector3(0, s.y, 0), c + new Vector3(0, -s.y, 0), c + new Vector3(s.x, 0, 0), c + new Vector3(0, 0, s.z), c + new Vector3(-s.x, 0, 0), c + new Vector3(0, 0, -s.z)};
            int[] t = {0, 2, 3, 0, 3, 4, 0, 4, 5, 0, 5, 2, 1, 3, 2, 1, 4, 3, 1, 5, 4, 1, 2, 5};
            Mesh m = new Mesh {name = name, vertices = v, triangles = t, uv = new[]{new Vector2(0.5f, 1), new Vector2(0.5f, 0), new Vector2(1, 0.5f), new Vector2(0.5f, 1), new Vector2(0, 0.5f), new Vector2(0.5f, 0)}};
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        static Mesh GetMesh(Mesh src, Material[] mats, int stage, bool solid, string key)
        {
            Ensure(Meshes);

            string id = Safe(key) + "_L" + stage;
            string path = Meshes + "/" + id + ".asset";

            Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing)
                return existing;

            bool[] leaf = new bool[src.subMeshCount];
            for (int i = 0; i < leaf.Length; i++)
                leaf[i] = mats != null && i < mats.Length ? IsLeaf(mats[i]) : !solid;

            if (solid && stage < 3 && Array.TrueForAll(leaf, v => !v))
                return src;

            bool proxy = solid && (stage >= 3) && src.subMeshCount == 1;
            Mesh mesh = SparseMesh(src, leaf, Proportions[stage], proxy, id);
            AssetDatabase.CreateAsset(mesh, path);
            return mesh;
        }

        public static GameObject BuildOptimizedPrefab(GameObject source, string name, bool isTree, bool forceRebuild = false)
        {
            if (!source)
                throw new InvalidOperationException($"원본 prefab 없음: {name}");

            Ensure(Prefabs);
            Ensure(Materials);
            Ensure(Meshes);

            string path = Prefabs + "/" + Safe(name) + "_LOD5.prefab";
            GameObject existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (!forceRebuild && existing && existing.GetComponent<LODGroup>() && existing.GetComponent<LODGroup>().GetLODs().Length == 5)
                return existing;

            GameObject go = (GameObject)PrefabUtility.InstantiatePrefab(source);
            if (!go)
                throw new InvalidOperationException($"오브젝트 생성 실패: {name}");

            try
            {
                PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                go.name = name + "_LOD5";

                LODGroup lod = go.GetComponent<LODGroup>();
                if (!lod)
                    lod = go.AddComponent<LODGroup>();

                LOD[] original = lod.GetLODs();
                Renderer[] high = original.Length > 0 && original[0].renderers != null && original[0].renderers.Length > 0 ? original[0].renderers : go.GetComponentsInChildren<MeshRenderer>(true);
                if (high.Length == 0)
                    throw new InvalidOperationException($"LOD0 mesh 렌더러 없음: {name}");

                Renderer[] low = (isTree && original.Length > 1 && original[1].renderers != null && original[1].renderers.Length > 0) ? original[1].renderers : high;

                var stages = new LOD[5];
                for (int i = 0; i < 5; i++)
                {
                    Renderer[] srcRender = (i == 0) ? high : low;
                    List<Renderer> output = new List<Renderer>();
                    for (int j = 0; j < srcRender.Length; j++)
                    {
                        Renderer render = srcRender[j];
                        if (!render)
                            continue;

                        MeshFilter mf = render.GetComponent<MeshFilter>();
                        MeshRenderer mr = render as MeshRenderer;
                        if (!mf || !mf.sharedMesh || !mr)
                            continue;

                        Renderer outputR;
                        if ((i == 0) || (i == 1 && isTree && original.Length > 1))
                            outputR = render;
                        else
                        {
                            GameObject clone = Object.Instantiate(render.gameObject, render.transform.parent);
                            clone.name = "LOD" + i + "_" + j;
                            clone.transform.localPosition = render.transform.localPosition;
                            clone.transform.localRotation = render.transform.localRotation;
                            clone.transform.localScale = render.transform.localScale;

                            foreach (var c in clone.GetComponentsInChildren<Collider>(true))
                                Object.DestroyImmediate(c);

                            outputR = clone.GetComponent<Renderer>();
                        }

                        var dstMR = outputR as MeshRenderer;
                        MeshFilter dstMF = outputR.GetComponent<MeshFilter>();

                        if (i >= 2 || (!isTree && i >= 1))
                        {
                            bool rock = name.StartsWith("Rock_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Decayed", StringComparison.OrdinalIgnoreCase);
                            dstMF.sharedMesh = GetMesh(mf.sharedMesh, mr.sharedMaterials, i, rock, name + "_" + j);
                        }

                        var srcMats = mr.sharedMaterials;
                        var newMats = new Material[srcMats.Length];

                        for (int k = 0; k < srcMats.Length; k++)
                            newMats[k] = Adapt(srcMats[k], name + "_" + srcMats[k].name);

                        dstMR.sharedMaterials = newMats;
                        dstMR.shadowCastingMode = (i <= 1) ? ShadowCastingMode.On : ShadowCastingMode.Off;
                        dstMR.receiveShadows = (i <= 1);
                        output.Add(outputR);
                    }

                    if (output.Count == 0)
                        throw new InvalidOperationException($"LOD{i} 렌더러 누락: {name}");

                    stages[i] = new LOD((isTree ? TreeHeights[i] : Heights[i]), output.ToArray());
                }

                lod.SetLODs(stages);
                lod.fadeMode = LODFadeMode.None;
                lod.RecalculateBounds();

                GameObject saved = PrefabUtility.SaveAsPrefabAsset(go,path);
                if (!saved || saved.GetComponent<LODGroup>().GetLODs().Length != 5)
                    throw new InvalidOperationException($"5단계 LOD prefab 저장 실패: {path}");
                return saved;
            }
            finally
            {
                if (go)
                    Object.DestroyImmediate(go);
            }
        }

        public static Mesh[] LODMeshes(GameObject prefab)
        {
            var lo = preFabLODs(prefab);
            var meshes = new Mesh[5];
            for (int i = 0; i < 5; i++)
            {
                foreach (var r in lo[i].renderers)
                {
                    if (r && r.GetComponent<MeshFilter>() && r.GetComponent<MeshFilter>().sharedMesh)
                    {
                        meshes[i] = r.GetComponent<MeshFilter>().sharedMesh;
                        break;
                    }
                }

                if (!meshes[i])
                    throw new InvalidOperationException($"LOD{i} mesh 누락: {prefab.name}");
            }
            return meshes;
        }

        static LOD[] preFabLODs(GameObject go)
        {
            if (!go || !go.GetComponent<LODGroup>() || go.GetComponent<LODGroup>().GetLODs().Length != 5)
                throw new InvalidOperationException($"5단계 LOD 구성 오류: {(go ? go.name : "오브젝트 없음")}");
            return go.GetComponent<LODGroup>().GetLODs();
        }

        public static Material FirstMaterial(GameObject prefab)
        {
            var rs = preFabLODs(prefab)[0].renderers;
            foreach (Renderer r in rs)
            {
                if (r && r.sharedMaterial)
                    return r.sharedMaterial;
            }

            throw new InvalidOperationException($"Material 누락: {prefab.name}");
        }

        public static void OptimizeAndroidTextures()
        {
            string folder = Source+"Materials/Textures";
            var guids = AssetDatabase.FindAssets("t:Texture2D", new[]{folder});
            int changed = 0;
            foreach (string id in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(id);
                TextureImporter importer=AssetImporter.GetAtPath(path) as TextureImporter;
                if (!importer)
                    continue;

                TextureImporterPlatformSettings p = importer.GetPlatformTextureSettings("Android");
                int limit = path.ToLowerInvariant().Contains("_n.") ? 1024 : 1024;
                if (p.overridden && p.maxTextureSize <= limit)
                    continue;

                p.name = "Android";
                p.overridden = true;
                p.maxTextureSize = limit;
                p.format = TextureImporterFormat.Automatic;
                p.compressionQuality = 60;

                importer.SetPlatformTextureSettings(p);
                importer.SaveAndReimport();

                changed++;
            }
            Debug.Log($"Android 텍스처 설정 완료: {changed}개 (원본 유지)");
        }

        [MenuItem("Tools/Map Streaming/Android 텍스처 최적화 (최대 1024px)")]
        static void MenuTex() => OptimizeAndroidTextures();
    }
}
#endif
