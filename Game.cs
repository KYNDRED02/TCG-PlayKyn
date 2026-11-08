using System;
using System.Collections.Generic;
using System.Linq;

namespace PokeTCGPlay
{
    public enum Status { None, Asleep, Confused, Paralyzed }
    public enum Phase { CoinToss, Setup, Main, ChoosePromotion, GameOver }

    public enum AnimKind
    {
        Draw,
        ToActive,
        ToBench,
        Evolve,
        AttachEnergy,
        PlayTrainer,
        RetreatSwap,
        AttackLunge,
        KnockOut,
        TakePrize,
        CoinFlip
    }

    public struct AnimEvent
    {
        public AnimKind Kind;
        public int PlayerIndex; // 0 = Jugador, 1 = NPC
        public CardDef Card;
        public int BenchIndex;  // -1 = Active, 0..4 = Bench
        public bool CoinHeads;
        public string Label;
    }

    public class PokemonInPlay
    {
        public List<CardDef> Stack = new List<CardDef>(); // [0] básico ... [n] carta superior
        public List<CardDef> Energy = new List<CardDef>();
        public List<CardDef> Attached = new List<CardDef>(); // PlusPower / Defender
        public int Damage;
        public Status Status;
        public int StatusTurns; // Duración restante en turnos si aplica (ej. Paralizado = 1)
        public bool Poisoned;
        public int TurnPlaced;
        public int TurnEvolved = -1;
        public int DefenderUntilTurn = -1;
        public int PlusPower;
        public bool IsDoll; // Clefairy Doll

        public CardDef Card => Stack[Stack.Count - 1];
        public int RemainingHp => Card.Hp - Damage;
        public string Label => $"{Card.Name} {RemainingHp}/{Card.Hp}" + (Status != Status.None ? $" [{Status}]" : "") + (Poisoned ? " [Poison]" : "");

        public int EnergyCount(string type) =>
            Energy.Where(e => e.EnergyProvides == type || e.EnergyProvides == "Rainbow").Sum(e => e.EnergyAmount);
        public int EnergyTotal => Energy.Sum(e => e.EnergyAmount);

        public void ClearConditions() { Status = Status.None; StatusTurns = 0; Poisoned = false; }

        public IEnumerable<CardDef> AllCards() => Stack.Concat(Energy).Concat(Attached);

        /// <summary>Comprueba si la energía unida paga el coste (Colorless lo paga cualquiera; Rainbow paga cualquier color).</summary>
        public bool CanPay(List<string> cost)
        {
            var pool = new Dictionary<string, int>();
            foreach (var e in Energy) { pool.TryGetValue(e.EnergyProvides, out int v); pool[e.EnergyProvides] = v + e.EnergyAmount; }
            int colorless = 0;
            foreach (var c in cost)
            {
                if (c == "Colorless") { colorless++; continue; }
                if (pool.TryGetValue(c, out int v) && v > 0)
                {
                    pool[c] = v - 1;
                }
                else if (pool.TryGetValue("Rainbow", out int rb) && rb > 0)
                {
                    pool["Rainbow"] = rb - 1;
                }
                else
                {
                    return false;
                }
            }
            return pool.Values.Sum() >= colorless;
        }
    }

    public class PlayerState
    {
        public string Name;
        public bool IsAI;
        public List<CardDef> Deck = new List<CardDef>();
        public List<CardDef> Hand = new List<CardDef>();
        public List<CardDef> Discard = new List<CardDef>();
        public List<CardDef> Prizes = new List<CardDef>();
        public PokemonInPlay Active;
        public List<PokemonInPlay> Bench = new List<PokemonInPlay>();
        public bool EnergyPlayedThisTurn;
        public int BlaineFireAttachedThisTurn;
        public bool SetupDone;

        public IEnumerable<PokemonInPlay> InPlay => (Active != null ? new[] { Active } : new PokemonInPlay[0]).Concat(Bench);
    }

