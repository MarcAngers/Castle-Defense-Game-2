using System.Text.Json;
using System.Text.Json.Serialization;
using CastleDefense.Engine.Definitions;
using CastleDefense.Engine.Models;
using CastleDefense.Engine.Models.Hazards;

namespace CastleDefense.Api.Services
{
    /// <summary>
    /// The shape of a GameState as it goes down the wire to a browser.
    ///
    /// WHY THIS EXISTS. The game loop broadcasts a full GameState to every client 30 times
    /// a second. Serialising the ENGINE's objects sent every public field of every unit,
    /// and most of them cannot change: Width, Height, Damage, Range, AttackSpeed, Weight,
    /// PushForce, AttackType and ArmorType are fixed for a unit's whole life, and the three
    /// GadgetDefinitions on each PlayerState carried a Description string and a serialised
    /// IGadgetEffect. Measured over a complete HeuristicBot game (533 samples, 30 ticks/s):
    ///
    /// Measured over 1,377 sampled ticks across three complete HeuristicBot games:
    ///
    ///     full engine state    20,926 B/tick   613 KB/s   180 MB per 5-minute game
    ///     dropping dead fields 10,528 B/tick   308 KB/s    90 MB   (1.99x)
    ///     + packed units        4,999 B/tick   146 KB/s    43 MB   (4.19x)
    ///
    /// It matters twice over: egress is the only resource this game uses in quantity (a
    /// whole game costs 0.14% of a CPU core, so hosting cost IS bandwidth cost), and 180 MB
    /// of cellular data per game is not something a phone can play, which this game is
    /// required to do.
    ///
    /// The remaining 4,999 B splits as ~3,050 B of units and ~1,950 B of header, of which
    /// ~857 B is the six GadgetDefinitions. Those are constant except on upgrade, so the
    /// next lever is caching them client-side off GameJoined/GadgetUpgraded -- not taken
    /// here, because a client whose cache missed an upgrade would misprice a button with no
    /// visible symptom. The lever after that is delta encoding against the previous tick.
    ///
    /// Compression would have been the change that needed no allowlist at all, and gzip on
    /// the raw state measures 6.2x. It is NOT available: permessage-deflate is reachable
    /// only through WebSocketAcceptContext.DangerousEnableCompression, which applies to the
    /// raw WebSocket middleware, and SignalR accepts its own socket. The whole of
    /// Http.Connections.WebSocketOptions is CloseTimeout and SubProtocolSelector.
    ///
    /// THE RULE THAT KEEPS THIS CORRECT: every property below is named exactly as the
    /// client already reads it, so the browser needed no change at all. The field list is
    /// not a guess -- it is every state field read anywhere in wwwroot/src and
    /// wwwroot/static/views/view-logic.
    ///
    /// THE TRADE, STATED PLAINLY: this is now a hand-maintained allowlist. Adding a field
    /// to GameState, PlayerState or Unit no longer makes it visible to the client; it has
    /// to be added HERE too. That is a real maintenance cost and the reason this file is
    /// one place rather than scattered over the call sites. If a new client feature reads
    /// a state field and gets undefined, this file is why.
    ///
    /// Deliberately NOT sent, beyond the constants above:
    ///   - PlayerState.ConnectionId. The loop broadcasts to the GROUP, so this was handing
    ///     each player their opponent's connection id every tick. CLAUDE.md's rejoin note
    ///     already says anything on PlayerState is handed to the opponent, which is why the
    ///     rejoin TOKEN is kept off it; the connection id had simply been missed.
    ///   - (UnitCharges / CooldownTimers WERE listed here as unread. They are sent again as
///     of 2026-09-01, when unit charges became a real mechanic and the unit buttons grew
///     the same cooldown wash the gadgets have. They are sent SPARSELY -- see PlayerWire.)
    ///   - Unit.CurrentSpeed, BaseSpeed, Damage, Range, AttackSpeed, Weight, PushForce,
    ///     EffectiveWeight, AttackType, ArmorType, PendingKnockback, LastKnockbackTick,
    ///     AttacksWithoutKnockback. The client draws from the roster CSV it already loads.
    ///   - ActiveStatus.SourceGadgetId / ExpiresAtTick / Value / Side. The client switches
    ///     on the NAME alone (view.js StatusColorMap, status-particle-map.js), so a status
    ///     is one string on the wire -- but it stays an OBJECT with a name property,
    ///     because that is what the client reads.
    /// </summary>
    public sealed class GameStateWire
    {
        public Guid GameId { get; init; }
        public string GameMode { get; init; }
        // Both of these index loader.assets.teamList, so they must stay NUMERIC. Typed as
        // int rather than TeamColour so that adding a string-enum converter anywhere in the
        // app cannot silently turn them into names and break every map and team lookup.
        public int Map { get; init; }
        public bool ShadowMap { get; init; }
        public bool IsGameOver { get; init; }
        public bool IsTimeLimit { get; init; }
        public int WinnerSide { get; init; }
        public long CurrentTick { get; init; }
        public PlayerWire Player1 { get; init; }
        public PlayerWire Player2 { get; init; }
        public List<UnitWire> Units { get; init; }
        public List<HazardWire> Hazards { get; init; }

