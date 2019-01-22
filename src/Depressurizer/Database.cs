using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using Depressurizer.Core.Enums;
using Depressurizer.Core.Helpers;
using Depressurizer.Core.Models;
using Newtonsoft.Json;

namespace Depressurizer
{
    public sealed class Database : Core.Database
    {
        #region Static Fields

        private static volatile Database _instance;

        #endregion

        #region Constructors and Destructors

        private Database() { }

        #endregion

        #region Public Properties

        public static Database Instance
        {
            get
            {
                if (_instance != null)
                {
                    return _instance;
                }

                lock (SyncRoot)
                {
                    if (_instance == null)
                    {
                        _instance = new Database();
                    }
                }

                return _instance;
            }
        }

        #endregion

        #region Public Methods and Operators

        public Dictionary<string, int> CalculateSortedDevList(GameList gameList, int minCount)
        {
            Dictionary<string, int> devCounts = new Dictionary<string, int>();
            if (gameList == null)
            {
                foreach (DatabaseEntry entry in Values)
                {
                    CalculateSortedDevListHelper(devCounts, entry);
                }
            }
            else
            {
                foreach (int appId in gameList.Games.Keys)
                {
                    if (Contains(appId, out DatabaseEntry entry) && !gameList.Games[appId].IsHidden)
                    {
                        CalculateSortedDevListHelper(devCounts, entry);
                    }
                }
            }

            return devCounts.Where(e => e.Value >= minCount).ToDictionary(p => p.Key, p => p.Value);
        }

        public Dictionary<string, int> CalculateSortedPubList(GameList filter, int minCount)
        {
            Dictionary<string, int> pubCounts = new Dictionary<string, int>();
            if (filter == null)
            {
                foreach (DatabaseEntry entry in Values)
                {
                    CalculateSortedPubListHelper(pubCounts, entry);
                }
            }
            else
            {
                foreach (int appId in filter.Games.Keys)
                {
                    if (!Contains(appId, out DatabaseEntry entry) || filter.Games[appId].IsHidden)
                    {
                        continue;
                    }

                    CalculateSortedPubListHelper(pubCounts, entry);
                }
            }

            return pubCounts.Where(e => e.Value >= minCount).ToDictionary(p => p.Key, p => p.Value);
        }

        public Dictionary<string, float> CalculateSortedTagList(GameList filter, float weightFactor, int minScore, int tagsPerGame, bool excludeGenres, bool scoreSort)
        {
            Dictionary<string, float> tagCounts = new Dictionary<string, float>();
            if (filter == null)
            {
                foreach (DatabaseEntry entry in Values)
                {
                    CalculateSortedTagListHelper(tagCounts, entry, weightFactor, tagsPerGame);
                }
            }
            else
            {
                foreach (int appId in filter.Games.Keys)
                {
                    if (Contains(appId, out DatabaseEntry entry) && !filter.Games[appId].IsHidden)
                    {
                        CalculateSortedTagListHelper(tagCounts, entry, weightFactor, tagsPerGame);
                    }
                }
            }

            if (excludeGenres)
            {
                foreach (string genre in AllGenres)
                {
                    tagCounts.Remove(genre);
                }
            }

            IEnumerable<KeyValuePair<string, float>> unsorted = tagCounts.Where(e => e.Value >= minScore);
            if (scoreSort)
            {
                return unsorted.OrderByDescending(e => e.Value).ToDictionary(e => e.Key, e => e.Value);
            }

            return unsorted.OrderBy(e => e.Key).ToDictionary(e => e.Key, e => e.Value);
        }

