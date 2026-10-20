using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace PokeTCGPlay
{
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

        static float waitTimer;
        static Vector3 savedStandPos;
        static Transform topDownCamLoc;
        static Transform topDownLookTarget;

        static readonly FieldInfo fHasStartPlayerPlayCard = AccessTools.Field(typeof(InteractablePlayTable), "m_HasStartPlayerPlayCard");
        static readonly FieldInfo fIsPlayerOccupied = AccessTools.Field(typeof(InteractablePlayTable), "m_IsPlayerOccupied");
        static readonly MethodInfo mThinkWantToPlayTable = AccessTools.Method(typeof(Customer), "ThinkWantToPlayTable");

        public static void SitAtTable(InteractablePlayTable table)
        {
            if (table == null || CurrentTable != null) return;
            if (DeckBuilderUI.Instance != null && DeckBuilderUI.Instance.IsOpen) return;

            var ipc = CSingleton<InteractionPlayerController>.Instance;
            if (ipc == null) return;

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
                GameUI.Instance?.ShowToast("Esta mesa ya está ocupada por 2 clientes.");
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

            // Mostrar el tapete en la mesa 3D con la textura de nuestro Playmat
            ApplyPlaymatOn3DTable(table, seat);

            Transform sitLoc = table.GetSitLoc(seat);
            Transform oppSitLoc = table.GetSitLoc(1 - seat);
            ipc.StopCameraLerp();
            ipc.EnterUIMode();
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

            Vector3 sitA = table.GetSitLoc(seat).position;
            Vector3 sitB = table.GetSitLoc(1 - seat).position;
            Vector3 center = (sitA + sitB) * 0.5f;
            Vector3 toCenter = (center - sitA).normalized;

            // Posicionar la cámara justo encima de la mesa mirando verticalmente hacia el centro de la mesa física
            Vector3 tableSurface = new Vector3(center.x, table.transform.position.y + 0.78f, center.z);
            topDownCamLoc.position = tableSurface + Vector3.up * 1.42f - toCenter * 0.08f;
            topDownLookTarget.position = tableSurface + toCenter * 0.02f;

            ipc.SetCameraWorldPositionTarget(topDownCamLoc);
            ipc.StartAimLookAt(topDownLookTarget, 8f, 0f);
            ipc.m_IsPlayingTopDownGameMode = true;
            if (ipc.m_CameraFOVController != null)
                ipc.m_CameraFOVController.StartLerpToFOV(42f);
        }

        public static void OnOpponentSeated(Customer npc)
        {
            if (CurrentTable == null || npc == null) return;
            CurrentOpponent = npc;

            CurrentTable.m_HasStartPlay = true;
            fHasStartPlayerPlayCard?.SetValue(CurrentTable, true);
            fIsPlayerOccupied?.SetValue(CurrentTable, true);
            CurrentTable.m_CurrentPlayTime = 0f;
            CurrentTable.m_CurrentPlayTimeMax = 99999f;
            npc.PlayTableGameStarted();

            ApplyPlaymatOn3DTable(CurrentTable, 1 - PlayerSeat);
            EnterTopDownTableCamera(CurrentTable, PlayerSeat);

            GameUI.Instance?.StartTableMatch(npc);
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

            if (table != null && seat >= 0)
            {
                if (opp != null)
                {
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
                if (ipc.m_PlayerRigidbody != null) ipc.m_PlayerRigidbody.isKinematic = false;
                if (ipc.m_PlayerCollider != null) ipc.m_PlayerCollider.isTrigger = false;
                if (ipc.m_WalkerCtrl != null) ipc.m_WalkerCtrl.SetStopMovement(false);
                ipc.ExitUIMode();
            }
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

        [HarmonyPatch(typeof(InteractablePlayTable), nameof(InteractablePlayTable.CustomerHasReached))]
        [HarmonyPostfix]
        public static void CustomerHasReached_Postfix(InteractablePlayTable __instance, Customer customer, int seatIndex)
        {
            if (PlayTableSession.CurrentTable == __instance && seatIndex != PlayTableSession.PlayerSeat && customer != null)
            {
                PlayTableSession.OnOpponentSeated(customer);
            }
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
    /// Parches sobre la Mesa de Creación (InteractableWorkbench / WorkbenchUIScreen) para abrir el Creador de Mazos Pokémon TCG.
    /// </summary>
    public static class WorkbenchPatches
    {
        [HarmonyPatch(typeof(InteractableWorkbench), nameof(InteractableWorkbench.OnRightMouseButtonUp))]
        [HarmonyPrefix]
        public static bool WorkbenchRightClick_Prefix(InteractableWorkbench __instance)
        {
            if (PlayTableSession.CurrentTable == null && DeckBuilderUI.Instance != null)
            {
                DeckBuilderUI.Instance.OpenFromWorkbench(__instance);
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(WorkbenchUIScreen), nameof(WorkbenchUIScreen.OnPressEditDeckButton))]
        [HarmonyPrefix]
        public static bool OnPressEditDeckButton_Prefix(WorkbenchUIScreen __instance)
        {
            if (DeckBuilderUI.Instance != null)
            {
                DeckBuilderUI.Instance.OpenFromWorkbenchScreen(__instance);
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(WorkbenchUIScreen), nameof(WorkbenchUIScreen.OpenScreen))]
        [HarmonyPostfix]
        public static void OpenScreen_Postfix()
        {
            if (DeckBuilderUI.Instance != null)
                DeckBuilderUI.Instance.OnWorkbenchScreenOpened();
        }

        [HarmonyPatch(typeof(WorkbenchUIScreen), nameof(WorkbenchUIScreen.CloseScreen))]
        [HarmonyPostfix]
        public static void CloseScreen_Postfix()
        {
            if (DeckBuilderUI.Instance != null)
                DeckBuilderUI.Instance.OnWorkbenchScreenClosed();
        }
    }
}
