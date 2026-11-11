// TCG-PlayKyn: 3D physical card mesh instance with depth, shadows and condition tilting
using System;
using System.Collections.Generic;
using UnityEngine;

namespace TCGPlayKyn
{
    /// <summary>
    /// F2: Representación física 3D de una carta Pokémon sobre la mesa.
    /// Incluye grosor real (núcleo 3D), cara frontal con el sprite de MyCurio, cara trasera con el reverso oficial,
    /// sombra proyectada sobre el tapete y borde de resaltado.
    /// </summary>
    public class CardView : MonoBehaviour
    {
        public const float CardThickness = 0.0010f;

        public CardDef Card { get; private set; }
        public bool FaceDown { get; private set; }

        // F4: Metadatos de interacción 3D para Raycast (Mano 3D, Pokémon en mesa, Descarte)
        public int HandIndex { get; set; } = -1;
        public int OwnerIndex { get; set; } = -1;
        public PokemonInPlay BoundPip { get; set; }
        public bool IsDiscardTop { get; set; }
        public float CurrentScale { get; private set; } = 1f;
        public float CurrentYaw { get; private set; }
        public bool IsRaycastEnabled { get; private set; } = true;
        public bool IsGrayscale { get; private set; }
        public BoxCollider HitCollider { get; private set; }

        Transform visualRoot;
        MeshRenderer coreRenderer;
        MeshRenderer frontRenderer;
        MeshRenderer backRenderer;
        MeshRenderer shadowRenderer;
        MeshRenderer highlightRenderer;
        MeshRenderer tintRenderer;

        Material frontMat;
        Material backMat;
        Material highlightMat;
        Material tintMat;

        static Mesh sQuadMesh;
        static Mesh sCubeMesh;
        static Material sCoreMat;
        static Material sShadowMat;
        static Texture2D sShadowTex;
        static Texture2D sFallbackFrontTex;

        public static void EnsureSharedResources()
        {
            if (sQuadMesh == null)
            {
                var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
                sQuadMesh = q.GetComponent<MeshFilter>().sharedMesh;
                DestroyImmediate(q);
            }
            if (sCubeMesh == null)
            {
                var c = GameObject.CreatePrimitive(PrimitiveType.Cube);
                sCubeMesh = c.GetComponent<MeshFilter>().sharedMesh;
                DestroyImmediate(c);
            }
            if (sCoreMat == null)
            {
                sCoreMat = CreateUnlitMaterial(new Color(0.13f, 0.11f, 0.15f, 1f), null, false);
            }
            if (sShadowTex == null)
            {
                sShadowTex = MakeSoftShadowTex(64, 64);
                sShadowMat = CreateUnlitMaterial(new Color(0f, 0f, 0f, 0.48f), sShadowTex, true);
            }
            if (sFallbackFrontTex == null)
            {
                sFallbackFrontTex = MakeSolidTex(new Color(0.18f, 0.25f, 0.38f, 1f));
            }
        }

        public static CardView Create(Transform parent)
        {
            EnsureSharedResources();
            var rootGo = new GameObject("PokeTCG_Card3D");
            if (parent != null) rootGo.transform.SetParent(parent, false);
            var cv = rootGo.AddComponent<CardView>();
            cv.BuildHierarchy();
            return cv;
        }

