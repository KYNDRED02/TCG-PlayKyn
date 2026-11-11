// TCG-PlayKyn: 3D metallic coin physics, parabolic launch, bounce and landing settles
using System;
using UnityEngine;

namespace TCGPlayKyn
{
    /// <summary>
    /// F7: Moneda 3D física sobre el tapete con caras Pikachu (Cara / Heads) y Pokéball (Cruz / Tails),
    /// canto metálico estriado, sombra dinámica proyectada sobre la mesa, lanzamiento parabólico,
    /// rebote doble, vibración de canto (rim wobble) y anillo luminoso al asentarse.
    /// </summary>
    public class Coin3D
    {
        public const float CoinDiameter = 0.058f;
        public const float CoinThickness = 0.0044f;

        enum CoinState
        {
            Hidden,
            ChoosePreview,
            Flipping,
            RestingOnMat
        }

        readonly Transform playmatRoot;
        readonly GameObject rootGo;
        readonly Transform discPivot;
        readonly MeshRenderer headsRenderer;
        readonly MeshRenderer tailsRenderer;
        readonly MeshRenderer glintHeadsRenderer;
        readonly MeshRenderer glintTailsRenderer;
        readonly GameObject shadowGo;
        readonly MeshRenderer shadowRenderer;
        readonly Material shadowMat;
        readonly GameObject ringGo;
        readonly MeshRenderer ringRenderer;
        readonly Material ringMat;
        readonly Material glintHeadsMat;
        readonly Material glintTailsMat;

        CoinState state = CoinState.Hidden;
        bool currentResultHeads = true;

        // Parámetros del lanzamiento activo (en espacio local de PlaymatRoot)
        Vector3 tossStartLocal;
        Vector3 tossLandLocal;
        Vector3 restTargetLocal;
        float tossPeakHeight = 0.22f;
        float tossElapsed;
        float tossDuration = 1.65f;
        int fullFlipsCount = 5;
        float axialSpinSpeed = 95f;
        bool playedBounce1Sound;
        bool playedBounce2Sound;
        bool triggeredSettleFx;
        string settlePopupLabel;

        // Animación del anillo de impacto en la mesa
        float ringTimer;
        const float RingDuration = 0.48f;
        Color ringBaseColor = Color.white;

        // Recursos geométricos y materiales compartidos
        static Mesh sHeadsDiscMesh;
        static Mesh sTailsDiscMesh;
        static Mesh sRimCylinderMesh;
        static Mesh sQuadMesh;
        static Material sRimMat;
        static Material sHeadsMat;
        static Material sTailsMat;
        static Texture2D sShadowTex;
        static Texture2D sRingTex;
        static Texture2D sGlintTex;

        public bool IsFlipping => state == CoinState.Flipping;

