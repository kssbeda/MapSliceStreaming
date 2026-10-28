#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MapSliceStreaming.Editor
{
    // 실제 Terrain의 경사와 해안선으로 이동 가능 격자 생성
    public sealed class MapSliceWalkGridBakerWindow : EditorWindow
    {
        [SerializeField] private MapSliceCatalog catalog;
        [SerializeField] private Transform sourceRoot;
        [SerializeField] private string outputPath = "Assets/MapSliceStreaming/Generated/Map/MainField_RouteGrid.asset";
        [SerializeField, Range(4f, 64f)] private float cellSize = 12f;
        [SerializeField, Range(1f, 80f)] private float maxSlope = 38f;
        [SerializeField] private bool applySampleCoast = true;
        [SerializeField] private bool excludeUnderSeaLevel = true;
        [SerializeField] private float seaLevel = MapSliceCoastline.SeaLevel;

        [MenuItem("Tools/Map Streaming/18. Bake Walkable Route Grid")]
        private static void Open()
        {
            GetWindow<MapSliceWalkGridBakerWindow>("Route Grid Baker");
        }

        private void OnGUI()
        {
            EditorGUILayout.HelpBox("Sample_Authoring 씬에서 Bake합니다. 급경사·바다를 피하는 지도용 예상 경로 데이터입니다. 실제 NPC NavMesh와는 별개입니다.", MessageType.Info);
            catalog = (MapSliceCatalog)EditorGUILayout.ObjectField("Slice Catalog", catalog, typeof(MapSliceCatalog), false);
            sourceRoot = (Transform)EditorGUILayout.ObjectField("Source Root", sourceRoot, typeof(Transform), true);
            outputPath = EditorGUILayout.TextField("Output Asset", outputPath);
            cellSize = EditorGUILayout.Slider("Grid Cell (m)", cellSize, 4f, 64f);
            maxSlope = EditorGUILayout.Slider("Max Slope", maxSlope, 1f, 80f);
            applySampleCoast = EditorGUILayout.Toggle("Sample Coast Rule", applySampleCoast);
            excludeUnderSeaLevel = EditorGUILayout.Toggle("Block Below Sea Level", excludeUnderSeaLevel);
            if (excludeUnderSeaLevel)
                seaLevel = EditorGUILayout.FloatField("Sea Level", seaLevel);
            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(catalog == null || sourceRoot == null))
                if (GUILayout.Button("Bake Route Grid", GUILayout.Height(36))) Bake();
        }

        private void Bake()
        {
            if (catalog == null || sourceRoot == null) return;
            if (!outputPath.StartsWith("Assets/", StringComparison.Ordinal) || !outputPath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogError("[MapSlice Route] 출력 경로는 Assets/.../*.asset 형식이어야 합니다.");
                return;
            }
            Terrain[] terrains = sourceRoot.GetComponentsInChildren<Terrain>(true);
            if (terrains.Length == 0)
            {
                Debug.LogError("[MapSlice Route] Source Root에 Terrain이 없습니다.");
                return;
            }
            int minX = int.MaxValue, minZ = int.MaxValue, maxX = int.MinValue, maxZ = int.MinValue;
            foreach (MapSliceEntry slice in catalog.Slices)
            {
                minX = Mathf.Min(minX, slice.Coordinate.x);
                minZ = Mathf.Min(minZ, slice.Coordinate.y);
                maxX = Mathf.Max(maxX, slice.Coordinate.x);
                maxZ = Mathf.Max(maxZ, slice.Coordinate.y);
            }
            if (minX == int.MaxValue)
            {
                Debug.LogError("[MapSlice Route] Catalog가 비어 있습니다.");
                return;
            }

            float sliceSize = catalog.SliceSize;
            Vector2 min = catalog.WorldOrigin + new Vector2(minX * sliceSize, minZ * sliceSize);
            Vector2 max = catalog.WorldOrigin + new Vector2((maxX + 1) * sliceSize, (maxZ + 1) * sliceSize);
            float size = Mathf.Clamp(cellSize, 4f, 64f);
            int width = Mathf.CeilToInt((max.x - min.x) / size);
            int height = Mathf.CeilToInt((max.y - min.y) / size);
            if ((long)width * height > 500000)
            {
                Debug.LogError("[MapSlice Route] 격자가 너무 큽니다. Cell Size를 늘리세요.");
                return;
            }
            bool[] passable = new bool[width * height];
            float[] heights = new float[width * height];
            int walkableCount = 0;

            try
            {
                for (int z = 0; z < height; z++)
                {
                    if (z % 16 == 0 && EditorUtility.DisplayCancelableProgressBar("Route Grid Bake", "Terrain 경사 분석", (float)z / height))
                    {
                        Debug.LogWarning("[MapSlice Route] 사용자가 Bake를 취소했습니다.");
                        return;
                    }
                    for (int x = 0; x < width; x++)
                    {
                        Vector2 point = min + new Vector2((x + 0.5f) * size, (z + 0.5f) * size);
                        int index = z * width + x;
                        if (!TryGetTerrain(terrains, point, out Terrain terrain, out Vector2 terrainUV))
                            continue;

                        TerrainData data = terrain.terrainData;
                        float y = terrain.transform.position.y + data.GetInterpolatedHeight(terrainUV.x, terrainUV.y);
                        heights[index] = y;
                        float steepness = data.GetSteepness(terrainUV.x, terrainUV.y);
                        bool allowed = steepness <= maxSlope && (!excludeUnderSeaLevel || y > seaLevel + 1f) && (!applySampleCoast || MapSliceCoastline.IsLand(point));
                        // Terrain Hole은 경로에서 제외
                        if (allowed && data.holesResolution > 0)
                        {
                            int hx = Mathf.Clamp(Mathf.FloorToInt(terrainUV.x * data.holesResolution), 0, data.holesResolution - 1);
                            int hz = Mathf.Clamp(Mathf.FloorToInt(terrainUV.y * data.holesResolution), 0, data.holesResolution - 1);
                            bool[,] holes = data.GetHoles(hz, hx, 1, 1);
                            allowed = holes[0, 0];
                        }
                        passable[index] = allowed;

                        if (allowed)
                            walkableCount++;
                    }
                }
            }
            finally { EditorUtility.ClearProgressBar(); }

            int slash = outputPath.LastIndexOf('/');
            string folder = outputPath.Substring(0, slash);
            MapSliceEditorUtility.EnsureFolder(folder);
            MapSliceWalkGrid asset = AssetDatabase.LoadAssetAtPath<MapSliceWalkGrid>(outputPath);
            if (asset == null)
            {
                asset = CreateInstance<MapSliceWalkGrid>();
                AssetDatabase.CreateAsset(asset, outputPath);
            }

            Undo.RecordObject(asset, "Bake Route Grid");
            asset.SetBakedData(min, size, width, height, maxSlope, passable, heights);
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            EditorGUIUtility.PingObject(asset);
            Debug.Log($"[MapSlice Route] Bake 완료: {width}x{height}, 이동 가능 {walkableCount}/{passable.Length} 셀. {outputPath}");
        }

        private static bool TryGetTerrain(Terrain[] terrains, Vector2 point, out Terrain terrain, out Vector2 uv)
        {
            foreach (Terrain candidate in terrains)
            {
                if (candidate == null || candidate.terrainData == null)
                    continue;

                Vector3 origin = candidate.transform.position;
                Vector3 size = candidate.terrainData.size;
                if (point.x < origin.x || point.y < origin.z || point.x >= origin.x + size.x || point.y >= origin.z + size.z)
                    continue;

                terrain = candidate;
                uv = new Vector2((point.x - origin.x) / size.x, (point.y - origin.z) / size.z);
                return true;
            }
            terrain = null;
            uv = default;
            return false;
        }
    }
}
#endif