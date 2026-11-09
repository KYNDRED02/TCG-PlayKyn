using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace PokeTCGPlay
{
    /// <summary>
    /// Submódulo de inspección 3D para la carta Pokédex y efectos de ordenamiento superior de la baraja.
    /// 
    /// ¿Para qué sirve?
    /// - Hace volar las 5 primeras cartas de la baraja hacia el centro de la pantalla frente a la cámara.
    /// - Oscurece ligeramente el fondo de la mesa (PokedexDimAlpha) para destacar las cartas.
    /// - Permite reordenar cartas haciendo clic o mediante los controles de interfaz.
    /// - Al confirmar la selección, las cartas regresan de forma animada a la parte superior del mazo.
    /// 
    /// Variables ajustables:
    /// - 'FlyOutDuration' y 'FlyInDuration': Velocidades de salida y retorno de las cartas.
    /// - 'PokedexDimAlpha': Intensidad del oscurecimiento del fondo.
    /// </summary>
    public partial class TableView
    {
        // --- Parte 1: Inspección y Reordenamiento de Mazo 3D (Pokédex & Top Deck) ---
        public class PokedexSession
        {
            public PlayerState Player;
            public int Count;
            public List<CardDef> OriginalCards = new List<CardDef>();
            public List<CardDef> CurrentOrder = new List<CardDef>();
            public Action<List<CardDef>> OnConfirm;
            public enum AnimPhase { FlyOut, Interacting, FlyIn, Finished }
            public AnimPhase Phase = AnimPhase.FlyOut;
            public float Timer;
            public int SelectedIdx = -1;
            public int HoveredIdx = -1;
        }

        readonly CardView[] pokedexCardViews = new CardView[5];
        PokedexSession pokedexSession;
        public bool IsPokedexActive => pokedexSession != null && pokedexSession.Phase != PokedexSession.AnimPhase.Finished;
        public bool IsPokedexInteracting => pokedexSession != null && pokedexSession.Phase == PokedexSession.AnimPhase.Interacting;
        public float PokedexDimAlpha { get; private set; }
        public PokedexSession CurrentPokedexSession => pokedexSession;



        // ==================== Parte 1: Inspección y Reordenamiento de Mazo 3D (Pokédex) ====================

        public void StartPokedex3D(PlayerState p, int count, Action<List<CardDef>> onConfirm)
        {
            if (p == null || count <= 0) return;
            count = Math.Min(5, count);
            pokedexSession = new PokedexSession
            {
                Player = p,
                Count = count,
                OriginalCards = p.Deck.Take(count).ToList(),
                CurrentOrder = p.Deck.Take(count).ToList(),
                OnConfirm = onConfirm,
                Phase = PokedexSession.AnimPhase.FlyOut,
                Timer = 0f,
                SelectedIdx = -1,
                HoveredIdx = -1
            };
            PokedexDimAlpha = 0f;

            EnsurePokedexCardViews();
            for (int i = 0; i < pokedexCardViews.Length; i++)
            {
                if (pokedexCardViews[i] != null)
                {
                    pokedexCardViews[i].gameObject.SetActive(false);
                    pokedexCardViews[i].SetRaycastEnabled(false);
                    pokedexCardViews[i].SetShadowEnabled(false);
                }
            }
            TcgAudio.PlayCardDraw();
        }

        public void ConfirmPokedexOrder()
        {
            if (pokedexSession == null || pokedexSession.Phase != PokedexSession.AnimPhase.Interacting) return;
            pokedexSession.Phase = PokedexSession.AnimPhase.FlyIn;
            pokedexSession.Timer = 0f;
            TcgAudio.PlayCardDraw();
        }

        public void SwapPokedexCards(int idxA, int idxB)
        {
            if (pokedexSession == null || pokedexSession.CurrentOrder == null) return;
            if (idxA < 0 || idxA >= pokedexSession.CurrentOrder.Count || idxB < 0 || idxB >= pokedexSession.CurrentOrder.Count) return;
            if (idxA == idxB) return;
            var temp = pokedexSession.CurrentOrder[idxA];
            pokedexSession.CurrentOrder[idxA] = pokedexSession.CurrentOrder[idxB];
            pokedexSession.CurrentOrder[idxB] = temp;
            pokedexSession.SelectedIdx = -1;
            GameUI.PlayClickSound(0);
        }

        public void InvertPokedexOrder()
        {
            if (pokedexSession == null || pokedexSession.CurrentOrder == null) return;
            pokedexSession.CurrentOrder.Reverse();
            pokedexSession.SelectedIdx = -1;
            GameUI.PlayClickSound(0);
        }

        public void ResetPokedexOrder()
        {
            if (pokedexSession == null || pokedexSession.OriginalCards == null) return;
            pokedexSession.CurrentOrder = new List<CardDef>(pokedexSession.OriginalCards);
            pokedexSession.SelectedIdx = -1;
            GameUI.PlayClickSound(0);
        }

        public void ResetPokedexSession()
        {
            if (pokedexSession != null)
            {
                pokedexSession = null;
                PokedexDimAlpha = 0f;
            }
            for (int i = 0; i < pokedexCardViews.Length; i++)
            {
                if (pokedexCardViews[i] != null)
                    pokedexCardViews[i].gameObject.SetActive(false);
            }
        }

        void EnsurePokedexCardViews()
        {
            for (int i = 0; i < 5; i++)
            {
                if (pokedexCardViews[i] == null)
                {
                    pokedexCardViews[i] = CardView.Create(PlaymatRoot);
                    pokedexCardViews[i].gameObject.name = "PokeTCG_PokedexCard_" + i;
                    pokedexCardViews[i].SetRaycastEnabled(false);
                    pokedexCardViews[i].SetShadowEnabled(false);
                    pokedexCardViews[i].gameObject.SetActive(false);
                }
            }
        }

        void UpdatePokedex3D(float dt)
        {
            if (pokedexSession == null) return;

            var cam = GetActiveCamera();
            if (cam == null) return;

            EnsurePokedexCardViews();

            Vector3 centerPos = cam.transform.position + cam.transform.forward * 0.38f - cam.transform.up * 0.012f;
            Quaternion faceCamRot = Quaternion.LookRotation(cam.transform.up, -cam.transform.forward);
            float spacing = 0.068f;

            const float FlyOutDuration = 0.52f;
            const float StaggerDelay = 0.055f;
            float totalOutTime = FlyOutDuration + (pokedexSession.Count - 1) * StaggerDelay;

            const float FlyInDuration = 0.42f;
            float totalInTime = FlyInDuration + (pokedexSession.Count - 1) * StaggerDelay;

            if (pokedexSession.Phase == PokedexSession.AnimPhase.FlyOut)
            {
                pokedexSession.Timer += dt;
                PokedexDimAlpha = Mathf.MoveTowards(PokedexDimAlpha, 1f, dt * 2.6f);

                for (int i = 0; i < pokedexSession.Count; i++)
                {
                    var cardDef = pokedexSession.CurrentOrder[i];
                    var cv = pokedexCardViews[i];
                    if (cv == null) continue;

                    Vector3 deckStartPos = (DeckAnchor[0] != null ? DeckAnchor[0].position : PlaymatRoot.position)
                        + Vector3.up * (0.003f + (pokedexSession.Player.Deck.Count - i) * 0.00054f);
                    Quaternion deckStartRot = (DeckAnchor[0] != null ? DeckAnchor[0].rotation : PlaymatRoot.rotation)
                        * Quaternion.Euler(0f, 0f, 180f);

                    float xOffset = (i - (pokedexSession.Count - 1) * 0.5f) * spacing;
                    Vector3 targetCardPos = centerPos + cam.transform.right * xOffset;
                    Quaternion targetCardRot = faceCamRot;

                    float cardT = Mathf.Clamp01((pokedexSession.Timer - i * StaggerDelay) / FlyOutDuration);
                    float u = EaseOutCubic(cardT);
                    float arcY = Mathf.Sin(u * Mathf.PI) * 0.045f;
                    Vector3 curPos = Vector3.Lerp(deckStartPos, targetCardPos, u) + cam.transform.up * arcY;
                    Quaternion curRot = Quaternion.Slerp(deckStartRot, targetCardRot, u);
                    float curScale = Mathf.Lerp(1.0f, 1.06f, u);

                    cv.gameObject.SetActive(true);
                    cv.Setup(cardDef, false, curScale, true, 0f);
                    cv.transform.position = curPos;
                    cv.transform.rotation = curRot;
                    cv.SetHighlight(false, Color.clear);
                }

                if (pokedexSession.Timer >= totalOutTime)
                {
                    pokedexSession.Phase = PokedexSession.AnimPhase.Interacting;
                    pokedexSession.Timer = 0f;
                }
            }
            else if (pokedexSession.Phase == PokedexSession.AnimPhase.Interacting)
            {
                PokedexDimAlpha = 1f;

                for (int i = 0; i < pokedexSession.Count; i++)
                {
                    var cardDef = pokedexSession.CurrentOrder[i];
                    var cv = pokedexCardViews[i];
                    if (cv == null) continue;

                    float xOffset = (i - (pokedexSession.Count - 1) * 0.5f) * spacing;
                    Vector3 targetCardPos = centerPos + cam.transform.right * xOffset;
                    Quaternion targetCardRot = faceCamRot;

                    bool isSel = pokedexSession.SelectedIdx == i;
                    bool isHov = pokedexSession.HoveredIdx == i;

                    float forwardLift = isSel ? 0.024f : (isHov ? 0.014f : 0f);
                    float curScale = isSel ? 1.15f : (isHov ? 1.10f : 1.06f);
                    Vector3 curPos = targetCardPos - cam.transform.forward * forwardLift;

                    cv.gameObject.SetActive(true);
                    cv.Setup(cardDef, false, curScale, true, 0f);
                    cv.transform.position = curPos;
                    cv.transform.rotation = targetCardRot;

                    if (isSel)
                    {
                        float pulse = 0.80f + 0.20f * Mathf.Sin(Time.time * 6.5f);
                        cv.SetHighlight(true, new Color(1f, 0.86f, 0.22f, pulse));
                    }
                    else if (isHov)
                    {
                        cv.SetHighlight(true, new Color(0.35f, 0.95f, 0.55f, 0.85f));
                    }
                    else
                    {
                        cv.SetHighlight(false, Color.clear);
                    }
                }
            }
            else if (pokedexSession.Phase == PokedexSession.AnimPhase.FlyIn)
            {
                pokedexSession.Timer += dt;
                PokedexDimAlpha = Mathf.MoveTowards(PokedexDimAlpha, 0f, dt * 2.4f);

                for (int i = 0; i < pokedexSession.Count; i++)
                {
                    var cardDef = pokedexSession.CurrentOrder[i];
                    var cv = pokedexCardViews[i];
                    if (cv == null) continue;

                    Vector3 deckStartPos = (DeckAnchor[0] != null ? DeckAnchor[0].position : PlaymatRoot.position)
                        + Vector3.up * (0.003f + (pokedexSession.Player.Deck.Count - i) * 0.00054f);
                    Quaternion deckStartRot = (DeckAnchor[0] != null ? DeckAnchor[0].rotation : PlaymatRoot.rotation)
                        * Quaternion.Euler(0f, 0f, 180f);

                    float xOffset = (i - (pokedexSession.Count - 1) * 0.5f) * spacing;
                    Vector3 targetCardPos = centerPos + cam.transform.right * xOffset;

                    float cardT = Mathf.Clamp01((pokedexSession.Timer - (pokedexSession.Count - 1 - i) * StaggerDelay) / FlyInDuration);
                    float u = EaseInOutCubic(cardT);
                    float arcY = Mathf.Sin(u * Mathf.PI) * 0.040f;
                    Vector3 curPos = Vector3.Lerp(targetCardPos, deckStartPos, u) + cam.transform.up * arcY;
                    Quaternion curRot = Quaternion.Slerp(faceCamRot, deckStartRot, u);
                    float curScale = Mathf.Lerp(1.06f, 1.0f, u);

                    cv.gameObject.SetActive(true);
                    cv.Setup(cardDef, false, curScale, true, 0f);
                    cv.transform.position = curPos;
                    cv.transform.rotation = curRot;
                    cv.SetHighlight(false, Color.clear);
                }

                if (pokedexSession.Timer >= totalInTime)
                {
                    pokedexSession.Phase = PokedexSession.AnimPhase.Finished;
                    for (int i = 0; i < pokedexCardViews.Length; i++)
                    {
                        if (pokedexCardViews[i] != null)
                            pokedexCardViews[i].gameObject.SetActive(false);
                    }
                    PokedexDimAlpha = 0f;
                    var onConfirm = pokedexSession.OnConfirm;
                    var finalOrder = new List<CardDef>(pokedexSession.CurrentOrder);
                    pokedexSession = null;
                    onConfirm?.Invoke(finalOrder);
                }
            }
        }

        static float EaseOutCubic(float x) => 1f - Mathf.Pow(1f - x, 3f);
        static float EaseInOutCubic(float x) => x < 0.5f ? 4f * x * x * x : 1f - Mathf.Pow(-2f * x + 2f, 3f) / 2f;
    }

}