        public Coin3D(Transform playmatRoot, Vector3 defaultRestLocalPos)
        {
            this.playmatRoot = playmatRoot;
            restTargetLocal = defaultRestLocalPos;
            EnsureSharedResources();

            // Sombra circular independiente pegada siempre a la superficie del Playmat
            shadowGo = new GameObject("PokeTCG_CoinShadow3D");
            shadowGo.transform.SetParent(playmatRoot, false);
            shadowGo.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            shadowGo.AddComponent<MeshFilter>().sharedMesh = sQuadMesh;
            shadowRenderer = shadowGo.AddComponent<MeshRenderer>();
            shadowMat = CardView.CreateUnlitMaterial(new Color(0f, 0f, 0f, 0.48f), sShadowTex, true);
            shadowRenderer.sharedMaterial = shadowMat;
            shadowRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            shadowGo.SetActive(false);

            // Anillo luminoso de aterrizaje sobre el tapete
            ringGo = new GameObject("PokeTCG_CoinImpactRing3D");
            ringGo.transform.SetParent(playmatRoot, false);
            ringGo.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            ringGo.AddComponent<MeshFilter>().sharedMesh = sQuadMesh;
            ringRenderer = ringGo.AddComponent<MeshRenderer>();
            ringMat = CardView.CreateUnlitMaterial(Color.clear, sRingTex, true);
            ringRenderer.sharedMaterial = ringMat;
            ringRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            ringGo.SetActive(false);

            // Raíz 3D de la moneda y pivote de rotación
            rootGo = new GameObject("PokeTCG_Coin3D");
            rootGo.transform.SetParent(playmatRoot, false);

            discPivot = new GameObject("DiscPivot").transform;
            discPivot.SetParent(rootGo.transform, false);

            // 1) Canto cilíndrico metálico estriado
            var rimGo = new GameObject("MilledRim");
            rimGo.transform.SetParent(discPivot, false);
            rimGo.transform.localScale = new Vector3(CoinDiameter, CoinThickness, CoinDiameter);
            rimGo.AddComponent<MeshFilter>().sharedMesh = sRimCylinderMesh;
            var rimMr = rimGo.AddComponent<MeshRenderer>();
            rimMr.sharedMaterial = sRimMat;
            rimMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            // 2) Cara superior (+Y): Pikachu dorado (CARA / Heads)
            var headsGo = new GameObject("HeadsFace_Pikachu");
            headsGo.transform.SetParent(discPivot, false);
            headsGo.transform.localScale = new Vector3(CoinDiameter, CoinThickness, CoinDiameter);
            headsGo.AddComponent<MeshFilter>().sharedMesh = sHeadsDiscMesh;
            headsRenderer = headsGo.AddComponent<MeshRenderer>();
            headsRenderer.sharedMaterial = sHeadsMat;
            headsRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            // 3) Cara inferior (-Y): Pokéball negra en relieve (CRUZ / Tails)
            var tailsGo = new GameObject("TailsFace_Pokeball");
            tailsGo.transform.SetParent(discPivot, false);
            tailsGo.transform.localScale = new Vector3(CoinDiameter, CoinThickness, CoinDiameter);
            tailsGo.AddComponent<MeshFilter>().sharedMesh = sTailsDiscMesh;
            tailsRenderer = tailsGo.AddComponent<MeshRenderer>();
            tailsRenderer.sharedMaterial = sTailsMat;
            tailsRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            // 4) Brillo especular metálico dinámico sobre ambas caras al girar
            var ghGo = new GameObject("HeadsMetallicGlint");
            ghGo.transform.SetParent(discPivot, false);
            ghGo.transform.localPosition = new Vector3(0f, CoinThickness * 0.5f + 0.00016f, 0f);
            ghGo.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            ghGo.transform.localScale = new Vector3(CoinDiameter * 0.96f, CoinDiameter * 0.96f, 1f);
            ghGo.AddComponent<MeshFilter>().sharedMesh = sQuadMesh;
            glintHeadsRenderer = ghGo.AddComponent<MeshRenderer>();
            glintHeadsMat = CardView.CreateUnlitMaterial(new Color(1f, 0.96f, 0.78f, 0f), sGlintTex, true);
            glintHeadsRenderer.sharedMaterial = glintHeadsMat;
            glintHeadsRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            var gtGo = new GameObject("TailsMetallicGlint");
            gtGo.transform.SetParent(discPivot, false);
            gtGo.transform.localPosition = new Vector3(0f, -CoinThickness * 0.5f - 0.00016f, 0f);
            gtGo.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            gtGo.transform.localScale = new Vector3(CoinDiameter * 0.96f, CoinDiameter * 0.96f, 1f);
            gtGo.AddComponent<MeshFilter>().sharedMesh = sQuadMesh;
            glintTailsRenderer = gtGo.AddComponent<MeshRenderer>();
            glintTailsMat = CardView.CreateUnlitMaterial(new Color(0.82f, 0.90f, 1f, 0f), sGlintTex, true);
            glintTailsRenderer.sharedMaterial = glintTailsMat;
            glintTailsRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            rootGo.SetActive(false);
        }

        public void SetRestLocalPosition(Vector3 localPos)
        {
            restTargetLocal = localPos;
        }

