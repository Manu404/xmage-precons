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
        public int CardCount { get; set; }
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
        public bool InSideboard { get; set; }

        public string GetXmageReference()
        {
            return $"{GetSideboardPrefix()}{Quantity} [{SetCode.ToUpper()}:{SetId}] {Name}";
        }

        private string GetSideboardPrefix()
        {
            return InSideboard ? "SB: " : "";
        }
    }

    class Program
    {
        static void Main(string[] args)
        {
            (new ListBuilder()).Run();
        }
    }

    class ListBuilder
    {
        readonly Repository repository = new Repository();
        readonly string tempDir = "./temp";
        readonly string outputDir = "./decks";
        bool debug = false;
        bool clean = false;

        public void Run()
        {
            BuildDataStoreFromHomePage(!debug ? "http://localhost/mtg/home.html" : "https://mtg.wtf/");
            BuildLocalCopy();
            BuildDeckLists();
        }

        void BuildDataStoreFromHomePage(string url)
        {
            var contentInParenthesis = @"(?<=\().+?(?=\))";
            var doc = new HtmlWeb().Load(url);

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
                        Url = list.GetAttributeValue("href", "")
                    });
                }
            }
        }

        void BuildLocalCopy()
        {
            BuildAndCleanDir(tempDir);
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
            BuildAndCleanDir(outputDir);
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

                    if(debug) Console.WriteLine($"{quantity}x {name} ({set}:{id})");

                    decklist.Cards.Add(new Card()
                    {
                        SetCode = System.Net.WebUtility.HtmlDecode(set),
                        SetId = System.Net.WebUtility.HtmlDecode(id),
                        Id = System.Net.WebUtility.HtmlDecode(name),
                        Name = System.Net.WebUtility.HtmlDecode(name),
                        Quantity = quantity.ToString(),
                        InSideboard = isSideboard
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
            return Path.Combine(tempDir, string.Concat(entry.Url.Split(Path.GetInvalidFileNameChars())));
        }

        string GetOutputPath(DeckListEntry entry)
        {
            return Path.Combine(outputDir, string.Concat($"[{entry.Set.Id} - {entry.Type.Id}] {entry.Id}.dck".Split(Path.GetInvalidFileNameChars())));
        }

        void BuildAndCleanDir(string dir)
        {
            if (clean && Directory.Exists(dir))
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