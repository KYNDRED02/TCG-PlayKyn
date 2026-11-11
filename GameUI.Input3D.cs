// TCG-PlayKyn: Mouse raycast, card drag-and-drop targeting and board click handlers
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace TCGPlayKyn
{
    /// <summary>
    /// Submódulo de interactividad con ratón y arrastre (Drag & Drop) sobre el tapete 3D.
    /// 
    /// ¿Para qué sirve?
    /// - Detecta cuando el jugador agarra una carta de la mano y la arrastra hacia la mesa.
    /// - 'ComputeValidTargetAnchors' calcula y resalta las casillas legales del tapete según la carta.
    /// - 'TryDropHandCard3D' ejecuta la acción correspondiente al soltar la carta:
    ///   bajar a banca, evolucionar a un Pokémon existente, unir energía o jugar entrenador.
    /// - Gestiona clics directos sobre el campo de batalla (atacar, retirar, inspeccionar).
    /// 
    /// Notas para desarrolladores:
    /// - Si añades una nueva regla de colocación o arrastre, agrégala en 'TryDropHandCard3D'.
    /// </summary>
    public partial class GameUI
    {
void Update3DInput()
        {
            var tv = TableView.Instance;
            if (tv == null || !tv.IsBoard3DReady || game == null) return;

            bool animBusy = IsAnimBusy();
            bool allow3DInput = zoomedCard == null && !showSettingsModal && game.Phase != Phase.CoinToss && game.Phase != Phase.GameOver &&
                                !startCoinSpinning && !animBusy && !game.PendingAiTurn && !tv.IsPokedexActive;

            if (!allow3DInput)
            {
                Reset3DInputState();
                tv.UpdateSlotHighlights3D(null, null);
                return;
            }

            var cam = TableView.GetActiveCamera();
            if (cam == null) return;

            Vector2 guiMouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            bool overHud = IsMouseOverHud(guiMouse);
            Ray ray = cam.ScreenPointToRay(Input.mousePosition);

            if (!isDragging3D)
            {
                if (!overHud)
                {
                    if (tv.RaycastHandCard3D(ray, out int hIdx, out _) && hIdx >= 0 && hIdx < Me.Hand.Count)
                    {
                        if (hoveredHandIdx != hIdx)
                        {
                            hoveredHandIdx = hIdx;
                            PlayClickSound(0);
                        }
                        hoveredSlot3D = null;
                        hoveredBoardCard3D = null;
                    }
                    else
                    {
                        hoveredHandIdx = -1;
                        tv.RaycastBoard3D(ray, out hoveredSlot3D, out hoveredBoardCard3D);
                    }
                }
                else
                {
                    hoveredHandIdx = -1;
                    hoveredSlot3D = null;
                    hoveredBoardCard3D = null;
                }
            }
            else
            {
                tv.TryGetDragWorldPos(ray, out currentDragWorldPos);
                if (tv.RaycastHandCard3D(ray, out int overHandIdx, out _) && overHandIdx >= 0)
                {
                    hoveredSlot3D = null;
                    hoveredBoardCard3D = null;
                }
                else
                {
                    tv.RaycastBoard3D(ray, out hoveredSlot3D, out hoveredBoardCard3D);
                }
            }

            // Actualizar casillas iluminadas en el tapete 3D
            int activeCardIdx = isDragging3D ? pressedHandIdx : (selHand >= 0 ? selHand : hoveredHandIdx);
            ComputeValidTargetAnchors(activeCardIdx);
            Transform hoveredAnchor = hoveredSlot3D != null ? hoveredSlot3D.Anchor : null;
            tv.UpdateSlotHighlights3D(validTargetAnchors, hoveredAnchor);

            // --- Clic Izquierdo y Arrastre 3D (Select + Drag & Drop) ---
            if (Input.GetMouseButtonDown(0) && !overHud)
            {
                if (hoveredHandIdx >= 0 && hoveredHandIdx < Me.Hand.Count)
                {
                    pressedHandIdx = hoveredHandIdx;
                    pressMousePos = Input.mousePosition;
                    isDragging3D = false;
                    tv.TryGetDragWorldPos(ray, out currentDragWorldPos);
                }
                else if (hoveredSlot3D != null || hoveredBoardCard3D != null)
                {
                    On3DBoardLeftClick(hoveredSlot3D, hoveredBoardCard3D);
                }
                else
                {
                    selHand = -1;
                }
            }

            if (Input.GetMouseButton(0) && pressedHandIdx >= 0 && pressedHandIdx < Me.Hand.Count)
            {
                if (!isDragging3D && Vector2.Distance(Input.mousePosition, pressMousePos) > 7f)
                {
                    isDragging3D = true;
                    selHand = pressedHandIdx;
                    retreatMode = false;
                    var c = Me.Hand[pressedHandIdx];
                    message = $"Dragging: {c.Name} — {HintFor(c)}";
                }
            }

            if (Input.GetMouseButtonUp(0) && pressedHandIdx >= 0)
            {
                int idx = pressedHandIdx;
                bool wasDragging = isDragging3D;
                pressedHandIdx = -1;
                isDragging3D = false;

                if (idx >= 0 && idx < Me.Hand.Count)
                {
                    if (wasDragging)
                    {
                        TryDropHandCard3D(idx, hoveredSlot3D, currentDragWorldPos);
                    }
                    else
                    {
                        Vector3 handWorldPos = tv.GetHandCardWorldPos(0, idx, Me.Hand.Count);
                        BoardAnimator.Instance?.SetNextHandOrigin(handWorldPos);
                        PlayClickSound(0);
                        OnHandLeftClicked(idx);
                    }
                }
            }

            // --- Clic Derecho en 3D = Inspeccionar cualquier carta ---
            if (Input.GetMouseButtonDown(1) && !overHud)
            {
                if (isDragging3D)
                {
                    isDragging3D = false;
                    pressedHandIdx = -1;
                    PlayClickSound(2);
                    return;
                }

                if (hoveredHandIdx >= 0 && hoveredHandIdx < Me.Hand.Count)
                {
                    var c = Me.Hand[hoveredHandIdx];
                    Vector3 cardWorldPos = tv.GetHandCardWorldPos(0, hoveredHandIdx, Me.Hand.Count);
                    Quaternion cardWorldRot = tv.GetHandCardWorldRot(0, hoveredHandIdx, Me.Hand.Count);
                    if (!tv.TryProjectWorldPosToGUI(cardWorldPos, 1.05f, slotW, slotH, out Rect originRect))
                        originRect = GetHandCenterRect(0);
                    PlayClickSound(0);
                    StartZoom(c, null, originRect, cardWorldPos, cardWorldRot, 1.02f);
                }
                else if (hoveredBoardCard3D != null && hoveredBoardCard3D.BoundPip != null)
                {
                    var pip = hoveredBoardCard3D.BoundPip;
                    int pOwner = Mathf.Clamp(hoveredBoardCard3D.OwnerIndex, 0, 1);
                    Rect originRect = game.Players[pOwner].Active == pip
                        ? GetActiveRect(pOwner)
                        : GetBenchRect(pOwner, Mathf.Max(0, game.Players[pOwner].Bench.IndexOf(pip)));
                    PlayClickSound(0);
                    StartZoom(pip.Card, pip, originRect, hoveredBoardCard3D.transform.position, hoveredBoardCard3D.transform.rotation, hoveredBoardCard3D.CurrentScale);
                }
                else if (hoveredBoardCard3D != null && hoveredBoardCard3D.IsDiscardTop)
                {
                    int pOwner = Mathf.Clamp(hoveredBoardCard3D.OwnerIndex, 0, 1);
                    PlayClickSound(0);
                    StartZoom(hoveredBoardCard3D.Card, null, GetTrashRect(pOwner), hoveredBoardCard3D.transform.position, hoveredBoardCard3D.transform.rotation, 1f);
                }
                else if (hoveredSlot3D != null)
                {
                    int pOwner = Mathf.Clamp(hoveredSlot3D.PlayerIndex, 0, 1);
                    Transform slotAnchor = hoveredSlot3D.Anchor;
                    Vector3 slotPos = slotAnchor != null ? slotAnchor.position : Vector3.zero;
                    Quaternion slotRot = slotAnchor != null ? slotAnchor.rotation : Quaternion.identity;
                    if (hoveredSlot3D.Kind == TableSlotKind.Discard)
                    {
                        var disc = game.Players[pOwner].Discard;
                        if (disc.Count > 0)
                        {
                            PlayClickSound(0);
                            StartZoom(disc[disc.Count - 1], null, GetTrashRect(pOwner), slotPos, slotRot, 1f);
                        }
                    }
                    else if (hoveredSlot3D.Kind == TableSlotKind.Active)
                    {
                        var pip = game.Players[pOwner].Active;
                        if (pip != null)
                        {
                            PlayClickSound(0);
                            StartZoom(pip.Card, pip, GetActiveRect(pOwner), slotPos, slotRot, 1.08f);
                        }
                    }
                    else if (hoveredSlot3D.Kind == TableSlotKind.Bench)
                    {
                        int bIdx = hoveredSlot3D.BenchIndex;
                        var bench = game.Players[pOwner].Bench;
                        if (bIdx >= 0 && bIdx < bench.Count)
                        {
                            PlayClickSound(0);
                            StartZoom(bench[bIdx].Card, bench[bIdx], GetBenchRect(pOwner, bIdx), slotPos, slotRot, 0.94f);
                        }
                    }
                }
            }
        }

        bool CanEvolveTarget(CardDef evoCard, PokemonInPlay target)
        {
            if (evoCard == null || target == null || target.IsDoll) return false;
            if (evoCard.Supertype != Supertype.Pokemon || evoCard.Stage == "Basic") return false;
            if (evoCard.EvolvesFrom != target.Card.Name) return false;
            if (game.Turn <= 2 || target.TurnPlaced == game.Turn || target.TurnEvolved == game.Turn) return false;
            return true;
        }

        static bool IsGlobalTrainer(CardDef c)
        {
            if (c == null || c.Supertype != Supertype.Trainer) return false;
            switch (c.Name)
            {
                case "Potion":
                case "Super Potion":
                case "Defender":
                case "Scoop Up":
                case "Devolution Spray":
                case "Pokémon Breeder":
                case "Gust of Wind":
                case "Energy Removal":
                case "Super Energy Removal":
                    return false;
                default:
                    return true;
            }
        }

        void ComputeValidTargetAnchors(int handIdx)
        {
            validTargetAnchors.Clear();
            var tv = TableView.Instance;
            if (tv == null || game == null) return;

            if (game.Phase == Phase.Setup)
            {
                if (handIdx >= 0 && handIdx < Me.Hand.Count && !Me.Hand[handIdx].IsBasicPokemon)
                    return;

                if (Me.Active == null)
                {
                    if (tv.ActiveAnchor[0] != null) validTargetAnchors.Add(tv.ActiveAnchor[0]);
                }
                else if (Me.Bench.Count < 5)
                {
                    for (int i = Me.Bench.Count; i < 5; i++)
                        if (tv.BenchAnchor[0, i] != null) validTargetAnchors.Add(tv.BenchAnchor[0, i]);
                }
                return;
            }

            if (game.Phase == Phase.ChoosePromotion && game.PromotionPending == Me)
            {
                for (int i = 0; i < Me.Bench.Count; i++)
                    if (tv.BenchAnchor[0, i] != null) validTargetAnchors.Add(tv.BenchAnchor[0, i]);
                return;
            }

            if (retreatMode && game.Phase == Phase.Main && game.Current == Me)
            {
                for (int i = 0; i < Me.Bench.Count; i++)
                    if (tv.BenchAnchor[0, i] != null) validTargetAnchors.Add(tv.BenchAnchor[0, i]);
                return;
            }

            if (game.Phase != Phase.Main || game.Current != Me) return;
            if (handIdx < 0 || handIdx >= Me.Hand.Count) return;

            var c = Me.Hand[handIdx];
            if (c.IsBasicPokemon)
            {
                if (Me.Bench.Count < 5)
                {
                    for (int i = Me.Bench.Count; i < 5; i++)
                        if (tv.BenchAnchor[0, i] != null) validTargetAnchors.Add(tv.BenchAnchor[0, i]);
                }
            }
            else if (c.Supertype == Supertype.Pokemon && c.Stage != "Basic")
            {
                if (CanEvolveTarget(c, Me.Active) && tv.ActiveAnchor[0] != null)
                    validTargetAnchors.Add(tv.ActiveAnchor[0]);
                for (int i = 0; i < Me.Bench.Count; i++)
                    if (CanEvolveTarget(c, Me.Bench[i]) && tv.BenchAnchor[0, i] != null)
                        validTargetAnchors.Add(tv.BenchAnchor[0, i]);
            }
            else if (c.Supertype == Supertype.Energy)
            {
                if (!Me.EnergyPlayedThisTurn)
                {
                    if (Me.Active != null && !Me.Active.IsDoll && tv.ActiveAnchor[0] != null)
                        validTargetAnchors.Add(tv.ActiveAnchor[0]);
                    for (int i = 0; i < Me.Bench.Count; i++)
                        if (!Me.Bench[i].IsDoll && tv.BenchAnchor[0, i] != null)
                            validTargetAnchors.Add(tv.BenchAnchor[0, i]);
                }
            }
            else if (c.Supertype == Supertype.Trainer)
            {
                switch (c.Name)
                {
                    case "Potion":
                    case "Super Potion":
                        if (Me.Active != null && Me.Active.Damage > 0 && (c.Name == "Potion" || Me.Active.Energy.Count > 0))
                            validTargetAnchors.Add(tv.ActiveAnchor[0]);
                        for (int i = 0; i < Me.Bench.Count; i++)
                            if (Me.Bench[i].Damage > 0 && (c.Name == "Potion" || Me.Bench[i].Energy.Count > 0))
                                validTargetAnchors.Add(tv.BenchAnchor[0, i]);
                        break;

                    case "Defender":
                    case "Scoop Up":
                        if (Me.Active != null && (c.Name != "Scoop Up" || Me.Bench.Count > 0))
                            validTargetAnchors.Add(tv.ActiveAnchor[0]);
                        for (int i = 0; i < Me.Bench.Count; i++)
                            validTargetAnchors.Add(tv.BenchAnchor[0, i]);
                        break;

                    case "Devolution Spray":
                        if (Me.Active != null && Me.Active.Stack.Count >= 2)
                            validTargetAnchors.Add(tv.ActiveAnchor[0]);
                        for (int i = 0; i < Me.Bench.Count; i++)
                            if (Me.Bench[i].Stack.Count >= 2)
                                validTargetAnchors.Add(tv.BenchAnchor[0, i]);
                        break;

                    case "Switch":
                        for (int i = 0; i < Me.Bench.Count; i++)
                            validTargetAnchors.Add(tv.BenchAnchor[0, i]);
                        break;

                    case "Gust of Wind":
                        for (int i = 0; i < Cpu.Bench.Count; i++)
                            validTargetAnchors.Add(tv.BenchAnchor[1, i]);
                        break;

                    case "Energy Removal":
                    case "Super Energy Removal":
                        if (Cpu.Active != null && Cpu.Active.Energy.Count > 0)
                            validTargetAnchors.Add(tv.ActiveAnchor[1]);
                        for (int i = 0; i < Cpu.Bench.Count; i++)
                            if (Cpu.Bench[i].Energy.Count > 0)
                                validTargetAnchors.Add(tv.BenchAnchor[1, i]);
                        break;

                    case "PlusPower":
                    case "Full Heal":
                        if (Me.Active != null && tv.ActiveAnchor[0] != null)
                            validTargetAnchors.Add(tv.ActiveAnchor[0]);
                        if (tv.CoinAnchor != null)
                            validTargetAnchors.Add(tv.CoinAnchor);
                        break;

                    case "Revive":
                    case "Clefairy Doll":
                        if (Me.Bench.Count < 5)
                        {
                            for (int i = Me.Bench.Count; i < 5; i++)
                                validTargetAnchors.Add(tv.BenchAnchor[0, i]);
                        }
                        if (tv.CoinAnchor != null)
                            validTargetAnchors.Add(tv.CoinAnchor);
                        break;

                    default:
                        if (tv.CoinAnchor != null) validTargetAnchors.Add(tv.CoinAnchor);
                        if (tv.ActiveAnchor[0] != null) validTargetAnchors.Add(tv.ActiveAnchor[0]);
                        break;
                }
            }
        }

        PokemonInPlay ResolvePipFromSlotOrCard(TableSlot3D slot, CardView boardCard, out PlayerState owner)
        {
            owner = null;
            if (boardCard != null && boardCard.BoundPip != null)
            {
                int p = Mathf.Clamp(boardCard.OwnerIndex, 0, 1);
                owner = game.Players[p];
                return boardCard.BoundPip;
            }
            if (slot != null)
            {
                int p = Mathf.Clamp(slot.PlayerIndex, 0, 1);
                owner = game.Players[p];
                if (slot.Kind == TableSlotKind.Active)
                    return owner.Active;
                if (slot.Kind == TableSlotKind.Bench && slot.BenchIndex >= 0 && slot.BenchIndex < owner.Bench.Count)
                    return owner.Bench[slot.BenchIndex];
            }
            return null;
        }

        bool IsWorldPosOverPlaymat(Vector3 worldPos)
        {
            var tv = TableView.Instance;
            if (tv == null || tv.PlaymatRoot == null) return false;
            Vector3 local = tv.PlaymatRoot.InverseTransformPoint(worldPos);
            return Mathf.Abs(local.x) <= TableView.MatWidth * 0.48f &&
                   local.z >= -TableView.MatLength * 0.36f &&
                   local.z <= TableView.MatLength * 0.48f;
        }

        void TryDropHandCard3D(int handIdx, TableSlot3D dropSlot, Vector3 dropWorldPos)
        {
            if (handIdx < 0 || handIdx >= Me.Hand.Count) return;
            var c = Me.Hand[handIdx];

            // 1) En Fase de Preparación (Setup)
            if (game.Phase == Phase.Setup)
            {
                bool onPlayerSide = (dropSlot != null && dropSlot.PlayerIndex == 0 && (dropSlot.Kind == TableSlotKind.Active || dropSlot.Kind == TableSlotKind.Bench))
                                 || IsWorldPosOverPlaymat(dropWorldPos);
                if (onPlayerSide)
                {
                    BoardAnimator.Instance?.SetNextHandOrigin(dropWorldPos);
                    PlayClickSound(1);
                    Report(game.SetupPlace(Me, handIdx));
                    return;
                }
                PlayClickSound(0);
                return;
            }

            // 2) En Fase Principal (Main)
            if (game.Phase == Phase.Main && game.Current == Me)
            {
                var targetPip = ResolvePipFromSlotOrCard(dropSlot, hoveredBoardCard3D, out PlayerState owner);

                if (c.IsBasicPokemon)
                {
                    if ((dropSlot != null && dropSlot.PlayerIndex == 0 && (dropSlot.Kind == TableSlotKind.Bench || dropSlot.Kind == TableSlotKind.Active))
                        || IsWorldPosOverPlaymat(dropWorldPos))
                    {
                        BoardAnimator.Instance?.SetNextHandOrigin(dropWorldPos);
                        PlayClickSound(1);
                        Report(game.PlayBasic(Me, handIdx));
                        return;
                    }
                }
                else if (c.Supertype == Supertype.Energy)
                {
                    if (targetPip != null && owner == Me)
                    {
                        BoardAnimator.Instance?.SetNextHandOrigin(dropWorldPos);
                        PlayClickSound(1);
                        Report(game.AttachEnergy(Me, handIdx, targetPip));
                        return;
                    }
                }
                else if (c.Supertype == Supertype.Pokemon && c.Stage != "Basic")
                {
                    if (targetPip != null && owner == Me)
                    {
                        BoardAnimator.Instance?.SetNextHandOrigin(dropWorldPos);
                        PlayClickSound(1);
                        Report(game.Evolve(Me, handIdx, targetPip));
                        return;
                    }
                }
                else if (c.Supertype == Supertype.Trainer)
                {
                    if (targetPip != null)
                    {
                        BoardAnimator.Instance?.SetNextHandOrigin(dropWorldPos);
                        PlayClickSound(1);
                        Report(game.PlayTrainer(Me, handIdx, targetPip));
                        return;
                    }
                    if (IsGlobalTrainer(c) && (dropSlot != null || IsWorldPosOverPlaymat(dropWorldPos)))
                    {
                        BoardAnimator.Instance?.SetNextHandOrigin(dropWorldPos);
                        PlayClickSound(1);
                        Report(game.PlayTrainer(Me, handIdx, null));
                        return;
                    }
                }
            }

            // Si se soltó fuera de un objetivo válido, la carta regresa suavemente al abanico 3D de la mano
            PlayClickSound(0);
        }

        void On3DBoardLeftClick(TableSlot3D slot, CardView boardCard)
        {
            var pip = ResolvePipFromSlotOrCard(slot, boardCard, out PlayerState owner);

            if (game.Phase == Phase.ChoosePromotion && game.PromotionPending == Me)
            {
                if (pip != null && owner == Me && Me.Bench.Contains(pip))
                {
                    PlayClickSound(1);
                    Report(game.Promote(Me, Me.Bench.IndexOf(pip)));
                }
                return;
            }

            if (retreatMode && game.Phase == Phase.Main && game.Current == Me)
            {
                if (pip != null && owner == Me && Me.Bench.Contains(pip))
                {
                    retreatMode = false;
                    PlayClickSound(1);
                    Report(game.Retreat(Me, Me.Bench.IndexOf(pip)));
                }
                return;
            }

            if (selHand >= 0 && selHand < Me.Hand.Count)
            {
                Vector3 handOrigin = TableView.Instance != null
                    ? TableView.Instance.GetHandCardWorldPos(0, selHand, Me.Hand.Count)
                    : default;

                if (game.Phase == Phase.Setup && slot != null && slot.PlayerIndex == 0 &&
                    (slot.Kind == TableSlotKind.Active || slot.Kind == TableSlotKind.Bench))
                {
                    BoardAnimator.Instance?.SetNextHandOrigin(handOrigin);
                    PlayClickSound(1);
                    Report(game.SetupPlace(Me, selHand));
                    return;
                }

                if (game.Phase == Phase.Main && game.Current == Me)
                {
                    var c = Me.Hand[selHand];
                    if (c.IsBasicPokemon && slot != null && slot.PlayerIndex == 0 && slot.Kind == TableSlotKind.Bench)
                    {
                        BoardAnimator.Instance?.SetNextHandOrigin(handOrigin);
                        PlayClickSound(1);
                        Report(game.PlayBasic(Me, selHand));
                        return;
                    }

                    if (pip != null)
                    {
                        BoardAnimator.Instance?.SetNextHandOrigin(handOrigin);
                        PlayClickSound(1);
                        OnPokemonLeftClicked(owner, pip);
                        return;
                    }

                    if (c.Supertype == Supertype.Trainer && IsGlobalTrainer(c) && slot != null &&
                        (slot.Kind == TableSlotKind.PlayfieldCenter || slot.Kind == TableSlotKind.Active || slot.Kind == TableSlotKind.Bench))
                    {
                        BoardAnimator.Instance?.SetNextHandOrigin(handOrigin);
                        PlayClickSound(1);
                        Report(game.PlayTrainer(Me, selHand, null));
                        return;
                    }
                }
            }
            else if (pip != null)
            {
                OnPokemonLeftClicked(owner, pip);
            }
        }

        // ==================== Interacciones del Jugador ====================
        void OnHandLeftClicked(int i)
        {
            if (game.Phase == Phase.Setup) { Report(game.SetupPlace(Me, i)); return; }
            selHand = selHand == i ? -1 : i;
            if (selHand >= 0 && selHand < Me.Hand.Count)
            {
                var c = Me.Hand[selHand];
                message = $"Selected: {c.Name} — {HintFor(c)}";
            }
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

        string HintFor(CardDef c)
        {
            if (c.Supertype == Supertype.Energy) return "Drag or click one of your Pokémon to attach energy.";
            if (c.Supertype == Supertype.Pokemon && c.Stage != "Basic") return $"Drag or click your {c.EvolvesFrom} to evolve it.";
            if (c.Supertype == Supertype.Trainer) return IsGlobalTrainer(c) ? "Drag to the playmat or click 'Play Trainer'." : "Drag or click the target Pokémon.";
            if (c.IsBasicPokemon) return game.Phase == Phase.Setup ? "Drag to the highlighted spot or click to place." : "Drag to an empty Bench slot or click 'Play to Bench'.";
            return "";
        }

        void Report(string err)
        {
            message = err ?? "";
            if (err == null) selHand = -1;
        }
    }
}
