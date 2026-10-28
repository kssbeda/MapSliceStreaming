using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MapSliceStreaming
{
    /// <summary>
    /// 기존의 단순한 삼각형 나무 표현 대신, 실제 나무의 5단계 LOD 메시와 머티리얼을 재사용하여 원거리 숲을 렌더링합니다.
    /// Terrain 청크가 로드되지 않은 영역에서만 원거리 나무를 표시하여 청크 로딩 전후의 나무 외형 차이와 팝핑 현상을 줄이도록 구성했습니다.
    /// 기존 Terrain의 나무 배치 데이터, 스트리밍 시스템, Addressables 리소스는 변경하지 않습니다.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MapSliceRealForestRenderer : MonoBehaviour
    {
        public MapSliceRealForestCatalog forest;
        public MapSliceCatalog sliceCatalog;
        public MapSliceStreamer streamer;
        public Transform followTarget;
        public Camera viewingCamera;

        [Header("Optimized five-LOD tree prefabs")]
        public GameObject japaneseCypress;
        public GameObject sawtoothOak;
        public GameObject ginkgo;

        [Header("Far tree quality")]
        [Range(0, 4)] public int nearFallbackLOD = 2;
        [Range(0, 4)] public int distantFallbackLOD = 4;
        [Min(300f)] public float highDetailDistance = 1150f;
        [Min(500f)] public float maxDistance = 2300f;
        [Min(.05f)] public float updateInterval = .15f;

        public bool logDiagnostics;

        private sealed class Batch
        {
            public Mesh mesh;
            public Material material;
            public int submesh;
            public Matrix4x4[] instances;
        }

        private sealed class RuntimeTile
        {
            public Vector2Int id;
            public Bounds bounds;
            public List<Batch>[] lodBatches = { new List<Batch>(), new List<Batch>() };
            public bool draw;
            public int lodSelection;
        }

        private struct Template
        {
            public Mesh mesh;
            public Material material;
            public int submesh;
            public Matrix4x4 localToRoot;
        }

        private readonly List<RuntimeTile> tiles = new List<RuntimeTile>();
        private readonly HashSet<Vector2Int> loadedSlices = new HashSet<Vector2Int>();
        private readonly HashSet<Vector2Int> visibleTerrainSlices = new HashSet<Vector2Int>();
        private readonly Plane[] frustumPlanes = new Plane[6];
        private float nextUpdate;
        private int tileCount;
        private int drawCalls;

        private void OnEnable()
        {
            if (!streamer)
                streamer = GetComponent<MapSliceStreamer>();
            if (!viewingCamera)
                viewingCamera = Camera.main;
            if (streamer)
            {
                streamer.SliceLoaded += OnSliceLoaded;
                streamer.SliceUnloaded += OnSliceUnloaded;
            }
            Rebuild();
            nextUpdate = 0;
        }

        private void OnDisable()
        {
            if (streamer)
            {
                streamer.SliceLoaded -= OnSliceLoaded;
                streamer.SliceUnloaded -= OnSliceUnloaded;
            }
            tiles.Clear(); loadedSlices.Clear(); visibleTerrainSlices.Clear();
        }

        private void OnSliceLoaded(Vector2Int id, GameObject root)
        {
            loadedSlices.Add(id);
            // Disable the far representation in this very frame; avoid 0.15s overlap.
            for (int i = 0; i < tiles.Count; i++) if (tiles[i].id == id) tiles[i].draw = false;
        }

        private void OnSliceUnloaded(Vector2Int id)
        {
            loadedSlices.Remove(id);
            nextUpdate = 0;
        }

        private static Template[] Templates(GameObject prefab, int lodIndex)
        {
            if (!prefab)
                return new Template[0];

            LODGroup group = prefab.GetComponent<LODGroup>();
            if (!group)
                return new Template[0];

            LOD[] lods = group.GetLODs();
            if (lods == null || lods.Length < 5)
                return new Template[0];

            var result = new List<Template>();
            Matrix4x4 rootInverse = prefab.transform.worldToLocalMatrix;
            foreach (var renderer in lods[Mathf.Clamp(lodIndex, 0, lods.Length - 1)].renderers)
            {
                if (!renderer)
                    continue;

                MeshFilter mf = renderer.GetComponent<MeshFilter>();
                MeshRenderer mr = renderer as MeshRenderer;
                if (!mf || !mf.sharedMesh || !mr)
                    continue;

                Material[] materials = mr.sharedMaterials;
                int numSubMeshes = mf.sharedMesh.subMeshCount;
                Matrix4x4 offset = rootInverse * renderer.transform.localToWorldMatrix;
                for (int sm = 0; sm < numSubMeshes && sm < materials.Length; sm++)
                {
                    if (!materials[sm])
                        continue;
                    result.Add(new Template { mesh = mf.sharedMesh, material = materials[sm], submesh = sm, localToRoot = offset });
                }
            }
            return result.ToArray();
        }

        private sealed class BatchAccumulator
        {
            public Template template;
            public List<Matrix4x4> positions = new List<Matrix4x4>();
        }

        private static List<Batch> MakeBatches(MapSliceRealForestCatalog.Tile tile, Template[][] templates)
        {
            var accumulators = new Dictionary<int, BatchAccumulator>();
            foreach (var tree in tile.trees)
            {
                if (tree == null || tree.species < 0 || tree.species >= templates.Length)
                    continue;

                Matrix4x4 world = Matrix4x4.TRS(tree.position,
                    Quaternion.Euler(0f, tree.rotationRadians * Mathf.Rad2Deg, 0f),
                    new Vector3(tree.widthScale, tree.heightScale, tree.widthScale));
                for (int i = 0; i < templates[tree.species].Length; i++)
                {
                    Template t = templates[tree.species][i];
                    // Group identical mesh/submesh/material/child offsets for true instanced draws.
                    int key = (tree.species << 16) | i;
                    BatchAccumulator a;
                    if (!accumulators.TryGetValue(key, out a))
                    {
                        a = new BatchAccumulator { template = t };
                        accumulators.Add(key, a);
                    }
                    a.positions.Add(world * t.localToRoot);
                }
            }

            var batches = new List<Batch>();
            foreach (var a in accumulators.Values)
            {
                if (!a.template.material.enableInstancing)
                    a.template.material.enableInstancing = true;

                int count = a.positions.Count;
                for (int start = 0; start < count; start += 1023)
                {
                    int n = Mathf.Min(1023, count - start);
                    Matrix4x4[] arr = new Matrix4x4[n];
                    a.positions.CopyTo(start, arr, 0, n);
                    batches.Add(new Batch { mesh = a.template.mesh, material = a.template.material, submesh = a.template.submesh, instances = arr });
                }
            }
            return batches;
        }

        public void Rebuild()
        {
            tiles.Clear();
            if (!forest)
                return;

            GameObject[] prefabs = { japaneseCypress, sawtoothOak, ginkgo };
            Template[][] near = new Template[3][], far = new Template[3][];
            for (int sp = 0; sp < 3; sp++)
            {
                near[sp] = Templates(prefabs[sp], nearFallbackLOD);
                far[sp] = Templates(prefabs[sp], distantFallbackLOD);
                if (near[sp].Length == 0 || far[sp].Length == 0)
                {
                    Debug.LogError("[] Missing valid five-LOD tree prefab or mesh for species " + sp);
                    tiles.Clear();
                    enabled = false;
                    return;
                }
            }
            foreach (var source in forest.tiles)
            {
                if (source == null || source.trees == null || source.trees.Count == 0)
                    continue;

                var rt = new RuntimeTile { id = source.coordinate, bounds = source.worldBounds };
                rt.lodBatches[0] = MakeBatches(source, near);
                rt.lodBatches[1] = nearFallbackLOD == distantFallbackLOD ? rt.lodBatches[0] : MakeBatches(source, far);
                tiles.Add(rt);
            }
            nextUpdate = 0f;
            Debug.Log("[] Real tree proxies prepared on " + tiles.Count + " tiles. Original triangle meshes unused.");
        }

        private void RefreshVisibility()
        {
            if (!followTarget || !viewingCamera)
                return;

            visibleTerrainSlices.Clear();
            if (sliceCatalog)
            {
                foreach (Terrain terrain in Terrain.activeTerrains)
                {
                    if (!terrain || !terrain.terrainData)
                        continue;

                    Vector3 center = terrain.transform.position + terrain.terrainData.size * .5f;
                    visibleTerrainSlices.Add(sliceCatalog.WorldToSlice(center));
                }
            }

            GeometryUtility.CalculateFrustumPlanes(viewingCamera, frustumPlanes);
            Vector3 position = followTarget.position;
            float limit = maxDistance * maxDistance;
            tileCount = 0;
            foreach (var tile in tiles)
            {
                Vector3 offset = tile.bounds.center - position;
                float dx = offset.x, dz = offset.z;
                float planarHalfDiagonal = Mathf.Sqrt(tile.bounds.extents.x * tile.bounds.extents.x + tile.bounds.extents.z * tile.bounds.extents.z);
                float nearEdge = Mathf.Max(0, Mathf.Sqrt(dx * dx + dz * dz) - planarHalfDiagonal);
                bool isDrawn = !loadedSlices.Contains(tile.id) && !visibleTerrainSlices.Contains(tile.id) && nearEdge * nearEdge <= limit && GeometryUtility.TestPlanesAABB(frustumPlanes, tile.bounds);
                tile.draw = isDrawn;
                tile.lodSelection = nearEdge < highDetailDistance ? 0 : 1;
                if (isDrawn)
                    tileCount++;
            }

            if (logDiagnostics && Time.frameCount % 300 == 0)
                Debug.Log("[] Real horizon tiles visible: " + tileCount + "/" + tiles.Count);
        }

        private void LateUpdate()
        {
            if (!isActiveAndEnabled || !forest || !viewingCamera)
                return;

            if (Time.unscaledTime >= nextUpdate)
            {
                nextUpdate = Time.unscaledTime + updateInterval;
                RefreshVisibility();
            }

            drawCalls = 0;
            foreach (var tile in tiles)
            {
                if (!tile.draw || loadedSlices.Contains(tile.id))
                    continue;

                foreach (var batch in tile.lodBatches[tile.lodSelection])
                {
                    if (batch.mesh && batch.material && batch.instances.Length > 0)
                    {
                        Graphics.DrawMeshInstanced(batch.mesh, batch.submesh, batch.material, batch.instances,
                            batch.instances.Length, null, ShadowCastingMode.Off, false, 0, viewingCamera,
                            LightProbeUsage.Off, null);
                        drawCalls++;
                    }
                }
            }
        }
    }
}
