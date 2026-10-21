using System;
using System.Collections.Generic;
using UnityEngine;

namespace PokeTCGPlay
{
    /// <summary>
    /// F1: Vista 3D de la mesa y Playmat físico sobre InteractablePlayTable.
    /// Genera el tapete 3D con grosor sobre la superficie real de la mesa, define todos los anclajes 3D
    /// en coordenadas normalizadas del playmat (Active, Bench×5, Prizes×6, Deck, Discard, Stadium)
    /// y proyecta las posiciones 3D para las fases visuales.
    /// </summary>
    public class TableView : MonoBehaviour
    {
        public static TableView Instance { get; private set; }

        // Dimensiones físicas del tapete de 2 jugadores sobre la mesa (en metros del mundo 3D)
        // Proporción exacta de playmat.png: 840 x 868 (0.9677 : 1)
        public const float MatWidth = 0.72f;
        public const float MatLength = 0.744f;
        public const float MatThickness = 0.004f;

        // Tamaño estándar de una carta Pokémon en el mundo 3D (proporción 63:88)
        public const float CardWorldWidth = 0.086f;
        public const float CardWorldLength = 0.120f;

        public InteractablePlayTable Table { get; private set; }
        public int PlayerSeat { get; private set; }

        public Transform PlaymatRoot { get; private set; }
        MeshRenderer matTopRenderer;
        MeshRenderer matBaseRenderer;

        // Anclajes 3D por jugador (0 = Jugador, 1 = NPC)
        public readonly Transform[] ActiveAnchor = new Transform[2];
        public readonly Transform[,] BenchAnchor = new Transform[2, 5];
        public readonly Transform[,] PrizeBoxAnchor = new Transform[2, 3];
        public readonly Transform[,] PrizeAnchor = new Transform[2, 6];
        public readonly Transform[] DeckAnchor = new Transform[2];
        public readonly Transform[] DiscardAnchor = new Transform[2];
        public readonly Transform[] HandAnchor = new Transform[2];
        public Transform StadiumAnchor { get; private set; }
        public Transform CoinAnchor { get; private set; }

        // Indicadores 3D sutiles sobre las casillas del tapete (se iluminan cuando son destino válido)
        readonly Dictionary<Transform, MeshRenderer> slotIndicators = new Dictionary<Transform, MeshRenderer>();
        CardViewPool cardPool;
        readonly DeckStack3D[] deckStacks = new DeckStack3D[2];
        Mesh sharedQuadMesh;
        Mesh sharedCubeMesh;
        Material matTopMaterial;
        Material matEdgeMaterial;

        public bool IsBoard3DReady => PlaymatRoot != null && cardPool != null;

        void Awake()
        {
            Instance = this;
        }

        void OnDestroy()
        {
            Cleanup3D();
            if (Instance == this) Instance = null;
        }

        /// <summary>
        /// Monta el tapete 3D y todos sus anclajes sobre la mesa física donde se sentó el jugador.
        /// </summary>
        public void AttachToTable(InteractablePlayTable table, int playerSeat)
        {
            Cleanup3D();
            Table = table;
            PlayerSeat = playerSeat;
            if (table == null) return;

            SpriteCache.EnsureInitialized();
            SpriteCache.PreloadFromEpl();

            // Activar los sets de la mesa para conservar las DeckBoxes decorativas, pero ocultar los 2 tapetes pequeños originales
            float surfaceY = table.transform.position.y + 0.785f;
            bool foundBounds = false;
            float sumY = 0f;
            int countY = 0;

            if (table.m_TableGameItemSetList != null)
            {
                for (int i = 0; i < table.m_TableGameItemSetList.Count; i++)
                {
                    var set = table.m_TableGameItemSetList[i];
                    if (set == null) continue;
                    try
                    {
                        set.RandomizeSetup();
                        if (table.m_TableGameItemSetDataList != null && i < table.m_TableGameItemSetDataList.Count)
                            table.m_TableGameItemSetDataList[i] = set.m_TableGameItemSetData;
                    }
                    catch { }
                    set.gameObject.SetActive(true);
                    if (set.m_PlayMatMesh != null)
                    {
                        set.m_PlayMatMesh.gameObject.SetActive(true);
                        Bounds b = set.m_PlayMatMesh.bounds;
                        if (b.size.sqrMagnitude > 0.0001f)
                        {
                            sumY += b.max.y;
                            countY++;
                            foundBounds = true;
                        }
                        // Ocultamos el tapete individual del juego base para que luzca nuestro Playmat 3D completo
                        set.m_PlayMatMesh.gameObject.SetActive(false);
                    }
                    if (set.m_ComicBookMesh != null)
                        set.m_ComicBookMesh.gameObject.SetActive(false);
                }
            }
            if (foundBounds && countY > 0)
                surfaceY = sumY / countY;

            Vector3 sitPlayer = table.GetSitLoc(playerSeat).position;
            Vector3 sitOpp = table.GetSitLoc(1 - playerSeat).position;
            Vector3 center = (sitPlayer + sitOpp) * 0.5f;
            Vector3 forward = sitOpp - sitPlayer;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.001f) forward = table.transform.forward;
            forward.Normalize();

