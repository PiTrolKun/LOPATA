# 1C in LOPATA

Open Creation → Vibe Coding → 1C. This scenario helps you write small modules and understand source code. You can start without installing 1C.

1. Create or reopen a project in the Projects panel. Collapse the panel with ☰ when you need more space.
2. Paste source code, add a BSL/OS file, or import a folder of BSL/XML text materials. Imported materials are read-only snapshots; original files remain untouched.
3. Select a range in a file tab and choose “Use in task”. With no selection, the entire file is used. You can also start with a task and no source.
4. Choose “Build a solution” or “Understand” and describe your goal. Starter prompts help you begin. Learning mode includes small exercises, hints and separate sample solutions.
5. Choose a text model using AI. The scenario uses LOPATA’s shared model library and its own selection settings. It does not change the reader’s model choice. Prepare or download a model through the standard model window.
6. Use “Prepare checker” to obtain BSL Language Server. This separate component includes its Java runtime; it does not install Java in Windows.
7. Select “Prepare proposal”. The response is saved before actual static analysis. Up to two correction attempts may follow static errors, with all variants preserved.
8. Compare the original range with the proposal. Edit the proposed code, copy clean code, export a new copy, or create an owned project file.

“Apply to my file” only edits files created by this scenario in this project. A changed task or source requires a new proposal. Importing a file or changing its name or origin flag never grants write access. Export refuses to overwrite an existing file and grants no later permission to edit the external copy automatically.

If an error occurs after you paste code into 1C, use the action beside that proposal and paste the error. Optionally include the code you actually tried. The message is linked to that proposal’s code version. Explicitly request a new proposal; previous versions remain available.

## Understanding checks

- A model response is a proposal for human review.
- A static check refers to one exact code version. Edit the text and the check becomes stale.
- The 1C platform is not executed. Static analysis does not prove your configuration contains the referenced objects or attributes, nor that the code runs successfully in a database.
- Learning solutions are text examples. Expected results are not presented as executed tests.

Owned files currently support BSL, OS, TXT and MD. CF/CFE/EPF/ERF require a genuine external builder and cannot be made by renaming text. OneScript execution is not provided; it requires a separately verified isolation profile.

## Saving and resuming

Changes save automatically and with Ctrl+S. Projects retain tasks, source snapshots, proposals, diagnostics and processing checkpoints. Pause uses the shared background controller and releases model resources. Resume reuses completed stages. Canceling or closing never applies a proposal automatically. Background processing can continue while LOPATA is in the tray.

Owned files use immutable revisions. The project registry is written atomically with a last-good backup. External changes produce a conflict and are preserved. Do not manually remove the project folder or its ownership registry. Projects are local; importing arbitrary portable archives with write permissions is not provided yet.

## Extending the scenario

The help buttons open this module’s source and DEVELOPMENT.en.md. That guide lists real extension points, a working folder adapter example and verification commands. This first cycle is an open working foundation, not a complete replacement for Configurator or EDT.
