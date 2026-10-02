#!/usr/bin/env bash
set -euo pipefail

# Build only the codec paths used by LOPATA, in an isolated MSYS2 UCRT64 toolchain.
# The caller verifies source archive hashes against sources.json before execution.
test "${MSYSTEM:-}" = UCRT64 || { echo 'Run in MSYS2 UCRT64.' >&2; exit 1; }
root=$(realpath "${1:?Pass the isolated build directory}")
prefix="$root/prefix"
jobs=${2:-4}
mkdir -p "$prefix" "$root/work" "$root/logs"
export PKG_CONFIG_PATH="$prefix/lib/pkgconfig"
export PKG_CONFIG_LIBDIR="$prefix/lib/pkgconfig"

unpack() {
    test -f "$root/sources/$1.tar.gz"
    if ! test -d "$root/work/$1"; then
        mkdir "$root/work/$1"
        tar -xzf "$root/sources/$1.tar.gz" --strip-components=1 -C "$root/work/$1"
    fi
}
for component in ffmpeg vpx opus openh264 nvcodec; do unpack "$component"; done

if ! test -f "$prefix/lib/libvpx.a"; then
    mkdir -p "$root/work/vpx-build"
    (
        cd "$root/work/vpx-build"
        ../vpx/configure --prefix="$prefix" --target=x86_64-win64-gcc \
            --disable-shared --enable-static --disable-examples --disable-tools \
            --disable-docs --disable-unit-tests --disable-vp8 --enable-vp9
        make -j"$jobs"
        make install
    ) >"$root/logs/vpx.log" 2>&1
fi

if ! test -f "$prefix/lib/libopus.a"; then
    (
        cmake -S "$root/work/opus" -B "$root/work/opus-build" -G Ninja \
            -DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX="$(cygpath -m "$prefix")" \
            -DBUILD_SHARED_LIBS=OFF -DOPUS_BUILD_PROGRAMS=OFF -DOPUS_BUILD_TESTING=OFF \
            -DOPUS_INSTALL_PKG_CONFIG_MODULE=ON
        cmake --build "$root/work/opus-build" --parallel "$jobs"
        cmake --install "$root/work/opus-build"
    ) >"$root/logs/opus.log" 2>&1
fi

if ! test -f "$prefix/lib/libopenh264.a"; then
    (
        cd "$root/work/openh264"
        make OS=mingw_nt ARCH=x86_64 BUILDTYPE=Release PREFIX="$prefix" -j"$jobs" libraries
        make OS=mingw_nt ARCH=x86_64 BUILDTYPE=Release PREFIX="$prefix" install-static
    ) >"$root/logs/openh264.log" 2>&1
fi

(
    cd "$root/work/nvcodec"
    make PREFIX="$prefix" install
) >"$root/logs/nvcodec.log" 2>&1

mkdir -p "$root/work/ffmpeg-build"
(
    cd "$root/work/ffmpeg-build"
    ../ffmpeg/configure --prefix="$prefix" --pkg-config-flags=--static \
        --disable-autodetect --disable-everything --disable-network \
        --enable-shared --disable-static --disable-debug --disable-doc --disable-ffplay \
        --enable-ffmpeg --enable-ffprobe --enable-w32threads --disable-pthreads \
        --enable-mediafoundation --enable-d3d11va --enable-libvpx --enable-libopus --enable-libopenh264 \
        --enable-ffnvcodec --enable-nvenc --disable-cuvid --disable-cuda-llvm \
        --enable-encoder=h264_mf,h264_nvenc,libopenh264,libvpx_vp9,aac,libopus,rawvideo,pcm_f32le \
        --enable-decoder=rawvideo,h264,vp9,aac,opus,pcm_s16le,pcm_f32le \
        --enable-parser=h264,vp9,aac,opus \
        --enable-demuxer=rawvideo,pcm_s16le,pcm_f32le,mov,matroska \
        --enable-muxer=mp4,matroska,webm,rawvideo,pcm_f32le --enable-protocol=file,pipe \
        --enable-indev=lavfi \
        --enable-filter=anullsrc,aresample,aformat,apad,atrim,anull,amix,alimiter,format,null,scale \
        --extra-cflags="-I$prefix/include" --extra-ldflags="-L$prefix/lib -static-libgcc -static-libstdc++ -static" \
        --extra-version=lopata-minimal-1
    make -j"$jobs"
    make install
) >"$root/logs/ffmpeg.log" 2>&1

pacman -Q >"$root/toolchain-packages.txt"
gcc --version >"$root/compiler.txt"
echo "Built isolated capture runtime in $prefix/bin"