        public void Reset()
        {
            state = CoinState.Hidden;
            ringTimer = 0f;
            if (rootGo != null) rootGo.SetActive(false);
            if (shadowGo != null) shadowGo.SetActive(false);
            if (ringGo != null) ringGo.SetActive(false);
        }

        /// <summary>
        /// Muestra la moneda 3D flotando y girando suavemente en el centro del tapete mientras el jugador elige Cara o Cruz.
        /// </summary>
        public void ShowChoosePreview(Vector3 centerLocalPos)
        {
            if (state == CoinState.Flipping) return;
            RefreshFaceTextures();
            state = CoinState.ChoosePreview;
            tossLandLocal = centerLocalPos;
            if (!rootGo.activeSelf) rootGo.SetActive(true);
            if (!shadowGo.activeSelf) shadowGo.SetActive(true);
        }

        /// <summary>
        /// Lanza la moneda 3D inicial desde el borde del jugador hacia el centro de la Pokéball del Playmat.
        /// </summary>
        public void LaunchStartToss(Vector3 launchOriginLocal, Vector3 centerLandLocal, bool headsResult, float duration)
        {
            RefreshFaceTextures();
            state = CoinState.Flipping;
            currentResultHeads = headsResult;
            tossStartLocal = launchOriginLocal;
            tossLandLocal = centerLandLocal;
            tossPeakHeight = 0.235f;
            tossElapsed = 0f;
            tossDuration = Mathf.Max(0.8f, duration);
            fullFlipsCount = 5;
            axialSpinSpeed = 110f;
            playedBounce1Sound = false;
            playedBounce2Sound = false;
            triggeredSettleFx = false;
            settlePopupLabel = headsResult
                ? Loc.T("¡CARA! (Pikachu)", "HEADS! (Pikachu)")
                : Loc.T("¡CRUZ! (Pokéball)", "TAILS! (Pokéball)");

            rootGo.transform.localScale = Vector3.one * 1.12f;
            rootGo.SetActive(true);
            shadowGo.SetActive(true);
            TcgAudio.PlayCoinLaunch();
        }

        /// <summary>
        /// Lanza la moneda 3D durante la partida (ataques, entrenadores, confusión, sueño) hacia la zona despejada del tapete.
        /// </summary>
        public void LaunchGameFlip(Vector3 launchOriginLocal, Vector3 arenaLandLocal, bool headsResult, float duration, string reason)
        {
            RefreshFaceTextures();
            state = CoinState.Flipping;
            currentResultHeads = headsResult;
            tossStartLocal = launchOriginLocal;
            tossLandLocal = arenaLandLocal;
            tossPeakHeight = 0.195f;
            tossElapsed = 0f;
            tossDuration = Mathf.Max(0.65f, duration);
            fullFlipsCount = 4;
            axialSpinSpeed = 95f;
            playedBounce1Sound = false;
            playedBounce2Sound = false;
            triggeredSettleFx = false;
            settlePopupLabel = headsResult
                ? Loc.T("¡CARA!", "HEADS!")
                : Loc.T("¡CRUZ!", "TAILS!");

            rootGo.transform.localScale = Vector3.one * 1.05f;
            rootGo.SetActive(true);
            shadowGo.SetActive(true);
            TcgAudio.PlayCoinLaunch();
        }

