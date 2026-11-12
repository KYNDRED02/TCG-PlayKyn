// TCG-PlayKyn: 3D playmat model, card view pooling and board state synchronization
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace TCGPlayKyn
{
    public enum TableSlotKind
    {
        Active,
        Bench,
        Discard,
        Deck,
        PlayfieldCenter
    }

    /// <summary>
    /// F4: Identificador y collider de una casilla 3D del tapete para Raycast e interacción Drag & Drop.
    /// </summary>
    public class TableSlot3D : MonoBehaviour
    {
        public TableSlotKind Kind;
        public int PlayerIndex; // 0 = Jugador, 1 = NPC
        public int BenchIndex = -1;
        public float Scale = 1f;
        public Transform Anchor;
    }

    /// <summary>
    /// F1–F4: Vista 3D de la mesa, Playmat físico, Mano 3D en abanico y detección de Raycast 3D sobre InteractablePlayTable.
    /// </summary>
    /// <summary>
    /// Controlador principal de la vista 3D de la mesa de juego.
    /// Administra el tapete (Playmat), la mano en abanico 3D y la sincronización visual de cartas.
    /// Las casillas/anclajes están en TableView.Anchors.cs y la Pokédex 3D en TableView.Pokedex.cs.
    /// </summary>
    public partial class TableView : MonoBehaviour
    {
        public static TableView Instance { get; private set; }

        // Dimensiones físicas del tapete de 2 jugadores sobre la mesa (en metros del mundo 3D)
        // Proporción exacta de playmat.png: 840 x 868 (0.9677 : 1)
        public const float MatWidth = 0.72f;
        public const float MatLength = 0.744f;
        public const float MatThickness = 0.004f;

        // Tamaño estándar de una carta Pokémon en el mundo 3D (proporción 63:88)
        public const float CardWorldWidth = 0.086f;
        public const float CardWorldLength = 0.120f;

        public InteractablePlayTable Table { get; private set; }
        public int PlayerSeat { get; private set; }

        public Transform PlaymatRoot { get; private set; }
        MeshRenderer matTopRenderer;
        MeshRenderer matBaseRenderer;

        // [Los anclajes 3D (Active, Bench, Deck, Prize, Stadium, Coin) se definen en TableView.Anchors.cs]
        CardViewPool cardPool;
        PhysicalCardBlockPool physicalBlockPool;
        readonly DeckStack3D[] deckStacks = new DeckStack3D[2];
        Mesh sharedQuadMesh;
        Mesh sharedCubeMesh;
        Material matTopMaterial;
        Material matEdgeMaterial;

        // Respaldo exclusivo de la mesa actual para cambiar el tope de la baraja a Pokémon y ocultar cartas sueltas sin afectar otras mesas
        struct SavedTablePropState
        {
            public GameObject TargetGo;
            public bool WasActive;
            public MeshRenderer Renderer;
            public Material[] OrigSharedMaterials;
        }
        readonly List<SavedTablePropState> savedTableProps = new List<SavedTablePropState>();
        NativeDeckMeshSpec nativeDeckSpec;
        Material nativeDeckTemplateMat;
        Material pokemonDeckInstanceMat;
        Texture2D pokemonDeckAtlasTex;
        Texture2D lastBuiltDeckAtlasSource;

        // F4: Mano 3D en abanico, Carta Arrastrada en 3D y Raycast geométrico exacto
        // [tableSlots se define en TableView.Anchors.cs]
        readonly float[] handLiftAnims = new float[64];
        readonly float[] handSpreadAnims = new float[64];
        float handDeployAnim;
        float handTrayLiftAnim;
        CardView dragCardView;
        Vector3 smoothDragWorldPos;
        Vector3 prevDragWorldPos;
        bool dragPosInitialized;

        // F5: Inspección 3D de Carta (elevación física 3D hacia cámara con giro interactivo y telón 3D oscurecido detrás)
        CardView inspectCardView;
        GameObject inspectBackdropGo;
        Material inspectBackdropMat;

        // F6: Fichas 3D de Contadores de Daño (10/50/100), Marcadores 3D de Estado, Tinte animado y Textos Flotantes
        StatusCounters3D statusCounters;

        // F7: Moneda 3D cilíndrica (Pikachu / Pokéball) con curva parabólica, rebote y vibración de canto
        public Coin3D Coin { get; private set; }

        public bool IsBoard3DReady => PlaymatRoot != null && cardPool != null;

        // [PokedexSession y variables de Pokédex 3D se definen en TableView.Pokedex.cs]
        public static Camera GetActiveCamera()
        {
            try
            {
                var ipc = CSingleton<InteractionPlayerController>.Instance;
                if (ipc != null && ipc.m_Cam != null)
                    return ipc.m_Cam;
            }
            catch { }
            return Camera.main;
        }

        void Awake()
        {
            Instance = this;
        }

        void OnDestroy()
        {
            Cleanup3D();
            if (Instance == this) Instance = null;
        }

        /// <summary>
        /// Monta el tapete 3D y todos sus anclajes sobre la mesa física donde se sentó el jugador.
        /// </summary>
        public void AttachToTable(InteractablePlayTable table, int playerSeat)
        {
            Cleanup3D();
            Table = table;
            PlayerSeat = playerSeat;
            if (table == null) return;

            SpriteCache.EnsureInitialized();
            SpriteCache.PreloadFromEpl();

            // Activar los sets de la mesa para conservar las DeckBoxes decorativas, pero ocultar los 2 tapetes pequeños originales
            float surfaceY = table.transform.position.y + 0.785f;
            bool foundBounds = false;
            float sumY = 0f;
            int countY = 0;

            if (table.m_TableGameItemSetList != null)
            {
                for (int i = 0; i < table.m_TableGameItemSetList.Count; i++)
                {
                    var set = table.m_TableGameItemSetList[i];
                    if (set == null) continue;
                    try
                    {
                        set.RandomizeSetup();
                        if (table.m_TableGameItemSetDataList != null && i < table.m_TableGameItemSetDataList.Count)
                            table.m_TableGameItemSetDataList[i] = set.m_TableGameItemSetData;
                    }
                    catch { }
                    set.gameObject.SetActive(true);
                    if (set.m_PlayMatMesh != null)
                    {
                        set.m_PlayMatMesh.gameObject.SetActive(true);
                        Bounds b = set.m_PlayMatMesh.bounds;
                        if (b.size.sqrMagnitude > 0.0001f)
                        {
                            sumY += b.max.y;
                            countY++;
                            foundBounds = true;
                        }
                        // Ocultamos el tapete individual del juego base para que luzca nuestro Playmat 3D completo
                        set.m_PlayMatMesh.gameObject.SetActive(false);
                    }
                    if (set.m_ComicBookMesh != null)
                        set.m_ComicBookMesh.gameObject.SetActive(false);
                }
            }
            if (foundBounds && countY > 0)
                surfaceY = sumY / countY;

            // Compatibilidad con FurnitureOverhaul (CC_PlayTableLook tiene marco de latón y fieltro en y = +0.783..0.785)
            if (table.transform.Find("CC_PlayTableLook") != null)
                surfaceY = Mathf.Max(surfaceY, table.transform.position.y + 0.785f);

            Vector3 sitPlayer = table.GetSitLoc(playerSeat).position;
            Vector3 sitOpp = table.GetSitLoc(1 - playerSeat).position;
            Vector3 center = (sitPlayer + sitOpp) * 0.5f;
            Vector3 forward = sitOpp - sitPlayer;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.001f) forward = table.transform.forward;
            forward.Normalize();

            Vector3 matCenter = new Vector3(center.x, surfaceY + 0.002f, center.z);
            Quaternion matRot = Quaternion.LookRotation(forward, Vector3.up);

            BuildPlaymat3D(matCenter, matRot);
            CaptureAndPrepareCurrentTableProps();
            BuildAnchors();
        }

        void SavePropOnce(GameObject go, MeshRenderer mr = null)
        {
            if (go == null) return;
            for (int i = 0; i < savedTableProps.Count; i++)
            {
                if (savedTableProps[i].TargetGo == go) return;
            }
            savedTableProps.Add(new SavedTablePropState
            {
                TargetGo = go,
                WasActive = go.activeSelf,
                Renderer = mr,
                OrigSharedMaterials = mr != null ? mr.sharedMaterials : null
            });
        }

        void CaptureAndPrepareCurrentTableProps()
        {
            if (Table == null || Table.m_TableGameItemSetList == null || PlaymatRoot == null) return;

            Mesh foundDeckMesh = null;
            Material foundTemplateMat = null;
            Quaternion foundRelRot = Quaternion.identity;
            Vector3 foundLossyScale = Vector3.one;

            // Buscar primero en el asiento del jugador para capturar la rotación exacta de CardDeck
            int seatCount = Table.m_TableGameItemSetList.Count;
            for (int pass = 0; pass < seatCount; pass++)
            {
                int seatIdx = pass == 0 ? Mathf.Clamp(PlayerSeat, 0, seatCount - 1) : (pass == 1 ? Mathf.Clamp(1 - PlayerSeat, 0, seatCount - 1) : pass);
                var set = Table.m_TableGameItemSetList[seatIdx];
                if (set == null) continue;

                var renderers = set.GetComponentsInChildren<MeshRenderer>(true);
                for (int r = 0; r < renderers.Length; r++)
                {
                    var mr = renderers[r];
                    if (mr == null) continue;
                    string rName = mr.name ?? "";
                    if (rName.StartsWith("CardDeck", StringComparison.OrdinalIgnoreCase))
                    {
                        var mf = mr.GetComponent<MeshFilter>();
                        if (foundDeckMesh == null && mf != null && mf.sharedMesh != null && mr.sharedMaterial != null)
                        {
                            foundDeckMesh = mf.sharedMesh;
                            foundTemplateMat = mr.sharedMaterial;

                            Quaternion seatRefRot = seatIdx == PlayerSeat
                                ? PlaymatRoot.rotation
                                : PlaymatRoot.rotation * Quaternion.Euler(0f, 180f, 0f);
                            Quaternion rawRel = Quaternion.Inverse(seatRefRot) * mr.transform.rotation;
                            Vector3 eul = rawRel.eulerAngles;
                            foundRelRot = Quaternion.Euler(
                                Mathf.Round(eul.x / 90f) * 90f,
                                Mathf.Round(eul.y / 90f) * 90f,
                                Mathf.Round(eul.z / 90f) * 90f);

                            Vector3 ls = mr.transform.lossyScale;
                            foundLossyScale = new Vector3(Mathf.Abs(ls.x), Mathf.Abs(ls.y), Mathf.Abs(ls.z));
                            if (foundLossyScale.x < 0.0001f) foundLossyScale.x = 1f;
                            if (foundLossyScale.y < 0.0001f) foundLossyScale.y = 1f;
                            if (foundLossyScale.z < 0.0001f) foundLossyScale.z = 1f;
                        }
                    }
                }
            }

            if (foundDeckMesh != null && foundTemplateMat != null)
            {
                nativeDeckTemplateMat = foundTemplateMat;
                pokemonDeckInstanceMat = new Material(nativeDeckTemplateMat)
                {
                    name = "PokeTCG_MatchOnly_PlayCardMat"
                };

                Bounds mb = foundDeckMesh.bounds;
                Vector3 c0 = mb.min;
                Vector3 c1 = mb.max;
                Matrix4x4 trs = Matrix4x4.TRS(Vector3.zero, foundRelRot, foundLossyScale);
                float minX = float.MaxValue, maxX = float.MinValue;
                float minY = float.MaxValue, maxY = float.MinValue;
                float minZ = float.MaxValue, maxZ = float.MinValue;

                for (int ix = 0; ix < 2; ix++)
                {
                    for (int iy = 0; iy < 2; iy++)
                    {
                        for (int iz = 0; iz < 2; iz++)
                        {
                            Vector3 corner = new Vector3(
                                ix == 0 ? c0.x : c1.x,
                                iy == 0 ? c0.y : c1.y,
                                iz == 0 ? c0.z : c1.z);
                            Vector3 p = trs.MultiplyPoint3x4(corner);
                            if (p.x < minX) minX = p.x;
                            if (p.x > maxX) maxX = p.x;
                            if (p.y < minY) minY = p.y;
                            if (p.y > maxY) maxY = p.y;
                            if (p.z < minZ) minZ = p.z;
                            if (p.z > maxZ) maxZ = p.z;
                        }
                    }
                }

                nativeDeckSpec = new NativeDeckMeshSpec
                {
                    Mesh = foundDeckMesh,
                    InstanceMaterial = pokemonDeckInstanceMat,
                    LocalRotation = foundRelRot,
                    NativeLossyScale = foundLossyScale,
                    PivotOffset = new Vector3(-(minX + maxX) * 0.5f, -minY, -(minZ + maxZ) * 0.5f),
                    NativeWidth = Mathf.Max(0.01f, maxX - minX),
                    NativeHeight = Mathf.Max(0.002f, maxY - minY),
                    NativeLength = Mathf.Max(0.01f, maxZ - minZ)
                };

                EnsurePokemonDeckAtlasUpToDate();
            }

            HideDefaultTableMats();
        }

        void EnsurePokemonDeckAtlasUpToDate()
        {
            if (pokemonDeckInstanceMat == null) return;
            var cb = SpriteCache.GetCardBack();
            if (cb == null || cb.texture == null) return;
            if (lastBuiltDeckAtlasSource == cb.texture && pokemonDeckAtlasTex != null) return;

            lastBuiltDeckAtlasSource = cb.texture;

            const int AtlasSize = 1024;
            const int SlotW = 242;
            const int SlotH = 330;
            const int CornerR = 14;
            Color32 edgeDark = new Color32(30, 24, 32, 255);

            Texture2D readable = null;
            bool createdTemp = false;
            try
            {
                readable = cb.texture.isReadable ? cb.texture : SpriteCache.MakeReadableCopy(cb.texture);
                createdTemp = readable != null && !ReferenceEquals(readable, cb.texture);
                if (readable == null) return;

                Color32[] srcPixels = readable.GetPixels32();
                int srcTexW = readable.width;
                int srcTexH = readable.height;
                Rect sr = cb.textureRect;

                // Construir el bloque 242x330 del reverso oficial de Pokémon con esquinas redondeadas suaves
                Color32[] cardStamp = new Color32[SlotW * SlotH];
                for (int y = 0; y < SlotH; y++)
                {
                    float v = (y + 0.5f) / SlotH;
                    float sy = Mathf.Clamp(sr.y + v * sr.height, 0f, srcTexH - 1f);
                    int y0 = Mathf.FloorToInt(sy);
                    int y1 = Mathf.Min(srcTexH - 1, y0 + 1);
                    float fy = sy - y0;

                    for (int x = 0; x < SlotW; x++)
                    {
                        float cornerDist = CalcRoundedCornerDist(x, y, SlotW, SlotH, CornerR);
                        if (cornerDist >= 1f)
                        {
                            cardStamp[y * SlotW + x] = edgeDark;
                            continue;
                        }

                        float u = (x + 0.5f) / SlotW;
                        float sx = Mathf.Clamp(sr.x + u * sr.width, 0f, srcTexW - 1f);
                        int x0 = Mathf.FloorToInt(sx);
                        int x1 = Mathf.Min(srcTexW - 1, x0 + 1);
                        float fx = sx - x0;

                        Color32 c00 = srcPixels[y0 * srcTexW + x0];
                        Color32 c10 = srcPixels[y0 * srcTexW + x1];
                        Color32 c01 = srcPixels[y1 * srcTexW + x0];
                        Color32 c11 = srcPixels[y1 * srcTexW + x1];

                        byte r = (byte)Mathf.RoundToInt(Mathf.Lerp(Mathf.Lerp(c00.r, c10.r, fx), Mathf.Lerp(c01.r, c11.r, fx), fy));
                        byte g = (byte)Mathf.RoundToInt(Mathf.Lerp(Mathf.Lerp(c00.g, c10.g, fx), Mathf.Lerp(c01.g, c11.g, fx), fy));
                        byte b = (byte)Mathf.RoundToInt(Mathf.Lerp(Mathf.Lerp(c00.b, c10.b, fx), Mathf.Lerp(c01.b, c11.b, fx), fy));

                        if (cornerDist > 0f)
                        {
                            r = (byte)Mathf.RoundToInt(Mathf.Lerp(r, edgeDark.r, cornerDist));
                            g = (byte)Mathf.RoundToInt(Mathf.Lerp(g, edgeDark.g, cornerDist));
                            b = (byte)Mathf.RoundToInt(Mathf.Lerp(b, edgeDark.b, cornerDist));
                        }
                        cardStamp[y * SlotW + x] = new Color32(r, g, b, 255);
                    }
                }

                Color32[] atlasPixels = new Color32[AtlasSize * AtlasSize];
                for (int i = 0; i < atlasPixels.Length; i++)
                    atlasPixels[i] = edgeDark;

                // Estampar el reverso de Pokémon en las 12 celdas del atlas 4x3 de MAT_PlayCard (incluyendo la celda superior izquierda de CardDeck)
                for (int row = 0; row < 3; row++)
                {
                    int baseY = row == 2 ? 682 : (row == 1 ? 341 : 0);
                    int cellH = row == 1 ? 341 : 342;
                    int destY = baseY + (cellH - SlotH) / 2;
                    for (int col = 0; col < 4; col++)
                    {
                        int destX = col * 256 + 7;
                        for (int sy = 0; sy < SlotH; sy++)
                        {
                            Array.Copy(cardStamp, sy * SlotW, atlasPixels, (destY + sy) * AtlasSize + destX, SlotW);
                        }
                    }
                }

                if (pokemonDeckAtlasTex != null)
                    Destroy(pokemonDeckAtlasTex);

                pokemonDeckAtlasTex = new Texture2D(AtlasSize, AtlasSize, TextureFormat.RGBA32, true)
                {
                    name = "PokeTCG_MatchOnly_DeckAtlas",
                    filterMode = FilterMode.Trilinear,
                    anisoLevel = 8,
                    wrapMode = TextureWrapMode.Clamp
                };
                pokemonDeckAtlasTex.SetPixels32(atlasPixels);
                pokemonDeckAtlasTex.Apply(true, false);

                pokemonDeckInstanceMat.mainTexture = pokemonDeckAtlasTex;
                if (pokemonDeckInstanceMat.HasProperty("_MainTex"))
                    pokemonDeckInstanceMat.SetTexture("_MainTex", pokemonDeckAtlasTex);
                if (pokemonDeckInstanceMat.HasProperty("_BaseMap"))
                    pokemonDeckInstanceMat.SetTexture("_BaseMap", pokemonDeckAtlasTex);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("EnsurePokemonDeckAtlasUpToDate: " + e.Message);
            }
            finally
            {
                if (createdTemp && readable != null)
                    Destroy(readable);
            }
        }

        static float CalcRoundedCornerDist(int x, int y, int w, int h, int r)
        {
            int cx = x < r ? (r - x) : (x >= w - r ? (x - (w - r - 1)) : 0);
            int cy = y < r ? (r - y) : (y >= h - r ? (y - (h - r - 1)) : 0);
            if (cx == 0 || cy == 0) return -1f;
            float d = Mathf.Sqrt(cx * cx + cy * cy);
            return Mathf.Clamp01(d - (r - 0.5f));
        }

        public void HideDefaultTableMats()
        {
            if (Table == null || Table.m_TableGameItemSetList == null) return;
            for (int i = 0; i < Table.m_TableGameItemSetList.Count; i++)
            {
                var set = Table.m_TableGameItemSetList[i];
                if (set == null) continue;
                if (set.m_PlayMatMesh != null) set.m_PlayMatMesh.gameObject.SetActive(false);
                if (set.m_ComicBookMesh != null) set.m_ComicBookMesh.gameObject.SetActive(false);

                // Ocultar cartas estáticas de abanico (CardFanRandomizer / CardSingle) y el CardDeck esquinero solo en ESTA mesa durante la partida
                var fans = set.GetComponentsInChildren<CardFanRandomizer>(true);
                for (int f = 0; f < fans.Length; f++)
                {
                    var fan = fans[f];
                    if (fan == null || fan.m_CardFanGrpList == null) continue;
                    for (int g = 0; g < fan.m_CardFanGrpList.Count; g++)
                    {
                        var grp = fan.m_CardFanGrpList[g];
                        if (grp == null) continue;
                        SavePropOnce(grp);
                        grp.SetActive(false);
                    }
                }

                var renderers = set.GetComponentsInChildren<MeshRenderer>(true);
                for (int r = 0; r < renderers.Length; r++)
                {
                    var mr = renderers[r];
                    if (mr == null) continue;
                    string rName = mr.name ?? "";
                    if (rName.StartsWith("CardSingle", StringComparison.OrdinalIgnoreCase))
                    {
                        SavePropOnce(mr.gameObject, mr);
                        mr.gameObject.SetActive(false);
                    }
                    else if (rName.StartsWith("CardDeck", StringComparison.OrdinalIgnoreCase))
                    {
                        SavePropOnce(mr.gameObject, mr);
                        if (pokemonDeckInstanceMat != null)
                            mr.sharedMaterial = pokemonDeckInstanceMat;
                        // Ocultamos la copia esquinera que cae sobre Trash porque nuestro Mazo 3D con esa misma malla se coloca exactamente sobre Deck
                        mr.gameObject.SetActive(false);
                    }
                }
            }
        }

        public void DetachFromTable()
        {
            // Restaurar intactos los materiales y objetos originales de esta mesa para no afectar ninguna partida posterior ni otras mesas
            for (int i = 0; i < savedTableProps.Count; i++)
            {
                var sp = savedTableProps[i];
                if (sp.Renderer != null && sp.OrigSharedMaterials != null)
                {
                    sp.Renderer.sharedMaterials = sp.OrigSharedMaterials;
                }
                if (sp.TargetGo != null)
                {
                    sp.TargetGo.SetActive(sp.WasActive);
                }
            }
            savedTableProps.Clear();

            if (Table != null && Table.m_TableGameItemSetList != null)
            {
                for (int i = 0; i < Table.m_TableGameItemSetList.Count; i++)
                {
                    var set = Table.m_TableGameItemSetList[i];
                    if (set == null) continue;
                    if (set.m_PlayMatMesh != null) set.m_PlayMatMesh.gameObject.SetActive(true);
                    set.gameObject.SetActive(false);
                }
            }
            Cleanup3D();
            Table = null;
        }

        void Cleanup3D()
        {
            slotIndicators.Clear();
            tableSlots.Clear();
            cardPool = null;
            physicalBlockPool = null;
            nativeDeckSpec = null;
            nativeDeckTemplateMat = null;
            lastBuiltDeckAtlasSource = null;
            if (pokemonDeckInstanceMat != null)
            {
                Destroy(pokemonDeckInstanceMat);
                pokemonDeckInstanceMat = null;
            }
            if (pokemonDeckAtlasTex != null)
            {
                Destroy(pokemonDeckAtlasTex);
                pokemonDeckAtlasTex = null;
            }
            deckStacks[0] = null;
            deckStacks[1] = null;
            dragCardView = null;
            dragPosInitialized = false;
            inspectCardView = null;
            inspectBackdropGo = null;
            inspectBackdropMat = null;
            statusCounters = null;
            StatusCounters3D.ActivePopups.Clear();
            handDeployAnim = 0f;
            handTrayLiftAnim = 0f;
            Array.Clear(handLiftAnims, 0, handLiftAnims.Length);
            Array.Clear(handSpreadAnims, 0, handSpreadAnims.Length);
            ResetPokedexSession();
            Array.Clear(pokedexCardViews, 0, pokedexCardViews.Length);
            if (PlaymatRoot != null)
            {
                Destroy(PlaymatRoot.gameObject);
                PlaymatRoot = null;
            }
        }

        void BuildPlaymat3D(Vector3 center, Quaternion rotation)
        {
            EnsurePrimitives();

            PlaymatRoot = new GameObject("PokeTCG_Playmat3D").transform;
            PlaymatRoot.SetPositionAndRotation(center, rotation);

            // Base de neopreno negro bajo el tapete (da relieve físico real sobre la madera de la mesa)
            var baseGo = new GameObject("NeopreneBase");
            baseGo.transform.SetParent(PlaymatRoot, false);
            baseGo.transform.localPosition = new Vector3(0f, MatThickness * 0.5f, 0f);
            baseGo.transform.localScale = new Vector3(MatWidth + 0.010f, MatThickness, MatLength + 0.010f);
            var baseMf = baseGo.AddComponent<MeshFilter>();
            baseMf.sharedMesh = sharedCubeMesh;
            matBaseRenderer = baseGo.AddComponent<MeshRenderer>();
            if (matEdgeMaterial == null)
            {
                matEdgeMaterial = CreateMaterial(new Color(0.08f, 0.10f, 0.14f, 1f), null);
            }
            matBaseRenderer.sharedMaterial = matEdgeMaterial;

            // Superficie superior con la textura del Playmat oficial
            var topGo = new GameObject("PlaymatSurface");
            topGo.transform.SetParent(PlaymatRoot, false);
            topGo.transform.localPosition = new Vector3(0f, MatThickness + 0.0005f, 0f);
            topGo.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            topGo.transform.localScale = new Vector3(MatWidth, MatLength, 1f);
            var topMf = topGo.AddComponent<MeshFilter>();
            topMf.sharedMesh = sharedQuadMesh;
            matTopRenderer = topGo.AddComponent<MeshRenderer>();
            matTopMaterial = CreateMaterial(Color.white, SpriteCache.PlaymatTex);
            matTopRenderer.sharedMaterial = matTopMaterial;
        }

        // [CreateNormalizedAnchor, AttachSlotCollider y BuildAnchors se definen en TableView.Anchors.cs]

        float setupRevealFlipZ = 180f;

        public void ResetStatusTracking()
        {
            statusCounters?.ResetTracking();
            Coin?.Reset();
            PolishEffects3D.ClearAll();
            setupRevealFlipZ = 180f;
            ResetPokedexSession();
        }

        public void LaunchStartCoinToss3D(bool headsResult, float duration)
        {
            if (Coin == null || CoinAnchor == null) return;
            Vector3 launchOrigin = new Vector3(0f, MatThickness + 0.002f, -0.34f * MatLength);
            Coin.LaunchStartToss(launchOrigin, CoinAnchor.localPosition, headsResult, duration);
        }

        public void LaunchGameCoinFlip3D(int playerIdx, bool headsResult, float duration, string reason)
        {
            if (Coin == null || CoinArenaAnchor == null) return;
            float fromZ = playerIdx == 0 ? -0.24f * MatLength : 0.24f * MatLength;
            Vector3 launchOrigin = new Vector3(CoinArenaAnchor.localPosition.x + 0.045f, MatThickness + 0.002f, fromZ);
            Coin.LaunchGameFlip(launchOrigin, CoinArenaAnchor.localPosition, headsResult, duration, reason);
        }

        /// <summary>
        /// F2–F7: Sincroniza todas las cartas, fichas de daño/estado y moneda 3D sobre el tapete físico.
        /// </summary>
        public void SyncBoard3D(
            Game game,
            CardDef zoomedCard,
            PokemonInPlay zoomedPip,
            float zoomAnim,
            bool retreatMode,
            int hoveredHandIdx = -1,
            int selectedHandIdx = -1,
            int draggingHandIdx = -1,
            Vector3 targetDragWorldPos = default,
            bool isOverValidDropSlot = false,
            Vector3 zoomOriginWorldPos = default,
            Quaternion zoomOriginWorldRot = default,
            float zoomOriginScale = 1f,
            bool startCoinSpinning = false)
        {
            if (PlaymatRoot == null || cardPool == null) return;
            EnsurePokemonDeckAtlasUpToDate();
            cardPool.BeginFrame();
            physicalBlockPool?.BeginFrame();
            statusCounters?.BeginFrame();

            if (game == null)
            {
                deckStacks[0]?.Sync(0);
                deckStacks[1]?.Sync(0);
                Coin?.SyncFrame(false, false, Vector3.zero, MatThickness + 0.0008f);
                if (dragCardView != null && dragCardView.gameObject.activeSelf)
                    dragCardView.gameObject.SetActive(false);
                if (inspectCardView != null && inspectCardView.gameObject.activeSelf)
                    inspectCardView.gameObject.SetActive(false);
                if (inspectBackdropGo != null && inspectBackdropGo.activeSelf)
                    inspectBackdropGo.SetActive(false);
                dragPosInitialized = false;
                return;
            }

            bool usePhysicalNativeMesh = nativeDeckSpec != null && nativeDeckSpec.IsValid && physicalBlockPool != null;
            var anim = BoardAnimator.Instance;

            // F8: En Fase de Preparación (Setup) los Pokémon colocados por el NPC permanecen boca abajo (180°),
            // y al iniciar la partida (Phase.Main) se voltean simultáneamente boca arriba (180° -> 0°).
            if (game.Phase == Phase.CoinToss || game.Phase == Phase.Setup)
                setupRevealFlipZ = 180f;
            else
                setupRevealFlipZ = Mathf.MoveTowards(setupRevealFlipZ, 0f, Time.deltaTime * 420f);

            for (int p = 0; p < 2; p++)
            {
                var ps = game.Players[p];
                if (ps == null) continue;

                // 1) Mazo físico 3D con reverso oficial de Pokémon (misma malla física redondeada de la baraja, exclusiva para esta partida)
                int pendingDraws = anim != null ? anim.GetPendingDeckDraws(p) : 0;
                int visibleDeckCount = ps.Deck.Count + pendingDraws;
                if (usePhysicalNativeMesh)
                {
                    deckStacks[p]?.Sync(0);
                    if (visibleDeckCount > 0)
                    {
                        float deckHeight = Mathf.Lerp(0.0024f, 0.032f, Mathf.Clamp01(visibleDeckCount / 60f));
                        physicalBlockPool.SpawnBlock(DeckAnchor[p], Vector3.zero, nativeDeckSpec, 1.0f, deckHeight, 0f);
                    }
                }
                else
                {
                    deckStacks[p]?.Sync(visibleDeckCount);
                }

                // 2) Descarte apilado en 3D con desorden leve (hasta las últimas 6 cartas visibles)
                int dCount = ps.Discard.Count;
                int startD = Mathf.Max(0, dCount - 6);
                for (int k = startD; k < dCount; k++)
                {
                    var dCard = ps.Discard[k];
                    bool isTop = (k == dCount - 1);
                    if (isTop && zoomedPip == null && zoomedCard == dCard && zoomAnim > 0.02f)
                        continue;
                    if (isTop && anim != null && anim.IsCardFlyingToDiscard(p, dCard))
                        continue;

                    int layer = k - startD;
                    int seed = k * 7919 + p * 131;
                    float offX = k == 0 ? 0f : (((seed % 11) - 5) / 5f) * 0.0024f;
                    float offZ = k == 0 ? 0f : ((((seed / 11) % 11) - 5) / 5f) * 0.0024f;
                    float yaw = k == 0 ? 0f : (((seed / 121) % 13) - 6) * 1.15f;
                    Vector3 dOff = new Vector3(offX, layer * (CardView.CardThickness + 0.00025f), offZ);
                    var cv = cardPool.Spawn(DiscardAnchor[p], dOff, dCard, false, 1f, false, yaw);
                    if (cv != null)
                    {
                        cv.OwnerIndex = p;
                        cv.IsDiscardTop = isTop;
                        cv.SetRaycastEnabled(isTop);
                    }
                }

                // 3) Premios en 3D a los costados (Side): cartas físicas 3D con el mismo cuerpo y reverso que la baraja
                int pendingPrizeTakes = anim != null ? anim.GetPendingPrizeTakes(p) : 0;
                int pCount = Mathf.Min(6, ps.Prizes.Count + pendingPrizeTakes);
                for (int i = 0; i < pCount; i++)
                {
                    if (usePhysicalNativeMesh)
                    {
                        float subtleYaw = (i % 2 == 0) ? -0.8f : 0.6f;
                        physicalBlockPool.SpawnBlock(PrizeAnchor[p, i], Vector3.zero, nativeDeckSpec, 0.88f, 0.0024f, subtleYaw);
                    }
                    else
                    {
                        var pCard = i < ps.Prizes.Count ? ps.Prizes[i] : null;
                        var cv = cardPool.Spawn(PrizeAnchor[p, i], Vector3.zero, pCard, true, 0.88f, false, 0f);
                        if (cv != null) cv.SetRaycastEnabled(false);
                    }
                }

                // 4) Banca en 3D (5 casillas)
                for (int i = 0; i < 5; i++)
                {
                    var bPip = i < ps.Bench.Count ? ps.Bench[i] : null;
                    bool hl = retreatMode && p == 0 && bPip != null;
                    SyncPokemonSlot3D(p, i, BenchAnchor[p, i], bPip, 0.94f, false, hl, zoomedPip, zoomAnim, game.Turn);
                }

                // 5) Pokémon Activo en el Campo de Batalla Central 3D
                SyncPokemonSlot3D(p, -1, ActiveAnchor[p], ps.Active, 1.08f, true, false, zoomedPip, zoomAnim, game.Turn);
            }

            // 5.5) Estadio 3D Activo en StadiumAnchor
            if (StadiumAnchor != null && !string.IsNullOrEmpty(game.ActiveStadium))
            {
                var sCard = CardDatabase.All.FirstOrDefault(c => string.Equals(c.Name, game.ActiveStadium, StringComparison.OrdinalIgnoreCase));
                if (sCard != null)
                {
                    var sCv = cardPool.Spawn(StadiumAnchor, Vector3.zero, sCard, false, 0.95f, false, 0f);
                    if (sCv != null)
                    {
                        sCv.SetRaycastEnabled(true);
                    }
                }
            }

            // 6) F4 & F5: Mano 3D en abanico (más baja en reposo para no tapar la Banca; se eleva al pasar el cursor)
            SyncHands3D(game, zoomedCard, zoomedPip, zoomAnim, hoveredHandIdx, selectedHandIdx, draggingHandIdx);

            // 7) F4: Carta 3D arrastrada por el jugador sobre el tapete con inclinación física
            SyncDragCard3D(game, draggingHandIdx, targetDragWorldPos, isOverValidDropSlot);

            // 8) F5: Carta 3D en Inspección (sube hacia la cámara con giro interactivo y telón 3D oscurecido detrás)
            SyncInspectCard3D(zoomedCard, zoomAnim, zoomOriginWorldPos, zoomOriginWorldRot, zoomOriginScale);

            // 8.5) Parte 1: Inspección y Reordenamiento de Mazo 3D (Pokédex & Top Deck)
            UpdatePokedex3D(Time.deltaTime);

            // 9) F7: Moneda 3D (vista previa flotante en CoinToss, lanzamiento parabólico con rebote y reposo en el tapete)
            bool inCoinChoice = game.Phase == Phase.CoinToss && !startCoinSpinning;
            Vector3 centerLocal = CoinAnchor != null ? CoinAnchor.localPosition : Vector3.zero;
            Coin?.SyncFrame(true, inCoinChoice, centerLocal, MatThickness + 0.0008f);

            // 10) F10: Efectos visuales 3D por tipo elemental y sacudida de cámara
            PolishEffects3D.SyncFrame(PlaymatRoot);
        }

        public Vector3 GetAttachedEnergyWorldPos(int ownerIdx, int benchSlotIdx, int energySlotIdx)
        {
            int p = Mathf.Clamp(ownerIdx, 0, 1);
            Transform anchor = benchSlotIdx < 0 ? ActiveAnchor[p] : BenchAnchor[p, Mathf.Clamp(benchSlotIdx, 0, 4)];
            if (anchor == null) return PlaymatRoot != null ? PlaymatRoot.position : Vector3.zero;
            float scale = benchSlotIdx < 0 ? 1.08f : 0.94f;
            int idx = Mathf.Max(0, energySlotIdx);
            Vector3 eOff = new Vector3(
                (-0.012f - idx * 0.0085f) * scale,
                0f,
                (-0.011f - idx * 0.0075f) * scale);
            return anchor.TransformPoint(eOff);
        }

        void SyncPokemonSlot3D(int ownerIdx, int benchSlotIdx, Transform anchor, PokemonInPlay pip, float scale, bool isActive, bool highlight, PokemonInPlay zoomedPip, float zoomAnim, int currentTurn)
        {
            if (anchor == null || pip == null) return;
            if (pip == zoomedPip && zoomAnim > 0.05f) return;

            var anim = BoardAnimator.Instance;
            if (anim != null && anim.IsPokemonSlotInFlightOrQueued(ownerIdx, benchSlotIdx))
                return;

            var curFlight = anim?.Current;
            int pendingEvolves = anim != null ? anim.GetPendingEvolveCount(ownerIdx, benchSlotIdx) : 0;
            int queuedEnergies = anim != null ? anim.GetQueuedEnergyCount(ownerIdx, benchSlotIdx) : 0;

            // Elevación suave de la carta objetivo al unir Energía:
            // 1) Se eleva (0.00..0.34), 2) espera arriba mientras la Energía entra debajo (0.34..0.72), 3) desciende a su origen (0.72..1.00)
            bool isTargetOfFlyingEnergy = curFlight != null &&
                                          curFlight.Kind == AnimKind.AttachEnergy &&
                                          curFlight.PlayerIndex == ownerIdx &&
                                          curFlight.TargetBenchIndex == benchSlotIdx;
            float energyAnimT = isTargetOfFlyingEnergy
                ? Mathf.Clamp01(curFlight.Elapsed / Mathf.Max(0.05f, curFlight.Duration))
                : -1f;
            bool hideNewestStaticEnergy = isTargetOfFlyingEnergy && energyAnimT < 0.72f;

            float energyLiftY = 0f;
            float energyLiftPitch = 0f;
            if (isTargetOfFlyingEnergy)
            {
                float liftFactor;
                if (energyAnimT < 0.34f)
                    liftFactor = Mathf.SmoothStep(0f, 1f, energyAnimT / 0.34f);
                else if (energyAnimT < 0.72f)
                    liftFactor = 1f;
                else
                    liftFactor = 1f - Mathf.SmoothStep(0f, 1f, (energyAnimT - 0.72f) / 0.28f);

                energyLiftY = liftFactor * 0.026f;
                energyLiftPitch = -5.5f * liftFactor;
            }

            // 1) Energías adheridas físicamente DEBAJO del Pokémon (asomando por el borde inferior/izquierdo)
            int visibleEnergyCount = Mathf.Max(0, pip.Energy.Count - queuedEnergies);
            int renderedEnergyCount = hideNewestStaticEnergy ? Mathf.Max(0, visibleEnergyCount - 1) : visibleEnergyCount;
            for (int i = visibleEnergyCount - 1; i >= 0; i--)
            {
                if (hideNewestStaticEnergy && i == visibleEnergyCount - 1) continue;
                var eCard = pip.Energy[i];
                int orderFromTop = visibleEnergyCount - 1 - i;
                Vector3 eOff = new Vector3(
                    (-0.012f - i * 0.0085f) * scale,
                    orderFromTop * (CardView.CardThickness + 0.00015f),
                    (-0.011f - i * 0.0075f) * scale);
                var eCv = cardPool.Spawn(anchor, eOff, eCard, false, scale, false, 0f);
                if (eCv != null) eCv.SetRaycastEnabled(false);
            }

            float baseY = renderedEnergyCount * (CardView.CardThickness + 0.00015f) + 0.0004f + energyLiftY;

            // 2) Etapas previas de evolución apiladas debajo del Pokémon evolucionado
            int fullStackCount = pip.Stack != null ? pip.Stack.Count : 1;
            int visibleStackCount = Mathf.Clamp(fullStackCount - pendingEvolves, 1, fullStackCount);
            for (int s = 0; s < visibleStackCount - 1; s++)
            {
                var preCard = pip.Stack[s];
                int depth = visibleStackCount - 1 - s;
                Vector3 preOff = new Vector3(0.0022f * depth, baseY + s * (CardView.CardThickness + 0.0002f), 0.0022f * depth);
                var preCv = cardPool.Spawn(anchor, preOff, preCard, false, scale, false, 0f, energyLiftPitch);
                if (preCv != null) preCv.SetRaycastEnabled(false);
            }

            CardDef visibleTopCard = pip.Stack != null && pip.Stack.Count >= visibleStackCount
                ? pip.Stack[visibleStackCount - 1]
                : pip.Card;

            // 3) F6: Rotación física suavizada según estado alterado (Dormido -90°, Paralizado +90°, Confundido 180°) + vibración al recibir golpe
            float statusYaw = statusCounters != null
                ? statusCounters.GetSmoothedStatusYaw(pip, isActive)
                : 0f;
            Vector3 hitShake = statusCounters != null
                ? statusCounters.GetHitShakeLocalOffset(pip)
                : Vector3.zero;

            float revealHopY = setupRevealFlipZ > 0.5f && setupRevealFlipZ < 179.5f
                ? Mathf.Sin((setupRevealFlipZ / 180f) * Mathf.PI) * 0.022f
                : 0f;
            float topCardY = baseY + Math.Max(0, visibleStackCount - 1) * (CardView.CardThickness + 0.0002f) + 0.0003f + revealHopY;
            Vector3 topOff = new Vector3(hitShake.x, topCardY, hitShake.z);
            bool isFaceDownSetup = setupRevealFlipZ > 90f;
            var topCv = cardPool.Spawn(anchor, topOff, visibleTopCard, isFaceDownSetup, scale, highlight, statusYaw, energyLiftPitch);
            if (topCv != null)
            {
                topCv.SetFlipRoll(setupRevealFlipZ, energyLiftPitch, statusYaw);
                topCv.OwnerIndex = ownerIdx;
                topCv.BoundPip = pip;
                topCv.SetRaycastEnabled(!isFaceDownSetup);
                if (statusCounters != null && !isFaceDownSetup)
                    topCv.SetStatusTintOverlay(statusCounters.GetAnimatedCardTintOverlay(pip));
            }

            // 4) F6: Fichas 3D de Contadores de Daño (10, 50, 100) y Marcadores 3D (Veneno, Defender, PlusPower) sobre la carta
            if (!isFaceDownSetup)
                statusCounters?.SyncPokemonCounters(anchor, pip, topCardY + CardView.CardThickness * 0.5f, scale, currentTurn);
        }

        /// <summary>
        /// F4 & F5: Renderiza la mano del Jugador y del NPC en 3D en disposición de abanico,
        /// manteniéndose baja en reposo para no cubrir la Banca y elevándose al pasar el cursor por encima.
        /// </summary>
        void SyncHands3D(
            Game game,
            CardDef zoomedCard,
            PokemonInPlay zoomedPip,
            float zoomAnim,
            int hoveredHandIdx,
            int selectedHandIdx,
            int draggingHandIdx)
        {
            float targetDeploy = game.Phase == Phase.CoinToss ? 0.35f : 1f;
            handDeployAnim = Mathf.MoveTowards(handDeployAnim, targetDeploy, Time.deltaTime * 2.8f);
            float deploySmooth = Mathf.SmoothStep(0f, 1f, handDeployAnim);

            int focusIdx = (hoveredHandIdx >= 0 && draggingHandIdx < 0) ? hoveredHandIdx : selectedHandIdx;
            float targetTrayLift = (hoveredHandIdx >= 0 || selectedHandIdx >= 0) ? 1f : 0f;
            handTrayLiftAnim = Mathf.MoveTowards(handTrayLiftAnim, targetTrayLift, Time.deltaTime * 8.5f);
            float trayLiftSmooth = Mathf.SmoothStep(0f, 1f, handTrayLiftAnim);

            for (int p = 0; p < 2; p++)
            {
                var ps = game.Players[p];
                if (ps == null || HandAnchor[p] == null) continue;

                int n = ps.Hand.Count;
                int pendingArrivals = BoardAnimator.Instance != null ? BoardAnimator.Instance.GetPendingHandArrivals(p) : 0;
                int visibleCount = Mathf.Max(0, n - pendingArrivals);

                for (int i = 0; i < visibleCount; i++)
                {
                    var card = ps.Hand[i];

                    if (p == 0)
                    {
                        // Si está siendo arrastrada en 3D o inspeccionada con clic derecho, no se dibuja en el abanico
                        if (i == draggingHandIdx) continue;
                        if (zoomedPip == null && zoomedCard == card && zoomAnim > 0.02f) continue;

                        bool isHovered = (i == hoveredHandIdx && draggingHandIdx < 0);
                        bool isSelected = (i == selectedHandIdx);
                        float targetLift = isHovered ? 1f : (isSelected ? 0.85f : 0f);
                        if (i < handLiftAnims.Length)
                        {
                            handLiftAnims[i] = Mathf.MoveTowards(handLiftAnims[i], targetLift, Time.deltaTime * 11.5f);
                        }
                        float liftT = i < handLiftAnims.Length ? Mathf.SmoothStep(0f, 1f, handLiftAnims[i]) : targetLift;

                        float targetSpread = 0f;
                        if (focusIdx >= 0 && focusIdx < visibleCount && i != focusIdx)
                        {
                            float dist = Mathf.Abs(i - focusIdx);
                            float push = Mathf.Clamp01(1.25f / dist) * 0.022f;
                            targetSpread = i < focusIdx ? -push : push;
                        }
                        if (i < handSpreadAnims.Length)
                        {
                            handSpreadAnims[i] = Mathf.MoveTowards(handSpreadAnims[i], targetSpread, Time.deltaTime * 0.18f);
                        }
                        float spreadX = i < handSpreadAnims.Length ? handSpreadAnims[i] : targetSpread;

                        ComputeHandCardTransform(0, i, visibleCount, focusIdx, liftT, out Vector3 localPos, out float yawDeg, out float scale, spreadX, deploySmooth, trayLiftSmooth);
                        bool highlight = isHovered || isSelected;
                        var cv = cardPool.Spawn(HandAnchor[0], localPos, card, false, scale, highlight, yawDeg);
                        if (cv != null)
                        {
                            cv.OwnerIndex = 0;
                            cv.HandIndex = i;
                            cv.SetRaycastEnabled(true);
                            if (isSelected && !isHovered)
                                cv.SetHighlight(true, new Color(0.25f, 0.96f, 0.55f, 0.96f));
                            else if (isHovered)
                                cv.SetHighlight(true, new Color(1f, 0.86f, 0.20f, 0.96f));
                        }
                    }
                    else
                    {
                        // Mano 3D del NPC (boca abajo en el borde superior del tapete)
                        ComputeHandCardTransform(1, i, visibleCount, -1, 0f, out Vector3 localPos, out float yawDeg, out float scale, 0f, deploySmooth, 0f);
                        var cv = cardPool.Spawn(HandAnchor[1], localPos, card, true, scale, false, yawDeg);
                        if (cv != null)
                        {
                            cv.OwnerIndex = 1;
                            cv.SetRaycastEnabled(false);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Calcula la posición local, ángulo de abanico y escala de la carta `index` dentro de una mano 3D de `count` cartas.
        /// </summary>
        public static void ComputeHandCardTransform(
            int playerIdx,
            int index,
            int count,
            int hoveredIdx,
            float liftT,
            out Vector3 localPos,
            out float yawDeg,
            out float scale,
            float customSpreadX = float.NaN,
            float deployFactor = 1f,
            float trayLift = 0f)
        {
            float t = count <= 1 ? 0f : (index / (float)(count - 1)) * 2f - 1f; // -1 (izq) .. +1 (der)

            if (playerIdx == 0)
            {
                float baseScale = 0.85f;
                float maxSpanX = 0.44f * deployFactor;
                float idealStep = 0.055f * deployFactor;
                float totalW = count <= 1 ? 0f : Mathf.Min(maxSpanX, (count - 1) * idealStep);
                float x = count <= 1 ? 0f : (-totalW * 0.5f + index * (totalW / (count - 1)));

                if (!float.IsNaN(customSpreadX))
                {
                    x += customSpreadX;
                }
                else if (hoveredIdx >= 0 && hoveredIdx < count && index != hoveredIdx)
                {
                    float dist = Mathf.Abs(index - hoveredIdx);
                    float push = Mathf.Clamp01(1.25f / dist) * 0.022f;
                    x += index < hoveredIdx ? -push : push;
                }

                float archZ = -(t * t) * Mathf.Min(0.020f, (count - 1) * 0.0026f) * deployFactor;
                float maxYaw = Mathf.Min(15f, (count - 1) * 2.3f) * deployFactor;
                yawDeg = t * maxYaw * (1f - liftT * 0.94f);

                // En reposo el abanico llega hasta el límite inferior de Bench;
                // al pasar el cursor por encima (trayLift / liftT) se eleva suavemente para seleccionar la carta.
                float idleWave = Mathf.Sin(Time.time * 2.2f + index * 0.55f) * 0.0012f * (1f - liftT);
                float y = index * 0.0015f + trayLift * 0.006f + liftT * 0.028f + idleWave;
                float z = archZ + trayLift * 0.010f + liftT * 0.048f;

                localPos = new Vector3(x, y, z);
                scale = Mathf.Lerp(baseScale, 1.13f, liftT);
            }
            else
            {
                float baseScale = 0.76f;
                float maxSpanX = 0.34f * deployFactor;
                float idealStep = 0.044f * deployFactor;
                float totalW = count <= 1 ? 0f : Mathf.Min(maxSpanX, (count - 1) * idealStep);
                float x = count <= 1 ? 0f : (-totalW * 0.5f + index * (totalW / (count - 1)));
                float archZ = -(t * t) * Mathf.Min(0.014f, (count - 1) * 0.0020f) * deployFactor;
                yawDeg = t * Mathf.Min(12f, (count - 1) * 1.9f) * deployFactor;
                float idleWave = Mathf.Sin(Time.time * 1.9f + index * 0.6f) * 0.0010f;
                localPos = new Vector3(x, index * 0.0011f + idleWave, archZ);
                scale = baseScale;
            }
        }

        /// <summary>
        /// Devuelve la posición en el mundo 3D de la carta `index` dentro del abanico de la mano de `playerIdx`.
        /// </summary>
        public Vector3 GetHandCardWorldPos(int playerIdx, int index, int count)
        {
            int p = Mathf.Clamp(playerIdx, 0, 1);
            if (HandAnchor[p] == null)
                return PlaymatRoot != null ? PlaymatRoot.position : Vector3.zero;
            int clampedCount = Mathf.Max(1, count);
            int clampedIdx = Mathf.Clamp(index, 0, clampedCount - 1);
            float lift = (p == 0 && clampedIdx < handLiftAnims.Length) ? Mathf.SmoothStep(0f, 1f, handLiftAnims[clampedIdx]) : 0f;
            float spread = (p == 0 && clampedIdx < handSpreadAnims.Length) ? handSpreadAnims[clampedIdx] : 0f;
            float tray = p == 0 ? Mathf.SmoothStep(0f, 1f, handTrayLiftAnim) : 0f;
            ComputeHandCardTransform(p, clampedIdx, clampedCount, -1, lift, out Vector3 localPos, out _, out _, spread, Mathf.SmoothStep(0f, 1f, handDeployAnim), tray);
            return HandAnchor[p].TransformPoint(localPos);
        }

        /// <summary>
        /// Devuelve la rotación en el mundo 3D de la carta `index` dentro del abanico de la mano de `playerIdx`.
        /// </summary>
        public Quaternion GetHandCardWorldRot(int playerIdx, int index, int count)
        {
            int p = Mathf.Clamp(playerIdx, 0, 1);
            if (HandAnchor[p] == null)
                return PlaymatRoot != null ? PlaymatRoot.rotation : Quaternion.identity;
            int clampedCount = Mathf.Max(1, count);
            int clampedIdx = Mathf.Clamp(index, 0, clampedCount - 1);
            float lift = (p == 0 && clampedIdx < handLiftAnims.Length) ? Mathf.SmoothStep(0f, 1f, handLiftAnims[clampedIdx]) : 0f;
            ComputeHandCardTransform(p, clampedIdx, clampedCount, -1, lift, out _, out float yawDeg, out _, 0f, Mathf.SmoothStep(0f, 1f, handDeployAnim), 0f);
            return HandAnchor[p].rotation * Quaternion.Euler(0f, yawDeg, 0f);
        }

        float inspectFoilSpinTimer;
        const float inspectFoilSpinDuration = 0.50f;
        bool inspectFoilSpinActive;
        bool inspectFoilSpinHalfwayCalled;
        Action onInspectFoilSpinHalfway;
        bool? currentInspectFoilOverride;

        public bool IsInspectFoilSpinning => inspectFoilSpinActive;
        public bool CurrentInspectFoil => currentInspectFoilOverride ?? (inspectCardView != null && inspectCardView.IsFoil);

        public void SetInspectFoil(bool foil)
        {
            currentInspectFoilOverride = foil;
            if (inspectCardView != null) inspectCardView.SetFoil(foil);
        }

        public void TriggerFoilFlip(Action onHalfway)
        {
            if (inspectFoilSpinActive) return;
            inspectFoilSpinActive = true;
            inspectFoilSpinTimer = 0f;
            inspectFoilSpinHalfwayCalled = false;
            onInspectFoilSpinHalfway = onHalfway;
            TcgAudio.PlayCardDraw();
        }

        /// <summary>
        /// F5: Eleva en 3D la carta inspeccionada desde su posición en la mesa o mano hacia la cámara con giro interactivo,
        /// oscureciendo el fondo 3D detrás de la carta y devolviéndola a su sitio al cerrar.
        /// </summary>
        void SyncInspectCard3D(
            CardDef zoomedCard,
            float zoomAnim,
            Vector3 originWorldPos,
            Quaternion originWorldRot,
            float originScale)
        {
            if (inspectCardView == null || inspectBackdropGo == null) return;
            var cam = GetActiveCamera();
            if (zoomedCard == null || zoomAnim <= 0.005f || cam == null)
            {
                currentInspectFoilOverride = null;
                inspectFoilSpinActive = false;
                if (inspectCardView.gameObject.activeSelf)
                    inspectCardView.gameObject.SetActive(false);
                if (inspectBackdropGo.activeSelf)
                    inspectBackdropGo.SetActive(false);
                return;
            }

            if (!inspectCardView.gameObject.activeSelf)
                inspectCardView.gameObject.SetActive(true);
            if (!inspectBackdropGo.activeSelf)
                inspectBackdropGo.SetActive(true);

            float smooth = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(zoomAnim));

            float flipAngle = 0f;
            if (inspectFoilSpinActive)
            {
                inspectFoilSpinTimer += Time.deltaTime;
                float progress = inspectFoilSpinTimer / inspectFoilSpinDuration;
                if (progress >= 0.5f && !inspectFoilSpinHalfwayCalled)
                {
                    inspectFoilSpinHalfwayCalled = true;
                    onInspectFoilSpinHalfway?.Invoke();
                }
                flipAngle = progress * 360f;
                if (progress >= 1f)
                {
                    flipAngle = 0f;
                    inspectFoilSpinActive = false;
                }
            }

            // 1) Telón 3D oscurecido colocado entre la mesa y la carta inspeccionada para que la carta 3D brille al frente
            inspectBackdropGo.transform.position = cam.transform.position + cam.transform.forward * 0.37f;
            inspectBackdropGo.transform.rotation = cam.transform.rotation;
            inspectBackdropGo.transform.localScale = new Vector3(1.4f, 0.9f, 1f);
            if (inspectBackdropMat != null)
                inspectBackdropMat.color = new Color(0.02f, 0.03f, 0.06f, 0.74f * smooth);

            // 2) Posición objetivo en 3D frente a la cámara (ligeramente a la izquierda para dejar espacio al panel lateral F5)
            Vector3 targetPos = cam.transform.position
                              + cam.transform.forward * 0.295f
                              - cam.transform.right * 0.082f
                              + cam.transform.up * 0.003f;

            // 3) Rotación objetivo en 3D mirando a la cámara + giro leve interactivo con el ratón
            Quaternion faceCamRot = Quaternion.LookRotation(cam.transform.up, -cam.transform.forward);
            float mx = Mathf.Clamp((Input.mousePosition.x / Mathf.Max(1f, Screen.width)) * 2f - 1f, -1f, 1f);
            float my = Mathf.Clamp((Input.mousePosition.y / Mathf.Max(1f, Screen.height)) * 2f - 1f, -1f, 1f);
            float flightSpin = Mathf.Sin(smooth * Mathf.PI) * 16f;
            Quaternion tilt = Quaternion.Euler(
                -my * 7f * smooth,
                (mx * 9f + Mathf.Sin(Time.time * 1.7f) * 2.2f) * smooth,
                -mx * 4.5f * smooth + flightSpin);
            Quaternion targetRot = faceCamRot * tilt;

            Vector3 startPos = originWorldPos.sqrMagnitude > 0.001f ? originWorldPos : PlaymatRoot.position;
            Quaternion startRot = originWorldRot.x == 0f && originWorldRot.y == 0f && originWorldRot.z == 0f && originWorldRot.w == 0f
                ? PlaymatRoot.rotation
                : originWorldRot;

            Vector3 curPos = Vector3.Lerp(startPos, targetPos, smooth) + cam.transform.up * (Mathf.Sin(smooth * Mathf.PI) * 0.028f);
            Quaternion curRot = Quaternion.Slerp(startRot, targetRot, smooth);
            float curScale = Mathf.Lerp(Mathf.Max(0.75f, originScale), 1.30f, smooth);

            bool targetFoil = currentInspectFoilOverride ?? (zoomedCard != null && zoomedCard.IsFoil);
            if (inspectCardView.Card != zoomedCard || inspectCardView.IsFoil != targetFoil)
            {
                inspectCardView.Setup(zoomedCard, false, curScale, true, 0f, 0f, overrideFoil: currentInspectFoilOverride);
            }
            inspectCardView.transform.localScale = new Vector3(curScale, 1f, curScale);
            inspectCardView.SetFlipRoll(flipAngle, 0f, 0f);
            inspectCardView.SetShadowEnabled(false);
            inspectCardView.SetRaycastEnabled(false);
            inspectCardView.SetHighlight(true, new Color(1f, 0.88f, 0.26f, 0.88f * smooth));
            inspectCardView.transform.position = curPos;
            inspectCardView.transform.rotation = curRot;
        }

        /// <summary>
        /// F4: Actualiza la posición, inclinación dinámica por velocidad y resaltado de la carta arrastrada en 3D.
        /// </summary>
        void SyncDragCard3D(Game game, int draggingHandIdx, Vector3 targetDragWorldPos, bool isOverValidDropSlot)
        {
            if (dragCardView == null) return;
            var me = game?.Players[0];
            if (me == null || draggingHandIdx < 0 || draggingHandIdx >= me.Hand.Count)
            {
                if (dragCardView.gameObject.activeSelf)
                    dragCardView.gameObject.SetActive(false);
                dragPosInitialized = false;
                return;
            }

            var card = me.Hand[draggingHandIdx];
            if (!dragCardView.gameObject.activeSelf)
                dragCardView.gameObject.SetActive(true);

            if (!dragPosInitialized)
            {
                smoothDragWorldPos = targetDragWorldPos;
                prevDragWorldPos = targetDragWorldPos;
                dragPosInitialized = true;
            }
            else
            {
                smoothDragWorldPos = Vector3.Lerp(smoothDragWorldPos, targetDragWorldPos, Mathf.Clamp01(Time.deltaTime * 24f));
            }

            Vector3 vel = (smoothDragWorldPos - prevDragWorldPos) / Mathf.Max(0.001f, Time.deltaTime);
            prevDragWorldPos = smoothDragWorldPos;

            Quaternion baseRot = PlaymatRoot != null ? PlaymatRoot.rotation : Quaternion.identity;
            Vector3 localVel = Quaternion.Inverse(baseRot) * vel;
            float pitch = Mathf.Clamp(localVel.z * 24f, -20f, 20f);
            float roll = Mathf.Clamp(-localVel.x * 24f, -20f, 20f);

            float scale = isOverValidDropSlot ? 1.12f : 1.05f;
            dragCardView.Setup(card, false, scale, true, 0f);
            dragCardView.SetRaycastEnabled(false);
            dragCardView.SetHighlight(
                true,
                isOverValidDropSlot
                    ? new Color(0.22f, 0.98f, 0.52f, 0.96f)
                    : new Color(1f, 0.86f, 0.20f, 0.92f));
            dragCardView.transform.position = smoothDragWorldPos;
            dragCardView.transform.rotation = baseRot * Quaternion.Euler(pitch, 0f, roll);
        }

        /// <summary>
        /// Intersección geométrica exacta de un rayo 3D contra un rectángulo orientado en el espacio 3D (independiente de PhysX).
        /// </summary>
        static bool IntersectsWorldQuad(Ray ray, Vector3 center, Vector3 right, Vector3 forward, Vector3 up, float halfW, float halfL, out float dist, out float normDistSq)
        {
            dist = float.MaxValue;
            normDistSq = float.MaxValue;
            Plane plane = new Plane(up, center);
            if (!plane.Raycast(ray, out float enter) || enter <= 0.01f || enter > 5f)
            {
                // Probar también con la normal invertida por si el ángulo de cámara es rasante
                Plane backPlane = new Plane(-up, center);
                if (!backPlane.Raycast(ray, out enter) || enter <= 0.01f || enter > 5f)
                    return false;
            }

            Vector3 hitPt = ray.GetPoint(enter);
            Vector3 delta = hitPt - center;
            float lx = Vector3.Dot(delta, right);
            float lz = Vector3.Dot(delta, forward);

            if (Mathf.Abs(lx) <= halfW && Mathf.Abs(lz) <= halfL)
            {
                dist = enter;
                float nx = lx / Mathf.Max(0.001f, halfW);
                float nz = lz / Mathf.Max(0.001f, halfL);
                normDistSq = nx * nx + nz * nz;
                return true;
            }
            return false;
        }

        /// <summary>
        /// F4: Detecta qué carta de la mano 3D del jugador está bajo el cursor mediante intersección geométrica 3D
        /// directa sobre las cartas activas del abanico (con respaldo de proyección en pantalla).
        /// </summary>
        public bool RaycastHandCard3D(Ray ray, out int handIdx, out CardView hitCard)
        {
            handIdx = -1;
            hitCard = null;
            if (PlaymatRoot == null || cardPool == null) return false;

            int bestIdx = -1;
            float bestScore = float.MinValue;

            var active = cardPool.ActiveCards;
            for (int i = 0; i < active.Count; i++)
            {
                var cv = active[i];
                if (cv == null || !cv.gameObject.activeInHierarchy || !cv.IsRaycastEnabled) continue;
                if (cv.OwnerIndex != 0 || cv.HandIndex < 0) continue;

                Transform t = cv.transform;
                float s = Mathf.Max(0.5f, cv.CurrentScale);
                float halfW = CardWorldWidth * s * 0.56f;
                float halfL = CardWorldLength * s * 0.58f;

                if (IntersectsWorldQuad(ray, t.position, t.right, t.forward, t.up, halfW, halfL, out _, out float normDistSq))
                {
                    // Priorizar la carta apilada encima en el abanico (mayor HandIndex) y más cercana al centro de la carta
                    float liftBonus = cv.HandIndex < handLiftAnims.Length ? handLiftAnims[cv.HandIndex] * 1.5f : 0f;
                    float score = cv.HandIndex * 0.35f + liftBonus - normDistSq * 0.6f;
                    if (bestIdx < 0 || score > bestScore)
                    {
                        bestIdx = cv.HandIndex;
                        bestScore = score;
                        hitCard = cv;
                    }
                }
            }

            if (bestIdx >= 0)
            {
                handIdx = bestIdx;
                return true;
            }

            // Respaldo por proyección en pantalla por si el cursor roza el borde inferior del abanico
            var cam = GetActiveCamera();
            if (cam != null)
            {
                Vector2 mouse = Input.mousePosition;
                float bestScreenDist = float.MaxValue;
                for (int i = 0; i < active.Count; i++)
                {
                    var cv = active[i];
                    if (cv == null || !cv.gameObject.activeInHierarchy || !cv.IsRaycastEnabled) continue;
                    if (cv.OwnerIndex != 0 || cv.HandIndex < 0) continue;

                    Vector3 sp = cam.WorldToScreenPoint(cv.transform.position);
                    if (sp.z <= 0.05f) continue;

                    float halfPixW = Screen.height * 0.052f * cv.CurrentScale;
                    float halfPixH = Screen.height * 0.078f * cv.CurrentScale;
                    float dx = Mathf.Abs(mouse.x - sp.x);
                    float dy = Mathf.Abs(mouse.y - sp.y);
                    if (dx <= halfPixW && dy <= halfPixH)
                    {
                        float d = (dx / halfPixW) * (dx / halfPixW) + (dy / halfPixH) * (dy / halfPixH) - cv.HandIndex * 0.08f;
                        if (d < bestScreenDist)
                        {
                            bestScreenDist = d;
                            bestIdx = cv.HandIndex;
                            hitCard = cv;
                        }
                    }
                }
                if (bestIdx >= 0)
                {
                    handIdx = bestIdx;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        // [RaycastBoard3D, TryGetDragWorldPos y TryProjectWorldPosToGUI se definen en TableView.Anchors.cs]

        void Update()
        {
            if (PlaymatRoot == null) return;
            if (matTopRenderer != null && SpriteCache.PlaymatTex != null && matTopRenderer.sharedMaterial.mainTexture != SpriteCache.PlaymatTex)
                matTopRenderer.sharedMaterial.mainTexture = SpriteCache.PlaymatTex;
        }

        // [UpdateSlotHighlights3D, TryGetScreenPlaymatRect y TryProjectSlotToGUI se definen en TableView.Anchors.cs]
        void EnsurePrimitives()
        {
            if (sharedQuadMesh == null)
            {
                var tmp = GameObject.CreatePrimitive(PrimitiveType.Quad);
                sharedQuadMesh = tmp.GetComponent<MeshFilter>().sharedMesh;
                DestroyImmediate(tmp);
            }
            if (sharedCubeMesh == null)
            {
                var tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
                sharedCubeMesh = tmp.GetComponent<MeshFilter>().sharedMesh;
                DestroyImmediate(tmp);
            }
        }

        static Material CreateMaterial(Color color, Texture2D tex, bool transparent = false)
        {
            Shader sh = Shader.Find(transparent ? "Sprites/Default" : "Unlit/Texture");
            if (sh == null || (!transparent && tex == null))
                sh = Shader.Find("Sprites/Default") ?? Shader.Find("Standard");
            var m = new Material(sh);
            m.color = color;
            if (tex != null) m.mainTexture = tex;
            return m;
        }

        // [StartPokedex3D, UpdatePokedex3D y métodos de Pokédex 3D se definen en TableView.Pokedex.cs]
    }
}
