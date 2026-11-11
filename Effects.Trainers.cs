// TCG-PlayKyn: Resolution logic for Trainer, Supporter and Stadium cards
using System;
using System.Collections.Generic;
using System.Linq;

namespace TCGPlayKyn
{
    /// <summary>
    /// SubmÃ³dulo de resoluciÃ³n de cartas de Entrenador, Partidarios y Estadios.
    /// Si necesitas programar un nuevo Entrenador, agrÃ©galo en el switch 'PlayTrainer'.
    /// </summary>
    public static partial class Effects
    {
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
                        g.ForceSwitch(p, idx);
                    }
                    break;

                case "Gust of Wind":
                    if (!Theirs(target) || target == opp.Active) return Need("un Pokémon de la Banca rival");
                    {
                        int idx = opp.Bench.IndexOf(target);
                        g.ForceSwitch(opp, idx);
                    }
                    break;

                case "Energy Removal":
                    if (!Theirs(target) || target.Energy.Count == 0) return Need("un Pokémon rival con Energía");
                    if (target.PreventAllUntilTurn >= g.Turn) return $"{target.Card.Name} previene los efectos de entrenadores.";
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
                    {
                        int count = Math.Min(5, p.Deck.Count);
                        if (count == 0) return Loc.T("No hay cartas en el mazo.", "There are no cards in the deck.");
                        if (p.IsAI)
                        {
                            var top = p.Deck.Take(count).ToList();
                            top.Sort((x, y) =>
                            {
                                int Score(CardDef cd) => cd.IsBasicPokemon ? 3 : (cd.IsBasicEnergy ? 2 : 1);
                                return Score(y).CompareTo(Score(x));
                            });
                            p.Deck.RemoveRange(0, count);
                            p.Deck.InsertRange(0, top);
                            g.Say(Loc.T("  CPU mira el top 5 de su mazo y lo reorganiza.", "  CPU looks at the top 5 cards and rearranges them."));
                            break;
                        }

                        g.RunAfterAnimations(() =>
                        {
                            if (TableView.Instance != null && TableView.Instance.IsBoard3DReady)
                            {
                                TableView.Instance.StartPokedex3D(p, count, newOrder =>
                                {
                                    p.Deck.RemoveRange(0, count);
                                    p.Deck.InsertRange(0, newOrder);
                                    g.Say(Loc.T("  Pokédex: orden de las cartas reorganizado con éxito.", "  Pokédex: deck cards successfully rearranged."));
                                });
                            }
                            else
                            {
                                g.Say(Loc.T("  Pokédex: cartas del mazo revisadas.", "  Pokédex: deck cards viewed."));
                            }
                        });
                        break;
                    }

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
                case "Mysterious Fossil":
                    {
                        if (p.Bench.Count >= 5) return "Banca llena.";
                        var doll = new CardDef { Id = c.Id, SetId = c.SetId, Name = c.Name, Supertype = Supertype.Pokemon, Stage = "Basic", Hp = 10, Number = c.Number, Sprite = c.Sprite };
                        var pip = new PokemonInPlay { TurnPlaced = g.Turn, IsDoll = true }; pip.Stack.Add(doll);
                        int bIdx = p.Bench.Count;
                        p.Bench.Add(pip);
                        g.Emit(AnimKind.ToBench, p, doll, bIdx);
                        break;
                    }

                // --- Jungle & Fossil ---
                case "Poké Ball":
                    {
                        g.RunCoinFlip(p, "Poké Ball", heads =>
                        {
                            if (!heads) { g.Say("  Poké Ball falló (Cruz)."); return; }
                            var pick = p.Deck.FirstOrDefault(x => x.Supertype == Supertype.Pokemon && p.InPlay.Any(ip => ip.Card.Name == x.EvolvesFrom))
                                    ?? p.Deck.FirstOrDefault(x => x.IsBasicPokemon)
                                    ?? p.Deck.FirstOrDefault(x => x.Supertype == Supertype.Pokemon);
                            if (pick != null)
                            {
                                p.Deck.Remove(pick); p.Hand.Add(pick);
                                g.Say($"  Poké Ball encuentra a {pick.Name}.");
                                g.Shuffle(p.Deck);
                            }
                        });
                        break;
                    }

                case "Mr. Fuji":
                    {
                        var benchTarget = Own(target) && target != p.Active ? target : p.Bench.FirstOrDefault();
                        if (benchTarget == null) return Need("un Pokémon de tu Banca");
                        p.Bench.Remove(benchTarget);
                        p.Deck.AddRange(benchTarget.AllCards());
                        g.Shuffle(p.Deck);
                        break;
                    }