        /// <summary>
        /// Actualiza cada frame la física/curva de la moneda 3D, sus rebotes, su brillo metálico y su sombra sobre el Playmat.
        /// </summary>
        public void SyncFrame(bool matchActive, bool inCoinTossChoice, Vector3 centerLocalPos, float matSurfaceY)
        {
            if (!matchActive)
            {
                if (state != CoinState.Hidden) Reset();
                return;
            }

            if (inCoinTossChoice && state != CoinState.Flipping)
            {
                ShowChoosePreview(centerLocalPos);
            }
            else if (!inCoinTossChoice && state == CoinState.ChoosePreview)
            {
                state = CoinState.RestingOnMat;
            }

            UpdateImpactRing(matSurfaceY);

            if (state == CoinState.Hidden) return;

            float dt = Time.deltaTime * Loc.AnimSpeed;
            float coinHalfThick = CoinThickness * 0.5f;

            if (state == CoinState.ChoosePreview)
            {
                float hoverBob = 0.115f + 0.009f * Mathf.Sin(Time.time * 2.7f);
                Vector3 previewLocal = new Vector3(centerLocalPos.x, matSurfaceY + coinHalfThick + hoverBob, centerLocalPos.z - 0.055f);
                rootGo.transform.localPosition = previewLocal;
                rootGo.transform.localScale = Vector3.one * 1.24f;

                // Giro continuo mostrando ambas caras (Pikachu dorado y Pokéball en relieve) frente a los ojos del jugador
                float spinPitch = Time.time * 105f;
                float gentleRoll = Mathf.Sin(Time.time * 1.8f) * 14f;
                discPivot.localRotation = Quaternion.Euler(spinPitch, 0f, gentleRoll);

                UpdateShadow(previewLocal, hoverBob, matSurfaceY, 1.24f);
                UpdateMetallicGlint(spinPitch, gentleRoll);
                return;
            }

            if (state == CoinState.Flipping)
            {
                tossElapsed += dt;
                float u = Mathf.Clamp01(tossElapsed / tossDuration);

                // Desplazamiento horizontal desde el lanzador hasta el punto de caída (se completa al final del primer rebote)
                float horizT = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(u / 0.74f));
                Vector3 groundPos = Vector3.Lerp(tossStartLocal, tossLandLocal, horizT);

                // Curva vertical con lanzamiento parabólico + 2 rebotes físicos sobre el neopreno
                float heightAboveMat = EvaluateBounceHeight(u, tossPeakHeight);

                // Sonidos metálicos al tocar la mesa en el 1er y 2do impacto
                if (u >= 0.56f && !playedBounce1Sound)
                {
                    playedBounce1Sound = true;
                    TcgAudio.PlayCoinBounce(1);
                }
                if (u >= 0.74f && !playedBounce2Sound)
                {
                    playedBounce2Sound = true;
                    TcgAudio.PlayCoinBounce(2);
                }

                // Rotación 3D: volteretas completas en el aire + vibración de canto (rim wobble) al asentarse
                float targetFinalPitch = currentResultHeads ? 0f : 180f;
                float totalTargetPitch = fullFlipsCount * 360f + targetFinalPitch;
                float pitchDeg;
                float rollDeg;
                float yawDeg;

                if (u < 0.74f)
                {
                    float spinU = u / 0.74f;
                    float easedSpin = 1f - Mathf.Pow(1f - spinU, 1.62f);
                    pitchDeg = easedSpin * totalTargetPitch;
                    rollDeg = Mathf.Sin(u * 20f) * 15f * (1f - spinU);
                    yawDeg = Mathf.Sin(u * 9.5f) * (axialSpinSpeed * 0.18f) * (1f - spinU);
                }
                else if (u < 0.93f)
                {
                    // Vibración metálica circular apoyada sobre el borde antes de quedar completamente plana
                    float w = (u - 0.74f) / 0.19f;
                    float tiltAmp = 16f * (1f - w) * (1f - w);
                    float phase = w * Mathf.PI * 5.5f;
                    pitchDeg = targetFinalPitch + Mathf.Cos(phase) * tiltAmp;
                    rollDeg = Mathf.Sin(phase) * tiltAmp;
                    yawDeg = 0f;
                    heightAboveMat = Mathf.Max(heightAboveMat, Mathf.Sin(tiltAmp * Mathf.Deg2Rad) * (CoinDiameter * 0.5f));
                }
                else
                {
                    pitchDeg = targetFinalPitch;
                    rollDeg = 0f;
                    yawDeg = 0f;
                }

                // Efecto luminoso y texto flotante justo cuando la moneda se asienta mostrando el resultado
                if (u >= 0.84f && !triggeredSettleFx)
                {
                    triggeredSettleFx = true;
                    TcgAudio.PlayCoinSettle(currentResultHeads);
                    TriggerImpactRing(tossLandLocal, currentResultHeads);
                    if (!string.IsNullOrEmpty(settlePopupLabel) && playmatRoot != null)
                    {
                        Vector3 popupWorld = playmatRoot.TransformPoint(new Vector3(tossLandLocal.x, matSurfaceY + 0.028f, tossLandLocal.z));
                        Color popColor = currentResultHeads
                            ? new Color(1.00f, 0.88f, 0.24f, 1f)
                            : new Color(0.55f, 0.86f, 1.00f, 1f);
                        StatusCounters3D.SpawnPopup(popupWorld, settlePopupLabel, popColor, 1.25f, 1.16f);
                    }
                }

                Vector3 coinLocal = new Vector3(groundPos.x, matSurfaceY + coinHalfThick + heightAboveMat, groundPos.z);
                rootGo.transform.localPosition = coinLocal;
                discPivot.localRotation = Quaternion.Euler(pitchDeg, yawDeg, rollDeg);

                UpdateShadow(coinLocal, heightAboveMat, matSurfaceY, rootGo.transform.localScale.x);
                UpdateMetallicGlint(pitchDeg, rollDeg);

                if (u >= 1f)
                {
                    state = CoinState.RestingOnMat;
                }
                return;
            }

