using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
#endif

namespace MapSliceStreaming
{
    // 월드맵 텍스처는 스트리밍 청크와 분리하여 항상 표시
    public sealed class MapSliceWorldMapUI : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private MapSliceWorldMapData mapData;
        [SerializeField] private Transform player;
        [SerializeField] private Font uiFont;
        [SerializeField] private MapSliceExploration exploration;
        [SerializeField] private MapSliceRoutePlanner routePlanner;
        [SerializeField] private bool hideUndiscoveredMarkers = true;

        [Header("Mini Map")]
        [SerializeField, Min(80f)] private float miniMapSize = 220f;
        [SerializeField, Min(20f)] private float miniMapWorldRadius = 180f;
        [SerializeField] private Vector2 miniMapMargin = new Vector2(24f, 24f);
        [SerializeField, Min(1f)] private float waypointArrivalRadius = 12f;
        [SerializeField] private bool clearWaypointOnArrival = true;

        [Header("World Map")]
        [SerializeField, Range(1f, 12f)] private float maxZoom = 8f;
        [SerializeField, Range(1f, 2f)] private float zoomStep = 1.25f;
        [SerializeField] private bool openCenteredOnPlayer = true;
        [SerializeField] private bool pauseGameOnOpen;
        [SerializeField] private KeyCode legacyToggleKey = KeyCode.M;

        private sealed class MarkerWidget
        {
            public RectTransform Rect;
            public Image Image;
            public MapSliceMapMarker Marker;
        }

        private Canvas canvas;
        private RectTransform safeRoot;
        private Vector2Int lastScreenSize;
        private Rect lastSafeArea;
        private RectTransform miniRect;
        private RawImage miniImage;
        private RawImage miniFogImage;
        private RawImage worldFogImage;
        private readonly List<RectTransform> miniRouteSegments = new List<RectTransform>();
        private readonly List<RectTransform> worldRouteSegments = new List<RectTransform>();
        private RectTransform worldPanel;
        private RectTransform worldRect;
        private RawImage worldImage;
        private RectTransform miniPlayer;
        private RectTransform worldPlayer;
        private RectTransform miniWaypoint;
        private RectTransform worldWaypoint;
        private Text coordinateLabel;
        private Text miniDistanceLabel;
        private readonly List<MarkerWidget> miniMarkers = new List<MarkerWidget>();
        private readonly List<MarkerWidget> worldMarkers = new List<MarkerWidget>();
        private readonly Dictionary<string, MapSliceMapMarker> runtimeMarkers = new Dictionary<string, MapSliceMapMarker>();
        private Rect miniUV;
        private Rect worldUV = new Rect(0f, 0f, 1f, 1f);
        private Vector2 worldCenterUV = new Vector2(0.5f, 0.5f);
        private float worldZoom = 1f;
        private bool hasWaypoint;
        private string mapFeedback;
        private float feedbackUntil;
        private Vector3 waypoint;
        private float originalTimeScale = 1f;
        private bool ownsCanvas;
        private bool warnedNoEventSystem;
        private Vector3 lastPlayerPosition;
        private float lastPlayerHeading;
        private MapSliceOrbitCamera orbitCamera;
        private float CurrentHeading => orbitCamera != null ? orbitCamera.Heading : player.eulerAngles.y;
        private bool hasLastPlayerState;

        public event Action<Vector3> WaypointChanged;
        public event Action<Vector3> WorldPositionSelected;
        public event Action WaypointCleared;
        public bool IsWorldMapOpen => worldPanel != null && worldPanel.gameObject.activeSelf;
        public bool HasWaypoint => hasWaypoint;
        public Vector3 Waypoint => waypoint;

        private void Start()
        {
            if (mapData == null || player == null)
            {
                Debug.LogError("[MapSlice] World Map Data / Player를 지정하세요.", this);
                enabled = false;
                return;
            }

            if (mapData.Texture == null)
                Debug.LogWarning("[MapSlice] 지도 텍스처가 없습니다. World Map Baker를 실행하세요.", this);

            if (uiFont == null)
                uiFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            orbitCamera = FindFirstObjectByType<MapSliceOrbitCamera>();
            CreateUI();
            EnsureEventSystem();
            RebuildMarkers();

            if (exploration != null) 
                exploration.ExplorationChanged += OnExplorationChanged;

            if (routePlanner != null)
                routePlanner.RouteChanged += OnRouteChanged;

            UpdateMaps();
        }

