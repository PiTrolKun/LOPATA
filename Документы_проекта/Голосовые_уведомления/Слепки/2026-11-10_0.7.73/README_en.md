# Speech asset creation — requirements and examples

Open **Processing / utilities → Audio tools → Create assets** in LOPATA.
The utility turns Markdown list items into separate audio recordings for notifications, games, interfaces and other uses.

## Requirements

- An installed copy of LOPATA and the components required by your chosen text-to-speech model. Select the model and voice in the utility; component preparation and licence acknowledgement use the standard application workflow.
- A regular UTF-8 text file with the `.md` extension.
- A model and voice that support the intended language. Memory requirements and CPU/GPU availability depend on the model; the utility does not impose one universal graphics-card requirement.
- Free space for the project, source WAV takes and exported ZIP. A compact export format reduces the archive size; the project's working recordings are stored separately.

## Input format

**One list item becomes one future recording.** Bulleted and numbered lists are supported. For example:

```markdown
# Notifications

- Ready to start.
- The file has been saved.
- An error occurred. Check the settings.
```

Headings define groups and are not spoken. Text outside lists and code blocks do not become utterances. A nested list item produces a separate recording; the parent item's own paragraphs remain part of the parent's text. Markdown formatting and list markers are removed from the spoken text.

`LOPATA_Notifications_and_Statuses_en.md` is the full English example; `LOPATA_уведомления_и_статусы_ru.md` is the Russian example. Each contains 1,529 items. Copy a few items into your own file for a small trial. The examples contain no audio and do not start generation automatically.

The example headings include `voice.…` identifiers linking the two languages. Ordinary user lists do not require these identifiers. When translating LOPATA notifications, preserve them: a pronunciation edit must not change an utterance's connection to its event.

## Generation and review

1. Choose the `.md` file, then select the model, language, voice and settings.
2. Generate remaining lines as a batch or generate individual lines manually. Batch mode makes up to three retries after an initial failure, then proceeds to the next line.
3. Edit text directly in a row when a model needs a different spelling or an expanded abbreviation. The original text and previous takes are retained. After editing, generate a recording matching the current text.
4. Listen to the alternatives and select **one** take. Selecting another clears the previous selection; older recordings remain in the project.
5. Confirm the line. `V` means confirmed; `−` means review is needed. The selected take must match the current text. Changing the text or selection requires confirmation again.

Batch and manual generation differ in how the list is processed; both require confirmation. Keep service identifiers out of the text that should be spoken.

## Projects and resuming

The Projects button opens the internal catalogue of saved sets. A request saves its project automatically; you can also keep and rename a project manually. Pause, stop and reopening allow work to continue using saved takes and verified completed parts.

The selected model is warmed up and remains loaded while you use the scenario, including idle time and the system tray. Leaving the scenario, an explicit pause or emergency STOP releases it. Do not manually alter an active project's internal files.

## Export

Every included line needs one selected, confirmed take matching its current text. Exclude lines you do not need. The ZIP contains selected recordings and a `manifest.json` listing their IDs, texts, language, voice, models and hashes. Metadata is also embedded in the audio files.

Opus, MP3, FLAC and WAV are available. The default is Opus at 64 kbps. Edit filenames in their rows; they must be valid Windows filenames, and final names must be unique. Extract the archive and place the recordings in your target application as needed.

Confirmation and technical validation do not replace listening. Before distributing recordings, consider the terms of the selected model and voice.

Use the RU / EN examples button to open and copy these files. If LOPATA is installed in a protected Windows folder, save your edited list in a personal folder.

---
LOPATA · 0.7.71-dev · utility help and examples · 2026-11-10
