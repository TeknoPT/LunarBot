using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace LunarLabs.Bots
{
    public class CustomBot
    {
        public class Command
        {
            public string name;
            public string description;
            public bool hidden;
            public Func<Message, int, Task<int>> handler;

            public Command(string name, string description, bool hidden, Func<Message, int, Task<int>> handler)
            {
                this.name = name;
                this.description = description;
                this.hidden = hidden;
                this.handler = handler;
            }
        }

        private TelegramBotClient client;

        public CustomBot(string token)
        {
            this.client = new TelegramBotClient(token);
            RegisterCommand("groups", "Shows list of groups", ShowGroups, true);
        }

        private async Task<int> ShowGroups(Message msg, int state)
        {
            if (_groupList.Count == 0)
            {
                await Speak(msg.Chat.Id, $"No groups found");
                return 0;
            }

            foreach (var entry in _groupList)
            {
                await Speak(msg.Chat.Id, $"{entry.Value} => {entry.Key}");
            }

            return 0;
        }

        public void Start(Action idle)
        {
            var me = client.GetMeAsync().GetAwaiter().GetResult();
            Console.WriteLine($"Starting {me.FirstName}");

            Console.CancelKeyPress += (sender, e) =>
            {
                Console.WriteLine("Stopping bot");
                client.StopReceiving();
                System.Environment.Exit(0);
            };

            client.OnMessage += OnMessage;

            client.StartReceiving();

            while (true)
            {
                idle();
                Thread.Sleep(5000);
            }
        }

        private Dictionary<string, Command> _commands = new Dictionary<string, Command>();
        private Dictionary<ChatId, string> _groupList = new Dictionary<ChatId, string>();

        public void RegisterCommand(string name, string description, Func<Message, int, Task<int>> handler, bool hidden = false)
        {
            if (string.IsNullOrEmpty(name) || name.StartsWith("/"))
            {
                throw new ArgumentException("Invalid command name");
            }

            var cmd = new Command(name, description, hidden, handler);
            _commands["/" + name] = cmd;
        }

        private async void OnMessage(object sender, Telegram.Bot.Args.MessageEventArgs e)
        {
            var msg = e.Message;

            try
            {
                switch (msg.Chat.Type)
                {
                    case ChatType.Private: await OnPrivateMessage(msg); break;
                    default: await OnPublicMessage(msg); break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        private async Task OnPublicMessage(Message msg)
        {
            switch (msg.Type)
            {
                case MessageType.Text:
                    {
                        if (!_groupList.ContainsKey(msg.Chat.Id))
                        {
                            var groupName = msg.Chat.FirstName;
                            Console.WriteLine("Listening in group: " + groupName);
                            _groupList[msg.Chat.Id] = groupName;
                        }

                        break;
                    }
            }
        }

        private HashSet<long> _admins = new HashSet<long>();

        public bool IsAdmin(ChatId id)
        {
            return _admins.Contains(id.Identifier);
        }

        public void AddAdmin(ChatId id)
        {
            _admins.Add(id.Identifier);
        }

        public void RemoveAdmin(ChatId id)
        {
            _admins.Remove(id.Identifier);
        }

        protected virtual async Task<bool> OnDefaultChatAsync(Message msg)
        {
            return true;
        }

        protected virtual async Task<bool> OnPermissionFailedForCommand(Message msg)
        {
            return true;
        }

        private Dictionary<long, int> _state = new Dictionary<long, int>();
        private Dictionary<long, Command> _cmds = new Dictionary<long, Command>();

        private async Task OnPrivateMessage(Message msg)
        {
            switch (msg.Type)
            {
                case MessageType.Text:
                    {
                        var text = msg.Text;

                        Command cmd;

                        if (_cmds.ContainsKey(msg.Chat.Id))
                        {
                            cmd = _cmds[msg.Chat.Id];
                        }
                        else
                        if (_commands.ContainsKey(text))
                        {
                            cmd = _commands[text];
                        }
                        else
                        {
                            cmd = null;
                        }

                        if (cmd !=null && (!cmd.hidden || IsAdmin(msg.Chat.Id)))
                        {
                            var state = _state.ContainsKey(msg.Chat.Id) ? _state[msg.Chat.Id] : 0;
                            state = await cmd.handler(msg, state);
                            if (state != 0)
                            {
                                _state[msg.Chat.Id] = state;
                                _cmds[msg.Chat.Id] = cmd;
                            }
                            else
                            {
                                try
                                {
                                    _state.Remove(msg.Chat.Id);
                                    _cmds.Remove(msg.Chat.Id);
                                }
                                catch
                                {
                                    // ignore
                                }
                            }
                            return;
                        }
                        else
                        {
                            if (!await OnPermissionFailedForCommand(msg))
                            {
                                return;
                            }
                        }

                        if (await OnDefaultChatAsync(msg))
                        {
                            var sb = new StringBuilder();
                            foreach (var entry in _commands.Values)
                            {
                                if (!entry.hidden || IsAdmin(msg.Chat.Id))
                                {
                                    sb.AppendLine($"/{entry.name}\t\t{entry.description}");
                                }
                            }

                            if (sb.Length > 0)
                            {
                                await Speak(msg.Chat.Id, sb.ToString());
                            }
                        }

                        break;
                    }
            }
        }

        public async Task Speak(ChatId id, string text)
        {
            await Speak(id, new string[] { text });
        }

        public async Task Speak(ChatId id, IEnumerable<string> lines)
        {
            foreach (var line in lines)
            {
                if (string.IsNullOrEmpty(line))
                {
                    continue;
                }

                await client.SendChatActionAsync(id, ChatAction.Typing);

                await Task.Delay(1000); // simulate longer running task

                await client.SendTextMessageAsync(id, line);
            }
        }

    }
}
