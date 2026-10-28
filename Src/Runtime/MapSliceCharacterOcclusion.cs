using UnityEngine;

namespace MapSliceStreaming
{
    /// <summary>
    /// 캐릭터의 머리 위치만 사용하는 대신, 캐릭터 전체 바운드를 화면 좌표로 변환하여 가림 영역을 계산합니다.
    /// 3인칭 카메라의 이동과 회전이 반영된 후 처리하도록 LateUpdate에서 가림 영역을 갱신합니다.
    /// Terrain에 배치된 나무는 전역 셰이더 파라미터를 통해 제어하며, 기존 나무 렌더러나 Terrain 데이터는 직접 수정하지 않습니다.
    /// 노이즈 기반 Alpha Clipping을 적용하여 캐릭터를 가리는 나무 영역을 자연스럽게 처리합니다.
    /// </summary>
    [DefaultExecutionOrder(19000)]
    [DisallowMultipleComponent]
    public sealed class MapSliceCharacterOcclusion : MonoBehaviour
    {
        public Transform character;
        public Camera viewCamera;

        [Range(4f,80f)] public float extraPaddingPixels=15f;
        [Range(1f,20f)] public float transitionPixels=3f;
        [Range(0.0f,0.75f)] public float softFadeInnerAlpha=0.08f;
        [Range(2f,100f)] public float maxOccluderRange=38f;

        public Vector3 targetOffset=new Vector3(0f,1.05f,0f);
        public bool enableOcclusion=true;

        Renderer[] actorRenderers;
        Transform previousCharacter;
        float nextCacheTime;

        static readonly int CamId=Shader.PropertyToID("_CameraWS");
        static readonly int PlayerId=Shader.PropertyToID("_CameraToPlayer");
        static readonly int ScreenId=Shader.PropertyToID("_ScreenCenterRadii");
        static readonly int ParamsId=Shader.PropertyToID("_Parameters");

        void OnEnable() { Shader.SetGlobalVector(ParamsId, Vector4.zero); nextCacheTime = 0f; }
        void LateUpdate()
        {
            // 기존 프로젝트의 셰이더 효과가 중복 적용되어 캐릭터 주변에 점 형태의 투시 효과가 발생하지 않도록 방지합니다.
            Shader.SetGlobalFloat("_FadeEnabled", 0f);
            Shader.SetGlobalFloat("_Enable", 0f);
            Shader.SetGlobalVector("_OcclusionFlags", Vector4.zero);
            if (!enableOcclusion || !character)
            {
                Shader.SetGlobalVector(ParamsId, Vector4.zero);
                return;
            }

            Camera cam=viewCamera?viewCamera:Camera.main;
            if (!cam || cam.pixelWidth < 2 || cam.pixelHeight < 2)
            {
                Shader.SetGlobalVector(ParamsId, Vector4.zero); 
                return;
            }

            if (previousCharacter != character || actorRenderers == null || Time.unscaledTime >= nextCacheTime)
            {
                actorRenderers = character.GetComponentsInChildren<Renderer>();
                previousCharacter = character;
                nextCacheTime = Time.unscaledTime + 2f;
            }

            Bounds worldBounds=new Bounds(character.position+targetOffset,new Vector3(.65f,1.6f,.65f));
            bool found=false;
            foreach (Renderer r in actorRenderers)
            {
                // 캐릭터의 몸체와 장비 메시만 가림 영역 계산에 포함합니다.
                // 파티클이나 트레일 렌더러는 제외하며, 선택적 Unity 모듈에 대한 의존성을 방지하기 위해 해당 렌더러 클래스를 직접 참조하지 않습니다.
                if (!r || !r.enabled || !r.gameObject.activeInHierarchy || (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)))
                    continue;

                if (!found)
                {
                    worldBounds = r.bounds;
                    found = true;
                }
                else
                    worldBounds.Encapsulate(r.bounds);
            }

            Vector3 cameraPosition=cam.transform.position;
            Vector3 target=worldBounds.center;
            Vector3 direction=target-cameraPosition;
            float distance=direction.magnitude;
            if (distance < .25f)
            {
                Shader.SetGlobalVector(ParamsId, Vector4.zero);
                return;
            }

            direction /= distance;
            Vector3 projected=cam.WorldToViewportPoint(target);
            if (projected.z <= cam.nearClipPlane || projected.x < -.2f || projected.x > 1.2f || projected.y < -.2f || projected.y > 1.2f)
            {
                Shader.SetGlobalVector(ParamsId, Vector4.zero);
                return;
            }

            // 캐릭터 월드 바운드의 8개 꼭짓점을 화면 좌표로 변환합니다.
            // 머리 위치만 기준으로 계산할 때 발생할 수 있는 다리나 등 장비의 가림 문제를 방지하고,
            // 캐릭터 전체가 포함되는 가림 영역을 구합니다.
            Vector3 ext=worldBounds.extents;
            Vector3 center=worldBounds.center;
            float radiusX=0,radiusY=0;
            for (int x = -1; x <= 1; x += 2)
            {
                for (int y = -1; y <= 1; y += 2)
                {
                    for (int z = -1; z <= 1; z += 2)
                    {
                        Vector3 v=cam.WorldToViewportPoint(center+Vector3.Scale(ext,new Vector3(x,y,z)));
                        if (v.z <= 0)
                            continue;

                        radiusX = Mathf.Max(radiusX, Mathf.Abs(v.x - projected.x) * cam.pixelWidth);
                        radiusY = Mathf.Max(radiusY, Mathf.Abs(v.y - projected.y) * cam.pixelHeight);
                    }
                }
            }

            radiusX = Mathf.Clamp(radiusX + extraPaddingPixels, 18f, Mathf.Min(220, cam.pixelWidth * .43f));
            radiusY = Mathf.Clamp(radiusY + extraPaddingPixels, 24f, Mathf.Min(300, cam.pixelHeight * .48f));

            Shader.SetGlobalVector(CamId, new Vector4(cameraPosition.x, cameraPosition.y, cameraPosition.z, 1f));
            Shader.SetGlobalVector(PlayerId, new Vector4(direction.x, direction.y, direction.z, distance));
            Shader.SetGlobalVector(ScreenId, new Vector4(projected.x, projected.y, radiusX, radiusY));
            Shader.SetGlobalVector(ParamsId, new Vector4(1, transitionPixels, softFadeInnerAlpha, maxOccluderRange));
        }

        void OnDisable()
        {
            Shader.SetGlobalVector(ParamsId, Vector4.zero);
        }
    }
}
