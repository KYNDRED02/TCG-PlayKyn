// TCG-PlayKyn: Deck builder GUI with collection filtering, format checks and live search
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;

namespace TCGPlayKyn
{
    /// <summary>
    /// F9 / F9.5 (Plan Menor): PokeDeck Builder 3D independiente en la Mesa de Creación (InteractableWorkbench).
    /// - Separado del creador de mazos del juego base con su propio botón 'PokeDeck Builder'.
    /// - Renderizado 100% con la tipografía nativa del juego (FredokaOne-Regular SDF), autoajuste de texto y cero emojis.
    /// - Barra lateral izquierda con vista dedicada de 'Mazos' (filtro multitipo con iconos, expansión, búsqueda en vivo, renombrar, borrar, importar).
    /// - Vista 'Cartas' con contador grande xN en la esquina superior derecha, cartas no obtenidas en gris, hover con inclinación suave sin zoom ni marco trasero,
    ///   zoom con clic derecho y panel lateral de filtros avanzados (Vida PS, Color/Tipo con iconos, Tipo de carta, Etapa y Expansión).
    /// </summary>
    public class DeckBuilderUI : MonoBehaviour
    {
        public static DeckBuilderUI Instance { get; private set; }
        public bool IsOpen { get; private set; }

        WorkbenchUIScreen activeWorkbenchScreen;
        bool workbenchScreenVisible;
        bool baseWorkbenchSubscreenOpen;
        bool openedStandalone;

        public bool NeedsNativeOverlay =>
            IsOpen ||
            (workbenchScreenVisible && !baseWorkbenchSubscreenOpen && (activeWorkbenchScreen == null || activeWorkbenchScreen.m_ScreenGrp == null || activeWorkbenchScreen.m_ScreenGrp.activeInHierarchy));

        readonly List<CardDef> currentDeck = new List<CardDef>();
        readonly List<CardDef> pageCardsBuffer = new List<CardDef>(DeckBuilderStage3D.PageSize);
        readonly DeckBuilderStage3D stage3D = new DeckBuilderStage3D();

        // Cachés ligeras para evitar escanear miles de cartas o archivos .deck.json en cada frame de OnGUI
        readonly List<CardDef> filteredCardsCache = new List<CardDef>();
        readonly List<DeckSummaryInfo> deckSummariesCache = new List<DeckSummaryInfo>();
        readonly Dictionary<string, int> cachedDeckIdCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        List<string> cachedDeckErrors = new List<string>();
        int cachedDeckBasic, cachedDeckEvo, cachedDeckTr, cachedDeckEn;
        bool deckStatsDirty = true;
        bool deckSummariesDirty = true;
        bool catalogFilterDirty = true;
        string lastCatalogFilterKey = "";
        float lastOwnershipRefreshTime = -999f;

        DeckDef deckDef = new DeckDef();
        FormatDef format = new FormatDef();
        List<string> deckNames = new List<string>();
        string deckNameEdit = "";

        // Main navigation: false = Cards view, true = Decks view
        bool inDecksView;
        bool showAdvancedFilters;
        bool showExpansionDropdown;
        int expansionDropdownPage;
        int hoveredExpansionIdx = -1;
        float hoveredExpansionTimer;

        // Cards view filters
        // ownershipMode: 0 = All Cards (color + grayscale), 1 = Owned, 2 = Unowned
        int ownershipMode;
        // cardCategoryFilter: 0 = All, 1 = Pokémon, 2 = Trainer, 3 = Energy, 4 = Tool, 5 = Stadium
        int cardCategoryFilter;
        // filterStage: "" = All, "Basic" = Basic, "Stage 1" = Stage 1, "Stage 2" = Stage 2
        string filterStage = "";
        // filterHpBucket: 0 = All, 1 = 10-50, 2 = 60-120, 3 = 130-200, 4 = 200+
        int filterHpBucket;
        string filterSetId = "";
        readonly HashSet<string> cardFilterTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string searchQuery = "";
        int currentPage;
        int lastTotalPages = 1;
        int deckListPage;

        // Decks view filters & state
        string deckSearchQuery = "";
        string deckFilterSetId = "";
        readonly HashSet<string> deckFilterTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int decksViewPage;
        string renamingDeckName = "";
        string renameBuffer = "";

        string statusMsg = "";
        Rect lastCatalogGuiRect;
        Rect lastDeckPedestalGuiRect;

        // Right-click 3D card inspection
        CardDef zoomCard;
        Rect zoomOriginRect;
        Vector3 zoomOriginWorldPos;
        Quaternion zoomOriginWorldRot = Quaternion.identity;
        float zoomOriginScale = 1f;
        float zoomAnim;
        bool zoomClosing;

        // Selector contextual de variantes (No Foil / Foil)
        CardDef variantPopupCard;
        int variantPopupSlotIndex;
        Rect variantPopupRect;

        // Native modals (Import .txt / Official Decks / Settings)
        bool showImportModal;
        List<string> importTxtFiles = new List<string>();
        PtcgoImportReport lastImportReport;
        int importFilesPage;
        int importReportPage;

        bool showThemeDecksModal;
        bool showSettingsModal;
        string themeFilterSetId = "";
        int themeDecksPage;

        class DeckSummaryInfo
        {
            public DeckDef Def;
            public List<CardDef> Cards;
            public List<string> Types;
            public HashSet<string> Sets;
            public CardDef CoverCard;
        }

        void Awake()
        {
            Instance = this;
        }

        void OnDestroy()
        {
            stage3D.Destroy();
            if (Instance == this) Instance = null;
        }

        public void OnWorkbenchScreenOpened(WorkbenchUIScreen screen = null)
        {
            activeWorkbenchScreen = screen;
            workbenchScreenVisible = true;
            baseWorkbenchSubscreenOpen = false;
            SpriteCache.EnsureInitialized();
            SpriteCache.PreloadFromEpl();
        }

        public void SetBaseWorkbenchSubscreenOpen(bool subOpen)
        {
            baseWorkbenchSubscreenOpen = subOpen;
        }

        public void OnWorkbenchScreenClosed()
        {
            workbenchScreenVisible = false;
            baseWorkbenchSubscreenOpen = false;
            activeWorkbenchScreen = null;
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
            activeWorkbenchScreen = screen;
            if (activeWorkbenchScreen != null && activeWorkbenchScreen.m_ScreenGrp != null)
                activeWorkbenchScreen.m_ScreenGrp.SetActive(false);
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
            inDecksView = false;
            showAdvancedFilters = false;
            showExpansionDropdown = false;
            hoveredExpansionIdx = -1;
            hoveredExpansionTimer = 0f;
            renamingDeckName = "";
            currentPage = 0;
            deckListPage = 0;
            decksViewPage = 0;
            stage3D.SetActive(true);
            deckSummariesDirty = true;
            catalogFilterDirty = true;
            MarkDeckDirty();
            string missingArt = CardDatabase.GetMissingArtNotice();
            statusMsg = !string.IsNullOrEmpty(missingArt)
                ? $"<color=#ffd166>{missingArt}</color>"
                : "Left click: add to deck  |  Right click: zoom & info  |  Wheel: change page";
            IsOpen = true;
        }

        void MarkDeckDirty()
        {
            deckStatsDirty = true;
        }

        void InvalidateDeckSummaries()
        {
            deckSummariesDirty = true;
        }

        void RefreshDeckStatsIfNeeded()
        {
            if (!deckStatsDirty) return;
            deckStatsDirty = false;
            cachedDeckIdCounts.Clear();
            cachedDeckBasic = 0;
            cachedDeckEvo = 0;
            cachedDeckTr = 0;
            cachedDeckEn = 0;

            for (int i = 0; i < currentDeck.Count; i++)
            {
                var c = currentDeck[i];
                if (c == null) continue;
                cachedDeckIdCounts.TryGetValue(c.Id, out int curCount);
                cachedDeckIdCounts[c.Id] = curCount + 1;

                if (c.Supertype == Supertype.Pokemon)
                {
                    if (c.IsBasicPokemon) cachedDeckBasic++;
                    else cachedDeckEvo++;
                }
                else if (c.Supertype == Supertype.Trainer)
                {
                    cachedDeckTr++;
                }
                else if (c.Supertype == Supertype.Energy)
                {
                    cachedDeckEn++;
                }
            }
            cachedDeckErrors = DeckValidator.Validate(currentDeck, format);
        }

        int GetInDeckCount(string cardId)
        {
            RefreshDeckStatsIfNeeded();
            return !string.IsNullOrEmpty(cardId) && cachedDeckIdCounts.TryGetValue(cardId, out int count) ? count : 0;
        }

        List<CardDef> GetFilteredCatalog()
        {
            string typeSig = "";
            if (cardFilterTypes.Count > 0)
            {
                var sb = new System.Text.StringBuilder(32);
                for (int i = 0; i < SpriteCache.AllTypeIconNames.Length; i++)
                {
                    if (cardFilterTypes.Contains(SpriteCache.AllTypeIconNames[i]))
                        sb.Append(SpriteCache.AllTypeIconNames[i]).Append('|');
                }
                typeSig = sb.ToString();
            }

            string filterKey = $"{format?.Id}|{filterSetId}|{ownershipMode}|{cardCategoryFilter}|{filterStage}|{filterHpBucket}|{typeSig}|{searchQuery}";
            bool periodicOwnershipCheck = ownershipMode != 0 && (Time.realtimeSinceStartup - lastOwnershipRefreshTime > 1.5f);
            if (!catalogFilterDirty && !periodicOwnershipCheck && string.Equals(lastCatalogFilterKey, filterKey, StringComparison.Ordinal))
                return filteredCardsCache;

            lastCatalogFilterKey = filterKey;
            lastOwnershipRefreshTime = Time.realtimeSinceStartup;
            catalogFilterDirty = false;
            filteredCardsCache.Clear();

            var allCards = CardDatabase.All;
            bool hasSetFilter = !string.IsNullOrEmpty(filterSetId);
            for (int i = 0; i < allCards.Count; i++)
            {
                var c = allCards[i];
                if (hasSetFilter ? !string.Equals(c.SetId, filterSetId, StringComparison.OrdinalIgnoreCase) : (!format.Allows(c) && c.IsPlayable))
                    continue;
                if (!CardMatchesOwnership(c) || !CardMatchesCategory(c) || !CardMatchesStage(c) ||
                    !CardMatchesHpBucket(c) || !CardMatchesTypeFilter(c) || !CardMatchesSearch(c, searchQuery))
                    continue;
                filteredCardsCache.Add(c);
            }
            return filteredCardsCache;
        }

        void CloseBuilder()
        {
            IsOpen = false;
            zoomCard = null;
            zoomClosing = false;
            showImportModal = false;
            showThemeDecksModal = false;
            showSettingsModal = false;
            showAdvancedFilters = false;
            showExpansionDropdown = false;
            hoveredExpansionIdx = -1;
            hoveredExpansionTimer = 0f;
            inDecksView = false;
            renamingDeckName = "";
            stage3D.SetActive(false);

            if (activeWorkbenchScreen != null && workbenchScreenVisible && activeWorkbenchScreen.m_ScreenGrp != null)
            {
                activeWorkbenchScreen.m_ScreenGrp.SetActive(true);
            }

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
            deckDef = d ?? new DeckDef { Name = UniqueName("My Deck"), Format = FormatRegistry.Default.Id };
            deckNameEdit = deckDef.Name;
            format = FormatRegistry.Get(deckDef.Format) ?? FormatRegistry.Default;
            var missing = new List<string>();
            currentDeck.AddRange(deckDef.Resolve(missing));
            deckListPage = 0;
            MarkDeckDirty();
            catalogFilterDirty = true;
            if (missing.Count > 0)
                statusMsg = "<color=#ffd166>Unknown cards ignored: " + string.Join(", ", missing) + "</color>";
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
                    statusMsg = "<color=#ff6b6b>Please enter a deck name.</color>";
                    return;
                }
                if (!string.Equals(newName, deckDef.Name, StringComparison.Ordinal))
                {
                    if (DeckStore.List().Any(n => string.Equals(n, newName, StringComparison.OrdinalIgnoreCase) && !string.Equals(n, deckDef.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        statusMsg = "<color=#ff6b6b>A deck with that name already exists.</color>";
                        return;
                    }
                    DeckStore.Delete(deckDef.Name);
                    deckDef.Name = newName;
                }
                deckDef.Format = format.Id;
                deckDef.SetCards(currentDeck);
                DeckStore.Save(deckDef);
                InvalidateDeckSummaries();
                deckNames = DeckStore.List();
                MarkDeckDirty();
                RefreshDeckStatsIfNeeded();
                if (cachedDeckErrors.Count == 0)
                {
                    DeckStore.SetActive(deckDef.Name);
                    statusMsg = $"<color=#66ff99>Deck '{deckDef.Name}' saved and set as active.</color>";
                }
                else
                {
                    statusMsg = $"<color=#ffd166>Deck '{deckDef.Name}' saved (incomplete): {cachedDeckErrors[0]}</color>";
                }
            }
            catch (Exception e)
            {
                statusMsg = "<color=#ff6b6b>Save error: " + e.Message + "</color>";
            }
        }

        void RenameSavedDeck(string oldName, string requestedNewName)
        {
            string trimmed = (requestedNewName ?? "").Trim();
            if (string.IsNullOrEmpty(trimmed) || string.Equals(oldName, trimmed, StringComparison.Ordinal))
            {
                renamingDeckName = "";
                return;
            }
            if (DeckStore.List().Any(n => string.Equals(n, trimmed, StringComparison.OrdinalIgnoreCase) && !string.Equals(n, oldName, StringComparison.OrdinalIgnoreCase)))
            {
                statusMsg = "<color=#ff6b6b>Another deck with that name already exists.</color>";
                return;
            }
            var loaded = DeckStore.Load(oldName);
            if (loaded != null)
            {
                bool wasActive = string.Equals(Plugin.ActiveDeck?.Value, oldName, StringComparison.OrdinalIgnoreCase);
                bool wasEditing = string.Equals(deckDef.Name, oldName, StringComparison.OrdinalIgnoreCase);
                DeckStore.Delete(oldName);
                loaded.Name = trimmed;
                DeckStore.Save(loaded);
                InvalidateDeckSummaries();
                if (wasActive) DeckStore.SetActive(trimmed);
                if (wasEditing)
                {
                    deckDef.Name = trimmed;
                    deckNameEdit = trimmed;
                }
                deckNames = DeckStore.List();
                statusMsg = $"<color=#80ed99>Deck renamed to '{trimmed}'.</color>";
            }
            renamingDeckName = "";
        }

        bool AddCardToDeck(CardDef c, int sourceSlotIndex = -1, bool isFoil = false)
        {
            if (c == null) return false;
            if (!c.IsPlayable)
            {
                statusMsg = "<color=#ff6b6b>Not implemented expansion mechanics yet</color>";
                TcgAudio.PlayCardPlace();
                return false;
            }
            if (!format.Allows(c))
            {
                var unlim = FormatRegistry.Get("unlimited") ?? FormatRegistry.Default;
                if (unlim != null && unlim.Allows(c))
                    format = unlim;
            }
            if (DeckValidator.RemainingAllowed(c, currentDeck, format) <= 0)
            {
                int owned = Collection.Owned(c);
                string why = currentDeck.Count >= format.DeckSize
                    ? $"The deck already has {format.DeckSize} cards."
                    : owned <= 0
                        ? $"You do not own '{c.Name}' in your collection ({c.SetId.ToUpperInvariant()})."
                        : owned != int.MaxValue && currentDeck.Count(x => x.Id == c.Id) >= owned
                            ? $"You only own {owned} copy(ies) of {c.Name} in your collection."
                            : $"Maximum {format.MaxCopies} copies of {c.Name}.";
                statusMsg = $"<color=#ffd166>{why}</color>";
                return false;
            }

            // Comprobar disponibilidad de la variante específica (Foil o No Foil) en la colección
            if (Collection.Enforced && (!Collection.FreeBasicEnergy || !c.IsBasicEnergy))
            {
                int ownedVariant = isFoil ? Collection.OwnedFoil(c) : Collection.OwnedNonFoil(c);
                int inDeckVariant = currentDeck.Count(x => x.Id == c.Id && x.IsFoil == isFoil);
                if (inDeckVariant >= ownedVariant)
                {
                    string vName = isFoil ? "Foil" : "No Foil";
                    statusMsg = $"<color=#ffd166>You already have all your {vName} copies of {c.Name} in the deck ({inDeckVariant}/{ownedVariant}).</color>";
                    return false;
                }
            }

            var cardToAdd = c.Clone(isFoil);
            currentDeck.Add(cardToAdd);
            MarkDeckDirty();
            stage3D.SpawnAddFlight(sourceSlotIndex, cardToAdd);
            string foilTag = isFoil ? " <color=#ffe066>[Foil]</color>" : "";
            statusMsg = $"<color=#80ed99>+ Added:</color> {c.Name}{foilTag} ({currentDeck.Count}/{format.DeckSize})";
            return true;
        }

