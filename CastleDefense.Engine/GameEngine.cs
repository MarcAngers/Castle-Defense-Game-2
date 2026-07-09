using CastleDefense.Engine.Data;
using CastleDefense.Engine.Definitions;
using CastleDefense.Engine.Gadgets;
using CastleDefense.Engine.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;

namespace CastleDefense.Engine
{
    public class GameEngine
    {
        public GameState _state;

        // Config
        public const int MAP_WIDTH = 2000;
        public const int TICKS_PER_SECOND = 30;
        public const int MAX_TICKS = 18_000;
        private const int INCOME_FREQUENCY = 30;
        private ConcurrentQueue<Action> _actionQueue = new ConcurrentQueue<Action>();

        // Optimization: Fast Lookup Cache
        private Dictionary<string, UnitDefinition> _unitCache;
        private Dictionary<string, GadgetDefinition> _gadgetCache;
        private RewardParams _rewardParams;

        // Event for successful gadget use
        // Parameters: string gadgetId, int side, int position
        public event Action<string, int, int, Guid> OnGadgetAnimation;
        public event Action<int, GadgetDefinition> OnGadgetUpgraded;

        private class ScheduledEvent
        {
            public int ExecuteAtTick { get; set; }
            public Action Action { get; set; }
        }
        private List<ScheduledEvent> _scheduledEvents = new List<ScheduledEvent>();

        public GameEngine(GameState state, RewardParams rewardParams = null)
        {
            _state = state;
            _rewardParams = rewardParams ?? RewardParams.Default;
            BuildCache();

            _state.Player1.OnGadgetUpgraded += (side, def) => OnGadgetUpgraded?.Invoke(side, def);
            _state.Player2.OnGadgetUpgraded += (side, def) => OnGadgetUpgraded?.Invoke(side, def);
        }

        // Re-subscribe to OnGadgetUpgraded after PlayerState objects are replaced
        // (e.g., when the league JoinGame branch swaps in new PlayerState instances).
        public void RewirePlayerEvents()
        {
            _state.Player1.OnGadgetUpgraded += (side, def) => OnGadgetUpgraded?.Invoke(side, def);
            _state.Player2.OnGadgetUpgraded += (side, def) => OnGadgetUpgraded?.Invoke(side, def);
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

            _gadgetCache = new Dictionary<string, GadgetDefinition>();
            foreach (var gadget in GameDataManager.Gadgets)
            {
                if (!_gadgetCache.ContainsKey(gadget.Id))
                    _gadgetCache[gadget.Id] = gadget;
            }

            _unitCache["wall"] = GameDataManager.WallDefinition(1);
            _unitCache["wall_2"] = GameDataManager.WallDefinition(2);
            _unitCache["wall_3"] = GameDataManager.WallDefinition(3);
        }

        public void EnqueueAction(Action action)
        {
            _actionQueue.Enqueue(action);
        }

        private void ProcessActions()
        {
            while (_actionQueue.TryDequeue(out var action))
            {
                action.Invoke();
            }
        }

        public void ScheduleAction(int delayInTicks, Action action)
        {
            _scheduledEvents.Add(new ScheduledEvent
            {
                ExecuteAtTick = (int)_state.CurrentTick + delayInTicks,
                Action = action
            });
        }

        public void TriggerGadgetAnimation(string gadgetId, int side, int position, Guid instanceId = new Guid())
        {
            OnGadgetAnimation?.Invoke(gadgetId, side, position, instanceId);
        }

        public void Tick()
        {
            ProcessActions();

            for (int i = _scheduledEvents.Count - 1; i >= 0; i--)
            {
                if (_state.CurrentTick >= _scheduledEvents[i].ExecuteAtTick)
                {
                    _scheduledEvents[i].Action.Invoke();
                    _scheduledEvents.RemoveAt(i);
                }
            }

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

            // 2. Process Gadget Effects
            ProcessHazards();

            // 3. Process Status Effects
            if (_state.CurrentTick % 5 == 0)
            {
                ProcessStatuses();
            }

            // 4. Movement & Combat
            MoveAndFight();

            // 5. Time limit — set game over so CalculateReward applies the overtime terminal reward
            if (!_state.IsGameOver && _state.CurrentTick >= MAX_TICKS)
            {
                _state.IsGameOver  = true;
                _state.IsTimeLimit = true;
                _state.WinnerSide  = _state.Player1.CastleHealth > _state.Player2.CastleHealth ? 1 :
                                     _state.Player2.CastleHealth > _state.Player1.CastleHealth ? 2 : 0;
            }
        }

