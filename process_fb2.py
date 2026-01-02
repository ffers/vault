#!/usr/bin/env python3
"""
Process FB2 files to extract vocabulary and translate to Ukrainian.
Generates CSV file with word pairs for LanguageLab bot.
"""

import re
import sys
import csv
from pathlib import Path
from typing import Set, Dict, List, Tuple
from lxml import etree
import nltk
from nltk.stem import WordNetLemmatizer
from nltk.corpus import wordnet, stopwords
from nltk.tokenize import word_tokenize
from nltk import pos_tag
from functools import lru_cache
from tqdm import tqdm
from translate import Translator

# Download required NLTK data
nltk.download('punkt', quiet=True)
nltk.download('wordnet', quiet=True)
nltk.download('averaged_perceptron_tagger_eng', quiet=True)
nltk.download('stopwords', quiet=True)

# Initialize tools
lemmatizer = WordNetLemmatizer()
stop_words = set(stopwords.words('english'))
translator = Translator(from_lang="en", to_lang="uk")

# Common prefixes and suffixes for filtering
prefixes = {'one', 'two', 'three', 'four', 'five', 'six', 'seven', 'eight', 'nine', 'ten'}
suffixes = {'th', 'st', 'nd', 'rd'}

# Cache for lemmas and translations
lemma_cache: Dict[str, str] = {}
translation_cache: Dict[str, str] = {}


def get_wordnet_pos(treebank_tag: str) -> str:
    """Map POS tag to WordNet POS tag."""
    if treebank_tag.startswith('J'):
        return wordnet.ADJ
    elif treebank_tag.startswith('V'):
        return wordnet.VERB
    elif treebank_tag.startswith('N'):
        return wordnet.NOUN
    elif treebank_tag.startswith('R'):
        return wordnet.ADV
    else:
        return wordnet.NOUN  # default


@lru_cache(maxsize=10000)
def get_lemma(word: str) -> str:
    """Get the lemma (base form) of a word with POS tagging."""
    if not word or len(word) < 2:
        return word

    if word in lemma_cache:
        return lemma_cache[word]

    pos_tag_result = pos_tag([word])
    if not pos_tag_result:
        lemma_cache[word] = word
        return word

    treebank_tag = pos_tag_result[0][1]
    wordnet_pos = get_wordnet_pos(treebank_tag)

    lemma = lemmatizer.lemmatize(word, pos=wordnet_pos)

    if lemma == word and wordnet_pos != wordnet.NOUN:
        lemma = lemmatizer.lemmatize(word)

    lemma_cache[word] = lemma.lower()
    return lemma_cache[word]


def clean_word(word: str) -> str:
    """Clean word from punctuation and convert to lowercase."""
    cleaned = re.sub(r'[^\w\s-]', '', word.lower())
    cleaned = cleaned.strip("-_'\"")
    return cleaned


def split_compound_word(word: str) -> Set[str]:
    """Split compound words and return individual parts."""
    parts = set()
    hyphen_parts = word.split('-')

    for part in hyphen_parts:
        clean_part = clean_word(part)

        if len(clean_part) < 2 or not clean_part:
            continue

        clean_part = re.sub(r'[0-9]+', '', clean_part)

        skip = False
        for prefix in prefixes:
            if clean_part.startswith(prefix):
                remaining = clean_part[len(prefix):]
                if remaining in suffixes or not remaining:
                    skip = True
                    break

        if not skip and clean_part.isalpha() and len(clean_part) > 2:
            parts.add(clean_part)

    return parts


def get_word_base_forms(word: str) -> Set[str]:
    """Get base forms of a word including lemmas and split parts."""
    base_forms = set()
    cleaned_word = clean_word(word)

    if not cleaned_word:
        return base_forms

    lemma = get_lemma(cleaned_word)
    if lemma and len(lemma) > 1:
        base_forms.add(lemma)

    if '-' in cleaned_word:
        parts = split_compound_word(cleaned_word)
        for part in parts:
            part_lemma = get_lemma(part)
            if part_lemma and len(part_lemma) > 1:
                base_forms.add(part_lemma)
    else:
        if lemma and len(lemma) > 1:
            base_forms.add(lemma)

    return base_forms


def is_valid_word(word: str) -> bool:
    """Check if a word is valid for inclusion in the vocabulary."""
    cleaned_word = clean_word(word)

    if not cleaned_word or len(cleaned_word) < 2:
        return False

    if cleaned_word in stop_words:
        return False

    if len(cleaned_word) > 30:
        return False

    if sum(1 for c in cleaned_word if c.isdigit() or not c.isalpha()) > 3:
        return False

    if not cleaned_word[0].isalpha():
        return False

    if cleaned_word in {'ll', 've', 're', 't', 's', 'd', 'm'}:
        return False

    vowels = set('aeiouy')
    if all(c in vowels for c in cleaned_word) or all(c not in vowels for c in cleaned_word):
        if len(cleaned_word) > 3:
            return False

    return True