                case "Gambler":
                    {
                        p.Deck.AddRange(p.Hand); p.Hand.Clear(); g.Shuffle(p.Deck);
                        g.RunCoinFlip(p, "Gambler", heads =>
                        {
                            g.Draw(p, heads ? 8 : 1);
                        });
                        break;
                    }

                case "Energy Search":
                    {
                        var e = p.Deck.FirstOrDefault(x => x.IsBasicEnergy);
                        if (e == null) return "No hay Energías Básicas en el mazo.";
                        p.Deck.Remove(e); p.Hand.Add(e);
                        g.Say($"  Busca {e.Name}.");
                        g.Shuffle(p.Deck);
                        break;
                    }

                case "Recycle":
                    {
                        if (p.Discard.Count == 0) return "El descarte está vacío.";
                        g.RunCoinFlip(p, "Recycle", heads =>
                        {
                            if (!heads || p.Discard.Count == 0) return;
                            var top = p.Discard[p.Discard.Count - 1];
                            p.Discard.RemoveAt(p.Discard.Count - 1);
                            p.Deck.Insert(0, top);
                            g.Say($"  Recycle coloca {top.Name} encima del mazo.");
                        });
                        break;
                    }

                // --- Team Rocket ---
                case "Nightly Garbage Run":
                    {
                        var valid = p.Discard.Where(x => x.Supertype == Supertype.Pokemon || x.IsBasicEnergy).Take(3).ToList();
                        if (valid.Count == 0) return "No hay Pokémon ni Energías Básicas en el descarte.";
                        foreach (var card in valid) { p.Discard.Remove(card); p.Deck.Add(card); }
                        g.Shuffle(p.Deck);
                        g.Say($"  Devuelve {valid.Count} carta(s) del descarte al mazo.");
                        break;
                    }

                case "The Boss's Way":
                    {
                        var darkEvo = p.Deck.FirstOrDefault(x => x.Supertype == Supertype.Pokemon && x.Stage != "Basic" && x.Name.IndexOf("Dark", StringComparison.OrdinalIgnoreCase) >= 0);
                        if (darkEvo == null) return "No hay Evoluciones 'Dark' en el mazo.";
                        p.Deck.Remove(darkEvo); p.Hand.Add(darkEvo);
                        g.Say($"  The Boss's Way encuentra a {darkEvo.Name}.");
                        g.Shuffle(p.Deck);
                        break;
                    }

                case "Imposter Oak's Revenge":
                    {
                        if (p.Hand.Count == 0) return "Debes descartar 1 carta de tu mano.";
                        var disc = p.Hand[p.Hand.Count - 1]; p.Hand.Remove(disc); p.Discard.Add(disc);
                        opp.Deck.AddRange(opp.Hand); opp.Hand.Clear(); g.Shuffle(opp.Deck);
                        g.Draw(opp, 4);
                        break;
                    }

                case "Sleep!":
                    {
                        if (opp.Active == null) return "El rival no tiene Pokémon Activo.";
                        g.RunCoinFlip(p, "Sleep!", heads =>
                        {
                            if (heads && opp.Active != null)
                            {
                                opp.Active.Status = Status.Asleep;
                                g.Say($"  {opp.Active.Card.Name} queda Dormido.");
                            }
                        });
                        break;
                    }

                case "Rocket's Sneak Attack":
                    {
                        var tr = opp.Hand.FirstOrDefault(x => x.Supertype == Supertype.Trainer);
                        if (tr != null)
                        {
                            opp.Hand.Remove(tr); opp.Deck.Add(tr); g.Shuffle(opp.Deck);
                            g.Say($"  Rocket's Sneak Attack devuelve {tr.Name} al mazo rival.");
                        }
                        else g.Say("  El rival no tenía Entrenadores en la mano.");
                        break;
                    }

                case "Challenge!":
                    {
                        g.Draw(p, 2);
                        break;
                    }

                case "Digger":
                    {
                        g.RunCoinFlip(p, "Digger", heads =>
                        {
                            if (heads && opp.Active != null)
                            {
                                opp.Active.Damage += 10;
                                g.Say($"  Digger hace 10 de daño a {opp.Active.Card.Name}.");
                                g.CheckKnockOuts();
                            }
                            else if (!heads && p.Active != null)
                            {
                                p.Active.Damage += 10;
                                g.Say($"  Digger hace 10 de daño a {p.Active.Card.Name}.");
                                g.CheckKnockOuts();
                            }
                        });
                        break;
                    }

                // --- Gym Heroes & Gym Challenge ---
                case "Brock":
                    {
                        foreach (var pip in p.InPlay)
                            if (pip.Damage > 0) pip.Damage = Math.Max(0, pip.Damage - 10);
                        break;
                    }

