import { DependencyContainer } from "tsyringe";

import { IPreSptLoadMod } from "@spt/models/external/IPreSptLoadMod";
import { IPostDBLoadMod } from "@spt/models/external/IPostDBLoadMod";
import { DatabaseServer } from "@spt/servers/DatabaseServer";
import { LogTextColor } from "@spt/models/spt/logging/LogTextColor";
import { ILogger } from "@spt/models/spt/utils/ILogger";
import { JsonUtil } from "@spt/utils/JsonUtil";
import * as path from "path";

import JSON5 from "json5";
import { jsonc } from "jsonc"

const fs = require('fs');
const modPath = path.normalize(path.join(__dirname, '..'));

class SimpleTranslator implements IPreSptLoadMod, IPostDBLoadMod {
    mod: string
    logger: ILogger
    jsonUtil: JsonUtil
        
    constructor() {
        this.mod = "simple-translator-1.1.1"; 
    }

    public loadFiles(dirPath, extName, cb) {
        if (!fs.existsSync(dirPath)) return;
        const validExtensions = new Set(extName.map(e => e.toLowerCase()));
        const dir = fs.readdirSync(dirPath, { withFileTypes: true });

        dir.forEach((item) => {
            const itemPath = path.normalize(`${dirPath}/${item.name}`);
            const ext = path.extname(item.name).toLowerCase();

            if (item.isDirectory()) {
                this.loadFiles(itemPath, extName, cb);
            } else if (validExtensions.has(ext)) {
                let fileContent;
                const fileData = fs.readFileSync(itemPath, "utf-8");

                try {
                    if (ext === ".jsonc") {
                        fileContent = jsonc.parse(fileData);
                    } else if (ext === ".json5") {
                        fileContent = JSON5.parse(fileData);
                    } else if (ext === ".json") {
                        fileContent = JSON.parse(fileData);
                    }
                } catch (error) {
                    return;
                }

                if(ext === ".json" || ext === ".jsonc" || ext === ".json5"){
                    cb(fileContent, itemPath, ext);
                }
            }
        });
    }

    public preSptLoad(container: DependencyContainer): void {
        this.logger = container.resolve<ILogger>("WinstonLogger");
        this.jsonUtil = container.resolve<JsonUtil>("JsonUtil");
    }

    public postDBLoad(container: DependencyContainer): void {
        const databaseServer = container.resolve("DatabaseServer");
        const tables = databaseServer.getTables();
    
        let loadedFileCount = 0;
        const serverLocales = ['ch'];
        const addedLocales = {};
    
        // 用于记录覆盖情况的文件
        const partiallyCoveredFiles = [];
        const nonCoveredFiles = [];
        const fullyCoveredFiles = [];
    
        for (const locale of serverLocales) {
            const localePath = `${modPath}/db/locales/${locale}`;
            if (!fs.existsSync(localePath)) {
                this.logger.info(`[文本汉化]未找到汉化文件夹目录: ${locale}, 请检查是否在 user/mods/zzzzz-simple-translator/db/locales 中已经创建 ch 文件夹`);
                continue;
            }
    
            this.loadFiles(localePath, [".json", ".jsonc", ".json5"], (fileContent, filePath, ext) => {
                loadedFileCount += 1;
                if (Object.keys(fileContent).length < 1) return;
    
                const totalFields = Object.keys(fileContent).length;
                let coveredFields = 0;
    
                for (const currentItem in fileContent) {
                    // 检测 en 中是否存在对应的 ID
                    const englishLocale = tables.locales.global["en"];
                    if (englishLocale && currentItem in englishLocale) {
                        // 如果存在，覆盖 ch 的对应项
                        tables.locales.global[locale][currentItem] = fileContent[currentItem];
                        if (!addedLocales[locale]) addedLocales[locale] = {};
                        addedLocales[locale][currentItem] = fileContent[currentItem];
                        coveredFields += 1;
                    }
                }
    
                // 根据覆盖情况分类文件
                const fileName = path.basename(filePath); // 只获取文件名
                if (coveredFields === 0) {
                    nonCoveredFiles.push(fileName); // 没有任何字段被覆盖
                } else if (coveredFields < totalFields) {
                    partiallyCoveredFiles.push({ fileName, coveredFields, totalFields }); // 部分字段被覆盖
                } else {
                    fullyCoveredFiles.push(fileName); // 所有字段被覆盖
                }
            });
        }
    
        // 总计加载的文件数量
        this.logger.info(`[文本汉化]总计 [${loadedFileCount}] 个汉化文本文件加载`);
    
        // 统计字段覆盖信息
        for (const locale in addedLocales) {
            this.logger.info(`[文本汉化]  - 总计: ${Object.keys(addedLocales[locale]).length} 字段`);
            this.logger.info(``);
        }
    
        // 输出覆盖情况的文件信息
        if (fullyCoveredFiles.length > 0) {
            this.logger.logWithColor(`[文本汉化]完全覆盖 文本列表:`, LogTextColor.GREEN);
            fullyCoveredFiles.forEach((file) => this.logger.info(`  - ${file} `));
            this.logger.info(``);
        }

        if (partiallyCoveredFiles.length > 0) {
            this.logger.logWithColor(`[文本汉化]部分覆盖 文本列表:`, LogTextColor.YELLOW);
            partiallyCoveredFiles.forEach((file) => {
                this.logger.info(`  - ${file.fileName} (${file.coveredFields}/${file.totalFields} 字段)`);
            });
            this.logger.info(``);
        }
    
        if (nonCoveredFiles.length > 0) {
            this.logger.logWithColor(`[文本汉化]未覆盖 文本列表:`, LogTextColor.RED);
            nonCoveredFiles.forEach((file) =>
                 this.logger.info(`  - ${file}`));
            this.logger.info(``);
        }
    }
}

module.exports = { mod: new SimpleTranslator() };
