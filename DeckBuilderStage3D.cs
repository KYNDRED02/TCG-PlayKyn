using System;
using System.Collections.Generic;
using UnityEngine;

namespace PokeTCGPlay
{
    /// <summary>
    /// F9: Escenario 3D dedicado para el Constructor de Mazos (Workbench).
    /// Renderiza sobre una mesa de trabajo 3D aislada:
    /// 1) Cuadrícula/Carrusel 3D de la colección (5x2 cartas físicas con grosor, sombra, elevación en hover e inclinación al cursor).
    /// 2) Pila 3D física del mazo activo (0..60 cartas apiladas orgánicamente) + vuelos en arco 3D al añadir/quitar cartas.
    /// 3) Inspección 3D elevada frente a cámara con giro interactivo y volteo de 180° (frente / reverso).
    /// </summary>
    public class DeckBuilderStage3D
    {
        public const int GridCols = 5;
        public const int GridRows = 2;
        public const int PageSize = GridCols * GridRows; // 10 cartas 3D grandes por página

        public struct SlotScreenInfo
        {
            public int SlotIndex;
            public CardDef Card;
            public Vector2 BottomCenterGui;
            public Rect ApproxGuiRect;
            public Vector3 WorldPos;
            public Quaternion WorldRot;
            public float Scale;
        }

        class FlyingDeckCard
        {
            public CardDef Card;
            public Vector3 StartLocalPos;
            public Vector3 EndLocalPos;
            public float StartScale;
            public float EndScale;
            public float Elapsed;
            public float Duration;
            public bool ToDeck;
        }

        GameObject stageRoot;
        Camera stageCam;
        RenderTexture renderTex;
        Transform tableSurface;
        Transform catalogMat;
        Transform deckPedestal;
        Transform deckAnchor;

        CardViewPool cardPool;
        DeckStack3D deckStack3D;
        CardView inspectCardView;
        GameObject inspectBackdropGo;
        Material inspectBackdropMat;

        readonly float[] slotHoverLift = new float[PageSize];
        readonly Vector3[] lastSlotLocalPos = new Vector3[PageSize];
        readonly List<SlotScreenInfo> currentScreenSlots = new List<SlotScreenInfo>(PageSize);
        readonly List<FlyingDeckCard> activeFlights = new List<FlyingDeckCard>();

        float carouselSlideX;
        float deckBounceTimer;
        float inspectFlipAngle; // 0 = frente, 180 = reverso
        bool inspectShowBack;

        static Texture2D sWoodTex;
        static Texture2D sFeltMatTex;
        static Texture2D sPedestalTex;

        public RenderTexture RenderTexture => renderTex;
        public int HoveredSlotIndex { get; private set; } = -1;
        public bool IsHoveringDeckStack { get; private set; }
        public IReadOnlyList<SlotScreenInfo> ScreenSlots => currentScreenSlots;
        public bool InspectShowingBack => inspectShowBack;
        public Vector3 DeckStackWorldPos { get; private set; }
        public Quaternion DeckStackWorldRot { get; private set; } = Quaternion.identity;
        public float DeckStackScale { get; private set; } = 0.85f;

