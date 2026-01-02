using System.Diagnostics;
using System.Reflection;
using LanguageLab.Domain.Interfaces;
using LanguageLab.Infrastructure.Database;
using PowerBot.Lite.Attributes;
using PowerBot.Lite.Handlers;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace LanguageLab.TgBot.Handlers;

public class BotHandler : BaseHandler
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IModeratorsService _moderatorsService;

    public BotHandler(ApplicationDbContext dbContext, IModeratorsService moderatorsService)
    {
        _dbContext = dbContext;
        _moderatorsService = moderatorsService;
    }

    [MessageReaction(ChatAction.Typing)]
    [MessageHandler("^/start$")]
    public async Task Start()
    {
        var version = FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).FileVersion;

        var startMessageText = @$"LanguageLab bot.
Use command /train to start testing from first dictionary in database.
Use command /list to see all available dictionaries.

**Add new dictionary (admins only):**
- Send CSV file with word pairs (format: word,translation WITHOUT HEADER)
- Send FB2 book file - bot will auto-extract and translate words

`Bot version: {version}`";
        
        await BotClient.SendMessage(chatId: ChatId,
            text: startMessageText,
            parseMode: ParseMode.Markdown);
    }

    [MessageReaction(ChatAction.Typing)]
    [MessageHandler("^/list$")]
    public async Task ListDictionaries()
    {
        var dictionaries = _dbContext.Dictionaries.ToList();

        if (dictionaries.Count == 0)
        {
            await BotClient.SendMessage(chatId: ChatId,
                text: "No dictionaries found. Please add some first.",
                parseMode: ParseMode.Markdown);
            return;
        }

        var messageText = "Available dictionaries:\n" + string.Join("\n", dictionaries.Select(d => $"- {d.Name} ({d.WordsCount} words)"));

        await BotClient.SendMessage(chatId: ChatId,
            text: messageText,
            parseMode: ParseMode.Markdown);
    }
    
    [MessageReaction(ChatAction.Typing)]
    [MessageTypeFilter(MessageType.Document)]
    public async Task ProcessNewDictionary()
    {
        if (!_moderatorsService.IsUserModerator(User.Id))
        {
            await BotClient.SendMessage(chatId: ChatId,
                text: "You are not allowed to add new dictionaries",
                parseMode: ParseMode.Markdown);
            return;
        }

        var document = Message.Document!;

        // Check document size
        if (document.FileSize > 1024 * 1024 * 10)
        {
            await BotClient.SendMessage(chatId: ChatId,
                text: "File size exceeds the limit of 10 MB",
                parseMode: ParseMode.Markdown);
            return;
        }

        // Check if it's an FB2 file
        var isFb2 = document.FileName?.EndsWith(".fb2", StringComparison.OrdinalIgnoreCase) == true ||
                    document.MimeType == "application/xml" ||
                    document.MimeType == "text/xml";

        if (isFb2)
        {
            await ProcessFB2Book(document);
            return;
        }

        // Check file extension for CSV
        if (document.MimeType != "text/plain")
        {
            await BotClient.SendMessage(chatId: ChatId,
                text: "Unsupported file format. Only CSV (text/plain) and FB2 files are allowed",
                parseMode: ParseMode.Markdown);
            return;
        }
     
        // Download file
        var file = await BotClient.GetFile(document.FileId);
        using var memoryStream = new MemoryStream();
        await BotClient.DownloadFile(file.FilePath!, memoryStream);
        memoryStream.Position = 0;

        using var reader = new StreamReader(memoryStream);
        var content = await reader.ReadToEndAsync();

        // Parse content
        var lines = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var wordPairs = new List<LanguageLab.Domain.Entities.WordPair>();

        foreach (var line in lines)
        {
            var parts = line.Split([','], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                wordPairs.Add(new LanguageLab.Domain.Entities.WordPair
                {
                    Word = parts[0].Trim(),
                    Translation = parts[1].Trim()
                });
            }
        }

        if (wordPairs.Count == 0)
        {
            await BotClient.SendMessage(ChatId, "Не вдалося знайти жодної пари слів у файлі.");
            return;
        }

        // Create dictionary
        var dictionary = new LanguageLab.Domain.Entities.Dictionary
        {
            Name = document.FileName ?? "Новий словник",
            WordsCount = wordPairs.Count,
            Words = wordPairs
        };

        _dbContext.Dictionaries.Add(dictionary);
        await _dbContext.SaveChangesAsync();

        await BotClient.SendMessage(ChatId, $"Словник '{dictionary.Name}' успішно створено! Додано {wordPairs.Count} слів.");
    }

    private async Task ProcessFB2Book(Telegram.Bot.Types.Document document)
    {
        await BotClient.SendMessage(ChatId, "📚 Обробляю FB2 книгу... Це може зайняти кілька хвилин.");

        // Create temp directory if it doesn't exist
        var tempDir = Path.Combine(Path.GetTempPath(), "languagelab");
        Directory.CreateDirectory(tempDir);

        var fb2FilePath = Path.Combine(tempDir, $"{Guid.NewGuid()}.fb2");
        var csvFilePath = Path.Combine(tempDir, $"{Guid.NewGuid()}.csv");

        try
        {
            // Download FB2 file
            var file = await BotClient.GetFile(document.FileId);
            await using (var fileStream = System.IO.File.Create(fb2FilePath))
            {
                await BotClient.DownloadFile(file.FilePath!, fileStream);
            }

            await BotClient.SendMessage(ChatId, "⚙️ Екстрактую слова та перекладаю...");

            // Run Python script to process FB2
            var processStartInfo = new ProcessStartInfo
            {
                FileName = "python3",
                Arguments = $"process_fb2.py \"{fb2FilePath}\" \"{csvFilePath}\" 500",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(processStartInfo);
            if (process == null)
            {
                await BotClient.SendMessage(ChatId, "❌ Помилка запуску обробника FB2");
                return;
            }

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                await BotClient.SendMessage(ChatId, $"❌ Помилка обробки FB2:\n{error}");
                return;
            }

            // Check if CSV file was created
            if (!System.IO.File.Exists(csvFilePath))
            {
                await BotClient.SendMessage(ChatId, "❌ Не вдалося створити словник з книги");
                return;
            }

            // Read CSV and create dictionary
            var csvContent = await System.IO.File.ReadAllTextAsync(csvFilePath);
            var lines = csvContent.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            var wordPairs = new List<LanguageLab.Domain.Entities.WordPair>();

            foreach (var line in lines)
            {
                var parts = line.Split([','], 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    wordPairs.Add(new LanguageLab.Domain.Entities.WordPair
                    {
                        Word = parts[0].Trim().Trim('"'),
                        Translation = parts[1].Trim().Trim('"')
                    });
                }
            }

            if (wordPairs.Count == 0)
            {
                await BotClient.SendMessage(ChatId, "❌ Не вдалося знайти слова у книзі");
                return;
            }

            // Create dictionary
            var bookName = document.FileName ?? "FB2 Book";
            var dictionary = new LanguageLab.Domain.Entities.Dictionary
            {
                Name = bookName,
                WordsCount = wordPairs.Count,
                Words = wordPairs
            };

            _dbContext.Dictionaries.Add(dictionary);
            await _dbContext.SaveChangesAsync();

            await BotClient.SendMessage(ChatId,
                $"✅ Словник '{dictionary.Name}' успішно створено!\n" +
                $"📖 Додано {wordPairs.Count} слів з автоматичним перекладом.");
        }
        catch (Exception ex)
        {
            await BotClient.SendMessage(ChatId, $"❌ Помилка обробки FB2: {ex.Message}");
        }
        finally
        {
            // Cleanup temp files
            try
            {
                if (System.IO.File.Exists(fb2FilePath))
                    System.IO.File.Delete(fb2FilePath);
                if (System.IO.File.Exists(csvFilePath))
                    System.IO.File.Delete(csvFilePath);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [MessageReaction(ChatAction.Typing)]
    [MessageHandler("^/train")]
    public async Task Train()
    {
        var wordId = 5;
        var word = "cat";
        var translation = "кіт";
        var translations = new List<string> { translation, "пес", "авто", "телефон", "місто", "криниця" }.Shuffle().ToList();
        
        var messageText = @$"Вибери правильний варіант для слова:
**Cat**";

        var keyboardMarkup = new InlineKeyboardMarkup(new List<List<InlineKeyboardButton>> {
            new List<InlineKeyboardButton> {
                InlineKeyboardButton.WithCallbackData(translations[0], $"train_true_{wordId}"),
                InlineKeyboardButton.WithCallbackData(translations[1], $"train_false_{wordId}"),
            },
            new List<InlineKeyboardButton> {
                InlineKeyboardButton.WithCallbackData(translations[2], $"train_false_{wordId}"),
                InlineKeyboardButton.WithCallbackData(translations[3], $"train_false_{wordId}"),
            },
            new List<InlineKeyboardButton> {
                InlineKeyboardButton.WithCallbackData(translations[4], $"train_false_{wordId}"),
                InlineKeyboardButton.WithCallbackData(translations[5], $"train_false_{wordId}"),
            }
        });

        await BotClient.SendMessage(chatId: ChatId,
            text: messageText,
            replyMarkup: keyboardMarkup,
            parseMode: ParseMode.Markdown);
    }
    
    [MessageReaction(ChatAction.Typing)]
    [CallbackQueryHandler("^train_")]
    public async Task DictWordClicked()
    {
        await BotClient.EditMessageReplyMarkup(ChatId, MessageId, null);
        
        // Parse user id
        var oldWordId = long.Parse(CallbackQuery.Data!.Split('_').Last());

        // Parse result
        var result = CallbackQuery.Data
            .Split('_')[1] == "true";
        var resultStr = result ? "Правильно" : "Неправильно";

        var wordId = 5;
        var word = "cat";
        var translation = "кіт";
        var translations = new List<string> { translation, "пес", "авто", "телефон", "місто", "криниця" }.Shuffle().ToList();

        var messageText = @$"{resultStr}

Вибери правильний варіант для слова:
**Cat**";

        var keyboardMarkup = new InlineKeyboardMarkup(new List<List<InlineKeyboardButton>> {
            new () {
                InlineKeyboardButton.WithCallbackData(translations[0], $"train_true_{wordId}"),
                InlineKeyboardButton.WithCallbackData(translations[1], $"train_false_{wordId}"),
            },
            new () {
                InlineKeyboardButton.WithCallbackData(translations[2], $"train_false_{wordId}"),
                InlineKeyboardButton.WithCallbackData(translations[3], $"train_false_{wordId}"),
            },
            new () {
                InlineKeyboardButton.WithCallbackData(translations[4], $"train_false_{wordId}"),
                InlineKeyboardButton.WithCallbackData(translations[5], $"train_false_{wordId}"),
            }
        });

        await BotClient.SendMessage(chatId: ChatId,
            text: messageText,
            replyMarkup: keyboardMarkup,
            parseMode: ParseMode.Markdown);
    }
}
