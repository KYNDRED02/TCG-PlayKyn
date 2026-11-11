// TCG-PlayKyn: Core turn flow, draw phase, bench placement, evolution, energy and retreat
using System;
using System.Collections.Generic;
using System.Linq;

namespace TCGPlayKyn
{
    /// <summary>
    /// Submódulo de gestión del flujo de turnos y acciones del jugador durante la partida.
    /// 
    /// ¿Para qué sirve?
    /// - 'PlayBasic': Baja un Pokémon Básico de la mano a la Banca (o a Activo durante el Setup).
    /// - 'Evolve': Evoluciona un Pokémon en juego respetando el turno de bajada.
    /// - 'AttachEnergy': Une 1 energía por turno (o extra si lo permite un Entrenador como Blaine).
    /// - 'Retreat': Descarta las energías necesarias y retira al Pokémon Activo a la Banca.
    /// - 'Attack': Valida que el Pokémon no esté Dormido o Paralizado, comprueba el coste de energía y ataca.
    /// - 'EndTurn': Concluye las acciones del turno actual y avanza el juego tras el Checkup.
    /// 
    /// Modificaciones:
    /// - Si necesitas añadir una nueva acción disponible en el turno principal, impleméntala aquí.
    /// </summary>
    public partial class Game
    {
public void ExecutePendingAiSetup()
        {
            if (!PendingAiSetup) return;
            PendingAiSetup = false;
            if (Phase == Phase.Setup && !Players[1].SetupDone)
            {
                AI.DoSetup(this, Players[1]);
            }
        }

// ---------------- Preparación ----------------
        public string SetupPlace(PlayerState p, int handIdx)
        {
            var c = p.Hand[handIdx];
            if (!c.IsBasicPokemon) return "Solo Pokémon Básicos.";
            var pip = new PokemonInPlay { TurnPlaced = 0 }; pip.Stack.Add(c);
            if (p.Active == null)
            {
                p.Active = pip;
                p.Hand.RemoveAt(handIdx);
                Emit(AnimKind.ToActive, p, c, -1);
                // Al hacer el primer movimiento colocando al Pokémon Activo del jugador, la partida comienza automáticamente
                if (!p.IsAI)
                {
                    FinishSetup(p);
                }
            }
            else if (p.Bench.Count < 5)
            {
                int bIdx = p.Bench.Count;
                p.Bench.Add(pip);
                p.Hand.RemoveAt(handIdx);
                Emit(AnimKind.ToBench, p, c, bIdx);
            }
            else return "Banca llena.";
            return null;
        }

        public string FinishSetup(PlayerState p)
        {
            if (p.Active == null) return "Elige un Pokémon Activo.";
            p.SetupDone = true;
            if (!Players[1].SetupDone)
            {
                AI.DoSetup(this, Players[1]);
            }
            if (Players.All(x => x.SetupDone) && Phase == Phase.Setup)
            {
                Say("¡Ambos jugadores revelan sus Pokémon iniciales! Comienza la partida.");
                Phase = Phase.Main;
                Turn = 1;
                StartTurn();
            }
            return null;
        }

        // ---------------- Turnos ----------------
        void StartTurn()
        {
            var p = Current;
            p.EnergyPlayedThisTurn = false;
            p.BlaineFireAttachedThisTurn = 0;
            p.RainDanceActive = false;
            foreach (var pip in p.InPlay)
            {
                pip.PowersUsedThisTurn.Clear();
            }
            Say($"--- Turno {Turn}: {p.Name} ---");
            if (!Draw(p))
            {
                EndGame(
                    OpponentOf(p),
                    p.IsAI
                        ? Loc.T("Tu oponente se quedó sin mazo", "Your opponent ran out of cards in their deck")
                        : Loc.T("Te quedaste sin mazo", "You ran out of cards in your deck"));
                return;
            }
            if (p.IsAI) PendingAiTurn = true;
        }

        public void ExecutePendingAiTurn()
        {
            if (!PendingAiTurn || IsBusyResolving) return;
            PendingAiTurn = false;
            if (Phase == Phase.Main && Current.IsAI)
                AI.TakeTurn(this, Current);
        }

        public string EndTurn()
        {
            if (Phase != Phase.Main) return "No se puede terminar el turno ahora.";
            IsBusyResolving = true;
            RunAfterAnimations(PerformEndTurnSequence);
            return null;
        }

