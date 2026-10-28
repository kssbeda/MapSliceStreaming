using System.Collections.Generic;
using UnityEngine;

namespace MapSliceStreaming
{
    // 로드된 지형만 대상으로 이웃 관계를 갱신
    public sealed class MapSliceTerrainNeighbors
    {
        private readonly Dictionary<Vector2Int, Terrain> terrains = new Dictionary<Vector2Int, Terrain>();
        private bool androidRenderModeLogged;

        public void Register(Vector2Int coordinate, GameObject root)
        {
            if (root == null)
                return;

            Terrain terrain = root.GetComponentInChildren<Terrain>(true);
            if (terrain == null)
                return;

            terrain.groupingID = 52;
            terrain.allowAutoConnect = true;
            // Android에서 지형 표면이 누락되는 경우를 분리하기 위해 Terrain GPU Instancing만 해제
            // Editor 및 PC의 기존 렌더링 방식은 유지
            bool isAndroidPlayer = Application.platform == RuntimePlatform.Android;
            terrain.drawInstanced = !isAndroidPlayer;

            // 첫 청크에서만 표시하여 로그가 지나치게 늘어나지 않도록 처리
            if (isAndroidPlayer && !androidRenderModeLogged)
            {
                androidRenderModeLogged = true;
                Debug.Log("[MapSlice Terrain] Android Terrain GPU Instancing OFF (drawInstanced=False)");
            }

            // 서로 다른 Full Terrain의 LOD·Basemap 전환 차이를 줄임
            terrain.heightmapPixelError = 5f;
            terrain.basemapDistance = 500f;
            terrains[coordinate] = terrain;
            UpdateAround(coordinate);
        }

        public void Unregister(Vector2Int coordinate)
        {
            if (terrains.TryGetValue(coordinate, out Terrain terrain) && terrain != null)
                terrain.SetNeighbors(null, null, null, null);

            terrains.Remove(coordinate);
            UpdateAround(coordinate);
        }

        public void Clear()
        {
            foreach (Terrain terrain in terrains.Values)
                if (terrain != null)
                    terrain.SetNeighbors(null, null, null, null);
            terrains.Clear();
        }

        private void UpdateAround(Vector2Int center)
        {
            UpdateOne(center);
            UpdateOne(center + Vector2Int.left);
            UpdateOne(center + Vector2Int.right);
            UpdateOne(center + Vector2Int.up);
            UpdateOne(center + Vector2Int.down);
        }

        private Terrain Get(Vector2Int key)
        {
            terrains.TryGetValue(key, out Terrain terrain);
            return terrain;
        }

        private void UpdateOne(Vector2Int key)
        {
            Terrain terrain = Get(key);
            if (terrain == null)
                return;

            // +Z 방향을 Terrain.SetNeighbors의 top에 대응
            terrain.SetNeighbors(
                Get(key + Vector2Int.left),
                Get(key + Vector2Int.up),
                Get(key + Vector2Int.right),
                Get(key + Vector2Int.down));
        }
    }
}