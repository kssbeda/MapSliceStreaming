using System;
using System.Collections.Generic;
using UnityEngine;

namespace MapSliceStreaming
{
    [Serializable]
    public sealed class MapSliceMapMarker
    {
        public string id = "poi";
        public string title = "Location";
        public Vector2 worldXZ;
        public Color color = new Color(1f, 0.85f, 0.3f, 1f);
        public bool showOnMiniMap = true;
        public bool showOnWorldMap = true;
    }

    [CreateAssetMenu(fileName = "WorldMap", menuName = "Map Streaming/World Map Data")]
    public sealed class MapSliceWorldMapData : ScriptableObject
    {
        [SerializeField] private Texture2D overviewTexture;
        [SerializeField] private Vector2 worldMinXZ;
        [SerializeField] private Vector2 worldMaxXZ = new Vector2(1024f, 1024f);
        [SerializeField] private List<MapSliceMapMarker> markers = new List<MapSliceMapMarker>();

        public Texture2D Texture => overviewTexture;
        public Vector2 WorldMin => worldMinXZ;
        public Vector2 WorldMax => worldMaxXZ;
        public IReadOnlyList<MapSliceMapMarker> Markers => markers;
        public float WorldWidth => Mathf.Max(0.01f, worldMaxXZ.x - worldMinXZ.x);
        public float WorldHeight => Mathf.Max(0.01f, worldMaxXZ.y - worldMinXZ.y);

        public Vector2 WorldToUV(Vector3 position)
        {
            return new Vector2((position.x - worldMinXZ.x) / WorldWidth,
                               (position.z - worldMinXZ.y) / WorldHeight);
        }

        public Vector3 UVToWorld(Vector2 uv, float y = 0f)
        {
            return new Vector3(Mathf.Lerp(worldMinXZ.x, worldMaxXZ.x, uv.x), y,
                               Mathf.Lerp(worldMinXZ.y, worldMaxXZ.y, uv.y));
        }

#if UNITY_EDITOR
        public void SetBakedMap(Texture2D texture, Vector2 min, Vector2 max)
        {
            overviewTexture = texture;
            worldMinXZ = min;
            worldMaxXZ = max;
        }
#endif
    }
}
