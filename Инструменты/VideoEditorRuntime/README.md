# Video editor runtime

The editor uses a separate LGPL FFmpeg 8.1.3 build. CaptureRuntime is unchanged.
No third-party Windows binary is silently substituted for this component.

`build.sh <workspace-root>` runs inside the existing isolated MSYS2 UCRT64
toolchain. It reuses the pinned, hash-verified FFmpeg/OpenH264/Opus/VPX/NVIDIA
source and dependency inputs from `Инструменты/CaptureRuntime/sources.json`
and `Runtime/Capture/Minimal-8.1`. zlib comes from the recorded UCRT64 toolchain.
No network access or automatic package installation is performed by this recipe.
The new FFmpeg configuration/build/install directory is `Runtime/VideoEditor/Build-8.1`.

`package.ps1` stages a fresh `ffmpeg-8.1.3-editor-1` bundle with replaceable DLLs,
original notices, the recipe, source pins and build configuration. It creates
`Исходники/AIHub/Tools/video-editor-runtime-files.json` for the common audit.
An existing bundle is refused; retain prior development candidates separately.
The runtime supports CPU H.264 encoding with OpenH264. NVIDIA encoding and
Media Foundation are compiled but are not claimed as physically tested here.
GPL-only filters and nonfree libraries are disabled.

Current integration tests exercise actual MP4/MKV export, RGBA frame decoding,
vector overlays rendered by the shared resvg component, AAC audio, switchable
SubRip/tx3g subtitles and explicit optional SRT sidecars. Do not release a bundle
without keeping the exact source inputs, configuration and toolchain record
available alongside its release under the existing third-party notice policy.

The public source-availability archive is prepared at acceptance/publication;
this implementation pass does not publish a release or build an installer.