        public void EnsureCreated()
        {
            if (stageRoot != null) return;

            CardView.EnsureSharedResources();
            EnsureStageTextures();

            stageRoot = new GameObject("PokeTCG_DeckBuilderStage3D");
            UnityEngine.Object.DontDestroyOnLoad(stageRoot);
            stageRoot.transform.position = new Vector3(0f, -450f, 0f);
            stageRoot.transform.rotation = Quaternion.identity;

            // Cámara cenital de estudio con ligera inclinación (78.5°) para apreciar grosor 3D, elevación y altura del mazo
            var camGo = new GameObject("StageCamera3D");
            camGo.transform.SetParent(stageRoot.transform, false);
            camGo.transform.localPosition = new Vector3(0f, 0.90f, -0.175f);
            camGo.transform.localRotation = Quaternion.Euler(78.5f, 0f, 0f);
            stageCam = camGo.AddComponent<Camera>();
            stageCam.clearFlags = CameraClearFlags.SolidColor;
            stageCam.backgroundColor = new Color(0.045f, 0.062f, 0.095f, 1f);
            stageCam.fieldOfView = 33.5f;
            stageCam.nearClipPlane = 0.05f;
            stageCam.farClipPlane = 3.5f;
            stageCam.enabled = false; // Solo renderiza bajo demanda cuando el Deck Builder está abierto

            // Superficie 3D de la Mesa de Creación (Workbench)
            var woodGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            UnityEngine.Object.DestroyImmediate(woodGo.GetComponent<Collider>());
            woodGo.name = "WorkbenchWoodSurface";
            woodGo.transform.SetParent(stageRoot.transform, false);
            woodGo.transform.localPosition = new Vector3(0f, -0.004f, 0.02f);
            woodGo.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            woodGo.transform.localScale = new Vector3(1.55f, 0.96f, 1f);
            woodGo.GetComponent<MeshRenderer>().sharedMaterial = CardView.CreateUnlitMaterial(Color.white, sWoodTex, false);
            tableSurface = woodGo.transform;

            // Tapete 3D para la Cuadrícula/Carrusel de Colección
            var matGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
            UnityEngine.Object.DestroyImmediate(matGo.GetComponent<Collider>());
            matGo.name = "CatalogCollectorMat3D";
            matGo.transform.SetParent(stageRoot.transform, false);
            matGo.transform.localPosition = new Vector3(-0.14f, -0.0018f, -0.04f);
            matGo.transform.localScale = new Vector3(0.68f, 0.0032f, 0.40f);
            matGo.GetComponent<MeshRenderer>().sharedMaterial = CardView.CreateUnlitMaterial(Color.white, sFeltMatTex, false);
            catalogMat = matGo.transform;

            // Pedestal 3D para la Pila Física del Mazo Activo
            var pedGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
            UnityEngine.Object.DestroyImmediate(pedGo.GetComponent<Collider>());
            pedGo.name = "ActiveDeckPedestal3D";
            pedGo.transform.SetParent(stageRoot.transform, false);
            pedGo.transform.localPosition = new Vector3(0.28f, -0.0015f, 0.07f);
            pedGo.transform.localScale = new Vector3(0.125f, 0.0035f, 0.160f);
            pedGo.GetComponent<MeshRenderer>().sharedMaterial = CardView.CreateUnlitMaterial(Color.white, sPedestalTex, false);
            deckPedestal = pedGo.transform;

            var deckAncGo = new GameObject("BuilderDeckStackAnchor");
            deckAncGo.transform.SetParent(stageRoot.transform, false);
            deckAnchor = deckAncGo.transform;
            deckStack3D = new DeckStack3D(deckAnchor);

            cardPool = new CardViewPool(stageRoot.transform);

            // Carta y telón 3D exclusivos para la Inspección 3D (clic derecho)
            inspectCardView = CardView.Create(stageRoot.transform);
            inspectCardView.name = "BuilderInspectCard3D";
            inspectCardView.SetRaycastEnabled(false);
            inspectCardView.gameObject.SetActive(false);

            inspectBackdropGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            UnityEngine.Object.DestroyImmediate(inspectBackdropGo.GetComponent<Collider>());
            inspectBackdropGo.name = "BuilderInspectBackdrop3D";
            inspectBackdropGo.transform.SetParent(stageRoot.transform, false);
            inspectBackdropMat = CardView.CreateUnlitMaterial(new Color(0.02f, 0.03f, 0.06f, 0.78f), null, true);
            var bdMr = inspectBackdropGo.GetComponent<MeshRenderer>();
            bdMr.sharedMaterial = inspectBackdropMat;
            bdMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            inspectBackdropGo.SetActive(false);
        }

        public void SetActive(bool active)
        {
            if (active) EnsureCreated();
            if (stageRoot != null && stageRoot.activeSelf != active)
                stageRoot.SetActive(active);
            if (!active)
            {
                HoveredSlotIndex = -1;
                IsHoveringDeckStack = false;
                activeFlights.Clear();
                ResetInspectFlip();
            }
        }

        public void TriggerCarouselSlide(int direction)
        {
            carouselSlideX = Mathf.Clamp(carouselSlideX + direction * 0.26f, -0.42f, 0.42f);
        }

        public void ToggleInspectFlip()
        {
            inspectShowBack = !inspectShowBack;
        }

        public void ResetInspectFlip()
        {
            inspectShowBack = false;
            inspectFlipAngle = 0f;
        }

