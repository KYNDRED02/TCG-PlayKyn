// PokeTCGPlay: 3D playmat zone anchors, slot colliders and drop target highlights
using System;
using System.Collections.Generic;
using UnityEngine;

namespace PokeTCGPlay
{
    /// <summary>
    /// Submódulo de anclajes físicos y casillas 3D sobre el tapete de juego (Playmat).
    /// 
    /// ¿Para qué sirve?
    /// - Define las coordenadas físicas de cada zona en la mesa (Activo, Banca, Mazo, Descarte, Premios, Estadio, Moneda).
    /// - Proyecta posiciones 3D a la pantalla (TryProjectWorldPosToGUI, TryGetScreenPlaymatRect) para sincronizar menús e interfaz.
    /// - Detecta qué casilla o carta está señalando el ratón con 'RaycastBoard3D'.
    /// - Ilumina las casillas válidas ('UpdateSlotHighlights3D') al arrastrar cartas desde la mano.
    /// 
    /// Modificaciones habituales:
    /// - Para mover o alejar una zona del tapete, ajusta los valores nx (horizontal) o ny (vertical) en 'BuildAnchors'.
    /// </summary>
    public partial class TableView
    {
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
        public Transform CoinArenaAnchor { get; private set; }

        // Indicadores 3D sutiles sobre las casillas del tapete (se iluminan cuando son destino válido)
        readonly Dictionary<Transform, MeshRenderer> slotIndicators = new Dictionary<Transform, MeshRenderer>();

        readonly List<TableSlot3D> tableSlots = new List<TableSlot3D>();

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

            // Mazos (Deck) y Descartes (Trash) alineados con las cajas impresas en playmat.png
            DeckAnchor[0] = CreateNormalizedAnchor("Deck_Player", 0.913f, 0.640f, 0.0015f, false, true, 1f);
            DiscardAnchor[0] = CreateNormalizedAnchor("Trash_Player", 0.913f, 0.865f, 0.0015f, false, true, 1f);
            DeckAnchor[1] = CreateNormalizedAnchor("Deck_NPC", 0.087f, 0.360f, 0.0015f, true, true, 1f);
            DiscardAnchor[1] = CreateNormalizedAnchor("Trash_NPC", 0.087f, 0.135f, 0.0015f, true, true, 1f);
            AttachSlotCollider(DeckAnchor[0], TableSlotKind.Deck, 0, -1, 1f);
            AttachSlotCollider(DiscardAnchor[0], TableSlotKind.Discard, 0, -1, 1f);
            AttachSlotCollider(DeckAnchor[1], TableSlotKind.Deck, 1, -1, 1f);
            AttachSlotCollider(DiscardAnchor[1], TableSlotKind.Discard, 1, -1, 1f);

            // Casillas de Premios (Side: 3 casillas por jugador, 6 cartas físicas de premio totales)
            float[] p0Ys = { 0.584f, 0.742f, 0.900f };
            float[] p1Ys = { 0.100f, 0.258f, 0.416f };
            for (int b = 0; b < 3; b++)
            {
                PrizeBoxAnchor[0, b] = CreateNormalizedAnchor($"PrizeBox_Player_{b}", 0.092f, p0Ys[b], 0.0015f, false, false, 0.88f);
                PrizeBoxAnchor[1, b] = CreateNormalizedAnchor($"PrizeBox_NPC_{b}", 0.908f, p1Ys[b], 0.0015f, true, false, 0.88f);
                for (int k = 0; k < 2; k++)
                {
                    int pIdx = b * 2 + k;
                    float staggerX = (k == 0) ? -0.009f : 0.002f;
                    float staggerY = (k == 0) ? 0.005f : -0.002f;
                    float stackH = 0.0015f + k * 0.0025f;
                    PrizeAnchor[0, pIdx] = CreateNormalizedAnchor($"Prize_Player_{pIdx}", 0.092f + staggerX, p0Ys[b] + staggerY, stackH, false, false, 0.88f);
                    PrizeAnchor[1, pIdx] = CreateNormalizedAnchor($"Prize_NPC_{pIdx}", 0.908f - staggerX, p1Ys[b] - staggerY, stackH, true, false, 0.88f);
                }
            }

