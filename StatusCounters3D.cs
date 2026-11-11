// TCG-PlayKyn: 3D acrylic damage counter tokens and condition markers pooling
using System;
using System.Collections.Generic;
using UnityEngine;

namespace TCGPlayKyn
{
    /// <summary>
    /// F6: Sistema 3D de Fichas de Daño (10, 50, 100), Marcadores 3D de Estado (Veneno, Escudo Defender, PlusPower)
    /// y Textos Flotantes 3D->Pantalla (-30, +20 PS, ¡DORMIDO!, etc.) sobre los Pokémon en la mesa física.
    /// </summary>
    public class StatusCounters3D
    {
        // Dimensiones físicas en metros de las fichas acrílicas sobre la carta (carta = 6.3 x 8.8 cm)
        public const float DamageChipDiameter = 0.0155f;
        public const float DamageChipHeight = 0.0018f;
        public const float StatusChipDiameter = 0.0175f;

        public enum ChipKind
        {
            Damage10,
            Damage50,
            Damage100,
            Poison,
            Defender,
            PlusPower
        }

        class ChipView3D
        {
            public GameObject Root;
            public MeshRenderer RimRenderer;
            public MeshRenderer TopRenderer;
            public ChipKind Kind;
        }

        public class FloatingText3D
        {
            public Vector3 WorldOrigin;
            public string Text;
            public Color Color;
            public float Elapsed;
            public float Duration;
            public float FontScale;
        }

        readonly Transform poolParent;
        readonly List<ChipView3D> activeChips = new List<ChipView3D>();
        readonly Stack<ChipView3D> inactiveChips = new Stack<ChipView3D>();

        // Seguimiento de daño/estado previo por instancia de PokemonInPlay para disparar números flotantes y destellos
        readonly Dictionary<PokemonInPlay, int> lastKnownDamage = new Dictionary<PokemonInPlay, int>();
        readonly Dictionary<PokemonInPlay, Status> lastKnownStatus = new Dictionary<PokemonInPlay, Status>();
        readonly Dictionary<PokemonInPlay, bool> lastKnownPoison = new Dictionary<PokemonInPlay, bool>();
        readonly Dictionary<PokemonInPlay, float> smoothYawByPip = new Dictionary<PokemonInPlay, float>();
        readonly Dictionary<PokemonInPlay, float> hitFlashTimerByPip = new Dictionary<PokemonInPlay, float>();

        public static readonly List<FloatingText3D> ActivePopups = new List<FloatingText3D>();

        static Mesh sCylinderMesh;
        static Mesh sQuadMesh;
        static readonly Dictionary<ChipKind, Material> sTopMaterials = new Dictionary<ChipKind, Material>();
        static readonly Dictionary<ChipKind, Material> sRimMaterials = new Dictionary<ChipKind, Material>();

        public StatusCounters3D(Transform parent)
        {
            var go = new GameObject("PokeTCG_StatusCounters3D");
            go.transform.SetParent(parent, false);
            poolParent = go.transform;
            EnsureSharedResources();
        }

        public void ResetTracking()
        {
            lastKnownDamage.Clear();
            lastKnownStatus.Clear();
            lastKnownPoison.Clear();
            smoothYawByPip.Clear();
            hitFlashTimerByPip.Clear();
            ActivePopups.Clear();
        }

        public void BeginFrame()
        {
            for (int i = 0; i < activeChips.Count; i++)
            {
                var chip = activeChips[i];
                if (chip != null && chip.Root != null)
                {
                    chip.Root.SetActive(false);
                    inactiveChips.Push(chip);
                }
            }
            activeChips.Clear();

            // Actualizar tiempos de vida de los textos flotantes
            float dt = Time.deltaTime;
            for (int i = ActivePopups.Count - 1; i >= 0; i--)
            {
                ActivePopups[i].Elapsed += dt;
                if (ActivePopups[i].Elapsed >= ActivePopups[i].Duration)
                {
                    ActivePopups.RemoveAt(i);
                }
            }

            // Actualizar temporizadores de destello de impacto por Pokémon
            var keys = new List<PokemonInPlay>(hitFlashTimerByPip.Keys);
            for (int i = 0; i < keys.Count; i++)
            {
                var k = keys[i];
                float rem = hitFlashTimerByPip[k] - dt;
                if (rem <= 0f)
                    hitFlashTimerByPip.Remove(k);
                else
                    hitFlashTimerByPip[k] = rem;
            }
        }

