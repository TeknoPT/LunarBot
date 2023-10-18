using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using OpenAI.GPT3;
using OpenAI.GPT3.Managers;
using OpenAI.GPT3.ObjectModels.RequestModels;
using OpenAI.GPT3.ObjectModels;
using LunarLabs.WebServer.HTTP;
using LunarLabs.WebServer.Templates;
using System.IO;

namespace LunarLabs.Bots
{

    public struct ChatOption
    {
        public readonly string id;
        public readonly string caption;

        public ChatOption(string id, string caption)
        {
            this.id = id;
            this.caption = caption;
        }
    }

    public struct ChatEntry
    {
        public readonly bool isAssistant;
        public readonly string text;

        public readonly ChatOption[] options;

        public ChatEntry(bool isAssistant, string text, ChatOption[] options)
        {
            this.isAssistant = isAssistant;
            this.text = text;
            this.options = options;
        }

        public ChatEntry(bool isAssistant, string text)
        {
            this.isAssistant = isAssistant;
            this.options = null;

            var multChoiceTag = "1)";

            if (isAssistant)
            {
                var idx = text.IndexOf(multChoiceTag);
                if (idx >= 0)
                {
                    idx--; // catch the previous newline
                    var optionText = text.Substring(idx).TrimStart();
                    text = text.Substring(0, idx);

                    var lines = optionText.Split("\n");
                    var options = new List<ChatOption>();
                    foreach (var line in lines)
                    {
                        var tmp = line.Split(')', 2);

                        if (tmp.Length == 2)
                        {
                            var id = tmp[0];
                            var caption = tmp[1].Trim();

                            options.Add(new ChatOption(id, caption));
                        }
                    }

                    this.options = options.ToArray();
                }
            }

            this.text = text;
        }

        public override string ToString()
        {
            return text;
        }
    }


    public abstract class SmartBot
    {
        public const string CHAT_BREAK = "####";

        public readonly int chat_id;

        public readonly string rootPath;
        public readonly string chatLogPath;

        public List<ChatEntry> convo = new List<ChatEntry>();

        public string Memory { get; private set; } 

        public SmartBot(int chat_id, string path)
        {
            Memory = string.Empty;

            this.chat_id = chat_id;

            this.rootPath = path;
            chatLogPath = path + "Chatlogs/";
            if (!Directory.Exists(chatLogPath))
            {
                Directory.CreateDirectory(chatLogPath);
            }

            var fileName = GetChatLogPath();

            if (File.Exists(fileName))
            {
                var lines = File.ReadAllLines(fileName);


                var sb = new StringBuilder();

                bool waitingForUserType = true;
                bool isAssistant = false;

                foreach (var line in lines)
                {
                    if (line.StartsWith(CHAT_BREAK))
                    {
                        if (sb.Length > 0)
                        {
                            convo.Add(new ChatEntry(isAssistant, sb.ToString()));
                            sb.Clear();
                        }

                        waitingForUserType = true;
                    }
                    else
                    if (waitingForUserType)
                    {
                        isAssistant = line.StartsWith("ai:");
                        waitingForUserType = false;
                    }
                    else
                    {
                        sb.AppendLine(line);
                    }
                }

                if (sb.Length > 0)
                {
                    convo.Add(new ChatEntry(isAssistant, sb.ToString()));
                }
            }
        }

        public void AddToMemory(string text)
        {
            if (!string.IsNullOrEmpty(Memory))
            {
                text = "\n" + text;
            }

            Memory += text;
        }

        protected virtual string FormatAnswer(string answer) => answer;

        public void AddAnswerToConvo(string question, params string[] answers)
        {
            if (!string.IsNullOrEmpty(question))
            {
                convo.Add(new ChatEntry(false, question));
            }

            foreach (var choice in answers)
            {
                var answer = FormatAnswer(choice);
                convo.Add(new ChatEntry(true, answer));
            }
        }