        public bool SpawnUnit(int side, string unitId, bool ignoreCost = false, float position = -1, int yposition = -1)
        {
            // 1. Validation
            var player = side == 1 ? _state.Player1 : _state.Player2;
            if (!_unitCache.ContainsKey(unitId)) return false;

            var def = _unitCache[unitId];

            // 2. Check Cooldowns & Money
            // (Assuming you implement CheckCooldown logic here similar to your TickCooldowns)
            if (!ignoreCost)
            {
                if (player.Money < def.Cost) return false;

                // 3. Deduct Cost
                player.Money -= def.Cost;

                // Track action ID for recording (tier maps directly to action ID 1-8)
                if (def.Tier >= 1 && def.Tier <= 8)
                {
                    if (side == 1) LastActionP1 = def.Tier;
                    else LastActionP2 = def.Tier;
                }
            }

            Random random = new Random();
            if (position == -1)
            {
                position = (side == 1) ? 100 : MAP_WIDTH - 100;
            }
            if (yposition == -1)
            {
                yposition = 360 - def.Height + random.Next(0, 51);
                if (def.Id == "wall_2")
                    yposition -= 120;
                if (def.Id == "wall_3")
                    yposition -= 350;
            }

            // 4. Create Unit
            var newUnit = new Unit
            {
                // --- IDENTITY & UI ---
                DefinitionId = unitId,
                Side = side,
                Tier = def.Tier,
                Height = def.Height,
                Width = def.Width,
                PendingKnockback = 0,
                LastKnockbackTick = 0,
                AttacksWithoutKnockback = 0,

                // --- HEALTH & POSITION ---
                CurrentHealth = def.MaxHealth,
                MaxHealth = def.MaxHealth,
                CurrentShield = def.MaxShield,
                Position = position,
                YPosition = yposition,

                // --- COMBAT STATS ---
                CurrentSpeed = def.MoveSpeed,
                Damage = def.Damage,
                Range = def.Range,
                AttackSpeed = def.AttackSpeed,

                // --- PHYSICS & MECHANICS ---
                Weight = def.Weight,
                PushForce = def.PushForce,
                EffectiveWeight = def.EffectiveWeight,
                AttackType = def.AttackType,
                ArmorType = def.ArmorType
            };

            // Give Monky a shield
            if (unitId == "monky")
            {
                newUnit.CurrentHealth /= 2;
                newUnit.MaxHealth /= 2;
                newUnit.CurrentShield = newUnit.CurrentHealth;
            }

            _state.Units.Add(newUnit);
            return true;
        }

        public bool Invest(int side)
        {
            // 1. Validation
            var player = side == 1 ? _state.Player1 : _state.Player2;

            if (player.Money < player.InvestmentPrice) return false;

            if (side == 1) LastActionP1 = 9; else LastActionP2 = 9;

            if (player.InvestmentCount > 8) return false;

            // 2. Deduct cost
            player.Money -= player.InvestmentPrice;

            // 3. Increase income and next investment price
            player.InvestmentCount++;
            // Equation for player income: e^(                  0.0109x^3                 +                    0.0011x^2                +           0.4351x                + 0.5268                     log R^2 = 0.9997
            player.Income = Math.Pow(Math.E, 0.0109 * Math.Pow(player.InvestmentCount, 3) + 0.0011 * Math.Pow(player.InvestmentCount, 2) + 0.4351 * player.InvestmentCount + 0.5268);
            // Each investment should take twice as long as the last
            player.InvestmentPrice = player.Income * (player.InvestmentCount * 4 + 8);

            if (player.InvestmentCount == 7)
            {
                player.Income = 750;
                player.InvestmentPrice = 25000;
            }
            if (player.InvestmentCount == 8)
            {
                player.Income = 2500;
                player.InvestmentPrice = 100000;
            }

            return true;
        }

        public bool Repair(int side)
        {
            // 1. Validation
            var player = side == 1 ? _state.Player1 : _state.Player2;

            if (player.Money < player.RepairPrice) return false;

            if (side == 1) LastActionP1 = 10; else LastActionP2 = 10;

            // 2. Deduct cost
            player.Money -= player.RepairPrice;

            // 3. Increase health and repair price
            player.RepairCount++;
            double rp = Math.Pow(Math.E, 0.0109 * Math.Pow(player.RepairCount, 3) + 0.0011 * Math.Pow(player.RepairCount, 2) + 0.4351 * player.RepairCount + 0.5268);
            player.RepairPrice = rp * (player.RepairCount * 5 + 5);
            if (player.RepairCount >= 8)
                player.RepairPrice *= 2;

            float pct = (float)player.CastleHealth / player.CastleMaxHealth;
            // Equation for increasing castle health:
            player.CastleMaxHealth = 1000 + 11000 * player.RepairCount;
            // Increase castle health and heal by 20%:
            player.CastleHealth = (int)Math.Min(player.CastleMaxHealth * (pct + 0.2), player.CastleMaxHealth);

            return true;
        }

