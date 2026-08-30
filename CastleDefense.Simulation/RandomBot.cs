using System;
using System.Collections.Generic;
using System.Text;

namespace CastleDefense.Simulation
{
    public class RandomBot
    {
        Random rand;

        public RandomBot()
        {
            rand = new Random();
        }

        public int GetAction()
        {
            // Random bot plays completely randomly
            return rand.Next(14);
        }
    }
}
