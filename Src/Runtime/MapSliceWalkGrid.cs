using UnityEngine;

namespace MapSliceStreaming
{
    // 저작용 Terrain에서 생성한 이동 가능 격자. 스트리밍되지 않은 지역도 조회 가능
    [CreateAssetMenu(fileName = "MainField_RouteGrid", menuName = "Map Streaming/Walkable Route Grid")]
    public sealed class MapSliceWalkGrid : ScriptableObject
    {
        [SerializeField] private Vector2 worldMin;
        [SerializeField, Min(1f)] private float cellSize = 16f;
        [SerializeField, Min(1)] private int width = 1;
        [SerializeField, Min(1)] private int height = 1;
        [SerializeField, Range(1f, 89f)] private float maxSlopeDegrees = 38f;
        [SerializeField] private bool[] walkable;
        [SerializeField] private float[] heights;

        public Vector2 WorldMin => worldMin;
        public float CellSize => cellSize;
        public int Width => width;
        public int Height => height;
        public int Count => width * height;
        public float MaxSlopeDegrees => maxSlopeDegrees;
        public bool HasData => walkable != null && heights != null && walkable.Length == Count && heights.Length == Count;

        public bool Contains(int x, int z) => x >= 0 && z >= 0 && x < width && z < height;
        public int Index(int x, int z) => z * width + x;

        public bool TryGetCell(Vector2 world, out int x, out int z)
        {
            x = Mathf.FloorToInt((world.x - worldMin.x) / cellSize);
            z = Mathf.FloorToInt((world.y - worldMin.y) / cellSize);
            return Contains(x, z);
        }

        public bool IsWalkable(int x, int z) => HasData && Contains(x, z) && walkable[Index(x, z)];
        public bool IsWalkable(Vector2 world)
        {
            return TryGetCell(world, out int x, out int z) && IsWalkable(x, z);
        }

        public Vector3 Center(int x, int z)
        {
            return new Vector3(worldMin.x + (x + 0.5f) * cellSize,
                HasData && Contains(x, z) ? heights[Index(x, z)] : 0f,
                worldMin.y + (z + 0.5f) * cellSize);
        }

        // 인접 셀의 고도 차이와 대각선 모서리 통과를 제한
        public bool CanStep(int fromX, int fromZ, int toX, int toZ)
        {
            if (!IsWalkable(fromX, fromZ) || !IsWalkable(toX, toZ))
                return false;

            int dx = toX - fromX, dz = toZ - fromZ;
            if (Mathf.Abs(dx) > 1 || Mathf.Abs(dz) > 1 || (dx == 0 && dz == 0))
                return false;

            if (dx != 0 && dz != 0 && (!IsWalkable(fromX + dx, fromZ) || !IsWalkable(fromX, fromZ + dz)))
                return false;

            float distance = cellSize * (dx != 0 && dz != 0 ? 1.41421356f : 1f);
            float grade = Mathf.Abs(heights[Index(toX, toZ)] - heights[Index(fromX, fromZ)]) / distance;
            return grade <= Mathf.Tan(maxSlopeDegrees * Mathf.Deg2Rad);
        }

#if UNITY_EDITOR
        // 저작용 씬의 데이터만 에디터에서 기록
        public void SetBakedData(Vector2 min, float size, int xCount, int zCount, float slope, bool[] passable, float[] sampledHeights)
        {
            worldMin = min;
            cellSize = Mathf.Max(1f, size);
            width = Mathf.Max(1, xCount);
            height = Mathf.Max(1, zCount);
            maxSlopeDegrees = Mathf.Clamp(slope, 1f, 89f);
            walkable = passable;
            heights = sampledHeights;
        }
#endif
    }
}