using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace PokeTCGPlay
{
    /// <summary>Mesa de juego IMGUI (v0.2). Bloquea la cámara con InteractionPlayerController.EnterUIMode/ExitUIMode y carga sprites de MyCurio.</summary>
    public class GameUI : MonoBehaviour
    {
        bool open;
        bool enteredGameUIMode;
        Game game;
        string message = "";
        int selHand = -1;
        bool retreatMode;
        Vector2 logScroll, handScroll;
        PlayerState Me => game.Players[0];
        PlayerState Cpu => game.Players[1];

        const float CW = 110, CH = 154; // tamaño carta (proporción 63x88)
        CursorLockMode prevLock;
        bool prevVisible;

        void SetOpen(bool value)
        {
            if (open == value) return;
            open = value;
            if (open)
            {
                prevLock = Cursor.lockState;
                prevVisible = Cursor.visible;
                LockPlayerCamera(true);
                SpriteCache.EnsureInitialized();
                SpriteCache.PreloadFromEpl();
            }
            else
            {
                LockPlayerCamera(false);
                Cursor.lockState = prevLock;
                Cursor.visible = prevVisible;
            }
        }

        void LockPlayerCamera(bool lockCam)
        {
            try
            {
                var ipc = CSingleton<InteractionPlayerController>.Instance;
                if (ipc != null)
                {
                    if (lockCam)
                    {
                        if (!ipc.IsInUIMode())
                        {
                            ipc.EnterUIMode();
                            enteredGameUIMode = true;
                        }
                        InputManager.SetCursorLockMode(CursorLockMode.None);
                        Cursor.visible = true;
                    }
                    else
                    {
                        if (enteredGameUIMode)
                        {
                            ipc.ExitUIMode();
                            enteredGameUIMode = false;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("LockPlayerCamera: " + e.Message);
            }
        }

        void Update()
        {
            if (Plugin.ToggleKey.Value.IsDown())
            {
                SetOpen(!open);
            }
            if (open)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
        }

        void NewGame()
        {
            try
            {
                SpriteCache.PreloadFromEpl();
                string dir = Path.Combine(Plugin.DataDir, "decks");
                var a = DeckLoader.Load(Path.Combine(dir, "player.txt"));
                var b = DeckLoader.Load(Path.Combine(dir, "cpu.txt"));
                game = new Game(a, b); selHand = -1; retreatMode = false;
                message = "Preparación: haz clic en Básicos de tu mano (el primero será el Activo) y pulsa Listo.";
            }
            catch (Exception e) { message = e.Message; Plugin.Log.LogError(e); }
        }

        void OnGUI()
        {
            if (!open) return;
            GUI.depth = -1000;
            var r = new Rect(20, 20, Screen.width - 40, Screen.height - 40);
            GUI.Box(r, ""); GUI.Box(r, ""); // doble para opacidad
            GUILayout.BeginArea(new Rect(r.x + 10, r.y + 10, r.width - 20, r.height - 20));

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Nueva partida", GUILayout.Width(130))) NewGame();
            GUILayout.Label($"  Sprites MyCurio cargados: {SpriteCache.Count}/102   (F8 cerrar)");
            GUILayout.EndHorizontal();
            GUILayout.Label(message);

            if (game != null) DrawBoard();
            GUILayout.EndArea();
        }

        void DrawBoard()
        {
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical();

            // --- Rival ---
            GUILayout.Label($"<b>CPU</b>  Mano: {Cpu.Hand.Count}  Mazo: {Cpu.Deck.Count}  Premios: {Cpu.Prizes.Count}  Descarte: {Cpu.Discard.Count}", Rich);
            DrawRow(Cpu);
            GUILayout.Space(10);
            // --- Yo ---
            DrawRow(Me);
            GUILayout.Label($"<b>Tú</b>  Mazo: {Me.Deck.Count}  Premios: {Me.Prizes.Count}  Descarte: {Me.Discard.Count}  Energía este turno: {(Me.EnergyPlayedThisTurn ? "sí" : "no")}", Rich);
            DrawActions();
            DrawHand();

            GUILayout.EndVertical();

            // --- Log ---
            GUILayout.BeginVertical(GUILayout.Width(360));
            if (game.Phase == Phase.GameOver) GUILayout.Label($"<size=18><b>GANA {game.Winner.Name}</b></size>\n{game.WinReason}", Rich);
            else GUILayout.Label($"Turno {game.Turn} — {(game.Phase == Phase.Setup ? "Preparación" : game.Current.Name)}");
            logScroll = GUILayout.BeginScrollView(logScroll);
            GUILayout.Label(string.Join("\n", game.Log.Skip(Math.Max(0, game.Log.Count - 80))));
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
        }

        static GUIStyle rich;
        static GUIStyle Rich => rich ?? (rich = new GUIStyle(GUI.skin.label) { richText = true });

        void DrawRow(PlayerState p)
        {
            GUILayout.BeginHorizontal();
            DrawPokemon(p, p.Active, true);
            GUILayout.Space(30);
            foreach (var b in p.Bench.ToList()) DrawPokemon(p, b, false);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        void DrawPokemon(PlayerState owner, PokemonInPlay pip, bool active)
        {
            GUILayout.BeginVertical(GUILayout.Width(CW));
            if (pip == null) { GUILayout.Box(active ? "(sin Activo)" : "", GUILayout.Width(CW), GUILayout.Height(CH)); GUILayout.EndVertical(); return; }
            var rect = GUILayoutUtility.GetRect(CW, CH);
            if (DrawCard(rect, pip.Card)) OnPokemonClicked(owner, pip);
            GUILayout.Label($"{pip.Label}\nE: {string.Join(",", pip.Energy.Select(e => e.EnergyProvides.Substring(0, 2)))}" +
                            (pip.Attached.Count > 0 ? "\n+" + string.Join(",", pip.Attached.Select(x => x.Name)) : ""), GUILayout.Width(CW));
            GUILayout.EndVertical();
        }

        /// <summary>Dibuja la carta con el sprite de MyCurio (o texto si aún se está cargando). Devuelve true si se hizo clic.</summary>
        bool DrawCard(Rect rect, CardDef c, bool highlight = false)
        {
            var sp = SpriteCache.Get(c.Sprite);
            if (highlight) GUI.Box(new Rect(rect.x - 3, rect.y - 3, rect.width + 6, rect.height + 6), "");
            if (sp != null && sp.texture != null)
            {
                var t = sp.texture; var tr = sp.textureRect;
                GUI.DrawTextureWithTexCoords(rect, t, new Rect(tr.x / t.width, tr.y / t.height, tr.width / t.width, tr.height / t.height));
                return GUI.Button(rect, "", GUIStyle.none);
            }
            string txt = c.Name + "\n" + (c.Supertype == Supertype.Pokemon ? $"{c.Type} HP{c.Hp}\n{c.Stage}" : c.Supertype.ToString());
            return GUI.Button(rect, txt);
        }

        void DrawHand()
        {
            handScroll = GUILayout.BeginScrollView(handScroll, GUILayout.Height(CH + 30));
            GUILayout.BeginHorizontal();
            for (int i = 0; i < Me.Hand.Count; i++)
            {
                var rect = GUILayoutUtility.GetRect(CW, CH, GUILayout.Width(CW));
                if (DrawCard(rect, Me.Hand[i], i == selHand)) OnHandClicked(i);
            }
            GUILayout.EndHorizontal();
            GUILayout.EndScrollView();
        }

        void DrawActions()
        {
            GUILayout.BeginHorizontal();
            if (game.Phase == Phase.Setup)
            {
                if (GUILayout.Button("Listo", GUILayout.Width(100))) Report(game.FinishSetup(Me));
            }
            else if (game.Phase == Phase.ChoosePromotion && game.PromotionPending == Me)
            {
                GUILayout.Label("Tu Activo quedó fuera de combate: haz clic en un Pokémon de tu Banca.");
            }
            else if (game.Phase == Phase.Main && game.Current == Me)
            {
                var a = Me.Active;
                for (int i = 0; i < a.Card.Attacks.Count; i++)
                {
                    var atk = a.Card.Attacks[i];
                    GUI.enabled = a.CanPay(atk.Cost);
                    if (GUILayout.Button($"{atk.Name} [{string.Join("", atk.Cost.Select(c => c[0]))}] {atk.BaseDamage}{atk.DamageSuffix}", GUILayout.Width(220)))
                        Report(game.Attack(Me, i));
                    GUI.enabled = true;
                }
                if (GUILayout.Button(retreatMode ? "Cancelar retirada" : $"Retirarse ({a.Card.RetreatCost})", GUILayout.Width(150))) retreatMode = !retreatMode;
                if (selHand >= 0 && selHand < Me.Hand.Count)
                {
                    var c = Me.Hand[selHand];
                    if (c.IsBasicPokemon && GUILayout.Button("A la Banca", GUILayout.Width(110))) Report(game.PlayBasic(Me, selHand));
                    if (c.Supertype == Supertype.Trainer && GUILayout.Button("Jugar (sin objetivo)", GUILayout.Width(150))) Report(game.PlayTrainer(Me, selHand, null));
                }
                if (GUILayout.Button("Terminar turno", GUILayout.Width(130))) Report(game.EndTurn());
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            if (selHand >= 0 && selHand < Me.Hand.Count)
            {
                var c = Me.Hand[selHand];
                GUILayout.Label($"Seleccionada: {c.Name}. {HintFor(c)}");
            }
        }

        string HintFor(CardDef c)
        {
            if (c.Supertype == Supertype.Energy) return "Haz clic en un Pokémon tuyo para unirla.";
            if (c.Supertype == Supertype.Pokemon && c.Stage != "Basic") return $"Haz clic en {c.EvolvesFrom} para evolucionar.";
            if (c.Supertype == Supertype.Trainer) return "Haz clic en el Pokémon objetivo o en 'Jugar (sin objetivo)'.";
            return "";
        }

        void OnHandClicked(int i)
        {
            if (game.Phase == Phase.Setup) { Report(game.SetupPlace(Me, i)); return; }
            selHand = selHand == i ? -1 : i;
        }

        void OnPokemonClicked(PlayerState owner, PokemonInPlay pip)
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