            Vector3 matCenter = new Vector3(center.x, surfaceY + 0.002f, center.z);
            Quaternion matRot = Quaternion.LookRotation(forward, Vector3.up);

            BuildPlaymat3D(matCenter, matRot);
            BuildAnchors();
        }

        public void HideDefaultTableMats()
        {
            if (Table == null || Table.m_TableGameItemSetList == null) return;
            for (int i = 0; i < Table.m_TableGameItemSetList.Count; i++)
            {
                var set = Table.m_TableGameItemSetList[i];
                if (set == null) continue;
                if (set.m_PlayMatMesh != null) set.m_PlayMatMesh.gameObject.SetActive(false);
                if (set.m_ComicBookMesh != null) set.m_ComicBookMesh.gameObject.SetActive(false);
            }
        }

        public void DetachFromTable()
        {
            if (Table != null && Table.m_TableGameItemSetList != null)
            {
                for (int i = 0; i < Table.m_TableGameItemSetList.Count; i++)
                {
                    var set = Table.m_TableGameItemSetList[i];
                    if (set == null) continue;
                    if (set.m_PlayMatMesh != null) set.m_PlayMatMesh.gameObject.SetActive(true);
                    set.gameObject.SetActive(false);
                }
            }
            Cleanup3D();
            Table = null;
        }

        void Cleanup3D()
        {
            slotIndicators.Clear();
            cardPool = null;
            deckStacks[0] = null;
            deckStacks[1] = null;
            if (PlaymatRoot != null)
            {
                Destroy(PlaymatRoot.gameObject);
                PlaymatRoot = null;
            }
        }

        void BuildPlaymat3D(Vector3 center, Quaternion rotation)
        {
            EnsurePrimitives();

            PlaymatRoot = new GameObject("PokeTCG_Playmat3D").transform;
            PlaymatRoot.SetPositionAndRotation(center, rotation);

            // Base de neopreno negro bajo el tapete (da relieve físico real sobre la madera de la mesa)
            var baseGo = new GameObject("NeopreneBase");
            baseGo.transform.SetParent(PlaymatRoot, false);
            baseGo.transform.localPosition = new Vector3(0f, MatThickness * 0.5f, 0f);
            baseGo.transform.localScale = new Vector3(MatWidth + 0.010f, MatThickness, MatLength + 0.010f);
            var baseMf = baseGo.AddComponent<MeshFilter>();
            baseMf.sharedMesh = sharedCubeMesh;
            matBaseRenderer = baseGo.AddComponent<MeshRenderer>();
            if (matEdgeMaterial == null)
            {
                matEdgeMaterial = CreateMaterial(new Color(0.08f, 0.10f, 0.14f, 1f), null);
            }
            matBaseRenderer.sharedMaterial = matEdgeMaterial;

            // Superficie superior con la textura del Playmat oficial
            var topGo = new GameObject("PlaymatSurface");
            topGo.transform.SetParent(PlaymatRoot, false);
            topGo.transform.localPosition = new Vector3(0f, MatThickness + 0.0005f, 0f);
            topGo.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            topGo.transform.localScale = new Vector3(MatWidth, MatLength, 1f);
            var topMf = topGo.AddComponent<MeshFilter>();
            topMf.sharedMesh = sharedQuadMesh;
            matTopRenderer = topGo.AddComponent<MeshRenderer>();
            matTopMaterial = CreateMaterial(Color.white, SpriteCache.PlaymatTex);
            matTopRenderer.sharedMaterial = matTopMaterial;
        }