        void PerformEndTurnSequence()
        {
            if (Phase != Phase.Main)
            {
                IsBusyResolving = false;
                return;
            }

            var p = Current;
            foreach (var pip in p.InPlay) pip.PlusPower = 0;
            foreach (var pl in Players)
                foreach (var pip in pl.InPlay)
                    foreach (var t in pip.Attached.ToList())
                        if ((t.Name == "PlusPower" && pl == p) || (t.Name == "Defender" && pip.DefenderUntilTurn <= Turn))
                        { pip.Attached.Remove(t); pl.Discard.Add(t); }

            // Parálisis dura hasta el final del siguiente turno del dueño
            if (p.Active != null && p.Active.Status == Status.Paralyzed)
            {
                p.Active.StatusTurns = Math.Max(0, p.Active.StatusTurns - 1);
                if (p.Active.StatusTurns <= 0) p.Active.Status = Status.None;
            }

            RunPokemonCheckupSequence(() =>
            {
                CheckKnockOuts();
                RunAfterAnimations(() =>
                {
                    IsBusyResolving = false;
                    if (Phase == Phase.GameOver) return;
                    if (Phase == Phase.ChoosePromotion)
                    {
                        pendingAdvanceTurnAfterPromotion = true;
                        return;
                    }
                    AdvanceTurn();
                });
            });
        }

        bool pendingEndTurnAfterPromotion;
        bool pendingAdvanceTurnAfterPromotion;

        void AdvanceTurn()
        {
            pendingEndTurnAfterPromotion = false;
            pendingAdvanceTurnAfterPromotion = false;
            CurrentIndex = 1 - CurrentIndex; Turn++;
            StartTurn();
        }

// ---------------- Acciones principales ----------------
        string MainCheck(PlayerState p) =>
            IsBusyResolving ? "Esperando resolución en curso."
            : Phase != Phase.Main ? "No es fase principal."
            : p != Current ? "No es tu turno."
            : null;

        public string PlayBasic(PlayerState p, int handIdx)
        {
            var err = MainCheck(p); if (err != null) return err;
            var c = p.Hand[handIdx];
            if (!c.IsBasicPokemon) return "No es un Pokémon Básico.";
            int maxBench = ActiveStadium == "Narrow Gym" ? 4 : 5;
            if (p.Bench.Count >= maxBench) return $"Banca llena (máx. {maxBench}).";
            int bIdx = p.Bench.Count;
            var pip = new PokemonInPlay { TurnPlaced = Turn }; pip.Stack.Add(c);
            p.Bench.Add(pip); p.Hand.RemoveAt(handIdx);
            Say($"{p.Name} pone {c.Name} en la Banca.");
            Emit(AnimKind.ToBench, p, c, bIdx);
            TriggerOnPlayPower(p, c, pip);
            return null;
        }

        public string Evolve(PlayerState p, int handIdx, PokemonInPlay target, bool breeder = false)
        {
            var err = MainCheck(p); if (err != null) return err;

            // Prehistoric Power de Aerodactyl: ningún jugador puede jugar cartas de Evolución
            if (Players.Any(pl => pl.InPlay.Any(pip => pip.Card.Power?.Name == "Prehistoric Power" && !IsPowerDisabled(pip))))
                return "Prehistoric Power de Aerodactyl impide evolucionar.";
            var c = p.Hand[handIdx];
            if (c.Supertype != Supertype.Pokemon || c.Stage == "Basic") return "No es una evolución.";
            if (target.IsDoll && target.Card.Name != "Mysterious Fossil") return "No se puede evolucionar un Clefairy Doll.";
            if (!breeder && c.EvolvesFrom != target.Card.Name) return $"{c.Name} evoluciona de {c.EvolvesFrom}.";
            bool giovanniBypass = GiovanniAllowEvolveTurn == Turn && target.Card.Name.IndexOf("Giovanni", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!giovanniBypass && (Turn <= 2 || target.TurnPlaced == Turn || target.TurnEvolved == Turn))
                return "No puede evolucionar en el primer turno ni el turno en que se jugó/evolucionó.";
            int bIdx = target == p.Active ? -1 : p.Bench.IndexOf(target);
            target.IsDoll = false;
            target.Stack.Add(c); target.TurnEvolved = Turn; target.ClearConditions(); target.ClearTemporaryAttackEffects();
            if (ActiveStadium == "Viridian City Gym" && c.Name.IndexOf("Giovanni", StringComparison.OrdinalIgnoreCase) >= 0 && target.Damage > 0)
            {
                target.Damage = Math.Max(0, target.Damage - 20);
                Say($"  Viridian City Gym cura 20 PS a {c.Name}.");
            }
            p.Hand.RemoveAt(handIdx);
            Say($"{p.Name} evoluciona a {c.Name}.");
            Emit(AnimKind.Evolve, p, c, bIdx);
            TriggerOnPlayPower(p, c, target);
            return null;
        }

