using System;
using System.Collections.Generic;
using UnityEngine;

namespace MapSliceStreaming
{
    /// <summary>
    /// Addressables로 관리하는 Terrain 청크에 배치된 나무의 위치와 수종 정보를 에디터에서 미리 추출하여 저장합니다.
    /// 별도의 메시나 프리팹을 중복 생성하지 않고, 기존 나무의 5단계 LOD 메시와 머티리얼을 재사용합니다.
    /// 이를 통해 청크 로딩 전후의 나무 외형 차이를 줄이고, 원거리 숲이 자연스럽게 이어지도록 구성했습니다.
    /// </summary>
    [CreateAssetMenu(menuName = "Map Streaming/Real Forest Catalog")]
    public sealed class MapSliceRealForestCatalog : ScriptableObject
    {
        [Serializable]
        public sealed class Tree
        {
            public Vector3 position;
            public float rotationRadians;
            public float widthScale = 1f;
            public float heightScale = 1f;
            public int species;
        }

        [Serializable]
        public sealed class Tile
        {
            public Vector2Int coordinate;
            public Bounds worldBounds;
            public List<Tree> trees = new List<Tree>();
        }

        public List<Tile> tiles = new List<Tile>();
    }
}