        bool RemoveCardFromDeck(CardDef c, bool? preferFoil = null)
        {
            if (c == null) return false;
            int idx = -1;
            if (preferFoil.HasValue)
                idx = currentDeck.FindLastIndex(x => x.Id == c.Id && x.IsFoil == preferFoil.Value);
            if (idx < 0)
                idx = currentDeck.FindLastIndex(x => x.Id == c.Id);
            if (idx >= 0)
            {
                var removed = currentDeck[idx];
                currentDeck.RemoveAt(idx);
                MarkDeckDirty();
                stage3D.SpawnRemoveFlight(removed);
                string foilTag = removed.IsFoil ? " [Foil]" : "";
                statusMsg = $"<color=#ffd166>- Removed:</color> {c.Name}{foilTag} ({currentDeck.Count}/{format.DeckSize})";
                return true;
            }
            return false;
        }

        void OpenInspect3D(CardDef card, Rect approxRect, Vector3 originWorldPos, Quaternion originWorldRot, float originScale)
        {
            if (card == null) return;
            stage3D.ResetInspectFlip();
            bool startFoil = card.IsFoil || (Collection.HasFoil(card) && !Collection.HasNonFoil(card));
            stage3D.SetInspectFoil(startFoil);
            zoomCard = card;
            zoomOriginRect = approxRect;
            zoomOriginWorldPos = originWorldPos;
            zoomOriginWorldRot = originWorldRot;
            zoomOriginScale = originScale;
            zoomAnim = 0f;
            zoomClosing = false;
            variantPopupCard = null;
        }

        void ChangePage(int delta)
        {
            variantPopupCard = null;
            int next = Mathf.Clamp(currentPage + delta, 0, Mathf.Max(0, lastTotalPages - 1));
            if (next != currentPage)
            {
                int dir = next > currentPage ? 1 : -1;
                currentPage = next;
                stage3D.TriggerCarouselSlide(dir);
                GameUI.PlayClickSound(0);
            }
        }

        static bool IsToolCard(CardDef c)
        {
            if (c == null || c.Supertype != Supertype.Trainer) return false;
            if (c.Subtypes != null && c.Subtypes.Any(s => ContainsIgnoreCase(s, "Tool") || ContainsIgnoreCase(s, "Herramienta")))
                return true;
            if (string.Equals(c.Name, "PlusPower", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Name, "Defender", StringComparison.OrdinalIgnoreCase))
                return true;
            return ContainsIgnoreCase(c.RulesText, "Pokémon Tool") ||
                   (ContainsIgnoreCase(c.RulesText, "Attach ") && ContainsIgnoreCase(c.RulesText, "to 1 of your"));
        }

        static bool IsStadiumCard(CardDef c)
        {
            if (c == null || c.Supertype != Supertype.Trainer) return false;
            if (c.Subtypes != null && c.Subtypes.Any(s => ContainsIgnoreCase(s, "Stadium") || ContainsIgnoreCase(s, "Estadio")))
                return true;
            return ContainsIgnoreCase(c.RulesText, "This card stays in play") ||
                   ContainsIgnoreCase(c.Name, "Gym") ||
                   ContainsIgnoreCase(c.Name, "Stadium");
        }

        bool CardMatchesCategory(CardDef c)
        {
            switch (cardCategoryFilter)
            {
                case 1: return c.Supertype == Supertype.Pokemon;
                case 2: return c.Supertype == Supertype.Trainer;
                case 3: return c.Supertype == Supertype.Energy;
                case 4: return IsToolCard(c);
                case 5: return IsStadiumCard(c);
                default: return true;
            }
        }

        bool CardMatchesStage(CardDef c)
        {
            if (string.IsNullOrEmpty(filterStage)) return true;
            if (c.Supertype != Supertype.Pokemon) return false;
            return string.Equals(c.Stage, filterStage, StringComparison.OrdinalIgnoreCase);
        }

        bool CardMatchesHpBucket(CardDef c)
        {
            if (filterHpBucket <= 0) return true;
            if (c.Supertype != Supertype.Pokemon) return false;
            switch (filterHpBucket)
            {
                case 1: return c.Hp >= 10 && c.Hp <= 50;
                case 2: return c.Hp >= 60 && c.Hp <= 120;
                case 3: return c.Hp >= 130 && c.Hp <= 200;
                case 4: return c.Hp >= 200;
                default: return true;
            }
        }

        bool CardMatchesTypeFilter(CardDef c)
        {
            if (cardFilterTypes.Count == 0) return true;
            if (c.Supertype == Supertype.Pokemon && !string.IsNullOrEmpty(c.Type))
                return cardFilterTypes.Contains(c.Type);
            if (c.Supertype == Supertype.Energy && !string.IsNullOrEmpty(c.EnergyProvides))
                return cardFilterTypes.Contains(c.EnergyProvides);
            return false;
        }

        bool CardMatchesOwnership(CardDef c)
        {
            if (ownershipMode == 1) return Collection.HasCard(c);
            if (ownershipMode == 2) return !Collection.HasCard(c);
            return true; // 0 = Todas (obtenidas a color + no obtenidas en gris)
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

            if (activeWorkbenchScreen != null && activeWorkbenchScreen.m_ScreenGrp != null && activeWorkbenchScreen.m_ScreenGrp.activeSelf)
                activeWorkbenchScreen.m_ScreenGrp.SetActive(false);

            float dt = Time.unscaledDeltaTime > 0f ? Time.unscaledDeltaTime : Time.deltaTime;
            if (showExpansionDropdown && hoveredExpansionIdx >= 0)
                hoveredExpansionTimer += dt;
            else
                hoveredExpansionTimer = 0f;

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
            else if (!showImportModal && !showThemeDecksModal && !showSettingsModal && !inDecksView && !showExpansionDropdown && GUIUtility.keyboardControl == 0)
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
                else if (showExpansionDropdown) { showExpansionDropdown = false; hoveredExpansionIdx = -1; hoveredExpansionTimer = 0f; }
                else if (!string.IsNullOrEmpty(renamingDeckName)) renamingDeckName = "";
                else if (inDecksView) inDecksView = false;
                else if (showAdvancedFilters) showAdvancedFilters = false;
                else CloseBuilder();
            }
        }

        /// <summary>
        /// Main rendering over the native uGUI Canvas (PokeTCG_NativeCanvasOverlay) invoked from GameUI.OnGUI.
        /// Guarantees all texts use the official game font (FredokaOne-Regular SDF) with auto-sizing.
        /// </summary>
        public void DrawBuilderNativeGUI()
        {
            var ui = GameUI.Instance;
            if (ui == null) return;

            // M1: Standalone 'PokeDeck Builder' button in the workbench menu without interfering with the base game
            if (!IsOpen)
            {
                if (workbenchScreenVisible && !baseWorkbenchSubscreenOpen && (activeWorkbenchScreen == null || activeWorkbenchScreen.m_ScreenGrp == null || activeWorkbenchScreen.m_ScreenGrp.activeInHierarchy))
                {
                    Rect wbBtnRect = new Rect((Screen.width - 280f) * 0.5f, 28f, 280f, 54f);
                    if (ui.DrawNativeButton(wbBtnRect, "PokeDeck Builder", 22f, GameUI.sBtnGoldN, GameUI.sBtnGoldH, GameUI.sBtnGoldA))
                    {
                        GameUI.PlayClickSound(1);
                        OpenFromWorkbenchScreen(activeWorkbenchScreen);
                    }
                }
                return;
            }

            // General builder layout geometry
            float sidebarW = 186f;
            float rightPanelW = Mathf.Clamp(Screen.width * 0.235f, 336f, 405f);
            float topBarH = inDecksView ? 104f : 56f;
            float contentX = sidebarW + 26f;
            float centerW = Screen.width - contentX - rightPanelW - 24f;
            float bodyY = 14f + topBarH + 8f;
            float bodyH = Screen.height - bodyY - 14f;

            lastCatalogGuiRect = new Rect(contentX, bodyY + 6f, centerW, bodyH - 48f);
            Rect rightPanelRect = new Rect(contentX + centerW + 10f, bodyY, rightPanelW, bodyH);
            lastDeckPedestalGuiRect = new Rect(rightPanelRect.x + 10f, rightPanelRect.y + 10f, 122f, 152f);

            var filtered = GetFilteredCatalog();

            lastTotalPages = Mathf.Max(1, Mathf.CeilToInt(filtered.Count / (float)DeckBuilderStage3D.PageSize));
            currentPage = Mathf.Clamp(currentPage, 0, lastTotalPages - 1);

            pageCardsBuffer.Clear();
            int startIdx = currentPage * DeckBuilderStage3D.PageSize;
            for (int i = startIdx; i < filtered.Count && pageCardsBuffer.Count < DeckBuilderStage3D.PageSize; i++)
                pageCardsBuffer.Add(filtered[i]);

            bool modalOpen = showImportModal || showThemeDecksModal || showSettingsModal;
            bool showCatalog3D = !inDecksView;
            // Requirement 3: Remove 3D deck stack viewer from the right panel
            bool showDeckStack3D = false;

            if (Event.current.type == EventType.Repaint)
            {
                stage3D.UpdateAndRender(
                    pageCardsBuffer,
                    currentDeck,
                    format,
                    lastCatalogGuiRect,
                    lastDeckPedestalGuiRect,
                    modalOpen || inDecksView || showExpansionDropdown,
                    zoomCard,
                    zoomAnim,
                    zoomOriginWorldPos,
                    zoomOriginWorldRot,
                    zoomOriginScale,
                    showCatalog3D,
                    showDeckStack3D);
            }

            // 3D workbench background as first element of uGUI Canvas so all texts and buttons render on top
            if (stage3D.RenderTexture != null)
            {
                ui.DrawNativeRawTex(new Rect(0f, 0f, Screen.width, Screen.height), stage3D.RenderTexture, Color.white);
            }

            // Mouse wheel to paginate 3D cards when in 'Cards' view and dropdown is closed
            if (!modalOpen && zoomCard == null && !inDecksView && !showExpansionDropdown && Event.current.type == EventType.ScrollWheel && lastCatalogGuiRect.Contains(Event.current.mousePosition))
            {
                if (Event.current.delta.y > 0.05f) ChangePage(1);
                else if (Event.current.delta.y < -0.05f) ChangePage(-1);
                Event.current.Use();
            }

            // If no card zoom is active, draw sidebars and main area
            if (zoomCard == null || zoomAnim < 0.85f)
            {
                DrawLeftSidebar(ui, new Rect(14f, 14f, sidebarW, Screen.height - 28f));

                if (inDecksView)
                {
                    DrawDecksView(ui, new Rect(contentX, 14f, Screen.width - contentX - 14f, Screen.height - 28f));
                }
                else
                {
                    DrawCardsTopFilterBar(ui, new Rect(contentX, 14f, Screen.width - contentX - 14f, topBarH));
                    DrawCatalogOverlay(ui, new Rect(contentX, bodyY, centerW, bodyH), filtered.Count, startIdx);

                    if (variantPopupCard != null)
                        DrawVariantPopup(ui);

                    if (showAdvancedFilters)
                        DrawAdvancedFilterDrawer(ui, rightPanelRect);
                    else
                        DrawRightDeckPanel(ui, rightPanelRect);

                    if (showExpansionDropdown)
                    {
                        DrawExpansionSymbolsDropdown(ui, new Rect(contentX, bodyY - 4f, centerW, bodyH - 42f));
                    }
                }
            }

            // Left/Right click interaction on 3D grid cards
            if (!modalOpen && zoomCard == null && !inDecksView && !showExpansionDropdown && Event.current.type == EventType.MouseDown)
            {
                int btn = Event.current.button;
                int hovIdx = stage3D.HoveredSlotIndex;
                if (hovIdx >= 0 && hovIdx < stage3D.ScreenSlots.Count)
                {
                    var slot = stage3D.ScreenSlots[hovIdx];
                    if (btn == 0)
                    {
                        if (Collection.HasBothVariants(slot.Card))
                        {
                            variantPopupCard = slot.Card;
                            variantPopupSlotIndex = slot.SlotIndex;
                            variantPopupRect = slot.ApproxGuiRect;
                            GameUI.PlayClickSound(1);
                        }
                        else
                        {
                            bool useFoil = Collection.HasFoil(slot.Card) && !Collection.HasNonFoil(slot.Card);
                            if (AddCardToDeck(slot.Card, slot.SlotIndex, useFoil))
                                GameUI.PlayClickSound(0);
                            else
                                GameUI.PlayClickSound(2);
                        }
                        Event.current.Use();
                    }
                    else if (btn == 1)
                    {
                        variantPopupCard = null;
                        GameUI.PlayClickSound(1);
                        OpenInspect3D(slot.Card, slot.ApproxGuiRect, slot.WorldPos, slot.WorldRot, slot.Scale);
                        Event.current.Use();
                    }
                }
            }

            // Native modals
            if (showThemeDecksModal)
            {
                DrawThemeDecksModalNative(ui);
            }
            if (showImportModal)
            {
                DrawImportModalNative(ui);
            }
            if (showSettingsModal)
            {
                PolishEffects3D.DrawSettingsModal(ref showSettingsModal);
            }

            // M5: Right-click 3D card zoom & side info
            if (zoomCard != null)
            {
                CardDef inspected = zoomCard;
                bool closeRequested = GameUI.DrawElevatedZoomOverlay(
                    inspected,
                    null,
                    zoomOriginRect,
                    zoomAnim,
                    force3DCard: true,
                    nativeFooterDrawer: footerRect =>
                    {
                        int inDeckTotal = currentDeck.Count(x => x.Id == inspected.Id);
                        int inDeckFoil = currentDeck.Count(x => x.Id == inspected.Id && x.IsFoil);
                        int inDeckNonFoil = inDeckTotal - inDeckFoil;
                        int ownedTotal = Collection.DisplayOwnedCount(inspected);
                        int ownedFoil = Collection.OwnedFoil(inspected);
                        int ownedNonFoil = Collection.OwnedNonFoil(inspected);
                        string flipLabel = stage3D.InspectShowingBack
                            ? "Front (F)"
                            : "Back (F)";

                        ui.DrawNativeBox(footerRect, GameUI.sSubPanelSprite, new Color(0.10f, 0.15f, 0.24f, 0.96f));
                        string countStr = $"In deck: <color=#80ed99>{inDeckTotal}</color> (<color=#80ed99>{inDeckNonFoil}</color> reg, <color=#ffe066>{inDeckFoil}★</color>)  |  Collection: <color=#ffe066>x{ownedTotal}</color> (<color=#80ed99>{ownedNonFoil}</color> reg, <color=#ffe066>{ownedFoil}★</color>)";
                        ui.DrawNativeLabel(
                            new Rect(footerRect.x + 12f, footerRect.y + 6f, footerRect.width - 24f, 24f),
                            countStr,
                            13.5f,
                            TextAlignmentOptions.Center,
                            Color.white,
                            autoSize: true);

                        bool curInspectFoil = stage3D.CurrentInspectFoil;
                        string foilLabel = curInspectFoil ? "No Foil" : "Foil";
                        bool canToggleFoil = curInspectFoil ? Collection.HasNonFoil(inspected) : Collection.HasFoil(inspected);

                        float btnY = footerRect.y + 34f;
                        float btnH = 32f;
                        float totalW = footerRect.width - 24f;
                        float wFoil = 76f;
                        float wFlip = totalW * 0.26f;
                        float remW = totalW - wFlip - wFoil - 24f;
                        float wSub = remW * 0.5f;

                        float curBtnX = footerRect.x + 12f;

                        // 1. Botón Flip Front/Back
                        if (ui.DrawNativeButton(new Rect(curBtnX, btnY, wFlip, btnH), flipLabel, 13f, GameUI.sBtnBlueN, GameUI.sBtnBlueH, GameUI.sBtnBlueA))
                        {
                            GameUI.PlayClickSound(0);
                            stage3D.ToggleInspectFlip();
                        }
                        curBtnX += wFlip + 8f;

                        // 2. Botón Foil / No Foil con giro de 0.5s mostrando el reverso oficial
                        var foilBtnN = curInspectFoil ? GameUI.sBtnBlueN : GameUI.sBtnGoldN;
                        var foilBtnH = curInspectFoil ? GameUI.sBtnBlueH : GameUI.sBtnGoldH;
                        var foilBtnA = curInspectFoil ? GameUI.sBtnBlueA : GameUI.sBtnGoldA;
                        if (canToggleFoil)
                        {
                            if (ui.DrawNativeButton(new Rect(curBtnX, btnY, wFoil, btnH), foilLabel, 13f, foilBtnN, foilBtnH, foilBtnA))
                            {
                                if (!stage3D.IsInspectFoilSpinning)
                                {
                                    GameUI.PlayClickSound(0);
                                    stage3D.TriggerFoilFlip(() =>
                                    {
                                        stage3D.SetInspectFoil(!curInspectFoil);
                                    });
                                }
                            }
                        }
                        else
                        {
                            ui.DrawNativeBox(new Rect(curBtnX, btnY, wFoil, btnH), GameUI.sBtnBlueN, new Color(0.18f, 0.22f, 0.32f, 0.45f));
                            ui.DrawNativeLabel(new Rect(curBtnX, btnY + 4f, wFoil, btnH - 8f), foilLabel, 12f, TextAlignmentOptions.Center, new Color(0.6f, 0.65f, 0.75f, 0.6f));
                        }
                        curBtnX += wFoil + 8f;

                        // 3. Botón - Remove
                        if (ui.DrawNativeButton(new Rect(curBtnX, btnY, wSub, btnH), "- Remove", 13.5f, GameUI.sBtnRedN, GameUI.sBtnRedH, GameUI.sBtnRedA))
                        {
                            if (RemoveCardFromDeck(inspected, preferFoil: stage3D.CurrentInspectFoil)) GameUI.PlayClickSound(0);
                            else GameUI.PlayClickSound(2);
                        }
                        curBtnX += wSub + 8f;

                        // 4. Botón + Add
                        if (ui.DrawNativeButton(new Rect(curBtnX, btnY, wSub, btnH), "+ Add", 13.5f, GameUI.sBtnGreenN, GameUI.sBtnGreenH, GameUI.sBtnGreenA))
                        {
                            if (AddCardToDeck(inspected, -1, isFoil: stage3D.CurrentInspectFoil)) GameUI.PlayClickSound(0);
                            else GameUI.PlayClickSound(2);
                        }
                    });

                if (closeRequested)
                    zoomClosing = true;
            }
        }

