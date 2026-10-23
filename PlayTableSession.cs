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

            // Validar mazo activo ANTES de sentar al jugador en la mesa (evita bloquear la cámara si el mazo es inválido)
            SpriteCache.EnsureInitialized();
            SpriteCache.PreloadFromEpl();
            Collection.Invalidate();

            var active = DeckStore.Active;
            if (active == null)
            {
                GameUI.PlayClickSound(2);
                GameUI.Instance?.ShowToast("No tienes un mazo activo. Abre la Mesa de Creación (Workbench) para crearlo.", 5.5f);
                return;
            }
            var format = FormatRegistry.Get(active.Format) ?? FormatRegistry.Default;
            var missing = new System.Collections.Generic.List<string>();
            var playerDeck = active.Resolve(missing);
            var errors = DeckValidator.Validate(playerDeck, format);
            if (missing.Count > 0) errors.Insert(0, $"Cartas desconocidas: {string.Join(", ", missing)}");
            if (errors.Count > 0)
            {
                GameUI.PlayClickSound(2);
                GameUI.Instance?.ShowToast($"Tu mazo '{active.Name}' no es válido: {errors[0]} — Edítalo en la Workbench.", 6.5f);
                return;
            }

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

            // F1: Montar el Playmat 3D y todos los anclajes sobre la superficie real de la mesa
            TableView.Instance?.AttachToTable(table, seat);

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

            TableView.Instance?.DetachFromTable();

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