        public static GameStateWire From(GameState s)
        {
            var units = new List<UnitWire>(s.Units.Count);
            foreach (var u in s.Units) units.Add(UnitWire.From(u));

            var hazards = new List<HazardWire>(s.Hazards.Count);
            foreach (var h in s.Hazards) hazards.Add(HazardWire.From(h));

            return new GameStateWire
            {
                GameId = s.GameId,
                GameMode = s.GameMode,
                Map = (int)s.Map,
                ShadowMap = s.ShadowMap,
                IsGameOver = s.IsGameOver,
                IsTimeLimit = s.IsTimeLimit,
                WinnerSide = s.WinnerSide,
                CurrentTick = s.CurrentTick,
                Player1 = PlayerWire.From(s.Player1),
                Player2 = PlayerWire.From(s.Player2),
                Units = units,
                Hazards = hazards,
            };
        }
    }

    public sealed class PlayerWire
    {
        public int Side { get; init; }
        public int Team { get; init; }
        public double Money { get; init; }
        public double Income { get; init; }
        public double InvestmentPrice { get; init; }
        public int InvestmentCount { get; init; }
        public bool ArmageddonUsed { get; init; }
        public int CastleHealth { get; init; }
        public int CastleMaxHealth { get; init; }
        public int CastleShield { get; init; }
        public bool IsInvulnerable { get; init; }
        public double RepairPrice { get; init; }
        public int RepairCount { get; init; }
        public int AutoSpawnLevel { get; init; }
        public double AutoSpawnPrice { get; init; }

        /// <summary>
        /// Unit charges and their regeneration timers, for the cooldown wash on the unit
        /// buttons.
        ///
        /// SENT SPARSELY: only units that are actually short of charges appear. A player who
        /// has bought nothing sends two empty objects, and the common mid-game case is one or
        /// two entries -- which matters because this file exists to keep the per-tick payload
        /// small, and a dense version would carry sixteen roster entries per player forever.
        /// The client treats a missing id as "full, no cooldown", mirroring
        /// PlayerState.GetUnitCharges.
        /// </summary>
        public Dictionary<string, int> UnitCharges { get; init; }
        public Dictionary<string, long> UnitCooldowns { get; init; }
        public GadgetWire OffensiveGadget { get; init; }
        public GadgetWire DefensiveGadget { get; init; }
        public GadgetWire SignatureGadget { get; init; }
        // Both are read live every frame by game.js (button enable/disable and the XP
        // pips), so they stay per-tick rather than being sent once on join.
        public Dictionary<string, long> GadgetCooldowns { get; init; }
        public Dictionary<string, int> GadgetXp { get; init; }