        public bool UseGadget(int side, string gadgetId, int position)
        {
            // 1. Validation
            var player = side == 1 ? _state.Player1 : _state.Player2;

            if (!_gadgetCache.ContainsKey(gadgetId)) return false;

            if (gadgetId != player.OffensiveGadget.Id && gadgetId != player.DefensiveGadget.Id && gadgetId != player.SignatureGadget.Id) return false;

            var def = _gadgetCache[gadgetId];

            if (player.GadgetCooldowns.ContainsKey(gadgetId) && player.GadgetCooldowns[gadgetId] > 0)
            {
                return false; // Gadget is still on cooldown, do nothing!
            }

            if (player.Money < def.Cost) return false;

            // --- Bot Auto-Targeting Logic (-1) ---
            if (position == -1)
            {
                var enemies = _state.Units.Where(u => u.Side != side).ToList();

                if (enemies.Count > 0)
                {
                    if (side == 1)
                    {
                        // P1 targets P2's units. They are closest to P1 when X is lowest.
                        // They are moving left, so we lead them by subtracting 300.
                        var closestEnemy = enemies.OrderBy(e => e.Position).First();
                        position = (int)closestEnemy.Position - 300;
                    }
                    else
                    {
                        // P2 targets P1's units. They are closest to P2 when X is highest.
                        // They are moving right, so we lead them by adding 300.
                        var closestEnemy = enemies.OrderByDescending(e => e.Position).First();
                        position = (int)closestEnemy.Position + 300;
                    }
                }
                else
                {
                    // Fallback: If no enemies exist, drop it at the enemy castle
                    position = side == 1 ? MAP_WIDTH : 0;
                }

                // Clamp the final target to stay at least 100 units away from the castles
                position = Math.Max(300, Math.Min(MAP_WIDTH - 300, position));
            }

            // 2. Deduct Cost
            player.Money -= def.Cost;

            // Track action ID for recording (11=off, 12=def, 13=sig)
            int gadgetActionId = gadgetId == player.OffensiveGadget?.Id ? 11 :
                                 gadgetId == player.DefensiveGadget?.Id  ? 12 : 13;
            if (side == 1) LastActionP1 = gadgetActionId; else LastActionP2 = gadgetActionId;

            // 3. Apply gadget cooldown (converting ms to game ticks)
            player.GadgetCooldowns[gadgetId] = def.CooldownMs / (1000 / TICKS_PER_SECOND);

            // 4. Activate Gadget Effect
            def.GadgetEffect.Execute(this, side, position);

            return true;
        }

        private void ProcessHazards()
        {
            // Iterate backwards so we can safely remove them
            for (int i = _state.Hazards.Count - 1; i >= 0; i--)
            {
                var hazard = _state.Hazards[i];

                if (hazard.ExpiresAtTick <= _state.CurrentTick)
                {
                    hazard.OnExpire(_state);
                    _state.Hazards.RemoveAt(i);
                }
                else
                {
                    hazard.ProcessEffect(_state);
                }
            }
        }

        private void ProcessStatuses()
        {
            foreach (var unit in _state.Units)
            {
                // Remove expired effects
                unit.Statuses.RemoveAll(s => s.ExpiresAtTick <= _state.CurrentTick);

                // Apply DoT (Damage over Time)
                var burns = unit.Statuses.Where(s => s.Name == "Burn" || s.Name == "Poison" || s.Name == "Heal" || s.Name == "Blackhole");
                foreach (var burn in burns)
                {
                    int healthBefore = unit.CurrentHealth;

                    // Change to AttackType.Magic later?
                    ApplyDamage(unit, (int)burn.Value, AttackType.Melee, 0);
                }
            }

            // Invulnerability check:
            if (_state.Player1.IsInvulnerable)
            {
                if (_state.CurrentTick > _state.Player1.InvulnerableUntilTick)
                    _state.Player1.IsInvulnerable = false;
            }
            if (_state.Player2.IsInvulnerable)
            {
                if (_state.CurrentTick > _state.Player2.InvulnerableUntilTick)
                    _state.Player2.IsInvulnerable = false;
            }
        }

