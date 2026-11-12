// TCG-PlayKyn: Elevated card inspection overlay, stats display and power activation buttons
using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

namespace TCGPlayKyn
{
    /// <summary>
    /// Submódulo de inspección visual de cartas (Zoom elevado al hacer clic derecho o mantener cursor).
    /// 
    /// ¿Para qué sirve?
    /// - Eleva la carta hacia la cámara y dibuja a la derecha el panel detallado con estadísticas:
    ///   PS restantes / totales, tipo, debilidad, resistencia, coste de retirada y ataques completos.
    /// - Incluye los iconos circulares de energía nativos del juego.
    /// - Muestra la advertencia visual en rojo para cartas de expansiones modernas sin mecánicas
    ///   compatibles aún ('Not implemented expansion mechanics yet').
    /// 
    /// Modificaciones habituales:
    /// - Si necesitas cambiar el tamaño del panel informativo o los textos de ayuda, modifícalo aquí.
    /// </summary>
    public partial class GameUI
    {
public static bool DrawElevatedZoomOverlay(CardDef card, PokemonInPlay pip, Rect originRect, float animT, bool force3DCard = false, Action<Rect> nativeFooterDrawer = null)
        {
            EnsureStyles();
            var ui = Instance;
            if (ui == null || card == null) return false;

            float smooth = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(animT));
            bool use3DCard = force3DCard || (ui.open && TableView.Instance != null && TableView.Instance.IsBoard3DReady);

            float targetH = Mathf.Min(Screen.height * 0.76f, 590f);
            float targetW = Mathf.Round(targetH * (63f / 88f));
            float totalW = targetW + 490f;
            Rect targetCardRect = new Rect((Screen.width - totalW) * 0.5f, (Screen.height - targetH) * 0.5f, targetW, targetH);

            if (!use3DCard)
            {
                ui.DrawNativeBox(new Rect(0, 0, Screen.width, Screen.height), sDarkBackdropSprite, new Color(0.02f, 0.03f, 0.06f, 0.78f * smooth), false);
                Rect animCardRect = new Rect(
                    Mathf.Lerp(originRect.x, targetCardRect.x, smooth),
                    Mathf.Lerp(originRect.y, targetCardRect.y, smooth),
                    Mathf.Lerp(originRect.width, targetCardRect.width, smooth),
                    Mathf.Lerp(originRect.height, targetCardRect.height, smooth)
                );
                var sp = SpriteCache.Get(card.Sprite);
                if (sp != null)
                    ui.DrawNativeSprite(animCardRect, sp, Color.white);
            }