        void BuildHierarchy()
        {
            float w = TableView.CardWorldWidth;
            float l = TableView.CardWorldLength;

            // Sombra suave pegada al tapete (no gira cuando la carta se voltea boca abajo)
            var shadowGo = new GameObject("Shadow");
            shadowGo.transform.SetParent(transform, false);
            shadowGo.transform.localPosition = new Vector3(0.002f, -CardThickness * 0.45f, -0.002f);
            shadowGo.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            shadowGo.transform.localScale = new Vector3(w * 1.10f, l * 1.08f, 1f);
            shadowGo.AddComponent<MeshFilter>().sharedMesh = sQuadMesh;
            shadowRenderer = shadowGo.AddComponent<MeshRenderer>();
            shadowRenderer.sharedMaterial = sShadowMat;
            shadowRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            // Marco de resaltado dorado (para selección o destino válido)
            var hlGo = new GameObject("Highlight");
            hlGo.transform.SetParent(transform, false);
            hlGo.transform.localPosition = new Vector3(0f, -CardThickness * 0.35f, 0f);
            hlGo.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            hlGo.transform.localScale = new Vector3(w * 1.08f, l * 1.06f, 1f);
            hlGo.AddComponent<MeshFilter>().sharedMesh = sQuadMesh;
            highlightRenderer = hlGo.AddComponent<MeshRenderer>();
            highlightMat = CreateUnlitMaterial(new Color(1f, 0.84f, 0.18f, 0.90f), null, true);
            highlightRenderer.sharedMaterial = highlightMat;
            highlightRenderer.enabled = false;

            // Raíz visual que rota 180° en Z cuando la carta está boca abajo
            visualRoot = new GameObject("CardBody").transform;
            visualRoot.SetParent(transform, false);

            // F4: Collider 3D (trigger) para Raycast de mano y mesa (gira solidario con visualRoot)
            HitCollider = visualRoot.gameObject.AddComponent<BoxCollider>();
            HitCollider.isTrigger = true;
            HitCollider.size = new Vector3(w, CardThickness * 5f, l);
            HitCollider.center = Vector3.zero;

            // Núcleo con grosor físico de cartulina
            var coreGo = new GameObject("CoreEdge");
            coreGo.transform.SetParent(visualRoot, false);
            coreGo.transform.localPosition = Vector3.zero;
            coreGo.transform.localScale = new Vector3(w, CardThickness, l);
            coreGo.AddComponent<MeshFilter>().sharedMesh = sCubeMesh;
            coreRenderer = coreGo.AddComponent<MeshRenderer>();
            coreRenderer.sharedMaterial = sCoreMat;

            // Cara Frontal (+Y mirando hacia arriba)
            var frontGo = new GameObject("FrontFace");
            frontGo.transform.SetParent(visualRoot, false);
            frontGo.transform.localPosition = new Vector3(0f, CardThickness * 0.5f + 0.00012f, 0f);
            frontGo.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            frontGo.transform.localScale = new Vector3(w, l, 1f);
            frontGo.AddComponent<MeshFilter>().sharedMesh = sQuadMesh;
            frontRenderer = frontGo.AddComponent<MeshRenderer>();
            frontMat = CreateUnlitMaterial(Color.white, sFallbackFrontTex, false);
            frontRenderer.sharedMaterial = frontMat;

            // F6: Capa de tinte animado para estados alterados (Dormido, Paralizado, Confundido, Veneno) y destello de golpe
            var tintGo = new GameObject("StatusTintOverlay");
            tintGo.transform.SetParent(visualRoot, false);
            tintGo.transform.localPosition = new Vector3(0f, CardThickness * 0.5f + 0.00024f, 0f);
            tintGo.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            tintGo.transform.localScale = new Vector3(w, l, 1f);
            tintGo.AddComponent<MeshFilter>().sharedMesh = sQuadMesh;
            tintRenderer = tintGo.AddComponent<MeshRenderer>();
            tintMat = CreateUnlitMaterial(Color.clear, null, true);
            tintRenderer.sharedMaterial = tintMat;
            tintRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            tintRenderer.enabled = false;

            // Cara Trasera (-Y mirando hacia abajo cuando la carta está boca arriba, y hacia arriba cuando visualRoot gira 180° en Z)
            var backGo = new GameObject("BackFace");
            backGo.transform.SetParent(visualRoot, false);
            backGo.transform.localPosition = new Vector3(0f, -CardThickness * 0.5f - 0.00012f, 0f);
            backGo.transform.localRotation = Quaternion.Euler(-90f, 0f, 180f);
            backGo.transform.localScale = new Vector3(w, l, 1f);
            backGo.AddComponent<MeshFilter>().sharedMesh = sQuadMesh;
            backRenderer = backGo.AddComponent<MeshRenderer>();
            var cbTex = SpriteCache.GetCardBack()?.texture;
            backMat = CreateUnlitMaterial(Color.white, cbTex != null ? cbTex : sFallbackFrontTex, false);
            backRenderer.sharedMaterial = backMat;
        }

