using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PokeTCGPlay
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

    public class PowerDef { public string Name; public string Text; }

    /// <summary>Carta unificada: reglas de base1.json + sprite de MyCurio.</summary>
    public class CardDef
    {
        public string Id;          // base1-1
        public int Number;         // 1..102
        public string Name;
        public Supertype Supertype;
        public string Stage = "";  // Basic / Stage 1 / Stage 2
        public bool IsBasicEnergy;
        public int Hp;
        public string Type = "";   // Fire, Water...
        public string EvolvesFrom;
        public PowerDef Power;
        public List<AttackDef> Attacks = new List<AttackDef>();
        public string WeaknessType;
        public string ResistanceType;
        public int ResistanceValue;
        public int RetreatCost;
        public string RulesText = "";
        public string Rarity = "";
        public string Sprite;      // BS-001 (MyCurio)

        public bool IsBasicPokemon => Supertype == Supertype.Pokemon && Stage == "Basic";
        /// <summary>Tipo de energía que provee (Double Colorless = 2 Colorless).</summary>
        public string EnergyProvides => Name.Replace(" Energy", "").Replace("Double ", "");
        public int EnergyAmount => Name.StartsWith("Double") ? 2 : 1;
        public override string ToString() => $"{Name} (#{Number})";
    }

    public static class CardDatabase
    {
        public static readonly Dictionary<int, CardDef> ByNumber = new Dictionary<int, CardDef>();
        public static readonly Dictionary<string, CardDef> ByName = new Dictionary<string, CardDef>(StringComparer.OrdinalIgnoreCase);

        public static void Load(string dataDir)
        {
            ByNumber.Clear(); ByName.Clear();
            var rules = JArray.Parse(File.ReadAllText(Path.Combine(dataDir, "base1.json"), Encoding.UTF8));
            foreach (JObject c in rules)
            {
                var d = new CardDef
                {
                    Id = (string)c["id"],
                    Number = int.Parse((string)c["number"]),
                    Name = (string)c["name"],
                    Rarity = (string)c["rarity"] ?? "",
                };
                string st = (string)c["supertype"] ?? "";
                d.Supertype = st.StartsWith("Pok") ? Supertype.Pokemon : st == "Trainer" ? Supertype.Trainer : Supertype.Energy;
                var subs = c["subtypes"]?.ToObject<List<string>>() ?? new List<string>();
                d.Stage = subs.FirstOrDefault(s => s == "Basic" || s.StartsWith("Stage")) ?? "";
                d.IsBasicEnergy = d.Supertype == Supertype.Energy && subs.Contains("Basic");
                int.TryParse((string)c["hp"], out d.Hp);
                d.Type = c["types"]?.First?.ToString() ?? "";
                d.EvolvesFrom = (string)c["evolvesFrom"];
                d.RetreatCost = (int?)c["convertedRetreatCost"] ?? 0;
                d.RulesText = string.Join(" ", c["rules"]?.ToObject<List<string>>() ?? new List<string>());

                if (c["abilities"] is JArray ab && ab.Count > 0)
                    d.Power = new PowerDef { Name = (string)ab[0]["name"], Text = (string)ab[0]["text"] };
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
                        string digits = new string(dmg.Where(char.IsDigit).ToArray());
                        int.TryParse(digits, out ad.BaseDamage);
                        ad.DamageSuffix = new string(dmg.Where(ch => !char.IsDigit(ch)).ToArray()).Trim();
                        if (ad.Cost.Count == 1 && ad.Cost[0] == "Free") ad.Cost.Clear();
                        d.Attacks.Add(ad);
                    }
                if (c["weaknesses"] is JArray w && w.Count > 0) d.WeaknessType = (string)w[0]["type"];
                if (c["resistances"] is JArray r && r.Count > 0)
                {
                    d.ResistanceType = (string)r[0]["type"];
                    int.TryParse(new string(((string)r[0]["value"]).Where(char.IsDigit).ToArray()), out d.ResistanceValue);
                }
                ByNumber[d.Number] = d;
                ByName[d.Name] = d;
            }

            // Cruce con MyCurio: Sprite "BS-001" -> número 1
            var mc = JObject.Parse(File.ReadAllText(Path.Combine(dataDir, "mycurio-pokemon-base01.json"), Encoding.UTF8));
            foreach (JObject card in mc["CardExpansions"][0]["Cards"])
            {
                string sprite = (string)card["Sprite"];
                if (int.TryParse(new string(sprite.Where(char.IsDigit).ToArray()), out int n) && ByNumber.TryGetValue(n, out var def))
                    def.Sprite = sprite;
            }
        }
    }

    public static class DeckLoader
    {
        /// <summary>Formato: líneas "4 Charmander" o "4 #46". Comentarios con '#' al inicio de línea seguido de espacio.</summary>
        public static List<CardDef> Load(string path)
        {
            var list = new List<CardDef>();
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("//")) continue;
                int sp = line.IndexOf(' ');
                int qty = int.Parse(line.Substring(0, sp));
                string key = line.Substring(sp + 1).Trim();
                CardDef def = key.StartsWith("#") ? CardDatabase.ByNumber[int.Parse(key.Substring(1))] : CardDatabase.ByName[key];
                for (int i = 0; i < qty; i++) list.Add(def);
            }
            if (list.Count != 60) throw new Exception($"{Path.GetFileName(path)}: el mazo tiene {list.Count} cartas (deben ser 60).");
            foreach (var g in list.GroupBy(c => c.Name))
                if (!g.First().IsBasicEnergy && g.Count() > 4) throw new Exception($"Más de 4 copias de {g.Key}.");
            if (!list.Any(c => c.IsBasicPokemon)) throw new Exception("El mazo necesita al menos un Pokémon Básico.");
            return list;
        }
    }
}