            if (state == CoinState.RestingOnMat)
            {
                // Deslizarse suavemente hacia su zona de descanso lateral en el tapete (sin tapar los Pokémon Activos)
                Vector3 targetPos = new Vector3(restTargetLocal.x, matSurfaceY + coinHalfThick + 0.0003f, restTargetLocal.z);
                rootGo.transform.localPosition = Vector3.Lerp(rootGo.transform.localPosition, targetPos, dt * 6.5f);
                rootGo.transform.localScale = Vector3.Lerp(rootGo.transform.localScale, Vector3.one * 0.88f, dt * 6.5f);

                float finalPitch = currentResultHeads ? 0f : 180f;
                discPivot.localRotation = Quaternion.Slerp(discPivot.localRotation, Quaternion.Euler(finalPitch, 0f, 0f), dt * 10f);

                UpdateShadow(rootGo.transform.localPosition, 0.0003f, matSurfaceY, rootGo.transform.localScale.x);
                UpdateMetallicGlint(finalPitch, 0f);
            }
        }

        static float EvaluateBounceHeight(float u, float peakHeight)
        {
            const float startH = 0.042f;
            if (u < 0.56f)
            {
                // Parábola principal de lanzamiento hacia arriba y caída por gravedad
                float s = u / 0.56f;
                return startH * (1f - s) * (1f - s) + 4f * peakHeight * s * (1f - s);
            }
            if (u < 0.74f)
            {
                // Primer rebote sobre el tapete
                float b1 = (u - 0.56f) / 0.18f;
                return 4f * (peakHeight * 0.24f) * b1 * (1f - b1);
            }
            if (u < 0.84f)
            {
                // Segundo micro-rebote
                float b2 = (u - 0.74f) / 0.10f;
                return 4f * (peakHeight * 0.065f) * b2 * (1f - b2);
            }
            return 0f;
        }

        void UpdateShadow(Vector3 coinLocalPos, float heightAboveMat, float matSurfaceY, float scaleMul)
        {
            if (shadowGo == null || shadowMat == null) return;
            float hNorm = Mathf.Clamp01(heightAboveMat / 0.24f);
            float shadowSize = CoinDiameter * scaleMul * Mathf.Lerp(1.06f, 1.48f, hNorm);
            shadowGo.transform.localPosition = new Vector3(
                coinLocalPos.x + heightAboveMat * 0.15f,
                matSurfaceY + 0.0005f,
                coinLocalPos.z - heightAboveMat * 0.13f);
            shadowGo.transform.localScale = new Vector3(shadowSize, shadowSize, 1f);
            shadowMat.color = new Color(0f, 0f, 0f, Mathf.Lerp(0.52f, 0.15f, hNorm));
        }