    public class Game
    {
        public readonly PlayerState[] Players = new PlayerState[2];
        public int CurrentIndex;
        public int Turn;
        public Phase Phase = Phase.CoinToss;
        public PlayerState Winner;
        public string WinReason;
        public int GiovanniAllowEvolveTurn = -1;
        public int MistyBonusTurn = -1;
        public int KogaPoisonTurn = -1;
        public int BlaineExtraFireTurn = -1;
        public string ActiveStadium = "";
        public PlayerState PromotionPending;
        public readonly List<string> Log = new List<string>();
        public readonly Random Rng = new Random();
        public Action<AnimEvent> OnAnim;
        public bool PendingAiTurn;
        public bool PendingAiSetup;
        public bool IsBusyResolving { get; private set; }
        public int PlayerMulligans { get; private set; }
        public int NpcMulligans { get; private set; }
        public string MulliganSummary { get; private set; } = "";
        public int PlayerKOs { get; private set; }
        public int NpcKOs { get; private set; }
        public bool RewardsApplied;
        bool openingDealt;
        List<AnimEvent> deferredTrainerEvents;

        public PlayerState Current => Players[CurrentIndex];
        public PlayerState Opponent => Players[1 - CurrentIndex];
        public PlayerState OpponentOf(PlayerState p) => p == Players[0] ? Players[1] : Players[0];
        public int IndexOf(PlayerState p) => p == Players[0] ? 0 : 1;

        static bool Is3DAnimActive =>
            BoardAnimator.Instance != null &&
            TableView.Instance != null &&
            TableView.Instance.IsBoard3DReady;

        void DispatchAnimEvent(AnimEvent ev)
        {
            if (deferredTrainerEvents != null)
            {
                deferredTrainerEvents.Add(ev);
                return;
            }
            OnAnim?.Invoke(ev);
        }

        public void Emit(AnimKind kind, PlayerState p, CardDef card = null, int benchIdx = -1, bool heads = false, string label = null)
        {
            DispatchAnimEvent(new AnimEvent
            {
                Kind = kind,
                PlayerIndex = IndexOf(p),
                Card = card,
                BenchIndex = benchIdx,
                CoinHeads = heads,
                Label = label
            });
        }

        public void EmitPromotion(PlayerState p, CardDef card, int fromBenchIdx)
        {
            Emit(AnimKind.ToActive, p, card, fromBenchIdx);
        }

        public void Say(string s) { Log.Add(s); if (Log.Count > 200) Log.RemoveAt(0); Plugin.Log?.LogInfo(s); }

        public bool Flip(string why)
        {
            bool h = Rng.Next(2) == 0;
            Say($"  Moneda ({why}): {(h ? "CARA" : "CRUZ")}");
            Emit(AnimKind.CoinFlip, Current, null, -1, h, why);
            return h;
        }

        /// <summary>
        /// Lanza una moneda 3D y ejecuta `onLanded` ÚNICAMENTE cuando termina la animación de la moneda.
        /// </summary>
        public void RunCoinFlip(PlayerState p, string why, Action<bool> onLanded)
        {
            bool h = Rng.Next(2) == 0;
            if (Is3DAnimActive)
            {
                BoardAnimator.Instance.EnqueueCoinFlipStep(IndexOf(p), h, why, () =>
                {
                    Say($"  Moneda ({why}): {(h ? "CARA" : "CRUZ")}");
                    onLanded?.Invoke(h);
                });
            }
            else
            {
                Say($"  Moneda ({why}): {(h ? "CARA" : "CRUZ")}");
                Emit(AnimKind.CoinFlip, p, null, -1, h, why);
                onLanded?.Invoke(h);
            }
        }