            if (smooth > 0.20f)
            {
                float panelAlpha = Mathf.Clamp01((smooth - 0.20f) / 0.80f);
                Rect infoBox = new Rect(targetCardRect.xMax + 24f, targetCardRect.y, 468f, targetH);
                ui.DrawNativeBox(infoBox, sPanelSprite, new Color(1f, 1f, 1f, panelAlpha));

                float ix = infoBox.x + 18f;
                float iw = infoBox.width - 36f;
                float curY = infoBox.y + 14f;

                // Cabecera: Nombre, Set/Número, PS e Icono de Tipo
                ui.DrawNativeLabel(
                    new Rect(ix, curY, iw - 140f, 32f),
                    $"{card.Name} <color=#9ecbff><size=14>({card.SetId.ToUpperInvariant()} #{card.Number})</size></color>",
                    22f,
                    TextAlignmentOptions.Left,
                    new Color(1f, 1f, 1f, panelAlpha),
                    autoSize: true,
                    minFontSize: 13f);

                if (card.Supertype == Supertype.Pokemon)
                {
                    int curHp = pip != null ? pip.RemainingHp : card.Hp;
                    ui.DrawNativeLabel(
                        new Rect(infoBox.xMax - 154f, curY, 102f, 30f),
                        $"<color=#ff7b7b>{curHp}/{card.Hp} HP</color>",
                        18f,
                        TextAlignmentOptions.Right,
                        new Color(1f, 1f, 1f, panelAlpha),
                        autoSize: true);
                    var typeTex = SpriteCache.GetTypeIcon(card.Type);
                    if (typeTex != null)
                        ui.DrawNativeRawTex(new Rect(infoBox.xMax - 46f, curY + 2f, 28f, 28f), typeTex, new Color(1f, 1f, 1f, panelAlpha));
                }
                else if (card.Supertype == Supertype.Energy && !string.IsNullOrEmpty(card.EnergyProvides))
                {
                    var typeTex = SpriteCache.GetTypeIcon(card.EnergyProvides);
                    if (typeTex != null)
                        ui.DrawNativeRawTex(new Rect(infoBox.xMax - 46f, curY + 2f, 28f, 28f), typeTex, new Color(1f, 1f, 1f, panelAlpha));
                }

                curY += 34f;

                if (!card.IsPlayable || !string.IsNullOrEmpty(card.UnsupportedReason))
                {
                    string reason = !string.IsNullOrEmpty(card.UnsupportedReason)
                        ? card.UnsupportedReason
                        : "Not implemented expansion mechanics yet";
                    ui.DrawNativeLabel(
                        new Rect(ix, curY, iw, 22f),
                        $"<color=#ff6b6b>⚠ {reason}</color>",
                        13.5f,
                        TextAlignmentOptions.Left,
                        new Color(1f, 0.42f, 0.42f, panelAlpha),
                        autoSize: true);
                    curY += 24f;
                }

                if (card.Supertype == Supertype.Pokemon)
                {
                    string stageTxt = $"Stage: <color=#ffe066>{card.Stage}</color>" + (!string.IsNullOrEmpty(card.EvolvesFrom) ? $"   •   Evolves from {card.EvolvesFrom}" : "");
                    ui.DrawNativeLabel(new Rect(ix, curY, iw, 22f), stageTxt, 14.5f, TextAlignmentOptions.Left, new Color(0.92f, 0.96f, 1f, panelAlpha), autoSize: true);
                    curY += 24f;

                    float hpRatio = Mathf.Clamp01((pip != null ? pip.RemainingHp : card.Hp) / (float)Math.Max(1, card.Hp));
                    ui.DrawNativeRawTex(new Rect(ix, curY, iw, 8f), hpBgTex, new Color(1f, 1f, 1f, panelAlpha));
                    ui.DrawNativeRawTex(new Rect(ix, curY, iw * hpRatio, 8f), hpFillTex, new Color(1f, 1f, 1f, panelAlpha));
                    curY += 14f;

                    if (pip != null && pip.Energy.Count > 0)
                    {
                        ui.DrawNativeLabel(new Rect(ix, curY, 134f, 22f), "Attached Energy:", 14f, TextAlignmentOptions.Left, new Color(0.92f, 0.96f, 1f, panelAlpha));
                        float ex = ix + 136f;
                        foreach (var e in pip.Energy)
                        {
                            var eTex = SpriteCache.GetTypeIcon(e.EnergyProvides);
                            if (eTex != null)
                            {
                                ui.DrawNativeRawTex(new Rect(ex, curY + 1f, 20f, 20f), eTex, new Color(1f, 1f, 1f, panelAlpha));
                                ex += 23f;
                            }
                        }
                        curY += 24f;
                    }

                    var powerDef = card.Power ?? card.Abilities.FirstOrDefault();
                    if (powerDef != null)
                    {
                        float pBoxH = 74f;
                        Rect pBox = new Rect(ix, curY, iw, pBoxH);
                        ui.DrawNativeBox(pBox, sSubPanelSprite, new Color(1f, 1f, 1f, panelAlpha));
                        string powerStatus = (pip != null && ui.game != null && ui.game.IsPowerDisabled(pip)) ? " <color=#ff6b6b>(Disabled)</color>" : "";

                        bool isAct = ui.game != null && ui.game.IsActivatedPower(powerDef.Name);
                        float btnW = isAct ? 122f : 0f;
                        float titleW = iw - 20f - (isAct ? btnW + 6f : 0f);

                        ui.DrawNativeLabel(new Rect(ix + 10f, curY + 4f, titleW, 22f), $"<color=#ffd166>Poké-Power: {powerDef.Name}</color>{powerStatus}", 15f, TextAlignmentOptions.Left, Color.white, autoSize: true);

                        // Botón de activación al lado del título con el nombre en inglés usando la fuente nativa del juego
                        if (isAct)
                        {
                            Rect btnRect = new Rect(ix + iw - btnW - 8f, curY + 4f, btnW, 24f);
                            bool isTurn = pip != null && ui.game != null && ui.game.Phase == Phase.Main && ui.game.Current == ui.Me && ui.Me.InPlay.Contains(pip);
                            bool canUse = isTurn && !ui.game.IsPowerDisabled(pip) && !ui.game.IsBusyResolving && !pip.PowersUsedThisTurn.Contains(powerDef.Name);

                            Sprite bN = canUse ? sBtnGoldN : sBtnBlueN;
                            Sprite bH = canUse ? sBtnGoldH : sBtnBlueH;
                            Sprite bA = canUse ? sBtnGoldA : sBtnBlueA;

                            if (ui.DrawNativeButton(btnRect, powerDef.Name, 13f, bN, bH, bA))
                            {
                                if (canUse)
                                {
                                    PlayClickSound(1);
                                    ui.Report(ui.game.UsePokemonPower(ui.Me, pip));
                                }
                                else if (pip != null)
                                {
                                    PlayClickSound(0);
                                    string why = !isTurn ? "Solo puedes usar habilidades en tu turno."
                                        : (pip.PowersUsedThisTurn.Contains(powerDef.Name) ? "Ya usaste esta habilidad este turno."
                                        : "Habilidad bloqueada o no disponible.");
                                    ui.Report(why);
                                }
                            }
                        }

                        ui.DrawNativeLabel(new Rect(ix + 10f, curY + 28f, iw - 20f, pBoxH - 32f), powerDef.Text ?? "", 12f, TextAlignmentOptions.TopLeft, new Color(0.90f, 0.95f, 1f, panelAlpha), autoSize: true, minFontSize: 9.5f);
                        curY += pBoxH + 6f;
                    }

                    if (card.Attacks != null)
                    {
                        foreach (var atk in card.Attacks)
                        {
                            bool hasTxt = !string.IsNullOrEmpty(atk.Text);
                            float aBoxH = hasTxt ? 72f : 36f;
                            Rect aBox = new Rect(ix, curY, iw, aBoxH);
                            ui.DrawNativeBox(aBox, sSubPanelSprite, new Color(1f, 1f, 1f, panelAlpha));

                            float cx = ix + 10f;
                            if (atk.Cost != null && atk.Cost.Count > 0)
                            {
                                foreach (var cType in atk.Cost)
                                {
                                    var cTex = SpriteCache.GetTypeIcon(cType);
                                    if (cTex != null)
                                    {
                                        ui.DrawNativeRawTex(new Rect(cx, curY + 7f, 20f, 20f), cTex, new Color(1f, 1f, 1f, panelAlpha));
                                        cx += 22f;
                                    }
                                }
                            }
                            else
                            {
                                ui.DrawNativeLabel(new Rect(cx, curY + 5f, 26f, 22f), "[0]", 13f, TextAlignmentOptions.Left, new Color(0.65f, 0.85f, 1f, panelAlpha));
                                cx += 28f;
                            }

                            ui.DrawNativeLabel(new Rect(cx + 4f, curY + 5f, iw - (cx - ix) - 74f, 24f), $"<color=#80ed99>{atk.Name}</color>", 16f, TextAlignmentOptions.Left, Color.white, autoSize: true);
                            if (atk.BaseDamage > 0 || !string.IsNullOrEmpty(atk.DamageSuffix))
                            {
                                ui.DrawNativeLabel(new Rect(ix + iw - 72f, curY + 5f, 62f, 24f), $"<color=#ffe066>{atk.BaseDamage}{atk.DamageSuffix}</color>", 17f, TextAlignmentOptions.Right, Color.white, autoSize: true);
                            }

                            if (hasTxt)
                            {
                                ui.DrawNativeLabel(new Rect(ix + 10f, curY + 30f, iw - 20f, aBoxH - 34f), atk.Text, 12.5f, TextAlignmentOptions.TopLeft, new Color(0.86f, 0.92f, 0.98f, panelAlpha), autoSize: true, minFontSize: 9.5f);
                            }

                            curY += aBoxH + 6f;
                        }
                    }

                    // Franja de Debilidad, Resistencia y Retirada
                    float statsY = nativeFooterDrawer != null ? (infoBox.yMax - 140f) : (infoBox.yMax - 114f);
                    Rect statsBox = new Rect(ix, statsY, iw, 34f);
                    ui.DrawNativeBox(statsBox, sSubPanelSprite, new Color(1f, 1f, 1f, panelAlpha));

                    // Weakness
                    ui.DrawNativeLabel(new Rect(ix + 8f, statsY + 5f, 76f, 24f), "Weakness:", 13f, TextAlignmentOptions.Left, Color.white, autoSize: true);
                    if (!string.IsNullOrEmpty(card.WeaknessType))
                    {
                        var wTex = SpriteCache.GetTypeIcon(card.WeaknessType);
                        if (wTex != null) ui.DrawNativeRawTex(new Rect(ix + 82f, statsY + 7f, 20f, 20f), wTex, Color.white);
                        ui.DrawNativeLabel(new Rect(ix + 105f, statsY + 5f, 30f, 24f), "x2", 13.5f, TextAlignmentOptions.Left, new Color(1f, 0.88f, 0.45f, 1f));
                    }
                    else
                    {
                        ui.DrawNativeLabel(new Rect(ix + 84f, statsY + 5f, 30f, 24f), "-", 13.5f, TextAlignmentOptions.Left, new Color(0.75f, 0.82f, 0.9f, 1f));
                    }

                    // Resistance
                    float resX = ix + 142f;
                    ui.DrawNativeLabel(new Rect(resX, statsY + 5f, 86f, 24f), "Resistance:", 13f, TextAlignmentOptions.Left, Color.white, autoSize: true);
                    if (!string.IsNullOrEmpty(card.ResistanceType))
                    {
                        var rTex = SpriteCache.GetTypeIcon(card.ResistanceType);
                        if (rTex != null) ui.DrawNativeRawTex(new Rect(resX + 86f, statsY + 7f, 20f, 20f), rTex, Color.white);
                        ui.DrawNativeLabel(new Rect(resX + 108f, statsY + 5f, 36f, 24f), $"-{card.ResistanceValue}", 13.5f, TextAlignmentOptions.Left, new Color(0.55f, 0.95f, 0.72f, 1f));
                    }
                    else
                    {
                        ui.DrawNativeLabel(new Rect(resX + 88f, statsY + 5f, 30f, 24f), "-", 13.5f, TextAlignmentOptions.Left, new Color(0.75f, 0.82f, 0.9f, 1f));
                    }

                    // Retreat
                    float retX = ix + 292f;
                    ui.DrawNativeLabel(new Rect(retX, statsY + 5f, 66f, 24f), "Retreat:", 13f, TextAlignmentOptions.Left, Color.white, autoSize: true);
                    if (card.RetreatCost > 0)
                    {
                        var colTex = SpriteCache.GetTypeIcon("Colorless");
                        for (int r = 0; r < card.RetreatCost; r++)
                        {
                            if (colTex != null)
                                ui.DrawNativeRawTex(new Rect(retX + 66f + r * 20f, statsY + 7f, 18f, 18f), colTex, Color.white);
                        }
                    }
                    else
                    {
                        ui.DrawNativeLabel(new Rect(retX + 68f, statsY + 5f, 24f, 24f), "0", 13.5f, TextAlignmentOptions.Left, new Color(0.75f, 0.82f, 0.9f, 1f));
                    }
                }
                else
                {
                    string catTxt = card.Supertype == Supertype.Trainer ? "Trainer Card" : $"Energy Card ({card.EnergyProvides})";
                    ui.DrawNativeLabel(new Rect(ix, curY, iw, 24f), catTxt, 16f, TextAlignmentOptions.Left, new Color(1f, 0.88f, 0.45f, panelAlpha));
                    curY += 30f;

                    if (!string.IsNullOrEmpty(card.RulesText))
                    {
                        Rect rBox = new Rect(ix, curY, iw, 180f);
                        ui.DrawNativeBox(rBox, sSubPanelSprite, new Color(1f, 1f, 1f, panelAlpha));
                        ui.DrawNativeLabel(new Rect(ix + 12f, curY + 10f, iw - 24f, 160f), card.RulesText, 14.5f, TextAlignmentOptions.TopLeft, new Color(0.90f, 0.95f, 1f, panelAlpha), autoSize: true, minFontSize: 11f);
                    }
                }

                if (nativeFooterDrawer != null)
                {
                    Rect footerRect = new Rect(ix, infoBox.yMax - 98f, iw, 68f);
                    nativeFooterDrawer.Invoke(footerRect);
                }
                else if (TableView.Instance != null && TableView.Instance.IsBoard3DReady)
                {
                    // En inspección de mesa 3D durante partida: botón de alternar Foil / No Foil
                    bool curInspectFoil = TableView.Instance.CurrentInspectFoil;
                    string foilLabel = curInspectFoil ? "No Foil" : "Foil";
                    bool canToggleFoil = curInspectFoil ? Collection.HasNonFoil(card) : Collection.HasFoil(card);

                    float btnW = 120f;
                    float btnH = 30f;
                    float btnX = ix + (iw - btnW) * 0.5f;
                    float btnY = infoBox.yMax - 70f;

                    var foilBtnN = curInspectFoil ? sBtnBlueN : sBtnGoldN;
                    var foilBtnH = curInspectFoil ? sBtnBlueH : sBtnGoldH;
                    var foilBtnA = curInspectFoil ? sBtnBlueA : sBtnGoldA;

                    if (canToggleFoil)
                    {
                        if (ui.DrawNativeButton(new Rect(btnX, btnY, btnW, btnH), foilLabel, 13f, foilBtnN, foilBtnH, foilBtnA))
                        {
                            if (!TableView.Instance.IsInspectFoilSpinning)
                            {
                                PlayClickSound(0);
                                TableView.Instance.TriggerFoilFlip(() =>
                                {
                                    TableView.Instance.SetInspectFoil(!curInspectFoil);
                                });
                            }
                        }
                    }
                    else
                    {
                        ui.DrawNativeBox(new Rect(btnX, btnY, btnW, btnH), sBtnBlueN, new Color(0.18f, 0.22f, 0.32f, 0.45f));
                        ui.DrawNativeLabel(new Rect(btnX, btnY + 3f, btnW, btnH - 6f), foilLabel, 12f, TextAlignmentOptions.Center, new Color(0.6f, 0.65f, 0.75f, 0.6f));
                    }
                }

                ui.DrawNativeLabel(
                    new Rect(ix, infoBox.yMax - 26f, iw, 20f),
                    "Move mouse to tilt 3D card  •  Click outside or press Esc to close",
                    12.5f,
                    TextAlignmentOptions.Center,
                    new Color(0.72f, 0.80f, 0.90f, panelAlpha),
                    autoSize: true);

                // Si el clic fue dentro de infoBox y no lo consumió un botón, evitar cerrar el zoom accidentalmente
                if (Event.current != null && Event.current.type == EventType.MouseDown && infoBox.Contains(Event.current.mousePosition))
                {
                    Event.current.Use();
                    return false;
                }
            }

