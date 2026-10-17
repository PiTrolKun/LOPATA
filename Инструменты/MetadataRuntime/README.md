# ExifTool runtime for file metadata

Pinned official Windows x64 package: ExifTool 13.59.
Homepage: https://exiftool.org/ (the author's repository also hosts the page:
https://github.com/exiftool/exiftool/blob/master/html/index.html).
Distribution: https://downloads.sourceforge.net/project/exiftool/exiftool-13.59_64.zip
Official checksums: https://downloads.sourceforge.net/project/exiftool/checksums-13.59.txt
Archive SHA256: 44b512b25af500724ba579d0a53c8fc5851628b692dd5e5d94ae4a15c2cba9ec.

Run `./prepare.ps1` from PowerShell. An existing correctly hashed download is
reused. The default target is Runtime/Metadata/ExifTool-13.59/exiftool-13.59_64.
No system install, registry change or PATH modification is performed.
The original launcher is renamed from exiftool(-k).exe to exiftool.exe as the
upstream README directs; its bytes and all other original files are retained.
The manifest contains 510 file paths, lengths and SHA256 values, embedded into
the app as AIHub.MetadataExifToolManifest. MSBuild delivers the entire directory
as MetadataRuntime and fails clearly when the prepared runtime is absent.

Retain exiftool_files/LICENSE, readme_windows.txt and Licenses_Strawberry_Perl.zip.
The archive covers the bundled interpreter and libraries. The ExifTool Perl
modules and exiftool.pl are included as source. The launcher is CC0; ExifTool
uses Perl terms and the Windows package also carries GPL-3.0 LICENSE. Preserve
the complete original notices rather than labelling the whole dependency MIT.
The optional metadata.exiftool entry uses the existing component acknowledgement
gate and does not change the terms or receipts of unrelated dependencies.

The utility disables ExifTool configuration loading, uses UTF-8 argument files
and separate raw UTF-8 value files, and runs only local files through owned
processes with bounded output, cancellation and a timeout. Raw value files
avoid ExifTool JSON import's non-BMP surrogate conversion; values are never
interpreted as command lines. The executable is not a user-selected input.

Image writes are restricted to tested ordinary PNG/XMP fields. Audio reuses
LOPATA's existing container metadata editors; DOCX uses its core-properties
relationship. PDF, video, executables and unknown types are read-only.
Video previews reuse CaptureRuntime; no new GPU requirement or model is added.