        public void SpawnAddFlight(int slotIndex, CardDef card)
        {
            if (card == null || stageRoot == null || deckAnchor == null) return;
            TcgAudio.PlayCardPlace();
            Vector3 start = (slotIndex >= 0 && slotIndex < PageSize) ? lastSlotLocalPos[slotIndex] : catalogMat.localPosition;
            Vector3 end = deckAnchor.localPosition + new Vector3(0f, 0.028f, 0f);
            activeFlights.Add(new FlyingDeckCard
            {
                Card = card,
                StartLocalPos = start,
                EndLocalPos = end,
                StartScale = 1.16f,
                EndScale = 0.94f,
                Duration = 0.30f,
                ToDeck = true
            });
            deckBounceTimer = 0.22f;
        }

        public void SpawnRemoveFlight(CardDef card)
        {
            if (card == null || stageRoot == null || deckAnchor == null) return;
            TcgAudio.PlayCardDraw();
            Vector3 start = deckAnchor.localPosition + new Vector3(0f, 0.024f, 0f);
            Vector3 end = catalogMat.localPosition + new Vector3(0.12f, 0.015f, 0f);
            activeFlights.Add(new FlyingDeckCard
            {
                Card = card,
                StartLocalPos = start,
                EndLocalPos = end,
                StartScale = 0.94f,
                EndScale = 1.05f,
                Duration = 0.26f,
                ToDeck = false
            });
        }

        void EnsureRenderTexture()
        {
            int w = Mathf.Max(640, Screen.width);
            int h = Mathf.Max(360, Screen.height);
            if (renderTex != null && (renderTex.width != w || renderTex.height != h))
            {
                stageCam.targetTexture = null;
                renderTex.Release();
                UnityEngine.Object.Destroy(renderTex);
                renderTex = null;
            }
            if (renderTex == null)
            {
                renderTex = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
                renderTex.antiAliasing = 4;
                renderTex.filterMode = FilterMode.Bilinear;
                renderTex.Create();
            }
            stageCam.targetTexture = renderTex;
        }

        Vector3 GuiPointToLocalPlane(Vector2 guiPt, float localY = 0f)
        {
            Vector3 camPt = new Vector3(guiPt.x, Screen.height - guiPt.y, 0f);
            Ray ray = stageCam.ScreenPointToRay(camPt);
            float worldPlaneY = stageRoot.transform.position.y + localY;
            float denom = Mathf.Abs(ray.direction.y) < 0.0001f ? -0.0001f : ray.direction.y;
            float t = (worldPlaneY - ray.origin.y) / denom;
            Vector3 worldHit = ray.origin + ray.direction * Mathf.Max(0.05f, t);
            return stageRoot.transform.InverseTransformPoint(worldHit);
        }

        Vector2 LocalToGuiPoint(Vector3 localPos)
        {
            Vector3 world = stageRoot.transform.TransformPoint(localPos);
            Vector3 sp = stageCam.WorldToScreenPoint(world);
            return new Vector2(sp.x, Screen.height - sp.y);
        }