        /// <summary>
        /// Crea un anclaje 3D usando coordenadas normalizadas del playmat:
        /// nx: 0 (izquierda) .. 1 (derecha)
        /// ny: 0 (borde superior / lado NPC) .. 1 (borde inferior / lado Jugador)
        /// </summary>
        Transform CreateNormalizedAnchor(string name, float nx, float ny, float yOffset = 0.0015f, bool faceOpponent = false, bool withIndicator = true, float indicatorScale = 1f)
        {
            var go = new GameObject(name);
            go.transform.SetParent(PlaymatRoot, false);
            float localX = (nx - 0.5f) * MatWidth;
            float localZ = (0.5f - ny) * MatLength;
            go.transform.localPosition = new Vector3(localX, MatThickness + yOffset, localZ);
            go.transform.localRotation = faceOpponent ? Quaternion.Euler(0f, 180f, 0f) : Quaternion.identity;

            if (withIndicator)
            {
                var indGo = new GameObject("SlotHighlight");
                indGo.transform.SetParent(go.transform, false);
                indGo.transform.localPosition = new Vector3(0f, 0.0003f, 0f);
                indGo.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                indGo.transform.localScale = new Vector3(CardWorldWidth * indicatorScale * 1.06f, CardWorldLength * indicatorScale * 1.06f, 1f);
                var mf = indGo.AddComponent<MeshFilter>();
                mf.sharedMesh = sharedQuadMesh;
                var mr = indGo.AddComponent<MeshRenderer>();
                mr.sharedMaterial = CreateMaterial(new Color(0.98f, 0.85f, 0.25f, 0.0f), null, true);
                mr.enabled = false;
                slotIndicators[go.transform] = mr;
            }
            return go.transform;
        }

        void BuildAnchors()
        {
            // Campo de Batalla Central (Active)
            ActiveAnchor[0] = CreateNormalizedAnchor("Active_Player", 0.50f, 0.61f, 0.0015f, false, true, 1.08f);
            ActiveAnchor[1] = CreateNormalizedAnchor("Active_NPC", 0.50f, 0.39f, 0.0015f, true, true, 1.08f);

            // Bancas (5 casillas por jugador)
            float[] benchXs = { 0.27f, 0.385f, 0.50f, 0.615f, 0.73f };
            for (int i = 0; i < 5; i++)
            {
                BenchAnchor[0, i] = CreateNormalizedAnchor($"Bench_Player_{i}", benchXs[i], 0.80f, 0.0015f, false, true, 0.94f);
                BenchAnchor[1, i] = CreateNormalizedAnchor($"Bench_NPC_{i}", benchXs[4 - i], 0.135f, 0.0015f, true, true, 0.94f);
            }

            // Mazos (Deck) y Descartes (Trash)
            DeckAnchor[0] = CreateNormalizedAnchor("Deck_Player", 0.895f, 0.635f, 0.0015f, false, true, 1f);
            DiscardAnchor[0] = CreateNormalizedAnchor("Trash_Player", 0.895f, 0.835f, 0.0015f, false, true, 1f);
            DeckAnchor[1] = CreateNormalizedAnchor("Deck_NPC", 0.105f, 0.335f, 0.0015f, true, true, 1f);
            DiscardAnchor[1] = CreateNormalizedAnchor("Trash_NPC", 0.105f, 0.130f, 0.0015f, true, true, 1f);

            // Casillas de Premios (Side: 3 casillas por jugador, 6 premios totales)
            float[] p0Ys = { 0.525f, 0.685f, 0.845f };
            float[] p1Ys = { 0.115f, 0.275f, 0.435f };
            for (int b = 0; b < 3; b++)
            {
                PrizeBoxAnchor[0, b] = CreateNormalizedAnchor($"PrizeBox_Player_{b}", 0.105f, p0Ys[b], 0.0015f, false, false, 0.86f);
                PrizeBoxAnchor[1, b] = CreateNormalizedAnchor($"PrizeBox_NPC_{b}", 0.895f, p1Ys[b], 0.0015f, true, false, 0.86f);
                for (int k = 0; k < 2; k++)
                {
                    int pIdx = b * 2 + k;
                    PrizeAnchor[0, pIdx] = CreateNormalizedAnchor($"Prize_Player_{pIdx}", 0.105f - k * 0.008f, p0Ys[b] - k * 0.006f, 0.0015f + k * 0.0006f, false, false, 0.86f);
                    PrizeAnchor[1, pIdx] = CreateNormalizedAnchor($"Prize_NPC_{pIdx}", 0.895f + k * 0.008f, p1Ys[b] + k * 0.006f, 0.0015f + k * 0.0006f, true, false, 0.86f);
                }
            }

            // Estadio (para futuras expansiones) y Moneda
            StadiumAnchor = CreateNormalizedAnchor("Stadium", 0.31f, 0.50f, 0.0015f, false, false, 0.95f);
            CoinAnchor = CreateNormalizedAnchor("CoinCenter", 0.50f, 0.50f, 0.015f, false, false, 1f);

            // Anclajes de Mano (al borde inferior/superior del tapete hacia cada jugador)
            HandAnchor[0] = CreateNormalizedAnchor("Hand_Player", 0.50f, 1.06f, 0.06f, false, false, 1f);
            HandAnchor[1] = CreateNormalizedAnchor("Hand_NPC", 0.50f, -0.04f, 0.05f, true, false, 1f);

            // F2: Inicializar el Pool de Cartas 3D y las Pilas 3D de Mazo
            cardPool = new CardViewPool(PlaymatRoot);
            deckStacks[0] = new DeckStack3D(DeckAnchor[0]);
            deckStacks[1] = new DeckStack3D(DeckAnchor[1]);
            deckStacks[0].Sync(0);
            deckStacks[1].Sync(0);
        }