        private void Update()
        {
            RefreshSafeArea();

            bool toggle = false;
#if ENABLE_LEGACY_INPUT_MANAGER
            toggle = Input.GetKeyDown(legacyToggleKey);
#endif
#if ENABLE_INPUT_SYSTEM
            toggle |= Keyboard.current != null && Keyboard.current.mKey.wasPressedThisFrame;
#endif

            if (toggle) 
                ToggleWorldMap();
        }

        private void LateUpdate()
        {
            if (miniImage == null || player == null)
                return;

            // 지정한 위치에 도착하면 전체 지도와 미니맵 경로를 함께 정리
            if (clearWaypointOnArrival && hasWaypoint)
            {
                float dx = player.position.x - waypoint.x;
                float dz = player.position.z - waypoint.z;
                if (dx * dx + dz * dz <= waypointArrivalRadius * waypointArrivalRadius)
                {
                    ClearWaypoint();
                    return;
                }
            }

            if (!hasLastPlayerState || (player.position - lastPlayerPosition).sqrMagnitude > 0.01f || Mathf.Abs(Mathf.DeltaAngle(CurrentHeading, lastPlayerHeading)) > 0.2f)
                UpdateMaps();
        }

        private void OnDestroy()
        {
            if (exploration != null)
                exploration.ExplorationChanged -= OnExplorationChanged;

            if (routePlanner != null)
                routePlanner.RouteChanged -= OnRouteChanged;

            if (IsWorldMapOpen && pauseGameOnOpen)
                Time.timeScale = originalTimeScale;

            if (ownsCanvas && canvas != null)
                Destroy(canvas.gameObject);
        }

        private void OnExplorationChanged() => UpdateMaps();
        private void OnRouteChanged() => UpdateMaps();

        public void OpenWorldMap()
        {
            if (worldPanel == null || IsWorldMapOpen)
                return;

            if (openCenteredOnPlayer)
                worldCenterUV = mapData.WorldToUV(player.position);

            worldZoom = Mathf.Clamp(worldZoom, 1f, maxZoom);
            ClampWorldCenter();

            if (pauseGameOnOpen)
            {
                originalTimeScale = Time.timeScale;
                Time.timeScale = 0f;
            }

            worldPanel.gameObject.SetActive(true);
            UpdateMaps();
        }

        public void CloseWorldMap()
        {
            if (!IsWorldMapOpen)
                return;

            worldPanel.gameObject.SetActive(false);
            if (pauseGameOnOpen)
                Time.timeScale = originalTimeScale;
        }

        public void ToggleWorldMap()
        {
            if (IsWorldMapOpen)
                CloseWorldMap();
            else
                OpenWorldMap();
        }

        public void SetWaypoint(Vector3 worldPosition)
        {
            waypoint = worldPosition;
            hasWaypoint = true;
            WaypointChanged?.Invoke(waypoint);
            UpdateMaps();
        }

        public void ClearWaypoint()
        {
            if (!hasWaypoint)
                return;

            hasWaypoint = false;
            WaypointCleared?.Invoke();
            UpdateMaps();
        }

        public void AddOrUpdateMarker(string id, string title, Vector3 position, Color color, bool showOnMiniMap = true)
        {
            if (string.IsNullOrEmpty(id))
                return;

            runtimeMarkers[id] = new MapSliceMapMarker
            {
                id = id, title = title, worldXZ = new Vector2(position.x, position.z),
                color = color, showOnMiniMap = showOnMiniMap, showOnWorldMap = true
            };

            if (worldRect != null)
                RebuildMarkers();
        }

        public void RemoveMarker(string id)
        {
            if (runtimeMarkers.Remove(id) && worldRect != null)
                RebuildMarkers();
        }

        public void ZoomWorldMap(float factor)
        {
            worldZoom = Mathf.Clamp(worldZoom * factor, 1f, maxZoom);
            ClampWorldCenter();
            UpdateMaps();
        }

        public void PanWorldMap(Vector2 screenDelta)
        {
            if (worldRect == null || !IsWorldMapOpen) 
                return;

            Rect rect = worldRect.rect;
            if (rect.width <= 1 || rect.height <= 1) 
                return;

            float scale = canvas != null ? Mathf.Max(0.01f, canvas.scaleFactor) : 1f;
            worldCenterUV -= new Vector2(screenDelta.x / scale / rect.width * worldUV.width,
                                         screenDelta.y / scale / rect.height * worldUV.height);
            ClampWorldCenter();
            UpdateMaps();
        }