        public string AttachEnergy(PlayerState p, int handIdx, PokemonInPlay target)
        {
            var err = MainCheck(p); if (err != null) return err;
            var c = p.Hand[handIdx];
            if (c.Supertype != Supertype.Energy) return "No es Energía.";
            bool blaineExtra = BlaineExtraFireTurn == Turn && c.EnergyProvides == "Fire"
                && target.Card.Name.IndexOf("Blaine", StringComparison.OrdinalIgnoreCase) >= 0
                && p.BlaineFireAttachedThisTurn < 2;

            // Danza de la Lluvia (Rain Dance) de Blastoise: permite unir energías de Agua ilimitadas a Pokémon de Agua
            bool rainDance = (p.RainDanceActive || p.InPlay.Any(pip => pip.Card.Power?.Name == "Rain Dance" && !IsPowerDisabled(pip)))
                && c.EnergyProvides == "Water" && target.Card.Type == "Water";

            if (p.EnergyPlayedThisTurn && !blaineExtra && !rainDance) return "Ya uniste una Energía este turno.";
            if (target.IsDoll) return $"No se puede unir Energía a {target.Card.Name}.";
            int bIdx = target == p.Active ? -1 : p.Bench.IndexOf(target);
            target.Energy.Add(c);
            p.Hand.RemoveAt(handIdx);
            if (!rainDance) p.EnergyPlayedThisTurn = true;
            if (c.EnergyProvides == "Fire" && target.Card.Name.IndexOf("Blaine", StringComparison.OrdinalIgnoreCase) >= 0)
                p.BlaineFireAttachedThisTurn++;
            Say($"{p.Name} une {c.Name} debajo de {target.Card.Name}.");
            if (c.Name == "Full Heal Energy")
            {
                target.ClearConditions();
                Say($"  Full Heal Energy cura los estados de {target.Card.Name}.");
            }
            else if (c.Name == "Potion Energy" && target.Damage > 0)
            {
                target.Damage = Math.Max(0, target.Damage - 10);
                Say($"  Potion Energy cura 10 PS a {target.Card.Name}.");
            }
            else if (c.Name == "Rainbow Energy")
            {
                target.Damage += 10;
                Say($"  Rainbow Energy hace 10 de daño a {target.Card.Name}.");
            }
            Emit(AnimKind.AttachEnergy, p, c, bIdx);
            CheckKnockOuts();
            return null;
        }

        public string Retreat(PlayerState p, int benchIdx, List<int> energyIdxToDiscard = null)
        {
            var err = MainCheck(p); if (err != null) return err;
            var a = p.Active;
            if (a == null) return "Sin Pokémon Activo.";
            if (benchIdx < 0 || benchIdx >= p.Bench.Count) return "Pokémon de Banca inválido.";
            if (a.IsDoll) return $"{a.Card.Name} no puede retirarse.";
            if (a.Status == Status.Asleep || a.Status == Status.Paralyzed) return "Dormido o Paralizado: no puede retirarse.";
            if (a.CantRetreatUntilTurn >= Turn) return $"{a.Card.Name} no puede retirarse este turno (efecto de Ácido).";
            int retreatCost = a.Card.RetreatCost;

            // Retreat Aid de Dodrio: reduce el coste de retirada en 1 Incolora por cada Dodrio en la banca
            int dodrioDiscount = p.Bench.Count(b => b.Card.Power?.Name == "Retreat Aid" && !IsPowerDisabled(b));
            retreatCost = Math.Max(0, retreatCost - dodrioDiscount);

            // Sticky Goo de Dark Muk: si el Activo del oponente es Dark Muk, aumenta en 2 Incoloras el coste
            var opp = OpponentOf(p);
            if (opp.Active != null && opp.Active.Card.Power?.Name == "Sticky Goo" && !IsPowerDisabled(opp.Active))
                retreatCost += 2;
            if (ActiveStadium == "Cerulean City Gym" && a.Card.Name.IndexOf("Misty", StringComparison.OrdinalIgnoreCase) >= 0)
                retreatCost = Math.Max(0, retreatCost - 1);
            if (a.EnergyTotal < retreatCost) return $"Necesita {retreatCost} Energía para retirarse.";
            if (retreatedThisTurn == Turn) return "Ya te retiraste este turno.";

            if (a.Status == Status.Confused)
            {
                retreatedThisTurn = Turn;
                IsBusyResolving = true;
                RunAfterAnimations(() =>
                {
                    RunCoinFlip(p, "retirada confundido", heads =>
                    {
                        if (!heads)
                        {
                            Say($"  {a.Card.Name} no logra retirarse por confusión.");
                            IsBusyResolving = false;
                            return;
                        }
                        CompleteRetreatSwap(p, a, benchIdx);
                        RunAfterAnimations(() => IsBusyResolving = false);
                    });
                });
                return null;
            }

            CompleteRetreatSwap(p, a, benchIdx);
            return null;
        }

