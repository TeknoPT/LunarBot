using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace LunarLabs.Bots
{
    public class Bot
    {
        public class Command
        {
            public string name;
            public string description;
            public bool hidden;
            public Action<Bot, Message> handler;

            public Command(string name, string description, bool hidden, Action<Bot, Message> handler)
            {
                this.name = name;
                this.description = description;
                this.hidden = hidden;
                this.handler = handler;
            }
        }

        private TelegramBotClient client;

        public Bot(string token)
        {
            this.client = new TelegramBotClient(token);
            RegisterCommand("groups", "Shows list of groups", (bot, msg) => ShowGroups(msg), true);
        }

        private async void ShowGroups(Message msg)
        {
            if (_groupList.Count == 0)
            {
                await Speak(msg.Chat.Id, $"No groups found");
                return;
            }

            foreach (var entry in _groupList)
            {
                await Speak(msg.Chat.Id, $"{entry.Value} => {entry.Key}");
            }
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

        public void RegisterCommand(string name, string description, Action<Bot, Message> handler, bool hidden = false)
        {
            var cmd = new Command(name, description, hidden, handler);
            _commands["/" + name] = cmd;
        }

        private async void OnMessage(object sender, Telegram.Bot.Args.MessageEventArgs e)
        {
            var msg = e.Message;

            switch (msg.Chat.Type)
            {
                case ChatType.Private: await OnPrivateMessage(msg); break;
                default: await OnPublicMessage(msg); break;
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

        private async Task OnPrivateMessage(Message msg)
        {
            switch (msg.Type)
            {
                case MessageType.Text:
                    {
                        var text = msg.Text;

                        if (_commands.ContainsKey(text))
                        {
                            var cmd = _commands[text];
                            cmd.handler(this, msg);
                            return;
                        }

                        await Speak(msg.Chat.Id, "Hola amigo!");

                        var sb = new StringBuilder();
                        foreach (var cmd in _commands.Values)
                        {
                            sb.AppendLine($"/{cmd.name}\t\t{cmd.description}");
                        }

                        await Speak(msg.Chat.Id, sb.ToString());
                        break;
                    }
            }
        }

        private async Task Speak(ChatId id, string text)
        {
            await Speak(id, new string[] { text });
        }

        private async Task Speak(ChatId id, IEnumerable<string> lines)
        {
            foreach (var line in lines)
            {
                await client.SendChatActionAsync(id, ChatAction.Typing);

                await Task.Delay(1000); // simulate longer running task

                await client.SendTextMessageAsync(id, line);
            }
        }

    }
}