        private void MoveAndFight()
        {
            // 1. Sort units by Position (X-coordinate) for ultra-fast spatial lookups
            _state.Units.Sort((a, b) => a.Position.CompareTo(b.Position));

            // iterate backwards so we can remove dead units safely
            for (int i = _state.Units.Count - 1; i >= 0; i--)
            {
                var unit = _state.Units[i];
                if (!_unitCache.ContainsKey(unit.DefinitionId)) continue;
                var def = _unitCache[unit.DefinitionId];

                // --- 1. Calculate Stats (NO LINQ!) ---
                float speedMod = 1.0f;
                float dmgMod = 1.0f;
                bool isHardCcd = false;

                // One single fast loop to check all statuses without allocating memory
                for (int s = 0; s < unit.Statuses.Count; s++)
                {
                    var status = unit.Statuses[s];
                    if (status.Name == "Slow" || status.Name == "Speed") speedMod *= status.Value;
                    else if (status.Name == "Rage") dmgMod *= status.Value;
                    else if (status.Name == "Freeze" || status.Name == "Stun" || status.Name == "Knockback" || status.Name == "Blackhole") isHardCcd = true;
                }

                // Decrement Attack Cooldown
                if (unit.AttackCooldown > 0) unit.AttackCooldown -= (1000f / GameEngine.TICKS_PER_SECOND);

                // Check for hard CC
                if (isHardCcd)
                {
                    // Duplicate death logic here so we don't miss it
                    if (unit.CurrentHealth <= 0)
                    {
                        _state.Units.RemoveAt(i);
                    }
                    continue; // Do not attack or move if hard CC'd
                }

                // --- 2. Target Acquisition ---
                // Pass the index 'i' so we can look left and right instantly
                var enemies = FindTargetsFast(unit, i, def);

                float distToCastle = GetDistanceToEnemyCastle(unit);
                bool castleInRange = distToCastle <= def.Range;

                // --- 3. Combat Logic ---
                if (enemies.Count > 0)
                {
                    unit.CurrentSpeed = 0;
                    if (unit.AttackCooldown <= 0 && def.AttackSpeed > 0)
                    {
                        unit.AttackCooldown = (1000f / def.AttackSpeed);

                        float impactForce = def.PushForce;
                        if (def.AttackType == AttackType.Siege) impactForce *= 2;
                        if (def.AttackType == AttackType.Ranged || def.AttackType == AttackType.Magic) impactForce /= 2;

                        for (int e = 0; e < enemies.Count; e++)
                        {
                            ApplyDamage(enemies[e], (int)(def.Damage * dmgMod), def.AttackType, impactForce);
                        }
                    }
                }
                else if (castleInRange)
                {
                    unit.CurrentSpeed = 0;
                    if (unit.AttackCooldown <= 0) AttackCastle(unit, def);
                }
                else
                {
                    // --- 4. Movement Logic ---
                    unit.CurrentSpeed = def.MoveSpeed * speedMod;
                    if (speedMod > 0)
                    {
                        float direction = (unit.Side == 1) ? 1f : -1f;
                        unit.Position += (def.MoveSpeed * speedMod * direction);
                    }
                }

                // --- 5. Death Check ---
                if (unit.CurrentHealth <= 0)
                {
                    _state.Units.RemoveAt(i);
                }
            }

            // --- 6. Apply Knockback ---
            for (int i = _state.Units.Count - 1; i >= 0; i--)
            {
                var unit = _state.Units[i];
                if (unit.PendingKnockback != 0)
                {
                    unit.Position += unit.PendingKnockback;
                    unit.PendingKnockback = 0f;
                    unit.Statuses.Add(new ActiveStatus("Knockback", _state.CurrentTick + GameEngine.TICKS_PER_SECOND, 0f));
                    unit.AttacksWithoutKnockback = 0;
                    unit.LastKnockbackTick = _state.CurrentTick + 2 * GameEngine.TICKS_PER_SECOND;
                }
            }
        }

        // Note: We now pass in 'myIndex' from the MoveAndFight loop!
        private List<Unit> FindTargetsFast(Unit attacker, int myIndex, UnitDefinition attackerDef)
        {
            List<Unit> validTargets = new List<Unit>();

            // SAFETY BUFFER: Width of the largest units in the game
            float maxEnemyWidthPad = 200f;

            // --- 1. Look RIGHT (Forward in the sorted list) ---
            for (int i = myIndex + 1; i < _state.Units.Count; i++)
            {
                var other = _state.Units[i];

                // Original Direction Check
                if (attacker.Side == 1 && other.Position < attacker.Position) continue;
                if (attacker.Side == 2 && other.Position > attacker.Position) continue;

                float centerDist = Math.Abs(attacker.Position - other.Position);

                // OPTIMIZATION: If this unit's center is too far away, ALL subsequent units are too far! Break the loop.
                if (centerDist - (attackerDef.Width / 2f) - maxEnemyWidthPad > attackerDef.Range)
                    break;

                if (other.Side == attacker.Side) continue; // Friend
                if (other.CurrentHealth <= 0) continue; // Dead (Extra safety check)

                var otherDef = _unitCache[other.DefinitionId];
                if (otherDef.ArmorType == ArmorType.Flying && attackerDef.AttackType != AttackType.Ranged)
                    continue;

                // Original Hitbox Math
                float edgeToEdgeDist = centerDist - (attackerDef.Width / 2f) - (otherDef.Width / 2f);
                float actualDist = Math.Max(0f, edgeToEdgeDist);

                if (actualDist <= attackerDef.Range)
                {
                    validTargets.Add(other);
                }
            }

            // --- 2. Look LEFT (Backward in the sorted list) ---
            for (int i = myIndex - 1; i >= 0; i--)
            {
                var other = _state.Units[i];

                // Original Direction Check
                if (attacker.Side == 1 && other.Position < attacker.Position) continue;
                if (attacker.Side == 2 && other.Position > attacker.Position) continue;

                float centerDist = Math.Abs(attacker.Position - other.Position);

                // OPTIMIZATION: If this unit's center is too far away, ALL preceding units are too far! Break the loop.
                if (centerDist - (attackerDef.Width / 2f) - maxEnemyWidthPad > attackerDef.Range)
                    break;

                if (other.Side == attacker.Side) continue; // Friend
                if (other.CurrentHealth <= 0) continue; // Dead

                var otherDef = _unitCache[other.DefinitionId];
                if (otherDef.ArmorType == ArmorType.Flying && attackerDef.AttackType != AttackType.Ranged)
                    continue;

                // Original Hitbox Math
                float edgeToEdgeDist = centerDist - (attackerDef.Width / 2f) - (otherDef.Width / 2f);
                float actualDist = Math.Max(0f, edgeToEdgeDist);

                if (actualDist <= attackerDef.Range)
                {
                    validTargets.Add(other);
                }
            }

            return validTargets;
        }

