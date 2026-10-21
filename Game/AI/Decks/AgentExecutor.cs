namespace WindBot.Game.AI.Decks
{
    /// <summary>
    /// Placeholder for decks played by an external brain (Brain=host:port, see BrainBridge).
    /// It registers no card actions, so it only matters when the brain falls back.
    /// </summary>
    [Deck("Agent", "AI_Agent", "NotFinished")]
    public class AgentExecutor : DefaultExecutor
    {
        public AgentExecutor(GameAI ai, Duel duel)
            : base(ai, duel)
        {
        }
    }

    /// <summary>The deck's old name, kept so existing launch commands (Deck=Claude) still work.</summary>
    [Deck("Claude", "AI_Agent", "NotFinished")]
    public class ClaudeExecutor : AgentExecutor
    {
        public ClaudeExecutor(GameAI ai, Duel duel)
            : base(ai, duel)
        {
        }
    }
}
