using System;
using System.Collections.Generic;
using System.Linq;

namespace PokeTCGPlay
{
    /// <summary>
    /// Submódulo de cálculo de daño, debilidad, resistencia, efectos de protección y resolución de noqueos.
    /// 
    /// ¿Para qué sirve?
    /// - 'DealDamage': Calcula el daño final aplicando Debilidad (x2 en Base Era), Resistencia (-30),
    ///   bonificadores (PlusPower, Danza Espada) y reducciones (Defender, Minimize, Smokescreen).
    /// - 'CheckKnockOuts': Comprueba si los PS de algún Pokémon en juego han llegado a 0, descarta
    ///   las cartas asociadas, entrega Premios al rival y solicita promover un nuevo Activo.
    /// - 'RunPokemonCheckupSequence': Resuelve el veneno (10 o 20 por Toxic) y el lanzamiento
    ///   de moneda para despertar del sueño entre turnos.
    /// - 'IsPowerDisabled': Comprueba si un Poder Pokémon está bloqueado por estado alterado o Muk.
    /// 
    /// Modificaciones:
    /// - Si quieres cambiar la fórmula de debilidad o resistencia, modifícalo en 'DealDamage'.
    /// </summary>
    public partial class Game
    {
void RunPokemonCheckupSequence(Action onCheckupDone)
        {
            var sleepingActives = new List<(PlayerState owner, PokemonInPlay active)>();
            foreach (var p in Players)
            {
                var a = p.Active;
                if (a == null) continue;
                if (a.Poisoned)
                {
                    int pDmg = a.ToxicPoison ? 20 : 10;
                    a.Damage += pDmg;
                    Say($"{a.Card.Name} recibe {pDmg} por veneno.");
                }
                if (a.Status == Status.Asleep)
                {
                    sleepingActives.Add((p, a));
                }
            }

            void StepSleep(int idx)
            {
                if (idx >= sleepingActives.Count)
                {
                    onCheckupDone?.Invoke();
                    return;
                }
                var entry = sleepingActives[idx];
                if (entry.active == null || entry.owner.Active != entry.active || entry.active.Status != Status.Asleep)
                {
                    StepSleep(idx + 1);
                    return;
                }
                RunCoinFlip(entry.owner, $"{entry.active.Card.Name} dormido", heads =>
                {
                    if (heads && entry.active.Status == Status.Asleep)
                    {
                        entry.active.Status = Status.None;
                        entry.active.StatusTurns = 0;
                        Say($"{entry.active.Card.Name} despierta.");
                    }
                    StepSleep(idx + 1);
                });
            }

            StepSleep(0);
        }

public bool IsPowerDisabled(PokemonInPlay p)
        {
            if (p == null) return true;
            if (p.Status == Status.Asleep || p.Status == Status.Confused || p.Status == Status.Paralyzed)
                return true;

            bool isMukWithGas = p.Card.Name.StartsWith("Muk", StringComparison.OrdinalIgnoreCase)
                || (p.Card.Abilities != null && p.Card.Abilities.Any(ab => ab.Name == "Toxic Gas"))
                || p.Card.Power?.Name == "Toxic Gas";

            if (!isMukWithGas)
            {
                foreach (var pl in Players)
                {
                    foreach (var pip in pl.InPlay)
                    {
                        if ((pip.Card.Name.StartsWith("Muk", StringComparison.OrdinalIgnoreCase) ||
                             (pip.Card.Abilities != null && pip.Card.Abilities.Any(ab => ab.Name == "Toxic Gas")) ||
                             pip.Card.Power?.Name == "Toxic Gas") &&
                            pip.Status != Status.Asleep && pip.Status != Status.Confused && pip.Status != Status.Paralyzed)
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        /// <summary>Daño al Activo defensor aplicando PlusPower, Debilidad x2, Resistencia, Defender, efectos de prevención y Pokémon Powers.</summary>
        public void DealDamage(PokemonInPlay attacker, PokemonInPlay defender, int baseDmg, bool applyWR = true)
        {
            if (baseDmg <= 0 || defender == null) return;

            // Prevención total (Agility, Barrier, Fly)
            if (defender.PreventAllUntilTurn >= Turn)
            {
                Say($"  ¡{defender.Card.Name} previene todo daño y efectos de ataques!");
                return;
            }

            // Prevención de daño (Withdraw, Scrunch, Stiffen)
            if (defender.PreventDamageUntilTurn >= Turn)
            {
                Say($"  ¡{defender.Card.Name} previene todo daño de ataques!");
                return;
            }

            int dmg = baseDmg + (attacker?.PlusPower ?? 0) * 10;
            if (MistyBonusTurn == Turn && attacker != null && attacker.Card.Name.IndexOf("Misty", StringComparison.OrdinalIgnoreCase) >= 0)
                dmg += 20;
            if (applyWR && attacker != null)
            {
                bool ignoreWaterWeakness = ActiveStadium == "Cinnabar City Gym"
                    && attacker.Card.Type == "Water"
                    && defender.Card.Name.IndexOf("Blaine", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!ignoreWaterWeakness && defender.Card.WeaknessType == attacker.Card.Type) dmg *= 2;

                bool ignoreResistance = ActiveStadium == "Pewter City Gym"
                    && attacker.Card.Name.IndexOf("Brock", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!ignoreResistance && defender.Card.ResistanceType == attacker.Card.Type) dmg -= defender.Card.ResistanceValue;
            }
            if (defender.DefenderUntilTurn >= Turn) dmg -= 20;
            if (defender.DamageReductionUntilTurn >= Turn && defender.DamageReductionAmount > 0)
            {
                dmg -= defender.DamageReductionAmount;
                Say($"  ¡{defender.Card.Name} reduce el daño en {defender.DamageReductionAmount} (Minimize)!");
            }
            dmg = Math.Max(0, dmg);

            // Prevención por umbral (Harden de Onix / Graveler: 30 o menos)
            if (defender.PreventDamageThresholdUntilTurn >= Turn && defender.PreventDamageThreshold > 0 && dmg <= defender.PreventDamageThreshold)
            {
                Say($"  ¡El endurecimiento de {defender.Card.Name} previene el daño ({dmg} <= {defender.PreventDamageThreshold})!");
                return;
            }

            // Muro Invisible (Invisible Wall) de Mr. Mime: si el daño es >= 30, se reduce a 0
            bool hasInvisibleWall = defender.Card.Name.StartsWith("Mr. Mime", StringComparison.OrdinalIgnoreCase) ||
                                     (defender.Card.Abilities != null && defender.Card.Abilities.Any(ab => ab.Name == "Invisible Wall")) ||
                                     defender.Card.Power?.Name == "Invisible Wall";
            if (hasInvisibleWall && !IsPowerDisabled(defender))
            {
                if (dmg >= 30)
                {
                    Say($"  ¡Muro Invisible (Invisible Wall) de {defender.Card.Name} previene los {dmg} puntos de daño!");
                    dmg = 0;
                }
            }

            // Armadura Kabuto (Kabuto Armor): reduce todo el daño a la mitad (redondeado hacia abajo a la decena)
            if (defender.Card.Power?.Name == "Kabuto Armor" && !IsPowerDisabled(defender) && dmg > 0)
            {
                dmg = (dmg / 20) * 10;
                Say($"  ¡Armadura Kabuto reduce el daño a {dmg}!");
            }

            // Shell Armor de Misty's Cloyster: reduce el daño recibido en 10
            if (defender.Card.Power?.Name == "Shell Armor" && !IsPowerDisabled(defender) && dmg > 0)
            {
                dmg = Math.Max(0, dmg - 10);
                Say($"  ¡Shell Armor de {defender.Card.Name} reduce el daño en 10!");
            }

            defender.Damage += dmg;
            Say($"  {defender.Card.Name} recibe {dmg} de daño.");

            // Strikes Back de Machamp: cuando es dañado por un ataque, devuelve 10 de daño al atacante
            if (defender.Card.Power?.Name == "Strikes Back" && !IsPowerDisabled(defender) && dmg > 0 && attacker != null)
            {
                attacker.Damage += 10;
                Say($"  ¡Contraataque (Strikes Back) de Machamp devuelve 10 de daño a {attacker.Card.Name}!");
            }

            // Restless Sleep de Rocket's Snorlax: si está dormido y recibe daño, devuelve 20 de daño al atacante
            if (defender.Card.Power?.Name == "Restless Sleep" && defender.Status == Status.Asleep && dmg > 0 && attacker != null)
            {
                attacker.Damage += 20;
                Say($"  ¡Sueño Inquieto (Restless Sleep) de Rocket's Snorlax devuelve 20 de daño a {attacker.Card.Name}!");
            }
            if (dmg > 0 && KogaPoisonTurn == Turn && attacker != null && attacker.Card.Name.IndexOf("Koga", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                defender.Poisoned = true;
                Say($"  ¡El efecto de Koga envenena a {defender.Card.Name}!");
            }
        }

        public void CheckKnockOuts()
        {
            foreach (var p in Players)
                foreach (var pip in p.InPlay.ToList())
                    if (pip.RemainingHp <= 0)
                    {
                        // Fortitude de Giovanni's Machamp: lanza moneda, con caras sobrevive con 10 PS
                        if (pip.Card.Power?.Name == "Fortitude" && !IsPowerDisabled(pip))
                        {
                            if (Flip("Fortitude de Giovanni's Machamp"))
                            {
                                pip.Damage = pip.Card.Hp - 10;
                                Say($"  ¡{pip.Card.Name} resiste el Fuera de Combate con 10 PS gracias a Fortitude!");
                                continue;
                            }
                        }

                        // Final Beam de Dark Gyarados: con caras inflige 20 por cada energía de agua al Pokémon rival
                        if (pip.Card.Power?.Name == "Final Beam" && !IsPowerDisabled(pip))
                        {
                            int waterE = pip.EnergyCount("Water");
                            if (waterE > 0 && Flip("Final Beam de Dark Gyarados"))
                            {
                                var oppActive = OpponentOf(p).Active;
                                if (oppActive != null)
                                {
                                    int fbDmg = waterE * 20;
                                    oppActive.Damage += fbDmg;
                                    Say($"  ¡Rayo Final (Final Beam) de Dark Gyarados inflige {fbDmg} de daño a {oppActive.Card.Name}!");
                                }
                            }
                        }

                        // Rebirth de Rocket's Moltres: con caras regresa a la mano
                        if (pip.Card.Power?.Name == "Rebirth" && !IsPowerDisabled(pip))
                        {
                            if (Flip("Rebirth de Rocket's Moltres"))
                            {
                                p.Hand.Add(pip.Card);
                                foreach (var att in pip.Attached.Concat(pip.Energy)) p.Discard.Add(att);
                                if (p.Active == pip) p.Active = null; else p.Bench.Remove(pip);
                                Say($"  ¡Renacer (Rebirth): {pip.Card.Name} regresa a la mano de {p.Name}!");
                                TakePrize(OpponentOf(p));
                                continue;
                            }
                        }

                        Say($"¡{pip.Card.Name} de {p.Name} queda Fuera de Combate!");
                        int bIdx = p.Active == pip ? -1 : p.Bench.IndexOf(pip);
                        Emit(AnimKind.KnockOut, p, pip.Card, bIdx);
                        p.Discard.AddRange(pip.AllCards());
                        if (p.Active == pip) p.Active = null; else p.Bench.Remove(pip);
                        if (!pip.IsDoll)
                        {
                            if (p.IsAI) PlayerKOs++;
                            else NpcKOs++;
                            TakePrize(OpponentOf(p));
                        }
                    }
            if (Phase == Phase.GameOver) return;
            foreach (var p in Players)
                if (p.Active == null)
                {
                    if (p.Bench.Count == 0)
                    {
                        EndGame(
                            OpponentOf(p),
                            p.IsAI
                                ? Loc.T("Tu oponente se quedó sin Pokémon", "Your opponent ran out of Pokémon")
                                : Loc.T("Te quedaste sin Pokémon", "You ran out of Pokémon"));
                        return;
                    }
                    if (p.IsAI) { AI.Promote(this, p); continue; }
                    PromotionPending = p; Phase = Phase.ChoosePromotion; return;
                }
        }

        void TakePrize(PlayerState p)
        {
            if (p.Prizes.Count == 0) return;
            var prize = p.Prizes[0];
            p.Hand.Add(prize); p.Prizes.RemoveAt(0);
            Say($"{p.Name} toma un Premio ({p.Prizes.Count} restantes).");
            Emit(AnimKind.TakePrize, p, prize, p.Prizes.Count);
            if (p.Prizes.Count == 0)
            {
                EndGame(
                    p,
                    p.IsAI
                        ? Loc.T("Obtuvo todos los premios", "Your opponent took all Prize cards")
                        : Loc.T("Obtuviste todos los premios", "You took all Prize cards"));
            }
        }
    }
}