        public float GetDistanceToEnemyCastle(Unit attacker)
        {
            // If Player 1 (Left), enemy castle is at MAP_WIDTH - 200
            if (attacker.Side == 1)
            {
                float dist = MAP_WIDTH - 200 - (attacker.Position + attacker.Width);
                return Math.Max(0f, dist);
            }
            // If Player 2 (Right), enemy castle is at 200
            else
            {
                float dist = attacker.Position - 200;
                return Math.Max(0f, dist);
            }
        }

        public void AttackCastle(Unit attacker, UnitDefinition def)
        {
            // 1. Identify Enemy
            var enemyPlayer = attacker.Side == 1 ? _state.Player2 : _state.Player1;

            // 2. Reset Cooldown
            attacker.AttackCooldown = (1000f / def.AttackSpeed);

            // 3. Deal Damage
            float damage = def.Damage;

            // Siege units usually deal double damage to structures
            if (def.AttackType == AttackType.Siege) damage *= 2;

            var dmgStatuses = attacker.Statuses.Where(s => s.Name == "Rage");
            foreach (var status in dmgStatuses)
            {
                damage *= status.Value; // Multiplicative stacking (e.g. 0.5 * 1.5 = 0.75)
            }

            DamageCastle(enemyPlayer, (int)damage);
        }

        public void DamageCastle(PlayerState player, int damage)
        {
            // Invulnerability check:
            if (player.IsInvulnerable)
            {
                return; // No damage dealt
            }

            // Prevent 1-shots
            if (player.CastleHealth == player.CastleMaxHealth && damage >= player.CastleMaxHealth)
                player.CastleHealth = 1;
            else
                player.CastleHealth -= damage;

            // 5. Game Over Check
            if (player.CastleHealth <= 0)
            {
                player.CastleHealth = 0;
                _state.IsGameOver = true;
                _state.WinnerSide = player.Side == 1 ? 2 : 1;
            }
        }

        // Negative damage => heal
        public void ApplyDamage(Unit target, int amount, AttackType type, float impactForce)
        {
            // Check if unit is invulnerable
            if (target.Statuses.Any(s => s.Name == "Invulnerable"))
            {
                // Invulnerable units take no damage or knockback
                return;
            }

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
                else
                {
                    // Shielded units don't get knocked back
                    return;
                }
            }
            else
            {
                target.CurrentHealth -= amount;

                // Handle overheal
                if (target.CurrentHealth > target.MaxHealth)
                {
                    target.CurrentHealth = target.MaxHealth;
                }
            }

            // --- PHYSICS & MOMENTUM ---
            if (_state.CurrentTick < target.LastKnockbackTick)
            {
                return;
            }

            var enemyDef = _unitCache[target.DefinitionId];
            float resistance = Math.Max(1f, enemyDef.EffectiveWeight);
            float knockbackDist = impactForce / resistance;
            knockbackDist = Math.Min(knockbackDist, 3000f);

            if (target.AttacksWithoutKnockback >= 50 && target.Tier < 8)
            {
                knockbackDist = 25f;
            }
            else if (target.AttacksWithoutKnockback >= 250 && target.Tier == 8)
            {
                knockbackDist = 10f;
            }

            if (knockbackDist > 10f)
            {
                // Walls can only be knocked back a tiny bit
                if (target.DefinitionId.StartsWith("wall"))
                    knockbackDist = 10f;
                // Tier 8 units can only be knocked back a small amount
                if (target.Tier == 8)
                    knockbackDist = Math.Min(knockbackDist, 10f);

                float direction = (target.Side == 1) ? -1f : 1f;
                target.PendingKnockback += (knockbackDist * direction);
            }
            else
            {
                if (amount > 0)
                    target.AttacksWithoutKnockback++;
            }
        }

        public void AddGadgetXp(int side, string gadgetId, int amount)
        {
            var player = side == 1 ? _state.Player1 : _state.Player2;

            player.AddGadgetXp(gadgetId, amount);
        }

        private void GiveIncome(PlayerState player)
        {
            player.Money += player.Income;
        }

        private void TickCooldowns(PlayerState player)
        {
            // Tick unit cooldowns?
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

            // Tick gadget cooldowns
            foreach (var key in player.GadgetCooldowns.Keys.ToList())
            {
                if (player.GadgetCooldowns[key] > 0)
                {
                    player.GadgetCooldowns[key]--;
                }
            }
        }