        void UpdateMetallicGlint(float pitchDeg, float rollDeg)
        {
            float shimmer = Mathf.Pow(Mathf.Abs(Mathf.Sin((pitchDeg + rollDeg * 1.5f) * Mathf.Deg2Rad * 2f)), 3f);
            if (glintHeadsMat != null)
                glintHeadsMat.color = new Color(1f, 0.96f, 0.76f, 0.34f * shimmer);
            if (glintTailsMat != null)
                glintTailsMat.color = new Color(0.80f, 0.90f, 1f, 0.22f * shimmer);
        }

        void TriggerImpactRing(Vector3 landLocal, bool heads)
        {
            if (ringGo == null) return;
            ringTimer = RingDuration;
            ringBaseColor = heads
                ? new Color(1.00f, 0.85f, 0.22f, 0.88f)
                : new Color(0.45f, 0.84f, 1.00f, 0.88f);
            ringGo.transform.localPosition = new Vector3(landLocal.x, 0.0052f, landLocal.z);
            ringGo.SetActive(true);
        }

        void UpdateImpactRing(float matSurfaceY)
        {
            if (ringTimer <= 0f)
            {
                if (ringGo != null && ringGo.activeSelf) ringGo.SetActive(false);
                return;
            }
            ringTimer -= Time.deltaTime;
            float t = 1f - Mathf.Clamp01(ringTimer / RingDuration);
            float diam = CoinDiameter * Mathf.Lerp(1.05f, 2.45f, Mathf.SmoothStep(0f, 1f, t));
            Vector3 p = ringGo.transform.localPosition;
            ringGo.transform.localPosition = new Vector3(p.x, matSurfaceY + 0.0008f, p.z);
            ringGo.transform.localScale = new Vector3(diam, diam, 1f);
            ringMat.color = new Color(ringBaseColor.r, ringBaseColor.g, ringBaseColor.b, ringBaseColor.a * (1f - t));
        }

        void RefreshFaceTextures()
        {
            SpriteCache.EnsureInitialized();
            if (SpriteCache.CoinHeadsTex != null && sHeadsMat != null && sHeadsMat.mainTexture != SpriteCache.CoinHeadsTex)
                sHeadsMat.mainTexture = SpriteCache.CoinHeadsTex;
            if (SpriteCache.CoinTailsTex != null && sTailsMat != null && sTailsMat.mainTexture != SpriteCache.CoinTailsTex)
                sTailsMat.mainTexture = SpriteCache.CoinTailsTex;
        }

        static void EnsureSharedResources()
        {
            if (sQuadMesh == null)
            {
                var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
                sQuadMesh = q.GetComponent<MeshFilter>().sharedMesh;
                UnityEngine.Object.DestroyImmediate(q);
            }
            if (sHeadsDiscMesh == null || sTailsDiscMesh == null || sRimCylinderMesh == null)
            {
                BuildCoinMeshes(48, out sHeadsDiscMesh, out sTailsDiscMesh, out sRimCylinderMesh);
            }
            if (sRimMat == null)
            {
                sRimMat = CardView.CreateUnlitMaterial(Color.white, BuildMilledRimTexture(128, 32), false);
            }
            if (sHeadsMat == null)
            {
                SpriteCache.EnsureInitialized();
                Texture2D hTex = SpriteCache.CoinHeadsTex != null
                    ? SpriteCache.CoinHeadsTex
                    : BuildFallbackCoinTex(128, new Color(0.95f, 0.78f, 0.20f, 1f));
                sHeadsMat = CardView.CreateUnlitMaterial(Color.white, hTex, false);
            }
            if (sTailsMat == null)
            {
                SpriteCache.EnsureInitialized();
                Texture2D tTex = SpriteCache.CoinTailsTex != null
                    ? SpriteCache.CoinTailsTex
                    : BuildFallbackCoinTex(128, new Color(0.20f, 0.23f, 0.28f, 1f));
                sTailsMat = CardView.CreateUnlitMaterial(Color.white, tTex, false);
            }
            if (sShadowTex == null)
            {
                sShadowTex = BuildRadialDiscTex(64, innerRadius: 0f, outerRadius: 1f, softPower: 1.9f);
            }
            if (sRingTex == null)
            {
                sRingTex = BuildRingTex(96);
            }
            if (sGlintTex == null)
            {
                sGlintTex = BuildMetallicGlintTex(64);
            }
        }

