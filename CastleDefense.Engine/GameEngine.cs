using System;
using System.Collections.Generic;
using System.Linq;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Models;

namespace CastleDefense.Engine
{
    public class GameEngine
    {
        private GameState _state;

        // Config
        public const int MAP_WIDTH = 1500;
        public const int TICKS_PER_SECOND = 30;
        private const int INCOME_FREQUENCY = 30;

        // Optimization: Fast Lookup Cache
        private Dictionary<string, UnitDefinition> _unitCache;

        public GameEngine(GameState state)
        {
            _state = state;
            BuildCache();
        }

        private void BuildCache()
        {
            _unitCache = new Dictionary<string, UnitDefinition>();
            foreach (var team in GameDataManager.Teams)
            {
                foreach (var unit in team.Roster)
                {
                    if (!_unitCache.ContainsKey(unit.Id))
                        _unitCache[unit.Id] = unit;
                }
            }
        }

        public void Tick()
        {
            if (_state.IsGameOver) return;
            _state.CurrentTick++;

            // 1. Income & Cooldowns
            if (_state.CurrentTick % INCOME_FREQUENCY == 0)
            {
                GiveIncome(_state.Player1);
                GiveIncome(_state.Player2);
            }
            TickCooldowns(_state.Player1);
            TickCooldowns(_state.Player2);

            // 2. Process Status Effects
            ProcessStatuses();

            // 3. Movement & Combat
            MoveAndFight();
        }

        private void ProcessStatuses()
        {
            foreach (var unit in _state.Units)
            {
                // Remove expired effects
                unit.Statuses.RemoveAll(s => s.ExpiresAtTick <= _state.CurrentTick);

                // Apply DoT (Damage over Time)
                var burns = unit.Statuses.Where(s => s.Name == "Burn" || s.Name == "Poison");
                foreach (var burn in burns)
                {
                    // FIX: Cast Value to int
                    // FIX: Use a neutral AttackType (e.g., Siege or a new 'True' type) for DoTs 
                    // to ensure they aren't arbitrarily blocked by shields unless intended.
                    ApplyDamage(unit, (int)burn.Value, AttackType.Siege);
                }
            }
        }

        private void MoveAndFight()
        {
            // iterate backwards so we can remove dead units safely
            for (int i = _state.Units.Count - 1; i >= 0; i--)
            {
                var unit = _state.Units[i];
                if (!_unitCache.ContainsKey(unit.DefinitionId)) continue;
                var def = _unitCache[unit.DefinitionId];

                // 1. Calculate Dynamic Speed Mod
                float speedMod = 1.0f;
                var speedStatuses = unit.Statuses.Where(s => s.Name == "Slow" || s.Name == "SpeedBuff");
                foreach (var status in speedStatuses)
                {
                    speedMod *= status.Value; // Multiplicative stacking (e.g. 0.5 * 1.5 = 0.75)
                }

                // Check Hard CC
                if (unit.Statuses.Any(s => s.Name == "Freeze" || s.Name == "Stun")) speedMod = 0f;

                // 2. Check for Enemies in Range
                var enemy = FindTarget(unit, def);

                // Decrement Attack Cooldown (assuming Unit has this property)
                if (unit.AttackCooldown > 0) unit.AttackCooldown -= (1000f / TICKS_PER_SECOND);

                if (enemy != null)
                {
                    // --- COMBAT LOGIC ---
                    if (unit.AttackCooldown <= 0)
                    {
                        // Reset Cooldown
                        unit.AttackCooldown = (1000f / def.AttackSpeed);

                        // Apply Damage
                        ApplyDamage(enemy, def.Damage, def.AttackType);

                        // --- PHYSICS & MOMENTUM ---
                        var enemyDef = _unitCache[enemy.DefinitionId];

                        // Calculate Force based on Definitions + Current Speed Mod + Attack Type
                        // Fast units hit harder (Momentum)
                        float impactForce = def.PushForce * speedMod;
                        if (def.AttackType == AttackType.Siege)
                        {
                            impactForce *= 2;
                        }
                        if (def.AttackType == AttackType.Ranged || def.AttackType == AttackType.Magic)
                        {
                            impactForce /= 2;
                        }

                        // Calculate Resistance (Enemy weight)
                        // We prevent division by zero by clamping min weight
                        float resistance = Math.Max(1f, enemyDef.EffectiveWeight);

                        // Calculate Knockback
                        float knockbackDist = impactForce / resistance;

                        // Apply Knockback to Enemy
                        // Determine direction (Enemy is to the right? Push right.)
                        float direction = (enemy.Position > unit.Position) ? 1f : -1f;
                        enemy.Position += (knockbackDist * direction);

                        // OPTIONAL: Self-Recoil (Newton's 3rd Law)
                        // The attacker also bounces back slightly based on enemy density
                        float recoil = (enemyDef.PushForce / Math.Max(1f, def.EffectiveWeight)) * 0.5f;
                        unit.Position -= (recoil * direction);
                    }
                }
                else
                {
                    // --- MOVEMENT LOGIC ---
                    // Only move if speed > 0
                    unit.CurrentSpeed = def.MoveSpeed * speedMod;

                    if (speedMod > 0)
                    {
                        float direction = (unit.Side == 1) ? 1f : -1f;
                        unit.Position += (def.MoveSpeed * speedMod * direction);
                    }
                }

                // 3. Check Bounds (Despawn)
                if (unit.Position < -100 || unit.Position > MAP_WIDTH + 100)
                {
                    unit.CurrentHealth = 0; // Kill it
                }

                // 4. Death Check
                if (unit.CurrentHealth <= 0)
                {
                    _state.Units.RemoveAt(i);
                }
            }
        }