        /// <summary>
        /// Devuelve el ángulo físico suavizado de la carta según su condición especial:
        /// Normal = 0°, Dormido = -90°, Paralizado = +90°, Confundido = 180°.
        /// </summary>
        public float GetSmoothedStatusYaw(PokemonInPlay pokemon, bool isActiveSlot)
        {
            if (pokemon == null) return 0f;

            float targetYaw = 0f;
            if (isActiveSlot)
            {
                if (pokemon.Status == Status.Asleep) targetYaw = -90f;
                else if (pokemon.Status == Status.Paralyzed) targetYaw = 90f;
                else if (pokemon.Status == Status.Confused) targetYaw = 180f;
            }

            if (!smoothYawByPip.TryGetValue(pokemon, out float currentYaw))
            {
                smoothYawByPip[pokemon] = targetYaw;
                return targetYaw;
            }

            float nextYaw = Mathf.MoveTowardsAngle(currentYaw, targetYaw, Time.deltaTime * 340f);
            smoothYawByPip[pokemon] = nextYaw;
            return nextYaw;
        }

        /// <summary>
        /// Calcula la capa de tinte animada (overlay con alfa) que se dibuja sobre la carta 3D
        /// según su condición especial o si acaba de recibir un impacto.
        /// </summary>
        public Color GetAnimatedCardTintOverlay(PokemonInPlay pokemon)
        {
            if (pokemon == null) return Color.clear;

            // 1) Destello rojo intenso breve cuando el Pokémon acaba de recibir daño
            if (hitFlashTimerByPip.TryGetValue(pokemon, out float flashRem) && flashRem > 0f)
            {
                float flashStrength = Mathf.Clamp01(flashRem / 0.38f);
                return new Color(1f, 0.18f, 0.16f, 0.46f * flashStrength);
            }

            float pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * 4.6f);

            // 2) Tinte pulsante según condición especial
            if (pokemon.Status == Status.Asleep)
            {
                return new Color(0.32f, 0.58f, 1.00f, 0.14f + 0.15f * pulse);
            }
            if (pokemon.Status == Status.Paralyzed)
            {
                float spark = 0.5f + 0.5f * Mathf.Sin(Time.time * 10.5f);
                return new Color(1.00f, 0.90f, 0.18f, 0.14f + 0.18f * spark);
            }
            if (pokemon.Status == Status.Confused)
            {
                return new Color(0.95f, 0.35f, 0.95f, 0.13f + 0.15f * pulse);
            }
            if (pokemon.Poisoned)
            {
                return new Color(0.62f, 0.22f, 0.92f, 0.14f + 0.16f * pulse);
            }

            return Color.clear;
        }

        /// <summary>
        /// Detecta cambios de daño/curación/estado en un Pokémon para lanzar textos flotantes
        /// y coloca las fichas 3D físicas de daño (100, 50, 10) y marcadores 3D encima de la carta.
        /// </summary>
        public void SyncPokemonCounters(
            Transform slotAnchor,
            PokemonInPlay pokemon,
            float cardSurfaceY,
            float slotScale,
            int currentTurn)
        {
            if (slotAnchor == null || pokemon == null) return;

            DetectChangesAndSpawnPopups(slotAnchor, pokemon, cardSurfaceY);

            // 1) Desglosar el daño acumulado en fichas oficiales de 100, 50 y 10
            int remainingDmg = Mathf.Max(0, pokemon.Damage);
            if (remainingDmg > 0)
            {
                var chipStack = new List<ChipKind>();
                while (remainingDmg >= 100) { chipStack.Add(ChipKind.Damage100); remainingDmg -= 100; }
                while (remainingDmg >= 50) { chipStack.Add(ChipKind.Damage50); remainingDmg -= 50; }
                while (remainingDmg >= 10) { chipStack.Add(ChipKind.Damage10); remainingDmg -= 10; }
                if (remainingDmg > 0) chipStack.Add(ChipKind.Damage10);

                // Distribuir en hasta 2 pequeñas pilas sobre el costado derecho de la carta para que no tapen el arte central
                for (int i = 0; i < chipStack.Count; i++)
                {
                    int pileIndex = i % 2;
                    int heightIndex = i / 2;
                    float localX = 0.017f * slotScale;
                    float localZ = (0.014f - pileIndex * 0.018f) * slotScale;
                    float localY = cardSurfaceY + 0.0005f + heightIndex * (DamageChipHeight + 0.0002f);

                    // Rebote sutil si acaba de recibir golpe
                    if (i == chipStack.Count - 1 && hitFlashTimerByPip.TryGetValue(pokemon, out float fRem))
                    {
                        localY += Mathf.Sin((fRem / 0.38f) * Mathf.PI) * 0.008f;
                    }

                    SpawnChip(slotAnchor, new Vector3(localX, localY, localZ), DamageChipDiameter * slotScale, chipStack[i]);
                }
            }

            // 2) Marcador 3D físico de Veneno en la esquina superior izquierda de la carta
            if (pokemon.Poisoned)
            {
                float pulseScale = 1f + 0.07f * Mathf.Sin(Time.time * 5.2f);
                Vector3 poisonPos = new Vector3(-0.018f * slotScale, cardSurfaceY + 0.0006f, 0.024f * slotScale);
                SpawnChip(slotAnchor, poisonPos, StatusChipDiameter * slotScale * pulseScale, ChipKind.Poison);
            }

            // 3) Marcador 3D de Defender (-20 daño recibido) o PlusPower (+10 daño de ataque)
            if (pokemon.DefenderUntilTurn >= currentTurn)
            {
                Vector3 defPos = new Vector3(-0.018f * slotScale, cardSurfaceY + 0.0006f, 0.004f * slotScale);
                SpawnChip(slotAnchor, defPos, StatusChipDiameter * slotScale * 0.92f, ChipKind.Defender);
            }
            if (pokemon.PlusPower > 0)
            {
                for (int p = 0; p < pokemon.PlusPower; p++)
                {
                    Vector3 ppPos = new Vector3(-0.018f * slotScale, cardSurfaceY + 0.0006f + p * (DamageChipHeight + 0.0002f), -0.015f * slotScale);
                    SpawnChip(slotAnchor, ppPos, StatusChipDiameter * slotScale * 0.92f, ChipKind.PlusPower);
                }
            }
        }