        /// <summary>
        /// Configura la carta 3D (frente/reverso, escala, resaltado y rotación en mesa).
        /// </summary>
        public void Setup(CardDef card, bool faceDown, float scale = 1f, bool highlight = false, float yawDegrees = 0f, float pitchDegrees = 0f)
        {
            Card = card;
            FaceDown = faceDown;
            HandIndex = -1;
            OwnerIndex = -1;
            BoundPip = null;
            IsDiscardTop = false;
            CurrentScale = scale;
            CurrentYaw = yawDegrees;
            IsRaycastEnabled = true;
            IsGrayscale = false;
            if (HitCollider != null) HitCollider.enabled = true;

            transform.localScale = new Vector3(scale, 1f, scale);
            visualRoot.localRotation = Quaternion.Euler(pitchDegrees, yawDegrees, faceDown ? 180f : 0f);
            if (shadowRenderer != null)
            {
                shadowRenderer.enabled = true;
                shadowRenderer.transform.localRotation = Quaternion.Euler(90f, yawDegrees, 0f);
            }
            if (highlightRenderer != null)
            {
                highlightRenderer.enabled = highlight;
                if (highlightMat != null)
                    highlightMat.color = new Color(1f, 0.84f, 0.18f, 0.90f);
                highlightRenderer.transform.localRotation = Quaternion.Euler(90f, yawDegrees, 0f);
            }
            if (tintRenderer != null)
            {
                tintRenderer.enabled = false;
            }

            RefreshTextures();
        }

        public void SetStatusTintOverlay(Color overlayColor)
        {
            if (tintRenderer == null || tintMat == null) return;
            if (overlayColor.a <= 0.005f)
            {
                tintRenderer.enabled = false;
                return;
            }
            tintMat.color = overlayColor;
            tintRenderer.enabled = true;
        }

        public void SetHighlight(bool enabled, Color color)
        {
            if (highlightRenderer == null) return;
            highlightRenderer.enabled = enabled;
            if (enabled && highlightMat != null)
                highlightMat.color = color;
        }

        public void SetShadowEnabled(bool enabled)
        {
            if (shadowRenderer != null)
                shadowRenderer.enabled = enabled;
        }

        public void SetRaycastEnabled(bool enabled)
        {
            IsRaycastEnabled = enabled;
            if (HitCollider != null)
                HitCollider.enabled = enabled;
        }

        public void SetFlipRoll(float flipZ, float pitchDegrees = 0f, float yawDegrees = 0f)
        {
            if (visualRoot != null)
                visualRoot.localRotation = Quaternion.Euler(pitchDegrees, yawDegrees, flipZ);
        }

        public void SetGrayscale(bool grayscale)
        {
            if (IsGrayscale == grayscale) return;
            IsGrayscale = grayscale;
            RefreshTextures();
        }

        public void RefreshTextures()
        {
            // Reverso oficial
            var cb = SpriteCache.GetCardBack();
            if (cb != null && cb.texture != null && backMat != null)
            {
                ApplySpriteToMaterial(backMat, cb);
            }

            // Frente de la carta (MyCurio via EPL)
            if (Card != null && frontMat != null)
            {
                var sp = SpriteCache.Get(Card.Sprite);
                if (sp != null && sp.texture != null)
                {
                    if (IsGrayscale)
                    {
                        var grayTex = SpriteCache.GetGrayscaleTexture(Card.Sprite, sp);
                        if (grayTex != null)
                        {
                            frontMat.color = Color.white;
                            frontMat.mainTexture = grayTex;
                            frontMat.mainTextureOffset = Vector2.zero;
                            frontMat.mainTextureScale = Vector2.one;
                            return;
                        }
                    }
                    frontMat.color = Color.white;
                    ApplySpriteToMaterial(frontMat, sp);
                }
                else
                {
                    frontMat.mainTexture = sFallbackFrontTex;
                    frontMat.color = IsGrayscale ? new Color(0.28f, 0.28f, 0.30f, 1f) : new Color(0.22f, 0.32f, 0.48f, 1f);
                }
            }
        }

        static void ApplySpriteToMaterial(Material mat, Sprite sp)
        {
            Texture2D t = sp.texture;
            Rect tr = sp.textureRect;
            mat.mainTexture = t;
            mat.mainTextureOffset = new Vector2(tr.x / t.width, tr.y / t.height);
            mat.mainTextureScale = new Vector2(tr.width / t.width, tr.height / t.height);
        }