        private Unit FindTarget(Unit attacker, UnitDefinition attackerDef)
        {
            Unit bestTarget = null;
            float bestDist = 9999f;

            foreach (var other in _state.Units)
            {
                if (other.Side == attacker.Side) continue; // Friend

                // Flying Check
                var otherDef = _unitCache[other.DefinitionId];
                if (otherDef.ArmorType == ArmorType.Flying && attackerDef.AttackType != AttackType.Ranged)
                    continue;

                float dist = Math.Abs(attacker.Position - other.Position);

                // Range Check
                // (Optimized: Checking dist <= Range allows for simple "in range" logic)
                if (dist <= attackerDef.Range)
                {
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestTarget = other;
                    }
                }
            }
            return bestTarget;
        }

        private void ApplyDamage(Unit target, int amount, AttackType type)
        {
            // Shield Logic
            if (target.CurrentShield > 0)
            {
                // Siege deals double to shields
                if (type == AttackType.Siege) amount *= 2;

                target.CurrentShield -= amount;
                if (target.CurrentShield < 0)
                {
                    // Bleed over to health
                    target.CurrentHealth += target.CurrentShield; // (CurrentShield is negative here)
                    target.CurrentShield = 0;
                }
            }
            else
            {
                target.CurrentHealth -= amount;
            }
        }

        // ... GiveIncome and TickCooldowns remain unchanged ...
        private void GiveIncome(PlayerState player)
        {
            player.Money += player.Income;
        }

        private void TickCooldowns(PlayerState player)
        {
            // (Same as your previous implementation)
            foreach (var key in player.CooldownTimers.Keys.ToList())
            {
                if (player.CooldownTimers[key] > 0)
                {
                    player.CooldownTimers[key]--;
                    if (player.CooldownTimers[key] <= 0)
                    {
                        var unitDef = GameDataManager.Teams.SelectMany(t => t.Roster).FirstOrDefault(u => u.Id == key);
                        if (unitDef != null)
                        {
                            if (!player.UnitCharges.ContainsKey(key)) player.UnitCharges[key] = 0;
                            if (player.UnitCharges[key] < unitDef.MaxCharges)
                            {
                                player.UnitCharges[key]++;
                                if (player.UnitCharges[key] < unitDef.MaxCharges)
                                    player.CooldownTimers[key] = unitDef.CooldownMs / (1000 / TICKS_PER_SECOND);
                            }
                        }
                    }
                }
            }
        }
    }
}