        /// <summary>
        /// Devuelve el desplazamiento de retroceso/vibración en 3D cuando el Pokémon acaba de recibir un golpe.
        /// </summary>
        public Vector3 GetHitShakeLocalOffset(PokemonInPlay pokemon)
        {
            if (pokemon != null && hitFlashTimerByPip.TryGetValue(pokemon, out float rem) && rem > 0f)
            {
                float damper = rem / 0.38f;
                float shakeX = Mathf.Sin(Time.time * 65f) * 0.0032f * damper;
                float shakeZ = -Mathf.Sin(damper * Mathf.PI) * 0.0055f;
                return new Vector3(shakeX, 0f, shakeZ);
            }
            return Vector3.zero;
        }

        public static void SpawnPopup(Vector3 worldOrigin, string text, Color color, float duration = 1.25f, float fontScale = 1f)
        {
            ActivePopups.Add(new FloatingText3D
            {
                WorldOrigin = worldOrigin,
                Text = text,
                Color = color,
                Duration = duration,
                FontScale = fontScale
            });
        }

        void DetectChangesAndSpawnPopups(Transform slotAnchor, PokemonInPlay pokemon, float cardSurfaceY)
        {
            Vector3 worldTop = slotAnchor.TransformPoint(new Vector3(0f, cardSurfaceY + 0.022f, 0f));

            // 1) Cambio de daño o curación
            if (!lastKnownDamage.TryGetValue(pokemon, out int prevDmg))
            {
                lastKnownDamage[pokemon] = pokemon.Damage;
                if (pokemon.Damage > 0)
                {
                    hitFlashTimerByPip[pokemon] = 0.38f;
                    SpawnPopup(worldTop, $"-{pokemon.Damage}", new Color(1f, 0.30f, 0.25f, 1f), 1.30f, 1.15f);
                }
            }
            else if (pokemon.Damage != prevDmg)
            {
                int delta = pokemon.Damage - prevDmg;
                lastKnownDamage[pokemon] = pokemon.Damage;
                if (delta > 0)
                {
                    hitFlashTimerByPip[pokemon] = 0.38f;
                    SpawnPopup(worldTop, $"-{delta}", new Color(1f, 0.28f, 0.22f, 1f), 1.35f, delta >= 40 ? 1.30f : 1.12f);
                }
                else if (delta < 0)
                {
                    SpawnPopup(worldTop, $"+{-delta} PS", new Color(0.28f, 0.96f, 0.52f, 1f), 1.30f, 1.10f);
                }
            }

            // 2) Cambio de Condición Especial (Dormido, Confundido, Paralizado)
            if (!lastKnownStatus.TryGetValue(pokemon, out Status prevStatus))
            {
                lastKnownStatus[pokemon] = pokemon.Status;
            }
            else if (pokemon.Status != prevStatus)
            {
                lastKnownStatus[pokemon] = pokemon.Status;
                if (pokemon.Status == Status.Asleep)
                    SpawnPopup(worldTop + Vector3.up * 0.015f, "¡DORMIDO!", new Color(0.58f, 0.80f, 1f, 1f), 1.35f, 1.0f);
                else if (pokemon.Status == Status.Confused)
                    SpawnPopup(worldTop + Vector3.up * 0.015f, "¡CONFUNDIDO!", new Color(0.96f, 0.65f, 1f, 1f), 1.35f, 1.0f);
                else if (pokemon.Status == Status.Paralyzed)
                    SpawnPopup(worldTop + Vector3.up * 0.015f, "¡PARALIZADO!", new Color(1f, 0.90f, 0.28f, 1f), 1.35f, 1.0f);
            }

            // 3) Cambio de Veneno
            if (!lastKnownPoison.TryGetValue(pokemon, out bool prevPoison))
            {
                lastKnownPoison[pokemon] = pokemon.Poisoned;
            }
            else if (pokemon.Poisoned != prevPoison)
            {
                lastKnownPoison[pokemon] = pokemon.Poisoned;
                if (pokemon.Poisoned)
                    SpawnPopup(worldTop + Vector3.up * 0.025f, "¡ENVENENADO!", new Color(0.82f, 0.45f, 1f, 1f), 1.35f, 1.0f);
            }
        }