        /// <summary>
        /// F2: Sincroniza todas las cartas en 3D sobre el tapete físico (Mazos con altura dinámica,
        /// Descartes apilados con desorden leve, Premios boca abajo, Activos, Banca y Energías adheridas debajo).
        /// </summary>
        public void SyncBoard3D(Game game, PokemonInPlay zoomedPip, float zoomAnim, bool retreatMode)
        {
            if (PlaymatRoot == null || cardPool == null) return;
            cardPool.BeginFrame();

            if (game == null)
            {
                deckStacks[0]?.Sync(0);
                deckStacks[1]?.Sync(0);
                return;
            }

            for (int p = 0; p < 2; p++)
            {
                var ps = game.Players[p];
                if (ps == null) continue;

                // 1) Mazo como pila 3D cuya altura depende de las cartas restantes
                deckStacks[p]?.Sync(ps.Deck.Count);

                // 2) Descarte apilado en 3D con desorden leve (hasta las últimas 6 cartas visibles)
                int dCount = ps.Discard.Count;
                int startD = Mathf.Max(0, dCount - 6);
                for (int k = startD; k < dCount; k++)
                {
                    var dCard = ps.Discard[k];
                    int layer = k - startD;
                    int seed = k * 7919 + p * 131;
                    float offX = k == 0 ? 0f : (((seed % 11) - 5) / 5f) * 0.0024f;
                    float offZ = k == 0 ? 0f : ((((seed / 11) % 11) - 5) / 5f) * 0.0024f;
                    float yaw = k == 0 ? 0f : (((seed / 121) % 13) - 6) * 1.15f;
                    Vector3 dOff = new Vector3(offX, layer * (CardView.CardThickness + 0.00025f), offZ);
                    cardPool.Spawn(DiscardAnchor[p], dOff, dCard, false, 1f, false, yaw);
                }

                // 3) Premios en 3D boca abajo sobre las casillas Side (hasta 6 cartas)
                int pCount = Mathf.Min(6, ps.Prizes.Count);
                for (int i = 0; i < pCount; i++)
                {
                    var pCard = ps.Prizes[i];
                    cardPool.Spawn(PrizeAnchor[p, i], Vector3.zero, pCard, true, 0.86f, false, 0f);
                }

                // 4) Banca en 3D (5 casillas)
                for (int i = 0; i < 5; i++)
                {
                    var bPip = i < ps.Bench.Count ? ps.Bench[i] : null;
                    bool hl = retreatMode && p == 0 && bPip != null;
                    SyncPokemonSlot3D(BenchAnchor[p, i], bPip, 0.94f, false, hl, zoomedPip, zoomAnim);
                }

                // 5) Pokémon Activo en el Campo de Batalla Central 3D
                SyncPokemonSlot3D(ActiveAnchor[p], ps.Active, 1.08f, true, false, zoomedPip, zoomAnim);
            }
        }