        // ------------- RECORDING HOOKS ---------------
        public int LastActionP1 { get; private set; }
        public int LastActionP2 { get; private set; }

        public void ResetLastActions()
        {
            LastActionP1 = 0;
            LastActionP2 = 0;
        }

        // ------------- AI VIBE CODE ---------------
        // We track previous health to calculate immediate rewards!
        private int _prevP1CastleHealth;
        private double _prevP1Income;
        private double _prevP1Money;
        private int _prevP1MaxHealth;
        private int _prevP1UnitCount;
        private int _prevP2CastleHealth;
        private double _prevP2Income;
        private double _prevP2Money;
        private int _prevP2MaxHealth;
        private int _prevP2UnitCount;
        private int _prevP1OffGadgetLevel;
        private int _prevP1DefGadgetLevel;
        private int _prevP1SigGadgetLevel;
        private int _prevP2OffGadgetLevel;
        private int _prevP2DefGadgetLevel;
        private int _prevP2SigGadgetLevel;
        private float _currentDenseWeight = 1.0f;
        private bool _initialStateRewarded = false;

        public StepResult Step(int actionP1, int actionP2, float denseRewardWeight = 1.0f)
        {
            // 1. Record the "Before" state for reward calculation
            _prevP1CastleHealth = _state.Player1.CastleHealth;
            _prevP1Income = _state.Player1.Income;
            _prevP1Money = _state.Player1.Money;
            _prevP1MaxHealth = _state.Player1.CastleMaxHealth;
            _prevP1UnitCount = _state.Units.Where(u => u.Side == 1).ToList().Count();
            _prevP2CastleHealth = _state.Player2.CastleHealth;
            _prevP2Income = _state.Player2.Income;
            _prevP2Money = _state.Player2.Money;
            _prevP2MaxHealth = _state.Player2.CastleMaxHealth;
            _prevP2UnitCount = _state.Units.Where(u => u.Side == 2).ToList().Count();
            _prevP1OffGadgetLevel = _state.Player1.OffensiveGadget?.Level ?? 1;
            _prevP1DefGadgetLevel = _state.Player1.DefensiveGadget?.Level ?? 1;
            _prevP1SigGadgetLevel = _state.Player1.SignatureGadget?.Level ?? 1;
            _prevP2OffGadgetLevel = _state.Player2.OffensiveGadget?.Level ?? 1;
            _prevP2DefGadgetLevel = _state.Player2.DefensiveGadget?.Level ?? 1;
            _prevP2SigGadgetLevel = _state.Player2.SignatureGadget?.Level ?? 1;
            _currentDenseWeight = denseRewardWeight;

            // 2. Decode the AI's chosen action and execute it
            bool p1ActionSucceeded = ApplyAction(1, actionP1);
            bool p2ActionSucceeded = ApplyAction(2, actionP2);

            // 3. Advance the simulation by EXACTLY one tick
            Tick();

            // 4. Calculate how well the AI did on this specific tick
            float p1Reward = CalculateReward(1, p1ActionSucceeded && actionP1 > 10);
            float p2Reward = CalculateReward(2, p2ActionSucceeded && actionP2 > 10);

            // 5. Flatten the new game state for the AI's neural network
            float[] p1State = _state.GetStateVector(1);
            float[] p2State = _state.GetStateVector(2);

            // 6. Get action masks for both players
            int[] p1Mask = _state.GetActionMask(1);
            int[] p2Mask = _state.GetActionMask(2);

            return new StepResult
            {
                P1State = p1State,
                P2State = p2State,
                P1ActionMask = p1Mask,
                P2ActionMask = p2Mask,
                P1Reward = p1Reward,
                P2Reward = p2Reward,
                IsDone = _state.IsGameOver,
                WinnerSide = _state.WinnerSide
            };
        }

        public bool ApplyAction(int side, int actionId)
        {
            if (side == 1) LastActionP1 = actionId;
            else LastActionP2 = actionId;

            var player = side == 1 ? _state.Player1 : _state.Player2;
            bool actionSucceeded = false;

            // --- Actions 1 through 8: Dynamic Unit Spawning ---
            if (actionId >= 1 && actionId <= 8)
            {
                // Arrays are 0-indexed, so Action 1 looks at Roster[0] (Tier 1)
                int rosterIndex = actionId - 1;

                // Grab the roster for this specific player's team
                var teamRoster = GameDataManager.Teams.Find(t => t.Color == player.Team)?.Roster;
                if (teamRoster == null) return actionSucceeded;

                if (rosterIndex < teamRoster.Count)
                {
                    actionSucceeded = SpawnUnit(side, teamRoster[rosterIndex].Id);
                }
                return actionSucceeded;
            }

            // --- Actions 9 through 13: Abilities and Economy ---
            switch (actionId)
            {
                case 0:
                    // Do Nothing (Crucial so the AI can save up money!)
                    break;
                case 9:
                    actionSucceeded = Invest(side);
                    break;
                case 10:
                    actionSucceeded = Repair(side);
                    break;
                case 11:
                    if (player.OffensiveGadget != null) actionSucceeded = UseGadget(side, player.OffensiveGadget.Id, -1);
                    break;
                case 12:
                    if (player.DefensiveGadget != null) actionSucceeded = UseGadget(side, player.DefensiveGadget.Id, -1);
                    break;
                case 13:
                    if (player.SignatureGadget != null) actionSucceeded = UseGadget(side, player.SignatureGadget.Id, -1);
                    break;
            }

            return actionSucceeded;
        }