        // ==================== M3: Left Sidebar ====================
        void DrawLeftSidebar(GameUI ui, Rect sidebarRect)
        {
            ui.DrawNativeBox(sidebarRect, GameUI.sPanelSprite, new Color(0.07f, 0.10f, 0.16f, 0.96f));

            float bx = sidebarRect.x + 12f;
            float bw = sidebarRect.width - 24f;
            float bh = 46f;
            float by = sidebarRect.y + 16f;

            // Inside 'Decks', sidebar buttons disappear and only 'Back' is shown
            if (inDecksView)
            {
                if (ui.DrawNativeButton(new Rect(bx, by, bw, bh), "Back", 18f, GameUI.sBtnGoldN, GameUI.sBtnGoldH, GameUI.sBtnGoldA))
                {
                    GameUI.PlayClickSound(0);
                    inDecksView = false;
                    renamingDeckName = "";
                }

                Rect infoBox = new Rect(bx, by + bh + 18f, bw, 150f);
                ui.DrawNativeBox(infoBox, GameUI.sSubPanelSprite, new Color(0.11f, 0.16f, 0.25f, 0.94f));
                ui.DrawNativeLabel(
                    new Rect(infoBox.x + 10f, infoBox.y + 10f, infoBox.width - 20f, infoBox.height - 20f),
                    $"<color=#ffe066>Active Deck:</color>\n{deckDef.Name}\n\n<color=#9ecbff>{currentDeck.Count} / {format.DeckSize} cards</color>",
                    14f,
                    TextAlignmentOptions.TopLeft,
                    Color.white,
                    autoSize: true);
                return;
            }

            // Regular view: redistributed sidebar buttons without emojis
            if (ui.DrawNativeButton(new Rect(bx, by, bw, bh), "Decks", 18f, GameUI.sBtnBlueN, GameUI.sBtnBlueH, GameUI.sBtnBlueA))
            {
                GameUI.PlayClickSound(0);
                inDecksView = true;
                showAdvancedFilters = false;
                showExpansionDropdown = false;
                decksViewPage = 0;
                renamingDeckName = "";
            }
            by += bh + 10f;

            if (ui.DrawNativeButton(new Rect(bx, by, bw, bh), "Cards", 18f, GameUI.sBtnGoldN, GameUI.sBtnGoldH, GameUI.sBtnGoldA))
            {
                GameUI.PlayClickSound(0);
                inDecksView = false;
                showAdvancedFilters = false;
                showExpansionDropdown = false;
            }
            by += bh + 10f;

            if (ui.DrawNativeButton(new Rect(bx, by, bw, bh), "Official Decks", 16f, GameUI.sBtnBlueN, GameUI.sBtnBlueH, GameUI.sBtnBlueA))
            {
                GameUI.PlayClickSound(1);
                Collection.Invalidate();
                showImportModal = false;
                showSettingsModal = false;
                showExpansionDropdown = false;
                showThemeDecksModal = !showThemeDecksModal;
            }
            by += bh + 10f;

            if (ui.DrawNativeButton(new Rect(bx, by, bw, bh), "Import Deck", 16f, GameUI.sBtnBlueN, GameUI.sBtnBlueH, GameUI.sBtnBlueA))
            {
                GameUI.PlayClickSound(1);
                importTxtFiles = PtcgoImporter.ListTxtFiles();
                if (lastImportReport == null)
                    lastImportReport = PtcgoImporter.AnalyzeDeck(deckDef.Name, currentDeck);
                showThemeDecksModal = false;
                showSettingsModal = false;
                showExpansionDropdown = false;
                showImportModal = !showImportModal;
            }
            by += bh + 10f;

            if (ui.DrawNativeButton(new Rect(bx, by, bw, bh), "Save Deck", 17f, GameUI.sBtnGreenN, GameUI.sBtnGreenH, GameUI.sBtnGreenA))
            {
                GameUI.PlayClickSound(1);
                SaveCurrentPlayerDeck();
            }
            by += bh + 16f;

            // Quick summary box of deck being edited and status
            float bottomExitY = sidebarRect.yMax - bh - 14f;
            float summaryH = Mathf.Max(80f, bottomExitY - by - 12f);
            Rect summaryBox = new Rect(bx, by, bw, summaryH);
            ui.DrawNativeBox(summaryBox, GameUI.sSubPanelSprite, new Color(0.11f, 0.16f, 0.25f, 0.94f));

            RefreshDeckStatsIfNeeded();
            string validTxt = cachedDeckErrors.Count == 0
                ? "<color=#80ed99>Valid deck for play</color>"
                : $"<color=#ffd166>{cachedDeckErrors[0]}</color>";

            ui.DrawNativeLabel(
                new Rect(summaryBox.x + 10f, summaryBox.y + 10f, summaryBox.width - 20f, summaryBox.height - 20f),
                $"<color=#ffe066>Editing Deck:</color>\n{deckDef.Name}\n\n" +
                $"<color=#9ecbff>{currentDeck.Count} / {format.DeckSize} cards</color>\n" +
                $"Format: {format.Name}\n\n{validTxt}\n\n<color=#b8d0eb>{statusMsg}</color>",
                13.5f,
                TextAlignmentOptions.TopLeft,
                Color.white,
                autoSize: true,
                minFontSize: 9.5f);

            if (ui.DrawNativeButton(new Rect(bx, bottomExitY, bw, bh), "Exit", 18f, GameUI.sBtnRedN, GameUI.sBtnRedH, GameUI.sBtnRedA))
            {
                GameUI.PlayClickSound(2);
                CloseBuilder();
            }
        }

        // ==================== M3: 'Decks' View ====================
        List<DeckSummaryInfo> BuildUserDeckSummaries()
        {
            if (!deckSummariesDirty)
                return deckSummariesCache;

            deckSummariesDirty = false;
            deckSummariesCache.Clear();

            deckNames = DeckStore.List();
            if (deckNames.Count == 0)
            {
                DeckStore.Save(deckDef);
                deckNames = DeckStore.List();
            }

            foreach (var name in deckNames)
            {
                var d = DeckStore.Load(name);
                if (d == null) continue;
                var cards = d.Resolve();
                var types = new List<string>();
                var sets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var c in cards)
                {
                    if (c == null) continue;
                    if (!string.IsNullOrEmpty(c.SetId)) sets.Add(c.SetId);
                    string t = c.Supertype == Supertype.Pokemon ? c.Type : (c.Supertype == Supertype.Energy ? c.EnergyProvides : "");
                    if (!string.IsNullOrEmpty(t) && t != "Rainbow" && !types.Any(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase)))
                        types.Add(t);
                }

                CardDef cover = cards
                    .Where(c => c != null && c.Supertype == Supertype.Pokemon)
                    .OrderByDescending(c => c.Hp)
                    .FirstOrDefault() ?? cards.FirstOrDefault();

