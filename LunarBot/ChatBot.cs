using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LunarLabs.Bots
{
    public enum BotPlatform
    {
        Telegram,
        Discord
    }

    public enum MessageVisibility
    {
        Private,
        Public,
    }

    public enum MessageKind
    {
        Text,
        File,
        Sticker,
        Other
    }

    public class MessageSender
    {
        public BotPlatform Platform;
        public long ID;
        public string Handle;
        public string Name;

        public string Tag => $"{Platform.ToString().ToLower()}_{ID}";
    }

    public class MessageFile
    {
        public string Name;
        public int Size;
        public Func<byte[]> Fetch;
    }

    public class BotMessage
    {
        public MessageVisibility Visibility;
        public MessageKind Kind;
        public string Text;        
        public MessageSender Sender;
        public MessageFile File;
    }

    public interface BotConnection
    {
        string GetCommandPrefix();
        void Start(ConcurrentQueue<BotMessage> queue);
        void Stop();
        Task Send(long target, string text);
        void SendFile(long target, byte[] bytes, string fileName);
        MessageSender Expand(long ID);
    }

    public class BotCommand
    {
        public readonly string name;
        public readonly string description;
        public readonly bool AllowShortcut;
        internal readonly Func<BotMessage, int, int> handler;
        internal readonly Func<BotMessage, bool> filter;

        public BotCommand(string name, string description, Func<BotMessage, int, int> handler, Func<BotMessage, bool> filter = null, bool allowShortcut = false)
        {
            this.name = name;
            this.description = description;
            this.handler = handler;
            this.filter = filter;
            this.AllowShortcut = allowShortcut;
        }
    }

    public class ChatBot
    {
        private Dictionary<BotPlatform, BotConnection> _connections = new Dictionary<BotPlatform, BotConnection>();
        private ConcurrentQueue<BotMessage> _queue = new ConcurrentQueue<BotMessage>();

        public Collection Handles => Storage.FindCollection("handles");
        public Collection Admins => Storage.FindCollection("admins");

        private Collection Times => Storage.FindCollection("times");

        public Storage Storage { get; private set; }

        private bool _running;
        private string _path;
        public string Path => _path;

        public ChatBot(Storage storage, Dictionary<BotPlatform, string> apiKeys)
        {
            this.Storage = storage;

            foreach (var entry in apiKeys)
            {
                BotConnection source;

                switch (entry.Key)
                {
                    case BotPlatform.Telegram: source = new TelegramConnection(entry.Value); break;
                    case BotPlatform.Discord: source = new DiscordConnection(entry.Value); break;
                    default: source = null; break;
                }

                if (source != null)
                {
                    _connections[entry.Key] = source;
                }
            }

            RegisterCommand("me", "Shows your ID", ShowMe, (msg) => IsCommand(msg, "me"));
            RegisterCommand("whois", "Lookups someone by ID", ShowWhoIs, (msg) => IsCommand(msg, "whois"), true);
            RegisterCommand("where", "Shows list of public locations", WhereCommand, (msg) => msg.Visibility == MessageVisibility.Private && IsAdmin(msg.Sender));
            RegisterCommand("addadmin", "Promotes someone to admin", PromoteAdmin, (msg) => msg.Visibility == MessageVisibility.Private && IsAdmin(msg.Sender), true);
            //RegisterCommand("removeadmin", "Demotes someone from admin", DemoteAdmin, (msg) => msg.Visibility ==  MessageVisibility.Private && IsAdmin(msg.Sender), true);
        }

        public bool IsCommand(BotMessage msg, string cmd)
        {
            return msg.Text.StartsWith(_connections[msg.Sender.Platform].GetCommandPrefix() + cmd);
        }

        private int WhereCommand(BotMessage msg, int state)
        {
            if (_groupList.Count == 0)
            {
                Speak(msg.Sender.Platform, msg.Sender.ID, $"No groups found");
                return 0;
            }

            foreach (var entry in _groupList)
            {
                Speak(msg.Sender.Platform, msg.Sender.ID, $"{entry.Value} => {entry.Key}");
            }

            return 0;
        }

        private int ShowMe(BotMessage msg, int state)
        {
            Speak(msg.Sender, $"Your {msg.Sender.Platform} ID is {msg.Sender.ID}");
            return 0;
        }

        private int ShowWhoIs(BotMessage msg, int state)
        {
            switch (state)
            {
                case 1:
                    long ID;

                    if (long.TryParse(msg.Text, out ID))
                    {
                        try {
                            var user = Expand(msg.Sender.Platform, ID);
                            Speak(msg.Sender, $"ID {ID} belongs to user with handle @{user.Handle}");
                        }
                        catch
                        {
                            Speak(msg.Sender, $"Could not find user with ID {ID}");
                        }
                    }
                    else
                    {
                        Speak(msg.Sender, "That's not an valid ID!");
                    }
                    return 0;

                default:
                    Speak(msg.Sender, "What ID you want to look up?");
                    return 1;
            }
        }

        private int PromoteAdmin(BotMessage msg, int state)
        {
            switch (state)
            {
                case 1:
                    {
                        if (msg.Kind == MessageKind.Text)
                        {
                            long targetID;

                            if (long.TryParse(msg.Text, out targetID))
                            {
                                var target = new MessageSender() { Platform = msg.Sender.Platform, ID = targetID };

                                if (Admins.Contains(target))
                                {
                                    Speak(msg.Sender.Platform, msg.Sender.ID, $"This user already is an admin.");
                                    return 0;
                                }

                                AddAdmin(target);
                                Speak(msg.Sender, $"That user is now an admin!");
                            }
                            else
                            {
                                Speak(msg.Sender, $"You should give me an {msg.Sender.Platform} user ID please...");
                            }

                        }
                        else
                        {
                            Speak(msg.Sender, $"You should give me an {msg.Sender.Platform} user ID please...");
                        }

                        return 0;
                    }

                default:
                    {
                        Speak(msg.Sender, $"Who should be promoted to admin?");
                        return 1;
                    }
            }

            foreach (var entry in _groupList)
            {
                Speak(msg.Sender.Platform, msg.Sender.ID, $"{entry.Value} => {entry.Key}");
            }

            return 0;
        }

        private ConcurrentQueue<Action> _delayedQueue = new ConcurrentQueue<Action>();

        public void RunLater(Action action)
        {
            _delayedQueue.Enqueue(action);
        }

        public void Start()
        {
            foreach (var entry in _connections)
            {
                Console.WriteLine($"Connecting to {entry.Key}...");
                new Thread(delegate ()
                {
                    entry.Value.Start(_queue);
                }).Start();
            }

            var lastStorageWrite = DateTime.UtcNow;

            Console.WriteLine("Listening for messages...");
            _running = true;
            while (_running)
            {
                BotMessage msg;
                
                if (_queue.TryDequeue(out msg))
                {
                    try
                    {
                        msg = FilterMessage(msg);

                        if (msg != null)
                        {
                            ProcessMessage(msg);
                        }
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e);
                    }
                }

                Action delayedAction;
                if (_delayedQueue.TryDequeue(out delayedAction))
                {
                    try
                    {
                        delayedAction();
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e);
                    }
                }

                var diff = DateTime.UtcNow - lastStorageWrite;
                if (diff.TotalSeconds >= 5)
                {
                    this.Storage.Synchronize();
                    lastStorageWrite = DateTime.UtcNow;
                }
            }

            foreach (var entry in _connections)
            {
                Console.Write($"Shutting down connection to {entry.Key}...");
                try
                {
                    entry.Value.Stop();
                    Console.WriteLine("Done");
                }
                catch
                {
                    Console.WriteLine("Failed");
                }
            }

        }

        public void Stop()
        {
            if (_running)
            {
                Console.WriteLine("Stopping bot...");
                _running = false;
            }
        }

        private Dictionary<string, BotCommand> _commands = new Dictionary<string, BotCommand>();
        private Dictionary<long, string> _groupList = new Dictionary<long, string>();

        public void RegisterCommand(string name, string description, Func<BotMessage, int, int> handler, Func<BotMessage, bool> filter = null,  bool allowShortcut = false)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("Invalid command name");
            }

            var cmd = new BotCommand(name, description, handler, filter, allowShortcut);
            _commands[name] = cmd;
        }

        public bool IsAdmin(MessageSender sender)
        {
            return Admins.Contains(sender);
        }

        public void AddAdmin(BotPlatform platform, long ID)
        {
            AddAdmin(new MessageSender() { Platform = platform, Handle = "", ID = ID, Name = "" });
        }

        public void AddAdmin(MessageSender sender)
        {
            Admins.Set(sender, "true");
        }

        public void RemoveAdmin(MessageSender sender)
        {
            Admins.Remove(sender);
        }

        protected virtual BotMessage FilterMessage(BotMessage msg)
        {
            return msg;
        }

        protected virtual void OnPermissionFailedForCommand(BotMessage msg)
        {
        }

        private Dictionary<long, int> _state = new Dictionary<long, int>();
        private Dictionary<long, BotCommand> _cmds = new Dictionary<long, BotCommand>();

        private void ProcessMessage(BotMessage msg)
        {
            var text = msg.Text;

            string queue = null;

            if (msg.Visibility == MessageVisibility.Public && !_groupList.ContainsKey(msg.Sender.ID))
            {
                var content = $"{msg.Sender.Platform} => {msg.Sender.ID} / {msg.Sender.Handle}";
                Console.WriteLine("Listening in group: " + content);
                _groupList[msg.Sender.ID] = content;
            }

            BotCommand cmd = null;
            var prefix = _connections[msg.Sender.Platform].GetCommandPrefix();

            if (_cmds.ContainsKey(msg.Sender.ID))
            {
                cmd = _cmds[msg.Sender.ID];
            }
            else
            if (text.StartsWith(prefix))
            {
                var temp = text.Substring(prefix.Length).Split(new char[] { ' ' }, 2);

                var key = temp[0];

                if (_commands.ContainsKey(key))
                {
                    cmd = _commands[key];

                    if (temp.Length == 2 && cmd.AllowShortcut)
                    {
                        queue = temp[1];
                    }
                }
            }

            if (cmd !=null)
            {
                var state = _state.ContainsKey(msg.Sender.ID) ? _state[msg.Sender.ID] : 0;
                if (state !=0 || (cmd.filter == null || cmd.filter(msg)))
                {
                    if (queue != null && state == 0)
                    {
                        msg.Text = queue;
                        state = 1;
                    }

                    state = cmd.handler(msg, state);
                    if (state > 0)
                    {
                        _state[msg.Sender.ID] = state;
                        _cmds[msg.Sender.ID] = cmd;
                    }
                    else
                    {
                        try
                        {
                            _state.Remove(msg.Sender.ID);
                            _cmds.Remove(msg.Sender.ID);
                        }
                        catch
                        {
                            // ignore
                        }
                    }
                }
                else
                {
                    OnPermissionFailedForCommand(msg);
                }

                return;
            }

            OnMessage(msg);

            // update last seen
            var lastSeen = GetLastSeen(msg.Sender);
            var diff = (DateTime.UtcNow - lastSeen).TotalHours;
            if (diff > 1)
            {
                var timestamp = DateTime.UtcNow.Ticks;
                Times.Set(msg.Sender, timestamp.ToString());
            }
        }

        protected virtual void OnMessage(BotMessage msg)
        {
        }

        public void ListCommands(BotMessage msg, string prefix = null)
        {
            var platform = _connections[msg.Sender.Platform];

            var sb = new StringBuilder();
            foreach (var entry in _commands.Values)
            {
                var visible = entry.filter != null ? entry.filter(msg) : true;

                if (visible)
                {
                    sb.AppendLine($"{platform.GetCommandPrefix()}{entry.name}\t\t{entry.description}");
                }
            }

            if (sb.Length > 0)
            {
                var output = sb.ToString();
                if (!string.IsNullOrEmpty(prefix))
                {
                    output = prefix + output;
                }
                Speak(msg.Sender.Platform, msg.Sender.ID, output);
            }
        }

        public void Speak(MessageSender sender, string text)
        {
            Speak(sender.Platform, sender.ID, text);
        }

        public void Speak(BotPlatform platform, long target, string text)
        {
            Speak(platform, target, new string[] { text });
        }

        public void Speak(MessageSender sender, IEnumerable<string> lines)
        {
            Speak(sender.Platform, sender.ID, lines);
        }

        public void Speak(BotPlatform platform, long target, IEnumerable<string> lines)
        {
            var connection = _connections[platform];

            foreach (var line in lines)
            {
                if (string.IsNullOrEmpty(line))
                {
                    continue;
                }

                connection.Send(target, line).Wait();
            }
        }

        public void SendFile(BotPlatform platform, long target, byte[] content, string fileName)
        {
            var connection = _connections[platform];
            connection.SendFile(target, content, fileName);
        }

        private Dictionary<string, object> _memory = new Dictionary<string, object>();

        public void RememberValue(MessageSender sender, object obj)
        {
            _memory[sender.Tag] = obj;
        }

        public object GetLastValue(MessageSender sender)
        {
            var tag = sender.Tag;
            if (_memory.ContainsKey(tag))
            {
                return _memory[tag];
            }

            return null;
        }

        public MessageSender FromTag(string tag)
        {
            var temp = tag.Split('_');
            BotPlatform platform;
            if (Enum.TryParse<BotPlatform>(temp[0], true, out platform))
            {
                long id;
                
                if (long.TryParse(temp[1], out id))
                {
                    return new MessageSender() { Platform = platform, ID = id };
                }
            }

            throw new ArgumentException("Invalid tag");
        }

        public MessageSender Expand(BotPlatform platform, long ID)
        {
            var connection = _connections[platform];
            return connection.Expand(ID);
        }

        public DateTime GetLastSeen(MessageSender target)
        {
            var times = Storage.FindCollection("times", false);
            if (times != null && times.Contains(target))
            {
                var ticks = long.Parse(times.Get(target));
                return new DateTime(ticks);
            }
            else
            {
                return DateTime.MinValue;
            }
        }

    }
}
