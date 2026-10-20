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

        // --- Animación de Zoom con Clic Derecho (se eleva desde la mesa y vuelve a su sitio) ---
        CardDef zoomedCard;
        PokemonInPlay zoomedPip;
        Rect zoomOriginRect;
        float zoomAnim; // 0 = en la mesa, 1 = elevado frente a la cámara
        bool zoomClosing;

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

                string playerDeckPath = Path.Combine(Plugin.DataDir, "decks", "player.txt");
                List<CardDef> playerDeck = File.Exists(playerDeckPath)
                    ? DeckLoader.Load(playerDeckPath)
                    : DeckLoader.GenerateRandomNpcDeck();

                // El mazo del NPC se genera aleatoriamente en cada partida
                List<CardDef> npcDeck = DeckLoader.GenerateRandomNpcDeck();

                flightQueue.Clear();
                currentFlight = null;
                game = new Game(playerDeck, npcDeck);
                game.Players[1].Name = "Cliente";
                game.OnAnim = OnGameAnimEvent;

                selHand = -1;
                retreatMode = false;
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
                Plugin.Log.LogError(e);
            }
        }

        public void CloseFromTableStop()
        {
            open = false;
            game = null;
            zoomedCard = null;
            zoomedPip = null;
            flightQueue.Clear();
            currentFlight = null;
        }

        void LeaveTable()
        {
            bool finished = game != null && game.Phase == Phase.GameOver;
            bool won = finished && game.Winner == Me;
            open = false;
            game = null;
            zoomedCard = null;
            zoomedPip = null;
            flightQueue.Clear();
            currentFlight = null;
            PlayTableSession.StandUp(finished, won);
        }

        void StartZoom(CardDef card, PokemonInPlay pip, Rect screenRect)
        {
            if (card == null) return;
            zoomedCard = card;
            zoomedPip = pip;
            zoomOriginRect = screenRect;
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

            if (!open || game == null) return;

            InputManager.SetCursorLockMode(CursorLockMode.None);
            Cursor.visible = true;

            // Actualizar animación de Zoom elevado
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
                if (startCoinTimer >= StartCoinDuration && !startCoinDone)
                {
                    startCoinDone = true;
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

            // Ejecutar turno de la IA cuando terminen las animaciones en curso
            if (game.PendingAiTurn && currentFlight == null && flightQueue.Count == 0 && !startCoinSpinning)
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

        // ==================== Posiciones de las Zonas en el Tapete Real ====================
        void ComputePlaymatGeometry()
        {
            // Reservamos margen inferior para la mano del jugador y superior para la barra de estado
            float availH = Screen.height - 135f;
            float availW = Screen.width - 60f;
            float matSize = Mathf.Min(availH, availW * 0.86f);
            float matW = matSize * 1.02f;
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

        Rect GetActiveRect(int playerIdx) => playerIdx == 0 ? NormSlot(0.50f, 0.61f, 1.08f) : NormSlot(0.50f, 0.39f, 1.08f);
        Rect GetBenchRect(int playerIdx, int benchIdx)
        {
            float[] xs = { 0.27f, 0.385f, 0.50f, 0.615f, 0.73f };
            int idx = Mathf.Clamp(benchIdx, 0, 4);
            float ny = playerIdx == 0 ? 0.80f : 0.135f;
            return NormSlot(xs[idx], ny, 0.94f);
        }
        Rect GetDeckRect(int playerIdx) => playerIdx == 0 ? NormSlot(0.895f, 0.635f) : NormSlot(0.105f, 0.335f);
        Rect GetTrashRect(int playerIdx) => playerIdx == 0 ? NormSlot(0.895f, 0.835f) : NormSlot(0.105f, 0.130f);
        Rect GetSideBoxRect(int playerIdx, int boxIdx) // boxIdx = 0, 1, 2
        {
            if (playerIdx == 0)
            {
                float[] ys = { 0.525f, 0.685f, 0.845f };
                return NormSlot(0.105f, ys[Mathf.Clamp(boxIdx, 0, 2)], 0.86f);
            }
            else
            {
                float[] ys = { 0.115f, 0.275f, 0.435f };
                return NormSlot(0.895f, ys[Mathf.Clamp(boxIdx, 0, 2)], 0.86f);
            }
        }
        Rect GetHandCenterRect(int playerIdx) =>
            playerIdx == 0
                ? new Rect(Screen.width * 0.5f - slotW * 0.5f, Screen.height - slotH - 8f, slotW, slotH)
                : new Rect(Screen.width * 0.5f - slotW * 0.4f, 6f, slotW * 0.8f, slotH * 0.8f);

        void OnGameAnimEvent(AnimEvent ev)
        {
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

        // ==================== Estilos ====================
        public static GUIStyle BoxBgStyle, HeaderStyle, SubHeaderStyle, CardLabelStyle, ZoomTextStyle, BtnStyle, ToastStyle, BadgeStyle, DurationBadgeStyle;
        static Texture2D darkTex, panelTex, highlightTex, zoomBgTex, hpBgTex, hpFillTex, sleepTex, confTex, paraTex, poisTex, defTex, countTex;

        public static void EnsureStyles()
        {
            if (darkTex == null)
            {
                darkTex = MakeTex(new Color(0.07f, 0.09f, 0.13f, 0.94f));
                panelTex = MakeTex(new Color(0.12f, 0.15f, 0.21f, 0.94f));
                highlightTex = MakeTex(new Color(0.98f, 0.82f, 0.20f, 0.95f));
                zoomBgTex = MakeTex(new Color(0.02f, 0.03f, 0.05f, 0.82f));
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
                BoxBgStyle = new GUIStyle(GUI.skin.box) { normal = { background = darkTex } };
                HeaderStyle = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 17, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
                SubHeaderStyle = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 13, normal = { textColor = new Color(0.9f, 0.94f, 1f) } };
                CardLabelStyle = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 11, alignment = TextAnchor.UpperCenter, wordWrap = true, normal = { textColor = Color.white } };
                ZoomTextStyle = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 16, wordWrap = true, normal = { textColor = Color.white } };
                BtnStyle = new GUIStyle(GUI.skin.button) { fontSize = 13, fontStyle = FontStyle.Bold };
                ToastStyle = new GUIStyle(GUI.skin.box) { fontSize = 17, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white, background = darkTex } };
                BadgeStyle = new GUIStyle(GUI.skin.box) { fontSize = 10, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
                DurationBadgeStyle = new GUIStyle(GUI.skin.box) { fontSize = 10, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white, background = countTex } };
            }
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
                float ww = 660f, wh = 125f;
                Rect wr = new Rect((Screen.width - ww) * 0.5f, (Screen.height - wh) * 0.5f, ww, wh);
                GUI.Box(wr, "", BoxBgStyle);
                GUILayout.BeginArea(new Rect(wr.x + 20, wr.y + 16, wr.width - 40, wr.height - 32));
                GUILayout.Label("<size=19><b>Sentado en la mesa de juego</b></size>", HeaderStyle);
                GUILayout.Space(4);
                GUILayout.Label("Esperando a que un cliente se acerque y tome asiento frente a ti...", SubHeaderStyle);
                GUILayout.Space(10);
                if (GUILayout.Button("Levantarse de la mesa (Clic derecho / Esc)", BtnStyle, GUILayout.Height(32), GUILayout.Width(330)))
                    LeaveTable();
                GUILayout.EndArea();
                return;
            }

            if (!open || game == null) return;

            ComputePlaymatGeometry();

            // 1. Dibujar el Tapete Real sobre la mesa
            DrawPlaymatSurface();

            // 2. Barra superior compacta
            DrawTopBar();

            // 3. Dibujar todas las zonas sobre el Playmat (Mazos, Descartes, Premios, Bancas, Activos)
            DrawAllPlaymatZones();

            // 4. Mano del NPC (arriba boca abajo) y Mano del Jugador (abajo)
            DrawOpponentHand();
            DrawPlayerHand();

            // 5. Controles de Acción (Ataques, Retirada, Fin de turno) a la izquierda/derecha del tapete
            DrawSideControls();

            // 6. Vuelo animado de cartas (movimientos propios y del NPC)
            DrawCurrentFlight();

            // 7. Moneda secundaria (ataques / estados)
            if (subCoinTimer > 0f)
                DrawSubCoinFlip();

            // 8. Fase de Moneda Inicial antes de iniciar la partida
            if (game.Phase == Phase.CoinToss)
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

        void DrawPlaymatSurface()
        {
            // Sombra exterior del tapete sobre la madera de la mesa 3D
            GUI.Box(new Rect(playmatRect.x - 6f, playmatRect.y - 6f, playmatRect.width + 12f, playmatRect.height + 12f), "", BoxBgStyle);
            if (SpriteCache.PlaymatTex != null)
            {
                GUI.DrawTexture(playmatRect, SpriteCache.PlaymatTex, ScaleMode.StretchToFill);
            }
            else
            {
                GUI.Box(playmatRect, "");
            }
        }

        void DrawTopBar()
        {
            Rect topBar = new Rect(playmatRect.x, 2f, playmatRect.width, 32f);
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
            if (GUILayout.Button(showLogPanel ? "Ocultar Registro" : "Ver Registro", BtnStyle, GUILayout.Width(125), GUILayout.Height(22)))
                showLogPanel = !showLogPanel;
            GUILayout.Space(6);
            if (GUILayout.Button("Levantarse", BtnStyle, GUILayout.Width(105), GUILayout.Height(22)))
                LeaveTable();
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

        void DrawDeckPile(int playerIdx, int count)
        {
            Rect r = GetDeckRect(playerIdx);
            if (count > 0)
            {
                // Efecto de grosor de mazo (3 capas escalonadas)
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
            if (discard.Count > 0)
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
            // 3 casillas "Side" en el tapete, cada una aloja hasta 2 cartas de premio
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
        /// Dibuja un Pokémon en el tapete con:
        /// - Sus Energías colocadas físicamente DEBAJO de la carta (asomando por el borde inferior/izquierdo).
        /// - Su barra de HP.
        /// - Símbolos de Estados Alterados encima de la carta + número de duración restante si aplica.
        /// </summary>
        void DrawPokemonOnMat(PlayerState owner, PokemonInPlay pip, Rect rect, bool isActive, bool allowClick)
        {
            if (pip == null) return;

            // Si esta carta es la que está haciendo zoom elevado, no la dibujamos duplicada en la mesa
            if (zoomedCard == pip.Card && zoomedPip == pip && zoomAnim > 0.05f) return;

            // 1. Dibujar las cartas de Energía DEBAJO del Pokémon (asomando escalonadas por debajo/izquierda)
            int eCount = pip.Energy.Count;
            for (int i = eCount - 1; i >= 0; i--)
            {
                var eCard = pip.Energy[i];
                float offX = -10f - i * 8f;
                float offY = 10f + i * 7f;
                Rect eRect = new Rect(rect.x + offX, rect.y + offY, rect.width, rect.height);
                DrawCardSpriteOnly(eRect, eCard);

                // Insignia de color del tipo de energía en la esquina que asoma
                Rect pipBadge = new Rect(eRect.x + 2f, eRect.yMax - 16f, 18f, 15f);
                GUI.backgroundColor = EnergyColor(eCard.EnergyProvides);
                GUI.Box(pipBadge, eCard.EnergyProvides.Substring(0, 1), BadgeStyle);
                GUI.backgroundColor = Color.white;
            }

            // 2. Dibujar la carta del Pokémon encima de las energías
            bool highlight = retreatMode && owner == Me && Me.Bench.Contains(pip);
            int btn = DrawCardWithInput(rect, pip.Card, highlight, allowClick);
            if (btn == 0) OnPokemonLeftClicked(owner, pip);
            else if (btn == 1) StartZoom(pip.Card, pip, rect);

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
            // Panel de acciones a la derecha del tapete
            float panelW = Mathf.Max(230f, (Screen.width - playmatRect.width) * 0.5f - 16f);
            Rect rightPanel = new Rect(playmatRect.xMax + 8f, playmatRect.y + 40f, panelW, playmatRect.height - 80f);
            GUILayout.BeginArea(rightPanel);

            if (game.Phase == Phase.Setup)
            {
                GUI.Box(new Rect(0, 0, panelW, 130f), "", BoxBgStyle);
                GUILayout.BeginVertical(GUILayout.Width(panelW - 12f));
                GUILayout.Space(8);
                GUILayout.Label("<b>Fase de Preparación</b>\nHaz clic izquierdo en tus Pokémon Básicos para colocarlos en el Campo de Batalla y en la Banca.", SubHeaderStyle);
                GUILayout.Space(8);
                if (GUILayout.Button("¡Empezar Partida!", BtnStyle, GUILayout.Height(36)))
                    Report(game.FinishSetup(Me));
                GUILayout.EndVertical();
            }
            else if (game.Phase == Phase.ChoosePromotion && game.PromotionPending == Me)
            {
                GUI.Box(new Rect(0, 0, panelW, 95f), "", BoxBgStyle);
                GUILayout.Label("<color=#ffd166><b>Tu Pokémon Activo cayó:</b>\nHaz clic izquierdo en un Pokémon de tu Banca para enviarlo al Campo de Batalla.</color>", SubHeaderStyle);
            }
            else if (game.Phase == Phase.Main && game.Current == Me && Me.Active != null && currentFlight == null)
            {
                GUI.Box(new Rect(0, 0, panelW, 310f), "", BoxBgStyle);
                GUILayout.BeginVertical(GUILayout.Width(panelW - 10f));
                GUILayout.Space(6);
                GUILayout.Label($"<b>Acciones — {Me.Active.Card.Name}</b>", HeaderStyle);
                GUILayout.Space(6);

                var a = Me.Active;
                for (int i = 0; i < a.Card.Attacks.Count; i++)
                {
                    var atk = a.Card.Attacks[i];
                    GUI.enabled = a.CanPay(atk.Cost);
                    if (GUILayout.Button($"⚔ {atk.Name} ({atk.BaseDamage}{atk.DamageSuffix})", BtnStyle, GUILayout.Height(34)))
                        Report(game.Attack(Me, i));
                    GUI.enabled = true;
                    GUILayout.Space(4);
                }

                if (GUILayout.Button(retreatMode ? "Cancelar Retirada" : $"Retirarse (Coste: {a.Card.RetreatCost})", BtnStyle, GUILayout.Height(32)))
                    retreatMode = !retreatMode;
                GUILayout.Space(4);

                if (selHand >= 0 && selHand < Me.Hand.Count)
                {
                    var c = Me.Hand[selHand];
                    if (c.IsBasicPokemon && GUILayout.Button("Bajar a la Banca", BtnStyle, GUILayout.Height(32)))
                        Report(game.PlayBasic(Me, selHand));
                    if (c.Supertype == Supertype.Trainer && GUILayout.Button("Usar Entrenador", BtnStyle, GUILayout.Height(32)))
                        Report(game.PlayTrainer(Me, selHand, null));
                }

                GUILayout.Space(8);
                if (GUILayout.Button("Terminar Turno", BtnStyle, GUILayout.Height(36)))
                    Report(game.EndTurn());
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
                if (GUILayout.Button("Elegir CARA (Pikachu)", BtnStyle, GUILayout.Height(36)))
                {
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
                DrawAnimatedCoin(playmatRect.center.x, playmatRect.center.y, 175f, t, startCoinResultHeads, resultTxt);
                if (startCoinTimer >= StartCoinDuration + 0.95f)
                {
                    startCoinSpinning = false;
                    message = "Preparación: haz clic izquierdo en tus Pokémon Básicos para colocarlos y pulsa '¡Empezar Partida!'.";
                }
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
            if (GUILayout.Button("Jugar Revancha (Nuevo mazo NPC)", BtnStyle, GUILayout.Height(38)))
                StartTableMatch(PlayTableSession.CurrentOpponent);
            GUILayout.Space(12);
            if (GUILayout.Button("Levantarse de la mesa", BtnStyle, GUILayout.Height(38)))
                LeaveTable();
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        // ==================== Zoom con Elevación desde la Mesa ====================
        /// <summary>
        /// Anima la carta elevándose desde su posición en la mesa (originRect) hasta el centro-izquierda,
        /// mostrando al lado el cuadro de texto informativo. Devuelve true si el usuario cerró el zoom.
        /// </summary>
        public static bool DrawElevatedZoomOverlay(CardDef card, PokemonInPlay pip, Rect originRect, float animT)
        {
            EnsureStyles();
            float smooth = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(animT));

            // Oscurecer suavemente el fondo según la elevación
            Color prevCol = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, smooth * 0.85f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), zoomBgTex);
            GUI.color = prevCol;

            float targetH = Mathf.Min(Screen.height * 0.72f, 560f);
            float targetW = Mathf.Round(targetH * (63f / 88f));
            float totalW = targetW + 470f;
            Rect targetCardRect = new Rect((Screen.width - totalW) * 0.5f, (Screen.height - targetH) * 0.5f, targetW, targetH);

            // Interpolar la carta desde su hueco en el tapete hasta la posición elevada
            Rect animCardRect = new Rect(
                Mathf.Lerp(originRect.x, targetCardRect.x, smooth),
                Mathf.Lerp(originRect.y, targetCardRect.y, smooth),
                Mathf.Lerp(originRect.width, targetCardRect.width, smooth),
                Mathf.Lerp(originRect.height, targetCardRect.height, smooth)
            );

            // Sombra de elevación bajo la carta
            float shadowOffset = 18f * smooth;
            GUI.Box(new Rect(animCardRect.x + shadowOffset, animCardRect.y + shadowOffset, animCardRect.width, animCardRect.height), "", BoxBgStyle);

            DrawCardSpriteOnly(animCardRect, card);

            // Cuadro de texto lateral con toda la información de la carta
            if (smooth > 0.35f)
            {
                float panelAlpha = Mathf.Clamp01((smooth - 0.35f) / 0.65f);
                GUI.color = new Color(1f, 1f, 1f, panelAlpha);
                Rect infoBox = new Rect(targetCardRect.xMax + 22f, targetCardRect.y, 448f, targetH);
                GUI.Box(infoBox, "", BoxBgStyle);

                GUILayout.BeginArea(new Rect(infoBox.x + 20f, infoBox.y + 18f, infoBox.width - 40f, infoBox.height - 36f));
                GUILayout.Label($"<size=24><b>{card.Name}</b></size>  <color=#9ecbff>(#{card.Number})</color>", HeaderStyle);
                GUILayout.Space(8);

                if (card.Supertype == Supertype.Pokemon)
                {
                    GUILayout.Label($"<b>Etapa:</b> {card.Stage}" + (!string.IsNullOrEmpty(card.EvolvesFrom) ? $" (Evoluciona de {card.EvolvesFrom})" : ""), ZoomTextStyle);
                    GUILayout.Label($"<b>Tipo:</b> {card.Type}   |   <b>HP:</b> {(pip != null ? $"{pip.RemainingHp}/{card.Hp}" : card.Hp.ToString())}", ZoomTextStyle);

                    if (pip != null && pip.Energy.Count > 0)
                        GUILayout.Label($"<b>Energías debajo:</b> {string.Join(", ", pip.Energy.Select(e => e.Name))}", ZoomTextStyle);
                    if (pip != null && (pip.Status != Status.None || pip.Poisoned))
                        GUILayout.Label($"<color=#ffd166><b>Estado alterado:</b> {pip.Status} {(pip.StatusTurns > 0 ? $"({pip.StatusTurns} turno)" : "")} {(pip.Poisoned ? "[Envenenado]" : "")}</color>", ZoomTextStyle);

                    if (card.Power != null)
                    {
                        GUILayout.Space(8);
                        GUILayout.Label($"<color=#ffd166><b>Pokémon Power — {card.Power.Name}:</b></color>\n{card.Power.Text}", ZoomTextStyle);
                    }

                    GUILayout.Space(8);
                    foreach (var atk in card.Attacks)
                    {
                        string cost = atk.Cost.Count > 0 ? string.Join(", ", atk.Cost) : "Gratis";
                        GUILayout.Label($"<color=#80ed99><b>• {atk.Name}</b></color>  [{cost}]  <b>Daño: {atk.BaseDamage}{atk.DamageSuffix}</b>", ZoomTextStyle);
                        if (!string.IsNullOrEmpty(atk.Text))
                            GUILayout.Label($"  <i>{atk.Text}</i>", ZoomTextStyle);
                        GUILayout.Space(4);
                    }

                    GUILayout.Space(6);
                    GUILayout.Label($"<b>Debilidad:</b> {(string.IsNullOrEmpty(card.WeaknessType) ? "Ninguna" : card.WeaknessType + " ×2")}  |  " +
                                    $"<b>Resistencia:</b> {(string.IsNullOrEmpty(card.ResistanceType) ? "Ninguna" : $"{card.ResistanceType} -{card.ResistanceValue}")}  |  " +
                                    $"<b>Retirada:</b> {card.RetreatCost}", ZoomTextStyle);
                }
                else
                {
                    GUILayout.Label($"<b>Categoría:</b> {card.Supertype}", ZoomTextStyle);
                    GUILayout.Space(10);
                    if (!string.IsNullOrEmpty(card.RulesText))
                        GUILayout.Label(card.RulesText, ZoomTextStyle);
                }

                GUILayout.FlexibleSpace();
                GUILayout.Label("<color=#bbbbbb>Clic derecho o izquierdo para devolver la carta a la mesa.</color>", SubHeaderStyle);
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
            if (c.Supertype == Supertype.Energy) return "Haz clic izquierdo en uno de tus Pokémon para colocarla debajo.";
            if (c.Supertype == Supertype.Pokemon && c.Stage != "Basic") return $"Haz clic izquierdo en tu {c.EvolvesFrom} para evolucionarlo.";
            if (c.Supertype == Supertype.Trainer) return "Haz clic izquierdo en el Pokémon objetivo o pulsa 'Usar Entrenador'.";
            if (c.IsBasicPokemon) return "Pulsa 'Bajar a la Banca'.";
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