        public abstract string GetRules();
        public virtual string GetPreAnswer(ref string questionText) => null;

        public string GetChatLogPath()
        {
            return chatLogPath + chat_id + ".txt";
        }

        public virtual void SaveHistory()
        {
            var lines = new List<string>();

            foreach (var entry in convo)
            {
                lines.Add(entry.isAssistant ? "ai:": "user:");
                lines.Add(entry.text);

                if (entry.options != null)
                {
                    foreach (var option in entry.options)
                    {
                        lines.Add($"{option.id}) {option.caption}");
                    }
                }
                lines.Add(CHAT_BREAK);
            }

            var fileName = GetChatLogPath();
            File.WriteAllLines(fileName, lines);
        }
    }

    public class SimpleGPTBot: SmartBot
    {
        private string assistantText;

        public SimpleGPTBot(int chat_id, string path) : base(chat_id, path) 
        { 

            assistantText = File.ReadAllText(path + "assistant.txt");

            if (string.IsNullOrEmpty(assistantText))
            {
                throw new Exception("Could not load assistant data");
            }
        }

        public override string GetRules() => assistantText;
    }

    public class ChatGTPBotPlugin
    {   
        private string apiKey;

        private string rootPath;

        private OpenAIService gpt3;

        private Dictionary<int, SmartBot> instances = new Dictionary<int, SmartBot>();
        private HashSet<int> pending = new HashSet<int>();

        private Func<int, string, SmartBot> _botInit;

        public static ChatGTPBotPlugin Initialize<T>(string path, string apiKey) where T : SmartBot
        {
            Func<int, string, SmartBot> activator = (id, path) =>
            {
                var bot = (T)Activator.CreateInstance(typeof(T), id, path);
                return bot;
            };

            return new ChatGTPBotPlugin(activator, path, apiKey);
        }

        private ChatGTPBotPlugin(Func<int, string, SmartBot> botActivator, string path, string apiKey)
        {
            _botInit = botActivator;

            if (!path.EndsWith('/'))
            {
                path += "/";
            }

            rootPath = path;

            if (string.IsNullOrEmpty(apiKey))
            {
                throw new Exception("Please configure API key");
            }
            else
            {
                this.apiKey = apiKey;
            }

            // Create an instance of the OpenAIService class
            gpt3 = new OpenAIService(new OpenAiOptions()
            {
                ApiKey = apiKey
            });
        }

        public void Install(HTTPServer server, string entryPath = "/")
        {
            var templateEngine = new TemplateEngine(server, "botviews");

            if (!entryPath.EndsWith("/"))
            {
                entryPath += "/";
            }

            server.Get(entryPath, (request) =>
            {
                var id = request.session.GetInt("chat_id");

                if (id <= 0)
                {
                    id = GenerateUserID();
                }

                return HTTPResponse.Redirect(entryPath + id);
            });

            server.Get("/new", (request) =>
            {
                var id = GenerateUserID();
                request.session.SetInt("chat_id", id);
                return HTTPResponse.Redirect(entryPath + id);
            });

            server.Get(entryPath + "{chat_id}", (request) =>
            {
                var context = GetContext(request);
                return templateEngine.Render(context, "main");
            });

            server.Get("/convo/{chat_id}", (request) =>
            {
                var context = GetContext(request);
                return templateEngine.Render(context, "convo");
            });

            server.Post("/convo/{chat_id}", (request) =>
            {
                var text = request.GetVariable("message");

                var chat_id = GetChatID(request);

                var bot = FindBot(chat_id);
                //var result = Task.Run(() => DoChatRequest(userID, text)).Result; 
                DoChatRequest(chat_id, text);

                var context = GetContext(request);
                return templateEngine.Render(context, "convo");
            });
        }