                case "Brock's Training Method":
                    {
                        var brockCard = p.Deck.FirstOrDefault(x => x.Supertype == Supertype.Pokemon && x.Name.IndexOf("Brock", StringComparison.OrdinalIgnoreCase) >= 0);
                        if (brockCard == null) return "No hay Pokémon de Brock en el mazo.";
                        p.Deck.Remove(brockCard); p.Hand.Add(brockCard);
                        g.Say($"  Busca a {brockCard.Name}.");
                        g.Shuffle(p.Deck);
                        break;
                    }

                case "Misty":
                    {
                        if (p.Hand.Count < 2) return "Misty requiere descartar 2 cartas de tu mano.";
                        for (int i = 0; i < 2; i++) { var x = p.Hand[p.Hand.Count - 1]; p.Hand.Remove(x); p.Discard.Add(x); }
                        g.MistyBonusTurn = g.Turn;
                        g.Say("  ¡Los ataques de Pokémon de Misty hacen +20 de daño este turno!");
                        break;
                    }

                case "Misty's Wrath":
                    {
                        int n = Math.Min(7, p.Deck.Count);
                        if (n == 0) return "Mazo vacío.";
                        var top7 = p.Deck.Take(n).ToList();
                        p.Deck.RemoveRange(0, n);
                        var keep = top7.Take(2).ToList();
                        p.Hand.AddRange(keep);
                        foreach (var rest in top7.Skip(2)) p.Discard.Add(rest);
                        g.Say($"  Misty's Wrath toma {keep.Count} carta(s) y descarta el resto.");
                        break;
                    }

                case "Lt. Surge":
                    {
                        if (p.Active == null || p.Bench.Count >= 5) return "Necesitas espacio en la Banca.";
                        var basicInHand = p.Hand.FirstOrDefault(x => x.IsBasicPokemon);
                        if (basicInHand == null) return "Necesitas un Pokémon Básico en tu mano.";
                        p.Hand.Remove(basicInHand);
                        var oldActive = p.Active;
                        oldActive.ClearConditions();
                        p.Bench.Add(oldActive);
                        var newPip = new PokemonInPlay { TurnPlaced = g.Turn };
                        newPip.Stack.Add(basicInHand);
                        p.Active = newPip;
                        g.Emit(AnimKind.ToActive, p, basicInHand, -1);
                        break;
                    }

                case "Erika":
                    {
                        g.Draw(p, 3);
                        if (opp.Deck.Count > 3) g.Draw(opp, Math.Min(2, opp.Deck.Count));
                        break;
                    }

                case "Erika's Perfume":
                    {
                        var oppBasics = opp.Hand.Where(x => x.IsBasicPokemon).ToList();
                        foreach (var b in oppBasics)
                        {
                            if (opp.Bench.Count >= 5) break;
                            opp.Hand.Remove(b);
                            var pip = new PokemonInPlay { TurnPlaced = g.Turn }; pip.Stack.Add(b);
                            int bIdx = opp.Bench.Count;
                            opp.Bench.Add(pip);
                            g.Emit(AnimKind.ToBench, opp, b, bIdx);
                        }
                        break;
                    }

