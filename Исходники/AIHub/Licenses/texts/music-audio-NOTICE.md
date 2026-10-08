# Music audio runtime

Separate LGPL shared FFmpeg build for Opus, MP3, FLAC and PCM WAV. No GPL/nonfree build mode. DLLs remain replaceable. LOPATA does not restrict reverse engineering needed to debug modifications to these libraries.

Exact corresponding source archives, recipe, FFmpeg configuration and toolchain inventory accompany the binaries in `MusicAudioRuntime/corresponding-source.zip`. See `Инструменты/MusicAudioRuntime/README.md` for rebuilding. Redistribution must retain this package and the original licenses in `MusicAudioRuntime/licenses`.

- FFmpeg: LGPL-2.1-or-later, revision 330caae0c1acccd2222edc52a05940c574561ce5, https://github.com/FFmpeg/FFmpeg
- Opus 1.5.2: BSD-3-Clause and IPR notice, revision ddbe48383984d56acd9e1ab6a090c54ca6b735a6, https://github.com/xiph/opus
- LAME 3.100: LGPL-2.0-or-later, official archive SHA256 ddfe36cab873794038ae2c1210557ad34857a4b6bdc515785d1da9e175b1da1e, https://lame.sourceforge.io/
- MinGW-w64 runtime notices and GCC Runtime Library Exception 3.1 accompany the build.

Build uses an isolated MSYS2 UCRT64 toolchain; it does not install system codecs or change GPU drivers. The exact source bytes are verified before compilation. The source ZIP accompanies every installed/updated music audio bundle, rather than depending on a future external download link.