        public void ChangeLanguage(StoreLanguage language)
        {
            StoreLanguage dbLang = language;
            if (Language == dbLang)
            {
                return;
            }

            Language = dbLang;
            //clean DB from data in wrong language
            foreach (DatabaseEntry g in Values)
            {
                if (g.Id <= 0)
                {
                    continue;
                }

                g.Tags = null;
                g.Flags = null;
                g.Genres = null;
                g.SteamReleaseDate = null;
                g.LastStoreScrape = 1; //pretend it is really old data
                g.VRSupport = new VRSupport();
                g.LanguageSupport = new LanguageSupport();
            }

            // Update DB with data in correct language
            List<int> appIds = new List<int>();
            if (FormMain.CurrentProfile != null)
            {
                appIds.AddRange(FormMain.CurrentProfile.GameData.Games.Values.Where(g => g.Id > 0).Select(g => g.Id));
                using (DbScrapeDlg dialog = new DbScrapeDlg(appIds))
                {
                    dialog.ShowDialog();
                }
            }

            Save();
        }

        /// <summary>
        ///     Fetches and integrates the complete list of public apps.
        /// </summary>
        /// <returns>
        ///     The number of new entries.
        /// </returns>
        public int FetchIntegrateAppList()
        {
            int added = 0;
            int updated = 0;

            lock (SyncRoot)
            {
                HttpClient client = null;
                Stream stream = null;
                StreamReader streamReader = null;

                try
                {
                    Logger.Info("Database: Downloading list of public apps.");

                    client = new HttpClient();
                    stream = client.GetStreamAsync(Constants.GetAppList).Result;
                    streamReader = new StreamReader(stream);

                    using (JsonReader reader = new JsonTextReader(streamReader))
                    {
                        streamReader = null;
                        stream = null;
                        client = null;

                        Logger.Info("Database: Downloaded list of public apps.");
                        Logger.Info("Database: Parsing list of public apps.");

                        JsonSerializer serializer = new JsonSerializer();
                        AppList_RawData rawData = serializer.Deserialize<AppList_RawData>(reader);

                        foreach (App app in rawData.Applist.Apps)
                        {
                            if (Contains(app.AppId, out DatabaseEntry entry))
                            {
                                if (!string.IsNullOrWhiteSpace(entry.Name) && entry.Name == app.Name)
                                {
                                    continue;
                                }

                                entry.Name = app.Name;
                                entry.AppType = AppType.Unknown;

                                updated++;
                            }
                            else
                            {
                                entry = new DatabaseEntry(app.AppId)
                                {
                                    Name = app.Name
                                };

                                Add(entry);

                                added++;
                            }
                        }
                    }
                }
                finally
                {
                    streamReader?.Dispose();
                    stream?.Dispose();
                    client?.Dispose();
                }

                Logger.Info("Database: Parsed list of public apps, added {0} apps and updated {1} apps.", added, updated);
            }

            return added;
        }

        public override void Load(string path)
        {
            lock (SyncRoot)
            {
                Logger.Info("Database: Loading database from '{0}'.", path);
                if (!File.Exists(path))
                {
                    Logger.Warn("Database: Database file not found at '{0}'.", path);

                    return;
                }

                Stopwatch sw = new Stopwatch();
                sw.Start();

                using (StreamReader file = File.OpenText(path))
                {
                    JsonSerializer serializer = new JsonSerializer
                    {
#if DEBUG
                        Formatting = Formatting.Indented
#endif
                    };

                    _instance = (Database) serializer.Deserialize(file, typeof(Database));
                }

                sw.Stop();
                Logger.Info("Database: Loaded database from '{0}', in {1}ms.", path, sw.ElapsedMilliseconds);
            }
        }

        public void Reset()
        {
            lock (SyncRoot)
            {
                Logger.Info("Database: Database was reset.");
                _instance = new Database();
            }
        }

        public override void Save(string path)
        {
            lock (SyncRoot)
            {
                Logger.Info("Database: Saving database to '{0}'.", path);

                Stopwatch sw = new Stopwatch();
                sw.Start();

                using (StreamWriter file = File.CreateText(path))
                {
                    JsonSerializer serializer = new JsonSerializer
                    {
#if DEBUG
                        Formatting = Formatting.Indented
#endif
                    };

                    serializer.Serialize(file, _instance);
                }

                sw.Stop();
                Logger.Info("Database: Saved database to '{0}', in {1}ms.", path, sw.ElapsedMilliseconds);
            }
        }

       
        #endregion
    }
}
