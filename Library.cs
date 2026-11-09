// PokeTCGPlay: Collection scanner across binder, store shelves and storage boxes
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using EnhancedPrefabLoader.API;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PokeTCGPlay
{
    // ───────────────────────────── Formatos ─────────────────────────────

    /// <summary>Formato de juego definido en Data/formats/*.json.</summary>
    public class FormatDef
    {
        public string Id = "unlimited";
        public string Name = "Unlimited";
        public List<string> Sets = new List<string> { "*" };   // "*" = todas
        public List<string> Banned = new List<string>();        // ids o nombres
        public int DeckSize = 60;
        public int MaxCopies = 4;
        public int MinBasicPokemon = 1;
        public string Description = "";

        public bool Allows(CardDef c) =>
            (Sets.Contains("*") || Sets.Any(s => string.Equals(s, c.SetId, StringComparison.OrdinalIgnoreCase))) &&
            !Banned.Any(b => string.Equals(b, c.Id, StringComparison.OrdinalIgnoreCase) || string.Equals(b, c.Name, StringComparison.OrdinalIgnoreCase));
        public override string ToString() => Name;
    }

    public static class FormatRegistry
    {
        public static readonly List<FormatDef> Formats = new List<FormatDef>();
        public static FormatDef Default => Get(Plugin.DefaultFormat?.Value) ?? Formats.FirstOrDefault() ?? new FormatDef();

        public static FormatDef Get(string id) =>
            string.IsNullOrEmpty(id) ? null : Formats.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));

        public static void Load(string dataDir)
        {
            Formats.Clear();
            string dir = Path.Combine(dataDir, "formats");
            if (Directory.Exists(dir))
                foreach (var f in Directory.GetFiles(dir, "*.json"))
                {
                    try
                    {
                        var fd = JsonConvert.DeserializeObject<FormatDef>(File.ReadAllText(f, Encoding.UTF8));
                        if (fd != null) Formats.Add(fd);
                    }
                    catch (Exception e) { Plugin.Log?.LogWarning($"Formato inválido {Path.GetFileName(f)}: {e.Message}"); }
                }
            if (Formats.Count == 0) Formats.Add(new FormatDef());
            string[] order = { "base_era", "base_fossil", "base1", "unlimited" };
            Formats.Sort((a, b) =>
            {
                int ia = Array.FindIndex(order, x => string.Equals(x, a.Id, StringComparison.OrdinalIgnoreCase));
                int ib = Array.FindIndex(order, x => string.Equals(x, b.Id, StringComparison.OrdinalIgnoreCase));
                if (ia < 0) ia = 99;
                if (ib < 0) ib = 99;
                return ia != ib ? ia.CompareTo(ib) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
        }
    }

    // ───────────────────────────── Colección ─────────────────────────────

    /// <summary>Lee cuántas copias de cada carta tiene el jugador en el álbum y muebles de almacenamiento del juego (compatible con variantes MyCurio y EPL 6.7.1).</summary>
    public static class Collection
    {
        static readonly Dictionary<string, List<KeyValuePair<ECardExpansionType, EMonsterType>>> spriteMap =
            new Dictionary<string, List<KeyValuePair<ECardExpansionType, EMonsterType>>>(StringComparer.OrdinalIgnoreCase);
        static readonly ECardBorderType[] allBorderTypes = (ECardBorderType[])Enum.GetValues(typeof(ECardBorderType));
        static readonly Dictionary<string, int> cache = new Dictionary<string, int>();
        static float cacheTime = -999f;
        static bool mapBuilt;

        public static bool Enforced => Plugin.RequireOwnedCards == null || Plugin.RequireOwnedCards.Value;
        public static bool FreeBasicEnergy => Plugin.FreeBasicEnergy == null || Plugin.FreeBasicEnergy.Value;

        public static void Invalidate() { cache.Clear(); cacheTime = -999f; }

        public static bool TryParseExpansion(string exKey, out ECardExpansionType exp)
        {
            exp = default;
            if (string.IsNullOrWhiteSpace(exKey)) return false;
            if (Enum.TryParse(exKey, true, out exp)) return true;
            string sanitized = System.Text.RegularExpressions.Regex.Replace(
                System.Text.RegularExpressions.Regex.Replace(exKey, @"\s", ""), @"[^\w]", "_");
            return Enum.TryParse(sanitized, true, out exp);
        }

        static void BuildMap()
        {
            if (mapBuilt) return;
            if (!Epl.IsAvailable || Epl.Api == null || !Epl.Api.IsBundleLoadingComplete) return;
            mapBuilt = true;
            var allExpKeys = CardDatabase.Sets.Select(s => s.MyCurioExpansion)
                .Concat(MyCurioIndex.Expansions.Keys)
                .Where(x => MyCurioIndex.IsPokemonExpansion(x))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            int mappedSlots = 0;
            foreach (var exKey in allExpKeys)
            {
                try
                {
                    if (!TryParseExpansion(exKey, out var exp))
                        continue;
                    var dict = Epl.Api.Assets.CardExpansions.GetCardSprites(exp);
                    if (dict == null) continue;
                    foreach (var kv in dict)
                    {
                        string assetName = kv.Value?.AssetName;
                        if (string.IsNullOrEmpty(assetName)) continue;
                        if (!spriteMap.TryGetValue(assetName, out var list))
                        {
                            list = new List<KeyValuePair<ECardExpansionType, EMonsterType>>(2);
                            spriteMap[assetName] = list;
                        }
                        var pair = new KeyValuePair<ECardExpansionType, EMonsterType>(exp, kv.Key);
                        if (!list.Contains(pair))
                        {
                            list.Add(pair);
                            mappedSlots++;
                        }
                    }
                }
                catch (Exception e) { Plugin.Log?.LogWarning($"Colección: no se pudo mapear {exKey}: {e.Message}"); }
            }
            Plugin.Log?.LogInfo($"Colección: {spriteMap.Count} sprites Pokémon ({mappedSlots} ranuras MyCurio) mapeados al álbum del juego.");
        }

        /// <summary>Copias que posee el jugador (int.MaxValue si no aplica la restricción).</summary>
        public static int Owned(CardDef c)
        {
            if (c == null) return 0;
            if (!Enforced || (FreeBasicEnergy && c.IsBasicEnergy)) return int.MaxValue;
            return ActualOwnedCount(c);
        }

        /// <summary>Cantidad real de copias físicas en el álbum y estantes del jugador.</summary>
        public static int ActualOwnedCount(CardDef c)
        {
            if (c == null) return 0;
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now - cacheTime > 3f) { cache.Clear(); cacheTime = now; }
            if (cache.TryGetValue(c.Id, out int v)) return v;
            v = ReadOwnedBySprite(c.Sprite, c.MyCurioBorder, c.MyCurioFoil);
            cache[c.Id] = v;
            return v;
        }

        /// <summary>Indica si la carta cuenta como obtenida para filtros y visualización a color.</summary>
        public static bool HasCard(CardDef c)
        {
            if (c == null) return false;
            if (!Enforced || (FreeBasicEnergy && c.IsBasicEnergy)) return true;
            return ActualOwnedCount(c) > 0;
        }

        /// <summary>Cantidad numérica a mostrar en el contador superior derecho de la carta (x0, x3, x10).</summary>
        public static int DisplayOwnedCount(CardDef c)
        {
            if (c == null) return 0;
            int actual = ActualOwnedCount(c);
            if (actual > 0) return actual;
            if (!Enforced || (FreeBasicEnergy && c.IsBasicEnergy)) return 99;
            return 0;
        }

        public static int ReadOwnedBySprite(string sprite, string borderName = "Base", bool isFoil = false)
        {
            try
            {
                BuildMap();
                if (string.IsNullOrEmpty(sprite) || !spriteMap.TryGetValue(sprite, out var entries) || entries.Count == 0)
                    return 0;

                int total = 0;
                var seenSaveKeys = new HashSet<long>();
                var storageShelves = ShelfManager.GetCardStorageShelfList();
                var cardShelves = ShelfManager.GetCardShelfList();

                for (int eIdx = 0; eIdx < entries.Count; eIdx++)
                {
                    var key = entries[eIdx];
                    var collectedList = CPlayerData.GetCardCollectedList(key.Key, false);

                    // Contar todas las variantes de borde (Base, FirstEdition, Silver, Gold, EX, FullArt) y foil/no-foil
                    for (int bIdx = 0; bIdx < allBorderTypes.Length; bIdx++)
                    {
                        var border = allBorderTypes[bIdx];
                        for (int f = 0; f < 2; f++)
                        {
                            var variant = new CardData
                            {
                                expansionType = key.Key,
                                monsterType = key.Value,
                                borderType = border,
                                isFoil = (f == 1)
                            };

                            int saveIdx = CPlayerData.GetCardSaveIndex(variant);
                            if (saveIdx < 0) continue;
                            long compositeKey = ((long)(int)key.Key << 32) | (uint)saveIdx;
                            if (!seenSaveKeys.Add(compositeKey)) continue;

                            // 1) En el Álbum (Colección del jugador)
                            if (collectedList != null && saveIdx < collectedList.Count)
                                total += Math.Max(0, collectedList[saveIdx]);

                            // 2) En Muebles de Almacenamiento de Cartas (InteractableCardStorageShelf / Bulk Box / DiamondStorage)
                            if (storageShelves != null)
                            {
                                for (int i = 0; i < storageShelves.Count; i++)
                                {
                                    var sh = storageShelves[i];
                                    var compactList = sh != null ? sh.GetCompactCardDataAmountList() : null;
                                    if (compactList == null) continue;
                                    for (int j = 0; j < compactList.Count; j++)
                                    {
                                        var item = compactList[j];
                                        if (item != null && item.expansionType == key.Key && item.cardSaveIndex == saveIdx)
                                            total += Math.Max(0, item.amount);
                                    }
                                }
                            }
                        }
                    }

                    // 3) En Estantes / Expositores de Cartas de la tienda (CardShelf)
                    if (cardShelves != null)
                    {
                        for (int i = 0; i < cardShelves.Count; i++)
                        {
                            var comps = cardShelves[i]?.GetCardCompartmentList();
                            if (comps == null) continue;
                            for (int j = 0; j < comps.Count; j++)
                            {
                                var stored = comps[j]?.m_StoredCardList;
                                if (stored == null) continue;
                                for (int k = 0; k < stored.Count; k++)
                                {
                                    var d = stored[k]?.m_Card3dUI?.m_CardUI?.GetCardData();
                                    if (d != null && d.expansionType == key.Key && d.monsterType == key.Value)
                                        total++;
                                }
                            }
                        }
                    }
                }

                return total;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning($"ReadOwnedBySprite({sprite}): {e.Message}");
                return 0;
            }
        }
    }

    // ───────────────────────────── Mazos ─────────────────────────────

    public class DeckEntry { public string Id; public string Name; public int Count; }

    /// <summary>Mazo guardado en BepInEx/config/PokeTCGPlay/decks/&lt;nombre&gt;.deck.json.</summary>
    public class DeckDef
    {
        public string Name = "New Deck";
        public string Format = "unlimited";
        public List<DeckEntry> Cards = new List<DeckEntry>();

        public List<CardDef> Resolve(List<string> missing = null)
        {
            var list = new List<CardDef>();
            foreach (var e in Cards)
            {
                if (!CardDatabase.ById.TryGetValue(e.Id ?? "", out var c)) { missing?.Add(e.Id); continue; }
                for (int i = 0; i < e.Count; i++) list.Add(c);
            }
            return list;
        }

        public void SetCards(IEnumerable<CardDef> cards) =>
            Cards = cards.GroupBy(c => c.Id).Select(g => new DeckEntry { Id = g.Key, Count = g.Count() }).ToList();
    }

    public static class DeckStore
    {
        public static string Dir => Path.Combine(BepInEx.Paths.ConfigPath, "PokeTCGPlay", "decks");

        static string FileFor(string name)
        {
            foreach (var ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
            return Path.Combine(Dir, name.Trim() + ".deck.json");
        }

        public static List<string> List()
        {
            if (!Directory.Exists(Dir)) return new List<string>();
            return Directory.GetFiles(Dir, "*.deck.json")
                .Select(f => { try { return JObject.Parse(File.ReadAllText(f, Encoding.UTF8))["Name"]?.ToString(); } catch { return null; } })
                .Where(n => !string.IsNullOrEmpty(n)).OrderBy(n => n).ToList();
        }

        public static DeckDef Load(string name)
        {
            string f = FileFor(name ?? "");
            if (!File.Exists(f)) return null;
            try { return JsonConvert.DeserializeObject<DeckDef>(File.ReadAllText(f, Encoding.UTF8)); }
            catch (Exception e) { Plugin.Log?.LogWarning($"Mazo inválido {name}: {e.Message}"); return null; }
        }

        public static void Save(DeckDef d)
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FileFor(d.Name), JsonConvert.SerializeObject(d, Formatting.Indented), Encoding.UTF8);
        }

        public static void Delete(string name)
        {
            string f = FileFor(name);
            if (File.Exists(f)) File.Delete(f);
        }

        public static bool Rename(string oldName, string newName)
        {
            var d = Load(oldName);
            if (d == null || string.IsNullOrWhiteSpace(newName) || File.Exists(FileFor(newName))) return false;
            Delete(oldName);
            d.Name = newName.Trim();
            Save(d);
            return true;
        }

        public static DeckDef Active
        {
            get
            {
                var d = Load(Plugin.ActiveDeck?.Value);
                if (d != null) return d;
                var first = List().FirstOrDefault();
                return first != null ? Load(first) : null;
            }
        }

        public static void SetActive(string name) { if (Plugin.ActiveDeck != null) Plugin.ActiveDeck.Value = name; }

        /// <summary>Migra el antiguo Data/decks/player.txt al nuevo sistema (solo la primera vez).</summary>
        public static void MigrateLegacy(string dataDir)
        {
            try
            {
                var existing = List();
                if (existing.Any(n => string.Equals(n, "Mazo inicial", StringComparison.OrdinalIgnoreCase)) &&
                    !existing.Any(n => string.Equals(n, "Starter Deck", StringComparison.OrdinalIgnoreCase)))
                {
                    bool wasActive = string.Equals(Plugin.ActiveDeck?.Value, "Mazo inicial", StringComparison.OrdinalIgnoreCase);
                    if (Rename("Mazo inicial", "Starter Deck") && wasActive)
                        SetActive("Starter Deck");
                }
                if (List().Count > 0) return;
                string legacy = Path.Combine(dataDir, "decks", "player.txt");
                if (!File.Exists(legacy)) return;
                var warn = new List<string>();
                var cards = DeckLoader.ParseText(File.ReadAllLines(legacy), warn);
                if (cards.Count == 0) return;
                var d = new DeckDef { Name = "Starter Deck", Format = "base_era" };
                d.SetCards(cards);
                Save(d);
                SetActive(d.Name);
                Plugin.Log?.LogInfo($"Legacy deck migrated to {Dir} ({cards.Count} cards).");
            }
            catch (Exception e) { Plugin.Log?.LogWarning("Deck migration: " + e.Message); }
        }
    }

    // ───────────────────────────── Mazos Temáticos Oficiales (Base Era) ─────────────────────────────

    public class ThemeDeckDef
    {
        public string Id;
        public string Name;
        public string SetId;
        public string SetName;
        public string Format = "base_era";
        public List<string> Types = new List<string>();
        public List<DeckEntry> Cards = new List<DeckEntry>();

        public List<CardDef> Resolve(List<string> missing = null)
        {
            var list = new List<CardDef>();
            foreach (var e in Cards)
            {
                if (!CardDatabase.ById.TryGetValue(e.Id ?? "", out var c))
                {
                    missing?.Add(e.Id);
                    continue;
                }
                for (int i = 0; i < e.Count; i++) list.Add(c);
            }
            return list;
        }

        public int CountOwnedInCollection(out int totalNeeded)
        {
            totalNeeded = 0;
            int ownedSum = 0;
            foreach (var e in Cards)
            {
                totalNeeded += e.Count;
                if (!CardDatabase.ById.TryGetValue(e.Id ?? "", out var c)) continue;
                int owned = Collection.Owned(c);
                ownedSum += Math.Min(e.Count, owned == int.MaxValue ? e.Count : owned);
            }
            return ownedSum;
        }

        public DeckDef ToDeckDef(string customName = null)
        {
            return new DeckDef
            {
                Name = string.IsNullOrWhiteSpace(customName) ? $"{Name} ({SetName})" : customName.Trim(),
                Format = string.IsNullOrWhiteSpace(Format) ? "base_era" : Format,
                Cards = Cards.Select(c => new DeckEntry { Id = c.Id, Count = c.Count }).ToList()
            };
        }
    }

    public static class ThemeDeckRegistry
    {
        public static readonly List<ThemeDeckDef> Decks = new List<ThemeDeckDef>();

        public static void Load(string dataDir)
        {
            Decks.Clear();
            string dir = Path.Combine(dataDir, "theme_decks");
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    var list = JsonConvert.DeserializeObject<List<ThemeDeckDef>>(File.ReadAllText(f, Encoding.UTF8));
                    if (list != null) Decks.AddRange(list);
                }
                catch (Exception e)
                {
                    Plugin.Log?.LogWarning($"Error cargando mazos temáticos {Path.GetFileName(f)}: {e.Message}");
                }
            }
        }
    }

    // ───────────────────────────── Validación ─────────────────────────────

    public static class DeckValidator
    {
        public static List<string> Validate(List<CardDef> deck, FormatDef format, bool checkOwned = true)
        {
            var errors = new List<string>();
            format = format ?? FormatRegistry.Default;
            if (deck.Count != format.DeckSize) errors.Add($"Deck has {deck.Count}/{format.DeckSize} cards.");
            if (deck.Count(c => c.IsBasicPokemon) < format.MinBasicPokemon) errors.Add("Needs at least 1 Basic Pokémon.");
            foreach (var g in deck.GroupBy(c => c.Name))
                if (!g.First().IsBasicEnergy && g.Count() > format.MaxCopies) errors.Add($"More than {format.MaxCopies} copies of {g.Key}.");
            foreach (var c in deck.Distinct())
            {
                if (!format.Allows(c)) errors.Add($"{c.Name} ({c.Id}) is not allowed in {format.Name}.");
                if (!c.IsPlayable) errors.Add($"{c.Name}: {c.UnsupportedReason}.");
            }
            if (checkOwned && Collection.Enforced)
                foreach (var g in deck.GroupBy(c => c.Id))
                {
                    int owned = Collection.Owned(g.First());
                    if (g.Count() > owned) errors.Add($"You only own {owned} of {g.First().Name} ({g.Key}), deck uses {g.Count()}.");
                }
            return errors;
        }

        /// <summary>Cuántas copias más se pueden agregar de una carta al mazo dado.</summary>
        public static int RemainingAllowed(CardDef c, List<CardDef> deck, FormatDef format)
        {
            format = format ?? FormatRegistry.Default;
            if (!c.IsPlayable || !format.Allows(c)) return 0;
            int byRule = c.IsBasicEnergy ? int.MaxValue : format.MaxCopies - deck.Count(x => x.Name == c.Name);
            int owned = Collection.Owned(c);
            int byOwned = owned == int.MaxValue ? int.MaxValue : owned - deck.Count(x => x.Id == c.Id);
            int bySize = format.DeckSize - deck.Count;
            return Math.Max(0, Math.Min(byRule, Math.Min(byOwned, bySize)));
        }
    }

    // ───────────────────────────── Importador PTCGO / PTCG Live (.txt en vivo) ─────────────────────────────

    public class MissingCardReportItem
    {
        public string CardName;
        public string RequestedSetCode;
        public string RequestedNumber;
        public int Wanted;
        public int Owned;
        public int Missing => Math.Max(0, Wanted - Owned);
        public string ExpansionWhereToFind;
        public bool InstalledInMyCurio;
        public bool RulesReadyInMod;
        public string Note;
    }

    public class PtcgoImportReport
    {
        public string FileName;
        public string SuggestedDeckName;
        public int TotalRequestedCards;
        public readonly List<CardDef> ResolvedPlayableCards = new List<CardDef>();
        public readonly List<MissingCardReportItem> MissingOrUnavailable = new List<MissingCardReportItem>();
        public readonly List<MissingCardReportItem> AllParsedItems = new List<MissingCardReportItem>();
    }

    /// <summary>
    /// Importador de mazos en formato texto de PTCGO / PTCG Live sin reiniciar el juego.
    /// Funciona igual que Phone Overhaul: abre la carpeta de importación en Windows Explorer,
    /// pegas tus archivos .txt y al abrir/actualizar en la Workbench se detectan al instante.
    /// </summary>
    public static class PtcgoImporter
    {
        public static string ImportDir => Path.Combine(BepInEx.Paths.ConfigPath, "PokeTCGPlay", "import");

        public static void EnsureFolderExists()
        {
            try
            {
                Directory.CreateDirectory(ImportDir);
            }
            catch { }
        }

        public static void OpenImportFolder()
        {
            try
            {
                EnsureFolderExists();
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{ImportDir}\"",
                    UseShellExecute = true
                });
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("No se pudo abrir la carpeta de importación: " + e.Message);
            }
        }

        public static List<string> ListTxtFiles()
        {
            EnsureFolderExists();
            if (!Directory.Exists(ImportDir)) return new List<string>();
            return Directory.GetFiles(ImportDir, "*.txt", SearchOption.TopDirectoryOnly)
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        static readonly System.Text.RegularExpressions.Regex RxSectionHeader =
            new System.Text.RegularExpressions.Regex(@"^(Pok[eé]mon|Trainer|Entrenador|Energy|Energ[ií]a|Total\s+Cards)\s*[:\-]",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Formato PTCGO / PTCG Live: "3 Buneary PFL 83" o "* 2 Mega Lopunny ex PFL 84" o "2 Basic Fire Energy SVE 10"
        static readonly System.Text.RegularExpressions.Regex RxPtcgoLine =
            new System.Text.RegularExpressions.Regex(@"^\*?\s*(\d+)\s*x?\s+(.+?)\s+([A-Za-z0-9\-]{2,7})\s+([A-Za-z0-9\-]+)\s*$");

        static readonly System.Text.RegularExpressions.Regex RxSimpleCountLine =
            new System.Text.RegularExpressions.Regex(@"^\*?\s*(\d+)\s*x?\s+(.+?)\s*$");

        static string MapBasicEnergyName(string rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName)) return rawName;
            string n = rawName.Trim();
            // PTCG Live exporta "Basic Fire Energy" o "Basic {R} Energy"
            if (n.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase) && n.EndsWith(" Energy", StringComparison.OrdinalIgnoreCase))
            {
                n = n.Substring(6).Trim();
            }
            return n;
        }

        public static string SetIdToPtcgoCode(string setId)
        {
            switch ((setId ?? "").Trim().ToLowerInvariant())
            {
                case "base1": return "BS";
                case "base2": return "JU";
                case "base3": return "FO";
                case "base4": return "B2";
                case "base5": return "TR";
                case "gym1": return "G1";
                case "gym2": return "G2";
                case "basep": return "PR";
                default: return string.IsNullOrWhiteSpace(setId) ? "BS" : setId.Trim().ToUpperInvariant();
            }
        }

        public static string PtcgoCodeToSetId(string ptcgoCode)
        {
            switch ((ptcgoCode ?? "").Trim().ToUpperInvariant())
            {
                case "BS": case "BS1": case "BASE": case "BASE1": return "base1";
                case "JU": case "JNG": case "BASE2": return "base2";
                case "FO": case "FOS": case "BASE3": return "base3";
                case "B2": case "BS2": case "BASE4": return "base4";
                case "TR": case "RO": case "BASE5": return "base5";
                case "G1": case "GYH": case "GYM1": return "gym1";
                case "G2": case "GYC": case "GYM2": return "gym2";
                case "PR": case "WP": case "WPO": case "BASEP": return "basep";
                default: return (ptcgoCode ?? "").Trim().ToLowerInvariant();
            }
        }

        public static string ExportToText(string deckName, IEnumerable<CardDef> deck)
        {
            var cards = (deck ?? Enumerable.Empty<CardDef>()).Where(c => c != null).ToList();
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(deckName))
                sb.AppendLine($"# {deckName.Trim()}");

            var pokes = cards.Where(c => c.Supertype == Supertype.Pokemon).GroupBy(c => c.Id).OrderBy(g => g.First().SetId).ThenBy(g => g.First().Number).ToList();
            var trainers = cards.Where(c => c.Supertype == Supertype.Trainer).GroupBy(c => c.Id).OrderBy(g => g.First().Name).ToList();
            var energies = cards.Where(c => c.Supertype == Supertype.Energy).GroupBy(c => c.Id).OrderBy(g => g.First().Name).ToList();

            sb.AppendLine($"Pokémon: {pokes.Sum(g => g.Count())}");
            foreach (var g in pokes)
            {
                var c = g.First();
                string num = !string.IsNullOrEmpty(c.NumberText) ? c.NumberText : c.Number.ToString();
                sb.AppendLine($"{g.Count()} {c.Name} {SetIdToPtcgoCode(c.SetId)} {num}");
            }
            sb.AppendLine();

            sb.AppendLine($"Trainer: {trainers.Sum(g => g.Count())}");
            foreach (var g in trainers)
            {
                var c = g.First();
                string num = !string.IsNullOrEmpty(c.NumberText) ? c.NumberText : c.Number.ToString();
                sb.AppendLine($"{g.Count()} {c.Name} {SetIdToPtcgoCode(c.SetId)} {num}");
            }
            sb.AppendLine();

            sb.AppendLine($"Energy: {energies.Sum(g => g.Count())}");
            foreach (var g in energies)
            {
                var c = g.First();
                string num = !string.IsNullOrEmpty(c.NumberText) ? c.NumberText : c.Number.ToString();
                sb.AppendLine($"{g.Count()} {c.Name} {SetIdToPtcgoCode(c.SetId)} {num}");
            }
            sb.AppendLine();
            sb.AppendLine($"Total Cards: {cards.Count}");
            return sb.ToString();
        }

        public static string ExportToFile(string deckName, IEnumerable<CardDef> deck)
        {
            EnsureFolderExists();
            string safe = string.IsNullOrWhiteSpace(deckName) ? "Mazo_Exportado" : deckName.Trim();
            foreach (var ch in Path.GetInvalidFileNameChars()) safe = safe.Replace(ch, '_');
            string outPath = Path.Combine(ImportDir, safe + ".txt");
            string content = ExportToText(deckName, deck);
            File.WriteAllText(outPath, content, Encoding.UTF8);
            try { UnityEngine.GUIUtility.systemCopyBuffer = content; } catch { }
            return outPath;
        }

        public static PtcgoImportReport ImportFromFile(string filePath)
        {
            if (!File.Exists(filePath))
                return new PtcgoImportReport { FileName = Path.GetFileName(filePath), SuggestedDeckName = Path.GetFileNameWithoutExtension(filePath) };
            string[] lines = File.ReadAllLines(filePath, Encoding.UTF8);
            return ImportFromLines(Path.GetFileName(filePath), Path.GetFileNameWithoutExtension(filePath), lines);
        }

        public static PtcgoImportReport ImportFromText(string suggestedName, string rawText)
        {
            string[] lines = (rawText ?? "").Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            return ImportFromLines("Portapapeles", string.IsNullOrWhiteSpace(suggestedName) ? "Mazo Portapapeles" : suggestedName, lines);
        }

        static PtcgoImportReport ImportFromLines(string sourceLabel, string suggestedName, IEnumerable<string> lines)
        {
            Collection.Invalidate();
            var report = new PtcgoImportReport
            {
                FileName = sourceLabel,
                SuggestedDeckName = suggestedName
            };

            foreach (string rawLine in lines)
            {
                string line = (rawLine ?? "").Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//")) continue;
                if (RxSectionHeader.IsMatch(line)) continue;

                int count = 0;
                string cardName = null;
                string setCode = "";
                string colNumStr = "";

                var m = RxPtcgoLine.Match(line);
                if (m.Success)
                {
                    int.TryParse(m.Groups[1].Value, out count);
                    cardName = m.Groups[2].Value.Trim();
                    setCode = m.Groups[3].Value.Trim();
                    colNumStr = m.Groups[4].Value.Trim();
                }
                else
                {
                    var m2 = RxSimpleCountLine.Match(line);
                    if (m2.Success)
                    {
                        int.TryParse(m2.Groups[1].Value, out count);
                        cardName = m2.Groups[2].Value.Trim();
                    }
                }

                if (count <= 0 || string.IsNullOrEmpty(cardName)) continue;
                report.TotalRequestedCards += count;

                string lookupName = MapBasicEnergyName(cardName);
                int.TryParse(System.Text.RegularExpressions.Regex.Match(colNumStr ?? "", @"\d+").Value, out int colNum);

                // 1) Buscar en MyCurio (tanto por código de expansión + número como por nombre en todas las expansiones instaladas)
                var exactMcEntry = MyCurioIndex.FindEntryBySetAndNumber(setCode, colNum);
                var allMcEntries = MyCurioIndex.FindEntriesByName(lookupName);
                if (exactMcEntry != null && !allMcEntries.Any(e => string.Equals(e.Sprite, exactMcEntry.Sprite, StringComparison.OrdinalIgnoreCase)))
                    allMcEntries.Insert(0, exactMcEntry);

                // 2) Buscar en CardDatabase (cartas con reglas en el mod)
                CardDef matchedDef = null;
                if (!string.IsNullOrEmpty(setCode) && colNum > 0)
                {
                    string mappedSetId = PtcgoCodeToSetId(setCode);
                    if (!CardDatabase.ById.TryGetValue($"{mappedSetId}-{colNum}", out matchedDef))
                        CardDatabase.ById.TryGetValue($"{setCode.ToLowerInvariant()}-{colNum}", out matchedDef);
                    if (matchedDef == null && exactMcEntry != null)
                        matchedDef = CardDatabase.All.FirstOrDefault(c => string.Equals(c.Sprite, exactMcEntry.Sprite, StringComparison.OrdinalIgnoreCase));
                }
                if (matchedDef == null)
                {
                    CardDatabase.ByName.TryGetValue(lookupName, out matchedDef);
                    if (matchedDef == null)
                        matchedDef = CardDatabase.All.FirstOrDefault(c =>
                            string.Equals(MyCurioIndex.NormalizeName(c.Name), MyCurioIndex.NormalizeName(lookupName), StringComparison.OrdinalIgnoreCase));
                }

                // 3) Calcular cuántas copias tiene el jugador en su colección
                int owned = 0;
                if (matchedDef != null && matchedDef.IsBasicEnergy && Collection.FreeBasicEnergy)
                {
                    owned = int.MaxValue;
                }
                else
                {
                    var countedSprites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (matchedDef != null && !string.IsNullOrEmpty(matchedDef.Sprite) && countedSprites.Add(matchedDef.Sprite))
                        owned += Collection.ReadOwnedBySprite(matchedDef.Sprite, matchedDef.MyCurioBorder, matchedDef.MyCurioFoil);
                    foreach (var mcEntry in allMcEntries)
                    {
                        if (countedSprites.Add(mcEntry.Sprite))
                            owned += Collection.ReadOwnedBySprite(mcEntry.Sprite, mcEntry.Border, mcEntry.Foil);
                    }
                }

                // 4) Determinar en qué expansión(es) se puede conseguir esta carta
                bool inMyCurio = allMcEntries.Count > 0 || (matchedDef != null && matchedDef.HasMyCurioArt);
                string whereToFind;
                if (matchedDef != null && matchedDef.IsBasicEnergy && Collection.FreeBasicEnergy)
                {
                    whereToFind = "Basic Energy (Included free by default)";
                    inMyCurio = true;
                }
                else if (allMcEntries.Count > 0)
                {
                    var groupedByExp = allMcEntries
                        .GroupBy(e => e.ExpansionName)
                        .Select(g => $"{g.Key} ({string.Join(", ", g.Select(x => x.Sprite).Distinct())})");
                    whereToFind = "MyCurio Packs: " + string.Join(" | ", groupedByExp);
                }
                else
                {
                    string origSetName = (!string.IsNullOrEmpty(setCode) && MyCurioIndex.PtcgoCodeNames.TryGetValue(setCode, out string sn))
                        ? $"{sn} [{setCode}]"
                        : (!string.IsNullOrEmpty(setCode) ? setCode : "Unknown Expansion");
                    whereToFind = $"Not installed in MyCurio (Original Set: {origSetName})";
                }

                bool rulesReady = matchedDef != null && matchedDef.IsPlayable;
                string note = "";
                if (matchedDef == null)
                    note = inMyCurio ? "Art available in MyCurio (gameplay data pending in Data/sets)" : "Expansion not installed";
                else if (!matchedDef.IsPlayable)
                    note = matchedDef.UnsupportedReason;

                var item = new MissingCardReportItem
                {
                    CardName = cardName,
                    RequestedSetCode = setCode,
                    RequestedNumber = colNumStr,
                    Wanted = count,
                    Owned = owned,
                    ExpansionWhereToFind = whereToFind,
                    InstalledInMyCurio = inMyCurio,
                    RulesReadyInMod = rulesReady,
                    Note = note
                };
                report.AllParsedItems.Add(item);

                if (owned < count || !rulesReady)
                {
                    report.MissingOrUnavailable.Add(item);
                }

                // Añadir al mazo las copias que tengan definición en CardDatabase
                if (matchedDef != null)
                {
                    for (int i = 0; i < count; i++)
                        report.ResolvedPlayableCards.Add(matchedDef);
                }
            }

            return report;
        }

        /// <summary>Analiza el mazo actual en el editor y devuelve las cartas que faltan en la colección y en qué sobres salen.</summary>
        public static PtcgoImportReport AnalyzeDeck(string deckName, IEnumerable<CardDef> deck)
        {
            Collection.Invalidate();
            var report = new PtcgoImportReport
            {
                FileName = deckName,
                SuggestedDeckName = deckName
            };
            foreach (var grp in deck.GroupBy(c => c.Id))
            {
                var c = grp.First();
                int count = grp.Count();
                report.TotalRequestedCards += count;

                int owned = Collection.Owned(c);
                var allMcEntries = MyCurioIndex.FindEntriesByName(c.Name);
                string whereToFind;
                if (c.IsBasicEnergy && Collection.FreeBasicEnergy)
                {
                    whereToFind = "Basic Energy (Included free)";
                }
                else if (allMcEntries.Count > 0)
                {
                    var groupedByExp = allMcEntries
                        .GroupBy(e => e.ExpansionName)
                        .Select(g => $"{g.Key} ({string.Join(", ", g.Select(x => x.Sprite).Distinct())})");
                    whereToFind = "MyCurio Packs: " + string.Join(" | ", groupedByExp);
                }
                else
                {
                    var setDef = CardDatabase.GetSet(c.SetId);
                    whereToFind = setDef != null ? $"MyCurio Packs: {setDef.Name} ({c.Sprite})" : c.SetId;
                }

                var item = new MissingCardReportItem
                {
                    CardName = c.Name,
                    RequestedSetCode = c.SetId.ToUpperInvariant(),
                    RequestedNumber = c.NumberText,
                    Wanted = count,
                    Owned = owned,
                    ExpansionWhereToFind = whereToFind,
                    InstalledInMyCurio = c.HasMyCurioArt || allMcEntries.Count > 0,
                    RulesReadyInMod = c.IsPlayable,
                    Note = c.UnsupportedReason
                };
                report.AllParsedItems.Add(item);
                if (owned < count || !c.IsPlayable)
                    report.MissingOrUnavailable.Add(item);
            }
            return report;
        }
    }
}
