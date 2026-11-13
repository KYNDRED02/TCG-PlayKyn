// TCG-PlayKyn: 3D playmat world-space HP bars, HP labels and energy type icons (depth-occluded by flying/elevated cards)
using System;
using System.Collections.Generic;
using UnityEngine;

namespace TCGPlayKyn
{
    /// <summary>
    /// Dibuja en el espacio 3D sobre la superficie del tapete las barras de PS verticales de los Pokémon Activos,
    /// el texto de PS ("cur / max"), los iconos circulares de energía apilados de arriba hacia abajo y las barras
    /// horizontales de los Pokémon heridos en Banca. Al vivir sobre el tapete en 3D con ZTest estándar, cualquier
    /// carta que vuele por encima, se arrastre, esté en la mano 3D o se inspeccione con zoom las cubre naturalmente.
    /// </summary>
    public class BoardHud3D
    {
        class HpAnimState
        {
            public float DisplayHp;
            public float BenchBarAlpha;
            public bool WasDamagedOnBench;
        }

        class QuadSlot
        {
            public GameObject Root;
            public MeshFilter Filter;
            public MeshRenderer Renderer;
            public Mesh QuadMesh;
            public Material Mat;
            public float U0 = -1f, V0 = -1f, U1 = -1f, V1 = -1f;
        }

        readonly Transform hudRoot;
        readonly List<QuadSlot> activeQuads = new List<QuadSlot>();
        readonly Stack<QuadSlot> inactiveQuads = new Stack<QuadSlot>();
        readonly Dictionary<PokemonInPlay, HpAnimState> hpAnimStates = new Dictionary<PokemonInPlay, HpAnimState>();
        readonly Vector2[] scratchUvs = new Vector2[4];

        static Shader sSpritesShader;
        static Texture2D sTrackVertTex;
        static Texture2D sFillVertTex;
        static Texture2D sTrackHorizTex;
        static Texture2D sFillHorizTex;
        static readonly Dictionary<string, Texture2D> sTextTexCache = new Dictionary<string, Texture2D>(StringComparer.Ordinal);

        public BoardHud3D(Transform playmatRoot)
        {
            var go = new GameObject("PokeTCG_BoardHud3D");
            go.transform.SetParent(playmatRoot, false);
            hudRoot = go.transform;
            EnsureSharedResources();
        }

        public void ResetTracking()
        {
            hpAnimStates.Clear();
        }

        public void Destroy()
        {
            for (int i = 0; i < activeQuads.Count; i++)
                DestroySlotResources(activeQuads[i]);
            activeQuads.Clear();
            while (inactiveQuads.Count > 0)
                DestroySlotResources(inactiveQuads.Pop());
            hpAnimStates.Clear();
            if (hudRoot != null)
                UnityEngine.Object.Destroy(hudRoot.gameObject);
        }

        static void DestroySlotResources(QuadSlot slot)
        {
            if (slot == null) return;
            if (slot.QuadMesh != null) UnityEngine.Object.Destroy(slot.QuadMesh);
            if (slot.Mat != null) UnityEngine.Object.Destroy(slot.Mat);
        }

        public void BeginFrame()
        {
            for (int i = 0; i < activeQuads.Count; i++)
            {
                var q = activeQuads[i];
                if (q != null && q.Root != null)
                {
                    q.Root.SetActive(false);
                    inactiveQuads.Push(q);
                }
            }
            activeQuads.Clear();
        }