        /// <summary>
        /// Lanza una lista de monedas en estricto orden secuencial y ejecuta `onAllLanded` cuando la última aterriza.
        /// </summary>
        public void RunCoinSequence(PlayerState p, IList<string> reasons, Action<bool[]> onAllLanded)
        {
            if (reasons == null || reasons.Count == 0)
            {
                onAllLanded?.Invoke(Array.Empty<bool>());
                return;
            }

            bool[] results = new bool[reasons.Count];
            void Step(int idx)
            {
                if (idx >= reasons.Count)
                {
                    onAllLanded?.Invoke(results);
                    return;
                }
                RunCoinFlip(p, reasons[idx], h =>
                {
                    results[idx] = h;
                    Step(idx + 1);
                });
            }
            Step(0);
        }

        /// <summary>
        /// Ejecuta la animación 3D de embestida de ataque (`onImpact` en el golpe; `onFinished` al volver a su puesto).
        /// </summary>
        public void RunAttackLunge(PlayerState p, CardDef card, string atkName, Action onImpact, Action onFinished)
        {
            if (Is3DAnimActive)
            {
                BoardAnimator.Instance.EnqueueAttackLungeStep(IndexOf(p), card, atkName, onImpact, onFinished);
            }
            else
            {
                Emit(AnimKind.AttackLunge, p, card, -1, false, atkName);
                onImpact?.Invoke();
                onFinished?.Invoke();
            }
        }

        /// <summary>
        /// Ejecuta `callback` después de que terminen todas las animaciones 3D actualmente en cola.
        /// </summary>
        public void RunAfterAnimations(Action callback)
        {
            if (callback == null) return;
            if (Is3DAnimActive)
                BoardAnimator.Instance.EnqueueCallback(callback);
            else
                callback();
        }

        public Game(List<CardDef> deckA, List<CardDef> deckB)
        {
            Players[0] = new PlayerState { Name = "Jugador", Deck = new List<CardDef>(deckA) };
            Players[1] = new PlayerState { Name = "Cliente", IsAI = true, Deck = new List<CardDef>(deckB) };
            foreach (var p in Players)
            {
                Shuffle(p.Deck);
            }
            Phase = Phase.CoinToss;
            Say("Elige Cara (Pikachu) o Cruz (Pokéball) para lanzar la moneda inicial.");
        }

        /// <summary>Calcula el resultado de la moneda inicial (el reparto de mano inicial, paso a Setup y colocación del NPC ocurren cuando la moneda aterriza en la mesa).</summary>
        public bool PerformStartCoinToss(bool playerChoseHeads)
        {
            bool coinHeads = Rng.Next(2) == 0;
            bool playerWon = coinHeads == playerChoseHeads;
            CurrentIndex = playerWon ? 0 : 1;
            return coinHeads;
        }

        int DealOpeningHandWithMulligan(PlayerState p)
        {
            int mulligans = 0;
            int safety = 0;
            while (safety++ < 50)
            {
                var drawn = p.Deck.Take(7).ToList();
                p.Deck.RemoveRange(0, Math.Min(7, p.Deck.Count));
                if (drawn.Any(c => c.IsBasicPokemon) || safety >= 50)
                {
                    foreach (var c in drawn)
                    {
                        p.Hand.Add(c);
                        if (!p.IsAI)
                            Emit(AnimKind.Draw, p, c);
                    }
                    if (p.IsAI && p.Hand.Count > 0)
                        Emit(AnimKind.Draw, p, p.Hand[p.Hand.Count - 1]);
                    Say($"{p.Name} roba su mano inicial de {drawn.Count} cartas.");
                    break;
                }

                mulligans++;
                string revealed = string.Join(", ", drawn.Select(c => c.Name));
                Say($"¡Mulligan de {p.Name} (#{mulligans})! Mano sin Pokémon Básicos ({revealed}). Baraja de nuevo y roba 7 cartas.");
                p.Deck.AddRange(drawn);
                Shuffle(p.Deck);
            }
            return mulligans;
        }

