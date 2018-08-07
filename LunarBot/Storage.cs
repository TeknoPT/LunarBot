using LunarLabs.Parser;
using LunarLabs.Parser.XML;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LunarLabs.Bots
{
    public class BotStorage
    {
        public readonly string Name;
        public readonly string FileName;

        public bool Modified { get; private set; }

        private Dictionary<string, List<string>> _keystore = new Dictionary<string, List<string>>();

        public BotStorage(string path, string name)
        {
            this.Name = name;
            this.FileName = path + name + ".xml";
        }

        public void Visit(Action<string, List<string>> visitor)
        {
            foreach(var entry in _keystore)
            {
                visitor(entry.Key, entry.Value);
            }
        }

        internal bool Load()
        {
            if (File.Exists(FileName))
            {
                var content = File.ReadAllText(FileName);
                var root = XMLReader.ReadFromString(content);
                root = root["entries"];
                foreach (var child in root.Children)
                {
                    var id = child.GetString("key");
                    var list = new List<string>();
                    foreach (var item in child.Children)
                    {
                        if (item.Name == "item")
                        {
                            list.Add(item.Value);
                        }
                    }

                    _keystore[id] = list;
                }
                return true;
            }
            else
            {
                return false;
            }
        }

        internal bool Save()
        {
            try
            {
                var root = DataNode.CreateArray("entries");
                foreach (var entry in _keystore)
                {
                    var node = DataNode.CreateObject("entry");
                    node.AddField("key", entry.Key);

                    foreach (var temp in entry.Value)
                    {
                        var item = DataNode.CreateObject("item");
                        item.Value = temp;
                        node.AddNode(item);
                    }

                    root.AddNode(node);
                }

                var content = XMLWriter.WriteToString(root);
                File.WriteAllText(FileName, content);
                Modified = false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void Append(MessageSender sender, string obj) 
        {
            Add(sender, obj, true);
        }

        public void Set(MessageSender sender, string obj) 
        {
            if (obj == null)
            {
                obj = "";
            }

            var old = Get(sender);
            if (old != null && old == obj)
            {
                return;
            }

            Add(sender, obj, false);
        }

        private void Add(MessageSender sender, string obj, bool append)
        {
            var tag = sender.Tag;
            List<string> list;

            if (_keystore.ContainsKey(tag))
            {
                list = _keystore[tag];
            }
            else
            {
                list = new List<string>();
                _keystore[tag] = list;
            }

            if (list.Count > 0 && !append)
            {
                list.Clear();
            }

            list.Add(obj);

            Modified = true;
        }

        public void Remove(MessageSender sender)
        {
            var tag = sender.Tag;
            if (_keystore.ContainsKey(tag))
            {
                _keystore.Remove(tag);
                Modified = true;
            }
        }

        public string Get(MessageSender sender)
        {
            var tag = sender.Tag;
            if (_keystore.ContainsKey(tag))
            {
                return _keystore[tag].FirstOrDefault();
            }
            else
            {
                return null;
            }
        }

        public IEnumerable<string> List(MessageSender sender)
        {
            var tag = sender.Tag;
            if (_keystore.ContainsKey(tag))
            {
                return _keystore[tag];
            }
            else
            {
                return Array.Empty<string>();
            }
        }

        public bool Contains(MessageSender sender)
        {
            var tag = sender.Tag;
            return _keystore.ContainsKey(tag);
        }

        public int Count(MessageSender sender)
        {
            var tag = sender.Tag;
            return _keystore.ContainsKey(tag) ? _keystore[tag].Count : 0;
        }
    }
}