        public void UpdateAndRender(
            IList<CardDef> pageCards,
            IList<CardDef> currentDeck,
            FormatDef format,
            Rect catalogGuiRect,
            Rect deckPedestalGuiRect,
            bool inputBlockedByModal,
            CardDef zoomCard,
            float zoomAnim,
            Vector3 zoomOriginWorldPos,
            Quaternion zoomOriginWorldRot,
            float zoomOriginScale)
        {
            EnsureCreated();
            if (!stageRoot.activeSelf) stageRoot.SetActive(true);
            EnsureRenderTexture();

            float dt = Mathf.Min(0.05f, Time.unscaledDeltaTime > 0f ? Time.unscaledDeltaTime : Time.deltaTime);
            carouselSlideX = Mathf.Lerp(carouselSlideX, 0f, 1f - Mathf.Exp(-14f * dt));
            if (Mathf.Abs(carouselSlideX) < 0.0005f) carouselSlideX = 0f;

            if (deckBounceTimer > 0f)
                deckBounceTimer = Mathf.Max(0f, deckBounceTimer - dt);

            float targetFlip = inspectShowBack ? 180f : 0f;
            inspectFlipAngle = Mathf.MoveTowards(inspectFlipAngle, targetFlip, dt * 520f);

            cardPool.BeginFrame();
            currentScreenSlots.Clear();

            // 1) Alinear el Tapete 3D del Catálogo exactamente con el área de pantalla asignada
            Vector3 catTopLeft = GuiPointToLocalPlane(new Vector2(catalogGuiRect.xMin + 14f, catalogGuiRect.yMin + 10f));
            Vector3 catBotRight = GuiPointToLocalPlane(new Vector2(catalogGuiRect.xMax - 14f, catalogGuiRect.yMax - 26f));
            Vector3 catCenter = (catTopLeft + catBotRight) * 0.5f;
            float spanX = Mathf.Max(0.32f, Mathf.Abs(catBotRight.x - catTopLeft.x));
            float spanZ = Mathf.Max(0.20f, Mathf.Abs(catTopLeft.z - catBotRight.z));

            catalogMat.localPosition = new Vector3(catCenter.x, -0.0018f, catCenter.z - 0.004f);
            catalogMat.localScale = new Vector3(spanX + 0.032f, 0.0032f, spanZ + 0.032f);

            // Escala óptima de carta 3D para que quepan 5 columnas × 2 filas con holgura limpia
            float maxScaleByW = (spanX / (GridCols + 0.55f)) / TableView.CardWorldWidth;
            float maxScaleByH = (spanZ / (GridRows + 0.42f)) / TableView.CardWorldLength;
            float baseCardScale = Mathf.Clamp(Mathf.Min(maxScaleByW, maxScaleByH), 0.72f, 1.24f);

            float colStep = spanX / GridCols;
            float rowStep = spanZ / GridRows;
            float startX = catCenter.x - colStep * ((GridCols - 1) * 0.5f);
            float topRowZ = catCenter.z + rowStep * 0.48f;

            // 2) Alinear el Pedestal 3D y la Pila Física del Mazo Activo en el panel derecho
            Vector3 deckLocalCenter = GuiPointToLocalPlane(deckPedestalGuiRect.center);
            deckPedestal.localPosition = new Vector3(deckLocalCenter.x, -0.0015f, deckLocalCenter.z);
            float deckScale = Mathf.Clamp(baseCardScale * 0.86f, 0.68f, 0.98f);
            deckPedestal.localScale = new Vector3(TableView.CardWorldWidth * deckScale * 1.32f, 0.0034f, TableView.CardWorldLength * deckScale * 1.26f);

            float bounceScale = deckBounceTimer > 0f ? (1f + Mathf.Sin((deckBounceTimer / 0.22f) * Mathf.PI) * 0.06f) : 1f;
            deckAnchor.localPosition = new Vector3(deckLocalCenter.x, 0.0005f, deckLocalCenter.z);
            deckAnchor.localRotation = Quaternion.Euler(0f, -6f, 0f);
            deckAnchor.localScale = Vector3.one * (deckScale * bounceScale);

            int deckCount = currentDeck != null ? currentDeck.Count : 0;
            deckStack3D.Sync(deckCount);

            // Si el mazo tiene cartas, mostrar también la última carta añadida boca arriba coronando la pila 3D
            CardView deckTopCv = null;
            if (deckCount > 0 && currentDeck != null)
            {
                var topCard = currentDeck[deckCount - 1];
                float stackTopY = Mathf.Max(0.0018f, deckCount * 0.00048f) * deckScale + 0.0014f;
                deckTopCv = cardPool.Spawn(
                    stageRoot.transform,
                    new Vector3(deckLocalCenter.x, stackTopY, deckLocalCenter.z),
                    topCard,
                    false,
                    deckScale * bounceScale,
                    deckCount == (format?.DeckSize ?? 60),
                    -5.5f);
                if (deckTopCv != null)
                {
                    deckTopCv.SetShadowEnabled(false);
                    deckTopCv.SetRaycastEnabled(true);
                    if (deckCount == (format?.DeckSize ?? 60))
                        deckTopCv.SetHighlight(true, new Color(0.25f, 0.95f, 0.55f, 0.90f));
                }
            }

            // 3) Renderizar las 10 cartas físicas 3D de la página actual (con deslizamiento de carrusel y hover)
            int count = pageCards != null ? Mathf.Min(PageSize, pageCards.Count) : 0;
            var spawnedSlots = new CardView[PageSize];

            // Pre-calcular posiciones base para Raycast
            for (int i = 0; i < count; i++)
            {
                int col = i % GridCols;
                int row = i / GridCols;
                float lx = startX + col * colStep + carouselSlideX;
                float lz = topRowZ - row * rowStep;
                float lift = slotHoverLift[i];
                float ly = 0.0016f + lift * 0.032f;
                lastSlotLocalPos[i] = new Vector3(lx, ly, lz);

                var card = pageCards[i];
                bool beingInspected = zoomCard == card && zoomAnim > 0.03f;
                if (beingInspected) continue;

                float sc = baseCardScale * (1f + lift * 0.11f);
                var cv = cardPool.Spawn(stageRoot.transform, lastSlotLocalPos[i], card, false, sc, false, 0f);
                if (cv != null)
                {
                    cv.HandIndex = i;
                    cv.SetRaycastEnabled(true);
                    spawnedSlots[i] = cv;
                }
            }

            // 4) Intersección geométrica 3D exacta contra las cartas de la cuadrícula y la pila física del mazo (independiente de PhysX)
            HoveredSlotIndex = -1;
            IsHoveringDeckStack = false;
            if (!inputBlockedByModal && zoomCard == null)
            {
                Ray mouseRay = stageCam.ScreenPointToRay(Input.mousePosition);
                float bestDist = float.MaxValue;
                float halfW = TableView.CardWorldWidth * baseCardScale * 0.54f;
                float halfL = TableView.CardWorldLength * baseCardScale * 0.54f;

                for (int i = 0; i < count; i++)
                {
                    var cv = spawnedSlots[i];
                    if (cv == null) continue;
                    Transform t = cv.transform;
                    if (IntersectsWorldQuad(mouseRay, t.position, t.right, t.forward, t.up, halfW, halfL, out float hitDist) && hitDist < bestDist)
                    {
                        bestDist = hitDist;
                        HoveredSlotIndex = i;
                        IsHoveringDeckStack = false;
                    }
                }

                float deckHalfW = TableView.CardWorldWidth * deckScale * 0.62f;
                float deckHalfL = TableView.CardWorldLength * deckScale * 0.62f;
                Vector3 deckWorldTop = deckTopCv != null ? deckTopCv.transform.position : stageRoot.transform.TransformPoint(new Vector3(deckLocalCenter.x, 0.004f, deckLocalCenter.z));
                DeckStackWorldPos = deckWorldTop;
                DeckStackWorldRot = deckTopCv != null ? deckTopCv.transform.rotation : deckAnchor.rotation;
                DeckStackScale = deckScale;
                if (IntersectsWorldQuad(mouseRay, deckWorldTop, deckAnchor.right, deckAnchor.forward, deckAnchor.up, deckHalfW, deckHalfL, out float deckDist) && deckDist < bestDist)
                {
                    HoveredSlotIndex = -1;
                    IsHoveringDeckStack = true;
                }
            }
            else
            {
                DeckStackWorldPos = deckTopCv != null ? deckTopCv.transform.position : stageRoot.transform.TransformPoint(new Vector3(deckLocalCenter.x, 0.004f, deckLocalCenter.z));
                DeckStackWorldRot = deckTopCv != null ? deckTopCv.transform.rotation : deckAnchor.rotation;
                DeckStackScale = deckScale;
            }

            if (deckTopCv != null && IsHoveringDeckStack)
                deckTopCv.SetHighlight(true, new Color(1f, 0.86f, 0.22f, 0.96f));

            // 5) Aplicar animación de elevación 3D, inclinación hacia el cursor, tinte por colección y proyección de etiquetas
            Vector3 mouseLocalOnMat = GuiPointToLocalPlane(new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y));
            for (int i = 0; i < PageSize; i++)
            {
                float target = (i == HoveredSlotIndex && !inputBlockedByModal && zoomCard == null) ? 1f : 0f;
                slotHoverLift[i] = Mathf.MoveTowards(slotHoverLift[i], target, dt * 9.5f);
            }

