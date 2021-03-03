using CommandLine;
using HtmlAgilityPack;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace PreconDeckListBuilder
{
    interface IIndexedValue
    {
        string Id { get; set; }
    }

    class DeckType : IIndexedValue
    {
        public string Id { get; set; }
    }

    class DeckSet : IIndexedValue
    {
        public string Id { get; set; }
        public string FriendlyName { get; set; }
    }

    class DeckListEntry : IIndexedValue
    {
        public DeckSet Set { get; set; }
        public DeckType Type { get; set; }
        public string Id { get; set; }
        public string Url { get; set; }

        public List<Card> Cards { get; set; }

        public DeckListEntry()
        {
            Cards = new List<Card>();
        }
    }

    class Card
    {
        public string Id { get; set; }
        public string SetCode { get; set; }
        public string SetId { get; set; }
        public string Name { get; set; }
        public string Quantity { get; set; }
        public bool IsSideboard { get; set; }

        public string GetXmageReference()
        {
            return $"{GetSideboardPrefix()}{Quantity} [{SetCode.ToUpper()}:{SetId}] {Name}";
        }

        private string GetSideboardPrefix()
        {
            return IsSideboard ? "SB: " : "";
        }
    }

    class Options
    {
        [Option("Clean", Required = false, HelpText = "Delete temp and deck directory and files.")]
        public bool Clean { get; set; }

        [Option("Debug", Required = false, HelpText = "Output debug info in the console.")]
        public bool Debug { get; set; }

        [Option("OutDir", Required = false, HelpText = "Output dir.", Default = "./decks")]
        public string OutDir { get; set; }

        [Option("TempDir", Required = false, HelpText = "Temp dir.", Default = "./temp")]
        public string TempDir { get; set; }

        [Option("Url", Required = false, HelpText = "Source url (default: https://mtg.wtf/deck).", Default = "https://mtg.wtf/deck")]
        public string Url { get; set; }

    }

    class Program
    {
        static void Main(string[] args)
        {
            Parser.Default.ParseArguments<Options>(args).WithParsed(o => (new ListBuilder(o)).Run());
        }
    }

    class ListBuilder
    {
        readonly Repository repository = new Repository();
        readonly Options _options;
        string TempDir => _options.TempDir;
        string OutputDir => _options.OutDir;
        string Url => _options.Url;
        bool IsDebug => _options.Debug;
        bool Clean => _options.Clean;

        public ListBuilder(Options options)
        {
            _options = options;
        }

        public void Run()
        {
            BuildDataStoreFromHomePage();
            BuildLocalCopy();
            BuildDeckLists();
        }

        void BuildDataStoreFromHomePage()
        {
            BuildAndCleanDir(TempDir);
            var contentInParenthesis = @"(?<=\().+?(?=\))";
            var doc = new HtmlWeb().Load(Url);

            Console.WriteLine($"Parsing index.");

            var sets = doc.DocumentNode.SelectNodes("/html/body/div/div[not(@id) and not(@class)]");
            var decklists = doc.DocumentNode.SelectNodes("/html/body/div/ul[not(@id) and not(@class)]");

            if (sets.Count != decklists.Count) throw new Exception("sets and decklists count mismatch");

            for (int i = 0; i < sets.Count && i < decklists.Count; i++)
            {
                var setName = sets[i].InnerHtml.Trim();
                var setCode = Regex.Match(setName, contentInParenthesis).Value;
                var set = repository.DeckSets.UpdateOrCreate(new DeckSet()
                {
                    Id = setCode,
                    FriendlyName = System.Net.WebUtility.HtmlDecode(setName.Replace('(' + setCode + ')', "").Trim()),
                });

                foreach (var list in decklists[i].SelectNodes("li/a"))
                {
                    var meta = Regex.Match(list.NextSibling.InnerHtml, contentInParenthesis).Value.Split(',');
                    var type = repository.DeckTypes.GetOrCreate(meta[0]);

                    repository.DeckListEntries.Add(new DeckListEntry()
                    {
                        Id = System.Net.WebUtility.HtmlDecode(list.InnerHtml.Trim()),
                        Set = set,
                        Type = type,
                        Url = "https://" + new Uri(Url).Host + list.GetAttributeValue("href", "")
                    });
                }
            }
        }

        void BuildLocalCopy()
        {
            Console.WriteLine($"{repository.DeckListEntries.GetAll().Count()} decks listed, create local copy.");
            foreach (var decklist in repository.DeckListEntries.GetAll())
            {
                var decklistSourceFile = GetTempPath(decklist);

                if (!File.Exists(decklistSourceFile))
                    new HtmlWeb().Load(decklist.Url).Save(decklistSourceFile);                

                Console.Write(".");
            }
        }

        void BuildDeckLists()
        {
            BuildAndCleanDir(OutputDir);
            Console.WriteLine($"\nBuilding decks.");
            foreach (var decklist in repository.DeckListEntries.GetAll())
            {
                var decklistSourceFile = GetTempPath(decklist);

                HtmlDocument doc = new HtmlDocument();
                doc.Load(decklistSourceFile);

                var cards = doc.DocumentNode.SelectNodes("//div[@class='card_entry']");
                bool isSideboard = decklist.Type.Id.Contains("Commander");

                foreach (var card in cards)
                {
                    var quantity = Int32.Parse(Regex.Match(card.InnerText, @"([0-9]+).*").Value);
                    var name = card.SelectSingleNode("span/a").InnerText.Split('\n')[0];
                    var url = card.SelectSingleNode("span/a").GetAttributeValue("href", "/err/000/_/_").Split("/");
                    var set = url[2];
                    var id = url[3];

                    isSideboard = isSideboard || (card.ParentNode.GetAttributeValue("class", "") == "card_group" && card.ParentNode.InnerText.Contains("Sideboard"));

                    if (IsDebug) Console.WriteLine($"{quantity}x {name} ({set}:{id})");

                    decklist.Cards.Add(new Card()
                    {
                        SetCode = System.Net.WebUtility.HtmlDecode(set),
                        SetId = System.Net.WebUtility.HtmlDecode(id),
                        Id = System.Net.WebUtility.HtmlDecode(name),
                        Name = System.Net.WebUtility.HtmlDecode(name),
                        Quantity = quantity.ToString(),
                        IsSideboard = isSideboard
                    });

                    isSideboard = false;
                }                
                SaveDeckList(decklist);
                Console.Write('.');
            }
        }

        void SaveDeckList(DeckListEntry decklist)
        {
            string list = "";
            foreach (var card in decklist.Cards)
                list += card.GetXmageReference() + Environment.NewLine;

            File.WriteAllTextAsync(GetOutputPath(decklist), list);
        }

        string GetTempPath(DeckListEntry entry)
        {
            return Path.Combine(TempDir, string.Concat(entry.Url.Split(Path.GetInvalidFileNameChars())));
        }

        string GetOutputPath(DeckListEntry entry)
        {
            return Path.Combine(OutputDir, string.Concat($"[{entry.Set.Id} - {entry.Type.Id}] {entry.Id}.dck".Split(Path.GetInvalidFileNameChars())));
        }

        void BuildAndCleanDir(string dir)
        {
            if (Clean && Directory.Exists(dir))
                Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
        }
    }

    class Repository
    {
        public class GenericRepo<T> where T : IIndexedValue, new()
        {
            private List<T> _instances { get; }

            public GenericRepo()
            {
                this._instances = new List<T>();
            }

            public T GetOrCreate(string id)
            {
                if (!this._instances.Any(i => String.Equals(i.Id, id)))
                    this._instances.Add(new T() { Id = id });

                return this._instances.FirstOrDefault(i => String.Equals(i.Id, id));
            }

            public T UpdateOrCreate(T instance)
            {
                if (this._instances.Any(i => String.Equals(i.Id, instance.Id)))
                    this._instances.RemoveAll(i => String.Equals(i.Id, instance.Id));

                this._instances.Add(instance);

                return instance;
            }

            public void Add(T _newInstance)
            {
                if (!this._instances.Any(i => String.Equals(i.Id, _newInstance.Id)))
                    this._instances.Add(_newInstance);
            }

            public List<T> GetAll()
            {
                return this._instances;
            }
        }

        public class DeckTypeRepository : GenericRepo<DeckType> { }
        public class DeckSetRepository : GenericRepo<DeckSet> { }
        public class DeckListEntryRepository : GenericRepo<DeckListEntry> { }

        public DeckListEntryRepository DeckListEntries = new DeckListEntryRepository();
        public DeckTypeRepository DeckTypes = new DeckTypeRepository();
        public DeckSetRepository DeckSets = new DeckSetRepository();
    }
}