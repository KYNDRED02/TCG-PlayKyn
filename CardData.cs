// TCG-PlayKyn: Pokemon card schema, sets loader, MyCurio textures atlas & collection resolver
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TCGPlayKyn
{
    public enum Supertype { Pokemon, Trainer, Energy }

    public class AttackDef
    {
        public string Name;
        public List<string> Cost = new List<string>();
        public int BaseDamage;
        public string DamageSuffix = ""; // "+", "×", "-"
        public string Text = "";
    }

    public class PowerDef { public string Name; public string Text; public string Kind = ""; }

    /// <summary>Expansión cargada desde Data/sets/&lt;carpeta&gt;/set.json.</summary>
    public class SetDef
    {
        public string Id;                 // base1
        public string Name;               // Base Set
        public string MyCurioExpansion;   // MyCurioPokemon-BS (enum de EPL)
        public string SpritePrefix;       // BS-
        public int SpriteDigits = 3;      // BS-001
        public bool EffectsImplemented;   // true si los efectos de sus cartas están programados
        public int Order;                 // orden de lanzamiento (para listas)
        public string Folder;
        public readonly List<CardDef> Cards = new List<CardDef>();
        public override string ToString() => $"{Name} ({Id})";
    }

    /// <summary>Carta unificada: reglas (formato pokemontcg.io + overrides manuales) + sprite de MyCurio.</summary>
    public class CardDef
    {
        public string Id;          // base1-1 (único entre expansiones)
        public string SetId;       // base1
        public int Number;         // 1..102 (parte numérica)
        public string NumberText = ""; // "1", "TG01"...
        public string Name;
        public Supertype Supertype;
        public List<string> Subtypes = new List<string>();
        public string Stage = "";  // Basic / Stage 1 / Stage 2
        public bool IsBasicEnergy;
        public int Hp;
        public string Type = "";   // Fire, Water...
        public string EvolvesFrom;
        public PowerDef Power;
        public List<PowerDef> Abilities = new List<PowerDef>();
        public List<AttackDef> Attacks = new List<AttackDef>();
        public string WeaknessType;
        public string ResistanceType;
        public int ResistanceValue;
        public int RetreatCost;
        public string RulesText = "";
        public string Rarity = "";
        public string Sprite;      // BS-001 (nombre del asset en el bundle de MyCurio)

        // Datos de MyCurio (para leer la colección del jugador)
        public string MyCurioExpansion;
        public string MyCurioBorder;
        public bool MyCurioFoil;
        public bool HasMyCurioArt;

        /// <summary>Indica si esta copia específica de la carta es Foil / Holográfica.</summary>
        public bool IsFoil;

        /// <summary>Crea una copia de la definición de carta asignándole el estado Foil deseado.</summary>
        public CardDef Clone(bool isFoil = false)
        {
            var copy = (CardDef)this.MemberwiseClone();
            copy.IsFoil = isFoil;
            return copy;
        }

        /// <summary>Motivo por el que no se puede usar aún (mecánica futura, efectos sin programar...). Vacío = jugable.</summary>
        public string UnsupportedReason = "";
        public bool IsPlayable => string.IsNullOrEmpty(UnsupportedReason);

        public bool IsBasicPokemon => Supertype == Supertype.Pokemon && Stage == "Basic";
        /// <summary>Tipo de energía que provee (Double Colorless = 2 Colorless; Rainbow = Rainbow; Full Heal / Potion = Colorless).</summary>
        public string EnergyProvides =>
            Name == "Rainbow Energy" ? "Rainbow"
            : (Name == "Full Heal Energy" || Name == "Potion Energy" || Name == "Double Colorless Energy" || Name == "Recycle Energy" || Name == "Warp Energy") ? "Colorless"
            : Name.Replace(" Energy", "").Replace("Double ", "");
        public int EnergyAmount => Name.StartsWith("Double") ? 2 : 1;
        public override string ToString() => $"{Name} ({Id})";
    }

    /// <summary>
    /// Índice de las expansiones de MyCurio instaladas (lee sus .json directamente de BepInEx/plugins).
    /// Así las imágenes siempre salen del mod de MyCurio, sin copiar nada.
    /// </summary>
    public static class MyCurioIndex
    {
        public class Entry { public string Name, Sprite, Border, Rarity, ExpansionKey, ExpansionName; public bool Foil; }
        public class Expansion
        {
            public string Key, Name, JsonPath, BundlePath, CardBackSprite, SpritePrefix;
            public readonly Dictionary<string, Entry> BySprite = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            public readonly List<Entry> AllEntries = new List<Entry>();
        }

        public static readonly Dictionary<string, Expansion> Expansions = new Dictionary<string, Expansion>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Mapeo de códigos PTCGO / PTCG Live a claves de expansión de MyCurio.</summary>
        public static readonly Dictionary<string, string> PtcgoCodeToMyCurio = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "BS", "MyCurioPokemon-BS" }, { "BS1", "MyCurioPokemon-BS" }, { "BASE", "MyCurioPokemon-BS" }, { "BASE1", "MyCurioPokemon-BS" },
            { "JU", "MyCurioPokemon-JU" }, { "JNG", "MyCurioPokemon-JU" }, { "BASE2", "MyCurioPokemon-JU" },
            { "FO", "MyCurioPokemon-FO" }, { "FOS", "MyCurioPokemon-FO" }, { "BASE3", "MyCurioPokemon-FO" },
            { "B2", "MyCurioPokemon-B2" }, { "BS2", "MyCurioPokemon-B2" }, { "BASE4", "MyCurioPokemon-B2" },
            { "TR", "MyCurioPokemon-TR" }, { "RO", "MyCurioPokemon-TR" }, { "BASE5", "MyCurioPokemon-TR" },
            { "G1", "MyCurioPokemon-GYM1" }, { "GYH", "MyCurioPokemon-GYM1" }, { "GYM1", "MyCurioPokemon-GYM1" },
            { "G2", "MyCurioPokemon-GYM2" }, { "GYC", "MyCurioPokemon-GYM2" }, { "GYM2", "MyCurioPokemon-GYM2" },
            { "PR", "MyCurioPokemon-JU" }, { "WP", "MyCurioPokemon-JU" }, { "WPO", "MyCurioPokemon-JU" }, { "BASEP", "MyCurioPokemon-JU" },
            { "MEG", "MyCurioPokemon-ME01" }, { "ME01", "MyCurioPokemon-ME01" }, { "ME1", "MyCurioPokemon-ME01" },
            { "PFL", "MyCurioPokemon-ME02" }, { "ME02", "MyCurioPokemon-ME02" }, { "ME2", "MyCurioPokemon-ME02" },
            { "ASC", "MyCurioPokemon-ME025" }, { "ME025", "MyCurioPokemon-ME025" }, { "ME2.5", "MyCurioPokemon-ME025" },
            { "POR", "MyCurioPokemon-ME03" }, { "ME03", "MyCurioPokemon-ME03" }, { "ME3", "MyCurioPokemon-ME03" },
            { "CRI", "MyCurioPokemon-ME04" }, { "ME04", "MyCurioPokemon-ME04" }, { "ME4", "MyCurioPokemon-ME04" },
            { "PBL", "MyCurioPokemon-ME05" }, { "ME05", "MyCurioPokemon-ME05" }, { "ME5", "MyCurioPokemon-ME05" },
            { "30C", "MyCurioPokemon-ME30TH" }, { "ME30TH", "MyCurioPokemon-ME30TH" }, { "30TH", "MyCurioPokemon-ME30TH" },
            { "MEP", "MyCurioPokemon-MEP" }
        };

        /// <summary>Nombres legibles de códigos PTCGO/PTCG Live para informar al usuario en qué expansión sale una carta.</summary>
        public static readonly Dictionary<string, string> PtcgoCodeNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "BS", "Base Set" }, { "BS1", "Base Set" }, { "BASE1", "Base Set" },
            { "JU", "Jungle" }, { "JNG", "Jungle" }, { "BASE2", "Jungle" },
            { "FO", "Fossil" }, { "FOS", "Fossil" }, { "BASE3", "Fossil" },
            { "B2", "Base Set 2" }, { "BS2", "Base Set 2" }, { "BASE4", "Base Set 2" },
            { "TR", "Team Rocket" }, { "RO", "Team Rocket" }, { "BASE5", "Team Rocket" },
            { "G1", "Gym Heroes" }, { "GYH", "Gym Heroes" }, { "GYM1", "Gym Heroes" },
            { "G2", "Gym Challenge" }, { "GYC", "Gym Challenge" }, { "GYM2", "Gym Challenge" },
            { "PR", "Wizards Black Star Promos" }, { "WP", "Wizards Black Star Promos" }, { "WPO", "Wizards Black Star Promos" }, { "BASEP", "Wizards Black Star Promos" },
            { "MEG", "Mega Evolution (MyCurio ME01)" },
            { "PFL", "Phantasmal Flames (MyCurio ME02)" },
            { "ASC", "Ascended Heroes (MyCurio ME025)" },
            { "POR", "Perfect Order (MyCurio ME03)" },
            { "CRI", "Chaos Rising (MyCurio ME04)" },
            { "PBL", "Pitch Black (MyCurio ME05)" },
            { "30C", "30th Celebration (MyCurio ME30TH)" },
            { "MEP", "MEP Black Star Promos (MyCurio MEP)" },
            { "TEF", "Temporal Forces" },
            { "JTG", "Journey Together" },
            { "SCR", "Stellar Crown" },
            { "WHT", "White Flare" },
            { "BLK", "Black Bolt" },
            { "SSP", "Surging Sparks" },
            { "PRE", "Prismatic Evolutions" },
            { "SVI", "Scarlet & Violet" },
            { "PAL", "Paldea Evolved" },
            { "OBF", "Obsidian Flames" },
            { "MEW", "Pokémon 151" },
            { "PAR", "Paradox Rift" },
            { "PAF", "Paldean Fates" },
            { "TWM", "Twilight Masquerade" },
            { "SFA", "Shrouded Fable" },
            { "SVE", "Scarlet & Violet Basic Energy" }
        };

        public static string NormalizeName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            string norm = s.Trim()
                .Replace("é", "e").Replace("É", "E")
                .Replace("’", "'").Replace("`", "'");
            return norm;
        }

        public static Expansion ResolvePtcgoExpansion(string ptcgoCode)
        {
            if (string.IsNullOrWhiteSpace(ptcgoCode)) return null;
            if (PtcgoCodeToMyCurio.TryGetValue(ptcgoCode.Trim(), out string mcKey) && Expansions.TryGetValue(mcKey, out var exp))
                return exp;
            if (Expansions.TryGetValue(ptcgoCode.Trim(), out var direct))
                return direct;
            if (Expansions.TryGetValue("MyCurioPokemon-" + ptcgoCode.Trim(), out var pref))
                return pref;
            return null;
        }

        public static Entry FindEntryBySetAndNumber(string ptcgoCode, int number)
        {
            if (number <= 0) return null;
            string code = (ptcgoCode ?? "").Trim();
            if (code.Equals("PR", StringComparison.OrdinalIgnoreCase) ||
                code.Equals("WP", StringComparison.OrdinalIgnoreCase) ||
                code.Equals("WPO", StringComparison.OrdinalIgnoreCase) ||
                code.Equals("BASEP", StringComparison.OrdinalIgnoreCase))
            {
                string wpoKey = $"WPO-{number:D3}";
                foreach (var ex in Expansions.Values)
                    if (ex.BySprite.TryGetValue(wpoKey, out var wpoEntry))
                        return wpoEntry;
            }
            var exp = ResolvePtcgoExpansion(code);
            if (exp == null) return null;
            string prefix = exp.SpritePrefix ?? "";
            string sp3 = $"{prefix}{number:D3}";
            if (exp.BySprite.TryGetValue(sp3, out var e3)) return e3;
            string spRaw = $"{prefix}{number}";
            if (exp.BySprite.TryGetValue(spRaw, out var eRaw)) return eRaw;
            return null;
        }

        public static bool IsPokemonExpansion(string key, string jsonPath = null)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;
            if (!string.IsNullOrEmpty(jsonPath) && jsonPath.IndexOf("OnePiece", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            if (key.IndexOf("OnePiece", StringComparison.OrdinalIgnoreCase) >= 0 ||
                key.StartsWith("OP", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("EB", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("PRB", StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }

        public static List<Entry> FindEntriesByName(string cardName)
        {
            var results = new List<Entry>();
            if (string.IsNullOrWhiteSpace(cardName)) return results;
            string target = NormalizeName(cardName);
            var seenSprites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var exp in Expansions.Values)
            {
                if (!IsPokemonExpansion(exp.Key, exp.JsonPath))
                    continue;

                foreach (var entry in exp.BySprite.Values)
                {
                    if (string.Equals(NormalizeName(entry.Name), target, StringComparison.OrdinalIgnoreCase) && seenSprites.Add(entry.Sprite))
                    {
                        results.Add(entry);
                    }
                }
            }
            return results;
        }

        public static void Scan(string pluginsDir)
        {
            Expansions.Clear();
            if (!Directory.Exists(pluginsDir)) return;
            foreach (var file in Directory.GetFiles(pluginsDir, "*.json", SearchOption.AllDirectories))
            {
                try
                {
                    var fi = new FileInfo(file);
                    if (fi.Length > 8 * 1024 * 1024) continue;
                    if (file.IndexOf(Path.DirectorySeparatorChar + "TCG-PlayKyn" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0 || file.IndexOf(Path.DirectorySeparatorChar + "PokeTCGPlay" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (!IsPokemonExpansion("check", file)) continue;
                    string text = File.ReadAllText(file, Encoding.UTF8);
                    if (text.IndexOf("\"CardExpansions\"", StringComparison.Ordinal) < 0) continue;
                    var root = JObject.Parse(text);
                    string bundle = Path.Combine(Path.GetDirectoryName(file), Path.GetFileNameWithoutExtension(file));
                    foreach (JObject ex in (root["CardExpansions"] as JArray) ?? new JArray())
                    {
                        string key = (string)ex["CardExpansion"];
                        if (!IsPokemonExpansion(key, file)) continue;
                        var e = new Expansion
                        {
                            Key = key,
                            Name = (string)ex["Name"] ?? key,
                            JsonPath = file,
                            BundlePath = File.Exists(bundle) ? bundle : null,
                            CardBackSprite = (string)ex["CardbackSprite"] ?? "CardBack"
                        };
                        foreach (JObject c in (ex["Cards"] as JArray) ?? new JArray())
                        {
                            string sp = (string)c["Sprite"];
                            if (string.IsNullOrEmpty(sp)) continue;
                            if (string.IsNullOrEmpty(e.SpritePrefix))
                            {
                                int dash = sp.IndexOf('-');
                                if (dash > 0) e.SpritePrefix = sp.Substring(0, dash + 1);
                            }
                            var entry = new Entry
                            {
                                Name = (string)c["Name"],
                                Sprite = sp,
                                Border = (string)c["BorderType"] ?? "Base",
                                Foil = (bool?)c["IsFoil"] ?? false,
                                Rarity = (string)c["Rarity"] ?? "",
                                ExpansionKey = key,
                                ExpansionName = e.Name
                            };
                            if (!e.BySprite.ContainsKey(sp))
                                e.BySprite[sp] = entry;
                            e.AllEntries.Add(entry);
                        }
                        Expansions[key] = e;
                    }
                }
                catch (Exception ex) { Plugin.Log?.LogWarning($"MyCurioIndex: no se pudo leer {file}: {ex.Message}"); }
            }
        }
    }

    public static class CardDatabase
    {
        /// <summary>Subtipos de mecánicas que aún no están implementadas (se cargan pero no se pueden usar).</summary>
        public static readonly HashSet<string> FutureSubtypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "EX", "ex", "MEGA", "Mega", "V", "VMAX", "VSTAR", "V-UNION", "GX", "TAG TEAM", "BREAK", "LEGEND", "Radiant", "Tera", "ACE SPEC", "Prism Star", "Level-Up", "LV.X" };

        public static readonly List<SetDef> Sets = new List<SetDef>();
        public static readonly List<CardDef> All = new List<CardDef>();
        public static readonly Dictionary<string, CardDef> ById = new Dictionary<string, CardDef>(StringComparer.OrdinalIgnoreCase);
        public static readonly Dictionary<string, CardDef> ByName = new Dictionary<string, CardDef>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Sprite -> ruta del bundle de MyCurio (para pedirlo a EPL).</summary>
        public static readonly Dictionary<string, string> SpriteBundle = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static SetDef GetSet(string id) => Sets.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

        public static void Load(string dataDir, string pluginsDir)
        {
            Sets.Clear(); All.Clear(); ById.Clear(); ByName.Clear(); SpriteBundle.Clear();
            MyCurioIndex.Scan(pluginsDir);
            Plugin.Log?.LogInfo($"Expansiones MyCurio detectadas: {string.Join(", ", MyCurioIndex.Expansions.Values.Select(e => e.Name))}");

            string setsDir = Path.Combine(dataDir, "sets");
            if (Directory.Exists(setsDir))
            {
                foreach (var dir in Directory.GetDirectories(setsDir))
                {
                    string setJson = Path.Combine(dir, "set.json");
                    if (!File.Exists(setJson)) continue;
                    try { LoadSet(dir, setJson); }
                    catch (Exception e) { Plugin.Log?.LogError($"Error cargando expansión {Path.GetFileName(dir)}: {e.Message}"); }
                }
            }

            // Auto-registro de expansiones más nuevas de MyCurio (ej. ME01..ME05, ME30TH, MEP...) para verlas en el crafteador
            foreach (var mcExp in MyCurioIndex.Expansions.Values)
            {
                if (Sets.Any(s => string.Equals(s.MyCurioExpansion, mcExp.Key, StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(s.Id, mcExp.Key.Replace("MyCurioPokemon-", "").Replace("mycurio-pokemon-", ""), StringComparison.OrdinalIgnoreCase)))
                    continue;

                string rawKey = mcExp.Key.Replace("MyCurioPokemon-", "").Replace("mycurio-pokemon-", "");
                string setId = rawKey.ToLowerInvariant();
                var newSet = new SetDef
                {
                    Id = setId,
                    Name = mcExp.Name,
                    MyCurioExpansion = mcExp.Key,
                    SpritePrefix = mcExp.SpritePrefix ?? "",
                    EffectsImplemented = false,
                    Order = 1000 + Sets.Count
                };

                foreach (var entry in mcExp.AllEntries)
                {
                    string cardId = $"{setId}-{entry.Sprite}";
                    if (ById.ContainsKey(cardId)) continue;

                    var d = new CardDef
                    {
                        Id = cardId,
                        SetId = setId,
                        Name = entry.Name ?? entry.Sprite,
                        Sprite = entry.Sprite,
                        Supertype = Supertype.Pokemon,
                        Stage = "Basic",
                        Hp = 70,
                        Rarity = entry.Rarity ?? "",
                        HasMyCurioArt = true,
                        MyCurioExpansion = mcExp.Key,
                        MyCurioBorder = entry.Border,
                        MyCurioFoil = entry.Foil,
                        UnsupportedReason = "Not implemented expansion mechanics yet"
                    };

                    if (mcExp.BundlePath != null) SpriteBundle[d.Sprite] = mcExp.BundlePath;
                    newSet.Cards.Add(d);
                    All.Add(d);
                    ById[d.Id] = d;
                }

                if (newSet.Cards.Count > 0)
                {
                    Sets.Add(newSet);
                    Plugin.Log?.LogInfo($"[MyCurio] Auto-registrada expansión moderna '{newSet.Name}' ({newSet.Cards.Count} cartas).");
                }
            }

            Sets.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));

            // ByName: preferir cartas jugables y de expansiones más antiguas (las reimpresiones comparten reglas)
            foreach (var s in Sets)
                foreach (var c in s.Cards)
                    if (!ByName.TryGetValue(c.Name, out var prev) || (!prev.IsPlayable && c.IsPlayable))
                        ByName[c.Name] = c;
        }

        static void LoadSet(string dir, string setJson)
        {
            var sj = JObject.Parse(File.ReadAllText(setJson, Encoding.UTF8));
            var set = new SetDef
            {
                Id = (string)sj["id"] ?? Path.GetFileName(dir),
                Name = (string)sj["name"] ?? Path.GetFileName(dir),
                MyCurioExpansion = (string)sj["mycurioExpansion"],
                SpritePrefix = (string)sj["spritePrefix"] ?? "",
                SpriteDigits = (int?)sj["spriteDigits"] ?? 3,
                EffectsImplemented = (bool?)sj["effectsImplemented"] ?? false,
                Order = (int?)sj["order"] ?? 999,
                Folder = dir
            };

            // 1) Reglas base (formato pokemontcg.io). 2) Overrides manuales: cambian campos o agregan cartas nuevas.
            var cards = new List<JObject>();
            string cardsFile = Path.Combine(dir, (string)sj["cardsFile"] ?? "cards.json");
            if (File.Exists(cardsFile))
                foreach (var t in JArray.Parse(File.ReadAllText(cardsFile, Encoding.UTF8))) if (t is JObject o) cards.Add(o);

            string ovFile = Path.Combine(dir, (string)sj["overridesFile"] ?? "overrides.json");
            if (File.Exists(ovFile))
            {
                var ovRoot = JToken.Parse(File.ReadAllText(ovFile, Encoding.UTF8));
                var ovList = ovRoot is JArray arr ? arr : (ovRoot["cards"] as JArray ?? new JArray());
                foreach (JObject ov in ovList.OfType<JObject>())
                {
                    string id = (string)ov["id"];
                    string num = (string)ov["number"];
                    if (string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(num)) id = $"{set.Id}-{num}";
                    var target = cards.FirstOrDefault(c => string.Equals((string)c["id"], id, StringComparison.OrdinalIgnoreCase));
                    if (target != null) target.Merge(ov, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace, MergeNullValueHandling = MergeNullValueHandling.Merge });
                    else { if (ov["id"] == null) ov["id"] = id; cards.Add(ov); }
                }
            }

            MyCurioIndex.Expansions.TryGetValue(set.MyCurioExpansion ?? "", out var mc);
            if (!string.IsNullOrEmpty(set.MyCurioExpansion) && mc == null)
                Plugin.Log?.LogWarning($"[{set.Id}] No se encontró la expansión de MyCurio '{set.MyCurioExpansion}'. Las cartas no tendrán imagen.");

            foreach (var c in cards)
            {
                var d = ParseCard(c, set);
                if (d == null) continue;
                if (ById.ContainsKey(d.Id)) { Plugin.Log?.LogWarning($"[{set.Id}] Id duplicado {d.Id}, se ignora."); continue; }

                MyCurioIndex.Entry me = null;
                MyCurioIndex.Expansion resolvedExp = mc;
                if (mc == null || string.IsNullOrEmpty(d.Sprite) || !mc.BySprite.TryGetValue(d.Sprite, out me))
                {
                    if (!string.IsNullOrEmpty(d.Sprite))
                    {
                        foreach (var exp in MyCurioIndex.Expansions.Values)
                        {
                            if (exp.BySprite.TryGetValue(d.Sprite, out me))
                            {
                                resolvedExp = exp;
                                break;
                            }
                        }
                    }
                }
                if (resolvedExp != null && me != null)
                {
                    d.HasMyCurioArt = true;
                    d.MyCurioExpansion = resolvedExp.Key;
                    d.MyCurioBorder = me.Border;
                    d.MyCurioFoil = me.Foil;
                    if (resolvedExp.BundlePath != null) SpriteBundle[d.Sprite] = resolvedExp.BundlePath;
                }

                if (string.IsNullOrEmpty(d.UnsupportedReason))
                {
                    var future = d.Subtypes.FirstOrDefault(s => FutureSubtypes.Contains(s));
                    if (future != null) d.UnsupportedReason = $"Mecánica '{future}' aún no implementada";
                    else if (!set.EffectsImplemented && (d.Supertype != Supertype.Energy || !d.IsBasicEnergy))
                        d.UnsupportedReason = "Efectos de esta expansión aún no programados";
                }

                set.Cards.Add(d);
                All.Add(d);
                ById[d.Id] = d;
            }
            Sets.Add(set);
            Plugin.Log?.LogInfo($"Expansión {set.Name}: {set.Cards.Count} cartas ({set.Cards.Count(x => x.HasMyCurioArt)} con imagen MyCurio, {set.Cards.Count(x => x.IsPlayable)} jugables).");
        }

        static CardDef ParseCard(JObject c, SetDef set)
        {
            string numText = (string)c["number"] ?? "";
            var d = new CardDef
            {
                Id = (string)c["id"] ?? $"{set.Id}-{numText}",
                SetId = set.Id,
                NumberText = numText,
                Name = (string)c["name"],
                Rarity = (string)c["rarity"] ?? "",
            };
            if (string.IsNullOrEmpty(d.Name)) return null;
            int.TryParse(new string(numText.Where(char.IsDigit).ToArray()), out d.Number);

            string st = (string)c["supertype"] ?? "";
            d.Supertype = st.StartsWith("Pok") ? Supertype.Pokemon : st.StartsWith("Trainer") ? Supertype.Trainer : Supertype.Energy;
            d.Subtypes = c["subtypes"]?.ToObject<List<string>>() ?? new List<string>();
            d.Stage = d.Subtypes.FirstOrDefault(s => s == "Basic" || s.StartsWith("Stage")) ?? "";
            d.IsBasicEnergy = d.Supertype == Supertype.Energy && d.Subtypes.Contains("Basic");
            int.TryParse((string)c["hp"], out d.Hp);
            d.Type = c["types"]?.First?.ToString() ?? "";
            d.EvolvesFrom = (string)c["evolvesFrom"];
            d.RetreatCost = (int?)c["convertedRetreatCost"] ?? (c["retreatCost"] as JArray)?.Count ?? 0;
            d.RulesText = string.Join(" ", c["rules"]?.ToObject<List<string>>() ?? new List<string>());
            d.UnsupportedReason = (string)c["unsupported"] ?? "";

            if (c["abilities"] is JArray ab)
                foreach (JObject a in ab)
                    d.Abilities.Add(new PowerDef { Name = (string)a["name"], Text = (string)a["text"] ?? "", Kind = (string)a["type"] ?? "" });
            d.Power = d.Abilities.FirstOrDefault();

            if (c["attacks"] is JArray atks)
                foreach (JObject a in atks)
                {
                    string dmg = (string)a["damage"] ?? "";
                    var ad = new AttackDef
                    {
                        Name = (string)a["name"],
                        Cost = a["cost"]?.ToObject<List<string>>() ?? new List<string>(),
                        Text = (string)a["text"] ?? ""
                    };
                    int.TryParse(new string(dmg.Where(char.IsDigit).ToArray()), out ad.BaseDamage);
                    ad.DamageSuffix = new string(dmg.Where(ch => !char.IsDigit(ch)).ToArray()).Trim();
                    if (ad.Cost.Count == 1 && ad.Cost[0] == "Free") ad.Cost.Clear();
                    d.Attacks.Add(ad);
                }
            if (c["weaknesses"] is JArray w && w.Count > 0) d.WeaknessType = (string)w[0]["type"];
            if (c["resistances"] is JArray r && r.Count > 0)
            {
                d.ResistanceType = (string)r[0]["type"];
                int.TryParse(new string(((string)r[0]["value"] ?? "").Where(char.IsDigit).ToArray()), out d.ResistanceValue);
            }

            // Sprite: explícito ("sprite" en el JSON/override) o prefijo + número (BS- + 001)
            d.Sprite = (string)c["sprite"];
            if (string.IsNullOrEmpty(d.Sprite) && !string.IsNullOrEmpty(set.SpritePrefix) && d.Number > 0)
                d.Sprite = set.SpritePrefix + d.Number.ToString(new string('0', Math.Max(1, set.SpriteDigits)));
            return d;
        }
    }

    public static class DeckLoader
    {
        /// <summary>Formato de texto antiguo: líneas "4 Charmander", "4 #46" o "4 base1-46". Usado para importar.</summary>
        public static List<CardDef> ParseText(IEnumerable<string> lines, List<string> warnings = null)
        {
            var list = new List<CardDef>();
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("//")) continue;
                int sp = line.IndexOf(' ');
                if (sp <= 0 || !int.TryParse(line.Substring(0, sp), out int qty)) { warnings?.Add($"Línea inválida: {line}"); continue; }
                string key = line.Substring(sp + 1).Trim();
                CardDef def = null;
                if (key.StartsWith("#") && int.TryParse(key.Substring(1), out int n))
                    def = CardDatabase.All.FirstOrDefault(c => c.SetId == "base1" && c.Number == n);
                else if (!CardDatabase.ById.TryGetValue(key, out def))
                    CardDatabase.ByName.TryGetValue(key, out def);
                if (def == null) { warnings?.Add($"Carta desconocida: {key}"); continue; }
                for (int i = 0; i < qty; i++) list.Add(def);
            }
            return list;
        }

        public static List<string> ToText(List<CardDef> cards)
        {
            var lines = new List<string>();
            foreach (var g in cards.GroupBy(c => c.Id).OrderBy(g => g.First().Supertype).ThenBy(g => g.First().Number))
                lines.Add($"{g.Count()} {g.Key}");
            return lines;
        }

        /// <summary>Genera un mazo aleatorio legal para el NPC con las cartas jugables del formato (no depende de la colección).</summary>
        public static List<CardDef> GenerateRandomNpcDeck(Random rng = null, FormatDef format = null)
        {
            rng = rng ?? new Random();
            format = format ?? FormatRegistry.Default;
            int size = format?.DeckSize ?? 60;
            int maxCopies = format?.MaxCopies ?? 4;
            var pool = CardDatabase.All.Where(c => c.IsPlayable && (format == null || format.Allows(c))).ToList();
            var deck = new List<CardDef>();

            CardDef Energy(string type) => pool.FirstOrDefault(c => c.IsBasicEnergy && c.EnergyProvides == type);
            var types = new[] { "Fire", "Water", "Grass", "Lightning", "Psychic", "Fighting" }
                .Where(t => Energy(t) != null && pool.Any(c => c.IsBasicPokemon && c.Type == t))
                .OrderBy(_ => rng.Next()).ToList();
            if (types.Count == 0) throw new Exception("El formato no tiene cartas suficientes para generar un mazo.");
            string typeA = types[0];
            string typeB = types.Count > 1 ? types[1] : types[0];
            var chosenTypes = new HashSet<string> { typeA, typeB, "Colorless" };

            int CountOf(CardDef c) => deck.Count(x => x.Name == c.Name);
            void AddCopies(CardDef c, int count)
            {
                if (c == null) return;
                for (int i = 0; i < count && deck.Count < size && (c.IsBasicEnergy || CountOf(c) < maxCopies); i++)
                    deck.Add(c);
            }

            var basics = pool.Where(c => c.IsBasicPokemon && chosenTypes.Contains(c.Type)).OrderBy(_ => rng.Next()).ToList();
            foreach (var b in basics)
            {
                if (deck.Count >= 22) break;
                int bQty = rng.Next(2, 4);
                AddCopies(b, bQty);
                var stage1 = pool.FirstOrDefault(c => c.Stage == "Stage 1" && c.EvolvesFrom == b.Name);
                if (stage1 != null && deck.Count < 23)
                {
                    AddCopies(stage1, Math.Max(1, bQty - 1));
                    var stage2 = pool.FirstOrDefault(c => c.Stage == "Stage 2" && c.EvolvesFrom == stage1.Name);
                    if (stage2 != null && deck.Count < 24) AddCopies(stage2, 1);
                }
            }

            for (int guard = 0; deck.Count(c => c.IsBasicPokemon) < 10 && basics.Count > 0 && guard < 50; guard++)
            {
                var b = basics[rng.Next(basics.Count)];
                if (CountOf(b) < maxCopies) deck.Add(b);
            }

            // Entrenadores: preferir los clásicos si existen, si no cualquiera del formato
            string[] preferred = { "Bill", "Professor Oak", "Potion", "Super Potion", "Switch", "Gust of Wind", "PlusPower", "Defender", "Energy Removal", "Full Heal", "Energy Retrieval" };
            var trainers = pool.Where(c => c.Supertype == Supertype.Trainer).ToList();
            var trainerPool = trainers.Where(t => preferred.Contains(t.Name)).ToList();
            if (trainerPool.Count == 0) trainerPool = trainers;
            int target = Math.Min(size - 24, deck.Count + 13);
            for (int guard = 0; deck.Count < target && trainerPool.Count > 0 && guard < 300; guard++)
            {
                var tr = trainerPool[rng.Next(trainerPool.Count)];
                if (CountOf(tr) < (tr.Name == "Bill" || tr.Name == "Potion" ? 3 : 2)) deck.Add(tr);
            }

            int countA = Math.Max(1, deck.Count(c => c.Supertype == Supertype.Pokemon && c.Type == typeA));
            int countB = Math.Max(1, deck.Count(c => c.Supertype == Supertype.Pokemon && c.Type == typeB));
            int remaining = size - deck.Count;
            int energyA = (int)Math.Round(remaining * (countA / (double)(countA + countB)));
            var eA = Energy(typeA); var eB = Energy(typeB);
            for (int i = 0; i < energyA; i++) deck.Add(eA);
            while (deck.Count < size) deck.Add(eB);
            return deck;
        }

        /// <summary>
        /// Obtiene el mazo que usará el NPC según la preferencia configurada:
        /// - Mazo temático específico (si NpcDeckMode coincide con un Id o Nombre de ThemeDeckRegistry)
        /// - "ThemeOnly": elige al azar uno de los 25 mazos temáticos oficiales de Base Era
        /// - "RandomOnly": genera un mazo aleatorio del formato
        /// - "ThemeOrRandom" (por defecto): elige entre los mazos temáticos oficiales o uno aleatorio
        /// </summary>
        public static List<CardDef> GenerateNpcDeck(Random rng, FormatDef format, out string deckLabel)
        {
            rng = rng ?? new Random();
            string mode = Plugin.NpcDeckMode?.Value ?? "ThemeOrRandom";

            if (ThemeDeckRegistry.Decks.Count > 0 && !string.Equals(mode, "RandomOnly", StringComparison.OrdinalIgnoreCase))
            {
                // 1) Si el usuario seleccionó un mazo temático específico para el NPC
                var specific = ThemeDeckRegistry.Decks.FirstOrDefault(d =>
                    string.Equals(d.Id, mode, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(d.Name, mode, StringComparison.OrdinalIgnoreCase));
                if (specific != null)
                {
                    var cards = specific.Resolve();
                    if (cards.Count == 60)
                    {
                        deckLabel = $"{specific.Name} ({specific.SetName})";
                        return cards;
                    }
                }

                // 2) Mazos temáticos compatibles con el formato actual (o todos los de Base Era si es ThemeOnly / ThemeOrRandom)
                var validThemes = ThemeDeckRegistry.Decks
                    .Select(d => new { Deck = d, Cards = d.Resolve() })
                    .Where(x => x.Cards.Count == 60)
                    .ToList();

                var formatThemes = format != null
                    ? validThemes.Where(x => x.Cards.All(c => format.Allows(c))).ToList()
                    : validThemes;
                var pool = formatThemes.Count > 0 ? formatThemes : validThemes;

                bool pickTheme = string.Equals(mode, "ThemeOnly", StringComparison.OrdinalIgnoreCase) || rng.NextDouble() < 0.75;
                if (pickTheme && pool.Count > 0)
                {
                    var chosen = pool[rng.Next(pool.Count)];
                    deckLabel = $"{chosen.Deck.Name} ({chosen.Deck.SetName})";
                    return chosen.Cards;
                }
            }

            deckLabel = $"Mazo Aleatorio ({format?.Name ?? "Base Era"})";
            return GenerateRandomNpcDeck(rng, format);
        }
    }
}