        void SyncPokemonSlot3D(Transform anchor, PokemonInPlay pip, float scale, bool isActive, bool highlight, PokemonInPlay zoomedPip, float zoomAnim)
        {
            if (anchor == null || pip == null) return;
            if (pip == zoomedPip && zoomAnim > 0.05f) return;

            var curFlight = BoardAnimator.Instance?.Current;
            bool isFlyingPokemon = curFlight != null && curFlight.Card == pip.Card &&
                (curFlight.Kind == AnimKind.ToActive || curFlight.Kind == AnimKind.ToBench ||
                 curFlight.Kind == AnimKind.AttackLunge || curFlight.Kind == AnimKind.RetreatSwap);
            if (isFlyingPokemon) return;

            bool isFlyingEvolve = curFlight != null && curFlight.Card == pip.Card && curFlight.Kind == AnimKind.Evolve;
            bool isFlyingEnergy = curFlight != null && curFlight.Kind == AnimKind.AttachEnergy &&
                                  pip.Energy.Count > 0 && pip.Energy[pip.Energy.Count - 1] == curFlight.Card;

            // 1) Energías adheridas físicamente DEBAJO del Pokémon (asomando por el borde inferior/izquierdo)
            int eCount = pip.Energy.Count;
            for (int i = eCount - 1; i >= 0; i--)
            {
                if (isFlyingEnergy && i == eCount - 1) continue;
                var eCard = pip.Energy[i];
                int orderFromTop = eCount - 1 - i;
                Vector3 eOff = new Vector3(
                    (-0.012f - i * 0.0085f) * scale,
                    orderFromTop * (CardView.CardThickness + 0.00015f),
                    (-0.011f - i * 0.0075f) * scale);
                cardPool.Spawn(anchor, eOff, eCard, false, scale, false, 0f);
            }

            float baseY = eCount * (CardView.CardThickness + 0.00015f) + 0.0004f;

            // 2) Etapas previas de evolución apiladas debajo del Pokémon evolucionado
            int stackCount = pip.Stack != null ? pip.Stack.Count : 1;
            for (int s = 0; s < stackCount - 1; s++)
            {
                var preCard = pip.Stack[s];
                int depth = stackCount - 1 - s;
                Vector3 preOff = new Vector3(0.0022f * depth, baseY + s * (CardView.CardThickness + 0.0002f), 0.0022f * depth);
                cardPool.Spawn(anchor, preOff, preCard, false, scale, false, 0f);
            }

            if (isFlyingEvolve) return;

            // 3) Carta principal del Pokémon encima (con giro físico según estado alterado si está Activo)
            float statusYaw = 0f;
            if (isActive)
            {
                if (pip.Status == Status.Asleep) statusYaw = -90f;
                else if (pip.Status == Status.Paralyzed) statusYaw = 90f;
                else if (pip.Status == Status.Confused) statusYaw = 180f;
            }
            Vector3 topOff = new Vector3(0f, baseY + Math.Max(0, stackCount - 1) * (CardView.CardThickness + 0.0002f) + 0.0003f, 0f);
            cardPool.Spawn(anchor, topOff, pip.Card, false, scale, highlight, statusYaw);
        }

        void Update()
        {
            if (PlaymatRoot == null) return;
            if (matTopRenderer != null && SpriteCache.PlaymatTex != null && matTopRenderer.sharedMaterial.mainTexture != SpriteCache.PlaymatTex)
                matTopRenderer.sharedMaterial.mainTexture = SpriteCache.PlaymatTex;
        }