            if (Event.current != null && Event.current.type == EventType.MouseDown)
            {
                Event.current.Use();
                return true;
            }
            return false;
        }

        // ==================== Helpers de Dibujo de Cartas ====================
        public static void DrawCardSpriteOnly(Rect rect, CardDef c)
        {
            if (c == null) return;
            var sp = SpriteCache.Get(c.Sprite);
            if (sp != null && sp.texture != null)
            {
                var t = sp.texture;
                var tr = sp.textureRect;
                GUI.DrawTextureWithTexCoords(rect, t, new Rect(tr.x / t.width, tr.y / t.height, tr.width / t.width, tr.height / t.height));
            }
            else
            {
                GUI.Box(rect, c.Name);
            }
        }

        public static void DrawCardBackRect(Rect rect)
        {
            var cb = SpriteCache.GetCardBack();
            if (cb != null && cb.texture != null)
            {
                var t = cb.texture;
                var tr = cb.textureRect;
                GUI.DrawTextureWithTexCoords(rect, t, new Rect(tr.x / t.width, tr.y / t.height, tr.width / t.width, tr.height / t.height));
            }
            else
            {
                GUI.Box(rect, "POKÉMON");
            }
        }

        /// <summary>Dibuja una carta y devuelve 0 (clic izquierdo), 1 (clic derecho / zoom) o -1.</summary>
        public static int DrawCardWithInput(Rect rect, CardDef c, bool highlight, bool allowInput)
        {
            EnsureStyles();
            if (highlight)
                GUI.DrawTexture(new Rect(rect.x - 4f, rect.y - 4f, rect.width + 8f, rect.height + 8f), highlightTex);

            DrawCardSpriteOnly(rect, c);

            if (allowInput && Event.current.type == EventType.MouseDown && rect.Contains(Event.current.mousePosition))
            {
                int b = Event.current.button;
                Event.current.Use();
                return b;
            }
            return -1;
        }

        // ==================== F4: Interacciones 3D (Mano en Abanico, Hover, Drag & Drop y Raycast 3D) ====================
        bool IsMouseOverHud(Vector2 guiMouse)
        {
            // Botones laterales izquierdos ("Opciones" y "Levantarse")
            Rect leftBtns = new Rect(20f, 98f, 184f, 114f);
            if (leftBtns.Contains(guiMouse)) return true;

            bool hasRightPanel = game != null && (
                                     (game.Phase == Phase.ChoosePromotion && game.PromotionPending == Me) ||
                                     (game.Phase == Phase.Main && game.Current == Me && Me.Active != null && !IsAnimBusy()));
            if (hasRightPanel)
            {
                float panelW = 248f;
                float panelH = 340f;
                Rect rightPanel = new Rect(Screen.width - panelW - 14f, 46f, panelW, panelH);
                if (rightPanel.Contains(guiMouse)) return true;
            }

            return false;
        }
    }
}
