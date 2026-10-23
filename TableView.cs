using System;
using System.Collections.Generic;
using UnityEngine;

namespace PokeTCGPlay
{
    public enum TableSlotKind
    {
        Active,
        Bench,
        Discard,
        Deck,
        PlayfieldCenter
    }

    /// <summary>
    /// F4: Identificador y collider de una casilla 3D del tapete para Raycast e interacción Drag & Drop.
    /// </summary>
    public class TableSlot3D : MonoBehaviour
    {
        public TableSlotKind Kind;
        public int PlayerIndex; // 0 = Jugador, 1 = NPC
        public int BenchIndex = -1;
        public float Scale = 1f;
        public Transform Anchor;
    }

    /// <summary>
    /// F1–F4: Vista 3D de la mesa, Playmat físico, Mano 3D en abanico y detección de Raycast 3D sobre InteractablePlayTable.
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

        // F4: Mano 3D en abanico, Carta Arrastrada en 3D y Raycast geométrico exacto
        readonly List<TableSlot3D> tableSlots = new List<TableSlot3D>();
        readonly float[] handLiftAnims = new float[64];
        readonly float[] handSpreadAnims = new float[64];
        float handDeployAnim;
        CardView dragCardView;
        Vector3 smoothDragWorldPos;
        Vector3 prevDragWorldPos;
        bool dragPosInitialized;

        public bool IsBoard3DReady => PlaymatRoot != null && cardPool != null;

        public static Camera GetActiveCamera()
        {
            try
            {
                var ipc = CSingleton<InteractionPlayerController>.Instance;
                if (ipc != null && ipc.m_Cam != null)
                    return ipc.m_Cam;
            }
            catch { }
            return Camera.main;
        }

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
            tableSlots.Clear();
            cardPool = null;
            deckStacks[0] = null;
            deckStacks[1] = null;
            dragCardView = null;
            dragPosInitialized = false;
            handDeployAnim = 0f;
            Array.Clear(handLiftAnims, 0, handLiftAnims.Length);
            Array.Clear(handSpreadAnims, 0, handSpreadAnims.Length);
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

        void AttachSlotCollider(Transform anchor, TableSlotKind kind, int playerIdx, int benchIdx = -1, float scale = 1f)
        {
            if (anchor == null) return;
            var slot = anchor.gameObject.AddComponent<TableSlot3D>();
            slot.Kind = kind;
            slot.PlayerIndex = playerIdx;
            slot.BenchIndex = benchIdx;
            slot.Anchor = anchor;
            slot.Scale = scale;
            tableSlots.Add(slot);

            var bc = anchor.gameObject.AddComponent<BoxCollider>();
            bc.isTrigger = true;
            bc.size = new Vector3(CardWorldWidth * scale * 1.12f, 0.025f, CardWorldLength * scale * 1.12f);
            bc.center = new Vector3(0f, 0.008f, 0f);
        }

