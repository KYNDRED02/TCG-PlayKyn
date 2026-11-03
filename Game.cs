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

        public int EnergyCount(string type) => Energy.Where(e => e.EnergyProvides == type).Sum(e => e.EnergyAmount);
        public int EnergyTotal => Energy.Sum(e => e.EnergyAmount);

        public void ClearConditions() { Status = Status.None; StatusTurns = 0; Poisoned = false; }

        public IEnumerable<CardDef> AllCards() => Stack.Concat(Energy).Concat(Attached);

        /// <summary>Comprueba si la energía unida paga el coste (Colorless lo paga cualquiera).</summary>
        public bool CanPay(List<string> cost)
        {
            var pool = new Dictionary<string, int>();
            foreach (var e in Energy) { pool.TryGetValue(e.EnergyProvides, out int v); pool[e.EnergyProvides] = v + e.EnergyAmount; }
            int colorless = 0;
            foreach (var c in cost)
            {
                if (c == "Colorless") { colorless++; continue; }
                if (!pool.TryGetValue(c, out int v) || v == 0) return false;
                pool[c] = v - 1;
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
        public PlayerState PromotionPending;
        public readonly List<string> Log = new List<string>();
        public readonly Random Rng = new Random();
        public Action<AnimEvent> OnAnim;
        public bool PendingAiTurn;
        public bool PendingAiSetup;
        public bool IsBusyResolving { get; private set; }
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

        void Emit(AnimKind kind, PlayerState p, CardDef card = null, int benchIdx = -1, bool heads = false, string label = null)
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
                int safety = 0;
                while (safety++ < 50)
                {
                    Shuffle(p.Deck);
                    if (p.Deck.Take(7).Any(c => c.IsBasicPokemon)) break;
                }
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

        /// <summary>Se invoca cuando la moneda inicial ya cayó sobre la mesa y se mostró su resultado.</summary>
        public void OnStartCoinLanded(bool coinHeads)
        {
            Say($"Moneda inicial: salió {(coinHeads ? "CARA (Pikachu)" : "CRUZ (Pokéball)")}. ¡Inicia {Current.Name}!");
            Phase = Phase.Setup;

            if (!openingDealt)
            {
                openingDealt = true;
                // Robar las 7 cartas iniciales del jugador con animación hacia pantalla, revelación y bajada a la mano
                Draw(Players[0], 7, silent: false);
                Players[0].Prizes.AddRange(Players[0].Deck.Take(6));
                Players[0].Deck.RemoveRange(0, Math.Min(6, Players[0].Deck.Count));

                // Mano inicial y premios del NPC
                Draw(Players[1], 7, silent: true);
                if (Players[1].Hand.Count > 0)
                    Emit(AnimKind.Draw, Players[1], Players[1].Hand[Players[1].Hand.Count - 1]);
                Players[1].Prizes.AddRange(Players[1].Deck.Take(6));
                Players[1].Deck.RemoveRange(0, Math.Min(6, Players[1].Deck.Count));
            }

            // Si el NPC ganó la tirada, esperará a que termine el robo inicial para bajar su Pokémon Básico
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

        public void Shuffle(List<CardDef> l) { for (int i = l.Count - 1; i > 0; i--) { int j = Rng.Next(i + 1); (l[i], l[j]) = (l[j], l[i]); } }

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
            Say($"--- Turno {Turn}: {p.Name} ---");
            if (!Draw(p)) { EndGame(OpponentOf(p), $"{p.Name} no puede robar carta."); return; }
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
            if (target.IsDoll) return "No se puede evolucionar un Clefairy Doll.";
            if (!breeder && c.EvolvesFrom != target.Card.Name) return $"{c.Name} evoluciona de {c.EvolvesFrom}.";
            if (Turn <= 2 || target.TurnPlaced == Turn || target.TurnEvolved == Turn)
                return "No puede evolucionar en el primer turno ni el turno en que se jugó/evolucionó.";
            int bIdx = target == p.Active ? -1 : p.Bench.IndexOf(target);
            target.Stack.Add(c); target.TurnEvolved = Turn; target.ClearConditions();
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
            if (p.EnergyPlayedThisTurn) return "Ya uniste una Energía este turno.";
            if (target.IsDoll) return "No se puede unir Energía a Clefairy Doll.";
            int bIdx = target == p.Active ? -1 : p.Bench.IndexOf(target);
            target.Energy.Add(c); p.Hand.RemoveAt(handIdx); p.EnergyPlayedThisTurn = true;
            Say($"{p.Name} une {c.Name} debajo de {target.Card.Name}.");
            Emit(AnimKind.AttachEnergy, p, c, bIdx);
            return null;
        }

        public string Retreat(PlayerState p, int benchIdx, List<int> energyIdxToDiscard = null)
        {
            var err = MainCheck(p); if (err != null) return err;
            var a = p.Active;
            if (a == null) return "Sin Pokémon Activo.";
            if (benchIdx < 0 || benchIdx >= p.Bench.Count) return "Pokémon de Banca inválido.";
            if (a.IsDoll) return "Clefairy Doll no puede retirarse.";
            if (a.Status == Status.Asleep || a.Status == Status.Paralyzed) return "Dormido o Paralizado: no puede retirarse.";
            if (a.EnergyTotal < a.Card.RetreatCost) return $"Necesita {a.Card.RetreatCost} Energía para retirarse.";
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
            if (!(c.Name == "PlusPower" || c.Name == "Defender" || c.Name == "Clefairy Doll")) p.Discard.Add(c);
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
            if (a.IsDoll) return "Clefairy Doll no puede atacar.";
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

        /// <summary>Daño al Activo defensor aplicando PlusPower, Debilidad x2, Resistencia y Defender.</summary>
        public void DealDamage(PokemonInPlay attacker, PokemonInPlay defender, int baseDmg, bool applyWR = true)
        {
            if (baseDmg <= 0) return;
            int dmg = baseDmg + (attacker?.PlusPower ?? 0) * 10;
            if (applyWR && attacker != null)
            {
                if (defender.Card.WeaknessType == attacker.Card.Type) dmg *= 2;
                if (defender.Card.ResistanceType == attacker.Card.Type) dmg -= defender.Card.ResistanceValue;
            }
            if (defender.DefenderUntilTurn >= Turn) dmg -= 20;
            dmg = Math.Max(0, dmg);
            defender.Damage += dmg;
            Say($"  {defender.Card.Name} recibe {dmg} de daño.");
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
                        if (!pip.IsDoll) TakePrize(OpponentOf(p));
                    }
            if (Phase == Phase.GameOver) return;
            foreach (var p in Players)
                if (p.Active == null)
                {
                    if (p.Bench.Count == 0) { EndGame(OpponentOf(p), $"{p.Name} no tiene Pokémon en juego."); return; }
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
            if (p.Prizes.Count == 0) EndGame(p, "tomó todos sus Premios.");
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
