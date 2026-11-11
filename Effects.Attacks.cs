// TCG-PlayKyn: Attack damage resolution, coin toss triggers and status condition parser
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace TCGPlayKyn
{
    /// <summary>
    /// SubmÃ³dulo de resoluciÃ³n de ataques, daÃ±o y monedas en combate.
    /// </summary>
    public static partial class Effects
    {
        /// <summary>
        /// Resuelve el ataque respetando estrictamente el orden visual:
        /// 1) Si el ataque requiere lanzar una o más monedas, se lanzan y animan PRIMERO.
        /// 2) Solo cuando termina la animación de la(s) moneda(s), el Pokémon ejecuta la embestida y aplica daño/estados en el impacto.
        /// </summary>
        public static void ResolveAttackSequence(Game g, PlayerState p, PokemonInPlay a, AttackDef atk, Action onSequenceDone)
        {
            var opp = g.OpponentOf(p);
            var def = opp.Active;
            string t = atk.Text ?? "";
            bool handled = string.IsNullOrEmpty(t);

            // Descartar energía como coste ("Discard 1 Fire Energy card attached to Charizard in order to use this attack")
            var mDisc = Regex.Match(t, @"Discard (\d+|all) (\w+ )?Energy cards? attached to [\w ]+ in order to use this attack", I);
            if (mDisc.Success)
            {
                string type = mDisc.Groups[2].Value.Trim();
                int n = mDisc.Groups[1].Value == "all" ? 99 : int.Parse(mDisc.Groups[1].Value);
                for (int i = 0; i < n; i++)
                {
                    var e = a.Energy.FirstOrDefault(x => type == "" || x.EnergyProvides == type);
                    if (e == null) break;
                    a.Energy.Remove(e); p.Discard.Add(e);
                }
                handled = true;
            }

            // Barrier de Mewtwo: Discard 1 Psychic Energy card attached to Mewtwo in order to prevent all effects of attacks, including damage, done to Mewtwo during your opponent's next turn.
            var mBarrier = Regex.Match(t, @"Discard (\d+) (\w+ )?Energy cards? attached to [\w ]+ in order to prevent all effects of attacks", I);
            if (mBarrier.Success)
            {
                string type = mBarrier.Groups[2].Value.Trim();
                int n = int.Parse(mBarrier.Groups[1].Value);
                for (int i = 0; i < n; i++)
                {
                    var e = a.Energy.FirstOrDefault(x => type == "" || x.EnergyProvides == type || x.EnergyProvides == "Rainbow");
                    if (e == null) break;
                    a.Energy.Remove(e); p.Discard.Add(e);
                }
                a.PreventAllUntilTurn = g.Turn + 1;
                g.Say($"  {a.Card.Name} descarta Energía y previene todo daño y efectos durante el próximo turno del rival.");
                handled = true;
                onSequenceDone?.Invoke();
                return;
            }

            // Paso 1: "Flip a coin. If tails, this attack does nothing."
            if (Regex.IsMatch(t, @"Flip a coin\. If tails, this attack does nothing", I))
            {
                handled = true;
                g.RunCoinFlip(p, atk.Name, heads =>
                {
                    if (!heads)
                    {
                        g.Say("  El ataque no hace nada.");
                        onSequenceDone?.Invoke();
                        return;
                    }
                    ContinueAttackCoinsAndImpact(g, p, a, opp, def, atk, t, handled, onSequenceDone);
                });
                return;
            }

            ContinueAttackCoinsAndImpact(g, p, a, opp, def, atk, t, handled, onSequenceDone);
        }

        static void ContinueAttackCoinsAndImpact(
            Game g,
            PlayerState p,
            PokemonInPlay a,
            PlayerState opp,
            PokemonInPlay def,
            AttackDef atk,
            string t,
            bool alreadyHandled,
            Action onSequenceDone)
        {
            bool handled = alreadyHandled;
            int flipPhraseCount = Regex.Matches(t, @"Flip (\d+|a) coins?", I).Count;

            var mTimes = Regex.Match(t, @"Flip (\d+|a) coins?\. This attack does (\d+) damage times the number of heads", I);
            var mPlus = Regex.Match(t, @"Flip a coin\. If heads, this attack does \d+ damage plus (\d+) more damage", I);
            var mPerEnergy = Regex.Match(t, @"plus (\d+) more damage for each (\w+ )?Energy (attached to [\w ]+ )?but not used to pay", I);
            var mCoinStatus = Regex.Match(t, @"Flip a coin\. If heads, the Defending Pok.mon is now ([^.]+)\.(?: If tails, it is now ([^.]+)\.)?", I);
            var mDirectStatus = !mCoinStatus.Success ? Regex.Match(t, @"The Defending Pok.mon is now ([^.]+)\.", I) : Match.Empty;
            var mSelf = Regex.Match(t, @"(?:does|deals?) (\d+) damage to itself", I);
            bool selfUsesTailsCoin = mSelf.Success && Regex.IsMatch(t, @"if tails, (?:this attack does \d+ damage plus )?[\w ]+ (?:does|deals?) \d+ damage to itself", I);
            bool selfSharesPlusCoin = selfUsesTailsCoin && mPlus.Success && flipPhraseCount <= 1;
            var mBench = Regex.Match(t, @"does (\d+) damage to each (Pok.mon on each player's Bench|of your opponent's Benched)", I);
            var mHeal = Regex.Match(t, @"remove (\d+|all) damage counters? from", I);

            // Agility / Fly (prevenir daño y efectos el próximo turno)
            var mAgility = Regex.Match(t, @"(?:Flip a coin\. If heads, )?(?:during your opponent's next turn, )?prevent all effects of attacks, including damage, done to (?:this Pok.mon|[\w ]+)(?: during your opponent's next turn)?(?:\s*;\s*if tails, this attack does nothing)?", I);
            bool agilityNeedsCoin = mAgility.Success && t.IndexOf("Flip a coin", StringComparison.OrdinalIgnoreCase) >= 0;
            bool flyDoesNothingOnTails = mAgility.Success && t.IndexOf("if tails, this attack does nothing", StringComparison.OrdinalIgnoreCase) >= 0;
            int agilityCoinIdx = -1;

            // Withdraw / Scrunch / Stiffen (prevenir todo daño)
            var mWithdraw = Regex.Match(t, @"Flip a coin\. If heads, prevent all damage done to (?:this Pok.mon|[\w ]+) during your opponent's next turn", I);
            int withdrawCoinIdx = -1;

            // Harden (prevenir daño de 30 o menos)
            var mHarden = Regex.Match(t, @"During your opponent's next turn, whenever (\d+) or less damage is done to [\w ]+ \(after applying Weakness and Resistance\), prevent that damage", I);

            // Hyper Beam / Whirlpool (descartar 1 energía del defensor)
            var mDiscardDefEnergy = Regex.Match(t, @"(?:Flip a coin\. If heads, )?(?:(?:If the Defending Pok.mon has any Energy cards attached to it, )?choose 1 of them and discard it|discard 1 Energy card attached to the Defending Pok.mon)", I);
            bool discardDefNeedsCoin = mDiscardDefEnergy.Success && t.IndexOf("Flip a coin", StringComparison.OrdinalIgnoreCase) >= 0;
            int discardDefCoinIdx = -1;

            // Whirlwind y Lure (cambio forzado de banca)
            var mWhirlwind = Regex.Match(t, @"If your opponent has any Benched Pok.mon, (?:he or she|they) chooses? 1 of them and switch(?:es)? it with the Defending Pok.mon", I);
            var mLure = Regex.Match(t, @"If your opponent has any Benched Pok.mon, choose 1 of them and switch it with (?:his or her|their)? Active Pok.mon", I);

            // Escalado de daño y efectos especiales de la Base Era
            var mDefEnergy = Regex.Match(t, @"plus (\d+) more damage for each Energy card attached to the Defending Pok.mon", I);
            var mMeditate = Regex.Match(t, @"plus (\d+) more damage for each damage counter on the Defending Pok.mon", I);
            var mFlail = Regex.Match(t, @"Does (\d+) damage times the number of damage counters on [\w ]+", I);
            var mKarate = Regex.Match(t, @"Does (\d+) damage minus (\d+) damage for each damage counter on [\w ]+", I);
            var mSuperFang = Regex.Match(t, @"Does damage to the Defending Pok.mon equal to half the Defending Pok.mon's remaining HP \(rounded up to the nearest 10\)", I);
            var mLeechSeed = Regex.Match(t, @"Unless all damage from this attack is prevented, you may remove 1 damage counter from [\w ]+", I);
            var mRecover = Regex.Match(t, @"Remove all damage counters from [\w ]+", I);

            // Mecánicas específicas de Jungle y Fossil
            var mDoTheWave = Regex.Match(t, @"plus (\d+) more damage for each of your Benched Pok.mon", I);
            var mBoyfriends = Regex.Match(t, @"plus (\d+) more damage for each Nidoking you have in play", I);
            var mFetch = Regex.Match(t, @"^Draw a card", I);
            var mMinimize = Regex.Match(t, @"All damage done by attacks to [\w ]+ during your opponent's next turn is reduced by (\d+)", I);
            var mSmokescreen = Regex.Match(t, @"If the Defending Pok.mon tries to attack during your opponent's next turn, your opponent flips a coin\. If tails, that attack does nothing", I);
            var mLeechLife = Regex.Match(t, @"Remove a number of damage counters from [\w ]+ equal to the damage done to the Defending Pok.mon", I);
            var mSwordsDance = Regex.Match(t, @"During your next turn, Scyther's Slash attack's base damage is 60 instead of 30", I);
            var mAcid = Regex.Match(t, @"Flip a coin\. If heads, the Defending Pok.mon can't retreat", I);
            int acidCoinIdx = -1;
            var mHeadache = Regex.Match(t, @"Your opponent can't play Trainer cards during (?:his or her|their) next turn", I);
            var mStoneBarrage = Regex.Match(t, @"Flip a coin until you get tails\. This attack does (\d+) damage times the number of heads", I);
            int stoneBarrageHeads = 0;
            var mClamp = Regex.Match(t, @"Flip a coin\. If heads, the Defending Pok.mon is now Paralyzed\. If tails, this attack does nothing", I);
            int clampCoinIdx = -1;
            var mSpacingOut = Regex.Match(t, @"Flip a coin\. If heads, remove a damage counter from Slowpoke", I);
            int spacingCoinIdx = -1;
            var mScavenge = Regex.Match(t, @"Discard 1 Psychic Energy.*Put a Trainer card from your discard pile into your hand", I);
            var mHurricane = Regex.Match(t, @"Unless this attack Knocks Out the Defending Pok.mon, return the Defending Pok.mon and all cards attached to it to your opponent's hand", I);
            var mChainLightning = Regex.Match(t, @"does (\d+) damage to each Benched Pok.mon of the same type as the Defending Pok.mon", I);

            var coinReasons = new List<string>();
            int timesCoinStart = -1, timesCoinCount = 0;
            int plusCoinIdx = -1;
            int statusCoinIdx = -1;
            int selfCoinIdx = -1;

            if (mTimes.Success)
            {
                timesCoinStart = coinReasons.Count;
                timesCoinCount = mTimes.Groups[1].Value.Equals("a", StringComparison.OrdinalIgnoreCase) ? 1 : int.Parse(mTimes.Groups[1].Value);
                for (int idx = 0; idx < timesCoinCount; idx++)
                    coinReasons.Add(timesCoinCount > 1 ? $"{atk.Name} ({idx + 1}/{timesCoinCount})" : atk.Name);
                handled = true;
            }
            if (mPlus.Success)
            {
                plusCoinIdx = coinReasons.Count;
                coinReasons.Add(atk.Name);
                handled = true;
            }
            if (mCoinStatus.Success)
            {
                statusCoinIdx = coinReasons.Count;
                coinReasons.Add(atk.Name);
                handled = true;
            }
            if (selfUsesTailsCoin && !selfSharesPlusCoin)
            {
                selfCoinIdx = coinReasons.Count;
                coinReasons.Add("autodaño");
                handled = true;
            }
            if (agilityNeedsCoin)
            {
                agilityCoinIdx = coinReasons.Count;
                coinReasons.Add(atk.Name);
                handled = true;
            }
            else if (mAgility.Success) handled = true;

            if (mWithdraw.Success)
            {
                withdrawCoinIdx = coinReasons.Count;
                coinReasons.Add(atk.Name);
                handled = true;
            }

            if (discardDefNeedsCoin)
            {
                discardDefCoinIdx = coinReasons.Count;
                coinReasons.Add(atk.Name);
                handled = true;
            }
            else if (mDiscardDefEnergy.Success) handled = true;

            if (mAcid.Success)
            {
                acidCoinIdx = coinReasons.Count;
                coinReasons.Add(atk.Name);
                handled = true;
            }
            if (mClamp.Success)
            {
                clampCoinIdx = coinReasons.Count;
                coinReasons.Add(atk.Name);
                handled = true;
            }
            if (mSpacingOut.Success)
            {
                spacingCoinIdx = coinReasons.Count;
                coinReasons.Add(atk.Name);
                handled = true;
            }
            if (mStoneBarrage.Success)
            {
                stoneBarrageHeads = 0;
                while (g.Rng.Next(2) == 0 && stoneBarrageHeads < 20)
                {
                    stoneBarrageHeads++;
                    coinReasons.Add($"{atk.Name} ({stoneBarrageHeads})");
                }
                coinReasons.Add($"{atk.Name} (Final)");
                handled = true;
            }

            if (mHarden.Success || mWhirlwind.Success || mLure.Success || mDefEnergy.Success ||
                mMeditate.Success || mFlail.Success || mKarate.Success || mSuperFang.Success ||
                mLeechSeed.Success || mRecover.Success || mDoTheWave.Success || mBoyfriends.Success ||
                mFetch.Success || mMinimize.Success || mSmokescreen.Success || mLeechLife.Success ||
                mSwordsDance.Success || mHeadache.Success || mScavenge.Success || mHurricane.Success ||
                mChainLightning.Success)
            {
                handled = true;
            }

            // Si el ataque requiere monedas, se tiran TODAS antes de ejecutar la animación de ataque y el daño/estado
            g.RunCoinSequence(p, coinReasons, coinResults =>
            {
                int dmg = atk.BaseDamage;

                if (mTimes.Success)
                {
                    int heads = 0;
                    for (int idx = 0; idx < timesCoinCount; idx++)
                        if (coinResults[timesCoinStart + idx]) heads++;
                    dmg = int.Parse(mTimes.Groups[2].Value) * heads;
                }

                if (mPlus.Success && plusCoinIdx >= 0 && coinResults[plusCoinIdx])
                {
                    dmg += int.Parse(mPlus.Groups[1].Value);
                }

                if (mPerEnergy.Success)
                {
                    string type = mPerEnergy.Groups[2].Value.Trim();
                    int extra = (type == "" ? a.EnergyTotal : a.EnergyCount(type)) - atk.Cost.Count(c => type == "" || c == type);
                    dmg += Math.Min(2, Math.Max(0, extra)) * int.Parse(mPerEnergy.Groups[1].Value);
                    handled = true;
                }

                if (mDefEnergy.Success && def != null)
                {
                    dmg += int.Parse(mDefEnergy.Groups[1].Value) * def.EnergyTotal;
                    handled = true;
                }

                if (mMeditate.Success && def != null)
                {
                    dmg += int.Parse(mMeditate.Groups[1].Value) * (def.Damage / 10);
                    handled = true;
                }

                if (mFlail.Success)
                {
                    dmg = int.Parse(mFlail.Groups[1].Value) * (a.Damage / 10);
                    handled = true;
                }

                if (mKarate.Success)
                {
                    dmg = Math.Max(0, int.Parse(mKarate.Groups[1].Value) - int.Parse(mKarate.Groups[2].Value) * (a.Damage / 10));
                    handled = true;
                }

                if (mSuperFang.Success && def != null)
                {
                    dmg = (int)Math.Ceiling(def.RemainingHp / 20.0) * 10;
                    handled = true;
                }

                if (a.SwordsDanceUntilTurn >= g.Turn && atk.Name == "Slash")
                {
                    dmg = 60;
                }

                if (mDoTheWave.Success)
                {
                    dmg += int.Parse(mDoTheWave.Groups[1].Value) * p.Bench.Count;
                    handled = true;
                }

                if (mBoyfriends.Success)
                {
                    int nidokingCount = p.InPlay.Count(x => x.Card.Name.StartsWith("Nidoking", StringComparison.OrdinalIgnoreCase));
                    dmg += int.Parse(mBoyfriends.Groups[1].Value) * nidokingCount;
                    handled = true;
                }

                if (mStoneBarrage.Success)
                {
                    dmg = int.Parse(mStoneBarrage.Groups[1].Value) * stoneBarrageHeads;
                    handled = true;
                }

                bool agilitySuccess = false;
                if (mAgility.Success)
                {
                    if (agilityNeedsCoin)
                    {
                        if (agilityCoinIdx >= 0 && coinResults[agilityCoinIdx]) agilitySuccess = true;
                        else if (flyDoesNothingOnTails) dmg = 0;
                    }
                    else agilitySuccess = true;
                }

                bool withdrawSuccess = false;
                if (mWithdraw.Success && withdrawCoinIdx >= 0 && coinResults[withdrawCoinIdx])
                {
                    withdrawSuccess = true;
                }

                bool discardDefEnergySuccess = false;
                if (mDiscardDefEnergy.Success)
                {
                    if (discardDefNeedsCoin)
                    {
                        if (discardDefCoinIdx >= 0 && coinResults[discardDefCoinIdx]) discardDefEnergySuccess = true;
                    }
                    else discardDefEnergySuccess = true;
                }

                bool clampSuccess = false;
                if (mClamp.Success)
                {
                    if (clampCoinIdx >= 0 && coinResults[clampCoinIdx])
                    {
                        clampSuccess = true;
                    }
                    else
                    {
                        dmg = 0;
                    }
                }

                bool acidSuccess = false;
                if (mAcid.Success && acidCoinIdx >= 0 && coinResults[acidCoinIdx])
                {
                    acidSuccess = true;
                }

                bool spacingSuccess = false;
                if (mSpacingOut.Success && spacingCoinIdx >= 0 && coinResults[spacingCoinIdx])
                {
                    spacingSuccess = true;
                }

                string conditionToApply = null;
                if (clampSuccess)
                {
                    conditionToApply = "Paralyzed";
                }
                else if (mCoinStatus.Success && statusCoinIdx >= 0)
                {
                    if (coinResults[statusCoinIdx])
                        conditionToApply = mCoinStatus.Groups[1].Value;
                    else if (mCoinStatus.Groups[2].Success)
                        conditionToApply = mCoinStatus.Groups[2].Value;
                }
                else if (mDirectStatus.Success)
                {
                    conditionToApply = mDirectStatus.Groups[1].Value;
                    handled = true;
                }

                int selfDmgToApply = 0;
                if (mSelf.Success)
                {
                    handled = true;
                    bool applySelf = !selfUsesTailsCoin
                        || (selfSharesPlusCoin ? !coinResults[plusCoinIdx] : (selfCoinIdx >= 0 && !coinResults[selfCoinIdx]));
                    if (applySelf)
                        selfDmgToApply = int.Parse(mSelf.Groups[1].Value);
                }

                if (mBench.Success || mHeal.Success)
                    handled = true;

                bool hasAnyImpactEffect = dmg > 0 || conditionToApply != null || selfDmgToApply > 0 ||
                    mBench.Success || mHeal.Success || agilitySuccess || withdrawSuccess ||
                    mHarden.Success || discardDefEnergySuccess || mWhirlwind.Success || mLure.Success ||
                    mLeechSeed.Success || mRecover.Success || mFetch.Success || mMinimize.Success ||
                    mSmokescreen.Success || mLeechLife.Success || mSwordsDance.Success || acidSuccess ||
                    mHeadache.Success || spacingSuccess || mScavenge.Success || mHurricane.Success ||
                    mChainLightning.Success || !handled;
                if (!hasAnyImpactEffect && coinReasons.Count > 0)
                {
                    g.Say("  El ataque no hace nada.");
                    onSequenceDone?.Invoke();
                    return;
                }

                // Ahora que terminaron las monedas, el Pokémon embiste y aplica el daño/efecto justo en el impacto
                g.RunAttackLunge(
                    p,
                    a.Card,
                    atk.Name,
                    onImpact: () =>
                    {
                        if (def != null)
                            g.DealDamage(a, def, dmg);

                        if (conditionToApply != null && def != null)
                            ApplyCondition(g, def, conditionToApply);

                        if (selfDmgToApply > 0)
                        {
                            a.Damage += selfDmgToApply;
                            g.Say($"  {a.Card.Name} se hace {selfDmgToApply} de daño.");
                        }

                        if (mBench.Success)
                        {
                            int bd = int.Parse(mBench.Groups[1].Value);
                            foreach (var b in opp.Bench) b.Damage += bd;
                            if (mBench.Groups[2].Value.Contains("each player"))
                                foreach (var b in p.Bench) b.Damage += bd;
                            g.Say($"  {bd} de daño a la Banca.");
                        }

                        if (mHeal.Success)
                        {
                            int n = mHeal.Groups[1].Value == "all" ? 999 : int.Parse(mHeal.Groups[1].Value) * 10;
                            a.Damage = Math.Max(0, a.Damage - n);
                        }

                        if (mFetch.Success)
                        {
                            g.Draw(p, 1);
                        }

                        if (mMinimize.Success)
                        {
                            int red = int.Parse(mMinimize.Groups[1].Value);
                            a.DamageReductionAmount = red;
                            a.DamageReductionUntilTurn = g.Turn + 1;
                            g.Say($"  {a.Card.Name} reduce en {red} el daño recibido durante el próximo turno del rival.");
                        }

                        if (mSmokescreen.Success && def != null && def.PreventAllUntilTurn < g.Turn)
                        {
                            def.SmokescreenUntilTurn = g.Turn + 1;
                            g.Say($"  {def.Card.Name} queda envuelto en Pantalla de Humo.");
                        }

                        if (mLeechLife.Success && dmg > 0 && a.Damage > 0)
                        {
                            int heal = Math.Min(a.Damage, dmg);
                            a.Damage -= heal;
                            g.Say($"  {a.Card.Name} se cura {heal} PS con Leech Life.");
                        }

                        if (mSwordsDance.Success)
                        {
                            a.SwordsDanceUntilTurn = g.Turn + 2;
                            g.Say($"  ¡{a.Card.Name} prepara Danza Espada! El próximo Slash hará 60 de daño.");
                        }

                        if (acidSuccess && def != null && def.PreventAllUntilTurn < g.Turn)
                        {
                            def.CantRetreatUntilTurn = g.Turn + 1;
                            g.Say($"  {def.Card.Name} no puede retirarse durante el próximo turno.");
                        }

                        if (mHeadache.Success)
                        {
                            opp.TrainerLockUntilTurn = g.Turn + 1;
                            g.Say($"  ¡Dolor de cabeza! {opp.Name} no puede jugar cartas de Entrenador en su próximo turno.");
                        }

                        if (spacingSuccess && a.Damage > 0)
                        {
                            a.Damage = Math.Max(0, a.Damage - 10);
                            g.Say($"  {a.Card.Name} se cura 10 PS con Spacing Out.");
                        }

                        if (mScavenge.Success)
                        {
                            var trainerInDiscard = p.Discard.FirstOrDefault(x => x.Supertype == Supertype.Trainer);
                            if (trainerInDiscard != null)
                            {
                                p.Discard.Remove(trainerInDiscard);
                                p.Hand.Add(trainerInDiscard);
                                g.Say($"  {a.Card.Name} recupera {trainerInDiscard.Name} del descarte.");
                            }
                        }

                        if (mHurricane.Success && def != null && def.RemainingHp > 0 && def.PreventAllUntilTurn < g.Turn)
                        {
                            g.Say($"  ¡Huracán regresa a {def.Card.Name} y todas sus cartas a la mano de {opp.Name}!");
                            opp.Hand.AddRange(def.AllCards());
                            opp.Active = null;
                            if (opp.Bench.Count > 0)
                                g.ForceSwitch(opp, 0);
                        }

                        if (mChainLightning.Success && def != null)
                        {
                            int bd = int.Parse(mChainLightning.Groups[1].Value);
                            string defType = def.Card.Type;
                            foreach (var b in opp.Bench.Concat(p.Bench))
                            {
                                if (b.Card.Type == defType)
                                {
                                    b.Damage += bd;
                                    g.Say($"  Rayo en cadena hace {bd} de daño a {b.Card.Name} ({defType}).");
                                }
                            }
                        }

                        if (agilitySuccess)
                        {
                            a.PreventAllUntilTurn = g.Turn + 1;
                            g.Say($"  {a.Card.Name} previene todo daño y efectos durante el próximo turno del rival.");
                        }

                        if (withdrawSuccess)
                        {
                            a.PreventDamageUntilTurn = g.Turn + 1;
                            g.Say($"  {a.Card.Name} previene todo daño durante el próximo turno del rival.");
                        }

                        if (mHarden.Success)
                        {
                            int th = int.Parse(mHarden.Groups[1].Value);
                            a.PreventDamageThreshold = th;
                            a.PreventDamageThresholdUntilTurn = g.Turn + 1;
                            g.Say($"  {a.Card.Name} previene ataques de {th} o menos de daño durante el próximo turno del rival.");
                        }

                        if (discardDefEnergySuccess && def != null && def.Energy.Count > 0 && def.PreventAllUntilTurn < g.Turn)
                        {
                            var discardedEnergy = def.Energy[0];
                            def.Energy.Remove(discardedEnergy);
                            opp.Discard.Add(discardedEnergy);
                            g.Say($"  Se descarta {discardedEnergy.Name} de {def.Card.Name}.");
                        }

                        if (mLeechSeed.Success && dmg > 0 && a.Damage > 0)
                        {
                            a.Damage = Math.Max(0, a.Damage - 10);
                            g.Say($"  {a.Card.Name} se cura 10 PS con Leech Seed.");
                        }

                        if (mRecover.Success)
                        {
                            a.Damage = 0;
                            g.Say($"  {a.Card.Name} se recupera completamente de su daño.");
                        }

                        if (mWhirlwind.Success && def != null && def.RemainingHp > 0 && opp.Bench.Count > 0 && def.PreventAllUntilTurn < g.Turn)
                        {
                            g.ForceSwitch(opp, 0);
                        }

                        if (mLure.Success && def != null && def.RemainingHp > 0 && opp.Bench.Count > 0 && def.PreventAllUntilTurn < g.Turn)
                        {
                            int bIdx = 0;
                            if (p.IsAI)
                                bIdx = opp.Bench.Select((b, i) => new { b, i }).OrderBy(x => x.b.RemainingHp).First().i;
                            g.ForceSwitch(opp, bIdx);
                        }

                        if (!handled)
                            g.Say($"  (Efecto aún no implementado: \"{t}\")");
                    },
                    onFinished: onSequenceDone);
            });
        }

    }
}