        void BuildAnchors()
        {
            // Campo de Batalla Central (Active)
            ActiveAnchor[0] = CreateNormalizedAnchor("Active_Player", 0.50f, 0.61f, 0.0015f, false, true, 1.08f);
            ActiveAnchor[1] = CreateNormalizedAnchor("Active_NPC", 0.50f, 0.39f, 0.0015f, true, true, 1.08f);
            AttachSlotCollider(ActiveAnchor[0], TableSlotKind.Active, 0, -1, 1.08f);
            AttachSlotCollider(ActiveAnchor[1], TableSlotKind.Active, 1, -1, 1.08f);

            // Bancas (5 casillas por jugador)
            float[] benchXs = { 0.27f, 0.385f, 0.50f, 0.615f, 0.73f };
            for (int i = 0; i < 5; i++)
            {
                BenchAnchor[0, i] = CreateNormalizedAnchor($"Bench_Player_{i}", benchXs[i], 0.80f, 0.0015f, false, true, 0.94f);
                BenchAnchor[1, i] = CreateNormalizedAnchor($"Bench_NPC_{i}", benchXs[4 - i], 0.135f, 0.0015f, true, true, 0.94f);
                AttachSlotCollider(BenchAnchor[0, i], TableSlotKind.Bench, 0, i, 0.94f);
                AttachSlotCollider(BenchAnchor[1, i], TableSlotKind.Bench, 1, i, 0.94f);
            }

            // Mazos (Deck) y Descartes (Trash)
            DeckAnchor[0] = CreateNormalizedAnchor("Deck_Player", 0.895f, 0.635f, 0.0015f, false, true, 1f);
            DiscardAnchor[0] = CreateNormalizedAnchor("Trash_Player", 0.895f, 0.835f, 0.0015f, false, true, 1f);
            DeckAnchor[1] = CreateNormalizedAnchor("Deck_NPC", 0.105f, 0.335f, 0.0015f, true, true, 1f);
            DiscardAnchor[1] = CreateNormalizedAnchor("Trash_NPC", 0.105f, 0.130f, 0.0015f, true, true, 1f);
            AttachSlotCollider(DeckAnchor[0], TableSlotKind.Deck, 0, -1, 1f);
            AttachSlotCollider(DiscardAnchor[0], TableSlotKind.Discard, 0, -1, 1f);
            AttachSlotCollider(DeckAnchor[1], TableSlotKind.Deck, 1, -1, 1f);
            AttachSlotCollider(DiscardAnchor[1], TableSlotKind.Discard, 1, -1, 1f);

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

            // Estadio (para futuras expansiones) y Moneda / Centro del campo (para Entrenadores globales)
            StadiumAnchor = CreateNormalizedAnchor("Stadium", 0.31f, 0.50f, 0.0015f, false, false, 0.95f);
            CoinAnchor = CreateNormalizedAnchor("CoinCenter", 0.50f, 0.50f, 0.0018f, false, true, 1.35f);
            AttachSlotCollider(CoinAnchor, TableSlotKind.PlayfieldCenter, 0, -1, 1.45f);

            // F4: Anclajes 3D de Mano para la cámara desde los ojos del jugador sentado
            // (Jugador: sostenida frente a la vista en el borde inferior; NPC: sostenida frente al asiento opuesto)
            HandAnchor[0] = CreateNormalizedAnchor("Hand_Player", 0.50f, 0.965f, 0.140f, false, false, 1f);
            HandAnchor[0].localRotation = Quaternion.Euler(-36f, 0f, 0f);
            HandAnchor[1] = CreateNormalizedAnchor("Hand_NPC", 0.50f, 0.020f, 0.055f, true, false, 1f);
            HandAnchor[1].localRotation = Quaternion.Euler(28f, 180f, 0f);

            // F2: Inicializar el Pool de Cartas 3D y las Pilas 3D de Mazo
            cardPool = new CardViewPool(PlaymatRoot);
            deckStacks[0] = new DeckStack3D(DeckAnchor[0]);
            deckStacks[1] = new DeckStack3D(DeckAnchor[1]);
            deckStacks[0].Sync(0);
            deckStacks[1].Sync(0);

            // F4: Carta 3D dedicada para el arrastre (Drag & Drop) sobre el tapete
            dragCardView = CardView.Create(PlaymatRoot);
            dragCardView.gameObject.name = "PokeTCG_DragCard3D";
            dragCardView.SetRaycastEnabled(false);
            dragCardView.gameObject.SetActive(false);
        }