        /// <summary>Se invoca cuando la moneda inicial ya cayó sobre la mesa y se mostró su resultado.</summary>
        public void OnStartCoinLanded(bool coinHeads)
        {
            Say($"Moneda inicial: salió {(coinHeads ? "CARA (Pikachu)" : "CRUZ (Pokéball)")}. ¡Inicia {Current.Name}!");
            Phase = Phase.Setup;

            if (!openingDealt)
            {
                openingDealt = true;
                PlayerMulligans = DealOpeningHandWithMulligan(Players[0]);
                NpcMulligans = DealOpeningHandWithMulligan(Players[1]);

                // Colocar las 6 cartas de Premio de cada jugador
                Players[0].Prizes.AddRange(Players[0].Deck.Take(6));
                Players[0].Deck.RemoveRange(0, Math.Min(6, Players[0].Deck.Count));
                Players[1].Prizes.AddRange(Players[1].Deck.Take(6));
                Players[1].Deck.RemoveRange(0, Math.Min(6, Players[1].Deck.Count));

                // Regla de Mulligan: si un jugador hizo más mulligans que el otro, el rival roba hasta 2 cartas extra
                var summaryParts = new List<string>();
                if (PlayerMulligans > 0) summaryParts.Add($"Jugador hizo {PlayerMulligans} Mulligan(s)");
                if (NpcMulligans > 0) summaryParts.Add($"Cliente hizo {NpcMulligans} Mulligan(s)");

                if (PlayerMulligans != NpcMulligans)
                {
                    int netBonus = Math.Min(2, Math.Abs(PlayerMulligans - NpcMulligans));
                    PlayerState beneficiary = NpcMulligans > PlayerMulligans ? Players[0] : Players[1];
                    PlayerState other = OpponentOf(beneficiary);
                    Draw(beneficiary, netBonus, silent: beneficiary.IsAI);
                    if (beneficiary.IsAI && beneficiary.Hand.Count > 0)
                        Emit(AnimKind.Draw, beneficiary, beneficiary.Hand[beneficiary.Hand.Count - 1]);
                    string bonusMsg = $"{beneficiary.Name} roba {netBonus} carta(s) extra por Mulligan de {other.Name}";
                    Say($"  Regla de Mulligan: {bonusMsg}.");
                    summaryParts.Add(bonusMsg);
                }

                MulliganSummary = summaryParts.Count > 0 ? string.Join(" • ", summaryParts) : "";
            }

            // Si el NPC ganó la tirada, esperará a que termine el robo inicial para bajar su Pokémon Básico boca abajo
            if (CurrentIndex == 1 && !Players[1].SetupDone)
            {
                PendingAiSetup = true;
            }
        }

        public void ExecutePendingAiSetup()
        {
            if (!PendingAiSetup) return;
            PendingAiSetup = false;
            if (Phase == Phase.Setup && !Players[1].SetupDone)
            {
                AI.DoSetup(this, Players[1]);
            }
        }

        public void Shuffle(List<CardDef> l)
        {
            for (int i = l.Count - 1; i > 0; i--)
            {
                int j = Rng.Next(i + 1);
                (l[i], l[j]) = (l[j], l[i]);
            }
            TcgAudio.PlayShuffle();
        }