        public void SelectWaypointFromScreen(Vector2 screenPosition, Camera eventCamera)
        {
            if (worldRect == null || !IsWorldMapOpen)
                return;

            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(worldRect, screenPosition,eventCamera, out Vector2 local))
                return;

            Rect rect = worldRect.rect;
            Vector2 pos = new Vector2(Mathf.InverseLerp(rect.xMin, rect.xMax, local.x),
                                      Mathf.InverseLerp(rect.yMin, rect.yMax, local.y));

            if (pos.x < 0 || pos.x > 1 || pos.y < 0 || pos.y > 1)
                return;

            Vector2 uv = worldUV.position + Vector2.Scale(pos, worldUV.size);
            Vector3 selectedPosition = mapData.UVToWorld(uv);

            // 샘플 월드는 바다 위 목적지 지정도 차단
            if (FindFirstObjectByType<MapSliceSampleWalker>() != null && !MapSliceCoastline.IsLand(new Vector2(selectedPosition.x, selectedPosition.z)))
                return;

            // 나무 줄기 또는 막힌 셀 클릭 시 가까운 이동 가능 셀로 보정
            Vector3 snappedPosition = selectedPosition;
            if (routePlanner != null && !routePlanner.TrySnapDestination(selectedPosition, out snappedPosition))
            {
                mapFeedback = "근처에 이동 가능한 경로가 없습니다 (바다/급경사/나무/지형 구멍)";
                feedbackUntil = Time.unscaledTime + 3f;
                UpdateMaps();
                return;
            }

            if (routePlanner != null)
                selectedPosition = snappedPosition;

