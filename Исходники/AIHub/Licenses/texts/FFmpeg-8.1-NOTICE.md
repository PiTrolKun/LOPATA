# FFmpeg video capture runtime

Bundled helper: FFmpeg n8.1.3-14-g330caae0c1-20261001, x86_64 Windows,
BtbN LGPL shared build. Copyright (c) 2000–2026 the FFmpeg developers.
FFmpeg is a separate executable communicating through pipes; its shared libraries
remain separately replaceable. LOPATA does not statically link them into its executable.

The unmodified original GNU LGPL version 3 text is included as LICENSE.txt.
GNU GPL version 3, incorporated by LGPL version 3, is included in LOPATA's license texts.
FFmpeg licensing: https://ffmpeg.org/legal.html

Pinned binary archive:
https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-10-01-13-06/ffmpeg-n8.1.3-14-g330caae0c1-win64-lgpl-shared-8.1.zip

Archive SHA256: BF545D8FEE9BB6957C1F3DEA0F384BF64EDEAD407D763326DBBD2DE1B04768A4.
The archive differs in root-directory naming from the latest alias; all nine delivered
EXE/DLL files were verified byte-for-byte identical. Individual hashes are in provenance.json.
ffplay, headers, import libraries and unused development documentation are not delivered.

FFmpeg source matching the encoded commit identifier:
https://github.com/FFmpeg/FFmpeg/tree/330caae0c1
https://github.com/FFmpeg/FFmpeg/archive/330caae0c1.tar.gz

Builder source, dependency recipes and patches at the build date:
https://github.com/BtbN/FFmpeg-Builds/tree/e88e49f624457c455700b058f0a84ca87d499cc2
https://github.com/BtbN/FFmpeg-Builds/archive/e88e49f624457c455700b058f0a84ca87d499cc2.tar.gz

The upstream build enables version 3 and shared libraries, disables static FFmpeg
libraries, libx264, libx265 and libfdk-aac, and does not enable GPL or nonfree mode.
The configuration printed by `ffmpeg -version` is retained in the implementation report.
LOPATA uses Windows Media Foundation H.264 / NVIDIA NVENC / OpenH264 fallback,
FFmpeg AAC, libvpx VP9 and libopus. Windows codecs are supplied by Windows;
NVIDIA encoding requires the user's existing compatible driver and device.
WebM uses software VP9 even when GPU capture is preferred.

The shared FFmpeg DLLs also contain the build provider's wider set of dependencies.
Their upstream licenses remain applicable. This notice does not claim that unused
compiled dependencies disappear when LOPATA does not call them. The provider's
LGPL variant is documented at https://github.com/BtbN/FFmpeg-Builds#targets-variants-and-addins.
Source availability and notices for that complete transitive binary composition must be
rechecked when preparing a public installer/update; this development task does not
publish a binary package or grant a new redistribution permission.

NAudio.Core and NAudio.Wasapi 2.3.0 are separate managed MIT packages.
Copyright (c) 2020 Mark Heath. Original upstream license is bundled with LOPATA:
https://github.com/naudio/NAudio/blob/v2.3.0/license.txt
Source: https://github.com/naudio/NAudio/tree/v2.3.0.