        void CompleteRetreatSwap(PlayerState p, PokemonInPlay a, int benchIdx)
        {
            if (p.Active != a || benchIdx < 0 || benchIdx >= p.Bench.Count) return;
            int toPay = a.Card.RetreatCost;
            if (ActiveStadium == "Cerulean City Gym" && a.Card.Name.IndexOf("Misty", StringComparison.OrdinalIgnoreCase) >= 0)
                toPay = Math.Max(0, toPay - 1);
            while (toPay > 0 && a.Energy.Count > 0)
            {
                var e = a.Energy.OrderBy(x => x.EnergyAmount).First();
                a.Energy.Remove(e); p.Discard.Add(e); toPay -= e.EnergyAmount;
            }
            a.ClearConditions();
            a.ClearTemporaryAttackEffects();
            var b = p.Bench[benchIdx];
            p.Bench[benchIdx] = a; p.Active = b;
            retreatedThisTurn = Turn;
            Say($"{p.Name} retira a {a.Card.Name}; entra {b.Card.Name}.");
            Emit(AnimKind.RetreatSwap, p, b.Card, benchIdx);
        }

        public void ForceSwitch(PlayerState player, int benchIndex)
        {
            if (player == null || player.Active == null || benchIndex < 0 || benchIndex >= player.Bench.Count) return;
            var oldActive = player.Active;
            var newActive = player.Bench[benchIndex];
            oldActive.ClearConditions();
            oldActive.ClearTemporaryAttackEffects();
            player.Bench[benchIndex] = oldActive;
            player.Active = newActive;
            Say($"  {oldActive.Card.Name} vuelve a la Banca; entra {newActive.Card.Name}.");
            Emit(AnimKind.RetreatSwap, player, newActive.Card, benchIndex);
        }
        int retreatedThisTurn = -1;

        public string PlayTrainer(PlayerState p, int handIdx, PokemonInPlay target)
        {
            var err = MainCheck(p); if (err != null) return err;

            // Hay Fever de Dark Vileplume: no se pueden jugar cartas de Entrenador
            if (Players.Any(pl => pl.InPlay.Any(pip => pip.Card.Power?.Name == "Hay Fever" && !IsPowerDisabled(pip))))
                return "Hay Fever de Dark Vileplume impide jugar cartas de Entrenador.";
            if (p.TrainerLockUntilTurn >= Turn) return "No puedes jugar cartas de Entrenador este turno (Headache).";
            var c = p.Hand[handIdx];
            if (c.Supertype != Supertype.Trainer) return "No es Entrenador.";
            p.Hand.RemoveAt(handIdx);

            var prevDeferred = deferredTrainerEvents;
            var captured = new List<AnimEvent>();
            deferredTrainerEvents = captured;
            string res;
            try
            {
                res = Effects.PlayTrainer(this, p, c, target);
            }
            finally
            {
                deferredTrainerEvents = prevDeferred;
            }

            if (res != null) { p.Hand.Insert(handIdx, c); return res; }
            if (!(c.Name == "PlusPower" || c.Name == "Defender" || c.Name == "Clefairy Doll" || c.Name == "Mysterious Fossil")) p.Discard.Add(c);
            // Primero vuela la carta de Entrenador y después sus efectos derivados (ej. robos de Bill / Oak)
            Emit(AnimKind.PlayTrainer, p, c, -1);
            foreach (var ev in captured)
                DispatchAnimEvent(ev);
            CheckKnockOuts();
            return null;
        }

