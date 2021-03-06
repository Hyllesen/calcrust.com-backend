using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WebAPI.Models;

namespace WebAPI
{
    internal static class DataManager
    {
        public static RustData Data { get; private set; }

        private static string filePath;

        private static FileSystemWatcher watcher;
        private static DateTime lastWriteTime;

        private static Dictionary<string, int> authTries = new Dictionary<string, int>();

        public static void Start(string _filePath)
        {
            filePath = _filePath;

            if (watcher != null)
                throw new InvalidOperationException("Already started.");

            if (!File.Exists(filePath))
                throw new FileNotFoundException("The specified data file can't be found.", filePath);
            
            string directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
            string fileName = Path.GetFileName(filePath);
            watcher = new FileSystemWatcher(directory, fileName);
            Console.WriteLine($"Watching directory {directory} for {fileName}");

            watcher.Changed += OnChanged;
            watcher.EnableRaisingEvents = true;

            LoadFile();
        }

        private static void OnChanged(object sender, FileSystemEventArgs e)
        {
            try
            {
                var writeTime = File.GetLastWriteTime(filePath);

                if (writeTime - lastWriteTime <= new TimeSpan(0, 0, 0, 0, 50)) // Hack: avoid event being triggered twice caused by bug by checking if time since last save is > 50ms.
                    return;

                lastWriteTime = writeTime;

                Thread.Sleep(100); // File in use for some reason (by the watcher i assume).

                Console.Write($"Reloading...");
                Data?.Dispose();
                Data = null;
                LoadFile();
                Console.WriteLine(" done.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
            }
        }

        public static void Stop()
        {
            watcher?.Dispose();
            watcher = null;
        }

        private static void LoadFile()
        {
            string contents = File.ReadAllText(filePath);
            Data = ParseData(contents);

            if (!File.Exists("auths.json"))
            {
                SaveAuthTries();
            }
            else
            {
                authTries = JsonConvert.DeserializeObject<Dictionary<string, int>>(File.ReadAllText("auths.json"));
            }
        }

        private static RustData ParseData(string contents)
        {
            var data = new RustData();
            JObject jData = JObject.Parse(contents);

            data.Items = jData["items"].ToObject<Dictionary<string, Item>>();

            // Set shortname
            foreach (var keyval in data.Items)
            {
                keyval.Value.Shortname = keyval.Key;
            }

            data.Meta = jData["meta"].ToObject<Meta>();
            data.Recipes = new Dictionary<string, Recipe>();
            data.Cookables = new Dictionary<string, Cookable>();
            data.DamageInfo = jData["damageInfo"].ToObject<Dictionary<string, DamageInfo>>();

            var jRecipes = jData["recipes"].Value<JObject>();
            var jCookables = jData["cookables"].Value<JObject>();

            foreach (var keyval in jRecipes)
            {
                dynamic jRecipe = keyval.Value;
                string shortname = keyval.Key;
                var recipe = keyval.Value.ToObject<Recipe>();

                recipe.Output = ParseRecipeItem(jRecipe.output, data.Items);
                recipe.Input = ((JArray) jRecipe.input).Select(r => ParseRecipeItem(r, data.Items)).ToList();

                if (jRecipe.parent != null)
                    recipe.Parent = data.Items[(string) jRecipe.parent];

                data.Recipes.Add(shortname, recipe);
            }

            foreach (var keyval in jCookables)
            {
                dynamic jCookable = keyval.Value;
                string shortname = keyval.Key;
                var cookable = keyval.Value.ToObject<Cookable>();

                cookable.Output = ParseRecipeItem(jCookable.output, data.Items);
                cookable.UsableOvens = ((JArray) jCookable.usableOvens).Select(jItem => data.Items[jItem["shortname"].Value<string>()]).ToList();

                data.Cookables.Add(shortname, cookable);
            }
            
            return data;
        }

        private static Recipe.Item ParseRecipeItem(JToken jItem, Dictionary<string, Item> items)
        {
            return new Recipe.Item(jItem["count"].Value<double>(), items[jItem["item"].Value<string>()]);
        }

        public static void ChangeData(string json)
        {
            var newData = ParseData(json);
            Data.Dispose();
            Data = newData;
        }

        public static bool IsBanned(string ip)
        {
            return authTries.ContainsKey(ip) && authTries[ip] >= 5;
        }

        public static void IncrementTries(string ip)
        {
            if (!authTries.ContainsKey(ip))
                authTries.Add(ip, 0);

            ++authTries[ip];
            SaveAuthTries();
        }

        public static void ResetTries(string ip)
        {
            if (authTries.ContainsKey(ip))
                authTries.Remove(ip);

            SaveAuthTries();
        }

        private static readonly object _saveLock = new object();
        private static void SaveAuthTries()
        {
            lock (_saveLock)
            {
                using (var file = File.CreateText("auths.json"))
                {
                    file.Write(JsonConvert.SerializeObject(authTries, Formatting.Indented));
                }
            }
        }

        public static void Save(string json)
        {
            using (var file = File.CreateText(filePath))
            {
                file.Write(json);
            }
        }
    }
}