            var deckAsList = currentDeck as List<CardDef> ?? (currentDeck != null ? new List<CardDef>(currentDeck) : new List<CardDef>());
            for (int i = 0; i < count; i++)
            {
                var card = pageCards[i];
                var cv = spawnedSlots[i];
                float lift = Mathf.SmoothStep(0f, 1f, slotHoverLift[i]);

                int owned = Collection.Owned(card);
                int inDeck = CountInList(deckAsList, card.Id);
                bool canAddMore = DeckValidator.RemainingAllowed(card, deckAsList, format) > 0;

                if (cv != null)
                {
                    // Inclinación 3D dinámica al pasar el ratón encima
                    if (lift > 0.01f)
                    {
                        Vector3 delta = mouseLocalOnMat - lastSlotLocalPos[i];
                        float pitch = Mathf.Clamp(-delta.z * 120f, -9f, 9f) * lift;
                        float roll = Mathf.Clamp(-delta.x * 135f, -10f, 10f) * lift;
                        cv.SetFlipRoll(roll, pitch, 0f);
                    }

                    if (owned <= 0)
                    {
                        cv.SetStatusTintOverlay(new Color(0.04f, 0.05f, 0.08f, 0.58f));
                        if (i == HoveredSlotIndex)
                            cv.SetHighlight(true, new Color(1f, 0.42f, 0.42f, 0.90f));
                    }
                    else if (i == HoveredSlotIndex)
                    {
                        cv.SetHighlight(true, canAddMore
                            ? new Color(1f, 0.86f, 0.22f, 0.96f)
                            : new Color(0.45f, 0.82f, 1f, 0.90f));
                    }
                    else if (inDeck > 0)
                    {
                        cv.SetHighlight(true, new Color(0.24f, 0.92f, 0.56f, 0.85f));
                    }
                }

                // Coordenadas proyectadas en pantalla para dibujar el contador debajo de cada carta 3D
                float halfW = TableView.CardWorldWidth * baseCardScale * 0.5f;
                float halfL = TableView.CardWorldLength * baseCardScale * 0.5f;
                Vector3 baseSlotPos = new Vector3(lastSlotLocalPos[i].x, 0.002f, lastSlotLocalPos[i].z);
                Vector2 topLeftGui = LocalToGuiPoint(baseSlotPos + new Vector3(-halfW, 0f, halfL));
                Vector2 botRightGui = LocalToGuiPoint(baseSlotPos + new Vector3(halfW, 0f, -halfL));
                Vector2 botCenterGui = LocalToGuiPoint(baseSlotPos + new Vector3(0f, 0f, -halfL - 0.004f));

                currentScreenSlots.Add(new SlotScreenInfo
                {
                    SlotIndex = i,
                    Card = card,
                    BottomCenterGui = botCenterGui,
                    ApproxGuiRect = Rect.MinMaxRect(
                        Mathf.Min(topLeftGui.x, botRightGui.x),
                        Mathf.Min(topLeftGui.y, botRightGui.y),
                        Mathf.Max(topLeftGui.x, botRightGui.x),
                        Mathf.Max(topLeftGui.y, botRightGui.y)),
                    WorldPos = cv != null ? cv.transform.position : stageRoot.transform.TransformPoint(lastSlotLocalPos[i]),
                    WorldRot = cv != null ? cv.transform.rotation : stageRoot.transform.rotation,
                    Scale = baseCardScale
                });
            }

