using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

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
        bool showLogPanel;
        Vector2 logScroll;

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

        PlayerState Me => game.Players[0];
        PlayerState Cpu => game.Players[1];

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

                // El mazo del NPC se genera aleatoriamente en cada partida, con el mismo formato
                List<CardDef> npcDeck = DeckLoader.GenerateRandomNpcDeck(null, format);

                flightQueue.Clear();
                currentFlight = null;
                BoardAnimator.Instance?.Clear();
                game = new Game(playerDeck, npcDeck);
                game.Players[1].Name = "Cliente";
                game.OnAnim = OnGameAnimEvent;

                selHand = -1;
                retreatMode = false;
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
            Reset3DInputState();
            zoomedCard = null;
            zoomedPip = null;
            flightQueue.Clear();
            currentFlight = null;
            BoardAnimator.Instance?.Clear();
        }

        void LeaveTable()
        {
            bool finished = game != null && game.Phase == Phase.GameOver;
            bool won = finished && game.Winner == Me;
            open = false;
            game = null;
            Reset3DInputState();
            zoomedCard = null;
            zoomedPip = null;
            flightQueue.Clear();
            currentFlight = null;
            BoardAnimator.Instance?.Clear();
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
                PlayTableSession.UpdateWaiting();
                InputManager.SetCursorLockMode(CursorLockMode.None);
                Cursor.visible = true;
                if (Input.GetKeyDown(KeyCode.Escape) || Input.GetMouseButtonDown(1))
                    LeaveTable();
                return;
            }

            if (!open || game == null)
            {
                if (PlayTableSession.CurrentTable != null)
                    LeaveTable();
                return;
            }

            InputManager.SetCursorLockMode(CursorLockMode.None);
            Cursor.visible = true;

            ComputePlaymatGeometry();

            // F4: Procesar Raycast 3D de Mano y Mesa (Hover, Selección, Drag & Drop 3D y Clic Derecho de Inspección)
            if (TableView.Instance != null && TableView.Instance.IsBoard3DReady)
            {
                Update3DInput();
            }

            // F2, F4 & F5: Sincronizar cartas 3D físicas, Mano 3D en abanico, Carta arrastrada e Inspección 3D
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
                zoomOriginScale);

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
                startCoinTimer += Time.deltaTime;
                // Solo cuando la moneda ya cayó sobre la mesa y mostró su resultado (t >= 0.88),
                // pasamos a Fase de Preparación, repartimos la mano inicial y, si el NPC ganó, prepara su jugada tras el reparto.
                if (startCoinTimer >= StartCoinDuration * 0.88f && !startCoinDone)
                {
                    startCoinDone = true;
                    game.OnStartCoinLanded(startCoinResultHeads);
                    message = "Preparación: haz clic izquierdo o arrastra tus Pokémon Básicos para colocarlos y pulsa '¡Empezar Partida!'.";
                }
                if (startCoinTimer >= StartCoinDuration + 0.85f)
                {
                    startCoinSpinning = false;
                }
            }

            // Actualizar vuelo de cartas sobre el tapete
            if (currentFlight != null)
            {
                currentFlight.Elapsed += Time.deltaTime;
                if (currentFlight.Elapsed >= currentFlight.Duration)
                    currentFlight = null;
            }
            if (currentFlight == null && flightQueue.Count > 0)
            {
                currentFlight = flightQueue.Dequeue();
            }

            // Ejecutar colocación inicial o turno de la IA cuando terminen las animaciones 3D y 2D en curso
            bool animBusy = currentFlight != null || flightQueue.Count > 0 || (BoardAnimator.Instance != null && BoardAnimator.Instance.IsAnimating);
            if (game.PendingAiSetup && !animBusy && !startCoinSpinning)
            {
                aiSetupDelay += Time.deltaTime;
                if (aiSetupDelay >= 0.30f)
                {
                    aiSetupDelay = 0f;
                    game.ExecutePendingAiSetup();
                }
            }
            if (game.PendingAiTurn && !animBusy && !startCoinSpinning)
            {
                aiTurnDelay += Time.deltaTime;
                if (aiTurnDelay >= 0.55f)
                {
                    aiTurnDelay = 0f;
                    game.ExecutePendingAiTurn();
                }
            }

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                if (zoomedCard != null) zoomClosing = true;
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
            return playerIdx == 0 ? NormSlot(0.895f, 0.635f) : NormSlot(0.105f, 0.335f);
        }

        Rect GetTrashRect(int playerIdx)
        {
            var tv = TableView.Instance;
            if (tv != null && tv.TryProjectSlotToGUI(tv.DiscardAnchor[playerIdx], 1f, slotW, slotH, out Rect r))
                return r;
            return playerIdx == 0 ? NormSlot(0.895f, 0.835f) : NormSlot(0.105f, 0.130f);
        }

        Rect GetSideBoxRect(int playerIdx, int boxIdx) // boxIdx = 0, 1, 2
        {
            int b = Mathf.Clamp(boxIdx, 0, 2);
            var tv = TableView.Instance;
            if (tv != null && tv.TryProjectSlotToGUI(tv.PrizeBoxAnchor[playerIdx, b], 0.86f, slotW, slotH, out Rect r))
                return r;
            if (playerIdx == 0)
            {
                float[] ys = { 0.525f, 0.685f, 0.845f };
                return NormSlot(0.105f, ys[b], 0.86f);
            }
            else
            {
                float[] ys = { 0.115f, 0.275f, 0.435f };
                return NormSlot(0.895f, ys[b], 0.86f);
            }
        }
        Rect GetHandCenterRect(int playerIdx) =>
            playerIdx == 0
                ? new Rect(Screen.width * 0.5f - slotW * 0.5f, Screen.height - slotH - 8f, slotW, slotH)
                : new Rect(Screen.width * 0.5f - slotW * 0.4f, 6f, slotW * 0.8f, slotH * 0.8f);

        void OnGameAnimEvent(AnimEvent ev)
        {
            if (ev.Kind == AnimKind.CoinFlip)
            {
                subCoinHeads = ev.CoinHeads;
                subCoinLabel = $"{ev.Label}: {(ev.CoinHeads ? "CARA" : "CRUZ")}";
                subCoinTimer = 1.6f;
                return;
            }

            // F3: Si el tablero 3D y el BoardAnimator están activos, todas las cartas vuelan físicamente en 3D sobre la mesa
            if (TableView.Instance != null && TableView.Instance.IsBoard3DReady && BoardAnimator.Instance != null)
            {
                BoardAnimator.Instance.EnqueueEvent(ev, game);
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
                               BtnStyle, BtnGreenStyle, BtnGoldStyle, BtnRedStyle, ToastStyle, BadgeStyle, DurationBadgeStyle;
        static Texture2D darkTex, panelTex, subPanelTex, inputTex, highlightTex, zoomBgTex, hpBgTex, hpFillTex, sleepTex, confTex, paraTex, poisTex, defTex, countTex;
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
                sleepTex = MakeTex(new Color(0.20f, 0.48f, 0.92f, 0.95f));
                confTex = MakeTex(new Color(0.92f, 0.55f, 0.12f, 0.95f));
                paraTex = MakeTex(new Color(0.90f, 0.76f, 0.10f, 0.95f));
                poisTex = MakeTex(new Color(0.66f, 0.20f, 0.85f, 0.95f));
                defTex = MakeTex(new Color(0.15f, 0.72f, 0.85f, 0.95f));
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
        void OnGUI()
        {
            EnsureStyles();
            GUI.depth = -1000;

            if (toastTimer > 0f && !string.IsNullOrEmpty(toastMessage))
            {
                GUI.Box(new Rect((Screen.width - 520) * 0.5f, 40, 520, 48), toastMessage, ToastStyle);
            }

            if (PlayTableSession.IsWaitingForNpc)
            {
                float ww = 660f, wh = 135f;
                Rect wr = new Rect((Screen.width - ww) * 0.5f, (Screen.height - wh) * 0.5f, ww, wh);
                GUI.Box(wr, "", BoxBgStyle);
                GUILayout.BeginArea(new Rect(wr.x + 22, wr.y + 16, wr.width - 44, wr.height - 32));
                GUILayout.Label("<size=19><b>Sentado en la mesa de juego (Playmat 3D listo)</b></size>", HeaderStyle);
                GUILayout.Space(4);
                GUILayout.Label("Esperando a que un cliente se acerque y tome asiento frente a ti...", SubHeaderStyle);
                GUILayout.Space(12);
                if (GUILayout.Button("Levantarse de la mesa (Clic derecho / Esc)", BtnRedStyle, GUILayout.Height(34), GUILayout.Width(340)))
                {
                    PlayClickSound(2);
                    LeaveTable();
                }
                GUILayout.EndArea();
                return;
            }

            if (!open || game == null) return;

            ComputePlaymatGeometry();
            Update3DSlotHighlights();

            // 1. Tapete 3D sobre la mesa (solo dibuja respaldo 2D si TableView aún no está activo)
            DrawPlaymatSurface();

            // 2. Barra superior compacta
            DrawTopBar();

            // 3. Dibujar todas las zonas alineadas a los anclajes 3D del Playmat
            DrawAllPlaymatZones();

            // 4. Mano del NPC (arriba boca abajo) y Mano del Jugador (abajo)
            DrawOpponentHand();
            DrawPlayerHand();

            // 5. Controles de Acción (Ataques, Retirada, Fin de turno) a la derecha del tapete
            DrawSideControls();

            // 6. Vuelo animado de cartas (movimientos propios y del NPC)
            DrawCurrentFlight();

            // 7. Moneda secundaria (ataques / estados)
            if (subCoinTimer > 0f)
                DrawSubCoinFlip();

            // 8. Fase de Moneda Inicial antes de iniciar la partida (se oculta el modal mientras gira la moneda)
            if (game.Phase == Phase.CoinToss && !startCoinSpinning)
                DrawInitialCoinTossModal();

            // 9. Pantalla de Fin de Partida
            if (game.Phase == Phase.GameOver)
                DrawGameOverModal();

            // 10. Zoom con Clic Derecho (la carta se eleva desde la mesa y regresa a ella)
            if (zoomedCard != null)
            {
                if (DrawElevatedZoomOverlay(zoomedCard, zoomedPip, zoomOriginRect, zoomAnim))
                    zoomClosing = true;
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

        void DrawTopBar()
        {
            float topW = Mathf.Min(Screen.width - 32f, 1180f);
            Rect topBar = new Rect((Screen.width - topW) * 0.5f, 4f, topW, 34f);
            GUI.Box(topBar, "", BoxBgStyle);
            GUILayout.BeginArea(new Rect(topBar.x + 12f, topBar.y + 4f, topBar.width - 24f, 26f));
            GUILayout.BeginHorizontal();
            string phaseTxt = game.Phase == Phase.CoinToss ? "Lanzamiento de Moneda"
                            : game.Phase == Phase.Setup ? "Preparación"
                            : $"Turno {game.Turn} ({game.Current.Name})";
            GUILayout.Label($"<b>Pokémon TCG</b> — <color=#9ecbff>{phaseTxt}</color>", HeaderStyle);
            GUILayout.Space(14);
            GUILayout.Label($"<color=#ffe066>{message}</color>", SubHeaderStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(showLogPanel ? "Ocultar Registro" : "Ver Registro", BtnStyle, GUILayout.Width(135), GUILayout.Height(24)))
            {
                PlayClickSound(0);
                showLogPanel = !showLogPanel;
            }
            GUILayout.Space(6);
            if (GUILayout.Button("Levantarse", BtnRedStyle, GUILayout.Width(110), GUILayout.Height(24)))
            {
                PlayClickSound(2);
                LeaveTable();
            }
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        void DrawAllPlaymatZones()
        {
            bool allowClick = zoomedCard == null && game.Phase != Phase.CoinToss && currentFlight == null;

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
            if (!has3D && count > 0)
            {
                // Respaldo 2D si no hay vista 3D
                int layers = Mathf.Clamp(count / 15 + 1, 1, 3);
                for (int i = layers - 1; i >= 0; i--)
                {
                    DrawCardBackRect(new Rect(r.x - i * 2f, r.y - i * 2f, r.width, r.height));
                }
            }
            Rect badge = new Rect(r.x + 6f, r.yMax - 22f, r.width - 12f, 20f);
            GUI.Box(badge, $"Mazo: {count}", BoxBgStyle);
        }

        void DrawTrashPile(int playerIdx, List<CardDef> discard, bool allowClick)
        {
            Rect r = GetTrashRect(playerIdx);
            bool has3D = TableView.Instance != null && TableView.Instance.IsBoard3DReady;
            if (!has3D && discard.Count > 0)
            {
                var top = discard[discard.Count - 1];
                int btn = DrawCardWithInput(r, top, false, allowClick);
                if (btn == 1) StartZoom(top, null, r);
            }
            Rect badge = new Rect(r.x + 4f, r.yMax - 22f, r.width - 8f, 20f);
            GUI.Box(badge, $"Trash: {discard.Count}", BoxBgStyle);
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
        /// Dibuja los indicadores de un Pokémon en el tapete (cuando las cartas 3D de F2 están activas,
        /// las cartas físicas y energías debajo se ven en 3D real sobre la mesa y aquí solo se dibuja el HUD de HP/Estado y clic).
        /// </summary>
        void DrawPokemonOnMat(PlayerState owner, PokemonInPlay pip, Rect rect, bool isActive, bool allowClick)
        {
            if (pip == null) return;

            // Si esta carta es la que está haciendo zoom elevado, no la dibujamos duplicada en la mesa
            if (zoomedCard == pip.Card && zoomedPip == pip && zoomAnim > 0.05f) return;

            bool has3D = TableView.Instance != null && TableView.Instance.IsBoard3DReady;

            // 1. Cartas de Energía DEBAJO del Pokémon + insignias de tipo
            int eCount = pip.Energy.Count;
            for (int i = eCount - 1; i >= 0; i--)
            {
                var eCard = pip.Energy[i];
                float offX = -10f - i * 8f;
                float offY = 10f + i * 7f;
                Rect eRect = new Rect(rect.x + offX, rect.y + offY, rect.width, rect.height);
                if (!has3D)
                    DrawCardSpriteOnly(eRect, eCard);

                // Insignia de color del tipo de energía en la esquina que asoma
                Rect pipBadge = new Rect(eRect.x + 2f, eRect.yMax - 16f, 18f, 15f);
                GUI.backgroundColor = EnergyColor(eCard.EnergyProvides);
                GUI.Box(pipBadge, eCard.EnergyProvides.Substring(0, 1), BadgeStyle);
                GUI.backgroundColor = Color.white;
            }

            // 2. Carta del Pokémon (en 3D ya está sobre el tapete y los clics se procesan por Raycast 3D en Update3DInput)
            if (!has3D)
            {
                bool highlight = retreatMode && owner == Me && Me.Bench.Contains(pip);
                int btn = DrawCardWithInput(rect, pip.Card, highlight, allowClick);
                if (btn == 0) OnPokemonLeftClicked(owner, pip);
                else if (btn == 1) StartZoom(pip.Card, pip, rect);
            }

            // 3. Barra de HP compacta en la base de la carta
            float hpRatio = Mathf.Clamp01(pip.RemainingHp / (float)Math.Max(1, pip.Card.Hp));
            Rect hpBar = new Rect(rect.x + 3f, rect.yMax - 17f, rect.width - 6f, 15f);
            GUI.DrawTexture(hpBar, hpBgTex);
            GUI.DrawTexture(new Rect(hpBar.x, hpBar.y, hpBar.width * hpRatio, hpBar.height), hpFillTex);
            GUI.Label(hpBar, $"<b>{pip.RemainingHp}/{pip.Card.Hp} HP</b>", CardLabelStyle);

            // 4. Iconos de Estados Alterados sobre la carta (+ contador de turnos encima si tiene duración)
            float iconX = rect.xMax - 30f;
            float iconY = rect.y + 6f;

            if (pip.Status != Status.None)
            {
                string sym = pip.Status == Status.Asleep ? "ZZZ" : pip.Status == Status.Confused ? "CNF" : "PAR";
                Texture2D bg = pip.Status == Status.Asleep ? sleepTex : pip.Status == Status.Confused ? confTex : paraTex;
                int duration = pip.Status == Status.Paralyzed ? Math.Max(1, pip.StatusTurns) : 0;
                DrawStatusIcon(new Rect(iconX, iconY, 28f, 24f), sym, bg, duration);
                iconY += 28f;
            }
            if (pip.Poisoned)
            {
                DrawStatusIcon(new Rect(iconX, iconY, 28f, 24f), "VEN", poisTex, 0);
                iconY += 28f;
            }
            if (pip.DefenderUntilTurn >= game.Turn)
            {
                int remTurns = Math.Max(1, pip.DefenderUntilTurn - game.Turn);
                DrawStatusIcon(new Rect(iconX, iconY, 28f, 24f), "DEF", defTex, remTurns);
                iconY += 28f;
            }
        }

        void DrawStatusIcon(Rect iconRect, string symbol, Texture2D bg, int remainingTurns)
        {
            GUI.DrawTexture(iconRect, bg);
            GUI.Label(iconRect, symbol, BadgeStyle);
            if (remainingTurns > 0)
            {
                // Pequeño número encima del icono de estado alterado indicando la duración restante
                Rect numRect = new Rect(iconRect.xMax - 12f, iconRect.y - 7f, 16f, 16f);
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

            if (game.Phase == Phase.Setup)
            {
                GUI.Box(new Rect(0, 0, panelW, 130f), "", BoxBgStyle);
                GUILayout.BeginVertical(GUILayout.Width(panelW - 12f));
                GUILayout.Space(8);
                GUILayout.Label("<b>Fase de Preparación</b>\nHaz clic izquierdo en tus Pokémon Básicos para colocarlos en el Campo de Batalla y en la Banca.", SubHeaderStyle);
                GUILayout.Space(8);
                if (GUILayout.Button("¡Empezar Partida!", BtnGreenStyle, GUILayout.Height(38)))
                {
                    PlayClickSound(1);
                    Report(game.FinishSetup(Me));
                }
                GUILayout.EndVertical();
            }
            else if (game.Phase == Phase.ChoosePromotion && game.PromotionPending == Me)
            {
                GUI.Box(new Rect(0, 0, panelW, 95f), "", BoxBgStyle);
                GUILayout.Label("<color=#ffd166><b>Tu Pokémon Activo cayó:</b>\nHaz clic izquierdo en un Pokémon de tu Banca para enviarlo al Campo de Batalla.</color>", SubHeaderStyle);
            }
            else if (game.Phase == Phase.Main && game.Current == Me && Me.Active != null && currentFlight == null)
            {
                GUI.Box(new Rect(0, 0, panelW, 315f), "", BoxBgStyle);
                GUILayout.BeginVertical(GUILayout.Width(panelW - 10f));
                GUILayout.Space(6);
                GUILayout.Label($"<b>Acciones — {Me.Active.Card.Name}</b>", HeaderStyle);
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

                if (GUILayout.Button(retreatMode ? "Cancelar Retirada" : $"Retirarse (Coste: {a.Card.RetreatCost})", retreatMode ? BtnRedStyle : BtnStyle, GUILayout.Height(33)))
                {
                    PlayClickSound(0);
                    retreatMode = !retreatMode;
                }
                GUILayout.Space(4);

                if (selHand >= 0 && selHand < Me.Hand.Count)
                {
                    var c = Me.Hand[selHand];
                    if (c.IsBasicPokemon && GUILayout.Button("Bajar a la Banca", BtnGreenStyle, GUILayout.Height(33)))
                    {
                        PlayClickSound(1);
                        Report(game.PlayBasic(Me, selHand));
                    }
                    if (c.Supertype == Supertype.Trainer && GUILayout.Button("Usar Entrenador", BtnGreenStyle, GUILayout.Height(33)))
                    {
                        PlayClickSound(1);
                        Report(game.PlayTrainer(Me, selHand, null));
                    }
                }

                GUILayout.Space(8);
                if (GUILayout.Button("Terminar Turno", BtnGoldStyle, GUILayout.Height(36)))
                {
                    PlayClickSound(1);
                    Report(game.EndTurn());
                }
                GUILayout.EndVertical();
            }

            if (showLogPanel)
            {
                GUILayout.Space(12);
                GUILayout.BeginVertical(BoxBgStyle, GUILayout.ExpandHeight(true));
                GUILayout.Label("<b>Registro de jugadas</b>", SubHeaderStyle);
                logScroll = GUILayout.BeginScrollView(logScroll);
                GUILayout.Label(string.Join("\n", game.Log.Skip(Math.Max(0, game.Log.Count - 40))), SubHeaderStyle);
                GUILayout.EndScrollView();
                GUILayout.EndVertical();
            }

            GUILayout.EndArea();
        }

        // ==================== Lanzamiento de Moneda Inicial y Secundaria ====================
        void DrawInitialCoinTossModal()
        {
            float mw = 540f, mh = 360f;
            Rect mr = new Rect((Screen.width - mw) * 0.5f, (Screen.height - mh) * 0.5f, mw, mh);
            GUI.Box(mr, "", BoxBgStyle);

            GUILayout.BeginArea(new Rect(mr.x + 24f, mr.y + 20f, mr.width - 48f, mr.height - 40f));
            GUILayout.Label("<size=22><b>Lanzamiento de Moneda Inicial</b></size>", HeaderStyle);
            GUILayout.Space(6);

            if (!startCoinSpinning)
            {
                GUILayout.Label("Elige una cara de la moneda para decidir quién empieza la partida:", SubHeaderStyle);
                GUILayout.Space(16);

                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();

                // Opción CARA (Pikachu dorado)
                GUILayout.BeginVertical(GUILayout.Width(170));
                Rect rHeads = GUILayoutUtility.GetRect(150f, 150f);
                if (SpriteCache.CoinHeadsTex != null) GUI.DrawTexture(rHeads, SpriteCache.CoinHeadsTex, ScaleMode.ScaleToFit);
                if (GUILayout.Button("Elegir CARA (Pikachu)", BtnGoldStyle, GUILayout.Height(36)))
                {
                    PlayClickSound(1);
                    startCoinPlayerChoseHeads = true;
                    startCoinResultHeads = game.PerformStartCoinToss(true);
                    startCoinSpinning = true;
                    startCoinTimer = 0f;
                }
                GUILayout.EndVertical();

                GUILayout.Space(40);

                // Opción CRUZ (Pokéball negra)
                GUILayout.BeginVertical(GUILayout.Width(170));
                Rect rTails = GUILayoutUtility.GetRect(150f, 150f);
                if (SpriteCache.CoinTailsTex != null) GUI.DrawTexture(rTails, SpriteCache.CoinTailsTex, ScaleMode.ScaleToFit);
                if (GUILayout.Button("Elegir CRUZ (Pokéball)", BtnStyle, GUILayout.Height(36)))
                {
                    PlayClickSound(1);
                    startCoinPlayerChoseHeads = false;
                    startCoinResultHeads = game.PerformStartCoinToss(false);
                    startCoinSpinning = true;
                    startCoinTimer = 0f;
                }
                GUILayout.EndVertical();

                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }
            else
            {
                // Mantener el modal visible mientras gira y cae la moneda
                GUILayout.EndArea();
                return;
            }

            GUILayout.EndArea();
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
            // Si la moneda inicial está girando, mostrar su animación en el centro del tapete
            if (startCoinSpinning)
            {
                float t = Mathf.Clamp01(startCoinTimer / StartCoinDuration);
                string resultTxt = t >= 0.75f
                    ? $"¡Salió {(startCoinResultHeads ? "CARA (Pikachu)" : "CRUZ (Pokéball)")}! Inicia {game.Current.Name}"
                    : "Lanzando moneda...";
                DrawAnimatedCoin(Screen.width * 0.5f, Screen.height * 0.44f, 175f, t, startCoinResultHeads, resultTxt);
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

        void DrawGameOverModal()
        {
            float mw = 480f, mh = 220f;
            Rect mr = new Rect((Screen.width - mw) * 0.5f, (Screen.height - mh) * 0.5f, mw, mh);
            GUI.Box(mr, "", BoxBgStyle);
            GUILayout.BeginArea(new Rect(mr.x + 24f, mr.y + 20f, mr.width - 48f, mr.height - 40f));
            GUILayout.Label($"<size=24><color=#66ff99><b>¡GANA {game.Winner.Name.ToUpper()}!</b></color></size>", HeaderStyle);
            GUILayout.Space(6);
            GUILayout.Label(game.WinReason, SubHeaderStyle);
            GUILayout.Space(20);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Jugar Revancha (Nuevo mazo NPC)", BtnGreenStyle, GUILayout.Height(38)))
            {
                PlayClickSound(1);
                StartTableMatch(PlayTableSession.CurrentOpponent);
            }
            GUILayout.Space(12);
            if (GUILayout.Button("Levantarse de la mesa", BtnRedStyle, GUILayout.Height(38)))
            {
                PlayClickSound(2);
                LeaveTable();
            }
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
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
        public static bool DrawElevatedZoomOverlay(CardDef card, PokemonInPlay pip, Rect originRect, float animT)
        {
            EnsureStyles();
            float smooth = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(animT));
            bool use3DCard = Instance != null && Instance.open && TableView.Instance != null && TableView.Instance.IsBoard3DReady;

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

                GUILayout.Space(6);
                GUILayout.Label("<color=#aebcd0><i>Mueve el ratón para inclinar la carta 3D  •  Clic o Esc para devolverla a su sitio</i></color>", SubHeaderStyle);
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
            float topW = Mathf.Min(Screen.width - 32f, 1180f);
            Rect topBar = new Rect((Screen.width - topW) * 0.5f, 4f, topW, 34f);
            if (topBar.Contains(guiMouse)) return true;

            float panelW = 248f;
            float panelH = showLogPanel ? (Screen.height - 96f) : 340f;
            Rect rightPanel = new Rect(Screen.width - panelW - 14f, 46f, panelW, panelH);
            if (rightPanel.Contains(guiMouse)) return true;

            return false;
        }

        void Update3DInput()
        {
            var tv = TableView.Instance;
            if (tv == null || !tv.IsBoard3DReady || game == null) return;

            bool animBusy = currentFlight != null || flightQueue.Count > 0 || (BoardAnimator.Instance != null && BoardAnimator.Instance.IsAnimating);
            bool allow3DInput = zoomedCard == null && game.Phase != Phase.CoinToss && game.Phase != Phase.GameOver &&
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
            if (target.TurnPlaced == game.Turn || target.TurnEvolved == game.Turn) return false;
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
            logScroll.y = float.MaxValue;
        }
    }
}
