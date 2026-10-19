using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace PokeTCGPlay
{
    /// <summary>
    /// Interfaz del tablero de Pokémon TCG centrada y a gran escala, activada al sentarse en una mesa con un NPC.
    /// - Clic izquierdo: seleccionar / jugar cartas.
    /// - Clic derecho sobre cualquier carta: hacer zoom de lectura.
    /// </summary>
    public class GameUI : MonoBehaviour
    {
        public static GameUI Instance { get; private set; }

        bool open;
        Game game;
        string message = "";
        string toastMessage = "";
        float toastTimer;
        int selHand = -1;
        bool retreatMode;
        CardDef zoomedCard;
        PokemonInPlay zoomedPip;
        Vector2 logScroll, handScroll;

        PlayerState Me => game.Players[0];
        PlayerState Cpu => game.Players[1];

        void Awake()
        {
            Instance = this;
        }

        public void ShowToast(string text, float seconds = 3.5f)
        {
            toastMessage = text;
            toastTimer = seconds;
        }

        public void StartTableMatch(Customer opponent)
        {
            try
            {
                SpriteCache.EnsureInitialized();
                SpriteCache.PreloadFromEpl();
                string dir = Path.Combine(Plugin.DataDir, "decks");
                var a = DeckLoader.Load(Path.Combine(dir, "player.txt"));
                var b = DeckLoader.Load(Path.Combine(dir, "cpu.txt"));
                game = new Game(a, b);
                game.Players[1].Name = "Cliente";
                selHand = -1;
                retreatMode = false;
                zoomedCard = null;
                zoomedPip = null;
                open = true;
                message = "Preparación: haz clic izquierdo en Básicos de tu mano (el primero será el Activo) y pulsa '¡Listo para jugar!'. Clic derecho en cualquier carta para hacer Zoom.";
            }
            catch (Exception e)
            {
                message = e.Message;
                Plugin.Log.LogError(e);
            }
        }

        public void CloseFromTableStop()
        {
            open = false;
            game = null;
            zoomedCard = null;
            zoomedPip = null;
        }

        void LeaveTable()
        {
            bool finished = game != null && game.Phase == Phase.GameOver;
            bool won = finished && game.Winner == Me;
            open = false;
            game = null;
            zoomedCard = null;
            zoomedPip = null;
            PlayTableSession.StandUp(finished, won);
        }

        void Update()
        {
            if (toastTimer > 0f) toastTimer -= Time.deltaTime;

            if (PlayTableSession.IsWaitingForNpc)
            {
                PlayTableSession.UpdateWaiting();
                InputManager.SetCursorLockMode(CursorLockMode.None);
                Cursor.visible = true;
                if (Input.GetKeyDown(KeyCode.Escape) || Input.GetMouseButtonDown(1))
                {
                    LeaveTable();
                }
                return;
            }

            if (open)
            {
                InputManager.SetCursorLockMode(CursorLockMode.None);
                Cursor.visible = true;
                if (Input.GetKeyDown(KeyCode.Escape))
                {
                    if (zoomedCard != null) { zoomedCard = null; zoomedPip = null; }
                }
            }
        }

        static GUIStyle boxBgStyle, headerStyle, subHeaderStyle, cardLabelStyle, zoomTextStyle, btnStyle, toastStyle;
        static Texture2D darkTex, panelTex, highlightTex, zoomBgTex;

        static void EnsureStyles()
        {
            if (darkTex == null)
            {
                darkTex = MakeTex(new Color(0.08f, 0.10f, 0.14f, 0.96f));
                panelTex = MakeTex(new Color(0.14f, 0.17f, 0.23f, 0.95f));
                highlightTex = MakeTex(new Color(0.95f, 0.78f, 0.18f, 0.95f));
                zoomBgTex = MakeTex(new Color(0.03f, 0.04f, 0.06f, 0.94f));
            }
            if (boxBgStyle == null)
            {
                boxBgStyle = new GUIStyle(GUI.skin.box) { normal = { background = darkTex } };
                headerStyle = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 18, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
                subHeaderStyle = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 14, normal = { textColor = new Color(0.85f, 0.9f, 1f) } };
                cardLabelStyle = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 12, alignment = TextAnchor.UpperCenter, wordWrap = true, normal = { textColor = Color.white } };
                zoomTextStyle = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 16, wordWrap = true, normal = { textColor = Color.white } };
                btnStyle = new GUIStyle(GUI.skin.button) { fontSize = 14, fontStyle = FontStyle.Bold };
                toastStyle = new GUIStyle(GUI.skin.box) { fontSize = 18, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white, background = darkTex } };
            }
        }

        static Texture2D MakeTex(Color col)
        {
            var t = new Texture2D(2, 2);
            t.SetPixels(new[] { col, col, col, col });
            t.Apply();
            return t;
        }

        void OnGUI()
        {
            EnsureStyles();
            GUI.depth = -1000;

            if (toastTimer > 0f && !string.IsNullOrEmpty(toastMessage))
            {
                GUI.Box(new Rect((Screen.width - 520) * 0.5f, 40, 520, 50), toastMessage, toastStyle);
            }

            // Pantalla de espera mientras estamos sentados en la mesa esperando a un NPC
            if (PlayTableSession.IsWaitingForNpc)
            {
                float ww = 680f, wh = 130f;
                Rect wr = new Rect((Screen.width - ww) * 0.5f, (Screen.height - wh) * 0.5f, ww, wh);
                GUI.Box(wr, "", boxBgStyle);
                GUILayout.BeginArea(new Rect(wr.x + 20, wr.y + 18, wr.width - 40, wr.height - 36));
                GUILayout.Label("<size=20><b>Sentado en la mesa de juego</b></size>", headerStyle);
                GUILayout.Space(6);
                GUILayout.Label("Esperando a que un cliente se acerque y tome asiento frente a ti...", subHeaderStyle);
                GUILayout.Space(12);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Levantarse de la mesa (Clic derecho / Esc)", btnStyle, GUILayout.Height(34), GUILayout.Width(340)))
                    LeaveTable();
                GUILayout.EndHorizontal();
                GUILayout.EndArea();
                return;
            }

            if (!open || game == null) return;

            // Dimensiones centradas del tablero principal (94% de la pantalla, centrado)
            float bw = Mathf.Min(Screen.width - 40f, 1760f);
            float bh = Mathf.Min(Screen.height - 30f, 1020f);
            Rect boardRect = new Rect((Screen.width - bw) * 0.5f, (Screen.height - bh) * 0.5f, bw, bh);
            GUI.Box(boardRect, "", boxBgStyle);

            // Tamaño de carta dinámico según la altura del tablero para que se vean grandes y quepan las 3 filas
            float cardH = Mathf.Clamp((bh - 250f) / 3.05f, 150f, 235f);
            float cardW = Mathf.Round(cardH * (63f / 88f));

            GUILayout.BeginArea(new Rect(boardRect.x + 18, boardRect.y + 14, boardRect.width - 36, boardRect.height - 28));

            // Barra superior
            GUILayout.BeginHorizontal();
            GUILayout.Label($"<size=20><b>Pokémon TCG — Mesa de Juego</b></size>   <color=#9ecbff>Turno {game.Turn} ({(game.Phase == Phase.Setup ? "Preparación" : game.Current.Name)})</color>", headerStyle);
            GUILayout.FlexibleSpace();
            GUILayout.Label($"Cartas MyCurio: {SpriteCache.Count}/102   |   <b>Clic Izq:</b> Seleccionar/Jugar   <b>Clic Der:</b> Zoom carta", subHeaderStyle);
            GUILayout.Space(16);
            if (GUILayout.Button("Levantarse de la mesa", btnStyle, GUILayout.Width(190), GUILayout.Height(30)))
            {
                LeaveTable();
                GUILayout.EndHorizontal();
                GUILayout.EndArea();
                return;
            }
            GUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(message))
                GUILayout.Label($"<color=#ffe066>{message}</color>", subHeaderStyle);

            GUILayout.Space(4);

            GUILayout.BeginHorizontal();

            // --- Zona central del tablero (centrada) ---
            float logWidth = 340f;
            float playAreaWidth = boardRect.width - 36f - logWidth - 16f;
            GUILayout.BeginVertical(GUILayout.Width(playAreaWidth));

            // Estadísticas del Rival (centradas)
            DrawCenteredHeader($"<b>RIVAL ({Cpu.Name})</b>   |   Mano: <b>{Cpu.Hand.Count}</b>   |   Mazo: <b>{Cpu.Deck.Count}</b>   |   Premios restantes: <b>{Cpu.Prizes.Count}</b>   |   Descarte: <b>{Cpu.Discard.Count}</b>");
            DrawRowCentered(Cpu, cardW, cardH);

            GUILayout.Space(8);

            // Estadísticas y fila del Jugador (centradas)
            DrawRowCentered(Me, cardW, cardH);
            DrawCenteredHeader($"<b>TÚ</b>   |   Mazo: <b>{Me.Deck.Count}</b>   |   Premios restantes: <b>{Me.Prizes.Count}</b>   |   Descarte: <b>{Me.Discard.Count}</b>   |   Energía unida este turno: <b>{(Me.EnergyPlayedThisTurn ? "Sí" : "No")}</b>");

            // Botones de acción centrados
            DrawActionsCentered();

            // Mano del jugador centrada
            DrawHandCentered(playAreaWidth, cardW, cardH);

            GUILayout.EndVertical();

            GUILayout.Space(12);

            // --- Panel lateral de Registro y estado ---
            GUILayout.BeginVertical(GUI.skin.box, GUILayout.Width(logWidth), GUILayout.ExpandHeight(true));
            if (game.Phase == Phase.GameOver)
            {
                GUILayout.Label($"<size=20><color=#66ff99><b>¡GANA {game.Winner.Name.ToUpper()}!</b></color></size>\n{game.WinReason}", headerStyle);
                GUILayout.Space(8);
                if (GUILayout.Button("Revancha contra este cliente", btnStyle, GUILayout.Height(34)))
                    StartTableMatch(PlayTableSession.CurrentOpponent);
                if (GUILayout.Button("Terminar y levantarse", btnStyle, GUILayout.Height(34)))
                {
                    LeaveTable();
                    GUILayout.EndVertical();
                    GUILayout.EndHorizontal();
                    GUILayout.EndArea();
                    return;
                }
                GUILayout.Space(10);
            }
            GUILayout.Label("<b>Registro de la partida</b>", subHeaderStyle);
            logScroll = GUILayout.BeginScrollView(logScroll, GUILayout.ExpandHeight(true));
            GUILayout.Label(string.Join("\n", game.Log.Skip(Math.Max(0, game.Log.Count - 80))), subHeaderStyle);
            GUILayout.EndScrollView();
            GUILayout.EndVertical();

            GUILayout.EndHorizontal();
            GUILayout.EndArea();

            // --- Superposición de Zoom con Clic Derecho ---
            if (zoomedCard != null)
            {
                DrawZoomModal();
            }
        }

        void DrawCenteredHeader(string text)
        {
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.Label(text, subHeaderStyle);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        void DrawRowCentered(PlayerState p, float cw, float ch)
        {
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            // Pokémon Activo destacado
            DrawPokemonSlot(p, p.Active, true, cw, ch);

            GUILayout.Space(28);

            // 5 huecos de Banca
            for (int i = 0; i < 5; i++)
            {
                var b = i < p.Bench.Count ? p.Bench[i] : null;
                DrawPokemonSlot(p, b, false, cw, ch);
                if (i < 4) GUILayout.Space(10);
            }

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        void DrawPokemonSlot(PlayerState owner, PokemonInPlay pip, bool active, float cw, float ch)
        {
            GUILayout.BeginVertical(GUILayout.Width(cw + 8));
            if (pip == null)
            {
                GUILayout.Box(active ? "ACTIVO\n(vacío)" : "BANCA", GUILayout.Width(cw), GUILayout.Height(ch));
                GUILayout.Label(" ", cardLabelStyle, GUILayout.Width(cw), GUILayout.Height(36));
                GUILayout.EndVertical();
                return;
            }

            var rect = GUILayoutUtility.GetRect(cw, ch);
            int btn = DrawCardInteractive(rect, pip.Card, active);
            if (btn == 0) OnPokemonLeftClicked(owner, pip);
            else if (btn == 1) { zoomedCard = pip.Card; zoomedPip = pip; }

            string energies = pip.Energy.Count > 0
                ? string.Join(",", pip.Energy.Select(e => e.EnergyProvides.Substring(0, Math.Min(2, e.EnergyProvides.Length))))
                : "-";
            string status = (pip.Status != Status.None ? $" <color=#ffd166>[{pip.Status}]</color>" : "") +
                            (pip.Poisoned ? " <color=#c77dff>[Veneno]</color>" : "");
            GUILayout.Label($"<b>{pip.RemainingHp}/{pip.Card.Hp} HP</b>{status}\nE: {energies}", cardLabelStyle, GUILayout.Width(cw), GUILayout.Height(36));
            GUILayout.EndVertical();
        }

        /// <summary>
        /// Dibuja una carta y devuelve: 0 si se hizo clic izquierdo, 1 si se hizo clic derecho, -1 si no se pulsó.
        /// </summary>
        int DrawCardInteractive(Rect rect, CardDef c, bool highlight = false)
        {
            if (highlight)
            {
                GUI.DrawTexture(new Rect(rect.x - 4, rect.y - 4, rect.width + 8, rect.height + 8), highlightTex);
            }

            var sp = SpriteCache.Get(c.Sprite);
            if (sp != null && sp.texture != null)
            {
                var t = sp.texture;
                var tr = sp.textureRect;
                GUI.DrawTextureWithTexCoords(rect, t, new Rect(tr.x / t.width, tr.y / t.height, tr.width / t.width, tr.height / t.height));
            }
            else
            {
                string txt = c.Name + "\n" + (c.Supertype == Supertype.Pokemon ? $"{c.Type} HP {c.Hp}\n{c.Stage}" : c.Supertype.ToString());
                GUI.Box(rect, txt);
            }

            Event e = Event.current;
            if (e.type == EventType.MouseDown && rect.Contains(e.mousePosition) && zoomedCard == null)
            {
                int b = e.button;
                e.Use();
                return b;
            }
            return -1;
        }

        void DrawHandCentered(float areaWidth, float cw, float ch)
        {
            handScroll = GUILayout.BeginScrollView(handScroll, GUILayout.Height(ch + 28));
            GUILayout.BeginHorizontal();
            float totalCardsW = Me.Hand.Count * (cw + 10f);
            if (totalCardsW < areaWidth) GUILayout.FlexibleSpace();

            for (int i = 0; i < Me.Hand.Count; i++)
            {
                var rect = GUILayoutUtility.GetRect(cw, ch, GUILayout.Width(cw));
                int btn = DrawCardInteractive(rect, Me.Hand[i], i == selHand);
                if (btn == 0) OnHandLeftClicked(i);
                else if (btn == 1) { zoomedCard = Me.Hand[i]; zoomedPip = null; }
                GUILayout.Space(8);
            }

            if (totalCardsW < areaWidth) GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.EndScrollView();
        }

        void DrawActionsCentered()
        {
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (game.Phase == Phase.Setup)
            {
                if (GUILayout.Button("¡Listo para jugar!", btnStyle, GUILayout.Width(200), GUILayout.Height(34)))
                    Report(game.FinishSetup(Me));
            }
            else if (game.Phase == Phase.ChoosePromotion && game.PromotionPending == Me)
            {
                GUILayout.Label("<size=16><color=#ffd166><b>Tu Pokémon Activo cayó: haz clic izquierdo en un Pokémon de tu Banca para promoverlo.</b></color></size>", headerStyle);
            }
            else if (game.Phase == Phase.Main && game.Current == Me && Me.Active != null)
            {
                var a = Me.Active;
                for (int i = 0; i < a.Card.Attacks.Count; i++)
                {
                    var atk = a.Card.Attacks[i];
                    GUI.enabled = a.CanPay(atk.Cost);
                    if (GUILayout.Button($"Atacar: {atk.Name} [{string.Join("", atk.Cost.Select(c => c[0]))}] ({atk.BaseDamage}{atk.DamageSuffix})", btnStyle, GUILayout.Width(250), GUILayout.Height(34)))
                        Report(game.Attack(Me, i));
                    GUI.enabled = true;
                    GUILayout.Space(6);
                }
                if (GUILayout.Button(retreatMode ? "Cancelar retirada" : $"Retirarse (Coste: {a.Card.RetreatCost})", btnStyle, GUILayout.Width(180), GUILayout.Height(34)))
                    retreatMode = !retreatMode;
                GUILayout.Space(6);

                if (selHand >= 0 && selHand < Me.Hand.Count)
                {
                    var c = Me.Hand[selHand];
                    if (c.IsBasicPokemon && GUILayout.Button("Bajar a la Banca", btnStyle, GUILayout.Width(150), GUILayout.Height(34)))
                        Report(game.PlayBasic(Me, selHand));
                    if (c.Supertype == Supertype.Trainer && GUILayout.Button("Usar Entrenador", btnStyle, GUILayout.Width(160), GUILayout.Height(34)))
                        Report(game.PlayTrainer(Me, selHand, null));
                }
                GUILayout.Space(6);
                if (GUILayout.Button("Terminar turno", btnStyle, GUILayout.Width(150), GUILayout.Height(34)))
                    Report(game.EndTurn());
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            if (selHand >= 0 && selHand < Me.Hand.Count)
            {
                var c = Me.Hand[selHand];
                DrawCenteredHeader($"<color=#ffe066>Carta seleccionada: <b>{c.Name}</b> — {HintFor(c)}</color>");
            }
        }

        void DrawZoomModal()
        {
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), zoomBgTex);

            float zw = 920f, zh = 620f;
            Rect zr = new Rect((Screen.width - zw) * 0.5f, (Screen.height - zh) * 0.5f, zw, zh);
            GUI.Box(zr, "", boxBgStyle);

            float imgH = zh - 50f;
            float imgW = Mathf.Round(imgH * (63f / 88f));
            Rect imgRect = new Rect(zr.x + 25f, zr.y + 25f, imgW, imgH);

            var sp = SpriteCache.Get(zoomedCard.Sprite);
            if (sp != null && sp.texture != null)
            {
                var t = sp.texture;
                var tr = sp.textureRect;
                GUI.DrawTextureWithTexCoords(imgRect, t, new Rect(tr.x / t.width, tr.y / t.height, tr.width / t.width, tr.height / t.height));
            }
            else
            {
                GUI.Box(imgRect, zoomedCard.Name);
            }

            Rect infoRect = new Rect(imgRect.xMax + 28f, zr.y + 25f, zr.width - imgW - 78f, imgH);
            GUILayout.BeginArea(infoRect);
            GUILayout.Label($"<size=26><b>{zoomedCard.Name}</b></size>  <color=#9ecbff>(#{zoomedCard.Number})</color>", headerStyle);
            GUILayout.Space(8);

            if (zoomedCard.Supertype == Supertype.Pokemon)
            {
                GUILayout.Label($"<b>Etapa:</b> {zoomedCard.Stage}" + (!string.IsNullOrEmpty(zoomedCard.EvolvesFrom) ? $" (Evoluciona de {zoomedCard.EvolvesFrom})" : ""), zoomTextStyle);
                GUILayout.Label($"<b>Tipo:</b> {zoomedCard.Type}   |   <b>HP:</b> {(zoomedPip != null ? $"{zoomedPip.RemainingHp}/{zoomedCard.Hp}" : zoomedCard.Hp.ToString())}", zoomTextStyle);

                if (zoomedPip != null && zoomedPip.Energy.Count > 0)
                    GUILayout.Label($"<b>Energías unidas:</b> {string.Join(", ", zoomedPip.Energy.Select(e => e.Name))}", zoomTextStyle);

                if (zoomedCard.Power != null)
                {
                    GUILayout.Space(10);
                    GUILayout.Label($"<color=#ffd166><b>Pokémon Power — {zoomedCard.Power.Name}:</b></color>\n{zoomedCard.Power.Text}", zoomTextStyle);
                }

                GUILayout.Space(10);
                foreach (var atk in zoomedCard.Attacks)
                {
                    string cost = atk.Cost.Count > 0 ? string.Join(", ", atk.Cost) : "Gratis";
                    GUILayout.Label($"<color=#80ed99><b>• {atk.Name}</b></color>   (Coste: {cost})   <b>Daño: {atk.BaseDamage}{atk.DamageSuffix}</b>", zoomTextStyle);
                    if (!string.IsNullOrEmpty(atk.Text))
                        GUILayout.Label($"   <i>{atk.Text}</i>", zoomTextStyle);
                    GUILayout.Space(6);
                }

                GUILayout.Space(8);
                GUILayout.Label($"<b>Debilidad:</b> {(string.IsNullOrEmpty(zoomedCard.WeaknessType) ? "Ninguna" : zoomedCard.WeaknessType + " ×2")}   |   " +
                                $"<b>Resistencia:</b> {(string.IsNullOrEmpty(zoomedCard.ResistanceType) ? "Ninguna" : $"{zoomedCard.ResistanceType} -{zoomedCard.ResistanceValue}")}   |   " +
                                $"<b>Retirada:</b> {zoomedCard.RetreatCost}", zoomTextStyle);
            }
            else
            {
                GUILayout.Label($"<b>Categoría:</b> {zoomedCard.Supertype}", zoomTextStyle);
                GUILayout.Space(10);
                if (!string.IsNullOrEmpty(zoomedCard.RulesText))
                    GUILayout.Label(zoomedCard.RulesText, zoomTextStyle);
            }

            GUILayout.FlexibleSpace();
            GUILayout.Label("<color=#aaaaaa>Haz clic derecho o clic izquierdo en cualquier lugar para cerrar el zoom.</color>", subHeaderStyle);
            GUILayout.EndArea();

            if (Event.current.type == EventType.MouseDown)
            {
                zoomedCard = null;
                zoomedPip = null;
                Event.current.Use();
            }
        }

        string HintFor(CardDef c)
        {
            if (c.Supertype == Supertype.Energy) return "Haz clic izquierdo en uno de tus Pokémon para unirla.";
            if (c.Supertype == Supertype.Pokemon && c.Stage != "Basic") return $"Haz clic izquierdo en tu {c.EvolvesFrom} en juego para evolucionarlo.";
            if (c.Supertype == Supertype.Trainer) return "Haz clic izquierdo en el Pokémon objetivo o pulsa 'Usar Entrenador'.";
            if (c.IsBasicPokemon) return "Pulsa el botón 'Bajar a la Banca'.";
            return "";
        }

        void OnHandLeftClicked(int i)
        {
            if (game.Phase == Phase.Setup) { Report(game.SetupPlace(Me, i)); return; }
            selHand = selHand == i ? -1 : i;
        }

        void OnPokemonLeftClicked(PlayerState owner, PokemonInPlay pip)
        {
            if (game.Phase == Phase.ChoosePromotion && owner == Me && Me.Bench.Contains(pip)) { Report(game.Promote(Me, Me.Bench.IndexOf(pip))); return; }
            if (retreatMode && owner == Me && Me.Bench.Contains(pip)) { retreatMode = false; Report(game.Retreat(Me, Me.Bench.IndexOf(pip))); return; }
            if (selHand < 0 || selHand >= Me.Hand.Count) return;
            var c = Me.Hand[selHand];
            if (c.Supertype == Supertype.Energy && owner == Me) Report(game.AttachEnergy(Me, selHand, pip));
            else if (c.Supertype == Supertype.Pokemon && owner == Me) Report(game.Evolve(Me, selHand, pip));
            else if (c.Supertype == Supertype.Trainer) Report(game.PlayTrainer(Me, selHand, pip));
        }

        void Report(string err)
        {
            message = err ?? "";
            if (err == null) selHand = -1;
            logScroll.y = float.MaxValue;
        }
    }
}
