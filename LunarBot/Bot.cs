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

    public struct MessageSender
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
        Task Send(long target, string text);
    }

    public class BotCommand
    {
        public readonly string name;
        public readonly string description;
        internal readonly Func<BotMessage, int, int> handler;
        internal readonly Func<BotMessage, bool> filter;

        public BotCommand(string name, string description, Func<BotMessage, int, int> handler, Func<BotMessage, bool> filter = null)
        {
            this.name = name;
            this.description = description;
            this.handler = handler;
            this.filter = filter;
        }
    }

    public class Bot
    {
        private Dictionary<BotPlatform, BotConnection> _connections = new Dictionary<BotPlatform, BotConnection>();
        private ConcurrentQueue<BotMessage> _queue = new ConcurrentQueue<BotMessage>();
        private Dictionary<string, BotStorage> _storage = new Dictionary<string, BotStorage>();

        public BotStorage Handles => FindStorage("handles");
        public BotStorage Admins => FindStorage("admins");

        private bool _running;
        private string _path;
        public string Path => _path;

        public Bot(string path, Dictionary<BotPlatform, string> apiKeys)
        {
            path = path.Replace(@"\", "/");

            if (!path.EndsWith("/"))
            {
                path = path + "/";
            }

            if (!Directory.Exists(path))
            {
                Console.WriteLine($"Creating directory {path}...");
                Directory.CreateDirectory(path);
            }

            this._path = path;

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
            RegisterCommand("groups", "Shows list of groups", ShowGroups, (msg) => msg.Visibility == MessageVisibility.Private && IsAdmin(msg.Sender));
            RegisterCommand("addadmin", "Promotes someone to admin", PromoteAdmin, (msg) => msg.Visibility == MessageVisibility.Private && IsAdmin(msg.Sender));
            //RegisterCommand("removeadmin", "Demotes someone from admin", DemoteAdmin, (msg) => msg.Visibility ==  MessageVisibility.Private && IsAdmin(msg.Sender));
        }

        public bool IsCommand(BotMessage msg, string cmd)
        {
            return msg.Text == _connections[msg.Sender.Platform].GetCommandPrefix() + cmd;
        }

        private int ShowGroups(BotMessage msg, int state)
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
            Speak(msg.Sender.Platform, msg.Sender.ID, $"Your {msg.Sender.Platform} ID is {msg.Sender.ID}");
            return 0;

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

        public BotStorage FindStorage(string name)
        {
            name = name.ToLower();

            if (_storage.ContainsKey(name)) 
            {
                return _storage[name];
            }

            var result = new BotStorage(_path, name);
            result.Load();
            _storage[name] = result;
            return result;
        }

        public void Start(Action idle = null)
        {
            foreach (var entry in _connections)
            {
                Console.WriteLine($"Connecting to {entry.Key}...");
                new Thread(delegate ()
                {
                    entry.Value.Start(_queue);
                }).Start();
            }

            var lastIdle = DateTime.UtcNow;

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

                var diff = DateTime.UtcNow - lastIdle;
                if (diff.TotalSeconds >= 5)
                {
                    foreach (var entry in _storage)
                    {
                        var storage = entry.Value;
                        if (storage.Modified)
                        {
                            Console.WriteLine($"Saving storage for {storage.Name}...");
                            storage.Save();
                        }
                    }

                    if (idle != null)
                    {
                        try
                        {
                            idle();
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine(e);
                        }
                    }

                    lastIdle = DateTime.UtcNow;
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

        public void RegisterCommand(string name, string description, Func<BotMessage, int, int> handler, Func<BotMessage, bool> filter = null)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("Invalid command name");
            }

            var cmd = new BotCommand(name, description, handler, filter);
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
                var key = text.Substring(prefix.Length);

                if (_commands.ContainsKey(key))
                {
                    cmd = _commands[key];
                }
            }

            if (cmd !=null)
            {
                if ( cmd.filter == null || cmd.filter(msg))
                {
                    var state = _state.ContainsKey(msg.Sender.ID) ? _state[msg.Sender.ID] : 0;
                    state = cmd.handler(msg, state);
                    if (state != 0)
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

    }
}