        private string FilterCodeTags(string input, ref bool insideCode)
        {
            var sb = new StringBuilder();

            var prev1 = '\0';
            var prev2 = '\0';

            foreach (var ch in input)
            {
                if (ch == '`')
                {
                    if (prev2 == ch && prev1 == ch)
                    {
                        insideCode = !insideCode;

                        if (insideCode)
                        {
                            sb.AppendLine("</p><pre>");
                        }
                        else
                        {
                            sb.AppendLine("</pre><p>");
                        }

                    }
                }
                else
                    switch (ch)
                    {
                        case '<': sb.Append("&lt;"); break;
                        case '>': sb.Append("&gt;"); break;
                        case '&': sb.Append("&amp;"); break;
                        case '"': sb.Append("&quot;"); break;
                        case '\'': sb.Append("&#39;"); break;

                        default:
                            sb.Append(ch); break;
                    }

                prev1 = prev2;
                prev2 = ch;
            }

            return sb.ToString();
        }

        private List<ChatEntry> BeautifyConvo(List<ChatEntry> convo)
        {
            var result = new List<ChatEntry>();

            int idx = -1;

            bool insideCode = false;
            foreach (var entry in convo)
            {
                idx++;

                string output;

                int optionID;

                if (idx > 0 && int.TryParse(entry.text, out optionID))
                {
                    var prev = convo[idx - 1];
                    optionID--;
                    output = prev.options[optionID].caption;
                }
                else
                {
                    var wasInsideCode = insideCode;

                    var lines = entry.text.Split('\n');

                    var sb = new StringBuilder();

                    foreach (var line in lines)
                    {
                        var text = FilterCodeTags(line, ref insideCode);

                        sb.Append(text);

                        if (insideCode)
                        {
                            sb.Append("\n");
                        }
                        else
                        {
                            sb.Append("<br>");
                        }
                    }

                    output = sb.ToString().Trim(); //.Replace("<br><br>", "<br>");
                }

                result.Add(new ChatEntry(entry.isAssistant, output, entry.options));
            }

            return result;
        }

        private SmartBot FindBot(int chat_id, bool createIfNotExist = true)
        {
            SmartBot bot;

            lock (instances)
            {
                if (instances.ContainsKey(chat_id))
                {
                    bot = instances[chat_id];
                }
                else
                if (createIfNotExist)
                {
                    bot = _botInit(chat_id, rootPath);

                    if (bot.convo.Any(x => !x.isAssistant) || createIfNotExist)
                    {
                        instances[chat_id] = bot;
                    }
                    else
                    {
                        bot = null;
                    }
                }
                else 
                {
                    bot = null;
                }
            }

            return bot;
        }

        private async Task<bool> DoChatRequest(int chat_id, string questionText)
        {
            var bot = FindBot(chat_id);

            var quickAnswer = bot.GetPreAnswer(ref questionText);

            if (!string.IsNullOrEmpty(quickAnswer))
            {
                bot.AddAnswerToConvo(questionText, quickAnswer);
                return true;
            }

            Console.WriteLine($"CHATGPT.beginRequest({chat_id})");

            lock (pending)
            {
                if (pending.Contains(chat_id))
                {
                    return false;
                }

                pending.Add(chat_id);

                bot.AddAnswerToConvo(questionText);
            }

            IList<ChatMessage> messages = new List<ChatMessage>();

            var botRules = bot.GetRules();

            if (!string.IsNullOrEmpty(bot.Memory))
            {
                if (string.IsNullOrEmpty(botRules))
                {
                    botRules = bot.Memory;
                }
                else
                {
                    botRules += "\n" + bot.Memory;
                }
            }

            if (!string.IsNullOrEmpty(botRules))
            {
                messages.Add(new ChatMessage("system", botRules));
            }

            foreach (var entry in bot.convo)
            {
                string role = entry.isAssistant ? "assistant" : "user";

                messages.Add(new ChatMessage(role, entry.text));
            }

            messages.Add(new ChatMessage("user", questionText));

            // https://platform.openai.com/docs/models/gpt-3-5
            LimitTokens(messages, 4000);

            Console.WriteLine("Sending ChatGTP request...");
            // Create a chat completion request
            var completionResult = await gpt3.ChatCompletion.CreateCompletion(
                                    new ChatCompletionCreateRequest()
                                    {
                                        Messages = messages,
                                        Model = Models.ChatGpt3_5Turbo,
                                        Temperature = 0.5f,
                                        MaxTokens = 2500,
                                        N = 1,
                                    });

            // Check if the completion result was successful and handle the response
            Console.WriteLine("Got ChatGTP answer...");
            if (completionResult.Successful)
            {
                var answers = completionResult.Choices.Select(x => x.Message.Content).ToArray();

                foreach (var answer in answers)  Console.WriteLine(answer);

                bot.AddAnswerToConvo(null, answers);
                bot.SaveHistory();
            }
            else
            {
                if (completionResult.Error == null)
                {
                    throw new Exception("Unknown Error");
                }
                Console.WriteLine($"{completionResult.Error.Code}:{completionResult.Error.Message}");
            }


            lock (pending)
            {
                pending.Remove(chat_id);
            }

            Console.WriteLine($"CHATGPT.endRequest({chat_id})");

            return completionResult.Successful;
        }

