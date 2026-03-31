using System;
using System.Collections.Generic;
using System.Text;

namespace CastleDefense.Simulation
{
    public class SpamBot
    {
        public int spamTier;

        public SpamBot(int tier)
        {
            spamTier = tier;
        }

        public int GetAction()
        {
            // Spam bot just spams the unit of a given tier the whole game
            return this.spamTier;
        }
    }
}