            // 6) Animar cartas volando en arco 3D (añadir al mazo / quitar del mazo)
            for (int f = activeFlights.Count - 1; f >= 0; f--)
            {
                var fl = activeFlights[f];
                fl.Elapsed += dt * Loc.AnimSpeed;
                float t = Mathf.Clamp01(fl.Elapsed / Mathf.Max(0.05f, fl.Duration));
                float smooth = Mathf.SmoothStep(0f, 1f, t);
                Vector3 pos = Vector3.Lerp(fl.StartLocalPos, fl.EndLocalPos, smooth);
                pos.y += Mathf.Sin(smooth * Mathf.PI) * 0.11f;
                float sc = Mathf.Lerp(fl.StartScale, fl.EndScale, smooth) * baseCardScale;
                float yaw = Mathf.Sin(smooth * Mathf.PI) * (fl.ToDeck ? -14f : 14f);
                float pitch = Mathf.Sin(smooth * Mathf.PI) * -12f;

                var fcv = cardPool.Spawn(stageRoot.transform, pos, fl.Card, false, sc, true, yaw, pitch);
                if (fcv != null)
                {
                    fcv.SetRaycastEnabled(false);
                    fcv.SetHighlight(true, fl.ToDeck
                        ? new Color(0.30f, 0.98f, 0.60f, 0.92f)
                        : new Color(1f, 0.55f, 0.42f, 0.90f));
                }

                if (t >= 1f)
                    activeFlights.RemoveAt(f);
            }