            mapFeedback = null;
            WorldPositionSelected?.Invoke(selectedPosition);
            SetWaypoint(selectedPosition);
        }

        private void UpdateMaps()
        {
            if (miniImage == null || player == null)
                return;

            lastPlayerPosition = player.position;
            lastPlayerHeading = CurrentHeading;
            hasLastPlayerState = true;

            Vector2 playerUV = mapData.WorldToUV(player.position);
            float widthUV = 2f * miniMapWorldRadius / mapData.WorldWidth;
            float heightUV = 2f * miniMapWorldRadius / mapData.WorldHeight;

            // 원점과 화면 범위를 분리하여 맵 밖에서도 좌표가 튀지 않도록 처리
            miniUV = new Rect(playerUV.x - widthUV * 0.5f, playerUV.y - heightUV * 0.5f, widthUV, heightUV);
            miniImage.uvRect = miniUV;
            if (miniFogImage != null)
            {
                miniFogImage.texture = exploration != null ? exploration.FogTexture : null;
                miniFogImage.color = miniFogImage.texture != null ? Color.white : Color.clear;
                miniFogImage.uvRect = miniUV;
            }

            float visible = 1f / worldZoom;
            worldUV = new Rect(worldCenterUV.x - visible * 0.5f, worldCenterUV.y - visible * 0.5f, visible, visible);

            if (IsWorldMapOpen)
            {
                worldImage.uvRect = worldUV;
                if (worldFogImage != null)
                {
                    worldFogImage.texture = exploration != null ? exploration.FogTexture : null;
                    worldFogImage.color = worldFogImage.texture != null ? Color.white : Color.clear;
                    worldFogImage.uvRect = worldUV;
                }
            }

            PositionMarker(miniPlayer, playerUV, miniUV, false);
            PositionMarker(worldPlayer, playerUV, worldUV, true);

            float heading = CurrentHeading;
            miniPlayer.localRotation = Quaternion.Euler(0, 0, -heading);
            worldPlayer.localRotation = Quaternion.Euler(0, 0, -heading);

            PositionMarker(miniWaypoint, mapData.WorldToUV(waypoint), miniUV, false, hasWaypoint);
            PositionMarker(worldWaypoint, mapData.WorldToUV(waypoint), worldUV, true, hasWaypoint);

            foreach (MarkerWidget widget in miniMarkers)
            {
                Vector3 pos = new Vector3(widget.Marker.worldXZ.x, 0, widget.Marker.worldXZ.y);
                PositionMarker(widget.Rect, mapData.WorldToUV(pos), miniUV, true, !hideUndiscoveredMarkers || exploration == null || exploration.IsExplored(pos));
            }

            if (IsWorldMapOpen)
            {
                foreach (MarkerWidget widget in worldMarkers)
                {
                    Vector3 pos = new Vector3(widget.Marker.worldXZ.x, 0, widget.Marker.worldXZ.y);
                    PositionMarker(widget.Rect, mapData.WorldToUV(pos), worldUV, true, !hideUndiscoveredMarkers || exploration == null || exploration.IsExplored(pos));
                }
            }

            // 미니맵에도 전체 지도와 동일한 경로를 클리핑하여 표시
            DrawRoute(miniRect, miniUV, miniRouteSegments, 3f);

            if (miniDistanceLabel != null)
            {
                miniDistanceLabel.gameObject.SetActive(hasWaypoint);
                if (hasWaypoint)
                {
                    float remaining = Vector2.Distance(
                        new Vector2(player.position.x, player.position.z),
                        new Vector2(waypoint.x, waypoint.z));
                    miniDistanceLabel.text = $"목적지 {remaining:0}m";
                }
            }

            if (IsWorldMapOpen)
                DrawRoute(worldRect, worldUV, worldRouteSegments, 4f);

            miniPlayer.SetAsLastSibling();
            miniWaypoint.SetAsLastSibling();

            if (miniDistanceLabel != null)
                miniDistanceLabel.transform.SetAsLastSibling();

            worldPlayer.SetAsLastSibling();
            worldWaypoint.SetAsLastSibling();

            if (coordinateLabel != null && IsWorldMapOpen)
            {
                float straightDistance = hasWaypoint ?
                    Vector2.Distance(new Vector2(player.position.x, player.position.z),
                        new Vector2(waypoint.x, waypoint.z)) : 0f;

                float routeDistance = 0f;
                if (routePlanner != null && routePlanner.Corners != null)
                {
                    IReadOnlyList<Vector3> route = routePlanner.Corners;
                    for (int i = 1; i < route.Count; i++)
                        routeDistance += Vector2.Distance(new Vector2(route[i - 1].x, route[i - 1].z), new Vector2(route[i].x, route[i].z));
                }

                string distance = hasWaypoint ? $" | 직선 {straightDistance:0}m" : "";
                string routeStatus = routePlanner == null ? "" :
                    routePlanner.Quality == MapSliceRouteQuality.VerifiedNavMesh ? $" | NavMesh 경로 {routeDistance:0}m" :
                    routePlanner.Quality == MapSliceRouteQuality.CellPreview ? $" | 지형 기반 예상 경로 {routeDistance:0}m (미검증)" :
                    routePlanner.Quality == MapSliceRouteQuality.Unavailable ? " | 이동 경로 없음" : "";

                coordinateLabel.text = Time.unscaledTime < feedbackUntil && !string.IsNullOrEmpty(mapFeedback) ? mapFeedback : $"X {player.position.x:0} / Z {player.position.z:0}{distance}{routeStatus}";
            }
        }

        private void DrawRoute(RectTransform rect, Rect bounds, List<RectTransform> segments, float thickness)
        {
            if (!hasWaypoint || routePlanner == null || rect == null || routePlanner.Corners == null)
            {
                foreach (RectTransform segment in segments)
                {
                    if (segment != null)
                        segment.gameObject.SetActive(false);
                }
                return;
            }

            IReadOnlyList<Vector3> points = routePlanner.Corners;

            bool verified = routePlanner.Quality == MapSliceRouteQuality.VerifiedNavMesh;
            bool preview = routePlanner.Quality == MapSliceRouteQuality.CellPreview;
            Color color = verified ? new Color(0.25f, 1f, 0.65f, 0.95f) : new Color(1f, 0.8f, 0.25f, 0.9f);
            int used = 0;
            const int maxVisibleSegments = 1200;
            float width = rect.rect.width;
            float height = rect.rect.height;

            if (width <= 0f || height <= 0f || bounds.width <= 0f || bounds.height <= 0f)
                return;

            if (verified || preview)
            {
                for (int i = 0; i + 1 < points.Count && used < maxVisibleSegments; i++)
                {
                    Vector2 aUV = mapData.WorldToUV(points[i]);
                    Vector2 bUV = mapData.WorldToUV(points[i + 1]);
                    Vector2 a = new Vector2((aUV.x - bounds.xMin) / bounds.width * width, (aUV.y - bounds.yMin) / bounds.height * height);
                    Vector2 b = new Vector2((bUV.x - bounds.xMin) / bounds.width * width, (bUV.y - bounds.yMin) / bounds.height * height);

                    // 뷰 밖 선분은 잘라서 UI 오브젝트가 불필요하게 커지지 않도록 처리
                    if (!ClipToMap(ref a, ref b, width, height))
                        continue;

                    Vector2 delta = b - a;
                    float length = delta.magnitude;
                    if (length < 0.01f)
                        continue;

                    Vector2 forward = delta / length;
                    if (verified)
                        DrawOneSegment(rect, segments, used++, a, b, thickness, color);
                    else
                    {
                        // 기존 코드는 홀수 번째 '전체 경로 선분'을 생략해 경로가 끊겼음
                        // 수정 후에는 각 선분의 길이를 따라 10px 표시 / 6px 간격으로 반복
                        const float dash = 10f;
                        const float gap = 6f;
                        for (float t = 0f; t < length && used < maxVisibleSegments; t += dash + gap)
                        {
                            Vector2 first = a + forward * t;
                            Vector2 last = a + forward * Mathf.Min(length, t + dash);
                            DrawOneSegment(rect, segments, used++, first, last, thickness, color);
                        }
                    }
                }
            }

            for (int i = used; i < segments.Count; i++)
            {
                if (segments[i] != null)
                    segments[i].gameObject.SetActive(false);
            }
        }

        private static void DrawOneSegment(RectTransform root, List<RectTransform> pool, int index, Vector2 a, Vector2 b, float thickness, Color color)
        {
            while (pool.Count <= index)
            {
                RectTransform segment = CreateRect("RouteSegment", root);
                Image image = AddImage(segment, color);
                image.raycastTarget = false;
                pool.Add(segment);
            }

            RectTransform line = pool[index];

            if (!line.gameObject.activeSelf)
                line.gameObject.SetActive(true);

            line.GetComponent<Image>().color = color;
            line.anchorMin = line.anchorMax = Vector2.zero;
            line.pivot = new Vector2(0f, 0.5f);
            line.anchoredPosition = a;
            line.sizeDelta = new Vector2(Vector2.Distance(a, b), thickness);
            line.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(b.y - a.y, b.x - a.x) * Mathf.Rad2Deg);
        }

        private static bool ClipAxis(float p, float q, ref float min, ref float max)
        {
            if (Mathf.Abs(p) < 0.00001f)
                return q >= 0f;

            float t = q / p;
            if (p < 0f)
            {
                if (t > max)
                    return false;

                if (t > min)
                    min = t;
            }
            else
            {
                if (t < min)
                    return false;

                if (t < max)
                    max = t;
            }
            return true;
        }

        private static bool ClipToMap(ref Vector2 a, ref Vector2 b, float width, float height)
        {
            Vector2 start = a;
            Vector2 delta = b - start;
            float min = 0f, max = 1f;

            if (!ClipAxis(-delta.x, start.x, ref min, ref max) ||
                !ClipAxis(delta.x, width - start.x, ref min, ref max) ||
                !ClipAxis(-delta.y, start.y, ref min, ref max) ||
                !ClipAxis(delta.y, height - start.y, ref min, ref max))
            {
                return false;
            }

            a = start + delta * min;
            b = start + delta * max;
            return true;
        }

        private void ClampWorldCenter()
        {
            float half = 0.5f / worldZoom;
            worldCenterUV.x = Mathf.Clamp(worldCenterUV.x, half, 1f - half);
            worldCenterUV.y = Mathf.Clamp(worldCenterUV.y, half, 1f - half);
        }

        private static void PositionMarker(RectTransform marker, Vector2 uv, Rect view, bool hideOutside, bool enabled = true)
        {
            if (marker == null)
                return;

            Vector2 normalized = new Vector2((uv.x - view.xMin) / view.width, (uv.y - view.yMin) / view.height);

            // 미니맵 외곽 목적지는 가장자리까지 표시
            if (hideOutside && (normalized.x < 0 || normalized.x > 1 || normalized.y < 0 || normalized.y > 1))
                enabled = false;

            marker.gameObject.SetActive(enabled);
            
            if (!enabled)
                return;

            normalized.x = Mathf.Clamp01(normalized.x);
            normalized.y = Mathf.Clamp01(normalized.y);
            marker.anchorMin = normalized;
            marker.anchorMax = normalized;
            marker.anchoredPosition = Vector2.zero;
        }

        private void RebuildMarkers()
        {
            ClearWidgets(miniMarkers);
            ClearWidgets(worldMarkers);

            foreach (MapSliceMapMarker marker in mapData.Markers)
                AddMarkerWidgets(marker);

            foreach (MapSliceMapMarker marker in runtimeMarkers.Values)
                AddMarkerWidgets(marker);

            // 플레이어 마커가 POI보다 위에 보이게 배치
            miniPlayer.SetAsLastSibling();
            miniWaypoint.SetAsLastSibling();

            if (miniDistanceLabel != null)
                miniDistanceLabel.transform.SetAsLastSibling();

            worldPlayer.SetAsLastSibling();
            worldWaypoint.SetAsLastSibling();

            UpdateMaps();
        }

        private static void ClearWidgets(List<MarkerWidget> widgets)
        {
            foreach (MarkerWidget widget in widgets)
            {
                if (widget.Rect != null)
                    Destroy(widget.Rect.gameObject);
            }

            widgets.Clear();
        }

        private void AddMarkerWidgets(MapSliceMapMarker marker)
        {
            if (marker == null)
                return;
            if (marker.showOnMiniMap)
                miniMarkers.Add(new MarkerWidget { Rect = MakeMarker(miniRect, "POI", marker.color, 10), Marker = marker });
            if (marker.showOnWorldMap)
                worldMarkers.Add(new MarkerWidget { Rect = MakeMarker(worldRect, "POI", marker.color, 12), Marker = marker });
        }

        private void CreateUI()
        {
            var canvasGO = new GameObject("MapSliceMapCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvas = canvasGO.GetComponent<Canvas>();
            ownsCanvas = true;
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 180;
            CanvasScaler scaler = canvasGO.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = Screen.height > Screen.width ? new Vector2(1080f, 1920f) : new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            safeRoot = CreateRect("MapSafeArea", canvas.transform);
            SetStretch(safeRoot, 0f);
            RefreshSafeArea();

            miniRect = CreateRect("MiniMap", safeRoot);
            miniRect.anchorMin = miniRect.anchorMax = Vector2.one;
            miniRect.pivot = Vector2.one;
            miniRect.sizeDelta = new Vector2(miniMapSize, miniMapSize);
            miniRect.anchoredPosition = -miniMapMargin;
            AddImage(miniRect, new Color(0.05f, 0.07f, 0.09f, 0.94f));
            miniImage = CreateRawMap("MiniTexture", miniRect, mapData.Texture);
            SetStretch(miniImage.rectTransform, 6f);
            miniImage.raycastTarget = false;
            miniRect.gameObject.AddComponent<RectMask2D>();

            if (exploration != null)
            {
                miniFogImage = CreateRawMap("ExplorationFog", miniRect, exploration.FogTexture);
                SetStretch(miniFogImage.rectTransform, 6f);
                miniFogImage.raycastTarget = false;
            }

            // 플레이어와 목적지 마커는 지도 이미지 위에 표시
            miniPlayer = MakeMarker(miniRect, "Player", Color.cyan, 17, "▲");
            miniWaypoint = MakeMarker(miniRect, "Waypoint", Color.yellow, 15, "◆");
            miniDistanceLabel = CreateLabel("WaypointDistance", miniRect, 16, TextAnchor.MiddleCenter);
            miniDistanceLabel.text = "";
            miniDistanceLabel.raycastTarget = false;
            miniDistanceLabel.rectTransform.anchorMin = new Vector2(0.05f, 0.04f);
            miniDistanceLabel.rectTransform.anchorMax = new Vector2(0.95f, 0.18f);
            miniDistanceLabel.rectTransform.offsetMin = miniDistanceLabel.rectTransform.offsetMax = Vector2.zero;
            miniDistanceLabel.gameObject.SetActive(false);
            Button miniButton = miniRect.gameObject.AddComponent<Button>();
            miniButton.targetGraphic = miniRect.GetComponent<Image>();
            miniButton.onClick.AddListener(OpenWorldMap);

            worldPanel = CreateRect("WorldMapOverlay", safeRoot);
            SetStretch(worldPanel, 0);
            AddImage(worldPanel, new Color(0.015f, 0.02f, 0.025f, 0.94f));
            worldPanel.GetComponent<Image>().raycastTarget = true;

            RectTransform frame = CreateRect("MapFrame", worldPanel);
            frame.anchorMin = new Vector2(0.08f, 0.11f);
            frame.anchorMax = new Vector2(0.92f, 0.89f);
            frame.offsetMin = frame.offsetMax = Vector2.zero;
            worldRect = CreateRect("FullMap", frame);
            SetStretch(worldRect, 0);
            AspectRatioFitter fitter = worldRect.gameObject.AddComponent<AspectRatioFitter>();
            fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            fitter.aspectRatio = mapData.WorldWidth / mapData.WorldHeight;
            worldImage = worldRect.gameObject.AddComponent<RawImage>();
            worldImage.texture = mapData.Texture != null ? mapData.Texture : Texture2D.grayTexture;
            worldImage.raycastTarget = true;
            worldRect.gameObject.AddComponent<RectMask2D>();

            if (exploration != null)
            {
                worldFogImage = CreateRawMap("ExplorationFog", worldRect, exploration.FogTexture);
                SetStretch(worldFogImage.rectTransform, 0f);
                worldFogImage.raycastTarget = false;
            }

            MapSliceWorldMapPointer pointer = worldRect.gameObject.AddComponent<MapSliceWorldMapPointer>();
            pointer.Configure(this);
            worldPlayer = MakeMarker(worldRect, "Player", Color.cyan, 19, "▲");
            worldWaypoint = MakeMarker(worldRect, "Waypoint", Color.yellow, 20, "◆");

            coordinateLabel = CreateLabel("Coordinates", worldPanel, 21, TextAnchor.MiddleLeft);
            RectTransform labelRect = coordinateLabel.rectTransform;
            labelRect.anchorMin = new Vector2(0.1f, 0.025f);
            labelRect.anchorMax = new Vector2(0.78f, 0.095f);
            labelRect.offsetMin = labelRect.offsetMax = Vector2.zero;

            Button close = CreateButton("Close", worldPanel, "X", new Vector2(1, 1), new Vector2(-35, -30), new Vector2(62, 62));
            close.onClick.AddListener(CloseWorldMap);

            Button zoomIn = CreateButton("ZoomIn", worldPanel, "+", new Vector2(1, 0.5f), new Vector2(-35, 48), new Vector2(58, 58));
            zoomIn.onClick.AddListener(() => ZoomWorldMap(zoomStep));

            Button zoomOut = CreateButton("ZoomOut", worldPanel, "-", new Vector2(1, 0.5f), new Vector2(-35, -20), new Vector2(58, 58));
            zoomOut.onClick.AddListener(() => ZoomWorldMap(1f / zoomStep));

            Button clear = CreateButton("ClearWaypoint", worldPanel, "Clear waypoint", new Vector2(0.88f, 0.05f), Vector2.zero, new Vector2(180, 50));
            clear.onClick.AddListener(ClearWaypoint);

            worldPanel.gameObject.SetActive(false);
        }

        private void RefreshSafeArea()
        {
            if (safeRoot == null || Screen.width <= 0 || Screen.height <= 0)
                return;

            Rect safe = Screen.safeArea;
            Vector2Int resolution = new Vector2Int(Screen.width, Screen.height);

            if (lastScreenSize == resolution && lastSafeArea == safe)
                return;

            lastScreenSize = resolution;
            lastSafeArea = safe;
            CanvasScaler scale = canvas != null ? canvas.GetComponent<CanvasScaler>() : null;

            if (scale != null)
                scale.referenceResolution = Screen.height > Screen.width ? new Vector2(1080f, 1920f) : new Vector2(1920f, 1080f);

            safeRoot.anchorMin = new Vector2(safe.xMin / Screen.width, safe.yMin / Screen.height);
            safeRoot.anchorMax = new Vector2(safe.xMax / Screen.width, safe.yMax / Screen.height);
            safeRoot.offsetMin = Vector2.zero;
            safeRoot.offsetMax = Vector2.zero;
        }

        private static RectTransform CreateRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = go.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            return rect;
        }

        private static void SetStretch(RectTransform rect, float margin)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(margin, margin);
            rect.offsetMax = new Vector2(-margin, -margin);
        }

        private static Image AddImage(RectTransform rect, Color color)
        {
            Image image = rect.gameObject.AddComponent<Image>();
            image.color = color;
            return image;
        }

        private static RawImage CreateRawMap(string name, Transform parent, Texture texture)
        {
            RectTransform rect = CreateRect(name, parent);
            RawImage image = rect.gameObject.AddComponent<RawImage>();
            image.texture = texture != null ? texture : Texture2D.grayTexture;
            return image;
        }

        private RectTransform MakeMarker(RectTransform parent, string name, Color color, int size, string symbol = "●")
        {
            RectTransform rect = CreateRect(name, parent);
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(size + 12, size + 12);
            Text text = rect.gameObject.AddComponent<Text>();
            text.font = uiFont;
            text.text = symbol;
            text.fontSize = size;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = color;
            text.raycastTarget = false;
            return rect;
        }

        private Text CreateLabel(string name, Transform parent, int fontSize, TextAnchor alignment)
        {
            RectTransform rect = CreateRect(name, parent);
            Text text = rect.gameObject.AddComponent<Text>();
            text.font = uiFont;
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.color = Color.white;
            text.raycastTarget = false;
            return text;
        }

        private Button CreateButton(string name, Transform parent, string label, Vector2 anchor, Vector2 position, Vector2 size)
        {
            RectTransform rect = CreateRect(name, parent);
            rect.anchorMin = rect.anchorMax = anchor;
            rect.sizeDelta = size;
            rect.anchoredPosition = position;
            Image image = AddImage(rect, new Color(0.14f, 0.18f, 0.22f, 0.96f));
            Button button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            Text text = CreateLabel("Label", rect, 20, TextAnchor.MiddleCenter);
            text.text = label;
            SetStretch(text.rectTransform, 2f);
            return button;
        }

        private void EnsureEventSystem()
        {
            if (EventSystem.current != null)
                return;

            var go = new GameObject("MapSliceEventSystem", typeof(EventSystem));
#if ENABLE_INPUT_SYSTEM
            go.AddComponent<InputSystemUIInputModule>();
#elif ENABLE_LEGACY_INPUT_MANAGER
            go.AddComponent<StandaloneInputModule>();
#else
            if (!warnedNoEventSystem)
            {
                Debug.LogWarning("[MapSlice] UI Input Module을 EventSystem에 연결하세요.", this);
                warnedNoEventSystem = true;
            }
#endif
        }
    }

    // 클릭은 목적지 지정, 드래그는 지도 이동, 휠은 확대/축소
    public sealed class MapSliceWorldMapPointer : MonoBehaviour, IPointerDownHandler, IPointerClickHandler, IPointerUpHandler, IDragHandler, IScrollHandler
    {
        private MapSliceWorldMapUI owner;
        private bool dragged;
        private bool gestureWasMultiTouch;
        private readonly Dictionary<int, Vector2> touches = new Dictionary<int, Vector2>();

        public void Configure(MapSliceWorldMapUI value) => owner = value;

        public void OnPointerDown(PointerEventData eventData)
        {
            if (touches.Count == 0)
            {
                dragged = false;
                gestureWasMultiTouch = false;
            }

            touches[eventData.pointerId] = eventData.position;

            if (touches.Count >= 2)
                gestureWasMultiTouch = true;
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            touches.Remove(eventData.pointerId);
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (owner == null)
                return;

            dragged = true;

            if (touches.Count >= 2)
            {
                Vector2 other = eventData.position;
                bool found = false;
                foreach (KeyValuePair<int, Vector2> touch in touches)
                {
                    if (touch.Key != eventData.pointerId)
                    {
                        other = touch.Value;
                        found = true;
                        break;
                    }
                }

                if (found && touches.TryGetValue(eventData.pointerId, out Vector2 previous))
                {
                    float oldDistance = Vector2.Distance(previous, other);
                    float newDistance = Vector2.Distance(eventData.position, other);
                    if (oldDistance > 15f && newDistance > 15f)
                        owner.ZoomWorldMap(newDistance / oldDistance);
                }
            }
            else
                owner.PanWorldMap(eventData.delta);

            touches[eventData.pointerId] = eventData.position;
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (owner == null)
                return;

            if (dragged || gestureWasMultiTouch)
            {
                dragged = false;
                return;
            }

            if (eventData.button != PointerEventData.InputButton.Left)
                return;

            owner.SelectWaypointFromScreen(eventData.position, eventData.pressEventCamera);
        }

        public void OnScroll(PointerEventData eventData)
        {
            if (owner == null) 
                return;

            owner.ZoomWorldMap(eventData.scrollDelta.y > 0 ? 1.25f : 0.8f);
        }
    }
}
