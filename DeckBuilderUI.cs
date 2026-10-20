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
        int filterSupertype = -1; // -1 = Todos, 0 = Pokémon, 1 = Trainer, 2 = Energy
        string filterType = "";
        Vector2 catalogScroll, deckScroll;
        string statusMsg = "";

        // Animación de Zoom con clic derecho
        CardDef zoomCard;
        Rect zoomOriginRect;
        float zoomAnim; // 0..1
        bool zoomClosing;

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
            SpriteCache.EnsureInitialized();
            SpriteCache.PreloadFromEpl();
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
            if (openedStandalone)
            {
                openedStandalone = false;
                var ipc = CSingleton<InteractionPlayerController>.Instance;
                if (ipc != null) ipc.ExitUIMode();
            }
        }

        void LoadCurrentPlayerDeck()
        {
            currentDeck.Clear();
            try
            {
                string p = Path.Combine(Plugin.DataDir, "decks", "player.txt");
                if (File.Exists(p))
                    currentDeck.AddRange(DeckLoader.Load(p));
            }
            catch (Exception e)
            {
                statusMsg = "Aviso al cargar mazo actual: " + e.Message;
            }
        }

        void SaveCurrentPlayerDeck()
        {
            if (currentDeck.Count != 60)
            {
                statusMsg = $"<color=#ff6b6b>El mazo tiene {currentDeck.Count}/60 cartas (debe tener exactamente 60).</color>";
                return;
            }
            if (!currentDeck.Any(c => c.IsBasicPokemon))
            {
                statusMsg = "<color=#ff6b6b>El mazo debe incluir al menos 1 Pokémon Básico.</color>";
                return;
            }
            try
            {
                string p = Path.Combine(Plugin.DataDir, "decks", "player.txt");
                DeckLoader.Save(p, currentDeck);
                statusMsg = "<color=#66ff99>¡Mazo de 60 cartas guardado correctamente en player.txt!</color>";
            }
            catch (Exception e)
            {
                statusMsg = "<color=#ff6b6b>Error al guardar: " + e.Message + "</color>";
            }
        }

        void AddCardToDeck(CardDef c)
        {
            if (currentDeck.Count >= 60)
            {
                statusMsg = "<color=#ffd166>Ya tienes 60 cartas en el mazo. Quita alguna antes de añadir más.</color>";
                return;
            }
            int copies = currentDeck.Count(x => x.Number == c.Number);
            if (!c.IsBasicEnergy && copies >= 4)
            {
                statusMsg = $"<color=#ffd166>Máximo 4 copias permitidas de {c.Name}.</color>";
                return;
            }
            currentDeck.Add(c);
            statusMsg = $"Añadido: {c.Name} ({copies + 1} en el mazo) — Total: {currentDeck.Count}/60";
        }

        void RemoveCardFromDeck(CardDef c)
        {
            int idx = currentDeck.FindLastIndex(x => x.Number == c.Number);
            if (idx >= 0)
            {
                currentDeck.RemoveAt(idx);
                statusMsg = $"Quitado: {c.Name} — Total: {currentDeck.Count}/60";
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
                else CloseBuilder();
            }
        }

        void OnGUI()
        {
            GameUI.EnsureStyles();
            GUI.depth = -1100;

            // Si el jugador abrió la Mesa de Creación del juego (WorkbenchUIScreen), mostrar botón superior para abrir el creador de mazos Pokémon
            if (workbenchScreenVisible && !IsOpen)
            {
                Rect btnRect = new Rect((Screen.width - 360f) * 0.5f, 18f, 360f, 44f);
                if (GUI.Button(btnRect, "⚡ Armar Mazo Pokémon TCG (60 cartas)", GameUI.BtnStyle))
                {
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
            if (GUILayout.Button("Guardar Mazo (60)", GameUI.BtnStyle, GUILayout.Width(170), GUILayout.Height(34)))
                SaveCurrentPlayerDeck();
            GUILayout.Space(8);
            if (GUILayout.Button("Cerrar", GameUI.BtnStyle, GUILayout.Width(110), GUILayout.Height(34)))
            {
                CloseBuilder();
                GUILayout.EndHorizontal();
                GUILayout.EndArea();
                return;
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(8);

            // Filtros
            GUILayout.BeginHorizontal();
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
            if (GUILayout.Button("Autogenerar Mazo Aleatorio", GameUI.BtnStyle, GUILayout.Width(220), GUILayout.Height(28)))
            {
                currentDeck.Clear();
                currentDeck.AddRange(DeckLoader.GenerateRandomNpcDeck());
                statusMsg = "<color=#9ecbff>Se ha generado un mazo equilibrado de 60 cartas. Pulsa 'Guardar Mazo' para conservarlo.</color>";
            }
            if (GUILayout.Button("Vaciar Mazo", GameUI.BtnStyle, GUILayout.Width(120), GUILayout.Height(28)))
            {
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
            GUILayout.BeginVertical(GUI.skin.box, GUILayout.Width(leftPanelW), GUILayout.ExpandHeight(true));
            var filtered = CardDatabase.ByNumber.Values.Where(c =>
                (filterSupertype == -1 || (int)c.Supertype == filterSupertype) &&
                (string.IsNullOrEmpty(filterType) || c.Type == filterType || (c.Supertype == Supertype.Energy && c.EnergyProvides == filterType))
            ).OrderBy(c => c.Number).ToList();

            float cardW = 128f, cardH = 178f;
            int cols = Mathf.Max(1, (int)((leftPanelW - 40f) / (cardW + 12f)));

            catalogScroll = GUILayout.BeginScrollView(catalogScroll);
            for (int i = 0; i < filtered.Count; i += cols)
            {
                GUILayout.BeginHorizontal();
                for (int j = 0; j < cols && (i + j) < filtered.Count; j++)
                {
                    var card = filtered[i + j];
                    int countInDeck = currentDeck.Count(x => x.Number == card.Number);
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
                    GUILayout.Label($"<b>#{card.Number} {card.Name}</b>\nEn mazo: <color=#80ed99><b>{countInDeck}</b></color>", GameUI.CardLabelStyle, GUILayout.Width(cardW));
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
            GUILayout.BeginVertical(GUI.skin.box, GUILayout.Width(rightPanelW), GUILayout.ExpandHeight(true));
            int nBasic = currentDeck.Count(c => c.IsBasicPokemon);
            int nEvo = currentDeck.Count(c => c.Supertype == Supertype.Pokemon && !c.IsBasicPokemon);
            int nTr = currentDeck.Count(c => c.Supertype == Supertype.Trainer);
            int nEn = currentDeck.Count(c => c.Supertype == Supertype.Energy);
            string colorCount = currentDeck.Count == 60 && nBasic > 0 ? "#80ed99" : "#ffd166";
            GUILayout.Label($"<size=18><b>Tu Mazo: <color={colorCount}>{currentDeck.Count} / 60</color> cartas</b></size>", GameUI.HeaderStyle);
            GUILayout.Label($"Básicos: <b>{nBasic}</b>  |  Evoluciones: <b>{nEvo}</b>  |  Entrenadores: <b>{nTr}</b>  |  Energías: <b>{nEn}</b>", GameUI.SubHeaderStyle);
            GUILayout.Space(6);

            deckScroll = GUILayout.BeginScrollView(deckScroll, GUILayout.ExpandHeight(true));
            foreach (var grp in currentDeck.GroupBy(c => c.Number).OrderBy(g => g.First().Supertype).ThenBy(g => g.Key).ToList())
            {
                var c = grp.First();
                int qty = grp.Count();
                GUILayout.BeginHorizontal(GUI.skin.box);
                GUILayout.Label($"<b>{qty}x</b>  #{c.Number:000} {c.Name} <color=#9ecbff>({(c.Supertype == Supertype.Pokemon ? c.Stage : c.Supertype.ToString())})</color>", GameUI.SubHeaderStyle);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("?", GameUI.BtnStyle, GUILayout.Width(28), GUILayout.Height(24)))
                {
                    zoomOriginRect = new Rect(Screen.width * 0.7f, Screen.height * 0.5f, 120f, 168f);
                    zoomCard = c;
                    zoomAnim = 0f;
                    zoomClosing = false;
                }
                if (GUILayout.Button("-", GameUI.BtnStyle, GUILayout.Width(28), GUILayout.Height(24)))
                    RemoveCardFromDeck(c);
                if (GUILayout.Button("+", GameUI.BtnStyle, GUILayout.Width(28), GUILayout.Height(24)))
                    AddCardToDeck(c);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            GUILayout.EndVertical();

            GUILayout.EndHorizontal();
            GUILayout.EndArea();

            // Zoom elevado con animación
            if (zoomCard != null)
            {
                if (GameUI.DrawElevatedZoomOverlay(zoomCard, null, zoomOriginRect, zoomAnim))
                    zoomClosing = true;
            }
        }

        bool FilterBtn(string text, bool active)
        {
            Color prev = GUI.backgroundColor;
            if (active) GUI.backgroundColor = new Color(0.3f, 0.85f, 0.55f);
            bool clicked = GUILayout.Button(text, GameUI.BtnStyle, GUILayout.Height(26));
            GUI.backgroundColor = prev;
            return clicked;
        }
    }
}
