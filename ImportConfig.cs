using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Azure.Data.AppConfiguration;
using CommandLine;
using ImportConfig;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

await Parser.Default.ParseArguments<Options>(args)
    .WithParsedAsync(async options =>
    {
        ConfigurationSetting CreateFeatureSetting((string Key, bool Value) data) =>
            new($".appconfig.featureflag/{data.Key}", FormatFeatureSetting(data), options.Label)
            {
                ContentType = "application/vnd.microsoft.appconfig.ff+json;charset=utf-8"
            };

        ConfigurationSetting CreateSecretSetting((string Key, string Value) data) =>
            new(data.Key, FormatSecretSetting(data.Value), options.Label)
            {
                ContentType = "application/vnd.microsoft.appconfig.keyvaultref+json;charset=utf-8"
            };

        ConfigurationSetting CreateConfigSetting((string Key, string Value) data) =>
            new(data.Key, data.Value, options.Label);

        var json = await File.ReadAllTextAsync(options.Path);
        var jsonObject = JObject.Parse(json);
        var props = GetFlattenedProperties(jsonObject, options.Separator);
        var features = GetFeatures(jsonObject).ToArray();


        Console.WriteLine($"Updating settings using label '{options.Label}'...");
        var client = new ConfigurationClient(options.ConnectionString);
        var tasks = props
            .Select(p =>
                p.SecretReference
                    ? CreateSecretSetting((p.Key, p.Value))
                    : CreateConfigSetting((p.Key, p.Value)))
            .Concat(features.Select(CreateFeatureSetting))
            .Select(async s =>
            {
                var response = await client.GetConfigurationSettingsAsync(new SettingSelector
                {
                    KeyFilter = s.Key,
                    LabelFilter = s.Label
                }).AsPages().ToArrayAsync();
                var current = response.FirstOrDefault()?.Values.FirstOrDefault();
                if (current != null && current.Value == s.Value && current.ContentType == s.ContentType)
                    return;
                await client.SetConfigurationSettingAsync(s);
                Console.WriteLine($"{s.Key} -> {s.Value}");
            });
        await Task.WhenAll(tasks);
        Console.WriteLine("Done.");
    });

const string FeatureManagement = "FeatureManagement";

IEnumerable<(string Key, string Value, bool SecretReference)> GetFlattenedProperties(
    JContainer container, char separator) =>
    container.Descendants()
        // Filter out only nodes with no children
        .Where(t => !t.Any())
        .Where(t => !t.Path.StartsWith(FeatureManagement))
        .Aggregate(new List<(string, string, bool)>(), (properties, t) =>
        {
            var key = t.Path.Replace('.', separator);
            var value = t.ToString();
            var isSecret = t.Path.EndsWith(".uri") && Regex.IsMatch(value, @"^https://.+\.vault\.azure\.net");
            properties.Add((isSecret ? key[..^4] : key, value, isSecret));
            return properties;
        });

IEnumerable<(string Key, bool Value)> GetFeatures(JObject jsonObject) =>
    jsonObject[FeatureManagement]?.Children()
        .Select(jToken => jToken as JProperty)
        .Where(p => p != null)
        .Select(p => (p.Name, p.ToObject<bool>()))
    ?? Array.Empty<(string Key, bool Value)>();

string FormatFeatureSetting((string Key, bool Value) data) =>
    JsonConvert.SerializeObject(new
    {
        id = data.Key,
        description = "",
        enabled = data.Value,
        conditions = new
        {
            client_filters = Array.Empty<object>()
        }
    });

string FormatSecretSetting(string value) =>
    JsonConvert.SerializeObject(new { uri = value });

// ReSharper disable once ClassNeverInstantiated.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace ImportConfig
{
    internal class Options
    {
        [Value(index: 0, Required = true, HelpText = "Path to configuration file.")]
        public string Path { get; set; }

        [Option(longName: "label", Required = false, HelpText = "Label of the imported configuration.", Default = null)]
        public string Label { get; set; }

        [Option(longName: "connection-string", Required = true, HelpText = "Used for connecting.")]
        public string ConnectionString { get; set; }

        [Option(longName: "separator", Required = false, HelpText = "Used for connecting.", Default = ':')]
        public char Separator { get; set; }
    }
}
