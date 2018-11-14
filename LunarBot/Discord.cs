using Discord;
using Discord.WebSocket;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

//TO CREATE A BOT => https://discordapp.com/developers/applications/
//TO MAKE BOT JOIN SERVER => https://discordapp.com/oauth2/authorize?&client_id=YOUR_CLIENT_ID_HERE&scope=bot&permissions=0
namespace LunarLabs.Bots
{
    public class DiscordConnection : BotConnection
    {
        private ConcurrentQueue<BotMessage> _queue;
        private DiscordSocketClient _client;

        public DiscordConnection(string token)
        {
            _client = new DiscordSocketClient();

            //client.Log += Log;

            _client.LoginAsync(TokenType.Bot, token).Wait();
        }

        private Dictionary<ulong, ISocketMessageChannel> _channels = new Dictionary<ulong, ISocketMessageChannel>();

        private IMessageChannel GetChannel(ulong id)
        {
            var channel = _client.GetChannel(id) as IMessageChannel;

            if (channel == null)
            {
                var user = _client.GetUser(id);

                if (user == null)
                {
                    return null;
                }

                channel = (IMessageChannel)(user.GetOrCreateDMChannelAsync().GetAwaiter().GetResult());
            }

            return channel;
        }

        public async Task Send(object target, string text)
        {
            var id = (ulong)target;
            var socket = GetChannel(id);

            await socket.SendMessageAsync(text);
        }

        public void Start(ConcurrentQueue<BotMessage> queue)
        {
            this._queue = queue;
            _client.MessageReceived += MessageReceived;
            _client.StartAsync().Wait();

            Console.WriteLine($"Connected to Discord");
        }

        public void Stop()
        {
            _client.StopAsync().Wait();
        }

        private async Task MessageReceived(SocketMessage src)
        {
            try
            {
                if (src.Author.Id == _client.CurrentUser.Id)
                {
                    return;
                }

                MessageKind kind;

                var msg = new BotMessage();
                msg.Visibility = (src.Channel is IPrivateChannel) ? MessageVisibility.Private : MessageVisibility.Public;


                var sourceID =  msg.Visibility == MessageVisibility.Private ? src.Author.Id : src.Channel.Id;
                var sourceHandle = msg.Visibility == MessageVisibility.Private ? src.Author.Username : src.Channel.Name;
                var sourceName = msg.Visibility == MessageVisibility.Private ? src.Author.Username : src.Channel.Name;

                msg.Kind = MessageKind.Text;
                msg.Text = src.Content;
                msg.Sender = new MessageSender() { ID = (long)sourceID, Handle = sourceHandle, Name = sourceName, Platform = BotPlatform.Discord};
                msg.File = null;

                msg.channelID = src.Channel.Id;
                msg.msgID = (ulong)src.Id;
                msg.platform = BotPlatform.Discord;

                _channels[sourceID] = src.Channel;

                _queue.Enqueue(msg);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        public string GetCommandPrefix()
        {
            return "!";
        }

        public void SendFile(object target, byte[] bytes, string fileName)
        {
            var id = (ulong)target;
            var socket = GetChannel(id);

            using (var stream = new MemoryStream(bytes))
            {
                socket.SendFileAsync(stream, fileName).Wait();
            }            
        }

        public MessageSender Expand(long ID)
        {
            var targetId = (ulong)ID;

            var channel = _client.GetChannel(targetId) as IMessageChannel;

            if (channel != null)
            {
                return new MessageSender() { ID = ID, Handle = channel.Name, Name = channel.Name, Platform = BotPlatform.Discord };
            }

            var user = _client.GetUser(targetId);

            if (user != null)
            {
                return new MessageSender() { ID = ID, Handle = user.Username, Name = user.Username, Platform = BotPlatform.Discord };
            }

            throw new Exception("Can't find Discord ID " + targetId);
        }

        public void Delete(BotMessage msg)
        {
            if (msg != null)
            {
                var id = (ulong)msg.channelID;
                var socket = GetChannel(id);

                socket.DeleteMessagesAsync(new ulong[] { (ulong)msg.msgID });
            }
        }

    }
}
