using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace PokeTCGPlay
{
    /// <summary>
    /// Efectos de ataques (por patrones de texto de base1.json) y de Entrenadores.
    /// Lo que no se reconoce se resuelve solo con el daño base y se avisa en el log.
    /// </summary>
    public static class Effects
    {
        static readonly RegexOptions I = RegexOptions.IgnoreCase;

        static Status? ParseStatus(string s)
        {
            if (s.Contains("Paralyzed")) return Status.Paralyzed;
            if (s.Contains("Confused")) return Status.Confused;
            if (s.Contains("Asleep")) return Status.Asleep;
            return null;
        }

        static void ApplyCondition(Game g, PokemonInPlay target, string text)
        {
            if (target == null) return;
            if (text.Contains("Poisoned")) { target.Poisoned = true; g.Say($"  {target.Card.Name} queda Envenenado."); }
            var st = ParseStatus(text);
            if (st != null)
            {
                target.Status = st.Value;
                target.StatusTurns = st.Value == Status.Paralyzed ? 1 : 0;
                g.Say($"  {target.Card.Name} queda {st}.");
            }
        }

        public static void ResolveAttack(Game g, PlayerState p, PokemonInPlay a, AttackDef atk)
        {
            ResolveAttackSequence(g, p, a, atk, null);
        }

        /// <summary>
        /// Resuelve el ataque respetando estrictamente el orden visual:
        /// 1) Si el ataque requiere lanzar una o más monedas, se lanzan y animan PRIMERO.
        /// 2) Solo cuando termina la animación de la(s) moneda(s), el Pokémon ejecuta la embestida y aplica daño/estados en el impacto.
        /// </summary>
        public static void ResolveAttackSequence(Game g, PlayerState p, PokemonInPlay a, AttackDef atk, Action onSequenceDone)
        {
            var opp = g.OpponentOf(p);
            var def = opp.Active;
            string t = atk.Text ?? "";
            bool handled = string.IsNullOrEmpty(t);

            // Descartar energía como coste ("Discard 1 Fire Energy card attached to Charizard in order to use this attack")
            var mDisc = Regex.Match(t, @"Discard (\d+|all) (\w+ )?Energy cards? attached to [\w ]+ in order to use this attack", I);
            if (mDisc.Success)
            {
                string type = mDisc.Groups[2].Value.Trim();
                int n = mDisc.Groups[1].Value == "all" ? 99 : int.Parse(mDisc.Groups[1].Value);
                for (int i = 0; i < n; i++)
                {
                    var e = a.Energy.FirstOrDefault(x => type == "" || x.EnergyProvides == type);
                    if (e == null) break;
                    a.Energy.Remove(e); p.Discard.Add(e);
                }
                handled = true;
            }

            // Paso 1: "Flip a coin. If tails, this attack does nothing."
            if (Regex.IsMatch(t, @"Flip a coin\. If tails, this attack does nothing", I))
            {
                handled = true;
                g.RunCoinFlip(p, atk.Name, heads =>
                {
                    if (!heads)
                    {
                        g.Say("  El ataque no hace nada.");
                        onSequenceDone?.Invoke();
                        return;
                    }
                    ContinueAttackCoinsAndImpact(g, p, a, opp, def, atk, t, handled, onSequenceDone);
                });
                return;
            }

            ContinueAttackCoinsAndImpact(g, p, a, opp, def, atk, t, handled, onSequenceDone);
        }

        static void ContinueAttackCoinsAndImpact(
            Game g,
            PlayerState p,
            PokemonInPlay a,
            PlayerState opp,
            PokemonInPlay def,
            AttackDef atk,
            string t,
            bool alreadyHandled,
            Action onSequenceDone)
        {
            bool handled = alreadyHandled;
            int flipPhraseCount = Regex.Matches(t, @"Flip (\d+|a) coins?", I).Count;

            var mTimes = Regex.Match(t, @"Flip (\d+|a) coins?\. This attack does (\d+) damage times the number of heads", I);
            var mPlus = Regex.Match(t, @"Flip a coin\. If heads, this attack does \d+ damage plus (\d+) more damage", I);
            var mPerEnergy = Regex.Match(t, @"plus (\d+) more damage for each (\w+ )?Energy (attached to [\w ]+ )?but not used to pay", I);
            var mCoinStatus = Regex.Match(t, @"Flip a coin\. If heads, the Defending Pok.mon is now ([^.]+)\.(?: If tails, it is now ([^.]+)\.)?", I);
            var mDirectStatus = !mCoinStatus.Success ? Regex.Match(t, @"The Defending Pok.mon is now ([^.]+)\.", I) : Match.Empty;
            var mSelf = Regex.Match(t, @"(?:does|deals?) (\d+) damage to itself", I);
            bool selfUsesTailsCoin = mSelf.Success && Regex.IsMatch(t, @"if tails, (?:this attack does \d+ damage plus )?[\w ]+ (?:does|deals?) \d+ damage to itself", I);
            bool selfSharesPlusCoin = selfUsesTailsCoin && mPlus.Success && flipPhraseCount <= 1;
            var mBench = Regex.Match(t, @"does (\d+) damage to each (Pok.mon on each player's Bench|of your opponent's Benched)", I);
            var mHeal = Regex.Match(t, @"remove (\d+|all) damage counters? from", I);

            var coinReasons = new List<string>();
            int timesCoinStart = -1, timesCoinCount = 0;
            int plusCoinIdx = -1;
            int statusCoinIdx = -1;
            int selfCoinIdx = -1;

            if (mTimes.Success)
            {
                timesCoinStart = coinReasons.Count;
                timesCoinCount = mTimes.Groups[1].Value.Equals("a", StringComparison.OrdinalIgnoreCase) ? 1 : int.Parse(mTimes.Groups[1].Value);
                for (int idx = 0; idx < timesCoinCount; idx++)
                    coinReasons.Add(timesCoinCount > 1 ? $"{atk.Name} ({idx + 1}/{timesCoinCount})" : atk.Name);
                handled = true;
            }
            if (mPlus.Success)
            {
                plusCoinIdx = coinReasons.Count;
                coinReasons.Add(atk.Name);
                handled = true;
            }
            if (mCoinStatus.Success)
            {
                statusCoinIdx = coinReasons.Count;
                coinReasons.Add(atk.Name);
                handled = true;
            }
            if (selfUsesTailsCoin && !selfSharesPlusCoin)
            {
                selfCoinIdx = coinReasons.Count;
                coinReasons.Add("autodaño");
                handled = true;
            }

            // Si el ataque requiere monedas, se tiran TODAS antes de ejecutar la animación de ataque y el daño/estado
            g.RunCoinSequence(p, coinReasons, coinResults =>
            {
                int dmg = atk.BaseDamage;

                if (mTimes.Success)
                {
                    int heads = 0;
                    for (int idx = 0; idx < timesCoinCount; idx++)
                        if (coinResults[timesCoinStart + idx]) heads++;
                    dmg = int.Parse(mTimes.Groups[2].Value) * heads;
                }

                if (mPlus.Success && plusCoinIdx >= 0 && coinResults[plusCoinIdx])
                {
                    dmg += int.Parse(mPlus.Groups[1].Value);
                }

                if (mPerEnergy.Success)
                {
                    string type = mPerEnergy.Groups[2].Value.Trim();
                    int extra = (type == "" ? a.EnergyTotal : a.EnergyCount(type)) - atk.Cost.Count(c => type == "" || c == type);
                    dmg += Math.Min(2, Math.Max(0, extra)) * int.Parse(mPerEnergy.Groups[1].Value);
                    handled = true;
                }

                string conditionToApply = null;
                if (mCoinStatus.Success && statusCoinIdx >= 0)
                {
                    if (coinResults[statusCoinIdx])
                        conditionToApply = mCoinStatus.Groups[1].Value;
                    else if (mCoinStatus.Groups[2].Success)
                        conditionToApply = mCoinStatus.Groups[2].Value;
                }
                else if (mDirectStatus.Success)
                {
                    conditionToApply = mDirectStatus.Groups[1].Value;
                    handled = true;
                }

                int selfDmgToApply = 0;
                if (mSelf.Success)
                {
                    handled = true;
                    bool applySelf = !selfUsesTailsCoin
                        || (selfSharesPlusCoin ? !coinResults[plusCoinIdx] : (selfCoinIdx >= 0 && !coinResults[selfCoinIdx]));
                    if (applySelf)
                        selfDmgToApply = int.Parse(mSelf.Groups[1].Value);
                }

                if (mBench.Success || mHeal.Success)
                    handled = true;

                bool hasAnyImpactEffect = dmg > 0 || conditionToApply != null || selfDmgToApply > 0 || mBench.Success || mHeal.Success || !handled;
                if (!hasAnyImpactEffect && coinReasons.Count > 0)
                {
                    g.Say("  El ataque no hace nada.");
                    onSequenceDone?.Invoke();
                    return;
                }

                // Ahora que terminaron las monedas, el Pokémon embiste y aplica el daño/efecto justo en el impacto
                g.RunAttackLunge(
                    p,
                    a.Card,
                    atk.Name,
                    onImpact: () =>
                    {
                        if (def != null)
                            g.DealDamage(a, def, dmg);

                        if (conditionToApply != null && def != null)
                            ApplyCondition(g, def, conditionToApply);

                        if (selfDmgToApply > 0)
                        {
                            a.Damage += selfDmgToApply;
                            g.Say($"  {a.Card.Name} se hace {selfDmgToApply} de daño.");
                        }

                        if (mBench.Success)
                        {
                            int bd = int.Parse(mBench.Groups[1].Value);
                            foreach (var b in opp.Bench) b.Damage += bd;
                            if (mBench.Groups[2].Value.Contains("each player"))
                                foreach (var b in p.Bench) b.Damage += bd;
                            g.Say($"  {bd} de daño a la Banca.");
                        }

                        if (mHeal.Success)
                        {
                            int n = mHeal.Groups[1].Value == "all" ? 999 : int.Parse(mHeal.Groups[1].Value) * 10;
                            a.Damage = Math.Max(0, a.Damage - n);
                        }

                        if (!handled)
                            g.Say($"  (Efecto aún no implementado: \"{t}\")");
                    },
                    onFinished: onSequenceDone);
            });
        }

        // ------------------------- Entrenadores -------------------------
        public static string PlayTrainer(Game g, PlayerState p, CardDef c, PokemonInPlay target)
        {
            var opp = g.OpponentOf(p);
            bool Own(PokemonInPlay x) => x != null && p.InPlay.Contains(x);
            bool Theirs(PokemonInPlay x) => x != null && opp.InPlay.Contains(x);
            string Need(string what) => $"{c.Name}: selecciona {what}.";

            switch (c.Name)
            {
                case "Bill": g.Draw(p, 2); break;

                case "Professor Oak":
                    p.Discard.AddRange(p.Hand); p.Hand.Clear(); g.Draw(p, 7); break;

                case "Impostor Professor Oak":
                    opp.Deck.AddRange(opp.Hand); opp.Hand.Clear(); g.Shuffle(opp.Deck); g.Draw(opp, 7); break;

                case "Lass":
                    foreach (var pl in g.Players)
                    {
                        var tr = pl.Hand.Where(x => x.Supertype == Supertype.Trainer).ToList();
                        foreach (var x in tr) { pl.Hand.Remove(x); pl.Deck.Add(x); }
                        g.Shuffle(pl.Deck);
                    }
                    break;

                case "Potion":
                case "Super Potion":
                    {
                        if (!Own(target) || target.Damage == 0) return Need("un Pokémon tuyo dañado");
                        if (c.Name == "Super Potion")
                        {
                            if (target.Energy.Count == 0) return "Super Potion requiere descartar 1 Energía del Pokémon.";
                            var e = target.Energy[0]; target.Energy.RemoveAt(0); p.Discard.Add(e);
                        }
                        int heal = c.Name == "Potion" ? 20 : 40;
                        target.Damage = Math.Max(0, target.Damage - heal);
                        break;
                    }

                case "Full Heal":
                    if (p.Active == null) return "Sin Activo.";
                    p.Active.ClearConditions(); break;

                case "Switch":
                    if (p.Bench.Count == 0) return "No tienes Banca.";
                    {
                        int idx = Own(target) && target != p.Active ? p.Bench.IndexOf(target) : 0;
                        var b = p.Bench[idx]; p.Bench[idx] = p.Active; p.Active = b;
                        p.Bench[idx].ClearConditions();
                    }
                    break;

                case "Gust of Wind":
                    if (!Theirs(target) || target == opp.Active) return Need("un Pokémon de la Banca rival");
                    {
                        int idx = opp.Bench.IndexOf(target);
                        opp.Bench[idx] = opp.Active; opp.Active = target;
                        opp.Bench[idx].ClearConditions();
                    }
                    break;

                case "Energy Removal":
                    if (!Theirs(target) || target.Energy.Count == 0) return Need("un Pokémon rival con Energía");
                    { var e = target.Energy[0]; target.Energy.RemoveAt(0); opp.Discard.Add(e); }
                    break;

                case "Super Energy Removal":
                    {
                        if (!Theirs(target) || target.Energy.Count == 0) return Need("un Pokémon rival con Energía");
                        var mine = p.InPlay.FirstOrDefault(x => x.Energy.Count > 0);
                        if (mine == null) return "Necesitas descartar 1 Energía de un Pokémon tuyo.";
                        var me = mine.Energy[0]; mine.Energy.RemoveAt(0); p.Discard.Add(me);
                        for (int i = 0; i < 2 && target.Energy.Count > 0; i++) { var e = target.Energy[0]; target.Energy.RemoveAt(0); opp.Discard.Add(e); }
                        break;
                    }

                case "PlusPower":
                    if (p.Active == null) return "Sin Activo.";
                    p.Active.PlusPower++; p.Active.Attached.Add(c); break;

                case "Defender":
                    if (!Own(target)) return Need("un Pokémon tuyo");
                    target.DefenderUntilTurn = g.Turn + 1; target.Attached.Add(c); break;

                case "Revive":
                    {
                        if (p.Bench.Count >= 5) return "Banca llena.";
                        var b = p.Discard.FirstOrDefault(x => x.IsBasicPokemon);
                        if (b == null) return "No hay Básicos en el descarte.";
                        p.Discard.Remove(b);
                        var pip = new PokemonInPlay { TurnPlaced = g.Turn, Damage = (b.Hp / 2 + 9) / 10 * 10 }; pip.Stack.Add(b);
                        p.Bench.Add(pip); break;
                    }

                case "Pokémon Center":
                    foreach (var x in p.InPlay.Where(x => x.Damage > 0))
                    { x.Damage = 0; p.Discard.AddRange(x.Energy); x.Energy.Clear(); }
                    break;

                case "Scoop Up":
                    {
                        if (!Own(target)) return Need("un Pokémon tuyo");
                        if (target == p.Active && p.Bench.Count == 0) return "Necesitas otro Pokémon en Banca.";
                        p.Hand.Add(target.Stack[0]);
                        p.Discard.AddRange(target.Stack.Skip(1)); p.Discard.AddRange(target.Energy); p.Discard.AddRange(target.Attached);
                        if (target == p.Active) { p.Active = null; g.CheckKnockOuts(); } else p.Bench.Remove(target);
                        break;
                    }

                case "Energy Retrieval":
                    {
                        if (p.Hand.Count == 0) return "Debes descartar 1 carta de la mano.";
                        var basics = p.Discard.Where(x => x.IsBasicEnergy).Take(2).ToList();
                        if (basics.Count == 0) return "No hay Energías básicas en el descarte.";
                        var disc = p.Hand[p.Hand.Count - 1]; p.Hand.Remove(disc); p.Discard.Add(disc);
                        foreach (var e in basics) { p.Discard.Remove(e); p.Hand.Add(e); }
                        break;
                    }

                case "Maintenance":
                    {
                        if (p.Hand.Count < 2) return "Necesitas 2 cartas en mano.";
                        for (int i = 0; i < 2; i++) { var x = p.Hand[p.Hand.Count - 1]; p.Hand.Remove(x); p.Deck.Add(x); }
                        g.Shuffle(p.Deck); g.Draw(p, 1); break;
                    }

                case "Computer Search":
                    {
                        if (p.Hand.Count < 2) return "Debes descartar 2 cartas.";
                        for (int i = 0; i < 2; i++) { var x = p.Hand[p.Hand.Count - 1]; p.Hand.Remove(x); p.Discard.Add(x); }
                        // Busca la primera carta útil: básico si hay pocos en juego, si no evolución, si no energía
                        var pick = p.Deck.FirstOrDefault(x => x.IsBasicPokemon && p.Bench.Count < 2)
                                ?? p.Deck.FirstOrDefault(x => x.Supertype == Supertype.Pokemon && p.InPlay.Any(ip => ip.Card.Name == x.EvolvesFrom))
                                ?? p.Deck.FirstOrDefault();
                        if (pick != null) { p.Deck.Remove(pick); p.Hand.Add(pick); g.Say($"  Busca {pick.Name}."); }
                        g.Shuffle(p.Deck); break;
                    }

                case "Item Finder":
                    {
                        if (p.Hand.Count < 2) return "Debes descartar 2 cartas.";
                        var tr = p.Discard.LastOrDefault(x => x.Supertype == Supertype.Trainer);
                        if (tr == null) return "No hay Entrenadores en el descarte.";
                        for (int i = 0; i < 2; i++) { var x = p.Hand[p.Hand.Count - 1]; p.Hand.Remove(x); p.Discard.Add(x); }
                        p.Discard.Remove(tr); p.Hand.Add(tr); break;
                    }

                case "Pokémon Trader":
                    {
                        var give = p.Hand.FirstOrDefault(x => x.Supertype == Supertype.Pokemon);
                        if (give == null) return "Necesitas un Pokémon en la mano.";
                        var want = p.Deck.FirstOrDefault(x => x.Supertype == Supertype.Pokemon && p.InPlay.Any(ip => ip.Card.Name == x.EvolvesFrom))
                                ?? p.Deck.FirstOrDefault(x => x.Supertype == Supertype.Pokemon);
                        if (want == null) return "No hay Pokémon en el mazo.";
                        p.Hand.Remove(give); p.Deck.Add(give); p.Deck.Remove(want); p.Hand.Add(want);
                        g.Shuffle(p.Deck); break;
                    }

                case "Pokédex":
                    g.Say("  (Pokédex: se mira el top 5; reordenar aún no implementado)"); break;

                case "Pokémon Flute":
                    {
                        if (opp.Bench.Count >= 5) return "La Banca rival está llena.";
                        var b = opp.Discard.FirstOrDefault(x => x.IsBasicPokemon);
                        if (b == null) return "No hay Básicos en el descarte rival.";
                        opp.Discard.Remove(b);
                        var pip = new PokemonInPlay { TurnPlaced = g.Turn }; pip.Stack.Add(b); opp.Bench.Add(pip); break;
                    }

                case "Devolution Spray":
                    {
                        if (!Own(target) || target.Stack.Count < 2) return Need("un Pokémon evolucionado tuyo");
                        var top = target.Card; target.Stack.RemoveAt(target.Stack.Count - 1); p.Discard.Add(top);
                        target.ClearConditions(); break;
                    }

                case "Pokémon Breeder":
                    {
                        var st2 = p.Hand.FirstOrDefault(x => x.Stage == "Stage 2");
                        if (st2 == null || !Own(target)) return Need("el Básico objetivo (con un Fase 2 en mano)");
                        if (!(CardDatabase.ByName.TryGetValue(st2.EvolvesFrom, out var st1) && st1.EvolvesFrom == target.Card.Name))
                            return $"{st2.Name} no corresponde a {target.Card.Name}.";
                        return g.Evolve(p, p.Hand.IndexOf(st2), target, breeder: true);
                    }

                case "Clefairy Doll":
                    {
                        if (p.Bench.Count >= 5) return "Banca llena.";
                        var doll = new CardDef { Name = "Clefairy Doll", Supertype = Supertype.Pokemon, Stage = "Basic", Hp = 10, Number = c.Number, Sprite = c.Sprite };
                        var pip = new PokemonInPlay { TurnPlaced = g.Turn, IsDoll = true }; pip.Stack.Add(doll);
                        p.Bench.Add(pip); break;
                    }

                default: return $"{c.Name} aún no implementado.";
            }
            g.Say($"{p.Name} juega {c.Name}.");
            return null;
        }
    }
}