        /// <summary>
        /// Construye las 3 mallas circulares exactas de la moneda 3D:
        /// cara superior (+Y), cara inferior (-Y des-espejada para lectura directa al voltearse 180° en X) y canto cilíndrico.
        /// </summary>
        static void BuildCoinMeshes(int segments, out Mesh headsMesh, out Mesh tailsMesh, out Mesh rimMesh)
        {
            const float uvRadius = 0.476f;

            // --- 1. Cara Superior (+Y, Cara / Pikachu) ---
            var hVerts = new Vector3[segments + 1];
            var hNorms = new Vector3[segments + 1];
            var hUvs = new Vector2[segments + 1];
            var hTris = new int[segments * 3];

            hVerts[0] = new Vector3(0f, 0.5f, 0f);
            hNorms[0] = Vector3.up;
            hUvs[0] = new Vector2(0.5f, 0.5f);

            // --- 2. Cara Inferior (-Y, Cruz / Pokéball) ---
            var tVerts = new Vector3[segments + 1];
            var tNorms = new Vector3[segments + 1];
            var tUvs = new Vector2[segments + 1];
            var tTris = new int[segments * 3];

            tVerts[0] = new Vector3(0f, -0.5f, 0f);
            tNorms[0] = Vector3.down;
            tUvs[0] = new Vector2(0.5f, 0.5f);

            for (int i = 0; i < segments; i++)
            {
                float angle = (i / (float)segments) * Mathf.PI * 2f;
                float cx = Mathf.Cos(angle);
                float cz = Mathf.Sin(angle);

                int vIdx = i + 1;
                hVerts[vIdx] = new Vector3(cx * 0.5f, 0.5f, cz * 0.5f);
                hNorms[vIdx] = Vector3.up;
                hUvs[vIdx] = new Vector2(0.5f + cx * uvRadius, 0.5f + cz * uvRadius);

                tVerts[vIdx] = new Vector3(cx * 0.5f, -0.5f, cz * 0.5f);
                tNorms[vIdx] = Vector3.down;
                tUvs[vIdx] = new Vector2(0.5f + cx * uvRadius, 0.5f - cz * uvRadius);

                int nextIdx = 1 + ((i + 1) % segments);
                int triBase = i * 3;

                // Winding horario visto desde +Y
                hTris[triBase] = 0;
                hTris[triBase + 1] = nextIdx;
                hTris[triBase + 2] = vIdx;

                // Winding horario visto desde -Y
                tTris[triBase] = 0;
                tTris[triBase + 1] = vIdx;
                tTris[triBase + 2] = nextIdx;
            }

            headsMesh = new Mesh { name = "PokeTCG_CoinHeadsDisc" };
            headsMesh.vertices = hVerts;
            headsMesh.normals = hNorms;
            headsMesh.uv = hUvs;
            headsMesh.triangles = hTris;

            tailsMesh = new Mesh { name = "PokeTCG_CoinTailsDisc" };
            tailsMesh.vertices = tVerts;
            tailsMesh.normals = tNorms;
            tailsMesh.uv = tUvs;
            tailsMesh.triangles = tTris;

            // --- 3. Canto Cilíndrico Estriado ---
            int rimCols = segments + 1;
            var rVerts = new Vector3[rimCols * 2];
            var rNorms = new Vector3[rimCols * 2];
            var rUvs = new Vector2[rimCols * 2];
            var rTris = new int[segments * 6];

            for (int i = 0; i < rimCols; i++)
            {
                float u = i / (float)segments;
                float angle = u * Mathf.PI * 2f;
                float cx = Mathf.Cos(angle);
                float cz = Mathf.Sin(angle);
                Vector3 n = new Vector3(cx, 0f, cz);

                rVerts[i * 2] = new Vector3(cx * 0.5f, -0.5f, cz * 0.5f);
                rNorms[i * 2] = n;
                rUvs[i * 2] = new Vector2(u * 6f, 0f);

                rVerts[i * 2 + 1] = new Vector3(cx * 0.5f, 0.5f, cz * 0.5f);
                rNorms[i * 2 + 1] = n;
                rUvs[i * 2 + 1] = new Vector2(u * 6f, 1f);
            }

            for (int i = 0; i < segments; i++)
            {
                int b0 = i * 2;
                int t0 = b0 + 1;
                int b1 = (i + 1) * 2;
                int t1 = b1 + 1;
                int tb = i * 6;

                rTris[tb] = b0;
                rTris[tb + 1] = t0;
                rTris[tb + 2] = t1;

                rTris[tb + 3] = b0;
                rTris[tb + 4] = t1;
                rTris[tb + 5] = b1;
            }

            rimMesh = new Mesh { name = "PokeTCG_CoinMilledRim" };
            rimMesh.vertices = rVerts;
            rimMesh.normals = rNorms;
            rimMesh.uv = rUvs;
            rimMesh.triangles = rTris;
        }