            // 7) Inspección 3D elevada hacia la cámara (con soporte para voltear 180° y ver el reverso oficial)
            SyncInspect3D(zoomCard, zoomAnim, zoomOriginWorldPos, zoomOriginWorldRot, zoomOriginScale);

            // 8) Renderizar el escenario 3D a nuestra RenderTexture con MSAA 4x
            stageCam.Render();
        }

        void SyncInspect3D(
            CardDef zoomCard,
            float zoomAnim,
            Vector3 originWorldPos,
            Quaternion originWorldRot,
            float originScale)
        {
            if (inspectCardView == null || inspectBackdropGo == null) return;
            if (zoomCard == null || zoomAnim <= 0.005f)
            {
                if (inspectCardView.gameObject.activeSelf) inspectCardView.gameObject.SetActive(false);
                if (inspectBackdropGo.activeSelf) inspectBackdropGo.SetActive(false);
                return;
            }

            if (!inspectCardView.gameObject.activeSelf) inspectCardView.gameObject.SetActive(true);
            if (!inspectBackdropGo.activeSelf) inspectBackdropGo.SetActive(true);

            float smooth = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(zoomAnim));

            inspectBackdropGo.transform.position = stageCam.transform.position + stageCam.transform.forward * 0.39f;
            inspectBackdropGo.transform.rotation = stageCam.transform.rotation;
            inspectBackdropGo.transform.localScale = new Vector3(1.6f, 1.0f, 1f);
            if (inspectBackdropMat != null)
                inspectBackdropMat.color = new Color(0.02f, 0.03f, 0.06f, 0.78f * smooth);

            Vector3 targetPos = stageCam.transform.position
                              + stageCam.transform.forward * 0.305f
                              - stageCam.transform.right * 0.084f
                              + stageCam.transform.up * 0.003f;

            Quaternion faceCamRot = Quaternion.LookRotation(stageCam.transform.up, -stageCam.transform.forward);
            float mx = Mathf.Clamp((Input.mousePosition.x / Mathf.Max(1f, Screen.width)) * 2f - 1f, -1f, 1f);
            float my = Mathf.Clamp((Input.mousePosition.y / Mathf.Max(1f, Screen.height)) * 2f - 1f, -1f, 1f);
            float flightSpin = Mathf.Sin(smooth * Mathf.PI) * 14f;
            Quaternion tilt = Quaternion.Euler(
                -my * 8f * smooth,
                (mx * 10f + Mathf.Sin(Time.unscaledTime * 1.6f) * 2.0f) * smooth,
                -mx * 4.5f * smooth + flightSpin);
            Quaternion targetRot = faceCamRot * tilt;

            Vector3 fallbackOrigin = stageRoot.transform.position + new Vector3(-0.12f, 0.01f, 0f);
            Vector3 startPos = originWorldPos.sqrMagnitude > 0.001f ? originWorldPos : fallbackOrigin;
            Quaternion startRot = (originWorldRot.x == 0f && originWorldRot.y == 0f && originWorldRot.z == 0f && originWorldRot.w == 0f)
                ? stageRoot.transform.rotation
                : originWorldRot;

            Vector3 curPos = Vector3.Lerp(startPos, targetPos, smooth) + stageCam.transform.up * (Mathf.Sin(smooth * Mathf.PI) * 0.026f);
            Quaternion curRot = Quaternion.Slerp(startRot, targetRot, smooth);
            float curScale = Mathf.Lerp(Mathf.Max(0.75f, originScale), 1.32f, smooth);