            // Estadio (derecha del campo), Centro de Pokéball y Zona libre de Moneda (izquierda del campo)
            StadiumAnchor = CreateNormalizedAnchor("Stadium", 0.748f, 0.50f, 0.0015f, false, false, 0.95f);
            AttachSlotCollider(StadiumAnchor, TableSlotKind.PlayfieldCenter, 0, -1, 1.1f);
            CoinAnchor = CreateNormalizedAnchor("CoinCenter", 0.50f, 0.50f, 0.0018f, false, true, 1.35f);
            CoinArenaAnchor = CreateNormalizedAnchor("CoinArena", 0.255f, 0.50f, 0.0018f, false, false, 1f);
            AttachSlotCollider(CoinAnchor, TableSlotKind.PlayfieldCenter, 0, -1, 1.45f);

            // F4 & F5: Anclajes 3D de Mano para la cámara desde los ojos del jugador sentado
            // (Subida hasta el límite marcado justo debajo de Bench sin cubrir la Banca)
            HandAnchor[0] = CreateNormalizedAnchor("Hand_Player", 0.50f, 1.018f, 0.066f, false, false, 1f);
            HandAnchor[0].localRotation = Quaternion.Euler(-44.5f, 0f, 0f);
            HandAnchor[1] = CreateNormalizedAnchor("Hand_NPC", 0.50f, 0.020f, 0.055f, true, false, 1f);
            HandAnchor[1].localRotation = Quaternion.Euler(28f, 180f, 0f);

            // F2: Inicializar el Pool de Cartas 3D, el Pool de Bloques Físicos (Mazo y Premios) y respaldo de Mazo
            cardPool = new CardViewPool(PlaymatRoot);
            physicalBlockPool = new PhysicalCardBlockPool(PlaymatRoot);
            deckStacks[0] = new DeckStack3D(DeckAnchor[0]);
            deckStacks[1] = new DeckStack3D(DeckAnchor[1]);
            deckStacks[0].Sync(0);
            deckStacks[1].Sync(0);

            // F4: Carta 3D dedicada para el arrastre (Drag & Drop) sobre el tapete
            dragCardView = CardView.Create(PlaymatRoot);
            dragCardView.gameObject.name = "PokeTCG_DragCard3D";
            dragCardView.SetRaycastEnabled(false);
            dragCardView.gameObject.SetActive(false);

            // F5: Telón 3D oscurecido y Carta 3D dedicada para la Inspección 3D con clic derecho
            inspectBackdropGo = new GameObject("PokeTCG_InspectBackdrop3D");
            inspectBackdropGo.transform.SetParent(PlaymatRoot, false);
            inspectBackdropGo.AddComponent<MeshFilter>().sharedMesh = sharedQuadMesh;
            var bdRenderer = inspectBackdropGo.AddComponent<MeshRenderer>();
            inspectBackdropMat = CardView.CreateUnlitMaterial(new Color(0.02f, 0.03f, 0.06f, 0.72f), null, true);
            bdRenderer.sharedMaterial = inspectBackdropMat;
            bdRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            inspectBackdropGo.SetActive(false);

            inspectCardView = CardView.Create(PlaymatRoot);
            inspectCardView.gameObject.name = "PokeTCG_InspectCard3D";
            inspectCardView.SetRaycastEnabled(false);
            inspectCardView.SetShadowEnabled(false);
            inspectCardView.gameObject.SetActive(false);

            // F6: Inicializar el Pool 3D de Fichas de Daño y Marcadores de Estado
            statusCounters = new StatusCounters3D(PlaymatRoot);
            StatusCounters3D.ActivePopups.Clear();

            // F7: Inicializar la Moneda 3D física sobre el Playmat
            Coin = new Coin3D(PlaymatRoot, CoinArenaAnchor.localPosition);
        }


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


    }
}
