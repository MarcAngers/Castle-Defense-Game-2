using System;
using System.Collections.Generic;
using System.Text;

namespace CastleDefense.Engine.Models
{
    public enum TeamColour
    {
        White,
        Black,
        Red,
        Blue,
        Green,
        Orange,
        Purple,
        Yellow
    }

    public enum AttackType
    {
        Melee,      // Standard physical hit
        Ranged,     // Can hit Flying units
        Siege,      // Bonus damage vs Castle/Walls
        Magic,      // Ignores Armor
        Support     // Heals or Buffs (Negative damage)
    }

    public enum ArmorType
    {
        None,      // Standard
        Shield,      // Resistant to physical, weak to Magic
        Flying,     // Immune to Melee/Siege, weak to Ranged
        Divine,     // (Yellow Team) Has a shield layer
        Building    // Castles/Walls (Takes extra Siege damage)
    }

    public enum GadgetSlot
    {
        Offense,    // The Red Slot (Nukes, Snipes)
        Defense,    // The Blue Slot (Walls, Heals)
        Signature   // The Team Slot (Black Hole, Stampede)
    }

    public enum GadgetType
    {
        DirectDamage,   // Hits specific target or area
        SpawnUnit,      // Summons units
        StatusEffect,   // Freeze, Slow, Buff
        ModifyTerrain,  // Walls, Fire on ground
        Economy         // Money modification
    }
}