                deckSummariesCache.Add(new DeckSummaryInfo
                {
                    Def = d,
                    Cards = cards,
                    Types = types,
                    Sets = sets,
                    CoverCard = cover
                });
            }
            return deckSummariesCache;
        }

        void DrawDecksView(GameUI ui, Rect area)
        {
            // Top bar in 'Decks' (Row 1: actions + search + expansion; Row 2: circular type icons)
            Rect topBox = new Rect(area.x, area.y, area.width, 102f);
            ui.DrawNativeBox(topBox, GameUI.sPanelSprite, new Color(0.08f, 0.11f, 0.18f, 0.96f));

            float rx = topBox.x + 12f;
            float ry1 = topBox.y + 10f;
            float bh = 38f;

            // 1. New Deck
            if (ui.DrawNativeButton(new Rect(rx, ry1, 142f, bh), "New Deck", 15.5f, GameUI.sBtnGreenN, GameUI.sBtnGreenH, GameUI.sBtnGreenA))
            {
                GameUI.PlayClickSound(1);
                var fresh = new DeckDef { Name = UniqueName("New Deck"), Format = format.Id };
                DeckStore.Save(fresh);
                InvalidateDeckSummaries();
                LoadDeck(fresh);
                inDecksView = false;
                statusMsg = $"<color=#80ed99>Deck '{fresh.Name}' created. Add cards from the collection.</color>";
            }
            rx += 150f;

            // 2. Import Deck
            if (ui.DrawNativeButton(new Rect(rx, ry1, 152f, bh), "Import Deck", 15.5f, GameUI.sBtnBlueN, GameUI.sBtnBlueH, GameUI.sBtnBlueA))
            {
                GameUI.PlayClickSound(1);
                importTxtFiles = PtcgoImporter.ListTxtFiles();
                if (lastImportReport == null)
                    lastImportReport = PtcgoImporter.AnalyzeDeck(deckDef.Name, currentDeck);
                showThemeDecksModal = false;
                showSettingsModal = false;
                showImportModal = true;
            }
            rx += 160f;

            // 3. Filter Decks by Expansion
            string expLabel = string.IsNullOrEmpty(deckFilterSetId)
                ? "Set: All"
                : (CardDatabase.GetSet(deckFilterSetId)?.Name ?? deckFilterSetId);
            if (ui.DrawNativeButton(new Rect(rx, ry1, 195f, bh), expLabel, 15f,
                string.IsNullOrEmpty(deckFilterSetId) ? GameUI.sBtnBlueN : GameUI.sBtnGoldN,
                string.IsNullOrEmpty(deckFilterSetId) ? GameUI.sBtnBlueH : GameUI.sBtnGoldH,
                string.IsNullOrEmpty(deckFilterSetId) ? GameUI.sBtnBlueA : GameUI.sBtnGoldA))
            {
                GameUI.PlayClickSound(0);
                CycleSetFilter(ref deckFilterSetId);
                decksViewPage = 0;
            }
            rx += 205f;

            // 4. Gear Button (Mod Settings) at top-right corner
            Rect gearRect = new Rect(topBox.xMax - 52f, ry1, 40f, bh);
            if (ui.DrawNativeIconButton(gearRect, SpriteCache.GearIconTex, GameUI.sBtnBlueN, GameUI.sBtnBlueH, GameUI.sBtnBlueA, 7f))
            {
                GameUI.PlayClickSound(0);
                showImportModal = false;
                showThemeDecksModal = false;
                showSettingsModal = !showSettingsModal;
            }

            // 5. Search box by deck name
            float searchW = Mathf.Clamp(gearRect.x - rx - 12f, 180f, 340f);
            Rect searchRect = new Rect(gearRect.x - searchW - 10f, ry1, searchW, bh);
            string nextDeckSearch = ui.DrawNativeTextField(
                searchRect,
                "DeckSearchField",
                deckSearchQuery,
                "Search deck...",
                15f,
                45,
                SpriteCache.SearchLensIconTex);
            if (!string.Equals(nextDeckSearch, deckSearchQuery, StringComparison.Ordinal))
            {
                deckSearchQuery = nextDeckSearch;
                decksViewPage = 0;
            }

            // Row 2: Filter by Deck Type with circular type icons
            float ry2 = topBox.y + 56f;
            ui.DrawNativeLabel(
                new Rect(topBox.x + 14f, ry2 + 4f, 150f, 30f),
                "Filter by type:",
                15f,
                TextAlignmentOptions.Left,
                new Color(0.85f, 0.92f, 1f, 1f),
                autoSize: true);

            float iconX = topBox.x + 168f;
            float iconSz = 34f;
            bool anyDeckTypeActive = deckFilterTypes.Count > 0;
            for (int i = 0; i < SpriteCache.AllTypeIconNames.Length; i++)
            {
                string tName = SpriteCache.AllTypeIconNames[i];
                bool sel = deckFilterTypes.Contains(tName);
                if (ui.DrawNativeTypeIconButton(new Rect(iconX, ry2 + 2f, iconSz, iconSz), tName, sel, anyDeckTypeActive))
                {
                    GameUI.PlayClickSound(0);
                    if (sel) deckFilterTypes.Remove(tName);
                    else deckFilterTypes.Add(tName);
                    decksViewPage = 0;
                }
                iconX += iconSz + 8f;
            }

            if (anyDeckTypeActive)
            {
                if (ui.DrawNativeButton(new Rect(iconX + 6f, ry2 + 3f, 130f, 32f), "Clear types", 14f, GameUI.sBtnRedN, GameUI.sBtnRedH, GameUI.sBtnRedA))
                {
                    GameUI.PlayClickSound(0);
                    deckFilterTypes.Clear();
                    decksViewPage = 0;
                }
            }

            // Created Decks Grid
            var allDecks = BuildUserDeckSummaries();
            var matchingDecks = allDecks.Where(d =>
            {
                if (!string.IsNullOrWhiteSpace(deckSearchQuery) && !ContainsIgnoreCase(d.Def.Name, deckSearchQuery.Trim()))
                    return false;
                if (!string.IsNullOrEmpty(deckFilterSetId) && !d.Sets.Contains(deckFilterSetId))
                    return false;
                if (deckFilterTypes.Count > 0 && !deckFilterTypes.All(req => d.Types.Any(dt => string.Equals(dt, req, StringComparison.OrdinalIgnoreCase))))
                    return false;
                return true;
            }).ToList();

            Rect gridBox = new Rect(area.x, topBox.yMax + 10f, area.width, area.height - topBox.height - 10f);
            ui.DrawNativeBox(gridBox, GameUI.sPanelSprite, new Color(0.06f, 0.09f, 0.14f, 0.88f));

            if (matchingDecks.Count == 0)
            {
                ui.DrawNativeLabel(
                    new Rect(gridBox.center.x - 280f, gridBox.center.y - 40f, 560f, 80f),
                    "No decks match the current search or filters.",
                    20f,
                    TextAlignmentOptions.Center,
                    new Color(1f, 0.88f, 0.52f, 1f),
                    autoSize: true);
                return;
            }

            int perPage = 8; // 4 cols x 2 rows
            int totalDeckPages = Mathf.Max(1, Mathf.CeilToInt(matchingDecks.Count / (float)perPage));
            decksViewPage = Mathf.Clamp(decksViewPage, 0, totalDeckPages - 1);

            int cols = 4;
            int rows = 2;
            float padX = 22f;
            float padY = 16f;
            float availW = gridBox.width - padX * 2f;
            float availH = gridBox.height - 54f - padY * 2f;
            float cellW = (availW - (cols - 1) * 20f) / cols;
            float cellH = (availH - (rows - 1) * 18f) / rows;

            int start = decksViewPage * perPage;
            for (int i = 0; i < perPage && (start + i) < matchingDecks.Count; i++)
            {
                var info = matchingDecks[start + i];
                int c = i % cols;
                int r = i / cols;
                Rect cell = new Rect(
                    gridBox.x + padX + c * (cellW + 20f),
                    gridBox.y + padY + r * (cellH + 18f),
                    cellW,
                    cellH);

                DrawSingleDeckPortraitCard(ui, cell, info);
            }

            // Deck pagination at bottom
            if (totalDeckPages > 1)
            {
                float navY = gridBox.yMax - 42f;
                if (ui.DrawNativeButton(new Rect(gridBox.center.x - 190f, navY, 110f, 32f), "Previous", 14f, GameUI.sBtnBlueN, GameUI.sBtnBlueH, GameUI.sBtnBlueA))
                {
                    GameUI.PlayClickSound(0);
                    decksViewPage = Mathf.Max(0, decksViewPage - 1);
                }
                ui.DrawNativeLabel(
                    new Rect(gridBox.center.x - 70f, navY, 140f, 32f),
                    $"{decksViewPage + 1} / {totalDeckPages}",
                    16f,
                    TextAlignmentOptions.Center,
                    Color.white,
                    autoSize: true);
                if (ui.DrawNativeButton(new Rect(gridBox.center.x + 80f, navY, 110f, 32f), "Next", 14f, GameUI.sBtnBlueN, GameUI.sBtnBlueH, GameUI.sBtnBlueA))
                {
                    GameUI.PlayClickSound(0);
                    decksViewPage = Mathf.Min(totalDeckPages - 1, decksViewPage + 1);
                }
            }
        }

        void DrawSingleDeckPortraitCard(GameUI ui, Rect cell, DeckSummaryInfo info)
        {
            bool isActive = string.Equals(Plugin.ActiveDeck?.Value, info.Def.Name, StringComparison.OrdinalIgnoreCase);
            bool isEditing = string.Equals(deckDef.Name, info.Def.Name, StringComparison.OrdinalIgnoreCase);

            // Outer frame of deck portrait
            float portraitH = cell.height - 76f;
            float portraitW = Mathf.Min(cell.width - 16f, portraitH * 0.76f);
            Rect portraitRect = new Rect(cell.center.x - portraitW * 0.5f, cell.y + 4f, portraitW, portraitH);

            if (isActive || isEditing)
            {
                Rect glow = new Rect(portraitRect.x - 4f, portraitRect.y - 4f, portraitRect.width + 8f, portraitRect.height + 8f);
                ui.DrawNativeBox(glow, GameUI.sBtnGoldN, new Color(1f, 0.88f, 0.35f, 0.95f));
            }
            ui.DrawNativeBox(portraitRect, GameUI.sPanelSprite, new Color(0.12f, 0.17f, 0.27f, 0.98f));

            // Deck cover art
            Rect artRect = new Rect(portraitRect.x + 8f, portraitRect.y + 8f, portraitRect.width - 16f, portraitRect.height - 16f);
            Sprite sp = info.CoverCard != null ? SpriteCache.Get(info.CoverCard.Sprite) : SpriteCache.GetCardBack();
            if (sp == null) sp = SpriteCache.GetCardBack();
            if (sp != null)
            {
                ui.DrawNativeSprite(artRect, sp, Color.white);
            }

            // Card count badge at top-right of portrait
            Rect countPill = new Rect(portraitRect.xMax - 68f, portraitRect.y + 8f, 60f, 24f);
            ui.DrawNativeBox(countPill, GameUI.sPanelSprite, new Color(0.04f, 0.07f, 0.12f, 0.92f));
            ui.DrawNativeLabel(countPill, $"{info.Cards.Count}/60", 13f, TextAlignmentOptions.Center, new Color(0.92f, 0.96f, 1f, 1f), autoSize: true);

            if (isActive)
            {
                Rect actPill = new Rect(portraitRect.x + 8f, portraitRect.y + 8f, 66f, 24f);
                ui.DrawNativeBox(actPill, GameUI.sBtnGreenN, Color.white);
                ui.DrawNativeLabel(actPill, "ACTIVE", 11.5f, TextAlignmentOptions.Center, Color.white, autoSize: true);
            }

            // Circular type icons overlaid at bottom of portrait
            int tCount = Mathf.Min(3, info.Types.Count);
            if (tCount > 0)
            {
                float orbSz = 34f;
                float totalOrbsW = tCount * orbSz + (tCount - 1) * 6f;
                float startOrbX = portraitRect.center.x - totalOrbsW * 0.5f;
                float orbY = portraitRect.yMax - orbSz - 10f;
                for (int t = 0; t < tCount; t++)
                {
                    Rect orbBg = new Rect(startOrbX + t * (orbSz + 6f) - 2f, orbY - 2f, orbSz + 4f, orbSz + 4f);
                    ui.DrawNativeBox(orbBg, GameUI.sPanelSprite, new Color(0.04f, 0.06f, 0.10f, 0.92f));
                    Texture2D typeTex = SpriteCache.GetTypeIcon(info.Types[t]);
                    if (typeTex != null)
                        ui.DrawNativeRawTex(new Rect(startOrbX + t * (orbSz + 6f), orbY, orbSz, orbSz), typeTex, Color.white);
                }
            }

            // Click on portrait -> load and open deck for editing/use
            if (GUI.Button(portraitRect, GUIContent.none, GUIStyle.none))
            {
                GameUI.PlayClickSound(1);
                LoadDeck(info.Def);
                if (DeckValidator.Validate(currentDeck, format).Count == 0)
                    DeckStore.SetActive(info.Def.Name);
                inDecksView = false;
                statusMsg = $"<color=#80ed99>Deck '{info.Def.Name}' loaded.</color>";
            }

            // Deck name or rename input field below portrait
            float nameY = portraitRect.yMax + 4f;
            bool isRenamingThis = string.Equals(renamingDeckName, info.Def.Name, StringComparison.OrdinalIgnoreCase);

            if (isRenamingThis)
            {
                Rect tfRect = new Rect(cell.x + 6f, nameY, cell.width - 78f, 30f);
                renameBuffer = ui.DrawNativeTextField(tfRect, "RenameDeckField", renameBuffer, "Name...", 14f, 40);
                if (ui.DrawNativeButton(new Rect(tfRect.xMax + 4f, nameY, 62f, 30f), "OK", 14f, GameUI.sBtnGreenN, GameUI.sBtnGreenH, GameUI.sBtnGreenA))
                {
                    GameUI.PlayClickSound(1);
                    RenameSavedDeck(info.Def.Name, renameBuffer);
                }
            }
            else
            {
                ui.DrawNativeLabel(
                    new Rect(cell.x + 4f, nameY, cell.width - 8f, 28f),
                    info.Def.Name,
                    16.5f,
                    TextAlignmentOptions.Center,
                    isActive ? new Color(1f, 0.92f, 0.55f, 1f) : Color.white,
                    autoSize: true);
            }

            // Action buttons below name: Edit, Rename, Delete
            float actY = nameY + 32f;
            float btnW = (cell.width - 16f) / 3f;
            if (ui.DrawNativeButton(new Rect(cell.x + 2f, actY, btnW, 30f), "Edit", 13.5f, GameUI.sBtnBlueN, GameUI.sBtnBlueH, GameUI.sBtnBlueA))
            {
                GameUI.PlayClickSound(1);
                LoadDeck(info.Def);
                inDecksView = false;
            }
            if (ui.DrawNativeButton(new Rect(cell.x + 2f + btnW + 6f, actY, btnW, 30f), "Rename", 13.5f, GameUI.sBtnGoldN, GameUI.sBtnGoldH, GameUI.sBtnGoldA))
            {
                GameUI.PlayClickSound(0);
                if (isRenamingThis)
                {
                    RenameSavedDeck(info.Def.Name, renameBuffer);
                }
                else
                {
                    renamingDeckName = info.Def.Name;
                    renameBuffer = info.Def.Name;
                }
            }
            if (ui.DrawNativeButton(new Rect(cell.x + 2f + (btnW + 6f) * 2f, actY, btnW, 30f), "Delete", 13.5f, GameUI.sBtnRedN, GameUI.sBtnRedH, GameUI.sBtnRedA))
            {
                GameUI.PlayClickSound(2);
                string delName = info.Def.Name;
                DeckStore.Delete(delName);
                InvalidateDeckSummaries();
                deckNames = DeckStore.List();
                if (string.Equals(deckDef.Name, delName, StringComparison.OrdinalIgnoreCase))
                    LoadDeck(deckNames.Count > 0 ? DeckStore.Load(deckNames[0]) : null);
                statusMsg = $"<color=#ffd166>Deck '{delName}' deleted.</color>";
            }
        }

        // ==================== M4: 'Cards' Top Filter Bar & Expansion Symbols Dropdown ====================
        void CycleSetFilter(ref string currentSetFilter)
        {
            string cur = currentSetFilter;
            var allowedSets = CardDatabase.Sets
                .Where(s => format.Sets.Contains("*") || format.Sets.Any(fs => string.Equals(fs, s.Id, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (allowedSets.Count == 0)
            {
                currentSetFilter = "";
                return;
            }
            if (string.IsNullOrEmpty(cur))
            {
                currentSetFilter = allowedSets[0].Id;
                return;
            }
            int idx = allowedSets.FindIndex(s => string.Equals(s.Id, cur, StringComparison.OrdinalIgnoreCase));
            if (idx < 0 || idx + 1 >= allowedSets.Count)
                currentSetFilter = "";
            else
                currentSetFilter = allowedSets[idx + 1].Id;
        }

        void DrawCardsTopFilterBar(GameUI ui, Rect barRect)
        {
            ui.DrawNativeBox(barRect, GameUI.sPanelSprite, new Color(0.08f, 0.11f, 0.18f, 0.96f));

            float x = barRect.x + 10f;
            float y = barRect.y + 8f;
            float h = barRect.height - 16f;

            // 1. Advanced Filters Button (sliders icon)
            bool hasAdvActive = showAdvancedFilters || cardCategoryFilter != 0 || !string.IsNullOrEmpty(filterStage) || filterHpBucket != 0 || !string.IsNullOrEmpty(filterSetId);
            if (ui.DrawNativeIconButton(
                new Rect(x, y, 44f, h),
                SpriteCache.FilterSlidersIconTex,
                hasAdvActive ? GameUI.sBtnGoldN : GameUI.sBtnBlueN,
                hasAdvActive ? GameUI.sBtnGoldH : GameUI.sBtnBlueH,
                hasAdvActive ? GameUI.sBtnGoldA : GameUI.sBtnBlueA,
                8f))
            {
                GameUI.PlayClickSound(0);
                showAdvancedFilters = !showAdvancedFilters;
                showExpansionDropdown = false;
            }
            x += 52f;

            // 2. Ownership Filter: All Cards / Owned / Unowned
            string ownLabel = ownershipMode == 1
                ? "Owned"
                : (ownershipMode == 2 ? "Unowned" : "All Cards");
            if (ui.DrawNativeButton(new Rect(x, y, 132f, h), ownLabel, 14.5f, GameUI.sBtnBlueN, GameUI.sBtnBlueH, GameUI.sBtnBlueA))
            {
                GameUI.PlayClickSound(0);
                ownershipMode = (ownershipMode + 1) % 3;
                currentPage = 0;
            }
            x += 140f;

            // 3. All Sets button (resets expansion filter or shows active set)
            string setLabel = string.IsNullOrEmpty(filterSetId)
                ? "All Sets"
                : (CardDatabase.GetSet(filterSetId)?.Name ?? filterSetId);
            if (ui.DrawNativeButton(new Rect(x, y, 152f, h), setLabel, 14.5f,
                string.IsNullOrEmpty(filterSetId) ? GameUI.sBtnBlueN : GameUI.sBtnGoldN,
                string.IsNullOrEmpty(filterSetId) ? GameUI.sBtnBlueH : GameUI.sBtnGoldH,
                string.IsNullOrEmpty(filterSetId) ? GameUI.sBtnBlueA : GameUI.sBtnGoldA))
            {
                GameUI.PlayClickSound(0);
                filterSetId = "";
                showExpansionDropdown = false;
                currentPage = 0;
            }
            x += 160f;

            // 4. Requirement 4: 'Expansion' Dropdown button (replaces 'Base Set')
            bool expDropHighlight = showExpansionDropdown || !string.IsNullOrEmpty(filterSetId);
            if (ui.DrawNativeButton(new Rect(x, y, 128f, h), "Expansion", 14.5f,
                expDropHighlight ? GameUI.sBtnGoldN : GameUI.sBtnBlueN,
                expDropHighlight ? GameUI.sBtnGoldH : GameUI.sBtnBlueH,
                expDropHighlight ? GameUI.sBtnGoldA : GameUI.sBtnBlueA))
            {
                GameUI.PlayClickSound(0);
                showExpansionDropdown = !showExpansionDropdown;
                hoveredExpansionIdx = -1;
                hoveredExpansionTimer = 0f;
            }
            x += 136f;

            // 5. Gear Button (Mod Settings) at top-right corner
            Rect gearRect = new Rect(barRect.xMax - 50f, y, 40f, h);
            if (ui.DrawNativeIconButton(gearRect, SpriteCache.GearIconTex, GameUI.sBtnBlueN, GameUI.sBtnBlueH, GameUI.sBtnBlueA, 7f))
            {
                GameUI.PlayClickSound(0);
                showImportModal = false;
                showThemeDecksModal = false;
                showExpansionDropdown = false;
                showSettingsModal = !showSettingsModal;
            }

            // 6. 'Search' input field to the left of the gear button
            float searchW = 205f;
            Rect searchRect = new Rect(gearRect.x - searchW - 8f, y, searchW, h);
            string nextSearch = ui.DrawNativeTextField(
                searchRect,
                "BuilderSearchField",
                searchQuery,
                "Search",
                14.5f,
                45,
                SpriteCache.SearchLensIconTex);
            if (!string.Equals(nextSearch, searchQuery, StringComparison.Ordinal))
            {
                searchQuery = nextSearch;
                currentPage = 0;
            }

            // 7. Circular icons for the 11 Energy Types between Expansion and Search
            float availIconsW = searchRect.x - x - 8f;
            int numTypes = SpriteCache.AllTypeIconNames.Length;
            if (availIconsW >= numTypes * 24f)
            {
                float iconSz = Mathf.Clamp((availIconsW - (numTypes - 1) * 4f) / numTypes, 22f, 32f);
                float totalIconsW = numTypes * iconSz + (numTypes - 1) * 4f;
                float iconStartX = x + (availIconsW - totalIconsW) * 0.5f;
                float iconY = barRect.center.y - iconSz * 0.5f;
                bool anyTypeActive = cardFilterTypes.Count > 0;

                for (int i = 0; i < numTypes; i++)
                {
                    string tp = SpriteCache.AllTypeIconNames[i];
                    bool sel = cardFilterTypes.Contains(tp);
                    if (ui.DrawNativeTypeIconButton(new Rect(iconStartX + i * (iconSz + 4f), iconY, iconSz, iconSz), tp, sel, anyTypeActive))
                    {
                        GameUI.PlayClickSound(0);
                        if (sel) cardFilterTypes.Remove(tp);
                        else cardFilterTypes.Add(tp);
                        currentPage = 0;
                    }
                }
            }
        }

        // ==================== Requirement 4: Expansion Symbols Dropdown (all-symbols.pdf) ====================
        void DrawExpansionSymbolsDropdown(GameUI ui, Rect catalogArea)
        {
            var catalog = SpriteCache.ExpansionCatalog;
            Texture2D atlas = SpriteCache.ExpansionsAtlasTex;
            if (catalog == null || catalog.Count == 0 || atlas == null)
            {
                showExpansionDropdown = false;
                return;
            }

            int cols = 10;
            int rows = 5;
            int perPage = cols * rows;
            int totalPages = Mathf.Max(1, Mathf.CeilToInt(catalog.Count / (float)perPage));
            expansionDropdownPage = Mathf.Clamp(expansionDropdownPage, 0, totalPages - 1);

            float padX = 12f;
            float padY = 12f;
            float gap = 6f;
            float footerH = 40f;
            float availW = catalogArea.width - padX * 2f;
            float cellSz = Mathf.Clamp(Mathf.Floor((availW - (cols - 1) * gap) / cols), 54f, 84f);
            float gridW = cols * cellSz + (cols - 1) * gap;
            float gridH = rows * cellSz + (rows - 1) * gap;
            float dropW = gridW + padX * 2f;
            float dropH = gridH + padY * 2f + footerH;

            Rect dropRect = new Rect(catalogArea.x + (catalogArea.width - dropW) * 0.5f, catalogArea.y, dropW, dropH);
            ui.DrawNativeBox(dropRect, GameUI.sPanelSprite, new Color(0.06f, 0.09f, 0.15f, 0.98f));

            // Mouse wheel scrolls expansion pages when hovering dropdown
            Vector2 mp = Event.current.mousePosition;
            if (Event.current.type == EventType.ScrollWheel && dropRect.Contains(mp))
            {
                if (Event.current.delta.y > 0.05f) expansionDropdownPage = Mathf.Min(totalPages - 1, expansionDropdownPage + 1);
                else if (Event.current.delta.y < -0.05f) expansionDropdownPage = Mathf.Max(0, expansionDropdownPage - 1);
                Event.current.Use();
            }

            int start = expansionDropdownPage * perPage;
            int newHoveredIdx = -1;
            Rect hoveredCellRect = default;
            SpriteCache.ExpansionSymbolInfo hoveredInfo = null;

            for (int i = 0; i < perPage && (start + i) < catalog.Count; i++)
            {
                int globalIdx = start + i;
                var entry = catalog[globalIdx];
                int c = i % cols;
                int r = i / cols;

                Rect cellRect = new Rect(
                    dropRect.x + padX + c * (cellSz + gap),
                    dropRect.y + padY + r * (cellSz + gap),
                    cellSz,
                    cellSz);

                bool isInstalled = entry.IsInstalled();
                string resolvedSetId = entry.ResolveSetId();
                bool isSelected = !string.IsNullOrEmpty(filterSetId) &&
                                  !string.IsNullOrEmpty(resolvedSetId) &&
                                  string.Equals(filterSetId, resolvedSetId, StringComparison.OrdinalIgnoreCase);
                bool isHover = cellRect.Contains(mp);

                if (isHover)
                {
                    newHoveredIdx = globalIdx;
                    hoveredCellRect = cellRect;
                    hoveredInfo = entry;
                }

                // Cell background (matches image.png dark slate cells, highlighted if selected/hovered)
                Color cellBg;
                if (isSelected)
                    cellBg = new Color(0.36f, 0.29f, 0.10f, 0.98f);
                else if (isInstalled)
                    cellBg = isHover ? new Color(0.22f, 0.30f, 0.44f, 0.98f) : new Color(0.15f, 0.21f, 0.32f, 0.96f);
                else
                    cellBg = isHover ? new Color(0.14f, 0.18f, 0.26f, 0.96f) : new Color(0.10f, 0.13f, 0.20f, 0.95f);

                ui.DrawNativeBox(cellRect, GameUI.sSubPanelSprite, cellBg);

                // Icon tint: regular bright color when installed, dark muted tone when not installed
                float iconPad = Mathf.Round(cellSz * 0.09f);
                Rect iconRect = new Rect(cellRect.x + iconPad, cellRect.y + iconPad, cellSz - iconPad * 2f, cellSz - iconPad * 2f);
                Color iconTint = isInstalled
                    ? Color.white
                    : new Color(0.14f, 0.17f, 0.23f, 0.72f);

                ui.DrawNativeAtlasIcon(iconRect, atlas, entry.UvRect, iconTint);

                if (GUI.Button(cellRect, GUIContent.none, GUIStyle.none))
                {
                    if (isInstalled && !string.IsNullOrEmpty(resolvedSetId))
                    {
                        GameUI.PlayClickSound(0);
                        filterSetId = isSelected ? "" : resolvedSetId;
                        if (!string.IsNullOrEmpty(filterSetId) && !format.Sets.Contains("*") && !format.Sets.Any(s => string.Equals(s, filterSetId, StringComparison.OrdinalIgnoreCase)))
                        {
                            format = FormatRegistry.Get("unlimited") ?? format;
                        }
                        currentPage = 0;
                        showExpansionDropdown = false;
                        hoveredExpansionIdx = -1;
                        hoveredExpansionTimer = 0f;
                    }
                    else
                    {
                        GameUI.PlayClickSound(2);
                        statusMsg = $"<color=#ffd166>Expansion '{entry.Name}' is not installed.</color>";
                    }
                }
            }

            if (newHoveredIdx != hoveredExpansionIdx)
            {
                hoveredExpansionIdx = newHoveredIdx;
                hoveredExpansionTimer = 0f;
            }

            // Footer bar inside dropdown: All Expansions, Prev/Next page, Close
            float fy = dropRect.yMax - footerH + 4f;
            float fh = 30f;
            if (ui.DrawNativeButton(new Rect(dropRect.x + padX, fy, 145f, fh), "All Expansions", 13.5f, GameUI.sBtnBlueN, GameUI.sBtnBlueH, GameUI.sBtnBlueA))
            {
                GameUI.PlayClickSound(0);
                filterSetId = "";
                currentPage = 0;
                showExpansionDropdown = false;
            }

            if (totalPages > 1)
            {
                if (ui.DrawNativeButton(new Rect(dropRect.center.x - 165f, fy, 95f, fh), "Previous", 13.5f, GameUI.sBtnGoldN, GameUI.sBtnGoldH, GameUI.sBtnGoldA))
                {
                    GameUI.PlayClickSound(0);
                    expansionDropdownPage = Mathf.Max(0, expansionDropdownPage - 1);
                }
                ui.DrawNativeLabel(
                    new Rect(dropRect.center.x - 65f, fy, 130f, fh),
                    $"Page {expansionDropdownPage + 1} / {totalPages}",
                    14f,
                    TextAlignmentOptions.Center,
                    Color.white,
                    autoSize: true);
                if (ui.DrawNativeButton(new Rect(dropRect.center.x + 70f, fy, 95f, fh), "Next", 13.5f, GameUI.sBtnGoldN, GameUI.sBtnGoldH, GameUI.sBtnGoldA))
                {
                    GameUI.PlayClickSound(0);
                    expansionDropdownPage = Mathf.Min(totalPages - 1, expansionDropdownPage + 1);
                }
            }

            if (ui.DrawNativeButton(new Rect(dropRect.xMax - padX - 95f, fy, 95f, fh), "Close", 13.5f, GameUI.sBtnRedN, GameUI.sBtnRedH, GameUI.sBtnRedA))
            {
                GameUI.PlayClickSound(0);
                showExpansionDropdown = false;
            }

            // Clicking outside the dropdown closes it
            if (Event.current.type == EventType.MouseDown && !dropRect.Contains(mp) && mp.y > catalogArea.y)
            {
                showExpansionDropdown = false;
                hoveredExpansionIdx = -1;
                hoveredExpansionTimer = 0f;
                Event.current.Use();
            }

            // Hover tooltip showing expansion name after a brief hover (~0.65s), matching image.png
            if (hoveredInfo != null && hoveredExpansionTimer >= 0.65f)
            {
                string tipText = hoveredInfo.Name;
                float tipW = Mathf.Clamp(tipText.Length * 11.5f + 28f, 110f, 340f);
                float tipH = 34f;
                float tipX = Mathf.Clamp(hoveredCellRect.center.x - tipW * 0.5f, dropRect.x + 6f, dropRect.xMax - tipW - 6f);
                float tipY = hoveredCellRect.yMax - 6f;
                if (tipY + tipH > dropRect.yMax - 4f)
                    tipY = hoveredCellRect.y - tipH + 4f;

                Rect tipRect = new Rect(tipX, tipY, tipW, tipH);
                ui.DrawNativeBox(tipRect, GameUI.sPanelSprite, new Color(0.04f, 0.06f, 0.11f, 0.96f));
                ui.DrawNativeLabel(
                    new Rect(tipRect.x + 8f, tipRect.y + 2f, tipRect.width - 16f, tipRect.height - 4f),
                    tipText,
                    18f,
                    TextAlignmentOptions.Center,
                    Color.white,
                    autoSize: true,
                    minFontSize: 11f);
            }
        }

        // ==================== M5: 3D Grid Overlay & Large xN Counter ====================
        void DrawCatalogOverlay(GameUI ui, Rect catalogOuterRect, int totalFiltered, int startIdx)
        {
            string missingArt = CardDatabase.GetMissingArtNotice();
            if (!string.IsNullOrEmpty(missingArt))
            {
                Rect warnBanner = new Rect(catalogOuterRect.x + 12f, catalogOuterRect.y + 4f, catalogOuterRect.width - 24f, 28f);
                ui.DrawNativeBox(warnBanner, GameUI.sSubPanelSprite, new Color(0.24f, 0.16f, 0.04f, 0.94f));
                ui.DrawNativeLabel(warnBanner, missingArt, 12.5f, TextAlignmentOptions.Center, new Color(1f, 0.88f, 0.50f, 1f), autoSize: true, minFontSize: 9.5f);
            }

            if (totalFiltered == 0)
            {
                Rect emptyBox = new Rect(catalogOuterRect.center.x - 260f, catalogOuterRect.center.y - 54f, 520f, 108f);
                ui.DrawNativeBox(emptyBox, GameUI.sPanelSprite, new Color(0.08f, 0.12f, 0.20f, 0.95f));
                ui.DrawNativeLabel(
                    new Rect(emptyBox.x + 16f, emptyBox.y + 12f, emptyBox.width - 32f, 46f),
                    "No cards match the selected filters.",
                    16f,
                    TextAlignmentOptions.Center,
                    new Color(1f, 0.88f, 0.52f, 1f),
                    autoSize: true);

                if (ui.DrawNativeButton(new Rect(emptyBox.center.x - 110f, emptyBox.y + 62f, 220f, 34f), "Reset Filters", 14.5f, GameUI.sBtnGoldN, GameUI.sBtnGoldH, GameUI.sBtnGoldA))
                {
                    GameUI.PlayClickSound(0);
                    ResetAllCardFilters();
                }
            }
            else
            {
                // Large counter in top-right corner of each 3D card (e.g., x0, x3, x10)
                var slots = stage3D.ScreenSlots;
                for (int i = 0; i < slots.Count; i++)
                {
                    var s = slots[i];
                    if (s.Card == null) continue;

                    int owned = Collection.DisplayOwnedCount(s.Card);
                    int inDeck = GetInDeckCount(s.Card.Id);

                    Rect countBadge = new Rect(s.TopRightGui.x - 54f, s.TopRightGui.y - 6f, 60f, 32f);
                    ui.DrawNativeBox(countBadge, GameUI.sPanelSprite, new Color(0.04f, 0.06f, 0.11f, 0.94f));
                    Color countCol = owned > 0 ? new Color(1f, 0.94f, 0.58f, 1f) : new Color(0.68f, 0.72f, 0.78f, 0.92f);
                    ui.DrawNativeLabel(countBadge, $"x{owned}", 21f, TextAlignmentOptions.Center, countCol, autoSize: true, minFontSize: 12f);

                    if (inDeck > 0)
                    {
                        Rect inDeckPill = new Rect(s.BottomCenterGui.x - 56f, s.BottomCenterGui.y - 20f, 112f, 24f);
                        ui.DrawNativeBox(inDeckPill, GameUI.sSubPanelSprite, new Color(0.06f, 0.16f, 0.12f, 0.94f));
                        ui.DrawNativeLabel(inDeckPill, $"In deck: {inDeck}", 13f, TextAlignmentOptions.Center, new Color(0.55f, 1f, 0.72f, 1f), autoSize: true);
                    }
                }
            }

            // Bottom 3D pagination bar
            Rect navBar = new Rect(catalogOuterRect.x, catalogOuterRect.yMax - 38f, catalogOuterRect.width, 36f);
            ui.DrawNativeBox(navBar, GameUI.sPanelSprite, new Color(0.07f, 0.10f, 0.16f, 0.94f));

            float ny = navBar.y + 4f;
            float nh = 28f;
            if (ui.DrawNativeButton(new Rect(navBar.x + 10f, ny, 96f, nh), "First", 13.5f, GameUI.sBtnBlueN, GameUI.sBtnBlueH, GameUI.sBtnBlueA))
            {
                if (currentPage > 0) { currentPage = 0; stage3D.TriggerCarouselSlide(-1); GameUI.PlayClickSound(0); }
            }
            if (ui.DrawNativeButton(new Rect(navBar.x + 112f, ny, 110f, nh), "Previous", 13.5f, GameUI.sBtnGoldN, GameUI.sBtnGoldH, GameUI.sBtnGoldA))
            {
                ChangePage(-1);
            }

            int shownEnd = Mathf.Min(totalFiltered, startIdx + pageCardsBuffer.Count);
            string rangeStr = totalFiltered > 0 ? $"({startIdx + 1}-{shownEnd} of {totalFiltered})" : "(0)";
            ui.DrawNativeLabel(
                new Rect(navBar.center.x - 150f, ny, 300f, nh),
                $"Page {currentPage + 1} / {lastTotalPages}   <color=#9ecbff>{rangeStr}</color>",
                14.5f,
                TextAlignmentOptions.Center,
                Color.white,
                autoSize: true);

            if (ui.DrawNativeButton(new Rect(navBar.xMax - 222f, ny, 110f, nh), "Next", 13.5f, GameUI.sBtnGoldN, GameUI.sBtnGoldH, GameUI.sBtnGoldA))
            {
                ChangePage(1);
            }
            if (ui.DrawNativeButton(new Rect(navBar.xMax - 106f, ny, 96f, nh), "Last", 13.5f, GameUI.sBtnBlueN, GameUI.sBtnBlueH, GameUI.sBtnBlueA))
            {
                if (currentPage < lastTotalPages - 1) { currentPage = lastTotalPages - 1; stage3D.TriggerCarouselSlide(1); GameUI.PlayClickSound(0); }
            }
        }

        /// <summary>
        /// Dibuja el selector contextual con los 2 botones al hacer clic en una carta que posee variantes Foil y No Foil.
        /// - Botón 1: Estrella negra de 4 puntas (No Foil).
        /// - Botón 2: Múltiples estrellas amarillas (Foil).
        /// </summary>
        void DrawVariantPopup(GameUI ui)
        {
            if (variantPopupCard == null) return;

            float btnSize = 38f;
            float pad = 6f;
            float popW = btnSize + pad * 2f;
            float popH = btnSize * 2f + pad * 3f;

            float popX = variantPopupRect.xMax + 6f;
            if (popX + popW > Screen.width - 340f)
                popX = variantPopupRect.xMin - popW - 6f;

            float popY = Mathf.Clamp(variantPopupRect.center.y - popH * 0.5f, 60f, Screen.height - popH - 40f);
            Rect popRect = new Rect(popX, popY, popW, popH);

            ui.DrawNativeBox(popRect, GameUI.sSubPanelSprite, new Color(0.06f, 0.09f, 0.16f, 0.98f));

            Vector2 mp = Event.current.mousePosition;
            string hoveredTip = null;
            Rect tipAnchor = Rect.zero;

            // Botón Superior: Estrella negra 4 puntas (No Foil)
            Rect btnNoFoilRect = new Rect(popX + pad, popY + pad, btnSize, btnSize);
            bool hovNoFoil = btnNoFoilRect.Contains(mp);
            Color colNoFoilBg = hovNoFoil ? new Color(0.24f, 0.32f, 0.46f, 0.95f) : new Color(0.12f, 0.16f, 0.24f, 0.90f);
            ui.DrawNativeBox(btnNoFoilRect, GameUI.sBtnBlueN, colNoFoilBg);
            if (SpriteCache.IconNoFoil != null)
            {
                ui.DrawNativeRawTex(new Rect(btnNoFoilRect.x + 5f, btnNoFoilRect.y + 5f, btnSize - 10f, btnSize - 10f), SpriteCache.IconNoFoil, Color.white);
            }
            else
            {
                ui.DrawNativeLabel(btnNoFoilRect, "✦", 20f, TextAlignmentOptions.Center, Color.black);
            }

            if (hovNoFoil)
            {
                int owned = Collection.OwnedNonFoil(variantPopupCard);
                hoveredTip = $"No Foil (x{owned})";
                tipAnchor = btnNoFoilRect;
            }

            if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && hovNoFoil)
            {
                GameUI.PlayClickSound(0);
                AddCardToDeck(variantPopupCard, variantPopupSlotIndex, isFoil: false);
                variantPopupCard = null;
                Event.current.Use();
                return;
            }

            // Botón Inferior: Estrellas doradas (Foil)
            Rect btnFoilRect = new Rect(popX + pad, popY + pad * 2f + btnSize, btnSize, btnSize);
            bool hovFoil = btnFoilRect.Contains(mp);
            Color colFoilBg = hovFoil ? new Color(0.42f, 0.35f, 0.16f, 0.95f) : new Color(0.18f, 0.16f, 0.12f, 0.90f);
            ui.DrawNativeBox(btnFoilRect, GameUI.sBtnGoldN, colFoilBg);
            if (SpriteCache.IconFoil != null)
            {
                ui.DrawNativeRawTex(new Rect(btnFoilRect.x + 4f, btnFoilRect.y + 4f, btnSize - 8f, btnSize - 8f), SpriteCache.IconFoil, Color.white);
            }
            else
            {
                ui.DrawNativeLabel(btnFoilRect, "★", 20f, TextAlignmentOptions.Center, new Color(1f, 0.88f, 0.2f, 1f));
            }

            if (hovFoil)
            {
                int owned = Collection.OwnedFoil(variantPopupCard);
                hoveredTip = $"Foil (x{owned})";
                tipAnchor = btnFoilRect;
            }

            if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && hovFoil)
            {
                GameUI.PlayClickSound(0);
                AddCardToDeck(variantPopupCard, variantPopupSlotIndex, isFoil: true);
                variantPopupCard = null;
                Event.current.Use();
                return;
            }

            // Tooltip flotante si se pasa el cursor por encima
            if (!string.IsNullOrEmpty(hoveredTip))
            {
                float tw = hoveredTip.Length * 9.5f + 20f;
                float th = 26f;
                float tx = tipAnchor.xMax + 6f;
                if (tx + tw > Screen.width - 10f) tx = tipAnchor.xMin - tw - 6f;
                Rect tipR = new Rect(tx, tipAnchor.center.y - th * 0.5f, tw, th);
                ui.DrawNativeBox(tipR, GameUI.sPanelSprite, new Color(0.04f, 0.06f, 0.10f, 0.98f));
                ui.DrawNativeLabel(tipR, hoveredTip, 13f, TextAlignmentOptions.Center, new Color(1f, 0.92f, 0.65f, 1f));
            }

            // Cerrar si hace clic fuera del popup
            if (Event.current.type == EventType.MouseDown && !popRect.Contains(mp) && !variantPopupRect.Contains(mp))
            {
                variantPopupCard = null;
            }
        }

        void ResetAllCardFilters()
        {
            ownershipMode = 0;
            cardCategoryFilter = 0;
            filterStage = "";
            filterHpBucket = 0;
            filterSetId = "";
            cardFilterTypes.Clear();
            searchQuery = "";
            currentPage = 0;
        }

        // ==================== M4: Panel Lateral de Filtros Avanzados (Estilo Imagen 5) ====================
        void DrawAdvancedFilterDrawer(GameUI ui, Rect panelRect)
        {
            ui.DrawNativeBox(panelRect, GameUI.sPanelSprite, new Color(0.08f, 0.11f, 0.18f, 0.98f));

            float x = panelRect.x + 14f;
            float w = panelRect.width - 28f;
            float y = panelRect.y + 12f;

            // Cabecera del panel de filtros
            ui.DrawNativeLabel(new Rect(x, y, w - 90f, 28f), Loc.T("FILTRAR BÚSQUEDA", "FILTER SEARCH"), 18f, TextAlignmentOptions.Left, new Color(1f, 0.90f, 0.50f, 1f), autoSize: true);
            if (ui.DrawNativeButton(new Rect(panelRect.xMax - 96f, y, 82f, 28f), Loc.T("Cerrar", "Close"), 13.5f, GameUI.sBtnRedN, GameUI.sBtnRedH, GameUI.sBtnRedA))
            {
                GameUI.PlayClickSound(0);
                showAdvancedFilters = false;
                return;
            }
            y += 34f;

            // 1. CANTIDAD DE VIDA (PS) — en lugar de costo de maná (10-50, 60-120, 130-200, 200+)
            ui.DrawNativeLabel(new Rect(x, y, w, 22f), Loc.T("CANTIDAD DE VIDA (PS)", "HIT POINTS (HP)"), 14f, TextAlignmentOptions.Left, new Color(0.78f, 0.88f, 1f, 1f), autoSize: true);
            y += 24f;
            string[] hpLabels = { Loc.T("Todos", "All"), "10-50", "60-120", "130-200", "200+" };
            float hpBtnW = (w - 16f) / 5f;
            for (int i = 0; i < hpLabels.Length; i++)
            {
                bool active = filterHpBucket == i;
                if (ui.DrawNativeButton(new Rect(x + i * (hpBtnW + 4f), y, hpBtnW, 30f), hpLabels[i], 13f,
                    active ? GameUI.sBtnGoldN : GameUI.sBtnBlueN,
                    active ? GameUI.sBtnGoldH : GameUI.sBtnBlueH,
                    active ? GameUI.sBtnGoldA : GameUI.sBtnBlueA))
                {
                    GameUI.PlayClickSound(0);
                    filterHpBucket = (filterHpBucket == i && i != 0) ? 0 : i;
                    currentPage = 0;
                }
            }
            y += 38f;

            // 2. COLOR / ELEMENTO (Iconos circulares de los 11 tipos)
            ui.DrawNativeLabel(new Rect(x, y, w, 22f), Loc.T("COLOR / ELEMENTO", "COLOR / TYPE"), 14f, TextAlignmentOptions.Left, new Color(0.78f, 0.88f, 1f, 1f), autoSize: true);
            y += 24f;
            int colsType = 6;
            float iconStep = w / colsType;
            float iconSize = Mathf.Min(36f, iconStep - 6f);
            bool anyType = cardFilterTypes.Count > 0;
            for (int i = 0; i < SpriteCache.AllTypeIconNames.Length; i++)
            {
                string tp = SpriteCache.AllTypeIconNames[i];
                int c = i % colsType;
                int r = i / colsType;
                Rect ir = new Rect(x + c * iconStep + (iconStep - iconSize) * 0.5f, y + r * (iconSize + 6f), iconSize, iconSize);
                bool sel = cardFilterTypes.Contains(tp);
                if (ui.DrawNativeTypeIconButton(ir, tp, sel, anyType))
                {
                    GameUI.PlayClickSound(0);
                    if (sel) cardFilterTypes.Remove(tp);
                    else cardFilterTypes.Add(tp);
                    currentPage = 0;
                }
            }
            // Botón 'Todos' en la celda 12 de la cuadrícula 6x2
            Rect clearTypeRect = new Rect(x + 5 * iconStep + 2f, y + (iconSize + 6f) + 3f, iconStep - 4f, iconSize - 6f);
            if (ui.DrawNativeButton(clearTypeRect, Loc.T("Todos", "All"), 12f,
                !anyType ? GameUI.sBtnGoldN : GameUI.sBtnBlueN,
                !anyType ? GameUI.sBtnGoldH : GameUI.sBtnBlueH,
                !anyType ? GameUI.sBtnGoldA : GameUI.sBtnBlueA))
            {
                GameUI.PlayClickSound(0);
                cardFilterTypes.Clear();
                currentPage = 0;
            }
            y += (iconSize + 6f) * 2f + 8f;

            // 3. TIPO DE CARTA (Todas, Pokémon, Entrenador, Energía, Herramienta, Estadio)
            ui.DrawNativeLabel(new Rect(x, y, w, 22f), Loc.T("TIPO DE CARTA", "CARD TYPE"), 14f, TextAlignmentOptions.Left, new Color(0.78f, 0.88f, 1f, 1f), autoSize: true);
            y += 24f;
            string[] catLabels =
            {
                Loc.T("Todas", "All"),
                Loc.T("Pokémon", "Pokémon"),
                Loc.T("Entrenador", "Trainer"),
                Loc.T("Energía", "Energy"),
                Loc.T("Herramienta", "Tool"),
                Loc.T("Estadio", "Stadium")
            };
            float catW = (w - 12f) / 3f;
            for (int i = 0; i < catLabels.Length; i++)
            {
                int c = i % 3;
                int r = i / 3;
                bool active = cardCategoryFilter == i;
                if (ui.DrawNativeButton(new Rect(x + c * (catW + 6f), y + r * 34f, catW, 30f), catLabels[i], 13f,
                    active ? GameUI.sBtnGoldN : GameUI.sBtnBlueN,
                    active ? GameUI.sBtnGoldH : GameUI.sBtnBlueH,
                    active ? GameUI.sBtnGoldA : GameUI.sBtnBlueA))
                {
                    GameUI.PlayClickSound(0);
                    cardCategoryFilter = (cardCategoryFilter == i && i != 0) ? 0 : i;
                    currentPage = 0;
                }
            }
            y += 74f;

            // 4. RAREZA / ETAPA POKÉMON (Todas, Básico, Stage 1, Stage 2)
            ui.DrawNativeLabel(new Rect(x, y, w, 22f), Loc.T("RAREZA / ETAPA POKÉMON", "STAGE / RARITY"), 14f, TextAlignmentOptions.Left, new Color(0.78f, 0.88f, 1f, 1f), autoSize: true);
            y += 24f;
            string[] stageIds = { "", "Basic", "Stage 1", "Stage 2" };
            string[] stageLabels = { Loc.T("Todas", "All"), Loc.T("Básico", "Basic"), "Stage 1", "Stage 2" };
            float stgW = (w - 18f) / 4f;
            for (int i = 0; i < stageIds.Length; i++)
            {
                bool active = string.Equals(filterStage, stageIds[i], StringComparison.OrdinalIgnoreCase);
                if (ui.DrawNativeButton(new Rect(x + i * (stgW + 6f), y, stgW, 30f), stageLabels[i], 13f,
                    active ? GameUI.sBtnGoldN : GameUI.sBtnBlueN,
                    active ? GameUI.sBtnGoldH : GameUI.sBtnBlueH,
                    active ? GameUI.sBtnGoldA : GameUI.sBtnBlueA))
                {
                    GameUI.PlayClickSound(0);
                    filterStage = (active && i != 0) ? "" : stageIds[i];
                    currentPage = 0;
                }
            }
            y += 38f;

            // 5. FILTRAR POR EXPANSIÓN
            ui.DrawNativeLabel(new Rect(x, y, w, 22f), Loc.T("FILTRAR POR EXPANSIÓN", "FILTER BY EXPANSION"), 14f, TextAlignmentOptions.Left, new Color(0.78f, 0.88f, 1f, 1f), autoSize: true);
            y += 24f;

            var sets = CardDatabase.Sets;
            int totalSetBtns = sets.Count + 1;
            int setCols = 2;
            float setBtnW = (w - 6f) / setCols;
            float setBtnH = 28f;

            for (int i = 0; i < totalSetBtns; i++)
            {
                int c = i % setCols;
                int r = i / setCols;
                float by = y + r * (setBtnH + 5f);
                if (by + setBtnH > panelRect.yMax - 46f) break;

                string sId = i == 0 ? "" : sets[i - 1].Id;
                string sName = i == 0 ? Loc.T("Todas", "All Sets") : sets[i - 1].Name;
                bool active = string.Equals(filterSetId, sId, StringComparison.OrdinalIgnoreCase);

                if (ui.DrawNativeButton(new Rect(x + c * (setBtnW + 6f), by, setBtnW, setBtnH), sName, 13f,
                    active ? GameUI.sBtnGoldN : GameUI.sBtnBlueN,
                    active ? GameUI.sBtnGoldH : GameUI.sBtnBlueH,
                    active ? GameUI.sBtnGoldA : GameUI.sBtnBlueA))
                {
                    GameUI.PlayClickSound(0);
                    filterSetId = sId;
                    currentPage = 0;
                }
            }

            // Botones inferiores del panel de filtros
            float botY = panelRect.yMax - 42f;
            float halfW = (w - 8f) * 0.5f;
            if (ui.DrawNativeButton(new Rect(x, botY, halfW, 32f), Loc.T("Limpiar Filtros", "Clear Filters"), 13.5f, GameUI.sBtnRedN, GameUI.sBtnRedH, GameUI.sBtnRedA))
            {
                GameUI.PlayClickSound(0);
                ResetAllCardFilters();
            }
            if (ui.DrawNativeButton(new Rect(x + halfW + 8f, botY, halfW, 32f), Loc.T("Ver Mazo Activo", "View Active Deck"), 13.5f, GameUI.sBtnGreenN, GameUI.sBtnGreenH, GameUI.sBtnGreenA))
            {
                GameUI.PlayClickSound(0);
                showAdvancedFilters = false;
            }
        }

        // ==================== Right Panel: Active Deck (full width, no 3D viewer) ====================
        void DrawRightDeckPanel(GameUI ui, Rect panelRect)
        {
            float headerBlockH = 176f;
            Rect statsBox = new Rect(panelRect.x, panelRect.y, panelRect.width, headerBlockH);
            ui.DrawNativeBox(statsBox, GameUI.sPanelSprite, new Color(0.08f, 0.11f, 0.18f, 0.96f));

            float sx = statsBox.x + 12f;
            float sw = statsBox.width - 24f;
            float sy = statsBox.y + 10f;

            // Full-width deck name field
            deckNameEdit = ui.DrawNativeTextField(
                new Rect(sx, sy, sw, 32f),
                "ActiveDeckNameField",
                deckNameEdit,
                "Deck name...",
                15f,
                40);
            sy += 38f;

            RefreshDeckStatsIfNeeded();
            int nBasic = cachedDeckBasic;
            int nEvo = cachedDeckEvo;
            int nTr = cachedDeckTr;
            int nEn = cachedDeckEn;
            string colorCount = cachedDeckErrors.Count == 0 ? "#80ed99" : "#ffd166";

            // Full-width centered card count box
            Rect countBox = new Rect(sx, sy, sw, 78f);
            ui.DrawNativeBox(countBox, GameUI.sSubPanelSprite, new Color(0.11f, 0.16f, 0.25f, 0.94f));
            ui.DrawNativeLabel(
                new Rect(countBox.x + 8f, countBox.y + 4f, countBox.width - 16f, countBox.height - 8f),
                $"Cards: <color={colorCount}>{currentDeck.Count} / {format.DeckSize}</color>\n" +
                $"Basic: {nBasic}  |  Evol.: {nEvo}\n" +
                $"Trainer: {nTr}  |  Energy: {nEn}",
                15f,
                TextAlignmentOptions.Center,
                Color.white,
                autoSize: true,
                minFontSize: 10f);
            sy += 84f;

            // Full-width row of quick deck buttons
            float thirdW = (sw - 12f) / 3f;
            if (ui.DrawNativeButton(new Rect(sx, sy, thirdW, 30f), "Auto-Fill", 13.5f, GameUI.sBtnGoldN, GameUI.sBtnGoldH, GameUI.sBtnGoldA))
            {
                GameUI.PlayClickSound(1);
                AutoFillFromCollection();
            }
            if (ui.DrawNativeButton(new Rect(sx + thirdW + 6f, sy, thirdW, 30f), "Clean", 13.5f, GameUI.sBtnBlueN, GameUI.sBtnBlueH, GameUI.sBtnBlueA))
            {
                GameUI.PlayClickSound(0);
                RemoveInvalidFromDeck();
            }
            if (ui.DrawNativeButton(new Rect(sx + (thirdW + 6f) * 2f, sy, thirdW, 30f), "Clear", 13.5f, GameUI.sBtnRedN, GameUI.sBtnRedH, GameUI.sBtnRedA))
            {
                GameUI.PlayClickSound(2);
                currentDeck.Clear();
                MarkDeckDirty();
                statusMsg = "Deck cleared.";
            }

            // Full-width grouped card list below
            Rect listBox = new Rect(panelRect.x, panelRect.y + headerBlockH + 8f, panelRect.width, panelRect.height - headerBlockH - 8f);
            ui.DrawNativeBox(listBox, GameUI.sPanelSprite, new Color(0.08f, 0.11f, 0.18f, 0.96f));

            if (currentDeck.Count == 0)
            {
                ui.DrawNativeLabel(
                    new Rect(listBox.x + 16f, listBox.y + 24f, listBox.width - 32f, 90f),
                    "Your deck is empty.\nLeft-click any 3D card to add it or open 'Official Decks' on the left sidebar.",
                    14.5f,
                    TextAlignmentOptions.Top,
                    new Color(0.75f, 0.84f, 0.95f, 0.92f),
                    autoSize: true);
                return;
            }

            var groups = currentDeck
                .GroupBy(c => c.Id)
                .OrderBy(g => g.First().Supertype)
                .ThenBy(g => g.First().SetId)
                .ThenBy(g => g.First().Number)
                .ToList();

            float rowH = 31f;
            float availListH = listBox.height - 46f;
            int rowsPerPage = Mathf.Max(4, Mathf.FloorToInt(availListH / (rowH + 3f)));
            int totalListPages = Mathf.Max(1, Mathf.CeilToInt(groups.Count / (float)rowsPerPage));
            deckListPage = Mathf.Clamp(deckListPage, 0, totalListPages - 1);

            if (Event.current.type == EventType.ScrollWheel && listBox.Contains(Event.current.mousePosition))
            {
                if (Event.current.delta.y > 0.05f) deckListPage = Mathf.Min(totalListPages - 1, deckListPage + 1);
                else if (Event.current.delta.y < -0.05f) deckListPage = Mathf.Max(0, deckListPage - 1);
                Event.current.Use();
            }

            int startG = deckListPage * rowsPerPage;
            float ry = listBox.y + 8f;
            for (int i = 0; i < rowsPerPage && (startG + i) < groups.Count; i++)
            {
                var grp = groups[startG + i];
                var c = grp.First();
                int qty = grp.Count();
                int foilQty = grp.Count(x => x.IsFoil);
                string foilTag = foilQty > 0 ? $" <color=#ffe066>({foilQty}★)</color>" : "";
                int owned = Collection.Owned(c);
                bool missingOwned = Collection.Enforced && qty > owned;
                string qtyCol = missingOwned ? "#ff6b6b" : "#80ed99";

                Rect rowRect = new Rect(listBox.x + 8f, ry, listBox.width - 16f, rowH);
                ui.DrawNativeBox(rowRect, GameUI.sSubPanelSprite, new Color(0.12f, 0.18f, 0.28f, 0.94f));

                float btnW = 28f;
                float btnsTotalW = btnW * 3f + 8f;
                ui.DrawNativeLabel(
                    new Rect(rowRect.x + 8f, rowRect.y + 2f, rowRect.width - btnsTotalW - 16f, rowH - 4f),
                    $"<color={qtyCol}>{qty}x</color> {c.Name}{foilTag} <color=#9ecbff>({c.Id})</color>",
                    13.5f,
                    TextAlignmentOptions.Left,
                    Color.white,
                    autoSize: true,
                    minFontSize: 9.5f);

                float bx = rowRect.xMax - btnsTotalW - 4f;
                if (ui.DrawNativeButton(new Rect(bx, rowRect.y + 3f, btnW, rowH - 6f), "?", 13f, GameUI.sBtnBlueN, GameUI.sBtnBlueH, GameUI.sBtnBlueA))
                {
                    GameUI.PlayClickSound(1);
                    OpenInspect3D(c, lastDeckPedestalGuiRect, stage3D.DeckStackWorldPos, stage3D.DeckStackWorldRot, stage3D.DeckStackScale);
                }
                if (ui.DrawNativeButton(new Rect(bx + btnW + 4f, rowRect.y + 3f, btnW, rowH - 6f), "-", 14f, GameUI.sBtnRedN, GameUI.sBtnRedH, GameUI.sBtnRedA))
                {
                    if (RemoveCardFromDeck(c)) GameUI.PlayClickSound(0);
                }
                if (ui.DrawNativeButton(new Rect(bx + (btnW + 4f) * 2f, rowRect.y + 3f, btnW, rowH - 6f), "+", 14f, GameUI.sBtnGreenN, GameUI.sBtnGreenH, GameUI.sBtnGreenA))
                {
                    bool addFoil = foilQty > 0 && Collection.OwnedFoil(c) > foilQty;
                    if (AddCardToDeck(c, -1, isFoil: addFoil)) GameUI.PlayClickSound(0);
                    else GameUI.PlayClickSound(2);
                }

                ry += rowH + 3f;
            }

            // Paginación de la lista del mazo si supera una página
            if (totalListPages > 1)
            {
                float navY = listBox.yMax - 32f;
                if (ui.DrawNativeButton(new Rect(listBox.x + 12f, navY, 86f, 26f), Loc.T("Anterior", "Prev"), 12.5f, GameUI.sBtnBlueN, GameUI.sBtnBlueH, GameUI.sBtnBlueA))
                {
                    GameUI.PlayClickSound(0);
                    deckListPage = Mathf.Max(0, deckListPage - 1);
                }
                ui.DrawNativeLabel(new Rect(listBox.center.x - 60f, navY, 120f, 26f), $"{deckListPage + 1} / {totalListPages}", 13f, TextAlignmentOptions.Center, Color.white, autoSize: true);
                if (ui.DrawNativeButton(new Rect(listBox.xMax - 98f, navY, 86f, 26f), Loc.T("Siguiente", "Next"), 12.5f, GameUI.sBtnBlueN, GameUI.sBtnBlueH, GameUI.sBtnBlueA))
                {
                    GameUI.PlayClickSound(0);
                    deckListPage = Mathf.Min(totalListPages - 1, deckListPage + 1);
                }
            }
        }

        // ==================== Modal Nativo: Mazos Temáticos Oficiales (Sin Emojis, Fuente del Juego) ====================
        void DrawThemeDecksModalNative(GameUI ui)
        {
            ui.DrawNativeBox(new Rect(0f, 0f, Screen.width, Screen.height), GameUI.sDarkBackdropSprite, new Color(0.02f, 0.03f, 0.06f, 0.84f), sliced: false);

            float mw = Mathf.Min(Screen.width - 80f, 1260f);
            float mh = Mathf.Min(Screen.height - 70f, 780f);
            Rect mRect = new Rect((Screen.width - mw) * 0.5f, (Screen.height - mh) * 0.5f, mw, mh);
            ui.DrawNativeBox(mRect, GameUI.sPanelSprite, Color.white);

            float x = mRect.x + 20f;
            float w = mRect.width - 40f;
            float y = mRect.y + 14f;

            ui.DrawNativeLabel(
                new Rect(x, y, w - 130f, 34f),
                Loc.T($"Mazos Temáticos Oficiales ({ThemeDeckRegistry.Decks.Count}) — Jugador y Clientes NPC", $"Official Theme Decks ({ThemeDeckRegistry.Decks.Count}) — Player & NPCs"),
                21f,
                TextAlignmentOptions.Left,
                new Color(1f, 0.90f, 0.50f, 1f),
                autoSize: true);

            if (ui.DrawNativeButton(new Rect(mRect.xMax - 130f, y, 110f, 34f), Loc.T("Cerrar", "Close"), 15f, GameUI.sBtnRedN, GameUI.sBtnRedH, GameUI.sBtnRedA))
            {
                GameUI.PlayClickSound(2);
                showThemeDecksModal = false;
                return;
            }
            y += 42f;

            // Selector de modo de mazo para los NPCs
            string npcMode = Plugin.NpcDeckMode?.Value ?? "ThemeOrRandom";
            Rect npcBar = new Rect(x, y, w, 40f);
            ui.DrawNativeBox(npcBar, GameUI.sSubPanelSprite, Color.white);
            ui.DrawNativeLabel(new Rect(x + 12f, y + 6f, 220f, 28f), Loc.T("Mazos de los Clientes (NPCs):", "Customer (NPC) Decks:"), 14.5f, TextAlignmentOptions.Left, Color.white, autoSize: true);

            float nx = x + 238f;
            if (DrawFilterPill(ui, new Rect(nx, y + 5f, 190f, 30f), Loc.T("Oficiales + Aleatorios", "Official + Random"), string.Equals(npcMode, "ThemeOrRandom", StringComparison.OrdinalIgnoreCase)))
                if (Plugin.NpcDeckMode != null) Plugin.NpcDeckMode.Value = "ThemeOrRandom";
            nx += 198f;
            if (DrawFilterPill(ui, new Rect(nx, y + 5f, 190f, 30f), Loc.T("Solo Oficiales", "Official Only"), string.Equals(npcMode, "ThemeOnly", StringComparison.OrdinalIgnoreCase)))
                if (Plugin.NpcDeckMode != null) Plugin.NpcDeckMode.Value = "ThemeOnly";
            nx += 198f;
            if (DrawFilterPill(ui, new Rect(nx, y + 5f, 170f, 30f), Loc.T("Solo Aleatorios", "Random Only"), string.Equals(npcMode, "RandomOnly", StringComparison.OrdinalIgnoreCase)))
                if (Plugin.NpcDeckMode != null) Plugin.NpcDeckMode.Value = "RandomOnly";
            y += 46f;

            // Filtro por expansión de los mazos oficiales
            Rect setBar = new Rect(x, y, w, 40f);
            ui.DrawNativeBox(setBar, GameUI.sSubPanelSprite, Color.white);
            float sx = x + 10f;
            if (DrawFilterPill(ui, new Rect(sx, y + 5f, 110f, 30f), Loc.T("Todas", "All"), string.IsNullOrEmpty(themeFilterSetId)))
            {
                themeFilterSetId = "";
                themeDecksPage = 0;
            }
            sx += 116f;
            foreach (var grp in ThemeDeckRegistry.Decks.GroupBy(d => d.SetId))
            {
                string sName = grp.First().SetName ?? grp.Key;
                float bw = Mathf.Clamp(sName.Length * 9.5f + 36f, 110f, 165f);
                if (sx + bw > mRect.xMax - 24f) break;
                if (DrawFilterPill(ui, new Rect(sx, y + 5f, bw, 30f), $"{sName} ({grp.Count()})", string.Equals(themeFilterSetId, grp.Key, StringComparison.OrdinalIgnoreCase)))
                {
                    themeFilterSetId = grp.Key;
                    themeDecksPage = 0;
                }
                sx += bw + 6f;
            }
            y += 48f;

            var list = ThemeDeckRegistry.Decks
                .Where(d => string.IsNullOrEmpty(themeFilterSetId) || string.Equals(d.SetId, themeFilterSetId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            float rowH = 66f;
            float availH = mRect.yMax - y - 50f;
            int perPage = Mathf.Max(4, Mathf.FloorToInt(availH / (rowH + 6f)));
            int totalPages = Mathf.Max(1, Mathf.CeilToInt(list.Count / (float)perPage));
            themeDecksPage = Mathf.Clamp(themeDecksPage, 0, totalPages - 1);

            int start = themeDecksPage * perPage;
            for (int i = 0; i < perPage && (start + i) < list.Count; i++)
            {
                var td = list[start + i];
                int ownedCount = td.CountOwnedInCollection(out int totalNeeded);
                bool hasAll = ownedCount >= totalNeeded;
                string ownedColor = hasAll ? "#80ed99" : "#ffd166";
                bool isNpcFixed = string.Equals(npcMode, td.Id, StringComparison.OrdinalIgnoreCase);

                Rect row = new Rect(x, y + i * (rowH + 6f), w, rowH);
                ui.DrawNativeBox(row, GameUI.sSubPanelSprite, new Color(0.11f, 0.16f, 0.25f, 0.96f));

                // Iconos de tipo del mazo oficial
                float ix = row.x + 12f;
                if (td.Types != null)
                {
                    for (int t = 0; t < Mathf.Min(2, td.Types.Count); t++)
                    {
                        var tex = SpriteCache.GetTypeIcon(td.Types[t]);
                        if (tex != null)
                        {
                            ui.DrawNativeRawTex(new Rect(ix, row.y + 16f, 32f, 32f), tex, Color.white);
                            ix += 36f;
                        }
                    }
                }

                string summary = string.Join(", ", td.Cards.Take(5).Select(c => $"{c.Count}x {c.Name}"));
                ui.DrawNativeLabel(
                    new Rect(ix + 6f, row.y + 6f, row.width - 590f, rowH - 12f),
                    $"{td.Name}  <color=#9ecbff>[{td.SetName}]</color>\n<color=#c8d6e8>{summary}</color>",
                    14.5f,
                    TextAlignmentOptions.Left,
                    Color.white,
                    autoSize: true);

                ui.DrawNativeLabel(
                    new Rect(row.xMax - 485f, row.y + 10f, 130f, rowH - 20f),
                    $"Collection:\n<color={ownedColor}>{ownedCount} / {totalNeeded}</color>",
                    13.5f,
                    TextAlignmentOptions.Center,
                    Color.white,
                    autoSize: true);

                if (ui.DrawNativeButton(new Rect(row.xMax - 348f, row.y + 14f, 110f, 38f), Loc.T("Cargar", "Load"), 14f, GameUI.sBtnBlueN, GameUI.sBtnBlueH, GameUI.sBtnBlueA))
                {
                    GameUI.PlayClickSound(1);
                    var d = td.ToDeckDef(UniqueName($"{td.Name} ({td.SetName})"));
                    LoadDeck(d);
                    lastImportReport = PtcgoImporter.AnalyzeDeck(d.Name, currentDeck);
                    showThemeDecksModal = false;
                    inDecksView = false;
                    statusMsg = $"<color=#80ed99>Official deck '{td.Name}' loaded in the editor.</color>";
                }

                if (ui.DrawNativeButton(new Rect(row.xMax - 232f, row.y + 14f, 110f, 38f), Loc.T("Guardar", "Save & Use"), 14f, GameUI.sBtnGreenN, GameUI.sBtnGreenH, GameUI.sBtnGreenA))
                {
                    GameUI.PlayClickSound(1);
                    var d = td.ToDeckDef(UniqueName($"{td.Name} ({td.SetName})"));
                    DeckStore.Save(d);
                    InvalidateDeckSummaries();
                    deckNames = DeckStore.List();
                    LoadDeck(d);
                    if (DeckValidator.Validate(currentDeck, format).Count == 0)
                        DeckStore.SetActive(d.Name);
                    showThemeDecksModal = false;
                    inDecksView = false;
                    statusMsg = $"<color=#80ed99>Official deck '{d.Name}' saved.</color>";
                }

                if (ui.DrawNativeButton(new Rect(row.xMax - 116f, row.y + 14f, 106f, 38f), isNpcFixed ? Loc.T("NPC Fijado", "NPC Set") : Loc.T("Dar a NPC", "Set NPC"), 13.5f,
                    isNpcFixed ? GameUI.sBtnGoldN : GameUI.sBtnBlueN,
                    isNpcFixed ? GameUI.sBtnGoldH : GameUI.sBtnBlueH,
                    isNpcFixed ? GameUI.sBtnGoldA : GameUI.sBtnBlueA))
                {
                    GameUI.PlayClickSound(1);
                    if (Plugin.NpcDeckMode != null)
                        Plugin.NpcDeckMode.Value = isNpcFixed ? "ThemeOrRandom" : td.Id;
                }
            }

            if (totalPages > 1)
            {
                float navY = mRect.yMax - 42f;
                if (ui.DrawNativeButton(new Rect(mRect.center.x - 180f, navY, 105f, 30f), Loc.T("Anterior", "Previous"), 13.5f, GameUI.sBtnBlueN, GameUI.sBtnBlueH, GameUI.sBtnBlueA))
                {
                    GameUI.PlayClickSound(0);
                    themeDecksPage = Mathf.Max(0, themeDecksPage - 1);
                }
                ui.DrawNativeLabel(new Rect(mRect.center.x - 65f, navY, 130f, 30f), $"{themeDecksPage + 1} / {totalPages}", 15f, TextAlignmentOptions.Center, Color.white, autoSize: true);
                if (ui.DrawNativeButton(new Rect(mRect.center.x + 75f, navY, 105f, 30f), Loc.T("Siguiente", "Next"), 13.5f, GameUI.sBtnBlueN, GameUI.sBtnBlueH, GameUI.sBtnBlueA))
                {
                    GameUI.PlayClickSound(0);
                    themeDecksPage = Mathf.Min(totalPages - 1, themeDecksPage + 1);
                }
            }
        }

        // ==================== Modal Nativo: Importar / Exportar (.txt y Portapapeles) ====================
        void DrawImportModalNative(GameUI ui)
        {
            ui.DrawNativeBox(new Rect(0f, 0f, Screen.width, Screen.height), GameUI.sDarkBackdropSprite, new Color(0.02f, 0.03f, 0.06f, 0.84f), sliced: false);

            float mw = Mathf.Min(Screen.width - 80f, 1260f);
            float mh = Mathf.Min(Screen.height - 70f, 760f);
            Rect mRect = new Rect((Screen.width - mw) * 0.5f, (Screen.height - mh) * 0.5f, mw, mh);
            ui.DrawNativeBox(mRect, GameUI.sPanelSprite, Color.white);

            float x = mRect.x + 20f;
            float w = mRect.width - 40f;
            float y = mRect.y + 14f;

            ui.DrawNativeLabel(
                new Rect(x, y, 480f, 34f),
                Loc.T("Importar / Exportar Mazo (.txt)", "Import / Export Deck (.txt)"),
                21f,
                TextAlignmentOptions.Left,
                new Color(1f, 0.90f, 0.50f, 1f),
                autoSize: true);

            float bx = mRect.xMax - 640f;
            if (ui.DrawNativeButton(new Rect(bx, y, 165f, 34f), Loc.T("Pegar Portapapeles", "Paste Clipboard"), 14f, GameUI.sBtnGreenN, GameUI.sBtnGreenH, GameUI.sBtnGreenA))
            {
                GameUI.PlayClickSound(1);
                ImportFromClipboard();
            }
            bx += 171f;
            if (ui.DrawNativeButton(new Rect(bx, y, 140f, 34f), Loc.T("Exportar Actual", "Export Current"), 14f, GameUI.sBtnBlueN, GameUI.sBtnBlueH, GameUI.sBtnBlueA))
            {
                GameUI.PlayClickSound(1);
                ExportCurrentDeckToTxt();
            }
            bx += 146f;
            if (ui.DrawNativeButton(new Rect(bx, y, 140f, 34f), Loc.T("Abrir Carpeta", "Open Folder"), 14f, GameUI.sBtnGoldN, GameUI.sBtnGoldH, GameUI.sBtnGoldA))
            {
                GameUI.PlayClickSound(0);
                PtcgoImporter.OpenImportFolder();
            }
            bx += 146f;
            if (ui.DrawNativeButton(new Rect(bx, y, 75f, 34f), Loc.T("Actualizar", "Refresh"), 13.5f, GameUI.sBtnBlueN, GameUI.sBtnBlueH, GameUI.sBtnBlueA))
            {
                GameUI.PlayClickSound(0);
                importTxtFiles = PtcgoImporter.ListTxtFiles();
                Collection.Invalidate();
            }
            bx += 81f;
            if (ui.DrawNativeButton(new Rect(bx, y, 86f, 34f), Loc.T("Cerrar", "Close"), 14f, GameUI.sBtnRedN, GameUI.sBtnRedH, GameUI.sBtnRedA))
            {
                GameUI.PlayClickSound(2);
                showImportModal = false;
                return;
            }
            y += 42f;

            ui.DrawNativeLabel(
                new Rect(x, y, w, 24f),
                $"<color=#9ecbff>Folder:</color> {PtcgoImporter.ImportDir}",
                13.5f,
                TextAlignmentOptions.Left,
                Color.white,
                autoSize: true);
            y += 30f;

            float colH = mRect.yMax - y - 18f;
            float leftW = 360f;
            Rect leftBox = new Rect(x, y, leftW, colH);
            Rect rightBox = new Rect(x + leftW + 14f, y, w - leftW - 14f, colH);

            ui.DrawNativeBox(leftBox, GameUI.sSubPanelSprite, Color.white);
            ui.DrawNativeBox(rightBox, GameUI.sSubPanelSprite, Color.white);

            // Columna izquierda: archivos .txt
            ui.DrawNativeLabel(new Rect(leftBox.x + 12f, leftBox.y + 10f, leftBox.width - 24f, 26f), $".txt Files ({importTxtFiles.Count})", 16f, TextAlignmentOptions.Left, new Color(1f, 0.90f, 0.50f, 1f), autoSize: true);
            if (ui.DrawNativeButton(new Rect(leftBox.x + 12f, leftBox.y + 40f, leftBox.width - 24f, 30f), Loc.T("Analizar mi mazo actual", "Analyze current deck"), 13.5f, GameUI.sBtnBlueN, GameUI.sBtnBlueH, GameUI.sBtnBlueA))
            {
                GameUI.PlayClickSound(0);
                lastImportReport = PtcgoImporter.AnalyzeDeck(deckDef.Name, currentDeck);
                importReportPage = 0;
            }

            float fy = leftBox.y + 78f;
            int fPerPage = Mathf.Max(4, Mathf.FloorToInt((leftBox.height - 118f) / 38f));
            int fTotalPages = Mathf.Max(1, Mathf.CeilToInt(importTxtFiles.Count / (float)fPerPage));
            importFilesPage = Mathf.Clamp(importFilesPage, 0, fTotalPages - 1);

            if (importTxtFiles.Count == 0)
            {
                ui.DrawNativeLabel(
                    new Rect(leftBox.x + 14f, fy + 10f, leftBox.width - 28f, 90f),
                    Loc.T("No hay archivos .txt en la carpeta.\nUsa 'Pegar Portapapeles' o 'Abrir Carpeta'.", "No .txt files found.\nUse 'Paste Clipboard' or 'Open Folder'."),
                    14f,
                    TextAlignmentOptions.TopLeft,
                    new Color(1f, 0.88f, 0.52f, 1f),
                    autoSize: true);
            }
            else
            {
                int fStart = importFilesPage * fPerPage;
                for (int i = 0; i < fPerPage && (fStart + i) < importTxtFiles.Count; i++)
                {
                    string file = importTxtFiles[fStart + i];
                    string fname = Path.GetFileName(file);
                    Rect fr = new Rect(leftBox.x + 10f, fy + i * 38f, leftBox.width - 20f, 34f);
                    ui.DrawNativeBox(fr, GameUI.sPanelSprite, new Color(0.08f, 0.12f, 0.20f, 0.94f));
                    ui.DrawNativeLabel(new Rect(fr.x + 8f, fr.y + 2f, fr.width - 108f, 30f), fname, 13.5f, TextAlignmentOptions.Left, Color.white, autoSize: true);
                    if (ui.DrawNativeButton(new Rect(fr.xMax - 94f, fr.y + 3f, 88f, 28f), Loc.T("Importar", "Import"), 13f, GameUI.sBtnGreenN, GameUI.sBtnGreenH, GameUI.sBtnGreenA))
                    {
                        GameUI.PlayClickSound(1);
                        ImportTxtFile(file);
                    }
                }
            }

            // Columna derecha: reporte de cartas e información de sobres
            if (lastImportReport == null)
            {
                ui.DrawNativeLabel(
                    new Rect(rightBox.x + 18f, rightBox.y + 24f, rightBox.width - 36f, 80f),
                    Loc.T("Selecciona un archivo .txt a la izquierda o pega desde el portapapeles para importar un mazo y verificar tu colección.", "Select a .txt file on the left or paste from clipboard to import a deck and check your collection."),
                    15f,
                    TextAlignmentOptions.TopLeft,
                    new Color(0.82f, 0.90f, 1f, 1f),
                    autoSize: true);
            }
            else
            {
                int totalMissing = lastImportReport.MissingOrUnavailable.Sum(m => m.Missing);
                string headerStatus = totalMissing == 0
                    ? "<color=#80ed99>You own every card in this deck</color>"
                    : $"<color=#ffd166>Missing {totalMissing} copy(ies) in your collection</color>";

                ui.DrawNativeLabel(
                    new Rect(rightBox.x + 14f, rightBox.y + 10f, rightBox.width - 28f, 44f),
                    $"Report: {lastImportReport.FileName} ({lastImportReport.TotalRequestedCards} cards)\n{headerStatus}",
                    15f,
                    TextAlignmentOptions.TopLeft,
                    Color.white,
                    autoSize: true);

                var items = lastImportReport.AllParsedItems;
                float ry = rightBox.y + 60f;
                int rPerPage = Mathf.Max(4, Mathf.FloorToInt((rightBox.height - 104f) / 46f));
                int rTotalPages = Mathf.Max(1, Mathf.CeilToInt(items.Count / (float)rPerPage));
                importReportPage = Mathf.Clamp(importReportPage, 0, rTotalPages - 1);

                int rStart = importReportPage * rPerPage;
                for (int i = 0; i < rPerPage && (rStart + i) < items.Count; i++)
                {
                    var item = items[rStart + i];
                    bool ok = item.Owned >= item.Wanted;
                    string ownedStr = item.Owned == int.MaxValue ? "99" : item.Owned.ToString();
                    string badge = ok
                        ? $"<color=#80ed99>Owned {ownedStr}/{item.Wanted}</color>"
                        : $"<color=#ff7b7b>Missing {item.Missing} (owned {ownedStr}/{item.Wanted})</color>";

                    Rect ir = new Rect(rightBox.x + 12f, ry + i * 46f, rightBox.width - 24f, 42f);
                    ui.DrawNativeBox(ir, GameUI.sPanelSprite, new Color(0.08f, 0.12f, 0.20f, 0.94f));
                    ui.DrawNativeLabel(
                        new Rect(ir.x + 10f, ir.y + 3f, ir.width - 20f, 36f),
                        $"{item.Wanted}x {item.CardName}   —   {badge}\n<color=#9ecbff>Expansion: {item.ExpansionWhereToFind}</color>",
                        12.5f,
                        TextAlignmentOptions.Left,
                        Color.white,
                        autoSize: true);
                }

                if (rTotalPages > 1)
                {
                    float navY = rightBox.yMax - 36f;
                    if (ui.DrawNativeButton(new Rect(rightBox.center.x - 160f, navY, 95f, 28f), Loc.T("Anterior", "Prev"), 13f, GameUI.sBtnBlueN, GameUI.sBtnBlueH, GameUI.sBtnBlueA))
                    {
                        GameUI.PlayClickSound(0);
                        importReportPage = Mathf.Max(0, importReportPage - 1);
                    }
                    ui.DrawNativeLabel(new Rect(rightBox.center.x - 55f, navY, 110f, 28f), $"{importReportPage + 1} / {rTotalPages}", 14f, TextAlignmentOptions.Center, Color.white, autoSize: true);
                    if (ui.DrawNativeButton(new Rect(rightBox.center.x + 65f, navY, 95f, 28f), Loc.T("Siguiente", "Next"), 13f, GameUI.sBtnBlueN, GameUI.sBtnBlueH, GameUI.sBtnBlueA))
                    {
                        GameUI.PlayClickSound(0);
                        importReportPage = Mathf.Min(rTotalPages - 1, importReportPage + 1);
                    }
                }
            }
        }

        static bool DrawFilterPill(GameUI ui, Rect rect, string text, bool active)
        {
            bool clicked = ui.DrawNativeButton(
                rect,
                text,
                13.5f,
                active ? GameUI.sBtnGoldN : GameUI.sBtnBlueN,
                active ? GameUI.sBtnGoldH : GameUI.sBtnBlueH,
                active ? GameUI.sBtnGoldA : GameUI.sBtnBlueA);
            if (clicked) GameUI.PlayClickSound(0);
            return clicked;
        }

        void ExportCurrentDeckToTxt()
        {
            if (currentDeck.Count == 0)
            {
                statusMsg = "<color=#ffd166>The deck is empty; add cards before exporting to .txt.</color>";
                return;
            }
            try
            {
                string name = string.IsNullOrWhiteSpace(deckNameEdit) ? deckDef.Name : deckNameEdit.Trim();
                string outPath = PtcgoImporter.ExportToFile(name, currentDeck);
                importTxtFiles = PtcgoImporter.ListTxtFiles();
                statusMsg = $"<color=#80ed99>Exported '{Path.GetFileName(outPath)}' and copied to clipboard.</color>";
            }
            catch (Exception e)
            {
                statusMsg = "<color=#ff6b6b>.txt export error: " + e.Message + "</color>";
            }
        }

        void ImportFromClipboard()
        {
            string clip = "";
            try { clip = GUIUtility.systemCopyBuffer ?? ""; } catch { }
            if (string.IsNullOrWhiteSpace(clip))
            {
                statusMsg = "<color=#ffd166>Clipboard is empty. Copy a PTCGO / PTCG Live deck list first.</color>";
                return;
            }

            lastImportReport = PtcgoImporter.ImportFromText("Clipboard Deck", clip);
            importReportPage = 0;
            if (lastImportReport.AllParsedItems.Count == 0)
            {
                statusMsg = "<color=#ff6b6b>No valid deck lines detected in the clipboard.</color>";
                return;
            }

            currentDeck.Clear();
            currentDeck.AddRange(lastImportReport.ResolvedPlayableCards);
            deckDef = new DeckDef { Name = UniqueName(lastImportReport.SuggestedDeckName), Format = format.Id };
            deckNameEdit = deckDef.Name;
            deckDef.SetCards(currentDeck);
            DeckStore.Save(deckDef);
            InvalidateDeckSummaries();
            MarkDeckDirty();
            deckNames = DeckStore.List();

            int miss = lastImportReport.MissingOrUnavailable.Sum(x => x.Missing);
            statusMsg = miss == 0
                ? $"<color=#80ed99>Deck '{deckDef.Name}' imported from clipboard ({currentDeck.Count} cards).</color>"
                : $"<color=#ffd166>Deck '{deckDef.Name}' imported with {miss} missing copy(ies).</color>";
        }

        void ImportTxtFile(string filePath)
        {
            lastImportReport = PtcgoImporter.ImportFromFile(filePath);
            importReportPage = 0;
            currentDeck.Clear();
            currentDeck.AddRange(lastImportReport.ResolvedPlayableCards);
            string baseName = string.IsNullOrWhiteSpace(lastImportReport.SuggestedDeckName) ? "Imported Deck" : lastImportReport.SuggestedDeckName;
            deckDef = new DeckDef { Name = UniqueName(baseName), Format = format.Id };
            deckNameEdit = deckDef.Name;
            deckDef.SetCards(currentDeck);
            DeckStore.Save(deckDef);
            InvalidateDeckSummaries();
            MarkDeckDirty();
            deckNames = DeckStore.List();

            int miss = lastImportReport.MissingOrUnavailable.Sum(x => x.Missing);
            if (miss == 0 && lastImportReport.MissingOrUnavailable.Count == 0)
                statusMsg = $"<color=#80ed99>Deck '{deckDef.Name}' imported and saved ({currentDeck.Count} cards).</color>";
            else
                statusMsg = $"<color=#ffd166>Deck '{deckDef.Name}' imported: check the report for missing cards.</color>";
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
            MarkDeckDirty();
            statusMsg = removed > 0
                ? $"<color=#80ed99>Removed {removed} cards exceeding your collection or limits ({currentDeck.Count}/{format.DeckSize}).</color>"
                : "All current deck cards are in your collection.";
        }

        void AutoFillFromCollection()
        {
            RemoveInvalidFromDeck();
            var pool = CardDatabase.All.Where(c => c.IsPlayable && format.Allows(c) && Collection.Owned(c) > 0).ToList();

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
            MarkDeckDirty();

            if (currentDeck.Count == format.DeckSize && currentDeck.Any(c => c.IsBasicPokemon))
                statusMsg = "<color=#80ed99>Deck filled to 60 cards from your collection. Press 'Save Deck'.</color>";
            else
                statusMsg = "<color=#ffd166>Open at least 1 booster pack to get a Basic Pokémon in your collection.</color>";
        }
    }
}