        public static PlayerWire From(PlayerState p)
        {
            if (p == null) return null;
            return new PlayerWire
            {
                Side = p.Side,
                Team = (int)p.Team,
                Money = p.Money,
                Income = p.Income,
                InvestmentPrice = p.InvestmentPrice,
                InvestmentCount = p.InvestmentCount,
                ArmageddonUsed = p.ArmageddonUsed,
                CastleHealth = p.CastleHealth,
                CastleMaxHealth = p.CastleMaxHealth,
                CastleShield = p.CastleShield,
                IsInvulnerable = p.IsInvulnerable,
                RepairPrice = p.RepairPrice,
                RepairCount = p.RepairCount,
                AutoSpawnLevel = p.AutoSpawnLevel,
                AutoSpawnPrice = p.AutoSpawnPrice,
                // Only what is not at rest -- see the note on the properties.
                UnitCharges = p.UnitCharges
                    .Where(kv => kv.Value < PlayerState.UnitMaxCharges)
                    .ToDictionary(kv => kv.Key, kv => kv.Value),
                UnitCooldowns = p.CooldownTimers
                    .Where(kv => kv.Value > 0)
                    .ToDictionary(kv => kv.Key, kv => kv.Value),
                OffensiveGadget = GadgetWire.From(p.OffensiveGadget),
                DefensiveGadget = GadgetWire.From(p.DefensiveGadget),
                SignatureGadget = GadgetWire.From(p.SignatureGadget),
                GadgetCooldowns = p.GadgetCooldowns,
                GadgetXp = p.GadgetXp,
            };
        }
    }

    /// <summary>
    /// A gadget as the game screen needs it. Description and the IGadgetEffect object are
    /// the two heavy fields and neither is read from state -- the Collection and gadget-info
    /// screens take their text from wwwroot/assets/master_gadgets.csv, not from here.
    ///
    /// These change only when a gadget upgrades, which is rare, but they stay in the
    /// per-tick payload rather than being cached client-side off GadgetUpgraded: at this
    /// size they are no longer worth the risk of a client whose cache missed an upgrade.
    /// </summary>
    public sealed class GadgetWire
    {
        public string Id { get; init; }
        public string Name { get; init; }
        public int Slot { get; init; }
        public int Tier { get; init; }
        public bool Targeted { get; init; }
        public int Cost { get; init; }
        public int UpgradeCost { get; init; }
        public string NextTierId { get; init; }
        public int CooldownMs { get; init; }
        public int Level { get; init; }

        public static GadgetWire From(GadgetDefinition g)
        {
            if (g == null) return null;
            return new GadgetWire
            {
                Id = g.Id,
                Name = g.Name,
                Slot = (int)g.Slot,
                Tier = g.Tier,
                Targeted = g.Targeted,
                Cost = g.Cost,
                UpgradeCost = g.UpgradeCost,
                NextTierId = g.NextTierId,
                CooldownMs = g.CooldownMs,
                Level = g.Level,
            };
        }
    }

    /// <summary>
    /// A unit as the browser draws it.
    ///
    /// PACKED POSITIONALLY ON THE WIRE. Units are the only part of the payload that scales
    /// with the battle -- 37 of them on an average tick, up to 158 -- so their JSON keys
    /// were the single largest cost in the trimmed state: the fourteen names below cost
    /// more per unit than the values they label. UnitWireConverter writes this class as a
    /// bare JSON array instead, which is where 3,050 of the 8,575 bytes came from.
    ///
    /// The class deliberately keeps NAMED properties even though the wire has none, so that
    /// the ordering lives in exactly one place on each side: UnitWireConverter.Write here,
    /// and the UNIT_FIELD list in wwwroot/src/game-connection.js there. Those two orderings
    /// are the whole contract. Changing one without the other silently shifts every field
    /// by one, which is why the converter writes them in declaration order and the comment
    /// above the client's list points back at this type.
    /// </summary>
    [JsonConverter(typeof(UnitWireConverter))]
    public sealed class UnitWire
    {
        public Guid InstanceId { get; init; }
        public string DefinitionId { get; init; }
        public int Side { get; init; }
        public int Tier { get; init; }
        public float Position { get; init; }
        public int YPosition { get; init; }
        // LOGICAL size -- what the engine fights with. Sent because view.js draws the
        // sprite and health bar from it, and because Width is the invariant the weirdo
        // note in CLAUDE.md exists to protect. VisualScale is the appearance-only
        // multiplier on top; see Unit.VisualScale.
        public int Width { get; init; }
        public int Height { get; init; }
        public float VisualScale { get; init; }
        public int CurrentHealth { get; init; }
        public int MaxHealth { get; init; }
        public int CurrentShield { get; init; }
        // VisualUnit watches this for the RISING EDGE that triggers the attack lunge, so
        // it has to be the real per-tick timer, not a rounded one.
        public float AttackCooldown { get; init; }
        // Just the NAMES. The client switches on the name alone and reads nothing else off
        // a status, so the four other fields on ActiveStatus never leave the server. The
        // client rebuilds the { name } objects it expects.
        public List<string> Statuses { get; init; }

