using System;
using System.Collections.Generic;
using UnityEngine;

namespace PokeTCGPlay
{
    /// <summary>
    /// F3: Animator 3D de Cartas sobre el Playmat físico.
    /// Gestiona una cola de animaciones 3D con curvas de easing, vuelos parabólicos en arco,
    /// inclinación dinámica según la velocidad (pitch/roll estilo Legends of Runeterra),
    /// giro 3D (flip de reverso a frente al robar o tomar premio), deslizamiento de energía debajo del Pokémon,
    /// evolución posándose encima, embestida de ataque y caída al descarte en KO.
    /// </summary>
    public class BoardAnimator : MonoBehaviour
    {
        public static BoardAnimator Instance { get; private set; }

        public class Flight3D
        {
            public AnimKind Kind;
            public int PlayerIndex;
            public int TargetBenchIndex = -1;
            public CardDef Card;
            public Vector3 StartWorldPos;
            public Vector3 ApproachWorldPos;
            public Vector3 EndWorldPos;
            public Quaternion BaseRotation;
            public float StartScale = 1f;
            public float EndScale = 1f;
            public float StartFlipZ; // 180 = boca abajo, 0 = boca arriba
            public float EndFlipZ;
            public float YawSpin;    // Giro adicional en Y (ej. al caer al descarte por KO)
            public float ArcHeight = 0.075f;
            public float Duration = 0.42f;
            public float Elapsed;
            public string Caption;
            public bool SlideUnderAtEnd; // true para Energías que se deslizan debajo del Pokémon elevado
            public bool PulseScaleAtEnd; // true para Evoluciones que se posan encima
            public bool RevealAtScreen;  // true para robos del jugador: vuela a pantalla de espaldas, gira, espera 0.5s y baja a la mano
            public int HandSlotIndex = -1;
            public int HandTotalCount = 1;
            public bool CoinHeads;       // F7: resultado de la moneda 3D en lanzamientos de ataque/estado
            public string CoinReason;
            public bool IsInstantCallback;
            public Action OnStart;
            public Action OnComplete;
        }

        readonly Queue<Flight3D> queue = new Queue<Flight3D>();
        public Flight3D Current { get; private set; }
        CardView flyingCard;
        Vector3 prevWorldPos;
        Vector3? customNextHandOrigin;

        public bool IsAnimating => Current != null || queue.Count > 0;
        public string CurrentCaption => Current?.Caption;

        /// <summary>
        /// Devuelve cuántas cartas robadas o tomadas de premios de `playerIndex` siguen en vuelo hacia su mano.
        /// </summary>
        public int GetPendingHandArrivals(int playerIndex)
        {
            int count = 0;
            if (Current != null && Current.PlayerIndex == playerIndex &&
                (Current.Kind == AnimKind.Draw || Current.Kind == AnimKind.TakePrize))
            {
                count++;
            }
            foreach (var f in queue)
            {
                if (f.PlayerIndex == playerIndex && (f.Kind == AnimKind.Draw || f.Kind == AnimKind.TakePrize))
                    count++;
            }
            return count;
        }

        /// <summary>
        /// Devuelve cuántos robos desde el mazo de `playerIndex` aún están en la cola de vuelo.
        /// </summary>
        public int GetPendingDeckDraws(int playerIndex)
        {
            int count = 0;
            foreach (var f in queue)
            {
                if (f.PlayerIndex == playerIndex && f.Kind == AnimKind.Draw)
                    count++;
            }
            return count;
        }

        /// <summary>
        /// Devuelve cuántos premios de `playerIndex` aún esperan en la cola antes de despegar de la casilla Side.
        /// </summary>
        public int GetPendingPrizeTakes(int playerIndex)
        {
            int count = 0;
            foreach (var f in queue)
            {
                if (f.PlayerIndex == playerIndex && f.Kind == AnimKind.TakePrize)
                    count++;
            }
            return count;
        }

        /// <summary>
        /// Indica si la llegada inicial de un Pokémon a `destBenchSlotIdx` (-1 = Activo, 0..4 = Banca)
        /// o su embestida de ataque está actualmente en vuelo o pendiente en la cola.
        /// </summary>
        public bool IsPokemonSlotInFlightOrQueued(int playerIndex, int destBenchSlotIdx)
        {
            if (Current != null && MatchesSlotArrivalOrAction(Current, playerIndex, destBenchSlotIdx, true))
                return true;
            foreach (var f in queue)
            {
                if (MatchesSlotArrivalOrAction(f, playerIndex, destBenchSlotIdx, false))
                    return true;
            }
            return false;
        }

        static bool MatchesSlotArrivalOrAction(Flight3D f, int playerIndex, int destBenchSlotIdx, bool isCurrent)
        {
            if (f == null || f.PlayerIndex != playerIndex) return false;
            if (f.Kind == AnimKind.ToActive && destBenchSlotIdx == -1) return true;
            if (f.Kind == AnimKind.ToBench && f.TargetBenchIndex == destBenchSlotIdx) return true;
            if (f.Kind == AnimKind.RetreatSwap && destBenchSlotIdx == -1) return true;
            if (isCurrent && f.Kind == AnimKind.AttackLunge && destBenchSlotIdx == -1) return true;
            return false;
        }

