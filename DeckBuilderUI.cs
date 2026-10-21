using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace PokeTCGPlay
{
    /// <summary>
    /// Creador de Mazos integrado en la Mesa de Creación (InteractableWorkbench) del juego.
    /// Permite armar y guardar tu mazo de 60 cartas con clic izquierdo (añadir/quitar) y clic derecho (zoom con elevación).
    /// </summary>
    public class DeckBuilderUI : MonoBehaviour
    {
        public static DeckBuilderUI Instance { get; private set; }
        public bool IsOpen { get; private set; }

        bool workbenchScreenVisible;
        bool openedStandalone;
        readonly List<CardDef> currentDeck = new List<CardDef>();
        DeckDef deckDef = new DeckDef();
        FormatDef format = new FormatDef();
        List<string> deckNames = new List<string>();
        string deckNameEdit = "";
        int filterSupertype = -1; // -1 = Todos, 0 = Pokémon, 1 = Trainer, 2 = Energy
        string filterType = "";
        Vector2 catalogScroll, deckScroll;
        string statusMsg = "";

        // Animación de Zoom con clic derecho
        CardDef zoomCard;
        Rect zoomOriginRect;
        float zoomAnim; // 0..1
        bool zoomClosing;

        // Importador en vivo de archivos .txt estilo PTCGO / PTCG Live y Reporte de Faltantes
        bool showImportModal;
        List<string> importTxtFiles = new List<string>();
        PtcgoImportReport lastImportReport;
        Vector2 importFilesScroll;
        Vector2 importReportScroll;

        void Awake()
        {
            Instance = this;
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
                IsOpen = false;
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
            statusMsg = "Clic Izquierdo: añadir/quitar carta   |   Clic Derecho: elevar carta (Zoom)";
            IsOpen = true;
        }

        void CloseBuilder()
        {
            IsOpen = false;
            zoomCard = null;
            showImportModal = false;
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
            if (missing.Count > 0) statusMsg = "<color=#ffd166>Cartas desconocidas ignoradas: " + string.Join(", ", missing) + "</color>";
        }

        string UniqueName(string baseName)
        {
            var names = new HashSet<string>(DeckStore.List(), StringComparer.OrdinalIgnoreCase);
            string n = baseName; int i = 2;
            while (names.Contains(n)) n = $"{baseName} {i++}";
            return n;
        }

        void SaveCurrentPlayerDeck()
        {
            try
            {
                string newName = (deckNameEdit ?? "").Trim();
                if (newName.Length == 0) { statusMsg = "<color=#ff6b6b>Escribe un nombre para el mazo.</color>"; return; }
                if (!string.Equals(newName, deckDef.Name, StringComparison.Ordinal))
                {
                    if (DeckStore.List().Contains(newName, StringComparer.OrdinalIgnoreCase)) { statusMsg = "<color=#ff6b6b>Ya existe un mazo con ese nombre.</color>"; return; }
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
                    statusMsg = $"<color=#66ff99>Mazo '{deckDef.Name}' guardado y seleccionado para jugar.</color>";
                }
                else statusMsg = $"<color=#ffd166>Guardado (incompleto, no se puede jugar aún): {errors[0]}</color>";
            }
            catch (Exception e)
            {
                statusMsg = "<color=#ff6b6b>Error al guardar: " + e.Message + "</color>";
            }
        }

        void AddCardToDeck(CardDef c)
        {
            if (DeckValidator.RemainingAllowed(c, currentDeck, format) <= 0)
            {
                int owned = Collection.Owned(c);
                string why = currentDeck.Count >= format.DeckSize ? $"El mazo ya tiene {format.DeckSize} cartas."
                    : owned != int.MaxValue && currentDeck.Count(x => x.Id == c.Id) >= owned ? $"Solo tienes {owned} copia(s) de {c.Name} en tu colección."
                    : $"Máximo {format.MaxCopies} copias de {c.Name}.";
                statusMsg = $"<color=#ffd166>{why}</color>";
                return;
            }
            currentDeck.Add(c);
            statusMsg = $"Añadido: {c.Name} — Total: {currentDeck.Count}/{format.DeckSize}";
        }

        void RemoveCardFromDeck(CardDef c)
        {
            int idx = currentDeck.FindLastIndex(x => x.Id == c.Id);
            if (idx >= 0)
            {
                currentDeck.RemoveAt(idx);
                statusMsg = $"Quitado: {c.Name} — Total: {currentDeck.Count}/{format.DeckSize}";
            }
        }

        void Update()
        {
            if (!IsOpen) return;
            InputManager.SetCursorLockMode(CursorLockMode.None);
            Cursor.visible = true;

            if (zoomCard != null)
            {
                float target = zoomClosing ? 0f : 1f;
                zoomAnim = Mathf.MoveTowards(zoomAnim, target, Time.deltaTime * 5.5f);
                if (zoomClosing && zoomAnim <= 0.01f)
                {
                    zoomCard = null;
                    zoomClosing = false;
                }
            }

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                if (zoomCard != null) zoomClosing = true;
                else if (showImportModal) showImportModal = false;
                else CloseBuilder();
            }
        }

        void OnGUI()
        {
            GameUI.EnsureStyles();
            GUI.depth = -1100;

            // Si el jugador abrió la Mesa de Creación del juego (WorkbenchUIScreen), mostrar botón en la esquina superior derecha para no cubrir el texto de la workbench
            if (workbenchScreenVisible && !IsOpen)
            {
                Rect btnRect = new Rect(Screen.width - 350f - 24f, 20f, 350f, 44f);
                if (GUI.Button(btnRect, "⚡ Editar Mazo Pokémon TCG", GameUI.BtnGoldStyle))
                {
                    GameUI.PlayClickSound(1);
                    OpenCommon();
                }
                return;
            }

            if (!IsOpen) return;

            float w = Mathf.Min(Screen.width - 40f, 1680f);
            float h = Mathf.Min(Screen.height - 40f, 960f);
            Rect win = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
            GUI.Box(win, "", GameUI.BoxBgStyle);

            GUILayout.BeginArea(new Rect(win.x + 18, win.y + 14, win.width - 36, win.height - 28));

            // Cabecera
            GUILayout.BeginHorizontal();
            GUILayout.Label("<size=22><b>Mesa de Creación — Constructor de Mazo Pokémon TCG</b></size>", GameUI.HeaderStyle);
            GUILayout.FlexibleSpace();
            GUILayout.Label(statusMsg, GameUI.SubHeaderStyle);
            GUILayout.Space(16);
            if (GUILayout.Button("✔ Guardar Mazo", GameUI.BtnGreenStyle, GUILayout.Width(160), GUILayout.Height(36)))
            {
                GameUI.PlayClickSound(1);
                SaveCurrentPlayerDeck();
            }
            GUILayout.Space(8);
            if (GUILayout.Button("✖ Cerrar", GameUI.BtnRedStyle, GUILayout.Width(115), GUILayout.Height(36)))
            {
                GameUI.PlayClickSound(2);
                CloseBuilder();
                GUILayout.EndHorizontal();
                GUILayout.EndArea();
                return;
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(6);

            // Mazos y formato
            GUILayout.BeginHorizontal(GameUI.SubBoxStyle);
            GUILayout.Label("<b>Mazo:</b>", GameUI.SubHeaderStyle, GUILayout.Width(50));
            if (GUILayout.Button("◀", GameUI.BtnStyle, GUILayout.Width(32), GUILayout.Height(28))) { GameUI.PlayClickSound(0); CycleDeck(-1); }
            deckNameEdit = GUILayout.TextField(deckNameEdit ?? "", 40, GameUI.InputFieldStyle, GUILayout.Width(220), GUILayout.Height(28));
            if (GUILayout.Button("▶", GameUI.BtnStyle, GUILayout.Width(32), GUILayout.Height(28))) { GameUI.PlayClickSound(0); CycleDeck(1); }
            GUILayout.Label($"({deckNames.Count} guardados{(string.Equals(Plugin.ActiveDeck?.Value, deckDef.Name, StringComparison.OrdinalIgnoreCase) ? ", <color=#80ed99>activo</color>" : "")})", GameUI.SubHeaderStyle, GUILayout.Width(150));
            if (GUILayout.Button("Nuevo", GameUI.BtnStyle, GUILayout.Width(85), GUILayout.Height(28)))
            {
                GameUI.PlayClickSound(0);
                LoadDeck(null);
                statusMsg = "Nuevo mazo vacío. Pulsa 'Guardar Mazo' para conservarlo.";
            }
            if (GUILayout.Button("Duplicar", GameUI.BtnStyle, GUILayout.Width(95), GUILayout.Height(28)))
            {
                GameUI.PlayClickSound(0);
                deckDef = new DeckDef { Name = UniqueName(deckDef.Name + " copia"), Format = format.Id };
                deckNameEdit = deckDef.Name;
                statusMsg = "Copia creada. Pulsa 'Guardar Mazo' para conservarla.";
            }
            if (GUILayout.Button("Borrar", GameUI.BtnRedStyle, GUILayout.Width(80), GUILayout.Height(28)))
            {
                GameUI.PlayClickSound(2);
                DeckStore.Delete(deckDef.Name);
                deckNames = DeckStore.List();
                statusMsg = $"Mazo '{deckDef.Name}' borrado.";
                LoadDeck(deckNames.Count > 0 ? DeckStore.Load(deckNames[0]) : null);
            }
            GUILayout.Space(8);
            if (GUILayout.Button($"📥 Importar .txt ({importTxtFiles.Count})", GameUI.BtnGoldStyle, GUILayout.Width(165), GUILayout.Height(28)))
            {
                GameUI.PlayClickSound(1);
                importTxtFiles = PtcgoImporter.ListTxtFiles();
                if (lastImportReport == null)
                    lastImportReport = PtcgoImporter.AnalyzeDeck(deckDef.Name, currentDeck);
                showImportModal = !showImportModal;
            }
            if (GUILayout.Button("📂 Carpeta .txt", GameUI.BtnStyle, GUILayout.Width(130), GUILayout.Height(28)))
            {
                GameUI.PlayClickSound(0);
                PtcgoImporter.OpenImportFolder();
                statusMsg = "Carpeta de importación abierta en Windows. Pega tus .txt ahí y reabre/refresca.";
            }
            if (GUILayout.Button("📋 Ver Faltantes", GameUI.BtnStyle, GUILayout.Width(135), GUILayout.Height(28)))
            {
                GameUI.PlayClickSound(0);
                importTxtFiles = PtcgoImporter.ListTxtFiles();
                lastImportReport = PtcgoImporter.AnalyzeDeck(deckDef.Name, currentDeck);
                showImportModal = true;
            }
            GUILayout.Space(12);
            GUILayout.Label("<b>Formato:</b>", GameUI.SubHeaderStyle, GUILayout.Width(65));
            foreach (var f in FormatRegistry.Formats)
                if (FilterBtn(f.Name, f == format)) { format = f; statusMsg = $"Formato: {f.Name}. {f.Description}"; }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Space(6);

            // Filtros
            GUILayout.BeginHorizontal(GameUI.SubBoxStyle);
            GUILayout.Label("<b>Categoría:</b>", GameUI.SubHeaderStyle, GUILayout.Width(80));
            if (FilterBtn("Todas", filterSupertype == -1)) filterSupertype = -1;
            if (FilterBtn("Pokémon", filterSupertype == 0)) filterSupertype = 0;
            if (FilterBtn("Entrenadores", filterSupertype == 1)) filterSupertype = 1;
            if (FilterBtn("Energías", filterSupertype == 2)) filterSupertype = 2;

            GUILayout.Space(20);
            GUILayout.Label("<b>Elemento:</b>", GameUI.SubHeaderStyle, GUILayout.Width(75));
            if (FilterBtn("Todos", filterType == "")) filterType = "";
            foreach (var tp in new[] { "Fire", "Water", "Grass", "Lightning", "Psychic", "Fighting", "Colorless" })
                if (FilterBtn(tp, filterType == tp)) filterType = tp;

            GUILayout.FlexibleSpace();
            if (GUILayout.Button("🧹 Quitar no poseídas", GameUI.BtnStyle, GUILayout.Width(165), GUILayout.Height(28)))
            {
                GameUI.PlayClickSound(0);
                RemoveInvalidFromDeck();
            }
            if (GUILayout.Button("⚡ Autocompletar (Mis Cartas)", GameUI.BtnGoldStyle, GUILayout.Width(215), GUILayout.Height(28)))
            {
                GameUI.PlayClickSound(1);
                AutoFillFromCollection();
            }
            if (GUILayout.Button("Vaciar Mazo", GameUI.BtnRedStyle, GUILayout.Width(115), GUILayout.Height(28)))
            {
                GameUI.PlayClickSound(2);
                currentDeck.Clear();
                statusMsg = "Mazo vaciado.";
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(8);

            // Cuerpo principal: Catálogo a la izquierda + Lista del mazo a la derecha
            GUILayout.BeginHorizontal();

            float rightPanelW = 430f;
            float leftPanelW = win.width - 36f - rightPanelW - 16f;

            // --- Catálogo de Cartas ---
            GUILayout.BeginVertical(GameUI.SubBoxStyle, GUILayout.Width(leftPanelW), GUILayout.ExpandHeight(true));
            var filtered = CardDatabase.All.Where(c =>
                c.IsPlayable && format.Allows(c) && Collection.Owned(c) > 0 &&
                (filterSupertype == -1 || (int)c.Supertype == filterSupertype) &&
                (string.IsNullOrEmpty(filterType) || c.Type == filterType || (c.Supertype == Supertype.Energy && c.EnergyProvides == filterType))
            ).ToList();
            if (filtered.Count == 0)
                GUILayout.Label(Collection.Enforced
                    ? "<color=#ffd166>No tienes cartas de este formato/filtro en tu colección. Abre sobres de las expansiones de MyCurio para conseguirlas.</color>"
                    : "Sin cartas para este filtro.", GameUI.SubHeaderStyle);

            float cardW = 128f, cardH = 178f;
            int cols = Mathf.Max(1, (int)((leftPanelW - 40f) / (cardW + 12f)));

            catalogScroll = GUILayout.BeginScrollView(catalogScroll);
            for (int i = 0; i < filtered.Count; i += cols)
            {
                GUILayout.BeginHorizontal();
                for (int j = 0; j < cols && (i + j) < filtered.Count; j++)
                {
                    var card = filtered[i + j];
                    int countInDeck = currentDeck.Count(x => x.Id == card.Id);
                    int owned = Collection.Owned(card);
                    string ownedTxt = owned == int.MaxValue ? "∞" : owned.ToString();
                    GUILayout.BeginVertical(GUILayout.Width(cardW + 6));
                    Rect r = GUILayoutUtility.GetRect(cardW, cardH);
                    int click = GameUI.DrawCardWithInput(r, card, countInDeck > 0, zoomCard == null);
                    if (click == 0) AddCardToDeck(card);
                    else if (click == 1)
                    {
                        Vector2 screenPos = GUIUtility.GUIToScreenPoint(r.position);
                        zoomOriginRect = new Rect(screenPos.x, screenPos.y, r.width, r.height);
                        zoomCard = card;
                        zoomAnim = 0f;
                        zoomClosing = false;
                    }
                    GUILayout.Label($"<b>{card.Name}</b> <color=#9ecbff>{card.Id}</color>\nEn mazo: <color=#80ed99><b>{countInDeck}</b></color> / Tienes: <b>{ownedTxt}</b>", GameUI.CardLabelStyle, GUILayout.Width(cardW));
                    GUILayout.EndVertical();
                    GUILayout.Space(6);
                }
                GUILayout.EndHorizontal();
                GUILayout.Space(6);
            }
            GUILayout.EndScrollView();
            GUILayout.EndVertical();

            GUILayout.Space(12);

            // --- Lista del Mazo Actual ---
            GUILayout.BeginVertical(GameUI.SubBoxStyle, GUILayout.Width(rightPanelW), GUILayout.ExpandHeight(true));
            int nBasic = currentDeck.Count(c => c.IsBasicPokemon);
            int nEvo = currentDeck.Count(c => c.Supertype == Supertype.Pokemon && !c.IsBasicPokemon);
            int nTr = currentDeck.Count(c => c.Supertype == Supertype.Trainer);
            int nEn = currentDeck.Count(c => c.Supertype == Supertype.Energy);
            var errs = DeckValidator.Validate(currentDeck, format);
            string colorCount = errs.Count == 0 ? "#80ed99" : "#ffd166";
            GUILayout.Label($"<size=18><b>Tu Mazo: <color={colorCount}>{currentDeck.Count} / {format.DeckSize}</color> cartas</b></size>", GameUI.HeaderStyle);
            GUILayout.Label($"Básicos: <b>{nBasic}</b>  |  Evoluciones: <b>{nEvo}</b>  |  Entrenadores: <b>{nTr}</b>  |  Energías: <b>{nEn}</b>", GameUI.SubHeaderStyle);
            GUILayout.Label(errs.Count == 0 ? "<color=#80ed99>✔ Mazo válido para jugar</color>" : $"<color=#ffd166>⚠ {errs[0]}{(errs.Count > 1 ? $" (+{errs.Count - 1})" : "")}</color>", GameUI.SubHeaderStyle);
            GUILayout.Space(6);

            deckScroll = GUILayout.BeginScrollView(deckScroll, GUILayout.ExpandHeight(true));
            foreach (var grp in currentDeck.GroupBy(c => c.Id).OrderBy(g => g.First().Supertype).ThenBy(g => g.First().SetId).ThenBy(g => g.First().Number).ToList())
            {
                var c = grp.First();
                int qty = grp.Count();
                GUILayout.BeginHorizontal(GameUI.SubBoxStyle);
                GUILayout.Label($"<b>{qty}x</b>  {c.Name} <color=#9ecbff>{c.Id} ({(c.Supertype == Supertype.Pokemon ? c.Stage : c.Supertype.ToString())})</color>", GameUI.SubHeaderStyle);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("?", GameUI.BtnStyle, GUILayout.Width(30), GUILayout.Height(26)))
                {
                    GameUI.PlayClickSound(0);
                    zoomOriginRect = new Rect(Screen.width * 0.7f, Screen.height * 0.5f, 120f, 168f);
                    zoomCard = c;
                    zoomAnim = 0f;
                    zoomClosing = false;
                }
                if (GUILayout.Button("-", GameUI.BtnRedStyle, GUILayout.Width(30), GUILayout.Height(26)))
                {
                    GameUI.PlayClickSound(0);
                    RemoveCardFromDeck(c);
                }
                if (GUILayout.Button("+", GameUI.BtnGreenStyle, GUILayout.Width(30), GUILayout.Height(26)))
                {
                    GameUI.PlayClickSound(0);
                    AddCardToDeck(c);
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            GUILayout.EndVertical();

            GUILayout.EndHorizontal();
            GUILayout.EndArea();

            // Modal de Importación .txt estilo PTCGO / PTCG Live + Reporte de Cartas Faltantes y Expansiones
            if (showImportModal)
            {
                DrawImportAndMissingModal(win);
            }

            // Zoom elevado con animación
            if (zoomCard != null)
            {
                if (GameUI.DrawElevatedZoomOverlay(zoomCard, null, zoomOriginRect, zoomAnim))
                    zoomClosing = true;
            }
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
            GUILayout.Label("<size=20><b>📥 Importador de Mazos (.txt PTCGO / PTCG Live) y Buscador de Expansiones</b></size>", GameUI.HeaderStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("📂 Abrir Carpeta .txt", GameUI.BtnGoldStyle, GUILayout.Width(175), GUILayout.Height(34)))
            {
                GameUI.PlayClickSound(0);
                PtcgoImporter.OpenImportFolder();
            }
            GUILayout.Space(6);
            if (GUILayout.Button("🔄 Refrescar Carpeta", GameUI.BtnStyle, GUILayout.Width(165), GUILayout.Height(34)))
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
            if (GUILayout.Button("✖ Cerrar Ventana", GameUI.BtnRedStyle, GUILayout.Width(145), GUILayout.Height(34)))
            {
                GameUI.PlayClickSound(2);
                showImportModal = false;
                GUILayout.EndHorizontal();
                GUILayout.EndArea();
                return;
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            GUILayout.Label($"<color=#9ecbff>Carpeta en vivo (sin cerrar el juego):</color> <b>{PtcgoImporter.ImportDir}</b> — Pega tus archivos <b>.txt</b> ahí y al reabrir o pulsar Refrescar aparecerán aquí.", GameUI.SubHeaderStyle);
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
                GUILayout.Label("<color=#ffd166>No hay archivos .txt en la carpeta todavía.\n\nPulsa '📂 Abrir Carpeta .txt', pega tu archivo (ej. mega_lopunny-deck.txt) y pulsa '🔄 Refrescar Carpeta' o cierra y vuelve a abrir la Workbench.</color>", GameUI.SubHeaderStyle);
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
                GUILayout.Label("Selecciona un archivo <b>.txt</b> a la izquierda para importarlo y ver qué cartas te faltan y en qué sobres de MyCurio se consiguen.", GameUI.SubHeaderStyle);
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
                statusMsg = $"<color=#80ed99>Mazo '{deckDef.Name}' importado completo ({currentDeck.Count} cartas). Pulsa '✔ Guardar Mazo'.</color>";
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
                statusMsg = "<color=#80ed99>⚡ Mazo completado a 60 cartas con tu colección. Pulsa '✔ Guardar Mazo'.</color>";
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
            bool clicked = GUILayout.Button(text, active ? GameUI.BtnGoldStyle : GameUI.BtnStyle, GUILayout.Height(28));
            if (clicked) GameUI.PlayClickSound(0);
            return clicked;
        }
    }
}