        public bool Draw(PlayerState p, int n = 1, bool silent = false)
        {
            for (int i = 0; i < n; i++)
            {
                if (p.Deck.Count == 0) return false;
                var top = p.Deck[0];
                p.Hand.Add(top); p.Deck.RemoveAt(0);
                if (!silent) Emit(AnimKind.Draw, p, top);
            }
            if (!silent && n > 0) Say($"{p.Name} roba {n} carta(s).");
            return true;
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

        void RunPokemonCheckupSequence(Action onCheckupDone)
        {
            var sleepingActives = new List<(PlayerState owner, PokemonInPlay active)>();
            foreach (var p in Players)
            {
                var a = p.Active;
                if (a == null) continue;
                if (a.Poisoned)
                {
                    a.Damage += 10;
                    Say($"{a.Card.Name} recibe 10 por veneno.");
                }
                if (a.Status == Status.Asleep)
                {
                    sleepingActives.Add((p, a));
                }
            }

            void StepSleep(int idx)
            {
                if (idx >= sleepingActives.Count)
                {
                    onCheckupDone?.Invoke();
                    return;
                }
                var entry = sleepingActives[idx];
                if (entry.active == null || entry.owner.Active != entry.active || entry.active.Status != Status.Asleep)
                {
                    StepSleep(idx + 1);
                    return;
                }
                RunCoinFlip(entry.owner, $"{entry.active.Card.Name} dormido", heads =>
                {
                    if (heads && entry.active.Status == Status.Asleep)
                    {
                        entry.active.Status = Status.None;
                        entry.active.StatusTurns = 0;
                        Say($"{entry.active.Card.Name} despierta.");
                    }
                    StepSleep(idx + 1);
                });
            }

            StepSleep(0);
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
            if (p.Bench.Count >= 5) return "Banca llena (máx. 5).";
            int bIdx = p.Bench.Count;
            var pip = new PokemonInPlay { TurnPlaced = Turn }; pip.Stack.Add(c);
            p.Bench.Add(pip); p.Hand.RemoveAt(handIdx);
            Say($"{p.Name} pone {c.Name} en la Banca.");
            Emit(AnimKind.ToBench, p, c, bIdx);
            return null;
        }

        public string Evolve(PlayerState p, int handIdx, PokemonInPlay target, bool breeder = false)
        {
            var err = MainCheck(p); if (err != null) return err;
            var c = p.Hand[handIdx];
            if (c.Supertype != Supertype.Pokemon || c.Stage == "Basic") return "No es una evolución.";
            if (target.IsDoll && target.Card.Name != "Mysterious Fossil") return "No se puede evolucionar un Clefairy Doll.";
            if (!breeder && c.EvolvesFrom != target.Card.Name) return $"{c.Name} evoluciona de {c.EvolvesFrom}.";
            bool giovanniBypass = GiovanniAllowEvolveTurn == Turn && target.Card.Name.IndexOf("Giovanni", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!giovanniBypass && (Turn <= 2 || target.TurnPlaced == Turn || target.TurnEvolved == Turn))
                return "No puede evolucionar en el primer turno ni el turno en que se jugó/evolucionó.";
            int bIdx = target == p.Active ? -1 : p.Bench.IndexOf(target);
            target.IsDoll = false;
            target.Stack.Add(c); target.TurnEvolved = Turn; target.ClearConditions();
            if (ActiveStadium == "Viridian City Gym" && c.Name.IndexOf("Giovanni", StringComparison.OrdinalIgnoreCase) >= 0 && target.Damage > 0)
            {
                target.Damage = Math.Max(0, target.Damage - 20);
                Say($"  Viridian City Gym cura 20 PS a {c.Name}.");
            }
            p.Hand.RemoveAt(handIdx);
            Say($"{p.Name} evoluciona a {c.Name}.");
            Emit(AnimKind.Evolve, p, c, bIdx);
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
            if (p.EnergyPlayedThisTurn && !blaineExtra) return "Ya uniste una Energía este turno.";
            if (target.IsDoll) return $"No se puede unir Energía a {target.Card.Name}.";
            int bIdx = target == p.Active ? -1 : p.Bench.IndexOf(target);
            target.Energy.Add(c);
            p.Hand.RemoveAt(handIdx);
            p.EnergyPlayedThisTurn = true;
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
            int retreatCost = a.Card.RetreatCost;
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
            var b = p.Bench[benchIdx];
            p.Bench[benchIdx] = a; p.Active = b;
            retreatedThisTurn = Turn;
            Say($"{p.Name} retira a {a.Card.Name}; entra {b.Card.Name}.");
            Emit(AnimKind.RetreatSwap, p, b.Card, benchIdx);
        }
        int retreatedThisTurn = -1;

        public string PlayTrainer(PlayerState p, int handIdx, PokemonInPlay target)
        {
            var err = MainCheck(p); if (err != null) return err;
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
            });
            return null;
        }