        /// <summary>
        /// F2 & F4: Sincroniza todas las cartas en 3D sobre el tapete físico (Mazos con altura dinámica,
        /// Descartes apilados con desorden leve, Premios boca abajo, Activos, Banca, Energías adheridas debajo,
        /// Mano 3D en abanico para Jugador y NPC, y Carta arrastrada en 3D).
        /// </summary>
        public void SyncBoard3D(
            Game game,
            CardDef zoomedCard,
            PokemonInPlay zoomedPip,
            float zoomAnim,
            bool retreatMode,
            int hoveredHandIdx = -1,
            int selectedHandIdx = -1,
            int draggingHandIdx = -1,
            Vector3 targetDragWorldPos = default,
            bool isOverValidDropSlot = false)
        {
            if (PlaymatRoot == null || cardPool == null) return;
            cardPool.BeginFrame();

            if (game == null)
            {
                deckStacks[0]?.Sync(0);
                deckStacks[1]?.Sync(0);
                if (dragCardView != null && dragCardView.gameObject.activeSelf)
                    dragCardView.gameObject.SetActive(false);
                dragPosInitialized = false;
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
                    var cv = cardPool.Spawn(DiscardAnchor[p], dOff, dCard, false, 1f, false, yaw);
                    if (cv != null)
                    {
                        cv.OwnerIndex = p;
                        cv.IsDiscardTop = (k == dCount - 1);
                        cv.SetRaycastEnabled(k == dCount - 1);
                    }
                }

                // 3) Premios en 3D boca abajo sobre las casillas Side (hasta 6 cartas)
                int pCount = Mathf.Min(6, ps.Prizes.Count);
                for (int i = 0; i < pCount; i++)
                {
                    var pCard = ps.Prizes[i];
                    var cv = cardPool.Spawn(PrizeAnchor[p, i], Vector3.zero, pCard, true, 0.86f, false, 0f);
                    if (cv != null) cv.SetRaycastEnabled(false);
                }

                // 4) Banca en 3D (5 casillas)
                for (int i = 0; i < 5; i++)
                {
                    var bPip = i < ps.Bench.Count ? ps.Bench[i] : null;
                    bool hl = retreatMode && p == 0 && bPip != null;
                    SyncPokemonSlot3D(p, BenchAnchor[p, i], bPip, 0.94f, false, hl, zoomedPip, zoomAnim);
                }

                // 5) Pokémon Activo en el Campo de Batalla Central 3D
                SyncPokemonSlot3D(p, ActiveAnchor[p], ps.Active, 1.08f, true, false, zoomedPip, zoomAnim);
            }

            // 6) F4: Mano 3D en abanico para el Jugador (frente a cámara) y el NPC (boca abajo en el borde superior)
            SyncHands3D(game, zoomedCard, zoomedPip, zoomAnim, hoveredHandIdx, selectedHandIdx, draggingHandIdx);