        /// <summary>
        /// Ilumina en el tapete 3D los destinos válidos según la acción que el jugador está realizando.
        /// </summary>
        public void SetSlotHighlights(bool highlightPlayerActive, bool highlightPlayerBench)
        {
            foreach (var kv in slotIndicators)
            {
                if (kv.Value == null) continue;
                bool on = false;
                if (highlightPlayerActive && kv.Key == ActiveAnchor[0]) on = true;
                if (highlightPlayerBench)
                {
                    for (int i = 0; i < 5; i++)
                        if (kv.Key == BenchAnchor[0, i]) { on = true; break; }
                }
                kv.Value.enabled = on;
                if (on)
                {
                    float pulse = 0.28f + 0.18f * Mathf.Sin(Time.time * 5.5f);
                    kv.Value.sharedMaterial.color = new Color(1f, 0.86f, 0.22f, pulse);
                }
            }
        }

        /// <summary>
        /// Proyecta las esquinas del tapete 3D a coordenadas GUI de pantalla (OnGUI).
        /// </summary>
        public bool TryGetScreenPlaymatRect(out Rect screenRect)
        {
            screenRect = default;
            var cam = Camera.main;
            if (PlaymatRoot == null || cam == null) return false;

            Vector3 halfW = PlaymatRoot.right * (MatWidth * 0.5f);
            Vector3 halfL = PlaymatRoot.forward * (MatLength * 0.5f);
            Vector3 center = PlaymatRoot.position + Vector3.up * MatThickness;

            Vector3[] corners =
            {
                center - halfW + halfL, // top-left
                center + halfW + halfL, // top-right
                center - halfW - halfL, // bottom-left
                center + halfW - halfL  // bottom-right
            };

            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < 4; i++)
            {
                Vector3 sp = cam.WorldToScreenPoint(corners[i]);
                if (sp.z <= 0.05f) return false;
                float gx = sp.x;
                float gy = Screen.height - sp.y;
                if (gx < minX) minX = gx;
                if (gx > maxX) maxX = gx;
                if (gy < minY) minY = gy;
                if (gy > maxY) maxY = gy;
            }

            if (maxX - minX < 200f || maxY - minY < 200f) return false;
            screenRect = Rect.MinMaxRect(minX, minY, maxX, maxY);
            return true;
        }

        /// <summary>
        /// Proyecta un anclaje 3D de carta sobre la mesa a su Rect correspondiente en pantalla (OnGUI).
        /// </summary>
        public bool TryProjectSlotToGUI(Transform anchor, float scale, float baseSlotW, float baseSlotH, out Rect rect)
        {
            rect = default;
            var cam = Camera.main;
            if (anchor == null || cam == null) return false;

            Vector3 sp = cam.WorldToScreenPoint(anchor.position);
            if (sp.z <= 0.05f) return false;

            float w = baseSlotW * scale;
            float h = baseSlotH * scale;
            float gx = sp.x;
            float gy = Screen.height - sp.y;
            rect = new Rect(gx - w * 0.5f, gy - h * 0.5f, w, h);
            return true;
        }

        void EnsurePrimitives()
        {
            if (sharedQuadMesh == null)
            {
                var tmp = GameObject.CreatePrimitive(PrimitiveType.Quad);
                sharedQuadMesh = tmp.GetComponent<MeshFilter>().sharedMesh;
                DestroyImmediate(tmp);
            }
            if (sharedCubeMesh == null)
            {
                var tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
                sharedCubeMesh = tmp.GetComponent<MeshFilter>().sharedMesh;
                DestroyImmediate(tmp);
            }
        }

        static Material CreateMaterial(Color color, Texture2D tex, bool transparent = false)
        {
            Shader sh = Shader.Find(transparent ? "Sprites/Default" : "Unlit/Texture");
            if (sh == null || (!transparent && tex == null))
                sh = Shader.Find("Sprites/Default") ?? Shader.Find("Standard");
            var m = new Material(sh);
            m.color = color;
            if (tex != null) m.mainTexture = tex;
            return m;
        }
    }
}
