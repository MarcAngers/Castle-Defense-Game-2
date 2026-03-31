using CastleDefense.Engine.Data;
using CastleDefense.Engine.Models;
using Microsoft.AspNetCore.Authentication;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Numerics;
using System.Text;

namespace CastleDefense.Simulation
{
    internal class AntiSpamBot
    {
        public AntiSpamBot() { }

        public int GetAction(long tick, TeamColour team)
        {
            if (tick <= 421)
            {
                return 10; // Upgrade HP
            } else if (tick <= 781)
            {
                return 9; // Invest
            } else if (tick <= 1050)
            {
                return 3; // Spawn Tier 3 Unit
            } else if (tick <= 1380)
            {
                return 10; // Upgrade HP
            } else if (tick <= 1590)
            {
                return 3; // Spawn Tier 3 Unit
            } else if (tick <= 2100)
            {
                return 9; // Invest
            } else
            {
                if (team == TeamColour.Orange)
                    return 5; // Spawn Tier 5 Units for the rest of the game
                else if (team == TeamColour.Purple || team == TeamColour.Blue)
                    return 3; // Spawn Tier 3 Units for the rest of the game
                else
                    return 4; // Spawn Tier 4 Units for the rest of the game
            }
        }
    }

    // -------- ACTION MAP -------- //
    //if (actionId >= 1 && actionId <= 8)
    //{
    //    // Arrays are 0-indexed, so Action 1 looks at Roster[0] (Tier 1)
    //    int rosterIndex = actionId - 1;

    //    // Grab the roster for this specific player's team
    //    var teamRoster = GameDataManager.Teams.Find(t => t.Color == player.Team).Roster;

    //    if (rosterIndex<teamRoster.Count)
    //    {
    //        actionSucceeded = SpawnUnit(side, teamRoster[rosterIndex].Id);
    //    }
    //    return actionSucceeded;
    //}

    //        // --- Actions 9 through 13: Abilities and Economy ---
    //switch (actionId)
    //{
    //    case 0:
    //        // Do Nothing (Crucial so the AI can save up money!)
    //        break;
    //    case 9:
    //        actionSucceeded = Invest(side);
    //        break;
    //    case 10:
    //        actionSucceeded = Repair(side); // Now your "Upgrade Castle" logic
    //        break;
    //    case 11:
    //        if (player.DefensiveGadget != null) actionSucceeded = UseGadget(side, player.DefensiveGadget.Id, -1);
    //        break;
    //    case 12:
    //        if (player.OffensiveGadget != null) actionSucceeded = UseGadget(side, player.OffensiveGadget.Id, -1);
    //        break;
    //    case 13:
    //        if (player.SignatureGadget != null) actionSucceeded = UseGadget(side, player.SignatureGadget.Id, -1);
    //        break;
    //}
}