        public static UnitWire From(Unit u)
        {
            List<string> statuses = null;
            if (u.Statuses != null && u.Statuses.Count > 0)
            {
                statuses = new List<string>(u.Statuses.Count);
                foreach (var st in u.Statuses) statuses.Add(st.Name);
            }

            return new UnitWire
            {
                InstanceId = u.InstanceId,
                DefinitionId = u.DefinitionId,
                Side = u.Side,
                Tier = u.Tier,
                Position = u.Position,
                YPosition = u.YPosition,
                Width = u.Width,
                Height = u.Height,
                VisualScale = u.VisualScale,
                CurrentHealth = u.CurrentHealth,
                MaxHealth = u.MaxHealth,
                CurrentShield = u.CurrentShield,
                AttackCooldown = u.AttackCooldown,
                // Left NULL when empty, which is the common case -- the converter writes a
                // bare null and the client turns it back into []. The client's contract is
                // still "statuses is always an array"; that promise is kept there, where
                // end-game-show.js's `unit.statuses = []` assignment also lives.
                Statuses = statuses,
            };
        }
    }

    /// <summary>
    /// Writes a UnitWire as a positional JSON array. Outbound only -- nothing reads a unit
    /// back off the wire, and a Read that silently returned an empty unit would be worse
    /// than one that throws.
    ///
    /// THE ORDER HERE IS THE CONTRACT. It must match UNIT_FIELDS in
    /// wwwroot/src/game-connection.js exactly, including the trailing statuses slot.
    /// </summary>
    public sealed class UnitWireConverter : JsonConverter<UnitWire>
    {
        public override UnitWire Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o)
            => throw new NotSupportedException("UnitWire is serialise-only; the client never sends one back.");

        public override void Write(Utf8JsonWriter w, UnitWire u, JsonSerializerOptions o)
        {
            w.WriteStartArray();
            w.WriteStringValue(u.InstanceId);      // 0
            w.WriteStringValue(u.DefinitionId);    // 1
            w.WriteNumberValue(u.Side);            // 2
            w.WriteNumberValue(u.Tier);            // 3
            w.WriteNumberValue(u.Position);        // 4
            w.WriteNumberValue(u.YPosition);       // 5
            w.WriteNumberValue(u.Width);           // 6
            w.WriteNumberValue(u.Height);          // 7
            w.WriteNumberValue(u.VisualScale);     // 8
            w.WriteNumberValue(u.CurrentHealth);   // 9
            w.WriteNumberValue(u.MaxHealth);       // 10
            w.WriteNumberValue(u.CurrentShield);   // 11
            w.WriteNumberValue(u.AttackCooldown);  // 12
            if (u.Statuses == null || u.Statuses.Count == 0)
            {
                w.WriteNullValue();                // 13
            }
            else
            {
                w.WriteStartArray();
                foreach (var name in u.Statuses) w.WriteStringValue(name);
                w.WriteEndArray();
            }
            w.WriteEndArray();
        }
    }

    /// <summary>
    /// Hazards are already thinner than they look: GameState.Hazards is a List of the
    /// ABSTRACT Hazard, and System.Text.Json serialises the declared type, so subclass
    /// fields never went over the wire in the first place. Only wave-animator.js reads
    /// hazards at all, and only Type/Side/Position -- Width and ExpiresAtTick are kept
    /// because game-over.js reasons about hazards outliving the final state.
    /// </summary>
    public sealed class HazardWire
    {
        public string Type { get; init; }
        public int Side { get; init; }
        public float Position { get; init; }
        public float Width { get; init; }
        public int ExpiresAtTick { get; init; }

        public static HazardWire From(Hazard h) => new HazardWire
        {
            Type = h.Type,
            Side = h.Side,
            Position = h.Position,
            Width = h.Width,
            ExpiresAtTick = h.ExpiresAtTick,
        };
    }
}