        void SpawnChip(Transform parentAnchor, Vector3 localPos, float diameter, ChipKind kind)
        {
            ChipView3D chip = inactiveChips.Count > 0 ? inactiveChips.Pop() : CreateChipObject();
            chip.Kind = kind;
            chip.Root.transform.SetParent(parentAnchor, false);
            chip.Root.transform.localPosition = localPos;
            chip.Root.transform.localRotation = Quaternion.identity;
            chip.Root.transform.localScale = new Vector3(diameter, DamageChipHeight, diameter);

            if (sRimMaterials.TryGetValue(kind, out Material rimMat))
                chip.RimRenderer.sharedMaterial = rimMat;
            if (sTopMaterials.TryGetValue(kind, out Material topMat))
                chip.TopRenderer.sharedMaterial = topMat;

            chip.Root.SetActive(true);
            activeChips.Add(chip);
        }

        ChipView3D CreateChipObject()
        {
            var root = new GameObject("PokeTCG_Chip3D");
            root.transform.SetParent(poolParent, false);

            // Cuerpo cilíndrico de la ficha acrílica (el cilindro primitivo de Unity mide 2 unidades de alto y 1 de diámetro)
            var cylGo = new GameObject("ChipRim");
            cylGo.transform.SetParent(root.transform, false);
            cylGo.transform.localPosition = new Vector3(0f, 0.5f, 0f);
            cylGo.transform.localScale = new Vector3(1f, 0.5f, 1f);
            cylGo.AddComponent<MeshFilter>().sharedMesh = sCylinderMesh;
            var rimRenderer = cylGo.AddComponent<MeshRenderer>();
            rimRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            // Cara superior circular con el valor ("10", "50", "100", "VEN", etc.)
            var topGo = new GameObject("ChipFace");
            topGo.transform.SetParent(root.transform, false);
            topGo.transform.localPosition = new Vector3(0f, 1.03f, 0f);
            topGo.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            topGo.transform.localScale = new Vector3(0.96f, 0.96f, 1f);
            topGo.AddComponent<MeshFilter>().sharedMesh = sQuadMesh;
            var topRenderer = topGo.AddComponent<MeshRenderer>();
            topRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            return new ChipView3D
            {
                Root = root,
                RimRenderer = rimRenderer,
                TopRenderer = topRenderer
            };
        }

