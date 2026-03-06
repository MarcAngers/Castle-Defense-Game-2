using System;
using System.Collections.Generic;
using System.Text;

namespace CastleDefense.Engine.Gadgets
{
    public interface IGadgetEffect
    {
        void Execute(GameEngine engine, int side, int position);
    }
}
