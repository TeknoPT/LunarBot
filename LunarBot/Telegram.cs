using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Types.Enums;

namespace LunarLabs.Bots
{
    public class TelegramConnection: BotConnection
    {
        private TelegramBotClient _client;
        private ConcurrentQueue<BotMessage> _queue;

        public TelegramConnection(string token)
        {
            this._client = new TelegramBotClient(token);
        }

        public void Start(ConcurrentQueue<BotMessage> queue)
        {
            this._queue = queue;

            var me = _client.GetMeAsync().GetAwaiter().GetResult();
            Console.WriteLine($"Connected to {me.FirstName}@Telegram");

            _client.OnMessage += OnMessage;

            _client.StartReceiving();
        }

        private byte[] FetchFile(string fileID)
        {
            using (var outputStream = new MemoryStream())
            {
                var file = _client.GetInfoAndDownloadFileAsync(fileID, outputStream).GetAwaiter().GetResult();
                return outputStream.ToArray();
            }
        }

        private void OnMessage(object sender, MessageEventArgs e)
        {
            var src = e.Message;

            try
            {
                MessageKind kind;
                switch (src.Type)
                {
                    case MessageType.Text: kind = MessageKind.Text; break;
                    case MessageType.Document: kind = MessageKind.File; break;
                    case MessageType.Photo: kind = MessageKind.File; break;

                    default: kind = MessageKind.Other; break;

                }

                var msg = new BotMessage();
                msg.Visibility = src.Chat.Type == ChatType.Private ? MessageVisibility.Private : MessageVisibility.Public;
                msg.Kind = kind;
                msg.Text = kind == MessageKind.Text ? src.Text: "";
                msg.Sender = new MessageSender() { ID = src.Chat.Id, Handle = src.Chat.Username, Name = Combine(src.Chat.FirstName, src.Chat.LastName), Platform = BotPlatform.Telegram };

                MessageFile file;
                switch (src.Type)
                {
                    case MessageType.Photo:
                        {
                            file = new MessageFile();
                            var i = src.Photo.Length - 1; //to get the highest resolution version of the photo
                            var fileId = src.Photo[i].FileId;

                            var info = _client.GetFileAsync(fileId).GetAwaiter().GetResult();
                            
                            file.Size = src.Photo[i].FileSize;
                            file.Name = Path.GetFileName(info.FilePath);
                            file.Fetch = () => FetchFile(fileId);
                            break;
                        }

                    case MessageType.Document:
                        {
                            file = new MessageFile();
                            var fileId = src.Document.FileId;
                            file.Size = src.Document.FileSize;
                            file.Name = src.Document.FileName;
                            file.Fetch = () => FetchFile(fileId);
                            break;
                        }

                    default: file = null; break;
                }

                msg.File = file;

                _queue.Enqueue(msg);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        private string Combine(string firstName, string lastName)
        {
            if (string.IsNullOrEmpty(firstName))
            {
                return lastName;
            }

            if (string.IsNullOrEmpty(lastName))
            {
                return firstName;
            }

            return $"{firstName} {lastName}";
        }

        public async Task Send(long target, string text)
        {
            await _client.SendChatActionAsync(target, ChatAction.Typing);

            await Task.Delay(1500); // simulate longer running task

            await _client.SendTextMessageAsync(target, text);
        }

        public string GetCommandPrefix()
        {
            return "/";
        }
    }
}
