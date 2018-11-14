using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Types;
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

        public void Stop()
        {
            _client.StopReceiving();
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
                    case MessageType.ChatMembersAdded: kind = MessageKind.Join; break;

                    default: kind = MessageKind.Other; break;

                }

                var msg = new BotMessage();
                msg.Visibility = src.Chat.Type == ChatType.Private ? MessageVisibility.Private : MessageVisibility.Public;
                msg.Kind = kind;
                msg.Sender = FromChat(src.Chat);

                string text = "";
                MessageFile file = null;

                switch (src.Type)
                {
                    case MessageType.ChatMembersAdded:
                        {
                            if (src.NewChatMembers!=null)
                            {
                                foreach (var member in src.NewChatMembers)
                                {
                                    if (text.Length > 0)
                                    {
                                        text += ", ";
                                    }

                                    text += Combine(member.FirstName, member.LastName);
                                }
                            }

                            text = "Joined: " + text;
                            return;
                        }

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

                    case MessageType.Text:
                        {
                            text = src.Text;
                            break;
                        }

                    default: file = null; break;
                }

                msg.File = file;
                msg.Text = text;
                msg.channelID = src.Chat.Id;
                msg.msgID = src.MessageId;
                msg.platform = BotPlatform.Telegram;

                _queue.Enqueue(msg);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        private MessageSender FromChat(Chat chat)
        {
            return new MessageSender() { ID = chat.Id, Handle = chat.Username, Name = Combine(chat.FirstName, chat.LastName), Platform = BotPlatform.Telegram };
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

        public async Task Send(object target, string text)
        {
            var id = (long)target;
            await _client.SendChatActionAsync(id, ChatAction.Typing);

            await Task.Delay(1500); // simulate longer running task

            await _client.SendTextMessageAsync(id, text, ParseMode.Markdown);
        }

        public string GetCommandPrefix()
        {
            return "/";
        }

        public void SendFile(object target, byte[] bytes, string fileName)
        {
            var id = (long)target;
            using (var stream = new MemoryStream(bytes))
            {
                var document = new Telegram.Bot.Types.InputFiles.InputOnlineFile(stream, fileName);
                _client.SendDocumentAsync(id, document).Wait();
            }
        }

        public MessageSender Expand(long userID)
        {
            var chat = _client.GetChatAsync(userID).GetAwaiter().GetResult();
            return FromChat(chat);
        }

        public void Delete(BotMessage msg)
        {
            if (msg != null)
            {
                _client.DeleteMessageAsync((long)msg.channelID, (int)msg.msgID);
            }
        }
    }
}
