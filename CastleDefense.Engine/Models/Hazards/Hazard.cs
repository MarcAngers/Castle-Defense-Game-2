namespace CastleDefense.Engine.Models.Hazards
{
    public abstract class Hazard
    {
        /// The hazard kind. The COMPLETE set the engine creates is: "Blackhole", "Fire",
        /// "Goo", "Poison", "Wave".
        ///
        /// Corrected 2026-08-29: this line used to read `"Fire", "Ice", "PoisonCloud", etc.`
        /// Neither "Ice" nor "PoisonCloud" has ever existed -- the poison cloud's Type is
        /// plain "Poison", and there is no ice hazard at all (freeze is a unit STATUS, not a
        /// ground hazard). The "etc." was doing real damage too: it implied the list was
        /// open, when ProcessHazards dispatches on exactly these five.
        public string Type { get; set; }
        public string SourceGadgetId { get; set; }
        public int Side { get; set; }
        public float BaseValue { get; set; }

        public float Position { get; set; } // The starting X coordinate (left edge)
        public float Width { get; set; }    // How far the hazard stretches
        public int ExpiresAtTick { get; set; } // When the hazard disappears

        public abstract void ProcessEffect(GameState state);
        public virtual void OnExpire(GameState state) { }

        /// <summary>
        /// Polymorphic deep copy. MemberwiseClone preserves the runtime type, so a
        /// FireHazard clones to a FireHazard without this base class needing to know the
        /// subclass list — and every subclass today adds only behaviour (an overridden
        /// ProcessEffect), no state, so copying the base fields copies everything.
        ///
        /// IF YOU ADD A REFERENCE-TYPED FIELD to Hazard or any subclass, override this.
        /// A memberwise copy would share it between the clone and the original, which
        /// silently corrupts search rollouts rather than failing loudly.
        /// </summary>
        public virtual Hazard Clone() => (Hazard)MemberwiseClone();
    }
}
