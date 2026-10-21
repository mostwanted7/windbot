using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using YGOSharp.OCGWrapper.Enums;

namespace WindBot.Game
{
    [DataContract]
    public class BrainReply
    {
        [DataMember(Name = "id")] public int Id { get; set; }
        [DataMember(Name = "choice")] public int[] Choice { get; set; }
        [DataMember(Name = "say")] public string Say { get; set; }
    }

    /// <summary>
    /// Forwards duel events and decisions to an external "brain" process, one JSON object per line over TCP.
    /// Every decision method returns null (or a negative sentinel) when the brain is unavailable or declines,
    /// in which case GameAI falls back to the regular executor logic. Only created when WindBot gets Brain=host:port.
    /// The message schema is documented in the ygo-agent repo (docs/decision-protocol.md).
    /// </summary>
    public class BrainBridge
    {
        public const int Fallback = -2;
        public const int Pass = -1;

        private TcpClient _client;
        private StreamReader _reader;
        private StreamWriter _writer;
        private readonly Duel _duel;
        private readonly Action<string, bool> _chat;
        private readonly Action<string> _log;
        private int _nextId;

        public bool Connected { get { return _client != null; } }

        public BrainBridge(string host, int port, Duel duel, Action<string, bool> chat, Action<string> log)
        {
            _duel = duel;
            _chat = chat;
            _log = log;
            try
            {
                _client = new TcpClient(host, port);
                _client.NoDelay = true;
                // Last-resort guard; the brain enforces its own (shorter) deadline and always answers.
                _client.ReceiveTimeout = 15 * 60 * 1000;
                NetworkStream stream = _client.GetStream();
                UTF8Encoding utf8 = new UTF8Encoding(false);
                _reader = new StreamReader(stream, utf8);
                _writer = new StreamWriter(stream, utf8);
                _writer.NewLine = "\n";
                _writer.AutoFlush = true;
                _log("Connected to brain at " + host + ":" + port + ".");
            }
            catch (Exception ex)
            {
                _log("Could not connect to brain at " + host + ":" + port + " (" + ex.Message + "), using the built-in AI.");
                _client = null;
            }
        }

        private void Disconnect(string reason)
        {
            if (_client == null) return;
            _log("Brain disconnected (" + reason + "), using the built-in AI from now on.");
            try { _client.Close(); } catch (Exception) { }
            _client = null;
        }

