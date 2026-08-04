namespace CastleDefense.Engine.Models.Hazards
{
    public abstract class Hazard
    {
        public string Type { get; set; } // "Fire", "Ice", "PoisonCloud", etc.
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