        public string Attack(PlayerState p, int atkIdx)
        {
            var err = MainCheck(p); if (err != null) return err;
            var a = p.Active; var atk = a?.Card.Attacks.ElementAtOrDefault(atkIdx);
            if (atk == null) return "Ataque inválido.";
            if (a.IsDoll) return $"{a.Card.Name} no puede atacar.";
            if (!a.CanPay(atk.Cost)) return "Energía insuficiente.";
            if (a.Status == Status.Asleep || a.Status == Status.Paralyzed) return $"{a.Card.Name} no puede atacar ({a.Status}).";

            IsBusyResolving = true;

            // Esperar a cualquier animación previa del turno (ej. unir Energía o bajar Básico) antes de iniciar el ataque
            RunAfterAnimations(() =>
            {
                Say($"{a.Card.Name} usa {atk.Name}.");

                void FinishAttackSequence()
                {
                    CheckKnockOuts();
                    RunAfterAnimations(() =>
                    {
                        IsBusyResolving = false;
                        if (Phase == Phase.GameOver) return;
                        if (Phase == Phase.Main)
                            EndTurn();
                        else if (Phase == Phase.ChoosePromotion)
                            pendingEndTurnAfterPromotion = true;
                    });
                }

                void ProceedWithConfusionCheck()
                {
                    if (a.Status == Status.Confused)
                    {
                        // Primero se lanza y anima la moneda de confusión; solo al terminar ocurre el autodaño o el ataque
                        RunCoinFlip(p, "confusión", heads =>
                        {
                            if (!heads)
                            {
                                a.Damage += 20;
                                Say($"{a.Card.Name} se hace 20 de daño por confusión.");
                                FinishAttackSequence();
                                return;
                            }
                            Effects.ResolveAttackSequence(this, p, a, atk, FinishAttackSequence);
                        });
                    }
                    else
                    {
                        Effects.ResolveAttackSequence(this, p, a, atk, FinishAttackSequence);
                    }
                }

                if (a.SmokescreenUntilTurn >= Turn)
                {
                    RunCoinFlip(p, "Pantalla de Humo", heads =>
                    {
                        if (!heads)
                        {
                            Say($"  ¡{a.Card.Name} falla el ataque por la Pantalla de Humo!");
                            FinishAttackSequence();
                            return;
                        }
                        ProceedWithConfusionCheck();
                    });
                }
                else
                {
                    ProceedWithConfusionCheck();
                }
            });
            return null;
        }

        /// <summary>Comprueba si los Pokémon Powers de un Pokémon están anulados por condiciones especiales o por Muk (Gas Tóxico).</summary>

public string Promote(PlayerState p, int benchIdx)
        {
            if (Phase != Phase.ChoosePromotion || PromotionPending != p) return "No hay que elegir.";
            if (benchIdx < 0 || benchIdx >= p.Bench.Count) return "Pokémon de Banca inválido.";
            p.Active = p.Bench[benchIdx]; p.Bench.RemoveAt(benchIdx);
            Say($"{p.Name} pone a {p.Active.Card.Name} como Activo.");
            Emit(AnimKind.ToActive, p, p.Active.Card, benchIdx);
            PromotionPending = null; Phase = Phase.Main;
            CheckKnockOuts();

            if (Phase == Phase.Main && (pendingEndTurnAfterPromotion || pendingAdvanceTurnAfterPromotion))
            {
                bool shouldEndTurn = pendingEndTurnAfterPromotion;
                pendingEndTurnAfterPromotion = false;
                pendingAdvanceTurnAfterPromotion = false;
                IsBusyResolving = true;
                RunAfterAnimations(() =>
                {
                    IsBusyResolving = false;
                    if (Phase != Phase.Main) return;
                    if (shouldEndTurn) EndTurn();
                    else AdvanceTurn();
                });
            }
            return null;
        }

        void EndGame(PlayerState w, string reason)
        {
            Phase = Phase.GameOver; Winner = w; WinReason = reason;
            Say($"=== GANA {w.Name}: {reason} ===");
        }
    }
}