        static void EnsureSharedResources()
        {
            if (sCylinderMesh == null)
            {
                var tmp = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                sCylinderMesh = tmp.GetComponent<MeshFilter>().sharedMesh;
                UnityEngine.Object.DestroyImmediate(tmp);
            }
            if (sQuadMesh == null)
            {
                var tmp = GameObject.CreatePrimitive(PrimitiveType.Quad);
                sQuadMesh = tmp.GetComponent<MeshFilter>().sharedMesh;
                UnityEngine.Object.DestroyImmediate(tmp);
            }
            if (sTopMaterials.Count == 0)
            {
                RegisterChipVisual(ChipKind.Damage10, new Color(0.95f, 0.72f, 0.12f, 1f), new Color(0.20f, 0.14f, 0.04f, 1f), "10");
                RegisterChipVisual(ChipKind.Damage50, new Color(0.94f, 0.36f, 0.14f, 1f), Color.white, "50");
                RegisterChipVisual(ChipKind.Damage100, new Color(0.84f, 0.14f, 0.22f, 1f), new Color(1f, 0.90f, 0.35f, 1f), "100");
                RegisterChipVisual(ChipKind.Poison, new Color(0.58f, 0.18f, 0.82f, 1f), new Color(0.65f, 1f, 0.45f, 1f), "VEN");
                RegisterChipVisual(ChipKind.Defender, new Color(0.20f, 0.56f, 0.92f, 1f), Color.white, "DEF");
                RegisterChipVisual(ChipKind.PlusPower, new Color(0.92f, 0.22f, 0.26f, 1f), Color.white, "+10");
            }
        }

        static void RegisterChipVisual(ChipKind kind, Color bodyColor, Color textColor, string label)
        {
            Color rimColor = new Color(bodyColor.r * 0.68f, bodyColor.g * 0.68f, bodyColor.b * 0.68f, 1f);
            sRimMaterials[kind] = CardView.CreateUnlitMaterial(rimColor, null, false);
            Texture2D faceTex = BuildCircularChipTexture(64, bodyColor, textColor, label);
            sTopMaterials[kind] = CardView.CreateUnlitMaterial(Color.white, faceTex, true);
        }

        // Pequeña fuente bitmap 3x5 limpia para grabar "10", "50", "100", "VEN", "DEF", "+10" sobre las fichas 3D
        static readonly Dictionary<char, string[]> sTinyGlyphs = new Dictionary<char, string[]>
        {
            { '0', new[] { "111", "101", "101", "101", "111" } },
            { '1', new[] { "010", "110", "010", "010", "111" } },
            { '5', new[] { "111", "100", "111", "001", "111" } },
            { '+', new[] { "000", "010", "111", "010", "000" } },
            { 'V', new[] { "101", "101", "101", "101", "010" } },
            { 'E', new[] { "111", "100", "110", "100", "111" } },
            { 'N', new[] { "101", "111", "111", "101", "101" } },
            { 'D', new[] { "110", "101", "101", "101", "110" } },
            { 'F', new[] { "111", "100", "110", "100", "100" } },
        };

        static Texture2D BuildCircularChipTexture(int size, Color bgCol, Color fgCol, string text)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;

            float half = (size - 1) * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x - half) / half;
                    float dy = (y - half) / half;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    if (r > 0.98f)
                    {
                        tex.SetPixel(x, y, Color.clear);
                        continue;
                    }

                    // Anillo exterior biselado brillante
                    Color c = bgCol;
                    if (r > 0.80f && r <= 0.90f)
                        c = Color.Lerp(bgCol, Color.white, 0.38f);
                    else if (r > 0.90f)
                        c = new Color(bgCol.r * 0.55f, bgCol.g * 0.55f, bgCol.b * 0.55f, 1f);

                    float alpha = r > 0.93f ? Mathf.Clamp01((0.98f - r) / 0.05f) : 1f;
                    c.a = alpha;
                    tex.SetPixel(x, y, c);
                }
            }

            // Dibujar los caracteres centrados sobre la ficha
            int scale = text.Length >= 3 ? 3 : 4;
            int glyphW = 3 * scale;
            int glyphH = 5 * scale;
            int spacing = scale;
            int totalW = text.Length * glyphW + (text.Length - 1) * spacing;
            int startX = (size - totalW) / 2;
            int startY = (size - glyphH) / 2;

            for (int idx = 0; idx < text.Length; idx++)
            {
                if (!sTinyGlyphs.TryGetValue(text[idx], out string[] rows)) continue;
                int gx = startX + idx * (glyphW + spacing);
                for (int row = 0; row < 5; row++)
                {
                    // row 0 es la fila superior del glifo -> mayor Y en la textura
                    int pyBase = startY + (4 - row) * scale;
                    for (int col = 0; col < 3; col++)
                    {
                        if (rows[row][col] != '1') continue;
                        int pxBase = gx + col * scale;
                        for (int sy = 0; sy < scale; sy++)
                        {
                            for (int sx = 0; sx < scale; sx++)
                            {
                                tex.SetPixel(pxBase + sx, pyBase + sy, fgCol);
                            }
                        }
                    }
                }
            }

            tex.Apply();
            return tex;
        }
    }
}
