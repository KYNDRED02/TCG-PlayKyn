using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace PokeTCGPlay
{
    /// <summary>
    /// Submódulo de interfaz de usuario para el efecto interactivo de la carta Pokédex.
    /// 
    /// ¿Para qué sirve?
    /// - Muestra los botones de acción para reorganizar las 5 primeras cartas de la baraja:
    ///   intercambiar, invertir orden y confirmar nuevo orden.
    /// - Dibuja las instrucciones en pantalla para el jugador.
    /// 
    /// Notas de edición:
    /// - Si cambias los tamaños de botones o colores del texto, se verán reflejados al activar la Pokédex.
    /// </summary>
    public partial class GameUI
    {
void DrawPokedexInteractiveUI()
        {
            var tv = TableView.Instance;
            if (tv == null) return;
            var session = tv.CurrentPokedexSession;
            if (session == null || session.CurrentOrder == null || session.Count == 0) return;

            var cam = TableView.GetActiveCamera();
            if (cam == null) return;

            // 1) Cabecera superior con título y ayuda
            float headerW = Mathf.Clamp(Screen.width * 0.54f, 540f, 740f);
            float headerH = 74f;
            Rect headerRect = new Rect((Screen.width - headerW) * 0.5f, 22f, headerW, headerH);
            DrawNativeBox(headerRect, sPanelSprite, new Color(0.08f, 0.12f, 0.20f, 0.96f));
            DrawNativeLabel(
                new Rect(headerRect.x + 16f, headerRect.y + 8f, headerRect.width - 32f, 32f),
                Loc.T("Pokédex — Reorganizar cartas del Mazo", "Pokédex — Rearrange Deck Cards"),
                21f,
                TextAlignmentOptions.Center,
                new Color(1f, 0.88f, 0.40f, 1f),
                autoSize: true);
            DrawNativeLabel(
                new Rect(headerRect.x + 16f, headerRect.y + 40f, headerRect.width - 32f, 24f),
                Loc.T("Izquierda = Próxima carta a robar (#1)   |   Derecha = Quinta carta (#5)", "Left = Next to draw (#1)   |   Right = Fifth card down (#5)"),
                14f,
                TextAlignmentOptions.Center,
                new Color(0.82f, 0.90f, 1f, 0.92f),
                autoSize: true);

            // 2) Detección de interacción sobre las cartas 3D y controles inferiores de cada carta
            float spacing = 0.068f;
            Vector3 centerPos = cam.transform.position + cam.transform.forward * 0.38f - cam.transform.up * 0.012f;
            int hoveredThisFrame = -1;

            for (int i = 0; i < session.Count; i++)
            {
                float xOffset = (i - (session.Count - 1) * 0.5f) * spacing;
                Vector3 targetCardPos = centerPos + cam.transform.right * xOffset;
                Vector3 sp = cam.WorldToScreenPoint(targetCardPos);
                if (sp.z <= 0.05f) continue;

                float cardW = Mathf.Clamp(Screen.width * 0.114f, 126f, 182f);
                float cardH = cardW * 1.39f;
                float cardGuiX = sp.x;
                float cardGuiY = Screen.height - sp.y;
                Rect cardRect = new Rect(cardGuiX - cardW * 0.5f, cardGuiY - cardH * 0.5f, cardW, cardH);

                Vector2 mousePos = Event.current.mousePosition;
                if (cardRect.Contains(mousePos))
                    hoveredThisFrame = i;

                if (Event.current.type == EventType.MouseDown && cardRect.Contains(mousePos))
                {
                    if (Event.current.button == 0) // Clic izquierdo: seleccionar / intercambiar
                    {
                        if (session.SelectedIdx < 0)
                        {
                            session.SelectedIdx = i;
                            PlayClickSound(0);
                        }
                        else if (session.SelectedIdx == i)
                        {
                            session.SelectedIdx = -1;
                            PlayClickSound(0);
                        }
                        else
                        {
                            tv.SwapPokedexCards(session.SelectedIdx, i);
                        }
                        Event.current.Use();
                    }
                    else if (Event.current.button == 1) // Clic derecho: zoom / inspección
                    {
                        PlayClickSound(1);
                        StartZoom(session.CurrentOrder[i], null, cardRect, targetCardPos, Quaternion.LookRotation(cam.transform.up, -cam.transform.forward), 1.06f);
                        Event.current.Use();
                    }
                }

                // Badge de posición (#1 Top, #2, etc.)
                float underY = cardRect.yMax + 10f;
                float badgeW = 76f;
                float badgeH = 26f;
                Rect badgeRect = new Rect(cardRect.center.x - badgeW * 0.5f, underY, badgeW, badgeH);

                string posLabel = i == 0 ? Loc.T("#1 (Top)", "#1 (Top)") : $"#{i + 1}";
                Color badgeBg = i == 0 ? new Color(0.12f, 0.32f, 0.20f, 0.95f) : new Color(0.10f, 0.16f, 0.26f, 0.94f);
                DrawNativeBox(badgeRect, sSubPanelSprite, badgeBg);
                DrawNativeLabel(
                    badgeRect,
                    posLabel,
                    13.5f,
                    TextAlignmentOptions.Center,
                    i == 0 ? new Color(0.55f, 1f, 0.72f, 1f) : new Color(1f, 0.92f, 0.55f, 1f),
                    autoSize: true);

                // Botones de flecha [<] y [>] para mover la carta a la izquierda o derecha
                float arrowW = 28f;
                float arrowH = 26f;
                if (i > 0)
                {
                    Rect lArr = new Rect(badgeRect.x - arrowW - 4f, underY, arrowW, arrowH);
                    if (DrawNativeButton(lArr, "<", 13.5f, sBtnBlueN, sBtnBlueH, sBtnBlueA))
                    {
                        tv.SwapPokedexCards(i, i - 1);
                    }
                }
                if (i < session.Count - 1)
                {
                    Rect rArr = new Rect(badgeRect.xMax + 4f, underY, arrowW, arrowH);
                    if (DrawNativeButton(rArr, ">", 13.5f, sBtnBlueN, sBtnBlueH, sBtnBlueA))
                    {
                        tv.SwapPokedexCards(i, i + 1);
                    }
                }
            }
            session.HoveredIdx = hoveredThisFrame;

            // 3) Barra de acciones inferior (Invertir, Restaurar y Confirmar Orden)
            float barW = 540f;
            float barH = 50f;
            Rect barRect = new Rect((Screen.width - barW) * 0.5f, Screen.height - barH - 22f, barW, barH);
            DrawNativeBox(barRect, sPanelSprite, new Color(0.08f, 0.12f, 0.20f, 0.96f));

            float bx = barRect.x + 12f;
            float by = barRect.y + 8f;
            float bh = 34f;

            if (DrawNativeButton(new Rect(bx, by, 120f, bh), Loc.T("Invertir", "Invert"), 14f, sBtnBlueN, sBtnBlueH, sBtnBlueA))
            {
                tv.InvertPokedexOrder();
            }
            bx += 128f;

            if (DrawNativeButton(new Rect(bx, by, 120f, bh), Loc.T("Restaurar", "Reset"), 14f, sBtnBlueN, sBtnBlueH, sBtnBlueA))
            {
                tv.ResetPokedexOrder();
            }
            bx += 128f;

            if (DrawNativeButton(new Rect(bx, by, 252f, bh), Loc.T("Confirmar Orden", "Confirm Order"), 16f, sBtnGreenN, sBtnGreenH, sBtnGreenA))
            {
                tv.ConfirmPokedexOrder();
            }

            // Atajos de teclado: Enter, Espacio o Escape confirman el orden
            if (Event.current.type == EventType.KeyDown && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter || Event.current.keyCode == KeyCode.Space || Event.current.keyCode == KeyCode.Escape))
            {
                tv.ConfirmPokedexOrder();
                Event.current.Use();
            }
        }

        /// <summary>
        /// F5: Muestra la Inspección de Carta con la fuente nativa del juego e iconos circulares de tipos.
        /// En mesa 3D o constructor 3D la carta sube físicamente hacia la cámara con giro interactivo al mover el ratón,
        /// y a la derecha se muestra el panel de información (ataques, costes con iconos de energía, debilidad, resistencia y retirada).
        /// </summary>
    }
}
