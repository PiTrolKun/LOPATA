# FFmpeg minimal capture runtime — 2026-10-03

LOPATA supplies FFmpeg 8.1.3-lopata-minimal-1 for Windows x64 as a separate
process and separately replaceable shared DLLs. Copyright (c) 2000–2026
FFmpeg developers. License: GNU LGPL version 2.1 or later. Original LGPL 2.1
and GPL 2 texts accompany the runtime and the component license catalogue.
FFmpeg legal/build guidance: https://ffmpeg.org/legal.html

Exact source commit: 330caae0c1acccd2222edc52a05940c574561ce5.
Complete corresponding source archives, recipe, configuration, compiler/package
versions and notices are available with the same release as the binary update:
https://github.com/PiTrolKun/LOPATA/releases/download/v0.3.20-beta/LOPATA-Capture-Sources-0.3.20-beta.zip
The archive SHA-256 and delivered EXE/DLL hashes are in provenance.json.
Sources are unmodified; configuration/version headers are generated products.
Build recipe: https://github.com/PiTrolKun/LOPATA/tree/main/Инструменты/CaptureRuntime

The recipe disables automatic external-library discovery, network support,
GPL/nonfree mode, and unused codecs. There is no x264, x265 or fdk-aac.
Only required Windows Media Foundation/NVENC/OpenH264, VP9, AAC and Opus paths,
raw inputs, MP4/MKV/WebM containers and audio/video filters are built.
Windows UCRT/codecs and the NVIDIA driver remain OS/device components; they are
not redistributed. NVENC requires a compatible installed NVIDIA driver.

Compiled external sources and their original notices:
- libvpx 1.15.2: BSD-3-Clause, WebM authors; LICENSE, PATENTS, included libyuv
  and x86inc notices. Source d168454ecd099805c675d4a98c66f4891373302a.
- Opus 1.5.2: BSD-3-Clause and notices in COPYING / LICENSE_PLEASE_READ.txt.
  Source ddbe48383984d56acd9e1ab6a090c54ca6b735a6.
- OpenH264 2.6.0: BSD-2-Clause, Cisco. Source
  652bdb7719f30b52b08e506645a7322ff1b2cc6f. This own build is not Cisco's
  prebuilt binary royalty programme; no blanket patent clearance is claimed.
- nv-codec headers n13.0.19.0: NVIDIA permissive header notices; source
  e844e5b26f46bb77479f063029595293aa8f812d. No NVIDIA SDK binary is included.
- MinGW-w64 CRT/headers and winpthreads: original copyright/permission notices
  in Capture-MinGW-* / Capture-winpthreads-COPYING.txt.
- GCC support runtime: GPL v3 with GCC Runtime Library Exception 3.1, originals
  in Capture-GCC-GPL3.txt / Capture-GCC-Exception.txt. Compilation uses GCC and
  compatible open-source build tools without proprietary IR optimizers.

All original notices listed above are in the adjacent licenses directory and
LOPATA's Licenses/texts. sources.json pins every upstream commit/archive hash.
Do not replace those notices with this summary when redistributing.

NAudio.Core / NAudio.Wasapi 2.3.0 are separate managed MIT components,
Copyright (c) 2020 Mark Heath. Original license is included in LOPATA.
Source: https://github.com/naudio/NAudio/tree/v2.3.0.