        /// <summary>
        /// Sincroniza en 3D la barra de PS e iconos de energía de un Pokémon en Activo o Banca.
        /// </summary>
        public void SyncPokemonHud(
            Transform playmatRoot,
            Transform slotAnchor,
            PokemonInPlay pokemon,
            int ownerIdx,
            bool isActive,
            float cardScale,
            float matTopY)
        {
            if (hudRoot == null || playmatRoot == null || slotAnchor == null || pokemon == null || pokemon.Card == null)
                return;

            EnsureSharedResources();

            int maxHp = Mathf.Max(1, pokemon.Card.Hp);
            if (!hpAnimStates.TryGetValue(pokemon, out var st))
            {
                st = new HpAnimState
                {
                    DisplayHp = pokemon.RemainingHp,
                    BenchBarAlpha = (!isActive && pokemon.RemainingHp < maxHp) ? 1f : 0f,
                    WasDamagedOnBench = pokemon.RemainingHp < maxHp
                };
                hpAnimStates[pokemon] = st;
            }

            float dt = Mathf.Min(0.05f, Time.deltaTime);
            float animSpeed = Mathf.Max(42f, maxHp * 1.15f);
            st.DisplayHp = Mathf.MoveTowards(st.DisplayHp, pokemon.RemainingHp, dt * animSpeed);

            if (isActive)
            {
                if (pokemon.RemainingHp < maxHp || st.DisplayHp < maxHp - 0.05f)
                {
                    st.WasDamagedOnBench = true;
                    st.BenchBarAlpha = 1f;
                }
                else
                {
                    st.WasDamagedOnBench = false;
                    st.BenchBarAlpha = 0f;
                }
            }
            else
            {
                if (pokemon.RemainingHp < maxHp || st.DisplayHp < maxHp - 0.05f)
                {
                    st.WasDamagedOnBench = true;
                    st.BenchBarAlpha = Mathf.MoveTowards(st.BenchBarAlpha, 1f, dt * 6f);
                }
                else if (st.WasDamagedOnBench)
                {
                    st.BenchBarAlpha = Mathf.MoveTowards(st.BenchBarAlpha, 0f, dt * 1.35f);
                    if (st.BenchBarAlpha <= 0.005f)
                    {
                        st.BenchBarAlpha = 0f;
                        st.WasDamagedOnBench = false;
                    }
                }
            }

            float hpRatio = Mathf.Clamp01(st.DisplayHp / maxHp);
            Color hpColor = GetHpThresholdColor(hpRatio);

            Vector3 localCenter = playmatRoot.InverseTransformPoint(slotAnchor.position);
            float halfW = TableView.CardWorldWidth * 0.5f * cardScale;
            float halfL = TableView.CardWorldLength * 0.5f * cardScale;

            if (isActive)
            {
                bool rightSide = (ownerIdx == 0);
                float barW = 0.0115f;
                float barH = TableView.CardWorldLength * cardScale * 0.78f;
                float cardGap = 0.0145f;
                float barX = rightSide ? (localCenter.x + halfW + cardGap) : (localCenter.x - halfW - cardGap);
                float barBottomZ = localCenter.z - halfL + 0.004f;
                float barCenterZ = barBottomZ + barH * 0.5f;

                // 1) Fondo translúcido vertical de la barra
                SpawnQuad(
                    new Vector3(barX, matTopY + 0.00045f, barCenterZ),
                    new Vector2(barW, barH),
                    sTrackVertTex,
                    Color.white,
                    0f, 0f, 1f, 1f);

                // 2) Relleno animado con color por umbral (Verde / Amarillo / Rojo)
                if (hpRatio > 0.001f)
                {
                    float fillH = Mathf.Clamp(barH * hpRatio, 0.006f, barH);
                    float fillCenterZ = barBottomZ + fillH * 0.5f;
                    float uvMaxY = Mathf.Clamp01(fillH / barH);
                    SpawnQuad(
                        new Vector3(barX, matTopY + 0.00055f, fillCenterZ),
                        new Vector2(barW, fillH),
                        sFillVertTex,
                        hpColor,
                        0f, 0f, 1f, uvMaxY);
                }

                // 3) Texto "PS / Máx" centrado encima de la barra
                int dispHpInt = Mathf.Clamp(Mathf.RoundToInt(st.DisplayHp), 0, maxHp);
                Texture2D labelTex = GetOrCreateHpTextTexture($"{dispHpInt} / {maxHp}");
                if (labelTex != null)
                {
                    float aspect = labelTex.width / (float)Mathf.Max(1, labelTex.height);
                    float txtH = 0.0145f;
                    float txtW = txtH * aspect;
                    // Desplazar ligeramente hacia afuera si el texto es muy ancho para que jamás invada la carta
                    float minEdgeX = rightSide ? (localCenter.x + halfW + 0.006f) : (localCenter.x - halfW - 0.006f);
                    float txtX = barX;
                    if (rightSide)
                        txtX = Mathf.Max(txtX, minEdgeX + txtW * 0.5f);
                    else
                        txtX = Mathf.Min(txtX, minEdgeX - txtW * 0.5f);

                    float txtZ = barBottomZ + barH + 0.0105f;
                    SpawnQuad(
                        new Vector3(txtX, matTopY + 0.00060f, txtZ),
                        new Vector2(txtW, txtH),
                        labelTex,
                        Color.white,
                        0f, 0f, 1f, 1f);
                }

                // 4) Iconos circulares de energía apilados de arriba hacia abajo junto a la barra
                int eCount = pokemon.Energy != null ? pokemon.Energy.Count : 0;
                if (eCount > 0)
                {
                    float iconSize = 0.0155f;
                    float rowStep = iconSize + 0.0022f;
                    int maxRowsPerCol = Mathf.Max(4, Mathf.FloorToInt((barH + 0.004f) / rowStep));
                    float barTopZ = barBottomZ + barH;

                    for (int i = 0; i < eCount; i++)
                    {
                        var eCard = pokemon.Energy[i];
                        if (eCard == null) continue;
                        Texture2D iconTex = SpriteCache.GetTypeIcon(eCard.EnergyProvides);
                        if (iconTex == null) continue;

                        int col = i / maxRowsPerCol;
                        int row = i % maxRowsPerCol;
                        float ix = rightSide
                            ? (barX + barW * 0.5f + 0.0045f + iconSize * 0.5f + col * (iconSize + 0.0025f))
                            : (barX - barW * 0.5f - 0.0045f - iconSize * 0.5f - col * (iconSize + 0.0025f));
                        float iz = barTopZ - iconSize * 0.5f - row * rowStep;

                        SpawnQuad(
                            new Vector3(ix, matTopY + 0.00058f, iz),
                            new Vector2(iconSize, iconSize),
                            iconTex,
                            Color.white,
                            0f, 0f, 1f, 1f);
                    }
                }
            }
            else if (st.BenchBarAlpha > 0.01f)
            {
                // Banca: barra horizontal más pequeña debajo de la carta herida (se desvanece al curarse al 100%)
                float bw = TableView.CardWorldWidth * cardScale * 0.94f;
                float bh = 0.0085f;
                float bx = localCenter.x;
                float bz = localCenter.z - halfL - 0.0095f;
                float alpha = st.BenchBarAlpha;

                SpawnQuad(
                    new Vector3(bx, matTopY + 0.00045f, bz),
                    new Vector2(bw, bh),
                    sTrackHorizTex,
                    new Color(1f, 1f, 1f, alpha),
                    0f, 0f, 1f, 1f);

                if (hpRatio > 0.001f)
                {
                    float fillW = Mathf.Clamp(bw * hpRatio, 0.006f, bw);
                    float leftX = bx - bw * 0.5f;
                    float fillCenterX = leftX + fillW * 0.5f;
                    float uvMaxX = Mathf.Clamp01(fillW / bw);
                    SpawnQuad(
                        new Vector3(fillCenterX, matTopY + 0.00055f, bz),
                        new Vector2(fillW, bh),
                        sFillHorizTex,
                        new Color(hpColor.r, hpColor.g, hpColor.b, alpha),
                        0f, 0f, uvMaxX, 1f);
                }
            }
        }