        /// <summary>
        /// Textura del canto estriado: bisel superior dorado (foil Pikachu) + cuerpo grafito/oscuro estriado como las monedas oficiales de Pokémon TCG.
        /// </summary>
        static Texture2D BuildMilledRimTexture(int w, int h)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.filterMode = FilterMode.Bilinear;

            Color goldLight = new Color(0.98f, 0.86f, 0.38f, 1f);
            Color goldDark = new Color(0.68f, 0.52f, 0.14f, 1f);
            Color graphiteLight = new Color(0.32f, 0.35f, 0.39f, 1f);
            Color graphiteDark = new Color(0.14f, 0.16f, 0.19f, 1f);

            for (int y = 0; y < h; y++)
            {
                float vt = y / (float)(h - 1);
                bool isGoldLip = vt >= 0.66f;
                for (int x = 0; x < w; x++)
                {
                    float ridge = 0.5f + 0.5f * Mathf.Sin((x / (float)w) * Mathf.PI * 2f * 24f);
                    Color c = isGoldLip
                        ? Color.Lerp(goldDark, goldLight, ridge)
                        : Color.Lerp(graphiteDark, graphiteLight, ridge);
                    tex.SetPixel(x, y, c);
                }
            }
            tex.Apply();
            return tex;
        }

        static Texture2D BuildFallbackCoinTex(int size, Color col)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    tex.SetPixel(x, y, col);
            tex.Apply();
            return tex;
        }

        static Texture2D BuildRadialDiscTex(int size, float innerRadius, float outerRadius, float softPower)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            float half = (size - 1) * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x - half) / half;
                    float dy = (y - half) / half;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = 0f;
                    if (r <= outerRadius && r >= innerRadius)
                    {
                        float norm = 1f - Mathf.Clamp01((r - innerRadius) / Mathf.Max(0.001f, outerRadius - innerRadius));
                        a = Mathf.Pow(norm, softPower);
                    }
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            }
            tex.Apply();
            return tex;
        }

        static Texture2D BuildRingTex(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            float half = (size - 1) * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x - half) / half;
                    float dy = (y - half) / half;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float band = 1f - Mathf.Clamp01(Mathf.Abs(r - 0.80f) / 0.14f);
                    float a = band * band;
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            }
            tex.Apply();
            return tex;
        }

        static Texture2D BuildMetallicGlintTex(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            float half = (size - 1) * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x - half) / half;
                    float dy = (y - half) / half;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    if (r > 0.96f)
                    {
                        tex.SetPixel(x, y, Color.clear);
                        continue;
                    }
                    // Franja diagonal suave tipo reflejo metálico
                    float diag = (dx * 0.72f + dy * 0.69f);
                    float streak = Mathf.Clamp01(1f - Mathf.Abs(diag - 0.12f) / 0.34f);
                    float edgeFade = Mathf.Clamp01((0.96f - r) / 0.12f);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, streak * streak * edgeFade));
                }
            }
            tex.Apply();
            return tex;
        }
    }
}
