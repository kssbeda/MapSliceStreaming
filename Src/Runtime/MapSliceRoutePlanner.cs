using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace MapSliceStreaming
{
    public enum MapSliceRouteQuality { None, VerifiedNavMesh, CellPreview, Unavailable }

    // 로드된 NavMesh는 검증 경로, 미로드 지역은 베이크된 지형 격자로 예상 경로 계산
    public sealed class MapSliceRoutePlanner : MonoBehaviour
    {
        [SerializeField] private MapSliceStreamer streamer;
        [SerializeField] private MapSliceWorldMapUI mapUI;
        [SerializeField] private Transform player;
        [SerializeField] private MapSliceWalkGrid routeGrid;

        [SerializeField, Min(0.2f)] private float refreshInterval = 1.5f;
        [SerializeField, Min(1f)] private float deviationRepathMeters = 8f;
        [SerializeField, Min(1f)] private float movementRepathMeters = 5f;
        [SerializeField, Min(0.1f)] private float minimumRepathInterval = 0.35f;
        [SerializeField, Min(0.1f)] private float navMeshProbeRadius = 5f;
        [SerializeField, Min(0)] private int agentSettingsIndex;
        [SerializeField, Min(128)] private int maximumVisitedCells = 20000;

        [SerializeField] private bool allowUnverifiedPreview = true;

        private readonly List<Vector3> corners = new List<Vector3>();
        private NavMeshPath navPath;
        private float nextRefresh;
        private bool updateRequested;
        private bool warnedNoGrid;
        private Vector3 lastPlannedPosition;
        private float lastPlannedAt = float.NegativeInfinity;
        private bool hasPlannedPosition;

        // A* 힙은 열린 셀에서 예상 총비용이 가장 낮은 셀을 우선 선택
        private struct HeapItem
        {
            public int Index;
            public float Score;
            public HeapItem(int index, float score) { Index = index; Score = score; }
        }

        private readonly List<HeapItem> heap = new List<HeapItem>();
        private readonly List<int> reversedCells = new List<int>();
        private float[] gScore = Array.Empty<float>();
        private int[] previous = Array.Empty<int>();
        private bool[] closed = Array.Empty<bool>();
        private static readonly int[] OffsetX = { 1, 0, -1, 0, 1, -1, -1, 1 };
        private static readonly int[] OffsetZ = { 0, 1, 0, -1, 1, 1, -1, -1 };

        public MapSliceRouteQuality Quality { get; private set; }
        public IReadOnlyList<Vector3> Corners => corners;
        public MapSliceWalkGrid RouteGrid => routeGrid;
        public event Action RouteChanged;

        private void Awake()
        {
            // Unity 네이티브 객체는 필드 초기화가 아니라 Awake에서 생성
            navPath = new NavMeshPath();
        }

        private void OnEnable()
        {
            if (mapUI != null)
            {
                mapUI.WaypointChanged += OnWaypoint;
                mapUI.WaypointCleared += OnCleared;
            }

            if (streamer != null)
            {
                streamer.NavMeshLoaded += OnNavChanged;
                streamer.NavMeshUnloaded += OnNavChanged;
            }
            updateRequested = true;
        }

        private void OnDisable()
        {
            if (mapUI != null)
            {
                mapUI.WaypointChanged -= OnWaypoint;
                mapUI.WaypointCleared -= OnCleared;
            }

            if (streamer != null)
            {
                streamer.NavMeshLoaded -= OnNavChanged;
                streamer.NavMeshUnloaded -= OnNavChanged;
            }
        }

        private void OnWaypoint(Vector3 _)
        {
            // 다른 목적지를 선택했을 때 이전 경로가 잠시 남지 않도록 제거
            corners.Clear();
            Quality = MapSliceRouteQuality.None;
            updateRequested = true;
            RouteChanged?.Invoke();
        }

        private void OnCleared()
        {
            // 도착 시 기존 경로가 한 프레임 더 보이지 않도록 즉시 제거
            corners.Clear();
            Quality = MapSliceRouteQuality.None;
            updateRequested = false;
            nextRefresh = Time.unscaledTime + refreshInterval;
            RouteChanged?.Invoke();
        }

        private void OnNavChanged(Vector2Int _) => updateRequested = true;

        private void Update()
        {
            if (mapUI == null || player == null || !mapUI.HasWaypoint)
                return;

            float now = Time.unscaledTime;
            bool timerReady = now >= nextRefresh;
            bool moved = !hasPlannedPosition || FlatDistance(player.position, lastPlannedPosition) >= movementRepathMeters;
            bool offRoute = corners.Count > 1 && DistanceToRoute(player.position) >= deviationRepathMeters;

            // 경로를 벗어나면 정해진 주기를 기다리지 않고 다시 탐색
            bool urgent = updateRequested || offRoute;
            if (!urgent && !timerReady)
                return;

            if (!urgent && !moved && Quality != MapSliceRouteQuality.Unavailable)
                return;

            if (now - lastPlannedAt < minimumRepathInterval)
                return;

            nextRefresh = now + refreshInterval;
            updateRequested = false;
            Recalculate();
        }

        private static float FlatDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private float DistanceToRoute(Vector3 point)
        {
            Vector2 p = new Vector2(point.x, point.z);
            float closest = float.PositiveInfinity;
            for (int i = 1; i < corners.Count; ++i)
            {
                Vector2 a = new Vector2(corners[i - 1].x, corners[i - 1].z);
                Vector2 b = new Vector2(corners[i].x, corners[i].z);
                Vector2 segment = b - a;

                float lengthSquared = segment.sqrMagnitude;
                float t = lengthSquared < 0.001f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, segment) / lengthSquared);
                closest = Mathf.Min(closest, Vector2.Distance(p, a + segment * t));
            }
            return closest;
        }

        public bool TrySnapDestination(Vector3 desired, out Vector3 snapped, float maxDistance = 14f)
        {
            snapped = desired;
            if (routeGrid == null || !routeGrid.HasData)
                return true;

            Vector2 point = new Vector2(desired.x, desired.z);
            if (routeGrid.IsWalkable(point) && !routeGrid.IsInsideTreeTrunk(point))
                return true;

            int radius = Mathf.Max(1, Mathf.CeilToInt(maxDistance / routeGrid.CellSize));
            if (!routeGrid.TryNearestWalkable(point, radius, out Vector3 nearby))
                return false;

            Vector2 nearbyXZ = new Vector2(nearby.x, nearby.z);
            if (Vector2.Distance(point, nearbyXZ) > maxDistance)
                return false;

            snapped = nearby;
            return true;
        }

        public bool IsDestinationWalkable(Vector3 world)
        {
            // Grid가 있다면 바다·급경사·Terrain Hole을 정확하게 필터링
            return routeGrid == null || routeGrid.IsWalkable(new Vector2(world.x, world.z));
        }

        public void Recalculate()
        {
            if (player != null)
            {
                lastPlannedPosition = player.position;
                hasPlannedPosition = true;
            }

            lastPlannedAt = Time.unscaledTime;
            corners.Clear();
            Quality = MapSliceRouteQuality.None;
            if (streamer == null || mapUI == null || player == null || !mapUI.HasWaypoint)
            {
                RouteChanged?.Invoke();
                return;
            }

            Vector3 start = player.position;
            Vector3 target = mapUI.Waypoint;
            Vector2 destinationXZ = new Vector2(target.x, target.z);
            if (routeGrid != null && (!routeGrid.IsWalkable(destinationXZ) || routeGrid.IsInsideTreeTrunk(destinationXZ)))
            {
                Quality = MapSliceRouteQuality.Unavailable;
                RouteChanged?.Invoke();
                return;
            }

            // NavMesh 목적지 Y는 플레이어 높이가 아니라 목적지 Terrain 고도를 사용
            Vector3 targetAtTerrain = target;
            if (routeGrid != null && routeGrid.TryGetCell(destinationXZ, out int gx, out int gz))
                targetAtTerrain.y = routeGrid.Center(gx, gz).y;

            int settingsCount = NavMesh.GetSettingsCount();
            if (settingsCount > 0)
            {
                int agentType = NavMesh.GetSettingsByIndex(Mathf.Clamp(agentSettingsIndex, 0, settingsCount - 1)).agentTypeID;
                NavMeshQueryFilter filter = new NavMeshQueryFilter
                {
                    agentTypeID = agentType,
                    areaMask = NavMesh.AllAreas
                };

                if (NavMesh.SamplePosition(start, out NavMeshHit startHit, navMeshProbeRadius, filter) &&
                    NavMesh.SamplePosition(targetAtTerrain, out NavMeshHit targetHit, navMeshProbeRadius, filter) &&
                    NavMesh.CalculatePath(startHit.position, targetHit.position, filter, navPath) &&
                    navPath.status == NavMeshPathStatus.PathComplete && navPath.corners.Length >= 2 &&
                    Vector2.Distance(new Vector2(targetHit.position.x, targetHit.position.z), destinationXZ) <= navMeshProbeRadius &&
                    IsNavPathClearOfTrees(navPath))
                {
                    corners.AddRange(navPath.corners);
                    Quality = MapSliceRouteQuality.VerifiedNavMesh;
                    RouteChanged?.Invoke();
                    return;
                }
            }

            if (allowUnverifiedPreview && routeGrid != null && routeGrid.HasData && FindWalkableRoute(start, target))
                Quality = MapSliceRouteQuality.CellPreview;
            else
            {
                Quality = MapSliceRouteQuality.Unavailable;
                if (routeGrid == null && !warnedNoGrid)
                {
                    warnedNoGrid = true;
                    Debug.LogWarning("[MapSlice Route] Route Grid가 없습니다. 18번 베이커를 실행하고 Route Planner에 연결하세요. 기존 청크 직선 예상 경로는 표시하지 않습니다.", this);
                }
            }
            RouteChanged?.Invoke();
        }

        private bool IsNavPathClearOfTrees(NavMeshPath path)
        {
            if (routeGrid == null || routeGrid.TreeObstacleCount == 0)
                return true;

            Vector3[] navCorners = path.corners;
            for (int i = 1; i < navCorners.Length; i++)
                if (!routeGrid.IsTreeFreeSegment(navCorners[i - 1], navCorners[i]))
                    return false;
            return true;
        }

        private void HeapPush(int index, float score)
        {
            int position = heap.Count;
            heap.Add(new HeapItem(index, score));
            while (position > 0)
            {
                int parent = (position - 1) / 2;
                if (heap[parent].Score <= score)
                    break;

                heap[position] = heap[parent];
                position = parent;
            }
            heap[position] = new HeapItem(index, score);
        }

        private int HeapPop()
        {
            int result = heap[0].Index;
            HeapItem last = heap[heap.Count - 1];
            heap.RemoveAt(heap.Count - 1);
            if (heap.Count == 0)
                return result;

            int position = 0;
            while (position * 2 + 1 < heap.Count)
            {
                int left = position * 2 + 1;
                int right = left + 1;
                int child = right < heap.Count && heap[right].Score < heap[left].Score ? right : left;
                if (heap[child].Score >= last.Score)
                    break;

                heap[position] = heap[child];
                position = child;
            }

            heap[position] = last;
            return result;
        }

        private bool FindNearestCell(Vector2 world, int radius, out int result)
        {
            result = -1;
            if (routeGrid == null || !routeGrid.TryGetCell(world, out int cx, out int cz))
                return false;

            float best = float.PositiveInfinity;
            for (int dz = -radius; dz <= radius; dz++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int x = cx + dx, z = cz + dz;
                    if (!routeGrid.IsWalkable(x, z))
                        continue;

                    Vector3 candidate = routeGrid.Center(x, z);
                    float distance = (new Vector2(candidate.x, candidate.z) - world).sqrMagnitude;
                    if (distance >= best)
                        continue;

                    best = distance;
                    result = routeGrid.Index(x, z);
                }
            }
            return result >= 0;
        }

        private float Heuristic(int index, int goal)
        {
            int width = routeGrid.Width;
            int dx = index % width - goal % width;
            int dz = index / width - goal / width;
            return Mathf.Sqrt(dx * dx + dz * dz) * routeGrid.CellSize;
        }

        private bool FindWalkableRoute(Vector3 start, Vector3 goal)
        {
            Vector2 startXZ = new Vector2(start.x, start.z);
            Vector2 goalXZ = new Vector2(goal.x, goal.z);
            if (!FindNearestCell(startXZ, 3, out int from) || !FindNearestCell(goalXZ, 3, out int to))
                return false;

            if (from == to)
            {
                corners.Add(start);
                corners.Add(goal);
                return true;
            }

            int size = routeGrid.Count;
            // 같은 크기의 A* 작업 버퍼를 재사용해 반복 탐색의 GC Alloc 감소
            if (gScore.Length != size)
            {
                gScore = new float[size];
                previous = new int[size];
                closed = new bool[size];
            }

            for (int i = 0; i < size; i++)
            {
                gScore[i] = float.PositiveInfinity;
                previous[i] = -1;
                closed[i] = false;
            }

            heap.Clear();
            gScore[from] = 0f;
            HeapPush(from, Heuristic(from, to));

            int visited = 0;
            bool found = false;
            int width = routeGrid.Width;
            while (heap.Count != 0 && visited < maximumVisitedCells)
            {
                int current = HeapPop();
                if (closed[current])
                    continue;

                if (current == to)
                {
                    found = true;
                    break;
                }

                closed[current] = true;
                visited++;

                int cx = current % width, cz = current / width;
                for (int n = 0; n < OffsetX.Length; n++)
                {
                    int nx = cx + OffsetX[n], nz = cz + OffsetZ[n];
                    if (!routeGrid.CanStep(cx, cz, nx, nz))
                        continue;

                    int neighbor = routeGrid.Index(nx, nz);
                    if (closed[neighbor])
                        continue;

                    float length = n < 4 ? 1f : 1.41421356f;
                    float altitude = Mathf.Abs(routeGrid.Center(nx, nz).y - routeGrid.Center(cx, cz).y);
                    float stepCost = length * routeGrid.CellSize + altitude * 0.4f;
                    float tentative = gScore[current] + stepCost;
                    if (tentative >= gScore[neighbor])
                        continue;

                    gScore[neighbor] = tentative;
                    previous[neighbor] = current;
                    HeapPush(neighbor, tentative + Heuristic(neighbor, to));
                }
            }

            if (!found)
                return false;

            reversedCells.Clear();
            for (int cursor = to; cursor >= 0; cursor = previous[cursor])
            {
                reversedCells.Add(cursor);
                if (cursor == from)
                    break;

                if (reversedCells.Count > size)
                    return false;
            }

            if (reversedCells.Count == 0 || reversedCells[reversedCells.Count - 1] != from)
                return false;

            reversedCells.Reverse();

            // 출발/도착점은 줄기에서 벗어난 격자 중심에 보정
            Vector3 safeStart = routeGrid.Center(from % width, from / width);
            Vector3 safeGoal = routeGrid.Center(to % width, to / width);
            corners.Add(start);
            if (Vector3.Distance(start, safeStart) > routeGrid.CellSize * 0.25f)
                corners.Add(safeStart);

            int lastDX = int.MinValue, lastDZ = int.MinValue;
            for (int i = 1; i < reversedCells.Count; i++)
            {
                int previousIndex = reversedCells[i - 1], currentIndex = reversedCells[i];
                int dx = currentIndex % width - previousIndex % width;
                int dz = currentIndex / width - previousIndex / width;
                if (i > 1 && (dx != lastDX || dz != lastDZ))
                {
                    int pivot = reversedCells[i - 1];
                    corners.Add(routeGrid.Center(pivot % width, pivot / width));
                }
                lastDX = dx;
                lastDZ = dz;
            }

            // 클릭 지점이 나무줄기와 겹치면 마지막 안전 셀 중심으로 끝냄
            if (Vector3.Distance(corners[corners.Count - 1], safeGoal) > 0.1f)
                corners.Add(safeGoal);

            if (routeGrid.IsWalkable(goalXZ) && !routeGrid.IsInsideTreeTrunk(goalXZ))
                corners.Add(goal);
            return true;
        }
    }
}