        void SpawnQuad(Vector3 localPos, Vector2 size, Texture2D tex, Color tint, float u0, float v0, float u1, float v1)
        {
            if (tex == null) return;
            QuadSlot slot;
            if (inactiveQuads.Count > 0)
            {
                slot = inactiveQuads.Pop();
            }
            else
            {
                var go = new GameObject("HudQuad3D");
                go.transform.SetParent(hudRoot, false);
                var mf = go.AddComponent<MeshFilter>();
                var mr = go.AddComponent<MeshRenderer>();
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                var mesh = BuildUnitQuadMesh();
                mf.sharedMesh = mesh;
                var mat = new Material(sSpritesShader);
                mr.sharedMaterial = mat;
                slot = new QuadSlot { Root = go, Filter = mf, Renderer = mr, QuadMesh = mesh, Mat = mat };
            }

            slot.Root.transform.localPosition = localPos;
            slot.Root.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            slot.Root.transform.localScale = new Vector3(size.x, size.y, 1f);

            if (Mathf.Abs(slot.U0 - u0) > 0.0001f || Mathf.Abs(slot.V0 - v0) > 0.0001f ||
                Mathf.Abs(slot.U1 - u1) > 0.0001f || Mathf.Abs(slot.V1 - v1) > 0.0001f)
            {
                slot.U0 = u0;
                slot.V0 = v0;
                slot.U1 = u1;
                slot.V1 = v1;
                scratchUvs[0] = new Vector2(u0, v0);
                scratchUvs[1] = new Vector2(u1, v0);
                scratchUvs[2] = new Vector2(u1, v1);
                scratchUvs[3] = new Vector2(u0, v1);
                slot.QuadMesh.uv = scratchUvs;
            }

            if (slot.Mat.mainTexture != tex)
                slot.Mat.mainTexture = tex;
            slot.Mat.color = tint;
            if (!slot.Root.activeSelf)
                slot.Root.SetActive(true);
            activeQuads.Add(slot);
        }

