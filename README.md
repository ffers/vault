

# LanguageLab

Telegram bot for learn new words

1. Pick dictionary
2. learn new words
3. GOTO 1

### TODO:
* [x] .fb2 Words extractor (extract words in base form and export)
* [x] english words translator and export
* [x] Automatic FB2 book processing in Telegram bot
* [x] docker compose file
* [x] Database with migrations for telegram bot
* [x] Telegram bot -> dictionary list
* [ ] Telegram bot -> procedural exercises (pick dict, learn new 20 words)
* [ ] Telegram bot -> procedural exercises (test yourself, if not ok - relearn)
* [ ] Telegram bot -> procedural exercises (learn next batch of words from dictionary)
* [ ] Telegram bot -> pick new dict, learn new words except for already learned
* [ ] Telegram bot -> my stats

## Development

### Telegram bot

Use next environment variables:

* `TELEGRAM_TOKEN={your token}` - telegram token
* `DB_CONNECTION_STRING={connection string}` - postgres connection string for entity framework
* `MODERATORS_LIST={userid1,userid2,...}` - (optional) list of telegram users id allowed to upload dictionaries

**Docker compose:**  create `.env` file and fill it with that variables.

#### Features:

**Automatic Dictionary Creation from FB2 Books:**
- Send an FB2 (FictionBook 2.0) file to the bot
- Bot automatically extracts vocabulary (up to 500 unique words)
- Words are lemmatized to their base forms
- Automatic translation to Ukrainian using Google Translate
- Dictionary is created and ready for training

**Manual CSV Upload:**
- Send a CSV file with format: `word,translation` (no header)
- Each line represents a word pair

## Run

```
docker-compose up --build -d
```

Postgresql database is required.

### Database migrations

```
dotnet ef --project LanguageLab.Infrastructure --startup-project LanguageLab.TgBot migrations add {migrationName}
```

## Python Scripts

**Standalone tools for local processing:**

### `extract.py`
Extract words in base form from FB2 file and save to txt file (without translations).

### `process_fb2.py`
Complete FB2 processing with automatic translation to Ukrainian.

```bash
# Install dependencies
pip install -r requirements.txt

# Process FB2 book and generate CSV with translations
python3 process_fb2.py <fb2_file> [output_csv] [max_words]

# Example:
python3 process_fb2.py mybook.fb2 dictionary.csv 500
```

**Parameters:**
- `fb2_file` - Path to FB2 file to process
- `output_csv` - Output CSV file path (default: output.csv)
- `max_words` - Maximum number of words to process (default: 500)

**Note:** These scripts are also integrated into the Docker container and used automatically by the Telegram bot.