        private void LimitTokens(IList<ChatMessage> messages, int tokenLimit)
        {
            int discardedCount = 0;

            do
            {
                int charCount = 0;
                foreach (var msg in messages)
                {
                    charCount += msg.Content.Length;
                }

                var tokenCount = charCount / 3; // approximation

                if (tokenCount <= tokenLimit)
                {
                    break;
                }

                for (int i = 0; i < messages.Count; i++)
                {
                    if (messages[i].Role != "system")
                    {
                        discardedCount += messages[i].Content.Length;
                        messages.RemoveAt(i);
                        break;
                    }
                }

            } while (true);

            if (discardedCount > 0)
            {
                Console.WriteLine($"Context pruned, {discardedCount} characters discarded");
            }
        }

        private string GetConvoAsJSON(int chat_id)
        {
            var json = new StringBuilder();
            json.Append('[');

            var bot = FindBot(chat_id);
            var entries = bot.convo;
            foreach (var entry in entries)
            {
                json.AppendLine($"\"{entry.text}\"");
            }

            json.Append(']');
            return json.ToString();
        }

        private static Random random = new Random();

        private int GenerateUserID()
        {
            lock (instances)
            {
                int randomID;

                do
                {
                    randomID = 1000 + random.Next() % 899999;

                    var bot = FindBot(randomID, createIfNotExist: false);

                    if (bot == null)
                    {
                        return randomID;
                    }

                } while (true);
            }
        }

        private int GetChatID(HTTPRequest request)
        {
            int chat_id;

            int.TryParse(request.GetVariable("chat_id"), out chat_id);

            return chat_id;
        }

        private Dictionary<string, object> GetContext(HTTPRequest request)
        {
            var session = request.session;

            var user_id = session.ID.Substring(0, 16);

            var chat_id = GetChatID(request);

            var bot = FindBot(chat_id);

            bool hasControls = false;

            var context = new Dictionary<string, object>();
            context["user_name"] = "Anonymous";
            context["user_id"] = user_id;
            context["chat_id"] = chat_id;

            if (bot != null && bot.convo != null)
            {
                var chat = BeautifyConvo(bot.convo);

                if (chat.Any())
                {
                    hasControls = chat.Count >= 2;

                    var last = chat.Last();

                    if (last.isAssistant && last.options != null)
                    {
                        context["options"] = last.options;
                    }
                }

                context["chat"] = chat;
            }

            request.session.SetString("user_id", user_id);
            request.session.SetInt("chat_id", chat_id);

            lock (pending)
            {
                context["pending"] = pending.Contains(chat_id);
            }

            context["has_controls"] = hasControls;

            return context;
        }


    }
}
