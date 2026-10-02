# Minimal Windows capture runtime

This directory builds the FFmpeg helper used by LOPATA, without the broad optional
dependency set of the former development binary. Source revisions and SHA-256
are pinned in `sources.json`. The five unmodified upstream archives are also
distributed as `LOPATA-Capture-Sources-0.3.20-beta.zip` on the same GitHub release
as the binary update. FFmpeg DLLs remain separately replaceable.

## Build

Use an isolated MSYS2 UCRT64 directory, not a system-wide compiler installation.
Install `make diffutils nasm perl mingw-w64-ucrt-x86_64-gcc
mingw-w64-ucrt-x86_64-pkgconf mingw-w64-ucrt-x86_64-cmake` in it with pacman;
retain signature verification. From PowerShell 7:

```powershell
./build.ps1 -MsysRoot '<isolated MSYS2 root>' -BuildDirectory '<empty build directory>' -Jobs 4
```

The recipe verifies every original source archive before building. Successful
dependency installs are reused on retry; use an empty directory for a clean
rebuild. It disables FFmpeg autodetection, network support, GPL and nonfree mode.
Only Media Foundation/NVENC/OpenH264 H.264, VP9, AAC and Opus plus the necessary
raw inputs, MP4/MKV/WebM containers and audio/video filters are enabled.

`build-minimal.sh` is the complete configure/build/install recipe. Build logs,
compiler version, installed package versions and generated FFmpeg configuration
are retained in the build directory. Original upstream source files are not
patched. Generated configuration/version headers are build products.

The release source archive includes the exact archives, recipe, tool versions,
configuration and original notices. Rebuilding with different compiler packages
may change binary hashes; byte-identical reproducibility is not claimed.
The Windows UCRT/system codecs and NVIDIA driver are supplied by the user's OS
and device. No NVIDIA binary SDK or driver is redistributed.

## Licenses

FFmpeg: LGPL-2.1-or-later; libvpx and Opus: BSD-3-Clause; OpenH264: BSD-2-Clause;
nv-codec headers: NVIDIA permissive header terms. Preserve upstream PATENTS and
third-party notices inside the source archives and the delivered license texts.
Compiler support code retains MinGW-w64/winpthreads notices and the GNU GPL v3
plus GCC Runtime Library Exception 3.1. Building uses only GCC and compatible
open-source compiler tools, without proprietary intermediate-code optimizers.
The license notice does not claim patent clearance for every use or territory.
LOPATA's own OpenH264 build is not Cisco's prebuilt binary royalty programme.
