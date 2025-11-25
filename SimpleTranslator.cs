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
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

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
    LocaleService localeService,
    DatabaseService databaseService) : IOnLoad
{
    // 预编译正则表达式以提高性能
    private static readonly Regex JsonTrailingCommaRegex = new(@",\s*(\}|\])", RegexOptions.Compiled);
    private static readonly Regex JsonBlockCommentRegex = new(@"/\*[\s\S]*?\*/", RegexOptions.Compiled);
    private static readonly Regex JsonLineCommentRegex = new(@"^\s*//.*$", RegexOptions.Compiled | RegexOptions.Multiline);

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
            var localePath = System.IO.Path.Combine(modPath, "db", "locales", locale);
            if (!Directory.Exists(localePath))
            {
                logger.Info($"[文本汉化]未找到汉化文件夹目录: {locale}, 请检查是否在 SPT/user/mods/SimpleTranslatorCS/db/locales 中已经创建 ch 文件夹");
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(localePath, "*.*", SearchOption.AllDirectories)
                         .Where(f => new[] { ".json", ".json5", ".jsonc" }.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant()))
                         .Where(f => !f.Contains("\\dialogue\\") && !f.Contains("/dialogue/")))  // 排除 dialogue 目录
            {
                loadedFileCount++;

                var content = ParseFileContent(file, logger);
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

                var fileName = System.IO.Path.GetFileName(file);
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

        LogStatistics(loadedFileCount, serverLocales, addedLocalesCountByLang, fullyCoveredFiles, partiallyCoveredFiles, nonCoveredFiles, logger);

        // 处理 dialogue 本地化
        LoadDialogueLocalizations(modPath, serverLocales, logger);

        return Task.CompletedTask;
    }

    /// <summary>
    /// 解析文件内容，支持 JSON、JSON5 和 JSONC 格式
    /// </summary>
    private static Dictionary<string, string>? ParseFileContent(string filePath, ISptLogger<SimpleTranslator> logger)
    {
        try
        {
            var ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
            var raw = File.ReadAllText(filePath);

            return ext switch
            {
                ".json" => JsonSerializer.Deserialize<Dictionary<string, string>>(raw),
                ".json5" => ParseJsoncContent(raw),
                ".jsonc" => ParseJsoncContent(raw),
                _ => null
            };
        }
        catch (Exception ex)
        {
            logger.Error($"[文本汉化]解析文件失败: {filePath}, 错误: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 安全地解析 JSONC 内容，先剥离注释再移除尾随逗号
    /// </summary>
    private static Dictionary<string, string>? ParseJsoncContent(string rawContent)
    {
        var noComments = StripJsonComments(rawContent);
        var normalized = JsonTrailingCommaRegex.Replace(noComments, "$1");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(normalized);
    }

    /// <summary>
    /// 安全地剥离 JSON/JSONC 中的注释
    /// 这个实现通过逐字符解析来避免正则表达式在复杂JSON结构中的问题
    /// </summary>
    private static string StripJsonComments(string input)
    {
        var result = new System.Text.StringBuilder();
        var inString = false;
        var inBlockComment = false;
        var inLineComment = false;
        var escapeNext = false;

        for (int i = 0; i < input.Length; i++)
        {
            char current = input[i];
            char next = i + 1 < input.Length ? input[i + 1] : '\0';

            if (escapeNext)
            {
                result.Append(current);
                escapeNext = false;
                continue;
            }

            if (current == '\\' && inString)
            {
                result.Append(current);
                escapeNext = true;
                continue;
            }

            if (!inString && !inBlockComment && !inLineComment)
            {
                if (current == '/' && next == '/')
                {
                    inLineComment = true;
                    i++; // 跳过下一个字符 '/'
                    continue;
                }
                if (current == '/' && next == '*')
                {
                    inBlockComment = true;
                    i++; // 跳过下一个字符 '*'
                    continue;
                }
                if (current == '"')
                {
                    inString = true;
                    result.Append(current);
                    continue;
                }
            }
            else if (inString)
            {
                if (current == '"')
                {
                    inString = false;
                }
                result.Append(current);
                continue;
            }
            else if (inBlockComment)
            {
                if (current == '*' && next == '/')
                {
                    inBlockComment = false;
                    i++; // 跳过下一个字符 '/'
                    continue;
                }
                // 注释内容不添加到结果中
                continue;
            }
            else if (inLineComment)
            {
                if (current == '\n' || current == '\r')
                {
                    inLineComment = false;
                    result.Append(current); // 保留换行符以保持行号
                }
                // 注释内容不添加到结果中
                continue;
            }

            result.Append(current);
        }

        return result.ToString();
    }

    /// <summary>
    /// 记录和显示统计信息
    /// </summary>
    private static void LogStatistics(
        int loadedFileCount,
        string[] serverLocales,
        Dictionary<string, int> addedLocalesCountByLang,
        List<string> fullyCoveredFiles,
        List<(string fileName, int covered, int total)> partiallyCoveredFiles,
        List<string> nonCoveredFiles,
        ISptLogger<SimpleTranslator> logger)
    {
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
    }

    /// <summary>
    /// 加载并应用 dialogue 本地化文本
    /// </summary>
    private void LoadDialogueLocalizations(string modPath, string[] serverLocales, ISptLogger<SimpleTranslator> logger)
    {
        try
        {
            foreach (var locale in serverLocales)
            {
                var dialoguePath = System.IO.Path.Combine(modPath, "db", "locales", locale, "dialogue");
                if (!Directory.Exists(dialoguePath))
                {
                    continue;
                }

                var dialogueFiles = Directory.EnumerateFiles(dialoguePath, "*.json*", SearchOption.AllDirectories)
                    .Where(f => new[] { ".json", ".json5", ".jsonc" }.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant()))
                    .ToList();

                var updatedDialogueCount = 0;
                var updatedTextCount = 0;

                // 首先收集所有文件的内容
                var dialogueLocalizations = new Dictionary<string, string>();
                foreach (var file in dialogueFiles)
                {
                    var content = ParseFileContent(file, logger);
                    if (content == null || content.Count == 0)
                    {
                        continue;
                    }

                    // 合并所有文件的文本（后面的文件会覆盖前面的）
                    foreach (var kvp in content)
                    {
                        dialogueLocalizations[kvp.Key] = kvp.Value;
                    }
                }

                if (dialogueLocalizations.Count == 0)
                {
                    continue;
                }

                var dialogue = databaseService.GetTemplates().Dialogue;
                if (dialogue?.Elements == null)
                {
                    logger.Warning("[Dialogue本地化]无法获取 dialogue 数据");
                    continue;
                }

                // 遍历所有 dialogue elements，只更新目标语言的文本
                foreach (var element in dialogue.Elements)
                {
                    if (element.LocalizationDictionary == null)
                    {
                        element.LocalizationDictionary = new Dictionary<string, Dictionary<string, string>>();
                    }

                    if (!element.LocalizationDictionary.TryGetValue(locale, out var localeDict))
                    {
                        localeDict = new Dictionary<string, string>();
                        element.LocalizationDictionary[locale] = localeDict;
                    }

                    var originalCount = localeDict.Count;
                    var textsUpdated = 0;

                    // 只更新匹配的文本ID（如果 element 的 localization 中有这些文本ID）
                    foreach (var kvp in dialogueLocalizations)
                    {
                        // 检查当前 element 的 localization 中是否存在这个文本ID（任意语言都可以）
                        bool textExists = false;
                        foreach (var existingLocale in element.LocalizationDictionary.Keys)
                        {
                            if (element.LocalizationDictionary[existingLocale].ContainsKey(kvp.Key))
                            {
                                textExists = true;
                                break;
                            }
                        }

                        // 如果文本ID存在于任意语言的 localization 中，则更新目标语言的翻译
                        if (textExists)
                        {
                            localeDict[kvp.Key] = kvp.Value;
                            textsUpdated++;
                        }
                    }

                    if (textsUpdated > 0)
                    {
                        updatedDialogueCount++;
                        updatedTextCount += textsUpdated;
                    }
                }

                if (updatedDialogueCount > 0)
                {
                    logger.Info($"[Dialogue本地化] {locale}: 更新了 {updatedDialogueCount} 个对话元素，共 {updatedTextCount} 条文本");
                }
            }
        }
        catch (Exception ex)
        {
            logger.Error($"[Dialogue本地化]处理失败: {ex.Message}");
        }
    }
}