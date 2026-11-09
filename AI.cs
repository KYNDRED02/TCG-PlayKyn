// PokeTCGPlay: Rule-based heuristic decision maker for NPC turns and deck setup
using System.Linq;

namespace PokeTCGPlay
{
    /// <summary>IA sencilla: baja básicos, evoluciona, une energía al Activo y ataca con el mejor ataque disponible.</summary>
    public static class AI
    {
        public static void DoSetup(Game g, PlayerState p)
        {
            var basics = p.Hand.Where(c => c.IsBasicPokemon).OrderByDescending(c => c.Hp).ToList();
            if (basics.Count > 0)
            {
                int firstIdx = p.Hand.IndexOf(basics[0]);
                if (firstIdx >= 0) g.SetupPlace(p, firstIdx);
                foreach (var b in basics.Skip(1).Take(5))
                {
                    int idx = p.Hand.IndexOf(b);
                    if (idx >= 0) g.SetupPlace(p, idx);
                }
            }
            p.SetupDone = true;
        }

        public static void Promote(Game g, PlayerState p)
        {
            var best = p.Bench.OrderByDescending(b => b.Card.Attacks.Any(a => b.CanPay(a.Cost)) ? 1 : 0).ThenByDescending(b => b.RemainingHp).First();
            int bIdx = p.Bench.IndexOf(best);
            p.Active = best;
            p.Bench.RemoveAt(bIdx);
            g.Say($"{p.Name} pone a {best.Card.Name} como Activo.");
            g.EmitPromotion(p, best.Card, bIdx);
        }

        public static void TakeTurn(Game g, PlayerState p)
        {
            if (g.Phase != Phase.Main) return;
            // Robo
            foreach (var n in new[] { "Bill", "Professor Oak" })
            {
                int i = p.Hand.FindIndex(c => c.Name == n);
                if (i >= 0 && (n == "Bill" || p.Hand.Count <= 3)) g.PlayTrainer(p, i, null);
            }
            // Básicos a la banca
            for (int i = p.Hand.Count - 1; i >= 0; i--)
                if (p.Hand[i].IsBasicPokemon && p.Bench.Count < 5) g.PlayBasic(p, i);
            // Evoluciones (respetando la regla de no evolucionar en el primer turno de cada jugador)
            if (g.Turn > 2)
            {
                for (int i = p.Hand.Count - 1; i >= 0; i--)
                {
                    var c = p.Hand[i];
                    if (c.Supertype != Supertype.Pokemon || c.Stage == "Basic") continue;
                    var t = p.InPlay.FirstOrDefault(x => x.Card.Name == c.EvolvesFrom && x.TurnPlaced != g.Turn && x.TurnEvolved != g.Turn);
                    if (t != null) g.Evolve(p, i, t);
                }
            }
            // Pociones
            int pi = p.Hand.FindIndex(c => c.Name == "Potion");
            if (pi >= 0 && p.Active.Damage >= 20) g.PlayTrainer(p, pi, p.Active);
            // Energía: al Activo si le falta, si no a la banca
            int ei = p.Hand.FindIndex(c => c.Supertype == Supertype.Energy);
            if (ei >= 0)
            {
                var e = p.Hand[ei];
                var target = NeedsEnergy(p.Active, e) ? p.Active : p.Bench.FirstOrDefault(b => NeedsEnergy(b, e)) ?? p.Active;
                g.AttachEnergy(p, ei, target);
            }
            // PlusPower si sirve para noquear
            var opp = g.OpponentOf(p);
            var atk = BestAttack(p.Active);
            int pp = p.Hand.FindIndex(c => c.Name == "PlusPower");
            if (atk != null && pp >= 0 && opp.Active.RemainingHp - atk.BaseDamage <= 10 && opp.Active.RemainingHp > atk.BaseDamage)
                g.PlayTrainer(p, pp, p.Active);
            // Atacar o pasar
            if (g.Phase != Phase.Main || g.Current != p) return;
            if (atk != null && p.Active.Status != Status.Asleep && p.Active.Status != Status.Paralyzed)
                g.Attack(p, p.Active.Card.Attacks.IndexOf(atk));
            else g.EndTurn();
        }

        static bool NeedsEnergy(PokemonInPlay pip, CardDef e)
        {
            if (pip == null || pip.IsDoll) return false;
            var hardest = pip.Card.Attacks.OrderByDescending(a => a.Cost.Count).FirstOrDefault();
            if (hardest == null || pip.CanPay(hardest.Cost)) return false;
            return hardest.Cost.Contains(e.EnergyProvides) || hardest.Cost.Contains("Colorless");
        }

        static AttackDef BestAttack(PokemonInPlay a) =>
            a?.Card.Attacks.Where(x => a.CanPay(x.Cost)).OrderByDescending(x => x.BaseDamage + (x.Text.Length > 0 ? 5 : 0)).FirstOrDefault();
    }
}