                case "Sabrina":
                    {
                        var sabrinas = p.InPlay.Where(x => x.Card.Name.IndexOf("Sabrina", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                        if (sabrinas.Count < 2) return "Necesitas al menos 2 Pokémon de Sabrina en juego.";
                        var dst = Own(target) && sabrinas.Contains(target) ? target : p.Active;
                        var src = sabrinas.FirstOrDefault(x => x != dst && x.Energy.Count > 0);
                        if (src == null || dst == null) return "No hay Energías para mover entre Pokémon de Sabrina.";
                        dst.Energy.AddRange(src.Energy);
                        src.Energy.Clear();
                        break;
                    }

                case "Sabrina's Gaze":
                    {
                        foreach (var pl in g.Players)
                        {
                            int cnt = pl.Hand.Count;
                            if (cnt == 0) continue;
                            pl.Deck.AddRange(pl.Hand); pl.Hand.Clear(); g.Shuffle(pl.Deck);
                            g.Draw(pl, cnt);
                        }
                        break;
                    }

                case "Sabrina's Psychic Control":
                    {
                        g.RunCoinFlip(p, "Sabrina's Psychic Control", heads =>
                        {
                            if (!heads) return;
                            g.Draw(p, 2);
                        });
                        break;
                    }

                case "Koga":
                    {
                        g.KogaPoisonTurn = g.Turn;
                        g.Say("  ¡Los ataques de Pokémon de Koga envenenan automáticamente este turno!");
                        break;
                    }

                case "Blaine":
                    {
                        g.BlaineExtraFireTurn = g.Turn;
                        g.Say("  ¡Puedes unir hasta 2 Energías Fuego a un Pokémon de Blaine este turno!");
                        break;
                    }

                case "Blaine's Gamble":
                    {
                        int toDiscard = Math.Min(2, p.Hand.Count);
                        if (toDiscard == 0) return "Necesitas al menos 1 otra carta en la mano.";
                        for (int i = 0; i < toDiscard; i++) { var x = p.Hand[p.Hand.Count - 1]; p.Hand.Remove(x); p.Discard.Add(x); }
                        g.RunCoinFlip(p, "Blaine's Gamble", heads =>
                        {
                            if (heads) g.Draw(p, toDiscard * 2);
                        });
                        break;
                    }

                case "Fervor":
                    {
                        int n = Math.Min(3, p.Deck.Count);
                        if (n == 0) return "Mazo vacío.";
                        var top3 = p.Deck.Take(n).ToList();
                        p.Deck.RemoveRange(0, n);
                        foreach (var card in top3)
                        {
                            if (card.IsBasicEnergy && card.EnergyProvides == "Fire") p.Hand.Add(card);
                            else p.Discard.Add(card);
                        }
                        break;
                    }

                case "Giovanni":
                    {
                        g.GiovanniAllowEvolveTurn = g.Turn;
                        g.Say("  ¡Tus Pokémon de Giovanni pueden evolucionar este mismo turno!");
                        break;
                    }

                case "Secret Mission":
                    {
                        int n = Math.Min(2, p.Hand.Count);
                        for (int i = 0; i < n; i++) { var x = p.Hand[p.Hand.Count - 1]; p.Hand.Remove(x); p.Discard.Add(x); }
                        if (n > 0) g.Draw(p, n);
                        break;
                    }

                case "Max Revive":
                    {
                        if (p.Bench.Count >= 5) return "Banca llena.";
                        var energies = p.Hand.Where(x => x.Supertype == Supertype.Energy).Take(2).ToList();
                        if (energies.Count < 2) return "Debes descartar 2 Energías de tu mano.";
                        var b = p.Discard.FirstOrDefault(x => x.IsBasicPokemon);
                        if (b == null) return "No hay Básicos en el descarte.";
                        foreach (var e in energies) { p.Hand.Remove(e); p.Discard.Add(e); }
                        p.Discard.Remove(b);
                        var pip = new PokemonInPlay { TurnPlaced = g.Turn }; pip.Stack.Add(b);
                        int bIdx = p.Bench.Count;
                        p.Bench.Add(pip);
                        g.Emit(AnimKind.ToBench, p, b, bIdx);
                        break;
                    }

                case "Warp Point":
                    {
                        if (opp.Bench.Count > 0 && opp.Active != null)
                        {
                            var ob = opp.Bench[0]; opp.Bench[0] = opp.Active; opp.Active = ob;
                            opp.Bench[0].ClearConditions();
                        }
                        if (p.Bench.Count > 0 && p.Active != null)
                        {
                            int idx = Own(target) && target != p.Active ? p.Bench.IndexOf(target) : 0;
                            var pb = p.Bench[idx]; p.Bench[idx] = p.Active; p.Active = pb;
                            p.Bench[idx].ClearConditions();
                        }
                        break;
                    }

                case "Master Ball":
                    {
                        int n = Math.Min(7, p.Deck.Count);
                        var top7 = p.Deck.Take(n).ToList();
                        var pk = top7.FirstOrDefault(x => x.Supertype == Supertype.Pokemon);
                        if (pk != null) { p.Deck.Remove(pk); p.Hand.Add(pk); g.Say($"  Master Ball encuentra a {pk.Name}."); }
                        g.Shuffle(p.Deck);
                        break;
                    }

                case "Good Manners":
                    {
                        var b = p.Deck.FirstOrDefault(x => x.IsBasicPokemon);
                        if (b != null) { p.Deck.Remove(b); p.Hand.Add(b); g.Say($"  Good Manners encuentra a {b.Name}."); }
                        g.Shuffle(p.Deck);
                        break;
                    }

                case "Computer Error":
                    {
                        g.Draw(p, Math.Min(4, p.Deck.Count));
                        break;
                    }

                case "Pewter City Gym":
                case "Cerulean City Gym":
                case "Vermilion City Gym":
                case "Celadon City Gym":
                case "Fuchsia City Gym":
                case "Saffron City Gym":
                case "Cinnabar City Gym":
                case "Viridian City Gym":
                case "Chaos Gym":
                case "No Removal Gym":
                case "Narrow Gym":
                    {
                        g.ActiveStadium = c.Name;
                        g.Say($"  Estadio activo: {c.Name}.");
                        break;
                    }

                default:
                    {
                        // Si un Entrenador menor de expansión no tiene caso dedicado aún, se juega sin bloquear el turno
                        g.Say($"  ({c.Name}: efecto jugado)");
                        break;
                    }
            }
            g.Say($"{p.Name} juega {c.Name}.");
            return null;
        }
    }
}
