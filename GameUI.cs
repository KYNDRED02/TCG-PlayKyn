using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PokeTCGPlay
{
    /// <summary>
    /// Tablero de Pokémon TCG sobre la mesa física con vista cenital (top-down),
    /// usando la imagen real del Playmat, lanzamiento de moneda animado, energías debajo del Pokémon,
    /// iconos de estados alterados con contador de turnos, animaciones de movimiento de cartas y zoom con elevación.
    /// </summary>
    public class GameUI : MonoBehaviour
    {
        public static GameUI Instance { get; private set; }

        bool open;
        Game game;
        string message = "";
        string toastMessage = "";
        float toastTimer;
        int selHand = -1;
        bool retreatMode;
        bool showSettingsModal;

        // --- F4: Mano e Input 3D (Hover, Selección, Arrastre Drag & Drop y Raycast 3D) ---
        int hoveredHandIdx = -1;
        int pressedHandIdx = -1;
        Vector2 pressMousePos;
        bool isDragging3D;
        Vector3 currentDragWorldPos;
        TableSlot3D hoveredSlot3D;
        CardView hoveredBoardCard3D;
        readonly HashSet<Transform> validTargetAnchors = new HashSet<Transform>();

        // --- F5: Inspección 3D con Clic Derecho (la carta 3D se eleva hacia la cámara y vuelve a su sitio) ---
        CardDef zoomedCard;
        PokemonInPlay zoomedPip;
        Rect zoomOriginRect;
        Vector3 zoomOriginWorldPos;
        Quaternion zoomOriginWorldRot;
        float zoomOriginScale = 1f;
        float zoomAnim; // 0 = en la mesa/mano, 1 = elevado frente a la cámara
        bool zoomClosing;
        float aiSetupDelay;

        // --- Animación de Moneda Inicial y Monedas de Ataque ---
        bool startCoinSpinning;
        bool startCoinDone;
        bool startCoinPlayerChoseHeads;
        bool startCoinResultHeads;
        float startCoinTimer;
        const float StartCoinDuration = 1.65f;

        // Moneda secundaria cuando un ataque o estado lanza moneda
        float subCoinTimer;
        bool subCoinHeads;
        string subCoinLabel = "";

        // --- Cola de Animaciones de Movimiento de Cartas sobre el Playmat ---
        class CardFlight
        {
            public CardDef Card;
            public bool FaceDown;
            public Rect From;
            public Rect To;
            public float Duration = 0.42f;
            public float Elapsed;
            public string Caption;
        }
        readonly Queue<CardFlight> flightQueue = new Queue<CardFlight>();
        CardFlight currentFlight;
        float aiTurnDelay;

        // Geometría actual del tapete en pantalla (calculada cada frame en OnGUI)
        Rect playmatRect;
        float slotW, slotH;

        // --- Overlay uGUI + TextMeshProUGUI con la fuente nativa del juego (FredokaOne-Regular SDF) ---
        GameObject nativeOverlayRoot;
        RectTransform dynamicPoolRoot;
        readonly List<Image> nativeImgPool = new List<Image>();
        readonly List<RawImage> nativeRawPool = new List<RawImage>();
        readonly List<TextMeshProUGUI> nativeTmpPool = new List<TextMeshProUGUI>();
        int usedNativeImg;
        int usedNativeRaw;
        int usedNativeTmp;
        int nativeSiblingCursor;
        bool isNativeRepaintPass;

        GameObject waitingGroup;
        RectTransform waitingTitleRt;
        TextMeshProUGUI waitingTitleTmp;
        RectTransform waitingBtnRt;
        Image waitingBtnImg;
        TextMeshProUGUI waitingBtnTmp;

        GameObject gameOverGroup;
        RectTransform gameOverTitleRt;
        TextMeshProUGUI gameOverTitleTmp;
        RectTransform gameOverBoxRt;
        RectTransform gameOverSubBoxRt;
        RectTransform gameOverHeaderRt;
        TextMeshProUGUI gameOverHeaderTmp;
        RectTransform gameOverBodyRt;
        TextMeshProUGUI gameOverBodyTmp;
        RectTransform rematchBtnRt;
        Image rematchBtnImg;
        TextMeshProUGUI rematchBtnTmp;
        RectTransform leaveBtnRt;
        Image leaveBtnImg;
        TextMeshProUGUI leaveBtnTmp;
        bool nativeFontSynced;

        public static Sprite sPanelSprite, sSubPanelSprite, sGlowSprite, sDarkBackdropSprite,
                             sBtnBlueN, sBtnBlueH, sBtnBlueA,
                             sBtnGreenN, sBtnGreenH, sBtnGreenA,
                             sBtnGoldN, sBtnGoldH, sBtnGoldA,
                             sBtnRedN, sBtnRedH, sBtnRedA;

        PlayerState Me => game.Players[0];
        PlayerState Cpu => game.Players[1];

        bool IsAnimBusy() =>
            currentFlight != null ||
            flightQueue.Count > 0 ||
            (BoardAnimator.Instance != null && BoardAnimator.Instance.IsAnimating) ||
            (game != null && game.IsBusyResolving);

        void Awake()
        {
            Instance = this;
        }

        public void ShowToast(string text, float seconds = 3.5f)
        {
            toastMessage = text;
            toastTimer = seconds;
        }

        public void StartTableMatch(Customer opponent)
        {
            try
            {
                SpriteCache.EnsureInitialized();
                SpriteCache.PreloadFromEpl();

                var active = DeckStore.Active;
                if (active == null) throw new Exception("No tienes un mazo. Créalo en la Mesa de Creación (Workbench).");
                var format = FormatRegistry.Get(active.Format) ?? FormatRegistry.Default;
                var missing = new List<string>();
                List<CardDef> playerDeck = active.Resolve(missing);
                var errors = DeckValidator.Validate(playerDeck, format);
                if (missing.Count > 0) errors.Insert(0, $"Cartas desconocidas: {string.Join(", ", missing)}");
                if (errors.Count > 0) throw new Exception($"Tu mazo '{active.Name}' no es válido: {errors[0]}");

                // El mazo del NPC se obtiene según la opción configurada (Mazos Temáticos Oficiales de Base Era o Aleatorio)
                List<CardDef> npcDeck = DeckLoader.GenerateNpcDeck(null, format, out string npcDeckLabel);

                flightQueue.Clear();
                currentFlight = null;
                BoardAnimator.Instance?.Clear();
                TableView.Instance?.ResetStatusTracking();
                StatusCounters3D.ActivePopups.Clear();
                game = new Game(playerDeck, npcDeck);
                game.Players[1].Name = "Cliente";
                game.Say($"El cliente juega con el mazo: {npcDeckLabel}.");
                game.OnAnim = OnGameAnimEvent;

                selHand = -1;
                retreatMode = false;
                showSettingsModal = false;
                if (waitingGroup != null) waitingGroup.SetActive(false);
                if (gameOverGroup != null) gameOverGroup.SetActive(false);
                Reset3DInputState();
                zoomedCard = null;
                zoomedPip = null;
                zoomAnim = 0f;
                zoomClosing = false;

                startCoinSpinning = false;
                startCoinDone = false;
                startCoinTimer = 0f;
                subCoinTimer = 0f;

                open = true;
                message = "Lanzamiento de moneda: elige Cara (Pikachu) o Cruz (Pokéball) para decidir quién inicia.";
            }
            catch (Exception e)
            {
                message = e.Message;
                ShowToast(e.Message, 6f);
                Plugin.Log.LogError(e);
                LeaveTable();
            }
        }

        public void CloseFromTableStop()
        {
            open = false;
            game = null;
            showSettingsModal = false;
            if (waitingGroup != null) waitingGroup.SetActive(false);
            if (gameOverGroup != null) gameOverGroup.SetActive(false);
            if (nativeOverlayRoot != null) nativeOverlayRoot.SetActive(false);
            ClearNativeDynamicPool();
            Reset3DInputState();
            zoomedCard = null;
            zoomedPip = null;
            flightQueue.Clear();
            currentFlight = null;
            BoardAnimator.Instance?.Clear();
            TableView.Instance?.ResetStatusTracking();
            StatusCounters3D.ActivePopups.Clear();
        }

        void LeaveTable()
        {
            bool finished = game != null && game.Phase == Phase.GameOver;
            bool won = finished && game.Winner == Me;
            open = false;
            game = null;
            showSettingsModal = false;
            if (waitingGroup != null) waitingGroup.SetActive(false);
            if (gameOverGroup != null) gameOverGroup.SetActive(false);
            if (nativeOverlayRoot != null) nativeOverlayRoot.SetActive(false);
            ClearNativeDynamicPool();
            Reset3DInputState();
            zoomedCard = null;
            zoomedPip = null;
            flightQueue.Clear();
            currentFlight = null;
            BoardAnimator.Instance?.Clear();
            TableView.Instance?.ResetStatusTracking();
            StatusCounters3D.ActivePopups.Clear();
            PlayTableSession.StandUp(finished, won);
        }

        void Reset3DInputState()
        {
            hoveredHandIdx = -1;
            pressedHandIdx = -1;
            isDragging3D = false;
            hoveredSlot3D = null;
            hoveredBoardCard3D = null;
            validTargetAnchors.Clear();
        }

        void StartZoom(CardDef card, PokemonInPlay pip, Rect screenRect, Vector3 worldPos = default, Quaternion worldRot = default, float worldScale = 1f)
        {
            if (card == null) return;
            zoomedCard = card;
            zoomedPip = pip;
            zoomOriginRect = screenRect;
            zoomOriginWorldPos = worldPos;
            zoomOriginWorldRot = worldRot;
            zoomOriginScale = worldScale;
            zoomAnim = 0f;
            zoomClosing = false;
        }

        void Update()
        {
            if (toastTimer > 0f) toastTimer -= Time.deltaTime;
            if (subCoinTimer > 0f) subCoinTimer -= Time.deltaTime;

            if (PlayTableSession.IsWaitingForNpc)
            {
                PlayTableSession.EnforcePlayerCameraAndMovementLock();
                PlayTableSession.UpdateWaiting();
                if (Input.GetKeyDown(KeyCode.Escape) || Input.GetMouseButtonDown(1))
                    LeaveTable();
                return;
            }

            if (waitingGroup != null && waitingGroup.activeSelf)
                waitingGroup.SetActive(false);

            if (!open || game == null)
            {
                if (gameOverGroup != null && gameOverGroup.activeSelf)
                    gameOverGroup.SetActive(false);
                ClearNativeDynamicPool();
                if (PlayTableSession.CurrentTable != null)
                    LeaveTable();
                return;
            }

            if ((game.Phase != Phase.GameOver || IsAnimBusy()) && gameOverGroup != null && gameOverGroup.activeSelf)
            {
                gameOverGroup.SetActive(false);
            }

            PlayTableSession.EnforcePlayerCameraAndMovementLock();

            ComputePlaymatGeometry();

            // F4: Procesar Raycast 3D de Mano y Mesa (Hover, Selección, Drag & Drop 3D y Clic Derecho de Inspección)
            if (TableView.Instance != null && TableView.Instance.IsBoard3DReady)
            {
                Update3DInput();
            }

            // F2–F7: Sincronizar cartas 3D, Mano 3D, Carta arrastrada, Inspección 3D, Fichas de Daño y Moneda 3D
            bool overValidSlot = hoveredSlot3D != null && validTargetAnchors.Contains(hoveredSlot3D.Anchor);
            TableView.Instance?.SyncBoard3D(
                game,
                zoomedCard,
                zoomedPip,
                zoomAnim,
                retreatMode,
                hoveredHandIdx,
                selHand,
                isDragging3D ? pressedHandIdx : -1,
                currentDragWorldPos,
                overValidSlot,
                zoomOriginWorldPos,
                zoomOriginWorldRot,
                zoomOriginScale,
                startCoinSpinning);

            // Actualizar animación de Inspección 3D elevada (F5)
            if (zoomedCard != null)
            {
                float target = zoomClosing ? 0f : 1f;
                zoomAnim = Mathf.MoveTowards(zoomAnim, target, Time.deltaTime * 5.2f);
                if (zoomClosing && zoomAnim <= 0.01f)
                {
                    zoomedCard = null;
                    zoomedPip = null;
                    zoomClosing = false;
                }
            }

            // Actualizar animación de moneda inicial
            if (startCoinSpinning)
            {
                startCoinTimer += Time.deltaTime * Loc.AnimSpeed;
                // Solo cuando la moneda ya cayó sobre la mesa y mostró su resultado (t >= 0.88),
                // pasamos a Fase de Preparación, repartimos la mano inicial y, si el NPC ganó, prepara su jugada tras el reparto.
                if (startCoinTimer >= StartCoinDuration * 0.88f && !startCoinDone)
                {
                    startCoinDone = true;
                    game.OnStartCoinLanded(startCoinResultHeads);
                    if (!string.IsNullOrEmpty(game.MulliganSummary))
                        ShowToast("Mulligan: " + game.MulliganSummary, 5.5f);
                    message = Loc.T(
                        "Coloca tu primer Pokémon Básico en el Campo de Batalla para comenzar la partida.",
                        "Place your first Basic Pokémon in the Active spot to begin the match.");
                }
                if (startCoinTimer >= StartCoinDuration + 0.85f)
                {
                    startCoinSpinning = false;
                }
            }

            // Actualizar vuelo de cartas sobre el tapete
            if (currentFlight != null)
            {
                currentFlight.Elapsed += Time.deltaTime * Loc.AnimSpeed;
                if (currentFlight.Elapsed >= currentFlight.Duration)
                    currentFlight = null;
            }
            if (currentFlight == null && flightQueue.Count > 0)
            {
                currentFlight = flightQueue.Dequeue();
            }

            // F8: Aplicar recompensas de tienda cuando termina la última animación de la partida
            if (game.Phase == Phase.GameOver && !game.RewardsApplied && !IsAnimBusy())
            {
                PlayTableSession.ApplyMatchOutcomeRewards(game);
                TcgAudio.PlayMatchResult(game.Winner == Me);
            }

            // Ejecutar colocación inicial o turno de la IA cuando terminen las animaciones 3D y 2D en curso
            bool flightsBusy = currentFlight != null || flightQueue.Count > 0 || (BoardAnimator.Instance != null && BoardAnimator.Instance.IsAnimating);
            if (game.PendingAiSetup && !flightsBusy && !startCoinSpinning)
            {
                aiSetupDelay += Time.deltaTime * Loc.AnimSpeed;
                if (aiSetupDelay >= 0.30f)
                {
                    aiSetupDelay = 0f;
                    game.ExecutePendingAiSetup();
                }
            }
            if (game.PendingAiTurn && !IsAnimBusy() && !startCoinSpinning)
            {
                aiTurnDelay += Time.deltaTime * Loc.AnimSpeed;
                if (aiTurnDelay >= 0.55f)
                {
                    aiTurnDelay = 0f;
                    game.ExecutePendingAiTurn();
                }
            }

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                if (showSettingsModal) showSettingsModal = false;
                else if (zoomedCard != null) zoomClosing = true;
            }
        }

        // ==================== Posiciones de las Zonas en el Tapete 3D (F1: TableView) ====================
        void ComputePlaymatGeometry()
        {
            var tv = TableView.Instance;
            if (tv != null && tv.TryGetScreenPlaymatRect(out Rect projectedMat))
            {
                playmatRect = projectedMat;
                slotH = Mathf.Round(playmatRect.height * 0.165f);
                slotW = Mathf.Round(slotH * (63f / 88f));
                return;
            }

            // Respaldo 2D si la cámara aún está haciendo transición
            float availH = Screen.height - 135f;
            float availW = Screen.width - 60f;
            float matSize = Mathf.Min(availH, availW * 0.86f);
            float matW = matSize * 0.97f;
            float matH = matSize;
            playmatRect = new Rect((Screen.width - matW) * 0.5f, 36f, matW, matH);

            slotH = Mathf.Round(matH * 0.165f);
            slotW = Mathf.Round(slotH * (63f / 88f));
        }

        Rect NormSlot(float nx, float ny, float scale = 1f)
        {
            float w = slotW * scale;
            float h = slotH * scale;
            float cx = playmatRect.x + nx * playmatRect.width;
            float cy = playmatRect.y + ny * playmatRect.height;
            return new Rect(cx - w * 0.5f, cy - h * 0.5f, w, h);
        }

        Rect GetActiveRect(int playerIdx)
        {
            var tv = TableView.Instance;
            if (tv != null && tv.TryProjectSlotToGUI(tv.ActiveAnchor[playerIdx], 1.08f, slotW, slotH, out Rect r))
                return r;
            return playerIdx == 0 ? NormSlot(0.50f, 0.61f, 1.08f) : NormSlot(0.50f, 0.39f, 1.08f);
        }

        Rect GetBenchRect(int playerIdx, int benchIdx)
        {
            int idx = Mathf.Clamp(benchIdx, 0, 4);
            var tv = TableView.Instance;
            if (tv != null && tv.TryProjectSlotToGUI(tv.BenchAnchor[playerIdx, idx], 0.94f, slotW, slotH, out Rect r))
                return r;
            float[] xs = { 0.27f, 0.385f, 0.50f, 0.615f, 0.73f };
            float ny = playerIdx == 0 ? 0.80f : 0.135f;
            return NormSlot(xs[idx], ny, 0.94f);
        }

        Rect GetDeckRect(int playerIdx)
        {
            var tv = TableView.Instance;
            if (tv != null && tv.TryProjectSlotToGUI(tv.DeckAnchor[playerIdx], 1f, slotW, slotH, out Rect r))
                return r;
            return playerIdx == 0 ? NormSlot(0.913f, 0.640f) : NormSlot(0.087f, 0.360f);
        }

        Rect GetTrashRect(int playerIdx)
        {
            var tv = TableView.Instance;
            if (tv != null && tv.TryProjectSlotToGUI(tv.DiscardAnchor[playerIdx], 1f, slotW, slotH, out Rect r))
                return r;
            return playerIdx == 0 ? NormSlot(0.913f, 0.865f) : NormSlot(0.087f, 0.135f);
        }

        Rect GetSideBoxRect(int playerIdx, int boxIdx) // boxIdx = 0, 1, 2
        {
            int b = Mathf.Clamp(boxIdx, 0, 2);
            var tv = TableView.Instance;
            if (tv != null && tv.TryProjectSlotToGUI(tv.PrizeBoxAnchor[playerIdx, b], 0.88f, slotW, slotH, out Rect r))
                return r;
            if (playerIdx == 0)
            {
                float[] ys = { 0.584f, 0.742f, 0.900f };
                return NormSlot(0.092f, ys[b], 0.88f);
            }
            else
            {
                float[] ys = { 0.100f, 0.258f, 0.416f };
                return NormSlot(0.908f, ys[b], 0.88f);
            }
        }
        Rect GetHandCenterRect(int playerIdx) =>
            playerIdx == 0
                ? new Rect(Screen.width * 0.5f - slotW * 0.5f, Screen.height - slotH - 8f, slotW, slotH)
                : new Rect(Screen.width * 0.5f - slotW * 0.4f, 6f, slotW * 0.8f, slotH * 0.8f);

        void OnGameAnimEvent(AnimEvent ev)
        {
            // F3 & F7: Si el tablero 3D y el BoardAnimator están activos, todas las cartas y monedas 3D se encolan en la mesa física
            if (TableView.Instance != null && TableView.Instance.IsBoard3DReady && BoardAnimator.Instance != null)
            {
                BoardAnimator.Instance.EnqueueEvent(ev, game);
                return;
            }

            if (ev.Kind == AnimKind.CoinFlip)
            {
                subCoinHeads = ev.CoinHeads;
                subCoinLabel = $"{ev.Label}: {(ev.CoinHeads ? "CARA" : "CRUZ")}";
                subCoinTimer = 1.6f;
                return;
            }

            ComputePlaymatGeometry();
            int p = ev.PlayerIndex;
            switch (ev.Kind)
            {
                case AnimKind.Draw:
                    flightQueue.Enqueue(new CardFlight
                    {
                        Card = ev.Card,
                        FaceDown = p == 1, // Las cartas robadas por el NPC van boca abajo
                        From = GetDeckRect(p),
                        To = GetHandCenterRect(p),
                        Duration = 0.36f,
                        Caption = p == 0 ? $"Robas {ev.Card?.Name}" : "Cliente roba carta"
                    });
                    break;

                case AnimKind.ToActive:
                    flightQueue.Enqueue(new CardFlight
                    {
                        Card = ev.Card,
                        From = ev.BenchIndex >= 0 ? GetBenchRect(p, ev.BenchIndex) : GetHandCenterRect(p),
                        To = GetActiveRect(p),
                        Duration = 0.42f,
                        Caption = $"{ev.Card?.Name} al Campo de Batalla"
                    });
                    break;

                case AnimKind.ToBench:
                    flightQueue.Enqueue(new CardFlight
                    {
                        Card = ev.Card,
                        From = GetHandCenterRect(p),
                        To = GetBenchRect(p, ev.BenchIndex),
                        Duration = 0.40f,
                        Caption = $"{ev.Card?.Name} a la Banca"
                    });
                    break;

                case AnimKind.Evolve:
                    flightQueue.Enqueue(new CardFlight
                    {
                        Card = ev.Card,
                        From = GetHandCenterRect(p),
                        To = ev.BenchIndex < 0 ? GetActiveRect(p) : GetBenchRect(p, ev.BenchIndex),
                        Duration = 0.46f,
                        Caption = $"¡Evoluciona a {ev.Card?.Name}!"
                    });
                    break;

                case AnimKind.AttachEnergy:
                    {
                        Rect dest = ev.BenchIndex < 0 ? GetActiveRect(p) : GetBenchRect(p, ev.BenchIndex);
                        dest.x -= 12f; dest.y += 14f;
                        flightQueue.Enqueue(new CardFlight
                        {
                            Card = ev.Card,
                            From = GetHandCenterRect(p),
                            To = dest,
                            Duration = 0.38f,
                            Caption = $"Une {ev.Card?.Name}"
                        });
                        break;
                    }

                case AnimKind.PlayTrainer:
                    flightQueue.Enqueue(new CardFlight
                    {
                        Card = ev.Card,
                        From = GetHandCenterRect(p),
                        To = GetTrashRect(p),
                        Duration = 0.48f,
                        Caption = $"Entrenador: {ev.Card?.Name}"
                    });
                    break;

                case AnimKind.RetreatSwap:
                    flightQueue.Enqueue(new CardFlight
                    {
                        Card = ev.Card,
                        From = GetBenchRect(p, ev.BenchIndex),
                        To = GetActiveRect(p),
                        Duration = 0.42f,
                        Caption = $"Retirada: entra {ev.Card?.Name}"
                    });
                    break;

                case AnimKind.AttackLunge:
                    {
                        Rect from = GetActiveRect(p);
                        Rect opp = GetActiveRect(1 - p);
                        Rect mid = new Rect(Mathf.Lerp(from.x, opp.x, 0.55f), Mathf.Lerp(from.y, opp.y, 0.55f), from.width, from.height);
                        flightQueue.Enqueue(new CardFlight
                        {
                            Card = ev.Card,
                            From = from,
                            To = mid,
                            Duration = 0.26f,
                            Caption = $"¡{ev.Card?.Name} usa {ev.Label}!"
                        });
                        flightQueue.Enqueue(new CardFlight
                        {
                            Card = ev.Card,
                            From = mid,
                            To = from,
                            Duration = 0.22f
                        });
                        break;
                    }

                case AnimKind.KnockOut:
                    flightQueue.Enqueue(new CardFlight
                    {
                        Card = ev.Card,
                        From = ev.BenchIndex < 0 ? GetActiveRect(p) : GetBenchRect(p, ev.BenchIndex),
                        To = GetTrashRect(p),
                        Duration = 0.48f,
                        Caption = $"¡{ev.Card?.Name} Fuera de Combate!"
                    });
                    break;

                case AnimKind.TakePrize:
                    flightQueue.Enqueue(new CardFlight
                    {
                        Card = ev.Card,
                        FaceDown = p == 1,
                        From = GetSideBoxRect(p, Mathf.Clamp(ev.BenchIndex / 2, 0, 2)),
                        To = GetHandCenterRect(p),
                        Duration = 0.45f,
                        Caption = $"{(p == 0 ? "Tomas" : "Cliente toma")} 1 Premio"
                    });
                    break;

                case AnimKind.CoinFlip:
                    subCoinHeads = ev.CoinHeads;
                    subCoinLabel = $"{ev.Label}: {(ev.CoinHeads ? "CARA" : "CRUZ")}";
                    subCoinTimer = 1.6f;
                    break;
            }
        }

        // ==================== Estilos Oficiales (Estilo TCG Card Shop Simulator) ====================
        public static GUIStyle BoxBgStyle, SubBoxStyle, InputFieldStyle, HeaderStyle, SubHeaderStyle, CardLabelStyle, ZoomTextStyle,
                               BtnStyle, BtnGreenStyle, BtnGoldStyle, BtnRedStyle, ToastStyle, BadgeStyle, DurationBadgeStyle, FloatingPopupStyle;
        static Texture2D darkTex, panelTex, subPanelTex, inputTex, highlightTex, zoomBgTex, hpBgTex, hpFillTex, hpMidTex, hpLowTex, sleepTex, confTex, paraTex, poisTex, defTex, plusTex, countTex;
        static Texture2D btnBlueN, btnBlueH, btnBlueA, btnGreenN, btnGreenH, btnGreenA, btnGoldN, btnGoldH, btnGoldA, btnRedN, btnRedH, btnRedA, btnDis;

        public static void PlayClickSound(int kind = 0)
        {
            try
            {
                if (kind == 1) SoundManager.GenericConfirm(1f, 1f);
                else if (kind == 2) SoundManager.GenericCancel(1f, 1f);
                else SoundManager.GenericLightTap(1f, 1f);
            }
            catch { }
        }

        public static void EnsureStyles()
        {
            if (darkTex == null)
            {
                // Ventanas y paneles con esquinas redondeadas, borde azul-acero y sombra estilo oficial del juego
                darkTex = MakeRoundedBoxTex(64, 64, 10f,
                    new Color(0.10f, 0.15f, 0.23f, 0.97f),
                    new Color(0.07f, 0.11f, 0.18f, 0.97f),
                    new Color(0.28f, 0.43f, 0.65f, 0.98f),
                    new Color(0.03f, 0.05f, 0.09f, 0.98f), 4);
                panelTex = darkTex;
                subPanelTex = MakeRoundedBoxTex(48, 48, 8f,
                    new Color(0.14f, 0.20f, 0.30f, 0.94f),
                    new Color(0.11f, 0.16f, 0.25f, 0.94f),
                    new Color(0.23f, 0.35f, 0.54f, 0.95f),
                    new Color(0.05f, 0.08f, 0.14f, 0.95f), 2);
                inputTex = MakeRoundedBoxTex(48, 48, 7f,
                    new Color(0.07f, 0.10f, 0.16f, 0.98f),
                    new Color(0.09f, 0.13f, 0.20f, 0.98f),
                    new Color(0.32f, 0.50f, 0.76f, 0.98f),
                    new Color(0.03f, 0.05f, 0.09f, 0.98f), 1);

                // Botón Azul (Estándar del juego)
                btnBlueN = MakeGameButtonTex(new Color(0.21f, 0.55f, 0.94f), new Color(0.14f, 0.43f, 0.82f), new Color(0.48f, 0.76f, 1f), new Color(0.08f, 0.24f, 0.46f), false);
                btnBlueH = MakeGameButtonTex(new Color(0.30f, 0.64f, 1.00f), new Color(0.20f, 0.51f, 0.90f), new Color(0.66f, 0.86f, 1f), new Color(0.10f, 0.29f, 0.54f), false);
                btnBlueA = MakeGameButtonTex(new Color(0.13f, 0.39f, 0.74f), new Color(0.16f, 0.45f, 0.82f), new Color(0.35f, 0.62f, 0.92f), new Color(0.06f, 0.18f, 0.36f), true);

                // Botón Verde (Confirmar / Guardar / Iniciar)
                btnGreenN = MakeGameButtonTex(new Color(0.18f, 0.73f, 0.41f), new Color(0.12f, 0.58f, 0.31f), new Color(0.46f, 0.92f, 0.64f), new Color(0.07f, 0.33f, 0.18f), false);
                btnGreenH = MakeGameButtonTex(new Color(0.24f, 0.82f, 0.48f), new Color(0.16f, 0.67f, 0.37f), new Color(0.62f, 0.97f, 0.75f), new Color(0.09f, 0.39f, 0.21f), false);
                btnGreenA = MakeGameButtonTex(new Color(0.11f, 0.52f, 0.28f), new Color(0.14f, 0.60f, 0.33f), new Color(0.35f, 0.78f, 0.52f), new Color(0.05f, 0.24f, 0.13f), true);

                // Botón Dorado/Naranja (Acción principal / Ataque / Destacado)
                btnGoldN = MakeGameButtonTex(new Color(0.95f, 0.61f, 0.14f), new Color(0.84f, 0.46f, 0.08f), new Color(1.00f, 0.84f, 0.48f), new Color(0.48f, 0.25f, 0.04f), false);
                btnGoldH = MakeGameButtonTex(new Color(1.00f, 0.69f, 0.22f), new Color(0.92f, 0.54f, 0.12f), new Color(1.00f, 0.90f, 0.62f), new Color(0.55f, 0.30f, 0.05f), false);
                btnGoldA = MakeGameButtonTex(new Color(0.76f, 0.42f, 0.07f), new Color(0.84f, 0.48f, 0.10f), new Color(0.92f, 0.72f, 0.36f), new Color(0.36f, 0.18f, 0.03f), true);

                // Botón Rojo (Cerrar / Levantarse / Borrar)
                btnRedN = MakeGameButtonTex(new Color(0.86f, 0.29f, 0.29f), new Color(0.72f, 0.19f, 0.19f), new Color(1.00f, 0.58f, 0.58f), new Color(0.42f, 0.10f, 0.10f), false);
                btnRedH = MakeGameButtonTex(new Color(0.94f, 0.37f, 0.37f), new Color(0.80f, 0.24f, 0.24f), new Color(1.00f, 0.70f, 0.70f), new Color(0.48f, 0.12f, 0.12f), false);
                btnRedA = MakeGameButtonTex(new Color(0.65f, 0.17f, 0.17f), new Color(0.72f, 0.21f, 0.21f), new Color(0.88f, 0.46f, 0.46f), new Color(0.32f, 0.07f, 0.07f), true);

                btnDis = MakeGameButtonTex(new Color(0.22f, 0.27f, 0.35f), new Color(0.18f, 0.22f, 0.29f), new Color(0.32f, 0.38f, 0.48f), new Color(0.10f, 0.13f, 0.18f), false);

                highlightTex = MakeTex(new Color(0.98f, 0.82f, 0.20f, 0.95f));
                zoomBgTex = MakeTex(new Color(0.02f, 0.03f, 0.06f, 0.84f));
                hpBgTex = MakeTex(new Color(0.15f, 0.05f, 0.05f, 0.90f));
                hpFillTex = MakeTex(new Color(0.18f, 0.85f, 0.38f, 0.95f));
                hpMidTex = MakeTex(new Color(0.95f, 0.74f, 0.14f, 0.96f));
                hpLowTex = MakeTex(new Color(0.92f, 0.22f, 0.20f, 0.96f));
                sleepTex = MakeTex(new Color(0.20f, 0.48f, 0.92f, 0.95f));
                confTex = MakeTex(new Color(0.92f, 0.55f, 0.12f, 0.95f));
                paraTex = MakeTex(new Color(0.90f, 0.76f, 0.10f, 0.95f));
                poisTex = MakeTex(new Color(0.66f, 0.20f, 0.85f, 0.95f));
                defTex = MakeTex(new Color(0.15f, 0.72f, 0.85f, 0.95f));
                plusTex = MakeTex(new Color(0.92f, 0.24f, 0.28f, 0.96f));
                countTex = MakeTex(new Color(0.92f, 0.16f, 0.16f, 0.98f));
            }
            if (BoxBgStyle == null)
            {
                BoxBgStyle = new GUIStyle(GUI.skin.box)
                {
                    border = new RectOffset(12, 12, 12, 14),
                    padding = new RectOffset(10, 10, 8, 10),
                    normal = { background = darkTex, textColor = Color.white }
                };
                SubBoxStyle = new GUIStyle(GUI.skin.box)
                {
                    border = new RectOffset(10, 10, 10, 10),
                    padding = new RectOffset(8, 8, 6, 6),
                    normal = { background = subPanelTex, textColor = Color.white }
                };
                InputFieldStyle = new GUIStyle(GUI.skin.textField)
                {
                    border = new RectOffset(8, 8, 8, 8),
                    padding = new RectOffset(10, 10, 5, 5),
                    fontSize = 14,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleLeft,
                    normal = { background = inputTex, textColor = Color.white },
                    focused = { background = inputTex, textColor = new Color(1f, 0.92f, 0.55f) },
                    hover = { background = inputTex, textColor = Color.white }
                };
                HeaderStyle = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 17, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
                SubHeaderStyle = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 13, normal = { textColor = new Color(0.90f, 0.95f, 1f) } };
                CardLabelStyle = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 11, alignment = TextAnchor.UpperCenter, wordWrap = true, normal = { textColor = Color.white } };
                ZoomTextStyle = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 15, wordWrap = true, normal = { textColor = Color.white } };

                BtnStyle = MakeButtonStyle(btnBlueN, btnBlueH, btnBlueA);
                BtnGreenStyle = MakeButtonStyle(btnGreenN, btnGreenH, btnGreenA);
                BtnGoldStyle = MakeButtonStyle(btnGoldN, btnGoldH, btnGoldA);
                BtnRedStyle = MakeButtonStyle(btnRedN, btnRedH, btnRedA);

                ToastStyle = new GUIStyle(BoxBgStyle) { fontSize = 16, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white, background = darkTex } };
                BadgeStyle = new GUIStyle(GUI.skin.box) { fontSize = 10, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
                DurationBadgeStyle = new GUIStyle(GUI.skin.box) { fontSize = 10, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white, background = countTex } };
                FloatingPopupStyle = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, richText = true, normal = { textColor = Color.white } };
            }
        }

        static GUIStyle MakeButtonStyle(Texture2D normal, Texture2D hover, Texture2D active)
        {
            return new GUIStyle(GUI.skin.button)
            {
                border = new RectOffset(10, 10, 10, 12),
                padding = new RectOffset(12, 12, 5, 7),
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                richText = true,
                normal = { background = normal, textColor = Color.white },
                hover = { background = hover, textColor = new Color(1f, 0.98f, 0.88f) },
                active = { background = active, textColor = new Color(0.92f, 0.95f, 1f) },
                focused = { background = hover, textColor = Color.white }
            };
        }

        static Texture2D MakeGameButtonTex(Color topCol, Color botCol, Color rimCol, Color lipCol, bool pressed)
        {
            return MakeRoundedBoxTex(48, 48, 8f, topCol, botCol, rimCol, lipCol, pressed ? 2 : 5);
        }

        /// <summary>
        /// Genera una textura 9-slice con esquinas redondeadas, gradiente vertical, borde brillante y bisel inferior 3D
        /// idéntico al estilo de botones y paneles de TCG Card Shop Simulator.
        /// </summary>
        static Texture2D MakeRoundedBoxTex(int w, int h, float radius, Color topCol, Color botCol, Color borderCol, Color bottomLipCol, int lipHeight)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            Color outlineCol = new Color(0.03f, 0.05f, 0.09f, 0.96f);

            for (int y = 0; y < h; y++)
            {
                float vt = y / (float)(h - 1);
                for (int x = 0; x < w; x++)
                {
                    float dist = RoundedRectDist(x, y, w, h, radius);
                    if (dist > 0.7f)
                    {
                        tex.SetPixel(x, y, Color.clear);
                        continue;
                    }
                    float alpha = Mathf.Clamp01(0.7f - dist);
                    Color c;
                    if (dist > -1.4f)
                        c = outlineCol;
                    else if (y < lipHeight + 1)
                        c = bottomLipCol;
                    else if (dist > -3.1f)
                        c = borderCol;
                    else
                    {
                        c = Color.Lerp(botCol, topCol, vt);
                        if (y >= h - 4) c = Color.Lerp(c, borderCol, 0.35f);
                    }
                    c.a *= alpha;
                    tex.SetPixel(x, y, c);
                }
            }
            tex.Apply();
            return tex;
        }

        static float RoundedRectDist(float x, float y, int w, int h, float r)
        {
            float cx = Mathf.Clamp(x, r, w - 1 - r);
            float cy = Mathf.Clamp(y, r, h - 1 - r);
            return Vector2.Distance(new Vector2(x, y), new Vector2(cx, cy)) - r;
        }

        static Texture2D MakeTex(Color col)
        {
            var t = new Texture2D(2, 2);
            t.SetPixels(new[] { col, col, col, col });
            t.Apply();
            return t;
        }

        // ==================== Renderizado Principal ====================
        public bool IsMatchUiActive => open && game != null;

        void OnGUI()
        {
            EnsureStyles();
            GUI.depth = -1000;

            if (toastTimer > 0f && !string.IsNullOrEmpty(toastMessage))
            {
                GUI.Box(new Rect((Screen.width - 520) * 0.5f, 40, 520, 48), toastMessage, ToastStyle);
            }

            if (!PlayTableSession.IsWaitingForNpc && (!open || game == null))
            {
                if (waitingGroup != null && waitingGroup.activeSelf)
                    waitingGroup.SetActive(false);
                if (gameOverGroup != null && gameOverGroup.activeSelf)
                    gameOverGroup.SetActive(false);
                if (nativeOverlayRoot != null && nativeOverlayRoot.activeSelf)
                    nativeOverlayRoot.SetActive(false);
                return;
            }

            BeginNativeRepaint();
            try
            {
                if (PlayTableSession.IsWaitingForNpc)
                {
                    if (gameOverGroup != null && gameOverGroup.activeSelf)
                        gameOverGroup.SetActive(false);
                    DrawWaitingForOpponentScreen();
                    return;
                }

                if (waitingGroup != null && waitingGroup.activeSelf)
                    waitingGroup.SetActive(false);

                ComputePlaymatGeometry();
                Update3DSlotHighlights();

                // 1. Tapete 3D sobre la mesa (solo dibuja respaldo 2D si TableView aún no está activo)
                DrawPlaymatSurface();

                // 9. Pantalla de Fin de Partida (se muestra tras concluir las animaciones finales de KO/Premio)
                if (game.Phase == Phase.GameOver && !IsAnimBusy())
                {
                    DrawGameOverModal();
                    return;
                }

                if (gameOverGroup != null && gameOverGroup.activeSelf)
                    gameOverGroup.SetActive(false);

                // 2. Botones de "Opciones" y "Levantarse" a la izquierda de la pantalla con la fuente del juego (sin cuadro superior ni registro)
                DrawLeftSideButtons();
                if (!open || game == null) return;

                // 11. F10: Modal de Configuración en Vivo con la fuente del juego
                if (showSettingsModal)
                {
                    PolishEffects3D.DrawSettingsModal(ref showSettingsModal);
                    return;
                }

                // 3. Dibujar todas las zonas alineadas a los anclajes 3D del Playmat
                DrawAllPlaymatZones();

                // 4. Mano del NPC (arriba boca abajo) y Mano del Jugador (abajo)
                DrawOpponentHand();
                DrawPlayerHand();

                // 5. Controles de Acción (Ataques, Retirada, Fin de turno) a la derecha del tapete
                DrawSideControls();
                if (!open || game == null) return;

                // 6. Vuelo animado de cartas (movimientos propios y del NPC) + Textos flotantes 3D de daño/estado (F6)
                DrawCurrentFlight();
                DrawFloatingPopups3D();

                // 7. Moneda secundaria (ataques / estados)
                if (subCoinTimer > 0f)
                    DrawSubCoinFlip();

                // 8. Fase de Moneda Inicial antes de iniciar la partida (se oculta el modal mientras gira la moneda)
                if (game.Phase == Phase.CoinToss && !startCoinSpinning)
                    DrawInitialCoinTossModal();

                // 10. Zoom con Clic Derecho (la carta se eleva desde la mesa y regresa a ella)
                if (zoomedCard != null)
                {
                    if (DrawElevatedZoomOverlay(zoomedCard, zoomedPip, zoomOriginRect, zoomAnim))
                        zoomClosing = true;
                }
            }
            finally
            {
                EndNativeRepaint();
            }
        }

        void Update3DSlotHighlights()
        {
            var tv = TableView.Instance;
            if (tv == null || game == null) return;
            int activeCardIdx = isDragging3D ? pressedHandIdx : (selHand >= 0 ? selHand : hoveredHandIdx);
            ComputeValidTargetAnchors(activeCardIdx);
            Transform hoveredAnchor = hoveredSlot3D != null ? hoveredSlot3D.Anchor : null;
            tv.UpdateSlotHighlights3D(validTargetAnchors, hoveredAnchor);
        }

        void DrawPlaymatSurface()
        {
            // F1: El Playmat ya existe como objeto 3D real sobre la mesa física (TableView.PlaymatRoot).
            // Solo dibujamos el tapete en 2D como respaldo si no hay mesa 3D activa.
            if (TableView.Instance != null && TableView.Instance.PlaymatRoot != null)
                return;

            GUI.Box(new Rect(playmatRect.x - 6f, playmatRect.y - 6f, playmatRect.width + 12f, playmatRect.height + 12f), "", BoxBgStyle);
            if (SpriteCache.PlaymatTex != null)
                GUI.DrawTexture(playmatRect, SpriteCache.PlaymatTex, ScaleMode.StretchToFill);
            else
                GUI.Box(playmatRect, "");
        }

        void DrawLeftSideButtons()
        {
            // Botones un poco más grandes a la izquierda de la pantalla con la fuente del juego (sin icono en Opciones)
            float leftX = 24f;
            float btnW = 176f;
            float btnH = 48f;
            Rect optRect = new Rect(leftX, 102f, btnW, btnH);
            Rect standRect = new Rect(leftX, 160f, btnW, btnH);

            if (DrawNativeButton(optRect, Loc.T("Opciones", "Settings"), 21f, sBtnGoldN, sBtnGoldH, sBtnGoldA))
            {
                PlayClickSound(0);
                showSettingsModal = !showSettingsModal;
            }
            if (DrawNativeButton(standRect, Loc.T("Levantarse", "Stand Up"), 21f, sBtnRedN, sBtnRedH, sBtnRedA))
            {
                PlayClickSound(2);
                LeaveTable();
            }
        }

        void DrawAllPlaymatZones()
        {
            float hudVisibility = zoomedCard != null ? Mathf.Clamp01(1f - zoomAnim * 1.45f) : 1f;
            if (hudVisibility <= 0.01f) return;

            Color prevGuiColor = GUI.color;
            GUI.color = new Color(prevGuiColor.r, prevGuiColor.g, prevGuiColor.b, prevGuiColor.a * hudVisibility);

            bool allowClick = zoomedCard == null && game.Phase != Phase.CoinToss && !IsAnimBusy();

            // --- Zonas de Mazo (Deck) y Descarte (Trash) ---
            DrawDeckPile(0, Me.Deck.Count);
            DrawDeckPile(1, Cpu.Deck.Count);
            DrawTrashPile(0, Me.Discard, allowClick);
            DrawTrashPile(1, Cpu.Discard, allowClick);

            // --- Zonas de Premios (Side: 3 casillas por jugador, hasta 2 cartas por casilla) ---
            DrawPrizeBoxes(0, Me.Prizes.Count);
            DrawPrizeBoxes(1, Cpu.Prizes.Count);

            // --- Bancas (5 casillas por jugador) ---
            for (int i = 0; i < 5; i++)
            {
                var myBenchPip = i < Me.Bench.Count ? Me.Bench[i] : null;
                DrawPokemonOnMat(Me, myBenchPip, GetBenchRect(0, i), false, allowClick);

                var cpuBenchPip = i < Cpu.Bench.Count ? Cpu.Bench[i] : null;
                DrawPokemonOnMat(Cpu, cpuBenchPip, GetBenchRect(1, i), false, allowClick);
            }

            // --- Campo de Batalla Central (Pokémon Activos) ---
            DrawPokemonOnMat(Cpu, Cpu.Active, GetActiveRect(1), true, allowClick);
            DrawPokemonOnMat(Me, Me.Active, GetActiveRect(0), true, allowClick);

            GUI.color = prevGuiColor;
        }

        static int DetectSlotMouseClick(Rect rect, bool allowInput)
        {
            if (allowInput && Event.current.type == EventType.MouseDown && rect.Contains(Event.current.mousePosition))
            {
                int b = Event.current.button;
                Event.current.Use();
                return b;
            }
            return -1;
        }

        void DrawDeckPile(int playerIdx, int count)
        {
            Rect r = GetDeckRect(playerIdx);
            bool has3D = TableView.Instance != null && TableView.Instance.IsBoard3DReady;
            if (!has3D)
            {
                if (count > 0)
                {
                    int layers = Mathf.Clamp(count / 15 + 1, 1, 3);
                    for (int i = layers - 1; i >= 0; i--)
                    {
                        DrawCardBackRect(new Rect(r.x - i * 2f, r.y - i * 2f, r.width, r.height));
                    }
                }
                Rect badge2D = new Rect(r.x + 2f, r.yMax - 24f, r.width - 4f, 22f);
                GUI.Box(badge2D, "", SubBoxStyle);
                GUI.Label(badge2D, $"<b>Mazo: {count}</b>", CardLabelStyle);
                return;
            }

            if (hoveredSlot3D != null && hoveredSlot3D.PlayerIndex == playerIdx && hoveredSlot3D.Kind == TableSlotKind.Deck)
            {
                Rect tip = new Rect(r.center.x - 48f, r.y - 26f, 96f, 22f);
                GUI.Box(tip, "", SubBoxStyle);
                GUI.Label(tip, $"<b>Mazo: {count}</b>", CardLabelStyle);
            }
        }

        void DrawTrashPile(int playerIdx, List<CardDef> discard, bool allowClick)
        {
            Rect r = GetTrashRect(playerIdx);
            bool has3D = TableView.Instance != null && TableView.Instance.IsBoard3DReady;
            if (!has3D)
            {
                if (discard.Count > 0)
                {
                    var top = discard[discard.Count - 1];
                    int btn = DrawCardWithInput(r, top, false, allowClick);
                    if (btn == 1) StartZoom(top, null, r);
                }
                Rect badge2D = new Rect(r.x + 2f, r.yMax - 24f, r.width - 4f, 22f);
                GUI.Box(badge2D, "", SubBoxStyle);
                GUI.Label(badge2D, $"<b>Trash: {discard.Count}</b>", CardLabelStyle);
                return;
            }

            bool hovered = (hoveredSlot3D != null && hoveredSlot3D.PlayerIndex == playerIdx && hoveredSlot3D.Kind == TableSlotKind.Discard) ||
                           (hoveredBoardCard3D != null && hoveredBoardCard3D.IsDiscardTop && hoveredBoardCard3D.OwnerIndex == playerIdx);
            if (hovered)
            {
                Rect tip = new Rect(r.center.x - 54f, r.y - 26f, 108f, 22f);
                GUI.Box(tip, "", SubBoxStyle);
                GUI.Label(tip, $"<b>Descarte: {discard.Count}</b>", CardLabelStyle);
            }
        }

        void DrawPrizeBoxes(int playerIdx, int prizeCount)
        {
            // En F2 los premios ya se dibujan físicamente en 3D sobre las casillas Side
            if (TableView.Instance != null && TableView.Instance.IsBoard3DReady)
                return;

            int rem = prizeCount;
            for (int b = 0; b < 3; b++)
            {
                Rect box = GetSideBoxRect(playerIdx, b);
                int inThisBox = Mathf.Clamp(rem, 0, 2);
                rem -= inThisBox;
                for (int k = 0; k < inThisBox; k++)
                {
                    Rect cr = new Rect(box.x - k * 5f, box.y - k * 4f, box.width, box.height);
                    DrawCardBackRect(cr);
                }
            }
        }

        /// <summary>
        /// F6: Dibuja el HUD compacto de estado sobre cada Pokémon en la mesa (barra de PS dinámica,
        /// marcador de daño total, orbes de energía unidos e iconos de estado animados con pulso y duración).
        /// </summary>
        void DrawPokemonOnMat(PlayerState owner, PokemonInPlay pip, Rect rect, bool isActive, bool allowClick)
        {
            if (pip == null) return;

            // F8: En Fase de Preparación (Setup), los Pokémon iniciales están boca abajo esperando la revelación simultánea
            if (game.Phase == Phase.Setup)
            {
                if (!(TableView.Instance != null && TableView.Instance.IsBoard3DReady))
                    DrawCardBackRect(rect);
                return;
            }

            // Si esta carta es la que está haciendo zoom elevado, no la dibujamos duplicada en la mesa
            if (zoomedCard == pip.Card && zoomedPip == pip && zoomAnim > 0.05f) return;

            bool has3D = TableView.Instance != null && TableView.Instance.IsBoard3DReady;
            float guiAlpha = GUI.color.a;

            // 1. Cartas de Energía DEBAJO del Pokémon + orbes de tipo compactos
            int eCount = pip.Energy.Count;
            if (!has3D)
            {
                for (int i = eCount - 1; i >= 0; i--)
                {
                    var eCard = pip.Energy[i];
                    float offX = -10f - i * 8f;
                    float offY = 10f + i * 7f;
                    Rect eRect = new Rect(rect.x + offX, rect.y + offY, rect.width, rect.height);
                    DrawCardSpriteOnly(eRect, eCard);
                }

                // 2. Carta 2D de respaldo si no hay mesa 3D activa
                bool highlight = retreatMode && owner == Me && Me.Bench.Contains(pip);
                int btn = DrawCardWithInput(rect, pip.Card, highlight, allowClick);
                if (btn == 0) OnPokemonLeftClicked(owner, pip);
                else if (btn == 1) StartZoom(pip.Card, pip, rect);
            }

            // Orbes limpios de energías unidas alineados sobre la barra de PS
            if (eCount > 0)
            {
                float orbSize = 16f;
                float step = eCount > 5 ? Mathf.Max(9f, (rect.width - 10f - orbSize) / (eCount - 1)) : 17f;
                for (int i = 0; i < eCount; i++)
                {
                    Rect orbRect = new Rect(rect.x + 3f + i * step, rect.yMax - 35f, orbSize, orbSize);
                    DrawEnergyOrb(orbRect, pip.Energy[i].EnergyProvides, guiAlpha);
                }
            }

            // 3. Barra de PS dinámica (Verde -> Ámbar -> Roja pulsante) + Píldora de Daño Total (-Dmg)
            float hpRatio = Mathf.Clamp01(pip.RemainingHp / (float)Math.Max(1, pip.Card.Hp));
            Texture2D barFill = hpRatio > 0.50f ? hpFillTex : (hpRatio > 0.25f ? hpMidTex : hpLowTex);
            Rect hpBar = new Rect(rect.x + 2f, rect.yMax - 17f, rect.width - 4f, 16f);
            GUI.DrawTexture(hpBar, hpBgTex);

            Color prevCol = GUI.color;
            if (hpRatio <= 0.25f)
            {
                float urgentPulse = 0.78f + 0.22f * Mathf.Sin(Time.time * 7.5f);
                GUI.color = new Color(1f, urgentPulse, urgentPulse, guiAlpha);
            }
            GUI.DrawTexture(new Rect(hpBar.x, hpBar.y, hpBar.width * hpRatio, hpBar.height), barFill);
            GUI.color = prevCol;

            GUI.Label(hpBar, $"<b>{pip.RemainingHp}/{pip.Card.Hp} PS</b>", CardLabelStyle);

            if (pip.Damage > 0)
            {
                Rect dmgPill = new Rect(rect.xMax - 36f, rect.yMax - 35f, 34f, 16f);
                GUI.Box(dmgPill, $"-{pip.Damage}", DurationBadgeStyle);
            }

            // 4. Iconos de Estados Alterados animados con pulso + contador de turnos encima
            float iconX = rect.xMax - 31f;
            float iconY = rect.y + 4f;
            float phase = 0f;

            if (pip.Status != Status.None)
            {
                string sym = pip.Status == Status.Asleep ? "ZZZ" : pip.Status == Status.Confused ? "CNF" : "PAR";
                Texture2D bg = pip.Status == Status.Asleep ? sleepTex : pip.Status == Status.Confused ? confTex : paraTex;
                int duration = pip.Status == Status.Paralyzed ? Math.Max(1, pip.StatusTurns) : 0;
                DrawStatusIcon(new Rect(iconX, iconY, 30f, 24f), sym, bg, duration, phase);
                iconY += 27f;
                phase += 1.1f;
            }
            if (pip.Poisoned)
            {
                DrawStatusIcon(new Rect(iconX, iconY, 30f, 24f), "VEN", poisTex, 0, phase);
                iconY += 27f;
                phase += 1.1f;
            }
            if (pip.DefenderUntilTurn >= game.Turn)
            {
                int remTurns = Math.Max(1, pip.DefenderUntilTurn - game.Turn + 1);
                DrawStatusIcon(new Rect(iconX, iconY, 30f, 24f), "DEF", defTex, remTurns, phase);
                iconY += 27f;
                phase += 1.1f;
            }
            if (pip.PlusPower > 0)
            {
                int bonusStacks = pip.PlusPower > 1 ? pip.PlusPower : 0;
                DrawStatusIcon(new Rect(iconX, iconY, 30f, 24f), "+10", plusTex, bonusStacks, phase);
            }
        }

        void DrawStatusIcon(Rect iconRect, string symbol, Texture2D bg, int remainingTurns, float phaseOffset = 0f)
        {
            float pulse = 1f + 0.09f * Mathf.Sin(Time.time * 5.6f + phaseOffset);
            float w = iconRect.width * pulse;
            float h = iconRect.height * pulse;
            Rect pulsedRect = new Rect(
                iconRect.center.x - w * 0.5f,
                iconRect.center.y - h * 0.5f,
                w,
                h);

            // Sombra/contorno oscuro detrás para máxima legibilidad sobre cualquier textura de carta 3D
            Color prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.72f * prev.a);
            GUI.DrawTexture(new Rect(pulsedRect.x - 1.5f, pulsedRect.y - 1.5f, pulsedRect.width + 3f, pulsedRect.height + 3f), Texture2D.whiteTexture);
            GUI.color = prev;

            GUI.DrawTexture(pulsedRect, bg);
            GUI.Label(pulsedRect, symbol, BadgeStyle);

            if (remainingTurns > 0)
            {
                // Insignia roja con la cantidad de turnos restantes (o cargas acumuladas)
                Rect numRect = new Rect(pulsedRect.xMax - 12f, pulsedRect.y - 7f, 16f, 16f);
                GUI.Box(numRect, remainingTurns.ToString(), DurationBadgeStyle);
            }
        }

        static Color EnergyColor(string type)
        {
            switch (type)
            {
                case "Fire": return new Color(0.95f, 0.32f, 0.20f);
                case "Water": return new Color(0.20f, 0.58f, 0.95f);
                case "Grass": return new Color(0.22f, 0.78f, 0.35f);
                case "Lightning": return new Color(0.95f, 0.82f, 0.15f);
                case "Psychic": return new Color(0.75f, 0.30f, 0.88f);
                case "Fighting": return new Color(0.82f, 0.45f, 0.20f);
                default: return new Color(0.78f, 0.78f, 0.78f);
            }
        }

        void DrawOpponentHand()
        {
            // F4: En modo 3D, la mano del NPC ya se dibuja como cartas 3D en abanico sobre HandAnchor[1]
            if (TableView.Instance != null && TableView.Instance.IsBoard3DReady)
                return;

            int n = Cpu.Hand.Count;
            if (n == 0) return;
            float cw = slotW * 0.58f;
            float ch = slotH * 0.58f;
            float step = Mathf.Min(cw * 0.72f, 260f / Math.Max(1, n));
            float totalW = cw + (n - 1) * step;
            float startX = (Screen.width - totalW) * 0.5f;
            for (int i = 0; i < n; i++)
            {
                DrawCardBackRect(new Rect(startX + i * step, 36f, cw, ch));
            }
        }

        void DrawPlayerHand()
        {
            int n = Me.Hand.Count;
            if (n == 0) return;

            // F4: En modo 3D, la mano del jugador se renderiza como abanico 3D interactivo (hover, clic, arrastrar y clic derecho)
            if (TableView.Instance != null && TableView.Instance.IsBoard3DReady)
            {
                int focusIdx = isDragging3D ? pressedHandIdx : (hoveredHandIdx >= 0 ? hoveredHandIdx : selHand);
                if (focusIdx >= 0 && focusIdx < n && zoomedCard == null)
                {
                    var fc = Me.Hand[focusIdx];
                    string hint = HintFor(fc);
                    string txt = $"<b>{fc.Name}</b>" + (!string.IsNullOrEmpty(hint) ? $" — {hint}" : "") +
                                 "   <color=#9ecbff>[Arrastrar / Clic izq.: Jugar | Clic der.: Inspeccionar]</color>";
                    float pillW = Mathf.Min(760f, playmatRect.width * 0.94f);
                    Rect pill = new Rect(playmatRect.center.x - pillW * 0.5f, Screen.height - 34f, pillW, 28f);
                    GUI.Box(pill, txt, SubBoxStyle);
                }
                return;
            }

            bool allowClick = zoomedCard == null && game.Phase != Phase.CoinToss && currentFlight == null;

            float cw = slotW * 1.04f;
            float ch = slotH * 1.04f;
            float maxTotalW = playmatRect.width * 0.88f;
            float step = n > 1 ? Mathf.Min(cw + 8f, (maxTotalW - cw) / (n - 1)) : cw;
            float totalW = cw + (n - 1) * step;
            float startX = (Screen.width - totalW) * 0.5f;
            float baseY = Screen.height - ch - 6f;

            for (int i = 0; i < n; i++)
            {
                var c = Me.Hand[i];
                if (zoomedCard == c && zoomedPip == null && zoomAnim > 0.05f) continue;

                bool selected = i == selHand;
                float y = selected ? baseY - 18f : baseY;
                Rect r = new Rect(startX + i * step, y, cw, ch);
                int btn = DrawCardWithInput(r, c, selected, allowClick);
                if (btn == 0) OnHandLeftClicked(i);
                else if (btn == 1) StartZoom(c, null, r);
            }
        }

        void DrawSideControls()
        {
            // Panel de acciones compacto anclado al borde derecho de la pantalla para no invadir la mesa ni el abanico 3D
            float panelW = 248f;
            Rect rightPanel = new Rect(Screen.width - panelW - 14f, 46f, panelW, Screen.height - 96f);
            GUILayout.BeginArea(rightPanel);

            if (game.Phase == Phase.ChoosePromotion && game.PromotionPending == Me)
            {
                GUI.Box(new Rect(0, 0, panelW, 95f), "", BoxBgStyle);
                GUILayout.Label(Loc.T(
                    "<color=#ffd166><b>Tu Pokémon Activo cayó:</b>\nHaz clic izquierdo en un Pokémon de tu Banca para enviarlo al Campo de Batalla.</color>",
                    "<color=#ffd166><b>Your Active Pokémon was Knocked Out:</b>\nLeft-click a Benched Pokémon to promote it to the Active spot.</color>"), SubHeaderStyle);
            }
            else if (game.Phase == Phase.Main && game.Current == Me && Me.Active != null && !IsAnimBusy())
            {
                GUI.Box(new Rect(0, 0, panelW, 315f), "", BoxBgStyle);
                GUILayout.BeginVertical(GUILayout.Width(panelW - 10f));
                GUILayout.Space(6);
                GUILayout.Label($"<b>{Loc.T("Acciones", "Actions")} — {Me.Active.Card.Name}</b>", HeaderStyle);
                GUILayout.Space(6);

                var a = Me.Active;
                for (int i = 0; i < a.Card.Attacks.Count; i++)
                {
                    var atk = a.Card.Attacks[i];
                    GUI.enabled = a.CanPay(atk.Cost);
                    if (GUILayout.Button($"⚔ {atk.Name} ({atk.BaseDamage}{atk.DamageSuffix})", BtnGoldStyle, GUILayout.Height(35)))
                    {
                        PlayClickSound(1);
                        Report(game.Attack(Me, i));
                    }
                    GUI.enabled = true;
                    GUILayout.Space(4);
                }

                if (GUILayout.Button(retreatMode ? Loc.T("Cancelar Retirada", "Cancel Retreat") : $"{Loc.T("Retirarse (Coste:", "Retreat (Cost:")} {a.Card.RetreatCost})", retreatMode ? BtnRedStyle : BtnStyle, GUILayout.Height(33)))
                {
                    PlayClickSound(0);
                    retreatMode = !retreatMode;
                }
                GUILayout.Space(4);

                if (selHand >= 0 && selHand < Me.Hand.Count)
                {
                    var c = Me.Hand[selHand];
                    if (c.IsBasicPokemon && GUILayout.Button(Loc.T("Bajar a la Banca", "Play to Bench"), BtnGreenStyle, GUILayout.Height(33)))
                    {
                        PlayClickSound(1);
                        Report(game.PlayBasic(Me, selHand));
                    }
                    if (c.Supertype == Supertype.Trainer && GUILayout.Button(Loc.T("Usar Entrenador", "Play Trainer"), BtnGreenStyle, GUILayout.Height(33)))
                    {
                        PlayClickSound(1);
                        Report(game.PlayTrainer(Me, selHand, null));
                    }
                }

                GUILayout.Space(8);
                if (GUILayout.Button(Loc.T("Terminar Turno", "End Turn"), BtnGoldStyle, GUILayout.Height(36)))
                {
                    PlayClickSound(1);
                    Report(game.EndTurn());
                }
                GUILayout.EndVertical();
            }

            GUILayout.EndArea();
        }

        // ==================== F7: Lanzamiento de Moneda 3D (con respaldo 2D) ====================
        void TriggerStartCoinToss(bool choseHeads)
        {
            PlayClickSound(1);
            startCoinPlayerChoseHeads = choseHeads;
            startCoinResultHeads = game.PerformStartCoinToss(choseHeads);
            startCoinSpinning = true;
            startCoinTimer = 0f;
            TableView.Instance?.LaunchStartCoinToss3D(startCoinResultHeads, StartCoinDuration);
        }

        void DrawInitialCoinTossModal()
        {
            if (startCoinSpinning) return;

            // Cuadro compacto subido respecto al borde inferior, mostrando únicamente las monedas de Cara y Cruz
            // con su texto debajo ("Cara" y "Cruz" en la fuente del juego), iluminándose al pasar el cursor y al hacer clic.
            float mw = 310f;
            float mh = 166f;
            Rect boxRect = new Rect((Screen.width - mw) * 0.5f, Screen.height - mh - 98f, mw, mh);
            DrawNativeBox(boxRect, sPanelSprite, Color.white);

            float coinSize = 96f;
            Rect headsRect = new Rect(boxRect.x + 38f, boxRect.y + 16f, coinSize, coinSize);
            Rect tailsRect = new Rect(boxRect.x + mw - 38f - coinSize, boxRect.y + 16f, coinSize, coinSize);

            if (DrawNativeCoinChoice(headsRect, SpriteCache.CoinHeadsTex, Loc.T("Cara", "Heads")))
            {
                TriggerStartCoinToss(true);
            }
            if (DrawNativeCoinChoice(tailsRect, SpriteCache.CoinTailsTex, Loc.T("Cruz", "Tails")))
            {
                TriggerStartCoinToss(false);
            }
        }

        bool DrawNativeCoinChoice(Rect coinRect, Texture2D coinTex, string label)
        {
            EnsureNativeCanvasOverlay();
            Vector2 guiMouse = Event.current.mousePosition;
            bool hover = coinRect.Contains(guiMouse);
            bool pressed = hover && Input.GetMouseButton(0);

            // Halo suave de iluminación detrás de la moneda al pasar el cursor o seleccionarla
            if (hover || pressed)
            {
                float pad = pressed ? 16f : 12f;
                Rect glowRect = new Rect(coinRect.x - pad, coinRect.y - pad, coinRect.width + pad * 2f, coinRect.height + pad * 2f);
                Color glowCol = pressed
                    ? new Color(1.00f, 0.92f, 0.45f, 0.80f)
                    : new Color(1.00f, 0.88f, 0.38f, 0.48f);
                DrawNativeBox(glowRect, sGlowSprite, glowCol);
            }

            float scale = pressed ? 1.10f : (hover ? 1.06f : 1.00f);
            float sw = coinRect.width * scale;
            float sh = coinRect.height * scale;
            Rect drawRect = new Rect(
                coinRect.center.x - sw * 0.5f,
                coinRect.center.y - sh * 0.5f,
                sw,
                sh);

            if (coinTex != null)
            {
                DrawNativeRawTex(drawRect, coinTex, Color.white);
            }

            // Iluminación ligera sobre la propia imagen de la moneda al pasar el cursor y al seleccionarla
            if (hover || pressed)
            {
                Color shineCol = pressed
                    ? new Color(1.00f, 0.98f, 0.78f, 0.42f)
                    : new Color(1.00f, 0.96f, 0.72f, 0.24f);
                DrawNativeBox(drawRect, sGlowSprite, shineCol);
            }

            Rect lblRect = new Rect(coinRect.x - 18f, coinRect.yMax + 6f, coinRect.width + 36f, 34f);
            Color lblCol = (hover || pressed) ? new Color(1.00f, 0.90f, 0.36f, 1f) : Color.white;
            DrawNativeLabel(lblRect, label, 23f, TextAlignmentOptions.Center, lblCol);

            return GUI.Button(coinRect, GUIContent.none, GUIStyle.none);
        }

        void DrawSubCoinFlip()
        {
            float t = 1f - (subCoinTimer / 1.6f);
            float coinSize = 120f;
            float cx = playmatRect.x + playmatRect.width * 0.25f;
            float cy = playmatRect.y + playmatRect.height * 0.48f;
            DrawAnimatedCoin(cx, cy, coinSize, t, subCoinHeads, subCoinLabel);
        }

        void DrawAnimatedCoin(float centerX, float centerY, float baseSize, float normalizedTime, bool finalHeads, string caption)
        {
            float t = Mathf.Clamp01(normalizedTime);
            // Parábola de elevación al lanzar la moneda y caer sobre la mesa
            float lift = t < 0.75f ? Mathf.Sin((t / 0.75f) * Mathf.PI) * 95f : 0f;
            float scale = 1f + (lift / 95f) * 0.35f;

            // Giro rápido que se frena al caer
            float spinPhase = t < 0.75f ? Mathf.Cos(t * 28f) : 1f;
            bool showHeads = t < 0.75f ? (spinPhase >= 0f) : finalHeads;
            float widthFactor = t < 0.75f ? Mathf.Max(0.12f, Mathf.Abs(spinPhase)) : 1f;

            float w = baseSize * scale * widthFactor;
            float h = baseSize * scale;
            Rect cRect = new Rect(centerX - w * 0.5f, centerY - h * 0.5f - lift, w, h);

            Texture2D tex = showHeads ? SpriteCache.CoinHeadsTex : SpriteCache.CoinTailsTex;
            if (tex != null) GUI.DrawTexture(cRect, tex, ScaleMode.StretchToFill);

            if (!string.IsNullOrEmpty(caption))
            {
                Rect capRect = new Rect(centerX - 140f, centerY + baseSize * 0.55f, 280f, 32f);
                GUI.Box(capRect, caption, ToastStyle);
            }
        }

        // ==================== Animación de Movimiento de Cartas ====================
        void DrawCurrentFlight()
        {
            // Si la moneda inicial está girando, en 3D vuela físicamente Coin3D y mostramos el resultado con la fuente del juego sin cuadro superior
            if (startCoinSpinning)
            {
                float t = Mathf.Clamp01(startCoinTimer / StartCoinDuration);
                bool has3D = TableView.Instance != null && TableView.Instance.IsBoard3DReady;
                if (has3D)
                {
                    if (t >= 0.84f)
                    {
                        string resName = startCoinResultHeads ? Loc.T("Cara", "Heads") : Loc.T("Cruz", "Tails");
                        string resultTxt = $"<color=#ffe066>¡{resName}!</color> — {Loc.T("Inicia", "Starts:")} {game.Current.Name}";
                        DrawNativeLabel(new Rect((Screen.width - 540f) * 0.5f, Screen.height - 150f, 540f, 44f), resultTxt, 28f, TextAlignmentOptions.Center, Color.white);
                    }
                }
                else
                {
                    string choiceName = startCoinPlayerChoseHeads ? "CARA" : "CRUZ";
                    string resultTxt = t >= 0.84f
                        ? $"¡Salió {(startCoinResultHeads ? "CARA (Pikachu)" : "CRUZ (Pokéball)")}! Inicia {game.Current.Name}"
                        : $"Elegiste {choiceName} — Lanzando moneda...";
                    DrawAnimatedCoin(Screen.width * 0.5f, Screen.height * 0.44f, 175f, t, startCoinResultHeads, resultTxt);
                }
            }

            // F3: Mostrar rótulo flotante de la acción 3D en curso sobre el tapete
            if (BoardAnimator.Instance != null && BoardAnimator.Instance.Current != null && !string.IsNullOrEmpty(BoardAnimator.Instance.CurrentCaption))
            {
                Rect capRect = new Rect(playmatRect.center.x - 190f, playmatRect.y + 42f, 380f, 34f);
                GUI.Box(capRect, BoardAnimator.Instance.CurrentCaption, ToastStyle);
            }

            if (currentFlight == null) return;
            float u = Mathf.Clamp01(currentFlight.Elapsed / Math.Max(0.05f, currentFlight.Duration));
            float smooth = Mathf.SmoothStep(0f, 1f, u);
            float arcLift = Mathf.Sin(u * Mathf.PI) * 26f;

            Rect cur = new Rect(
                Mathf.Lerp(currentFlight.From.x, currentFlight.To.x, smooth),
                Mathf.Lerp(currentFlight.From.y, currentFlight.To.y, smooth) - arcLift,
                Mathf.Lerp(currentFlight.From.width, currentFlight.To.width, smooth) * (1f + Mathf.Sin(u * Mathf.PI) * 0.12f),
                Mathf.Lerp(currentFlight.From.height, currentFlight.To.height, smooth) * (1f + Mathf.Sin(u * Mathf.PI) * 0.12f)
            );

            if (currentFlight.FaceDown || currentFlight.Card == null)
                DrawCardBackRect(cur);
            else
                DrawCardSpriteOnly(cur, currentFlight.Card);
        }

        /// <summary>
        /// F6: Proyecta los textos flotantes 3D (-30, +20 PS, ¡DORMIDO!, ¡PARALIZADO!, ¡ENVENENADO!, etc.)
        /// desde su posición 3D sobre la mesa física hacia pantalla con ascenso suave, golpe de escala y contorno oscuro.
        /// </summary>
        void DrawFloatingPopups3D()
        {
            if (StatusCounters3D.ActivePopups.Count == 0) return;
            var cam = TableView.GetActiveCamera();
            if (cam == null) return;

            Color prevCol = GUI.color;
            int baseFontSize = FloatingPopupStyle.fontSize;

            for (int i = 0; i < StatusCounters3D.ActivePopups.Count; i++)
            {
                var p = StatusCounters3D.ActivePopups[i];
                float u = Mathf.Clamp01(p.Elapsed / Math.Max(0.1f, p.Duration));

                // Elevación progresiva en el mundo 3D sobre la carta golpeada/afectada
                float riseMeters = Mathf.SmoothStep(0f, 1f, u) * 0.065f;
                Vector3 sp = cam.WorldToScreenPoint(p.WorldOrigin + Vector3.up * riseMeters);
                if (sp.z <= 0.05f) continue;

                float gx = sp.x;
                float gy = Screen.height - sp.y;

                // Golpe elástico inicial (punch) y desvanecimiento suave al final
                float punch = u < 0.18f
                    ? Mathf.Lerp(0.65f, 1.24f, u / 0.18f)
                    : Mathf.Lerp(1.24f, 1.00f, Mathf.Clamp01((u - 0.18f) / 0.22f));
                float alpha = u < 0.65f ? 1f : Mathf.Clamp01((1f - u) / 0.35f);

                FloatingPopupStyle.fontSize = Mathf.Clamp(Mathf.RoundToInt(20f * p.FontScale * punch), 13, 36);
                Rect r = new Rect(gx - 110f, gy - 20f, 220f, 40f);

                // Contorno oscuro en 4 direcciones para contraste limpio sobre cualquier carta 3D
                GUI.color = new Color(0f, 0f, 0f, 0.90f * alpha);
                GUI.Label(new Rect(r.x - 1.5f, r.y + 1.5f, r.width, r.height), p.Text, FloatingPopupStyle);
                GUI.Label(new Rect(r.x + 1.5f, r.y + 1.5f, r.width, r.height), p.Text, FloatingPopupStyle);
                GUI.Label(new Rect(r.x - 1.5f, r.y - 1.5f, r.width, r.height), p.Text, FloatingPopupStyle);
                GUI.Label(new Rect(r.x + 1.5f, r.y - 1.5f, r.width, r.height), p.Text, FloatingPopupStyle);

                GUI.color = new Color(p.Color.r, p.Color.g, p.Color.b, alpha);
                GUI.Label(r, p.Text, FloatingPopupStyle);
            }

            FloatingPopupStyle.fontSize = baseFontSize;
            GUI.color = prevCol;
        }

        static Sprite MakeSlicedSprite(Texture2D tex, float border)
        {
            if (tex == null) return null;
            return Sprite.Create(
                tex,
                new Rect(0f, 0f, tex.width, tex.height),
                new Vector2(0.5f, 0.5f),
                100f,
                0u,
                SpriteMeshType.FullRect,
                new Vector4(border, border, border, border));
        }

        static void SetScreenRect(RectTransform rt, Rect guiRect)
        {
            if (rt == null) return;
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(guiRect.x, -guiRect.y);
            rt.sizeDelta = new Vector2(guiRect.width, guiRect.height);
            rt.localScale = Vector3.one;
        }

        static TextMeshProUGUI GetReferenceGameTmp()
        {
            try
            {
                GameUIScreen gui = CSingleton<GameUIScreen>.InstanceExist()
                    ? CSingleton<GameUIScreen>.Instance
                    : FindFirstObjectByType<GameUIScreen>();
                if (gui != null)
                {
                    if (gui.m_GoNextDayText != null) return gui.m_GoNextDayText;
                    if (gui.m_TimeText != null) return gui.m_TimeText;
                    if (gui.m_ShopLevelText != null) return gui.m_ShopLevelText;
                }
            }
            catch { }
            return null;
        }

        static TextMeshProUGUI CreateNativeTmp(string name, Transform parent, float fontSize, TextAlignmentOptions align, out RectTransform rt)
        {
            TextMeshProUGUI refTmp = GetReferenceGameTmp();
            GameObject go;
            TextMeshProUGUI tmp;

            if (refTmp != null)
            {
                go = Instantiate(refTmp.gameObject, parent, false);
                go.name = name;
                foreach (var mb in go.GetComponents<MonoBehaviour>())
                {
                    if (!(mb is TextMeshProUGUI))
                        DestroyImmediate(mb);
                }
                for (int i = go.transform.childCount - 1; i >= 0; i--)
                    DestroyImmediate(go.transform.GetChild(i).gameObject);

                tmp = go.GetComponent<TextMeshProUGUI>();
                tmp.font = refTmp.font;
                tmp.fontSharedMaterial = refTmp.fontSharedMaterial;
            }
            else
            {
                go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
                go.transform.SetParent(parent, false);
                tmp = go.GetComponent<TextMeshProUGUI>();
            }

            go.SetActive(true);
            rt = go.GetComponent<RectTransform>();
            tmp.fontSize = fontSize;
            tmp.alignment = align;
            tmp.enableAutoSizing = false;
            tmp.textWrappingMode = TextWrappingModes.Normal;
            tmp.overflowMode = TextOverflowModes.Overflow;
            tmp.richText = true;
            tmp.raycastTarget = false;
            tmp.color = Color.white;
            return tmp;
        }

        static Image CreateSlicedImage(string name, Transform parent, Sprite sprite, out RectTransform rt)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);
            rt = go.GetComponent<RectTransform>();
            var img = go.GetComponent<Image>();
            img.sprite = sprite;
            img.type = Image.Type.Sliced;
            img.raycastTarget = false;
            img.color = Color.white;
            return img;
        }

        static Texture2D MakeSoftDiscTex(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            float half = (size - 1) * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x - half) / half;
                    float dy = (y - half) / half;
                    float r = Mathf.Clamp01(Mathf.Sqrt(dx * dx + dy * dy));
                    float a = Mathf.Pow(1f - r, 1.85f);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            }
            tex.Apply();
            return tex;
        }

        void EnsureNativeCanvasOverlay()
        {
            EnsureStyles();
            if (sPanelSprite == null)
            {
                sPanelSprite = MakeSlicedSprite(darkTex, 12f);
                sSubPanelSprite = MakeSlicedSprite(subPanelTex, 10f);
                sBtnBlueN = MakeSlicedSprite(btnBlueN, 10f);
                sBtnBlueH = MakeSlicedSprite(btnBlueH, 10f);
                sBtnBlueA = MakeSlicedSprite(btnBlueA, 10f);
                sBtnGreenN = MakeSlicedSprite(btnGreenN, 10f);
                sBtnGreenH = MakeSlicedSprite(btnGreenH, 10f);
                sBtnGreenA = MakeSlicedSprite(btnGreenA, 10f);
                sBtnGoldN = MakeSlicedSprite(btnGoldN, 10f);
                sBtnGoldH = MakeSlicedSprite(btnGoldH, 10f);
                sBtnGoldA = MakeSlicedSprite(btnGoldA, 10f);
                sBtnRedN = MakeSlicedSprite(btnRedN, 10f);
                sBtnRedH = MakeSlicedSprite(btnRedH, 10f);
                sBtnRedA = MakeSlicedSprite(btnRedA, 10f);

                var glowTex = MakeSoftDiscTex(64);
                sGlowSprite = Sprite.Create(glowTex, new Rect(0, 0, glowTex.width, glowTex.height), new Vector2(0.5f, 0.5f), 100f);
                sDarkBackdropSprite = Sprite.Create(zoomBgTex, new Rect(0, 0, zoomBgTex.width, zoomBgTex.height), new Vector2(0.5f, 0.5f), 100f);
            }

            if (nativeOverlayRoot == null)
            {
                nativeOverlayRoot = new GameObject("PokeTCG_NativeCanvasOverlay", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
                DontDestroyOnLoad(nativeOverlayRoot);
                var canvas = nativeOverlayRoot.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 500;
                canvas.pixelPerfect = true;

                var scaler = nativeOverlayRoot.GetComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
                scaler.scaleFactor = 1f;

                var poolGo = new GameObject("DynamicPoolRoot", typeof(RectTransform));
                poolGo.transform.SetParent(nativeOverlayRoot.transform, false);
                dynamicPoolRoot = poolGo.GetComponent<RectTransform>();
                SetScreenRect(dynamicPoolRoot, new Rect(0f, 0f, Screen.width, Screen.height));

                // 1) Grupo de Espera ("Esperando un oponente..." + botón céntrico "Levantarse de la mesa")
                waitingGroup = new GameObject("WaitingGroup", typeof(RectTransform));
                waitingGroup.transform.SetParent(nativeOverlayRoot.transform, false);
                SetScreenRect(waitingGroup.GetComponent<RectTransform>(), new Rect(0f, 0f, Screen.width, Screen.height));

                waitingTitleTmp = CreateNativeTmp("WaitingTitleTmp", waitingGroup.transform, 38f, TextAlignmentOptions.Center, out waitingTitleRt);
                waitingBtnImg = CreateSlicedImage("WaitingStandUpBtn", waitingGroup.transform, sBtnRedN, out waitingBtnRt);
                waitingBtnTmp = CreateNativeTmp("WaitingStandUpBtnLabel", waitingBtnRt, 22f, TextAlignmentOptions.Center, out var waitBtnLabelRt);
                waitBtnLabelRt.anchorMin = Vector2.zero;
                waitBtnLabelRt.anchorMax = Vector2.one;
                waitBtnLabelRt.pivot = new Vector2(0.5f, 0.5f);
                waitBtnLabelRt.anchoredPosition = new Vector2(0f, 1f);
                waitBtnLabelRt.sizeDelta = Vector2.zero;
                waitingGroup.SetActive(false);

                // 2) Grupo de Fin de Partida ("¡Victoria!" flotante sin cuadro encima del cuadro de Resumen de la Partida)
                gameOverGroup = new GameObject("GameOverGroup", typeof(RectTransform));
                gameOverGroup.transform.SetParent(nativeOverlayRoot.transform, false);
                SetScreenRect(gameOverGroup.GetComponent<RectTransform>(), new Rect(0f, 0f, Screen.width, Screen.height));

                gameOverTitleTmp = CreateNativeTmp("GameOverTitleTmp", gameOverGroup.transform, 58f, TextAlignmentOptions.Center, out gameOverTitleRt);
                CreateSlicedImage("GameOverBox", gameOverGroup.transform, sPanelSprite, out gameOverBoxRt);
                CreateSlicedImage("GameOverSubBox", gameOverGroup.transform, sSubPanelSprite, out gameOverSubBoxRt);
                gameOverHeaderTmp = CreateNativeTmp("GameOverHeaderTmp", gameOverGroup.transform, 23f, TextAlignmentOptions.Center, out gameOverHeaderRt);
                gameOverBodyTmp = CreateNativeTmp("GameOverBodyTmp", gameOverGroup.transform, 18.5f, TextAlignmentOptions.TopLeft, out gameOverBodyRt);

                rematchBtnImg = CreateSlicedImage("RematchBtn", gameOverGroup.transform, sBtnGreenN, out rematchBtnRt);
                rematchBtnTmp = CreateNativeTmp("RematchBtnLabel", rematchBtnRt, 19f, TextAlignmentOptions.Center, out var remLabelRt);
                remLabelRt.anchorMin = Vector2.zero;
                remLabelRt.anchorMax = Vector2.one;
                remLabelRt.pivot = new Vector2(0.5f, 0.5f);
                remLabelRt.anchoredPosition = new Vector2(0f, 1f);
                remLabelRt.sizeDelta = Vector2.zero;

                leaveBtnImg = CreateSlicedImage("LeaveBtn", gameOverGroup.transform, sBtnRedN, out leaveBtnRt);
                leaveBtnTmp = CreateNativeTmp("LeaveBtnLabel", leaveBtnRt, 19f, TextAlignmentOptions.Center, out var lvLabelRt);
                lvLabelRt.anchorMin = Vector2.zero;
                lvLabelRt.anchorMax = Vector2.one;
                lvLabelRt.pivot = new Vector2(0.5f, 0.5f);
                lvLabelRt.anchoredPosition = new Vector2(0f, 1f);
                lvLabelRt.sizeDelta = Vector2.zero;

                gameOverGroup.SetActive(false);
                nativeFontSynced = GetReferenceGameTmp() != null;
            }

            if (!nativeFontSynced)
            {
                var refTmp = GetReferenceGameTmp();
                if (refTmp != null && refTmp.font != null)
                {
                    TextMeshProUGUI[] all =
                    {
                        waitingTitleTmp, waitingBtnTmp,
                        gameOverTitleTmp, gameOverHeaderTmp, gameOverBodyTmp,
                        rematchBtnTmp, leaveBtnTmp
                    };
                    foreach (var t in all)
                    {
                        if (t == null) continue;
                        t.font = refTmp.font;
                        t.fontSharedMaterial = refTmp.fontSharedMaterial;
                    }
                    for (int i = 0; i < nativeTmpPool.Count; i++)
                    {
                        if (nativeTmpPool[i] != null)
                        {
                            nativeTmpPool[i].font = refTmp.font;
                            nativeTmpPool[i].fontSharedMaterial = refTmp.fontSharedMaterial;
                        }
                    }
                    nativeFontSynced = true;
                }
            }
        }

        public void BeginNativeRepaint()
        {
            EnsureNativeCanvasOverlay();
            if (nativeOverlayRoot != null && !nativeOverlayRoot.activeSelf)
                nativeOverlayRoot.SetActive(true);
            if (Event.current != null && Event.current.type == EventType.Repaint)
            {
                isNativeRepaintPass = true;
                usedNativeImg = 0;
                usedNativeRaw = 0;
                usedNativeTmp = 0;
                nativeSiblingCursor = 0;
                if (dynamicPoolRoot != null && !dynamicPoolRoot.gameObject.activeSelf)
                    dynamicPoolRoot.gameObject.SetActive(true);
            }
            else
            {
                isNativeRepaintPass = false;
            }
        }

        public void EndNativeRepaint()
        {
            if (!isNativeRepaintPass) return;
            isNativeRepaintPass = false;

            for (int i = usedNativeImg; i < nativeImgPool.Count; i++)
            {
                if (nativeImgPool[i] != null && nativeImgPool[i].gameObject.activeSelf)
                    nativeImgPool[i].gameObject.SetActive(false);
            }
            for (int i = usedNativeRaw; i < nativeRawPool.Count; i++)
            {
                if (nativeRawPool[i] != null && nativeRawPool[i].gameObject.activeSelf)
                    nativeRawPool[i].gameObject.SetActive(false);
            }
            for (int i = usedNativeTmp; i < nativeTmpPool.Count; i++)
            {
                if (nativeTmpPool[i] != null && nativeTmpPool[i].gameObject.activeSelf)
                    nativeTmpPool[i].gameObject.SetActive(false);
            }
        }

        public void ClearNativeDynamicPool()
        {
            usedNativeImg = 0;
            usedNativeRaw = 0;
            usedNativeTmp = 0;
            for (int i = 0; i < nativeImgPool.Count; i++)
            {
                if (nativeImgPool[i] != null && nativeImgPool[i].gameObject.activeSelf)
                    nativeImgPool[i].gameObject.SetActive(false);
            }
            for (int i = 0; i < nativeRawPool.Count; i++)
            {
                if (nativeRawPool[i] != null && nativeRawPool[i].gameObject.activeSelf)
                    nativeRawPool[i].gameObject.SetActive(false);
            }
            for (int i = 0; i < nativeTmpPool.Count; i++)
            {
                if (nativeTmpPool[i] != null && nativeTmpPool[i].gameObject.activeSelf)
                    nativeTmpPool[i].gameObject.SetActive(false);
            }
        }

        public void DrawNativeBox(Rect rect, Sprite sprite, Color col, bool sliced = true)
        {
            if (!isNativeRepaintPass) return;
            EnsureNativeCanvasOverlay();
            Image img;
            if (usedNativeImg < nativeImgPool.Count)
            {
                img = nativeImgPool[usedNativeImg];
            }
            else
            {
                img = CreateSlicedImage("PooledImg_" + nativeImgPool.Count, dynamicPoolRoot, sprite, out _);
                nativeImgPool.Add(img);
            }
            usedNativeImg++;
            if (!img.gameObject.activeSelf) img.gameObject.SetActive(true);
            img.sprite = sprite;
            img.type = (sliced && sprite != sGlowSprite && sprite != sDarkBackdropSprite) ? Image.Type.Sliced : Image.Type.Simple;
            img.color = col;
            var rt = img.rectTransform;
            SetScreenRect(rt, rect);
            rt.SetSiblingIndex(nativeSiblingCursor++);
        }

        public void DrawNativeRawTex(Rect rect, Texture tex, Color col)
        {
            if (!isNativeRepaintPass || tex == null) return;
            EnsureNativeCanvasOverlay();
            RawImage raw;
            if (usedNativeRaw < nativeRawPool.Count)
            {
                raw = nativeRawPool[usedNativeRaw];
            }
            else
            {
                var go = new GameObject("PooledRaw_" + nativeRawPool.Count, typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
                go.transform.SetParent(dynamicPoolRoot, false);
                raw = go.GetComponent<RawImage>();
                raw.raycastTarget = false;
                nativeRawPool.Add(raw);
            }
            usedNativeRaw++;
            if (!raw.gameObject.activeSelf) raw.gameObject.SetActive(true);
            raw.texture = tex;
            raw.color = col;
            var rt = raw.rectTransform;
            SetScreenRect(rt, rect);
            rt.SetSiblingIndex(nativeSiblingCursor++);
        }

        public void DrawNativeLabel(Rect rect, string text, float fontSize, TextAlignmentOptions align, Color col)
        {
            if (!isNativeRepaintPass) return;
            EnsureNativeCanvasOverlay();
            TextMeshProUGUI tmp;
            if (usedNativeTmp < nativeTmpPool.Count)
            {
                tmp = nativeTmpPool[usedNativeTmp];
            }
            else
            {
                tmp = CreateNativeTmp("PooledTmp_" + nativeTmpPool.Count, dynamicPoolRoot, fontSize, align, out _);
                nativeTmpPool.Add(tmp);
            }
            usedNativeTmp++;
            if (!tmp.gameObject.activeSelf) tmp.gameObject.SetActive(true);
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.alignment = align;
            tmp.color = col;
            var rt = tmp.rectTransform;
            SetScreenRect(rt, rect);
            rt.SetSiblingIndex(nativeSiblingCursor++);
        }

        public bool DrawNativeButton(Rect rect, string label, float fontSize, Sprite sNorm, Sprite sHov, Sprite sAct)
        {
            Vector2 guiMouse = Event.current != null ? Event.current.mousePosition : Vector2.zero;
            bool hover = rect.Contains(guiMouse);
            bool pressed = hover && Input.GetMouseButton(0);

            DrawNativeBox(rect, pressed ? sAct : (hover ? sHov : sNorm), Color.white);
            Rect lblRect = new Rect(rect.x + 6f, rect.y + (pressed ? 1.5f : -0.5f), rect.width - 12f, rect.height);
            Color lblCol = hover ? new Color(1f, 0.98f, 0.85f, 1f) : Color.white;
            DrawNativeLabel(lblRect, label, fontSize, TextAlignmentOptions.Center, lblCol);

            return GUI.Button(rect, GUIContent.none, GUIStyle.none);
        }

        void DrawWaitingForOpponentScreen()
        {
            EnsureNativeCanvasOverlay();
            if (waitingGroup != null && !waitingGroup.activeSelf)
                waitingGroup.SetActive(true);

            // Puntos suspensivos que avanzan de 1 en 1 (. -> .. -> ...) y al llegar a 3 se reinician en bucle
            int dotCount = ((int)(Time.unscaledTime / 0.52f) % 3) + 1;
            string visibleDots = new string('.', dotCount);
            string hiddenDots = new string('.', 3 - dotCount);
            string baseWait = Loc.T("Esperando un oponente", "Waiting for an opponent");

            float centerX = Screen.width * 0.5f;
            float centerY = Screen.height * 0.45f;
            Rect titleRect = new Rect(centerX - 380f, centerY - 38f, 760f, 58f);
            Rect btnRect = new Rect(centerX - 155f, centerY + 30f, 310f, 54f);

            SetScreenRect(waitingTitleRt, titleRect);
            waitingTitleTmp.text = $"{baseWait}{visibleDots}<color=#00000000>{hiddenDots}</color>";

            SetScreenRect(waitingBtnRt, btnRect);
            waitingBtnTmp.text = Loc.T("Levantarse de la mesa", "Stand up from table");

            Vector2 guiMouse = Event.current.mousePosition;
            bool hover = btnRect.Contains(guiMouse);
            bool pressed = hover && Input.GetMouseButton(0);
            waitingBtnImg.sprite = pressed ? sBtnRedA : (hover ? sBtnRedH : sBtnRedN);

            if (GUI.Button(btnRect, GUIContent.none, GUIStyle.none))
            {
                PlayClickSound(2);
                LeaveTable();
            }
        }

        void DrawGameOverModal()
        {
            if (!game.RewardsApplied)
                PlayTableSession.ApplyMatchOutcomeRewards(game);

            EnsureNativeCanvasOverlay();
            if (gameOverGroup != null && !gameOverGroup.activeSelf)
                gameOverGroup.SetActive(true);

            bool playerWon = game.Winner == Me;
            var reward = PlayTableSession.LastReward;
            int xp = playerWon && reward != null ? reward.ExpGained : 0;
            int turns = Math.Max(1, game.Turn);
            string reason = !string.IsNullOrEmpty(game.WinReason)
                ? game.WinReason
                : (playerWon
                    ? Loc.T("Tu oponente se quedó sin Pokémon", "Your opponent ran out of Pokémon")
                    : Loc.T("Te quedaste sin Pokémon", "You ran out of Pokémon"));

            // Cuadro de resumen de partida centrado debajo del mensaje grande "¡Victoria!"
            float mw = 520f, mh = 238f;
            Rect boxRect = new Rect((Screen.width - mw) * 0.5f, (Screen.height - mh) * 0.5f + 28f, mw, mh);

            // Mensaje grande encima del cuadro de resumen (solo texto en la fuente del juego, sin cuadro alrededor)
            Rect titleRect = new Rect((Screen.width - 720f) * 0.5f, boxRect.y - 82f, 720f, 74f);
            SetScreenRect(gameOverTitleRt, titleRect);
            gameOverTitleTmp.text = playerWon
                ? Loc.T("<color=#70ff94>¡Victoria!</color>", "<color=#70ff94>Victory!</color>")
                : Loc.T("<color=#ff7b7b>¡Derrota!</color>", "<color=#ff7b7b>Defeat!</color>");

            SetScreenRect(gameOverBoxRt, boxRect);

            // Subcuadro con el Resumen de la Partida (Motivo, Turnos y únicamente la cantidad de XP ganada)
            Rect subRect = new Rect(boxRect.x + 22f, boxRect.y + 18f, mw - 44f, 136f);
            SetScreenRect(gameOverSubBoxRt, subRect);

            SetScreenRect(gameOverHeaderRt, new Rect(subRect.x + 14f, subRect.y + 10f, subRect.width - 28f, 30f));
            gameOverHeaderTmp.text = Loc.T("Resumen de la Partida", "Match Summary");

            SetScreenRect(gameOverBodyRt, new Rect(subRect.x + 20f, subRect.y + 44f, subRect.width - 40f, 84f));
            gameOverBodyTmp.text =
                $"• <color=#9ecbff>{Loc.T("Motivo:", "Reason:")}</color> <color=#ffe066>{reason}</color>\n" +
                $"• <color=#9ecbff>{Loc.T("Turnos:", "Turns:")}</color> {turns}\n" +
                $"• <color=#9ecbff>{Loc.T("EXP ganada:", "EXP gained:")}</color> <color=#80ed99>+{xp} XP</color>";

            // Botones ajustados dentro del cuadro de victoria con margen amplio para que quepan adecuadamente
            float btnW = (mw - 44f - 16f) * 0.5f;
            float btnH = 46f;
            float btnY = boxRect.y + 170f;
            Rect rematchRect = new Rect(boxRect.x + 22f, btnY, btnW, btnH);
            Rect leaveRect = new Rect(boxRect.x + 22f + btnW + 16f, btnY, btnW, btnH);

            SetScreenRect(rematchBtnRt, rematchRect);
            rematchBtnTmp.text = Loc.T("Jugar Revancha", "Play Rematch");

            SetScreenRect(leaveBtnRt, leaveRect);
            leaveBtnTmp.text = Loc.T("Levantarse de la mesa", "Stand up from table");

            Vector2 guiMouse = Event.current.mousePosition;
            bool hovRematch = rematchRect.Contains(guiMouse);
            bool hovLeave = leaveRect.Contains(guiMouse);
            rematchBtnImg.sprite = (hovRematch && Input.GetMouseButton(0)) ? sBtnGreenA : (hovRematch ? sBtnGreenH : sBtnGreenN);
            leaveBtnImg.sprite = (hovLeave && Input.GetMouseButton(0)) ? sBtnRedA : (hovLeave ? sBtnRedH : sBtnRedN);

            if (GUI.Button(rematchRect, GUIContent.none, GUIStyle.none))
            {
                PlayClickSound(1);
                if (gameOverGroup != null) gameOverGroup.SetActive(false);
                StartTableMatch(PlayTableSession.CurrentOpponent);
                return;
            }
            if (GUI.Button(leaveRect, GUIContent.none, GUIStyle.none))
            {
                PlayClickSound(2);
                if (gameOverGroup != null) gameOverGroup.SetActive(false);
                LeaveTable();
            }
        }

        // ==================== F5: Inspección 3D con Elevación hacia Cámara e Iconos de Energía ====================
        static string EnergySymbol(string type)
        {
            switch (type)
            {
                case "Fire": return "F";
                case "Water": return "W";
                case "Grass": return "G";
                case "Lightning": return "L";
                case "Psychic": return "P";
                case "Fighting": return "M";
                default: return "★";
            }
        }

        static void DrawEnergyOrb(Rect r, string type, float alpha = 1f)
        {
            Color prevBg = GUI.backgroundColor;
            Color prevCol = GUI.color;
            Color ec = EnergyColor(type);
            GUI.backgroundColor = new Color(ec.r, ec.g, ec.b, alpha);
            GUI.color = new Color(1f, 1f, 1f, alpha);
            GUI.Box(r, EnergySymbol(type), BadgeStyle);
            GUI.backgroundColor = prevBg;
            GUI.color = prevCol;
        }

        static void DrawInlineEnergyOrb(string type, float size = 22f)
        {
            Rect r = GUILayoutUtility.GetRect(size, size, GUILayout.Width(size), GUILayout.Height(size));
            DrawEnergyOrb(r, type, GUI.color.a);
        }

        /// <summary>
        /// F5: Muestra la Inspección de Carta (en mesa 3D la carta sube físicamente en 3D hacia la cámara con giro interactivo
        /// y telón 3D oscurecido detrás; a la derecha se despliega el panel con ataques, costes como iconos de energía,
        /// debilidad, resistencia y coste de retirada). Devuelve true si el usuario cerró la inspección.
        /// </summary>
        public static bool DrawElevatedZoomOverlay(CardDef card, PokemonInPlay pip, Rect originRect, float animT, bool force3DCard = false, Action extraFooterUI = null)
        {
            EnsureStyles();
            float smooth = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(animT));
            bool use3DCard = force3DCard || (Instance != null && Instance.open && TableView.Instance != null && TableView.Instance.IsBoard3DReady);

            Color prevCol = GUI.color;
            float targetH = Mathf.Min(Screen.height * 0.74f, 570f);
            float targetW = Mathf.Round(targetH * (63f / 88f));
            float totalW = targetW + 480f;
            Rect targetCardRect = new Rect((Screen.width - totalW) * 0.5f, (Screen.height - targetH) * 0.5f, targetW, targetH);

            if (!use3DCard)
            {
                // Respaldo 2D (por ejemplo, al inspeccionar cartas dentro del Deck Builder en la Workbench)
                GUI.color = new Color(1f, 1f, 1f, smooth * 0.85f);
                GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), zoomBgTex);
                GUI.color = prevCol;

                Rect animCardRect = new Rect(
                    Mathf.Lerp(originRect.x, targetCardRect.x, smooth),
                    Mathf.Lerp(originRect.y, targetCardRect.y, smooth),
                    Mathf.Lerp(originRect.width, targetCardRect.width, smooth),
                    Mathf.Lerp(originRect.height, targetCardRect.height, smooth)
                );

                float shadowOffset = 18f * smooth;
                GUI.Box(new Rect(animCardRect.x + shadowOffset, animCardRect.y + shadowOffset, animCardRect.width, animCardRect.height), "", BoxBgStyle);
                DrawCardSpriteOnly(animCardRect, card);
            }

            // Panel lateral F5 con ataques, costes como iconos de energía, debilidad, resistencia y retirada
            if (smooth > 0.25f)
            {
                float panelAlpha = Mathf.Clamp01((smooth - 0.25f) / 0.75f);
                GUI.color = new Color(1f, 1f, 1f, panelAlpha);
                Rect infoBox = new Rect(targetCardRect.xMax + 24f, targetCardRect.y, 462f, targetH);
                GUI.Box(infoBox, "", BoxBgStyle);

                GUILayout.BeginArea(new Rect(infoBox.x + 20f, infoBox.y + 16f, infoBox.width - 40f, infoBox.height - 32f));

                // Cabecera: Nombre, Número y Orbe de Tipo + PS
                GUILayout.BeginHorizontal();
                GUILayout.Label($"<size=23><b>{card.Name}</b></size> <color=#9ecbff><size=14>({card.SetId} #{card.Number})</size></color>", HeaderStyle);
                GUILayout.FlexibleSpace();
                if (card.Supertype == Supertype.Pokemon)
                {
                    int curHp = pip != null ? pip.RemainingHp : card.Hp;
                    GUILayout.Label($"<size=18><color=#ff7b7b><b>{curHp}/{card.Hp} PS</b></color></size>", HeaderStyle);
                    GUILayout.Space(6);
                    DrawInlineEnergyOrb(card.Type, 24f);
                }
                else if (card.Supertype == Supertype.Energy && !string.IsNullOrEmpty(card.EnergyProvides))
                {
                    DrawInlineEnergyOrb(card.EnergyProvides, 24f);
                }
                GUILayout.EndHorizontal();

                GUILayout.Space(6);

                if (card.Supertype == Supertype.Pokemon)
                {
                    string stageTxt = $"<b>Etapa:</b> {card.Stage}" + (!string.IsNullOrEmpty(card.EvolvesFrom) ? $"  •  <i>Evoluciona de {card.EvolvesFrom}</i>" : "");
                    GUILayout.Label(stageTxt, ZoomTextStyle);

                    // Barra visual de PS
                    float hpRatio = Mathf.Clamp01((pip != null ? pip.RemainingHp : card.Hp) / (float)Math.Max(1, card.Hp));
                    Rect hpBar = GUILayoutUtility.GetRect(infoBox.width - 44f, 10f);
                    GUI.DrawTexture(hpBar, hpBgTex);
                    GUI.DrawTexture(new Rect(hpBar.x, hpBar.y, hpBar.width * hpRatio, hpBar.height), hpFillTex);

                    // Energías unidas físicamente como iconos de energía
                    if (pip != null && pip.Energy.Count > 0)
                    {
                        GUILayout.Space(6);
                        GUILayout.BeginHorizontal();
                        GUILayout.Label("<b>Energías unidas:</b>", ZoomTextStyle, GUILayout.Width(120f));
                        foreach (var e in pip.Energy)
                        {
                            DrawInlineEnergyOrb(e.EnergyProvides, 20f);
                            GUILayout.Space(3f);
                        }
                        GUILayout.FlexibleSpace();
                        GUILayout.EndHorizontal();
                    }

                    if (pip != null && (pip.Status != Status.None || pip.Poisoned || pip.DefenderUntilTurn >= 0))
                    {
                        GUILayout.Space(4);
                        GUILayout.Label($"<color=#ffd166><b>Estado en mesa:</b> {(pip.Status != Status.None ? pip.Status.ToString() : "Normal")} {(pip.Poisoned ? "[Envenenado]" : "")}</color>", ZoomTextStyle);
                    }

                    // Poké-Power / Habilidad
                    if (card.Power != null)
                    {
                        GUILayout.Space(8);
                        GUILayout.BeginVertical(SubBoxStyle);
                        GUILayout.Label($"<color=#ffd166><b>⚡ Poké-Power: {card.Power.Name}</b></color>", ZoomTextStyle);
                        GUILayout.Label(card.Power.Text, ZoomTextStyle);
                        GUILayout.EndVertical();
                    }

                    // Lista de Ataques con iconos de coste de energía
                    GUILayout.Space(8);
                    foreach (var atk in card.Attacks)
                    {
                        GUILayout.BeginVertical(SubBoxStyle);
                        GUILayout.BeginHorizontal();
                        if (atk.Cost != null && atk.Cost.Count > 0)
                        {
                            foreach (var cType in atk.Cost)
                            {
                                DrawInlineEnergyOrb(cType, 21f);
                                GUILayout.Space(3f);
                            }
                        }
                        else
                        {
                            GUILayout.Label("<color=#9ecbff>[0]</color>", ZoomTextStyle, GUILayout.Width(24f));
                        }
                        GUILayout.Space(6f);
                        GUILayout.Label($"<size=16><color=#80ed99><b>{atk.Name}</b></color></size>", ZoomTextStyle);
                        GUILayout.FlexibleSpace();
                        if (atk.BaseDamage > 0 || !string.IsNullOrEmpty(atk.DamageSuffix))
                        {
                            GUILayout.Label($"<size=17><color=#ffe066><b>{atk.BaseDamage}{atk.DamageSuffix}</b></color></size>", HeaderStyle);
                        }
                        GUILayout.EndHorizontal();

                        if (!string.IsNullOrEmpty(atk.Text))
                        {
                            GUILayout.Space(3f);
                            GUILayout.Label($"<color=#dce6f2><i>{atk.Text}</i></color>", ZoomTextStyle);
                        }
                        GUILayout.EndVertical();
                        GUILayout.Space(5);
                    }

                    GUILayout.FlexibleSpace();

                    // Barra inferior F5: Debilidad, Resistencia y Coste de Retirada con iconos de energía
                    GUILayout.BeginVertical(SubBoxStyle);
                    GUILayout.BeginHorizontal();

                    // Columna 1: Debilidad
                    GUILayout.BeginHorizontal(GUILayout.Width(132f));
                    GUILayout.Label("<b>Debilidad:</b>", ZoomTextStyle, GUILayout.Width(72f));
                    if (!string.IsNullOrEmpty(card.WeaknessType))
                    {
                        DrawInlineEnergyOrb(card.WeaknessType, 19f);
                        GUILayout.Space(3f);
                        GUILayout.Label("<b>×2</b>", ZoomTextStyle);
                    }
                    else
                    {
                        GUILayout.Label("—", ZoomTextStyle);
                    }
                    GUILayout.EndHorizontal();

                    // Columna 2: Resistencia
                    GUILayout.BeginHorizontal(GUILayout.Width(142f));
                    GUILayout.Label("<b>Resistencia:</b>", ZoomTextStyle, GUILayout.Width(84f));
                    if (!string.IsNullOrEmpty(card.ResistanceType))
                    {
                        DrawInlineEnergyOrb(card.ResistanceType, 19f);
                        GUILayout.Space(3f);
                        GUILayout.Label($"<b>-{card.ResistanceValue}</b>", ZoomTextStyle);
                    }
                    else
                    {
                        GUILayout.Label("—", ZoomTextStyle);
                    }
                    GUILayout.EndHorizontal();

                    // Columna 3: Coste de Retirada
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("<b>Retirada:</b>", ZoomTextStyle, GUILayout.Width(64f));
                    if (card.RetreatCost > 0)
                    {
                        for (int r = 0; r < card.RetreatCost; r++)
                        {
                            DrawInlineEnergyOrb("Colorless", 18f);
                            GUILayout.Space(2f);
                        }
                    }
                    else
                    {
                        GUILayout.Label("0", ZoomTextStyle);
                    }
                    GUILayout.EndHorizontal();

                    GUILayout.EndHorizontal();
                    GUILayout.EndVertical();
                }
                else
                {
                    GUILayout.Label($"<b>Categoría:</b> {card.Supertype}" + (!string.IsNullOrEmpty(card.EnergyProvides) ? $" ({card.EnergyProvides})" : ""), ZoomTextStyle);
                    GUILayout.Space(10);
                    if (!string.IsNullOrEmpty(card.RulesText))
                    {
                        GUILayout.BeginVertical(SubBoxStyle);
                        GUILayout.Label(card.RulesText, ZoomTextStyle);
                        GUILayout.EndVertical();
                    }
                    GUILayout.FlexibleSpace();
                }

                if (extraFooterUI != null)
                {
                    GUILayout.Space(6);
                    extraFooterUI.Invoke();
                }

                GUILayout.Space(6);
                GUILayout.Label("<color=#aebcd0><i>Mueve el ratón para inclinar la carta 3D  •  Clic fuera o Esc para cerrar</i></color>", SubHeaderStyle);
                GUILayout.EndArea();
                GUI.color = prevCol;
            }

            if (Event.current.type == EventType.MouseDown)
            {
                Event.current.Use();
                return true;
            }
            return false;
        }

        // ==================== Helpers de Dibujo de Cartas ====================
        public static void DrawCardSpriteOnly(Rect rect, CardDef c)
        {
            if (c == null) return;
            var sp = SpriteCache.Get(c.Sprite);
            if (sp != null && sp.texture != null)
            {
                var t = sp.texture;
                var tr = sp.textureRect;
                GUI.DrawTextureWithTexCoords(rect, t, new Rect(tr.x / t.width, tr.y / t.height, tr.width / t.width, tr.height / t.height));
            }
            else
            {
                GUI.Box(rect, c.Name);
            }
        }

        public static void DrawCardBackRect(Rect rect)
        {
            var cb = SpriteCache.GetCardBack();
            if (cb != null && cb.texture != null)
            {
                var t = cb.texture;
                var tr = cb.textureRect;
                GUI.DrawTextureWithTexCoords(rect, t, new Rect(tr.x / t.width, tr.y / t.height, tr.width / t.width, tr.height / t.height));
            }
            else
            {
                GUI.Box(rect, "POKÉMON");
            }
        }

        /// <summary>Dibuja una carta y devuelve 0 (clic izquierdo), 1 (clic derecho / zoom) o -1.</summary>
        public static int DrawCardWithInput(Rect rect, CardDef c, bool highlight, bool allowInput)
        {
            EnsureStyles();
            if (highlight)
                GUI.DrawTexture(new Rect(rect.x - 4f, rect.y - 4f, rect.width + 8f, rect.height + 8f), highlightTex);

            DrawCardSpriteOnly(rect, c);

            if (allowInput && Event.current.type == EventType.MouseDown && rect.Contains(Event.current.mousePosition))
            {
                int b = Event.current.button;
                Event.current.Use();
                return b;
            }
            return -1;
        }

        // ==================== F4: Interacciones 3D (Mano en Abanico, Hover, Drag & Drop y Raycast 3D) ====================
        bool IsMouseOverHud(Vector2 guiMouse)
        {
            // Botones laterales izquierdos ("Opciones" y "Levantarse")
            Rect leftBtns = new Rect(20f, 98f, 184f, 114f);
            if (leftBtns.Contains(guiMouse)) return true;

            bool hasRightPanel = game != null && (
                                     (game.Phase == Phase.ChoosePromotion && game.PromotionPending == Me) ||
                                     (game.Phase == Phase.Main && game.Current == Me && Me.Active != null && !IsAnimBusy()));
            if (hasRightPanel)
            {
                float panelW = 248f;
                float panelH = 340f;
                Rect rightPanel = new Rect(Screen.width - panelW - 14f, 46f, panelW, panelH);
                if (rightPanel.Contains(guiMouse)) return true;
            }

            return false;
        }

        void Update3DInput()
        {
            var tv = TableView.Instance;
            if (tv == null || !tv.IsBoard3DReady || game == null) return;

            bool animBusy = IsAnimBusy();
            bool allow3DInput = zoomedCard == null && !showSettingsModal && game.Phase != Phase.CoinToss && game.Phase != Phase.GameOver &&
                                !startCoinSpinning && !animBusy && !game.PendingAiTurn;

            if (!allow3DInput)
            {
                Reset3DInputState();
                tv.UpdateSlotHighlights3D(null, null);
                return;
            }

            var cam = TableView.GetActiveCamera();
            if (cam == null) return;

            Vector2 guiMouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            bool overHud = IsMouseOverHud(guiMouse);
            Ray ray = cam.ScreenPointToRay(Input.mousePosition);

            if (!isDragging3D)
            {
                if (!overHud)
                {
                    if (tv.RaycastHandCard3D(ray, out int hIdx, out _) && hIdx >= 0 && hIdx < Me.Hand.Count)
                    {
                        if (hoveredHandIdx != hIdx)
                        {
                            hoveredHandIdx = hIdx;
                            PlayClickSound(0);
                        }
                        hoveredSlot3D = null;
                        hoveredBoardCard3D = null;
                    }
                    else
                    {
                        hoveredHandIdx = -1;
                        tv.RaycastBoard3D(ray, out hoveredSlot3D, out hoveredBoardCard3D);
                    }
                }
                else
                {
                    hoveredHandIdx = -1;
                    hoveredSlot3D = null;
                    hoveredBoardCard3D = null;
                }
            }
            else
            {
                tv.TryGetDragWorldPos(ray, out currentDragWorldPos);
                if (tv.RaycastHandCard3D(ray, out int overHandIdx, out _) && overHandIdx >= 0)
                {
                    hoveredSlot3D = null;
                    hoveredBoardCard3D = null;
                }
                else
                {
                    tv.RaycastBoard3D(ray, out hoveredSlot3D, out hoveredBoardCard3D);
                }
            }

            // Actualizar casillas iluminadas en el tapete 3D
            int activeCardIdx = isDragging3D ? pressedHandIdx : (selHand >= 0 ? selHand : hoveredHandIdx);
            ComputeValidTargetAnchors(activeCardIdx);
            Transform hoveredAnchor = hoveredSlot3D != null ? hoveredSlot3D.Anchor : null;
            tv.UpdateSlotHighlights3D(validTargetAnchors, hoveredAnchor);

            // --- Clic Izquierdo y Arrastre 3D (Select + Drag & Drop) ---
            if (Input.GetMouseButtonDown(0) && !overHud)
            {
                if (hoveredHandIdx >= 0 && hoveredHandIdx < Me.Hand.Count)
                {
                    pressedHandIdx = hoveredHandIdx;
                    pressMousePos = Input.mousePosition;
                    isDragging3D = false;
                    tv.TryGetDragWorldPos(ray, out currentDragWorldPos);
                }
                else if (hoveredSlot3D != null || hoveredBoardCard3D != null)
                {
                    On3DBoardLeftClick(hoveredSlot3D, hoveredBoardCard3D);
                }
                else
                {
                    selHand = -1;
                }
            }

            if (Input.GetMouseButton(0) && pressedHandIdx >= 0 && pressedHandIdx < Me.Hand.Count)
            {
                if (!isDragging3D && Vector2.Distance(Input.mousePosition, pressMousePos) > 7f)
                {
                    isDragging3D = true;
                    selHand = pressedHandIdx;
                    retreatMode = false;
                    var c = Me.Hand[pressedHandIdx];
                    message = $"Arrastrando: {c.Name} — {HintFor(c)}";
                }
            }

            if (Input.GetMouseButtonUp(0) && pressedHandIdx >= 0)
            {
                int idx = pressedHandIdx;
                bool wasDragging = isDragging3D;
                pressedHandIdx = -1;
                isDragging3D = false;

                if (idx >= 0 && idx < Me.Hand.Count)
                {
                    if (wasDragging)
                    {
                        TryDropHandCard3D(idx, hoveredSlot3D, currentDragWorldPos);
                    }
                    else
                    {
                        Vector3 handWorldPos = tv.GetHandCardWorldPos(0, idx, Me.Hand.Count);
                        BoardAnimator.Instance?.SetNextHandOrigin(handWorldPos);
                        PlayClickSound(0);
                        OnHandLeftClicked(idx);
                    }
                }
            }

            // --- Clic Derecho en 3D = Inspeccionar cualquier carta ---
            if (Input.GetMouseButtonDown(1) && !overHud)
            {
                if (isDragging3D)
                {
                    isDragging3D = false;
                    pressedHandIdx = -1;
                    PlayClickSound(2);
                    return;
                }

                if (hoveredHandIdx >= 0 && hoveredHandIdx < Me.Hand.Count)
                {
                    var c = Me.Hand[hoveredHandIdx];
                    Vector3 cardWorldPos = tv.GetHandCardWorldPos(0, hoveredHandIdx, Me.Hand.Count);
                    Quaternion cardWorldRot = tv.GetHandCardWorldRot(0, hoveredHandIdx, Me.Hand.Count);
                    if (!tv.TryProjectWorldPosToGUI(cardWorldPos, 1.05f, slotW, slotH, out Rect originRect))
                        originRect = GetHandCenterRect(0);
                    PlayClickSound(0);
                    StartZoom(c, null, originRect, cardWorldPos, cardWorldRot, 1.02f);
                }
                else if (hoveredBoardCard3D != null && hoveredBoardCard3D.BoundPip != null)
                {
                    var pip = hoveredBoardCard3D.BoundPip;
                    int pOwner = Mathf.Clamp(hoveredBoardCard3D.OwnerIndex, 0, 1);
                    Rect originRect = game.Players[pOwner].Active == pip
                        ? GetActiveRect(pOwner)
                        : GetBenchRect(pOwner, Mathf.Max(0, game.Players[pOwner].Bench.IndexOf(pip)));
                    PlayClickSound(0);
                    StartZoom(pip.Card, pip, originRect, hoveredBoardCard3D.transform.position, hoveredBoardCard3D.transform.rotation, hoveredBoardCard3D.CurrentScale);
                }
                else if (hoveredBoardCard3D != null && hoveredBoardCard3D.IsDiscardTop)
                {
                    int pOwner = Mathf.Clamp(hoveredBoardCard3D.OwnerIndex, 0, 1);
                    PlayClickSound(0);
                    StartZoom(hoveredBoardCard3D.Card, null, GetTrashRect(pOwner), hoveredBoardCard3D.transform.position, hoveredBoardCard3D.transform.rotation, 1f);
                }
                else if (hoveredSlot3D != null)
                {
                    int pOwner = Mathf.Clamp(hoveredSlot3D.PlayerIndex, 0, 1);
                    Transform slotAnchor = hoveredSlot3D.Anchor;
                    Vector3 slotPos = slotAnchor != null ? slotAnchor.position : Vector3.zero;
                    Quaternion slotRot = slotAnchor != null ? slotAnchor.rotation : Quaternion.identity;
                    if (hoveredSlot3D.Kind == TableSlotKind.Discard)
                    {
                        var disc = game.Players[pOwner].Discard;
                        if (disc.Count > 0)
                        {
                            PlayClickSound(0);
                            StartZoom(disc[disc.Count - 1], null, GetTrashRect(pOwner), slotPos, slotRot, 1f);
                        }
                    }
                    else if (hoveredSlot3D.Kind == TableSlotKind.Active)
                    {
                        var pip = game.Players[pOwner].Active;
                        if (pip != null)
                        {
                            PlayClickSound(0);
                            StartZoom(pip.Card, pip, GetActiveRect(pOwner), slotPos, slotRot, 1.08f);
                        }
                    }
                    else if (hoveredSlot3D.Kind == TableSlotKind.Bench)
                    {
                        int bIdx = hoveredSlot3D.BenchIndex;
                        var bench = game.Players[pOwner].Bench;
                        if (bIdx >= 0 && bIdx < bench.Count)
                        {
                            PlayClickSound(0);
                            StartZoom(bench[bIdx].Card, bench[bIdx], GetBenchRect(pOwner, bIdx), slotPos, slotRot, 0.94f);
                        }
                    }
                }
            }
        }

        bool CanEvolveTarget(CardDef evoCard, PokemonInPlay target)
        {
            if (evoCard == null || target == null || target.IsDoll) return false;
            if (evoCard.Supertype != Supertype.Pokemon || evoCard.Stage == "Basic") return false;
            if (evoCard.EvolvesFrom != target.Card.Name) return false;
            if (game.Turn <= 2 || target.TurnPlaced == game.Turn || target.TurnEvolved == game.Turn) return false;
            return true;
        }

        static bool IsGlobalTrainer(CardDef c)
        {
            if (c == null || c.Supertype != Supertype.Trainer) return false;
            switch (c.Name)
            {
                case "Potion":
                case "Super Potion":
                case "Defender":
                case "Scoop Up":
                case "Devolution Spray":
                case "Pokémon Breeder":
                case "Gust of Wind":
                case "Energy Removal":
                case "Super Energy Removal":
                    return false;
                default:
                    return true;
            }
        }

        void ComputeValidTargetAnchors(int handIdx)
        {
            validTargetAnchors.Clear();
            var tv = TableView.Instance;
            if (tv == null || game == null) return;

            if (game.Phase == Phase.Setup)
            {
                if (handIdx >= 0 && handIdx < Me.Hand.Count && !Me.Hand[handIdx].IsBasicPokemon)
                    return;

                if (Me.Active == null)
                {
                    if (tv.ActiveAnchor[0] != null) validTargetAnchors.Add(tv.ActiveAnchor[0]);
                }
                else if (Me.Bench.Count < 5)
                {
                    for (int i = Me.Bench.Count; i < 5; i++)
                        if (tv.BenchAnchor[0, i] != null) validTargetAnchors.Add(tv.BenchAnchor[0, i]);
                }
                return;
            }

            if (game.Phase == Phase.ChoosePromotion && game.PromotionPending == Me)
            {
                for (int i = 0; i < Me.Bench.Count; i++)
                    if (tv.BenchAnchor[0, i] != null) validTargetAnchors.Add(tv.BenchAnchor[0, i]);
                return;
            }

            if (retreatMode && game.Phase == Phase.Main && game.Current == Me)
            {
                for (int i = 0; i < Me.Bench.Count; i++)
                    if (tv.BenchAnchor[0, i] != null) validTargetAnchors.Add(tv.BenchAnchor[0, i]);
                return;
            }

            if (game.Phase != Phase.Main || game.Current != Me) return;
            if (handIdx < 0 || handIdx >= Me.Hand.Count) return;

            var c = Me.Hand[handIdx];
            if (c.IsBasicPokemon)
            {
                if (Me.Bench.Count < 5)
                {
                    for (int i = Me.Bench.Count; i < 5; i++)
                        if (tv.BenchAnchor[0, i] != null) validTargetAnchors.Add(tv.BenchAnchor[0, i]);
                }
            }
            else if (c.Supertype == Supertype.Pokemon && c.Stage != "Basic")
            {
                if (CanEvolveTarget(c, Me.Active) && tv.ActiveAnchor[0] != null)
                    validTargetAnchors.Add(tv.ActiveAnchor[0]);
                for (int i = 0; i < Me.Bench.Count; i++)
                    if (CanEvolveTarget(c, Me.Bench[i]) && tv.BenchAnchor[0, i] != null)
                        validTargetAnchors.Add(tv.BenchAnchor[0, i]);
            }
            else if (c.Supertype == Supertype.Energy)
            {
                if (!Me.EnergyPlayedThisTurn)
                {
                    if (Me.Active != null && !Me.Active.IsDoll && tv.ActiveAnchor[0] != null)
                        validTargetAnchors.Add(tv.ActiveAnchor[0]);
                    for (int i = 0; i < Me.Bench.Count; i++)
                        if (!Me.Bench[i].IsDoll && tv.BenchAnchor[0, i] != null)
                            validTargetAnchors.Add(tv.BenchAnchor[0, i]);
                }
            }
            else if (c.Supertype == Supertype.Trainer)
            {
                switch (c.Name)
                {
                    case "Potion":
                    case "Super Potion":
                        if (Me.Active != null && Me.Active.Damage > 0 && (c.Name == "Potion" || Me.Active.Energy.Count > 0))
                            validTargetAnchors.Add(tv.ActiveAnchor[0]);
                        for (int i = 0; i < Me.Bench.Count; i++)
                            if (Me.Bench[i].Damage > 0 && (c.Name == "Potion" || Me.Bench[i].Energy.Count > 0))
                                validTargetAnchors.Add(tv.BenchAnchor[0, i]);
                        break;

                    case "Defender":
                    case "Scoop Up":
                        if (Me.Active != null && (c.Name != "Scoop Up" || Me.Bench.Count > 0))
                            validTargetAnchors.Add(tv.ActiveAnchor[0]);
                        for (int i = 0; i < Me.Bench.Count; i++)
                            validTargetAnchors.Add(tv.BenchAnchor[0, i]);
                        break;

                    case "Devolution Spray":
                        if (Me.Active != null && Me.Active.Stack.Count >= 2)
                            validTargetAnchors.Add(tv.ActiveAnchor[0]);
                        for (int i = 0; i < Me.Bench.Count; i++)
                            if (Me.Bench[i].Stack.Count >= 2)
                                validTargetAnchors.Add(tv.BenchAnchor[0, i]);
                        break;

                    case "Switch":
                        for (int i = 0; i < Me.Bench.Count; i++)
                            validTargetAnchors.Add(tv.BenchAnchor[0, i]);
                        break;

                    case "Gust of Wind":
                        for (int i = 0; i < Cpu.Bench.Count; i++)
                            validTargetAnchors.Add(tv.BenchAnchor[1, i]);
                        break;

                    case "Energy Removal":
                    case "Super Energy Removal":
                        if (Cpu.Active != null && Cpu.Active.Energy.Count > 0)
                            validTargetAnchors.Add(tv.ActiveAnchor[1]);
                        for (int i = 0; i < Cpu.Bench.Count; i++)
                            if (Cpu.Bench[i].Energy.Count > 0)
                                validTargetAnchors.Add(tv.BenchAnchor[1, i]);
                        break;

                    case "PlusPower":
                    case "Full Heal":
                        if (Me.Active != null && tv.ActiveAnchor[0] != null)
                            validTargetAnchors.Add(tv.ActiveAnchor[0]);
                        if (tv.CoinAnchor != null)
                            validTargetAnchors.Add(tv.CoinAnchor);
                        break;

                    case "Revive":
                    case "Clefairy Doll":
                        if (Me.Bench.Count < 5)
                        {
                            for (int i = Me.Bench.Count; i < 5; i++)
                                validTargetAnchors.Add(tv.BenchAnchor[0, i]);
                        }
                        if (tv.CoinAnchor != null)
                            validTargetAnchors.Add(tv.CoinAnchor);
                        break;

                    default:
                        if (tv.CoinAnchor != null) validTargetAnchors.Add(tv.CoinAnchor);
                        if (tv.ActiveAnchor[0] != null) validTargetAnchors.Add(tv.ActiveAnchor[0]);
                        break;
                }
            }
        }

        PokemonInPlay ResolvePipFromSlotOrCard(TableSlot3D slot, CardView boardCard, out PlayerState owner)
        {
            owner = null;
            if (boardCard != null && boardCard.BoundPip != null)
            {
                int p = Mathf.Clamp(boardCard.OwnerIndex, 0, 1);
                owner = game.Players[p];
                return boardCard.BoundPip;
            }
            if (slot != null)
            {
                int p = Mathf.Clamp(slot.PlayerIndex, 0, 1);
                owner = game.Players[p];
                if (slot.Kind == TableSlotKind.Active)
                    return owner.Active;
                if (slot.Kind == TableSlotKind.Bench && slot.BenchIndex >= 0 && slot.BenchIndex < owner.Bench.Count)
                    return owner.Bench[slot.BenchIndex];
            }
            return null;
        }

        bool IsWorldPosOverPlaymat(Vector3 worldPos)
        {
            var tv = TableView.Instance;
            if (tv == null || tv.PlaymatRoot == null) return false;
            Vector3 local = tv.PlaymatRoot.InverseTransformPoint(worldPos);
            return Mathf.Abs(local.x) <= TableView.MatWidth * 0.48f &&
                   local.z >= -TableView.MatLength * 0.36f &&
                   local.z <= TableView.MatLength * 0.48f;
        }

        void TryDropHandCard3D(int handIdx, TableSlot3D dropSlot, Vector3 dropWorldPos)
        {
            if (handIdx < 0 || handIdx >= Me.Hand.Count) return;
            var c = Me.Hand[handIdx];

            // 1) En Fase de Preparación (Setup)
            if (game.Phase == Phase.Setup)
            {
                bool onPlayerSide = (dropSlot != null && dropSlot.PlayerIndex == 0 && (dropSlot.Kind == TableSlotKind.Active || dropSlot.Kind == TableSlotKind.Bench))
                                 || IsWorldPosOverPlaymat(dropWorldPos);
                if (onPlayerSide)
                {
                    BoardAnimator.Instance?.SetNextHandOrigin(dropWorldPos);
                    PlayClickSound(1);
                    Report(game.SetupPlace(Me, handIdx));
                    return;
                }
                PlayClickSound(0);
                return;
            }

            // 2) En Fase Principal (Main)
            if (game.Phase == Phase.Main && game.Current == Me)
            {
                var targetPip = ResolvePipFromSlotOrCard(dropSlot, hoveredBoardCard3D, out PlayerState owner);

                if (c.IsBasicPokemon)
                {
                    if ((dropSlot != null && dropSlot.PlayerIndex == 0 && (dropSlot.Kind == TableSlotKind.Bench || dropSlot.Kind == TableSlotKind.Active))
                        || IsWorldPosOverPlaymat(dropWorldPos))
                    {
                        BoardAnimator.Instance?.SetNextHandOrigin(dropWorldPos);
                        PlayClickSound(1);
                        Report(game.PlayBasic(Me, handIdx));
                        return;
                    }
                }
                else if (c.Supertype == Supertype.Energy)
                {
                    if (targetPip != null && owner == Me)
                    {
                        BoardAnimator.Instance?.SetNextHandOrigin(dropWorldPos);
                        PlayClickSound(1);
                        Report(game.AttachEnergy(Me, handIdx, targetPip));
                        return;
                    }
                }
                else if (c.Supertype == Supertype.Pokemon && c.Stage != "Basic")
                {
                    if (targetPip != null && owner == Me)
                    {
                        BoardAnimator.Instance?.SetNextHandOrigin(dropWorldPos);
                        PlayClickSound(1);
                        Report(game.Evolve(Me, handIdx, targetPip));
                        return;
                    }
                }
                else if (c.Supertype == Supertype.Trainer)
                {
                    if (targetPip != null)
                    {
                        BoardAnimator.Instance?.SetNextHandOrigin(dropWorldPos);
                        PlayClickSound(1);
                        Report(game.PlayTrainer(Me, handIdx, targetPip));
                        return;
                    }
                    if (IsGlobalTrainer(c) && (dropSlot != null || IsWorldPosOverPlaymat(dropWorldPos)))
                    {
                        BoardAnimator.Instance?.SetNextHandOrigin(dropWorldPos);
                        PlayClickSound(1);
                        Report(game.PlayTrainer(Me, handIdx, null));
                        return;
                    }
                }
            }

            // Si se soltó fuera de un objetivo válido, la carta regresa suavemente al abanico 3D de la mano
            PlayClickSound(0);
        }

        void On3DBoardLeftClick(TableSlot3D slot, CardView boardCard)
        {
            var pip = ResolvePipFromSlotOrCard(slot, boardCard, out PlayerState owner);

            if (game.Phase == Phase.ChoosePromotion && game.PromotionPending == Me)
            {
                if (pip != null && owner == Me && Me.Bench.Contains(pip))
                {
                    PlayClickSound(1);
                    Report(game.Promote(Me, Me.Bench.IndexOf(pip)));
                }
                return;
            }

            if (retreatMode && game.Phase == Phase.Main && game.Current == Me)
            {
                if (pip != null && owner == Me && Me.Bench.Contains(pip))
                {
                    retreatMode = false;
                    PlayClickSound(1);
                    Report(game.Retreat(Me, Me.Bench.IndexOf(pip)));
                }
                return;
            }

            if (selHand >= 0 && selHand < Me.Hand.Count)
            {
                Vector3 handOrigin = TableView.Instance != null
                    ? TableView.Instance.GetHandCardWorldPos(0, selHand, Me.Hand.Count)
                    : default;

                if (game.Phase == Phase.Setup && slot != null && slot.PlayerIndex == 0 &&
                    (slot.Kind == TableSlotKind.Active || slot.Kind == TableSlotKind.Bench))
                {
                    BoardAnimator.Instance?.SetNextHandOrigin(handOrigin);
                    PlayClickSound(1);
                    Report(game.SetupPlace(Me, selHand));
                    return;
                }

                if (game.Phase == Phase.Main && game.Current == Me)
                {
                    var c = Me.Hand[selHand];
                    if (c.IsBasicPokemon && slot != null && slot.PlayerIndex == 0 && slot.Kind == TableSlotKind.Bench)
                    {
                        BoardAnimator.Instance?.SetNextHandOrigin(handOrigin);
                        PlayClickSound(1);
                        Report(game.PlayBasic(Me, selHand));
                        return;
                    }

                    if (pip != null)
                    {
                        BoardAnimator.Instance?.SetNextHandOrigin(handOrigin);
                        PlayClickSound(1);
                        OnPokemonLeftClicked(owner, pip);
                        return;
                    }

                    if (c.Supertype == Supertype.Trainer && IsGlobalTrainer(c) && slot != null &&
                        (slot.Kind == TableSlotKind.PlayfieldCenter || slot.Kind == TableSlotKind.Active || slot.Kind == TableSlotKind.Bench))
                    {
                        BoardAnimator.Instance?.SetNextHandOrigin(handOrigin);
                        PlayClickSound(1);
                        Report(game.PlayTrainer(Me, selHand, null));
                        return;
                    }
                }
            }
            else if (pip != null)
            {
                OnPokemonLeftClicked(owner, pip);
            }
        }

        // ==================== Interacciones del Jugador ====================
        void OnHandLeftClicked(int i)
        {
            if (game.Phase == Phase.Setup) { Report(game.SetupPlace(Me, i)); return; }
            selHand = selHand == i ? -1 : i;
            if (selHand >= 0 && selHand < Me.Hand.Count)
            {
                var c = Me.Hand[selHand];
                message = $"Seleccionada: {c.Name} — {HintFor(c)}";
            }
        }

        void OnPokemonLeftClicked(PlayerState owner, PokemonInPlay pip)
        {
            if (game.Phase == Phase.ChoosePromotion && owner == Me && Me.Bench.Contains(pip)) { Report(game.Promote(Me, Me.Bench.IndexOf(pip))); return; }
            if (retreatMode && owner == Me && Me.Bench.Contains(pip)) { retreatMode = false; Report(game.Retreat(Me, Me.Bench.IndexOf(pip))); return; }
            if (selHand < 0 || selHand >= Me.Hand.Count) return;
            var c = Me.Hand[selHand];
            if (c.Supertype == Supertype.Energy && owner == Me) Report(game.AttachEnergy(Me, selHand, pip));
            else if (c.Supertype == Supertype.Pokemon && owner == Me) Report(game.Evolve(Me, selHand, pip));
            else if (c.Supertype == Supertype.Trainer) Report(game.PlayTrainer(Me, selHand, pip));
        }

        string HintFor(CardDef c)
        {
            if (c.Supertype == Supertype.Energy) return "Arrástrala o haz clic en uno de tus Pokémon para unirla debajo.";
            if (c.Supertype == Supertype.Pokemon && c.Stage != "Basic") return $"Arrástrala o haz clic en tu {c.EvolvesFrom} para evolucionarlo.";
            if (c.Supertype == Supertype.Trainer) return IsGlobalTrainer(c) ? "Arrástrala al tapete o pulsa 'Usar Entrenador'." : "Arrástrala o haz clic en el Pokémon objetivo.";
            if (c.IsBasicPokemon) return game.Phase == Phase.Setup ? "Arrástralo al puesto iluminado o haz clic para colocarlo." : "Arrástralo a una casilla libre de tu Banca o pulsa 'Bajar a la Banca'.";
            return "";
        }

        void Report(string err)
        {
            message = err ?? "";
            if (err == null) selHand = -1;
        }
    }
}