def extract_text_from_fb2(fb2_file: str) -> str:
    """Extract text content from FB2 file."""
    print("Parsing FB2 file...")
    try:
        tree = etree.parse(fb2_file)
        root = tree.getroot()

        ns = {'fb': 'http://www.gribuser.ru/xml/fictionbook/2.0'}

        text_elements = []

        bodies = root.xpath('//fb:body', namespaces=ns)
        for body in bodies:
            paragraphs = body.xpath('.//fb:p', namespaces=ns)
            for p in paragraphs:
                if p.text:
                    text_elements.append(p.text)

            sections = body.xpath('.//fb:section', namespaces=ns)
            for section in sections:
                text_nodes = section.xpath('.//text()')
                text_elements.extend(text_nodes)

        total_text = ' '.join(text_elements)
        total_text = total_text.replace('—', ' ')
        total_text = re.sub(r'\s+', ' ', total_text)

        return total_text

    except Exception as e:
        print(f"Error parsing FB2 file: {e}")
        return ""


def process_word(word: str, vocabulary: Set[str]) -> None:
    """Process a single word and add its base form to vocabulary."""
    if not is_valid_word(word):
        return

    cleaned_word = clean_word(word)
    if not cleaned_word:
        return

    base_forms = get_word_base_forms(cleaned_word)

    for base_form in base_forms:
        if is_valid_word(base_form) and len(base_form) > 1:
            vocabulary.add(base_form)


def translate_word(word: str, max_retries: int = 3) -> str:
    """Translate a word to Ukrainian with caching and retry logic."""
    if word in translation_cache:
        return translation_cache[word]

    for attempt in range(max_retries):
        try:
            translation = translator.translate(word)
            translation_cache[word] = translation
            return translation
        except Exception as e:
            if attempt < max_retries - 1:
                print(f"  Retry translating '{word}' (attempt {attempt + 1}/{max_retries})")
                continue
            else:
                print(f"  Failed to translate '{word}': {e}")
                # Return original word if translation fails
                return word

    return word


def translate_vocabulary(vocabulary: List[str], batch_size: int = 10) -> List[Tuple[str, str]]:
    """Translate vocabulary words to Ukrainian."""
    print("\nTranslating words to Ukrainian...")
    word_pairs = []

    for i in tqdm(range(0, len(vocabulary), batch_size), desc="Translating batches"):
        batch = vocabulary[i:i + batch_size]

        for word in batch:
            translation = translate_word(word)
            word_pairs.append((word, translation))

    return word_pairs


def main():
    """Main function to process FB2 file and generate CSV with translations."""
    if len(sys.argv) < 2:
        print("Usage: python process_fb2.py <fb2_file> [output_csv] [max_words]")
        print("  fb2_file    - Path to FB2 file to process")
        print("  output_csv  - Output CSV file path (default: output.csv)")
        print("  max_words   - Maximum number of words to process (default: 500)")
        sys.exit(1)

    fb2_file = sys.argv[1]
    output_file = sys.argv[2] if len(sys.argv) > 2 else "output.csv"
    max_words = int(sys.argv[3]) if len(sys.argv) > 3 else 500

    # Check if FB2 file exists
    if not Path(fb2_file).exists():
        print(f"Error: File '{fb2_file}' not found")
        sys.exit(1)

    # Extract text from FB2
    text = extract_text_from_fb2(fb2_file)
    if not text:
        print("No text extracted from the FB2 file.")
        sys.exit(1)

    print(f"Extracted {len(text)} characters of text.")

    # Tokenize text
    print("Tokenizing text...")
    words = word_tokenize(text)

    # Process words and build vocabulary
    print("Processing words...")
    vocabulary = set()

    for word in tqdm(words, desc="Extracting vocabulary"):
        if '-' in word:
            process_word(word, vocabulary)
            parts = split_compound_word(word)
            for part in parts:
                process_word(part, vocabulary)
        else:
            process_word(word, vocabulary)

    # Sort vocabulary alphabetically
    sorted_vocabulary = sorted(vocabulary)

    # Limit to max_words
    if len(sorted_vocabulary) > max_words:
        print(f"\nLimiting vocabulary to {max_words} most common words...")
        sorted_vocabulary = sorted_vocabulary[:max_words]

    print(f"\nTotal unique words: {len(sorted_vocabulary)}")

    # Translate words
    word_pairs = translate_vocabulary(sorted_vocabulary)

    # Save to CSV
    print(f"\nSaving {len(word_pairs)} word pairs to {output_file}...")
    with open(output_file, 'w', encoding='utf-8', newline='') as f:
        writer = csv.writer(f)
        for word, translation in word_pairs:
            writer.writerow([word, translation])

    # Print statistics
    print("\nProcessing complete!")
    print(f"Total words processed: {len(words)}")
    print(f"Unique base forms found: {len(sorted_vocabulary)}")
    print(f"Word pairs saved to: {output_file}")
    print(f"\nYou can now upload '{output_file}' to the LanguageLab bot!")


if __name__ == "__main__":
    main()
