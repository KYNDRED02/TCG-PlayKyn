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
        }
    }

    // ───────────────────────────── Colección ─────────────────────────────

    /// <summary>Lee cuántas copias de cada carta tiene el jugador en el álbum del juego (cartas de MyCurio obtenidas en sobres).</summary>
    public static class Collection
    {
        static readonly Dictionary<string, KeyValuePair<ECardExpansionType, EMonsterType>> spriteMap =
            new Dictionary<string, KeyValuePair<ECardExpansionType, EMonsterType>>(StringComparer.OrdinalIgnoreCase);
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
            foreach (var exKey in CardDatabase.Sets.Select(s => s.MyCurioExpansion).Where(x => !string.IsNullOrEmpty(x)).Distinct())
            {
                try
                {
                    if (!TryParseExpansion(exKey, out var exp))
                    {
                        Plugin.Log?.LogWarning($"Colección: no se encontró el enum ECardExpansionType para '{exKey}'.");
                        continue;
                    }
                    var dict = Epl.Api.Assets.CardExpansions.GetCardSprites(exp);
                    if (dict == null) continue;
                    foreach (var kv in dict)
                        if (kv.Value != null && !string.IsNullOrEmpty(kv.Value.AssetName))
                            spriteMap[kv.Value.AssetName] = new KeyValuePair<ECardExpansionType, EMonsterType>(exp, kv.Key);
                }
                catch (Exception e) { Plugin.Log?.LogWarning($"Colección: no se pudo mapear {exKey}: {e.Message}"); }
            }
            Plugin.Log?.LogInfo($"Colección: {spriteMap.Count} cartas de MyCurio mapeadas al álbum del juego.");
        }

        /// <summary>Copias que posee el jugador (int.MaxValue si no aplica la restricción).</summary>
        public static int Owned(CardDef c)
        {
            if (c == null) return 0;
            if (!Enforced || (FreeBasicEnergy && c.IsBasicEnergy)) return int.MaxValue;
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now - cacheTime > 3f) { cache.Clear(); cacheTime = now; }
            if (cache.TryGetValue(c.Id, out int v)) return v;
            v = ReadOwned(c);
            cache[c.Id] = v;
            return v;
        }

        static int ReadOwned(CardDef c)
        {
            try
            {
                BuildMap();
                if (string.IsNullOrEmpty(c.Sprite) || !spriteMap.TryGetValue(c.Sprite, out var key)) return 0;

                int total = 0;
                var seenSaveIndices = new HashSet<int>();
                var cd = new CardData { expansionType = key.Key, monsterType = key.Value, isFoil = c.MyCurioFoil };
                if (Enum.TryParse<ECardBorderType>(c.MyCurioBorder ?? "Base", true, out var border)) cd.borderType = border;

                void AccumulateVariant(CardData variant)
                {
                    int saveIdx = CPlayerData.GetCardSaveIndex(variant);
                    if (saveIdx < 0 || !seenSaveIndices.Add(saveIdx)) return;

                    // 1) En el Álbum (Colección del jugador)
                    var list = CPlayerData.GetCardCollectedList(variant.expansionType, false);
                    if (list != null && saveIdx < list.Count)
                        total += Math.Max(0, list[saveIdx]);

                    // 2) En Muebles de Almacenamiento de Cartas (InteractableCardStorageShelf / Bulk Box)
                    var storageShelves = ShelfManager.GetCardStorageShelfList();
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
                                if (item != null && item.expansionType == variant.expansionType && item.cardSaveIndex == saveIdx)
                                    total += Math.Max(0, item.amount);
                            }
                        }
                    }
                }

                // Probar variante principal y también foil/no-foil (por si la expansión tiene HasRandomFoils)
                AccumulateVariant(cd);
                cd.isFoil = !cd.isFoil;
                AccumulateVariant(cd);

                // 3) En Estantes / Expositores de Cartas de la tienda (CardShelf)
                var cardShelves = ShelfManager.GetCardShelfList();
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

                return total;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning($"ReadOwned({c?.Id}): {e.Message}");
                return 0;
            }
        }
    }

    // ───────────────────────────── Mazos ─────────────────────────────

    public class DeckEntry { public string Id; public int Count; }

    /// <summary>Mazo guardado en BepInEx/config/PokeTCGPlay/decks/&lt;nombre&gt;.deck.json.</summary>
    public class DeckDef
    {
        public string Name = "Nuevo mazo";
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
                if (List().Count > 0) return;
                string legacy = Path.Combine(dataDir, "decks", "player.txt");
                if (!File.Exists(legacy)) return;
                var warn = new List<string>();
                var cards = DeckLoader.ParseText(File.ReadAllLines(legacy), warn);
                if (cards.Count == 0) return;
                var d = new DeckDef { Name = "Mazo inicial", Format = "base1" };
                d.SetCards(cards);
                Save(d);
                SetActive(d.Name);
                Plugin.Log?.LogInfo($"Mazo antiguo migrado a {Dir} ({cards.Count} cartas).");
            }
            catch (Exception e) { Plugin.Log?.LogWarning("Migración de mazo: " + e.Message); }
        }
    }

    // ───────────────────────────── Validación ─────────────────────────────

    public static class DeckValidator
    {
        public static List<string> Validate(List<CardDef> deck, FormatDef format, bool checkOwned = true)
        {
            var errors = new List<string>();
            format = format ?? FormatRegistry.Default;
            if (deck.Count != format.DeckSize) errors.Add($"El mazo tiene {deck.Count}/{format.DeckSize} cartas.");
            if (deck.Count(c => c.IsBasicPokemon) < format.MinBasicPokemon) errors.Add("Necesita al menos 1 Pokémon Básico.");
            foreach (var g in deck.GroupBy(c => c.Name))
                if (!g.First().IsBasicEnergy && g.Count() > format.MaxCopies) errors.Add($"Más de {format.MaxCopies} copias de {g.Key}.");
            foreach (var c in deck.Distinct())
            {
                if (!format.Allows(c)) errors.Add($"{c.Name} ({c.Id}) no está permitida en {format.Name}.");
                if (!c.IsPlayable) errors.Add($"{c.Name}: {c.UnsupportedReason}.");
            }
            if (checkOwned && Collection.Enforced)
                foreach (var g in deck.GroupBy(c => c.Id))
                {
                    int owned = Collection.Owned(g.First());
                    if (g.Count() > owned) errors.Add($"Solo tienes {owned} de {g.First().Name} ({g.Key}) y el mazo usa {g.Count()}.");
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
}