            inspectCardView.Setup(zoomCard, false, curScale, true, 0f);
            inspectCardView.SetFlipRoll(inspectFlipAngle, 0f, 0f);
            inspectCardView.SetShadowEnabled(false);
            inspectCardView.SetRaycastEnabled(false);
            inspectCardView.SetHighlight(true, new Color(1f, 0.88f, 0.26f, 0.88f * smooth));
            inspectCardView.transform.position = curPos;
            inspectCardView.transform.rotation = curRot;
        }

        public void Destroy()
        {
            if (renderTex != null)
            {
                if (stageCam != null) stageCam.targetTexture = null;
                renderTex.Release();
                UnityEngine.Object.Destroy(renderTex);
                renderTex = null;
            }
            if (stageRoot != null)
            {
                UnityEngine.Object.Destroy(stageRoot);
                stageRoot = null;
            }
        }

        static bool IntersectsWorldQuad(Ray ray, Vector3 center, Vector3 right, Vector3 forward, Vector3 up, float halfW, float halfL, out float dist)
        {
            dist = float.MaxValue;
            Plane plane = new Plane(up, center);
            if (!plane.Raycast(ray, out float enter) || enter <= 0.01f || enter > 5f)
            {
                Plane backPlane = new Plane(-up, center);
                if (!backPlane.Raycast(ray, out enter) || enter <= 0.01f || enter > 5f)
                    return false;
            }
            Vector3 delta = ray.GetPoint(enter) - center;
            float lx = Vector3.Dot(delta, right);
            float lz = Vector3.Dot(delta, forward);
            if (Mathf.Abs(lx) <= halfW && Mathf.Abs(lz) <= halfL)
            {
                dist = enter;
                return true;
            }
            return false;
        }

        static int CountInList(IList<CardDef> list, string id)
        {
            int c = 0;
            for (int i = 0; i < list.Count; i++)
                if (list[i] != null && string.Equals(list[i].Id, id, StringComparison.OrdinalIgnoreCase))
                    c++;
            return c;
        }

        static void EnsureStageTextures()
        {
            if (sWoodTex == null)
            {
                sWoodTex = new Texture2D(128, 128, TextureFormat.RGBA32, false);
                for (int y = 0; y < 128; y++)
                {
                    for (int x = 0; x < 128; x++)
                    {
                        float nx = (x / 127f) * 2f - 1f;
                        float ny = (y / 127f) * 2f - 1f;
                        float vig = Mathf.Clamp01(1f - (nx * nx * 0.28f + ny * ny * 0.32f));
                        float grain = Mathf.Sin(y * 0.35f + Mathf.Sin(x * 0.08f) * 2.5f) * 0.012f;
                        Color c = new Color((0.085f + grain) * vig, (0.095f + grain * 0.8f) * vig, (0.130f + grain * 0.5f) * vig, 1f);
                        sWoodTex.SetPixel(x, y, c);
                    }
                }
                sWoodTex.wrapMode = TextureWrapMode.Clamp;
                sWoodTex.Apply();
            }

            if (sFeltMatTex == null)
            {
                sFeltMatTex = MakeBorderedMatTex(256, 160,
                    new Color(0.08f, 0.14f, 0.21f, 1f),
                    new Color(0.05f, 0.09f, 0.14f, 1f),
                    new Color(0.82f, 0.68f, 0.26f, 1f));
            }

            if (sPedestalTex == null)
            {
                sPedestalTex = MakeBorderedMatTex(128, 160,
                    new Color(0.11f, 0.16f, 0.24f, 1f),
                    new Color(0.07f, 0.10f, 0.16f, 1f),
                    new Color(0.38f, 0.78f, 0.96f, 1f));
            }
        }

        static Texture2D MakeBorderedMatTex(int w, int h, Color centerCol, Color edgeCol, Color trimCol)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int distEdge = Mathf.Min(Mathf.Min(x, w - 1 - x), Mathf.Min(y, h - 1 - y));
                    Color c;
                    if (distEdge < 3)
                        c = edgeCol * 0.7f;
                    else if (distEdge >= 5 && distEdge <= 7)
                        c = Color.Lerp(edgeCol, trimCol, 0.65f);
                    else
                    {
                        float nx = (x / (float)(w - 1)) * 2f - 1f;
                        float ny = (y / (float)(h - 1)) * 2f - 1f;
                        float t = Mathf.Clamp01((nx * nx + ny * ny) * 0.55f);
                        c = Color.Lerp(centerCol, edgeCol, t);
                    }
                    tex.SetPixel(x, y, c);
                }
            }
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            tex.Apply();
            return tex;
        }
    }
}
