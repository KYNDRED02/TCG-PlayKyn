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
            public CardDef Card;
            public Vector3 StartWorldPos;
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
            public bool SlideUnderAtEnd; // true para Energías que se deslizan debajo del Pokémon
            public bool PulseScaleAtEnd; // true para Evoluciones que se posan encima
        }

        readonly Queue<Flight3D> queue = new Queue<Flight3D>();
        public Flight3D Current { get; private set; }
        CardView flyingCard;
        Vector3 prevWorldPos;
        Vector3? customNextHandOrigin;

        public bool IsAnimating => Current != null || queue.Count > 0;
        public string CurrentCaption => Current?.Caption;

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
                        EndScale = 1.04f,
                        // Sale boca abajo del tope del mazo y gira 180° en el aire si es del jugador
                        StartFlipZ = 180f,
                        EndFlipZ = p == 0 ? 0f : 180f,
                        ArcHeight = 0.085f,
                        Duration = 0.40f,
                        Caption = p == 0 ? $"Robas {ev.Card?.Name}" : "Cliente roba 1 carta"
                    });
                    break;

                case AnimKind.ToActive:
                    queue.Enqueue(new Flight3D
                    {
                        Kind = AnimKind.ToActive,
                        PlayerIndex = p,
                        Card = ev.Card,
                        StartWorldPos = ev.BenchIndex >= 0 ? benchPos : handPos,
                        EndWorldPos = activePos + Vector3.up * 0.002f,
                        BaseRotation = playerRot,
                        StartScale = ev.BenchIndex >= 0 ? 0.94f : 1f,
                        EndScale = 1.08f,
                        StartFlipZ = 0f,
                        EndFlipZ = 0f,
                        ArcHeight = 0.075f,
                        Duration = 0.42f,
                        Caption = $"{ev.Card?.Name} entra al Campo de Batalla"
                    });
                    break;

                case AnimKind.ToBench:
                    queue.Enqueue(new Flight3D
                    {
                        Kind = AnimKind.ToBench,
                        PlayerIndex = p,
                        Card = ev.Card,
                        StartWorldPos = handPos,
                        EndWorldPos = benchPos + Vector3.up * 0.002f,
                        BaseRotation = playerRot,
                        StartScale = 1f,
                        EndScale = 0.94f,
                        StartFlipZ = 0f,
                        EndFlipZ = 0f,
                        ArcHeight = 0.070f,
                        Duration = 0.38f,
                        Caption = $"{ev.Card?.Name} baja a la Banca"
                    });
                    break;

                case AnimKind.Evolve:
                    {
                        Vector3 dest = ev.BenchIndex < 0 ? activePos : benchPos;
                        float endScale = ev.BenchIndex < 0 ? 1.08f : 0.94f;
                        queue.Enqueue(new Flight3D
                        {
                            Kind = AnimKind.Evolve,
                            PlayerIndex = p,
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
                        Vector3 targetPos = targetAnchor != null ? targetAnchor.position : activePos;
                        float endScale = ev.BenchIndex < 0 ? 1.08f : 0.94f;
                        // Punto justo al borde del Pokémon desde donde se desliza por debajo
                        Vector3 underOffset = (targetAnchor != null ? -targetAnchor.right * 0.016f - targetAnchor.forward * 0.014f : new Vector3(-0.016f, 0f, -0.014f)) * endScale;
                        queue.Enqueue(new Flight3D
                        {
                            Kind = AnimKind.AttachEnergy,
                            PlayerIndex = p,
                            Card = ev.Card,
                            StartWorldPos = handPos,
                            EndWorldPos = targetPos + underOffset + Vector3.up * 0.0006f,
                            BaseRotation = playerRot,
                            StartScale = 1f,
                            EndScale = endScale,
                            StartFlipZ = 0f,
                            EndFlipZ = 0f,
                            ArcHeight = 0.065f,
                            Duration = 0.42f,
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
                        // Embestida hacia el Pokémon rival
                        queue.Enqueue(new Flight3D
                        {
                            Kind = AnimKind.AttackLunge,
                            PlayerIndex = p,
                            Card = ev.Card,
                            StartWorldPos = activePos,
                            EndWorldPos = hitPos,
                            BaseRotation = playerRot,
                            StartScale = 1.08f,
                            EndScale = 1.15f,
                            StartFlipZ = 0f,
                            EndFlipZ = 0f,
                            ArcHeight = 0.045f,
                            Duration = 0.24f,
                            Caption = $"¡{ev.Card?.Name} usa {ev.Label}!"
                        });
                        // Regreso al puesto Activo
                        queue.Enqueue(new Flight3D
                        {
                            Kind = AnimKind.AttackLunge,
                            PlayerIndex = p,
                            Card = ev.Card,
                            StartWorldPos = hitPos,
                            EndWorldPos = activePos,
                            BaseRotation = playerRot,
                            StartScale = 1.15f,
                            EndScale = 1.08f,
                            StartFlipZ = 0f,
                            EndFlipZ = 0f,
                            ArcHeight = 0.025f,
                            Duration = 0.22f,
                            Caption = $"¡{ev.Card?.Name} usa {ev.Label}!"
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
                            Caption = $"¡{ev.Card?.Name} Fuera de Combate!"
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
                            EndScale = 1.04f,
                            // Sale boca abajo desde los Premios y se voltea en el aire para el jugador
                            StartFlipZ = 180f,
                            EndFlipZ = p == 0 ? 0f : 180f,
                            ArcHeight = 0.090f,
                            Duration = 0.46f,
                            Caption = p == 0 ? $"Tomas 1 Premio ({ev.Card?.Name})" : "Cliente toma 1 Premio"
                        });
                        break;
                    }
            }
        }

        void Update()
        {
            if (Current == null && queue.Count > 0)
            {
                Current = queue.Dequeue();
                EnsureFlyingCard();
                prevWorldPos = Current.StartWorldPos;
                flyingCard.gameObject.SetActive(true);
                GameUI.PlayClickSound(0);
            }

            if (Current == null)
            {
                if (flyingCard != null && flyingCard.gameObject.activeSelf)
                    flyingCard.gameObject.SetActive(false);
                return;
            }

            Current.Elapsed += Time.deltaTime;
            float rawT = Mathf.Clamp01(Current.Elapsed / Mathf.Max(0.05f, Current.Duration));
            float easedT = EaseOutCubic(rawT);

            // Posición interpolada con arco parabólico 3D
            Vector3 flatPos;
            float arcY;
            if (Current.SlideUnderAtEnd && rawT > 0.68f)
            {
                // En el último tramo de unir Energía, la carta baja a ras del tapete y se desliza horizontalmente debajo del Pokémon
                float subT = (rawT - 0.68f) / 0.32f;
                Vector3 approachPos = Vector3.Lerp(Current.StartWorldPos, Current.EndWorldPos + new Vector3(-0.025f, 0.002f, -0.020f), EaseOutCubic(0.68f));
                flatPos = Vector3.Lerp(approachPos, Current.EndWorldPos, EaseOutCubic(subT));
                arcY = Mathf.Lerp(0.012f, 0f, subT);
            }
            else
            {
                flatPos = Vector3.Lerp(Current.StartWorldPos, Current.EndWorldPos, easedT);
                arcY = Mathf.Sin(rawT * Mathf.PI) * Current.ArcHeight;
            }

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
            flyingCard.SetRaycastEnabled(false);
            flyingCard.transform.position = worldPos;

            // Rotación combinada: orientación del jugador + inclinación por velocidad + giro 3D de flip
            float residualFlipTilt = showBack ? (flipZ - 180f) : flipZ;
            flyingCard.transform.rotation = Current.BaseRotation * Quaternion.Euler(pitch, yaw, roll + residualFlipTilt);

            if (rawT >= 1f)
            {
                Current = null;
                if (queue.Count == 0 && flyingCard != null)
                    flyingCard.gameObject.SetActive(false);
            }
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