        /// <summary>
        /// Devuelve cuántas evoluciones dirigidas a `benchSlotIdx` siguen en vuelo o en la cola.
        /// </summary>
        public int GetPendingEvolveCount(int playerIndex, int benchSlotIdx)
        {
            int count = 0;
            if (Current != null && Current.PlayerIndex == playerIndex && Current.Kind == AnimKind.Evolve && Current.TargetBenchIndex == benchSlotIdx)
                count++;
            foreach (var f in queue)
            {
                if (f.PlayerIndex == playerIndex && f.Kind == AnimKind.Evolve && f.TargetBenchIndex == benchSlotIdx)
                    count++;
            }
            return count;
        }

        /// <summary>
        /// Devuelve cuántas uniones de Energía dirigidas a `benchSlotIdx` están en la cola (aún no iniciadas).
        /// </summary>
        public int GetQueuedEnergyCount(int playerIndex, int benchSlotIdx)
        {
            int count = 0;
            foreach (var f in queue)
            {
                if (f.PlayerIndex == playerIndex && f.Kind == AnimKind.AttachEnergy && f.TargetBenchIndex == benchSlotIdx)
                    count++;
            }
            return count;
        }

        /// <summary>
        /// Indica si `card` está actualmente volando o en cola hacia la pila de Descarte (`KnockOut` o `PlayTrainer`).
        /// </summary>
        public bool IsCardFlyingToDiscard(int playerIndex, CardDef card)
        {
            if (card == null) return false;
            if (Current != null && Current.PlayerIndex == playerIndex && Current.Card == card &&
                (Current.Kind == AnimKind.KnockOut || Current.Kind == AnimKind.PlayTrainer))
                return true;
            foreach (var f in queue)
            {
                if (f.PlayerIndex == playerIndex && f.Card == card &&
                    (f.Kind == AnimKind.KnockOut || f.Kind == AnimKind.PlayTrainer))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// F4: Permite que cuando el jugador juega o suelta (Drag & Drop) una carta desde una posición 3D concreta,
        /// el vuelo 3D inicie exactamente desde esa posición en el mundo.
        /// </summary>
        public void SetNextHandOrigin(Vector3 worldPos)
        {
            customNextHandOrigin = worldPos;
        }

        void Awake()
        {
            Instance = this;
        }

        void OnDestroy()
        {
            Clear();
            if (flyingCard != null)
            {
                Destroy(flyingCard.gameObject);
                flyingCard = null;
            }
            if (Instance == this) Instance = null;
        }

        public void Clear()
        {
            queue.Clear();
            Current = null;
            customNextHandOrigin = null;
            if (flyingCard != null)
                flyingCard.gameObject.SetActive(false);
        }

        void EnsureFlyingCard()
        {
            if (flyingCard != null) return;
            flyingCard = CardView.Create(transform);
            flyingCard.gameObject.name = "PokeTCG_FlyingCard3D";
            flyingCard.SetRaycastEnabled(false);
            flyingCard.gameObject.SetActive(false);
        }

        static void InvokeSafe(Action cb)
        {
            if (cb == null) return;
            try
            {
                cb();
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("BoardAnimator callback error: " + e);
            }
        }

        public void EnqueueCallback(Action callback)
        {
            if (callback == null) return;
            var tv = TableView.Instance;
            if (tv == null || !tv.IsBoard3DReady || !IsAnimating)
            {
                InvokeSafe(callback);
                return;
            }
            queue.Enqueue(new Flight3D
            {
                IsInstantCallback = true,
                Duration = 0f,
                OnComplete = callback
            });
        }

        public void EnqueueCoinFlipStep(int playerIdx, bool coinHeads, string reason, Action onLanded)
        {
            var tv = TableView.Instance;
            if (tv == null || !tv.IsBoard3DReady)
            {
                InvokeSafe(onLanded);
                return;
            }

            bool hasCoinQueued = Current != null && Current.Kind == AnimKind.CoinFlip;
            if (!hasCoinQueued)
            {
                foreach (var q in queue)
                {
                    if (q.Kind == AnimKind.CoinFlip) { hasCoinQueued = true; break; }
                }
            }
            string cleanReason = string.IsNullOrEmpty(reason) ? "Moneda" : reason;
            queue.Enqueue(new Flight3D
            {
                Kind = AnimKind.CoinFlip,
                PlayerIndex = Mathf.Clamp(playerIdx, 0, 1),
                CoinHeads = coinHeads,
                CoinReason = cleanReason,
                Duration = hasCoinQueued ? 1.15f : 1.38f,
                Caption = $"{cleanReason} — Lanzando moneda...",
                OnComplete = onLanded
            });
        }

        public void EnqueueAttackLungeStep(int playerIdx, CardDef card, string attackName, Action onImpact, Action onFinished)
        {
            var tv = TableView.Instance;
            if (tv == null || !tv.IsBoard3DReady)
            {
                InvokeSafe(onImpact);
                InvokeSafe(onFinished);
                return;
            }

            int p = Mathf.Clamp(playerIdx, 0, 1);
            Quaternion playerRot = tv.ActiveAnchor[p] != null ? tv.ActiveAnchor[p].rotation : tv.PlaymatRoot.rotation;
            Vector3 activePos = tv.ActiveAnchor[p] != null ? tv.ActiveAnchor[p].position : tv.PlaymatRoot.position;
            Vector3 oppActivePos = tv.ActiveAnchor[1 - p] != null ? tv.ActiveAnchor[1 - p].position : tv.PlaymatRoot.position;
            Vector3 hitPos = Vector3.Lerp(activePos, oppActivePos, 0.58f) + Vector3.up * 0.015f;

            // 1) Embestida hacia el Pokémon rival (aplica VFX elemental, sonido de ataque, sacudida de cámara y daño justo en el impacto)
            queue.Enqueue(new Flight3D
            {
                Kind = AnimKind.AttackLunge,
                PlayerIndex = p,
                Card = card,
                StartWorldPos = activePos,
                EndWorldPos = hitPos,
                BaseRotation = playerRot,
                StartScale = 1.08f,
                EndScale = 1.15f,
                StartFlipZ = 0f,
                EndFlipZ = 0f,
                ArcHeight = 0.045f,
                Duration = 0.25f,
                Caption = $"¡{card?.Name} {Loc.T("usa", "uses")} {attackName}!",
                OnComplete = () =>
                {
                    TcgAudio.PlayAttackImpact(card?.Type, 35);
                    PolishEffects3D.SpawnElementalAttackVfx(oppActivePos, card?.Type, 35);
                    PolishEffects3D.TriggerCameraShake(0.011f, 0.20f);
                    InvokeSafe(onImpact);
                }
            });

            // 2) Regreso al puesto Activo (al posarse de vuelta se verifican KOs, premios y fin de turno)
            queue.Enqueue(new Flight3D
            {
                Kind = AnimKind.AttackLunge,
                PlayerIndex = p,
                Card = card,
                StartWorldPos = hitPos,
                EndWorldPos = activePos,
                BaseRotation = playerRot,
                StartScale = 1.15f,
                EndScale = 1.08f,
                StartFlipZ = 0f,
                EndFlipZ = 0f,
                ArcHeight = 0.025f,
                Duration = 0.22f,
                Caption = $"¡{card?.Name} usa {attackName}!",
                OnComplete = onFinished
            });
        }

        public void EnqueueEvent(AnimEvent ev, Game game)
        {
            var tv = TableView.Instance;
            if (tv == null || tv.PlaymatRoot == null) return;

            int p = Mathf.Clamp(ev.PlayerIndex, 0, 1);
            Quaternion playerRot = tv.ActiveAnchor[p] != null ? tv.ActiveAnchor[p].rotation : tv.PlaymatRoot.rotation;

            int handCount = game != null && game.Players[p] != null ? game.Players[p].Hand.Count : 1;
            Vector3 handSlotPos = tv.GetHandCardWorldPos(p, Mathf.Max(0, handCount - 1), Mathf.Max(1, handCount));
            Vector3 handPos = (p == 0 && customNextHandOrigin.HasValue) ? customNextHandOrigin.Value : handSlotPos;
            if (p == 0 && customNextHandOrigin.HasValue)
                customNextHandOrigin = null;

            Vector3 activePos = tv.ActiveAnchor[p] != null ? tv.ActiveAnchor[p].position : tv.PlaymatRoot.position;
            Vector3 oppActivePos = tv.ActiveAnchor[1 - p] != null ? tv.ActiveAnchor[1 - p].position : tv.PlaymatRoot.position;
            Vector3 deckPos = tv.DeckAnchor[p] != null ? tv.DeckAnchor[p].position : tv.PlaymatRoot.position;
            if (game != null && game.Players[p] != null)
            {
                float deckH = Mathf.Max(0.002f, game.Players[p].Deck.Count * 0.00054f);
                deckPos += Vector3.up * deckH;
            }
            Vector3 trashPos = tv.DiscardAnchor[p] != null ? tv.DiscardAnchor[p].position : tv.PlaymatRoot.position;
            int bIdx = Mathf.Clamp(ev.BenchIndex, 0, 4);
            Vector3 benchPos = tv.BenchAnchor[p, bIdx] != null ? tv.BenchAnchor[p, bIdx].position : activePos;

            switch (ev.Kind)
            {
                case AnimKind.Draw:
                    queue.Enqueue(new Flight3D
                    {
                        Kind = AnimKind.Draw,
                        PlayerIndex = p,
                        Card = ev.Card,
                        StartWorldPos = deckPos,
                        EndWorldPos = handPos,
                        BaseRotation = playerRot,
                        StartScale = 1f,
                        EndScale = p == 0 ? 0.88f : 0.76f,
                        // Para el jugador: vuela hacia la pantalla viéndose por detrás, gira 180°, se mantiene 0.5s y baja a la mano
                        StartFlipZ = 180f,
                        EndFlipZ = p == 0 ? 0f : 180f,
                        ArcHeight = 0.085f,
                        Duration = p == 0 ? 1.10f : 0.38f,
                        RevealAtScreen = (p == 0),
                        HandSlotIndex = Mathf.Max(0, handCount - 1),
                        HandTotalCount = Mathf.Max(1, handCount),
                        Caption = p == 0 ? $"Robas: {ev.Card?.Name}" : "Cliente roba 1 carta"
                    });
                    break;

                case AnimKind.ToActive:
                    {
                        bool faceDownSetup = game != null && game.Phase == Phase.Setup && p == 1;
                        queue.Enqueue(new Flight3D
                        {
                            Kind = AnimKind.ToActive,
                            PlayerIndex = p,
                            TargetBenchIndex = -1,
                            Card = ev.Card,
                            StartWorldPos = ev.BenchIndex >= 0 ? benchPos : handPos,
                            EndWorldPos = activePos + Vector3.up * 0.002f,
                            BaseRotation = playerRot,
                            StartScale = ev.BenchIndex >= 0 ? 0.94f : 1f,
                            EndScale = 1.08f,
                            StartFlipZ = faceDownSetup ? 180f : 0f,
                            EndFlipZ = faceDownSetup ? 180f : 0f,
                            ArcHeight = 0.075f,
                            Duration = 0.42f,
                            Caption = faceDownSetup ? "Cliente coloca su Pokémon Activo boca abajo" : $"{ev.Card?.Name} entra al Campo de Batalla"
                        });
                        break;
                    }

                case AnimKind.ToBench:
                    {
                        bool faceDownSetup = game != null && game.Phase == Phase.Setup && p == 1;
                        queue.Enqueue(new Flight3D
                        {
                            Kind = AnimKind.ToBench,
                            PlayerIndex = p,
                            TargetBenchIndex = bIdx,
                            Card = ev.Card,
                            StartWorldPos = handPos,
                            EndWorldPos = benchPos + Vector3.up * 0.002f,
                            BaseRotation = playerRot,
                            StartScale = 1f,
                            EndScale = 0.94f,
                            StartFlipZ = faceDownSetup ? 180f : 0f,
                            EndFlipZ = faceDownSetup ? 180f : 0f,
                            ArcHeight = 0.070f,
                            Duration = 0.38f,
                            Caption = faceDownSetup ? "Cliente coloca un Pokémon en Banca boca abajo" : $"{ev.Card?.Name} baja a la Banca"
                        });
                        break;
                    }

                case AnimKind.Evolve:
                    {
                        Vector3 dest = ev.BenchIndex < 0 ? activePos : benchPos;
                        float endScale = ev.BenchIndex < 0 ? 1.08f : 0.94f;
                        queue.Enqueue(new Flight3D
                        {
                            Kind = AnimKind.Evolve,
                            PlayerIndex = p,
                            TargetBenchIndex = ev.BenchIndex,
                            Card = ev.Card,
                            StartWorldPos = handPos,
                            EndWorldPos = dest + Vector3.up * 0.0045f,
                            BaseRotation = playerRot,
                            StartScale = 1.05f,
                            EndScale = endScale,
                            StartFlipZ = 0f,
                            EndFlipZ = 0f,
                            ArcHeight = 0.115f,
                            Duration = 0.48f,
                            PulseScaleAtEnd = true,
                            Caption = $"¡Evoluciona a {ev.Card?.Name}!"
                        });
                        break;
                    }

                case AnimKind.AttachEnergy:
                    {
                        Transform targetAnchor = ev.BenchIndex < 0 ? tv.ActiveAnchor[p] : tv.BenchAnchor[p, bIdx];
                        float endScale = ev.BenchIndex < 0 ? 1.08f : 0.94f;
                        int energySlotIdx = 0;
                        if (game != null && game.Players[p] != null)
                        {
                            var pip = ev.BenchIndex < 0
                                ? game.Players[p].Active
                                : (bIdx < game.Players[p].Bench.Count ? game.Players[p].Bench[bIdx] : null);
                            if (pip != null)
                                energySlotIdx = Mathf.Max(0, pip.Energy.Count - 1);
                        }

                        Vector3 finalEnergyPos = tv.GetAttachedEnergyWorldPos(p, ev.BenchIndex, energySlotIdx);
                        Vector3 edgeDir = targetAnchor != null
                            ? (-targetAnchor.right * 0.046f - targetAnchor.forward * 0.036f)
                            : new Vector3(-0.046f, 0f, -0.036f);
                        Vector3 approachPos = finalEnergyPos + edgeDir * endScale + Vector3.up * 0.002f;

                        queue.Enqueue(new Flight3D
                        {
                            Kind = AnimKind.AttachEnergy,
                            PlayerIndex = p,
                            TargetBenchIndex = ev.BenchIndex,
                            Card = ev.Card,
                            StartWorldPos = handPos,
                            ApproachWorldPos = approachPos,
                            EndWorldPos = finalEnergyPos,
                            BaseRotation = playerRot,
                            StartScale = 1f,
                            EndScale = endScale,
                            StartFlipZ = 0f,
                            EndFlipZ = 0f,
                            ArcHeight = 0.055f,
                            Duration = 0.68f,
                            SlideUnderAtEnd = true,
                            Caption = $"Une {ev.Card?.Name}"
                        });
                        break;
                    }

                case AnimKind.PlayTrainer:
                    {
                        Vector3 centerHover = (tv.CoinAnchor != null ? tv.CoinAnchor.position : tv.PlaymatRoot.position) + Vector3.up * 0.085f;
                        // Tramo 1: de la mano al centro del campo elevándose para mostrarse
                        queue.Enqueue(new Flight3D
                        {
                            Kind = AnimKind.PlayTrainer,
                            PlayerIndex = p,
                            Card = ev.Card,
                            StartWorldPos = handPos,
                            EndWorldPos = centerHover,
                            BaseRotation = playerRot,
                            StartScale = 1f,
                            EndScale = 1.18f,
                            StartFlipZ = 0f,
                            EndFlipZ = 0f,
                            ArcHeight = 0.04f,
                            Duration = 0.32f,
                            Caption = $"Entrenador: {ev.Card?.Name}"
                        });
                        // Tramo 2: del centro del campo a la pila 3D de Descarte (Trash)
                        queue.Enqueue(new Flight3D
                        {
                            Kind = AnimKind.PlayTrainer,
                            PlayerIndex = p,
                            Card = ev.Card,
                            StartWorldPos = centerHover,
                            EndWorldPos = trashPos + Vector3.up * 0.004f,
                            BaseRotation = playerRot,
                            StartScale = 1.18f,
                            EndScale = 1f,
                            StartFlipZ = 0f,
                            EndFlipZ = 0f,
                            ArcHeight = 0.03f,
                            Duration = 0.30f,
                            Caption = $"Entrenador: {ev.Card?.Name}"
                        });
                        break;
                    }

                case AnimKind.RetreatSwap:
                    queue.Enqueue(new Flight3D
                    {
                        Kind = AnimKind.RetreatSwap,
                        PlayerIndex = p,
                        Card = ev.Card,
                        StartWorldPos = benchPos,
                        EndWorldPos = activePos + Vector3.up * 0.002f,
                        BaseRotation = playerRot,
                        StartScale = 0.94f,
                        EndScale = 1.08f,
                        StartFlipZ = 0f,
                        EndFlipZ = 0f,
                        ArcHeight = 0.065f,
                        Duration = 0.40f,
                        Caption = $"Retirada: entra {ev.Card?.Name}"
                    });
                    break;

                case AnimKind.AttackLunge:
                    {
                        Vector3 hitPos = Vector3.Lerp(activePos, oppActivePos, 0.58f) + Vector3.up * 0.015f;
                        CardDef atkCard = ev.Card;
                        // Embestida hacia el Pokémon rival
                        queue.Enqueue(new Flight3D
                        {
                            Kind = AnimKind.AttackLunge,
                            PlayerIndex = p,
                            Card = atkCard,
                            StartWorldPos = activePos,
                            EndWorldPos = hitPos,
                            BaseRotation = playerRot,
                            StartScale = 1.08f,
                            EndScale = 1.15f,
                            StartFlipZ = 0f,
                            EndFlipZ = 0f,
                            ArcHeight = 0.045f,
                            Duration = 0.24f,
                            Caption = $"¡{atkCard?.Name} {Loc.T("usa", "uses")} {ev.Label}!",
                            OnComplete = () =>
                            {
                                TcgAudio.PlayAttackImpact(atkCard?.Type, 35);
                                PolishEffects3D.SpawnElementalAttackVfx(oppActivePos, atkCard?.Type, 35);
                                PolishEffects3D.TriggerCameraShake(0.011f, 0.20f);
                            }
                        });
                        // Regreso al puesto Activo
                        queue.Enqueue(new Flight3D
                        {
                            Kind = AnimKind.AttackLunge,
                            PlayerIndex = p,
                            Card = atkCard,
                            StartWorldPos = hitPos,
                            EndWorldPos = activePos,
                            BaseRotation = playerRot,
                            StartScale = 1.15f,
                            EndScale = 1.08f,
                            StartFlipZ = 0f,
                            EndFlipZ = 0f,
                            ArcHeight = 0.025f,
                            Duration = 0.22f,
                            Caption = $"¡{atkCard?.Name} {Loc.T("usa", "uses")} {ev.Label}!"
                        });
                        break;
                    }

                case AnimKind.KnockOut:
                    {
                        Vector3 koOrigin = ev.BenchIndex < 0 ? activePos : benchPos;
                        queue.Enqueue(new Flight3D
                        {
                            Kind = AnimKind.KnockOut,
                            PlayerIndex = p,
                            Card = ev.Card,
                            StartWorldPos = koOrigin,
                            EndWorldPos = trashPos + Vector3.up * 0.005f,
                            BaseRotation = playerRot,
                            StartScale = ev.BenchIndex < 0 ? 1.08f : 0.94f,
                            EndScale = 1f,
                            StartFlipZ = 0f,
                            EndFlipZ = 0f,
                            YawSpin = 180f,
                            ArcHeight = 0.095f,
                            Duration = 0.50f,
                            Caption = $"¡{ev.Card?.Name} {Loc.T("Fuera de Combate", "Knocked Out")}!"
                        });
                        break;
                    }

                case AnimKind.TakePrize:
                    {
                        int pIdx = Mathf.Clamp(ev.BenchIndex, 0, 5);
                        Vector3 prizeOrigin = tv.PrizeAnchor[p, pIdx] != null ? tv.PrizeAnchor[p, pIdx].position : deckPos;
                        queue.Enqueue(new Flight3D
                        {
                            Kind = AnimKind.TakePrize,
                            PlayerIndex = p,
                            Card = ev.Card,
                            StartWorldPos = prizeOrigin,
                            EndWorldPos = handPos,
                            BaseRotation = playerRot,
                            StartScale = 0.86f,
                            EndScale = p == 0 ? 0.88f : 0.76f,
                            // Sale boca abajo desde los Premios, vuela a pantalla de espaldas, gira y se muestra 0.5s antes de bajar a la mano
                            StartFlipZ = 180f,
                            EndFlipZ = p == 0 ? 0f : 180f,
                            ArcHeight = 0.090f,
                            Duration = p == 0 ? 1.12f : 0.44f,
                            RevealAtScreen = (p == 0),
                            HandSlotIndex = Mathf.Max(0, handCount - 1),
                            HandTotalCount = Mathf.Max(1, handCount),
                            Caption = p == 0
                                ? $"{Loc.T("Tomas 1 Premio", "You take 1 Prize")}: {ev.Card?.Name}"
                                : Loc.T("Cliente toma 1 Premio", "Opponent takes 1 Prize")
                        });
                        break;
                    }

                case AnimKind.CoinFlip:
                    {
                        bool hasCoinQueued = Current != null && Current.Kind == AnimKind.CoinFlip;
                        if (!hasCoinQueued)
                        {
                            foreach (var q in queue)
                            {
                                if (q.Kind == AnimKind.CoinFlip) { hasCoinQueued = true; break; }
                            }
                        }
                        string reason = string.IsNullOrEmpty(ev.Label) ? Loc.T("Moneda", "Coin") : ev.Label;
                        queue.Enqueue(new Flight3D
                        {
                            Kind = AnimKind.CoinFlip,
                            PlayerIndex = p,
                            CoinHeads = ev.CoinHeads,
                            CoinReason = reason,
                            Duration = hasCoinQueued ? 1.12f : 1.38f,
                            Caption = $"{reason} — {Loc.T("Lanzando moneda...", "Flipping coin...")}"
                        });
                        break;
                    }
            }
        }

        void StartNextInQueue()
        {
            while (Current == null && queue.Count > 0)
            {
                var next = queue.Dequeue();
                if (next.IsInstantCallback)
                {
                    InvokeSafe(next.OnStart);
                    InvokeSafe(next.OnComplete);
                    continue;
                }

                Current = next;
                InvokeSafe(Current.OnStart);

                if (Current.Kind == AnimKind.CoinFlip)
                {
                    if (flyingCard != null && flyingCard.gameObject.activeSelf)
                        flyingCard.gameObject.SetActive(false);
                    TableView.Instance?.LaunchGameCoinFlip3D(Current.PlayerIndex, Current.CoinHeads, Current.Duration * 0.86f, Current.CoinReason);
                }
                else
                {
                    EnsureFlyingCard();
                    prevWorldPos = Current.StartWorldPos;
                    flyingCard.gameObject.SetActive(true);

                    switch (Current.Kind)
                    {
                        case AnimKind.Draw:
                        case AnimKind.TakePrize:
                            TcgAudio.PlayCardDraw();
                            break;
                        case AnimKind.AttachEnergy:
                            TcgAudio.PlayAttachEnergy();
                            break;
                        case AnimKind.KnockOut:
                            PolishEffects3D.SpawnKnockOutVfx(Current.StartWorldPos);
                            break;
                        default:
                            GameUI.PlayClickSound(0);
                            break;
                    }
                }
            }

            if (Current == null && flyingCard != null && flyingCard.gameObject.activeSelf)
                flyingCard.gameObject.SetActive(false);
        }

        void CompleteCurrentAndAdvance()
        {
            var finished = Current;
            Current = null;
            if (finished != null)
            {
                if (finished.Kind == AnimKind.Evolve)
                {
                    PolishEffects3D.SpawnEvolveVfx(finished.EndWorldPos, finished.Card?.Type);
                }
                else if (finished.Kind == AnimKind.ToActive ||
                         finished.Kind == AnimKind.ToBench ||
                         finished.Kind == AnimKind.RetreatSwap ||
                         finished.Kind == AnimKind.PlayTrainer)
                {
                    TcgAudio.PlayCardPlace();
                }
                InvokeSafe(finished.OnComplete);
            }
            StartNextInQueue();
        }

        void Update()
        {
            if (Current == null && queue.Count > 0)
            {
                StartNextInQueue();
            }

            if (Current == null)
            {
                if (flyingCard != null && flyingCard.gameObject.activeSelf)
                    flyingCard.gameObject.SetActive(false);
                return;
            }

            Current.Elapsed += Time.deltaTime * Loc.AnimSpeed;
            float rawT = Mathf.Clamp01(Current.Elapsed / Mathf.Max(0.05f, Current.Duration));

            // F7: Lanzamiento de Moneda 3D en cola (sin revelar el resultado ni aplicar el ataque hasta que la moneda termine)
            if (Current.Kind == AnimKind.CoinFlip)
            {
                if (rawT >= 0.72f)
                {
                    string faceTxt = Current.CoinHeads
                        ? Loc.T("CARA (Pikachu)", "HEADS (Pikachu)")
                        : Loc.T("CRUZ (Pokéball)", "TAILS (Pokéball)");
                    Current.Caption = $"{Current.CoinReason}: ¡{faceTxt}!";
                }
                if (rawT >= 1f)
                {
                    CompleteCurrentAndAdvance();
                }
                return;
            }

            // F5: Robo hacia la pantalla (viéndose por el reverso -> vuelta de 180° -> pausa de 0.5s -> bajada a la mano)
            if (Current.RevealAtScreen)
            {
                UpdateScreenRevealFlight(rawT);
                if (rawT >= 1f)
                {
                    CompleteCurrentAndAdvance();
                }
                return;
            }

            // Coreografía en 3 etapas para unir Energía:
            // 1) (0.00..0.36): vuela hacia el borde mientras el Pokémon objetivo se eleva
            // 2) (0.36..0.72): se desliza horizontalmente debajo del Pokémon elevado
            // 3) (0.72..1.00): queda fija en su sitio debajo mientras el Pokémon desciende suavemente a su lugar de origen
            if (Current.SlideUnderAtEnd)
            {
                if (rawT >= 0.72f)
                {
                    if (flyingCard != null && flyingCard.gameObject.activeSelf)
                        flyingCard.gameObject.SetActive(false);
                    if (rawT >= 1f)
                    {
                        CompleteCurrentAndAdvance();
                    }
                    return;
                }

                Vector3 energyPos;
                float energyScale;
                if (rawT < 0.36f)
                {
                    float u = EaseOutCubic(rawT / 0.36f);
                    energyPos = Vector3.Lerp(Current.StartWorldPos, Current.ApproachWorldPos, u)
                              + Vector3.up * (Mathf.Sin((rawT / 0.36f) * Mathf.PI) * Current.ArcHeight);
                    energyScale = Mathf.Lerp(Current.StartScale, Current.EndScale, u);
                }
                else
                {
                    float u = EaseInOutCubic((rawT - 0.36f) / 0.36f);
                    energyPos = Vector3.Lerp(Current.ApproachWorldPos, Current.EndWorldPos, u);
                    energyScale = Current.EndScale;
                }

                Vector3 eVel = (energyPos - prevWorldPos) / Mathf.Max(0.001f, Time.deltaTime);
                prevWorldPos = energyPos;
                Vector3 eLocalVel = Quaternion.Inverse(Current.BaseRotation) * eVel;
                float ePitch = rawT < 0.36f ? Mathf.Clamp(eLocalVel.z * 20f, -16f, 16f) : 0f;
                float eRoll = rawT < 0.36f ? Mathf.Clamp(-eLocalVel.x * 20f, -16f, 16f) : 0f;

                flyingCard.Setup(Current.Card, false, energyScale, rawT < 0.55f, 0f);
                flyingCard.SetShadowEnabled(true);
                flyingCard.SetRaycastEnabled(false);
                flyingCard.transform.position = energyPos;
                flyingCard.transform.rotation = Current.BaseRotation * Quaternion.Euler(ePitch, 0f, eRoll);
                return;
            }

            float easedT = EaseOutCubic(rawT);

            // Posición interpolada con arco parabólico 3D
            Vector3 flatPos = Vector3.Lerp(Current.StartWorldPos, Current.EndWorldPos, easedT);
            float arcY = Mathf.Sin(rawT * Mathf.PI) * Current.ArcHeight;
            Vector3 worldPos = flatPos + Vector3.up * arcY;

            // Inclinación física según la velocidad instantánea en 3D (pitch y roll suaves)
            Vector3 vel = (worldPos - prevWorldPos) / Mathf.Max(0.001f, Time.deltaTime);
            prevWorldPos = worldPos;
            Vector3 localVel = Quaternion.Inverse(Current.BaseRotation) * vel;
            float pitch = Mathf.Clamp(localVel.z * 26f, -22f, 22f);
            float roll = Mathf.Clamp(-localVel.x * 26f, -22f, 22f);

            // Flip 3D (180° -> 0° al robar/revelar) y giro en Y
            float flipZ = Mathf.Lerp(Current.StartFlipZ, Current.EndFlipZ, EaseInOutCubic(rawT));
            float yaw = Current.YawSpin * easedT;

            // Escala (con ligero pulso al aterrizar una evolución)
            float scale = Mathf.Lerp(Current.StartScale, Current.EndScale, easedT);
            if (Current.PulseScaleAtEnd && rawT > 0.72f)
            {
                float pT = (rawT - 0.72f) / 0.28f;
                scale *= 1f + 0.14f * Mathf.Sin(pT * Mathf.PI);
            }

            // Determinar si en este instante del giro 3D se debe mostrar el reverso o el frente
            bool showBack = flipZ > 90f;
            flyingCard.Setup(Current.Card, showBack, scale, true, 0f);
            flyingCard.SetShadowEnabled(true);
            flyingCard.SetRaycastEnabled(false);
            flyingCard.transform.position = worldPos;

            // Rotación combinada: orientación del jugador + inclinación por velocidad + giro 3D de flip
            float residualFlipTilt = showBack ? (flipZ - 180f) : flipZ;
            flyingCard.transform.rotation = Current.BaseRotation * Quaternion.Euler(pitch, yaw, roll + residualFlipTilt);

            if (rawT >= 1f)
            {
                CompleteCurrentAndAdvance();
            }
        }

        /// <summary>
        /// F5: Anima el robo de una carta del jugador en 4 etapas:
        /// 1) Vuela desde el Mazo hacia la pantalla viéndose por detrás (reverso).
        /// 2) Da una vuelta de 180° frente a la pantalla para revelar qué carta es.
        /// 3) Se mantiene visible durante medio segundo (0.50s).
        /// 4) Baja suavemente hacia su lugar en el abanico 3D de la mano.
        /// </summary>
        void UpdateScreenRevealFlight(float rawT)
        {
            var cam = TableView.GetActiveCamera();
            var tv = TableView.Instance;

            Vector3 screenPos;
            Quaternion screenFrontRot;
            if (cam != null)
            {
                screenPos = cam.transform.position + cam.transform.forward * 0.34f - cam.transform.up * 0.010f;
                screenFrontRot = Quaternion.LookRotation(cam.transform.up, -cam.transform.forward);
            }
            else
            {
                screenPos = Current.StartWorldPos + Vector3.up * 0.26f;
                screenFrontRot = Current.BaseRotation;
            }

            Quaternion deckBackRot = Current.BaseRotation * Quaternion.Euler(0f, 0f, 180f);
            Quaternion screenBackRot = screenFrontRot * Quaternion.Euler(0f, 0f, 180f);

            float tSec = Current.Elapsed;
            const float flyToScreenEnd = 0.24f;
            const float flipEnd = 0.40f;
            const float holdEnd = 0.90f; // 0.40 -> 0.90 = 0.50s exactos de revelación frente a la pantalla

            Vector3 worldPos;
            Quaternion worldRot;
            float scale;

            if (tSec < flyToScreenEnd)
            {
                // Etapa 1: Vuela desde el tope del mazo hacia la pantalla viéndose desde la parte de atrás
                float u = EaseOutCubic(Mathf.Clamp01(tSec / flyToScreenEnd));
                worldPos = Vector3.Lerp(Current.StartWorldPos, screenPos, u) + Vector3.up * (Mathf.Sin(u * Mathf.PI) * 0.042f);
                worldRot = Quaternion.Slerp(deckBackRot, screenBackRot, u);
                scale = Mathf.Lerp(Current.StartScale, 1.15f, u);
            }
            else if (tSec < flipEnd)
            {
                // Etapa 2: Da una vuelta de 180° frente a la pantalla para revelar qué carta es
                float u = EaseInOutCubic(Mathf.Clamp01((tSec - flyToScreenEnd) / (flipEnd - flyToScreenEnd)));
                float flipDeg = Mathf.Lerp(180f, 0f, u);
                worldPos = screenPos - (cam != null ? cam.transform.forward : Vector3.forward) * (Mathf.Sin(u * Mathf.PI) * 0.018f);
                worldRot = screenFrontRot * Quaternion.Euler(0f, 0f, flipDeg);
                scale = 1.15f + Mathf.Sin(u * Mathf.PI) * 0.06f;
            }
            else if (tSec < holdEnd)
            {
                // Etapa 3: Se mantiene medio segundo (0.50s) frente a la pantalla mostrando la carta revelada
                float u = Mathf.Clamp01((tSec - flipEnd) / (holdEnd - flipEnd));
                float breathe = Mathf.Sin(u * Mathf.PI);
                worldPos = screenPos + (cam != null ? cam.transform.up : Vector3.up) * (breathe * 0.004f);
                worldRot = screenFrontRot * Quaternion.Euler(Mathf.Sin(u * Mathf.PI * 2f) * 1.6f, 0f, 0f);
                scale = 1.15f;
            }
            else
            {
                // Etapa 4: Baja suavemente desde la pantalla hasta su posición en el abanico de la mano
                float dropDur = Mathf.Max(0.12f, Current.Duration - holdEnd);
                float u = EaseInOutCubic(Mathf.Clamp01((tSec - holdEnd) / dropDur));
                Vector3 targetHandPos = (tv != null && Current.HandSlotIndex >= 0)
                    ? tv.GetHandCardWorldPos(0, Current.HandSlotIndex, Current.HandTotalCount)
                    : Current.EndWorldPos;
                Quaternion targetHandRot = (tv != null && tv.HandAnchor[0] != null)
                    ? tv.GetHandCardWorldRot(0, Current.HandSlotIndex, Current.HandTotalCount)
                    : Current.BaseRotation;

                worldPos = Vector3.Lerp(screenPos, targetHandPos, u);
                worldRot = Quaternion.Slerp(screenFrontRot, targetHandRot, u);
                scale = Mathf.Lerp(1.15f, Current.EndScale, u);
            }

            // Configuramos con faceDown = false para que tanto FrontFace (+Y) como BackFace (-Y) tengan su textura lista;
            // cuando worldRot está girado 180° en Z, la cámara ve BackFace; al girar a 0°, ve FrontFace.
            flyingCard.Setup(Current.Card, false, scale, true, 0f);
            flyingCard.SetShadowEnabled(false);
            flyingCard.SetRaycastEnabled(false);
            flyingCard.transform.position = worldPos;
            flyingCard.transform.rotation = worldRot;
            prevWorldPos = worldPos;
        }

        static float EaseOutCubic(float x)
        {
            float inv = 1f - x;
            return 1f - inv * inv * inv;
        }

        static float EaseInOutCubic(float x)
        {
            return x < 0.5f ? 4f * x * x * x : 1f - Mathf.Pow(-2f * x + 2f, 3f) * 0.5f;
        }
    }
}
