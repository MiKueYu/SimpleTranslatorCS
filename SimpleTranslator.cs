using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;

using fastJSON5;


namespace SimpleTranslatorCS;

public record ModMetadata : AbstractModMetadata
{
    public override string ModGuid { get; init; } = "com.simple.translator.cs";
    public override string Name { get; init; } = "SimpleTranslatorCS";
    public override string Author { get; init; } = "MiKueYu";
    public override List<string>? Contributors { get; init; }
    public override SemanticVersioning.Version Version { get; init; } = new("1.1.1");
    public override SemanticVersioning.Range SptVersion { get; init; } = new("~4.0.0");

    public override List<string>? Incompatibilities { get; init; }
    public override Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public override string? Url { get; init; }
    public override bool? IsBundleMod { get; init; } = false;
    public override string? License { get; init; } = "MIT";
}

[Injectable(TypePriority = OnLoadOrder.PostSptModLoader + 1)]
public class SimpleTranslator(
    ISptLogger<SimpleTranslator> logger,
    DatabaseServer databaseServer,
    ModHelper modHelper,
    LocaleService localeService) : IOnLoad
{
    public Task OnLoad()
    {
        var tables = databaseServer.GetTables();

        // 目标语言
        var serverLocales = new[] { "ch" };
        var loadedFileCount = 0;

        var partiallyCoveredFiles = new List<(string fileName, int covered, int total)>();
        var nonCoveredFiles = new List<string>();
        var fullyCoveredFiles = new List<string>();

        var addedLocalesCountByLang = new Dictionary<string, int>();

        var modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        foreach (var locale in serverLocales)
        {
            var localePath = Path.Combine(modPath, "db", "locales", locale);
            if (!Directory.Exists(localePath))
            {
                logger.Info($"[文本汉化]未找到汉化文件夹目录: {locale}, 请检查是否在 SPT/user/mods/SimpleTranslatorCS/db/locales 中已经创建 ch 文件夹");
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(localePath, "*.*", SearchOption.AllDirectories)
                         .Where(f => new[] { ".json", ".json5", ".jsonc" }.Contains(Path.GetExtension(f).ToLowerInvariant())))
            {
                loadedFileCount++;

                Dictionary<string, string>? content = null;
                var ext = Path.GetExtension(file).ToLowerInvariant();
                var raw = File.ReadAllText(file);

                try
                {
                    if (ext == ".json")
                    {
                        content = JsonSerializer.Deserialize<Dictionary<string, string>>(raw);
                    }
                    else if (ext == ".json5")
                    {
                        content = JSON5.ToObject<Dictionary<string, string>>(raw);
                    }
                    else if (ext == ".jsonc")
                    {
                        var noComments = StripJsonComments(raw);
                        var normalized = Regex.Replace(noComments, @",\s*(\}|\])", "$1");
                        content = JsonSerializer.Deserialize<Dictionary<string, string>>(normalized);
                    }
                }
                catch
                {
                    // 解析错误时跳过该文件
                    continue;
                }

                if (content == null || content.Count == 0)
                {
                    continue;
                }

                var totalFields = content.Count;
                // 使用英文(en)字典作为参考来计算覆盖字段数
                var referenceLocales = localeService.GetLocaleDb("en");
                var coveredFields = content.Keys.Count(k => referenceLocales.ContainsKey(k));



                // 目标语言容器必须存在
                if (!tables.Locales.Global.TryGetValue(locale, out var lazyTarget))
                {
                    logger.Warning($"目标语言 {locale} 在数据库中不存在，跳过该语言覆盖");
                    continue;
                }

                // 在目标语言上追加 transformer 以合并我们的更改（加入空字典保护）
                lazyTarget.AddTransformer(targetDict =>
                {
                    if (targetDict == null)
                    {
                        targetDict = new Dictionary<string, string>();
                    }

                    foreach (var kvp in content)
                    {
                        targetDict[kvp.Key] = kvp.Value;
                    }
                    return targetDict;
                });

                if (!addedLocalesCountByLang.ContainsKey(locale))
                {
                    addedLocalesCountByLang[locale] = 0;
                }
                addedLocalesCountByLang[locale] += coveredFields;

                var fileName = Path.GetFileName(file);
                if (coveredFields == 0)
                {
                    nonCoveredFiles.Add(fileName);
                }
                else if (coveredFields < totalFields)
                {
                    partiallyCoveredFiles.Add((fileName, coveredFields, totalFields));
                }
                else
                {
                    fullyCoveredFiles.Add(fileName);
                }
            }
        }

        logger.Info($"[文本汉化]总计 [{loadedFileCount}] 个汉化文本文件加载");

        foreach (var locale in serverLocales)
        {
            var count = addedLocalesCountByLang.TryGetValue(locale, out var c) ? c : 0;
            logger.Info($"[文本汉化]  - 总计: {count} 字段");
            logger.Info(string.Empty);
        }

        if (fullyCoveredFiles.Count > 0)
        {
            logger.LogWithColor($"[文本汉化]完全覆盖 文本列表:", LogTextColor.Green, LogBackgroundColor.Black);
            foreach (var f in fullyCoveredFiles)
            {
                logger.Info($"  - {f} ");
            }
            logger.Info(string.Empty);
        }

        if (partiallyCoveredFiles.Count > 0)
        {
            logger.LogWithColor($"[文本汉化]部分覆盖 文本列表:", LogTextColor.Yellow, LogBackgroundColor.Black);
            foreach (var f in partiallyCoveredFiles)
            {
                logger.Info($"  - {f.fileName} ({f.covered}/{f.total} 字段)");
            }
            logger.Info(string.Empty);
        }

        if (nonCoveredFiles.Count > 0)
        {
            logger.LogWithColor($"[文本汉化]未覆盖 文本列表:", LogTextColor.Red, LogBackgroundColor.Black);
            foreach (var f in nonCoveredFiles)
            {
                logger.Info($"  - {f}");
            }
            logger.Info(string.Empty);
        }

        return Task.CompletedTask;
    }

    // 简单的 JSONC 注释剥离：移除 // 行注释 与 /* */ 块注释
    private static string StripJsonComments(string input)
    {
        // 注意：这不是完全语法安全的实现，但足以处理常见 JSONC 场景
        var noBlock = Regex.Replace(input, @"/\*[\s\S]*?\*/", string.Empty);
        var noLine = Regex.Replace(noBlock, @"^\s*//.*$", string.Empty, RegexOptions.Multiline);
        return noLine;
    }


}