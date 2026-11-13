// TCG-PlayKyn: Table interaction lifecycle, seated match trigger and camera POV
using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace TCGPlayKyn
{
    public class MatchRewardSummary
    {
        public bool Applied;
        public bool PlayerWon;
        public int ExpGained;
        public float OldRating;
        public float NewRating;
        public int ReviewStars;
        public float TableFeeRetainedByNpc;
    }

    /// <summary>
    /// Gestiona el estado del jugador cuando se sienta en una mesa del juego (InteractablePlayTable),
    /// espera a que un NPC se siente enfrente, coloca la cámara en vista cenital (top-down) mirando a la mesa física
    /// y aplica el tapete de Pokémon sobre la mesa.
    /// </summary>
    public static class PlayTableSession
    {
        public static InteractablePlayTable CurrentTable { get; private set; }
        public static int PlayerSeat { get; private set; } = -1;
        public static Customer CurrentOpponent { get; private set; }
        public static bool IsWaitingForNpc => CurrentTable != null && CurrentOpponent == null;
        public static bool IsPlayingAtTable => CurrentTable != null && CurrentOpponent != null;
        public static float CapturedTableFee { get; private set; }
        public static MatchRewardSummary LastReward { get; private set; }

        static float waitTimer;
        static Vector3 savedStandPos;
        static Transform topDownCamLoc;
        static Transform topDownLookTarget;
        static Vector3 baseTopDownCamPos;
        static Vector3 baseTopDownLookPos;
        static bool hasBaseTopDownPose;

        static readonly FieldInfo fHasStartPlayerPlayCard = AccessTools.Field(typeof(InteractablePlayTable), "m_HasStartPlayerPlayCard");
        static readonly FieldInfo fIsPlayerOccupied = AccessTools.Field(typeof(InteractablePlayTable), "m_IsPlayerOccupied");
        static readonly FieldInfo fIsPlayTableGameMode = AccessTools.Field(typeof(InteractionPlayerController), "m_IsPlayTableGameMode");
        static readonly MethodInfo mSetCurrentGameState = AccessTools.Method(typeof(InteractionPlayerController), "SetCurrentGameState");
        static readonly MethodInfo mThinkWantToPlayTable = AccessTools.Method(typeof(Customer), "ThinkWantToPlayTable");
        static readonly MethodInfo mAddReviewData = AccessTools.Method(typeof(Customer), "AddReviewData");

        public static void SitAtTable(InteractablePlayTable table)
        {
            if (table == null || CurrentTable != null) return;
            if (DeckBuilderUI.Instance != null && DeckBuilderUI.Instance.IsOpen) return;

            var ipc = CSingleton<InteractionPlayerController>.Instance;
            if (ipc == null) return;

            // Validar mazo activo ANTES de sentar al jugador en la mesa (evita bloquear la cámara si el mazo es inválido)
            SpriteCache.EnsureInitialized();
            SpriteCache.PreloadFromEpl();
            Collection.Invalidate();

            var active = DeckStore.Active;
            if (active == null)
            {
                GameUI.PlayClickSound(2);
                GameUI.Instance?.ShowToast("You have no active deck. Open the Workbench (PokeDeck Builder) to create one.", 5.5f);
                return;
            }
            var format = FormatRegistry.Get(active.Format) ?? FormatRegistry.Default;
            var missing = new System.Collections.Generic.List<string>();
            var playerDeck = active.Resolve(missing);
            var errors = DeckValidator.Validate(playerDeck, format);
            if (missing.Count > 0) errors.Insert(0, $"Unknown cards: {string.Join(", ", missing)}");
            if (errors.Count > 0)
            {
                GameUI.PlayClickSound(2);
                GameUI.Instance?.ShowToast($"Your deck '{active.Name}' is not valid: {errors[0]} — Edit it at the Workbench.", 6.5f);
                return;
            }

            string missingArt = CardDatabase.GetMissingArtNotice();
            if (!string.IsNullOrEmpty(missingArt))
                GameUI.Instance?.ShowToast(missingArt, 6.5f);

            Vector3 playerPos = ipc.m_PlayerCollider != null ? ipc.m_PlayerCollider.transform.position : ipc.transform.position;
            float d0 = Vector3.Distance(playerPos, table.GetSitLoc(0).position);
            float d1 = Vector3.Distance(playerPos, table.GetSitLoc(1).position);
            int preferred = d0 <= d1 ? 0 : 1;
            int other = 1 - preferred;

            int seat = -1;
            if (!table.m_IsSeatOccupied[preferred]) seat = preferred;
            else if (!table.m_IsSeatOccupied[other]) seat = other;

            if (seat == -1)
            {
                GameUI.Instance?.ShowToast("This table is already occupied by 2 customers.");
                return;
            }

            CurrentTable = table;
            PlayerSeat = seat;
            CurrentOpponent = null;
            waitTimer = 0f;
            savedStandPos = playerPos;

            table.m_IsSeatOccupied[seat] = true;
            table.m_IsSeatBooked[seat] = true;
            table.m_IsQueueOccupied[seat] = true;
            table.m_IsPlayerSeat[seat] = true;
            table.m_CurrentPlayerCount = (table.m_IsSeatOccupied[0] ? 1 : 0) + (table.m_IsSeatOccupied[1] ? 1 : 0);

            SpriteCache.EnsureInitialized();
            SpriteCache.PreloadFromEpl();

            // F1: Montar el Playmat 3D y todos los anclajes sobre la superficie real de la mesa
            TableView.Instance?.AttachToTable(table, seat);

            Transform sitLoc = table.GetSitLoc(seat);
            Transform oppSitLoc = table.GetSitLoc(1 - seat);
            ipc.StopCameraLerp();
            ipc.EnterUIMode();
            ipc.OnEnterPlayTableGameMode(null);
            ApplySeatedShopHudFilter(true);
            if (ipc.m_PlayerCollider != null) ipc.m_PlayerCollider.isTrigger = true;
            if (ipc.m_WalkerCtrl != null) ipc.m_WalkerCtrl.SetStopMovement(true);
            InteractionPlayerController.SetPlayerPos(sitLoc.position + new Vector3(0f, 0.15f, 0f));
            if (oppSitLoc != null) ipc.StartAimLookAt(oppSitLoc, 6f, 0f);
            InputManager.SetCursorLockMode(CursorLockMode.None);
            Cursor.visible = true;

            int oppSeat = 1 - seat;
            if (table.m_IsSeatOccupied[oppSeat] && table.m_OccupiedCustomer != null && oppSeat < table.m_OccupiedCustomer.Count && table.m_OccupiedCustomer[oppSeat] != null)
            {
                OnOpponentSeated(table.m_OccupiedCustomer[oppSeat]);
            }
            else
            {
                fIsPlayerOccupied?.SetValue(table, false);
                fHasStartPlayerPlayCard?.SetValue(table, false);
                table.m_HasStartPlay = false;
                TryInviteShopCustomer();
            }
        }

        static void ApplyPlaymatOn3DTable(InteractablePlayTable table, int seat)
        {
            try
            {
                if (table.m_TableGameItemSetList == null || seat >= table.m_TableGameItemSetList.Count) return;
                var set = table.m_TableGameItemSetList[seat];
                if (set == null) return;
                set.RandomizeSetup();
                set.gameObject.SetActive(true);
                if (set.m_PlayMatMesh != null && SpriteCache.PlaymatTex != null)
                {
                    set.m_PlayMatMesh.gameObject.SetActive(true);
                    set.m_PlayMatMesh.material.mainTexture = SpriteCache.PlaymatTex;
                }
            }
            catch { }
        }

        static void EnterTopDownTableCamera(InteractablePlayTable table, int seat)
        {
            var ipc = CSingleton<InteractionPlayerController>.Instance;
            if (ipc == null || table == null) return;

            if (topDownCamLoc == null)
                topDownCamLoc = new GameObject("PokeTCG_TopDownCamLoc").transform;
            if (topDownLookTarget == null)
                topDownLookTarget = new GameObject("PokeTCG_TopDownLookTarget").transform;

            var tv = TableView.Instance;
            if (tv != null && tv.PlaymatRoot != null)
            {
                Vector3 matCenter = tv.PlaymatRoot.position + Vector3.up * TableView.MatThickness;
                Vector3 forward = tv.PlaymatRoot.forward;
                // Vista en primera persona desde los ojos del jugador sentado en la silla mirando el tapete y al oponente
                topDownCamLoc.position = matCenter - forward * 0.54f + Vector3.up * 0.52f;
                topDownLookTarget.position = matCenter - forward * 0.04f - Vector3.up * 0.02f;
                topDownCamLoc.rotation = Quaternion.LookRotation((topDownLookTarget.position - topDownCamLoc.position).normalized, Vector3.up);
            }
            else
            {
                Vector3 sitA = table.GetSitLoc(seat).position;
                Vector3 sitB = table.GetSitLoc(1 - seat).position;
                Vector3 center = (sitA + sitB) * 0.5f;
                Vector3 toCenter = (center - sitA).normalized;
                Vector3 tableSurface = new Vector3(center.x, table.transform.position.y + 0.785f, center.z);
                topDownCamLoc.position = tableSurface - toCenter * 0.54f + Vector3.up * 0.52f;
                topDownLookTarget.position = tableSurface - toCenter * 0.04f - Vector3.up * 0.02f;
            }

            baseTopDownCamPos = topDownCamLoc.position;
            baseTopDownLookPos = topDownLookTarget.position;
            hasBaseTopDownPose = true;

            ipc.SetCameraWorldPositionTarget(topDownCamLoc);
            ipc.StartAimLookAt(topDownLookTarget, 7.5f, 0f);
            ipc.m_IsPlayingTopDownGameMode = true;
            if (ipc.m_CameraFOVController != null)
                ipc.m_CameraFOVController.StartLerpToFOV(52f);
        }

        public static void OnOpponentSeated(Customer npc)
        {
            if (CurrentTable == null || npc == null) return;
            CurrentOpponent = npc;
            LastReward = null;

            int oppSeat = 1 - PlayerSeat;
            CapturedTableFee = ReadOpponentTableFee(CurrentTable, npc, oppSeat);

            CurrentTable.m_HasStartPlay = true;
            fHasStartPlayerPlayCard?.SetValue(CurrentTable, true);
            fIsPlayerOccupied?.SetValue(CurrentTable, true);
            CurrentTable.m_CurrentPlayTime = 0f;
            CurrentTable.m_CurrentPlayTimeMax = 99999f;
            npc.PlayTableGameStarted();

            if (TableView.Instance != null)
            {
                if (TableView.Instance.Table != CurrentTable)
                    TableView.Instance.AttachToTable(CurrentTable, PlayerSeat);
                else
                    TableView.Instance.HideDefaultTableMats();
            }
            EnterTopDownTableCamera(CurrentTable, PlayerSeat);

            GameUI.Instance?.StartTableMatch(npc);
        }

        static float ReadOpponentTableFee(InteractablePlayTable table, Customer npc, int oppSeat)
        {
            float fee = 0f;
            try
            {
                if (table != null && table.m_PlayTableFee != null && oppSeat >= 0 && oppSeat < table.m_PlayTableFee.Count)
                    fee = Mathf.Max(fee, table.m_PlayTableFee[oppSeat]);
                if (npc != null)
                    fee = Mathf.Max(fee, npc.GetCurrentPlayTableFee());
            }
            catch { }
            return fee;
        }

        /// <summary>
        /// F8: Mantiene bloqueado el movimiento y la cámara del jugador mientras permanece sentado en la mesa,
        /// y asegura que en el HUD nativo únicamente se vean Hora/Día, Nivel de tienda, EXP y Dinero.
        /// </summary>
        public static void EnforcePlayerCameraAndMovementLock()
        {
            if (CurrentTable == null) return;
            try
            {
                var ipc = CSingleton<InteractionPlayerController>.Instance;
                if (ipc != null)
                {
                    if (ipc.m_WalkerCtrl != null) ipc.m_WalkerCtrl.SetStopMovement(true);
                    if (ipc.m_PlayerCollider != null) ipc.m_PlayerCollider.isTrigger = true;
                    if (CurrentOpponent != null)
                        ipc.m_IsPlayingTopDownGameMode = true;
                }
                ApplySeatedShopHudFilter(true);
                if (hasBaseTopDownPose && topDownCamLoc != null && topDownLookTarget != null)
                {
                    Vector3 shake = PolishEffects3D.GetCameraShakeOffset();
                    topDownCamLoc.position = baseTopDownCamPos + shake;
                    topDownLookTarget.position = baseTopDownLookPos + shake * 0.75f;
                }
                InputManager.SetCursorLockMode(CursorLockMode.None);
                Cursor.visible = true;
            }
            catch { }
        }

        /// <summary>
        /// Oculta o restaura los indicadores del HUD nativo al sentarse/levantarse de la mesa:
        /// Al sentarse solo quedan visibles Hora/Día, Nivel de tienda, EXP y Dinero (se ocultan Teléfono, Álbum,
        /// Decorar, avisos de interacción a la izquierda, punto central y el aviso de siguiente día).
        /// </summary>
        public static void ApplySeatedShopHudFilter(bool seated)
        {
            try
            {
                if (!CSingleton<InteractionPlayerController>.InstanceExist()) return;
                var ipc = CSingleton<InteractionPlayerController>.Instance;
                var gui = CSingleton<GameUIScreen>.InstanceExist()
                    ? CSingleton<GameUIScreen>.Instance
                    : UnityEngine.Object.FindFirstObjectByType<GameUIScreen>();

                if (seated)
                {
                    try { CenterDot.SetVisibility(false); } catch { }
                    try { GameUIScreen.HideToolTip(); } catch { }
                    try { GameUIScreen.HideEnterGoNextDayIndicatorVisible(); } catch { }

                    if (gui != null)
                    {
                        if (gui.m_CanvasGrp != null) gui.m_CanvasGrp.alpha = 1f;
                        if (gui.m_PhoneTooltipUI != null && gui.m_PhoneTooltipUI.gameObject.activeSelf)
                            gui.m_PhoneTooltipUI.gameObject.SetActive(false);
                        if (gui.m_AlbumTooltipUI != null && gui.m_AlbumTooltipUI.gameObject.activeSelf)
                            gui.m_AlbumTooltipUI.gameObject.SetActive(false);
                        if (gui.m_DecoInventoryTooltipUI != null && gui.m_DecoInventoryTooltipUI.gameObject.activeSelf)
                            gui.m_DecoInventoryTooltipUI.gameObject.SetActive(false);
                        if (gui.m_GoNextDayTooltipUI != null && gui.m_GoNextDayTooltipUI.gameObject.activeSelf)
                            gui.m_GoNextDayTooltipUI.gameObject.SetActive(false);
                        if (gui.m_PressEnterGoNextDayIndicator != null && gui.m_PressEnterGoNextDayIndicator.activeSelf)
                            gui.m_PressEnterGoNextDayIndicator.SetActive(false);
                        if (gui.m_PressEnterGoNextDayIndicatorText != null && gui.m_PressEnterGoNextDayIndicatorText.activeSelf)
                            gui.m_PressEnterGoNextDayIndicatorText.SetActive(false);
                        if (gui.m_DeodorantBarGrp != null && gui.m_DeodorantBarGrp.activeSelf)
                            gui.m_DeodorantBarGrp.SetActive(false);
                    }

                    if (ipc != null && ipc.m_InputTooltipListDisplay != null)
                    {
                        var itld = ipc.m_InputTooltipListDisplay;
                        itld.ClearTooltip();
                        itld.SetTooltipGroupVisibility(false);
                        if (itld.m_PhoneTooltipUI != null && itld.m_PhoneTooltipUI.gameObject.activeSelf)
                            itld.m_PhoneTooltipUI.gameObject.SetActive(false);
                        if (itld.m_AlbumTooltipUI != null && itld.m_AlbumTooltipUI.gameObject.activeSelf)
                            itld.m_AlbumTooltipUI.gameObject.SetActive(false);
                        if (itld.m_DecoInventoryTooltipUI != null && itld.m_DecoInventoryTooltipUI.gameObject.activeSelf)
                            itld.m_DecoInventoryTooltipUI.gameObject.SetActive(false);
                    }
                }
                else
                {
                    try { CenterDot.SetVisibility(true); } catch { }
                    try { GameUIScreen.SetGameUIVisible(true); } catch { }
                    try { GameUIScreen.ResetToolTipVisibility(); } catch { }
                    try { GameUIScreen.ResetEnterGoNextDayIndicatorVisible(); } catch { }

                    if (gui != null)
                    {
                        if (gui.m_CanvasGrp != null) gui.m_CanvasGrp.alpha = 1f;
                        if (gui.m_PhoneTooltipUI != null) gui.m_PhoneTooltipUI.gameObject.SetActive(true);
                        if (gui.m_AlbumTooltipUI != null) gui.m_AlbumTooltipUI.gameObject.SetActive(true);
                        if (gui.m_DecoInventoryTooltipUI != null) gui.m_DecoInventoryTooltipUI.gameObject.SetActive(true);
                    }

                    if (ipc != null && ipc.m_InputTooltipListDisplay != null)
                    {
                        var itld = ipc.m_InputTooltipListDisplay;
                        itld.SetTooltipGroupVisibility(true);
                        itld.SetCurrentGameState(EGameState.DefaultState);
                        itld.UpdatePhoneAndAlbumTooltip();
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// F8: Aplica las recompensas confirmadas de fin de partida:
        /// - Si gana el jugador: sube la calificación de la tienda (reseña 5★) y otorga experiencia (XP). No da dinero.
        /// - Si gana el NPC: el NPC se queda con la tarifa de la mesa (no se paga a la tienda).
        /// </summary>
        public static MatchRewardSummary ApplyMatchOutcomeRewards(Game game)
        {
            if (game == null || game.Phase != Phase.GameOver) return LastReward;
            if (game.RewardsApplied && LastReward != null) return LastReward;

            game.RewardsApplied = true;
            bool playerWon = game.Winner == game.Players[0];
            float oldRating = 0f;
            try { oldRating = CustomerReviewManager.GetAverageRating(); } catch { }

            var summary = new MatchRewardSummary
            {
                Applied = true,
                PlayerWon = playerWon,
                OldRating = oldRating,
                NewRating = oldRating,
                TableFeeRetainedByNpc = CapturedTableFee
            };

            try
            {
                // Anular el cobro automático de monedas por tiempo de mesa del juego base
                if (CurrentTable != null)
                {
                    CurrentTable.m_CurrentPlayTime = 0f;
                    if (CurrentTable.m_PlayTableFee != null)
                    {
                        for (int i = 0; i < CurrentTable.m_PlayTableFee.Count; i++)
                            CurrentTable.m_PlayTableFee[i] = 0f;
                    }
                }

                // Contabilizar cliente atendido en mesa para estadísticas y logros de la tienda
                CPlayerData.m_GameReportDataCollect.customerPlayed++;
                CPlayerData.m_GameReportDataCollectPermanent.customerPlayed++;
                AchievementManager.OnCustomerFinishPlay(CPlayerData.m_GameReportDataCollectPermanent.customerPlayed);

                if (playerWon)
                {
                    int shopLevel = Mathf.Max(1, CPlayerData.m_ShopLevel);
                    int prizesTaken = Mathf.Clamp(6 - game.Players[0].Prizes.Count, 1, 6);
                    int turnsBonus = Mathf.Clamp(game.Turn, 1, 25) * 4;
                    int xp = 45 + shopLevel * 8 + prizesTaken * 15 + turnsBonus;

                    CEventManager.QueueEvent(new CEventPlayer_AddShopExp(xp, false));
                    summary.ExpGained = xp;

                    // Subir la calificación de la tienda con una reseña de 5 estrellas del cliente
                    if (CurrentOpponent != null && mAddReviewData != null)
                    {
                        mAddReviewData.Invoke(CurrentOpponent, new object[] { ECustomerReviewType.PlaytablePrice, EItemType.None, 2, 100, false });
                    }
                    CustomerReviewManager.AddCustomerReview(ECustomerReviewType.PlaytablePrice, EItemType.None, 2, 100);
                    summary.ReviewStars = 5;
                    summary.NewRating = CustomerReviewManager.GetAverageRating();
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("ApplyMatchOutcomeRewards: " + e.Message);
            }

            LastReward = summary;
            return summary;
        }

        public static void UpdateWaiting()
        {
            if (!IsWaitingForNpc || CurrentTable == null) return;

            int oppSeat = 1 - PlayerSeat;
            if (CurrentTable.m_IsSeatOccupied[oppSeat] && CurrentTable.m_OccupiedCustomer != null &&
                oppSeat < CurrentTable.m_OccupiedCustomer.Count && CurrentTable.m_OccupiedCustomer[oppSeat] != null)
            {
                OnOpponentSeated(CurrentTable.m_OccupiedCustomer[oppSeat]);
                return;
            }

            waitTimer += Time.deltaTime;
            if (waitTimer >= 3f)
            {
                waitTimer = 0f;
                if (!CurrentTable.m_IsSeatBooked[oppSeat] && !CurrentTable.m_IsQueueOccupied[oppSeat])
                {
                    TryInviteShopCustomer();
                }
            }
        }

        static void TryInviteShopCustomer()
        {
            try
            {
                var cm = CSingleton<CustomerManager>.Instance;
                var list = cm?.GetCustomerList();
                if (list == null) return;
                foreach (var c in list)
                {
                    if (c == null || !c.IsActive() || !c.IsInsideShop()) continue;
                    if (c.m_CurrentState == ECustomerState.Idle ||
                        c.m_CurrentState == ECustomerState.WantToBuyItem ||
                        c.m_CurrentState == ECustomerState.WalkToShelf ||
                        c.m_CurrentState == ECustomerState.WantToBuyCard ||
                        c.m_CurrentState == ECustomerState.WalkToCardShelf ||
                        c.m_CurrentState == ECustomerState.WantToPlayGame)
                    {
                        mThinkWantToPlayTable?.Invoke(c, null);
                        break;
                    }
                }
            }
            catch { }
        }

        public static void StandUp(bool finishedMatch, bool playerWon)
        {
            var table = CurrentTable;
            int seat = PlayerSeat;
            var opp = CurrentOpponent;

            CurrentTable = null;
            PlayerSeat = -1;
            CurrentOpponent = null;
            hasBaseTopDownPose = false;
            PolishEffects3D.ClearAll();

            TableView.Instance?.DetachFromTable();

            if (table != null && seat >= 0)
            {
                try
                {
                    if (opp != null)
                    {
                        // F8: Asegurar que el juego base no pague monedas al levantarse (la recompensa ya se aplicó en ApplyMatchOutcomeRewards)
                        table.m_CurrentPlayTime = 0f;
                        if (table.m_PlayTableFee != null)
                        {
                            for (int i = 0; i < table.m_PlayTableFee.Count; i++)
                                table.m_PlayTableFee[i] = 0f;
                        }
                        table.ExitPlayerCardGame(playerWon, !playerWon && !finishedMatch);
                    }
                    else
                    {
                        table.m_IsSeatOccupied[seat] = false;
                        table.m_IsSeatBooked[seat] = false;
                        table.m_IsQueueOccupied[seat] = false;
                        table.m_IsPlayerSeat[seat] = false;
                        fIsPlayerOccupied?.SetValue(table, false);
                        fHasStartPlayerPlayCard?.SetValue(table, false);
                        if (table.m_TableGameItemSetList != null && seat < table.m_TableGameItemSetList.Count && table.m_TableGameItemSetList[seat] != null)
                            table.m_TableGameItemSetList[seat].gameObject.SetActive(false);
                        table.m_CurrentPlayerCount = (table.m_IsSeatOccupied[0] ? 1 : 0) + (table.m_IsSeatOccupied[1] ? 1 : 0);
                    }
                }
                catch { }
            }

            var ipc = CSingleton<InteractionPlayerController>.Instance;
            if (ipc != null)
            {
                ipc.SetCameraWorldPositionTarget(null);
                if (ipc.m_CameraFOVController != null) ipc.m_CameraFOVController.StopLerpFOV();
                ipc.m_IsPlayingTopDownGameMode = false;
                ipc.StopAimLookAt();
                ipc.StopCameraLerp();
                Transform standTransform = table != null ? table.GetStandLoc(Math.Max(0, seat), true) : null;
                Vector3 standPos = standTransform != null ? standTransform.position : savedStandPos;
                InteractionPlayerController.SetPlayerPos(standPos);
                try { ipc.OnExitPlayTableGameMode(); } catch { }
                fIsPlayTableGameMode?.SetValue(ipc, false);
                ipc.m_IsPlayingTopDownGameMode = false;
                if (ipc.m_PlayerRigidbody != null) ipc.m_PlayerRigidbody.isKinematic = false;
                if (ipc.m_PlayerCollider != null) ipc.m_PlayerCollider.isTrigger = false;
                if (ipc.m_WalkerCtrl != null) ipc.m_WalkerCtrl.SetStopMovement(false);
                try { mSetCurrentGameState?.Invoke(ipc, new object[] { EGameState.DefaultState }); } catch { }
                try { ipc.ExitUIMode(); } catch { }
            }
            ApplySeatedShopHudFilter(false);
        }
    }

    public static class PlayTablePatches
    {
        [HarmonyPatch(typeof(InteractablePlayTable), nameof(InteractablePlayTable.OnRightMouseButtonUp))]
        [HarmonyPrefix]
        public static bool OnRightMouseButtonUp_Prefix(InteractablePlayTable __instance)
        {
            PlayTableSession.SitAtTable(__instance);
            return false;
        }

        [HarmonyPatch(typeof(InteractionPlayerController), "OnEnterPhoneScreenMode")]
        [HarmonyPrefix]
        public static bool BlockPhoneWhileSeated_Prefix() => PlayTableSession.CurrentTable == null;

        [HarmonyPatch(typeof(InteractionPlayerController), "EnterViewCardAlbumMode")]
        [HarmonyPrefix]
        public static bool BlockAlbumWhileSeated_Prefix() => PlayTableSession.CurrentTable == null;

        [HarmonyPatch(typeof(InteractionPlayerController), "ShowDecoInventoryScreen")]
        [HarmonyPrefix]
        public static bool BlockDecoWhileSeated_Prefix() => PlayTableSession.CurrentTable == null;

        [HarmonyPatch(typeof(InteractionPlayerController), "ShowGoNextDayScreen")]
        [HarmonyPrefix]
        public static bool BlockNextDayWhileSeated_Prefix() => PlayTableSession.CurrentTable == null;

        [HarmonyPatch(typeof(InteractablePlayTable), nameof(InteractablePlayTable.CustomerHasReached))]
        [HarmonyFinalizer]
        public static Exception CustomerHasReached_Finalizer(Exception __exception, InteractablePlayTable __instance, Customer customer, int seatIndex)
        {
            if (PlayTableSession.CurrentTable == __instance)
            {
                if (seatIndex != PlayTableSession.PlayerSeat && customer != null)
                {
                    PlayTableSession.OnOpponentSeated(customer);
                }
                return null;
            }
            return __exception;
        }

        [HarmonyPatch(typeof(InteractablePlayTable), "StopTableGame")]
        [HarmonyPrefix]
        public static void StopTableGame_Prefix(InteractablePlayTable __instance)
        {
            if (PlayTableSession.CurrentTable == __instance)
            {
                GameUI.Instance?.CloseFromTableStop();
            }
        }
    }

    /// <summary>
    /// Parches sobre la Mesa de Creación (WorkbenchUIScreen) para mostrar el botón independiente 'PokeDeck Builder'
    /// sin reemplazar ni interferir con el creador de mazos del juego base.
    /// </summary>
    public static class WorkbenchPatches
    {
        [HarmonyPatch(typeof(WorkbenchUIScreen), nameof(WorkbenchUIScreen.OpenScreen))]
        [HarmonyPostfix]
        public static void OpenScreen_Postfix(WorkbenchUIScreen __instance)
        {
            if (DeckBuilderUI.Instance != null)
                DeckBuilderUI.Instance.OnWorkbenchScreenOpened(__instance);
        }

        [HarmonyPatch(typeof(WorkbenchUIScreen), "OnPressEditDeckButton")]
        [HarmonyPostfix]
        public static void OnPressEditDeckButton_Postfix()
        {
            if (DeckBuilderUI.Instance != null)
                DeckBuilderUI.Instance.SetBaseWorkbenchSubscreenOpen(true);
        }

        [HarmonyPatch(typeof(WorkbenchUIScreen), "OnCloseEditDeckScreen")]
        [HarmonyPostfix]
        public static void OnCloseEditDeckScreen_Postfix()
        {
            if (DeckBuilderUI.Instance != null)
                DeckBuilderUI.Instance.SetBaseWorkbenchSubscreenOpen(false);
        }

        [HarmonyPatch(typeof(WorkbenchUIScreen), "OnPressRulebookButton")]
        [HarmonyPostfix]
        public static void OnPressRulebookButton_Postfix()
        {
            if (DeckBuilderUI.Instance != null)
                DeckBuilderUI.Instance.SetBaseWorkbenchSubscreenOpen(true);
        }

        [HarmonyPatch(typeof(WorkbenchUIScreen), "OnCloseRulebookScreen")]
        [HarmonyPostfix]
        public static void OnCloseRulebookScreen_Postfix()
        {
            if (DeckBuilderUI.Instance != null)
                DeckBuilderUI.Instance.SetBaseWorkbenchSubscreenOpen(false);
        }

        [HarmonyPatch(typeof(WorkbenchUIScreen), nameof(WorkbenchUIScreen.CloseScreen))]
        [HarmonyPrefix]
        public static bool CloseScreen_Prefix()
        {
            if (DeckBuilderUI.Instance != null && DeckBuilderUI.Instance.IsOpen)
                return false;
            return true;
        }

        [HarmonyPatch(typeof(WorkbenchUIScreen), nameof(WorkbenchUIScreen.CloseScreen))]
        [HarmonyPostfix]
        public static void CloseScreen_Postfix()
        {
            if (DeckBuilderUI.Instance != null)
                DeckBuilderUI.Instance.OnWorkbenchScreenClosed();
        }
    }

    /// <summary>
    /// Parche de compatibilidad automática con PlayTableVariety:
    /// 1) Evita que LoadTextureFromBundle bloquee el AssetBundle en EPL de forma asíncrona antes de llamar a AssetBundle.LoadFromFile (lo que generaba atlas en negro de 20,547 bytes).
    /// 2) Asegura que los materiales de atlas creados en el menú principal sin MAT_PlayCard cargado usen iluminación limpia y no se vean negros en las mesas de los NPC.
    /// </summary>
    public static class PlayTableVarietyCompat
    {
        static FieldInfo fOpenedBakeBundles;

        public static void Apply(Harmony h)
        {
            try
            {
                var ptvType = AccessTools.TypeByName("PlayTableVariety.PlayTableVarietyPlugin");
                if (ptvType == null) return;

                fOpenedBakeBundles = AccessTools.Field(ptvType, "s_OpenedBakeBundles");

                var mLoadTex = AccessTools.Method(ptvType, "LoadTextureFromBundle");
                if (mLoadTex != null)
                {
                    h.Patch(mLoadTex, prefix: new HarmonyMethod(typeof(PlayTableVarietyCompat), nameof(LoadTextureFromBundle_Prefix)));
                }

                var mBuildMat = AccessTools.Method(ptvType, "BuildAtlasMaterial");
                if (mBuildMat != null)
                {
                    h.Patch(mBuildMat, postfix: new HarmonyMethod(typeof(PlayTableVarietyCompat), nameof(BuildAtlasMaterial_Postfix)));
                }

                Plugin.Log?.LogInfo("Compatibilidad con PlayTableVariety activada (corrección de carga de bundles y materiales en mesas NPC).");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("PlayTableVarietyCompat: " + e.Message);
            }
        }

        public static bool LoadTextureFromBundle_Prefix(string bundlePath, string assetName, ref Texture2D __result)
        {
            __result = null;
            if (string.IsNullOrEmpty(bundlePath) || string.IsNullOrEmpty(assetName)) return false;

            try
            {
                var dict = fOpenedBakeBundles?.GetValue(null) as System.Collections.Generic.Dictionary<string, AssetBundle>;
                AssetBundle bundle = null;

                if (dict != null && dict.TryGetValue(bundlePath, out var cached) && cached != null)
                {
                    bundle = cached;
                }
                else
                {
                    string fileNoExt = System.IO.Path.GetFileNameWithoutExtension(bundlePath);
                    string fileName = System.IO.Path.GetFileName(bundlePath);
                    foreach (var b in AssetBundle.GetAllLoadedAssetBundles())
                    {
                        if (b == null) continue;
                        if (string.Equals(b.name, fileNoExt, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(b.name, fileName, StringComparison.OrdinalIgnoreCase))
                        {
                            bundle = b;
                            break;
                        }
                    }

                    if (bundle == null && System.IO.File.Exists(bundlePath))
                    {
                        bundle = AssetBundle.LoadFromFile(bundlePath);
                        if (bundle != null && dict != null)
                            dict[bundlePath] = bundle;
                    }
                }

                if (bundle == null) return false;

                var sp = bundle.LoadAsset<Sprite>(assetName);
                if (sp != null && sp.texture != null)
                {
                    __result = BlitReadable(sp.texture, sp.rect);
                    return false;
                }

                var tex = bundle.LoadAsset<Texture2D>(assetName);
                if (tex != null)
                {
                    __result = BlitReadable(tex, new Rect(0, 0, tex.width, tex.height));
                    return false;
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning($"PlayTableVarietyCompat.LoadTextureFromBundle [{assetName}]: {e.Message}");
            }
            return false;
        }

        static Texture2D BlitReadable(Texture2D src, Rect rect)
        {
            int w = Mathf.Max(1, Mathf.RoundToInt(rect.width));
            int h = Mathf.RoundToInt(rect.height) > 0 ? Mathf.RoundToInt(rect.height) : src.height;
            RenderTexture rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.Default, RenderTextureReadWrite.Default);
            Graphics.Blit(src, rt);
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            var readable = new Texture2D(w, h, TextureFormat.RGBA32, false);
            int rx = Mathf.Clamp(Mathf.RoundToInt(rect.x), 0, Math.Max(0, src.width - w));
            int ry = Mathf.Clamp(src.height - Mathf.RoundToInt(rect.y) - h, 0, Math.Max(0, src.height - h));
            readable.ReadPixels(new Rect(rx, ry, w, h), 0, 0);
            readable.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            return readable;
        }

        public static void BuildAtlasMaterial_Postfix(ref Material __result)
        {
            if (__result == null) return;
            try
            {
                Texture mainTex = __result.mainTexture;
                // Si el material se creó en la pantalla de título con Toony Colors Pro 2 sin rampa inicializada, asegurar colores claros o shader limpio
                if (__result.HasProperty("_Color")) __result.SetColor("_Color", Color.white);
                if (__result.HasProperty("_BaseColor")) __result.SetColor("_BaseColor", Color.white);
                if (__result.HasProperty("_HColor")) __result.SetColor("_HColor", Color.white);
                if (__result.HasProperty("_SColor")) __result.SetColor("_SColor", new Color(0.78f, 0.78f, 0.82f, 1f));
                if (mainTex != null) __result.mainTexture = mainTex;
            }
            catch { }
        }
    }
}