        private bool Send(Dictionary<string, object> message)
        {
            if (_client == null) return false;
            try
            {
                _writer.WriteLine(Json.Serialize(message));
                return true;
            }
            catch (Exception ex)
            {
                Disconnect(ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Called after the "win" event: one last round trip so the brain can say something at the end of the game
        /// (its reply's "say"; the choice is ignored). Decision ids then restart at 1 for the next game of the match.
        /// </summary>
        public void GameOver(int result, int reason)
        {
            Decide("game_over", new Dictionary<string, object> { { "result", result }, { "reason", reason } });
            _nextId = 0;
        }

        /// <summary>
        /// Reads an EDOPro STOC_CHAT_2 payload (u8 type, u8 is_team, u16 name[20], u16 message up to 256, UTF-16LE)
        /// into a "chat" event. "player" follows STOC_CHAT: our own position for our team's messages, the other
        /// position for the opponent's, 7 for spectators and 8 for the system.
        /// </summary>
        public static Dictionary<string, object> Chat2Event(BinaryReader packet, int position)
        {
            int type = packet.ReadByte();
            bool isTeam = packet.ReadByte() != 0;
            string name = Utf16(packet.ReadBytes(40));
            string message = Utf16(packet.ReadBytes((int)Math.Min(512, packet.BaseStream.Length - packet.BaseStream.Position)));
            string source = type == 0 ? "duelist" : type == 1 ? "spectator" : "system";
            int player = type == 0 ? (isTeam ? position : 1 - position) : type == 1 ? 7 : 8;
            return new Dictionary<string, object>
            {
                { "player", player }, { "position", position }, { "message", message }, { "name", name }, { "source", source },
            };
        }

        private static string Utf16(byte[] bytes)
        {
            string text = Encoding.Unicode.GetString(bytes, 0, bytes.Length - bytes.Length % 2);
            int end = text.IndexOf('\0');
            return end >= 0 ? text.Substring(0, end) : text;
        }

        public void Event(string name, Dictionary<string, object> data = null)
        {
            if (_client == null) return;
            Dictionary<string, object> message = data ?? new Dictionary<string, object>();
            message["t"] = "event";
            message["e"] = name;
            Send(message);
        }

        private int[] Decide(string kind, Dictionary<string, object> payload)
        {
            if (_client == null) return null;
            int id = ++_nextId;
            payload["t"] = "decide";
            payload["id"] = id;
            payload["kind"] = kind;
            try
            {
                payload["state"] = State();
            }
            catch (Exception ex)
            {
                _log("Brain: could not serialize the duel state (" + ex.Message + "), using the built-in AI for this decision.");
                return null;
            }
            if (!Send(payload)) return null;
            try
            {
                while (true)
                {
                    string line = _reader.ReadLine();
                    if (line == null)
                    {
                        Disconnect("connection closed");
                        return null;
                    }
                    BrainReply reply = Parse(line);
                    if (reply == null || reply.Id != id)
                        continue;
                    if (!string.IsNullOrEmpty(reply.Say))
                        SayChunks(reply.Say);
                    return reply.Choice;
                }
            }
            catch (Exception ex)
            {
                Disconnect(ex.Message);
                return null;
            }
        }

        private void SayChunks(string text)
        {
            foreach (string line in text.Split('\n'))
            {
                string rest = line.Trim();
                while (rest.Length > 0)
                {
                    int take = Math.Min(rest.Length, 240);
                    if (take < rest.Length)
                    {
                        int space = rest.LastIndexOf(' ', take);
                        if (space > 120) take = space;
                    }
                    _chat(rest.Substring(0, take), true);
                    rest = rest.Substring(take).Trim();
                }
            }
        }

        private static BrainReply Parse(string line)
        {
            try
            {
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(BrainReply));
                using (MemoryStream ms = new MemoryStream(Encoding.UTF8.GetBytes(line)))
                    return serializer.ReadObject(ms) as BrainReply;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ---- State serialization ----

        public static Dictionary<string, object> Card(ClientCard c)
        {
            if (c == null) return null;
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["id"] = c.Id;
            d["c"] = c.Controller;
            d["l"] = (int)c.Location;
            d["s"] = c.Sequence;
            d["p"] = c.Position;
            if (c.Id != 0)
            {
                d["atk"] = c.Attack;
                d["def"] = c.Defense;
                d["lv"] = c.Level;
                d["rk"] = c.Rank;
                d["lk"] = c.LinkCount;
                if (c.Overlays.Count > 0) d["ov"] = c.Overlays.ToList();
                if (c.IsDisabled()) d["dis"] = true;
                if (c.Attacked) d["att"] = true;
                if (c.EquipTarget != null) d["eq"] = Card(c.EquipTarget, false);
            }
            return d;
        }

        private static Dictionary<string, object> Card(ClientCard c, bool full)
        {
            if (full) return Card(c);
            if (c == null) return null;
            return new Dictionary<string, object> { { "id", c.Id }, { "c", c.Controller }, { "l", (int)c.Location }, { "s", c.Sequence } };
        }

        private static List<object> Cards(IEnumerable<ClientCard> cards)
        {
            return cards.Select(c => (object)Card(c)).ToList();
        }

        private static List<object> IndexedCards(IList<ClientCard> cards, CardLocation location)
        {
            // Hand/GY/banished sequences can be stale, so report the list position instead.
            List<object> result = new List<object>();
            for (int i = 0; i < cards.Count; ++i)
            {
                Dictionary<string, object> d = Card(cards[i]);
                if (d != null)
                {
                    d["l"] = (int)location;
                    d["s"] = i;
                }
                result.Add(d);
            }
            return result;
        }

        private Dictionary<string, object> Field(int player)
        {
            ClientField f = _duel.Fields[player];
            if (f.Hand == null) // before the duel starts (e.g. choosing who goes first)
            {
                List<object> none = new List<object>();
                return new Dictionary<string, object>
                {
                    { "lp", f.LifePoints }, { "hand", none }, { "mz", none }, { "sz", none }, { "gy", none },
                    { "ban", none }, { "deck", 0 }, { "extra", none },
                };
            }
            return new Dictionary<string, object>
            {
                { "lp", f.LifePoints },
                { "hand", IndexedCards(f.Hand, CardLocation.Hand) },
                { "mz", Cards(f.MonsterZone) },
                { "sz", Cards(f.SpellZone) },
                { "gy", IndexedCards(f.Graveyard, CardLocation.Grave) },
                { "ban", IndexedCards(f.Banished, CardLocation.Removed) },
                { "deck", f.Deck.Count },
                { "extra", IndexedCards(f.ExtraDeck, CardLocation.Extra) },
            };
        }

        public Dictionary<string, object> State()
        {
            Dictionary<string, object> s = new Dictionary<string, object>();
            s["turn"] = _duel.Turn;
            s["turn_player"] = _duel.Player;
            s["phase"] = (int)_duel.Phase;
            s["me"] = Field(0);
            s["opp"] = Field(1);
            List<object> chain = new List<object>();
            foreach (ChainInfo info in _duel.CurrentChainInfo)
            {
                chain.Add(new Dictionary<string, object>
                {
                    { "card", Card(info.RelatedCard) },
                    { "id", info.ActivateId },
                    { "player", info.ActivatePlayer },
                    { "desc", info.ActivateDescription },
                });
            }
            s["chain"] = chain;
            s["chain_targets"] = _duel.ChainTargets.Select(c => (object)Card(c, false)).ToList();
            s["last_summoned"] = _duel.LastSummonedCards.Select(c => (object)Card(c, false)).ToList();
            s["summon_player"] = _duel.LastSummonPlayer;
            s["battling"] = new List<object> { Card(_duel.Fields[0].BattlingMonster, false), Card(_duel.Fields[1].BattlingMonster, false) };
            return s;
        }

        private static Dictionary<string, object> Opt(string action, ClientCard card, long? desc = null)
        {
            Dictionary<string, object> d = new Dictionary<string, object> { { "a", action } };
            if (card != null) d["card"] = Card(card);
            if (desc.HasValue) d["desc"] = desc.Value;
            return d;
        }

        private static int Single(int[] choice, int count)
        {
            if (choice == null || choice.Length != 1) return Fallback;
            int k = choice[0];
            return k >= 0 && k < count ? k : Fallback;
        }

        // ---- Decisions ----

        public MainPhaseAction SelectIdle(MainPhase main)
        {
            try
            {
                return SelectIdleImpl(main);
            }
            catch (Exception ex)
            {
                _log("Brain: SelectIdle failed (" + ex.Message + "), using the built-in AI for this decision.");
                return null;
            }
        }

        private MainPhaseAction SelectIdleImpl(MainPhase main)
        {
            List<object> options = new List<object>();
            List<MainPhaseAction> actions = new List<MainPhaseAction>();
            for (int i = 0; i < main.ActivableCards.Count; ++i)
            {
                ClientCard card = main.ActivableCards[i];
                long desc = main.ActivableDescs[i];
                options.Add(Opt("activate", card, desc));
                actions.Add(new MainPhaseAction(MainPhaseAction.MainAction.Activate, card.ActionActivateIndex[desc]));
            }
            foreach (ClientCard card in main.SummonableCards)
            {
                options.Add(Opt("summon", card));
                actions.Add(new MainPhaseAction(MainPhaseAction.MainAction.Summon, card.ActionIndex));
            }
            foreach (ClientCard card in main.SpecialSummonableCards)
            {
                options.Add(Opt("spsummon", card));
                actions.Add(new MainPhaseAction(MainPhaseAction.MainAction.SpSummon, card.ActionIndex));
            }
            foreach (ClientCard card in main.MonsterSetableCards)
            {
                options.Add(Opt("set_monster", card));
                actions.Add(new MainPhaseAction(MainPhaseAction.MainAction.SetMonster, card.ActionIndex));
            }
            foreach (ClientCard card in main.SpellSetableCards)
            {
                options.Add(Opt("set_spell", card));
                actions.Add(new MainPhaseAction(MainPhaseAction.MainAction.SetSpell, card.ActionIndex));
            }
            foreach (ClientCard card in main.ReposableCards)
            {
                options.Add(Opt("reposition", card));
                actions.Add(new MainPhaseAction(MainPhaseAction.MainAction.Repos, card.ActionIndex));
            }
            if (main.CanBattlePhase)
            {
                options.Add(Opt("to_battle", null));
                actions.Add(new MainPhaseAction(MainPhaseAction.MainAction.ToBattlePhase));
            }
            if (main.CanEndPhase)
            {
                options.Add(Opt("to_end", null));
                actions.Add(new MainPhaseAction(MainPhaseAction.MainAction.ToEndPhase));
            }
            int k = Single(Decide("idle", new Dictionary<string, object> { { "options", options } }), actions.Count);
            return k == Fallback ? null : actions[k];
        }

        public BattlePhaseAction SelectBattle(BattlePhase battle)
        {
            try
            {
                return SelectBattleImpl(battle);
            }
            catch (Exception ex)
            {
                _log("Brain: SelectBattle failed (" + ex.Message + "), using the built-in AI for this decision.");
                return null;
            }
        }

        private BattlePhaseAction SelectBattleImpl(BattlePhase battle)
        {
            List<object> options = new List<object>();
            List<BattlePhaseAction> actions = new List<BattlePhaseAction>();
            for (int i = 0; i < battle.ActivableCards.Count; ++i)
            {
                ClientCard card = battle.ActivableCards[i];
                options.Add(Opt("activate", card, battle.ActivableDescs[i]));
                actions.Add(new BattlePhaseAction(BattlePhaseAction.BattleAction.Activate, card.ActionIndex));
            }
            foreach (ClientCard card in battle.AttackableCards)
            {
                Dictionary<string, object> opt = Opt("attack", card);
                opt["direct"] = card.CanDirectAttack;
                options.Add(opt);
                actions.Add(new BattlePhaseAction(BattlePhaseAction.BattleAction.Attack, card.ActionIndex));
            }
            if (battle.CanMainPhaseTwo)
            {
                options.Add(Opt("to_main2", null));
                actions.Add(new BattlePhaseAction(BattlePhaseAction.BattleAction.ToMainPhaseTwo));
            }
            if (battle.CanEndPhase)
            {
                options.Add(Opt("to_end", null));
                actions.Add(new BattlePhaseAction(BattlePhaseAction.BattleAction.ToEndPhase));
            }
            int k = Single(Decide("battle", new Dictionary<string, object> { { "options", options } }), actions.Count);
            return k == Fallback ? null : actions[k];
        }

        /// <returns>The chosen cards, an empty list to cancel, or null to fall back.</returns>
        public IList<ClientCard> SelectCards(string kind, IList<ClientCard> cards, int min, int max, long hint, bool cancelable)
        {
            int[] choice = Decide(kind, new Dictionary<string, object>
            {
                { "options", Cards(cards) }, { "min", min }, { "max", max }, { "hint", hint }, { "cancelable", cancelable },
            });
            if (choice == null) return null;
            if (choice.Length == 0 && (cancelable || min == 0)) return new List<ClientCard>();
            if (choice.Length < min || choice.Length > max || choice.Distinct().Count() != choice.Length) return null;
            if (choice.Any(k => k < 0 || k >= cards.Count)) return null;
            return choice.Select(k => cards[k]).ToList();
        }

        /// <returns>Index into selectable, Pass to finish/cancel, or Fallback.</returns>
        public int SelectUnselect(IList<ClientCard> selectable, IList<ClientCard> selected, int min, int max, bool finishable, bool cancelable, long hint)
        {
            int[] choice = Decide("select_unselect", new Dictionary<string, object>
            {
                { "options", Cards(selectable) }, { "selected", Cards(selected) }, { "min", min }, { "max", max },
                { "finishable", finishable }, { "cancelable", cancelable }, { "hint", hint },
            });
            if (choice == null) return Fallback;
            if (choice.Length == 0) return (finishable || cancelable) ? Pass : Fallback;
            return Single(choice, selectable.Count);
        }

        /// <returns>Index of the card to chain, Pass, or Fallback.</returns>
        public int SelectChain(IList<ClientCard> cards, IList<long> descs, bool forced)
        {
            List<object> options = new List<object>();
            for (int i = 0; i < cards.Count; ++i)
                options.Add(Opt("activate", cards[i], descs[i]));
            int[] choice = Decide("chain", new Dictionary<string, object> { { "options", options }, { "forced", forced } });
            if (choice == null) return Fallback;
            if (choice.Length == 0) return forced ? Fallback : Pass;
            return Single(choice, cards.Count);
        }

        private bool? YesNo(string kind, Dictionary<string, object> payload)
        {
            int[] choice = Decide(kind, payload);
            if (choice == null || choice.Length != 1 || choice[0] < 0 || choice[0] > 1) return null;
            return choice[0] == 1;
        }

        public bool? SelectEffectYn(ClientCard card, long desc)
        {
            return YesNo("effect_yn", new Dictionary<string, object> { { "card", Card(card) }, { "desc", desc } });
        }

        public bool? SelectYesNo(long desc)
        {
            return YesNo("yesno", new Dictionary<string, object> { { "desc", desc } });
        }

        public bool? SelectFirst()
        {
            return YesNo("go_first", new Dictionary<string, object>());
        }

        public int SelectOption(IList<long> options)
        {
            int k = Single(Decide("option", new Dictionary<string, object> { { "options", options.Cast<object>().ToList() } }), options.Count);
            return k == Fallback ? -1 : k;
        }

        /// <returns>The chosen position, or 0 to fall back.</returns>
        public CardPosition SelectPosition(int cardId, IList<CardPosition> positions)
        {
            int k = Single(Decide("position", new Dictionary<string, object>
            {
                { "card_id", cardId }, { "options", positions.Select(p => (object)(int)p).ToList() },
            }), positions.Count);
            return k == Fallback ? 0 : positions[k];
        }

        /// <returns>A zone bit mask, or 0 to fall back.</returns>
        public int SelectPlace(long cardId, int player, CardLocation location, int available)
        {
            int[] choice = Decide("place", new Dictionary<string, object>
            {
                { "card_id", cardId }, { "player", player }, { "location", (int)location }, { "available", available },
            });
            if (choice == null || choice.Length != 1) return 0;
            return choice[0] & available;
        }

        public IList<ClientCard> SelectSum(IList<ClientCard> cards, int sum, int min, int max, long hint, bool exact)
        {
            List<object> options = new List<object>();
            foreach (ClientCard card in cards)
            {
                Dictionary<string, object> d = Card(card);
                d["v1"] = card.OpParam1;
                d["v2"] = card.OpParam2;
                options.Add(d);
            }
            int[] choice = Decide("select_sum", new Dictionary<string, object>
            {
                { "options", options }, { "sum", sum }, { "min", min }, { "max", max }, { "hint", hint }, { "exact", exact },
            });
            if (choice == null || choice.Distinct().Count() != choice.Length || choice.Any(k => k < 0 || k >= cards.Count)) return null;
            if (choice.Length < min || choice.Length > max) return null;
            IList<ClientCard> selected = choice.Select(k => cards[k]).ToList();
            int s1 = 0, s2 = 0;
            foreach (ClientCard card in selected)
            {
                s1 += card.OpParam1;
                s2 += (card.OpParam2 != 0) ? card.OpParam2 : card.OpParam1;
            }
            bool ok = exact ? (s1 == sum || s2 == sum) : (s1 >= sum || s2 >= sum);
            return ok ? selected : null;
        }

        /// <returns>The declared card id, or 0 to fall back.</returns>
        public int AnnounceCard(IList<int> avail)
        {
            int[] choice = Decide("announce_card", new Dictionary<string, object> { { "options", avail.Cast<object>().ToList() } });
            if (choice == null || choice.Length != 1 || !avail.Contains(choice[0])) return 0;
            return choice[0];
        }

        /// <returns>Index into numbers, or -1 to fall back.</returns>
        public int AnnounceNumber(IList<int> numbers)
        {
            int k = Single(Decide("announce_number", new Dictionary<string, object> { { "options", numbers.Cast<object>().ToList() } }), numbers.Count);
            return k == Fallback ? -1 : k;
        }

        public IList<int> AnnounceFlags(string kind, int count, IList<long> flags)
        {
            int[] choice = Decide(kind, new Dictionary<string, object> { { "options", flags.Cast<object>().ToList() }, { "count", count } });
            if (choice == null || choice.Length != count || choice.Distinct().Count() != count || choice.Any(k => k < 0 || k >= flags.Count)) return null;
            return choice;
        }
    }

    /// <summary>Minimal JSON writer for dictionaries, lists, strings, numbers, booleans and null.</summary>
    public static class Json
    {
        public static string Serialize(object value)
        {
            StringBuilder sb = new StringBuilder();
            Write(sb, value);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, object value)
        {
            if (value == null) { sb.Append("null"); return; }
            if (value is string) { WriteString(sb, (string)value); return; }
            if (value is bool) { sb.Append((bool)value ? "true" : "false"); return; }
            if (value is Enum) { sb.Append(Convert.ToInt64(value).ToString(CultureInfo.InvariantCulture)); return; }
            if (value is int || value is long || value is short || value is byte || value is uint || value is ulong || value is ushort)
            {
                sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            }
            if (value is IDictionary)
            {
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry entry in (IDictionary)value)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, entry.Key.ToString());
                    sb.Append(':');
                    Write(sb, entry.Value);
                }
                sb.Append('}');
                return;
            }
            if (value is IEnumerable)
            {
                sb.Append('[');
                bool first = true;
                foreach (object item in (IEnumerable)value)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Write(sb, item);
                }
                sb.Append(']');
                return;
            }
            WriteString(sb, value.ToString());
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char ch in s)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