        /// <summary>Daño al Activo defensor aplicando PlusPower, Debilidad x2, Resistencia, Defender y efectos de Líderes de Gimnasio.</summary>
        public void DealDamage(PokemonInPlay attacker, PokemonInPlay defender, int baseDmg, bool applyWR = true)
        {
            if (baseDmg <= 0) return;
            int dmg = baseDmg + (attacker?.PlusPower ?? 0) * 10;
            if (MistyBonusTurn == Turn && attacker != null && attacker.Card.Name.IndexOf("Misty", StringComparison.OrdinalIgnoreCase) >= 0)
                dmg += 20;
            if (applyWR && attacker != null)
            {
                bool ignoreWaterWeakness = ActiveStadium == "Cinnabar City Gym"
                    && attacker.Card.Type == "Water"
                    && defender.Card.Name.IndexOf("Blaine", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!ignoreWaterWeakness && defender.Card.WeaknessType == attacker.Card.Type) dmg *= 2;

                bool ignoreResistance = ActiveStadium == "Pewter City Gym"
                    && attacker.Card.Name.IndexOf("Brock", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!ignoreResistance && defender.Card.ResistanceType == attacker.Card.Type) dmg -= defender.Card.ResistanceValue;
            }
            if (defender.DefenderUntilTurn >= Turn) dmg -= 20;
            dmg = Math.Max(0, dmg);
            defender.Damage += dmg;
            Say($"  {defender.Card.Name} recibe {dmg} de daño.");
            if (dmg > 0 && KogaPoisonTurn == Turn && attacker != null && attacker.Card.Name.IndexOf("Koga", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                defender.Poisoned = true;
                Say($"  ¡El efecto de Koga envenena a {defender.Card.Name}!");
            }
        }

        public void CheckKnockOuts()
        {
            foreach (var p in Players)
                foreach (var pip in p.InPlay.ToList())
                    if (pip.RemainingHp <= 0)
                    {
                        Say($"¡{pip.Card.Name} de {p.Name} queda Fuera de Combate!");
                        int bIdx = p.Active == pip ? -1 : p.Bench.IndexOf(pip);
                        Emit(AnimKind.KnockOut, p, pip.Card, bIdx);
                        p.Discard.AddRange(pip.AllCards());
                        if (p.Active == pip) p.Active = null; else p.Bench.Remove(pip);
                        if (!pip.IsDoll)
                        {
                            if (p.IsAI) PlayerKOs++;
                            else NpcKOs++;
                            TakePrize(OpponentOf(p));
                        }
                    }
            if (Phase == Phase.GameOver) return;
            foreach (var p in Players)
                if (p.Active == null)
                {
                    if (p.Bench.Count == 0)
                    {
                        EndGame(
                            OpponentOf(p),
                            p.IsAI
                                ? Loc.T("Tu oponente se quedó sin Pokémon", "Your opponent ran out of Pokémon")
                                : Loc.T("Te quedaste sin Pokémon", "You ran out of Pokémon"));
                        return;
                    }
                    if (p.IsAI) { AI.Promote(this, p); continue; }
                    PromotionPending = p; Phase = Phase.ChoosePromotion; return;
                }
        }

        void TakePrize(PlayerState p)
        {
            if (p.Prizes.Count == 0) return;
            var prize = p.Prizes[0];
            p.Hand.Add(prize); p.Prizes.RemoveAt(0);
            Say($"{p.Name} toma un Premio ({p.Prizes.Count} restantes).");
            Emit(AnimKind.TakePrize, p, prize, p.Prizes.Count);
            if (p.Prizes.Count == 0)
            {
                EndGame(
                    p,
                    p.IsAI
                        ? Loc.T("Obtuvo todos los premios", "Your opponent took all Prize cards")
                        : Loc.T("Obtuviste todos los premios", "You took all Prize cards"));
            }
        }

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