        private float CalculateReward(int side, bool gadgetUsedThisTick)
        {
            float reward = 0f;

            // Determine who is who
            var myPlayer = side == 1 ? _state.Player1 : _state.Player2;
            var enemyPlayer = side == 1 ? _state.Player2 : _state.Player1;

            int myPrevHealth = side == 1 ? _prevP1CastleHealth : _prevP2CastleHealth;
            double myPrevIncome = side == 1 ? _prevP1Income : _prevP2Income;
            double myPrevMoney = side == 1 ? _prevP1Money : _prevP2Money;
            int myPrevMaxHealth = side == 1 ? _prevP1MaxHealth : _prevP2MaxHealth;
            int prevAllyCount = side == 1 ? _prevP1UnitCount : _prevP2UnitCount;
            int enemyPrevHealth = side == 1 ? _prevP2CastleHealth : _prevP1CastleHealth;
            int prevEnemyCount = side == 1 ? _prevP2UnitCount : _prevP1UnitCount;

            // --- 0. INITIAL STATE REWARD (first tick only) ---
            // When the time machine drops the AI into a mid-game state, credit it for the
            // economy/upgrades already in place — the same rewards it would have earned by
            // reaching that state organically. This teaches the model that progressing through
            // early-game stages is inherently valuable, not just a means to an end.
            if (!_initialStateRewarded && side == 1)
            {
                _initialStateRewarded = true;
                for (int i = 0; i < _state.Player1.InvestmentCount; i++)
                    reward += _rewardParams.InvestReward + (11 - i) * _rewardParams.InvestDecay;
                for (int i = 0; i < _state.Player2.InvestmentCount; i++)
                    reward += _rewardParams.InvestReward + (11 - i) * _rewardParams.InvestDecay;
                reward += ((_state.Player1.OffensiveGadget?.Level ?? 1) - 1) * _rewardParams.GadgetUpgrade;
                reward += ((_state.Player1.DefensiveGadget?.Level  ?? 1) - 1) * _rewardParams.GadgetUpgrade;
                reward += ((_state.Player1.SignatureGadget?.Level  ?? 1) - 1) * _rewardParams.GadgetUpgrade;
                reward += ((_state.Player2.OffensiveGadget?.Level ?? 1) - 1) * _rewardParams.GadgetUpgrade;
                reward += ((_state.Player2.DefensiveGadget?.Level  ?? 1) - 1) * _rewardParams.GadgetUpgrade;
                reward += ((_state.Player2.SignatureGadget?.Level  ?? 1) - 1) * _rewardParams.GadgetUpgrade;
                for (int i = 1; i <= _state.Player1.RepairCount; i++)
                    reward += Math.Max((5 - i) * 5f, 0f);
                for (int i = 1; i <= _state.Player2.RepairCount; i++)
                    reward += Math.Max((5 - i) * 5f, 0f);
            }

            // --- 1. THE TIME PENALTY ---
            // A tiny negative incentive every single tick.
            // At 30 ticks a second, -0.01f is -0.3f points per second, or -90f over 5 minutes.
            // This pushes them to end the game fast without making them want to instantly die.
            reward -= 0.011f;

            // --- 2. COMBAT REWARDS ---

            // + Points per enemy slain
            int enemiesSlain = prevEnemyCount - _state.Units.Where(u => u.Side == enemyPlayer.Side).ToList().Count();
            reward += (float)Math.Max(enemiesSlain, 0) * 6f * _rewardParams.CombatScale;
            // - Points per ally lost
            int myUnitsLost = prevAllyCount - _state.Units.Where(u => u.Side == myPlayer.Side).Count();
            reward -= Math.Max(myUnitsLost, 0) * 5f * _rewardParams.CombatScale;

            // + Points for damaging the enemy castle (Percentage based!)
            int damageDealt = enemyPrevHealth - enemyPlayer.CastleHealth;
            if (damageDealt > 0)
            {
                float pctDamageDealt = (float)damageDealt / enemyPlayer.CastleMaxHealth;
                reward += (float)(Math.Pow(pctDamageDealt, 2f) * 300f * _rewardParams.CombatScale);
            }
            // - Points for taking castle damage
            int myDamageTaken = myPrevHealth - myPlayer.CastleHealth;
            if (myDamageTaken > 0)
            {
                float pctMyDamage = (float)myDamageTaken / myPlayer.CastleMaxHealth;
                float dangerMultiplier = Math.Min(3.0f, 5.0f * (1.0f - (float)myPlayer.CastleHealth / myPlayer.CastleMaxHealth));
                reward -= pctMyDamage * 600f * dangerMultiplier * _rewardParams.CombatScale;
            }

            // --- 3. ECONOMY & UPGRADE REWARDS ---

            // Reward them heavily for successfully increasing their income
            if (myPlayer.Income - myPrevIncome > 0)
            {
                reward += _rewardParams.InvestReward + (11 - myPlayer.InvestmentCount) * _rewardParams.InvestDecay;
            }
            // Anti-spend: penalises any unit purchase while HP is high and economy isn't maxed.
            // Fades naturally as the model builds its economy (full penalty at count=0, zero at count=8).
            float hpRatio = (float)myPlayer.CastleHealth / myPlayer.CastleMaxHealth;
            float savingsProgress = Math.Min(1.0f, (float)(myPrevMoney / myPlayer.InvestmentPrice));
            float hpPenaltyFactor = Math.Max(0.0f, Math.Min(1.0f, (hpRatio - 0.5f) / 0.4f));
            if (hpPenaltyFactor > 0f && myPlayer.InvestmentCount < 8
                && savingsProgress > 0.6f && myPlayer.Money < myPrevMoney && myPrevIncome == myPlayer.Income)
            {
                float penaltyScale = (savingsProgress - 0.6f) / 0.4f;
                reward -= penaltyScale * _rewardParams.AntiSpend * hpPenaltyFactor;
            }
            // Survival bonus: reward spending at low HP to encourage fighting back when cornered.
            // Capped at zero — negative urgency (HP > 90%) was causing action-0 dominance where the
            // penalty gradient overwhelmed the policy from the first update, freezing greedy inference.
            // Doesn't apply to investing.
            if (hpRatio < 0.9f && myPlayer.Money < myPrevMoney && myPrevIncome == myPlayer.Income)
            {
                float urgency = (0.9f - hpRatio) / 0.9f; // 0 at 90% HP, 1 at 0% HP
                reward += urgency * _rewardParams.AntiSpend;
            }
            // Reward saving up to invest, if we're at a high investment tier, we don't want to worry about saving any more
            if (myPlayer.InvestmentCount <= 7)
            {
                // Tent shape: linearly increases 0→1 from 0% to 100% of invest price,
                // then linearly decreases 1→0 from 100% to 120%, and stays at 0 beyond that.
                // No discontinuity at the threshold, and no incentive to accumulate excess savings.
                float savingsFraction = (float)(myPlayer.Money / myPlayer.InvestmentPrice);
                if (savingsFraction > 1.0f)
                    savingsFraction = Math.Max(0f, (1.2f - savingsFraction) / 0.2f);
                float savingsBoost = Math.Max(1.0f, 4.0f - myPlayer.InvestmentCount * 0.5f);
                reward += savingsFraction * _rewardParams.SavingsWeight * savingsBoost;
            }

            // Reward them for successfully upgrading their base health
            float healthDelta = myPlayer.CastleHealth - myPrevHealth; // min: 11,000 (+10)
            if (healthDelta > 0)
            {
                reward += healthDelta / 1100f + Math.Max((5 - myPlayer.RepairCount), 0) * 5f;
            }

            // Reward gadget upgrades (reaching the next tier after enough uses)
            int prevOffLevel = side == 1 ? _prevP1OffGadgetLevel : _prevP2OffGadgetLevel;
            int prevDefLevel = side == 1 ? _prevP1DefGadgetLevel : _prevP2DefGadgetLevel;
            int prevSigLevel = side == 1 ? _prevP1SigGadgetLevel : _prevP2SigGadgetLevel;
            if ((myPlayer.OffensiveGadget?.Level ?? 1) > prevOffLevel) reward += _rewardParams.GadgetUpgrade;
            if ((myPlayer.DefensiveGadget?.Level ?? 1) > prevDefLevel) reward += _rewardParams.GadgetUpgrade;
            if ((myPlayer.SignatureGadget?.Level ?? 1) > prevSigLevel) reward += _rewardParams.GadgetUpgrade;

            // Reward successfully activating a gadget (dense phase only)
            if (gadgetUsedThisTick)
                reward += _rewardParams.GadgetUse * _currentDenseWeight;

            // --- 4. ENDGAME MULTIPLIERS ---
            if (_state.IsGameOver)
            {
                float winRew  = _state.IsTimeLimit ? _rewardParams.WinReward * 0.45f : _rewardParams.WinReward;
                float lossRew = _state.IsTimeLimit ? _rewardParams.WinReward * 0.55f : _rewardParams.WinReward;

                if (_state.WinnerSide == side)
                    reward += winRew;
                else if (_state.WinnerSide != 0)
                    reward -= lossRew;
            }

            return reward / 1000f;
        }
    }
}