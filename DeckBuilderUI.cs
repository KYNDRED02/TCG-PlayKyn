using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace PokeTCGPlay
{
    /// <summary>
    /// F9: Creador de Mazos 3D integrado en la Mesa de Creación (InteractableWorkbench) del juego.
    /// Renderiza cartas físicas 3D en cuadrícula/carrusel sobre tapete de coleccionista, pila física 3D del mazo activo
    /// con vuelos en arco al añadir/quitar cartas, filtros y búsqueda en tiempo real, mazos múltiples/oficiales,
    /// importación/exportación .txt e inspección 3D con volteo de 180° (frente/reverso).
    /// </summary>
    public class DeckBuilderUI : MonoBehaviour
    {
        public static DeckBuilderUI Instance { get; private set; }
        public bool IsOpen { get; private set; }

        bool workbenchScreenVisible;
        bool openedStandalone;
        readonly List<CardDef> currentDeck = new List<CardDef>();
        readonly List<CardDef> pageCardsBuffer = new List<CardDef>(DeckBuilderStage3D.PageSize);
        readonly DeckBuilderStage3D stage3D = new DeckBuilderStage3D();

        DeckDef deckDef = new DeckDef();
        FormatDef format = new FormatDef();
        List<string> deckNames = new List<string>();
        string deckNameEdit = "";

        // F9: Filtros y búsqueda en tiempo real
        int filterSupertype = -1; // -1 = Todos, 0 = Pokémon, 1 = Trainer, 2 = Energy
        string filterType = "";
        string filterSetId = "";
        string searchQuery = "";
        bool showUnownedLocked;
        int currentPage;
        int lastTotalPages = 1;

        Vector2 deckScroll;
        string statusMsg = "";
        Rect lastCatalogGuiRect;
        Rect lastDeckPedestalGuiRect;

        // F9: Inspección 3D con elevación hacia cámara y volteo frente/reverso
        CardDef zoomCard;
        Rect zoomOriginRect;
        Vector3 zoomOriginWorldPos;
        Quaternion zoomOriginWorldRot = Quaternion.identity;
        float zoomOriginScale = 1f;
        float zoomAnim; // 0..1
        bool zoomClosing;

        // Importador/Exportador en vivo de archivos .txt estilo PTCGO / PTCG Live y Reporte de Faltantes
        bool showImportModal;
        List<string> importTxtFiles = new List<string>();
        PtcgoImportReport lastImportReport;
        Vector2 importFilesScroll;
        Vector2 importReportScroll;

        // Selector de Mazos Oficiales de Base Era (Jugador y NPC)
        bool showThemeDecksModal;
        bool showSettingsModal;
        string themeFilterSetId = "";
        Vector2 themeDecksScroll;

        static GUIStyle sPillBgStyle;
        static GUIStyle sPillLabelStyle;

        void Awake()
        {
            Instance = this;
        }

        void OnDestroy()
        {
            stage3D.Destroy();
            if (Instance == this) Instance = null;
        }

        static void EnsureBuilderStyles()
        {
            if (sPillBgStyle == null)
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                var col = new Color(0.05f, 0.08f, 0.13f, 0.88f);
                tex.SetPixels(new[] { col, col, col, col });
                tex.Apply();
                sPillBgStyle = new GUIStyle(GUI.skin.box)
                {
                    normal = { background = tex },
                    padding = new RectOffset(4, 4, 2, 2),
                    margin = new RectOffset(0, 0, 0, 0)
                };
            }
            if (sPillLabelStyle == null)
            {
                sPillLabelStyle = new GUIStyle(GUI.skin.label)
                {
                    richText = true,
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 11,
                    wordWrap = false,
                    clipping = TextClipping.Clip,
                    normal = { textColor = new Color(0.92f, 0.96f, 1f, 1f) }
                };
            }
        }

        public void OnWorkbenchScreenOpened()
        {
            workbenchScreenVisible = true;
            SpriteCache.EnsureInitialized();
            SpriteCache.PreloadFromEpl();
        }

        public void OnWorkbenchScreenClosed()
        {
            workbenchScreenVisible = false;
            if (IsOpen && !openedStandalone)
            {
                IsOpen = false;
                stage3D.SetActive(false);
            }
        }

        public void OpenFromWorkbench(InteractableWorkbench wb)
        {
            openedStandalone = true;
            OpenCommon();
            var ipc = CSingleton<InteractionPlayerController>.Instance;
            if (ipc != null)
            {
                ipc.EnterUIMode();
                InputManager.SetCursorLockMode(CursorLockMode.None);
                Cursor.visible = true;
            }
        }

        public void OpenFromWorkbenchScreen(WorkbenchUIScreen screen)
        {
            openedStandalone = false;
            OpenCommon();
        }

        void OpenCommon()
        {
            Collection.Invalidate();
            SpriteCache.EnsureInitialized();
            SpriteCache.PreloadFromEpl();
            importTxtFiles = PtcgoImporter.ListTxtFiles();
            LoadCurrentPlayerDeck();
            zoomCard = null;
            zoomAnim = 0f;
            zoomClosing = false;
            currentPage = 0;
            stage3D.SetActive(true);
            statusMsg = "Clic Izq: añadir carta 3D  |  Clic Der: Inspección 3D y Volteo  |  Rueda: Carrusel";
            IsOpen = true;
        }

        void CloseBuilder()
        {
            IsOpen = false;
            zoomCard = null;
            zoomClosing = false;
            showImportModal = false;
            showThemeDecksModal = false;
            showSettingsModal = false;
            stage3D.SetActive(false);
            if (openedStandalone)
            {
                openedStandalone = false;
                var ipc = CSingleton<InteractionPlayerController>.Instance;
                if (ipc != null) ipc.ExitUIMode();
            }
        }

        void LoadCurrentPlayerDeck()
        {
            deckNames = DeckStore.List();
            LoadDeck(DeckStore.Active);
        }

        void LoadDeck(DeckDef d)
        {
            currentDeck.Clear();
            deckDef = d ?? new DeckDef { Name = UniqueName("Mi mazo"), Format = FormatRegistry.Default.Id };
            deckNameEdit = deckDef.Name;
            format = FormatRegistry.Get(deckDef.Format) ?? FormatRegistry.Default;
            var missing = new List<string>();
            currentDeck.AddRange(deckDef.Resolve(missing));
            if (missing.Count > 0)
                statusMsg = "<color=#ffd166>Cartas desconocidas ignoradas: " + string.Join(", ", missing) + "</color>";
        }

        string UniqueName(string baseName)
        {
            var names = new HashSet<string>(DeckStore.List(), StringComparer.OrdinalIgnoreCase);
            string n = baseName;
            int i = 2;
            while (names.Contains(n)) n = $"{baseName} {i++}";
            return n;
        }

        void SaveCurrentPlayerDeck()
        {
            try
            {
                string newName = (deckNameEdit ?? "").Trim();
                if (newName.Length == 0)
                {
                    statusMsg = "<color=#ff6b6b>Escribe un nombre para el mazo.</color>";
                    return;
                }
                if (!string.Equals(newName, deckDef.Name, StringComparison.Ordinal))
                {
                    if (DeckStore.List().Contains(newName, StringComparer.OrdinalIgnoreCase))
                    {
                        statusMsg = "<color=#ff6b6b>Ya existe un mazo con ese nombre.</color>";
                        return;
                    }
                    DeckStore.Delete(deckDef.Name);
                    deckDef.Name = newName;
                }
                deckDef.Format = format.Id;
                deckDef.SetCards(currentDeck);
                DeckStore.Save(deckDef);
                deckNames = DeckStore.List();
                var errors = DeckValidator.Validate(currentDeck, format);
                if (errors.Count == 0)
                {
                    DeckStore.SetActive(deckDef.Name);
                    statusMsg = $"<color=#66ff99>✔ Mazo '{deckDef.Name}' guardado y activado para jugar.</color>";
                }
                else
                {
                    statusMsg = $"<color=#ffd166>Guardado (incompleto, aún no jugable): {errors[0]}</color>";
                }
            }
            catch (Exception e)
            {
                statusMsg = "<color=#ff6b6b>Error al guardar: " + e.Message + "</color>";
            }
        }

        bool AddCardToDeck(CardDef c, int sourceSlotIndex = -1)
        {
            if (c == null) return false;
            if (DeckValidator.RemainingAllowed(c, currentDeck, format) <= 0)
            {
                int owned = Collection.Owned(c);
                string why = currentDeck.Count >= format.DeckSize
                    ? $"El mazo ya tiene {format.DeckSize} cartas."
                    : owned <= 0
                        ? $"🔒 No tienes '{c.Name}' en tu colección. Abre sobres de {c.SetId.ToUpperInvariant()} para conseguirla."
                        : owned != int.MaxValue && currentDeck.Count(x => x.Id == c.Id) >= owned
                            ? $"Solo tienes {owned} copia(s) de {c.Name} en tu colección."
                            : $"Máximo {format.MaxCopies} copias de {c.Name}.";
                statusMsg = $"<color=#ffd166>{why}</color>";
                return false;
            }
            currentDeck.Add(c);
            stage3D.SpawnAddFlight(sourceSlotIndex, c);
            statusMsg = $"<color=#80ed99>+ Añadido:</color> {c.Name} — Mazo: <b>{currentDeck.Count}/{format.DeckSize}</b>";
            return true;
        }

        bool RemoveCardFromDeck(CardDef c)
        {
            if (c == null) return false;
            int idx = currentDeck.FindLastIndex(x => x.Id == c.Id);
            if (idx >= 0)
            {
                currentDeck.RemoveAt(idx);
                stage3D.SpawnRemoveFlight(c);
                statusMsg = $"<color=#ffd166>- Quitado:</color> {c.Name} — Mazo: <b>{currentDeck.Count}/{format.DeckSize}</b>";
                return true;
            }
            return false;
        }

        void OpenInspect3D(CardDef card, Rect approxRect, Vector3 originWorldPos, Quaternion originWorldRot, float originScale)
        {
            if (card == null) return;
            stage3D.ResetInspectFlip();
            zoomCard = card;
            zoomOriginRect = approxRect;
            zoomOriginWorldPos = originWorldPos;
            zoomOriginWorldRot = originWorldRot;
            zoomOriginScale = originScale;
            zoomAnim = 0f;
            zoomClosing = false;
        }

        void ChangePage(int delta)
        {
            int next = Mathf.Clamp(currentPage + delta, 0, Mathf.Max(0, lastTotalPages - 1));
            if (next != currentPage)
            {
                int dir = next > currentPage ? 1 : -1;
                currentPage = next;
                stage3D.TriggerCarouselSlide(dir);
                GameUI.PlayClickSound(0);
            }
        }

        static bool CardMatchesSearch(CardDef c, string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return true;
            string q = query.Trim();
            if (ContainsIgnoreCase(c.Name, q)) return true;
            if (ContainsIgnoreCase(c.Id, q)) return true;
            if (ContainsIgnoreCase(c.SetId, q)) return true;
            if (ContainsIgnoreCase(c.Stage, q)) return true;
            if (ContainsIgnoreCase(c.EvolvesFrom, q)) return true;
            if (ContainsIgnoreCase(c.RulesText, q)) return true;
            if (c.Power != null && (ContainsIgnoreCase(c.Power.Name, q) || ContainsIgnoreCase(c.Power.Text, q)))
                return true;
            if (c.Attacks != null)
            {
                for (int i = 0; i < c.Attacks.Count; i++)
                {
                    var atk = c.Attacks[i];
                    if (atk != null && (ContainsIgnoreCase(atk.Name, q) || ContainsIgnoreCase(atk.Text, q)))
                        return true;
                }
            }
            return false;
        }

        static bool ContainsIgnoreCase(string haystack, string needle)
        {
            return !string.IsNullOrEmpty(haystack) && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        void Update()
        {
            if (!IsOpen) return;
            InputManager.SetCursorLockMode(CursorLockMode.None);
            Cursor.visible = true;

            float dt = Time.unscaledDeltaTime > 0f ? Time.unscaledDeltaTime : Time.deltaTime;
            if (zoomCard != null)
            {
                float target = zoomClosing ? 0f : 1f;
                zoomAnim = Mathf.MoveTowards(zoomAnim, target, dt * 5.8f);
                if (!zoomClosing && (Input.GetKeyDown(KeyCode.F) || Input.GetKeyDown(KeyCode.Tab)))
                {
                    GameUI.PlayClickSound(0);
                    stage3D.ToggleInspectFlip();
                }
                if (zoomClosing && zoomAnim <= 0.01f)
                {
                    zoomCard = null;
                    zoomClosing = false;
                    stage3D.ResetInspectFlip();
                }
            }
            else if (!showImportModal && !showThemeDecksModal && !showSettingsModal && GUIUtility.keyboardControl == 0)
            {
                if (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.PageUp))
                    ChangePage(-1);
                else if (Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.PageDown))
                    ChangePage(1);
            }

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                if (zoomCard != null) zoomClosing = true;
                else if (showSettingsModal) showSettingsModal = false;
                else if (showThemeDecksModal) showThemeDecksModal = false;
                else if (showImportModal) showImportModal = false;
                else CloseBuilder();
            }
        }

        void OnGUI()
        {
            GameUI.EnsureStyles();
            EnsureBuilderStyles();
            GUI.depth = -1100;

            // Botón flotante no intrusivo cuando el jugador abre la Mesa de Creación nativa (WorkbenchUIScreen)
            if (workbenchScreenVisible && !IsOpen)
            {
                Rect btnRect = new Rect(Screen.width - 360f - 24f, 20f, 360f, 44f);
                if (GUI.Button(btnRect, "⚡ Constructor 3D Pokémon TCG", GameUI.BtnGoldStyle))
                {
                    GameUI.PlayClickSound(1);
                    OpenCommon();
                }
                return;
            }

            if (!IsOpen) return;

            // 1) Calcular geometría de layout y filtrar catálogo antes del renderizado 3D
            float pad = 16f;
            float w = Mathf.Min(Screen.width - 24f, 1760f);
            float h = Mathf.Min(Screen.height - 24f, 990f);
            Rect win = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);

            float topBarsH = 180f;
            float bodyY = win.y + topBarsH;
            float bodyH = Mathf.Max(300f, win.height - topBarsH - 14f);
            float rightPanelW = Mathf.Clamp(win.width * 0.275f, 415f, 470f);
            float leftPanelW = win.width - pad * 2f - rightPanelW - 14f;

            lastCatalogGuiRect = new Rect(win.x + pad, bodyY + 30f, leftPanelW, bodyH - 68f);
            Rect rightPanelRect = new Rect(win.x + pad + leftPanelW + 14f, bodyY, rightPanelW, bodyH);
            lastDeckPedestalGuiRect = new Rect(rightPanelRect.x + 8f, rightPanelRect.y + 8f, 134f, 166f);

            var filtered = CardDatabase.All.Where(c =>
                c.IsPlayable &&
                format.Allows(c) &&
                (showUnownedLocked || Collection.Owned(c) > 0) &&
                (string.IsNullOrEmpty(filterSetId) || string.Equals(c.SetId, filterSetId, StringComparison.OrdinalIgnoreCase)) &&
                (filterSupertype == -1 || (int)c.Supertype == filterSupertype) &&
                (string.IsNullOrEmpty(filterType) || c.Type == filterType || (c.Supertype == Supertype.Energy && c.EnergyProvides == filterType)) &&
                CardMatchesSearch(c, searchQuery)
            ).ToList();

            lastTotalPages = Mathf.Max(1, Mathf.CeilToInt(filtered.Count / (float)DeckBuilderStage3D.PageSize));
            currentPage = Mathf.Clamp(currentPage, 0, lastTotalPages - 1);

            pageCardsBuffer.Clear();
            int startIdx = currentPage * DeckBuilderStage3D.PageSize;
            for (int i = startIdx; i < filtered.Count && pageCardsBuffer.Count < DeckBuilderStage3D.PageSize; i++)
                pageCardsBuffer.Add(filtered[i]);

            bool modalOpen = showImportModal || showThemeDecksModal || showSettingsModal;

            // 2) Actualizar y renderizar el Escenario 3D del Deck Builder una vez por Repaint y dibujar la RenderTexture de fondo
            if (Event.current.type == EventType.Repaint)
            {
                stage3D.UpdateAndRender(
                    pageCardsBuffer,
                    currentDeck,
                    format,
                    lastCatalogGuiRect,
                    lastDeckPedestalGuiRect,
                    modalOpen,
                    zoomCard,
                    zoomAnim,
                    zoomOriginWorldPos,
                    zoomOriginWorldRot,
                    zoomOriginScale);
            }

            if (stage3D.RenderTexture != null)
            {
                GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), stage3D.RenderTexture, ScaleMode.StretchToFill, false);
            }

            // 3) Rueda del ratón para deslizar el carrusel 3D cuando el cursor está sobre el tapete de colección
            if (!modalOpen && zoomCard == null && Event.current.type == EventType.ScrollWheel && lastCatalogGuiRect.Contains(Event.current.mousePosition))
            {
                if (Event.current.delta.y > 0.05f) ChangePage(1);
                else if (Event.current.delta.y < -0.05f) ChangePage(-1);
                Event.current.Use();
            }

            // 4) Atenuar suavemente los paneles 2D durante la Inspección 3D para que la carta física 3D eleve limpia al frente
            Color savedGuiColor = GUI.color;
            float builderChromeAlpha = zoomCard != null
                ? Mathf.Clamp01(1f - Mathf.SmoothStep(0f, 1f, zoomAnim) * 1.35f)
                : 1f;

            if (builderChromeAlpha > 0.01f)
            {
                GUI.color = new Color(1f, 1f, 1f, builderChromeAlpha);
                DrawTopBars(new Rect(win.x + pad, win.y + 10f, win.width - pad * 2f, topBarsH - 14f));
                DrawCatalogOverlay(new Rect(win.x + pad, bodyY, leftPanelW, bodyH), filtered.Count, startIdx);
                DrawRightDeckPanel(rightPanelRect);
                GUI.color = savedGuiColor;
            }

            // 5) Interacción por Clic 3D sobre las cartas del tapete o sobre la pila física 3D del mazo
            if (!modalOpen && zoomCard == null && Event.current.type == EventType.MouseDown)
            {
                int btn = Event.current.button;
                int hovIdx = stage3D.HoveredSlotIndex;
                if (hovIdx >= 0 && hovIdx < stage3D.ScreenSlots.Count)
                {
                    var slot = stage3D.ScreenSlots[hovIdx];
                    if (btn == 0)
                    {
                        if (AddCardToDeck(slot.Card, slot.SlotIndex))
                            GameUI.PlayClickSound(0);
                        else
                            GameUI.PlayClickSound(2);
                        Event.current.Use();
                    }
                    else if (btn == 1)
                    {
                        GameUI.PlayClickSound(1);
                        OpenInspect3D(slot.Card, slot.ApproxGuiRect, slot.WorldPos, slot.WorldRot, slot.Scale);
                        Event.current.Use();
                    }
                }
                else if (stage3D.IsHoveringDeckStack && currentDeck.Count > 0)
                {
                    var topCard = currentDeck[currentDeck.Count - 1];
                    if (btn == 0)
                    {
                        if (RemoveCardFromDeck(topCard))
                            GameUI.PlayClickSound(0);
                        Event.current.Use();
                    }
                    else if (btn == 1)
                    {
                        GameUI.PlayClickSound(1);
                        OpenInspect3D(topCard, lastDeckPedestalGuiRect, stage3D.DeckStackWorldPos, stage3D.DeckStackWorldRot, stage3D.DeckStackScale);
                        Event.current.Use();
                    }
                }
            }

            // 6) Modales flotantes (Mazos Oficiales Base Era / Importador .txt y Faltantes / Opciones F10)
            if (showThemeDecksModal)
            {
                DrawThemeDecksModal(win);
            }
            if (showImportModal)
            {
                DrawImportAndMissingModal(win);
            }
            if (showSettingsModal)
            {
                PolishEffects3D.DrawSettingsModal(ref showSettingsModal);
            }

            // 7) Inspección 3D elevada con panel lateral F5 + Volteo de 180° (Frente / Reverso) + Acciones rápidas de Mazo
            if (zoomCard != null)
            {
                CardDef inspected = zoomCard;
                bool closeRequested = GameUI.DrawElevatedZoomOverlay(
                    inspected,
                    null,
                    zoomOriginRect,
                    zoomAnim,
                    force3DCard: true,
                    extraFooterUI: () =>
                    {
                        int inDeck = currentDeck.Count(x => x.Id == inspected.Id);
                        int owned = Collection.Owned(inspected);
                        string ownedTxt = owned == int.MaxValue ? "∞" : owned.ToString();
                        string flipLabel = stage3D.InspectShowingBack ? "🔄 Ver Frente (F)" : "🔄 Voltear Reverso (F)";

                        GUILayout.BeginVertical(GameUI.SubBoxStyle);
                        GUILayout.Label($"<b>En este mazo:</b> <color=#80ed99>{inDeck}</color>   •   <b>En tu colección:</b> <color=#9ecbff>{ownedTxt}</color>", GameUI.SubHeaderStyle);
                        GUILayout.Space(4);
                        GUILayout.BeginHorizontal();
                        if (GUILayout.Button(flipLabel, GameUI.BtnGoldStyle, GUILayout.Height(32), GUILayout.Width(165)))
                        {
                            GameUI.PlayClickSound(0);
                            stage3D.ToggleInspectFlip();
                        }
                        GUILayout.Space(6);
                        if (GUILayout.Button("- Quitar", GameUI.BtnRedStyle, GUILayout.Height(32)))
                        {
                            if (RemoveCardFromDeck(inspected)) GameUI.PlayClickSound(0);
                            else GameUI.PlayClickSound(2);
                        }
                        GUILayout.Space(6);
                        if (GUILayout.Button("+ Añadir", GameUI.BtnGreenStyle, GUILayout.Height(32)))
                        {
                            if (AddCardToDeck(inspected, -1)) GameUI.PlayClickSound(0);
                            else GameUI.PlayClickSound(2);
                        }
                        GUILayout.EndHorizontal();
                        GUILayout.EndVertical();
                    });

                if (closeRequested)
                    zoomClosing = true;
            }
        }

        void DrawTopBars(Rect area)
        {
            GUILayout.BeginArea(area);

            // Fila 1: Título, estado y botones principales
            GUILayout.BeginHorizontal(GameUI.SubBoxStyle);
            GUILayout.Label($"<size=20><b>{Loc.T("⚡ Mesa de Creación 3D — Constructor Pokémon TCG", "⚡ 3D Workbench — Pokémon TCG Deck Builder")}</b></size>", GameUI.HeaderStyle);
            GUILayout.FlexibleSpace();
            GUILayout.Label(statusMsg, GameUI.SubHeaderStyle);
            GUILayout.Space(10);
            if (GUILayout.Button(Loc.T("Opciones", "Settings"), GameUI.BtnGoldStyle, GUILayout.Width(110), GUILayout.Height(32)))
            {
                GameUI.PlayClickSound(0);
                showImportModal = false;
                showThemeDecksModal = false;
                showSettingsModal = !showSettingsModal;
            }
            GUILayout.Space(6);
            if (GUILayout.Button(Loc.T("✔ Guardar y Activar", "✔ Save & Activate"), GameUI.BtnGreenStyle, GUILayout.Width(175), GUILayout.Height(32)))
            {
                GameUI.PlayClickSound(1);
                SaveCurrentPlayerDeck();
            }
            GUILayout.Space(6);
            if (GUILayout.Button(Loc.T("✖ Cerrar", "✖ Close"), GameUI.BtnRedStyle, GUILayout.Width(105), GUILayout.Height(32)))
            {
                GameUI.PlayClickSound(2);
                CloseBuilder();
                GUILayout.EndHorizontal();
                GUILayout.EndArea();
                return;
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(3);

            // Fila 2: Gestión de Mazos Múltiples, Mazos Oficiales, Exportar/Importar .txt y Formato
            GUILayout.BeginHorizontal(GameUI.SubBoxStyle);
            GUILayout.Label("<b>Mazo:</b>", GameUI.SubHeaderStyle, GUILayout.Width(46));
            if (GUILayout.Button("◀", GameUI.BtnStyle, GUILayout.Width(30), GUILayout.Height(27))) { GameUI.PlayClickSound(0); CycleDeck(-1); }
            GUI.SetNextControlName("DeckNameField");
            deckNameEdit = GUILayout.TextField(deckNameEdit ?? "", 40, GameUI.InputFieldStyle, GUILayout.Width(205), GUILayout.Height(27));
            if (GUILayout.Button("▶", GameUI.BtnStyle, GUILayout.Width(30), GUILayout.Height(27))) { GameUI.PlayClickSound(0); CycleDeck(1); }

            bool isActiveDeck = string.Equals(Plugin.ActiveDeck?.Value, deckDef.Name, StringComparison.OrdinalIgnoreCase);
            GUILayout.Label($"({deckNames.Count}{(isActiveDeck ? ", <color=#80ed99>activo</color>" : "")})", GameUI.SubHeaderStyle, GUILayout.Width(86));

            if (GUILayout.Button("Nuevo", GameUI.BtnStyle, GUILayout.Width(68), GUILayout.Height(27)))
            {
                GameUI.PlayClickSound(0);
                LoadDeck(null);
                statusMsg = "Nuevo mazo vacío creado.";
            }
            if (GUILayout.Button("Duplicar", GameUI.BtnStyle, GUILayout.Width(78), GUILayout.Height(27)))
            {
                GameUI.PlayClickSound(0);
                deckDef = new DeckDef { Name = UniqueName(deckDef.Name + " copia"), Format = format.Id };
                deckNameEdit = deckDef.Name;
                statusMsg = "Copia en edición. Pulsa 'Guardar y Activar' para conservarla.";
            }
            if (GUILayout.Button("Borrar", GameUI.BtnRedStyle, GUILayout.Width(68), GUILayout.Height(27)))
            {
                GameUI.PlayClickSound(2);
                string old = deckDef.Name;
                DeckStore.Delete(old);
                deckNames = DeckStore.List();
                LoadDeck(deckNames.Count > 0 ? DeckStore.Load(deckNames[0]) : null);
                statusMsg = $"Mazo '{old}' borrado.";
            }

            GUILayout.Space(6);
            if (GUILayout.Button($"📦 Oficiales ({ThemeDeckRegistry.Decks.Count})", GameUI.BtnGoldStyle, GUILayout.Width(138), GUILayout.Height(27)))
            {
                GameUI.PlayClickSound(1);
                Collection.Invalidate();
                showImportModal = false;
                showThemeDecksModal = !showThemeDecksModal;
            }
            if (GUILayout.Button("📤 Exportar .txt", GameUI.BtnStyle, GUILayout.Width(122), GUILayout.Height(27)))
            {
                GameUI.PlayClickSound(1);
                ExportCurrentDeckToTxt();
            }
            if (GUILayout.Button($"📥 Importar .txt ({importTxtFiles.Count})", GameUI.BtnStyle, GUILayout.Width(142), GUILayout.Height(27)))
            {
                GameUI.PlayClickSound(1);
                importTxtFiles = PtcgoImporter.ListTxtFiles();
                if (lastImportReport == null)
                    lastImportReport = PtcgoImporter.AnalyzeDeck(deckDef.Name, currentDeck);
                showThemeDecksModal = false;
                showImportModal = !showImportModal;
            }
            if (GUILayout.Button("📋 Faltantes", GameUI.BtnStyle, GUILayout.Width(104), GUILayout.Height(27)))
            {
                GameUI.PlayClickSound(0);
                importTxtFiles = PtcgoImporter.ListTxtFiles();
                lastImportReport = PtcgoImporter.AnalyzeDeck(deckDef.Name, currentDeck);
                showThemeDecksModal = false;
                showImportModal = true;
            }

            GUILayout.Space(6);
            GUILayout.Label("<b>Formato:</b>", GameUI.SubHeaderStyle, GUILayout.Width(62));
            foreach (var f in FormatRegistry.Formats)
            {
                if (FilterBtn(f.Name, f == format))
                {
                    format = f;
                    currentPage = 0;
                    statusMsg = $"Formato: {f.Name}. {f.Description}";
                }
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Space(3);

            // Fila 3: Filtros por Expansión Base Era + Toggle de ver cartas bloqueadas/faltantes
            GUILayout.BeginHorizontal(GameUI.SubBoxStyle);
            GUILayout.Label("<b>Expansión:</b>", GameUI.SubHeaderStyle, GUILayout.Width(76));
            if (FilterBtn("Todas", filterSetId == "")) { filterSetId = ""; currentPage = 0; }
            foreach (var s in CardDatabase.Sets)
            {
                if (!format.Sets.Contains("*") && !format.Sets.Any(fs => string.Equals(fs, s.Id, StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (FilterBtn(s.Name, filterSetId == s.Id))
                {
                    filterSetId = s.Id;
                    currentPage = 0;
                }
            }
            GUILayout.FlexibleSpace();
            if (FilterBtn(showUnownedLocked ? "👁 Viendo todas (🔒 faltantes)" : "👁 Solo mis cartas", showUnownedLocked))
            {
                showUnownedLocked = !showUnownedLocked;
                currentPage = 0;
                statusMsg = showUnownedLocked
                    ? "Mostrando también cartas no poseídas atenuadas en 3D (Clic derecho para inspeccionar)."
                    : "Mostrando únicamente cartas disponibles en tu colección.";
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(3);

            // Fila 4: Búsqueda en tiempo real + Categoría + Tipo/Elemento
            GUILayout.BeginHorizontal(GameUI.SubBoxStyle);
            GUILayout.Label("<b>🔍 Buscar:</b>", GameUI.SubHeaderStyle, GUILayout.Width(74));
            GUI.SetNextControlName("BuilderSearchField");
            string nextSearch = GUILayout.TextField(searchQuery ?? "", 45, GameUI.InputFieldStyle, GUILayout.Width(210), GUILayout.Height(27));
            if (!string.Equals(nextSearch, searchQuery, StringComparison.Ordinal))
            {
                searchQuery = nextSearch;
                currentPage = 0;
            }
            if (!string.IsNullOrEmpty(searchQuery) && GUILayout.Button("✖", GameUI.BtnStyle, GUILayout.Width(28), GUILayout.Height(27)))
            {
                GameUI.PlayClickSound(0);
                searchQuery = "";
                currentPage = 0;
            }

            GUILayout.Space(10);
            GUILayout.Label("<b>Categoría:</b>", GameUI.SubHeaderStyle, GUILayout.Width(72));
            if (FilterBtn("Todas", filterSupertype == -1)) { filterSupertype = -1; currentPage = 0; }
            if (FilterBtn("Pokémon", filterSupertype == 0)) { filterSupertype = 0; currentPage = 0; }
            if (FilterBtn("Entrenadores", filterSupertype == 1)) { filterSupertype = 1; currentPage = 0; }
            if (FilterBtn("Energías", filterSupertype == 2)) { filterSupertype = 2; currentPage = 0; }

            GUILayout.Space(10);
            GUILayout.Label("<b>Elemento:</b>", GameUI.SubHeaderStyle, GUILayout.Width(68));
            if (FilterBtn("Todos", filterType == "")) { filterType = ""; currentPage = 0; }
            foreach (var tp in new[] { "Fire", "Water", "Grass", "Lightning", "Psychic", "Fighting", "Colorless" })
            {
                if (FilterBtn(tp, filterType == tp))
                {
                    filterType = tp;
                    currentPage = 0;
                }
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.EndArea();
        }

        void DrawCatalogOverlay(Rect catalogOuterRect, int totalFiltered, int startIdx)
        {
            // Barra informativa superior sobre el tapete 3D
            Rect topStrip = new Rect(catalogOuterRect.x, catalogOuterRect.y, catalogOuterRect.width, 26f);
            GUI.Box(topStrip, "", sPillBgStyle);
            int shownEnd = Mathf.Min(totalFiltered, startIdx + pageCardsBuffer.Count);
            string rangeTxt = totalFiltered > 0 ? $"{startIdx + 1}–{shownEnd} de {totalFiltered}" : "0";
            GUI.Label(
                new Rect(topStrip.x + 10f, topStrip.y + 2f, topStrip.width - 20f, 22f),
                $"<b>Tapete de Colección 3D:</b> <color=#9ecbff>{rangeTxt} cartas</color>   •   <color=#dce6f2>Clic Izq: Añadir al mazo  |  Clic Der: Inspección 3D + Voltear  |  Rueda / ◀ ▶: Pasar página</color>",
                GameUI.SubHeaderStyle);

            // Si no hay resultados, mostrar mensaje claro sobre el tapete 3D vacío
            if (totalFiltered == 0)
            {
                Rect emptyBox = new Rect(
                    catalogOuterRect.center.x - 290f,
                    catalogOuterRect.center.y - 58f,
                    580f,
                    116f);
                GUI.Box(emptyBox, "", GameUI.BoxBgStyle);
                GUILayout.BeginArea(new Rect(emptyBox.x + 16f, emptyBox.y + 14f, emptyBox.width - 32f, emptyBox.height - 28f));
                GUILayout.Label(
                    Collection.Enforced && !showUnownedLocked
                        ? "<color=#ffd166><b>No tienes cartas en tu colección para este filtro/búsqueda.</b></color>\nAbre sobres de las expansiones de MyCurio o pulsa abajo para previsualizar en 3D las cartas faltantes."
                        : "<color=#ffd166><b>No hay cartas que coincidan con la búsqueda actual.</b></color>",
                    GameUI.SubHeaderStyle);
                GUILayout.Space(8);
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (!showUnownedLocked && GUILayout.Button("👁 Previsualizar cartas faltantes en 3D", GameUI.BtnGoldStyle, GUILayout.Width(280), GUILayout.Height(30)))
                {
                    GameUI.PlayClickSound(0);
                    showUnownedLocked = true;
                    currentPage = 0;
                }
                if (!string.IsNullOrEmpty(searchQuery) && GUILayout.Button("Limpiar búsqueda", GameUI.BtnStyle, GUILayout.Width(150), GUILayout.Height(30)))
                {
                    GameUI.PlayClickSound(0);
                    searchQuery = "";
                    currentPage = 0;
                }
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.EndArea();
            }
            else
            {
                // Etiquetas flotantes proyectadas debajo de cada carta física 3D del carrusel
                var slots = stage3D.ScreenSlots;
                for (int i = 0; i < slots.Count; i++)
                {
                    var s = slots[i];
                    if (s.Card == null) continue;
                    int inDeck = currentDeck.Count(x => x.Id == s.Card.Id);
                    int owned = Collection.Owned(s.Card);
                    string ownedStr = owned == int.MaxValue ? "∞" : owned.ToString();

                    float pillW = Mathf.Clamp(s.ApproxGuiRect.width + 24f, 134f, 196f);
                    float pillH = 34f;
                    Rect pillRect = new Rect(s.BottomCenterGui.x - pillW * 0.5f, s.BottomCenterGui.y + 2f, pillW, pillH);

                    GUI.Box(pillRect, "", sPillBgStyle);
                    string line2 = owned <= 0
                        ? "<color=#ff7b7b>🔒 Faltante (0)</color>"
                        : $"Mazo: <color=#80ed99><b>{inDeck}</b></color> • Tienes: <b>{ownedStr}</b>";
                    GUI.Label(pillRect, $"<b>{s.Card.Name}</b>\n{line2}", sPillLabelStyle);
                }
            }

            // Barra inferior de paginación 3D (Carrusel)
            Rect navBar = new Rect(catalogOuterRect.x, catalogOuterRect.yMax - 36f, catalogOuterRect.width, 34f);
            GUI.Box(navBar, "", sPillBgStyle);
            GUILayout.BeginArea(new Rect(navBar.x + 10f, navBar.y + 3f, navBar.width - 20f, 28f));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("⏮ Primera", GameUI.BtnStyle, GUILayout.Width(95), GUILayout.Height(26)))
            {
                if (currentPage > 0) { currentPage = 0; stage3D.TriggerCarouselSlide(-1); GameUI.PlayClickSound(0); }
            }
            if (GUILayout.Button("◀ Anterior", GameUI.BtnGoldStyle, GUILayout.Width(115), GUILayout.Height(26)))
            {
                ChangePage(-1);
            }
            GUILayout.FlexibleSpace();
            GUILayout.Label($"<size=14><b>Página {currentPage + 1} / {lastTotalPages}</b></size>", GameUI.HeaderStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Siguiente ▶", GameUI.BtnGoldStyle, GUILayout.Width(115), GUILayout.Height(26)))
            {
                ChangePage(1);
            }
            if (GUILayout.Button("Última ⏭", GameUI.BtnStyle, GUILayout.Width(95), GUILayout.Height(26)))
            {
                if (currentPage < lastTotalPages - 1) { currentPage = lastTotalPages - 1; stage3D.TriggerCarouselSlide(1); GameUI.PlayClickSound(0); }
            }
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        void DrawRightDeckPanel(Rect panelRect)
        {
            // Dejar una ventana abierta arriba a la izquierda del panel derecho para ver el Pedestal y la Pila Física 3D del Mazo
            float headerBlockH = 182f;
            Rect statsBox = new Rect(panelRect.x + 148f, panelRect.y, panelRect.width - 148f, headerBlockH);
            GUI.Box(statsBox, "", GameUI.BoxBgStyle);

            // Etiqueta bajo el Pedestal 3D del mazo
            Rect pedestalCaption = new Rect(lastDeckPedestalGuiRect.x, lastDeckPedestalGuiRect.yMax - 2f, lastDeckPedestalGuiRect.width, 22f);
            GUI.Box(pedestalCaption, "", sPillBgStyle);
            GUI.Label(pedestalCaption, $"<b>Pila 3D ({currentDeck.Count})</b>", sPillLabelStyle);

            int nBasic = currentDeck.Count(c => c.IsBasicPokemon);
            int nEvo = currentDeck.Count(c => c.Supertype == Supertype.Pokemon && !c.IsBasicPokemon);
            int nTr = currentDeck.Count(c => c.Supertype == Supertype.Trainer);
            int nEn = currentDeck.Count(c => c.Supertype == Supertype.Energy);
            var errs = DeckValidator.Validate(currentDeck, format);
            string colorCount = errs.Count == 0 ? "#80ed99" : "#ffd166";

            GUILayout.BeginArea(new Rect(statsBox.x + 12f, statsBox.y + 8f, statsBox.width - 24f, statsBox.height - 16f));
            GUILayout.Label($"<size=17><b>Mazo Activo: <color={colorCount}>{currentDeck.Count} / {format.DeckSize}</color></b></size>", GameUI.HeaderStyle);
            GUILayout.Label($"Básicos: <b>{nBasic}</b>  •  Evol.: <b>{nEvo}</b>\nEntren.: <b>{nTr}</b>  •  Energías: <b>{nEn}</b>", GameUI.SubHeaderStyle);
            GUILayout.Label(
                errs.Count == 0
                    ? "<color=#80ed99>✔ Mazo válido para jugar</color>"
                    : $"<color=#ffd166>⚠ {errs[0]}</color>",
                GameUI.CardLabelStyle);

            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("⚡ Autocompletar", GameUI.BtnGoldStyle, GUILayout.Height(26)))
            {
                GameUI.PlayClickSound(1);
                AutoFillFromCollection();
            }
            if (GUILayout.Button("🧹 Limpiar", GameUI.BtnStyle, GUILayout.Width(82), GUILayout.Height(26)))
            {
                GameUI.PlayClickSound(0);
                RemoveInvalidFromDeck();
            }
            if (GUILayout.Button("🗑", GameUI.BtnRedStyle, GUILayout.Width(34), GUILayout.Height(26)))
            {
                GameUI.PlayClickSound(2);
                currentDeck.Clear();
                statusMsg = "Mazo vaciado.";
            }
            GUILayout.EndHorizontal();
            GUILayout.EndArea();

            // Lista agrupada desplazable debajo del Pedestal 3D y resumen
            Rect listBox = new Rect(panelRect.x, panelRect.y + headerBlockH + 6f, panelRect.width, panelRect.height - headerBlockH - 6f);
            GUI.Box(listBox, "", GameUI.BoxBgStyle);

            GUILayout.BeginArea(new Rect(listBox.x + 10f, listBox.y + 8f, listBox.width - 20f, listBox.height - 16f));
            deckScroll = GUILayout.BeginScrollView(deckScroll, GUILayout.ExpandHeight(true));

            if (currentDeck.Count == 0)
            {
                GUILayout.Label("<color=#aebcd0><i>Tu mazo está vacío.\nHaz clic izquierdo sobre cualquier carta 3D del tapete o carga uno de los 25 Mazos Oficiales de Base Era.</i></color>", GameUI.SubHeaderStyle);
            }
            else
            {
                var groups = currentDeck
                    .GroupBy(c => c.Id)
                    .OrderBy(g => g.First().Supertype)
                    .ThenBy(g => g.First().SetId)
                    .ThenBy(g => g.First().Number)
                    .ToList();

                foreach (var grp in groups)
                {
                    var c = grp.First();
                    int qty = grp.Count();
                    int owned = Collection.Owned(c);
                    bool missingOwned = Collection.Enforced && qty > owned;
                    string qtyCol = missingOwned ? "#ff6b6b" : "#80ed99";

                    GUILayout.BeginHorizontal(GameUI.SubBoxStyle);
                    GUILayout.Label(
                        $"<color={qtyCol}><b>{qty}x</b></color> {c.Name} <color=#9ecbff><size=11>({c.Id})</size></color>",
                        GameUI.SubHeaderStyle);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("?", GameUI.BtnStyle, GUILayout.Width(28), GUILayout.Height(24)))
                    {
                        GameUI.PlayClickSound(1);
                        OpenInspect3D(c, lastDeckPedestalGuiRect, stage3D.DeckStackWorldPos, stage3D.DeckStackWorldRot, stage3D.DeckStackScale);
                    }
                    if (GUILayout.Button("-", GameUI.BtnRedStyle, GUILayout.Width(28), GUILayout.Height(24)))
                    {
                        if (RemoveCardFromDeck(c)) GameUI.PlayClickSound(0);
                    }
                    if (GUILayout.Button("+", GameUI.BtnGreenStyle, GUILayout.Width(28), GUILayout.Height(24)))
                    {
                        if (AddCardToDeck(c, -1)) GameUI.PlayClickSound(0);
                        else GameUI.PlayClickSound(2);
                    }
                    GUILayout.EndHorizontal();
                }
            }

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        void ExportCurrentDeckToTxt()
        {
            if (currentDeck.Count == 0)
            {
                statusMsg = "<color=#ffd166>El mazo está vacío; añade cartas antes de exportar a .txt.</color>";
                return;
            }
            try
            {
                string name = string.IsNullOrWhiteSpace(deckNameEdit) ? deckDef.Name : deckNameEdit.Trim();
                string outPath = PtcgoImporter.ExportToFile(name, currentDeck);
                importTxtFiles = PtcgoImporter.ListTxtFiles();
                statusMsg = $"<color=#80ed99>📤 Exportado '{Path.GetFileName(outPath)}' en import/ y copiado al portapapeles.</color>";
            }
            catch (Exception e)
            {
                statusMsg = "<color=#ff6b6b>Error al exportar .txt: " + e.Message + "</color>";
            }
        }

        void ImportFromClipboard()
        {
            string clip = "";
            try { clip = GUIUtility.systemCopyBuffer ?? ""; } catch { }
            if (string.IsNullOrWhiteSpace(clip))
            {
                statusMsg = "<color=#ffd166>El portapapeles está vacío. Copia primero una lista en formato PTCGO / PTCG Live.</color>";
                return;
            }

            lastImportReport = PtcgoImporter.ImportFromText("Mazo Portapapeles", clip);
            if (lastImportReport.AllParsedItems.Count == 0)
            {
                statusMsg = "<color=#ff6b6b>No se detectaron líneas válidas de mazo en el portapapeles.</color>";
                return;
            }

            currentDeck.Clear();
            currentDeck.AddRange(lastImportReport.ResolvedPlayableCards);
            deckDef = new DeckDef { Name = UniqueName(lastImportReport.SuggestedDeckName), Format = format.Id };
            deckNameEdit = deckDef.Name;

            int miss = lastImportReport.MissingOrUnavailable.Sum(x => x.Missing);
            statusMsg = miss == 0
                ? $"<color=#80ed99>📋 Importado desde portapapeles ({currentDeck.Count} cartas). Pulsa '✔ Guardar y Activar'.</color>"
                : $"<color=#ffd166>📋 Importado con {miss} copia(s) faltante(s); revisa el detalle a la derecha.</color>";
        }

        void DrawThemeDecksModal(Rect parentWin)
        {
            float mw = Mathf.Min(parentWin.width - 60f, 1340f);
            float mh = Mathf.Min(parentWin.height - 60f, 820f);
            Rect mRect = new Rect((Screen.width - mw) * 0.5f, (Screen.height - mh) * 0.5f, mw, mh);
            GUI.Box(new Rect(0, 0, Screen.width, Screen.height), "");
            GUI.Box(mRect, "", GameUI.BoxBgStyle);

            GUILayout.BeginArea(new Rect(mRect.x + 18, mRect.y + 14, mRect.width - 36, mRect.height - 28));

            GUILayout.BeginHorizontal();
            GUILayout.Label($"<size=20><b>📦 Mazos Temáticos Oficiales de Base Era ({ThemeDeckRegistry.Decks.Count} mazos) — Jugador y NPCs</b></size>", GameUI.HeaderStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("✖ Cerrar", GameUI.BtnRedStyle, GUILayout.Width(115), GUILayout.Height(34)))
            {
                GameUI.PlayClickSound(2);
                showThemeDecksModal = false;
                GUILayout.EndHorizontal();
                GUILayout.EndArea();
                return;
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(8);

            // Configuración de Mazos de los NPCs
            string npcMode = Plugin.NpcDeckMode?.Value ?? "ThemeOrRandom";
            GUILayout.BeginHorizontal(GameUI.SubBoxStyle);
            GUILayout.Label("<b>🤖 Mazos de los Clientes (NPCs):</b>", GameUI.SubHeaderStyle, GUILayout.Width(240));
            if (FilterBtn("Oficiales + Aleatorios", string.Equals(npcMode, "ThemeOrRandom", StringComparison.OrdinalIgnoreCase)))
                if (Plugin.NpcDeckMode != null) Plugin.NpcDeckMode.Value = "ThemeOrRandom";
            if (FilterBtn("Solo Mazos Oficiales Base Era", string.Equals(npcMode, "ThemeOnly", StringComparison.OrdinalIgnoreCase)))
                if (Plugin.NpcDeckMode != null) Plugin.NpcDeckMode.Value = "ThemeOnly";
            if (FilterBtn("Solo Aleatorios", string.Equals(npcMode, "RandomOnly", StringComparison.OrdinalIgnoreCase)))
                if (Plugin.NpcDeckMode != null) Plugin.NpcDeckMode.Value = "RandomOnly";

            var specificNpcDeck = ThemeDeckRegistry.Decks.FirstOrDefault(d =>
                string.Equals(d.Id, npcMode, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.Name, npcMode, StringComparison.OrdinalIgnoreCase));
            if (specificNpcDeck != null)
            {
                GUILayout.Space(10);
                GUILayout.Label($"<color=#ffd166>Fijado a: <b>{specificNpcDeck.Name} ({specificNpcDeck.SetName})</b></color>", GameUI.SubHeaderStyle);
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Space(6);

            // Filtro por expansión de los mazos temáticos
            GUILayout.BeginHorizontal(GameUI.SubBoxStyle);
            GUILayout.Label("<b>Filtrar por Expansión:</b>", GameUI.SubHeaderStyle, GUILayout.Width(160));
            if (FilterBtn($"Todas ({ThemeDeckRegistry.Decks.Count})", themeFilterSetId == "")) themeFilterSetId = "";
            foreach (var grp in ThemeDeckRegistry.Decks.GroupBy(d => d.SetId))
            {
                string sName = grp.First().SetName ?? grp.Key;
                if (FilterBtn($"{sName} ({grp.Count()})", themeFilterSetId == grp.Key))
                    themeFilterSetId = grp.Key;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Space(8);

            themeDecksScroll = GUILayout.BeginScrollView(themeDecksScroll, GUILayout.ExpandHeight(true));
            var list = ThemeDeckRegistry.Decks
                .Where(d => string.IsNullOrEmpty(themeFilterSetId) || string.Equals(d.SetId, themeFilterSetId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var td in list)
            {
                int ownedCount = td.CountOwnedInCollection(out int totalNeeded);
                bool hasAll = ownedCount >= totalNeeded;
                string ownedColor = hasAll ? "#80ed99" : "#ffd166";
                string typesTxt = td.Types != null && td.Types.Count > 0 ? string.Join(" / ", td.Types) : "Mixto";
                bool isNpcFixed = string.Equals(npcMode, td.Id, StringComparison.OrdinalIgnoreCase);

                GUILayout.BeginHorizontal(GameUI.SubBoxStyle);
                GUILayout.BeginVertical();
                GUILayout.Label($"<size=16><b>{td.Name}</b></size>  <color=#9ecbff>[{td.SetName}]</color>   —   Tipos: <b>{typesTxt}</b>", GameUI.HeaderStyle);
                string summary = string.Join(", ", td.Cards.Take(6).Select(c => $"{c.Count}x {c.Name}"));
                if (td.Cards.Count > 6) summary += $" (+{td.Cards.Count - 6} más)";
                GUILayout.Label($"<color=#cccccc>{summary}</color>", GameUI.SubHeaderStyle);
                GUILayout.EndVertical();

                GUILayout.FlexibleSpace();

                GUILayout.Label($"En tu colección:\n<color={ownedColor}><b>{ownedCount} / {totalNeeded} cartas</b></color>", GameUI.SubHeaderStyle, GUILayout.Width(135));
                GUILayout.Space(8);

                if (GUILayout.Button("📥 Cargar en Editor", GameUI.BtnStyle, GUILayout.Width(155), GUILayout.Height(34)))
                {
                    GameUI.PlayClickSound(1);
                    var d = td.ToDeckDef(UniqueName($"{td.Name} ({td.SetName})"));
                    LoadDeck(d);
                    lastImportReport = PtcgoImporter.AnalyzeDeck(d.Name, currentDeck);
                    showThemeDecksModal = false;
                    statusMsg = $"<color=#80ed99>Mazo oficial '{td.Name}' cargado en el editor 3D ({ownedCount}/{totalNeeded} en colección).</color>";
                }
                GUILayout.Space(6);

                if (GUILayout.Button("✔ Guardar y Usar", GameUI.BtnGreenStyle, GUILayout.Width(150), GUILayout.Height(34)))
                {
                    GameUI.PlayClickSound(1);
                    var d = td.ToDeckDef(UniqueName($"{td.Name} ({td.SetName})"));
                    DeckStore.Save(d);
                    deckNames = DeckStore.List();
                    LoadDeck(d);
                    var errs = DeckValidator.Validate(currentDeck, format);
                    if (errs.Count == 0)
                    {
                        DeckStore.SetActive(d.Name);
                        statusMsg = $"<color=#80ed99>Mazo oficial '{d.Name}' guardado y activado para jugar.</color>";
                    }
                    else
                    {
                        statusMsg = $"<color=#ffd166>Mazo '{d.Name}' guardado. Pulsa '📋 Faltantes' para ver qué sobres abrir ({errs[0]}).</color>";
                    }
                    showThemeDecksModal = false;
                }
                GUILayout.Space(6);

                if (GUILayout.Button(isNpcFixed ? "🤖 NPC Fijado ✔" : "🤖 Asignar a NPC", isNpcFixed ? GameUI.BtnGoldStyle : GameUI.BtnStyle, GUILayout.Width(145), GUILayout.Height(34)))
                {
                    GameUI.PlayClickSound(1);
                    if (Plugin.NpcDeckMode != null)
                        Plugin.NpcDeckMode.Value = isNpcFixed ? "ThemeOrRandom" : td.Id;
                    statusMsg = isNpcFixed
                        ? "El NPC volverá a elegir entre todos los mazos oficiales y aleatorios."
                        : $"<color=#ffd166>Los clientes (NPCs) jugarán ahora con el mazo oficial: {td.Name} ({td.SetName}).</color>";
                }
                GUILayout.EndHorizontal();
                GUILayout.Space(4);
            }

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        void DrawImportAndMissingModal(Rect parentWin)
        {
            float mw = Mathf.Min(parentWin.width - 60f, 1340f);
            float mh = Mathf.Min(parentWin.height - 60f, 820f);
            Rect mRect = new Rect((Screen.width - mw) * 0.5f, (Screen.height - mh) * 0.5f, mw, mh);
            GUI.Box(new Rect(0, 0, Screen.width, Screen.height), "");
            GUI.Box(mRect, "", GameUI.BoxBgStyle);

            GUILayout.BeginArea(new Rect(mRect.x + 18, mRect.y + 14, mRect.width - 36, mRect.height - 28));

            GUILayout.BeginHorizontal();
            GUILayout.Label("<size=20><b>📥 Importar / Exportar (.txt PTCGO / PTCG Live) y Reporte de Colección</b></size>", GameUI.HeaderStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("📋 Pegar Portapapeles", GameUI.BtnGreenStyle, GUILayout.Width(185), GUILayout.Height(34)))
            {
                GameUI.PlayClickSound(1);
                ImportFromClipboard();
            }
            GUILayout.Space(6);
            if (GUILayout.Button("📤 Exportar Actual", GameUI.BtnStyle, GUILayout.Width(155), GUILayout.Height(34)))
            {
                GameUI.PlayClickSound(1);
                ExportCurrentDeckToTxt();
            }
            GUILayout.Space(6);
            if (GUILayout.Button("📂 Abrir Carpeta .txt", GameUI.BtnGoldStyle, GUILayout.Width(170), GUILayout.Height(34)))
            {
                GameUI.PlayClickSound(0);
                PtcgoImporter.OpenImportFolder();
            }
            GUILayout.Space(6);
            if (GUILayout.Button("🔄 Refrescar", GameUI.BtnStyle, GUILayout.Width(120), GUILayout.Height(34)))
            {
                GameUI.PlayClickSound(0);
                importTxtFiles = PtcgoImporter.ListTxtFiles();
                Collection.Invalidate();
                if (lastImportReport != null && !string.IsNullOrEmpty(lastImportReport.FileName))
                {
                    string p = Path.Combine(PtcgoImporter.ImportDir, lastImportReport.FileName);
                    if (File.Exists(p)) lastImportReport = PtcgoImporter.ImportFromFile(p);
                    else lastImportReport = PtcgoImporter.AnalyzeDeck(deckDef.Name, currentDeck);
                }
            }
            GUILayout.Space(6);
            if (GUILayout.Button("✖ Cerrar", GameUI.BtnRedStyle, GUILayout.Width(105), GUILayout.Height(34)))
            {
                GameUI.PlayClickSound(2);
                showImportModal = false;
                GUILayout.EndHorizontal();
                GUILayout.EndArea();
                return;
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            GUILayout.Label($"<color=#9ecbff>Carpeta en vivo:</color> <b>{PtcgoImporter.ImportDir}</b> — Pega archivos <b>.txt</b> ahí o usa <b>📋 Pegar Portapapeles</b> directamente.", GameUI.SubHeaderStyle);
            GUILayout.Space(8);

            GUILayout.BeginHorizontal();

            // Columna izquierda: archivos .txt detectados en la carpeta
            float leftW = 360f;
            GUILayout.BeginVertical(GameUI.SubBoxStyle, GUILayout.Width(leftW), GUILayout.ExpandHeight(true));
            GUILayout.Label($"<size=16><b>Archivos .txt detectados ({importTxtFiles.Count})</b></size>", GameUI.HeaderStyle);
            GUILayout.Space(4);
            if (GUILayout.Button("📋 Analizar mi mazo actual", GameUI.BtnStyle, GUILayout.Height(30)))
            {
                GameUI.PlayClickSound(0);
                lastImportReport = PtcgoImporter.AnalyzeDeck(deckDef.Name, currentDeck);
            }
            GUILayout.Space(6);

            importFilesScroll = GUILayout.BeginScrollView(importFilesScroll, GUILayout.ExpandHeight(true));
            if (importTxtFiles.Count == 0)
            {
                GUILayout.Label("<color=#ffd166>No hay archivos .txt en la carpeta todavía.\n\nPulsa '📂 Abrir Carpeta .txt' para pegar listas o usa '📋 Pegar Portapapeles' arriba.</color>", GameUI.SubHeaderStyle);
            }
            else
            {
                foreach (var file in importTxtFiles)
                {
                    string fname = Path.GetFileName(file);
                    GUILayout.BeginHorizontal(GameUI.SubBoxStyle);
                    GUILayout.Label($"<b>{fname}</b>", GameUI.SubHeaderStyle, GUILayout.Width(205));
                    if (GUILayout.Button("📥 Importar", GameUI.BtnGreenStyle, GUILayout.Height(28)))
                    {
                        GameUI.PlayClickSound(1);
                        ImportTxtFile(file);
                    }
                    GUILayout.EndHorizontal();
                }
            }
            GUILayout.EndScrollView();
            GUILayout.EndVertical();

            GUILayout.Space(12);

            // Columna derecha: Reporte detallado de cartas faltantes y en qué expansión de MyCurio salen
            GUILayout.BeginVertical(GameUI.SubBoxStyle, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            if (lastImportReport == null)
            {
                GUILayout.Label("Selecciona un archivo <b>.txt</b> a la izquierda o pega desde el portapapeles para ver qué cartas te faltan y en qué sobres de MyCurio se consiguen.", GameUI.SubHeaderStyle);
            }
            else
            {
                int missEntries = lastImportReport.MissingOrUnavailable.Count;
                int totalMissingCopies = lastImportReport.MissingOrUnavailable.Sum(x => x.Missing);
                GUILayout.Label($"<size=17><b>Reporte de Mazo: {lastImportReport.FileName}</b> ({lastImportReport.TotalRequestedCards} cartas en lista)</size>", GameUI.HeaderStyle);
                if (missEntries == 0)
                {
                    GUILayout.Label("<color=#80ed99><b>✔ ¡Tienes todas las cartas necesarias en tu colección y están listas para jugar!</b></color>", GameUI.SubHeaderStyle);
                }
                else
                {
                    GUILayout.Label($"<color=#ffd166><b>⚠ Te faltan {totalMissingCopies} copia(s) en tu colección (o requieren otra expansión) en {missEntries} entrada(s):</b></color>", GameUI.SubHeaderStyle);
                }
                GUILayout.Space(6);

                importReportScroll = GUILayout.BeginScrollView(importReportScroll, GUILayout.ExpandHeight(true));
                foreach (var item in lastImportReport.AllParsedItems)
                {
                    bool enoughOwned = item.Owned >= item.Wanted;
                    string ownedStr = item.Owned == int.MaxValue ? "∞" : item.Owned.ToString();
                    string codeTag = !string.IsNullOrEmpty(item.RequestedSetCode)
                        ? $" <color=#9ecbff>[{item.RequestedSetCode} {item.RequestedNumber}]</color>"
                        : "";

                    string statusBadge;
                    if (!enoughOwned)
                        statusBadge = $"<color=#ff6b6b><b>Te faltan {item.Missing}</b> (tienes {ownedStr} / pide {item.Wanted})</color>";
                    else
                        statusBadge = $"<color=#80ed99><b>Tienes {ownedStr}/{item.Wanted}</b></color>";

                    if (!item.RulesReadyInMod && !string.IsNullOrEmpty(item.Note))
                        statusBadge += $"   <color=#ffd166>[{item.Note}]</color>";

                    string expColor = item.InstalledInMyCurio ? "#9ecbff" : "#ffd166";

                    GUILayout.BeginVertical(GameUI.SubBoxStyle);
                    GUILayout.Label($"<b>{item.Wanted}x {item.CardName}</b>{codeTag}   —   {statusBadge}", GameUI.SubHeaderStyle);
                    GUILayout.Label($"   📦 <color={expColor}><b>Dónde conseguirla:</b> {item.ExpansionWhereToFind}</color>", GameUI.CardLabelStyle);
                    GUILayout.EndVertical();
                    GUILayout.Space(3);
                }
                GUILayout.EndScrollView();
            }
            GUILayout.EndVertical();

            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        void ImportTxtFile(string filePath)
        {
            lastImportReport = PtcgoImporter.ImportFromFile(filePath);
            currentDeck.Clear();
            currentDeck.AddRange(lastImportReport.ResolvedPlayableCards);
            string baseName = string.IsNullOrWhiteSpace(lastImportReport.SuggestedDeckName) ? "Mazo importado" : lastImportReport.SuggestedDeckName;
            deckDef = new DeckDef { Name = UniqueName(baseName), Format = format.Id };
            deckNameEdit = deckDef.Name;

            int miss = lastImportReport.MissingOrUnavailable.Sum(x => x.Missing);
            if (miss == 0 && lastImportReport.MissingOrUnavailable.Count == 0)
                statusMsg = $"<color=#80ed99>Mazo '{deckDef.Name}' importado completo ({currentDeck.Count} cartas). Pulsa '✔ Guardar y Activar'.</color>";
            else
                statusMsg = $"<color=#ffd166>Importado '{deckDef.Name}': revisa en el reporte qué cartas te faltan y en qué sobres salen.</color>";
        }

        void RemoveInvalidFromDeck()
        {
            Collection.Invalidate();
            var valid = new List<CardDef>();
            int removed = 0;
            foreach (var c in currentDeck)
            {
                if (DeckValidator.RemainingAllowed(c, valid, format) > 0)
                    valid.Add(c);
                else
                    removed++;
            }
            currentDeck.Clear();
            currentDeck.AddRange(valid);
            statusMsg = removed > 0
                ? $"<color=#80ed99>Se quitaron {removed} cartas que no tienes o exceden el límite ({currentDeck.Count}/{format.DeckSize}).</color>"
                : "Todas las cartas actuales del mazo están en tu colección.";
        }

        void AutoFillFromCollection()
        {
            RemoveInvalidFromDeck();
            var pool = CardDatabase.All.Where(c => c.IsPlayable && format.Allows(c) && Collection.Owned(c) > 0).ToList();

            // 1. Añadir Pokémon Básicos que el jugador posea (hasta ~16 Pokémon básicos)
            var basics = pool.Where(c => c.IsBasicPokemon).OrderByDescending(c => c.Hp).ToList();
            foreach (var b in basics)
            {
                if (currentDeck.Count(x => x.IsBasicPokemon) >= 16 || currentDeck.Count >= format.DeckSize) break;
                while (DeckValidator.RemainingAllowed(b, currentDeck, format) > 0 &&
                       currentDeck.Count(x => x.IsBasicPokemon) < 16 &&
                       currentDeck.Count < format.DeckSize)
                {
                    currentDeck.Add(b);
                }
            }

            // 2. Añadir Evoluciones cuyas pre-evoluciones estén en el mazo
            for (int pass = 0; pass < 2; pass++)
            {
                var evos = pool.Where(c => c.Supertype == Supertype.Pokemon && !c.IsBasicPokemon).ToList();
                foreach (var evo in evos)
                {
                    if (currentDeck.Count(x => x.Supertype == Supertype.Pokemon) >= 22 || currentDeck.Count >= format.DeckSize) break;
                    if (!currentDeck.Any(x => string.Equals(x.Name, evo.EvolvesFrom, StringComparison.OrdinalIgnoreCase))) continue;
                    while (DeckValidator.RemainingAllowed(evo, currentDeck, format) > 0 &&
                           currentDeck.Count(x => x.Supertype == Supertype.Pokemon) < 22 &&
                           currentDeck.Count < format.DeckSize)
                    {
                        currentDeck.Add(evo);
                    }
                }
            }

            // 3. Añadir Entrenadores que posea el jugador (hasta ~14 entrenadores)
            var trainers = pool.Where(c => c.Supertype == Supertype.Trainer).ToList();
            foreach (var tr in trainers)
            {
                if (currentDeck.Count(x => x.Supertype == Supertype.Trainer) >= 14 || currentDeck.Count >= format.DeckSize) break;
                while (DeckValidator.RemainingAllowed(tr, currentDeck, format) > 0 &&
                       currentDeck.Count(x => x.Supertype == Supertype.Trainer) < 14 &&
                       currentDeck.Count < format.DeckSize)
                {
                    currentDeck.Add(tr);
                }
            }

            // 4. Completar hasta 60 cartas con Energías Básicas de los tipos de los Pokémon del mazo
            var pokeTypes = currentDeck.Where(c => c.Supertype == Supertype.Pokemon && !string.IsNullOrEmpty(c.Type) && c.Type != "Colorless")
                                       .Select(c => c.Type)
                                       .Distinct()
                                       .ToList();
            var basicEnergies = pool.Where(c => c.IsBasicEnergy && pokeTypes.Contains(c.EnergyProvides)).ToList();
            if (basicEnergies.Count == 0)
                basicEnergies = pool.Where(c => c.IsBasicEnergy).ToList();

            int eIdx = 0;
            while (currentDeck.Count < format.DeckSize && basicEnergies.Count > 0 && eIdx < 200)
            {
                var en = basicEnergies[eIdx % basicEnergies.Count];
                if (DeckValidator.RemainingAllowed(en, currentDeck, format) > 0)
                    currentDeck.Add(en);
                eIdx++;
            }

            if (currentDeck.Count == format.DeckSize && currentDeck.Any(c => c.IsBasicPokemon))
                statusMsg = "<color=#80ed99>⚡ Mazo completado a 60 cartas con tu colección. Pulsa '✔ Guardar y Activar'.</color>";
            else
                statusMsg = "<color=#ffd166>Abre al menos 1 sobre para tener un Pokémon Básico en tu colección.</color>";
        }

        void CycleDeck(int dir)
        {
            deckNames = DeckStore.List();
            if (deckNames.Count == 0) { statusMsg = "No hay mazos guardados."; return; }
            int i = deckNames.FindIndex(n => string.Equals(n, deckDef.Name, StringComparison.OrdinalIgnoreCase));
            i = ((i < 0 ? 0 : i + dir) % deckNames.Count + deckNames.Count) % deckNames.Count;
            LoadDeck(DeckStore.Load(deckNames[i]));
            statusMsg = $"Mazo cargado: {deckDef.Name}";
        }

        bool FilterBtn(string text, bool active)
        {
            bool clicked = GUILayout.Button(text, active ? GameUI.BtnGoldStyle : GameUI.BtnStyle, GUILayout.Height(26));
            if (clicked) GameUI.PlayClickSound(0);
            return clicked;
        }
    }
}