            // 7) F4: Carta 3D arrastrada por el jugador sobre el tapete con inclinación física
            SyncDragCard3D(game, draggingHandIdx, targetDragWorldPos, isOverValidDropSlot);
        }

        void SyncPokemonSlot3D(int ownerIdx, Transform anchor, PokemonInPlay pip, float scale, bool isActive, bool highlight, PokemonInPlay zoomedPip, float zoomAnim)
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
                var eCv = cardPool.Spawn(anchor, eOff, eCard, false, scale, false, 0f);
                if (eCv != null) eCv.SetRaycastEnabled(false);
            }

            float baseY = eCount * (CardView.CardThickness + 0.00015f) + 0.0004f;

            // 2) Etapas previas de evolución apiladas debajo del Pokémon evolucionado
            int stackCount = pip.Stack != null ? pip.Stack.Count : 1;
            for (int s = 0; s < stackCount - 1; s++)
            {
                var preCard = pip.Stack[s];
                int depth = stackCount - 1 - s;
                Vector3 preOff = new Vector3(0.0022f * depth, baseY + s * (CardView.CardThickness + 0.0002f), 0.0022f * depth);
                var preCv = cardPool.Spawn(anchor, preOff, preCard, false, scale, false, 0f);
                if (preCv != null) preCv.SetRaycastEnabled(false);
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
            var topCv = cardPool.Spawn(anchor, topOff, pip.Card, false, scale, highlight, statusYaw);
            if (topCv != null)
            {
                topCv.OwnerIndex = ownerIdx;
                topCv.BoundPip = pip;
                topCv.SetRaycastEnabled(true);
            }
        }

        /// <summary>
        /// F4: Renderiza la mano del Jugador y del NPC en 3D en disposición de abanico,
        /// con animación de despliegue, respiración sutil, elevación suave al hacer hover o seleccionar,
        /// y separación animada de cartas vecinas.
        /// </summary>
        void SyncHands3D(
            Game game,
            CardDef zoomedCard,
            PokemonInPlay zoomedPip,
            float zoomAnim,
            int hoveredHandIdx,
            int selectedHandIdx,
            int draggingHandIdx)
        {
            var curFlight = BoardAnimator.Instance?.Current;
            float targetDeploy = game.Phase == Phase.CoinToss ? 0.35f : 1f;
            handDeployAnim = Mathf.MoveTowards(handDeployAnim, targetDeploy, Time.deltaTime * 2.8f);
            float deploySmooth = Mathf.SmoothStep(0f, 1f, handDeployAnim);

            int focusIdx = (hoveredHandIdx >= 0 && draggingHandIdx < 0) ? hoveredHandIdx : selectedHandIdx;

            for (int p = 0; p < 2; p++)
            {
                var ps = game.Players[p];
                if (ps == null || HandAnchor[p] == null) continue;

                int n = ps.Hand.Count;
                for (int i = 0; i < n; i++)
                {
                    var card = ps.Hand[i];

                    // Si la carta está volando hacia la mano (Draw o TakePrize), aún no se dibuja en el abanico estático
                    if (curFlight != null && curFlight.PlayerIndex == p && curFlight.Card == card &&
                        (curFlight.Kind == AnimKind.Draw || curFlight.Kind == AnimKind.TakePrize) && i == n - 1)
                    {
                        continue;
                    }

                    if (p == 0)
                    {
                        // Si está siendo arrastrada en 3D o inspeccionada con clic derecho, no se dibuja en el abanico
                        if (i == draggingHandIdx) continue;
                        if (zoomedPip == null && zoomedCard == card && zoomAnim > 0.05f) continue;

                        bool isHovered = (i == hoveredHandIdx && draggingHandIdx < 0);
                        bool isSelected = (i == selectedHandIdx);
                        float targetLift = isHovered ? 1f : (isSelected ? 0.85f : 0f);
                        if (i < handLiftAnims.Length)
                        {
                            handLiftAnims[i] = Mathf.MoveTowards(handLiftAnims[i], targetLift, Time.deltaTime * 11.5f);
                        }
                        float liftT = i < handLiftAnims.Length ? Mathf.SmoothStep(0f, 1f, handLiftAnims[i]) : targetLift;

                        float targetSpread = 0f;
                        if (focusIdx >= 0 && focusIdx < n && i != focusIdx)
                        {
                            float dist = Mathf.Abs(i - focusIdx);
                            float push = Mathf.Clamp01(1.25f / dist) * 0.022f;
                            targetSpread = i < focusIdx ? -push : push;
                        }
                        if (i < handSpreadAnims.Length)
                        {
                            handSpreadAnims[i] = Mathf.MoveTowards(handSpreadAnims[i], targetSpread, Time.deltaTime * 0.18f);
                        }
                        float spreadX = i < handSpreadAnims.Length ? handSpreadAnims[i] : targetSpread;

                        ComputeHandCardTransform(0, i, n, focusIdx, liftT, out Vector3 localPos, out float yawDeg, out float scale, spreadX, deploySmooth);
                        bool highlight = isHovered || isSelected;
                        var cv = cardPool.Spawn(HandAnchor[0], localPos, card, false, scale, highlight, yawDeg);
                        if (cv != null)
                        {
                            cv.OwnerIndex = 0;
                            cv.HandIndex = i;
                            cv.SetRaycastEnabled(true);
                            if (isSelected && !isHovered)
                                cv.SetHighlight(true, new Color(0.25f, 0.96f, 0.55f, 0.96f));
                            else if (isHovered)
                                cv.SetHighlight(true, new Color(1f, 0.86f, 0.20f, 0.96f));
                        }
                    }
                    else
                    {
                        // Mano 3D del NPC (boca abajo en el borde superior del tapete)
                        ComputeHandCardTransform(1, i, n, -1, 0f, out Vector3 localPos, out float yawDeg, out float scale, 0f, deploySmooth);
                        var cv = cardPool.Spawn(HandAnchor[1], localPos, card, true, scale, false, yawDeg);
                        if (cv != null)
                        {
                            cv.OwnerIndex = 1;
                            cv.SetRaycastEnabled(false);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Calcula la posición local, ángulo de abanico y escala de la carta `index` dentro de una mano 3D de `count` cartas.
        /// </summary>
        public static void ComputeHandCardTransform(
            int playerIdx,
            int index,
            int count,
            int hoveredIdx,
            float liftT,
            out Vector3 localPos,
            out float yawDeg,
            out float scale,
            float customSpreadX = float.NaN,
            float deployFactor = 1f)
        {
            float t = count <= 1 ? 0f : (index / (float)(count - 1)) * 2f - 1f; // -1 (izq) .. +1 (der)

            if (playerIdx == 0)
            {
                float baseScale = 0.86f;
                float maxSpanX = 0.44f * deployFactor;
                float idealStep = 0.056f * deployFactor;
                float totalW = count <= 1 ? 0f : Mathf.Min(maxSpanX, (count - 1) * idealStep);
                float x = count <= 1 ? 0f : (-totalW * 0.5f + index * (totalW / (count - 1)));

                if (!float.IsNaN(customSpreadX))
                {
                    x += customSpreadX;
                }
                else if (hoveredIdx >= 0 && hoveredIdx < count && index != hoveredIdx)
                {
                    float dist = Mathf.Abs(index - hoveredIdx);
                    float push = Mathf.Clamp01(1.25f / dist) * 0.022f;
                    x += index < hoveredIdx ? -push : push;
                }

                float archZ = -(t * t) * Mathf.Min(0.022f, (count - 1) * 0.0028f) * deployFactor;
                float maxYaw = Mathf.Min(16f, (count - 1) * 2.4f) * deployFactor;
                yawDeg = t * maxYaw * (1f - liftT * 0.92f);

                // Respiración sutil en mano + escalera fina en Y para evitar z-fighting + elevación al hacer hover/selección
                float idleWave = Mathf.Sin(Time.time * 2.2f + index * 0.55f) * 0.0014f * (1f - liftT);
                float y = index * 0.0015f + liftT * 0.024f + idleWave;
                float z = archZ + liftT * 0.042f;

                localPos = new Vector3(x, y, z);
                scale = Mathf.Lerp(baseScale, 1.12f, liftT);
            }
            else
            {
                float baseScale = 0.76f;
                float maxSpanX = 0.34f * deployFactor;
                float idealStep = 0.044f * deployFactor;
                float totalW = count <= 1 ? 0f : Mathf.Min(maxSpanX, (count - 1) * idealStep);
                float x = count <= 1 ? 0f : (-totalW * 0.5f + index * (totalW / (count - 1)));
                float archZ = -(t * t) * Mathf.Min(0.014f, (count - 1) * 0.0020f) * deployFactor;
                yawDeg = t * Mathf.Min(12f, (count - 1) * 1.9f) * deployFactor;
                float idleWave = Mathf.Sin(Time.time * 1.9f + index * 0.6f) * 0.0010f;
                localPos = new Vector3(x, index * 0.0011f + idleWave, archZ);
                scale = baseScale;
            }
        }

        /// <summary>
        /// Devuelve la posición en el mundo 3D de la carta `index` dentro del abanico de la mano de `playerIdx`.
        /// </summary>
        public Vector3 GetHandCardWorldPos(int playerIdx, int index, int count)
        {
            int p = Mathf.Clamp(playerIdx, 0, 1);
            if (HandAnchor[p] == null)
                return PlaymatRoot != null ? PlaymatRoot.position : Vector3.zero;
            int clampedCount = Mathf.Max(1, count);
            int clampedIdx = Mathf.Clamp(index, 0, clampedCount - 1);
            float lift = (p == 0 && clampedIdx < handLiftAnims.Length) ? Mathf.SmoothStep(0f, 1f, handLiftAnims[clampedIdx]) : 0f;
            float spread = (p == 0 && clampedIdx < handSpreadAnims.Length) ? handSpreadAnims[clampedIdx] : 0f;
            ComputeHandCardTransform(p, clampedIdx, clampedCount, -1, lift, out Vector3 localPos, out _, out _, spread, Mathf.SmoothStep(0f, 1f, handDeployAnim));
            return HandAnchor[p].TransformPoint(localPos);
        }

        /// <summary>
        /// F4: Actualiza la posición, inclinación dinámica por velocidad y resaltado de la carta arrastrada en 3D.
        /// </summary>
        void SyncDragCard3D(Game game, int draggingHandIdx, Vector3 targetDragWorldPos, bool isOverValidDropSlot)
        {
            if (dragCardView == null) return;
            var me = game?.Players[0];
            if (me == null || draggingHandIdx < 0 || draggingHandIdx >= me.Hand.Count)
            {
                if (dragCardView.gameObject.activeSelf)
                    dragCardView.gameObject.SetActive(false);
                dragPosInitialized = false;
                return;
            }

            var card = me.Hand[draggingHandIdx];
            if (!dragCardView.gameObject.activeSelf)
                dragCardView.gameObject.SetActive(true);

            if (!dragPosInitialized)
            {
                smoothDragWorldPos = targetDragWorldPos;
                prevDragWorldPos = targetDragWorldPos;
                dragPosInitialized = true;
            }
            else
            {
                smoothDragWorldPos = Vector3.Lerp(smoothDragWorldPos, targetDragWorldPos, Mathf.Clamp01(Time.deltaTime * 24f));
            }

            Vector3 vel = (smoothDragWorldPos - prevDragWorldPos) / Mathf.Max(0.001f, Time.deltaTime);
            prevDragWorldPos = smoothDragWorldPos;

            Quaternion baseRot = PlaymatRoot != null ? PlaymatRoot.rotation : Quaternion.identity;
            Vector3 localVel = Quaternion.Inverse(baseRot) * vel;
            float pitch = Mathf.Clamp(localVel.z * 24f, -20f, 20f);
            float roll = Mathf.Clamp(-localVel.x * 24f, -20f, 20f);

            float scale = isOverValidDropSlot ? 1.12f : 1.05f;
            dragCardView.Setup(card, false, scale, true, 0f);
            dragCardView.SetRaycastEnabled(false);
            dragCardView.SetHighlight(
                true,
                isOverValidDropSlot
                    ? new Color(0.22f, 0.98f, 0.52f, 0.96f)
                    : new Color(1f, 0.86f, 0.20f, 0.92f));
            dragCardView.transform.position = smoothDragWorldPos;
            dragCardView.transform.rotation = baseRot * Quaternion.Euler(pitch, 0f, roll);
        }

        /// <summary>
        /// Intersección geométrica exacta de un rayo 3D contra un rectángulo orientado en el espacio 3D (independiente de PhysX).
        /// </summary>
        static bool IntersectsWorldQuad(Ray ray, Vector3 center, Vector3 right, Vector3 forward, Vector3 up, float halfW, float halfL, out float dist, out float normDistSq)
        {
            dist = float.MaxValue;
            normDistSq = float.MaxValue;
            Plane plane = new Plane(up, center);
            if (!plane.Raycast(ray, out float enter) || enter <= 0.01f || enter > 5f)
            {
                // Probar también con la normal invertida por si el ángulo de cámara es rasante
                Plane backPlane = new Plane(-up, center);
                if (!backPlane.Raycast(ray, out enter) || enter <= 0.01f || enter > 5f)
                    return false;
            }

            Vector3 hitPt = ray.GetPoint(enter);
            Vector3 delta = hitPt - center;
            float lx = Vector3.Dot(delta, right);
            float lz = Vector3.Dot(delta, forward);

            if (Mathf.Abs(lx) <= halfW && Mathf.Abs(lz) <= halfL)
            {
                dist = enter;
                float nx = lx / Mathf.Max(0.001f, halfW);
                float nz = lz / Mathf.Max(0.001f, halfL);
                normDistSq = nx * nx + nz * nz;
                return true;
            }
            return false;
        }

        /// <summary>
        /// F4: Detecta qué carta de la mano 3D del jugador está bajo el cursor mediante intersección geométrica 3D
        /// directa sobre las cartas activas del abanico (con respaldo de proyección en pantalla).
        /// </summary>
        public bool RaycastHandCard3D(Ray ray, out int handIdx, out CardView hitCard)
        {
            handIdx = -1;
            hitCard = null;
            if (PlaymatRoot == null || cardPool == null) return false;

            int bestIdx = -1;
            float bestScore = float.MinValue;

            var active = cardPool.ActiveCards;
            for (int i = 0; i < active.Count; i++)
            {
                var cv = active[i];
                if (cv == null || !cv.gameObject.activeInHierarchy || !cv.IsRaycastEnabled) continue;
                if (cv.OwnerIndex != 0 || cv.HandIndex < 0) continue;

                Transform t = cv.transform;
                float s = Mathf.Max(0.5f, cv.CurrentScale);
                float halfW = CardWorldWidth * s * 0.56f;
                float halfL = CardWorldLength * s * 0.58f;

                if (IntersectsWorldQuad(ray, t.position, t.right, t.forward, t.up, halfW, halfL, out _, out float normDistSq))
                {
                    // Priorizar la carta apilada encima en el abanico (mayor HandIndex) y más cercana al centro de la carta
                    float liftBonus = cv.HandIndex < handLiftAnims.Length ? handLiftAnims[cv.HandIndex] * 1.5f : 0f;
                    float score = cv.HandIndex * 0.35f + liftBonus - normDistSq * 0.6f;
                    if (bestIdx < 0 || score > bestScore)
                    {
                        bestIdx = cv.HandIndex;
                        bestScore = score;
                        hitCard = cv;
                    }
                }
            }

            if (bestIdx >= 0)
            {
                handIdx = bestIdx;
                return true;
            }

            // Respaldo por proyección en pantalla por si el cursor roza el borde inferior del abanico
            var cam = GetActiveCamera();
            if (cam != null)
            {
                Vector2 mouse = Input.mousePosition;
                float bestScreenDist = float.MaxValue;
                for (int i = 0; i < active.Count; i++)
                {
                    var cv = active[i];
                    if (cv == null || !cv.gameObject.activeInHierarchy || !cv.IsRaycastEnabled) continue;
                    if (cv.OwnerIndex != 0 || cv.HandIndex < 0) continue;

                    Vector3 sp = cam.WorldToScreenPoint(cv.transform.position);
                    if (sp.z <= 0.05f) continue;

                    float halfPixW = Screen.height * 0.052f * cv.CurrentScale;
                    float halfPixH = Screen.height * 0.078f * cv.CurrentScale;
                    float dx = Mathf.Abs(mouse.x - sp.x);
                    float dy = Mathf.Abs(mouse.y - sp.y);
                    if (dx <= halfPixW && dy <= halfPixH)
                    {
                        float d = (dx / halfPixW) * (dx / halfPixW) + (dy / halfPixH) * (dy / halfPixH) - cv.HandIndex * 0.08f;
                        if (d < bestScreenDist)
                        {
                            bestScreenDist = d;
                            bestIdx = cv.HandIndex;
                            hitCard = cv;
                        }
                    }
                }
                if (bestIdx >= 0)
                {
                    handIdx = bestIdx;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// F4: Detecta la casilla (TableSlot3D) y/o la carta en mesa (CardView) bajo el cursor mediante intersección geométrica 3D.
        /// </summary>
        public bool RaycastBoard3D(Ray ray, out TableSlot3D hitSlot, out CardView hitBoardCard)
        {
            hitSlot = null;
            hitBoardCard = null;
            if (PlaymatRoot == null) return false;

            float bestSlotNorm = float.MaxValue;
            for (int i = 0; i < tableSlots.Count; i++)
            {
                var slot = tableSlots[i];
                if (slot == null || slot.Anchor == null) continue;
                Transform a = slot.Anchor;
                float s = Mathf.Max(0.5f, slot.Scale);
                float halfW = CardWorldWidth * s * 0.62f;
                float halfL = CardWorldLength * s * 0.62f;
                if (IntersectsWorldQuad(ray, a.position, a.right, a.forward, a.up, halfW, halfL, out _, out float normSq))
                {
                    if (normSq < bestSlotNorm)
                    {
                        bestSlotNorm = normSq;
                        hitSlot = slot;
                    }
                }
            }

            if (cardPool != null)
            {
                float bestCardDist = float.MaxValue;
                var active = cardPool.ActiveCards;
                for (int i = 0; i < active.Count; i++)
                {
                    var cv = active[i];
                    if (cv == null || !cv.gameObject.activeInHierarchy || !cv.IsRaycastEnabled) continue;
                    if (cv.HandIndex >= 0) continue;

                    Transform t = cv.transform;
                    float s = Mathf.Max(0.5f, cv.CurrentScale);
                    float halfW = CardWorldWidth * s * 0.56f;
                    float halfL = CardWorldLength * s * 0.56f;
                    if (IntersectsWorldQuad(ray, t.position, t.right, t.forward, t.up, halfW, halfL, out float dist, out _))
                    {
                        if (dist < bestCardDist)
                        {
                            bestCardDist = dist;
                            hitBoardCard = cv;
                        }
                    }
                }
            }

            return hitSlot != null || hitBoardCard != null;
        }

        /// <summary>
        /// F4: Calcula el punto 3D sobre el plano de arrastre (6 cm por encima del tapete) donde apunta el ratón.
        /// </summary>
        public bool TryGetDragWorldPos(Ray ray, out Vector3 worldPos)
        {
            worldPos = default;
            if (PlaymatRoot == null) return false;
            Plane dragPlane = new Plane(PlaymatRoot.up, PlaymatRoot.position + PlaymatRoot.up * (MatThickness + 0.060f));
            if (dragPlane.Raycast(ray, out float enter) && enter > 0f && enter < 5f)
            {
                worldPos = ray.GetPoint(enter);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Proyecta cualquier posición del mundo 3D a su Rect correspondiente en pantalla (OnGUI), p. ej. para iniciar el Zoom desde la mano 3D.
        /// </summary>
        public bool TryProjectWorldPosToGUI(Vector3 worldPos, float scale, float baseSlotW, float baseSlotH, out Rect rect)
        {
            rect = default;
            var cam = GetActiveCamera();
            if (cam == null) return false;

            Vector3 sp = cam.WorldToScreenPoint(worldPos);
            if (sp.z <= 0.05f) return false;

            float w = baseSlotW * scale;
            float h = baseSlotH * scale;
            float gx = sp.x;
            float gy = Screen.height - sp.y;
            rect = new Rect(gx - w * 0.5f, gy - h * 0.5f, w, h);
            return true;
        }

        void Update()
        {
            if (PlaymatRoot == null) return;
            if (matTopRenderer != null && SpriteCache.PlaymatTex != null && matTopRenderer.sharedMaterial.mainTexture != SpriteCache.PlaymatTex)
                matTopRenderer.sharedMaterial.mainTexture = SpriteCache.PlaymatTex;
        }

        /// <summary>
        /// F4: Ilumina en el tapete 3D todos los destinos legales (en dorado pulsante) y resalta en verde esmeralda
        /// el destino legal sobre el que se encuentra el cursor o la carta arrastrada.
        /// </summary>
        public void UpdateSlotHighlights3D(HashSet<Transform> validAnchors, Transform hoveredAnchor)
        {
            float pulse = 0.28f + 0.18f * Mathf.Sin(Time.time * 5.8f);
            foreach (var kv in slotIndicators)
            {
                if (kv.Value == null) continue;
                bool isValid = validAnchors != null && validAnchors.Contains(kv.Key);
                kv.Value.enabled = isValid;
                if (isValid)
                {
                    bool isHovered = hoveredAnchor != null && kv.Key == hoveredAnchor;
                    kv.Value.sharedMaterial.color = isHovered
                        ? new Color(0.22f, 0.98f, 0.52f, 0.48f + 0.18f * Mathf.Sin(Time.time * 8f))
                        : new Color(1f, 0.86f, 0.22f, pulse);
                }
            }
        }

        /// <summary>
        /// Proyecta las esquinas del tapete 3D a coordenadas GUI de pantalla (OnGUI).
        /// </summary>
        public bool TryGetScreenPlaymatRect(out Rect screenRect)
        {
            screenRect = default;
            var cam = GetActiveCamera();
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
            var cam = GetActiveCamera();
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
