namespace ArvinCombat
{
    /// <summary>
    /// What happened in one match, for achievements. Filled by MatchManager (offline) or OnlineMatch (online)
    /// from the local player's fighter events.
    /// </summary>
    public class FightStats
    {
        public int parries;
        public bool combo4;          // landed the 4th move of a combo
        public bool specialKO;       // won a round by K.O. with a special move
        public bool airKO;           // won a round by K.O. with a jump attack
        public bool flawlessRound;   // won a round without taking damage
        public bool untouchable = true; // took no damage the whole match
        public bool lostFirstRound;

        Fighter _me;
        bool _roundHit;
        int _rounds;

        public void Attach(Fighter me)
        {
            _me = me;
            if (me == null) return;
            me.OnDamaged += (victim, amount, point, blocked) =>
            {
                if (amount >= 1f) { _roundHit = true; untouchable = false; }
            };
            me.OnParry += _ => parries++;
            me.OnHitLanded += (f, chain) => { if (chain >= Fighter.MaxChain - 1) combo4 = true; };
        }

        public void RoundStart() => _roundHit = false;

        /// <summary>won: the local player won the round. ko: the round ended by knock-out (not time).</summary>
        public void RoundEnd(bool won, bool ko)
        {
            _rounds++;
            if (_rounds == 1 && !won) lostFirstRound = true;
            if (!won) return;
            if (!_roundHit) flawlessRound = true;
            if (ko && _me != null)
            {
                if (_me.LastLandedSpecial) specialKO = true;
                else if (_me.LastLandedJump) airKO = true;
            }
        }
    }
}
