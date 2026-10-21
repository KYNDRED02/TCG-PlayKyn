using System;
using System.Collections.Generic;
using UnityEngine;

namespace PokeTCGPlay
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

        Transform visualRoot;
        MeshRenderer coreRenderer;
        MeshRenderer frontRenderer;
        MeshRenderer backRenderer;
        MeshRenderer shadowRenderer;
        MeshRenderer highlightRenderer;

        Material frontMat;
        Material backMat;
        Material highlightMat;

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
                sCoreMat = CreateUnlitMaterial(new Color(0.88f, 0.89f, 0.92f, 1f), null, false);
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
        public void Setup(CardDef card, bool faceDown, float scale = 1f, bool highlight = false, float yawDegrees = 0f)
        {
            Card = card;
            FaceDown = faceDown;

            transform.localScale = new Vector3(scale, 1f, scale);
            visualRoot.localRotation = Quaternion.Euler(0f, yawDegrees, faceDown ? 180f : 0f);
            if (shadowRenderer != null)
                shadowRenderer.transform.localRotation = Quaternion.Euler(90f, yawDegrees, 0f);
            if (highlightRenderer != null)
            {
                highlightRenderer.enabled = highlight;
                highlightRenderer.transform.localRotation = Quaternion.Euler(90f, yawDegrees, 0f);
            }

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
            if (Card != null && !FaceDown && frontMat != null)
            {
                var sp = SpriteCache.Get(Card.Sprite);
                if (sp != null && sp.texture != null)
                {
                    frontMat.color = Color.white;
                    ApplySpriteToMaterial(frontMat, sp);
                }
                else
                {
                    frontMat.mainTexture = sFallbackFrontTex;
                    frontMat.color = new Color(0.22f, 0.32f, 0.48f, 1f);
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
            Shader sh = Shader.Find(transparent ? "Sprites/Default" : "Unlit/Texture");
            if (sh == null) sh = Shader.Find("Standard");
            var m = new Material(sh);
            m.color = color;
            if (tex != null) m.mainTexture = tex;
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
    /// F2: Pila 3D física para el Mazo (Deck), cuya altura en metros escala dinámicamente según las cartas restantes (0..60).
    /// </summary>
    public class DeckStack3D
    {
        public GameObject Root { get; private set; }
        Transform blockTransform;
        Transform topCardTransform;
        MeshRenderer topCardRenderer;
        MeshRenderer shadowRenderer;
        Material topMat;

        static Texture2D sDeckSideTex;
        static Material sDeckSideMat;

        public DeckStack3D(Transform anchor)
        {
            CardView.EnsureSharedResources();
            if (sDeckSideTex == null)
            {
                sDeckSideTex = MakeDeckEdgesTex(32, 128);
                sDeckSideMat = CardView.CreateUnlitMaterial(Color.white, sDeckSideTex, false);
            }

            float w = TableView.CardWorldWidth;
            float l = TableView.CardWorldLength;

            Root = new GameObject("DeckStack3D");
            Root.transform.SetParent(anchor, false);
            Root.transform.localPosition = Vector3.zero;
            Root.transform.localRotation = Quaternion.identity;

            // Sombra en la base del mazo
            var shGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            UnityEngine.Object.DestroyImmediate(shGo.GetComponent<Collider>());
            shGo.name = "DeckShadow";
            shGo.transform.SetParent(Root.transform, false);
            shGo.transform.localPosition = new Vector3(0.003f, 0.0002f, -0.003f);
            shGo.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            shGo.transform.localScale = new Vector3(w * 1.12f, l * 1.10f, 1f);
            shadowRenderer = shGo.GetComponent<MeshRenderer>();
            shadowRenderer.sharedMaterial = CardView.CreateUnlitMaterial(new Color(0f, 0f, 0f, 0.55f), null, true);

            // Bloque 3D con los cantos apilados de las cartas
            var blockGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
            UnityEngine.Object.DestroyImmediate(blockGo.GetComponent<Collider>());
            blockGo.name = "DeckBlock";
            blockGo.transform.SetParent(Root.transform, false);
            blockGo.GetComponent<MeshRenderer>().sharedMaterial = sDeckSideMat;
            blockTransform = blockGo.transform;

            // Carta superior boca abajo en la cima del mazo
            var topGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            UnityEngine.Object.DestroyImmediate(topGo.GetComponent<Collider>());
            topGo.name = "DeckTopCardBack";
            topGo.transform.SetParent(Root.transform, false);
            topGo.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            topGo.transform.localScale = new Vector3(w, l, 1f);
            topCardRenderer = topGo.GetComponent<MeshRenderer>();
            topMat = CardView.CreateUnlitMaterial(Color.white, SpriteCache.GetCardBack()?.texture, false);
            topCardRenderer.sharedMaterial = topMat;
            topCardTransform = topGo.transform;
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

            // Altura física proporcional al número de cartas (60 cartas ≈ 3.2 cm de alto)
            float stackHeight = Mathf.Max(0.0018f, cardCount * 0.00054f);
            blockTransform.localScale = new Vector3(TableView.CardWorldWidth, stackHeight, TableView.CardWorldLength);
            blockTransform.localPosition = new Vector3(0f, stackHeight * 0.5f, 0f);
            topCardTransform.localPosition = new Vector3(0f, stackHeight + 0.0002f, 0f);

            var cb = SpriteCache.GetCardBack();
            if (cb != null && cb.texture != null && topMat != null)
            {
                Texture2D t = cb.texture;
                Rect tr = cb.textureRect;
                topMat.mainTexture = t;
                topMat.mainTextureOffset = new Vector2(tr.x / t.width, tr.y / t.height);
                topMat.mainTextureScale = new Vector2(tr.width / t.width, tr.height / t.height);
            }
        }

        static Texture2D MakeDeckEdgesTex(int w, int h)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false);
            for (int y = 0; y < h; y++)
            {
                bool stripe = (y % 3) == 0;
                Color col = stripe ? new Color(0.72f, 0.74f, 0.78f, 1f) : new Color(0.92f, 0.91f, 0.88f, 1f);
                for (int x = 0; x < w; x++) t.SetPixel(x, y, col);
            }
            t.Apply();
            return t;
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

        public CardView Spawn(Transform anchor, Vector3 localOffset, CardDef card, bool faceDown, float scale = 1f, bool highlight = false, float yawDegrees = 0f)
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
            cv.Setup(card, faceDown, scale, highlight, yawDegrees);
            activeCards.Add(cv);
            return cv;
        }
    }
}