        public static Material CreateUnlitMaterial(Color color, Texture2D tex, bool transparent)
        {
            Shader sh = Shader.Find(transparent ? "Sprites/Default" : (tex == null ? "Unlit/Color" : "Unlit/Texture"));
            if (sh == null) sh = Shader.Find(transparent ? "Sprites/Default" : "Unlit/Texture");
            if (sh == null) sh = Shader.Find("Standard");
            var m = new Material(sh);
            m.color = color;
            if (tex != null)
                m.mainTexture = tex;
            else if (!transparent)
                m.mainTexture = MakeSolidTex(color);
            return m;
        }

        static Texture2D MakeSolidTex(Color col)
        {
            var t = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            var cols = new Color[16];
            for (int i = 0; i < 16; i++) cols[i] = col;
            t.SetPixels(cols);
            t.Apply();
            return t;
        }

        static Texture2D MakeSoftShadowTex(int w, int h)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false);
            t.wrapMode = TextureWrapMode.Clamp;
            for (int y = 0; y < h; y++)
            {
                float ny = (y / (float)(h - 1)) * 2f - 1f;
                for (int x = 0; x < w; x++)
                {
                    float nx = (x / (float)(w - 1)) * 2f - 1f;
                    float edge = Mathf.Max(Mathf.Abs(nx), Mathf.Abs(ny));
                    float alpha = Mathf.Clamp01((1f - edge) * 3.2f);
                    t.SetPixel(x, y, new Color(0f, 0f, 0f, alpha));
                }
            }
            t.Apply();
            return t;
        }
    }

    /// <summary>
    /// F2 & F5: Pila 3D física realista para el Mazo (Deck), compuesta por múltiples capas/paquetes de cartas
    /// con micro-desplazamientos orgánicos, cantos laminados visibles y bordes azules del reverso asomando entre capas.
    /// </summary>
    public class DeckStack3D
    {
        const int MaxSlices = 14;

        struct DeckSlice
        {
            public GameObject Root;
            public Transform Block;
            public Transform TopCard;
            public float OffX;
            public float OffZ;
            public float Yaw;
            public float Weight;
        }

        public GameObject Root { get; private set; }
        readonly DeckSlice[] slices = new DeckSlice[MaxSlices];
        MeshRenderer shadowRenderer;
        Material topMat;

        static Texture2D sDeckSideTex;
        static Material sDeckSideMat;

        // Desplazamientos orgánicos (en metros y grados) que imitan un mazo real barajado a mano como en la foto de referencia
        static readonly float[] sPresetOffX =
        {
            -0.0005f, -0.0011f, -0.0003f,  0.0008f,  0.0002f, -0.0009f, -0.0014f,
            -0.0004f,  0.0007f, -0.0006f, -0.0016f, -0.0021f,  0.0006f,  0.0009f
        };
        static readonly float[] sPresetOffZ =
        {
             0.0003f, -0.0004f, -0.0009f, -0.0002f,  0.0006f,  0.0009f,  0.0002f,
            -0.0006f, -0.0003f,  0.0005f,  0.0008f,  0.0004f, -0.0005f, -0.0002f
        };
        static readonly float[] sPresetYaw =
        {
            -0.4f,  0.6f,  0.9f, -0.5f, -0.8f,  0.4f,  1.1f,
             0.3f, -0.7f,  0.5f,  1.2f, -0.6f,  0.8f,  0.3f
        };
        static readonly float[] sPresetWeight =
        {
            1.35f, 1.30f, 1.25f, 1.20f, 1.15f, 1.10f, 1.00f,
            0.95f, 0.85f, 0.75f, 0.65f, 0.55f, 0.45f, 0.38f
        };

        public DeckStack3D(Transform anchor)
        {
            CardView.EnsureSharedResources();
            if (sDeckSideTex == null)
            {
                sDeckSideTex = MakeDeckEdgesTex(64, 256);
                sDeckSideMat = CardView.CreateUnlitMaterial(Color.white, sDeckSideTex, false);
            }

            float w = TableView.CardWorldWidth;
            float l = TableView.CardWorldLength;

            Root = new GameObject("DeckStack3D");
            Root.transform.SetParent(anchor, false);
            Root.transform.localPosition = Vector3.zero;
            Root.transform.localRotation = Quaternion.identity;

            // Sombra suave y profunda en la base del mazo físico
            var shGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            UnityEngine.Object.DestroyImmediate(shGo.GetComponent<Collider>());
            shGo.name = "DeckShadow";
            shGo.transform.SetParent(Root.transform, false);
            shGo.transform.localPosition = new Vector3(0.0032f, 0.0002f, -0.0032f);
            shGo.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            shGo.transform.localScale = new Vector3(w * 1.18f, l * 1.15f, 1f);
            shadowRenderer = shGo.GetComponent<MeshRenderer>();
            shadowRenderer.sharedMaterial = CardView.CreateUnlitMaterial(new Color(0f, 0f, 0f, 0.62f), null, true);

            topMat = CardView.CreateUnlitMaterial(Color.white, SpriteCache.GetCardBack()?.texture, false);

            for (int i = 0; i < MaxSlices; i++)
            {
                var sliceGo = new GameObject($"DeckSlice_{i}");
                sliceGo.transform.SetParent(Root.transform, false);

                var blockGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
                UnityEngine.Object.DestroyImmediate(blockGo.GetComponent<Collider>());
                blockGo.name = "SliceEdge";
                blockGo.transform.SetParent(sliceGo.transform, false);
                blockGo.GetComponent<MeshRenderer>().sharedMaterial = sDeckSideMat;

                var topGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
                UnityEngine.Object.DestroyImmediate(topGo.GetComponent<Collider>());
                topGo.name = "SliceTopBack";
                topGo.transform.SetParent(sliceGo.transform, false);
                topGo.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                topGo.transform.localScale = new Vector3(w, l, 1f);
                topGo.GetComponent<MeshRenderer>().sharedMaterial = topMat;

                slices[i] = new DeckSlice
                {
                    Root = sliceGo,
                    Block = blockGo.transform,
                    TopCard = topGo.transform,
                    OffX = sPresetOffX[i],
                    OffZ = sPresetOffZ[i],
                    Yaw = sPresetYaw[i],
                    Weight = sPresetWeight[i]
                };
            }
        }

        public void Sync(int cardCount)
        {
            if (Root == null) return;
            if (cardCount <= 0)
            {
                if (Root.activeSelf) Root.SetActive(false);
                return;
            }
            if (!Root.activeSelf) Root.SetActive(true);

            var cb = SpriteCache.GetCardBack();
            if (cb != null && cb.texture != null && topMat != null)
            {
                Texture2D t = cb.texture;
                Rect tr = cb.textureRect;
                topMat.mainTexture = t;
                topMat.mainTextureOffset = new Vector2(tr.x / t.width, tr.y / t.height);
                topMat.mainTextureScale = new Vector2(tr.width / t.width, tr.height / t.height);
            }

            // Altura física realista proporcional al número de cartas (60 cartas ≈ 2.85 cm de alto)
            float totalHeight = Mathf.Max(0.0014f, cardCount * 0.00048f);
            int activeCount = Mathf.Clamp(Mathf.CeilToInt(cardCount / 4.2f), 1, MaxSlices);

            float weightSum = 0f;
            for (int i = 0; i < activeCount; i++)
            {
                int presetIdx = MaxSlices - activeCount + i;
                weightSum += slices[presetIdx].Weight;
            }

            float w = TableView.CardWorldWidth;
            float l = TableView.CardWorldLength;
            float curY = 0f;

            for (int i = 0; i < MaxSlices; i++)
            {
                if (i >= activeCount)
                {
                    if (slices[i].Root.activeSelf)
                        slices[i].Root.SetActive(false);
                    continue;
                }

                if (!slices[i].Root.activeSelf)
                    slices[i].Root.SetActive(true);

                // Usar los desplazamientos superiores para que siempre se vean las cartas individuales de arriba escalonadas
                int presetIdx = MaxSlices - activeCount + i;
                float sliceH = totalHeight * (slices[presetIdx].Weight / Mathf.Max(0.01f, weightSum));
                float offScale = cardCount <= 3 ? 0.35f : 1f;

                Transform sRoot = slices[i].Root.transform;
                sRoot.localPosition = new Vector3(sPresetOffX[presetIdx] * offScale, curY, sPresetOffZ[presetIdx] * offScale);
                sRoot.localRotation = Quaternion.Euler(0f, sPresetYaw[presetIdx] * offScale, 0f);

                slices[i].Block.localScale = new Vector3(w, sliceH, l);
                slices[i].Block.localPosition = new Vector3(0f, sliceH * 0.5f, 0f);
                slices[i].TopCard.localPosition = new Vector3(0f, sliceH + 0.00012f, 0f);

                curY += sliceH;
            }
        }

        static Texture2D MakeDeckEdgesTex(int w, int h)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false);
            t.wrapMode = TextureWrapMode.Repeat;
            t.filterMode = FilterMode.Bilinear;

            // Estilo oscuro uniforme idéntico al bloque físico nativo de la baraja del juego (30, 24, 32)
            for (int y = 0; y < h; y++)
            {
                int mod = y % 5;
                Color baseCol = mod == 0
                    ? new Color(0.09f, 0.08f, 0.11f, 1f)
                    : new Color(0.13f, 0.11f, 0.15f, 1f);

                for (int x = 0; x < w; x++)
                {
                    float nx = Mathf.Abs((x / (float)(w - 1)) * 2f - 1f);
                    float cornerShade = nx > 0.86f ? Mathf.Lerp(1f, 0.72f, (nx - 0.86f) / 0.14f) : 1f;
                    t.SetPixel(x, y, new Color(baseCol.r * cornerShade, baseCol.g * cornerShade, baseCol.b * cornerShade, 1f));
                }
            }
            t.Apply();
            return t;
        }
    }

    /// <summary>
    /// Especificación geométrica de la malla 3D nativa "CardDeck" capturada de la mesa actual para construir
    /// mazos y cartas de Premio físicas con esquinas redondeadas e iluminación real sin alterar otras mesas.
    /// </summary>
    public class NativeDeckMeshSpec
    {
        public Mesh Mesh;
        public Material InstanceMaterial;
        public Quaternion LocalRotation;
        public Vector3 NativeLossyScale;
        public Vector3 PivotOffset;
        public float NativeWidth;
        public float NativeHeight;
        public float NativeLength;

        public bool IsValid => Mesh != null && InstanceMaterial != null &&
                               NativeWidth > 0.001f && NativeHeight > 0.0005f && NativeLength > 0.001f;
    }

    /// <summary>
    /// Pool de bloques físicos 3D redondeados basados en la malla nativa de la baraja (usado tanto para el Mazo
    /// como para que las 6 cartas de Premio a los costados luzcan como cartas físicas reales idénticas a la baraja).
    /// </summary>
    public class PhysicalCardBlockPool
    {
        class BlockItem
        {
            public GameObject Holder;
            public Transform MeshChild;
            public MeshFilter Filter;
            public MeshRenderer Renderer;
            public Transform Shadow;
        }

        readonly Transform poolRoot;
        readonly List<BlockItem> activeItems = new List<BlockItem>();
        readonly Stack<BlockItem> inactiveItems = new Stack<BlockItem>();
        static Material sSoftShadowMat;

        public PhysicalCardBlockPool(Transform parent)
        {
            CardView.EnsureSharedResources();
            var go = new GameObject("PhysicalCardBlockPool_3D");
            go.transform.SetParent(parent, false);
            poolRoot = go.transform;

            if (sSoftShadowMat == null)
            {
                sSoftShadowMat = CardView.CreateUnlitMaterial(new Color(0f, 0f, 0f, 0.44f), null, true);
            }
        }

        public void BeginFrame()
        {
            for (int i = 0; i < activeItems.Count; i++)
            {
                var it = activeItems[i];
                if (it?.Holder != null)
                {
                    it.Holder.SetActive(false);
                    inactiveItems.Push(it);
                }
            }
            activeItems.Clear();
        }

        public void SpawnBlock(Transform anchor, Vector3 localOffset, NativeDeckMeshSpec spec, float footprintScale, float heightMeters, float yawDegrees = 0f)
        {
            if (anchor == null || spec == null || !spec.IsValid) return;

            BlockItem item = null;
            while (inactiveItems.Count > 0 && item == null)
            {
                var cand = inactiveItems.Pop();
                if (cand?.Holder != null) item = cand;
            }

            if (item == null)
            {
                var holder = new GameObject("PhysicalCardBlock3D");
                holder.transform.SetParent(poolRoot, false);

                var shGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
                UnityEngine.Object.DestroyImmediate(shGo.GetComponent<Collider>());
                shGo.name = "ContactShadow";
                shGo.transform.SetParent(holder.transform, false);
                shGo.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                var shMr = shGo.GetComponent<MeshRenderer>();
                shMr.sharedMaterial = sSoftShadowMat;
                shMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

                var meshGo = new GameObject("NativeCardMesh");
                meshGo.transform.SetParent(holder.transform, false);
                var mf = meshGo.AddComponent<MeshFilter>();
                var mr = meshGo.AddComponent<MeshRenderer>();

                item = new BlockItem
                {
                    Holder = holder,
                    MeshChild = meshGo.transform,
                    Filter = mf,
                    Renderer = mr,
                    Shadow = shGo.transform
                };
            }

            float targetW = TableView.CardWorldWidth * footprintScale;
            float targetL = TableView.CardWorldLength * footprintScale;
            float sx = targetW / spec.NativeWidth;
            float sy = Mathf.Max(0.0008f, heightMeters) / spec.NativeHeight;
            float sz = targetL / spec.NativeLength;

            item.Holder.transform.SetParent(anchor, false);
            item.Holder.transform.localPosition = localOffset;
            item.Holder.transform.localRotation = Quaternion.Euler(0f, yawDegrees, 0f);
            item.Holder.transform.localScale = new Vector3(sx, sy, sz);

            item.MeshChild.localPosition = spec.PivotOffset;
            item.MeshChild.localRotation = spec.LocalRotation;
            item.MeshChild.localScale = spec.NativeLossyScale;

            item.Filter.sharedMesh = spec.Mesh;
            item.Renderer.sharedMaterial = spec.InstanceMaterial;

            item.Shadow.localPosition = new Vector3(0.002f / Mathf.Max(0.1f, sx), 0.0001f / Mathf.Max(0.05f, sy), -0.002f / Mathf.Max(0.1f, sz));
            item.Shadow.localScale = new Vector3(spec.NativeWidth * 1.07f, spec.NativeLength * 1.05f, 1f);

            item.Holder.SetActive(true);
            activeItems.Add(item);
        }
    }

    /// <summary>
    /// F2: Pool de objetos CardView para renderizar todas las cartas en 3D sobre la mesa sin alojar memoria cada frame.
    /// </summary>
    public class CardViewPool
    {
        readonly Transform poolRoot;
        readonly List<CardView> activeCards = new List<CardView>();
        readonly Stack<CardView> inactiveCards = new Stack<CardView>();

        public List<CardView> ActiveCards => activeCards;

        public CardViewPool(Transform parent)
        {
            var go = new GameObject("CardViewPool_3D");
            go.transform.SetParent(parent, false);
            poolRoot = go.transform;
        }

        public void BeginFrame()
        {
            for (int i = 0; i < activeCards.Count; i++)
            {
                var cv = activeCards[i];
                if (cv != null)
                {
                    cv.gameObject.SetActive(false);
                    inactiveCards.Push(cv);
                }
            }
            activeCards.Clear();
        }

        public CardView Spawn(Transform anchor, Vector3 localOffset, CardDef card, bool faceDown, float scale = 1f, bool highlight = false, float yawDegrees = 0f, float pitchDegrees = 0f)
        {
            if (anchor == null) return null;
            CardView cv = null;
            while (inactiveCards.Count > 0 && cv == null)
                cv = inactiveCards.Pop();

            if (cv == null)
                cv = CardView.Create(poolRoot);

            cv.transform.SetParent(anchor, false);
            cv.transform.localPosition = localOffset;
            cv.transform.localRotation = Quaternion.identity;
            cv.gameObject.SetActive(true);
            cv.Setup(card, faceDown, scale, highlight, yawDegrees, pitchDegrees);
            activeCards.Add(cv);
            return cv;
        }
    }
}
