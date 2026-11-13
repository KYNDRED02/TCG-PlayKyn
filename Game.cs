// TCG-PlayKyn: Core match state, player data structures, coin manager and action logs
using System;
using System.Collections.Generic;
using System.Linq;

namespace TCGPlayKyn
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
        public bool ToxicPoison; // 20 de veneno en lugar de 10 (ej. Toxic de Nidoking)
        public int TurnPlaced;
        public int TurnEvolved = -1;
        public int DefenderUntilTurn = -1;
        public int PlusPower;
        public bool IsDoll; // Clefairy Doll

        // Prevención temporal de ataques
        public int PreventAllUntilTurn = -1; // Daño y efectos de ataques prevenidos hasta el turno indicado
        public int PreventDamageUntilTurn = -1; // Solo daño de ataques prevenido hasta el turno indicado
        public int PreventDamageThreshold = 0; // Umbral de daño prevenido si <= valor (ej. Harden de Onix/Graveler)
        public int PreventDamageThresholdUntilTurn = -1;
        public int DamageReductionAmount = 0; // Minimize de Clefable/Grimer (-20 de daño)
        public int DamageReductionUntilTurn = -1;
        public int SmokescreenUntilTurn = -1; // Pantalla de humo: debe lanzar moneda para atacar
        public int CantRetreatUntilTurn = -1; // Ácido de Victreebel: no puede retirarse
        public int SwordsDanceUntilTurn = -1; // Danza Espada de Scyther: Slash hace 60 en el siguiente turno

        // Registro de habilidades (Poké-Powers) usadas en el turno actual
        public HashSet<string> PowersUsedThisTurn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Tipo elemental temporal alterado por una habilidad (ej. Shift de Venomoth)
        public string TemporaryType = null;
        // Carta de evolución copiada por Shapeshift de Brock's Ninetales
        public CardDef ShiftCard = null;

        public CardDef Card => ShiftCard ?? Stack[Stack.Count - 1];
        public int RemainingHp => Card.Hp - Damage;
        public string Label => $"{Card.Name} {RemainingHp}/{Card.Hp}" + (Status != Status.None ? $" [{Status}]" : "") + (Poisoned ? (ToxicPoison ? " [Toxic]" : " [Poison]") : "");

        public bool IsEnergyBurnActive =>
            (Card.Name.StartsWith("Charizard", StringComparison.OrdinalIgnoreCase) ||
             (Card.Abilities != null && Card.Abilities.Any(ab => ab.Name == "Energy Burn")) ||
             Card.Power?.Name == "Energy Burn") &&
            Status != Status.Asleep && Status != Status.Confused && Status != Status.Paralyzed;

        public int EnergyCount(string type)
        {
            if (IsEnergyBurnActive && type == "Fire") return EnergyTotal;
            return Energy.Where(e => e.EnergyProvides == type || e.EnergyProvides == "Rainbow").Sum(e => e.EnergyAmount);
        }
        public int EnergyTotal => Energy.Sum(e => e.EnergyAmount);

        public void ClearTemporaryAttackEffects()
        {
            PreventAllUntilTurn = -1;
            PreventDamageUntilTurn = -1;
            PreventDamageThreshold = 0;
            PreventDamageThresholdUntilTurn = -1;
            DamageReductionAmount = 0;
            DamageReductionUntilTurn = -1;
            SmokescreenUntilTurn = -1;
            CantRetreatUntilTurn = -1;
            SwordsDanceUntilTurn = -1;
        }

        public void ClearConditions()
        {
            Status = Status.None;
            StatusTurns = 0;
            Poisoned = false;
            ToxicPoison = false;
        }

        public IEnumerable<CardDef> AllCards() => Stack.Concat(Energy).Concat(Attached);

        /// <summary>Comprueba si la energía unida paga el coste (Colorless lo paga cualquiera; Rainbow paga cualquier color).</summary>
        public bool CanPay(List<string> cost)
        {
            if (IsEnergyBurnActive)
            {
                int otherColor = cost.Count(c => c != "Fire" && c != "Colorless");
                if (otherColor > 0) return false;
                return EnergyTotal >= cost.Count;
            }
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
        public int TrainerLockUntilTurn = -1; // Headache de Psyduck: no puede jugar cartas de Entrenador
        public bool RainDanceActive; // Rain Dance de Blastoise: permite unir múltiples energías de Agua este turno

        public IEnumerable<PokemonInPlay> InPlay => (Active != null ? new[] { Active } : new PokemonInPlay[0]).Concat(Bench);
    }

    /// <summary>
    /// Clase principal del estado de juego y simulación de la partida Pokémon TCG.
    /// El cálculo de daño y noqueos está en Game.Damage.cs, y el flujo de turnos en Game.TurnLoop.cs.
    /// </summary>
    public partial class Game
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
        public bool PlayerManualMulliganUsed { get; private set; }
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

        /// <summary>
        /// Permite al jugador realizar un único Mulligan voluntario al inicio de la partida (Fase Setup)
        /// devolviendo su mano al mazo, barajando y robando 7 cartas nuevas con al menos 1 Básico.
        /// </summary>
        public string ManualPlayerMulligan()
        {
            if (Phase != Phase.Setup || Players[0].SetupDone || Players[0].Active != null)
                return Loc.T("Solo puedes hacer Mulligan antes de comenzar el primer turno.", "You can only Mulligan before starting the first turn.");
            if (PlayerManualMulliganUsed)
                return Loc.T("Ya utilizaste tu Mulligan en esta partida.", "You already used your Mulligan this match.");

            var p = Players[0];
            PlayerManualMulliganUsed = true;
            if (p.Hand.Count > 0)
            {
                p.Deck.AddRange(p.Hand);
                p.Hand.Clear();
            }
            Shuffle(p.Deck);
            DealOpeningHandWithMulligan(p);
            Say(Loc.T($"{p.Name} realizó un Mulligan al inicio de la partida y robó una nueva mano de 7 cartas.",
                      $"{p.Name} took a Mulligan at the start of the match and drew a fresh 7-card hand."));
            return null;
        }

        // [ExecutePendingAiSetup se define en Game.TurnLoop.cs]
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

        // [SetupPlace, FinishSetup, StartTurn, EndTurn, AdvanceTurn se definen en Game.TurnLoop.cs]
        // [RunPokemonCheckupSequence se define en Game.Damage.cs]
        // [PlayBasic, Evolve, AttachEnergy, Retreat, PlayTrainer, Attack se definen en Game.TurnLoop.cs]
        // [IsPowerDisabled, DealDamage, CheckKnockOuts, TakePrize se definen en Game.Damage.cs]
        // [Promote y EndGame se definen en Game.TurnLoop.cs]
    }
}