        static Color GetHpThresholdColor(float hpRatio)
        {
            if (hpRatio >= 0.75f) return new Color(0.18f, 0.80f, 0.34f, 1f);
            if (hpRatio >= 0.35f) return new Color(0.98f, 0.84f, 0.08f, 1f);
            return new Color(0.95f, 0.16f, 0.15f, 1f);
        }

        static Mesh BuildUnitQuadMesh()
        {
            var m = new Mesh { name = "HudUnitQuad" };
            m.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f),
                new Vector3( 0.5f, -0.5f, 0f),
                new Vector3( 0.5f,  0.5f, 0f),
                new Vector3(-0.5f,  0.5f, 0f)
            };
            m.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
            m.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            m.triangles = new[] { 0, 3, 2, 0, 2, 1 };
            return m;
        }

        static void EnsureSharedResources()
        {
            if (sSpritesShader == null)
                sSpritesShader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Transparent");

            if (sTrackVertTex == null)
            {
                sTrackVertTex = MakePillTex(32, 160, 12f, new Color(0.24f, 0.27f, 0.32f, 0.68f), new Color(0.06f, 0.08f, 0.11f, 0.60f));
                sFillVertTex = MakePillTex(32, 160, 12f, new Color(1f, 1f, 1f, 0.98f), new Color(0.90f, 0.90f, 0.90f, 0.88f));
                sTrackHorizTex = MakePillTex(160, 24, 10f, new Color(0.24f, 0.27f, 0.32f, 0.68f), new Color(0.06f, 0.08f, 0.11f, 0.60f));
                sFillHorizTex = MakePillTex(160, 24, 10f, new Color(1f, 1f, 1f, 0.98f), new Color(0.90f, 0.90f, 0.90f, 0.88f));
            }
        }

        static Texture2D MakePillTex(int w, int h, float radius, Color fillCol, Color borderCol)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;

            float hw = w * 0.5f;
            float hh = h * 0.5f;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float qx = Mathf.Abs((x + 0.5f) - hw) - (hw - radius);
                    float qy = Mathf.Abs((y + 0.5f) - hh) - (hh - radius);
                    float dist = Mathf.Sqrt(Mathf.Max(qx, 0f) * Mathf.Max(qx, 0f) + Mathf.Max(qy, 0f) * Mathf.Max(qy, 0f))
                                 + Mathf.Min(Mathf.Max(qx, qy), 0f) - radius;
                    if (dist > 0.6f)
                    {
                        tex.SetPixel(x, y, Color.clear);
                        continue;
                    }
                    float aa = Mathf.Clamp01(0.6f - dist);
                    Color c = dist > -1.5f ? borderCol : fillCol;
                    c.a *= aa;
                    tex.SetPixel(x, y, c);
                }
            }
            tex.Apply();
            return tex;
        }

        // Fuente bitmap nítida 5x7 escalada 3x con contorno oscuro para etiquetas 3D ("60 / 90", "10 / 50")
        static readonly Dictionary<char, string[]> sHudGlyphs = new Dictionary<char, string[]>
        {
            { '0', new[] { "01110", "10001", "10011", "10101", "11001", "10001", "01110" } },
            { '1', new[] { "00100", "01100", "00100", "00100", "00100", "00100", "01110" } },
            { '2', new[] { "01110", "10001", "00001", "00110", "01000", "10000", "11111" } },
            { '3', new[] { "11110", "00001", "00001", "01110", "00001", "00001", "11110" } },
            { '4', new[] { "00010", "00110", "01010", "10010", "11111", "00010", "00010" } },
            { '5', new[] { "11111", "10000", "11110", "00001", "00001", "10001", "01110" } },
            { '6', new[] { "00110", "01000", "10000", "11110", "10001", "10001", "01110" } },
            { '7', new[] { "11111", "00001", "00010", "00100", "01000", "01000", "01000" } },
            { '8', new[] { "01110", "10001", "10001", "01110", "10001", "10001", "01110" } },
            { '9', new[] { "01110", "10001", "10001", "01111", "00001", "00010", "01100" } },
            { '/', new[] { "00001", "00010", "00100", "00100", "01000", "10000", "00000" } },
            { ' ', new[] { "00000", "00000", "00000", "00000", "00000", "00000", "00000" } },
        };

        static Texture2D GetOrCreateHpTextTexture(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            if (sTextTexCache.TryGetValue(text, out var cached) && cached != null)
                return cached;

            const int scale = 3;
            const int charW = 5 * scale;
            const int charH = 7 * scale;
            const int adv = charW + 3;
            const int pad = 4;

            int texW = text.Length * adv - 3 + pad * 2;
            int texH = charH + pad * 2;
            var tex = new Texture2D(texW, texH, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;

            var buf = new Color32[texW * texH];
            var mask = new bool[texW * texH];

            for (int i = 0; i < text.Length; i++)
            {
                if (!sHudGlyphs.TryGetValue(text[i], out string[] rows)) continue;
                int x0 = pad + i * adv;
                for (int r = 0; r < 7; r++)
                {
                    int pyBase = pad + (6 - r) * scale;
                    for (int c = 0; c < 5; c++)
                    {
                        if (rows[r][c] != '1') continue;
                        int pxBase = x0 + c * scale;
                        for (int sy = 0; sy < scale; sy++)
                            for (int sx = 0; sx < scale; sx++)
                                mask[(pyBase + sy) * texW + (pxBase + sx)] = true;
                    }
                }
            }

            // Sombra / contorno oscuro alrededor de los glifos para máxima legibilidad
            Color32 outline = new Color32(10, 12, 18, 225);
            Color32 fill = new Color32(255, 255, 255, 255);
            for (int y = 0; y < texH; y++)
            {
                for (int x = 0; x < texW; x++)
                {
                    int idx = y * texW + x;
                    if (mask[idx])
                    {
                        buf[idx] = fill;
                        continue;
                    }
                    bool near = false;
                    for (int dy = -2; dy <= 2 && !near; dy++)
                    {
                        int ny = y + dy;
                        if (ny < 0 || ny >= texH) continue;
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            int nx = x + dx;
                            if (nx < 0 || nx >= texW) continue;
                            if (dx * dx + dy * dy <= 5 && mask[ny * texW + nx]) { near = true; break; }
                        }
                    }
                    if (near) buf[idx] = outline;
                }
            }

            tex.SetPixels32(buf);
            tex.Apply();
            sTextTexCache[text] = tex;
            return tex;
        }
    }
}
